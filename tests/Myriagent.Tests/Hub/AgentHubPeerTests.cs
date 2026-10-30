using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 桌面遠端操作的中樞（sync-protocol 14.7；擁有者 2026-10-22 決定「已配對就能操作」）：副本上帶 commandId 的型錄寫入指令、或對其他電腦的帳戶與 CLI 指令，
/// 交給同步（<see cref="IPeerCommandSink"/>）而不是唯讀擋掉；結果只轉給發起的檢視、只收白名單的種類
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubPeerTests
{
    private const string OwnDevice = "d_this";

    private const string OtherDevice = "d_other";

    private static string NewId() => SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class FakeSink : IPeerCommandSink
    {
        public List<(string CommandId, JObject Cmd)> Calls { get; } = [];

        public string? DeviceId => OwnDevice;

        public void SubmitPeerCommand(string commandId, JObject cmd) => Calls.Add((commandId, cmd));
    }

    private static (string Id, FakeSession Session) Replica(HubHarness h)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentTextCompletedEvent("m:0", "代理的回覆"));
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(id));
        return (id, s);
    }

    private static FakeSink Attach(HubHarness h)
    {
        var sink = new FakeSink();
        h.Ui.Invoke(() => h.Hub.AttachPeerSink(sink));
        return sink;
    }

    [Fact]
    public async Task 副本上帶commandId的send交給同步_不送進代理_沒有唯讀提示()
    {
        using var h = new HubHarness();
        var (id, s) = Replica(h);
        var sink = Attach(h);
        var sent = s.Sent.Count;
        var mark = h.Main.Mark();
        var cid = NewId();

        h.Command(h.Main, "send", new { conversationId = id, text = "從另一台送的", commandId = cid });
        await h.Ui.DrainAsync();

        sink.Calls.Should().ContainSingle();
        sink.Calls[0].CommandId.Should().Be(cid);
        sink.Calls[0].Cmd.Properties().Select(p => p.Name).Should().BeEquivalentTo(["cmd", "conversationId", "text"]);
        sink.Calls[0].Cmd["text"]!.Value<string>().Should().Be("從另一台送的");
        s.Sent.Count.Should().Be(sent, "不送進這台的代理");
        h.Main.Messages("toast", mark).Should().NotContain(t => t.Value<string>("text") == AgentHub.ReplicaReadOnlyMessage);
    }

    [Fact]
    public async Task 副本上沒帶commandId的照舊唯讀擋下()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        var sink = Attach(h);
        var mark = h.Main.Mark();

        h.Command(h.Main, "send", new { conversationId = id, text = "x" });
        await h.Ui.DrainAsync();

        sink.Calls.Should().BeEmpty();
        h.Main.Messages("toast", mark).Should().Contain(t => t.Value<string>("text") == AgentHub.ReplicaReadOnlyMessage);
    }

    [Fact]
    public async Task 本機對話帶commandId_照本機處理_不交給同步()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var sink = Attach(h);

        h.Command(h.Main, "rename", new { conversationId = id, title = "本機改名", commandId = NewId() });
        await h.Ui.DrainAsync();

        sink.Calls.Should().BeEmpty();
        h.Conversation(id)!.Title.Should().Be("本機改名");
    }

    [Fact]
    public async Task 只看的指令不交給同步()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        var sink = Attach(h);

        h.Command(h.Main, "open", new { conversationId = id, commandId = NewId() });
        h.Command(h.Main, "loadOlder", new { conversationId = id, beforeId = "x", commandId = NewId() });
        await h.Ui.DrainAsync();

        sink.Calls.Should().BeEmpty("open、loadOlder 不在型錄");
    }

    [Fact]
    public async Task 結果只轉給發起的檢視_不在白名單或不認得的commandId丟掉()
    {
        using var h = new HubHarness();
        var other = h.AddView("other");
        var (id, _) = Replica(h);
        Attach(h);
        var cid = NewId();
        h.Command(h.Main, "stop", new { conversationId = id, commandId = cid });
        await h.Ui.DrainAsync();
        var mark = h.Main.Mark();
        var otherMark = other.Mark();

        h.Ui.Invoke(() =>
        {
            h.Hub.OnPeerReply(cid, new JObject { ["type"] = "init" });
            h.Hub.OnPeerReply(NewId(), new JObject { ["type"] = "toast", ["text"] = "不是這台發的" });
            h.Hub.OnPeerReply(cid, new JObject { ["type"] = "commandResult", ["commandId"] = cid, ["state"] = "done" });
        });
        await h.Ui.DrainAsync();

        h.Main.Messages("commandResult", mark).Should().ContainSingle().Which["commandId"]!.Value<string>().Should().Be(cid);
        h.Main.Messages("init", mark).Should().BeEmpty("不在白名單");
        h.Main.Messages("toast", mark).Should().NotContain(t => t.Value<string>("text") == "不是這台發的");
        other.Messages("commandResult", otherMark).Should().BeEmpty("只給發起的檢視");
    }

    [Fact]
    public async Task 型錄的輸出白名單_listFiles的files轉得出去()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        Attach(h);
        var cid = NewId();
        h.Command(h.Main, "listFiles", new { conversationId = id, requestId = "r1", path = (string?)null, commandId = cid });
        await h.Ui.DrainAsync();
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.OnPeerReply(cid, new JObject { ["type"] = "files", ["requestId"] = "r1", ["entries"] = new JArray() }));
        await h.Ui.DrainAsync();

        h.Main.Messages("files", mark).Should().ContainSingle();
    }

    [Fact]
    public async Task 沒有接上同步_回合成的not_sent()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        var cid = NewId();
        var mark = h.Main.Mark();

        h.Command(h.Main, "stop", new { conversationId = id, commandId = cid });
        await h.Ui.DrainAsync();

        var result = h.Main.Messages("commandResult", mark).Should().ContainSingle().Subject;
        (result.Value<string>("commandId"), result.Value<string>("state"), result.Value<string>("code"), result.Value<string>("origin"))
            .Should().Be((cid, RemoteResultStates.Rejected, PeerCommandRules.NotSent, "transport"));
    }

    [Fact]
    public async Task 其他電腦的CLI指令交給同步_這台的照本機處理()
    {
        using var h = new HubHarness();
        var sink = Attach(h);

        h.Command(h.Main, "cliCheck", new { deviceId = OtherDevice, commandId = NewId() });
        h.Command(h.Main, "cliCheck", new { deviceId = OwnDevice, commandId = NewId() });
        await h.Ui.DrainAsync();

        sink.Calls.Should().ContainSingle().Which.Cmd["deviceId"]!.Value<string>().Should().Be(OtherDevice);
    }

    [Fact]
    public void 規則_Forwardable_PeerBody_IsAllowedReply()
    {
        var cid = NewId();
        PeerHubRules.Forwardable(JObject.FromObject(new { cmd = "send", conversationId = "c", text = "x", commandId = cid }), isReplica: true, OwnDevice)!.Name.Should().Be("send");
        PeerHubRules.Forwardable(JObject.FromObject(new { cmd = "send", conversationId = "c", text = "x", commandId = cid }), isReplica: false, OwnDevice).Should().BeNull();
        PeerHubRules.Forwardable(JObject.FromObject(new { cmd = "send", conversationId = "c", text = "x", commandId = "x" }), isReplica: true, OwnDevice).Should().BeNull("commandId 要是 ULID");
        PeerHubRules.Forwardable(JObject.FromObject(new { cmd = "openPath", conversationId = "c", commandId = cid }), isReplica: true, OwnDevice).Should().BeNull("不在型錄");
        PeerHubRules.Forwardable(JObject.FromObject(new { cmd = "cliCheck", deviceId = OtherDevice, commandId = cid }), isReplica: false, null).Should().BeNull("未配對");
        PeerHubRules.Forwardable(JObject.FromObject(new { cmd = "cliCheck", commandId = cid }), isReplica: false, OwnDevice).Should().BeNull("沒帶 deviceId");

        var body = PeerHubRules.PeerBody(JObject.FromObject(new { cmd = "stop", conversationId = "c", commandId = cid }));
        body.ContainsKey("commandId").Should().BeFalse();

        PeerHubRules.IsAllowedReply("stop", new JObject { ["type"] = "commandResult" }).Should().BeTrue();
        PeerHubRules.IsAllowedReply("send", new JObject { ["type"] = "sendRejected" }).Should().BeTrue();
        PeerHubRules.IsAllowedReply("fileFetch", new JObject { ["type"] = "fileRef" }).Should().BeTrue();
        PeerHubRules.IsAllowedReply("stop", new JObject { ["type"] = "files" }).Should().BeFalse();
        PeerHubRules.IsAllowedReply("stop", new JObject { ["type"] = "init" }).Should().BeFalse();
        PeerHubRules.IsAllowedReply("stop", new JObject()).Should().BeFalse();
    }

    [Fact]
    public void 網頁的PEER_COMMANDS等於型錄裡由電腦執行的網頁指令()
    {
        var source = File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web", "liverules.js"));
        var start = source.IndexOf("export const PEER_COMMANDS = Object.freeze([", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var end = source.IndexOf("]);", start, StringComparison.Ordinal);
        var names = System.Text.RegularExpressions.Regex.Matches(source[start..end], "'([A-Za-z]+)'").Select(m => m.Groups[1].Value).ToList();
        var catalog = RemoteCommandCatalog.All.Where(s => s.Route is RemoteRoute.Owner or RemoteRoute.Device && !RemoteCommandCatalog.NonWebCommands.Contains(s.Name))
            .Select(s => s.Name).ToList();
        names.Should().BeEquivalentTo(catalog, "桌面只對型錄裡由電腦執行的網頁指令帶 commandId（liverules.wantsPeerId）");
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 規則_副本的卡片加requestDigest_其他種類不加()
    {
        var card = new AgentChatItem { Id = "q1", Kind = AgentItemKind.Approval, RequestDigest = "rd1:abc" };
        var rendered = new JObject();
        PeerHubRules.DecorateReplicaCard(rendered, card);
        rendered["requestDigest"]!.Value<string>().Should().Be("rd1:abc");

        var text = new JObject();
        PeerHubRules.DecorateReplicaCard(text, new AgentChatItem { Id = "t1", Kind = AgentItemKind.Assistant, RequestDigest = "rd1:abc" });
        text.ContainsKey("requestDigest").Should().BeFalse();
    }

    [Fact]
    public void 規則_發起者的稱呼_另一台桌面寫成電腦()
    {
        RemoteNotifyRules.IssuerDisplay("device", "DEMO-PC-3").Should().Be("電腦「DEMO-PC-3」");
        RemoteNotifyRules.IssuerDisplay("browser", "Chrome・Windows").Should().Be("Chrome・Windows");
        RemoteNotifyRules.IssuerDisplay("device", "").Should().Be("電腦「另一台電腦」");
        RemoteNotifyRules.Describe("send", RemoteNotifyRules.IssuerDisplay("device", "DEMO-PC-3"), "修正登入").Should().Be("電腦「DEMO-PC-3」 在『修正登入』送出了訊息");
    }
}
