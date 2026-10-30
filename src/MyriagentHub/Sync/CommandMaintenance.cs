using Myriagent.Remote;
using MyriagentHub.Data;

namespace MyriagentHub.Sync;

/// <summary>
/// 遠端指令的背景維護（WP4.5 計畫步 7、8；D19、D27、D28）：每秒以真實時間醒來一次，用注入的 <see cref="TimeProvider"/> 判斷時間（測試推進手動時鐘後直接呼叫
/// <see cref="SweepAsync"/>；同時只跑一輪）。
/// <list type="bullet">
/// <item>結果期限：非 durable、還是 delivered、<c>expires_at</c>（<c>iat</c>＋100 秒）到了的列，以一個寫入交易標成 unknown，再轉給有追蹤的發起連線
/// （<see cref="CommandResults.ForwardFinal"/>）。判斷依 sync.db，不靠記憶體：追蹤被擠掉、伺服器重啟前送出的列也會結束。候選先以唯讀連線找（不在寫入者裡掃表）。</item>
/// <item>durable 的 busy／expired 重送：到期的排進 <see cref="DurableCommands"/>（<see cref="DurableCommands.RunDue"/>）。</item>
/// <item>每 <see cref="CommandStateRules.RescanIntervalMs"/>（60 秒）：durable 超過 7 天的標成 expired、重掃 pending（<see cref="DurableCommands.Rescan"/>）。</item>
/// <item>每 <see cref="PruneIntervalMs"/>（24 小時）：清掉結束滿 30 天的列（<see cref="PruneAsync"/>；啟動時另由 <see cref="CommandStartup"/> 做第一次）。</item>
/// <item>整理追蹤（<see cref="CommandTracker.Prune"/>）。</item>
/// </list>
/// 啟動掃描（<see cref="StartupScanAsync"/>）也在這裡，由啟動工作在任何連線之前呼叫
/// </summary>
internal sealed class CommandMaintenance(SyncWriter writer, CommandStore store, CommandResults results, CommandTracker tracker, CommandCounters counters,
    DurableCommands durable, TimeProvider time, ILogger<CommandMaintenance> log) : BackgroundService
{
    /// <summary>維護迴圈多久醒一次（真實時間）</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>多久清理一次（D28）</summary>
    public const long PruneIntervalMs = 24L * 60 * 60 * 1000;

    private readonly SemaphoreSlim sweepGate = new(1, 1);

    /// <summary>上一次重掃 durable 的時間（第一次在啟動 60 秒後：剛上線的連線由 Prove 處理）</summary>
    private long lastRescanAt = time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>上一次清理的時間（啟動時由啟動工作做第一次）</summary>
    private long lastPruneAt = time.GetUtcNow().ToUnixTimeMilliseconds();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 真實時間：測試的手動時鐘不推進計時器，迴圈照樣每秒醒來看一次
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
            {
                // 一輪的最外層：維護的錯誤不讓伺服器停機，下一秒再試
                log.LogWarning(ex, "遠端指令的維護失敗，下一輪再試");
            }
        }
    }

    /// <summary>一輪（見類別說明）；回傳這一輪因結果期限標成 unknown 的 cid（測試用）</summary>
    internal async Task<IReadOnlyList<string>> SweepAsync(CancellationToken cancellationToken = default)
    {
        await sweepGate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var expired = await ExpireDeadlinesAsync(now, cancellationToken);
            durable.RunDue(now);
            if (now - lastRescanAt >= CommandStateRules.RescanIntervalMs)
            {
                lastRescanAt = now;
                await ExpireDurableAsync(now, cancellationToken);
                durable.Rescan();
            }
            if (now - lastPruneAt >= PruneIntervalMs)
            {
                lastPruneAt = now;
                await PruneAsync(cancellationToken);
            }
            tracker.Prune(time.GetUtcNow().ToUnixTimeMilliseconds());
            return expired;
        }
        finally
        {
            sweepGate.Release();
        }
    }

    /// <summary>
    /// 啟動掃描（D28；啟動工作在任何連線之前呼叫）：非 durable、還沒結束的列標成 unknown（絕不重送），durable 到期的標成 expired，durable 的 delivered
    /// 改回 pending。每批最多 <see cref="CommandStateRules.PruneBatch"/> 列、一個寫入交易
    /// </summary>
    internal async Task<StartupScanCounts> StartupScanAsync(CancellationToken cancellationToken = default)
    {
        var total = StartupScanCounts.Zero;
        string? after = null;
        while (true)
        {
            IReadOnlyList<string> ids;
            using (var connection = writer.OpenRead())
                ids = store.OpenCommandIds(connection, after, CommandStateRules.PruneBatch);
            if (ids.Count == 0)
                break;
            total = total.Add(await writer.WriteAsync(context => store.StartupScan(context, ids), cancellationToken));
            after = ids[^1];
            if (ids.Count < CommandStateRules.PruneBatch)
                break;
        }
        for (var i = 0; i < total.Unknown; i++)
            counters.Unknown();
        for (var i = 0; i < total.Expired; i++)
            counters.Expired();
        if (total != StartupScanCounts.Zero)
        {
            log.LogInformation("遠端指令的啟動掃描：{Unknown} 個沒有結果的標成 unknown、{Expired} 個 durable 超過 7 天標成 expired、{Pending} 個 durable 改回 pending",
                total.Unknown, total.Expired, total.Pending);
        }
        return total;
    }

    /// <summary>清理（D28）：一批一批刪掉結束滿 30 天的列，直到沒有；回傳刪了幾列</summary>
    internal async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (true)
        {
            var (candidates, deleted) = await PruneOnceAsync(cancellationToken);
            total += deleted;
            // 一批都刪不掉（候選讀出之後變了，不應發生）也停：不在同一批候選上空轉
            if (candidates < CommandStateRules.PruneBatch || deleted == 0)
                break;
        }
        if (total > 0)
            log.LogInformation("清掉 {Count} 個結束滿 30 天的遠端指令", total);
        return total;
    }

    /// <summary>清理一批（最多 <see cref="CommandStateRules.PruneBatch"/> 列、一個寫入交易）：回傳候選數與真的刪了幾列</summary>
    internal async Task<(int Candidates, int Deleted)> PruneOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        IReadOnlyList<string> candidates;
        using (var connection = writer.OpenRead())
            candidates = store.PruneCandidates(connection, now, CommandStateRules.PruneBatch);
        if (candidates.Count == 0)
            return (0, 0);
        var deleted = await writer.WriteAsync(context => store.Prune(context, candidates), cancellationToken);
        return (candidates.Count, deleted);
    }

    /// <summary>結果期限（D19）：標成 unknown 並轉送</summary>
    private async Task<IReadOnlyList<string>> ExpireDeadlinesAsync(long now, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> due;
        using (var connection = writer.OpenRead())
            due = store.DueDeadlines(connection, now);
        if (due.Count == 0)
            return [];
        var expired = await writer.WriteAsync(context => store.ExpireDeadlines(context, due), cancellationToken);
        foreach (var commandId in expired)
        {
            counters.Unknown();
            results.ForwardFinal(commandId, RemoteResultStates.Unknown, null, null, null);
        }
        if (expired.Count > 0)
            log.LogInformation("{Count} 個遠端指令在 {Seconds} 秒內沒有結果，標成 unknown", expired.Count, CommandStateRules.ResultDeadlineMs / 1000);
        return expired;
    }

    /// <summary>durable 的 7 天期限（D27）：標成 expired、清掉 unsigned_body，不轉送（發起的連線早就不追蹤了）</summary>
    private async Task ExpireDurableAsync(long now, CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyList<string> due;
            using (var connection = writer.OpenRead())
                due = store.DueDurableExpiries(connection, now);
            if (due.Count == 0)
                return;
            var expired = await writer.WriteAsync(context => store.ExpireDurable(context, due), cancellationToken);
            foreach (var commandId in expired)
            {
                counters.Expired();
                durable.Completed(commandId);
            }
            if (expired.Count > 0)
                log.LogInformation("{Count} 個 durable 遠端指令超過 7 天，標成 expired", expired.Count);
            if (due.Count < CommandStore.MaxDeadlinesPerSweep || expired.Count == 0)
                return;
        }
    }
}
