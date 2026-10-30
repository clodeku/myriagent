using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 預先放行（WP4.4 計畫步 6、D27；擁有者 2026-10-19 回答 WP4.4 Q1 選 A）：不偵測 CLI 設定檔，只看對話的權限模式——
/// 每步詢問的對話從遠端送文字是層級 1、不需要「允許遠端完全自動」；完全自動的對話才要。閘門 5d 與送出前的再查都經同一個偵測
/// （測試接縫有設就用它）。這台的政策是 Control、不允許遠端完全自動
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteTierTests
{
    /// <summary>通過了本機政策（5e）：之後的步驟是否處理這個指令與本測試無關（send、conversationCreate 的處理者在步 8、9）</summary>
    private static void ShouldPassPolicy(RemoteOutcome outcome)
    {
        outcome.Step.Should().NotBe(RemoteGateSteps.Policy, outcome.Message);
        outcome.Code.Should().NotBe(RemoteResultCodes.Policy, outcome.Message);
    }

    [Fact]
    public async Task 遠端send在Ask模式是層級1_不需要FullAccess()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Conversation(id)!.PermissionMode.Should().Be(AgentPermissionMode.Ask);

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text = "從手機送的" })));

        outcome.Tier.Should().Be(1, "沒有預先放行規則（Q1 選 A：CLI 的 allow 規則不算）");
        outcome.FullAccess.Should().BeFalse();
        ShouldPassPolicy(outcome);
    }

    [Fact]
    public async Task 完全自動模式的send要FullAccess()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });

        var denied = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text = "從手機送的" })));

        RemoteEntrySamples.ShouldBeRejected(denied, RemoteResultCodes.Policy, RemoteGateSteps.Policy);
        denied.Tier.Should().Be(2);
        denied.FullAccess.Should().BeTrue("完全自動的對話送文字要「允許遠端完全自動」（SEC-24）");

        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var allowed = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text = "從手機送的" })));

        allowed.Tier.Should().Be(2);
        allowed.FullAccess.Should().BeTrue();
        ShouldPassPolicy(allowed);
    }

    [Fact]
    public async Task 附言的approve_Ask模式是層級1()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", "npm test", "t1", CanAllowForSession: true));
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("approve", id,
            new { itemId = card.Id, choice = "deny", message = "改用別的做法", requestDigest = RequestDigestRules.Compute(card) })));

        outcome.Tier.Should().Be(1, "拒絕附言是代理輸入：Ask 模式、沒有預先放行規則是 1");
        outcome.FullAccess.Should().BeFalse();
        ShouldPassPolicy(outcome);
    }

    [Fact]
    public async Task conversationCreate的帳戶層級預先放行是false()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var create = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", "acc-claude"), ("permissionMode", "Ask"), ("text", "從網頁開的新對話"));

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(create));

        outcome.Tier.Should().Be(1, "新對話的帳戶也不偵測預先放行規則（Q1 選 A）");
        outcome.FullAccess.Should().BeFalse();
        ShouldPassPolicy(outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task 預先放行的接縫有設就用它_有或不明時要FullAccess(bool? preApproved)
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Hub.ConversationPreApprovedForTest = _ => preApproved);

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text = "從手機送的" })));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Policy);
        outcome.FullAccess.Should().BeTrue();
    }

    [Fact]
    public void 排隊的遠端訊息_Ask模式_預先放行不算_這一輪結束接著送出()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        // 入列時是層級 1（沒有完全自動）；送出前的再查用同一個偵測（Q1 選 A：false），所以照樣送出
        h.Ui.Invoke(() => h.Hub.EnqueueRemoteForTest(id, "遠端的訊息", new RemoteOrigin(RemoteEntrySamples.NewCid(), AgentPermissionMode.Ask, 1, false)));

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("遠端的訊息");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public void 排隊的遠端訊息_接縫說有預先放行_送出前留下()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Ui.Invoke(() =>
        {
            h.Hub.EnqueueRemoteForTest(id, "遠端的訊息", new RemoteOrigin(RemoteEntrySamples.NewCid(), AgentPermissionMode.Ask, 1, false));
            h.Hub.ConversationPreApprovedForTest = _ => true;
        });

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1, "送出前的再查也經接縫（D27）");
        h.Queue(id).Should().Equal("遠端的訊息");
    }
}
