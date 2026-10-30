using Myriagent.Accounts;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.SystemStatus;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 電腦狀態面板（web-protocol 的 monitor／deviceStats，sync-protocol 14.8）：只送給開著面板的視窗；有視窗開著時這台才每秒取樣、同步才收其他電腦；
/// 這台第一（未配對時 id 是 null）、其他電腦與 MyriagentHub 主機照同步給的，自己那一筆與太久沒更新的不送；視窗關閉或重新載入就不算開著
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubMonitorTests
{
    private static readonly DeviceStats Sample = new(37.5, 6L << 30, 16L << 30, [new DiskStats("C:", 200L << 30, 500L << 30)], 1_000, 200, Cores: 8);

    [Fact]
    public async Task 開了面板才取樣與送_關了停止_只送給開著的視窗()
    {
        using var h = new HubHarness();
        var monitor = new FakeMonitor();
        var bridge = new StatsBridge();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachSync(bridge);
            h.Hub.AttachSystemMonitor(monitor);
        });
        var other = h.AddView("other");
        monitor.Active.Should().BeFalse("沒有視窗開著面板時不取樣");

        var mark = h.Main.Mark();
        h.Command(h.Main, "monitor", new { on = true });
        monitor.Active.Should().BeTrue();
        bridge.Watching.Should().Equal(true);
        var first = await h.Main.WaitForAsync("deviceStats", since: mark);
        var self = (JObject)((JArray)first["devices"]!).Single();
        self.Value<bool>("self").Should().BeTrue();
        self["id"]!.Type.Should().Be(JTokenType.Null, "未配對的這台");
        self.Value<string>("name").Should().Be(Environment.MachineName);
        self.Value<double>("cpu").Should().Be(37.5);
        first["hub"]!.Type.Should().Be(JTokenType.Null);

        monitor.Raise(Sample with { Cpu = 80 });
        var next = await h.Main.WaitForAsync("deviceStats", m => m["devices"]![0]!.Value<double>("cpu") == 80, mark, timeoutMs: 4000);
        next["devices"]![0]!.Value<long>("seq").Should().Be(2);
        other.Messages("deviceStats").Should().BeEmpty("沒開面板的視窗不送");

        h.Command(h.Main, "monitor", new { on = false });
        monitor.Active.Should().BeFalse();
        bridge.Watching.Should().Equal(true, false);
    }

    [Fact]
    public async Task 其他電腦與主機_略過自己_太久沒更新的不送_斷線清空()
    {
        using var h = new HubHarness();
        var monitor = new FakeMonitor();
        var self = SyncIds.NewDeviceId();
        var bridge = new StatsBridge { DeviceId = self };
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachSync(bridge);
            h.Hub.AttachSystemMonitor(monitor);
        });
        h.Command(h.Main, "monitor", new { on = true });
        var other = SyncIds.NewDeviceId();
        var stale = SyncIds.NewDeviceId();
        var mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.OnRemoteStats(
            [
                new DeviceStatsEntry(self, 9, 100, Sample with { Cpu = 1 }),
                new DeviceStatsEntry(other, 4, 1_500, Sample with { Cpu = 55 }),
                new DeviceStatsEntry(stale, 5, DeviceStatsRules.StaleAfterMs + 1, Sample),
            ],
            new HubStatsEntry("HUB-PC", 3, 800, Sample with { Cpu = 12 }, Devices: 2, Browsers: 1)));
        h.Ui.Invoke(() => h.Hub.SetStatsRtt(48));

        var message = await h.Main.WaitForAsync("deviceStats", m => ((JArray)m["devices"]!).Count == 2, mark, timeoutMs: 4000);
        var devices = (JArray)message["devices"]!;
        devices[0]!.Value<string>("id").Should().Be(self, "已配對：這台的 id 是 deviceId");
        devices[0]!.Value<bool>("self").Should().BeTrue();
        devices[1]!.Value<string>("id").Should().Be(other);
        devices[1]!.Value<bool>("self").Should().BeFalse();
        devices[1]!["name"]!.Type.Should().Be(JTokenType.Null, "其他電腦的名稱由網頁照 id 查");
        devices[1]!.Value<long>("ageMs").Should().BeGreaterThanOrEqualTo(1_500);
        var hub = (JObject)message["hub"]!;
        hub.Value<string>("name").Should().Be("HUB-PC");
        hub.Value<int>("devices").Should().Be(2);
        hub.Value<int>("browsers").Should().Be(1);
        hub.Should().NotContainKey("rttMs");

        monitor.Raise(Sample);
        var withRtt = await h.Main.WaitForAsync("deviceStats", m => m["devices"]![0]!["rttMs"]!.Type == JTokenType.Integer, mark, timeoutMs: 4000);
        withRtt["devices"]![0]!.Value<int>("rttMs").Should().Be(48, "這台的往返時間是同步引擎量的");

        mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.OnRemoteStats([], null));
        var cleared = await h.Main.WaitForAsync("deviceStats", m => ((JArray)m["devices"]!).Count == 1, mark, timeoutMs: 4000);
        cleared["hub"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 視窗關閉或重新載入就不算開著()
    {
        using var h = new HubHarness();
        var monitor = new FakeMonitor();
        var bridge = new StatsBridge();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachSync(bridge);
            h.Hub.AttachSystemMonitor(monitor);
        });
        var popout = h.AddView("second");
        h.Command(h.Main, "monitor", new { on = true });
        h.Command(popout, "monitor", new { on = true });
        h.Command(h.Main, "ready");
        monitor.Active.Should().BeTrue("另一個視窗還開著");
        h.Ui.Invoke(() => h.Hub.DetachView(popout.Id));
        monitor.Active.Should().BeFalse();
        bridge.Watching.Should().Equal(true, false);
    }

    /// <summary>手動送樣本的取樣來源</summary>
    private sealed class FakeMonitor : ISystemMonitor
    {
        private long seq = 1;
        private SystemSample latest = new(1, System.Diagnostics.Stopwatch.GetTimestamp(), Sample);

        public bool Active { get; set; }

        public event Action<SystemSample>? Sampled;

        public SystemSample? Latest => latest;

        public SystemSample Current(TimeSpan maxAge) => latest;

        public void Raise(DeviceStats stats)
        {
            latest = new SystemSample(++seq, System.Diagnostics.Stopwatch.GetTimestamp(), stats);
            Sampled?.Invoke(latest);
        }
    }

    /// <summary>只記 OnMonitorWatchChanged 的同步橋</summary>
    private sealed class StatsBridge : ISyncBridge
    {
        public string? DeviceId { get; init; }

        public List<bool> Watching { get; } = [];

        string? ISyncBridge.StatsDeviceId => DeviceId;

        void ISyncBridge.OnMonitorWatchChanged(bool watching) => Watching.Add(watching);

        public bool HandleCommand(IAgentView view, string type, JObject msg) => false;

        public void OnAccountUpdated(string accountId)
        {
        }

        public void OnAccountsReordered()
        {
        }

        public void OnRoamingSettingChanged(string key, JToken value)
        {
        }

        public JObject? InitRemote() => null;

        public bool IsStarting => false;

        public JObject? DevicesCli() => null;

        public void DecorateAccounts(JArray accounts)
        {
        }

        public CatalogAccountTemplate? ClaimCatalogAccount(string? logicalAccountId, out string failure)
        {
            failure = string.Empty;
            return null;
        }

        public string? ExpectedEmail(string localAccountId) => null;
    }
}
