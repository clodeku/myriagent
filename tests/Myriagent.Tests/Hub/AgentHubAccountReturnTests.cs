using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Json;
using Myriagent.Usage;
using Newtonsoft.Json;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 額度用完自動換到別的帳戶之後，原帳戶的額度恢復了就換回去（AgentHub.AccountSwitch.cs 的 TryReturnHome，2026-10-26 使用者要求）：
/// 閒置時由維護換、送出之前換；執行中、有背景工作、等你回應、Remote Control 剛有動靜時不換；使用者自己換了帳戶、設定關掉時不換；
/// 原帳戶記在 meta.json，app 重開之後照樣換回
/// </summary>
public class AgentHubAccountReturnTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8));

    /// <summary>原帳戶的重置時間（HitLimit 帶的）</summary>
    private static readonly DateTimeOffset HomeReset = Now.AddHours(3);

    /// <summary>重置時間過了寬限之後</summary>
    private static readonly DateTimeOffset AfterReset = HomeReset + AccountSwitchRules.ReturnGrace + TimeSpan.FromMinutes(1);

    private sealed class Clock
    {
        public DateTimeOffset Value = Now;
    }

    private static HubHarness NewHarness(out Clock clock)
    {
        var h = new HubHarness();
        var c = new Clock();
        h.Hub.AccountSwitchClock = () => c.Value;
        clock = c;
        return h;
    }

    private static void SeedUsage(HubHarness h, string accountId, params UsageWindow[] windows)
        => h.Ui.Invoke(() => h.Accounts.SeedUsage(accountId, new UsageSnapshot(Now, "api", windows, null, null, null, false)));

    private static void Maintain(HubHarness h, DateTime? localNow = null) => h.Ui.Invoke(() => h.Hub.RunMaintenance(localNow ?? DateTime.Now));

    /// <summary>主帳戶這一輪額度用完、自動換到副帳戶，副帳戶的接續這一輪做完（閒置）；回傳副帳戶的會話</summary>
    private static FakeSession SwitchAway(HubHarness h, FakeSession s1, AgentProvider provider = AgentProvider.Claude, bool finish = true)
    {
        var before = h.Sessions.Count;
        h.Raise(s1, new AgentQuotaExhaustedEvent(provider, HomeReset));
        h.CompleteTurn(s1, AgentTurnOutcome.Failed, "You've hit your session limit · resets 3pm (Asia/Taipei)");
        h.Sessions.Count.Should().Be(before + 1, "換到副帳戶開了新的會話");
        var s2 = h.Sessions[^1];
        h.Raise(s2, new AgentSessionStartedEvent("sess-" + Guid.NewGuid().ToString("N")[..8], null, null));
        if (finish)
            h.CompleteTurn(s2);
        SettleRefresh(h);
        return s2;
    }

    /// <summary>
    /// 額度用完會立即重查那個帳戶的用量（AccountManager.ReportQuotaExhausted）；測試不連網，查詢失敗會把帳戶標成錯誤。
    /// 等查詢結束、把狀態放回已登入（正式環境的查詢會成功）
    /// </summary>
    private static void SettleRefresh(HubHarness h)
    {
        foreach (var account in h.OnUi(() => h.Accounts.Accounts.ToList()))
        {
            h.OnUi(() => h.Accounts.RefreshSettledForTest(account.Id)).Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("用量重查要結束");
            h.Ui.Invoke(() => h.Accounts.GetState(account.Id).Status = AccountStatus.Ready);
        }
    }

    [Fact]
    public void 原帳戶重置之後_閒置時換回去_加提示_下一則附前文節錄給原帳戶()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        var s2 = SwitchAway(h, s1);

        var meta = h.Conversation(id)!;
        meta.AccountId.Should().Be(h.Claude2.Id);
        meta.QuotaHome.Should().NotBeNull();
        meta.QuotaHome!.AccountId.Should().Be(h.Claude.Id);
        meta.QuotaHome.ResetsAt.Should().Be(HomeReset);
        h.Items(id).Single(i => i.NoticeKind == AgentNoticeKind.AccountSwitch).Text
            .Should().EndWith("「Claude 主帳戶」的額度恢復後會自動換回去。", "換走時說明之後會換回來");
        h.Store.LoadConversations().Single(m => m.Id == id).QuotaHome!.AccountId.Should().Be(h.Claude.Id, "記在 meta.json");

        // 還沒重置、剛重置還在寬限內：不換
        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        clock.Value = HomeReset.AddSeconds(30);
        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "重置之後再等 1 分鐘");

        clock.Value = AfterReset;
        Maintain(h);

        meta = h.Conversation(id)!;
        meta.AccountId.Should().Be(h.Claude.Id);
        meta.QuotaHome.Should().BeNull();
        meta.SessionStarted.Should().BeFalse("原帳戶開新的 session");
        s2.Disposed.Should().BeTrue("副帳戶的程序關掉");
        var notice = h.Items(id)[^1];
        notice.NoticeKind.Should().Be(AgentNoticeKind.AccountSwitch);
        notice.Text.Should().Be("「Claude 主帳戶」的額度已恢復，已從「Claude 副帳戶」換回「Claude 主帳戶」繼續這個對話；接下來的訊息會附上先前對話的節錄，讓代理了解前情。");
        h.Store.LoadConversations().Single(m => m.Id == id).QuotaHome.Should().BeNull("換回之後存檔");

        Maintain(h);
        h.Items(id).Count(i => i.NoticeKind == AgentNoticeKind.AccountSwitch).Should().Be(2, "換回之後不再處理");

        h.Send(h.Main, id, "接著做");
        var sent = h.Sessions[^1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude.Id + "|");
        sent.Input.Text.Should().StartWith("（系統說明：原本的對話紀錄已無法續接", "前文節錄在前面").And.EndWith("接著做");
    }

    [Fact]
    public void 送出之前換回_提示在使用者訊息之前()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        SwitchAway(h, s1);
        clock.Value = AfterReset;

        h.Send(h.Main, id, "下一步");

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        var items = h.Items(id);
        items[^2].NoticeKind.Should().Be(AgentNoticeKind.AccountSwitch);
        items[^2].Text.Should().StartWith("「Claude 主帳戶」的額度已恢復");
        items[^1].Kind.Should().Be(AgentItemKind.User);
        items[^1].Text.Should().Be("下一步");
        var sent = h.Sessions[^1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude.Id + "|");
        sent.Input.Text.Should().Contain("【使用者】第一則").And.EndWith("下一步");
    }

    [Fact]
    public void 斜線指令送出前不換_等下一則一般的訊息()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        var s2 = SwitchAway(h, s1);
        clock.Value = AfterReset;

        h.Send(h.Main, id, "/compact");

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        s2.Sent.All[^1].Input.Text.Should().Be("/compact");
    }

    [Fact]
    public void 執行中不換_這一輪結束後排隊的訊息在原帳戶送()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        var s2 = SwitchAway(h, s1, finish: false);
        clock.Value = AfterReset;

        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "副帳戶的這一輪還在執行");
        h.Send(h.Main, id, "排隊的");
        h.Queue(id).Should().Equal("排隊的");

        h.CompleteTurn(s2);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        s2.Disposed.Should().BeTrue();
        h.Queue(id).Should().BeEmpty();
        var sent = h.Sessions[^1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude.Id + "|");
        sent.Input.Text.Should().EndWith("排隊的");
    }

    [Fact]
    public void 有存活的背景工作_有等你回應的問卷_都等它們結束才換()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        var s2 = SwitchAway(h, s1, finish: false);
        clock.Value = AfterReset;

        // 這一輪留下以訊息回答的問卷（這一輪結束後還在等你回應），背景工作也還在跑
        h.Raise(s2, new AgentQuestionEvent("async:m1", new[] { new AgentQuestion { Id = "m1:0", Text = "要哪種格式？", Options = { new AgentQuestionOption { Label = "Excel" } } } }, AnswerAsMessage: true));
        h.CompleteTurn(s2);
        s2.HasLiveBackgroundWork = true;
        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "背景工作還在跑：換帳戶會關掉程序");

        s2.HasLiveBackgroundWork = false;
        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "問卷還在等你回應");

        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "Excel" } } });
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "答案給問的那個程序");
        s2.Sent.All[^1].Input.Text.Should().Be("我的回答：\n要哪種格式？：Excel");

        h.CompleteTurn(s2);
        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id, "都結束了就換");
    }

    [Fact]
    public void Claude進行中的目標_閒置與送出都不換_目標清掉之後才換()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        var s2 = SwitchAway(h, s1);
        h.Raise(s2, new AgentGoalEvent("九個檔案都改完"));
        clock.Value = AfterReset;

        Maintain(h);
        h.Send(h.Main, id, "繼續");
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "目標只活在副帳戶的程序裡");
        s2.Sent.All[^1].Input.Text.Should().Be("繼續");

        h.Raise(s2, new AgentGoalEvent(null));
        h.CompleteTurn(s2);
        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
    }

    [Fact]
    public void RemoteControl開著而且剛有動靜_閒置時先不換_10分鐘沒動靜才換()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        var s2 = SwitchAway(h, s1);
        s2.HasRemoteControl = true;
        clock.Value = AfterReset;

        Maintain(h);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "可能正從手機在用");

        Maintain(h, DateTime.Now + AccountSwitchRules.ReturnRemoteIdle + TimeSpan.FromMinutes(1));
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
    }

    [Fact]
    public void RemoteControl開著_從app送出時照樣換回()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        var s2 = SwitchAway(h, s1);
        s2.HasRemoteControl = true;
        clock.Value = AfterReset;

        h.Send(h.Main, id, "下一步");

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id, "人就在 app 這裡");
    }

    [Fact]
    public void 使用者自己換了帳戶_設定關掉_都不換回()
    {
        using var h = NewHarness(out var clock);

        // 自己換到 Codex：不再記著原帳戶
        var (manual, s1) = h.StartConversation(h.Main, h.Claude);
        SwitchAway(h, s1);
        h.Command(h.Main, "setAccount", new { conversationId = manual, accountId = h.Codex.Id });
        h.Conversation(manual)!.QuotaHome.Should().BeNull();

        // 設定關掉：記著，但不換
        var (off, s3) = h.StartConversation(h.Main, h.Claude);
        SwitchAway(h, s3);
        h.Command(h.Main, "setting", new { key = "autoSwitchAccount", value = false });

        clock.Value = AfterReset;
        Maintain(h);

        h.Conversation(manual)!.AccountId.Should().Be(h.Codex.Id);
        h.Conversation(off)!.AccountId.Should().Be(h.Claude2.Id);
        h.Conversation(off)!.QuotaHome.Should().NotBeNull("設定再打開還會換回");

        h.Command(h.Main, "setting", new { key = "autoSwitchAccount", value = true });
        Maintain(h);
        h.Conversation(off)!.AccountId.Should().Be(h.Claude.Id);
    }

    [Fact]
    public void 跨代理備援之後換回_還原原本的模型_推理強度與權限模式()
    {
        using var h = NewHarness(out var clock);
        SeedUsage(h, h.Claude2.Id, new UsageWindow(UsageRules.SessionKey, "5 小時", 100, Now.AddHours(4), Locked: true));
        h.Command(h.Main, "setting", new { key = "autoSwitchCrossProvider", value = true });
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s1);
        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "opus" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "effort", value = "max" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "AcceptEdits" });
        h.Send(h.Main, id, "第二則");

        SwitchAway(h, s1, AgentProvider.Claude);
        var meta = h.Conversation(id)!;
        (meta.AccountId, meta.Provider).Should().Be((h.Codex.Id, AgentProvider.Codex), "Claude 副帳戶也滿了，換到 Codex");
        meta.Model.Should().NotBe("opus");

        clock.Value = AfterReset;
        Maintain(h);

        meta = h.Conversation(id)!;
        (meta.AccountId, meta.Provider).Should().Be((h.Claude.Id, AgentProvider.Claude));
        (meta.Model, meta.Effort, meta.PermissionMode).Should().Be(("opus", "max", AgentPermissionMode.AcceptEdits));
    }

    [Fact]
    public void 連續換了兩個帳戶_原帳戶恢復時在第二個帳戶用完的那一輪直接換回()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        var s2 = SwitchAway(h, s1, finish: false);
        clock.Value = AfterReset;

        // 副帳戶這一輪也用完：原帳戶已恢復，就是接手的帳戶
        h.Raise(s2, new AgentQuotaExhaustedEvent(AgentProvider.Claude, AfterReset.AddHours(5)));
        h.CompleteTurn(s2, AgentTurnOutcome.Failed, "You've hit your session limit");

        var meta = h.Conversation(id)!;
        meta.AccountId.Should().Be(h.Claude.Id);
        meta.QuotaHome.Should().BeNull("回到原帳戶了");
        var notice = h.Items(id)[^1];
        notice.Text.Should().Contain("已自動改用「Claude 主帳戶」").And.NotContain("會自動換回去");
    }

    [Fact]
    public void QuotaHome是null時不寫進meta_json_有值時存讀往返()
    {
        JsonConvert.SerializeObject(new AgentConversation { Title = "x" }, ProtocolJson.Settings).Should().NotContain("QuotaHome", "舊檔逐位元組不變、前一版讀得懂");

        var home = new QuotaHomeAccount("acc-claude", HomeReset, Now, "opus", "max", AgentPermissionMode.AcceptEdits, "reviewer", "Explanatory", "sonnet", "high");
        var json = JsonConvert.SerializeObject(new AgentConversation { Title = "x", QuotaHome = home }, ProtocolJson.Settings);
        json.Should().Contain("\"PermissionMode\": \"AcceptEdits\"", "列舉存名稱");
        JsonConvert.DeserializeObject<AgentConversation>(json, ProtocolJson.Settings)!.QuotaHome.Should().Be(home);
    }

    [Fact]
    public async Task app重開之後照樣換回()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        SwitchAway(h, s1);

        await h.RestartAsync();
        h.Hub.AccountSwitchClock = () => clock.Value;
        clock.Value = AfterReset;
        Maintain(h);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Items(id)[^1].Text.Should().StartWith("「Claude 主帳戶」的額度已恢復");
    }
}
