using Myriagent.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 各電腦即時狀態的轉送（stat1；docs/sync-protocol.md 14.8）：看板變了（<see cref="DeviceStatsBoard.Changed"/>）或主機重新取樣後，在背景迴圈發布一份新的
/// <see cref="DeviceStatsBoard.Published"/>（最多每 <see cref="DeviceStatsRules.FanoutIntervalMs"/> 一份；瀏覽器閘道訂閱它，開著電腦狀態的瀏覽器連線收
/// <c>deviceStats</c>），送 <c>StatsUpdate</c> 給最後一次 watch 是 true、協商出 stat1 的已證明連線（這一份已經送過的不再送）。
/// MyriagentHub 主機自己的負載（<see cref="HubStatsSampler"/>）只在有人在看時每 <see cref="HubSampleInterval"/> 取一次，沒人看時不取樣（閒置不多做事）。
/// 裝置連線斷了就從看板拿掉；每 5 秒清一次太久沒送的。送不出去只記錄、不重試（下一份會再送）
/// </summary>
internal sealed class DeviceStatsFanout(
    DeviceStatsBoard board,
    ConnectionRegistry registry,
    IDeviceMessageSender sender,
    HubStatsSampler hubSampler,
    TimeProvider time,
    ILogger<DeviceStatsFanout> log,
    BrowserConnections? browsers = null) : BackgroundService
{
    /// <summary>沒有變動時多久醒來清一次太久沒送的</summary>
    internal static readonly TimeSpan ExpireInterval = TimeSpan.FromSeconds(5);

    /// <summary>有人在看時多久取樣一次主機（同電腦有人在看時的送出間隔）</summary>
    internal static readonly TimeSpan HubSampleInterval = DeviceStatsRules.WatchedInterval;

    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Dictionary<string, long> sent = new(StringComparer.Ordinal);
    private long publishedChanges = -1;
    private long? lastPublishAt;
    private (HubStatsEntry Entry, long TakenAt)? hub;
    private long hubSeq;

    /// <summary>上一次嘗試取樣主機的時間（失敗也算：讀不到時照間隔再試，不空轉）</summary>
    private long? hubAttemptAt;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        board.Changed += Wake;
        registry.ConnectionRemoved += OnConnectionRemoved;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        board.Changed -= Wake;
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
                await wake.WaitAsync(NextWake(), stoppingToken);
                board.ExpireStale();
                // 節流：距離上一份不到 1 秒就等到滿 1 秒（期間的變動併進同一份）
                if (lastPublishAt is { } last && time.GetElapsedTime(last) is var since && since < TimeSpan.FromMilliseconds(DeviceStatsRules.FanoutIntervalMs))
                    await Task.Delay(TimeSpan.FromMilliseconds(DeviceStatsRules.FanoutIntervalMs) - since, time, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 轉送的錯誤不讓伺服器停機（BackgroundService 丟出例外預設會停主機）
                log.LogError(ex, "各電腦即時狀態的轉送失敗");
            }
        }
    }

    /// <summary>
    /// 一輪（背景迴圈呼叫；單元測試直接驅動）：有人在看而且主機該取樣了就取一次；看板有變或主機取樣了就發布新的一份，
    /// 再送給還沒收到這一份、在看的裝置連線
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var hubChanged = false;
        if (board.AnyWatching() && (hubAttemptAt is not { } attempt || time.GetElapsedTime(attempt) >= HubSampleInterval - TimeSpan.FromMilliseconds(100)))
        {
            hubAttemptAt = time.GetTimestamp();
            try
            {
                var stats = hubSampler.Sample();
                hub = (new HubStatsEntry(hubSampler.Name, ++hubSeq, 0, stats, registry.OnlineDeviceCount, browsers?.Count ?? 0), time.GetTimestamp());
                hubChanged = true;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "讀不到 MyriagentHub 主機的負載");
            }
        }

        var changes = board.Changes;
        if (changes != publishedChanges || hubChanged)
        {
            publishedChanges = changes;
            var hubEntry = hub is { } h ? h.Entry with { AgeMs = (long)time.GetElapsedTime(h.TakenAt).TotalMilliseconds } : null;
            board.SetPublished(new DeviceStatsSnapshot((board.Published?.Version ?? 0) + 1, board.Snapshot(), hubEntry));
            lastPublishAt = time.GetTimestamp();
        }
        if (board.Published is not { } snapshot)
            return;

        var watching = board.WatchingConnections();
        foreach (var gone in sent.Keys.Except(watching, StringComparer.Ordinal).ToList())
            sent.Remove(gone);
        foreach (var connectionId in watching)
        {
            if (sent.TryGetValue(connectionId, out var version) && version == snapshot.Version)
                continue;
            if (registry.Get(connectionId) is not { IsProven: true } connection || !connection.Allows(DeviceHubMethods.StatsUpdate))
                continue;
            sent[connectionId] = snapshot.Version;
            try
            {
                await sender.SendAsync(connectionId, DeviceHubMethods.StatsUpdate, new StatsUpdate(snapshot.Entries, snapshot.Hub), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "StatsUpdate 送不到裝置連線 {Connection}", connectionId);
            }
        }
    }

    /// <summary>有人在看時醒來的時間是主機下一次取樣，否則是清太久沒送的</summary>
    private TimeSpan NextWake()
    {
        if (!board.AnyWatching())
            return ExpireInterval;
        if (hubAttemptAt is not { } attempt)
            return TimeSpan.Zero;
        var remaining = HubSampleInterval - time.GetElapsedTime(attempt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void OnConnectionRemoved(DeviceConnection connection) => board.Remove(connection.ConnectionId);

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
