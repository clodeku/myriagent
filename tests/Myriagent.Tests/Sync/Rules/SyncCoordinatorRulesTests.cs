using Myriagent.Core;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Pairing;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Rules;

public sealed class SyncCoordinatorRulesTests
{
    [Fact]
    public void HelloPolicy_遠端暫停時Off_否則取設定()
    {
        var settings = new SyncSettings { RemoteControlLevel = "Control", AllowRemoteFullAccess = true };

        SyncCoordinatorRules.HelloPolicy(settings).Should().Be(new DevicePolicy("Control", true));
        settings.RemotePaused = true;
        SyncCoordinatorRules.HelloPolicy(settings).Should().Be(new DevicePolicy("Off", false));
        SyncCoordinatorRules.HelloPolicy(new SyncSettings { RemoteControlLevel = "Admin" }).Should().Be(new DevicePolicy("Off", false), "不認得的層級當成 Off");
    }

    [Fact]
    public void ParsePolicy_任一可省略_不合格整個忽略()
    {
        var current = new SyncSettings { RemoteControlLevel = "View", AllowRemoteFullAccess = true };

        SyncCoordinatorRules.ParsePolicy(new JObject { ["remoteControlLevel"] = "Control" }, current).Should().Be(("Control", true));
        SyncCoordinatorRules.ParsePolicy(new JObject { ["allowRemoteFullAccess"] = false }, current).Should().Be(("View", false));
        SyncCoordinatorRules.ParsePolicy(new JObject(), current).Should().Be(("View", true));
        SyncCoordinatorRules.ParsePolicy(new JObject { ["remoteControlLevel"] = "Admin", ["allowRemoteFullAccess"] = false }, current).Should().BeNull();
        SyncCoordinatorRules.ParsePolicy(new JObject { ["remoteControlLevel"] = "off" }, current).Should().BeNull("大小寫要完全一樣");
        SyncCoordinatorRules.ParsePolicy(new JObject { ["remoteControlLevel"] = 1 }, current).Should().BeNull();
        SyncCoordinatorRules.ParsePolicy(new JObject { ["allowRemoteFullAccess"] = "true" }, current).Should().BeNull();
        SyncCoordinatorRules.ParsePolicy(new JObject { ["remoteControlLevel"] = JValue.CreateNull() }, current).Should().BeNull();
    }

    [Fact]
    public void ParseNotifyRemoteActions_沒給沿用_布林照收_其他整個忽略()
    {
        var on = new SyncSettings();
        var off = new SyncSettings { NotifyRemoteActions = false };

        on.NotifyRemoteActions.Should().BeTrue("D8 預設開");
        SyncCoordinatorRules.ParseNotifyRemoteActions(new JObject(), on).Should().BeTrue("沒給沿用目前的值");
        SyncCoordinatorRules.ParseNotifyRemoteActions(new JObject(), off).Should().BeFalse();
        SyncCoordinatorRules.ParseNotifyRemoteActions(new JObject { ["notifyRemoteActions"] = false }, on).Should().BeFalse();
        SyncCoordinatorRules.ParseNotifyRemoteActions(new JObject { ["notifyRemoteActions"] = true }, off).Should().BeTrue();
        SyncCoordinatorRules.ParseNotifyRemoteActions(new JObject { ["notifyRemoteActions"] = "false" }, on).Should().BeNull();
        SyncCoordinatorRules.ParseNotifyRemoteActions(new JObject { ["notifyRemoteActions"] = 0 }, on).Should().BeNull();
        SyncCoordinatorRules.ParseNotifyRemoteActions(new JObject { ["notifyRemoteActions"] = JValue.CreateNull() }, on).Should().BeNull("同 ParsePolicy：JSON 的 null 不合格");
        SyncCoordinatorRules.ParseNotifyRemoteActions(new JObject { ["remoteControlLevel"] = "Admin" }, off).Should().BeFalse("只看自己的欄位；政策的欄位由 ParsePolicy 判斷");
    }

    [Fact]
    public void PairServer_沒給或不合格null()
    {
        SyncCoordinatorRules.PairServer(null).Should().BeNull("公開版沒有預設的 Hub：配對一定要給自己的網址");
        SyncCoordinatorRules.PairServer(JValue.CreateNull()).Should().BeNull();
        SyncCoordinatorRules.PairServer(new JValue("https://hub.example.com")).Should().Be(new Uri("https://hub.example.com"));
        SyncCoordinatorRules.PairServer(new JValue("https://127.0.0.1:5443/")).Should().Be(new Uri("https://127.0.0.1:5443"));
        SyncCoordinatorRules.PairServer(new JValue("http://hub.test")).Should().BeNull();
        SyncCoordinatorRules.PairServer(new JValue("https://hub.test/api")).Should().BeNull();
        SyncCoordinatorRules.PairServer(new JValue("https://user@hub.test")).Should().BeNull();
        SyncCoordinatorRules.PairServer(new JValue(443)).Should().BeNull();
        SyncCoordinatorRules.PairServer(new JArray("https://hub.test")).Should().BeNull();
    }

    [Fact]
    public void DevicesDelay_第一次立刻_未滿500ms等到滿()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1791600000000);

        SyncCoordinatorRules.DevicesDelay(null, now).Should().BeNull();
        SyncCoordinatorRules.DevicesDelay(now.AddMilliseconds(-500), now).Should().BeNull();
        SyncCoordinatorRules.DevicesDelay(now.AddMilliseconds(-120), now).Should().Be(TimeSpan.FromMilliseconds(380));
        SyncCoordinatorRules.DevicesDelay(now, now).Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void 已配對的初始狀態與配對中的狀態()
    {
        var device = PairingTestData.Device();

        var initial = SyncCoordinatorRules.Initial(device, null);
        SyncViewRules.StatusView(initial).Value<string>("state").Should().Be("connecting");
        initial.DeviceId.Should().Be(device.DeviceId);
        initial.DeviceFingerprint.Should().Be(device.DeviceFingerprint);
        initial.HubFingerprint.Should().Be(device.HubFingerprint);
        initial.Server.Should().Be(PairingTestData.Server);
        initial.Detail.Should().BeNull();
        var dev = SyncCoordinatorRules.Initial(device, new Uri("https://127.0.0.1:5443"));
        dev.Server.Should().Be("https://127.0.0.1:5443");
        dev.DevMode.Should().BeTrue();
        dev.Detail.Should().Be(SyncStatusRules.DevModeNote);

        var pairing = SyncCoordinatorRules.Pairing(new Uri(PairingTestData.Server), null, SyncCoordinatorRules.PairingStarting, null);
        var view = SyncViewRules.StatusView(pairing);
        view.Value<string>("state").Should().Be("pairing");
        view.Value<string>("deviceId").Should().BeNull();
        view.Value<string>("server").Should().Be(PairingTestData.Server);
        view["pairing"]!.Value<string>("userCode").Should().BeEmpty("還沒拿到配對碼");
    }

    [Fact]
    public void Options_FromVariables_讀三個環境變數()
    {
        var env = new Dictionary<string, string>
        {
            ["MYRIAGENT_SYNC_SERVER"] = "https://127.0.0.1:5443",
            ["MYRIAGENT_SYNC_DEV_TRUST"] = "1",
            ["MYRIAGENT_SYNC_NO_TPM"] = "1",
        };

        var options = SyncCoordinatorOptions.FromVariables(k => env.TryGetValue(k, out var v) ? v : null, "0.5.0", "OFFICE-PC", "user");
        options.Should().Be(new SyncCoordinatorOptions(true, new Uri("https://127.0.0.1:5443"), false, "0.5.0", "OFFICE-PC", "user"));

        var none = SyncCoordinatorOptions.FromVariables(_ => null, "0.5.0", null, null);
        none.Should().Be(new SyncCoordinatorOptions(false, null, true, "0.5.0", null, null));

        env["MYRIAGENT_SYNC_SERVER"] = "http://127.0.0.1:5000";
        env["MYRIAGENT_SYNC_DEV_TRUST"] = "true";
        var invalid = SyncCoordinatorOptions.FromVariables(k => env.TryGetValue(k, out var v) ? v : null, "0.5.0", null, null);
        invalid.ServerOverride.Should().BeNull("不是 https：忽略");
        invalid.DevTrustEnv.Should().BeFalse("只認 1");
    }
}
