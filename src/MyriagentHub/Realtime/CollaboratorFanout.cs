using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Auth;

namespace MyriagentHub.Realtime;

/// <summary>
/// 多人協作的推送（docs/plans/2026-10-30-collaborators.md、sync-protocol 14.14）：名冊或分享改了、協作人連上或斷線（「在線」）時，
/// 喚醒瀏覽器閘道（擁有者的 <c>collaborators</c>、協作人的清單與關掉不再分享的對話）、送協作人自己的介面設定，
/// 並給每條協商出 <c>collab1</c> 的已證明裝置連線（只更新的除外）送 <c>CollabUpdate</c>：連線證明完成時送一份，之後內容變了才送（最多每秒一次）
/// </summary>
internal sealed class CollaboratorFanout(
    CollaboratorDirectory directory,
    BrowserConnections browsers,
    BrowserGateway gateway,
    ConnectionRegistry registry,
    IDeviceMessageSender sender,
    TimeProvider time,
    ILogger<CollaboratorFanout> log) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly ConcurrentDictionary<string, DeviceConnection> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);
    private long? lastRunAt;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        directory.Changed += OnChanged;
        browsers.Added += OnBrowser;
        browsers.Removed += OnBrowser;
        registry.ConnectionProven += OnConnectionProven;
        registry.ConnectionRemoved += OnConnectionRemoved;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        directory.Changed -= OnChanged;
        browsers.Added -= OnBrowser;
        browsers.Removed -= OnBrowser;
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
                await wake.WaitAsync(stoppingToken);
                if (lastRunAt is { } last && time.GetElapsedTime(last) is var since && since < Interval)
                    await Task.Delay(Interval - since, time, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "協作人狀態的推送失敗");
            }
        }
    }

    /// <summary>一次（背景迴圈呼叫；測試直接驅動）：給每條目標連線送目前的狀態（和上次送給它的相同就不送）</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        lastRunAt = time.GetTimestamp();
        foreach (var gone in sent.Keys.Where(id => !targets.ContainsKey(id)).ToList())
            sent.Remove(gone);
        if (targets.IsEmpty)
            return;
        var state = directory.State(browsers.OnlineCollaborators());
        var json = SyncJson.Serialize(state);
        foreach (var target in targets.Values.OrderBy(t => t.Order))
        {
            if (!ReferenceEquals(registry.Get(target.ConnectionId), target))
                continue;
            if (sent.TryGetValue(target.ConnectionId, out var last) && last == json)
                continue;
            sent[target.ConnectionId] = json;
            try
            {
                await sender.SendAsync(target.ConnectionId, DeviceHubMethods.CollabUpdate, state, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sent.Remove(target.ConnectionId);
                log.LogDebug(ex, "CollabUpdate 送不到裝置連線 {Connection}", target.ConnectionId);
            }
        }
    }

    /// <summary>名冊改了（提交之後、寫入者的執行緒上）：只記髒並喚醒，介面設定直接排給那位的連線</summary>
    internal void OnChanged(CollaboratorChange change)
    {
        gateway.CollaboratorsChanged();
        if (change.Prefs)
        {
            foreach (var user in change.Users)
                gateway.PushCollaboratorSettings(user);
        }
        Wake();
    }

    private void OnBrowser(BrowserConnection connection)
    {
        if (!connection.IsCollaborator)
            return;
        gateway.CollaboratorsChanged();
        Wake();
    }

    private void OnConnectionProven(DeviceConnection connection)
    {
        if (connection.UpdateOnly || !connection.Allows(DeviceHubMethods.CollabUpdate))
            return;
        targets[connection.ConnectionId] = connection;
        Wake();
    }

    private void OnConnectionRemoved(DeviceConnection connection)
    {
        if (targets.TryGetValue(connection.ConnectionId, out var target) && ReferenceEquals(target, connection))
            targets.TryRemove(new KeyValuePair<string, DeviceConnection>(connection.ConnectionId, target));
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
