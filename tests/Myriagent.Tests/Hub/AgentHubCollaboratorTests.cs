using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Rendering;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 多人協作的擁有者電腦（docs/plans/2026-10-30-collaborators.md 第 3 節、sync-protocol 14.14）：協作人發起的信封（<c>issuer.kind = collaborator</c>）
/// 只收「對話與核准」與檔案的指令（伺服器只是第一道），送出的使用者訊息記下 <see cref="AgentChatItem.Author"/>（排進佇列也帶著）、回應的卡片記下
/// <see cref="AgentChatItem.ResolvedBy"/>，上線的投影與渲染都帶著它們
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubCollaboratorTests
{
    private const string Name = "小明";

    private static CommandEnvelopeBody Collaborator(JObject cmd, string? label = Name) =>
        RemoteEntrySamples.Body(cmd) with { Issuer = new CommandIssuer(CommandIssuerKinds.Collaborator, label ?? string.Empty) };

    private static (string Id, FakeSession Session) Idle(HubHarness h, bool allowFullAccess = false)
    {
        RemoteEntrySamples.AllowRemote(h, allowFullAccess);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        return (id, s);
    }

    [Fact]
    public async Task 協作人的send_使用者訊息記下是誰送的_擁有者的不記()
    {
        using var h = new HubHarness();
        var (id, s) = Idle(h);

        var outcome = await RemoteEntrySamples.RunAsync(h, Collaborator(RemoteEntrySamples.Cmd("send", id, new { text = "幫我整理報表" })));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var item = h.Items(id).Last(i => i.Kind == AgentItemKind.User);
        item.Text.Should().Be("幫我整理報表");
        item.Author.Should().Be(Name);
        s.Sent.All[^1].Input.Text.Should().Be("幫我整理報表", "代理收到的文字不加名稱");
        h.CompleteTurn(s);

        (await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text = "擁有者的" })))).State.Should().Be(RemoteResultStates.Done);
        var owner = h.Items(id).Last(i => i.Kind == AgentItemKind.User);
        owner.Text.Should().Be("擁有者的");
        owner.Author.Should().BeNull();
        h.CompleteTurn(s);
        h.Send(h.Main, id, "本機的");
        var local = h.Items(id).Last(i => i.Kind == AgentItemKind.User);
        local.Text.Should().Be("本機的");
        local.Author.Should().BeNull();
    }

    [Fact]
    public async Task 協作人在執行中送的_排進佇列也帶著名稱_取出時記在訊息上()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        (await RemoteEntrySamples.RunAsync(h, Collaborator(RemoteEntrySamples.Cmd("send", id, new { text = "排隊的" })))).State.Should().Be(RemoteResultStates.Done);
        h.OnUi(() => h.Hub.QueueEntriesForTest(id)).Single().Origin!.Author.Should().Be(Name);
        h.CompleteTurn(s);
        await h.WaitUntilAsync(() => h.Items(id).Any(i => i.Kind == AgentItemKind.User && i.Text == "排隊的"), "這一輪結束後送出排隊的那則");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User && i.Text == "排隊的").Author.Should().Be(Name);
    }

    [Theory]
    [InlineData("rename", "{\"title\":\"改名\"}")]
    [InlineData("delete", "{}")]
    [InlineData("setOption", "{\"key\":\"model\",\"value\":\"opus\"}")]
    [InlineData("setFolder", "{\"path\":\"C:\\\\work\"}")]
    [InlineData("resetFolder", "{}")]
    public async Task 協作人不能改對話的設定_policy(string cmd, string args)
    {
        using var h = new HubHarness();
        var (id, _) = Idle(h, allowFullAccess: true);
        var title = h.Conversation(id)!.Title;
        var command = RemoteEntrySamples.Cmd(cmd, id);
        foreach (var p in JObject.Parse(args).Properties())
            command[p.Name] = p.Value;
        var outcome = await RemoteEntrySamples.RunAsync(h, Collaborator(command));
        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Route);
        h.Conversation(id).Should().NotBeNull();
        h.Conversation(id)!.Title.Should().Be(title);
    }

    [Fact]
    public async Task 協作人不能送路由Device的指令()
    {
        using var h = new HubHarness();
        Idle(h);
        var outcome = await RemoteEntrySamples.RunAsync(h, Collaborator(RemoteEntrySamples.Cmd("accountRefresh", null, new { accountId = h.Claude.Id })));
        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Route);
    }

    [Fact]
    public async Task 協作人回應核准卡_記下ResolvedBy_擁有者回應的是null()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", "npm test", "t1", CanAllowForSession: true));
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        var outcome = await RemoteEntrySamples.RunAsync(h, Collaborator(RemoteEntrySamples.Cmd("approve", id,
            new { itemId = card.Id, choice = "allow", message = (string?)null, requestDigest = card.RequestDigest })));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var resolved = h.Items(id).Single(i => i.Id == card.Id);
        resolved.Status.Should().Be(AgentItemStatus.Done);
        resolved.ResolvedBy.Should().Be(Name);

        h.Raise(s, new AgentApprovalEvent("p-2", "執行指令", "bash", "npm run build", "npm run build", "t2", CanAllowForSession: true));
        var second = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval && i.Status == AgentItemStatus.Waiting);
        (await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("approve", id,
            new { itemId = second.Id, choice = "allow", message = (string?)null, requestDigest = second.RequestDigest })))).State.Should().Be(RemoteResultStates.Done);
        h.Items(id).Single(i => i.Id == second.Id).ResolvedBy.Should().BeNull();
    }

    [Fact]
    public void 名稱上線_讀回_渲染_空的與不合格的當成沒有()
    {
        var item = new AgentChatItem { Id = "u1", Kind = AgentItemKind.User, Status = AgentItemStatus.Done, Text = "hi", Ord = 3, Author = Name, ResolvedBy = "  " };
        ConversationWire.TryItem(item, 1, out var wire).Should().BeTrue();
        wire!["author"]!.Value<string>().Should().Be(Name);
        wire.ContainsKey("resolvedBy").Should().BeFalse("空白的省略");
        ConversationWire.TryReadItem(wire, _ => null, out var read).Should().BeTrue();
        var back = WireItemReader.ToChatItem(read!)!;
        back.Author.Should().Be(Name);
        var rendered = new ItemRenderer(new NoMediaUrls()).Render(back, null);
        rendered["author"]!.Value<string>().Should().Be(Name);
        rendered.ContainsKey("resolvedBy").Should().BeFalse();

        var bad = new AgentChatItem { Id = "u2", Kind = AgentItemKind.User, Status = AgentItemStatus.Done, Text = "x", Ord = 4, Author = "壞" + (char)0x202E };
        ConversationWire.TryItem(bad, 1, out var badWire).Should().BeTrue();
        badWire!.ContainsKey("author").Should().BeFalse("控制與格式字元的名稱不上線");
        CollaboratorRules.CleanAuthor("  阿明 ").Should().Be("阿明");
    }

    [Fact]
    public void 發起者的稱呼_協作人標成協作人()
    {
        RemoteNotifyRules.IssuerDisplay(CommandIssuerKinds.Collaborator, Name).Should().Be("協作人「小明」");
        RemoteNotifyRules.IssuerDisplay(CommandIssuerKinds.Collaborator, "").Should().Be("協作人「未命名」");
        RemoteViewContext.FromEnvelope(Collaborator(RemoteEntrySamples.Cmd("stop", "abc"))).Collaborator.Should().Be(Name);
        RemoteViewContext.FromEnvelope(RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("stop", "abc"))).Collaborator.Should().BeNull();
    }

    private sealed class NoMediaUrls : IMediaUrlBuilder
    {
        public string MediaUrl(string path) => "m:" + path;

        public string ThumbUrl(string path, int size, string? version = null) => "t:" + path;

        public string MarkdownImageUrl(string path) => "i:" + path;

        public string MarkdownLinkUrl(string path) => "l:" + path;

        public void AllowMarkdownImage(string path)
        {
        }
    }
}
