using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 換帳戶時還有背景工作在跑（2026-10-27 記錄：「這一輪結束後再換」在一輪結束時直接換過去，ApplyAccountChange 關掉程序，
/// 兩個背景擷取跟著被停掉、代理不知道，新帳戶的代理花了 8 分鐘查原因再重跑）：等得到就等背景工作都結束、對話閒置時才換
/// （meta 的 DeferredAccount）；使用者確認現在就換（when now）時照換，並隱藏送出接續訊息告訴新帳戶哪些背景工作被停掉了
/// </summary>
public class AgentHubDeferredAccountSwitchTests
{
    private static readonly AgentBackgroundTool Scan = new("執行指令", "bash", "dotnet test 擷取第一批", null);

    private static object AtTurnEnd(string id, string accountId) => new { conversationId = id, accountId, when = AccountSwitchRules.WhenTurnEnd };

    private static void Maintain(HubHarness h) => h.Ui.Invoke(() => h.Hub.RunMaintenance());

    /// <summary>會話回報有背景工作（同真的會話：HasLiveBackgroundWork 也是 true）</summary>
    private static void StartBackground(HubHarness h, FakeSession session)
    {
        session.HasLiveBackgroundWork = true;
        h.Raise(session, new AgentBackgroundWorkEvent(1, [Scan]));
    }

    private static void EndBackground(HubHarness h, FakeSession session)
    {
        session.HasLiveBackgroundWork = false;
        h.Raise(session, new AgentBackgroundWorkEvent(0, []));
    }

    [Fact]
    public async Task 這一輪結束後再換_結束時還有背景工作_不關程序_等背景工作結束才換()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "擷取競業資料");
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));
        StartBackground(h, s1);
        var mark = h.Main.Mark();

        h.CompleteTurn(s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id, "背景工作還在跑：換帳戶會關掉程序");
        s1.Disposed.Should().BeFalse("背景工作跟著程序，不能關");
        h.Conversation(id)!.DeferredAccount.Should().Be(new DeferredAccountSwitch(h.Claude2.Id, RememberAccount: true));
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildDeferredNotice(1, "Claude 副帳戶"));
        var header = await h.Main.WaitForAsync("header", m => m["header"]?["pendingAccount"] is JObject, since: mark, conversationId: id);
        JToken.DeepEquals(header["header"]!["pendingAccount"],
                new JObject { ["accountId"] = h.Claude2.Id, ["label"] = "Claude 副帳戶", ["when"] = "turnEnd", ["background"] = true })
            .Should().BeTrue(header["header"]!["pendingAccount"]!.ToString());

        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id, "背景工作還沒結束，維護也不換");

        // 背景工作結束：Claude 的 CLI 自己開一輪回報結果，那一輪由原帳戶做完
        EndBackground(h, s1);
        h.Raise(s1, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id, "代理還在處理背景工作的結果");
        h.CompleteTurn(s1);
        Maintain(h);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Conversation(id)!.DeferredAccount.Should().BeNull();
        s1.Disposed.Should().BeTrue();
        h.Sessions.Should().ContainSingle("只換帳戶，沒有送東西給新帳戶");
        h.Items(id)[^1].Text.Should().StartWith("已從「Claude 主帳戶」改用「Claude 副帳戶」繼續；下一則訊息會附上");
        h.Settings.LastAccountId.Should().Be(h.Claude2.Id);
    }

    [Fact]
    public void 閒置時有背景工作_沒帶when_等背景工作結束才換_送出時已結束就先換過去由新帳戶送()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        StartBackground(h, s1);
        h.CompleteTurn(s1);

        // 瀏覽器版與其他電腦看不到這台的背景工作、不帶 when：等背景工作結束
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        s1.Disposed.Should().BeFalse();
        h.Conversation(id)!.DeferredAccount!.AccountId.Should().Be(h.Claude2.Id);

        EndBackground(h, s1);
        h.Send(h.Main, id, "接著做第二批");

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "背景工作已經結束：送出之前先換過去");
        s1.Sent.All.Should().ContainSingle("第二批不給舊帳戶");
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude2.Id + "|");
        sent.Input.Text.Should().EndWith("接著做第二批").And.StartWith("（系統說明：原本的對話紀錄已無法續接", "附前文節錄");
    }

    [Fact]
    public void 閒置時有背景工作_還沒結束就送出_由原帳戶送_仍然等著()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        StartBackground(h, s1);
        h.CompleteTurn(s1);
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));

        h.Send(h.Main, id, "背景跑完前先問一下");

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        s1.Sent.All.Should().HaveCount(2, "背景工作還在跑：照舊由原帳戶回答");
        h.Conversation(id)!.DeferredAccount.Should().NotBeNull();
    }

    [Fact]
    public void 閒置時有背景工作_確認現在換_換過去並隱藏送出接續訊息_列出被停掉的背景工作()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "擷取競業資料");
        StartBackground(h, s1);
        h.CompleteTurn(s1);

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id, when = AccountSwitchRules.WhenNow });

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Conversation(id)!.DeferredAccount.Should().BeNull();
        s1.Disposed.Should().BeTrue();
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude2.Id + "|");
        sent.Input.Text.Should().Contain("【使用者】擷取競業資料");
        sent.Input.Text.Should().Contain("（系統通知：上一輪結束後還有背景工作在跑，使用者選了現在就把帳戶換成另一個 Claude Code 帳戶");
        sent.Input.Text.Should().Contain("被停掉的背景工作：〈執行指令：dotnet test 擷取第一批〉。");
        sent.Input.Text.Should().Contain("原本還在跑的背景工作已隨舊的程序停止，結果未知");
        h.IsRunning(id).Should().BeTrue("新帳戶接著確認背景工作");
        var items = h.Items(id);
        items.Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "接續訊息不顯示成使用者訊息");
        items[^1].NoticeKind.Should().Be(AgentNoticeKind.AccountSwitch);
        items[^1].Text.Should().Be(AccountSwitchRules.BuildIdleSwitchStoppedNotice("Claude 主帳戶", "Claude 副帳戶", 1));
    }

    [Fact]
    public void 立即換_停止還沒生效這一輪就做完了_還有背景工作_照換並告訴新帳戶()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });
        s1.Interrupts.Should().Be(1);
        StartBackground(h, s1);

        h.CompleteTurn(s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "使用者選了立即換");
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Input.Text.Should().Contain("被停掉的背景工作：〈執行指令：dotnet test 擷取第一批〉。");
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildIdleSwitchStoppedNotice("Claude 主帳戶", "Claude 副帳戶", 1));
    }

    [Fact]
    public async Task 等背景工作結束才換_選回目前的帳戶就取消_背景工作結束後也不換()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        StartBackground(h, s1);
        h.CompleteTurn(s1);
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));
        var mark = h.Main.Mark();

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude.Id });

        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Be("已取消：背景工作結束後不換帳戶");
        await h.Main.WaitForAsync("header", m => m["header"]?["pendingAccount"]?.Type == JTokenType.Null, since: mark, conversationId: id);
        h.Conversation(id)!.DeferredAccount.Should().BeNull();

        EndBackground(h, s1);
        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        s1.Disposed.Should().BeFalse();
    }

    [Fact]
    public void 等背景工作結束才換_選的帳戶登出了_不換並說明()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        StartBackground(h, s1);
        h.CompleteTurn(s1);
        h.Command(h.Main, "setAccount", AtTurnEnd(id, h.Claude2.Id));
        h.Ui.Invoke(() => h.Accounts.GetState(h.Claude2.Id).Status = Myriagent.Accounts.AccountStatus.NotLoggedIn);

        EndBackground(h, s1);
        Maintain(h);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Conversation(id)!.DeferredAccount.Should().BeNull();
        s1.Disposed.Should().BeFalse();
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildDeferredDroppedNotice("Claude 副帳戶"));
    }

    [Fact]
    public void 等背景工作結束才換_記進meta檔_app換版重開之後照樣換過去()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        StartBackground(h, s1);
        h.CompleteTurn(s1);
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });

        var saved = h.Store.LoadConversations().Single(m => m.Id == id);
        saved.DeferredAccount.Should().Be(new DeferredAccountSwitch(h.Claude2.Id, RememberAccount: true),
            "app 換版會停掉背景工作：重開之後要記得使用者選了哪個帳戶");
    }
}
