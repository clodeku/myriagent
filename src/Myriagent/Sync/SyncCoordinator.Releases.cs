namespace Myriagent.Sync;

// 協調者的 app 自我更新 U3（設計 7.4、8.2、8.4）：給更新管理員的版本來源用的 HTTP 用戶端（HubReleaseSource，任何執行緒讀）、
// 已配對與否的快照（啟用條件 2；更新管理員在背景執行緒問），與「伺服器說有新版」的事件（UI 執行緒；AppController 接給 AppUpdateCoordinator）
internal sealed partial class SyncCoordinator
{
    /// <summary>伺服器說有新的桌面版（Hello 的 latestApp、ServerNotice{appRelease}；UI 執行緒，舊一代的不送）</summary>
    public event Action<string>? AppReleaseAnnounced;

    /// <summary>最後一次的 app 更新狀態（UI 執行緒）：還沒配對時先記著，引擎建好時交給它</summary>
    private AppStatePayload? lastAppState;

    /// <summary>app 更新狀態（AppUpdateCoordinator.AppStateChanged；UI 執行緒）→ app.state（合併鍵 app，內容沒變不送）</summary>
    public void OfferAppState(AppStatePayload state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lastAppState = state;
        bridge?.OfferApp(state);
    }

    /// <summary>下載新版用的 HTTP 用戶端（任何執行緒；未配對、引擎停了是 null）</summary>
    internal DeviceHttpClient? ReleaseHttp => (Volatile.Read(ref engine) as ISyncEngineReleases)?.ReleaseHttp;

    /// <summary>這台已配對而且同步引擎在（任何執行緒；更新管理員的啟用條件 2）</summary>
    internal bool IsPairedSnapshot => Volatile.Read(ref engine) is not null;

    private void OnAppRelease(int gen, string release) => host.BeginInvoke(() =>
    {
        if (disposed || gen != Volatile.Read(ref generation))
            return;
        AppReleaseAnnounced?.Invoke(release);
    });

    private sealed partial class EnginePort : IAppReleasePort
    {
        public void ReportAppRelease(string release) => owner.OnAppRelease(gen, release);
    }
}
