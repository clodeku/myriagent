using Myriagent.Core;
using Myriagent.RemoteExec;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteExec;

/// <summary>「允許其他電腦的代理在這台執行指令」的設定、Hello 的政策、電腦清單與 app 自我更新的阻擋（docs/sync-protocol.md 14.11）</summary>
public sealed class RemoteExecSettingsTests
{
    [Fact]
    public void Hello的政策_層級是Control而且開著才是true_暫停時一律關()
    {
        var settings = new SyncSettings { RemoteControlLevel = SyncSettings.LevelControl, AllowRemoteExec = true };
        SyncCoordinatorRules.HelloPolicy(settings).Should().Be(new DevicePolicy("Control", false, true));
        SyncCoordinatorRules.HelloPolicy(new SyncSettings { RemoteControlLevel = SyncSettings.LevelView, AllowRemoteExec = true }).AllowRemoteExec.Should().BeNull();
        SyncCoordinatorRules.HelloPolicy(new SyncSettings { RemoteControlLevel = SyncSettings.LevelControl }).AllowRemoteExec.Should().BeNull();
        settings.RemotePaused = true;
        SyncCoordinatorRules.HelloPolicy(settings).Should().Be(new DevicePolicy("Off", false));
        SyncJson.Serialize(new DevicePolicy("Control", false)).Should().NotContain("allowRemoteExec", "沒開時線上省略（舊版與黃金檔不變）");
    }

    [Fact]
    public void syncSetPolicy的allowRemoteExec()
    {
        var current = new SyncSettings { AllowRemoteExec = true };
        SyncCoordinatorRules.ParseAllowRemoteExec(new JObject(), current).Should().BeTrue("沒給沿用目前的值");
        SyncCoordinatorRules.ParseAllowRemoteExec(new JObject { ["allowRemoteExec"] = false }, current).Should().BeFalse();
        SyncCoordinatorRules.ParseAllowRemoteExec(new JObject { ["allowRemoteExec"] = "true" }, current).Should().BeNull();
        SyncCoordinatorRules.ParseAllowRemoteExec(new JObject { ["allowRemoteExec"] = null }, current).Should().BeNull();
    }

    [Fact]
    public void 電腦清單_名冊與device_upsert合起來()
    {
        const string self = "d_01J9QZ3K8M5R7T2V4W6X8Y0AAA";
        const string laptop = "d_01J9QZ3K8M5R7T2V4W6X8Y0BBB";
        const string old = "d_01J9QZ3K8M5R7T2V4W6X8Y0CCC";
        var view = new SyncCatalogView(self,
            new Dictionary<string, AccountUpsertPayload>(),
            new Dictionary<string, DeviceUpsertPayload>
            {
                [laptop] = new(laptop, "Laptop", "LT-01", "0.2.1", "cng", "fp", 1, 2, true, new DevicePolicy("Control", false, true), null,
                    [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1]),
                [self] = new(self, "Desk", null, "0.2.1", "cng", "fp", 1, 2, true, new DevicePolicy("Off", false), null),
            },
            [
                new DeviceRegistryEntry(self, "Desk", "spki", 1),
                new DeviceRegistryEntry(laptop, "Laptop", "spki", 1),
                new DeviceRegistryEntry(old, "Old", "spki", 1, RevokedAt: 5, RevokedReason: "retired"),
            ],
            new Dictionary<string, string>(),
            new Dictionary<string, DeviceRevokedPayload>());
        var devices = RemoteExecDirectoryRules.Devices(view);
        devices.Select(d => d.Name).Should().Equal("Desk", "Laptop", "Old");
        devices[0].Self.Should().BeTrue();
        devices[1].Should().Be(new RemoteExecDevice(laptop, "Laptop", "LT-01", false, true, false, true, "Control", true));
        devices[2].Revoked.Should().BeTrue();
        devices[2].Online.Should().BeFalse();
    }

    [Fact]
    public void app自我更新_有遠端執行指令在跑時不自動開始()
    {
        var situation = new AppRestartSituation(true, false, TimeSpan.FromHours(1), false, false, false, DateTime.UtcNow, null, null, false, RemoteExecRunning: true);
        AppUpdateRestartRules.Blocker(situation).Should().Be(AppRestartBlocker.RemoteExecRunning);
        AppUpdateRestartRules.CanBegin(situation, TimeSpan.FromMinutes(5)).Should().BeFalse();
        AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.RemoteExecRunning).Should().Contain("遠端指令");
        AppUpdateRestartRules.Blocker(situation with { UserRequested = true }).Should().Be(AppRestartBlocker.None, "按「立即重啟」照樣開始");
    }
}
