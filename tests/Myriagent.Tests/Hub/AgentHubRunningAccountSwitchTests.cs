using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 執行中換帳戶（使用者 2026-10-23）：Claude 換另一個 Claude 帳戶、Codex 換另一個 Codex 帳戶——先停下這一輪，結束時換過去，
/// 新帳戶帶著前文節錄與接續訊息從中斷處接著做（同額度用完的自動換帳戶）；2026-10-24 起四種代理之間都能互換（新的一方開新對話）。
/// 選「目前步驟完成後換」（when turnEnd）會等安全點再停下交接；整輪先自行結束時照原本結果收尾
/// </summary>
public class AgentHubRunningAccountSwitchTests
{
    private static readonly AgentAccount Codex2 = new() { Id = "acc-codex2", Provider = AgentProvider.Codex, Label = "Codex 副帳戶", Color = "#3B82F6" };

    private static AgentAccount Grok(HubHarness h, string id, string label)
        => new() { Id = id, Provider = AgentProvider.Grok, Label = label, Color = "#9CA3AF", ConfigDirectory = h.Temp.Combine("accounts", id, "grok") };

    private static void SeedMore(HubHarness h, params AgentAccount[] more)
        => h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Claude2, h.Codex }.Concat(more).ToArray(), AccountStatus.Ready));

    [Fact]
    public void Claude執行中換到另一個Claude帳戶_先停下_結束後換過去並從中斷處接著做()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        h.Raise(s1, new AgentToolStartedEvent("t1", "執行指令", "bash", "dotnet build", "dotnet build"));

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });

        s1.Interrupts.Should().Be(1, "執行中的 CLI 程序綁著原帳戶：先停下這一輪");
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id, "這一輪結束之前還是原帳戶");
        h.IsRunning(id).Should().BeTrue();
        h.Main.Messages("toast").Should().BeEmpty();

        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Conversation(id)!.SessionStarted.Should().BeFalse("新帳戶開新 session");
        s1.Disposed.Should().BeTrue("舊帳戶的程序關掉");
        var s2 = h.Sessions[1];
        var sent = s2.Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude2.Id + "|");
        sent.Input.Text.Should().StartWith("（系統說明：原本的對話紀錄已無法續接", "前文節錄在前面");
        sent.Input.Text.Should().Contain("【使用者】把建置修好");
        sent.Input.Text.Should().Contain("（系統通知：使用者在這一輪執行中把帳戶換成另一個 Claude Code 帳戶");
        sent.Input.Text.Should().Contain("〈執行指令：dotnet build（未完成）〉");
        h.IsRunning(id).Should().BeTrue("接續的一輪在執行");

        var items = h.Items(id);
        items.Should().NotContain(i => i.Kind == AgentItemKind.Notice && i.Text.StartsWith("已停止這一輪"), "為了換帳戶停下的不算「已停止」");
        items.Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "接續訊息不顯示成使用者訊息");
        var notice = items[^1];
        notice.NoticeKind.Should().Be(AgentNoticeKind.AccountSwitch);
        notice.Text.Should().Be(AccountSwitchRules.BuildUserSwitchNotice("Claude 主帳戶", "Claude 副帳戶"));
        h.Settings.LastAccountId.Should().Be(h.Claude2.Id, "本機換的記成上次使用的帳戶");
    }

    [Fact]
    public void Codex執行中換到另一個Codex帳戶_排隊的訊息不暫停_接續的一輪結束後照常送()
    {
        using var h = new HubHarness();
        SeedMore(h, Codex2);
        var (id, s1) = h.StartConversation(h.Main, h.Codex, "第一則");
        h.Send(h.Main, id, "排隊B");

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = Codex2.Id });
        s1.Interrupts.Should().Be(1);
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);

        h.Conversation(id)!.AccountId.Should().Be(Codex2.Id);
        h.Items(id).Should().NotContain(i => i.Text.Contains("排隊的訊息暫停"));
        var s2 = h.Sessions[1];
        s2.Sent.Count.Should().Be(1);
        s2.Sent.All[0].Input.Text.Should().Contain("另一個 Codex 帳戶");
        h.Queue(id).Should().Equal("排隊B");
        h.Raise(s2, new AgentSessionStartedEvent("thread-2", null, null));

        h.CompleteTurn(s2);

        s2.Sent.Count.Should().Be(2);
        s2.Sent.All[1].Input.Text.Should().Be("排隊B");
    }

    private static readonly AgentAccount Antigravity = new() { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity", Color = "#4285F4" };

    [Fact]
    public void Grok執行中換到另一個Grok帳戶_先停下_結束後由新帳戶從中斷處接著做()
    {
        using var h = new HubHarness();
        var grok1 = Grok(h, "acc-grok1", "Grok 主帳戶");
        var grok2 = Grok(h, "acc-grok2", "Grok 副帳戶");
        SeedMore(h, grok1, grok2);
        var (id, s1) = h.StartConversation(h.Main, grok1, "把建置修好");

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = grok2.Id });

        s1.Interrupts.Should().Be(1, "2026-10-24 起 Grok 也能在執行中換：先停下這一輪");
        h.Main.Messages("toast").Should().BeEmpty();
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);

        h.Conversation(id)!.AccountId.Should().Be(grok2.Id);
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(grok2.Id + "|");
        sent.Input.Text.Should().Contain("【使用者】把建置修好");
        sent.Input.Text.Should().Contain("（系統通知：使用者在這一輪執行中把帳戶換成另一個 Grok Build 帳戶");
        h.IsRunning(id).Should().BeTrue("接續的一輪在執行");
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Notice && i.Text.StartsWith("已停止這一輪"));
    }

    [Fact]
    public void Claude執行中選這一輪結束後換到Antigravity_Antigravity執行中再立即換回Claude_接續訊息寫換了代理()
    {
        using var h = new HubHarness();
        SeedMore(h, Antigravity);
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "第一則");

        h.Command(h.Main, "setAccount", AtTurnEnd(id, Antigravity.Id));
        s1.Interrupts.Should().Be(0, "等這一輪結束才換");
        h.CompleteTurn(s1);

        (h.Conversation(id)!.AccountId, h.Conversation(id)!.Provider).Should().Be((Antigravity.Id, AgentProvider.Antigravity));
        h.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Notice && i.Text.StartsWith("已從「Claude 主帳戶」改用「Antigravity」繼續；下一則訊息會附上"));

        h.Send(h.Main, id, "第二則");
        var s2 = h.Sessions[^1];
        s2.Sent.All.Should().ContainSingle().Which.Settings.AccountKey.Should().StartWith(Antigravity.Id + "|");
        h.IsRunning(id).Should().BeTrue();

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude.Id });
        s2.Interrupts.Should().Be(1, "Antigravity 也能在執行中換：先停下這一輪");
        h.CompleteTurn(s2, AgentTurnOutcome.Interrupted);

        (h.Conversation(id)!.AccountId, h.Conversation(id)!.Provider).Should().Be((h.Claude.Id, AgentProvider.Claude));
        var sent = h.Sessions[^1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude.Id + "|");
        sent.Input.Text.Should().StartWith("（系統說明：原本的對話紀錄已無法續接", "前文節錄在前面");
        sent.Input.Text.Should().Contain("（系統通知：使用者在這一輪執行中把這個對話從 Antigravity 改用 Claude Code");
        h.IsRunning(id).Should().BeTrue("接續的一輪在執行");
    }

    [Fact]
    public void 停止還沒生效這一輪就自己做完_只換帳戶不接續_排隊的訊息在新帳戶送()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.Send(h.Main, id, "排隊B");
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });

        h.CompleteTurn(s1);   // 停止送到之前就正常結束

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Notice && i.Text.StartsWith("已從「Claude 主帳戶」改用「Claude 副帳戶」繼續；下一則訊息會附上"));
        var s2 = h.Sessions[1];
        var sent = s2.Sent.All.Should().ContainSingle().Subject;
        sent.Input.Text.Should().EndWith("排隊B", "沒有接續訊息，排隊的訊息照常送（附前文節錄）");
        sent.Input.Text.Should().NotContain("系統通知：使用者在這一輪執行中");
    }

    [Fact]
    public void 執行中選了別的帳戶又選回目前的_不換_照一般的停止收尾()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude.Id });

        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Sessions.Should().ContainSingle();
        h.Items(id)[^1].Text.Should().Be("已停止這一輪");
        h.IsRunning(id).Should().BeFalse();
    }

    [Fact]
    public void 停下途中失敗也換過去接著做()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });

        h.CompleteTurn(s1, AgentTurnOutcome.Failed, "連線中斷");

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Error && i.Text == "連線中斷");
        h.Sessions[1].Sent.All.Should().ContainSingle().Which.Input.Text.Should().Contain("系統通知：使用者在這一輪執行中");
    }

    // ───── Claude 與 Codex 互換（使用者 2026-10-24） ─────

    [Fact]
    public void Claude執行中換到Codex_先停下_結束後以Codex開新對話從中斷處接著做()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        h.Raise(s1, new AgentToolStartedEvent("t1", "執行指令", "bash", "dotnet build", "dotnet build"));

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Codex.Id });

        s1.Interrupts.Should().Be(1, "立即換：先停下這一輪");
        h.Conversation(id)!.Provider.Should().Be(AgentProvider.Claude, "這一輪結束之前還是 Claude");
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);

        var meta = h.Conversation(id)!;
        (meta.AccountId, meta.Provider, meta.SessionStarted).Should().Be((h.Codex.Id, AgentProvider.Codex, false));
        s1.Disposed.Should().BeTrue();
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Codex.Id + "|");
        sent.Input.Text.Should().StartWith("（系統說明：原本的對話紀錄已無法續接", "前文節錄在前面");
        sent.Input.Text.Should().Contain("【使用者】把建置修好");
        sent.Input.Text.Should().Contain("（系統通知：使用者在這一輪執行中把這個對話從 Claude Code 改用 Codex");
        sent.Input.Text.Should().Contain("〈執行指令：dotnet build（未完成）〉");
        h.IsRunning(id).Should().BeTrue("接續的一輪在執行");
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildUserSwitchNotice("Claude 主帳戶", "Codex 帳戶"));
    }

    [Fact]
    public void Codex執行中選這一輪結束後換到Claude_做完後換過去_排隊的訊息由Claude送()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Codex, "第一則");
        h.Send(h.Main, id, "排隊B");
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude.Id));
        s1.Interrupts.Should().Be(0, "不打斷這一輪");

        h.CompleteTurn(s1);

        var meta = h.Conversation(id)!;
        (meta.AccountId, meta.Provider).Should().Be((h.Claude.Id, AgentProvider.Claude));
        h.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Notice && i.Text.StartsWith("已從「Codex 帳戶」改用「Claude 主帳戶」繼續；下一則訊息會附上"));
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude.Id + "|");
        sent.Input.Text.Should().StartWith("（系統說明：原本的對話紀錄已無法續接");
        sent.Input.Text.Should().EndWith("排隊B").And.NotContain("系統通知");
    }

    // ───── 這一輪結束後再換（when turnEnd） ─────

    private static object AtTurnEnd(string id, string accountId) => new { conversationId = id, accountId, when = AccountSwitchRules.WhenTurnEnd };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Codex等目前步驟完成_工具結束後自動交接_不等原任務做完(bool crossProvider)
    {
        using var h = new HubHarness();
        SeedMore(h, Codex2);
        var target = crossProvider ? h.Claude : Codex2;
        var (id, s1) = h.StartConversation(h.Main, h.Codex, "修正功能並跑完測試");
        h.Raise(s1, new AgentSessionStartedEvent("thread-old", null, null));
        h.Send(h.Main, id, "完成後整理說明");
        h.Raise(s1, new AgentToolStartedEvent("build", "執行指令", "bash", "dotnet build", "dotnet build"));
        h.Command(h.Main, "setAccount", AtTurnEnd(id, target.Id));
        s1.Interrupts.Should().Be(0, "正在建置時先等它完成");

        s1.SafePoint = true;
        h.Raise(s1, new AgentSafePointChangedEvent(false, false, false));
        s1.Interrupts.Should().Be(0, "會話先回安全點時，仍要等工具結果進入前文");
        h.Raise(s1, new AgentToolCompletedEvent("build", AgentItemStatus.Done, "建置成功", []));
        s1.Interrupts.Should().Be(1, "工具做完就主動交接，不依賴原模型自行結束長任務");
        h.Raise(s1, new AgentSafePointChangedEvent(false, false, false));
        s1.Interrupts.Should().Be(1, "重複事件不重複停止");
        h.Conversation(id)!.AccountId.Should().Be(h.Codex.Id, "先確認舊回合停下才換帳戶");

        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);
        h.Conversation(id)!.AccountId.Should().Be(target.Id);
        h.IsRunning(id).Should().BeTrue("新帳戶自動接續原任務");
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Input.Text.Should().Contain("修正功能並跑完測試").And.Contain("dotnet build");
        h.Items(id).Single(i => i.Kind == AgentItemKind.Tool).Status.Should().Be(AgentItemStatus.Done);
        h.Items(id).Should().NotContain(i => i.Text.StartsWith("已停止這一輪"));
        h.Queue(id).Should().Equal("完成後整理說明");
    }

    [Fact]
    public void 等目前步驟完成_已在安全點就開始交接_不用等新的事件()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s1, new AgentSessionStartedEvent("thread-old", null, null));
        s1.SafePoint = true;
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude.Id));
        s1.Interrupts.Should().Be(1);
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);
        h.IsRunning(id).Should().BeTrue();
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
    }

    [Fact]
    public void 等目前步驟完成_舊安全點事件不會打斷新工具或背景工作()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s1, new AgentSessionStartedEvent("thread-old", null, null));
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude.Id));
        s1.SafePoint = false;
        s1.HasLiveBackgroundWork = true;
        h.Raise(s1, new AgentSafePointChangedEvent(false, false, false));
        s1.Interrupts.Should().Be(0, "依會話的即時狀態，不能信排隊中的舊事件");
        s1.HasLiveBackgroundWork = false;
        s1.SafePoint = true;
        h.Raise(s1, new AgentSafePointChangedEvent(false, false, false));
        s1.Interrupts.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 等目前步驟完成_已取消或使用者按停止_安全點不會再自動交接(bool cancel)
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s1, new AgentSessionStartedEvent("thread-old", null, null));
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude.Id));
        if (cancel)
            h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Codex.Id });
        else
            h.Command(h.Main, "stop", new { conversationId = id });
        s1.SafePoint = true;
        h.Raise(s1, new AgentSafePointChangedEvent(false, false, false));
        s1.Interrupts.Should().Be(cancel ? 0 : 1);
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);
        h.IsRunning(id).Should().BeFalse();
        h.Sessions.Should().ContainSingle("使用者停止時不自動重啟工作");
    }

    [Fact]
    public void 等目前步驟完成_尚未建立會話先等待_到了安全點使用最後選擇的帳戶()
    {
        using var h = new HubHarness();
        SeedMore(h, Codex2);
        var (id, s1) = h.StartConversation(h.Main, h.Codex, started: false);
        s1.SafePoint = true;
        h.Command(h.Main, "setAccount", AtTurnEnd(id, Codex2.Id));
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude.Id));
        s1.Interrupts.Should().Be(0, "還在準備第一輪，不能把原訊息取消掉");
        h.Raise(s1, new AgentSessionStartedEvent("thread-old", null, null));
        s1.Interrupts.Should().Be(1);
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
    }

    [Fact]
    public void 等目前步驟完成_有待回答請求時先等_回答後才交接()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s1, new AgentQuestionEvent("q1", [new AgentQuestion { Id = "q", Text = "用哪個格式？" }], AnswerAsMessage: false));
        s1.SafePoint = true;
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude.Id));
        s1.Interrupts.Should().Be(0);
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new { q = new[] { "文字" } } });
        h.Raise(s1, new AgentSafePointChangedEvent(false, false, false));
        s1.Interrupts.Should().Be(1);
        s1.Answers.Count.Should().Be(1, "先把回答送給原帳戶，再切換");
    }

    [Fact]
    public void 等目前步驟完成_目標帳戶已登出_不為失效的切換中斷工作()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Codex);
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));
        h.Ui.Invoke(() => h.Accounts.GetState(h.Claude2.Id).Status = AccountStatus.NotLoggedIn);
        s1.SafePoint = true;
        h.Raise(s1, new AgentSafePointChangedEvent(false, false, false));
        s1.Interrupts.Should().Be(0);
        h.IsRunning(id).Should().BeTrue();
    }

    [Fact]
    public async Task 這一輪結束後再換_不停下_標頭標出要換的帳戶_正常結束後換過去_排隊的訊息在新帳戶送()
    {
        using var h = new HubHarness();
        SeedMore(h, Codex2);
        var (id, s1) = h.StartConversation(h.Main, h.Codex, "第一則");
        h.Send(h.Main, id, "排隊B");
        var mark = h.Main.Mark();

        h.Command(h.Main, "setAccount", AtTurnEnd(id, Codex2.Id));

        s1.Interrupts.Should().Be(0, "不打斷這一輪");
        h.IsRunning(id).Should().BeTrue();
        h.Conversation(id)!.AccountId.Should().Be(h.Codex.Id);
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Be("目前步驟完成後改用「Codex 副帳戶」繼續");
        var header = await h.Main.WaitForAsync("header", m => m["header"]?["pendingAccount"] is JObject, since: mark, conversationId: id);
        JToken.DeepEquals(header["header"]!["pendingAccount"], new JObject { ["accountId"] = Codex2.Id, ["label"] = "Codex 副帳戶", ["when"] = "turnEnd" })
            .Should().BeTrue(header["header"]!["pendingAccount"]!.ToString());
        mark = h.Main.Mark();

        h.CompleteTurn(s1);

        h.Conversation(id)!.AccountId.Should().Be(Codex2.Id);
        s1.Disposed.Should().BeTrue("舊帳戶的程序關掉");
        var items = h.Items(id);
        items.Should().Contain(i => i.Kind == AgentItemKind.Notice && i.Text.StartsWith("已從「Codex 帳戶」改用「Codex 副帳戶」繼續；下一則訊息會附上"));
        items.Should().NotContain(i => i.Text.StartsWith("已停止這一輪"));
        var s2 = h.Sessions[1];
        var sent = s2.Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(Codex2.Id + "|");
        sent.Input.Text.Should().EndWith("排隊B", "沒有接續訊息，排隊的訊息在新帳戶送（附前文節錄）");
        sent.Input.Text.Should().NotContain("系統通知");
        h.Settings.LastAccountId.Should().Be(Codex2.Id, "本機換的記成上次使用的帳戶");
        var switched = await h.Main.WaitForAsync("conversation", m => m["header"]?["accountId"]?.Value<string>() == Codex2.Id, since: mark, conversationId: id);
        switched["header"]!["pendingAccount"]!.Type.Should().Be(JTokenType.Null, "換過去之後沒有待換的帳戶");
    }

    [Fact]
    public void 這一輪結束後再換_使用者按停止_照停止收尾再換_不接續_佇列暫停()
    {
        using var h = new HubHarness();
        SeedMore(h, Codex2);
        var (id, s1) = h.StartConversation(h.Main, h.Codex, "第一則");
        h.Send(h.Main, id, "排隊B");
        h.Command(h.Main, "setAccount", AtTurnEnd(id, Codex2.Id));

        h.Command(h.Main, "stop", new { conversationId = id });
        s1.Interrupts.Should().Be(1);
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);

        h.Conversation(id)!.AccountId.Should().Be(Codex2.Id);
        h.IsRunning(id).Should().BeFalse("使用者按了停止：不接續");
        h.Items(id).Should().Contain(i => i.Text == "已停止這一輪（排隊的訊息暫停，按送出繼續）");
        h.Sessions.Should().ContainSingle("還沒送任何東西給新帳戶");
        h.Queue(id).Should().Equal("排隊B");
    }

    [Fact]
    public void 這一輪結束後再換_一般失敗_換過去但不接續_佇列暫停()
    {
        using var h = new HubHarness();
        SeedMore(h, Codex2);
        var (id, s1) = h.StartConversation(h.Main, h.Codex, "第一則");
        h.Send(h.Main, id, "排隊B");
        h.Command(h.Main, "setAccount", AtTurnEnd(id, Codex2.Id));

        h.CompleteTurn(s1, AgentTurnOutcome.Failed, "連線中斷");

        h.Conversation(id)!.AccountId.Should().Be(Codex2.Id);
        h.IsRunning(id).Should().BeFalse();
        var items = h.Items(id);
        items.Should().Contain(i => i.Kind == AgentItemKind.Error && i.Text == "連線中斷");
        items.Should().Contain(i => i.Text == "排隊的訊息已暫停，按送出繼續");
        h.Sessions.Should().ContainSingle();
    }

    [Fact]
    public void 這一輪結束後再換_原帳戶額度用完而失敗_改用選的帳戶從中斷處接著做()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));
        h.Raise(s1, new AgentToolStartedEvent("t1", "執行指令", "bash", "dotnet build", "dotnet build"));

        h.Raise(s1, new AgentQuotaExhaustedEvent(AgentProvider.Claude, DateTimeOffset.Now.AddHours(3)));
        h.CompleteTurn(s1, AgentTurnOutcome.Failed, "You've hit your session limit");

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude2.Id + "|");
        sent.Input.Text.Should().Contain("（系統通知：原本的帳戶訂閱額度用完");
        sent.Input.Text.Should().Contain("〈執行指令：dotnet build（未完成）〉");
        h.IsRunning(id).Should().BeTrue("接續的一輪在執行");
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildChosenAfterQuotaNotice("Claude 主帳戶", "Claude 副帳戶"));
    }

    [Fact]
    public async Task 這一輪結束後再換_選回目前的帳戶就取消_照常做完不換()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));
        var mark = h.Main.Mark();

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude.Id });

        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Be("已取消：目前步驟完成後不換帳戶");
        var header = await h.Main.WaitForAsync("header", m => m["header"]?["pendingAccount"]?.Type == JTokenType.Null, since: mark, conversationId: id);
        header["header"]!.Value<string>("status").Should().NotBe("停止中…", "沒有在停下");
        s1.Interrupts.Should().Be(0);

        h.CompleteTurn(s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Sessions.Should().ContainSingle();
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Notice);
    }

    [Fact]
    public void 立即換已在停下時改選這一輪結束後再換_停止收不回來_照立即換接著做()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });
        s1.Interrupts.Should().Be(1);

        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Items(id).Should().NotContain(i => i.Text.StartsWith("已停止這一輪"));
        h.Sessions[1].Sent.All.Should().ContainSingle().Which.Input.Text.Should().Contain("系統通知：使用者在這一輪執行中");
    }

    [Fact]
    public void 這一輪結束後再換之後改選立即_停下並接著做()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));
        s1.Interrupts.Should().Be(0);

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id, when = AccountSwitchRules.WhenNow });
        s1.Interrupts.Should().Be(1);
        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Sessions[1].Sent.All.Should().ContainSingle().Which.Input.Text.Should().Contain("系統通知：使用者在這一輪執行中");
    }

    [Fact]
    public async Task 遠端選這一輪結束後再換_done_不停下_結束後換過去_when不認得的值invalid()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);

        var bad = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("setAccount", id, new { accountId = h.Claude2.Id, when = "later" }), 2));
        RemoteEntrySamples.ShouldBeRejected(bad, RemoteResultCodes.Invalid, RemoteGateSteps.Shape);

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("setAccount", id, new { accountId = h.Claude2.Id, when = "turnEnd" }), 2));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s1.Interrupts.Should().Be(0, "不打斷這一輪");

        h.CompleteTurn(s1);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Settings.LastAccountId.Should().NotBe(h.Claude2.Id, "遠端不改「上次使用的帳戶」（D23）");
        h.IsRunning(id).Should().BeFalse("正常結束：不接續");
    }

    [Fact]
    public async Task 遠端執行中換帳戶_換到Grok等這一輪結束也done_改選立即換到同種帳戶_停下後換過去()
    {
        using var h = new HubHarness();
        var grok1 = Grok(h, "acc-grok1", "Grok 主帳戶");
        SeedMore(h, grok1);
        RemoteEntrySamples.AllowRemote(h);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);

        var crossed = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("setAccount", id, new { accountId = grok1.Id, when = "turnEnd" }), 2));
        crossed.State.Should().Be(RemoteResultStates.Done, crossed.Message);
        s1.Interrupts.Should().Be(0, "等這一輪結束才換");

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("setAccount", id, new { accountId = h.Claude2.Id }), 2));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s1.Interrupts.Should().Be(1);

        h.CompleteTurn(s1, AgentTurnOutcome.Interrupted);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Settings.LastAccountId.Should().NotBe(h.Claude2.Id, "遠端不改「上次使用的帳戶」（D23）");
        h.IsRunning(id).Should().BeTrue("接續的一輪在執行");
    }
}
