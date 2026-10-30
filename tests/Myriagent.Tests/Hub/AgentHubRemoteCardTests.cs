using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端的 approve、approvePlan（WP4.4 計畫步 8；D21、D22）：呼叫本機的 ApproveAsync／ApprovePlanAsync，同步的前段結束後卡片不再等待、
/// 而且 Decision 是這次要設的值才回 done；被作廢或已在別處處理的回 stale。這台的政策是 Control、允許遠端完全自動
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteCardTests
{
    private sealed record Cards(string Id, FakeSession Session, AgentChatItem Approval, AgentChatItem Plan);

    /// <summary>執行中的對話，畫出等待中的核准卡與計畫確認卡</summary>
    private static Cards StartWithCards(HubHarness h, AgentAccount account, string? instance = null)
    {
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var (id, s) = h.StartConversation(h.Main, account);
        if (instance != null)
            h.ProcessStarted(s, instance, null);
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", "npm test", "t1", CanAllowForSession: true));
        h.Raise(s, new AgentApprovalEvent("plan-1", "確認執行計畫", "plan", "請確認", "1. 改 A", null, CanAllowForSession: false, IsPlan: true));
        var items = h.Items(id);
        return new Cards(id, s, items.Single(i => i.Kind == AgentItemKind.Approval), items.Single(i => i.Kind == AgentItemKind.PlanApproval));
    }

    private static Task<RemoteOutcome> RespondAsync(HubHarness h, string cmd, string conversationId, AgentChatItem card, string choice, string? message = null,
        string? cid = null)
        => RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd(cmd, conversationId,
            new { itemId = card.Id, choice, message, requestDigest = card.RequestDigest }), cid: cid));

    private static AgentChatItem Card(HubHarness h, string conversationId, string itemId) => h.Items(conversationId).Single(i => i.Id == itemId);

    [Fact]
    public async Task 遠端approve_allow_卡片Done_done()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Claude);

        var outcome = await RespondAsync(h, "approve", cards.Id, cards.Approval, "allow");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var card = Card(h, cards.Id, cards.Approval.Id);
        (card.Status, card.Decision).Should().Be((AgentItemStatus.Done, "allow"));
        cards.Session.Approvals.All.Should().ContainSingle().Which.Should().Be(("p-1", AgentApprovalChoice.Allow, (string?)null));
    }

    [Fact]
    public async Task 遠端approve_allowSession_done()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Claude);

        var outcome = await RespondAsync(h, "approve", cards.Id, cards.Approval, "allowSession");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        Card(h, cards.Id, cards.Approval.Id).Decision.Should().Be("allowSession");
        cards.Session.Approvals.All.Single().Choice.Should().Be(AgentApprovalChoice.AllowForSession);
    }

    [Fact]
    public async Task 遠端approve_deny帶附言_Codex的PendingFeedback帶來源()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Codex);
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RespondAsync(h, "approve", cards.Id, cards.Approval, "deny", "改用資源回收筒", cid);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var card = Card(h, cards.Id, cards.Approval.Id);
        (card.Status, card.Decision).Should().Be((AgentItemStatus.Declined, "deny"));
        cards.Session.Approvals.All.Should().ContainSingle().Which.Should().Be(("p-1", AgentApprovalChoice.Deny, "改用資源回收筒"));
        var feedback = h.OnUi(() => h.Hub.PendingFeedbackForTest(cards.Id)).Single();
        feedback.Feedback.Should().Be("改用資源回收筒");
        feedback.RemoteOrigin.Should().Be(new RemoteOrigin(cid, h.Conversation(cards.Id)!.PermissionMode, outcome.Tier, outcome.FullAccess));
    }

    [Fact]
    public async Task 遠端approve_Claude的deny附言直接給CLI_沒有PendingFeedback()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Claude);

        var outcome = await RespondAsync(h, "approve", cards.Id, cards.Approval, "deny", "改用資源回收筒");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        cards.Session.Approvals.All.Single().Feedback.Should().Be("改用資源回收筒");
        h.OnUi(() => h.Hub.PendingFeedbackForTest(cards.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task 遠端approve_卡片已經被本機處理_stale()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Claude);
        h.Command(h.Main, "approve", new { conversationId = cards.Id, itemId = cards.Approval.Id, choice = "deny" });

        var outcome = await RespondAsync(h, "approve", cards.Id, cards.Approval, "allow");

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Stale, RemoteGateSteps.Card);
        Card(h, cards.Id, cards.Approval.Id).Decision.Should().Be("deny");
        cards.Session.Approvals.Count.Should().Be(1);
    }

    [Fact]
    public async Task 遠端approve_RejectStaleCard作廢_stale()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Claude, instance: "p1");
        // 產生卡片的程序已不是事件流上的目前程序（中樞沒看到它換掉）：回應時 RejectStaleCard 作廢、不轉送
        h.Ui.Invoke(() => cards.Approval.ProcessInstance = "p-old");

        var outcome = await RespondAsync(h, "approve", cards.Id, cards.Approval, "allow");

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Stale, RemoteGateSteps.Handler);
        Card(h, cards.Id, cards.Approval.Id).Status.Should().Be(AgentItemStatus.Cancelled);
        cards.Session.Approvals.Count.Should().Be(0);
    }

    [Fact]
    public async Task 遠端approvePlan_acceptEdits_done()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Claude);

        var outcome = await RespondAsync(h, "approvePlan", cards.Id, cards.Plan, "acceptEdits");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var card = Card(h, cards.Id, cards.Plan.Id);
        (card.Status, card.Decision).Should().Be((AgentItemStatus.Done, "acceptEdits"));
        cards.Session.PlanResponses.All.Should().ContainSingle().Which.Should().Be(("plan-1", AgentPlanChoice.AcceptEdits, (string?)null));
    }

    [Fact]
    public async Task 遠端approvePlan_keepPlanning帶說明_done()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Claude);

        var outcome = await RespondAsync(h, "approvePlan", cards.Id, cards.Plan, "keepPlanning", "先只改 A");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var card = Card(h, cards.Id, cards.Plan.Id);
        (card.Status, card.Decision, card.Output).Should().Be((AgentItemStatus.Declined, "keepPlanning", "你的說明：先只改 A"));
        cards.Session.PlanResponses.All.Should().ContainSingle().Which.Should().Be(("plan-1", AgentPlanChoice.KeepPlanning, "先只改 A"));
    }

    [Fact]
    public async Task 處理者_沒授權就Reject_卡片不動()
    {
        using var h = new HubHarness();
        var cards = StartWithCards(h, h.Claude);
        var handlers = h.OnUi(() => h.Hub.RemoteHandlersForTest);

        foreach (var (cmd, card, choice) in new[] { ("approve", cards.Approval, "allow"), ("approvePlan", cards.Plan, "acceptEdits") })
        {
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, cards.Id, 1, 1, false, null, 2_000, false,
                RemoteEntrySamples.IssuerLabel);
            var command = RemoteEntrySamples.Cmd(cmd, cards.Id, new { itemId = card.Id, choice, requestDigest = card.RequestDigest });
            h.OnUi(() => handlers.Any(handler => handler.TryHandle(context, h.Main, cmd, command))).Should().BeTrue(cmd + " 是這一包的指令");
            RemoteEntrySamples.ShouldBeRejected(await context.Completion.WaitAsync(TimeSpan.FromSeconds(5)), RemoteResultCodes.Invalid, RemoteGateSteps.Handler);
        }

        h.Items(cards.Id).Where(i => i.Kind is AgentItemKind.Approval or AgentItemKind.PlanApproval).Should().OnlyContain(i => i.Status == AgentItemStatus.Waiting);
        cards.Session.Approvals.Count.Should().Be(0);
        cards.Session.PlanResponses.Count.Should().Be(0);
    }
}
