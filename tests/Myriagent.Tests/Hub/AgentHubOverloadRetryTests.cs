using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>
/// API 伺服器暫時無法服務時自動重試（AgentHub.OverloadRetry.cs）：一輪因為 Claude 的 529 Overloaded 或 Antigravity 的 503 UNAVAILABLE 失敗後，錯誤之後加「多久後自動重試」的提示，
/// 時間到了隱藏送出接續訊息；等待期間使用者送出就改送使用者的、按了停止或其他錯誤不重試、連續到上限就停下照一般的失敗通知
/// </summary>
public class AgentHubOverloadRetryTests
{
    private const string OverloadedText = "API Error: 529 Overloaded. This is a server-side issue, usually temporary — try again in a moment. If it persists, check https://status.claude.com.";

    private static HubHarness NewHarness(int delayMs)
    {
        var h = new HubHarness();
        h.Hub.OverloadRetryDelay = TimeSpan.FromMilliseconds(delayMs);
        return h;
    }

    private static void Overloaded(HubHarness h, FakeSession s)
        => h.Raise(s, new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, OverloadedText, null) { ServerBusy = AgentServerBusy.Overloaded });

    /// <summary>等一段比重試時間長的時間，讓排定的重試（如果有）有機會送出</summary>
    private static async Task WaitPastRetryAsync(HubHarness h, int delayMs)
    {
        await Task.Delay(delayMs * 3 + 100);
        await h.Ui.DrainAsync();
    }

    [Fact]
    public async Task 過載而失敗_錯誤之後提示多久後重試_時間到隱藏送出接續訊息()
    {
        using var h = NewHarness(500);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "把建置修好");
        int attentionBefore = h.Host.AttentionRequests;

        Overloaded(h, s);

        var items = h.Items(id);
        items[^2].Kind.Should().Be(AgentItemKind.Error);
        items[^2].Text.Should().Be(OverloadedText, "原本的錯誤照常顯示");
        items[^1].Kind.Should().Be(AgentItemKind.Notice);
        items[^1].Text.Should().Be(OverloadRetryRules.ScheduledNotice(AgentServerBusy.Overloaded, 1, TimeSpan.FromMilliseconds(500)));
        items[^1].NoticeKind.Should().Be(AgentNoticeKind.OverloadRetry, "網頁以這個種類畫倒數條（從這則的時間起算）");
        h.Host.Spoken.Count.Should().Be(0, "會自動重試：不念失敗");
        h.Host.AttentionRequests.Should().Be(attentionBefore, "會自動重試：不要求注意");

        await h.WaitUntilAsync(() => s.Sent.Count == 2, "時間到了送出接續訊息");
        s.Sent.All[1].Input.Text.Should().Be(OverloadRetryRules.Continuation(AgentServerBusy.Overloaded, 1, TimeSpan.FromMilliseconds(500)));
        h.IsRunning(id).Should().BeTrue("重試的一輪在執行");
        var after = h.Items(id);
        after.Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "接續訊息不顯示成使用者訊息");
        after[^1].Kind.Should().Be(AgentItemKind.Notice);
        after[^1].Text.Should().Be(OverloadRetryRules.RetryNotice(AgentServerBusy.Overloaded, 1));
        after[^1].NoticeKind.Should().BeNull("一般提示");
    }

    [Fact]
    public async Task 等待期間使用者送出_改送使用者的訊息_不再重試()
    {
        using var h = NewHarness(300);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        Overloaded(h, s);

        h.Send(h.Main, id, "先改做別的");
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "使用者的訊息送出");
        s.Sent.All[1].Input.Text.Should().Be("先改做別的");

        await WaitPastRetryAsync(h, 300);
        s.Sent.Count.Should().Be(2, "已經開始了使用者的那一輪：排定的重試取消");
    }

    [Fact]
    public async Task 等待期間CLI自己開了一輪_不再重試()
    {
        using var h = NewHarness(300);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        Overloaded(h, s);

        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        h.IsRunning(id).Should().BeTrue();

        await WaitPastRetryAsync(h, 300);
        s.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task 其他錯誤_或按了停止_不重試()
    {
        using var h = NewHarness(30);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "API Error: 500 Internal server error.");
        h.Items(id)[^1].Kind.Should().Be(AgentItemKind.Error, "一般的失敗：沒有重試的提示");

        h.Send(h.Main, id, "再試一次");
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "使用者再送一次");
        h.Command(h.Main, "stop", new { conversationId = id });
        Overloaded(h, s);
        h.Items(id).Should().NotContain(i => i.Text == OverloadRetryRules.ScheduledNotice(AgentServerBusy.Overloaded, 1, TimeSpan.FromMilliseconds(30)));

        await WaitPastRetryAsync(h, 30);
        s.Sent.Count.Should().Be(2);
        h.IsRunning(id).Should().BeFalse();
    }

    [Fact]
    public async Task 排隊的訊息不暫停_重試的一輪正常結束後照常送()
    {
        using var h = NewHarness(50);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Send(h.Main, id, "排隊B");
        h.Queue(id).Should().Equal("排隊B");

        Overloaded(h, s);
        h.Items(id).Should().NotContain(i => i.Text == "排隊的訊息已暫停，按送出繼續");

        await h.WaitUntilAsync(() => s.Sent.Count == 2, "重試送出");
        s.Sent.All[1].Input.Text.Should().StartWith("（系統通知：上一輪呼叫 API 時伺服器過載");
        h.Queue(id).Should().Equal("排隊B");

        h.CompleteTurn(s);
        await h.WaitUntilAsync(() => s.Sent.Count == 3, "重試的一輪正常結束後送排隊的訊息");
        s.Sent.All[2].Input.Text.Should().Be("排隊B");
    }

    [Fact]
    public async Task 連續過載到上限_不再重試_照一般的失敗通知()
    {
        using var h = NewHarness(10);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        for (int attempt = 1; attempt <= OverloadRetryRules.MaxAttempts; attempt++)
        {
            Overloaded(h, s);
            await h.WaitUntilAsync(() => s.Sent.Count == attempt + 1, $"第 {attempt} 次重試送出");
            h.Items(id).Should().Contain(i => i.Text == OverloadRetryRules.ScheduledNotice(AgentServerBusy.Overloaded, attempt, TimeSpan.FromMilliseconds(10)));
        }

        Overloaded(h, s);

        h.Items(id)[^1].Text.Should().Be(OverloadRetryRules.GaveUpNotice(AgentServerBusy.Overloaded));
        h.Host.Spoken.Count.Should().Be(1, "不再重試：照一般的失敗念出來");
        await WaitPastRetryAsync(h, 10);
        s.Sent.Count.Should().Be(OverloadRetryRules.MaxAttempts + 1);
        h.IsRunning(id).Should().BeFalse();

        // 使用者自己再送：重新算次數
        h.Send(h.Main, id, "再試試");
        await h.WaitUntilAsync(() => s.Sent.Count == OverloadRetryRules.MaxAttempts + 2, "使用者的訊息送出");
        Overloaded(h, s);
        h.Items(id).Count(i => i.Text == OverloadRetryRules.ScheduledNotice(AgentServerBusy.Overloaded, 1, TimeSpan.FromMilliseconds(10))).Should().Be(2, "又從第 1 次算起");
    }

    [Fact]
    public async Task 重試成功之後再過載_從第一次算起()
    {
        using var h = NewHarness(10);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        Overloaded(h, s);
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "第一次重試送出");
        h.CompleteTurn(s);

        h.Send(h.Main, id, "下一步");
        await h.WaitUntilAsync(() => s.Sent.Count == 3, "下一則送出");
        Overloaded(h, s);
        h.Items(id).Count(i => i.Text == OverloadRetryRules.ScheduledNotice(AgentServerBusy.Overloaded, 1, TimeSpan.FromMilliseconds(10))).Should().Be(2, "又從第 1 次算起");
    }

    [Fact]
    public async Task 時間到了CLI正在更新_再等一次_不算次數()
    {
        using var h = NewHarness(50);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Claude, state => state with { InstallChanging = true }));
        Overloaded(h, s);

        await WaitPastRetryAsync(h, 50);
        s.Sent.Count.Should().Be(1, "CLI 更新中隱藏送出不排隊：這時送一定失敗");
        h.IsRunning(id).Should().BeFalse();

        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Claude, state => state with { InstallChanging = false }));
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "更新完成後的下一次照常送出");
        s.Sent.All[1].Input.Text.Should().Be(OverloadRetryRules.Continuation(AgentServerBusy.Overloaded, 1, TimeSpan.FromMilliseconds(50)), "還是第 1 次");
    }

    [Fact]
    public async Task 等待期間刪除對話_不送()
    {
        using var h = NewHarness(200);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        Overloaded(h, s);

        h.Command(h.Main, "delete", new { conversationId = id });
        await h.WaitUntilAsync(() => h.Hub.GetConversation(id) == null, "對話已刪除");

        await WaitPastRetryAsync(h, 200);
        s.Sent.Count.Should().Be(1);
    }

    private const string UnavailableText = "API error (attempt 1): UNAVAILABLE (code 503): The service is currently unavailable.";

    private static void Unavailable(HubHarness h, FakeSession s)
        => h.Raise(s, new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, UnavailableText, null) { ServerBusy = AgentServerBusy.Unavailable });

    [Fact]
    public async Task Antigravity暫時無法服務_60秒後重試_重試三次都失敗就停下照一般的失敗通知()
    {
        using var h = NewHarness(10);
        var agy = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity", Color = "#4285F4" };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Codex, agy }, AccountStatus.Ready));
        var (id, s) = h.StartConversation(h.Main, agy, "審查整合功能");

        Unavailable(h, s);
        var items = h.Items(id);
        items[^2].Kind.Should().Be(AgentItemKind.Error);
        items[^2].Text.Should().Be(UnavailableText, "原本的錯誤照常顯示");
        items[^1].Text.Should().Be(OverloadRetryRules.ScheduledNotice(AgentServerBusy.Unavailable, 1, TimeSpan.FromMilliseconds(10)));
        items[^1].Text.Should().Contain("最多 3 次");
        items[^1].NoticeKind.Should().Be(AgentNoticeKind.OverloadRetry, "網頁同樣畫倒數條");
        h.Host.Spoken.Count.Should().Be(0, "會自動重試：不念失敗");

        await h.WaitUntilAsync(() => s.Sent.Count == 2, "第 1 次重試送出");
        s.Sent.All[1].Input.Text.Should().Be(OverloadRetryRules.Continuation(AgentServerBusy.Unavailable, 1, TimeSpan.FromMilliseconds(10)));
        h.Items(id)[^1].Text.Should().Be(OverloadRetryRules.RetryNotice(AgentServerBusy.Unavailable, 1));
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "接續訊息不顯示成使用者訊息");

        for (int attempt = 2; attempt <= 3; attempt++)
        {
            Unavailable(h, s);
            await h.WaitUntilAsync(() => s.Sent.Count == attempt + 1, $"第 {attempt} 次重試送出");
        }

        // 第 3 次重試也失敗：不再重試
        Unavailable(h, s);
        h.Items(id)[^1].Text.Should().Be(OverloadRetryRules.GaveUpNotice(AgentServerBusy.Unavailable));
        h.Host.Spoken.Count.Should().Be(1, "不再重試：照一般的失敗念出來");
        await WaitPastRetryAsync(h, 10);
        s.Sent.Count.Should().Be(4, "第一次的一輪＋重試三次");
        h.IsRunning(id).Should().BeFalse();
    }

    [Fact]
    public async Task Antigravity網路連線中斷_預設10秒倒數提示_覆蓋延遲後自動重試最多三次()
    {
        // 先驗證未覆蓋 OverloadRetryDelay 時，NetworkError 取用 10 秒（503 仍為 60 秒）
        using (var defaultHarness = new HubHarness())
        {
            defaultHarness.Hub.GetOverloadRetryDelay(AgentServerBusy.NetworkError).Should().Be(TimeSpan.FromSeconds(10));
            defaultHarness.Hub.GetOverloadRetryDelay(AgentServerBusy.Unavailable).Should().Be(TimeSpan.FromSeconds(60));
            var agyDefault = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity", Color = "#4285F4" };
            defaultHarness.Ui.Invoke(() => defaultHarness.Accounts.Seed(new[] { defaultHarness.Claude, defaultHarness.Codex, agyDefault }, AccountStatus.Ready));
            var (defaultId, defaultSession) = defaultHarness.StartConversation(defaultHarness.Main, agyDefault, "分析問題");
            const string WsError = """API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": write tcp 192.168.0.10:55012->172.217.112.4:443: wsasend: An existing connection was forcibly closed by the remote host.""";
            defaultHarness.Raise(defaultSession, new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, WsError, null) { ServerBusy = AgentServerBusy.NetworkError });
            defaultHarness.Items(defaultId)[^1].Text.Should().Contain("10 秒後自動重試（第 1 次，最多 3 次）");
            defaultHarness.Items(defaultId)[^1].NoticeKind.Should().Be(AgentNoticeKind.OverloadRetry);
        }

        // 再以短延遲驗證完整自動重試與 3 次上限流程
        using var h = NewHarness(10);
        var agy = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity", Color = "#4285F4" };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Codex, agy }, AccountStatus.Ready));
        var (id, s) = h.StartConversation(h.Main, agy, "繼續實作");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            h.Raise(s, new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, "API error (attempt 2): request failed: EOF", null) { ServerBusy = AgentServerBusy.NetworkError });
            await h.WaitUntilAsync(() => s.Sent.Count == attempt + 1, $"第 {attempt} 次網路斷線重試送出");
        }

        h.Raise(s, new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, "API error (attempt 2): request failed: EOF", null) { ServerBusy = AgentServerBusy.NetworkError });
        h.Items(id)[^1].Text.Should().Be(OverloadRetryRules.GaveUpNotice(AgentServerBusy.NetworkError));
        h.Host.Spoken.Count.Should().Be(1);
    }
}
