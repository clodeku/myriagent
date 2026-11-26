using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Usage;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 登入失效時比照額度用完自動換帳戶繼續（AgentHub.AccountSwitch.cs，2026-11-27 使用者要求）：Claude 的登入到了使用期限時改用其他能用的帳戶、
/// 隱藏送出接續訊息；換不了時提示在 app 裡怎麼重新登入（一輪接一輪失敗只提示一次）；登入失效、登入已到期的帳戶不當候選；
/// 原帳戶重新登入、查得到用量之後換回去
/// </summary>
public class AgentHubLoginLostSwitchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(8));

    private const string TimeLimitText =
        "Failed to authenticate: this login reached its time limit and cannot be renewed. Run `claude auth login` to sign in again, or use `claude setup-token` and set CLAUDE_CODE_OAUTH_TOKEN for unattended jobs";

    private sealed class Clock
    {
        public DateTimeOffset Value = Now;
    }

    private static HubHarness NewHarness(out Clock clock)
    {
        var h = new HubHarness();
        var c = new Clock();
        h.Hub.AccountSwitchClock = () => c.Value;
        h.OnUi(() => h.Settings.AutoResumeAfterQuotaReset = false);
        clock = c;
        return h;
    }

    private static HubHarness NewHarness() => NewHarness(out _);

    /// <summary>這一輪因為登入到了使用期限而失敗（CLI 合成的訊息之後失敗的 result）</summary>
    private static void LoseLogin(HubHarness h, FakeSession s, bool timeLimit = true)
    {
        h.Raise(s, new AgentLoginLostEvent(AgentProvider.Claude, timeLimit));
        h.CompleteTurn(s, AgentTurnOutcome.Failed, timeLimit ? TimeLimitText : "Not logged in · Please run /login");
    }

    /// <summary>只留一個 Claude 帳戶（拿掉副帳戶；資料夾在暫存資料夾、不刪）</summary>
    private static void OnlyOneClaude(HubHarness h)
        => h.Ui.Invoke(() => h.Accounts.Remove(h.Claude2.Id, deleteData: false));

    [Fact]
    public void 登入到期_換到另一個帳戶_隱藏送出前文節錄與接續訊息_記下原帳戶()
    {
        using var h = NewHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        h.Raise(s1, new AgentToolStartedEvent("t1", "執行指令", "bash", "dotnet build", "dotnet build"));
        h.Raise(s1, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "失敗", Array.Empty<string>()));

        LoseLogin(h, s1);

        var meta = h.Conversation(id)!;
        meta.AccountId.Should().Be(h.Claude2.Id);
        meta.SessionStarted.Should().BeFalse("新帳戶開新 session");
        meta.QuotaHome.Should().NotBeNull();
        meta.QuotaHome!.AccountId.Should().Be(h.Claude.Id);
        meta.QuotaHome.LoginLost.Should().BeTrue("重新登入之後換回去");
        meta.QuotaHome.ResetsAt.Should().BeNull();
        h.Store.LoadConversations().Single(m => m.Id == id).QuotaHome!.LoginLost.Should().BeTrue("記在 meta.json");
        s1.Disposed.Should().BeTrue("舊帳戶的程序關掉");

        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude2.Id + "|");
        sent.Input.Text.Should().StartWith("（系統說明：原本的對話紀錄已無法續接", "前文節錄在前面");
        sent.Input.Text.Should().Contain("（系統通知：原本的帳戶登入失效（要使用者重新登入才能再用），這一輪在中途停下");
        sent.Input.Text.Should().Contain("〈執行指令：dotnet build〉");
        h.IsRunning(id).Should().BeTrue("接續的一輪在執行");

        var items = h.Items(id);
        items.Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "接續訊息不顯示成使用者訊息");
        items[^2].Kind.Should().Be(AgentItemKind.Error);
        items[^2].Text.Should().Be(TimeLimitText);
        items[^1].NoticeKind.Should().Be(AgentNoticeKind.AccountSwitch);
        items[^1].Text.Should().Be("「Claude 主帳戶」的登入已到期（Anthropic 的登入有使用期限，到期就要重新登入），已自動改用「Claude 副帳戶」繼續這個對話；" +
                                   "新帳戶會收到先前對話的節錄，從中斷處接著做。「Claude 主帳戶」重新登入後會自動換回去。");
    }

    [Fact]
    public void 只有一個帳戶_提示怎麼重新登入_一輪接一輪失敗只提示一次_正常結束後再提示()
    {
        using var h = NewHarness();
        OnlyOneClaude(h);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);

        LoseLogin(h, s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Sessions.Should().ContainSingle("沒有換帳戶");
        h.IsRunning(id).Should().BeFalse();
        var hint = AccountSwitchRules.BuildLoginLostHint("Claude 主帳戶", timeLimit: true);
        h.Items(id)[^1].Text.Should().Be(hint);
        h.Items(id)[^1].Kind.Should().Be(AgentItemKind.Notice);

        // CLI 自己開的一輪（排程、背景工作的通知）接著又失敗：不再提示
        h.Raise(s1, new AgentIdleTurnStartedEvent(AfterBackgroundTask: false));
        LoseLogin(h, s1);
        h.Send(h.Main, id, "再試一次");
        LoseLogin(h, s1);
        h.Items(id).Count(i => i.Text == hint).Should().Be(1);

        // 重新登入了、有一輪正常結束：之後再登入失效時重新提示
        h.Send(h.Main, id, "繼續");
        h.CompleteTurn(s1);
        h.Send(h.Main, id, "再一則");
        LoseLogin(h, s1);
        h.Items(id).Count(i => i.Text == hint).Should().Be(2);
    }

    [Fact]
    public void 其他帳戶都不能接手_提示寫明並提示重新登入()
    {
        using var h = NewHarness();
        h.Ui.Invoke(() => h.Accounts.SeedUsage(h.Claude2.Id, new UsageSnapshot(Now, "api",
            new[] { new UsageWindow(UsageRules.SessionKey, "5 小時", 100, Now.AddHours(1), Locked: true) }, null, null, null, false)));
        var (id, s1) = h.StartConversation(h.Main, h.Claude);

        LoseLogin(h, s1, timeLimit: false);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildLoginLostNoCandidateNotice("Claude 主帳戶", false, AgentProvider.Claude));
    }

    [Fact]
    public void 設定關掉_不換_照樣提示重新登入()
    {
        using var h = NewHarness();
        h.OnUi(() => h.Settings.AutoSwitchAccountOnLimit = false);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);

        LoseLogin(h, s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Sessions.Should().ContainSingle();
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildLoginLostHint("Claude 主帳戶", timeLimit: true));
        h.Conversation(id)!.QuotaHome.Should().BeNull();
    }

    [Fact]
    public void 按了停止_正常結束_閒置時收到_都不換也不提示()
    {
        using var h = NewHarness();

        var (stopped, s1) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "stop", new { conversationId = stopped });
        LoseLogin(h, s1);
        h.Conversation(stopped)!.AccountId.Should().Be(h.Claude.Id);
        h.Items(stopped).Should().NotContain(i => i.Text.Contains("重新登入"));

        var (completed, s2) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s2, new AgentLoginLostEvent(AgentProvider.Claude, true));
        h.CompleteTurn(s2);
        h.Conversation(completed)!.AccountId.Should().Be(h.Claude.Id, "這一輪照樣做完了：登入其實還能用");
        h.Items(completed).Should().NotContain(i => i.Kind == AgentItemKind.Notice && i.Text.Contains("登入"));

        var (idle, s3) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s3);
        h.Raise(s3, new AgentLoginLostEvent(AgentProvider.Claude, true));
        h.Send(h.Main, idle, "第二則");
        h.CompleteTurn(s3, AgentTurnOutcome.Failed, "別的錯誤");
        h.Conversation(idle)!.AccountId.Should().Be(h.Claude.Id, "閒置時收到的不留到下一輪");

        h.Sessions.Should().HaveCount(3, "都沒有開新的會話");
    }

    [Fact]
    public void 登入失效的帳戶_30分鐘內不當其他對話的候選()
    {
        using var h = NewHarness(out var clock);
        var (first, s1) = h.StartConversation(h.Main, h.Claude);
        LoseLogin(h, s1);
        h.Conversation(first)!.AccountId.Should().Be(h.Claude2.Id);

        // 另一個對話在副帳戶額度用完：主帳戶的登入剛失效，不換過去（帳戶狀態可能還沒改判未登入）
        var (second, s2) = h.StartConversation(h.Main, h.Claude2);
        h.Raise(s2, new AgentQuotaExhaustedEvent(AgentProvider.Claude, Now.AddHours(2)));
        h.CompleteTurn(s2, AgentTurnOutcome.Failed, "You've hit your session limit");
        h.Conversation(second)!.AccountId.Should().Be(h.Claude2.Id);
        h.Items(second)[^1].Text.Should().Be(AccountSwitchRules.BuildNoCandidateNotice(AgentProvider.Claude));

        // 30 分鐘之後（重新登入了）才又能接手
        clock.Value = Now + AccountSwitchRules.UnknownResetHold + TimeSpan.FromMinutes(1);
        var (third, s3) = h.StartConversation(h.Main, h.Claude2);
        h.Raise(s3, new AgentQuotaExhaustedEvent(AgentProvider.Claude, clock.Value.AddHours(2)));
        h.CompleteTurn(s3, AgentTurnOutcome.Failed, "You've hit your session limit");
        h.Conversation(third)!.AccountId.Should().Be(h.Claude.Id);
    }

    [Fact]
    public void 登入已到期的帳戶不當候選_額度用完與登入失效都是()
    {
        using var h = NewHarness();
        h.Ui.Invoke(() => h.Accounts.GetState(h.Claude2.Id).LoginExpiresAt = Now.AddHours(-2).ToUnixTimeMilliseconds());

        var (quota, s1) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s1, new AgentQuotaExhaustedEvent(AgentProvider.Claude, Now.AddHours(2)));
        h.CompleteTurn(s1, AgentTurnOutcome.Failed, "You've hit your session limit");
        h.Conversation(quota)!.AccountId.Should().Be(h.Claude.Id, "副帳戶的登入已到期：手上的權杖很快也會失效");
        h.Items(quota)[^1].Text.Should().Be(AccountSwitchRules.BuildNoCandidateNotice(AgentProvider.Claude));

        var (login, s2) = h.StartConversation(h.Main, h.Claude);
        LoseLogin(h, s2);
        h.Conversation(login)!.AccountId.Should().Be(h.Claude.Id);
        h.Items(login)[^1].Text.Should().Be(AccountSwitchRules.BuildLoginLostNoCandidateNotice("Claude 主帳戶", true, AgentProvider.Claude));

        // 到期時間還沒到的照常換
        h.Ui.Invoke(() => h.Accounts.GetState(h.Claude2.Id).LoginExpiresAt = Now.AddDays(10).ToUnixTimeMilliseconds());
        var (later, s3) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s3, new AgentQuotaExhaustedEvent(AgentProvider.Claude, Now.AddHours(2)));
        h.CompleteTurn(s3, AgentTurnOutcome.Failed, "You've hit your session limit");
        h.Conversation(later)!.AccountId.Should().Be(h.Claude2.Id);
    }

    [Fact]
    public void 這一輪結束後再換_原帳戶登入失效而失敗_改用選的帳戶從中斷處接著做()
    {
        using var h = NewHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id, when = AccountSwitchRules.WhenTurnEnd });

        LoseLogin(h, s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Conversation(id)!.QuotaHome.Should().BeNull("使用者自己選了帳戶：不換回");
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Input.Text.Should().Contain("（系統通知：原本的帳戶登入失效（要使用者重新登入才能再用），這一輪在中途停下；Clode Myriagent 已改用使用者事先選的另一個");
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildChosenAfterQuotaNotice("Claude 主帳戶", "Claude 副帳戶", new AgentLoginLostEvent(AgentProvider.Claude, true)));
    }

    [Fact]
    public void 原帳戶重新登入_查得到用量之後換回去_提示寫已重新登入()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        LoseLogin(h, s1);
        var s2 = h.Sessions[1];
        h.Raise(s2, new AgentSessionStartedEvent("sess-2", null, null));
        h.CompleteTurn(s2);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);

        // 原帳戶還沒重新登入（CLI 清空了登入、監看改判未登入）：不換回
        h.Ui.Invoke(() => h.Accounts.GetState(h.Claude.Id).Status = AccountStatus.NotLoggedIn);
        clock.Value = Now.AddHours(1);
        h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now));
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);

        // 重新登入了，但還沒查到登入之後的用量：不換回
        h.Ui.Invoke(() => h.Accounts.GetState(h.Claude.Id).Status = AccountStatus.Ready);
        h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now));
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "沒有正面的證據不換");

        h.Ui.Invoke(() => h.Accounts.SeedUsage(h.Claude.Id, new UsageSnapshot(clock.Value, "api",
            new[] { new UsageWindow(UsageRules.SessionKey, "5 小時", 10, clock.Value.AddHours(4)) }, null, null, null, false)));
        h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now));

        var meta = h.Conversation(id)!;
        meta.AccountId.Should().Be(h.Claude.Id);
        meta.QuotaHome.Should().BeNull();
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildReturnNotice("Claude 主帳戶", "Claude 副帳戶", loginLost: true));
    }
}
