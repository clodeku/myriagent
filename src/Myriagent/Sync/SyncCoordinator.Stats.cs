namespace Myriagent.Sync;

// 協調者的各電腦即時狀態（sync-protocol 14.8，stat1）：中樞的面板開了或關了（ISyncBridge.OnMonitorWatchChanged）記下來給引擎的 watch，
// 引擎經同一代的 EnginePort（IDeviceStatsPort）取這台的狀態（中樞掛上的 ISystemMonitor，任何執行緒都能讀），送回其他電腦的狀態與往返時間，
// 回 UI 執行緒交給中樞（舊一代的丟掉）。換代、解除配對時交給中樞一份空的
internal sealed partial class SyncCoordinator
{
    /// <summary>這台有視窗開著電腦狀態面板（UI 執行緒寫、引擎執行緒讀）</summary>
    private volatile bool monitorWatching;

    /// <summary>引擎送出前可以用多舊的取樣（面板開著時每秒取樣，送出時通常直接用最近那一份）</summary>
    private static readonly TimeSpan StatsMaxAge = TimeSpan.FromMilliseconds(1500);

    string? ISyncBridge.StatsDeviceId => device?.DeviceId;

    void ISyncBridge.OnMonitorWatchChanged(bool watching)
    {
        monitorWatching = watching;
        (engine as ISyncEngineStats)?.StatsWatchChanged();
    }

    /// <summary>引擎送回的其他電腦的狀態（任何執行緒）：回 UI 執行緒交給中樞（舊一代、已釋放的丟掉）</summary>
    private void OnStatsUpdate(int gen, StatsUpdate update) => host.BeginInvoke(() =>
    {
        if (disposed || gen != Volatile.Read(ref generation))
            return;
        hub.OnRemoteStats(update.Devices, update.Hub);
    });

    private void OnStatsRtt(int gen, int? rttMs) => host.BeginInvoke(() =>
    {
        if (disposed || gen != Volatile.Read(ref generation))
            return;
        hub.SetStatsRtt(rttMs);
    });

    /// <summary>換代或解除配對（UI 執行緒）：中樞的其他電腦與往返時間清空</summary>
    private void ClearStats()
    {
        hub.OnRemoteStats([], null);
        hub.SetStatsRtt(null);
    }

    private sealed partial class EnginePort : IDeviceStatsPort
    {
        // 電腦狀態面板的檔案傳輸：這台正在接收的檔（devicePut）跟著狀態送（給人看的即時資料，可丟）
        public DeviceStats? CurrentStats() => owner.hub.SystemMonitor?.Current(StatsMaxAge).Stats is { } stats
            ? stats with { Xfers = owner.hub.DeviceXferSnapshot() }
            : null;

        public bool Watching => owner.monitorWatching;

        public void DeliverStats(StatsUpdate update) => owner.OnStatsUpdate(gen, update);

        public void ReportRtt(int? rttMs) => owner.OnStatsRtt(gen, rttMs);
    }
}
