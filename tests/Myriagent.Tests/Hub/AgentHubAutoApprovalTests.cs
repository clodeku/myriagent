using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>四家共用的完全自動：等待中的核准、切換時晚到的請求、背景工作與切回其他模式。</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubAutoApprovalTests
{
    private static AgentAccount Account(HubHarness h, string provider)
    {
        var account = provider switch
        {
            "Claude" => h.Claude,
            "Codex" => h.Codex,
            "Grok" => new AgentAccount { Id = "acc-grok", Provider = AgentProvider.Grok, Label = "Grok", ConfigDirectory = h.Temp.Combine("grok") },
            "Antigravity" => new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity" },
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        if (account != h.Claude && account != h.Codex)
            h.Ui.Invoke(() => h.Accounts.Seed([h.Claude, h.Claude2, h.Codex, account], AccountStatus.Ready));
        return account;
    }

    private static AgentApprovalEvent Approval(string key, bool plan = false)
        => new(key, plan ? "計畫" : "執行指令", plan ? "plan" : "bash", "待核准", null, null, CanAllowForSession: !plan, IsPlan: plan);

    private static void SetMode(HubHarness h, string id, string mode)
        => h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = mode });

    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("Grok")]
    [InlineData("Antigravity")]
    public void 切到完全自動_放行全部等待核准一次_問卷計畫與拒絕保留(string provider)
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, Account(h, provider));
        // 即使 CLI 不能即時切換，已交給宿主的工具核准仍可回覆。
        session.CanSwitchModeLive = false;
        h.Raise(session, Approval("denied"));
        var denied = h.Items(id).Single(i => i.RequestKey == "denied");
        h.Command(h.Main, "approve", new { conversationId = id, itemId = denied.Id, choice = "deny" });
        h.Raise(session, Approval("first"));
        h.Raise(session, Approval("second"));
        h.Raise(session, Approval("plan", plan: true));
        h.Raise(session, new AgentQuestionEvent("question", [], AnswerAsMessage: false));

        SetMode(h, id, "FullAccess");
        SetMode(h, id, "FullAccess");
        h.Raise(session, new AgentAutoApproveEvent("first"));
        h.Raise(session, new AgentAutoApproveEvent("denied"));
        h.Raise(session, new AgentAutoApproveEvent("plan"));
        h.Raise(session, new AgentAutoApproveEvent("question"));

        session.Approvals.All.Should().Equal(
            ("denied", AgentApprovalChoice.Deny, (string?)null),
            ("first", AgentApprovalChoice.Allow, (string?)null),
            ("second", AgentApprovalChoice.Allow, (string?)null));
        h.Items(id).Where(i => i.RequestKey is "first" or "second")
            .Should().OnlyContain(i => i.Status == AgentItemStatus.Done && i.Decision == "allow" && i.Output == "已切換為完全自動，自動允許");
        h.Items(id).Where(i => i.RequestKey is "question" or "plan").Should().OnlyContain(i => i.Status == AgentItemStatus.Waiting);
        h.Items(id).Single(i => i.RequestKey == "denied").Status.Should().Be(AgentItemStatus.Declined);
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("Grok")]
    [InlineData("Antigravity")]
    public async Task 核准事件晚於切換抵達_完全自動放行_切回自動後不再放行(string provider)
    {
        using var h = new HubHarness(alwaysPost: true);
        var (id, session) = h.StartConversation(h.Main, Account(h, provider));
        await h.Ui.DrainAsync();
        h.Ui.Invoke(() =>
        {
            session.Raise(Approval("in-flight"));
            SetMode(h, id, "FullAccess");
        });
        await h.Ui.DrainAsync();
        session.Approvals.All.Should().Equal(("in-flight", AgentApprovalChoice.Allow, (string?)null));

        SetMode(h, id, "Auto");
        h.Raise(session, Approval("ask-again"));
        // 前一次切換送出的自動核准通知晚到，不能覆蓋使用者最新選的模式。
        h.Raise(session, new AgentAutoApproveEvent("ask-again"));
        await h.Ui.DrainAsync();
        session.Approvals.Count.Should().Be(1);
        h.Items(id).Single(i => i.RequestKey == "ask-again").Status.Should().Be(AgentItemStatus.Waiting);
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    [InlineData("Grok")]
    [InlineData("Antigravity")]
    public void 主回合已結束_背景工作提出核准_切到完全自動仍會放行(string provider)
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, Account(h, provider));
        h.CompleteTurn(session);
        h.Raise(session, Approval("background"));

        SetMode(h, id, "FullAccess");

        session.Approvals.All.Should().Equal(("background", AgentApprovalChoice.Allow, (string?)null));
        h.Items(id).Single(i => i.RequestKey == "background").Status.Should().Be(AgentItemStatus.Done);
    }
}
