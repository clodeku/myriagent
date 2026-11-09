using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.RemoteDesk;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端桌面在中樞（AgentHub.RemoteDesk.cs；docs/sync-protocol.md 14.16）：deskOpen 經真的閘門、管理員與同時一個的檢查、提示列、
/// 看的一方的封包、政策改掉就停、看的一方的網頁封包。畫面與提示列是假的
/// </summary>
public sealed class AgentHubRemoteDeskTests
{
    private const string Session = "01JDESKHUBTEST000000000000";

    private sealed class FakeTransport : IDeskTransport
    {
        public Recorder<DeskPacket> Sent { get; } = new();

        public void SendDeskUp(DeskPacket packet) => Sent.Add(packet);
    }

    private sealed class FakeIndicator : IDeskIndicator
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool attach = true)
        {
            H = new HubHarness();
            H.Ui.Invoke(() =>
            {
                H.Settings.Sync.RemoteControlLevel = SyncSettings.LevelControl;
                H.Hub.DeviceFilesElevatedForTest = () => Elevated;
                if (attach)
                {
                    H.Hub.AttachRemoteDesk(Transport, () => Screen, (text, disconnect) =>
                    {
                        IndicatorText = text;
                        Disconnect = disconnect;
                        Indicator = new FakeIndicator();
                        return Indicator;
                    });
                }
            });
        }

        public HubHarness H { get; }

        public bool Elevated { get; set; }

        public FakeTransport Transport { get; } = new();

        public FakeDeskScreen Screen { get; } = new();

        public string? IndicatorText { get; private set; }

        public Action? Disconnect { get; private set; }

        public FakeIndicator? Indicator { get; private set; }

        public async Task<RemoteOutcome> OpenAsync(string session = Session)
        {
            var command = new JObject { ["cmd"] = RemoteCommandCatalog.DeskOpen, ["deviceId"] = RemoteSamples.DeviceId, ["requestId"] = session };
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, null, null, 2, false, 1_000, 2_000, false, "電腦「書房」");
            return await H.Ui.Invoke(() => H.Hub.ExecuteRemoteAsync(context, command, _ => { })).WaitAsync(TimeSpan.FromSeconds(20));
        }

        public void Down(DeskPacket packet) => H.Ui.Invoke(() => H.Hub.OnDeskDown(packet));

        public void Dispose() => H.Dispose();
    }

    private static void ShouldBeRejected(RemoteOutcome outcome, string code)
    {
        outcome.State.Should().Be(RemoteResultStates.Rejected, outcome.Message);
        outcome.Code.Should().Be(code, outcome.Message);
    }

    [Fact]
    public async Task 沒有接上時回unsupported_管理員回policy()
    {
        using (var rig = new Rig(attach: false))
            ShouldBeRejected(await rig.OpenAsync(), RemoteResultCodes.Unsupported);

        using (var rig = new Rig { Elevated = true })
        {
            ShouldBeRejected(await rig.OpenAsync(), RemoteResultCodes.Policy);
            rig.Indicator.Should().BeNull();
        }
    }

    [Fact]
    public async Task 接下就開始擷取並顯示提示列_第二個conflict_看的一方close就停()
    {
        using var rig = new Rig();

        var outcome = await rig.OpenAsync();

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        rig.IndicatorText.Should().Contain("電腦「書房」").And.Contain("正在遠端操控");
        await rig.H.WaitUntilAsync(() => rig.Transport.Sent.All.Any(p => p.Kind == DeskRules.Frame), "第一個影格");
        rig.H.OnUi(() => rig.H.Hub.RemoteDeskActive).Should().BeTrue();
        ShouldBeRejected(await rig.OpenAsync("01JDESKHUBTEST00000000000B"), RemoteResultCodes.Conflict);

        rig.Down(new DeskPacket(Session, DeskRules.Close, 0, DeskRules.EndBody("viewerClosed", "x")));

        await rig.H.WaitUntilAsync(() => rig.Indicator!.Disposed, "提示列關掉");
        rig.H.OnUi(() => rig.H.Hub.RemoteDeskActive).Should().BeFalse();
        rig.Screen.Released.Should().Be(1);
        (await rig.OpenAsync("01JDESKHUBTEST00000000000C")).State.Should().Be(RemoteResultStates.Done, "結束之後可以再開");
    }

    [Fact]
    public async Task 提示列的中斷告訴看的一方_輸入交給螢幕()
    {
        using var rig = new Rig();
        (await rig.OpenAsync()).State.Should().Be(RemoteResultStates.Done);
        rig.Down(new DeskPacket(Session, DeskRules.Input, 1, new JObject { ["events"] = new JArray(new JObject { ["t"] = "kd", ["k"] = 65 }) }));
        rig.Screen.Injected.All.Should().ContainSingle();

        rig.H.Ui.Invoke(() => rig.Disconnect!());

        await rig.H.WaitUntilAsync(() => rig.Indicator!.Disposed, "提示列關掉");
        var end = rig.Transport.Sent.All.Last(p => p.Kind == DeskRules.End);
        end.Kind.Should().Be(DeskRules.End);
        end.Body!.Value<string>("reason").Should().Be("targetClosed");
    }

    [Fact]
    public async Task 政策不再是可以操作就停()
    {
        using var rig = new Rig();
        (await rig.OpenAsync()).State.Should().Be(RemoteResultStates.Done);
        rig.H.Ui.Invoke(() => rig.H.Settings.Sync.RemoteControlLevel = SyncSettings.LevelView);

        rig.Down(new DeskPacket(Session, DeskRules.Input, 1, new JObject { ["events"] = new JArray(new JObject { ["t"] = "kd", ["k"] = 65 }) }));

        await rig.H.WaitUntilAsync(() => rig.Indicator!.Disposed, "停下");
        rig.Screen.Injected.All.Should().BeEmpty("政策改掉之後的輸入不送進去");
        rig.Transport.Sent.All.Last(p => p.Kind == DeskRules.End).Body!.Value<string>("reason").Should().Be("policy");
        rig.Transport.Sent.All.SkipWhile(p => p.Kind != DeskRules.End).Skip(1).Should().BeEmpty("停下之後不再送影格");
    }

    [Fact]
    public void 看的一方_不是這個視窗開的工作階段回end_不送出()
    {
        using var rig = new Rig();
        rig.H.Ui.Invoke(() => rig.H.Hub.HandleCommand(rig.H.Main, new JObject
        {
            ["cmd"] = "desk", ["sessionId"] = Session, ["kind"] = DeskRules.Input, ["seq"] = 1, ["body"] = new JObject { ["ack"] = 1 },
        }, null));

        rig.Transport.Sent.All.Should().BeEmpty();
        var end = rig.H.Main.All().Last(m => m.Value<string>("type") == "desk");
        end.Value<string>("kind").Should().Be(DeskRules.End);
        end.Value<string>("sessionId").Should().Be(Session);
    }
}
