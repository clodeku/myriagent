using Myriagent.Sync;
using MyriagentHub.Sync;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 向擁有者送 <c>SnapshotRequest</c>（WP4.2 計畫步 12；R16，D26、P3；規則在 <see cref="SnapshotRequestRules"/>）：
/// <list type="bullet">
/// <item><b>open</b>（<see cref="OnOpen"/>，瀏覽器 open 的指令執行緒）：full、有 meta、沒有任何項目的對話，擁有者最新的已 Prove 連線協商出 conv1，
/// 每個對話冷卻 <c>SnapshotOpenCooldownMinutes</c>（記憶體）；在背景送。</item>
/// <item><b>restore 後備</b>（<see cref="TickAsync"/>，背景每 1000/<c>SnapshotRestorePerSecond</c> 毫秒一次）：擁有者協商出 conv1 的連線 Prove 之後
/// 過了 <c>SnapshotRestoreGraceSeconds</c>，對它仍是 recovering、這個還原世代還沒送過的對話依 id 逐一送，每台兩則之間至少 1000/N 毫秒；
/// 連線斷了就停、下次 Prove 重新計時，已送過的不再送。都送過了（或沒有）就不再掃那條連線。</item>
/// </list>
/// 送之前一律問 <see cref="DeviceConnection.Allows"/>（<c>SnapshotRequest</c> 要 conv1）；送失敗只記錄。狀態以一把鎖保護
/// </summary>
internal sealed class SnapshotRequester(
    ConversationDirectoryCache directory,
    ConnectionRegistry registry,
    IDeviceMessageSender sender,
    IOptions<HubOptions> options,
    TimeProvider time,
    ILogger<SnapshotRequester> log) : BackgroundService
{
    private readonly object sync = new();
    private readonly Dictionary<string, long> openRequested = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProvenConnection> proven = new(StringComparer.Ordinal);
    private readonly HashSet<string> restoreSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> restoreLastSent = new(StringComparer.Ordinal);
    private int attached;

    /// <summary>restore 已經送過的則數（測試與營運用）</summary>
    public int RestoreSentCount
    {
        get
        {
            lock (sync)
                return restoreSent.Count;
        }
    }

    public void Attach()
    {
        if (Interlocked.Exchange(ref attached, 1) == 1)
            return;
        registry.ConnectionProven += OnProven;
        registry.ConnectionRemoved += OnRemoved;
    }

    public void Detach()
    {
        if (Interlocked.Exchange(ref attached, 0) == 0)
            return;
        registry.ConnectionProven -= OnProven;
        registry.ConnectionRemoved -= OnRemoved;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Attach();
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Detach();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// 瀏覽器 open 了這個對話（指令已經查過目錄）：照 <see cref="SnapshotRequestRules.ShouldRequestOpen"/> 決定，要送就記下時間、在背景送給擁有者最新的已 Prove 連線。
    /// 送了回 true
    /// </summary>
    public bool OnOpen(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        var entry = directory.TryGet(conversationId, out var found) ? found : null;
        var connection = entry?.OwnerDeviceId is { } owner ? Latest(owner) : null;
        var nowMs = NowMs();
        var cooldownMs = (long)options.Value.Conversations.SnapshotOpenCooldownMinutes * 60_000;
        lock (sync)
        {
            var last = openRequested.TryGetValue(conversationId, out var at) ? at : (long?)null;
            if (!SnapshotRequestRules.ShouldRequestOpen(entry, connection is not null, connection?.Allows(DeviceHubMethods.SnapshotRequest) == true, last, nowMs, cooldownMs))
                return false;
            openRequested[conversationId] = nowMs;
        }
        _ = SendAsync(connection!, new SnapshotRequest(conversationId, SnapshotReasons.Open), CancellationToken.None);
        return true;
    }

    /// <summary>restore 後備的一輪（背景迴圈；測試以注入的時鐘直接呼叫）：每台到期的連線最多送一則</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value.Conversations;
        var nowMs = NowMs();
        var graceMs = (long)settings.SnapshotRestoreGraceSeconds * 1000;
        var cooldownMs = (long)settings.SnapshotOpenCooldownMinutes * 60_000;
        var generation = directory.RestoreGeneration;
        var sends = new List<(DeviceConnection Connection, string ConversationId)>();
        lock (sync)
        {
            foreach (var expired in openRequested.Where(p => nowMs - p.Value >= cooldownMs).Select(p => p.Key).ToList())
                openRequested.Remove(expired);
            foreach (var group in proven.Values.GroupBy(p => p.Connection.DeviceId, StringComparer.Ordinal))
            {
                var latest = group.MaxBy(p => p.Connection.Order)!;
                var deviceId = group.Key;
                if (latest.Done || !SnapshotRequestRules.RestoreDue(latest.ProvenAtMs, nowMs, graceMs))
                    continue;
                var last = restoreLastSent.TryGetValue(deviceId, out var at) ? at : (long?)null;
                if (!SnapshotRequestRules.RestorePaced(last, nowMs, settings.SnapshotRestorePerSecond))
                    continue;
                if (SnapshotRequestRules.NextRestore(directory.OwnedBy(deviceId), deviceId, generation, restoreSent) is not { } next)
                {
                    latest.Done = true;
                    continue;
                }
                restoreSent.Add(SnapshotRequestRules.RestoreKey(deviceId, generation, next.ConversationId));
                restoreLastSent[deviceId] = nowMs;
                sends.Add((latest.Connection, next.ConversationId));
            }
        }
        foreach (var (connection, conversationId) in sends)
            await SendAsync(connection, new SnapshotRequest(conversationId, SnapshotReasons.Restore), cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(1, 1000 / Math.Max(1, options.Value.Conversations.SnapshotRestorePerSecond)));
        using var timer = new PeriodicTimer(period, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "restore 的快照請求失敗");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 這台最新的已 Prove 完整連線（不在線是 null）。只更新的連線不算（<see cref="DeviceConnection.IsProvenSyncing"/>）：同一台較舊的完整連線還在時，
    /// 原本會挑到較新的只更新連線、它沒有 conv1 就不送，open 的快照請求就這樣漏掉
    /// </summary>
    private DeviceConnection? Latest(string deviceId) =>
        registry.ConnectionsOf(deviceId).Where(c => c.IsProvenSyncing).MaxBy(c => c.Order);

    private async Task SendAsync(DeviceConnection connection, SnapshotRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(connection.ConnectionId, DeviceHubMethods.SnapshotRequest, request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(ex, "送 SnapshotRequest（{Reason}）失敗：{DeviceId} {Connection}", request.Reason, connection.DeviceId, connection.ConnectionId);
        }
    }

    private void OnProven(DeviceConnection connection)
    {
        if (!connection.Allows(DeviceHubMethods.SnapshotRequest))
            return;
        lock (sync)
            proven[connection.ConnectionId] = new ProvenConnection(connection, NowMs());
    }

    private void OnRemoved(DeviceConnection connection)
    {
        lock (sync)
            proven.Remove(connection.ConnectionId);
    }

    private long NowMs() => time.GetTimestamp() * 1000 / time.TimestampFrequency;

    /// <summary>一條協商出 conv1 的已 Prove 連線：Prove 的時間（單調時鐘）與 restore 是不是都送過了</summary>
    private sealed class ProvenConnection(DeviceConnection connection, long provenAtMs)
    {
        public DeviceConnection Connection { get; } = connection;

        public long ProvenAtMs { get; } = provenAtMs;

        public bool Done { get; set; }
    }
}
