using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// CLI 熱更新的中樞重開協調（設計 7.4、7.7、7.8；以 FakeSession 模擬 RestartAsync 的各種結果）：
/// 閒置立即關、安全點重開並接續、工具執行中與等你回應的等待、立即套用、不自動接續的情況、分批上限、更新屏障、卡片作廢、佇列不暫停
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRestartTests
{
    private static readonly CliBinaryIdentity ClaudeOld = CliIdentities.Claude("2.1.295", fileId: 1);
    private static readonly CliBinaryIdentity ClaudeNew = CliIdentities.Claude("2.1.297", fileId: 2);
    private static readonly CliBinaryIdentity CodexOld = CliIdentities.Codex("0.160.1", fileId: 1);
    private static readonly CliBinaryIdentity CodexNew = CliIdentities.Codex("0.162.0", fileId: 2);

    /// <summary>中樞＋啟動時第一次讀到的 CLI 身分（Old 為 null，不算換版）；背景工作結束後不等安靜期（另有測試）</summary>
    private static async Task<HubHarness> NewHarnessAsync()
    {
        var h = new HubHarness();
        await h.SwitchCliAsync(AgentProvider.Claude, null, ClaudeOld);
        await h.SwitchCliAsync(AgentProvider.Codex, null, CodexOld);
        h.Ui.Invoke(() => h.Hub.BackgroundSettle = TimeSpan.Zero);
        return h;
    }

    /// <summary>開新對話送出第一則（執行中），並模擬會話以這個身分啟動程序</summary>
    private static (string Id, FakeSession Session) StartRunning(HubHarness h, AgentAccount account, string instance = "p1", CliBinaryIdentity? cli = null)
    {
        var (id, s) = h.StartConversation(h.Main, account, "做事");
        h.ProcessStarted(s, instance, cli ?? (account.Provider == AgentProvider.Claude ? ClaudeOld : CodexOld));
        return (id, s);
    }

    private static JToken HeaderRestart(HubHarness h, string id) => h.OnUi(() => h.Hub.RenderHeaderRestartForTest(id));

    private static string? State(HubHarness h, string id) => HeaderRestart(h, id) is JObject o ? o.Value<string>("state") : null;

    private static JObject CliView(HubHarness h, AgentProvider provider) => h.OnUi(() => h.Hub.RenderCliViewForTest(provider));

    private static bool IsNull(JToken? token) => token == null || token.Type == JTokenType.Null || token is JValue { Value: null };

    private static IEnumerable<AgentChatItem> CliNotices(HubHarness h, string id) => h.Items(id).Where(i => i.NoticeKind == AgentNoticeKind.CliUpdate);

    private static void SetClock(HubHarness h, Func<DateTime> clock) => h.Ui.Invoke(() => h.Hub.RestartClock = clock);

    /// <summary>更新結束（同 CliManager：有換版時先宣告新身分〔VersionChanged〕，再呼叫 UpdateFinished），等中樞處理完</summary>
    private static async Task FinishUpdateAsync(HubHarness h, AgentProvider provider, CliUpdateOutcome outcome, CliBinaryIdentity? identity, string? error = null)
    {
        if (outcome == CliUpdateOutcome.Updated && identity != null)
            await h.SwitchCliAsync(provider, h.Cli.GetIdentity(provider), identity, CliChangeOrigin.App);
        h.Cli.UpdateParticipant!.UpdateFinished(new CliUpdateCompletion(provider, outcome, identity, error));
        await h.Ui.DrainAsync();
    }

    // ───── 一般換版（設計 7.4.1 的 SafePoint 欄）─────

    [Fact]
    public async Task 閒置的對話換版_立即關程序_不送接續_不出現換版狀態_下次送出用新版()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude);
        h.CompleteTurn(s);
        s.Sent.All[0].Settings.Cli.Should().Be(ClaudeOld, "送出時帶目前安裝的身分（會話據此判斷要不要重開程序）");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        s.Closes.Should().Be(1, "閒置的立即關程序（保留會話物件，下次送出自然以新版續接）");
        s.Restarts.Count.Should().Be(0, "上一輪已完成：不重開、不送接續（S3：0 則接續訊息）");
        CliNotices(h, id).Should().BeEmpty();
        IsNull(HeaderRestart(h, id)).Should().BeTrue("閒置的對話換版不出現任何 restart 狀態");
        var cli = CliView(h, AgentProvider.Claude);
        cli.Value<int>("runningOnOldVersion").Should().Be(0);
        cli.Value<int>("restartPending").Should().Be(0);
        cli["restartDone"]!.Value<int>("restarted").Should().Be(1, "restarted＝閒置的只關程序");
        cli["lastChange"]!.Value<string>("from").Should().Be("2.1.295");
        cli["lastChange"]!.Value<string>("to").Should().Be("2.1.297");
        cli["lastChange"]!.Value<string>("origin").Should().Be("external");
        h.Host.Notifications.All.Where(n => n.ConversationId == null).Should().ContainSingle("一波換版處理完送一則系統匣彙總")
            .Which.Title.Should().Be("Claude Code 已更新到 2.1.297");

        h.Send(h.Main, id, "下一則");
        s.Sent.All[^1].Settings.Cli.Should().Be(ClaudeNew, "下次送出帶新身分");
    }

    [Fact]
    public async Task 系統匣彙總_一波都處理完才送一則_通知關掉時不送()
    {
        using var h = await NewHarnessAsync();
        var (_, idle) = StartRunning(h, h.Claude, "a1");
        h.CompleteTurn(idle);
        var (_, busy) = StartRunning(h, h.Claude, "b1");
        busy.SucceedRestarts();
        h.Raise(busy, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        h.Raise(busy, new AgentSafePointChangedEvent(ToolRunning: true, false, false));

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        idle.Closes.Should().Be(1);
        h.Host.Notifications.All.Where(n => n.ConversationId == null).Should().BeEmpty("還有對話在等工具結束：這一波還沒處理完");

        h.Raise(busy, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        busy.SafePoint = true;
        h.Raise(busy, new AgentSafePointChangedEvent(false, false, false));

        var summary = h.Host.Notifications.All.Where(n => n.ConversationId == null).Should().ContainSingle("一波換版處理完送一則彙總（不逐一跳）").Subject;
        summary.Title.Should().Be("Claude Code 已更新到 2.1.297");
        summary.Message.Should().Be("2 個對話已處理：1 個已自動接續、1 個閒置的已關閉（下次送出用新版）");

        // 通知關掉（settings.Notifications=false）：下一波照樣換版，但不送彙總
        h.Command(h.Main, "setting", new { key = "notifications", value = false });
        var (_, other) = StartRunning(h, h.Claude, "c1", ClaudeNew);
        h.CompleteTurn(other);
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeNew, CliIdentities.Claude("2.1.298", fileId: 3));
        other.Closes.Should().Be(1, "照樣換版");
        h.Host.Notifications.All.Count(n => n.ConversationId == null).Should().Be(1, "通知關掉時不送彙總");
    }

    [Fact]
    public async Task 執行中在安全點_以新版重開並自動接續_佇列不提前送_這一輪結束後清掉狀態()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SafePoint = true;
        s.SucceedRestarts();
        h.Send(h.Main, id, "排隊的下一則");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.Reason.Should().Be(AgentRestartReason.CliUpdated);
        request.Mode.Should().Be(AgentRestartMode.SafePoint, "會話在鎖內重新確認安全點");
        request.AutoContinue.Should().BeTrue();
        request.FromVersion.Should().Be("2.1.295");
        request.ToVersion.Should().Be("2.1.297");
        request.InterruptedTools.Should().BeEmpty("安全點重開沒有被打斷的工具");
        request.Settings!.Cli.Should().Be(ClaudeNew, "新程序以新身分啟動");
        request.Settings.SessionStarted.Should().BeTrue("以 --resume 接續原本的 session");
        request.ContinuationText.Should().Contain("由 2.1.295 更新為 2.1.297").And.Contain("沒有任何工具在執行").And.Contain("不是使用者拒絕");

        h.IsRunning(id).Should().BeTrue("重開期間與接續的一輪都是同一輪");
        CliNotices(h, id).Should().ContainSingle().Which.Text.Should().Be("已用 Claude Code 2.1.297 自動接續（重啟時沒有工具在執行）");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("continued");
        header.Value<string>("from").Should().Be("2.1.295");
        header.Value<string>("to").Should().Be("2.1.297");
        header.Value<bool>("canApplyNow").Should().BeFalse();
        h.Queue(id).Should().Equal(new[] { "排隊的下一則" }, "佇列照常排隊：接續的一輪結束後才送");
        CliView(h, AgentProvider.Claude)["restartDone"]!.Value<int>("continued").Should().Be(1);

        h.CompleteTurn(s);

        IsNull(HeaderRestart(h, id)).Should().BeTrue("這一輪結束：換版狀態清掉");
        s.Sent.All[^1].Input.Text.Should().Be("排隊的下一則", "接續的一輪正常結束後佇列照常接著送");
        s.Sent.All[^1].Settings.Cli.Should().Be(ClaudeNew);
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Notice && i.Text.StartsWith("已停止"), "不是使用者停止");
    }

    [Fact]
    public async Task 工具執行中_不打斷_等工具結束後的安全點才重開()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts();
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: false, HasLiveBackgroundWork: false));

        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);

        s.Restarts.Count.Should().Be(0, "工具在執行：舊版照常可用，不冒險打斷");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingTool");
        header.Value<string>("detail").Should().Be("CLI 已更新到 0.162.0，等目前的指令（npm test）完成後切換");
        header.Value<bool>("canApplyNow").Should().BeTrue();
        header.Value<bool>("canContinue").Should().BeFalse();
        var cli = CliView(h, AgentProvider.Codex);
        cli.Value<int>("runningOnOldVersion").Should().Be(1);
        cli.Value<int>("restartPending").Should().Be(1);
        var waiting = cli["restartWaiting"]!.Should().ContainSingle().Subject;
        waiting.Value<string>("conversationId").Should().Be(id);
        waiting.Value<string>("state").Should().Be("waitingTool");
        waiting.Value<string>("detail").Should().Be("npm test");

        // 工具結束了，但會話還沒說到了安全點（例如下一個回覆已開始）：照樣等
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        s.Restarts.Count.Should().Be(0);

        s.SafePoint = true;
        h.Raise(s, new AgentSafePointChangedEvent(false, false, false));

        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.Mode.Should().Be(AgentRestartMode.SafePoint);
        request.Settings!.Cli.Should().Be(CodexNew);
        State(h, id).Should().Be("continued");
        h.Items(id).Single(i => i.Kind == AgentItemKind.Tool).Status.Should().Be(AgentItemStatus.Done, "安全點重開時工具早已完成");
        CliView(h, AgentProvider.Codex).Value<int>("restartPending").Should().Be(0);
    }

    [Fact]
    public async Task 等使用者回應_不打斷_回應後到了安全點才重開()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SucceedRestarts();
        h.Raise(s, new AgentApprovalEvent("r1", "執行指令", "bash", "Set-Content done.txt done", null, "t1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(false, WaitingForUser: true, false));
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        card.ProcessInstance.Should().Be("p1", "卡片寫事件流上的目前實例");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        s.Restarts.Count.Should().Be(0, "等你回應：不打斷（卡片在程序重開後會失效）");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingUser");
        header.Value<string>("detail").Should().Be("CLI 已更新到 2.1.297，等你回應後切換");
        CliView(h, AgentProvider.Claude)["restartWaiting"]![0]!.Value<string>("detail").Should().Be("執行指令：Set-Content done.txt done");

        h.Command(h.Main, "approve", new { conversationId = id, itemId = card.Id, choice = "allow" });
        s.Approvals.All.Should().ContainSingle("同一個程序產生的卡片照常轉送");
        s.Restarts.Count.Should().Be(0, "核准後工具開始執行：還不是安全點");

        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
        s.SafePoint = true;
        h.Raise(s, new AgentSafePointChangedEvent(false, false, false));

        s.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.SafePoint);
        State(h, id).Should().Be("continued");
    }

    [Fact]
    public async Task 立即套用_不等安全點_被打斷的工具與經過時間寫進接續訊息()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SucceedRestarts();
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        var tool = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool);
        tool.CreatedAt = DateTime.Now.AddSeconds(-42.5);
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        State(h, id).Should().Be("waitingTool");

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.Reason.Should().Be(AgentRestartReason.UserApplyNow);
        request.Mode.Should().Be(AgentRestartMode.Immediate);
        request.AutoContinue.Should().BeTrue("npm test 不是不可重複的操作");
        var interrupted = request.InterruptedTools.Should().ContainSingle().Subject;
        interrupted.ToolName.Should().Be("執行指令");
        interrupted.Title.Should().Be("npm test");
        interrupted.Elapsed.Should().BeCloseTo(TimeSpan.FromSeconds(42.5), TimeSpan.FromSeconds(5));
        request.ContinuationText.Should().Contain("重啟時你正在執行〈執行指令：npm test〉").And.Contain("結果未知").And.Contain("請先確認它是否已生效");
        tool.Status.Should().Be(AgentItemStatus.Cancelled, "被打斷的工具標為已停止");
        CliNotices(h, id).Should().ContainSingle().Which.Text.Should().StartWith("已用 Claude Code 2.1.297 自動接續（中斷時正在執行：npm test，已執行 4");
        State(h, id).Should().Be("continued");
    }

    [Fact]
    public async Task 立即套用_Codex停在待核准_接續訊息寫明指令還沒執行_舊卡失效()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "Set-Content e16.txt e16", null, "item-1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: true, HasLiveBackgroundWork: false));
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);
        State(h, id).Should().Be("waitingUser");

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.Mode.Should().Be(AgentRestartMode.Immediate);
        request.AutoContinue.Should().BeTrue();
        request.ContinuationText.Should().Contain("重啟時你正在等使用者回應〈執行指令：Set-Content e16.txt e16〉").And.Contain("還沒有執行").And.Contain("重新提出");
        card.Status.Should().Be(AgentItemStatus.Cancelled, "直接殺的 Codex 不會撤回核准請求：新程序開始時舊卡作廢");
        card.Output.Should().Be(CliRestartRules.StaleCardOutput);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = card.Id, choice = "allow" });
        s.Approvals.Count.Should().Be(0, "request id 每個程序各自從 0 數，舊卡的回覆絕不能送給新程序");
    }

    [Fact]
    public async Task 立即套用_待核准的指令已有工具卡_不算被中斷_高風險也自動接續_工具卡寫沒有執行()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        // 真實的 Codex：item/started commandExecution(inProgress) 與 requestApproval 同時到（同一個 itemId）
        h.Raise(s, new AgentToolStartedEvent("item-1", "執行指令", "bash", "git push origin main", "git push origin main"));
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "git push origin main", null, "item-1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: true, HasLiveBackgroundWork: false));
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.InterruptedTools.Should().BeEmpty("還沒核准的指令沒有執行");
        request.AutoContinue.Should().BeTrue("沒有執行過的 git push 不是被打斷的高風險操作");
        request.ContinuationText.Should().Contain("重啟時你正在等使用者回應〈執行指令：git push origin main〉").And.NotContain("正在執行");
        var tool = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool);
        tool.Status.Should().Be(AgentItemStatus.Cancelled);
        tool.Output.Should().Be(CliRestartRules.NotApprovedToolOutput);
        CliNotices(h, id).Should().ContainSingle().Which.Text.Should().NotContain("中斷時正在執行");
    }

    [Fact]
    public async Task 核准之後才立即套用_工具已經開始執行_照常算被中斷()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        h.Raise(s, new AgentToolStartedEvent("item-1", "執行指令", "bash", "npm test", "npm test"));
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "npm test", null, "item-1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: true, HasLiveBackgroundWork: false));
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = card.Id, choice = "allow" });
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: false, HasLiveBackgroundWork: false));
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        s.Restarts.All.Should().ContainSingle().Which.InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("npm test");
        h.Items(id).Single(i => i.Kind == AgentItemKind.Tool).Output.Should().Contain("結果未知");
    }

    [Fact]
    public async Task 立即套用_CLI卡套用到這種CLI所有還在跑舊版的對話()
    {
        using var h = await NewHarnessAsync();
        h.Settings.CliRestartPolicy = CliRestartPolicy.NextSend;
        var (idleId, idle) = StartRunning(h, h.Codex, "a1");
        h.CompleteTurn(idle);
        var (runId, run) = StartRunning(h, h.Codex, "b1");
        run.SucceedRestarts();
        var (claudeId, claude) = StartRunning(h, h.Claude, "x1");
        h.CompleteTurn(claude);
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);
        idle.Closes.Should().Be(0, "下次送出才換：不主動關");
        CliView(h, AgentProvider.Codex).Value<int>("runningOnOldVersion").Should().Be(2, "CLI 卡在這時顯示「立即套用」");

        h.Command(h.Main, "cliApplyNow", new { provider = "Codex" });

        idle.Closes.Should().Be(1, "閒置的關程序");
        run.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.Immediate);
        claude.Closes.Should().Be(0, "只套用指定的 CLI");
        CliView(h, AgentProvider.Codex).Value<int>("runningOnOldVersion").Should().Be(0);
    }

    // ───── 不自動接續（7.7）─────

    [Fact]
    public async Task 使用者按過停止_重開時不自動接續_照停止的樣子收尾()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SucceedRestarts();
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        h.Command(h.Main, "stop", new { conversationId = id });
        s.Interrupts.Should().Be(1);

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        s.Restarts.All.Should().ContainSingle().Which.AutoContinue.Should().BeFalse("使用者按過停止");
        h.IsRunning(id).Should().BeFalse("沒有接續：這一輪在本機收尾");
        h.Items(id).Last().Text.Should().Be("已停止這一輪");
        CliNotices(h, id).Should().BeEmpty("使用者自己停的，不加換版的說明");
        IsNull(HeaderRestart(h, id)).Should().BeTrue();
    }

    [Fact]
    public async Task 高風險工具被打斷_不自動接續_標頭給繼續_按繼續隱藏送出接續訊息()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SucceedRestarts();
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "git push origin main", "git push origin main"));
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        s.Restarts.All.Should().ContainSingle().Which.AutoContinue.Should().BeFalse("git push 結果未知，不能盲目重做");
        h.IsRunning(id).Should().BeFalse();
        var notice = h.Items(id).Last();
        notice.NoticeKind.Should().Be(AgentNoticeKind.CliUpdate);
        notice.Text.Should().Be("CLI 已更新到 2.1.297；這一輪沒有自動接續，原因：上一個操作〈git push origin main〉結果未知，請確認後按「繼續」或送出新訊息");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();
        header.Value<string>("detail").Should().Contain("〈git push origin main〉");
        CliView(h, AgentProvider.Claude)["restartDone"]!.Value<int>("notContinued").Should().Be(1);

        var users = h.Items(id).Count(i => i.Kind == AgentItemKind.User);
        var sent = s.Sent.Count;
        h.Command(h.Main, "cliContinue", new { conversationId = id });

        s.Sent.Count.Should().Be(sent + 1);
        var (input, settings) = s.Sent.All[^1];
        input.Text.Should().Contain("使用者看過狀況後按了「繼續」").And.Contain("〈執行指令：git push origin main〉").And.Contain("結果未知");
        settings.Cli.Should().Be(ClaudeNew);
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(users, "接續訊息不顯示成使用者訊息");
        var continued = h.Items(id).Last();
        continued.Kind.Should().Be(AgentItemKind.Notice);
        continued.NoticeKind.Should().Be(AgentNoticeKind.CliUpdate);
        continued.Text.Should().Be(CliRestartRules.ManualContinueNotice);
        IsNull(HeaderRestart(h, id)).Should().BeTrue("按了繼續：狀態清掉");
        h.IsRunning(id).Should().BeTrue();

        // 不在可繼續的狀態就忽略
        h.Command(h.Main, "cliContinue", new { conversationId = id });
        s.Sent.Count.Should().Be(sent + 1);
    }

    [Fact]
    public async Task 高風險規則可在設定停用_停用後照常自動接續()
    {
        using var h = await NewHarnessAsync();
        h.Settings.CliRestartRiskDisabled.Add("git-push");
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SucceedRestarts();
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "git push origin main", "git push origin main"));
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        s.Restarts.All.Single().AutoContinue.Should().BeTrue();
        State(h, id).Should().Be("continued");
    }

    [Fact]
    public async Task 中斷超過1小時_更新後不自動接續_標頭給繼續()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        s.SucceedRestarts();
        var participant = h.Cli.UpdateParticipant!;

        var preparation = await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Ready.Should().BeTrue();
        preparation.Held.Should().Be(1);
        s.Restarts.All[0].Settings.Should().BeNull("屏障先停下，等更新完成");

        now = start.AddMinutes(61);
        await FinishUpdateAsync(h, AgentProvider.Codex, CliUpdateOutcome.Updated, CodexNew);

        var resume = s.Restarts.All[1];
        resume.AutoContinue.Should().BeFalse("中斷超過 1 小時不接續");
        resume.Settings!.Cli.Should().Be(CodexNew);
        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Last().Text.Should().Contain("中斷已超過 1 小時");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();
    }

    [Fact]
    public async Task 上一輪失敗而閒置_只關程序不接續()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "已達用量上限");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        s.Closes.Should().Be(1);
        s.Restarts.Count.Should().Be(0, "額度用完而結束的一輪不接續（閒置只關程序）");
        CliNotices(h, id).Should().BeEmpty();
    }

    // ───── RestartAsync 的其他結果 ─────

    [Fact]
    public async Task 會話不支援重開_退回這一輪結束後關程序()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SafePoint = true;   // OnRestart 為 null：回 NotSupported（同熱更新實作前的會話）

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        s.Restarts.Count.Should().Be(1);
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingTurn");
        header.Value<string>("detail").Should().Be("CLI 已更新到 2.1.297，這一輪結束後切換");
        s.Closes.Should().Be(0);

        h.CompleteTurn(s);

        s.Closes.Should().Be(1, "這一輪結束後關程序，下一則訊息用新版");
        s.Restarts.Count.Should().Be(1, "不再重試");
        IsNull(HeaderRestart(h, id)).Should().BeTrue();
    }

    [Fact]
    public async Task 以新版重開失敗_這一輪以提示收尾_佇列暫停_下次送出清掉狀態()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SafePoint = true;
        s.OnRestart = _ => new AgentRestartResult(AgentRestartOutcome.Failed, "p1", null, "找不到 claude.exe");
        h.Send(h.Main, id, "排隊的");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        h.IsRunning(id).Should().BeFalse();
        var notice = h.Items(id).Last();
        notice.NoticeKind.Should().Be(AgentNoticeKind.CliUpdate);
        notice.Text.Should().Be(CliRestartRules.FailedNotice("找不到 claude.exe", queuePaused: true));
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("failed");
        header.Value<string>("detail").Should().Be("以新版 CLI 重新啟動失敗：找不到 claude.exe");
        h.Queue(id).Should().Equal(new[] { "排隊的" }, "佇列暫停：按送出才接著送");
        CliView(h, AgentProvider.Claude)["restartDone"]!.Value<int>("failed").Should().Be(1);

        h.Send(h.Main, id, "");

        s.Sent.All[^1].Input.Text.Should().Be("排隊的");
        IsNull(HeaderRestart(h, id)).Should().BeTrue("下一次送出：failed 清掉");
    }

    [Fact]
    public async Task 會話說不在安全點_等下一個安全點事件再試_不連續重試()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        s.SucceedRestarts();
        var succeed = s.OnRestart!;
        int calls = 0;
        s.OnRestart = request => ++calls == 1 ? new AgentRestartResult(AgentRestartOutcome.Deferred) : succeed(request);

        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);
        await h.Ui.DrainAsync();

        s.Restarts.Count.Should().Be(1, "Deferred 之後不立刻再試");
        State(h, id).Should().Be("waitingTool");

        h.Raise(s, new AgentSafePointChangedEvent(false, false, false));

        s.Restarts.Count.Should().Be(2);
        State(h, id).Should().Be("continued");
    }

    [Fact]
    public async Task 佇列不暫停_重開期間舊程序回報的中斷不算停止_接續的一輪結束後照常送出()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SafePoint = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.RestartGate = gate;
        s.SucceedRestarts();
        h.Send(h.Main, id, "排隊的");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        s.Restarts.Count.Should().Be(1, "RestartAsync 進行中（殺樹、啟動新程序）");
        State(h, id).Should().Be("restarting");
        ((JObject)HeaderRestart(h, id)).Value<string>("detail").Should().Be("正在以 Claude Code 2.1.297 重新啟動…");

        // 舊程序被殺時萬一回報了這一輪中斷：不加「已停止」、佇列不暫停
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.IsRunning(id).Should().BeTrue();
        h.Items(id).Should().NotContain(i => i.Text.StartsWith("已停止"));
        h.Queue(id).Should().Equal("排隊的");

        gate.SetResult();
        await h.Ui.DrainAsync();
        State(h, id).Should().Be("continued");

        h.CompleteTurn(s);
        s.Sent.All[^1].Input.Text.Should().Be("排隊的");
    }

    // ───── 重開期間的送出（整合對齊：Codex 會話在重開中 SendAsync 會丟 AgentRestartInProgressException）─────

    [Fact]
    public async Task 重開進行中這一輪自己先結束_接著送的不呼叫SendAsync_放回佇列最前面_重開做完後以新版照常送出()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.RestartGate = gate;
        // Codex：這一輪在 turn/interrupt 生效前自己完成——會話照常回報結束，RestartAsync 回 Restarted（不接續、沒有新程序）
        s.OnRestart = _ =>
        {
            s.ProcessInstance = null;
            return new AgentRestartResult(AgentRestartOutcome.Restarted, "c1");
        };
        h.Send(h.Main, id, "排隊的第一則");
        h.Send(h.Main, id, "排隊的第二則");
        var sent = s.Sent.Count;

        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);
        s.Restarts.Count.Should().Be(1, "RestartAsync 進行中");

        h.CompleteTurn(s);

        h.IsRunning(id).Should().BeFalse("這一輪照常結束");
        s.Sent.Count.Should().Be(sent, "重開進行中不呼叫 SendAsync");
        h.Queue(id).Should().Equal(new[] { "排隊的第一則", "排隊的第二則" }, "接著送的那則放回佇列最前面");
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Error, "不是送出失敗");

        h.Send(h.Main, id, "重開中又送一則");
        s.Sent.Count.Should().Be(sent);
        h.Queue(id).Should().Equal("排隊的第一則", "排隊的第二則", "重開中又送一則");

        gate.SetResult();
        await h.Ui.DrainAsync();

        s.Sent.Count.Should().Be(sent + 1, "重開有了結果：照常送出");
        s.Sent.All[^1].Input.Text.Should().Be("排隊的第一則");
        s.Sent.All[^1].Settings.Cli.Should().Be(CodexNew, "以新版送出");
        h.Queue(id).Should().Equal("排隊的第二則", "重開中又送一則");
        h.IsRunning(id).Should().BeTrue();
        CliView(h, AgentProvider.Codex)["restartDone"]!.Value<int>("restarted").Should().Be(1, "這一輪自己先結束：算成只關程序");

        h.CompleteTurn(s);
        s.Sent.All[^1].Input.Text.Should().Be("排隊的第二則", "之後照佇列順序接著送");
    }

    [Fact]
    public async Task 接續的一輪在重開結果之前就結束_佇列的訊息等重開有了結果才送_不重複完成()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        h.Send(h.Main, id, "排隊的");
        var sent = s.Sent.Count;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentRestartRequest? pending = null;
        // 會話送出接續訊息（AgentTurnStartedByCliEvent）後還沒回 RestartedAndContinued，接續的一輪就已經做完
        s.OnRestart = request =>
        {
            pending = request;
            return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, "c1", "c2");
        };
        s.RestartGate = finish;

        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);
        h.ProcessStarted(s, "c2", CodexNew);
        h.Raise(s, new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
        h.IsRunning(id).Should().BeTrue();
        h.CompleteTurn(s);

        h.IsRunning(id).Should().BeFalse("接續的一輪照常結束");
        s.Sent.Count.Should().Be(sent, "重開還沒有結果：不呼叫 SendAsync");
        h.Queue(id).Should().Equal("排隊的");

        finish.SetResult();
        await h.Ui.DrainAsync();

        pending.Should().NotBeNull();
        h.IsRunning(id).Should().BeTrue("重開有了結果後才送出排隊的訊息（不是接續的一輪又開始）");
        s.Sent.All[^1].Input.Text.Should().Be("排隊的");
        s.Sent.Count.Should().Be(sent + 1);
        CliNotices(h, id).Should().ContainSingle("已用新版自動接續只提示一次").Which.Text.Should().StartWith("已用 Codex 0.162.0 自動接續");
        CliView(h, AgentProvider.Codex)["restartDone"]!.Value<int>("continued").Should().Be(1);
        h.Items(id).Count(i => i.Kind == AgentItemKind.Error).Should().Be(0);
        h.CompleteTurn(s);
        h.IsRunning(id).Should().BeFalse();
    }

    // ───── 重開結果與這一輪結束的先後（整合對齊）─────

    [Fact]
    public async Task Claude新程序一啟動就發開始事件_CLI沒有接續回Restarted_在本機收尾_不卡在執行中_不提示已自動接續()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SafePoint = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.RestartGate = gate;
        s.OnRestart = _ =>
        {
            s.ProcessInstance = null;
            s.IsRunning = false;
            return new AgentRestartResult(AgentRestartOutcome.Restarted, "p1");
        };
        h.Send(h.Main, id, "排隊的");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        // 會話卸下舊程序、以新版 --resume 加 RESUME 環境變數啟動：一啟動就發 AgentTurnStartedByCliEvent（CLI 這時還不一定會接續）
        h.ProcessStarted(s, "p2", ClaudeNew);
        h.Raise(s, new AgentTurnStartedByCliEvent(AgentAutoTurnKind.ResumeInterruptedTurn));
        h.IsRunning(id).Should().BeTrue();
        State(h, id).Should().Be("restarting", "還沒確定 CLI 會接續");
        CliNotices(h, id).Should().BeEmpty("「已用新版自動接續」等 RestartAsync 回 RestartedAndContinued 才加");

        // initialize 回應後 5 秒都沒有開始接續（CLI 認為這一輪已回覆完）：會話關掉新程序、回 Restarted
        gate.SetResult();
        await h.Ui.DrainAsync();

        h.IsRunning(id).Should().BeFalse("會話不會再回報這一輪的結束：在本機收尾，不能一直顯示執行中");
        CliNotices(h, id).Should().ContainSingle().Which.Text.Should().StartWith("CLI 已更新到 2.1.297；這一輪沒有自動接續");
        State(h, id).Should().Be("notContinued");
        ((JObject)HeaderRestart(h, id)).Value<bool>("canContinue").Should().BeTrue();
        h.Queue(id).Should().Equal(new[] { "排隊的" }, "佇列暫停：按送出或「繼續」才接著送");
        CliView(h, AgentProvider.Claude)["restartDone"]!.Value<int>("notContinued").Should().Be(1);
    }

    [Fact]
    public async Task 接續的一輪開始後按停止_會話回Restarted_照停止的樣子收尾()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SafePoint = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.RestartGate = gate;
        s.OnRestart = _ =>
        {
            s.ProcessInstance = null;
            s.IsRunning = false;
            return new AgentRestartResult(AgentRestartOutcome.Restarted, "p1");
        };
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        h.ProcessStarted(s, "p2", ClaudeNew);
        h.Raise(s, new AgentTurnStartedByCliEvent(AgentAutoTurnKind.ResumeInterruptedTurn));

        h.Command(h.Main, "stop", new { conversationId = id });
        s.Interrupts.Should().Be(1, "重開途中按停止＝不接續（會話處理）");
        gate.SetResult();
        await h.Ui.DrainAsync();

        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Last().Text.Should().Be("已停止這一輪");
        CliNotices(h, id).Should().BeEmpty("使用者自己停的");
        IsNull(HeaderRestart(h, id)).Should().BeTrue();
    }

    [Fact]
    public async Task 接續的一輪開始後重開失敗_照樣在本機收尾_只有失敗的提示()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.RestartGate = gate;
        s.OnRestart = _ =>
        {
            s.ProcessInstance = null;
            s.IsRunning = false;
            return new AgentRestartResult(AgentRestartOutcome.Failed, "c1", null, "Codex 無法開始接續的一輪：boom");
        };
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);
        // Codex 在送出接續訊息的 turn/start 之前發開始事件；turn/start 失敗＝回 Failed
        h.ProcessStarted(s, "c2", CodexNew);
        h.Raise(s, new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));

        gate.SetResult();
        await h.Ui.DrainAsync();

        h.IsRunning(id).Should().BeFalse("不能卡在執行中");
        CliNotices(h, id).Should().ContainSingle().Which.Text.Should().StartWith("以新版 CLI 重新啟動失敗：Codex 無法開始接續的一輪：boom");
        State(h, id).Should().Be("failed");
        CliView(h, AgentProvider.Codex)["restartDone"]!.Value<int>("failed").Should().Be(1);
    }

    [Fact]
    public async Task 重開中這一輪自己先失敗_錯誤照常顯示_佇列照失敗暫停_重開結果只算關了程序()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.RestartGate = gate;
        s.OnRestart = _ =>
        {
            s.ProcessInstance = null;
            return new AgentRestartResult(AgentRestartOutcome.Restarted, "c1");
        };
        h.Send(h.Main, id, "排隊的");
        var sent = s.Sent.Count;
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);

        // 在 turn/interrupt 生效前這一輪自己失敗：會話照常回報（不是被重開打斷的）
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "API Error: 529 overloaded");

        h.IsRunning(id).Should().BeFalse("照常結束這一輪");
        h.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Error && i.Text == "API Error: 529 overloaded", "失敗的原因要讓使用者看到");
        gate.SetResult();
        await h.Ui.DrainAsync();

        CliNotices(h, id).Should().BeEmpty("這一輪是自己失敗的，不是「沒有自動接續」");
        IsNull(HeaderRestart(h, id)).Should().BeTrue();
        h.Queue(id).Should().Equal(new[] { "排隊的" }, "照失敗的規則暫停佇列");
        s.Sent.Count.Should().Be(sent);
        CliView(h, AgentProvider.Codex)["restartDone"]!.Value<int>("restarted").Should().Be(1);
    }

    [Fact]
    public async Task 立即套用回Deferred_不必等安全點事件_稍後主動再試()
    {
        using var h = await NewHarnessAsync();
        h.Ui.Invoke(() => h.Hub.DeferredRetryDelay = TimeSpan.FromMilliseconds(100));
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SucceedRestarts();
        var succeed = s.OnRestart!;
        int calls = 0;
        // Claude：剛送出的訊息 CLI 一直沒接手（會話等了 10 秒）＝立即重開也回 Deferred
        s.OnRestart = request => ++calls == 1 ? new AgentRestartResult(AgentRestartOutcome.Deferred) : succeed(request);
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        State(h, id).Should().Be("waitingTool");

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        s.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.Immediate);
        State(h, id).Should().Be("waitingTool", "回 Deferred：先回到等待");
        await h.WaitUntilAsync(() => s.Restarts.Count == 2, "沒有安全點事件也會主動再試", 3000);
        s.Restarts.All[1].Mode.Should().Be(AgentRestartMode.Immediate);
        await h.Ui.DrainAsync();
        State(h, id).Should().Be("continued");
        CliNotices(h, id).Should().ContainSingle().Which.Text.Should().StartWith("已用 Claude Code 2.1.297 自動接續（中斷時正在執行：npm test");
    }

    [Fact]
    public async Task 防衛_會話說正在重開而拒絕送出_訊息放回佇列最前面_不當成這一輪失敗_稍後自動重送()
    {
        using var h = await NewHarnessAsync();
        h.Ui.Invoke(() => h.Hub.RejectedSendRetryDelay = TimeSpan.FromMilliseconds(50));
        var (id, s) = StartRunning(h, h.Codex, "c1");
        h.CompleteTurn(s);
        var sent = s.Sent.Count;
        var users = h.Items(id).Count(i => i.Kind == AgentItemKind.User);
        s.SendFailures.Enqueue(new AgentRestartInProgressException());

        h.Send(h.Main, id, "下一則");

        h.IsRunning(id).Should().BeFalse("不是這一輪失敗：回到閒置");
        h.Queue(id).Should().Equal(new[] { "下一則" }, "放回佇列最前面");
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Error);
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(users, "被拒的那則還沒送出：不留使用者項目（重送時才加）");

        await h.WaitUntilAsync(() => s.Sent.Count == sent + 1, "稍後自動重送");
        s.Sent.All[^1].Input.Text.Should().Be("下一則");
        h.Queue(id).Should().BeEmpty();
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(users + 1);
        h.IsRunning(id).Should().BeTrue();
    }

    [Fact]
    public async Task 防衛_一直被拒就停止自動重送_留在佇列等使用者按送出()
    {
        using var h = await NewHarnessAsync();
        h.Ui.Invoke(() => h.Hub.RejectedSendRetryDelay = TimeSpan.FromMilliseconds(20));
        var (id, s) = StartRunning(h, h.Codex, "c1");
        h.CompleteTurn(s);
        var sent = s.Sent.Count;
        for (int i = 0; i < 6; i++)
            s.SendFailures.Enqueue(new AgentRestartInProgressException());
        var mark = h.Main.Mark();

        h.Send(h.Main, id, "下一則");

        await h.Main.WaitForAsync("toast", m => m.Value<string>("text")!.Contains("請稍候再按送出"), mark, timeoutMs: 3000);
        s.SendFailures.Should().BeEmpty("第一次加上 5 次自動重送");
        await Task.Delay(150);
        s.Sent.Count.Should().Be(sent, "不再自動重送");
        h.Queue(id).Should().Equal("下一則");
        h.IsRunning(id).Should().BeFalse();

        h.Send(h.Main, id, "");
        s.Sent.Count.Should().Be(sent + 1, "使用者按送出：從佇列第一則接著送");
        s.Sent.All[^1].Input.Text.Should().Be("下一則");
    }

    [Fact]
    public async Task 自動接續的一輪開始_閒置的對話改成執行中_程序回報的版本存檔()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        h.Raise(s, new AgentRunningVersionEvent("p1", "2.1.295"));
        h.Raise(s, new AgentRunningVersionEvent("p0", "2.1.200"));

        h.Conversation(id)!.LastCliVersion.Should().Be("2.1.295", "舊程序晚到的回報不算");
        new ConversationStore(h.Store.RootDirectory).LoadConversations().Single(m => m.Id == id).LastCliVersion.Should().Be("2.1.295", "存進 meta.json");

        h.CompleteTurn(s);
        h.IsRunning(id).Should().BeFalse();
        var mark = h.Main.Mark();
        h.Raise(s, new AgentTurnStartedByCliEvent(AgentAutoTurnKind.ResumeInterruptedTurn));

        h.IsRunning(id).Should().BeTrue("不是由送出開始的一輪也算執行中");
        var header = await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), mark, conversationId: id);
        header["header"]!.Value<string>("status").Should().Be("已自動接續…");
        h.CompleteTurn(s);
        h.IsRunning(id).Should().BeFalse();
    }

    // ───── 安裝狀態 ─────

    [Fact]
    public async Task 安裝損壞_不重開任何對話_CLI卡顯示原因()
    {
        using var h = await NewHarnessAsync();
        var (idleId, idle) = StartRunning(h, h.Codex, "a1");
        h.CompleteTurn(idle);
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        s.SucceedRestarts();
        const string reason = "Codex 安裝不一致（package.json 0.162.0、執行檔 0.160.1），請修復";

        await h.PublishCliAsync(new CliVersionChange(AgentProvider.Codex, CodexOld, null, CliChangeOrigin.External, Stable: true, InstallBroken: true, reason));

        s.Restarts.Count.Should().Be(0);
        idle.Closes.Should().Be(0, "損壞時 0 次重開（S2）");
        var cli = CliView(h, AgentProvider.Codex);
        cli.Value<string>("installBroken").Should().Be(reason);
        cli.Value<bool>("changing").Should().BeFalse();
    }

    [Fact]
    public async Task 安裝變動中_送出排進佇列_穩定後先關舊程序再以新版送出()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        h.CompleteTurn(s);
        await h.PublishCliAsync(new CliVersionChange(AgentProvider.Claude, ClaudeOld, null, CliChangeOrigin.External, Stable: false, InstallBroken: false, null));
        CliView(h, AgentProvider.Claude).Value<bool>("changing").Should().BeTrue();
        var sent = s.Sent.Count;
        var mark = h.Main.Mark();

        h.Send(h.Main, id, "變動中送出");

        s.Sent.Count.Should().Be(sent, "換檔空窗啟動 CLI 會失敗");
        h.Queue(id).Should().Equal("變動中送出");
        h.Main.Messages("sendRejected", mark, id).Should().BeEmpty("不退回：排進佇列");
        h.Main.Messages("toast", mark).Should().Contain(m => m.Value<string>("text")!.Contains("Claude Code CLI 正在更新"));
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingUpdate");
        header.Value<string>("detail").Should().Be("等待 Claude Code 更新完成…");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        s.Closes.Should().Be(1, "先關掉跑舊版的閒置程序");
        s.Sent.Count.Should().Be(sent + 1, "穩定後自動送出");
        s.Sent.All[^1].Input.Text.Should().Be("變動中送出");
        s.Sent.All[^1].Settings.Cli.Should().Be(ClaudeNew);
        h.Queue(id).Should().BeEmpty();
        State(h, id).Should().BeNull();
    }

    // ───── 分批（7.4.3）─────

    [Fact]
    public async Task 分批_同時最多兩個Claude在啟動_執行中的先_空出名額再接著重開()
    {
        using var h = await NewHarnessAsync();
        var running = new List<(string Id, FakeSession Session, TaskCompletionSource Gate)>();
        for (int i = 0; i < 3; i++)
        {
            var (id, s) = StartRunning(h, h.Claude, $"p{i}");
            s.SafePoint = true;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            s.RestartGate = gate;
            s.SucceedRestarts($"n{i}");
            running.Add((id, s, gate));
        }
        var (bgId, bg) = StartRunning(h, h.Claude, "bg");
        h.CompleteTurn(bg);
        bg.HasLiveBackgroundWork = true;
        var bgGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bg.RestartGate = bgGate;
        bg.SucceedRestarts("bg");

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        running.Select(r => r.Session.Restarts.Count).Should().Equal(1, 1, 0);
        State(h, running[2].Id).Should().Be("restarting", "等名額");
        State(h, bgId).Should().Be("waitingBackground");

        // CLI 卡的立即套用：閒置但有背景工作的也要重開，排在執行中的後面
        h.Command(h.Main, "cliApplyNow", new { provider = "Claude" });
        bg.Restarts.Count.Should().Be(0, "名額滿了");

        running[0].Gate.SetResult();
        await h.Ui.DrainAsync();
        running[2].Session.Restarts.Count.Should().Be(1, "執行中的先");
        bg.Restarts.Count.Should().Be(0);

        running[1].Gate.SetResult();
        await h.Ui.DrainAsync();
        var bgRequest = bg.Restarts.All.Should().ContainSingle().Subject;
        bgRequest.AutoContinue.Should().BeFalse("閒置的沒有要接續的一輪");
        bgRequest.Mode.Should().Be(AgentRestartMode.Immediate);

        running[2].Gate.SetResult();
        bgGate.SetResult();
        await h.Ui.DrainAsync();
        running.Select(r => State(h, r.Id)).Should().AllBe("continued");
        State(h, bgId).Should().BeNull();
        CliView(h, AgentProvider.Claude).Value<int>("restartPending").Should().Be(0);
    }

    [Fact]
    public async Task 分批_Codex同時最多四個在啟動()
    {
        using var h = await NewHarnessAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new List<FakeSession>();
        for (int i = 0; i < 5; i++)
        {
            var (_, s) = StartRunning(h, h.Codex, $"c{i}");
            s.SafePoint = true;
            s.RestartGate = gate;
            s.SucceedRestarts($"n{i}");
            sessions.Add(s);
        }

        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);

        sessions.Count(s => s.Restarts.Count == 1).Should().Be(4);
        gate.SetResult();
        await h.Ui.DrainAsync();
        sessions.Should().OnlyContain(s => s.Restarts.Count == 1, "空出名額後第五個接著重開");
    }

    // ───── 政策 ─────

    [Fact]
    public async Task 政策_等這一輪結束再切換_設定指令只接受三個值()
    {
        using var h = await NewHarnessAsync();
        var mark = h.Main.Mark();
        h.Command(h.Main, "setting", new { key = "cliRestartPolicy", value = "AfterTurn" });
        h.Settings.CliRestartPolicy.Should().Be(CliRestartPolicy.AfterTurn);
        h.Main.Messages("settings", mark).Last()["settings"]!.Value<string>("cliRestartPolicy").Should().Be("AfterTurn");
        h.Command(h.Main, "setting", new { key = "cliRestartPolicy", value = "Immediately" });
        h.Settings.CliRestartPolicy.Should().Be(CliRestartPolicy.AfterTurn, "不認得的值不改");
        CliView(h, AgentProvider.Codex).Value<string>("restartPolicy").Should().Be("AfterTurn");

        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        s.SucceedRestarts();
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);

        s.Restarts.Count.Should().Be(0, "在安全點也不中途重開");
        State(h, id).Should().Be("waitingTurn");

        h.CompleteTurn(s);
        s.Closes.Should().Be(1, "這一輪結束時關掉舊程序");
        s.Restarts.Count.Should().Be(0);
        State(h, id).Should().BeNull();
    }

    [Fact]
    public async Task 政策_下次送出時才用新版_不主動關程序_改回安全點時立即處理()
    {
        using var h = await NewHarnessAsync();
        h.Command(h.Main, "setting", new { key = "cliRestartPolicy", value = "NextSend" });
        var (id, s) = StartRunning(h, h.Claude, "p1");
        h.CompleteTurn(s);
        var (otherId, other) = StartRunning(h, h.Claude, "q1");
        h.CompleteTurn(other);

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        s.Closes.Should().Be(0);
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("nextSend");
        header.Value<string>("detail").Should().Be("CLI 已更新到 2.1.297，下次送出時改用新版");
        header.Value<string>("from").Should().Be("2.1.295");
        var cli = CliView(h, AgentProvider.Claude);
        cli.Value<int>("runningOnOldVersion").Should().Be(2);
        cli.Value<int>("restartPending").Should().Be(0, "nextSend 不是待重開");

        h.Send(h.Main, id, "下一則");
        s.Sent.All[^1].Settings.Cli.Should().Be(ClaudeNew, "會話看到身分不同就以新版重開程序");
        h.ProcessStarted(s, "p2", ClaudeNew);
        State(h, id).Should().BeNull();

        h.Command(h.Main, "setting", new { key = "cliRestartPolicy", value = "SafePoint" });
        other.Closes.Should().Be(1, "改回安全點：還在跑舊版的閒置程序立即關");
        CliView(h, AgentProvider.Claude).Value<int>("runningOnOldVersion").Should().Be(0);
    }

    // ───── 背景工作 ─────

    [Fact]
    public async Task 背景工作_閒置有背景工作就延後_可以立即套用_結束後安靜下來才關()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = StartRunning(h, h.Claude, "p1");
        h.CompleteTurn(s);
        s.HasLiveBackgroundWork = true;

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        s.Closes.Should().Be(0, "背景工作撐不過重開：等它結束");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingBackground");
        header.Value<string>("detail").Should().Be("CLI 已更新到 2.1.297，等背景工作結束後切換（最多等 30 分鐘）");
        header.Value<bool>("canApplyNow").Should().BeTrue("不想等到上限就自己按");

        now = start.AddMinutes(29);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        s.Restarts.Count.Should().Be(0, "還沒滿 30 分鐘");
        CliNotices(h, id).Should().BeEmpty();

        // 背景工作結束：CLI 會自己開一輪告訴模型，對話安靜下來才關
        h.Ui.Invoke(() => h.Hub.BackgroundSettle = TimeSpan.FromMilliseconds(300));
        s.HasLiveBackgroundWork = false;
        h.Raise(s, new AgentSafePointChangedEvent(false, false, false));
        s.Closes.Should().Be(0, "剛結束：等一下");
        await h.WaitUntilAsync(() => s.Closes == 1, "安靜下來後關程序", 3000);
        State(h, id).Should().BeNull();
    }

    private static readonly AgentBackgroundTool NpmTestTask = new("執行指令", "bash", "npm test", "npm test");

    /// <summary>閒置、有背景工作在跑的 Claude 對話（重開成功：不接續的回 Restarted）</summary>
    private static (string Id, FakeSession S) IdleWithBackground(HubHarness h, AgentBackgroundTool tool)
    {
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SucceedRestarts();
        s.HasLiveBackgroundWork = true;
        s.LiveBackgroundTools = new[] { tool };
        h.Raise(s, new AgentBackgroundWorkEvent(1, new[] { tool }));
        h.CompleteTurn(s);
        return (id, s);
    }

    [Fact]
    public async Task 背景工作最多等30分鐘_之後照立即套用以新版重開_馬上請代理確認背景工作()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = IdleWithBackground(h, NpmTestTask);
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        State(h, id).Should().Be("waitingBackground");

        now = start.AddMinutes(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());

        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.Mode.Should().Be(AgentRestartMode.Immediate, "等滿上限就不再等背景工作");
        request.AutoContinue.Should().BeFalse("這一輪早就結束了，沒有要接續的");
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "以新版重開後馬上送出確認訊息");
        var text = s.Sent.All[1].Input.Text;
        text.Should().StartWith("（系統通知：Clode Myriagent 已把 Claude Code 由 2.1.29").And.Contain("更新為 2.1.297 並重新啟動");
        text.Should().Contain("上一輪你已經回覆完，正在等背景工作〈執行指令：npm test〉回報；它們在重啟時已經停止，結果未知");
        text.Should().Contain("可安全重跑才重跑");
        s.Sent.All[1].Settings.Cli.Should().Be(ClaudeNew);
        h.IsRunning(id).Should().BeTrue();
        CliNotices(h, id).Last().Text.Should().Be(CliRestartRules.BackgroundCheckNotice("2.1.297"));
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "確認訊息不顯示成使用者訊息");
        IsNull(HeaderRestart(h, id)).Should().BeTrue();
    }

    [Fact]
    public async Task 等背景工作時按立即套用_一樣以新版重開並請代理確認()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = IdleWithBackground(h, NpmTestTask);
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        s.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.Immediate);
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "送出確認訊息");
        s.Sent.All[1].Input.Text.Should().Contain("正在等背景工作〈執行指令：npm test〉回報");
    }

    [Fact]
    public async Task 換版停掉的背景工作是高風險操作_不自動請代理確認_標頭給繼續()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var push = new AgentBackgroundTool("執行指令", "bash", "git push origin main", "git push origin main");
        var (id, s) = IdleWithBackground(h, push);
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        now = start.AddMinutes(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());

        s.Restarts.Count.Should().Be(1);
        await Task.Delay(100);
        await h.Ui.DrainAsync();
        s.Sent.Count.Should().Be(1, "git push 被停在哪一步不知道：不自動請代理接著做");
        CliNotices(h, id).Last().Text.Should().Be(CliRestartRules.BackgroundNotCheckedNotice("2.1.297", "git push origin main"));
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();

        h.Command(h.Main, "cliContinue", new { conversationId = id });
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "按「繼續」送出確認訊息");
        s.Sent.All[1].Input.Text.Should().Contain("正在等背景工作〈執行指令：git push origin main〉回報").And.Contain("使用者看過狀況後按了「繼續」");
    }

    [Fact]
    public async Task 等背景工作時開始了別的一輪_背景工作照樣等_不另外送確認()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = IdleWithBackground(h, NpmTestTask);
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        // 背景工作跑完、CLI 自己開一輪告訴模型：這一輪結束、背景工作都沒了才關程序，不必另外請代理確認
        s.HasLiveBackgroundWork = false;
        s.LiveBackgroundTools = Array.Empty<AgentBackgroundTool>();
        h.Raise(s, new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        h.CompleteTurn(s);
        await h.WaitUntilAsync(() => s.Closes == 1, "背景工作做完、對話閒置：關掉舊程序");

        now = start.AddMinutes(31);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        s.Restarts.Count.Should().Be(0);
        s.Sent.Count.Should().Be(1, "背景工作自己做完了：不另外請代理確認");
    }

    [Fact]
    public async Task 屏障_等背景工作滿30分鐘停下_更新完成後馬上請代理確認背景工作()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = IdleWithBackground(h, NpmTestTask);
        var participant = h.Cli.UpdateParticipant!;

        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Claude, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        prepare.IsCompleted.Should().BeFalse("背景工作還在跑");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingBackground");
        header.Value<string>("detail").Should().Be("等背景工作結束後停下，讓 Claude Code 更新（最多等 30 分鐘）");

        now = start.AddMinutes(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());

        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.Settings.Should().BeNull("屏障是停下，等更新完成");
        request.Mode.Should().Be(AgentRestartMode.Immediate);
        s.Sent.Count.Should().Be(1, "CLI 還在更新：先記著");

        await FinishUpdateAsync(h, AgentProvider.Claude, CliUpdateOutcome.Updated, ClaudeNew);

        await h.WaitUntilAsync(() => s.Sent.Count == 2, "更新完成後馬上送出確認訊息");
        s.Sent.All[1].Input.Text.Should().Contain("正在等背景工作〈執行指令：npm test〉回報");
        s.Sent.All[1].Settings.Cli.Should().Be(ClaudeNew);
        h.IsRunning(id).Should().BeTrue();
    }

    [Fact]
    public async Task 閒置回收_有存活背景工作的程序不關()
    {
        using var h = await NewHarnessAsync();
        var (_, bg) = StartRunning(h, h.Claude, "p1");
        h.CompleteTurn(bg);
        bg.HasLiveBackgroundWork = true;
        var (_, plain) = StartRunning(h, h.Claude, "q1");
        h.CompleteTurn(plain);

        h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now + AgentHubRules.IdleProcessTimeout + TimeSpan.FromMinutes(1)));

        plain.Closes.Should().Be(1, "閒置太久照常回收");
        bg.Closes.Should().Be(0, "背景工作撐不過程序重開：不回收");
    }

    [Fact]
    public async Task 換版_閒置但開著RemoteControl的程序不關_下次送出才用新版()
    {
        using var h = await NewHarnessAsync();
        var (remoteId, remote) = StartRunning(h, h.Claude, "p1");
        h.CompleteTurn(remote);
        remote.HasRemoteControl = true;
        var (_, plain) = StartRunning(h, h.Claude, "q1");
        h.CompleteTurn(plain);

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        plain.Closes.Should().Be(1, "閒置的照常關");
        remote.Closes.Should().Be(0, "關了 claude.ai 與 Claude App 上的工作階段就斷線（CLI 一天可能自動更新好幾次）");
        remote.Restarts.Count.Should().Be(0);

        h.Send(h.Main, remoteId, "下一則");
        await h.WaitUntilAsync(() => remote.Sent.Count == 2, "送出");
        remote.Sent.All[1].Settings.Cli.Should().Be(ClaudeNew, "下次送出以新版啟動（會話依身分不同重開程序）");
    }

    // ───── 卡片的實例 ─────

    [Fact]
    public async Task 卡片作廢_換了新程序或程序結束_舊卡失效_回應舊卡不轉送()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        h.Raise(s, new AgentApprovalEvent("r1", "執行指令", "bash", "rm x", null, "t1", CanAllowForSession: true));
        h.Raise(s, new AgentQuestionEvent("q1", new[] { new AgentQuestion { Id = "q", Text = "要嗎？" } }, AnswerAsMessage: false));
        h.Raise(s, new AgentQuestionEvent("q2", new[] { new AgentQuestion { Id = "q", Text = "之後再答" } }, AnswerAsMessage: true));
        var items = h.Items(id);
        var approval = items.Single(i => i.Kind == AgentItemKind.Approval);
        var question = items.Single(i => i.Kind == AgentItemKind.Question && !i.AnswerAsMessage);
        var asyncQuestion = items.Single(i => i.AnswerAsMessage);

        h.ProcessStarted(s, "p2", ClaudeOld);

        approval.Status.Should().Be(AgentItemStatus.Cancelled, "產生卡片的程序已不在");
        approval.Output.Should().Be(CliRestartRules.StaleCardOutput);
        question.Status.Should().Be(AgentItemStatus.Cancelled);
        asyncQuestion.Status.Should().Be(AgentItemStatus.Waiting, "Codex 的非同步提問不靠程序");
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "allow" });
        s.Approvals.Count.Should().Be(0, "絕不把舊卡的回覆送給新程序");
        var message = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("id") == approval.Id && m["item"]!.Value<string>("status") == "Cancelled", conversationId: id);
        message["item"]!.Value<string>("output").Should().Be(CliRestartRules.StaleCardOutput);

        // 新卡寫新實例；那個程序結束（Codex 直接殺時沒有撤回核准的通知）就作廢
        h.Raise(s, new AgentApprovalEvent("r2", "執行指令", "bash", "rm y", null, "t2", CanAllowForSession: true));
        var fresh = h.Items(id).Single(i => i.RequestKey == "r2");
        fresh.ProcessInstance.Should().Be("p2");
        h.Raise(s, new AgentProcessExitedEvent(1, null, Expected: false, ProcessInstance: "p2"));
        fresh.Status.Should().Be(AgentItemStatus.Cancelled);
        fresh.Output.Should().Be(CliRestartRules.StaleCardOutput);

        // 回應時比對實例：兩邊都已知且不同就作廢、不轉送；任一不明不比對
        h.ProcessStarted(s, "p3", ClaudeOld);
        h.Raise(s, new AgentApprovalEvent("r3", "執行指令", "bash", "rm z", null, "t3", CanAllowForSession: true));
        h.Raise(s, new AgentApprovalEvent("r4", "執行指令", "bash", "rm w", null, "t4", CanAllowForSession: true));
        var stale = h.Items(id).Single(i => i.RequestKey == "r3");
        var unknown = h.Items(id).Single(i => i.RequestKey == "r4");
        stale.ProcessInstance = "p-old";
        unknown.ProcessInstance = null;
        var toast = h.Main.Mark();

        h.Command(h.Main, "approve", new { conversationId = id, itemId = stale.Id, choice = "allow" });
        h.Command(h.Main, "approve", new { conversationId = id, itemId = unknown.Id, choice = "allow" });

        stale.Status.Should().Be(AgentItemStatus.Cancelled);
        s.Approvals.All.Should().ContainSingle().Which.Key.Should().Be("r4", "舊資料、還沒發事件的卡片不做比對");
        h.Main.Messages("toast", toast).Should().Contain(m => m.Value<string>("text")!.Contains("這張卡已失效"));
    }

    [Fact]
    public async Task Claude屏障現在中斷只停下_沒有新程序也沒有結束事件_以PreviousProcessInstance作廢舊卡()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "q1");
        // Claude 重開時卸下舊程序再殺樹：舊程序不發 AgentProcessExitedEvent；只停下（Settings=null）也沒有新程序的開始事件
        s.OnRestart = request =>
        {
            s.ProcessInstance = null;
            s.IsRunning = false;
            return new AgentRestartResult(request.Settings == null ? AgentRestartOutcome.Stopped : AgentRestartOutcome.Restarted, "q1");
        };
        h.Raise(s, new AgentApprovalEvent("r1", "執行指令", "bash", "rm y", null, "t1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(false, WaitingForUser: true, false));
        var card = h.Items(id).Single(i => i.RequestKey == "r1");
        card.ProcessInstance.Should().Be("q1");
        var participant = h.Cli.UpdateParticipant!;
        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Claude, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        State(h, id).Should().Be("waitingUser", "等你回應：排空也不打斷");

        h.Command(h.Main, "cliApplyNow", new { provider = "Claude" });

        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.Restarts.All.Should().ContainSingle().Which.Settings.Should().BeNull("現在中斷：停下等更新");
        card.Status.Should().Be(AgentItemStatus.Cancelled, "產生卡片的程序已停下");
        card.Output.Should().Be(CliRestartRules.StaleCardOutput);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = card.Id, choice = "allow" });
        s.Approvals.Count.Should().Be(0, "舊卡的回覆不轉送");
        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Claude, CliUpdateOutcome.Cancelled, ClaudeOld));
        await h.Ui.DrainAsync();
    }

    [Fact]
    public async Task 被重開中斷的工具卡_會話沒送工具結束時_新程序開始時改成已停止並註明結果未知_保留輸出()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        h.Raise(s, new AgentToolOutputEvent("t1", "  12 passing\n"));
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);

        // 立即套用、interrupt 逾時直接結束程序樹：舊程序沒有送 item/completed
        h.Command(h.Main, "cliApplyNow", new { conversationId = id });

        var tool = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool);
        tool.Status.Should().Be(AgentItemStatus.Cancelled, "已停止，不是失敗");
        tool.Output.Should().Be("  12 passing\n\n" + CodexAppServerRules.InterruptedToolOutput, "保留輸出，後面註明被重開中斷、結果未知");
        // 與會話送的一致：Claude 立即重開時會話送 AgentToolCompletedEvent(Cancelled, InterruptedToolOutput)，
        // Codex 被結束的指令（turn/completed 之後的 item/completed(failed)）改成 Cancelled＋同樣的說明
        CliRestartRules.InterruptedToolOutput(AgentProvider.Claude).Should().Be(ClaudeStreamRules.InterruptedToolOutput);
        CliRestartRules.InterruptedToolOutput(AgentProvider.Codex).Should().Be(CodexAppServerRules.InterruptedToolOutput);
        CliRestartRules.AppendInterruptedNote(tool.Output, AgentProvider.Codex).Should().Be(tool.Output, "已經有說明就不重複");
    }

    // ───── 更新屏障（ICliUpdateParticipant）─────

    [Fact]
    public async Task 屏障_閒置的關掉_安全點的停下_工具執行中的等_送出排隊_更新完以新版接續()
    {
        using var h = await NewHarnessAsync();
        var (idleId, idle) = StartRunning(h, h.Codex, "a1");
        h.CompleteTurn(idle);
        var (safeId, safe) = StartRunning(h, h.Codex, "b1");
        safe.SafePoint = true;
        safe.SucceedRestarts("b");
        var (busyId, busy) = StartRunning(h, h.Codex, "c1");
        busy.SucceedRestarts("c");
        h.Raise(busy, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        h.Raise(busy, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
        var (_, claude) = StartRunning(h, h.Claude, "x1");
        h.CompleteTurn(claude);
        var participant = h.Cli.UpdateParticipant!;

        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();

        prepare.IsCompleted.Should().BeFalse("還有對話在執行工具");
        idle.Closes.Should().Be(1, "閒置的立刻關");
        safe.Restarts.All.Should().ContainSingle().Which.Settings.Should().BeNull("在安全點停下（Settings=null），等更新完成");
        busy.Restarts.Count.Should().Be(0, "工具在執行：等它結束");
        claude.Closes.Should().Be(0, "只排空要更新的那種 CLI");
        h.IsRunning(safeId).Should().BeTrue("停下等更新不是「已停止」：使用者看到的是同一輪");
        var safeHeader = (JObject)HeaderRestart(h, safeId);
        safeHeader.Value<string>("state").Should().Be("waitingUpdate");
        safeHeader.Value<string>("detail").Should().Be("等待 Codex 更新完成…");
        safeHeader.Value<bool>("canApplyNow").Should().BeFalse();
        var busyHeader = (JObject)HeaderRestart(h, busyId);
        busyHeader.Value<string>("state").Should().Be("waitingTool");
        busyHeader.Value<string>("detail").Should().Be("等目前的指令（npm test）完成後停下，讓 Codex 更新");
        busyHeader.Value<bool>("canApplyNow").Should().BeTrue();
        var update = CliView(h, AgentProvider.Codex)["update"]!;
        update.Value<string>("phase").Should().Be("draining");
        update.Value<int>("held").Should().Be(1);
        update.Value<int>("waiting").Should().Be(1);
        update.Value<bool>("overdue").Should().BeFalse();
        IsNull(CliView(h, AgentProvider.Claude)["update"]).Should().BeTrue();

        // 屏障期間的送出排進佇列（不啟動新程序）
        var sentBefore = idle.Sent.Count;
        h.Send(h.Main, idleId, "更新時送出");
        idle.Sent.Count.Should().Be(sentBefore);
        h.Queue(idleId).Should().Equal("更新時送出");
        State(h, idleId).Should().Be("waitingUpdate");

        // 工具結束、到了安全點：停下，全部停下才回 Ready
        h.Raise(busy, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        busy.SafePoint = true;
        h.Raise(busy, new AgentSafePointChangedEvent(false, false, false));
        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Should().Be(new CliUpdatePreparation(Ready: true, ClosedIdle: 1, Held: 2, Busy: 0));

        // 屏障期間的換版只記下新身分，等 UpdateFinished 才重開
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew, CliChangeOrigin.App);
        safe.Restarts.Count.Should().Be(1);
        busy.Restarts.Count.Should().Be(1);

        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Updated, CodexNew));
        await h.Ui.DrainAsync();

        foreach (var (conversationId, session) in new[] { (safeId, safe), (busyId, busy) })
        {
            var resume = session.Restarts.All.Should().HaveCount(2).And.Subject.Last();
            resume.Settings!.Cli.Should().Be(CodexNew);
            resume.AutoContinue.Should().BeTrue();
            resume.Mode.Should().Be(AgentRestartMode.Immediate, "已經沒有程序，不必等安全點");
            resume.FromVersion.Should().Be("0.160.1");
            resume.ToVersion.Should().Be("0.162.0");
            State(h, conversationId).Should().Be("continued");
        }
        idle.Sent.Count.Should().Be(sentBefore + 1, "更新完成後延後的訊息自動送出");
        idle.Sent.All[^1].Settings.Cli.Should().Be(CodexNew);
        h.Queue(idleId).Should().BeEmpty();
        IsNull(CliView(h, AgentProvider.Codex)["update"]).Should().BeTrue();
        var done = CliView(h, AgentProvider.Codex)["restartDone"]!;
        done.Value<int>("continued").Should().Be(2);
        done.Value<int>("restarted").Should().Be(1);
        h.Host.Notifications.All.Should().Contain(n => n.ConversationId == null && n.Title == "Codex 已更新到 0.162.0");
    }

    [Fact]
    public async Task 屏障_草稿的第一則訊息也延後_更新結束後送出()
    {
        using var h = await NewHarnessAsync();
        var participant = h.Cli.UpdateParticipant!;
        var preparation = await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Ready.Should().BeTrue("沒有任何程序");
        var draft = h.NewConversation(h.Main, h.Codex);

        h.Send(h.Main, draft, "草稿的第一則");

        h.Sessions.Should().BeEmpty("屏障期間不啟動新程序");
        h.Queue(draft).Should().Equal("草稿的第一則");
        State(h, draft).Should().Be("waitingUpdate");

        await FinishUpdateAsync(h, AgentProvider.Codex, CliUpdateOutcome.Updated, CodexNew);

        var session = h.Sessions.Should().ContainSingle().Subject;
        session.Sent.All.Should().ContainSingle().Which.Input.Text.Should().Be("草稿的第一則");
        session.Sent.All[0].Settings.Cli.Should().Be(CodexNew);
        h.Conversation(draft)!.Title.Should().Be("草稿的第一則", "送出時才轉成正式對話");
        h.Queue(draft).Should().BeEmpty();
    }

    [Fact]
    public async Task 重開途中開始更新屏障_新程序跑的也是要換掉的檔案_照屏障停下()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.RestartGate = gate;
        s.SucceedRestarts();
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexNew);
        s.Restarts.Count.Should().Be(1, "以新版重開進行中");
        var participant = h.Cli.UpdateParticipant!;

        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        prepare.IsCompleted.Should().BeFalse("重開還沒做完");

        gate.SetResult();
        await h.Ui.DrainAsync();

        s.Restarts.Count.Should().Be(2);
        s.Restarts.All[1].Settings.Should().BeNull("接續的一輪在安全點停下，等更新完成");
        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Should().Be(new CliUpdatePreparation(Ready: true, ClosedIdle: 0, Held: 1, Busy: 0));
        State(h, id).Should().Be("waitingUpdate");
        h.IsRunning(id).Should().BeTrue();
    }

    [Fact]
    public async Task 屏障_自動更新_有對話在跑就什麼都不停_全部閒置才關()
    {
        using var h = await NewHarnessAsync();
        var (_, idle) = StartRunning(h, h.Codex, "a1");
        h.CompleteTurn(idle);
        var (_, run) = StartRunning(h, h.Codex, "b1");
        var participant = h.Cli.UpdateParticipant!;

        var preparation = await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Background, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));

        preparation.Should().Be(new CliUpdatePreparation(Ready: false, ClosedIdle: 0, Held: 0, Busy: 1));
        idle.Closes.Should().Be(0, "自動更新不打擾：有對話在跑就一個都不關");
        run.Restarts.Count.Should().Be(0);
        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, CodexOld));
        await h.Ui.DrainAsync();

        h.CompleteTurn(run);
        preparation = await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Background, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));

        preparation.Should().Be(new CliUpdatePreparation(Ready: true, ClosedIdle: 2, Held: 0, Busy: 0));
        idle.Closes.Should().Be(1);
        run.Closes.Should().Be(1);
        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Unchanged, CodexOld));
        await h.Ui.DrainAsync();
        h.Host.Notifications.All.Should().NotContain(n => n.ConversationId == null, "沒有換版不送彙總");
    }

    [Fact]
    public async Task 屏障_現在中斷_不等安全點_被打斷的工具寫進更新後的接續訊息()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts();
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm run build", "npm run build"));
        var participant = h.Cli.UpdateParticipant!;
        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        prepare.IsCompleted.Should().BeFalse();
        State(h, id).Should().Be("waitingTool");

        h.Command(h.Main, "cliApplyNow", new { provider = "Codex" });

        var stop = s.Restarts.All.Should().ContainSingle().Subject;
        stop.Settings.Should().BeNull();
        stop.Mode.Should().Be(AgentRestartMode.Immediate);
        stop.InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("npm run build");
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();

        await FinishUpdateAsync(h, AgentProvider.Codex, CliUpdateOutcome.Updated, CodexNew);

        var resume = s.Restarts.All[1];
        resume.AutoContinue.Should().BeTrue();
        resume.InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("npm run build");
        resume.ContinuationText.Should().Contain("〈執行指令：npm run build〉").And.Contain("結果未知");
        CliNotices(h, id).Should().ContainSingle().Which.Text.Should().StartWith("已用 Codex 0.162.0 自動接續（中斷時正在執行：npm run build");
    }

    [Fact]
    public async Task 屏障_取消更新_還在跑的不再停下_已停下的以原本的版本重開接續()
    {
        using var h = await NewHarnessAsync();
        var (heldId, held) = StartRunning(h, h.Codex, "a1");
        held.SafePoint = true;
        held.SucceedRestarts("a");
        var (busyId, busy) = StartRunning(h, h.Codex, "b1");
        busy.SucceedRestarts("b");
        h.Raise(busy, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
        var participant = h.Cli.UpdateParticipant!;
        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        held.Restarts.Count.Should().Be(1);

        h.Command(h.Main, "cliCancelUpdate", new { provider = "Codex" });

        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Should().Be(new CliUpdatePreparation(Ready: false, ClosedIdle: 0, Held: 1, Busy: 1, Reason: "已取消更新"));
        busy.SafePoint = true;
        h.Raise(busy, new AgentSafePointChangedEvent(false, false, false));
        busy.Restarts.Count.Should().Be(0, "取消後不再為了更新停下");

        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, CodexOld));
        await h.Ui.DrainAsync();

        var resume = held.Restarts.All[1];
        resume.Settings!.Cli.Should().Be(CodexOld, "沒有更新：以目前的版本重開");
        resume.AutoContinue.Should().BeTrue();
        resume.ContinuationText.Should().Contain("為了更新 Codex 而重新啟動了它");
        State(h, heldId).Should().Be("continued");
        var header = (JObject)HeaderRestart(h, heldId);
        IsNull(header["from"]).Should().BeTrue("版本沒變：不送「0.160.1 → 0.160.1」");
        IsNull(header["to"]).Should().BeTrue();
        header.Value<string>("detail").Should().Be("Codex 沒有換版（仍是 0.160.1），已自動接續");
        CliNotices(h, heldId).Should().ContainSingle().Which.Text.Should().StartWith("Codex 沒有換版（仍是 0.160.1），已自動接續");
        busy.Restarts.Count.Should().Be(0, "還在跑的本來就是目前的版本");
        State(h, busyId).Should().BeNull();
        h.Host.Notifications.All.Should().NotContain(n => n.ConversationId == null, "沒有換版不送彙總");
    }

    [Fact]
    public async Task 屏障_取消更新只在排空中有效()
    {
        using var h = await NewHarnessAsync();
        var (_, s) = StartRunning(h, h.Codex, "a1");
        s.SafePoint = true;
        s.SucceedRestarts();
        var participant = h.Cli.UpdateParticipant!;
        var preparation = await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Ready.Should().BeTrue();

        h.Command(h.Main, "cliCancelUpdate", new { provider = "Codex" });
        await FinishUpdateAsync(h, AgentProvider.Codex, CliUpdateOutcome.Updated, CodexNew);

        s.Restarts.All[^1].Settings!.Cli.Should().Be(CodexNew, "換檔已開始就忽略取消：照常以新版接續");
    }

    [Fact]
    public async Task 屏障_更新後沒有可用的版本_停下的那一輪以提示收尾_佇列暫停()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SafePoint = true;
        s.SucceedRestarts();
        h.Send(h.Main, id, "排隊的");
        var participant = h.Cli.UpdateParticipant!;
        (await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5))).Held.Should().Be(1);

        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Failed, null, "npm 更新失敗：EBUSY"));
        await h.Ui.DrainAsync();

        s.Restarts.Count.Should().Be(1, "沒有可用的版本：不重開");
        h.IsRunning(id).Should().BeFalse();
        var notice = h.Items(id).Last();
        notice.NoticeKind.Should().Be(AgentNoticeKind.CliUpdate);
        notice.Text.Should().Be("Codex 更新後沒有可用的版本（npm 更新失敗：EBUSY），這一輪沒有接續；修好 CLI 後按送出繼續（排隊的訊息暫停，按送出繼續）");
        h.Queue(id).Should().Equal(new[] { "排隊的" }, "佇列暫停");
        State(h, id).Should().Be("failed");
    }

    [Fact]
    public async Task 屏障_排空超過寬限_CLI卡問使用者_停下的對話按停止在本機收尾()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (busyId, busy) = StartRunning(h, h.Codex, "a1");
        h.Raise(busy, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
        var (heldId, held) = StartRunning(h, h.Codex, "b1");
        held.SafePoint = true;
        held.SucceedRestarts();
        var participant = h.Cli.UpdateParticipant!;
        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        CliView(h, AgentProvider.Codex)["update"]!.Value<bool>("overdue").Should().BeFalse();

        now = start.AddMinutes(11);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());

        var update = CliView(h, AgentProvider.Codex)["update"]!;
        update.Value<bool>("overdue").Should().BeTrue("排空超過 10 分鐘：網頁問要等還是現在中斷");
        update.Value<int>("waiting").Should().Be(1);
        update.Value<int>("held").Should().Be(1);
        update["startedAt"]!.Type.Should().Be(JTokenType.Integer);

        // 停下等更新的那一輪按停止：沒有程序、不會回報 TurnCompleted，本機收尾
        h.Command(h.Main, "stop", new { conversationId = heldId });
        h.IsRunning(heldId).Should().BeFalse();
        h.Items(heldId).Last().Text.Should().Be("已停止這一輪");
        CliView(h, AgentProvider.Codex)["update"]!.Value<int>("held").Should().Be(0);

        h.Command(h.Main, "cliCancelUpdate", new { provider = "Codex" });
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeFalse();
        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, CodexOld));
        await h.Ui.DrainAsync();
        held.Restarts.Count.Should().Be(1, "已停止的不再接續");
        State(h, busyId).Should().BeNull();
    }

    [Fact]
    public async Task 屏障_CliManager放棄等待時停止排空_停下的照樣等UpdateFinished()
    {
        using var h = await NewHarnessAsync();
        var (busyId, busy) = StartRunning(h, h.Codex, "a1");
        h.Raise(busy, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
        var (_, held) = StartRunning(h, h.Codex, "b1");
        held.SafePoint = true;
        held.SucceedRestarts();
        var participant = h.Cli.UpdateParticipant!;
        using var cancel = new CancellationTokenSource();
        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, cancel.Token);
        await h.Ui.DrainAsync();

        cancel.Cancel();
        await h.Ui.DrainAsync();

        await FluentActions.Awaiting(() => prepare.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<OperationCanceledException>();
        busy.SafePoint = true;
        h.Raise(busy, new AgentSafePointChangedEvent(false, false, false));
        busy.Restarts.Count.Should().Be(0, "停止排空：還沒停下的不再停");
        held.Restarts.Count.Should().Be(1);

        await FinishUpdateAsync(h, AgentProvider.Codex, CliUpdateOutcome.Updated, CodexNew);
        held.Restarts.Count.Should().Be(2, "停下的等 UpdateFinished 後重開");
        busy.Restarts.Count.Should().Be(1, "還在跑舊版的依一般換版處理：在安全點重開");
        State(h, busyId).Should().Be("waitingTurn", "這個會話不支援重開（OnRestart 為 null）：退回這一輪結束後關程序");
    }

    // ───── 審查後的修正（hub-restart-1～5） ─────

    /// <summary>Codex 對話在屏障排空中、停在工具執行中；排空逾時後工具結束、停下進行中（RestartGate 掛住），回傳停下的閘門</summary>
    private static async Task<(string Id, FakeSession Session, TaskCompletionSource Gate, Task<CliUpdatePreparation> Prepare)> StopInFlightAfterOverdueAsync(HubHarness h)
    {
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.RestartGate = gate;
        h.Send(h.Main, id, "排隊的");
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
        var prepare = h.Cli.UpdateParticipant!.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        State(h, id).Should().Be("waitingTool");

        // 排空超過寬限：CLI 卡這時才給「取消更新」；接著工具結束、到了安全點，開始停下（Codex 要 interrupt、關 stdin、等程序樹）
        now = start.AddMinutes(11);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        CliView(h, AgentProvider.Codex)["update"]!.Value<bool>("overdue").Should().BeTrue();
        s.SafePoint = true;
        h.Raise(s, new AgentSafePointChangedEvent(false, false, false));
        s.Restarts.All.Should().ContainSingle().Which.Settings.Should().BeNull("停下進行中");
        CliView(h, AgentProvider.Codex)["update"]!.Value<bool>("overdue").Should().BeTrue("停下還沒有結果：取消更新的按鈕還在");
        return (id, s, gate, prepare);
    }

    [Fact]
    public async Task 屏障_停下途中按取消更新_UpdateFinished比停下的結果先到_停下回來就以原本的版本接續_不會卡在等待更新()
    {
        using var h = await NewHarnessAsync();
        var (id, s, gate, prepare) = await StopInFlightAfterOverdueAsync(h);

        // 停下還沒回來就按「取消更新」：CliManager 以 Ready=false 結束，馬上在 finally 呼叫 UpdateFinished（幾毫秒，一定比停下先到）
        h.Command(h.Main, "cliCancelUpdate", new { provider = "Codex" });
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeFalse();
        h.Cli.UpdateParticipant!.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, CodexOld));
        await h.Ui.DrainAsync();

        gate.SetResult();
        await h.Ui.DrainAsync();

        var resume = s.Restarts.All.Should().HaveCount(2, "停下回來後就以原本的版本重開接續（以前停在 Held，沒有人會再重開它）").And.Subject.Last();
        resume.Settings!.Cli.Should().Be(CodexOld, "沒有更新：以原本的版本重開");
        resume.AutoContinue.Should().BeTrue();
        State(h, id).Should().Be("continued");
        CliView(h, AgentProvider.Codex).Value<int>("restartPending").Should().Be(0);
        h.Queue(id).Should().Equal("排隊的");

        h.CompleteTurn(s);
        s.Sent.All[^1].Input.Text.Should().Be("排隊的", "接續的一輪結束後佇列照常送出");
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        State(h, id).Should().BeNull();
    }

    [Fact]
    public async Task 屏障_停下途中取消而之後沒有可用的版本_停下回來就以提示收尾_不卡在執行中()
    {
        using var h = await NewHarnessAsync();
        var (id, s, gate, prepare) = await StopInFlightAfterOverdueAsync(h);

        h.Command(h.Main, "cliCancelUpdate", new { provider = "Codex" });
        await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        h.Cli.UpdateParticipant!.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, null, "找不到 Codex"));
        await h.Ui.DrainAsync();
        gate.SetResult();
        await h.Ui.DrainAsync();

        s.Restarts.Count.Should().Be(1, "沒有可用的版本：不重開");
        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Last().Text.Should().Be(CliRestartRules.UpdateUnavailableNotice(AgentProvider.Codex, "找不到 Codex", queuePaused: true));
        State(h, id).Should().Be("failed");
        h.Queue(id).Should().Equal(new[] { "排隊的" }, "佇列暫停");
        CliView(h, AgentProvider.Codex).Value<int>("restartPending").Should().Be(0);
    }

    [Fact]
    public async Task 屏障_停下途中取消又開始下一次更新_停下回來留著等那一次的UpdateFinished()
    {
        using var h = await NewHarnessAsync();
        var (id, s, gate, prepare) = await StopInFlightAfterOverdueAsync(h);
        h.Command(h.Main, "cliCancelUpdate", new { provider = "Codex" });
        await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        h.Cli.UpdateParticipant!.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, CodexOld));
        await h.Ui.DrainAsync();

        // 停下回來之前又按了更新：新的屏障接手這一輪
        var second = h.Cli.UpdateParticipant!.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        second.IsCompleted.Should().BeFalse("停下還沒有結果");
        gate.SetResult();
        await h.Ui.DrainAsync();

        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(new CliUpdatePreparation(Ready: true, ClosedIdle: 0, Held: 1, Busy: 0));
        s.Restarts.Count.Should().Be(1, "上一次（已取消）的結果不再用：等這一次更新完");
        State(h, id).Should().Be("waitingUpdate");

        await FinishUpdateAsync(h, AgentProvider.Codex, CliUpdateOutcome.Updated, CodexNew);
        s.Restarts.All[^1].Settings!.Cli.Should().Be(CodexNew);
        State(h, id).Should().Be("continued");
    }

    [Fact]
    public async Task 立即套用_背景跑的是高風險指令_同前景的一樣不自動接續_一般的背景工作照常接續()
    {
        using var h = await NewHarnessAsync();
        // run_in_background 的 npm publish：工具結果一回來（「Command running in background…」）卡片就是完成，背景工作還活著
        var (riskyId, risky) = StartRunning(h, h.Claude, "p1");
        risky.SucceedRestarts("r");
        h.Raise(risky, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm publish", "npm publish"));
        h.Raise(risky, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "Command running in background with ID: b1", Array.Empty<string>()));
        risky.HasLiveBackgroundWork = true;
        risky.LiveBackgroundTools = new[] { new AgentBackgroundTool("執行指令", "bash", "npm publish", "npm publish") };
        h.Raise(risky, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));
        var (plainId, plain) = StartRunning(h, h.Claude, "q1");
        plain.SucceedRestarts("n");
        plain.HasLiveBackgroundWork = true;
        plain.LiveBackgroundTools = new[] { new AgentBackgroundTool("執行指令", "bash", "npm run dev", "npm run dev") };
        h.Raise(plain, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        State(h, riskyId).Should().Be("waitingBackground");

        h.Command(h.Main, "cliApplyNow", new { provider = "Claude" });

        risky.Restarts.All.Should().ContainSingle().Which.AutoContinue.Should().BeFalse("背景跑的 npm publish 被停掉、結果未知：同前景的高風險操作，不自動接續");
        h.IsRunning(riskyId).Should().BeFalse();
        var header = (JObject)HeaderRestart(h, riskyId);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();
        h.Items(riskyId).Last().Text.Should().Contain("〈npm publish〉");
        var plainRequest = plain.Restarts.All.Should().ContainSingle().Subject;
        plainRequest.AutoContinue.Should().BeTrue("一般的背景工作（dev server）照常接續");
        plainRequest.ContinuationText.Should().Contain("背景工作已停止，結果未知").And.Contain("不可重複的操作").And.NotContain("需要的話重跑");
        State(h, plainId).Should().Be("continued");
    }

    [Fact]
    public async Task 屏障現在中斷_背景跑的高風險指令被停下_更新後也不自動接續()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "p1");
        s.SucceedRestarts("r");
        s.HasLiveBackgroundWork = true;
        s.LiveBackgroundTools = new[] { new AgentBackgroundTool("執行指令", "bash", "npm run deploy:prod", "部署\n\nnpm run deploy:prod") };
        h.Raise(s, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));
        var participant = h.Cli.UpdateParticipant!;
        var prepare = participant.PrepareForUpdateAsync(AgentProvider.Claude, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();
        State(h, id).Should().Be("waitingBackground");

        h.Command(h.Main, "cliApplyNow", new { provider = "Claude" });

        s.Restarts.All.Should().ContainSingle().Which.Settings.Should().BeNull("現在中斷：停下等更新");
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        await FinishUpdateAsync(h, AgentProvider.Claude, CliUpdateOutcome.Updated, ClaudeNew);

        s.Restarts.All[^1].AutoContinue.Should().BeFalse("停下時記下的背景高風險操作，更新後接續時照樣不自動接續");
        ((JObject)HeaderRestart(h, id)).Value<string>("state").Should().Be("notContinued");
    }

    [Fact]
    public async Task 政策_下次送出_背景工作還在跑時沿用舊程序_結束後的第一次送出才換新版()
    {
        using var h = await NewHarnessAsync();
        h.Command(h.Main, "setting", new { key = "cliRestartPolicy", value = "NextSend" });
        var (id, s) = StartRunning(h, h.Claude, "p1");
        h.CompleteTurn(s);
        s.HasLiveBackgroundWork = true;   // run_in_background 的 dev server 還在跑
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        s.Closes.Should().Be(0);

        h.Send(h.Main, id, "背景工作還在跑時送出");
        s.Sent.All[^1].Settings.Cli.Should().Be(ClaudeOld, "身分不同會話就會重開程序、把背景工作一起殺掉：先沿用舊程序（同預設的安全點政策）");

        h.CompleteTurn(s);
        s.HasLiveBackgroundWork = false;
        h.Raise(s, new AgentSafePointChangedEvent(false, false, false));
        h.Send(h.Main, id, "背景工作結束後送出");
        s.Sent.All[^1].Settings.Cli.Should().Be(ClaudeNew, "背景工作結束後的第一次送出才換新版");
    }

    [Fact]
    public async Task 屏障_草稿的第一則被延後後關掉分頁_對話與排隊的訊息都還在_更新結束後照常送出()
    {
        using var h = await NewHarnessAsync();
        var participant = h.Cli.UpdateParticipant!;
        (await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var draft = h.NewConversation(h.Main, h.Codex);

        h.Send(h.Main, draft, "草稿的第一則");
        h.Command(h.Main, "close", new { conversationId = draft });   // 看到「已排進佇列」就關掉分頁

        h.Conversation(draft).Should().NotBeNull("排隊的訊息不能被當成沒送出的草稿丟掉");
        h.Store.LoadConversations().Should().Contain(c => c.Id == draft, "延後時就轉成正式對話並存檔（出現在對話清單）");
        Directory.Exists(h.Store.GetConversationDirectory(draft)).Should().BeTrue("對話資料夾（附件）沒有被刪");
        h.Queue(draft).Should().Equal("草稿的第一則");
        h.Main.Messages("sendRejected").Should().BeEmpty();

        await FinishUpdateAsync(h, AgentProvider.Codex, CliUpdateOutcome.Updated, CodexNew);

        var session = h.Sessions.Should().ContainSingle().Subject;
        session.Sent.All.Should().ContainSingle().Which.Input.Text.Should().Be("草稿的第一則");
        h.Conversation(draft)!.Title.Should().Be("草稿的第一則");
    }

    [Fact]
    public async Task 非同步提問的答案被延後_取消那則排隊訊息時卡片回到可作答()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        h.Raise(s, new AgentQuestionEvent("async:m1", new[] { new AgentQuestion { Id = "m1:0", Text = "要哪種格式？" } }, AnswerAsMessage: true));
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);

        // 執行中作答（排進佇列），這一輪結束時安裝正在變動：接著送的答案被延後
        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "Excel" } } });
        await h.PublishCliAsync(new CliVersionChange(AgentProvider.Codex, CodexOld, null, CliChangeOrigin.External, Stable: false, InstallBroken: false, "npm 正在換檔"));
        h.CompleteTurn(s);
        s.Sent.Count.Should().Be(1, "安裝正在變動：答案延後");
        var queued = h.Main.Last("composer", id)!["composer"]!["queue"]!.Single().Value<string>("id");
        h.Command(h.Main, "cancelQueued", new { conversationId = id, id = queued });
        question.Status.Should().Be(AgentItemStatus.Waiting, "代理沒收到答案：卡片要回到可作答");
        question.Decision.Should().BeNull();
        question.Answers.Should().BeEmpty();

        // 閒置時作答、遇到更新屏障：一樣延後，取消時一樣回到可作答
        await h.SwitchCliAsync(AgentProvider.Codex, CodexOld, CodexOld);
        var participant = h.Cli.UpdateParticipant!;
        (await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "CSV" } } });
        question.Status.Should().Be(AgentItemStatus.Done);
        queued = h.Main.Last("composer", id)!["composer"]!["queue"]!.Single().Value<string>("id");
        h.Command(h.Main, "cancelQueued", new { conversationId = id, id = queued });
        question.Status.Should().Be(AgentItemStatus.Waiting);

        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, CodexOld));
        await h.Ui.DrainAsync();
        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "CSV" } } });
        s.Sent.All[^1].Input.Text.Should().Be("我的回答：\n要哪種格式？：CSV", "更新結束後重新作答照常送出");
    }

    // ───── 協定欄位 ─────

    [Fact]
    public async Task 協定欄位_CLI卡_標頭_項目與設定()
    {
        using var h = await NewHarnessAsync();
        h.Command(h.Main, "ready", new { mode = "main" });
        var init = h.Main.Last("init")!;
        init["settings"]!.Value<string>("cliRestartPolicy").Should().Be("SafePoint");
        var cli = (JObject)init["cli"]!["Claude"]!;
        foreach (var key in new[] { "changing", "installBroken", "lastChange", "update", "restartPolicy", "runningOnOldVersion", "restartPending", "restartWaiting", "restartDone" })
            cli.ContainsKey(key).Should().BeTrue(key);
        IsNull(cli["lastChange"]).Should().BeTrue("啟動時第一次讀到的身分不算換版");
        IsNull(cli["restartDone"]).Should().BeTrue("沒有換版過");
        IsNull(cli["update"]).Should().BeTrue();
        cli["restartWaiting"]!.Should().BeEmpty();

        var (id, s) = StartRunning(h, h.Claude, "p1");
        var conversation = h.Main.Last("conversation", id)!;
        conversation["header"]!["restart"].Should().NotBeNull("標頭一定有 restart 欄位");
        IsNull(conversation["header"]!["restart"]).Should().BeTrue();

        s.SafePoint = true;
        s.SucceedRestarts();
        var mark = h.Main.Mark();
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);

        var notice = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("noticeKind") == "cliUpdate", mark, conversationId: id);
        notice["item"]!.Value<string>("kind").Should().Be("Notice");
        notice["item"]!.Value<string>("html").Should().Contain("已用 Claude Code 2.1.297 自動接續");
        var header = await h.Main.WaitForAsync("header", m => m["header"]!["restart"] is JObject, mark, conversationId: id);
        header["header"]!["restart"]!.Value<string>("state").Should().Be("continued");
        var cliMessage = await h.Main.WaitForAsync("cli", m => m["cli"]!["Claude"]!["restartDone"] is JObject, mark);
        cliMessage["cli"]!["Claude"]!["restartDone"]!.Value<int>("continued").Should().Be(1);

        h.Raise(s, new AgentNoticeEvent("一般提示", IsError: false));
        var plain = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("html")!.Contains("一般提示"), mark, conversationId: id);
        ((JObject)plain["item"]!).ContainsKey("noticeKind").Should().BeFalse("一般提示省略 noticeKind");
    }
}
