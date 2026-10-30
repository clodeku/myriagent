using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>
/// 其他電腦的排程（sched1；docs/sync-protocol.md 14.12）：給每條協商出 sched1 的已證明連線（只更新的除外）送 <c>SchedulesUpdate</c>——
/// 那台以外、沒撤銷的電腦最後回報的摘要（<see cref="ScheduleSyncRules.Others"/>）。做法同 tok2 的 <see cref="TokenTallyFanout"/>：
/// 連線證明完成 <see cref="ScheduleSyncRules.FirstSendDelay"/> 之後送第一份；device_schedules 有變時重讀（最多每
/// <see cref="ScheduleSyncRules.FanoutInterval"/> 一次）、沒有變動時每 <see cref="ScheduleSyncRules.RecheckInterval"/> 重讀；內容和上次送的相同就不送。
/// 只供顯示：不寫任何東西
/// </summary>
internal sealed class ScheduleFanout(
    DeviceScheduleStore store,
    SyncWriter writer,
    DeviceDirectory directory,
    ConnectionRegistry registry,
    IDeviceMessageSender sender,
    TimeProvider time,
    ILogger<ScheduleFanout> log) : BackgroundService
{
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly ConcurrentDictionary<string, (DeviceConnection Connection, long ProvenAt)> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);
    private long? lastRunAt;
    private TimeSpan? pendingDue;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        store.Changed += Wake;
        registry.ConnectionProven += OnConnectionProven;
        registry.ConnectionRemoved += OnConnectionRemoved;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        store.Changed -= Wake;
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
                var timeout = pendingDue is { } due && due < ScheduleSyncRules.RecheckInterval ? due : ScheduleSyncRules.RecheckInterval;
                await wake.WaitAsync(timeout, stoppingToken);
                if (lastRunAt is { } last && time.GetElapsedTime(last) is var since && since < ScheduleSyncRules.FanoutInterval)
                    await Task.Delay(ScheduleSyncRules.FanoutInterval - since, time, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "其他電腦的排程轉送失敗");
            }
        }
    }

    /// <summary>一次（背景迴圈呼叫；測試直接驅動）</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        lastRunAt = time.GetTimestamp();
        pendingDue = null;
        foreach (var gone in sent.Keys.Where(id => !targets.ContainsKey(id)).ToList())
            sent.Remove(gone);
        var ready = new List<DeviceConnection>();
        foreach (var (connection, provenAt) in targets.Values.OrderBy(t => t.Connection.Order))
        {
            var left = ScheduleSyncRules.FirstSendDelay - time.GetElapsedTime(provenAt);
            if (left <= TimeSpan.Zero)
                ready.Add(connection);
            else if (pendingDue is not { } due || left < due)
                pendingDue = left;
        }
        if (ready.Count == 0)
            return;
        List<DeviceSchedulesView> all;
        using (var connection = writer.OpenRead())
        using (var transaction = connection.BeginTransaction(deferred: true))
            all = DeviceScheduleStore.Read(connection, transaction, deviceId => directory.Get(deviceId) is { Revoked: false });
        foreach (var target in ready)
        {
            if (!ReferenceEquals(registry.Get(target.ConnectionId), target))
                continue;
            var update = ScheduleSyncRules.Others(all, target.DeviceId);
            var json = SyncJson.Serialize(update);
            if (sent.TryGetValue(target.ConnectionId, out var last) && last == json)
                continue;
            sent[target.ConnectionId] = json;
            try
            {
                await sender.SendAsync(target.ConnectionId, DeviceHubMethods.SchedulesUpdate, update, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sent.Remove(target.ConnectionId);
                log.LogDebug(ex, "SchedulesUpdate 送不到裝置連線 {Connection}", target.ConnectionId);
            }
        }
    }

    private void OnConnectionProven(DeviceConnection connection)
    {
        if (connection.UpdateOnly || !connection.Allows(DeviceHubMethods.SchedulesUpdate))
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
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
