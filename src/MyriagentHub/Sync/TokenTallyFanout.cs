using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>
/// 其他電腦的 token 計數（tok2；docs/sync-protocol.md 14.10）：給每條協商出 tok2 的已證明連線（只更新的除外）送 <c>TokensUpdate</c>——
/// 那台以外、沒撤銷的電腦在沒刪除的帳戶、保留天數內每一格的合計（<see cref="TokenTallySyncRules.Others"/>）。連線證明完成
/// <see cref="TokenTallySyncRules.FirstSendDelay"/> 之後送第一份（ConnectionProven 時 ProveResponse 還沒到裝置：0.2.621～0.2.624 的桌面在交握完成前收到
/// TokensUpdate 會丟例外、斷線重連，所以要等它交握完成）；
/// token_tallies 有變（<see cref="TokenTallies.Changed"/>）時重讀，最多每 <see cref="TokenTallySyncRules.FanoutInterval"/> 一次；沒有變動時每
/// <see cref="TokenTallySyncRules.RecheckInterval"/> 重讀一次（撤銷裝置、刪除帳戶、跨日都不經 Changed）。內容和這條連線上次收到的相同就不送；
/// 送不出去只記錄，下一次重讀再送。只供顯示：不寫任何東西
/// </summary>
internal sealed class TokenTallyFanout(
    TokenTallies tallies,
    SyncWriter writer,
    DeviceDirectory directory,
    ConnectionRegistry registry,
    IDeviceMessageSender sender,
    TimeProvider time,
    ILogger<TokenTallyFanout> log) : BackgroundService
{
    private readonly SemaphoreSlim wake = new(0, 1);

    /// <summary>要送的連線與證明完成的時間（單調時鐘；已證明、協商出 tok2、不是只更新的；連線事件的執行緒寫、迴圈讀）</summary>
    private readonly ConcurrentDictionary<string, (DeviceConnection Connection, long ProvenAt)> targets = new(StringComparer.Ordinal);

    /// <summary>上次送給各連線的內容（序列化後的字串；只在迴圈上讀寫）</summary>
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);

    private long? lastRunAt;

    /// <summary>上一次跑完時還沒滿 <see cref="TokenTallySyncRules.FirstSendDelay"/> 的連線最早什麼時候滿（null＝沒有；只在迴圈上讀寫）</summary>
    private TimeSpan? pendingDue;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        tallies.Changed += Wake;
        registry.ConnectionProven += OnConnectionProven;
        registry.ConnectionRemoved += OnConnectionRemoved;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        tallies.Changed -= Wake;
        registry.ConnectionProven -= OnConnectionProven;
        registry.ConnectionRemoved -= OnConnectionRemoved;
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var timeout = pendingDue is { } due && due < TokenTallySyncRules.RecheckInterval ? due : TokenTallySyncRules.RecheckInterval;
                await wake.WaitAsync(timeout, stoppingToken);
                // 節流：距離上一次不到最短間隔就等到滿（期間的變動併進同一次；各台本身至少隔 5 秒才送一次）
                if (lastRunAt is { } last && time.GetElapsedTime(last) is var since && since < TokenTallySyncRules.FanoutInterval)
                    await Task.Delay(TokenTallySyncRules.FanoutInterval - since, time, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 轉送的錯誤不讓伺服器停機（BackgroundService 丟出例外預設會停主機）
                log.LogError(ex, "其他電腦的 token 計數轉送失敗");
            }
        }
    }

    /// <summary>
    /// 一次（背景迴圈呼叫；測試直接驅動）：沒有到時候的連線就不讀；否則在一個 deferred 唯讀交易裡讀一次所有的格，
    /// 每條連線算它以外的合計，和上次送的不同才送。證明完成還不到 <see cref="TokenTallySyncRules.FirstSendDelay"/> 的連線這次不送，記下最早什麼時候到
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        lastRunAt = time.GetTimestamp();
        pendingDue = null;
        foreach (var gone in sent.Keys.Where(id => !targets.ContainsKey(id)).ToList())
            sent.Remove(gone);
        var ready = new List<DeviceConnection>();
        foreach (var (connection, provenAt) in targets.Values.OrderBy(t => t.Connection.Order))
        {
            var left = TokenTallySyncRules.FirstSendDelay - time.GetElapsedTime(provenAt);
            if (left <= TimeSpan.Zero)
                ready.Add(connection);
            else if (pendingDue is not { } due || left < due)
                pendingDue = left;
        }
        if (ready.Count == 0)
            return;

        List<(string DeviceId, TokenTallyRow Row)> rows;
        var today = tallies.Today(time.GetUtcNow().ToUnixTimeMilliseconds());
        using (var connection = writer.OpenRead())
        using (var transaction = connection.BeginTransaction(deferred: true))
            rows = TokenTallies.ReadDeviceRows(connection, transaction, today, deviceId => directory.Get(deviceId) is { Revoked: false });

        foreach (var target in ready)
        {
            // 登記裡已經不是這個物件（斷線、同一個 id 重新登記）：不送，連線事件會清掉
            if (!ReferenceEquals(registry.Get(target.ConnectionId), target))
                continue;
            var update = TokenTallySyncRules.Others(rows, target.DeviceId);
            var json = SyncJson.Serialize(update);
            if (sent.TryGetValue(target.ConnectionId, out var last) && last == json)
                continue;
            sent[target.ConnectionId] = json;
            try
            {
                await sender.SendAsync(target.ConnectionId, DeviceHubMethods.TokensUpdate, update, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 沒送到：下一次重讀再送（內容一樣也送）
                sent.Remove(target.ConnectionId);
                log.LogDebug(ex, "TokensUpdate 送不到裝置連線 {Connection}", target.ConnectionId);
            }
        }
    }

    private void OnConnectionProven(DeviceConnection connection)
    {
        if (connection.UpdateOnly || !connection.Allows(DeviceHubMethods.TokensUpdate))
            return;
        targets[connection.ConnectionId] = (connection, time.GetTimestamp());
        Wake();
    }

    private void OnConnectionRemoved(DeviceConnection connection)
    {
        if (targets.TryGetValue(connection.ConnectionId, out var target) && ReferenceEquals(target.Connection, connection))
            targets.TryRemove(new KeyValuePair<string, (DeviceConnection, long)>(connection.ConnectionId, target));
    }

    private void Wake()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已經排了一次喚醒
        }
        catch (ObjectDisposedException)
        {
            // 停機中
        }
    }
}
