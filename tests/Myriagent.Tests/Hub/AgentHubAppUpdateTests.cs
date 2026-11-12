using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>app 自我更新的排空（設計 9.2）：重用 CLI 更新屏障，兩種代理一起排空；等你回應視為安全點、背景工作最多等 30 分鐘、放棄時就地接續</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubAppUpdateTests
{
    private static readonly CliBinaryIdentity ClaudeOld = CliIdentities.Claude("2.1.295", fileId: 1);
    private static readonly CliBinaryIdentity CodexOld = CliIdentities.Codex("0.160.1", fileId: 1);

    private const string AppUpdateDetail = "等待 Clode Myriagent 更新，更新後自動接續…";

    private static async Task<HubHarness> NewHarnessAsync()
    {
        var h = new HubHarness();
        await h.SwitchCliAsync(AgentProvider.Claude, null, ClaudeOld);
        await h.SwitchCliAsync(AgentProvider.Codex, null, CodexOld);
        h.Ui.Invoke(() => h.Hub.BackgroundSettle = TimeSpan.Zero);
        return h;
    }

    private static (string Id, FakeSession Session) StartRunning(HubHarness h, AgentAccount account, string instance)
    {
        var (id, s) = h.StartConversation(h.Main, account, "做事");
        h.ProcessStarted(s, instance, account.Provider == AgentProvider.Claude ? ClaudeOld : CodexOld);
        return (id, s);
    }

    private static void RunTool(HubHarness h, FakeSession s, string command)
    {
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", command, command));
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
    }

    private static Task<AppUpdatePreparation> Prepare(HubHarness h) => h.OnUi(() => h.Hub.PrepareForAppUpdateAsync(CancellationToken.None));

    private static JToken HeaderRestart(HubHarness h, string id) => h.OnUi(() => h.Hub.RenderHeaderRestartForTest(id));

    private static string? State(HubHarness h, string id) => HeaderRestart(h, id) is JObject o ? o.Value<string>("state") : null;

    private static JObject CliView(HubHarness h, AgentProvider provider) => h.OnUi(() => h.Hub.RenderCliViewForTest(provider));

    private static bool IsNull(JToken? token) => token == null || token.Type == JTokenType.Null || token is JValue { Value: null };

    private static void SetClock(HubHarness h, Func<DateTime> clock) => h.Ui.Invoke(() => h.Hub.RestartClock = clock);

    [Fact]
    public async Task 兩種代理一起排空_閒置關掉_安全點停下_工具執行中的等_全部停下才Ready()
    {
        using var h = await NewHarnessAsync();
        var (idleId, idle) = StartRunning(h, h.Codex, "a1");
        h.CompleteTurn(idle);
        var (safeId, safe) = StartRunning(h, h.Claude, "b1");
        safe.SafePoint = true;
        safe.SucceedRestarts("b");
        var (busyId, busy) = StartRunning(h, h.Codex, "c1");
        busy.SucceedRestarts("c");
        RunTool(h, busy, "npm test");
        int progress = 0;
        h.Ui.Invoke(() => h.Hub.AppDrainProgress += () => progress++);
        var mark = h.Main.Mark();

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();

        prepare.IsCompleted.Should().BeFalse("還有對話在執行工具");
        idle.Closes.Should().Be(1, "閒置的立刻關");
        var stop = safe.Restarts.All.Should().ContainSingle().Subject;
        stop.Settings.Should().BeNull("停下、不啟動新程序：由新版 app 接續");
        stop.Reason.Should().Be(AgentRestartReason.AppUpdate);
        busy.Restarts.Count.Should().Be(0, "工具在執行：等它結束");
        h.IsRunning(safeId).Should().BeTrue("停下等更新不是「已停止」");
        var safeHeader = (JObject)HeaderRestart(h, safeId);
        safeHeader.Value<string>("state").Should().Be("appUpdate");
        safeHeader.Value<string>("detail").Should().Be(AppUpdateDetail);
        safeHeader.Value<bool>("canApplyNow").Should().BeFalse();
        IsNull(safeHeader["from"]).Should().BeTrue("標頭的版本箭頭是 CLI 的，app 更新不帶");
        IsNull(safeHeader["to"]).Should().BeTrue();
        var safeMessage = await h.Main.WaitForAsync("header", m => m["header"]!["restart"] is JObject r && r.Value<string>("state") == "appUpdate", mark, conversationId: safeId);
        safeMessage["header"]!.Value<string>("status").Should().Be(AppUpdateDetail, "執行中的狀態文字也說是在等 app 更新，不是等 CLI");
        var busyHeader = (JObject)HeaderRestart(h, busyId);
        busyHeader.Value<string>("state").Should().Be("waitingTool");
        IsNull(busyHeader["from"]).Should().BeTrue();
        busyHeader.Value<string>("detail").Should().Be("等目前的指令（npm test）完成後停下，讓 Clode Myriagent 更新");
        busyHeader.Value<bool>("canApplyNow").Should().BeFalse("app 更新由橫幅控制，不給逐一立即套用");
        IsNull(CliView(h, AgentProvider.Codex)["update"]).Should().BeTrue("CLI 卡不顯示 app 更新的排空");
        CliView(h, AgentProvider.Codex).Value<int>("restartPending").Should().Be(0);
        IsNull(CliView(h, AgentProvider.Codex)["restartDone"]).Should().BeTrue("關掉閒置程序是 app 更新做的，不算 CLI 的一波換版");
        progress.Should().BeGreaterThan(0);

        // 排空期間的送出排進佇列
        var toast = h.Main.Mark();
        h.Send(h.Main, idleId, "更新時送出");
        h.Queue(idleId).Should().Equal("更新時送出");
        h.Main.Messages("toast", toast).Should().Contain(m => m.Value<string>("text") == "Clode Myriagent 正在更新，訊息已排進佇列，更新完成後自動送出");
        State(h, idleId).Should().Be("appUpdate", "閒置對話延後的送出也是在等 app 更新");

        h.Raise(busy, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        busy.SafePoint = true;
        h.Raise(busy, new AgentSafePointChangedEvent(false, false, false));
        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Should().Be(new AppUpdatePreparation(Ready: true, Closed: 1, Held: 2, Busy: 0));
    }

    [Fact]
    public async Task 等你回應的對話視為安全點_立刻停下_舊卡失效()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "q1");
        s.SucceedRestarts("q");
        h.Raise(s, new AgentApprovalEvent("r1", "執行指令", "bash", "git status", null, "t1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(false, WaitingForUser: true, false));
        var card = h.Items(id).Single(i => i.RequestKey == "r1");

        var preparation = await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5));

        preparation.Ready.Should().BeTrue();
        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.Settings.Should().BeNull();
        request.Mode.Should().Be(AgentRestartMode.Immediate, "等回應時 CLI 不在安全點，照立即套用停下");
        card.Status.Should().Be(AgentItemStatus.Cancelled);
        card.Output.Should().Be(CliRestartRules.StaleCardOutput);
        State(h, id).Should().Be("appUpdate");
    }

    [Fact]
    public async Task 等核准時被app更新停下_還沒核准的工具不算被中斷_高風險的也照常接續_工具卡寫沒有執行()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "q1");
        s.SucceedRestarts("q");
        // 真實的 Claude：完整的 assistant 區塊先建工具卡，can_use_tool 的核准接著到（同一個 tool_use_id）
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "git push origin main", "git push origin main"));
        h.Raise(s, new AgentApprovalEvent("r1", "執行指令", "bash", "git push origin main", null, "t1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(false, WaitingForUser: true, false));
        s.RestartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        var request = s.Restarts.All.Should().ContainSingle().Subject;
        // 會話停下舊程序時把畫了卡、還沒有結果的工具結束為已取消（ClaudeStreamRules.InterruptOpenWork），比停下的結果先到
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Cancelled, ClaudeStreamRules.InterruptedToolOutput, Array.Empty<string>()));
        s.RestartGate.SetResult();
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();

        request.InterruptedTools.Should().BeEmpty("還沒核准的工具沒有執行，不是被中斷");
        request.ContinuationText.Should().Contain("重啟時你正在等使用者回應〈執行指令：git push origin main〉").And.NotContain("正在執行");
        var entry = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2")).Conversations.Single(e => e.Id == id);
        entry.Mode.Should().Be(ResumeMode.Continue, "沒有執行過的 git push 不是「結果未知」的高風險操作：接續後由代理重新提出（設計 9.2）");
        entry.WhyNot.Should().BeNull();
        entry.RiskOperation.Should().BeNull();
        entry.InterruptedTools.Should().BeEmpty();
        entry.PendingRequests.Should().ContainSingle().Which.Should().Contain("git push");
        var tool = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool);
        tool.Status.Should().Be(AgentItemStatus.Cancelled);
        tool.Output.Should().Be(CliRestartRules.NotApprovedToolOutput, "不能說一個沒執行過的指令「結果未知」");
    }

    [Fact]
    public async Task 等核准時被app更新停下_Codex沒有送工具結束_收尾時同樣寫沒有執行()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        // Codex：item/started commandExecution(inProgress) 與 requestApproval 同時到；停下時沒核准的指令不會有 item/completed（E-16）
        h.Raise(s, new AgentToolStartedEvent("item-1", "執行指令", "bash", "npm publish", "npm publish"));
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "npm publish", null, "item-1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: true, false));

        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();

        var entry = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2")).Conversations.Single(e => e.Id == id);
        entry.Mode.Should().Be(ResumeMode.Continue);
        entry.InterruptedTools.Should().BeEmpty();
        entry.PendingRequests.Should().ContainSingle().Which.Should().Contain("npm publish");
        var tool = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool);
        tool.Status.Should().Be(AgentItemStatus.Cancelled);
        tool.Output.Should().Be(CliRestartRules.NotApprovedToolOutput);
    }

    [Fact]
    public async Task 執行中的指令要求網路核准時被app更新停下_指令已經在跑_算被中斷_高風險不自動接續()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        h.Raise(s, new AgentToolStartedEvent("item-1", "執行指令", "bash", "git push origin main", "git push origin main"));
        // 受管網路的連線核准：指令已經開始執行，連線時才問（ToolStarted）
        h.Raise(s, new AgentApprovalEvent("0", "網路存取", "web", "連線到 github.com", null, "item-1", CanAllowForSession: true, ToolStarted: true));
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: true, false));

        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();

        var entry = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2")).Conversations.Single(e => e.Id == id);
        entry.InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("git push origin main");
        entry.Mode.Should().Be(ResumeMode.None, "跑到一半的 git push 結果未知");
        entry.WhyNot.Should().Be(CliNoContinueReason.RiskyTool);
        h.Items(id).Single(i => i.Kind == AgentItemKind.Tool).Output.Should().Contain("結果未知");
    }

    /// <summary>
    /// 「強制更新」（2026-10-28 使用者要求）：按下後滿 10 分鐘，還沒停下的對話——在等背景工作的、前景指令還在跑的——
    /// 一律照立即套用停下（起算點是按下的時間，不是開始排空的時間）；放棄這次就清掉
    /// </summary>
    [Fact]
    public async Task 強制更新_按下後滿10分鐘_等背景工作與前景指令的都照立即套用停下()
    {
        using var h = await NewHarnessAsync();
        var journal = new FakeJournal();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundJournal(journal));
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (bgId, bg) = StartRunning(h, h.Codex, "c1");
        bg.SucceedRestarts("c");
        bg.HasLiveBackgroundWork = true;
        h.Raise(bg, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));
        var (toolId, tool) = StartRunning(h, h.Claude, "a1");
        tool.SucceedRestarts("a");
        RunTool(h, tool, "npm run build");

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        State(h, bgId).Should().Be("waitingBackground");
        State(h, toolId).Should().Be("waitingTool");
        h.OnUi(() => h.Hub.AppUpdateForceAt).Should().BeNull("還沒按強制更新");

        now = start.AddMinutes(5);
        h.Ui.Invoke(() => h.Hub.ForceAppUpdate());
        h.OnUi(() => h.Hub.AppUpdateForceAt).Should().Be(now + AppUpdateRestartRules.ForceStopAfter);
        journal.Of("update.force").Should().ContainSingle();

        now = start.AddMinutes(14);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        bg.Restarts.Count.Should().Be(0, "按下後還沒滿 10 分鐘（排空已過 10 分鐘的寬限也只停等回應的）");
        tool.Restarts.Count.Should().Be(0);

        now = start.AddMinutes(15);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue("都停下了");
        bg.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.Immediate, "背景工作照立即套用停掉");
        tool.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.Immediate, "前景指令也不再等");
        journal.Of("restart.expired").Where(e => e.Value<bool?>("forced") == true).Should().HaveCount(2, "強制停下的記成 restart.expired（forced）");

        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("測試"));
        h.OnUi(() => h.Hub.AppUpdateForceAt).Should().BeNull("放棄這次就清掉");
    }

    [Fact]
    public async Task 背景工作最多等30分鐘_之後照立即套用停下()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        State(h, id).Should().Be("waitingBackground");
        ((JObject)HeaderRestart(h, id)).Value<long?>("since").Should().Be(AgentHub.ToUnixMs(start), "網頁從開始排空起顯示「已等 m:ss」，和 30 分鐘上限同一個起算點");

        now = start.AddMinutes(10);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        s.Restarts.Count.Should().Be(0, "以前 10 分鐘就停下；2026-10-24 起最多等 30 分鐘");
        ((JObject)HeaderRestart(h, id)).Value<long?>("since").Should().Be(AgentHub.ToUnixMs(start), "還在等：起算點不變（計時由網頁每秒更新）");
        now = start.AddMinutes(29);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        s.Restarts.Count.Should().Be(0, "還沒滿 30 分鐘");

        now = start.AddMinutes(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.Immediate);
    }

    [Fact]
    public async Task 背景活動日誌_記下排空等誰_等多久_逾時停掉了哪些背景工作()
    {
        // 2026-10-27 使用者要求：換版常被背景工作卡住或把它們中斷，先把經過記清楚（docs/background-journal.md）
        using var h = await NewHarnessAsync();
        var journal = new FakeJournal();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundJournal(journal));
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        var dev = new AgentBackgroundTool("執行指令", "bash", "npm run dev", "npm run dev", StopId: "dev1");
        s.HasLiveBackgroundWork = true;
        s.LiveBackgroundTools = [dev];
        h.Raise(s, new AgentBackgroundWorkEvent(1, [dev], ["dev1"]));
        h.CompleteTurn(s);
        h.Raise(s, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        now = start.AddMinutes(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        await h.Ui.DrainAsync();

        var drain = journal.Of("update.drain").Should().ContainSingle().Subject;
        var listed = ((JArray)drain["conversations"]!).Should().ContainSingle().Subject;
        listed.Value<string>("conv").Should().Be(id);
        listed["bg"]![0]!.Value<string>("title").Should().Be("npm run dev");
        journal.Of("barrier.begin").Select(e => e.Value<string>("provider")).Should().Contain("Codex");
        var wait = journal.Of("restart.wait").First(e => e.Value<string>("conv") == id);
        wait.Value<string>("state").Should().Be("WaitingBackground");
        wait.Value<bool>("appUpdate").Should().BeTrue();
        var expired = journal.Of("restart.expired").Should().ContainSingle().Subject;
        expired.Value<double>("waitedSec").Should().Be(1800);
        expired["bg"]![0]!.Value<string>("title").Should().Be("npm run dev");
        var call = journal.Of("restart.call").Should().ContainSingle().Subject;
        (call.Value<bool>("stopOnly"), call.Value<string>("mode"), call.Value<string>("reason")).Should().Be((true, "Immediate", "AppUpdate"));
        call.Value<bool>("hadBackground").Should().BeTrue();
        call["bg"]![0]!.Value<string>("title").Should().Be("npm run dev", "被停掉的背景工作");
        journal.Of("restart.result").Should().ContainSingle().Which.Value<string>("outcome").Should().NotBeNullOrEmpty();
        journal.Of("barrier.ready").Should().Contain(e => e.Value<string>("provider") == "Codex");
        var order = journal.Events("restart.");
        order.IndexOf("restart.expired").Should().BeLessThan(order.IndexOf("restart.call"));
    }

    [Fact]
    public async Task 讓出給模型的指令算背景工作_不當成在等的指令_排空逾時後前景指令一結束就停下()
    {
        // 2026-10-24：Codex 的 npm run dev 讓出給模型後一直跑，模型接著跑別的指令，標頭卻一直寫「等目前的指令（Copy-Item…）完成後停下」，
        // 排空超過 30 分鐘放棄更新，連續三次
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        h.Raise(s, new AgentToolStartedEvent("dev1", "執行指令", "bash", "npm run dev", "npm run dev"));
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(1, new[] { new AgentBackgroundTool("執行指令", "bash", "npm run dev", "npm run dev") }, new[] { "dev1" }));
        h.Raise(s, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingBackground", "卡片還在執行中，但模型沒有在等它");
        header.Value<string>("detail").Should().Be("等背景工作結束後停下，讓 Clode Myriagent 更新（最多等 30 分鐘）");

        h.Raise(s, new AgentToolStartedEvent("c2", "執行指令", "bash", "npm run build", "npm run build"));
        h.Raise(s, new AgentSafePointChangedEvent(true, false, HasLiveBackgroundWork: true));
        await h.Ui.DrainAsync();
        header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingTool");
        header.Value<string>("detail").Should().Be("等目前的指令（npm run build）完成後停下，讓 Clode Myriagent 更新", "在等的是前景的指令，不是讓出的 npm run dev");

        now = start.AddMinutes(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        s.Restarts.Count.Should().Be(0, "等背景工作滿上限只是不再等背景工作：前景的指令照樣等它結束");

        h.Raise(s, new AgentToolCompletedEvent("c2", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        h.Raise(s, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue("前景的指令一結束就停下，不必等維護剛好碰上兩個指令之間");
        s.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.Immediate);
    }

    [Fact]
    public async Task 放棄這次更新_已停下的以目前的CLI就地接續_還沒停下的照常跑()
    {
        using var h = await NewHarnessAsync();
        var (heldId, held) = StartRunning(h, h.Claude, "a1");
        held.SafePoint = true;
        held.SucceedRestarts("a");
        var (busyId, busy) = StartRunning(h, h.Codex, "b1");
        busy.SucceedRestarts("b");
        RunTool(h, busy, "npm test");
        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        State(h, heldId).Should().Be("appUpdate");

        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("排空超過 30 分鐘"));
        await h.Ui.DrainAsync();

        var result = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        result.Ready.Should().BeFalse();
        result.Reason.Should().Be("排空超過 30 分鐘");
        var resume = held.Restarts.All[1];
        resume.Settings!.Cli.Should().Be(ClaudeOld, "以目前的 CLI 重開");
        resume.AutoContinue.Should().BeTrue();
        resume.ContinuationText.Should().Contain("為了套用自身的更新而停下了 Claude Code");
        State(h, heldId).Should().Be("continued");
        var header = (JObject)HeaderRestart(h, heldId);
        IsNull(header["from"]).Should().BeTrue("CLI 沒有換版：不送「2.1.295 → 2.1.295」");
        IsNull(header["to"]).Should().BeTrue();
        header.Value<string>("detail").Should().Be("已放棄這次 Clode Myriagent 更新，已自動接續");
        h.Items(heldId).Where(i => i.NoticeKind == AgentNoticeKind.CliUpdate).Should().ContainSingle()
            .Which.Text.Should().StartWith("已放棄這次 Clode Myriagent 更新，已自動接續", "不是 CLI 換版，不說「已用 Claude Code 2.1.295 自動接續」");
        busy.Restarts.Count.Should().Be(0);
        IsNull(HeaderRestart(h, busyId)).Should().BeTrue();
        h.OnUi(() => h.Hub.AppUpdateDraining).Should().BeFalse();
        IsNull(CliView(h, AgentProvider.Claude)["restartDone"]).Should().BeTrue("就地接續不算 CLI 的一波換版");
    }

    [Fact]
    public async Task 排空中CLI要更新_回未就緒_它的UpdateFinished不影響app的屏障()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        RunTool(h, s, "npm test");
        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        var participant = h.Cli.UpdateParticipant!;

        var cli = await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));
        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, CodexOld));
        await h.Ui.DrainAsync();

        cli.Ready.Should().BeFalse();
        cli.Reason.Should().Be("Clode Myriagent 正在更新");
        h.OnUi(() => h.Hub.AppUpdateDraining).Should().BeTrue();
        State(h, id).Should().Be("waitingTool");
        prepare.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task CLI更新進行中_app更新不開始()
    {
        using var h = await NewHarnessAsync();
        var (_, s) = StartRunning(h, h.Codex, "c1");
        RunTool(h, s, "npm test");
        var participant = h.Cli.UpdateParticipant!;
        _ = participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();

        var preparation = await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5));

        preparation.Ready.Should().BeFalse();
        preparation.Reason.Should().Be("Codex CLI 正在更新");
        h.OnUi(() => h.Hub.AppUpdateDraining).Should().BeFalse();
        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, CodexOld));
        await h.Ui.DrainAsync();
    }

    [Fact]
    public async Task 同時停下達上限_等名額的對話也顯示等待app更新()
    {
        using var h = await NewHarnessAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new List<(string Id, FakeSession Session)>();
        for (int i = 0; i < 3; i++)
        {
            var (id, s) = StartRunning(h, h.Claude, $"s{i}");
            s.SafePoint = true;
            s.SucceedRestarts($"s{i}");
            s.RestartGate = gate;
            running.Add((id, s));
        }

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();

        running.Sum(r => r.Session.Restarts.Count).Should().Be(CliRestartRules.MaxConcurrentStarts(AgentProvider.Claude), "Claude 同時最多兩個在停下");
        foreach (var (id, _) in running)
        {
            var header = (JObject)HeaderRestart(h, id);
            header.Value<string>("state").Should().Be("appUpdate", "停下中與等名額的都是在等 app 更新");
            header.Value<string>("detail").Should().Be(AppUpdateDetail);
            header.Value<bool>("canApplyNow").Should().BeFalse();
        }
        prepare.IsCompleted.Should().BeFalse();

        gate.SetResult();
        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Should().Be(new AppUpdatePreparation(Ready: true, Closed: 0, Held: 3, Busy: 0));
        running.Should().OnlyContain(r => r.Session.Restarts.Count == 1);
    }

    [Fact]
    public async Task 排空停下的對話寫進resume_安全點的接續_等回應的記下請求_排空中送出的記成延後送出()
    {
        using var h = await NewHarnessAsync();
        var (safeId, safe) = StartRunning(h, h.Claude, "a1");
        safe.SafePoint = true;
        safe.SucceedRestarts("a");
        var (waitId, wait) = StartRunning(h, h.Codex, "b1");
        wait.SucceedRestarts("b");
        h.Raise(wait, new AgentApprovalEvent("r1", "執行指令", "bash", "git status", null, "t1", CanAllowForSession: false));
        h.Raise(wait, new AgentSafePointChangedEvent(false, WaitingForUser: true, false));
        var (idleId, idle) = StartRunning(h, h.Codex, "c1");
        h.CompleteTurn(idle);

        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        h.Send(h.Main, idleId, "排空後送出");
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

        file.Token.Should().Be("tok");
        file.Conversations.Select(e => e.Id).Should().BeEquivalentTo(new[] { safeId, waitId }, "閒置的不列");
        var safeEntry = file.Conversations.Single(e => e.Id == safeId);
        safeEntry.Mode.Should().Be(ResumeMode.Continue);
        safeEntry.Provider.Should().Be(AgentProvider.Claude);
        safeEntry.SessionId.Should().Be(h.Conversation(safeId)!.SessionId);
        safeEntry.CliVersion.Should().Be("2.1.295");
        safeEntry.StoppedAt.Should().NotBeNull();
        var waitEntry = file.Conversations.Single(e => e.Id == waitId);
        waitEntry.Mode.Should().Be(ResumeMode.Continue);
        waitEntry.PendingRequests.Should().ContainSingle().Which.Should().Contain("git status");
        file.DeferredSends.Should().Equal(idleId);
    }

    [Fact]
    public async Task 換版後以token接續_隱藏送出接續訊息_延後的送出也送出_全部開始後通知()
    {
        using var h = await NewHarnessAsync();
        var (heldId, held) = StartRunning(h, h.Codex, "a1");
        held.SafePoint = true;
        held.SucceedRestarts("a");
        var (idleId, idle) = StartRunning(h, h.Codex, "b1");
        h.CompleteTurn(idle);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        h.Send(h.Main, idleId, "排空時送出");
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

        await h.RestartAsync();
        AppResumeSummary? summary = null;
        int ready = 0;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += s => summary = s;
            h.Hub.MainViewReady += () => ready++;
            h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, "tok", DateTime.UtcNow), "0.2.2", RolledBack: false));
        });
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => summary != null, "接續都已送出");

        ready.Should().Be(1);
        summary!.Should().Be(new AppResumeSummary(Continued: 1, NotContinued: 0, DeferredSent: 1, RolledBack: false, AppTo: "0.2.2"));
        h.IsRunning(heldId).Should().BeTrue();
        var sent = h.Sessions.SelectMany(s => s.Sent.All).Select(x => x.Input.Text).ToList();
        sent.Should().Contain(t => t.StartsWith("（系統通知：Clode Myriagent 已由 0.2.1 更新為 0.2.2 並重新啟動（Codex 也跟著重新啟動）"));
        sent.Should().Contain("排空時送出");
        var notice = h.Items(heldId).Last();
        notice.Kind.Should().Be(AgentItemKind.Notice);
        notice.NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
        notice.Text.Should().Be("Clode Myriagent 已更新為 0.2.2，已請代理從中斷處繼續");
        h.Queue(idleId).Should().BeEmpty();

        // 只執行一次：網頁重新載入不會再接續
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();
        h.Sessions.SelectMany(s => s.Sent.All).Count().Should().Be(sent.Count);
        ready.Should().Be(2);
    }

    [Fact]
    public async Task 一般啟動讀到上次排空留下的resume_全部顯示繼續按鈕()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "a1");
        s.SafePoint = true;
        s.SucceedRestarts("a");
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok-old", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

        // 排空後、換版前程式結束（當掉、被工作管理員結束）：下次一般啟動沒有 token
        await h.RestartAsync();
        var validity = ResumeIntentRules.Validate(file, token: null, DateTime.UtcNow);
        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, validity, "0.2.1", RolledBack: false)));
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();

        validity.Should().Be(ResumeValidity.TokenMismatch);
        h.IsRunning(id).Should().BeFalse();
        h.Sessions.Count.Should().Be(1, "沒有自動送出");
        var notice = h.Items(id).Last();
        notice.NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
        notice.Text.Should().StartWith("上次 app 更新沒有完成");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();

        h.Command(h.Main, "cliContinue", new { conversationId = id });

        var text = h.Sessions[^1].Sent.All.Should().ContainSingle().Subject.Input.Text;
        text.Should().StartWith("（系統通知：Clode Myriagent 為了套用自身的更新而停下了 Claude Code，現在重新啟動了它");
        text.Should().Contain("使用者看過狀況後按了「繼續」");
        h.Items(id).Last().Text.Should().Be(ResumeIntentRules.ManualContinueNotice);
        h.Items(id).Last().NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
    }

    [Fact]
    public async Task app更新後沒有接續_還沒按繼續app又重新啟動_繼續按鈕還在_按了說明是app更新()
    {
        // 2026-11-12：標頭的「繼續」以前只在記憶體（resume.json 用過就刪、無法重建），再套用一版或重新開機就沒了
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "a1");
        s.SafePoint = true;
        s.SucceedRestarts("a");
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok-old", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
        await h.RestartAsync();
        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, token: null, DateTime.UtcNow), "0.2.1", RolledBack: false)));
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();
        ((JObject)HeaderRestart(h, id)).Value<bool>("canContinue").Should().BeTrue("前提：給了「繼續」");
        var items = h.Items(id).Count;
        var sessions = h.Sessions.Count;

        await h.RestartAsync();   // 這次沒有 resume.json
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();

        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();
        h.Items(id).Should().HaveCount(items, "提示只加一次");
        h.Sessions.Count.Should().Be(sessions, "還原按鈕不送任何東西");

        h.Command(h.Main, "cliContinue", new { conversationId = id });

        var text = h.Sessions[^1].Sent.All.Should().ContainSingle().Subject.Input.Text;
        text.Should().StartWith("（系統通知：Clode Myriagent 為了套用自身的更新而停下了 Claude Code，現在重新啟動了它");
        text.Should().Contain("使用者看過狀況後按了「繼續」");
        h.Items(id).Last().Text.Should().Be(ResumeIntentRules.ManualContinueNotice);
        File.Exists(ContinueOfferStore.PathFor(h.Store.GetConversationDirectory(id))).Should().BeFalse("按了就拿掉記錄");
    }

    [Fact]
    public async Task app更新後沒有接續_刪掉別的對話_別的對話換帳戶_移除別的帳戶_繼續按鈕仍在_只清受影響的()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "a1");
        s.SafePoint = true;
        s.SucceedRestarts("a");
        var (switchId, switching) = StartRunning(h, h.Claude, "b1");
        switching.SafePoint = true;
        switching.SucceedRestarts("b");
        var (codexId, codex) = StartRunning(h, h.Codex, "c1");
        codex.SafePoint = true;
        codex.SucceedRestarts("c");
        var (otherId, other) = StartRunning(h, h.Claude, "d1");
        h.CompleteTurn(other);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok-old", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

        // 排空後程式結束、一般啟動（token 不符）：三個停下的對話都沒有程序，只靠標頭的「繼續」
        await h.RestartAsync();
        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, token: null, DateTime.UtcNow), "0.2.1", RolledBack: false)));
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();
        foreach (var held in new[] { id, switchId, codexId })
            State(h, held).Should().Be("notContinued");

        // 刪掉無關的對話、別的對話換帳戶、移除別的帳戶：只清受影響的對話（換了帳戶的、屬於被移除帳戶的）
        h.Command(h.Main, "delete", new { conversationId = otherId });
        h.Command(h.Main, "setAccount", new { conversationId = switchId, accountId = h.Claude2.Id });
        h.Command(h.Main, "accountRemove", new { accountId = h.Codex.Id, deleteData = false });
        await h.Ui.DrainAsync();

        IsNull(HeaderRestart(h, switchId)).Should().BeTrue("換了帳戶：原本的接續不適用");
        IsNull(HeaderRestart(h, codexId)).Should().BeTrue("帳戶已移除：不能接續");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();

        var before = h.Sessions.Count;
        h.Command(h.Main, "cliContinue", new { conversationId = id });

        h.Sessions.Count.Should().Be(before + 1, "按「繼續」才啟動 CLI");
        h.Sessions[^1].Sent.All.Should().ContainSingle().Subject.Input.Text
            .Should().StartWith("（系統通知：Clode Myriagent 為了套用自身的更新而停下了 Claude Code，現在重新啟動了它");
        h.Items(id).Last().Text.Should().Be(ResumeIntentRules.ManualContinueNotice);
        h.Items(id).Last().NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
    }

    [Fact]
    public async Task 退回原版後接續_提示說明新版啟動失敗()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "a1");
        s.SafePoint = true;
        s.SucceedRestarts("a");
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

        await h.RestartAsync();
        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeValidity.Ok, "0.2.1", RolledBack: true)));
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();

        h.Items(id).Last().Text.Should().Be("新版啟動失敗，Clode Myriagent 已退回 0.2.1，已請代理從中斷處繼續");
        h.Sessions[^1].Sent.All.Single().Input.Text.Should().StartWith("（系統通知：Clode Myriagent 為了套用自身的更新而停下了 Claude Code");
    }

    [Fact]
    public async Task 接續時CLI正在更新_不啟動CLI_改成沒有接續並給繼續按鈕()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "a1");
        s.SafePoint = true;
        s.SucceedRestarts("a");
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

        // 新版啟動時 Claude CLI 正在更新（更新屏障）：這時啟動 CLI 會失敗
        await h.RestartAsync();
        var participant = h.Cli.UpdateParticipant!;
        (await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Claude, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        AppResumeSummary? summary = null;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += x => summary = x;
            h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, "tok", DateTime.UtcNow), "0.2.2", RolledBack: false));
        });
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => summary != null, "接續都已處理");

        summary!.Should().Be(new AppResumeSummary(Continued: 0, NotContinued: 1, DeferredSent: 0, RolledBack: false, AppTo: "0.2.2"));
        h.Sessions.Count.Should().Be(1, "CLI 正在更新：不啟動 CLI");
        h.IsRunning(id).Should().BeFalse();
        var notice = h.Items(id).Last();
        notice.NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
        notice.Text.Should().StartWith("Clode Myriagent 已更新為 0.2.2，這個對話沒有自動接續");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();

        // CLI 更新結束後按「繼續」：接續訊息照樣說明是 app 更新
        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Claude, CliUpdateOutcome.Cancelled, ClaudeOld));
        await h.Ui.DrainAsync();
        h.Command(h.Main, "cliContinue", new { conversationId = id });

        h.Sessions[^1].Sent.All.Should().ContainSingle().Subject.Input.Text
            .Should().StartWith("（系統通知：Clode Myriagent 已由 0.2.1 更新為 0.2.2 並重新啟動（Claude Code 也跟著重新啟動）");
        h.Items(id).Last().Text.Should().Be(ResumeIntentRules.ManualContinueNotice);
    }

    /// <summary>設計 13 節「mode=none（使用者按過停止）不自動接續」的中樞層：停下等更新後按停止、還沒到安全點就按停止，兩種都在本機收尾</summary>
    [Fact]
    public async Task 使用者按過停止的對話_不寫進resume_新版以有效token啟動也不自動接續()
    {
        using var h = await NewHarnessAsync();
        var (heldId, held) = StartRunning(h, h.Claude, "a1");
        held.SafePoint = true;
        held.SucceedRestarts("a");
        var (busyId, busy) = StartRunning(h, h.Claude, "b1");
        busy.SucceedRestarts("b");
        RunTool(h, busy, "npm test");
        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        State(h, heldId).Should().Be("appUpdate");
        State(h, busyId).Should().Be("waitingTool");

        // 停下等 app 更新之後按停止：沒有程序、不會回報 TurnCompleted，在本機收尾
        h.Command(h.Main, "stop", new { conversationId = heldId });
        h.IsRunning(heldId).Should().BeFalse();
        h.Items(heldId).Last().Text.Should().Be("已停止這一輪");
        prepare.IsCompleted.Should().BeFalse("另一個對話還在執行工具");

        // 工具執行中、還沒到安全點就按停止：這一輪以 Interrupted 結束，閒置的程序照排空關掉
        h.Command(h.Main, "stop", new { conversationId = busyId });
        busy.Interrupts.Should().Be(1);
        h.CompleteTurn(busy, AgentTurnOutcome.Interrupted);
        h.IsRunning(busyId).Should().BeFalse();
        h.Items(busyId).Last().Text.Should().Be("已停止這一輪");

        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Should().Be(new AppUpdatePreparation(Ready: true, Closed: 1, Held: 0, Busy: 0), "按過停止的不算停下等新版接續");
        held.Restarts.Count.Should().Be(1, "只有排空時的停下：按停止後不重開");
        busy.Restarts.Count.Should().Be(0, "按過停止的這一輪不由屏障停下");
        busy.Closes.Should().Be(1);
        foreach (var id in new[] { heldId, busyId })
            IsNull(HeaderRestart(h, id)).Should().BeTrue("已在本機收尾：標頭不再顯示等待 app 更新");
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
        file.Conversations.Should().BeEmpty("使用者按過停止的這一輪已在本機收尾：不列進 resume.json，新版不會接續");
        file.DeferredSends.Should().BeEmpty();

        // 新版以有效的 token 啟動
        await h.RestartAsync();
        var sessions = h.Sessions.Count;
        var sent = h.Sessions.Sum(s => s.Sent.Count);
        AppResumeSummary? summary = null;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += x => summary = x;
            h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, "tok", DateTime.UtcNow), "0.2.2", RolledBack: false));
        });
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => summary != null, "接續都已處理");

        summary!.Should().Be(new AppResumeSummary(Continued: 0, NotContinued: 0, DeferredSent: 0, RolledBack: false, AppTo: "0.2.2"));
        h.Sessions.Count.Should().Be(sessions, "沒有啟動 CLI");
        h.Sessions.Sum(s => s.Sent.Count).Should().Be(sent, "沒有送出任何訊息");
        foreach (var id in new[] { heldId, busyId })
        {
            h.IsRunning(id).Should().BeFalse();
            IsNull(HeaderRestart(h, id)).Should().BeTrue("沒有「沒有接續」的狀態，也沒有「繼續」按鈕");
            h.Items(id).Last().Text.Should().Be("已停止這一輪", "不加 app 更新的提示");
        }
    }

    /// <summary>resume.json 列出使用者按過停止的對話（別的版本寫的，或日後改成列出）：token 有效也不送出，提示說明原因、不給「繼續」</summary>
    [Fact]
    public async Task resume列出mode_none使用者按過停止的對話_有效token也不送出_提示你按了停止_不給繼續()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "a1");
        h.CompleteTurn(s);
        var file = new AppResumeFile
        {
            Token = "tok", From = "0.2.1-aaaaaa1", To = "0.2.2-bbbbbb2", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        file.Conversations.Add(new ResumeConversation
        {
            Id = id,
            Provider = AgentProvider.Claude,
            AccountId = h.Claude.Id,
            SessionId = h.Conversation(id)!.SessionId,
            Mode = ResumeMode.None,
            WhyNot = CliNoContinueReason.UserStopped,
            StoppedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CliVersion = "2.1.295",
        });

        await h.RestartAsync();
        var sessions = h.Sessions.Count;
        var validity = ResumeIntentRules.Validate(file, "tok", DateTime.UtcNow);
        validity.Should().Be(ResumeValidity.Ok);
        AppResumeSummary? summary = null;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += x => summary = x;
            h.Hub.ScheduleAppResume(new PendingAppResume(file, validity, "0.2.2", RolledBack: false));
        });
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => summary != null, "接續都已處理");

        summary!.Should().Be(new AppResumeSummary(Continued: 0, NotContinued: 1, DeferredSent: 0, RolledBack: false, AppTo: "0.2.2"));
        h.Sessions.Count.Should().Be(sessions, "不啟動 CLI");
        s.Sent.Count.Should().Be(1, "只有排空前的第一則");
        h.IsRunning(id).Should().BeFalse();
        var notice = h.Items(id).Last();
        notice.Kind.Should().Be(AgentItemKind.Notice);
        notice.NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
        notice.Text.Should().Be("Clode Myriagent 已更新為 0.2.2，這個對話沒有自動接續：你按了停止");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeFalse("使用者自己按了停止：不給「繼續」");

        // 網頁舊畫面還留著按鈕時按下：不接續
        h.Command(h.Main, "cliContinue", new { conversationId = id });
        h.Sessions.Count.Should().Be(sessions);
        h.IsRunning(id).Should().BeFalse();
    }

    [Fact]
    public async Task 草稿快照_收齊主視窗與彈出視窗的回覆()
    {
        using var h = await NewHarnessAsync();
        var (id1, s1) = StartRunning(h, h.Claude, "a1");
        h.CompleteTurn(s1);
        var (id2, s2) = StartRunning(h, h.Codex, "b1");
        h.CompleteTurn(s2);
        var popout = h.AddPopout("pop1", id2);
        var mark = h.Main.Mark();

        var task = h.OnUi(() => h.Hub.RequestDraftSnapshotAsync(TimeSpan.FromSeconds(2)));
        var request = await h.Main.WaitForAsync("draftSnapshotRequest", since: mark);
        var requestId = request.Value<string>("requestId");
        popout.Messages("draftSnapshotRequest").Should().ContainSingle();
        h.Command(h.Main, "draftSnapshot", new { requestId, drafts = new[] { new { conversationId = id1, text = "主視窗的字" } } });
        h.Command(popout, "draftSnapshot", new { requestId, drafts = new[] { new { conversationId = id2, text = "彈出視窗的字" } } });

        var drafts = await task.WaitAsync(TimeSpan.FromSeconds(5));
        drafts.Should().BeEquivalentTo(new[] { new ResumeDraft(id1, "主視窗的字"), new ResumeDraft(id2, "彈出視窗的字") });
    }

    [Fact]
    public async Task 草稿快照_網頁沒回應就在時限後回空的()
    {
        using var h = await NewHarnessAsync();
        var drafts = await h.OnUi(() => h.Hub.RequestDraftSnapshotAsync(TimeSpan.FromMilliseconds(100))).WaitAsync(TimeSpan.FromSeconds(5));
        drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task 草稿跨重啟保存_輸入框文字與還沒送出的新對話含附件()
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Claude, "a1");
        h.CompleteTurn(s);
        var draftId = h.NewConversation(h.Main, h.Codex);
        var emptyDraftId = h.NewConversation(h.Main, h.Claude);
        var ui = h.OnUi(() => h.Hub.BuildResumeUi(new List<ResumeDraft> { new(id, "還沒送的字"), new(draftId, "新對話的第一則") }));

        ui.Drafts.Should().Equal(new ResumeDraft(id, "還沒送的字"));
        var newConversation = ui.NewConversations.Should().ContainSingle("沒有字也沒有附件的草稿不保留").Subject;
        newConversation.DraftId.Should().Be(draftId);
        newConversation.AccountId.Should().Be(h.Codex.Id);
        newConversation.Text.Should().Be("新對話的第一則");
        emptyDraftId.Should().NotBe(draftId);

        // 附件：放在草稿自己的資料夾（同匯入流程複製的位置）
        var stored = h.Temp.WriteFile(Path.Combine("conversations", draftId, "attachments", "a.txt"), "附件內容");
        var attachment = new AgentAttachment { FileName = "a.txt", StoredPath = stored, Kind = AgentAttachmentKind.Document };
        var file = new AppResumeFile
        {
            Token = "tok", From = "0.2.1-aaaaaa1", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ui = new ResumeUi { Drafts = ui.Drafts, NewConversations = { newConversation with { Attachments = new List<AgentAttachment> { attachment } } } },
        };

        await h.RestartAsync(keepDrafts: new HashSet<string> { draftId });
        File.Exists(stored).Should().BeTrue("列在 resume.json 的草稿，結束時不刪它的資料夾");

        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeValidity.Ok, "0.2.2", RolledBack: false)));
        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");
        var restored = await h.Main.WaitForAsync("conversation", m => m.Value<string>("conversationId") == draftId, since: mark);
        restored.Value<string>("draft").Should().Be("新對話的第一則");
        ((JArray)restored["composer"]!["attachments"]!).Should().ContainSingle().Which.Value<string>("name").Should().Be("a.txt");

        h.Command(h.Main, "open", new { conversationId = id });
        h.Main.Last("conversation", id)!.Value<string>("draft").Should().Be("還沒送的字");
        h.Command(h.Main, "open", new { conversationId = id });
        IsNull(h.Main.Last("conversation", id)!["draft"]).Should().BeTrue("只送一次");
    }

    /// <summary>刪草稿資料夾在背景執行緒：輪詢約 1 秒，確認檔案一直都在</summary>
    private static async Task FileStaysAsync(string path, string because)
    {
        for (int i = 0; i < 50; i++)
        {
            File.Exists(path).Should().BeTrue(because);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task 結束時保留的草稿_之後主視窗關閉也不刪資料夾()
    {
        using var h = await NewHarnessAsync();
        var draftId = h.NewConversation(h.Main, h.Codex);
        var stored = h.Temp.WriteFile(Path.Combine("conversations", draftId, "attachments", "a.txt"), "附件內容");

        await h.Ui.Invoke(() => h.Hub.ShutdownAsync(new HashSet<string> { draftId }));
        File.Exists(stored).Should().BeTrue("列在 keepDrafts 的草稿，結束時不刪它的資料夾");
        // app 更新的結束流程：ShutdownAsync 之後 ExitImmediately → mainForm.Close → View.Detach → DetachView
        h.Ui.Invoke(() => h.Hub.DetachView(h.Main.Id));

        await FileStaysAsync(stored, "中樞已關閉：之後視窗關閉不再丟棄草稿（新版要還原它的附件）");
    }

    [Fact]
    public async Task 結束途中主視窗就關閉_保留的草稿也不刪資料夾()
    {
        using var h = await NewHarnessAsync();
        var (_, s) = StartRunning(h, h.Claude, "a1");
        h.CompleteTurn(s);
        s.CloseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draftId = h.NewConversation(h.Main, h.Codex);
        var stored = h.Temp.WriteFile(Path.Combine("conversations", draftId, "attachments", "a.txt"), "附件內容");

        var shutdown = h.OnUi(() => h.Hub.ShutdownAsync(new HashSet<string> { draftId }));
        await h.Ui.DrainAsync();
        shutdown.IsCompleted.Should().BeFalse("代理程序還沒結束");
        // 協調器等 ShutdownAsync 8 秒就不等了：ExitImmediately 在中樞還沒走到刪草稿那段時就關主視窗
        h.Ui.Invoke(() => h.Hub.DetachView(h.Main.Id));

        await FileStaysAsync(stored, "中樞關閉途中：視窗關閉不丟棄草稿，刪不刪由 ShutdownAsync 的 keepDrafts 決定");
        s.CloseGate.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        File.Exists(stored).Should().BeTrue("列在 keepDrafts 的草稿，結束時不刪它的資料夾");
    }

    [Fact]
    public async Task 草稿對話還原_資料夾照原樣_不改上次使用的帳戶_不是草稿資料夾的id略過()
    {
        using var h = await NewHarnessAsync();
        var recent = h.Temp.CreateDirectory("repo");
        h.Ui.Invoke(() =>
        {
            h.Settings.RecentFolders = new List<string> { recent };
            h.Settings.LastAccountId = h.Claude.Id;
        });
        await h.RestartAsync();   // 新的中樞從設定讀最近使用的資料夾
        var draftId = Guid.NewGuid().ToString("N");
        var file = new AppResumeFile
        {
            Token = "tok", From = "0.2.1-aaaaaa1", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ui = new ResumeUi
            {
                NewConversations =
                {
                    new ResumeNewConversation(draftId, h.Codex.Id, Folder: null, "用對話自己的工作區", new List<AgentAttachment>()),
                    new ResumeNewConversation("not-a-draft-id", h.Codex.Id, Folder: null, "不是草稿資料夾", new List<AgentAttachment>()),
                },
            },
        };

        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeValidity.Ok, "0.2.2", RolledBack: false)));
        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");
        await h.Main.WaitForAsync("conversation", m => m.Value<string>("conversationId") == draftId, since: mark);

        h.Conversation(draftId)!.WorkingDirectory.Should().BeNull("原本用對話自己的工作區，不改成最近使用的資料夾");
        h.OnUi(() => h.Settings.LastAccountId).Should().Be(h.Claude.Id, "還原草稿不是使用者選了帳戶");
        h.Conversation("not-a-draft-id").Should().BeNull("id 不是草稿資料夾的格式");
        h.Main.Messages("conversation", mark).Should().ContainSingle();
    }

    [Fact]
    public async Task 還原的草稿對話_init列出id_conversation在init之後送_沒還原的不列()
    {
        using var h = await NewHarnessAsync();
        var draftId = Guid.NewGuid().ToString("N");
        var file = new AppResumeFile
        {
            Token = "tok", From = "0.2.1-aaaaaa1", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ui = new ResumeUi
            {
                NewConversations =
                {
                    new ResumeNewConversation(draftId, h.Codex.Id, Folder: null, "新對話的第一則", new List<AgentAttachment>()),
                    new ResumeNewConversation(Guid.NewGuid().ToString("N"), "removed-account", Folder: null, "帳戶已移除", new List<AgentAttachment>()),
                    new ResumeNewConversation("not-a-draft-id", h.Codex.Id, Folder: null, "不是草稿資料夾", new List<AgentAttachment>()),
                },
            },
        };

        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeValidity.Ok, "0.2.2", RolledBack: false)));
        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");
        await h.Main.WaitForAsync("conversation", m => m.Value<string>("conversationId") == draftId, since: mark);

        // 網頁靠 init 的 restoredDrafts 照存下的版面把草稿放回原位、不另開空白的新對話：列出的要剛好是之後會送來的
        var after = h.Main.All().Skip(mark).ToList();
        int init = after.FindIndex(m => m.Value<string>("type") == "init");
        ((JArray)after[init]["restoredDrafts"]!).Select(t => t.Value<string>()).Should().Equal(new[] { draftId }, "帳戶已移除、id 不是草稿資料夾格式的不還原，也不列");
        after.FindIndex(m => m.Value<string>("type") == "conversation" && m.Value<string>("conversationId") == draftId)
            .Should().BeGreaterThan(init, "草稿對話的 conversation 在 init 之後送");

        // 網頁重新載入：沒有要還原的，空陣列
        var reload = h.Main.Mark();
        h.Command(h.Main, "ready");
        ((JArray)h.Main.Messages("init", reload).Should().ContainSingle().Subject["restoredDrafts"]!).Should().BeEmpty();
    }

    // ───── 第二批審查的跨任務修正（Task 10b）─────

    private static bool? IsUnread(JObject conversations, string id)
        => conversations["items"]!.FirstOrDefault(i => i.Value<string>("id") == id)?.Value<bool>("unread");

    private static bool IsFailureToast(JObject message) => message.Value<string>("text")?.StartsWith("操作失敗") == true;

    /// <summary>把 resume.json（JSON）改成含 null 的形狀：合法 JSON，但手改、損毀或別的版本寫的</summary>
    private static void BreakResume(JObject resume, string shape)
    {
        switch (shape)
        {
            case "DeferredSends:[null]":
                resume["DeferredSends"] = new JArray(JValue.CreateNull());
                break;
            case "Conversations:[null]":
                ((JArray)resume["Conversations"]!).Insert(0, JValue.CreateNull());
                break;
            case "Ui:null":
                resume["Ui"] = JValue.CreateNull();
                break;
            case "Ui.Drafts:[null]":
                resume["Ui"]!["Drafts"] = new JArray(JValue.CreateNull());
                break;
            case "NewConversations.Attachments:null":
                resume["Ui"]!["NewConversations"]![0]!["Attachments"] = JValue.CreateNull();
                break;
            case "RemoteControl:null":
                resume["RemoteControl"] = JValue.CreateNull();
                break;
            case "RemoteControl:[null]":
                resume["RemoteControl"] = new JArray(JValue.CreateNull(), " ");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    [Theory]
    [InlineData("DeferredSends:[null]")]
    [InlineData("Conversations:[null]")]
    [InlineData("Ui:null")]
    [InlineData("Ui.Drafts:[null]")]
    [InlineData("NewConversations.Attachments:null")]
    [InlineData("RemoteControl:null")]
    [InlineData("RemoteControl:[null]")]
    public async Task resume含null_送ready不丟例外_健康訊號照發_正常的對話照常接續(string shape)
    {
        using var h = await NewHarnessAsync();
        var (heldId, held) = StartRunning(h, h.Claude, "a1");
        held.SafePoint = true;
        held.SucceedRestarts("a");
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
        var draftId = Guid.NewGuid().ToString("N");
        file.Ui.NewConversations.Add(new ResumeNewConversation(draftId, h.Codex.Id, Folder: null, "新對話的第一則", new List<AgentAttachment>()));
        var json = JObject.Parse(JsonConvert.SerializeObject(file, JsonFile.Settings));
        BreakResume(json, shape);
        var path = ResumeFileStore.PathFor(h.Temp.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.ToString());

        await h.RestartAsync();
        var loaded = ResumeFileStore.Load(h.Temp.Root);
        loaded.Should().NotBeNull("合法 JSON：不當成損毀");
        AppResumeSummary? summary = null;
        int ready = 0;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += s => summary = s;
            h.Hub.MainViewReady += () => ready++;
            h.Hub.ScheduleAppResume(new PendingAppResume(loaded!, ResumeIntentRules.Validate(loaded, "tok", DateTime.UtcNow), "0.2.2", RolledBack: false));
        });
        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");

        ready.Should().Be(1, "健康訊號照發：好的新版不能因為 resume.json 被健康閘門判失敗而回滾");
        h.Main.Messages("toast", mark).Should().NotContain(m => IsFailureToast(m));
        await h.WaitUntilAsync(() => summary != null, "接續都已送出");
        summary!.Continued.Should().Be(1, "檔案裡正常的對話照常接續");
        h.IsRunning(heldId).Should().BeTrue();
        h.Items(heldId).Last().Text.Should().Be("Clode Myriagent 已更新為 0.2.2，已請代理從中斷處繼續");
        if (shape != "Ui:null")
            h.Conversation(draftId).Should().NotBeNull("正常的草稿對話照常還原");
    }

    [Fact]
    public async Task 接續處理到一半丟例外_健康訊號照發_已排入的接續照常_照樣通知接續已處理()
    {
        using var h = await NewHarnessAsync();
        var (heldId, held) = StartRunning(h, h.Claude, "a1");
        held.SafePoint = true;
        held.SucceedRestarts("a");
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
        // 沒經過讀檔的正規化（模擬接續流程裡沒料到的例外）：還原草稿在第一個項目就丟例外；對話清單在排入一個接續之後丟例外
        file.Ui.Drafts.Add(null!);
        file.Conversations.Add(null!);

        await h.RestartAsync();
        AppResumeSummary? summary = null;
        int ready = 0;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += s => summary = s;
            h.Hub.MainViewReady += () => ready++;
            h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeValidity.Ok, "0.2.2", RolledBack: false));
        });
        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");

        ready.Should().Be(1, "健康訊號照發");
        h.Main.Messages("toast", mark).Should().NotContain(m => IsFailureToast(m), "接續的錯誤只記 log，不往外丟");
        await h.WaitUntilAsync(() => summary != null, "照樣通知接續已處理（協調器刪 resume.json，下次一般啟動不再報一次）");
        summary!.Continued.Should().Be(1, "已排入的接續照常進行");
        h.IsRunning(heldId).Should().BeTrue();
        h.Items(heldId).Last().Text.Should().Be("Clode Myriagent 已更新為 0.2.2，已請代理從中斷處繼續");
    }

    [Fact]
    public async Task app更新後沒有接續_沒有視窗顯示的標未讀_有視窗顯示的不標()
    {
        using var h = await NewHarnessAsync();
        var (hiddenId, hidden) = StartRunning(h, h.Claude, "a1");
        hidden.SafePoint = true;
        hidden.SucceedRestarts("a");
        var (shownId, shown) = StartRunning(h, h.Codex, "b1");
        shown.SafePoint = true;
        shown.SucceedRestarts("b");
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok-old", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

        // 排空後程式結束、一般啟動（token 不符）：兩個對話都沒有自動接續。其中一個在主視窗就緒前就顯示在彈出視窗
        await h.RestartAsync();
        var popout = h.AddPopout("pop1", shownId);
        h.Command(popout, "open", new { conversationId = shownId });
        h.Command(popout, "visible", new { conversationIds = new[] { shownId } });
        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, token: null, DateTime.UtcNow), "0.2.1", RolledBack: false)));
        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");

        var list = await h.Main.WaitForAsync("conversations", m => IsUnread(m, hiddenId) == true, mark);
        IsUnread(list, shownId).Should().BeFalse("正顯示在視窗上：使用者看得到");
        State(h, hiddenId).Should().Be("notContinued");
        State(h, shownId).Should().Be("notContinued");
    }

    [Theory]
    [InlineData("cliCancelUpdate", false)]
    [InlineData("cliApplyNow", false)]
    [InlineData("cliApplyNow", true)]
    public async Task app排空中按CLI的取消更新或立即套用_忽略_app的排空與對話的plan不變(string cmd, bool byConversation)
    {
        using var h = await NewHarnessAsync();
        var (id, s) = StartRunning(h, h.Codex, "c1");
        s.SucceedRestarts("c");
        RunTool(h, s, "npm test");
        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        State(h, id).Should().Be("waitingTool");

        // 網頁舊畫面的短暫空檔按下：CLI 卡的按鈕（不指定對話），或對話標頭的「立即套用」（指定對話）
        if (byConversation)
            h.Command(h.Main, cmd, new { conversationId = id });
        else
            foreach (var provider in new[] { "Claude", "Codex" })
                h.Command(h.Main, cmd, new { provider });
        await h.Ui.DrainAsync();

        prepare.IsCompleted.Should().BeFalse("app 的排空不能被 CLI 的按鈕提前結束（取消更新：以未就緒結束；立即套用：不等安全點就停下）");
        s.Restarts.Count.Should().Be(0, "不能把對話改成立即停下");
        State(h, id).Should().Be("waitingTool");
        h.OnUi(() => h.Hub.AppUpdateDraining).Should().BeTrue();

        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        s.SafePoint = true;
        h.Raise(s, new AgentSafePointChangedEvent(false, false, false));
        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Should().Be(new AppUpdatePreparation(Ready: true, Closed: 0, Held: 1, Busy: 0));
        s.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.SafePoint, "照常到安全點才停下");
    }

    [Fact]
    public async Task 建立屏障途中丟例外_已建立的屏障收回_回未就緒並帶原因()
    {
        using var h = await NewHarnessAsync();
        var (claudeId, claude) = StartRunning(h, h.Claude, "a1");
        claude.SucceedRestarts("a");
        RunTool(h, claude, "npm test");
        var (codexId, codex) = StartRunning(h, h.Codex, "b1");

        // 第一種代理（Claude）的屏障已建立，第二種（Codex）建立途中丟例外
        var preparation = await h.OnUi(() =>
        {
            codex.SafePointFailure = new InvalidOperationException("讀不到會話狀態");
            try
            {
                return h.Hub.PrepareForAppUpdateAsync(CancellationToken.None);
            }
            finally
            {
                codex.SafePointFailure = null;
            }
        }).WaitAsync(TimeSpan.FromSeconds(5));
        await h.Ui.DrainAsync();

        preparation.Ready.Should().BeFalse();
        preparation.Reason.Should().Be("app 更新前排空對話失敗：讀不到會話狀態");
        h.OnUi(() => h.Hub.AppUpdateDraining).Should().BeFalse("已建立的屏障收回，不留到 30 分鐘逾時");
        IsNull(HeaderRestart(h, claudeId)).Should().BeTrue("Claude 的對話不再等 app 更新");
        IsNull(HeaderRestart(h, codexId)).Should().BeTrue("建立到一半的 Codex 屏障也收回");
        claude.Restarts.Count.Should().Be(0);
        codex.Restarts.Count.Should().Be(0);
        h.Send(h.Main, claudeId, "收回後送出");
        h.Queue(claudeId).Should().Equal(new[] { "收回後送出" }, "執行中照常排進佇列，不是因為更新被延後");
        h.Main.Messages("toast").Should().NotContain(m => m.Value<string>("text") == "Clode Myriagent 正在更新，訊息已排進佇列，更新完成後自動送出");
    }

    // ───── 合併 main 的 Claude Remote Control（設計 9.1、9.2、9.5）─────

    [Fact]
    public async Task 開著RemoteControl的對話等你回應_不視為安全點_等回應_排空滿10分鐘才照立即套用停下()
    {
        using var h = await NewHarnessAsync();
        var start = DateTime.Now;
        var now = start;
        SetClock(h, () => now);
        var (id, s) = StartRunning(h, h.Claude, "q1");
        s.SucceedRestarts("q");
        s.HasRemoteControl = true;
        h.Raise(s, new AgentApprovalEvent("r1", "執行指令", "bash", "git status", null, "t1", CanAllowForSession: true));
        h.Raise(s, new AgentSafePointChangedEvent(false, WaitingForUser: true, false));
        var card = h.Items(id).Single(i => i.RequestKey == "r1");

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();

        prepare.IsCompleted.Should().BeFalse("桌面沒人時使用者可能正在手機上要回應：照 CLI 更新等回應");
        s.Restarts.Count.Should().Be(0);
        card.Status.Should().Be(AgentItemStatus.Waiting, "卡片照常有效，手機上的核准不會被收回");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("waitingUser");
        header.Value<string>("detail").Should().Be("等你回應後停下，讓 Clode Myriagent 更新");

        now = start.AddMinutes(9);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        s.Restarts.Count.Should().Be(0, "還沒滿 10 分鐘");

        now = start.AddMinutes(10);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var request = s.Restarts.All.Should().ContainSingle().Subject;
        request.Settings.Should().BeNull();
        request.Mode.Should().Be(AgentRestartMode.Immediate, "滿 10 分鐘照立即套用停下（同背景工作）");
        card.Status.Should().Be(AgentItemStatus.Cancelled);
        card.Output.Should().Be(CliRestartRules.StaleCardOutput);
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
        var entry = file.Conversations.Single(e => e.Id == id);
        entry.Mode.Should().Be(ResumeMode.Continue);
        entry.PendingRequests.Should().ContainSingle().Which.Should().Contain("git status", "接續後由代理重新提出");
        file.RemoteControl.Should().Equal(new[] { id }, "停下時開著 Remote Control：記下（新版自動接續時程序照樣開 Remote Control，不另外接回）");
    }

    [Fact]
    public async Task 開著RemoteControl的閒置程序_排空照常關_記進resume_新版重開程序接回_接續與延後送出的不另外接回()
    {
        using var h = await NewHarnessAsync();
        // 開著 Remote Control 的閒置程序（手機或 claude.ai 上連著）
        var (idleId, idle) = StartRunning(h, h.Claude, "a1");
        idle.HasRemoteControl = true;
        h.Raise(idle, new AgentRemoteControlEvent("cse_idle", null));
        h.CompleteTurn(idle);
        // 開著 Remote Control、在安全點停下的：新版自動接續（接續的程序照樣開 Remote Control）
        var (heldId, held) = StartRunning(h, h.Claude, "b1");
        held.HasRemoteControl = true;
        held.SafePoint = true;
        held.SucceedRestarts("b");
        // 開著 Remote Control 的閒置程序、排空時的送出被延後：新版送出時照樣開 Remote Control
        var (sentId, sent) = StartRunning(h, h.Claude, "c1");
        sent.HasRemoteControl = true;
        h.CompleteTurn(sent);
        // 沒開 Remote Control 的閒置程序
        var (plainId, plain) = StartRunning(h, h.Claude, "d1");
        h.CompleteTurn(plain);

        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        idle.Closes.Should().Be(1, "app 要結束，程序留不住：照常關");
        plain.Closes.Should().Be(1);
        h.Send(h.Main, sentId, "排空時送出");
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
        file.RemoteControl.Should().BeEquivalentTo(new[] { idleId, heldId, sentId }, "沒開 Remote Control 的不記");
        file.DeferredSends.Should().Equal(sentId);
        file.Conversations.Select(e => e.Id).Should().Equal(heldId);

        await h.RestartAsync();
        int before = h.Sessions.Count;
        AppResumeSummary? summary = null;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += s => summary = s;
            h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, "tok", DateTime.UtcNow), "0.2.2", RolledBack: false));
        });
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => summary != null, "接續都已送出");
        await h.WaitUntilAsync(() => h.Sessions.Skip(before).Sum(s => s.Sent.Count) == 2, "接續與延後的送出各一則");
        await h.WaitUntilAsync(() => h.Sessions.Skip(before).Any(s => s.Reattaches.Count > 0), "重開程序接回 Remote Control");
        await h.Ui.DrainAsync();

        summary!.Should().Be(new AppResumeSummary(Continued: 1, NotContinued: 0, DeferredSent: 1, RolledBack: false, AppTo: "0.2.2"));
        var fresh = h.Sessions.Skip(before).ToList();
        var reattached = fresh.Where(s => s.Reattaches.Count > 0).Should().ContainSingle("只有沒有自動接續、也沒有延後送出的那個").Subject;
        var settings = reattached.Reattaches.All.Should().ContainSingle().Subject;
        var idleConversation = h.Conversation(idleId)!;
        settings.RemoteControl.Should().Be(new AgentRemoteControlOptions(idleConversation.Title, "cse_idle"), "接回原本的遠端工作階段（對話裡記的 id）");
        settings.SessionId.Should().Be(idleConversation.SessionId);
        settings.SessionStarted.Should().BeTrue();
        reattached.Sent.Count.Should().Be(0, "只接回、不送訊息、不開始一輪");
        h.IsRunning(idleId).Should().BeFalse();
        h.IsRunning(plainId).Should().BeFalse();
        fresh.Should().HaveCount(3, "沒開 Remote Control 的對話不啟動程序");
    }

    [Fact]
    public async Task 開著RemoteControl但2小時沒動靜的_換版不記進resume_舊版記了的新版也不接回()
    {
        using var h = await NewHarnessAsync();
        var (recentId, recent) = StartRunning(h, h.Claude, "a1");
        recent.HasRemoteControl = true;
        h.Raise(recent, new AgentRemoteControlEvent("cse_recent", null));
        h.CompleteTurn(recent);
        var (oldId, old) = StartRunning(h, h.Claude, "b1");
        old.HasRemoteControl = true;
        h.Raise(old, new AgentRemoteControlEvent("cse_old", null));
        h.CompleteTurn(old);
        h.Ui.Invoke(() => h.Hub.AgeConversationForTest(oldId, TimeSpan.FromHours(3)));

        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
        file.RemoteControl.Should().Equal(new[] { recentId }, "3 小時沒動靜的接回之後也只是等著被回收");

        // 舊版寫的 resume.json 不篩（換到這一版的第一次）：新版以對話的最後更新時間篩
        file.RemoteControl.Add(oldId);
        await h.RestartAsync();
        int before = h.Sessions.Count;
        AppResumeSummary? summary = null;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += s => summary = s;
            h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, "tok", DateTime.UtcNow), "0.2.2", RolledBack: false));
        });
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => summary != null, "接續都已送出");
        await h.WaitUntilAsync(() => h.Sessions.Skip(before).Any(s => s.Reattaches.Count > 0), "重開程序接回 Remote Control");
        await Task.Delay(300);
        await h.Ui.DrainAsync();
        var reattached = h.Sessions.Skip(before).Should().ContainSingle("只接回最近有動靜的那個").Subject;
        reattached.Reattaches.All.Should().ContainSingle().Which.RemoteControl!.ReattachSessionId.Should().Be("cse_recent");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 放棄這次更新_被關掉的RemoteControl閒置程序就地重開接回_設定關掉就不接回(bool remoteControlSetting)
    {
        using var h = await NewHarnessAsync();
        var (idleId, idle) = StartRunning(h, h.Claude, "a1");
        idle.HasRemoteControl = true;
        h.Raise(idle, new AgentRemoteControlEvent("cse_idle", null));
        h.CompleteTurn(idle);
        var (_, busy) = StartRunning(h, h.Codex, "b1");
        busy.SucceedRestarts("b");
        RunTool(h, busy, "npm test");   // 排空不會馬上結束
        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        idle.Closes.Should().Be(1, "排空照常關開著 Remote Control 的閒置程序");
        if (!remoteControlSetting)
            h.Command(h.Main, "setting", new { key = "claudeRemoteControl", value = false });

        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("排空超過 30 分鐘"));
        await h.Ui.DrainAsync();

        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeFalse();
        if (remoteControlSetting)
        {
            await h.WaitUntilAsync(() => idle.Reattaches.Count > 0, "就地重開程序接回 Remote Control");
            idle.Reattaches.All.Should().ContainSingle().Which.RemoteControl
                .Should().Be(new AgentRemoteControlOptions(h.Conversation(idleId)!.Title, "cse_idle"));
        }
        else
        {
            await Task.Delay(200);
            await h.Ui.DrainAsync();
            idle.Reattaches.Count.Should().Be(0, "設定已關掉 Remote Control：不重開程序");
        }
        idle.Sent.Count.Should().Be(1, "只有第一則：接回不送訊息");
        busy.Restarts.Count.Should().Be(0, "還沒停下的照常跑");
    }

    [Fact]
    public async Task RemoteControl在用_等回應與工具在執行1小時內_其他10分鐘內有動靜才算_背景工作與沒開的不算()
    {
        using var h = await NewHarnessAsync();
        var idleThreshold = AppUpdateRestartRules.RemoteIdleThreshold;
        var waitThreshold = AppUpdateRestartRules.RemoteWaitThreshold;
        var almost = waitThreshold - TimeSpan.FromMinutes(1);
        bool InUse(TimeSpan later) => h.OnUi(() => h.Hub.RemoteControlInUse(DateTime.Now + later, idleThreshold, waitThreshold));
        var (_, idle) = StartRunning(h, h.Claude, "a1");
        h.CompleteTurn(idle);
        InUse(TimeSpan.Zero).Should().BeFalse("沒開 Remote Control：桌面閒置就算沒人在用");

        idle.HasRemoteControl = true;
        InUse(TimeSpan.Zero).Should().BeTrue("剛有動靜（遠端的送出、回覆與核准都經過程序）");
        InUse(idleThreshold).Should().BeFalse("10 分鐘沒有動靜");
        idle.HasLiveBackgroundWork = true;
        InUse(idleThreshold).Should().BeFalse("存活的背景工作（開發伺服器之類）本身不算：可能一直不結束，排空照常最多等它 30 分鐘");
        idle.HasLiveBackgroundWork = false;
        // 從手機送來的訊息由 CLI 自己開一輪（中樞沒有在執行），跑很久的指令期間沒有其他動靜
        h.Raise(idle, new AgentToolStartedEvent("t9", "執行指令", "bash", "npm test", "npm test"));
        InUse(idleThreshold).Should().BeTrue("有工具在執行");
        InUse(almost).Should().BeTrue("有工具在執行：1 小時內都算");
        InUse(waitThreshold).Should().BeFalse("1 小時沒有動靜的工具（可能是不會結束的前景指令）：交給排空，等到排空上限 30 分鐘");
        h.Raise(idle, new AgentToolCompletedEvent("t9", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        InUse(idleThreshold).Should().BeFalse();

        var (_, waiting) = StartRunning(h, h.Claude, "b1");
        waiting.HasRemoteControl = true;
        h.Raise(waiting, new AgentApprovalEvent("r1", "執行指令", "bash", "git status", null, "t1", CanAllowForSession: true));
        InUse(almost).Should().BeTrue("在等回應：手機上可能正要核准，1 小時內都算");
        InUse(waitThreshold).Should().BeFalse("卡片 1 小時沒人回：不能讓自動更新永遠不開始，交給排空（滿 10 分鐘照立即套用停下、新版重新提出）");
        waiting.HasRemoteControl = false;
        InUse(almost).Should().BeFalse("沒開 Remote Control 的等回應交給桌面的閒置判斷");
    }
}
