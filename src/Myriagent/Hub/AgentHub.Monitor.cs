using System.Diagnostics;
using Myriagent.SystemStatus;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 電腦狀態面板（stat1；web-protocol 的 monitor 與 deviceStats）：網頁開著面板而且看得到時送 monitor {on:true}，中樞記下是哪些視窗，
// 有任何一個時這台每秒取樣（ISystemMonitor.Active）、同步引擎的 watch 是 true（收其他電腦的狀態）。deviceStats 只送給開著面板的視窗、
// 最多每秒一則（這台每秒的取樣與伺服器每秒最多一份的其他電腦合併成一則）：第一筆是這台（已配對時 id 是 deviceId、往返時間是同步引擎量的），
// 之後是其他電腦（太久沒更新的不送）。視窗關閉或重新載入（ready）就不算開著，網頁收到 init 之後會再送一次 monitor。只在 UI 執行緒
internal sealed partial class AgentHub
{
    private const long StatsPostIntervalMs = DeviceStatsRules.FanoutIntervalMs;

    private ISystemMonitor? systemMonitor;
    private readonly HashSet<string> monitorViews = new(StringComparer.Ordinal);
    private IReadOnlyList<DeviceStatsEntry> remoteStats = [];
    private HubStatsEntry? remoteHub;

    /// <summary>收到 <see cref="remoteStats"/> 的時間（Stopwatch 時間戳；算每筆送出時已經多舊）</summary>
    private long remoteStatsAt;

    private int? statsRtt;
    private bool statsPostScheduled;
    private long? lastStatsPostMs;

    /// <summary>這台的狀態來源（AppController 掛上；沒有時面板只有其他電腦）。同步引擎從任何執行緒讀它（掛上之後不再換）</summary>
    internal ISystemMonitor? SystemMonitor => systemMonitor;

    /// <summary>掛上這台的狀態來源（建好中樞之後、同步啟動之前；只掛一次）</summary>
    internal void AttachSystemMonitor(ISystemMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        systemMonitor = monitor;
        monitor.Sampled += _ => host.BeginInvoke(ScheduleStatsPost);
        monitor.Active = monitorViews.Count > 0;
    }

    /// <summary>有沒有視窗開著電腦狀態面板</summary>
    internal bool MonitorWatched => monitorViews.Count > 0;

    /// <summary>同步送來的其他電腦與 MyriagentHub 主機的狀態（整份取代；連線斷了是空的、主機是 null）</summary>
    internal void OnRemoteStats(IReadOnlyList<DeviceStatsEntry> entries, HubStatsEntry? hub)
    {
        remoteStats = entries ?? [];
        remoteHub = hub;
        remoteStatsAt = Stopwatch.GetTimestamp();
        ScheduleStatsPost();
    }

    /// <summary>這台和伺服器最近一次量到的往返時間（沒連線是 null）；下一則 deviceStats 帶上</summary>
    internal void SetStatsRtt(int? rttMs) => statsRtt = rttMs;

    /// <summary>monitor 指令：開了就立刻送這個視窗一則（有資料的話），之後跟著節流</summary>
    private void SetMonitor(IAgentView view, bool on)
    {
        if (view is RemoteResponseView)
            return;
        var before = monitorViews.Count > 0;
        var changed = on ? monitorViews.Add(view.Id) : monitorViews.Remove(view.Id);
        if (!changed)
            return;
        ApplyMonitorDemand(before);
        if (on)
        {
            var message = BuildDeviceStats();
            TryPost([view], () => message);
        }
    }

    /// <summary>視窗關閉或重新載入：不再算開著面板</summary>
    private void ForgetMonitorView(string viewId)
    {
        var before = monitorViews.Count > 0;
        if (monitorViews.Remove(viewId))
            ApplyMonitorDemand(before);
    }

    private void ApplyMonitorDemand(bool before)
    {
        var now = monitorViews.Count > 0;
        if (systemMonitor is { } monitor)
            monitor.Active = now;
        if (now != before)
            sync?.OnMonitorWatchChanged(now);
    }

    /// <summary>距上一則不到 1 秒就排到滿 1 秒（期間的變動併進同一則）；沒有視窗開著面板時不排</summary>
    private void ScheduleStatsPost()
    {
        if (disposed || statsPostScheduled || monitorViews.Count == 0)
            return;
        statsPostScheduled = true;
        var wait = lastStatsPostMs is { } last ? StatsPostIntervalMs - (Environment.TickCount64 - last) : 0;
        if (wait <= 0)
            host.BeginInvoke(FlushStats);
        else
            _ = Task.Delay(TimeSpan.FromMilliseconds(wait)).ContinueWith(_ => host.BeginInvoke(FlushStats), TaskScheduler.Default);
    }

    private void FlushStats()
    {
        statsPostScheduled = false;
        if (disposed || monitorViews.Count == 0)
            return;
        lastStatsPostMs = Environment.TickCount64;
        var message = BuildDeviceStats();
        var targets = monitorViews.Select(id => views.TryGetValue(id, out var v) ? v : null).Where(v => v is not null).Select(v => v!).ToList();
        TryPost(targets, () => message);
    }

    /// <summary>deviceStats（web-protocol 第一節）：這台第一、之後是其他電腦（整份取代，兩邊都沒有資料時是空的清單），hub 是 MyriagentHub 主機（沒有或太久沒更新是 null）</summary>
    private JObject BuildDeviceStats()
    {
        var selfId = sync?.StatsDeviceId;
        var devices = new JArray();
        if (systemMonitor?.Latest is { } sample)
        {
            var ageMs = (long)Stopwatch.GetElapsedTime(sample.TakenAt).TotalMilliseconds;
            devices.Add(DeviceStatsRules.WebView(selfId, self: true, Environment.MachineName, sample.Seq, ageMs, sample.Stats with { RttMs = statsRtt }));
        }
        var elapsed = remoteStats.Count == 0 && remoteHub is null ? 0 : (long)Stopwatch.GetElapsedTime(remoteStatsAt).TotalMilliseconds;
        foreach (var entry in remoteStats)
        {
            var ageMs = entry.AgeMs + elapsed;
            if (string.Equals(entry.DeviceId, selfId, StringComparison.Ordinal) || ageMs > DeviceStatsRules.StaleAfterMs)
                continue;
            devices.Add(DeviceStatsRules.WebView(entry.DeviceId, self: false, name: null, entry.Seq, ageMs, entry.Stats));
        }
        var hub = remoteHub is { } h && h.AgeMs + elapsed <= DeviceStatsRules.StaleAfterMs ? DeviceStatsRules.HubWebView(h, h.AgeMs + elapsed) : null;
        return new JObject { ["type"] = "deviceStats", ["devices"] = devices, ["hub"] = hub };
    }
}
