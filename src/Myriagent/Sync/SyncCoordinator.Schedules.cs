using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 協調者的各電腦的排程（sync-protocol 14.12，sched1）：中樞的摘要變了（ISyncBridge.OnSchedulesChanged）就通知引擎，
// 引擎經同一代的 EnginePort（IScheduleSyncPort）取中樞存好的摘要（不會再被改動的 JObject，任何執行緒都能讀）；
// 伺服器送來的其他電腦的摘要回 UI 執行緒交給中樞（排程頁的其他電腦；舊一代的丟掉）。解除配對時清掉
internal sealed partial class SyncCoordinator
{
    void ISyncBridge.OnSchedulesChanged() => (engine as ISyncEngineSchedules)?.SchedulesChanged();

    private void OnOtherSchedules(int gen, IReadOnlyList<DeviceSchedulesView> devices) => host.BeginInvoke(() =>
    {
        if (disposed || gen != Volatile.Read(ref generation))
            return;
        hub.ApplyOtherSchedules(devices);
    });

    /// <summary>解除配對（UI 執行緒）：排程頁只剩這台</summary>
    private void ClearOtherSchedules() => hub.ApplyOtherSchedules(Array.Empty<DeviceSchedulesView>());

    private sealed partial class EnginePort : IScheduleSyncPort
    {
        public JObject? CurrentSchedules() => owner.hub.ScheduleSyncSnapshot();

        public void DeliverOtherSchedules(IReadOnlyList<DeviceSchedulesView> devices) => owner.OnOtherSchedules(gen, devices);
    }
}
