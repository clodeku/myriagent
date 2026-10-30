using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 額度全用完時等重置後自動接著做（AgentHub.QuotaWait.cs，設定 autoResumeAfterQuota 預設開）：沒有帳戶可以接手時記下等待、標頭帶 quotaWait，
/// 維護在時間到了以同一個帳戶隱藏送出接續訊息；等待期間別的帳戶恢復就換過去；使用者送出、取消、設定關掉、重置太久都不等
/// </summary>
public class AgentHubQuotaWaitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Reset = Now.AddHours(3);

    private sealed class Clock
    {
        public DateTimeOffset Value = Now;
    }

    /// <summary>副帳戶的 5 小時額度鎖定到 5 小時後（沒有帳戶可以接手）</summary>
    private static HubHarness NewHarness(out Clock clock)
    {
        var h = new HubHarness();
        var c = new Clock();
        h.Hub.AccountSwitchClock = () => c.Value;
        clock = c;
        SeedUsage(h, h.Claude2.Id, new UsageWindow(UsageRules.SessionKey, "5 小時", 100, Now.AddHours(5), Locked: true));
        return h;
    }

    private static void SeedUsage(HubHarness h, string accountId, params UsageWindow[] windows)
        => h.Ui.Invoke(() => h.Accounts.SeedUsage(accountId, new UsageSnapshot(Now, "api", windows, null, null, null, false)));

    private static void Maintain(HubHarness h) => h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now));

    private static void HitLimit(HubHarness h, FakeSession s, DateTimeOffset? resetsAt = null)
    {
        h.Raise(s, new AgentQuotaExhaustedEvent(AgentProvider.Claude, resetsAt ?? Reset));
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "You've hit your session limit · resets 3pm (Asia/Taipei)");
    }

    /// <summary>額度用完會立即重查用量（測試不連網會標成錯誤）：等查詢結束、把狀態放回已登入</summary>
    private static void SettleRefresh(HubHarness h)
    {
        foreach (var account in h.OnUi(() => h.Accounts.Accounts.ToList()))
        {
            h.OnUi(() => h.Accounts.RefreshSettledForTest(account.Id)).Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("用量重查要結束");
            h.Ui.Invoke(() => h.Accounts.GetState(account.Id).Status = AccountStatus.Ready);
        }
    }

    [Fact]
    public async Task 沒有帳戶可以接手_記下等待與提示_時間到了同一個帳戶接著做()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        h.Raise(s1, new AgentToolStartedEvent("t1", "執行指令", "bash", "dotnet build", "dotnet build"));
        h.Raise(s1, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "失敗", Array.Empty<string>()));
        var mark = h.Main.Mark();

        HitLimit(h, s1);
        SettleRefresh(h);

        var wait = h.Conversation(id)!.QuotaWait;
        wait.Should().Be(new QuotaWait(h.Claude.Id, Reset + QuotaWaitRules.Grace));
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        h.Sessions.Should().ContainSingle("沒有換帳戶");
        var notice = h.Items(id)[^1];
        notice.Kind.Should().Be(AgentItemKind.Notice);
        notice.Text.Should().StartWith("額度用完了，也沒有能接手的帳戶：等「Claude 主帳戶」在");
        h.Items(id).Should().NotContain(i => i.Text == AccountSwitchRules.BuildNoCandidateNotice(AgentProvider.Claude), "等待的提示已經說了沒有帳戶可以接手");
        var header = await h.Main.WaitForAsync("header", m => m["header"]?["quotaWait"] is JObject, mark, conversationId: id);
        header["header"]!["quotaWait"]!.Value<long>("until").Should().Be((Reset + QuotaWaitRules.Grace).ToUnixTimeMilliseconds());
        header["header"]!["quotaWait"]!.Value<string>("account").Should().Be("Claude 主帳戶");

        clock.Value = Reset;
        Maintain(h);
        s1.Sent.Count.Should().Be(1, "還沒到時間");

        clock.Value = Reset + QuotaWaitRules.Grace + TimeSpan.FromSeconds(1);
        Maintain(h);

        h.Conversation(id)!.QuotaWait.Should().BeNull();
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id);
        var sent = h.Sessions[^1].Sent.All[^1];
        sent.Input.Text.Should().Contain("額度已重置").And.Contain("〈執行指令：dotnet build〉");
        h.IsRunning(id).Should().BeTrue();
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "接續訊息不顯示成使用者訊息");
        h.Items(id).Should().Contain(i => i.Text == QuotaWaitRules.ResumedNotice("Claude 主帳戶"));
    }

    [Fact]
    public void 等待期間別的帳戶恢復_換過去接著做()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        HitLimit(h, s1);
        SettleRefresh(h);
        h.Conversation(id)!.QuotaWait.Should().NotBeNull();

        SeedUsage(h, h.Claude2.Id, new UsageWindow(UsageRules.SessionKey, "5 小時", 20, Now.AddHours(4)));
        clock.Value = Now.AddMinutes(30);
        Maintain(h);

        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Conversation(id)!.QuotaWait.Should().BeNull();
        h.Sessions.Should().HaveCount(2);
        h.Sessions[1].Sent.All.Should().ContainSingle().Which.Input.Text.Should().Contain("（系統通知：原本的帳戶訂閱額度用完");
    }

    [Fact]
    public void 使用者自己送出_不再等()
    {
        using var h = NewHarness(out _);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        HitLimit(h, s1);
        SettleRefresh(h);
        h.Conversation(id)!.QuotaWait.Should().NotBeNull();

        h.Send(h.Main, id, "先做別的");

        h.Conversation(id)!.QuotaWait.Should().BeNull();
    }

    [Fact]
    public void 取消等待_加提示_時間到了也不接著做()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        HitLimit(h, s1);
        SettleRefresh(h);

        h.Command(h.Main, "quotaWaitCancel", new { conversationId = id });

        h.Conversation(id)!.QuotaWait.Should().BeNull();
        h.Items(id)[^1].Text.Should().Be(QuotaWaitRules.CancelledNotice);
        clock.Value = Reset.AddHours(1);
        Maintain(h);
        s1.Sent.Count.Should().Be(1);
        h.IsRunning(id).Should().BeFalse();
    }

    [Fact]
    public void 設定關掉_不等_照舊提示()
    {
        using var h = NewHarness(out _);
        h.Command(h.Main, "setting", new { key = "autoResumeAfterQuota", value = false });
        h.OnUi(() => h.Settings.AutoResumeAfterQuotaReset).Should().BeFalse();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);

        HitLimit(h, s1);

        h.Conversation(id)!.QuotaWait.Should().BeNull();
        h.Items(id)[^1].Text.Should().Be(AccountSwitchRules.BuildNoCandidateNotice(AgentProvider.Claude));
    }

    [Fact]
    public void 重置超過12小時_不等()
    {
        using var h = NewHarness(out _);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);

        HitLimit(h, s1, resetsAt: Now.AddDays(3));

        h.Conversation(id)!.QuotaWait.Should().BeNull();
    }

    [Fact]
    public void 等待中設定被關掉_維護清掉等待()
    {
        using var h = NewHarness(out var clock);
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        HitLimit(h, s1);
        SettleRefresh(h);

        h.OnUi(() => h.Settings.AutoResumeAfterQuotaReset = false);
        clock.Value = Reset.AddHours(1);
        Maintain(h);

        h.Conversation(id)!.QuotaWait.Should().BeNull();
        s1.Sent.Count.Should().Be(1);
    }
}
