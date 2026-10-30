using System.Collections.Concurrent;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Ops;
using MyriagentHub.Releases;
using MyriagentHub.Sync;
using MyriagentHub.Trust;
using Microsoft.AspNetCore.SignalR;

namespace MyriagentHub.Realtime;

/// <summary>
/// 伺服器 → 裝置的通知（sync-protocol 4）：
/// <list type="bullet">
/// <item><c>Notify</c>：有新的持久事件（≤每 200 ms 合併一次，帶目前的 high 與有新事件的類別），裝置自己來拉。</item>
/// <item><c>TrustChanged</c>：裝置名冊變了。</item>
/// <item><c>ServerNotice{shutdown}</c>：開始排空（部署或維護），帶排空的 retryAfterSec，裝置等那麼久再重連、不顯示錯誤。
/// 服務直接停止時不送：部署一律先送排空要求，沒有通知的斷線，裝置照退避重連（U2 設計 D7）。</item>
/// <item><c>ServerNotice{appRelease}</c>：收件收進新的版本（同一版重送不觸發），帶 release；排空不影響（D17）。</item>
/// <item><c>UsageAssign</c>：轉送用量指派給那台的連線（不在線的收不到；重連時 ProveResponse 帶完整的清單）。
/// 送出的順序不保證，裝置以 term 判斷新舊。</item>
/// </list>
/// ServerNotice 只送給已 Prove 的連線（<see cref="NoticeTargets"/>）。事件來源大多在 sync.db 的寫入執行緒上觸發：這裡只記下或丟出非同步的送出，不阻塞。
/// </summary>
internal sealed class DeviceBroadcaster(
    IHubContext<DeviceHub> hub,
    SyncNotifier notifier,
    TrustService trust,
    DrainState drain,
    UsageCoordinator usage,
    ConnectionRegistry connections,
    AppReleaseStore releases,
    ILogger<DeviceBroadcaster> log) : BackgroundService
{
    public static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(200);

    private readonly ConcurrentDictionary<string, byte> pendingClasses = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim wake = new(0, 1);

    /// <summary>ServerNotice（shutdown、appRelease）的對象：已 Prove 的連線，含只更新的（sync-protocol 1.4）。Notify、TrustChanged 另外直接送 devices 群組</summary>
    private IClientProxy NoticeTargets => hub.Clients.Groups([DeviceHubRuntime.Group, DeviceHubRuntime.UpdateGroup]);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        notifier.EventCommitted += OnCommitted;
        trust.Changed += OnTrustChanged;
        drain.Changed += OnDrainChanged;
        usage.Assigned += OnAssigned;
        releases.Ingested += OnIngested;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await wake.WaitAsync(stoppingToken);
                var classes = new List<string>();
                foreach (var cls in pendingClasses.Keys)
                {
                    if (pendingClasses.TryRemove(cls, out _))
                        classes.Add(cls);
                }
                if (classes.Count > 0)
                {
                    classes.Sort(StringComparer.Ordinal);
                    await SendAsync(hub.Clients.Group(DeviceHubRuntime.Group).SendAsync(DeviceHubMethods.Notify, new Notify(notifier.High, classes), stoppingToken));
                }
                await Task.Delay(NotifyInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 服務停止
        }
        finally
        {
            notifier.EventCommitted -= OnCommitted;
            trust.Changed -= OnTrustChanged;
            drain.Changed -= OnDrainChanged;
            usage.Assigned -= OnAssigned;
            releases.Ingested -= OnIngested;
        }
    }

    private void OnCommitted(string cls, long seq)
    {
        pendingClasses.TryAdd(cls, 0);
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已經排了：下一次送出會一起帶
        }
    }

    private void OnTrustChanged(TrustRevs revs) =>
        _ = SendAsync(hub.Clients.Group(DeviceHubRuntime.Group).SendAsync(DeviceHubMethods.TrustChanged, new TrustChanged(revs.Registry)));

    private void OnDrainChanged(bool draining)
    {
        if (draining)
            _ = SendAsync(NoticeTargets.SendAsync(DeviceHubMethods.ServerNotice, new ServerNotice(ServerNoticeKinds.Shutdown, drain.RetryAfterSec)));
    }

    /// <summary>收件收進新的版本（AppReleaseStore 只對新版本觸發，同一版重送不觸發）；排空中照送，裝置稍後照 Retry-After 再下載</summary>
    private void OnIngested(AppReleaseInfo info) =>
        _ = SendAsync(NoticeTargets.SendAsync(DeviceHubMethods.ServerNotice, new ServerNotice(ServerNoticeKinds.AppRelease, Release: info.Release)));

    /// <summary>
    /// 用量查詢的指派變了：送給這台的完整連線。只更新的連線（sync-protocol 1.4）不送：它不是候選，UsagePublish 被關卡擋下，
    /// Prove 時拿到的是空清單（桌面以 ProveResponse 的清單為準）；換版交疊時同一台的完整連線照收
    /// </summary>
    private void OnAssigned(string deviceId, UsageAssign message)
    {
        var ids = connections.ConnectionsOf(deviceId).Where(c => !c.UpdateOnly).Select(c => c.ConnectionId).ToList();
        if (ids.Count > 0)
            _ = SendAsync(hub.Clients.Clients(ids).SendAsync(DeviceHubMethods.UsageAssign, message));
    }

    private async Task SendAsync(Task send)
    {
        try
        {
            await send;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "送給裝置的通知失敗");
        }
    }

    public override void Dispose()
    {
        wake.Dispose();
        base.Dispose();
    }
}
