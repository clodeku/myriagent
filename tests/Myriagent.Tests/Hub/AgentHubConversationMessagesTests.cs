using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 對話之間互送訊息（AgentHub.ConversationMessages.cs；2026-11-15 使用者要求：A 電腦的對話 A1 的代理通知 B 電腦的對話 B1 的代理）：
/// myriagent-remote conversations 列出各台的對話與 ID，send 照電腦與對話 ID（或標題）送——這台的直接送、其他電腦的以型錄的 send 經同步送
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubConversationMessagesTests
{
    private const string OwnDevice = "d_this";

    private const string OtherDevice = "d_other";

    private sealed class FakeSink : IPeerCommandSink
    {
        public List<(string CommandId, JObject Cmd)> Calls { get; } = [];

        public string? DeviceId => OwnDevice;

        public void SubmitPeerCommand(string commandId, JObject cmd) => Calls.Add((commandId, cmd));
    }

    private static RemoteExecDirectory Directory(bool otherOnline = true) => new(true, true, OwnDevice,
    [
        new RemoteExecDevice(OwnDevice, "PC-A", "PC-A", true, true, false, true, SyncSettings.LevelControl, false),
        new RemoteExecDevice(OtherDevice, "PC-B", "PC-B", false, otherOnline, false, true, SyncSettings.LevelControl, false),
    ]);

    private static RemoteExecPipeRequest Send(string device, string target, string text)
        => new(RemoteExecPipeRules.OpSend, "x", "t", device, null, null, null, text) { Target = target };

    private static RemoteExecPipeRequest List(string? device = null)
        => new(RemoteExecPipeRules.OpConversations, "x", "t", device, null, null, null, null);

    private static Task<JObject> Handle(HubHarness h, string caller, RemoteExecPipeRequest request, RemoteExecDirectory? directory = null)
        => h.Ui.Invoke(() => h.Hub.HandleConversationCli(caller, request, directory ?? Directory())).WaitAsync(TimeSpan.FromSeconds(10));

    private static (string Id, FakeSession Session) Idle(HubHarness h, string title)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentTextCompletedEvent("m:0", "好"));
        h.CompleteTurn(s);
        h.Command(h.Main, "rename", new { conversationId = id, title });
        return (id, s);
    }

    [Fact]
    public async Task 列出這台與其他電腦的對話_ID是小寫的線上id_標出所在的電腦()
    {
        using var h = new HubHarness();
        var (a1, _) = Idle(h, "A1 報表");
        var (b1, _) = Idle(h, "B1 部署");
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(b1, ownerDeviceId: OtherDevice));

        var all = await Handle(h, a1, List());
        all.Value<string>("t").Should().Be("conversations");
        var items = all["items"]!.Children<JObject>().ToList();
        items.Should().Contain(i => i.Value<string>("id") == a1.ToLowerInvariant() && i.Value<string>("device") == "PC-A" && i.Value<bool>("self") && i.Value<bool>("current"));
        items.Should().Contain(i => i.Value<string>("id") == b1.ToLowerInvariant() && i.Value<string>("device") == "PC-B" && !i.Value<bool>("self")
                                    && i.Value<string>("deviceId") == OtherDevice);

        var onlyB = await Handle(h, a1, List("PC-B"));
        onlyB["items"]!.Children<JObject>().Select(i => i.Value<string>("title")).Should().Equal("B1 部署");
        var onlyHere = await Handle(h, a1, List("這台"));
        onlyHere["items"]!.Children<JObject>().Should().NotContain(i => i.Value<string>("title") == "B1 部署");
    }

    [Fact]
    public async Task 送給這台的對話_照一般的送出_前面註明來源與怎麼回覆()
    {
        using var h = new HubHarness();
        var (a1, _) = Idle(h, "A1 報表");
        var (b1, s) = Idle(h, "B1 部署");
        var before = s.Sent.Count;

        var result = await Handle(h, a1, Send("這台", b1.ToUpperInvariant(), "報表做好了，請部署"));
        await h.Ui.DrainAsync();

        result.Value<string>("t").Should().Be("sent", result.ToString());
        result.Value<string>("conversation").Should().Be(b1.ToLowerInvariant());
        s.Sent.Count.Should().Be(before + 1);
        var text = s.Sent.All[^1].Input.Text;
        text.Should().Contain("電腦「PC-A」的對話「A1 報表」").And.Contain(a1.ToLowerInvariant())
            .And.Contain($"myriagent-remote send {OwnDevice} {a1.ToLowerInvariant()}").And.EndWith("報表做好了，請部署");
    }

    [Fact]
    public async Task 照標題找_同名的要改用ID_對話在別台時說出在哪台()
    {
        using var h = new HubHarness();
        var (a1, _) = Idle(h, "A1 報表");
        var (x1, _) = Idle(h, "重複");
        var (x2, _) = Idle(h, "重複");
        var (b1, _) = Idle(h, "B1 部署");
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(b1, ownerDeviceId: OtherDevice));

        var dup = await Handle(h, a1, Send("這台", "重複", "hi"));
        dup.Value<string>("t").Should().Be("error");
        dup.Value<string>("message").Should().Contain("2 個對話叫「重複」").And.Contain(x1.ToLowerInvariant()).And.Contain(x2.ToLowerInvariant());

        var wrongDevice = await Handle(h, a1, Send("這台", b1, "hi"));
        wrongDevice.Value<string>("message").Should().Contain("在「PC-B」");

        var wrongTitle = await Handle(h, a1, Send("這台", "B1 部署", "hi"));
        wrongTitle.Value<string>("message").Should().Contain("「PC-B」有一個");

        var self = await Handle(h, a1, Send("這台", a1, "hi"));
        self.Value<string>("message").Should().Contain("不能送給自己");
    }

    [Fact]
    public async Task 送給其他電腦的對話_以型錄的send交給同步_等那台的結果()
    {
        using var h = new HubHarness();
        var (a1, _) = Idle(h, "A1 報表");
        var (b1, s) = Idle(h, "B1 部署");
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(b1, ownerDeviceId: OtherDevice));
        var sink = new FakeSink();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(sink);
            h.Hub.PeerLive = true;
        });
        var before = s.Sent.Count;

        var pending = Handle(h, a1, Send("PC-B", b1, "請部署"));
        await h.Ui.DrainAsync();
        sink.Calls.Should().ContainSingle();
        var (cid, cmd) = sink.Calls[0];
        cmd.Properties().Select(p => p.Name).Should().BeEquivalentTo(["cmd", "conversationId", "text"]);
        cmd.Value<string>("cmd").Should().Be("send");
        cmd.Value<string>("conversationId").Should().Be(b1);
        cmd.Value<string>("text").Should().Contain("對話「A1 報表」").And.EndWith("請部署");
        s.Sent.Count.Should().Be(before, "不送進這台的代理（副本）");

        h.Ui.Invoke(() => h.Hub.OnPeerReply(cid, new JObject { ["type"] = "commandResult", ["commandId"] = cid, ["state"] = RemoteResultStates.Done }));
        var result = await pending;
        result.Value<string>("t").Should().Be("sent");
        result.Value<string>("device").Should().Be("PC-B");
        h.Main.Messages("commandResult").Should().BeEmpty("結果不轉給任何視窗");
    }

    [Fact]
    public async Task 其他電腦拒絕或離線時回錯誤()
    {
        using var h = new HubHarness();
        var (a1, _) = Idle(h, "A1 報表");
        var (b1, _) = Idle(h, "B1 部署");
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(b1, ownerDeviceId: OtherDevice));
        var sink = new FakeSink();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(sink);
            h.Hub.PeerLive = true;
        });

        var offline = await Handle(h, a1, Send("PC-B", b1, "hi"), Directory(otherOnline: false));
        offline.Value<string>("code").Should().Be(RemoteExecPipeRules.ErrorOffline);
        sink.Calls.Should().BeEmpty();

        var pending = Handle(h, a1, Send("PC-B", b1, "hi"));
        await h.Ui.DrainAsync();
        var cid = sink.Calls.Single().CommandId;
        h.Ui.Invoke(() => h.Hub.OnPeerReply(cid, new JObject
        {
            ["type"] = "commandResult", ["commandId"] = cid, ["state"] = RemoteResultStates.Rejected, ["code"] = RemoteResultCodes.Policy, ["message"] = "那台的設定不允許",
        }));
        var rejected = await pending;
        rejected.Value<string>("code").Should().Be(RemoteExecPipeRules.ErrorRejected);
        rejected.Value<string>("message").Should().Be("那台的設定不允許");

        // 解除配對時等結果的一律收掉（結果不明）
        var dropped = Handle(h, a1, Send("PC-B", b1, "hi"));
        await h.Ui.DrainAsync();
        h.Ui.Invoke(() => h.Hub.AttachPeerSink(null));
        (await dropped).Value<string>("code").Should().Be(RemoteExecPipeRules.ErrorUnknown);
    }

    [Fact]
    public async Task 未配對時只能送給這台的對話()
    {
        using var h = new HubHarness();
        var (a1, _) = Idle(h, "A1 報表");
        var (b1, s) = Idle(h, "B1 部署");
        var unpaired = new RemoteExecDirectory(false, false, null, []);

        var other = await Handle(h, a1, Send("PC-B", b1, "hi"), unpaired);
        other.Value<string>("message").Should().Contain("還沒有和 MyriagentHub 配對");

        var before = s.Sent.Count;
        var ok = await Handle(h, a1, Send("這台", "B1 部署", "hi"), unpaired);
        await h.Ui.DrainAsync();
        ok.Value<string>("t").Should().Be("sent");
        s.Sent.Count.Should().Be(before + 1);
        s.Sent.All[^1].Input.Text.Should().Contain("myriagent-remote send 這台 ");
    }
}
