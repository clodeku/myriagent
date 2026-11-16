using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.SystemStatus;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 電腦選單的喇叭音量、立即關閉螢幕（AgentHub.DeviceControl.cs；2026-11-17）：這台的卡片宿主自己做、只回發起的視窗；
/// 其他電腦經型錄的 deviceControl（閘門要「可以操作」），做完以 deviceControlState 回
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubDeviceControlTests
{
    private const string CommandId = "01J9QZ3K8M5R7T2V4W6X8Y0A";

    private static int sequence;

    private sealed class FakeControls : IDeviceControls
    {
        public int Volume { get; set; } = 40;

        public bool Muted { get; set; }

        public bool HasDevice { get; set; } = true;

        public int ScreenOffs { get; private set; }

        public (int Volume, bool Muted)? GetAudio() => HasDevice ? (Volume, Muted) : null;

        public (int Volume, bool Muted)? SetAudio(int? volume, bool? mute)
        {
            if (!HasDevice)
                return null;
            if (volume is { } v)
                Volume = v;
            if (mute is { } m)
                Muted = m;
            return (Volume, Muted);
        }

        public bool ScreenOff()
        {
            ScreenOffs++;
            return true;
        }
    }

    private static FakeControls Attach(HubHarness h)
    {
        var controls = new FakeControls();
        h.Ui.Invoke(() => h.Hub.AttachDeviceControls(controls));
        return controls;
    }

    private static async Task<JObject> LocalAsync(HubHarness h, object args)
    {
        var mark = h.Main.Mark();
        h.Command(h.Main, "deviceControl", args);
        JObject? state = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (state is null && DateTime.UtcNow < deadline)
        {
            await h.Ui.DrainAsync();
            state = h.Main.Messages("deviceControlState", mark).LastOrDefault();
            if (state is null)
                await Task.Delay(20);
        }

        return state ?? throw new TimeoutException("沒有收到 deviceControlState");
    }

    [Fact]
    public async Task 這台的卡片_讀與設定音量_關螢幕_結果只回這個視窗()
    {
        using var h = new HubHarness();
        var controls = Attach(h);

        var read = await LocalAsync(h, new { action = "audioGet", requestId = "dc-1" });
        read.Value<int>("volume").Should().Be(40);
        read.Value<bool>("muted").Should().BeFalse();
        read.Value<string>("requestId").Should().Be("dc-1");

        var set = await LocalAsync(h, new { action = "audioSet", volume = "75", mute = true, requestId = "dc-2" });
        (set.Value<int>("volume"), set.Value<bool>("muted"), controls.Volume, controls.Muted).Should().Be((75, true, 75, true));

        var off = await LocalAsync(h, new { action = "screenOff", requestId = "dc-3" });
        off["error"]!.Type.Should().Be(JTokenType.Null);
        controls.ScreenOffs.Should().Be(1);
    }

    [Fact]
    public async Task 不合格與沒有播放裝置時以error說明()
    {
        using var h = new HubHarness();
        var controls = Attach(h);
        (await LocalAsync(h, new { action = "audioSet", requestId = "a" })).Value<string>("error").Should().Contain("音量或靜音");
        (await LocalAsync(h, new { action = "audioSet", volume = "101", requestId = "b" })).Value<string>("error").Should().Contain("0～100");
        controls.HasDevice = false;
        (await LocalAsync(h, new { action = "audioGet", requestId = "c" })).Value<string>("error").Should().Contain("播放裝置");
    }

    [Fact]
    public async Task 沒接上調整的來源_這台說明_遠端回unsupported()
    {
        using var h = new HubHarness();
        (await LocalAsync(h, new { action = "audioGet", requestId = "x" })).Value<string>("error").Should().Contain("不能調整");
        AllowControl(h);
        var outcome = await RemoteAsync(h, new JObject { ["action"] = "audioGet" }, []);
        outcome.State.Should().Be(RemoteResultStates.Rejected);
        outcome.Code.Should().Be(RemoteResultCodes.Unsupported);
    }

    [Fact]
    public async Task 其他電腦的_閘門要可以操作_做完回deviceControlState()
    {
        using var h = new HubHarness();
        var controls = Attach(h);
        AllowControl(h, SyncSettings.LevelView);
        var outputs = new List<JObject>();
        var denied = await RemoteAsync(h, new JObject { ["action"] = "audioSet", ["volume"] = "10" }, outputs);
        denied.State.Should().Be(RemoteResultStates.Rejected);
        controls.Volume.Should().Be(40, "只能看的政策不調整");

        AllowControl(h);
        var done = await RemoteAsync(h, new JObject { ["action"] = "audioSet", ["volume"] = "10", ["requestId"] = "r1" }, outputs);
        done.State.Should().Be(RemoteResultStates.Done, done.Message);
        controls.Volume.Should().Be(10);
        outputs.Should().ContainSingle(o => o.Value<string>("type") == DeviceControlRules.StateType && o.Value<int>("volume") == 10 && o.Value<string>("requestId") == "r1");
        outputs.Clear();

        var bad = await RemoteAsync(h, new JObject { ["action"] = "audioSet", ["volume"] = "abc" }, outputs);
        bad.Code.Should().Be(RemoteResultCodes.Invalid);
    }

    private static void AllowControl(HubHarness h, string level = SyncSettings.LevelControl)
        => h.Ui.Invoke(() =>
        {
            h.Settings.Sync.RemoteControlLevel = level;
            h.Settings.Sync.RemotePaused = false;
        });

    private static Task<RemoteOutcome> RemoteAsync(HubHarness h, JObject extra, List<JObject> outputs)
    {
        var command = new JObject { ["cmd"] = RemoteCommandCatalog.DeviceControl, ["deviceId"] = RemoteSamples.DeviceId, ["requestId"] = "dc-r" };
        foreach (var (key, value) in extra)
            command[key] = value;
        var context = new RemoteViewContext(CommandId + Interlocked.Increment(ref sequence), RemoteSamples.DeviceId, null, null, 2, false, null, 2_000, false,
            RemoteNotifyRules.IssuerDisplay("device", "LAPTOP"));
        return h.Ui.Invoke(() => h.Hub.ExecuteRemoteAsync(context, command, o => outputs.Add(o))).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
