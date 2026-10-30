using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 額度用完時自動換帳戶繼續（AgentHub.AccountSwitch.cs）：一輪因為額度用完而失敗時改用還有額度的同種帳戶、隱藏送出接續訊息；
/// 設定關著、按了停止、沒有可以接手的帳戶、只是一般失敗時照舊停下
/// </summary>
public class AgentHubAccountSwitchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));

    private static HubHarness NewHarness()
    {
        var h = new HubHarness();
        h.Hub.AccountSwitchClock = () => Now;
        // 這裡測換帳戶：換不了時「等重置後自動接著做」另外在 AgentHubQuotaWaitTests 測
        h.OnUi(() => h.Settings.AutoResumeAfterQuotaReset = false);
        return h;
    }

    private static void SeedUsage(HubHarness h, string accountId, params UsageWindow[] windows)
        => h.Ui.Invoke(() => h.Accounts.SeedUsage(accountId, new UsageSnapshot(Now, "api", windows, null, null, null, false)));

    private static void HitLimit(HubHarness h, FakeSession s, AgentProvider provider = AgentProvider.Claude, DateTimeOffset? resetsAt = null)
    {
        h.Raise(s, new AgentQuotaExhaustedEvent(provider, resetsAt ?? Now.AddHours(3)));
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "You've hit your session limit · resets 3pm (Asia/Taipei)");
    }

    [Fact]
    public void 額度用完_換到還有額度的帳戶_隱藏送出前文節錄與接續訊息()
    {
        using var h = NewHarness();
        SeedUsage(h, h.Claude2.Id, new UsageWindow(UsageRules.SessionKey, "5 小時", 30, Now.AddMinutes(90)));
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        h.Raise(s1, new AgentToolStartedEvent("t1", "執行指令", "bash", "dotnet build", "dotnet build"));
        h.Raise(s1, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "失敗", Array.Empty<string>()));

        HitLimit(h, s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Conversation(id)!.SessionStarted.Should().BeFalse("新帳戶開新 session");
        s1.Disposed.Should().BeTrue("舊帳戶的程序關掉");
        h.Sessions.Should().HaveCount(2);
        var s2 = h.Sessions[1];
        var sent = s2.Sent.All.Should().ContainSingle().Subject;
        sent.Settings.AccountKey.Should().StartWith(h.Claude2.Id + "|");
        sent.Settings.SessionStarted.Should().BeFalse();
        sent.Input.Text.Should().StartWith("（系統說明：原本的對話紀錄已無法續接", "前文節錄在前面");
        sent.Input.Text.Should().Contain("【使用者】把建置修好");
        sent.Input.Text.Should().Contain("（系統通知：原本的帳戶訂閱額度用完");
        sent.Input.Text.Should().Contain("〈執行指令：dotnet build〉");
        h.IsRunning(id).Should().BeTrue("接續的一輪在執行");

        var items = h.Items(id);
        items.Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "接續訊息不顯示成使用者訊息");
        items[^2].Kind.Should().Be(AgentItemKind.Error);
        var notice = items[^1];
        notice.Kind.Should().Be(AgentItemKind.Notice);
        notice.NoticeKind.Should().Be(AgentNoticeKind.AccountSwitch);
        notice.Text.Should().StartWith("「Claude 主帳戶」的額度已用完").And.Contain("已自動改用「Claude 副帳戶」（1 小時 30 分後重置）繼續這個對話");
    }

    [Fact]
    public void 排隊的訊息不暫停_接續的一輪正常結束後照常送()
    {
        using var h = NewHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.Send(h.Main, id, "排隊B");
        h.Queue(id).Should().Equal("排隊B");

        HitLimit(h, s1);

        h.Items(id).Should().NotContain(i => i.Text == "排隊的訊息已暫停，按送出繼續");
        h.Queue(id).Should().Equal("排隊B");
        var s2 = h.Sessions[1];
        s2.Sent.Count.Should().Be(1);
        h.Raise(s2, new AgentSessionStartedEvent("sess-2", null, null));   // 新帳戶的 CLI 建立了 session

        h.CompleteTurn(s2);

        s2.Sent.Count.Should().Be(2);
        s2.Sent.All[1].Input.Text.Should().Be("排隊B", "接續之後 session 已開始，不再附節錄");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public void 用完的帳戶不再被選_兩個都用完就停下並提示()
    {
        using var h = NewHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        HitLimit(h, s1, resetsAt: Now.AddHours(2));
        var s2 = h.Sessions[1];
        h.Raise(s2, new AgentSessionStartedEvent("sess-2", null, null));
        h.Send(h.Main, id, "排隊C");

        HitLimit(h, s2, resetsAt: Now.AddHours(1));

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "主帳戶兩小時後才重置：不換回去");
        h.Sessions.Should().HaveCount(2);
        h.IsRunning(id).Should().BeFalse();
        h.Items(id).TakeLast(3).Select(i => i.Text).Should().Equal(
            "You've hit your session limit · resets 3pm (Asia/Taipei)",
            AccountSwitchRules.BuildNoCandidateNotice(AgentProvider.Claude),
            "排隊的訊息已暫停，按送出繼續");
        h.Queue(id).Should().Equal("排隊C");
    }

    [Fact]
    public void 沒有額度的帳戶不選_用量顯示用滿或鎖定()
    {
        using var h = NewHarness();
        SeedUsage(h, h.Claude2.Id, new UsageWindow(UsageRules.SessionKey, "5 小時", 100, Now.AddHours(1), Locked: true));
        var (id, s1) = h.StartConversation(h.Main, h.Claude);

        HitLimit(h, s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Sessions.Should().HaveCount(1);
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildNoCandidateNotice(AgentProvider.Claude));
    }

    [Fact]
    public void 設定關掉時照舊停下_設定送給網頁並寫進設定檔()
    {
        using var h = NewHarness();
        h.OnUi(() => h.Settings.AutoSwitchAccountOnLimit).Should().BeTrue("預設開");
        var mark = h.Main.Mark();
        h.Command(h.Main, "setting", new { key = "autoSwitchAccount", value = false });
        h.OnUi(() => h.Settings.AutoSwitchAccountOnLimit).Should().BeFalse();
        h.Main.Messages("settings", mark).Last()["settings"]!.Value<bool>("autoSwitchAccount").Should().BeFalse();

        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        HitLimit(h, s1);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Sessions.Should().HaveCount(1);
        h.IsRunning(id).Should().BeFalse();
        h.Items(id)[^1].Kind.Should().Be(AgentItemKind.Error, "沒有換帳戶的提示");
    }

    [Fact]
    public void 按了停止_一般的失敗_只有一個同種帳戶時都不換也不提示()
    {
        using var h = NewHarness();

        // 按了停止：這一輪就算因為額度失敗也不換
        var (stopped, s1) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "stop", new { conversationId = stopped });
        HitLimit(h, s1);
        h.Conversation(stopped)!.AccountId.Should().Be(h.Claude.Id);

        // 沒有額度用完的一般失敗
        var (plain, s2) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s2, AgentTurnOutcome.Failed, "Claude 程序意外結束");
        h.Conversation(plain)!.AccountId.Should().Be(h.Claude.Id);

        // Codex 只有一個帳戶：照舊停下、不加「沒有可以接手的帳戶」
        var (codex, sx) = h.StartConversation(h.Main, h.Codex);
        HitLimit(h, sx, AgentProvider.Codex);
        h.Conversation(codex)!.AccountId.Should().Be(h.Codex.Id);
        h.Items(codex)[^1].Kind.Should().Be(AgentItemKind.Error);

        h.Sessions.Should().HaveCount(3, "都沒有開新的會話");
    }

    [Fact]
    public void 閒置時收到的額度用完不留到下一輪()
    {
        using var h = NewHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s1);
        h.Raise(s1, new AgentQuotaExhaustedEvent(AgentProvider.Claude, null));

        h.Send(h.Main, id, "第二則");
        h.CompleteTurn(s1, AgentTurnOutcome.Failed, "別的錯誤");

        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Sessions.Should().HaveCount(1);
    }

    private static void SeedGrok(HubHarness h, out AgentAccount primary, out AgentAccount other)
    {
        primary = new AgentAccount { Id = "acc-grok1", Provider = AgentProvider.Grok, Label = "Grok 主帳戶", Color = "#9CA3AF", ConfigDirectory = h.Temp.Combine("accounts", "acc-grok1", "grok") };
        other = new AgentAccount { Id = "acc-grok2", Provider = AgentProvider.Grok, Label = "Grok 副帳戶", Color = "#6B7280", ConfigDirectory = h.Temp.Combine("accounts", "acc-grok2", "grok") };
        var first = primary;
        var second = other;
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Claude2, h.Codex, first, second }, AccountStatus.Ready));
    }

    [Fact]
    public void Grok額度用完_失敗時換到另一個Grok_隱藏送出接續()
    {
        using var h = NewHarness();
        SeedGrok(h, out var primary, out var other);
        SeedUsage(h, other.Id, new UsageWindow(UsageRules.GrokCreditsKey, "每週", 10, Now.AddHours(2)));
        var (id, s1) = h.StartConversation(h.Main, primary, "把建置修好");

        h.Raise(s1, new AgentQuotaExhaustedEvent(AgentProvider.Grok, Now.AddHours(3)));
        h.CompleteTurn(s1, AgentTurnOutcome.Failed, "額度用完");

        h.Conversation(id)!.AccountId.Should().Be(other.Id);
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Input.Text.Should().Contain("系統通知：原本的帳戶訂閱額度用完");
        h.Items(id)[^1].Text.Should().Contain("已自動改用「Grok 副帳戶」（2 小時 0 分後重置）");
    }

    [Fact]
    public void Grok額度用完_正常結束也換_沒查過用量的帳戶也能接手()
    {
        using var h = NewHarness();
        SeedGrok(h, out var primary, out var other);
        var (id, s1) = h.StartConversation(h.Main, primary, "第一則");

        h.Raise(s1, new AgentQuotaExhaustedEvent(AgentProvider.Grok, Now.AddHours(3)));
        h.CompleteTurn(s1);

        h.Conversation(id)!.AccountId.Should().Be(other.Id, "沒查過用量不算沒額度");
        h.Sessions[1].Sent.All.Should().ContainSingle().Which.Input.Text.Should().Contain("系統通知：原本的帳戶訂閱額度用完");
    }

    [Fact]
    public void 開啟跨代理備援_Antigravity單一帳戶額度用完時自動換到還有額度的Claude或Codex接手()
    {
        using var h = NewHarness();
        var agy = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity", Color = "#4285F4" };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Codex, agy }, AccountStatus.Ready));
        SeedUsage(h, h.Claude.Id, new UsageWindow(UsageRules.SessionKey, "5 小時", 100, Now.AddHours(3), Locked: true));
        SeedUsage(h, h.Codex.Id, new UsageWindow("codex:300", "5 小時", 20, Now.AddMinutes(45)));

        h.OnUi(() => h.Settings.AutoSwitchCrossProviderOnLimit).Should().BeFalse("預設關，保護同家隔離語意");
        h.Command(h.Main, "setting", new { key = "autoSwitchCrossProvider", value = true });
        h.OnUi(() => h.Settings.AutoSwitchCrossProviderOnLimit).Should().BeTrue();

        var (id, s1) = h.StartConversation(h.Main, agy, "整理專案架構");
        HitLimit(h, s1, AgentProvider.Antigravity, Now.AddHours(2));

        var meta = h.Conversation(id)!;
        (meta.AccountId, meta.Provider, meta.SessionStarted).Should().Be((h.Codex.Id, AgentProvider.Codex, false));
        var sent = h.Sessions[1].Sent.All.Should().ContainSingle().Subject;
        sent.Input.Text.Should().Contain("原本的 Antigravity 帳戶訂閱額度用完").And.Contain("自動改用 Codex 帳戶接續");
        h.Items(id)[^1].Text.Should().Contain("「Antigravity」的額度已用完").And.Contain("已自動改用「Codex 帳戶」（45 分後重置）");
    }
}
