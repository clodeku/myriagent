using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// app 更新時不停下還在執行的一輪（代理主機 H2，AgentHub.Detach.cs、AgentHub.DetachTurn.cs；設計 docs/plans/2026-11-26-agent-host.md 第 10 節）：
/// 放得開的執行中對話排空時不停也不等，其他對話都停好之後、排空的最後一步才放開程序；這一輪在中樞的狀態寫進 resume.json，
/// 新版還原之後接回，之後的事件照舊寫進原本的項目。放棄更新時就地接回；放不開、接不回去都退回原本的做法
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubTurnDetachTests
{
    private static readonly CliBinaryIdentity ClaudeCli = CliIdentities.Claude("2.1.295", fileId: 1);

    private sealed class RecordingHost : IAgentProcessHost
    {
        public Recorder<AgentProcessDetachState> Discarded { get; } = new();

        private int unclaimed;

        public int DiscardUnclaimedCalls => Volatile.Read(ref unclaimed);

        public HostedProcess Spawn(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyList<KeyValuePair<string, string>> environment)
            => throw new AgentHostUnavailableException("測試用的主機不開程序");

        public HostedProcess Attach(AgentProcessDetachState state) => throw new AgentHostUnavailableException("測試用的主機不接回程序");

        public void Discard(AgentProcessDetachState state) => Discarded.Add(state);

        public void DiscardUnclaimed() => Interlocked.Increment(ref unclaimed);
    }

    private static async Task<(HubHarness H, RecordingHost Host)> NewHarnessAsync()
    {
        var h = new HubHarness();
        await h.SwitchCliAsync(AgentProvider.Claude, null, ClaudeCli);
        await h.SwitchCliAsync(AgentProvider.Codex, null, CliIdentities.Codex("0.160.1", fileId: 1));
        var host = new RecordingHost();
        h.Ui.Invoke(() =>
        {
            h.Hub.BackgroundSettle = TimeSpan.Zero;
            h.Hub.HandoffPoll = TimeSpan.FromMilliseconds(15);
            h.Hub.AttachProcessHost(host);
        });
        return (h, host);
    }

    /// <summary>一個一輪還在執行的 Claude 對話：回覆了一段文字（區塊 b1）、指令 npm test（工具 t1）正在跑；程序經代理主機開、執行中也放得開</summary>
    private static (string Id, FakeSession Session) RunningTool(HubHarness h, string instance = "r1", bool detachable = true)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.ProcessStarted(s, instance, ClaudeCli);
        s.SucceedRestarts("r");
        s.Detachable = detachable;
        s.DetachableWhileRunning = detachable;
        h.Raise(s, new AgentTextDeltaEvent("b1", "先跑測試"));
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        s.SafePoint = false;
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: false, HasLiveBackgroundWork: false));
        return (id, s);
    }

    private static Task<AppUpdatePreparation> Prepare(HubHarness h) => h.OnUi(() => h.Hub.PrepareForAppUpdateAsync(CancellationToken.None));

    private static RunningTurnRecord? Marker(HubHarness h, string id) => RunningTurnStore.TryLoad(h.Store.GetConversationDirectory(id)).Record;

    private static string? State(HubHarness h, string id) => h.OnUi(() => h.Hub.RenderHeaderRestartForTest(id)) is JObject o ? o.Value<string>("state") : null;

    private static AppResumeFile Resume(HubHarness h) => h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

    private static List<string> SentTexts(HubHarness h) => h.Sessions.SelectMany(s => s.Sent.All).Select(s => s.Input.Text).ToList();

    private static AgentChatItem Card(HubHarness h, string id) => h.Items(id).Single(i => i.Kind == AgentItemKind.Tool && i.Title == "npm test");

    private static AgentChatItem Reply(HubHarness h, string id) => h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);

    /// <summary>新的 app：重建中樞（同一個資料夾）、接上主機、排定 resume.json 的接續，新建的會話先照 configure 設定</summary>
    private static async Task<RecordingHost> RestartWithResumeAsync(HubHarness h, AppResumeFile? file, Action<FakeSession>? configure = null)
    {
        // resume.json 經過磁碟：照樣序列化再讀回（欄位名稱、列舉、巢狀物件都要撐得過）
        if (file != null)
            file = Newtonsoft.Json.JsonConvert.DeserializeObject<AppResumeFile>(Newtonsoft.Json.JsonConvert.SerializeObject(file))!;
        await h.RestartAsync();
        var host = new RecordingHost();
        h.OnSessionCreated = configure;
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachProcessHost(host);
            if (file != null)
                h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeValidity.Ok, "0.2.2", RolledBack: false));
        });
        return host;
    }

    [Fact]
    public async Task 執行中的對話_排空時不停也不等_resume帶著這一輪在中樞的狀態()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var journal = new FakeJournal();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundJournal(journal));
        var (id, s) = RunningTool(h);
        var cardId = Card(h, id).Id;
        var replyId = Reply(h, id).Id;

        var preparation = await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5));

        preparation.Ready.Should().BeTrue("不必等這一輪做完");
        preparation.Held.Should().Be(0);
        s.Detaches.Should().Be(1);
        s.Restarts.Count.Should().Be(0, "沒有停下它");
        s.Interrupts.Should().Be(0);
        s.Closes.Should().Be(0);
        h.IsRunning(id).Should().BeTrue("這一輪沒有結束：中樞照舊算執行中");
        Card(h, id).Status.Should().Be(AgentItemStatus.Running);
        h.OnUi(() => h.Hub.DetachedProcessCount).Should().Be(1);

        var file = Resume(h);
        file.Conversations.Should().BeEmpty("沒有停下待接續的一輪");
        var attached = file.Attached.Should().ContainSingle().Subject;
        attached.Id.Should().Be(id);
        attached.State!.Value<bool>("Running").Should().BeTrue();
        var turn = attached.Turn!;
        turn.Tools.Should().Equal(new Dictionary<string, string> { ["t1"] = cardId });
        turn.Blocks.Should().Equal(new Dictionary<string, string> { ["b1"] = replyId });
        turn.Statuses.Should().Contain(cardId, "Running").And.Contain(replyId, "Running");
        turn.Status.Should().NotBe(AgentHub.DetachedStatus, "記的是放開之前的狀態列");

        // 接不回去時新版照這份快照接續：記成 app 停掉的、列著被打斷的工具；之後不被沒有程序時的快照蓋掉
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        var marker = Marker(h, id)!;
        marker.ClosedByApp.Should().BeTrue();
        marker.WaitingBackground.Should().BeFalse();
        marker.InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("npm test");
        journal.Of("restart.keep").Should().ContainSingle().Which.Value<bool>("keep").Should().BeTrue();
        journal.Of("restart.detach").Should().ContainSingle().Which.Value<bool>("running").Should().BeTrue();
        journal.Of("restart.call").Should().BeEmpty();
    }

    [Fact]
    public async Task 其他對話還沒停好時_放得開的照常執行_都停好了才放開()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = RunningTool(h);
        var (otherId, other) = RunningTool(h, "d1", detachable: false);

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        await Task.Delay(80);

        prepare.IsCompleted.Should().BeFalse("直接開的那個要等指令做完才停得下來");
        State(h, otherId).Should().Be("waitingTool");
        State(h, id).Should().BeNull("放得開的不算在等：標頭不顯示");
        s.Detaches.Should().Be(0, "還沒到最後一步：照常執行、照常顯示");
        h.OnUi(() => h.Hub.AppUpdateWaitingCount).Should().Be(1);
        h.Raise(s, new AgentTextDeltaEvent("b1", "，目前 3 個通過"));
        Reply(h, id).Text.Should().Be("先跑測試，目前 3 個通過");

        // 另一個對話的指令做完、到了安全點：停下它，然後才放開這一個
        h.Raise(other, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        other.SafePoint = true;
        h.Raise(other, new AgentSafePointChangedEvent(false, false, false));

        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Ready.Should().BeTrue();
        preparation.Held.Should().Be(1);
        other.Restarts.Count.Should().Be(1, "放不開的照原本的規則在安全點停下");
        other.Detaches.Should().Be(0);
        s.Detaches.Should().Be(1);
        s.Restarts.Count.Should().Be(0);
        var file = Resume(h);
        file.Conversations.Select(c => c.Id).Should().Equal(otherId);
        file.Attached.Select(a => a.Id).Should().Equal(id);
    }

    [Fact]
    public async Task 新版啟動_還原這一輪再接回_之後的事件寫進原本的項目_這一輪照常結束()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, _) = RunningTool(h);
        var cardId = Card(h, id).Id;
        var replyId = Reply(h, id).Id;
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = Resume(h);
        var sentBefore = SentTexts(h).Count;

        // 接上時，放開期間的輸出馬上補送進來
        var host = await RestartWithResumeAsync(h, file, s => s.AfterAttach = attached =>
        {
            attached.SafePoint = false;
            attached.Raise(new AgentToolOutputEvent("t1", "12 passed\n"));
        });
        Card(h, id).Status.Should().Be(AgentItemStatus.Cancelled, "載入紀錄時執行中的工具一律先算被中斷");
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => host.DiscardUnclaimedCalls == 1, "接回有了結果");

        var resumed = h.Sessions[^1];
        resumed.Attaches.All.Should().ContainSingle().Which.State.Value<bool>("Running").Should().BeTrue();
        h.IsRunning(id).Should().BeTrue("這一輪照舊在執行");
        SentTexts(h).Should().HaveCount(sentBefore, "沒有被中斷：不必送接續訊息");
        host.Discarded.Count.Should().Be(0);
        var card = Card(h, id);
        card.Id.Should().Be(cardId);
        card.Status.Should().Be(AgentItemStatus.Running, "改回執行中");
        card.Output.Should().Be("12 passed\n", "放開期間的輸出寫進原本那張卡");
        var marker = Marker(h, id)!;
        marker.ClosedByApp.Should().BeFalse("接回了：改回平常的執行中快照");
        marker.InterruptedTools.Should().ContainSingle();
        h.Main.Messages("conversation").Should().BeEmpty();

        // 之後的事件照舊對到原本的項目，這一輪照常結束
        h.Raise(resumed, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "12 passed\n", Array.Empty<string>()));
        h.Raise(resumed, new AgentTextDeltaEvent("b1", "，都通過了"));
        Card(h, id).Status.Should().Be(AgentItemStatus.Done);
        var reply = Reply(h, id);
        reply.Id.Should().Be(replyId);
        reply.Text.Should().Be("先跑測試，都通過了");
        h.CompleteTurn(resumed);
        h.IsRunning(id).Should().BeFalse();
        Reply(h, id).Status.Should().Be(AgentItemStatus.Done);
        Marker(h, id).Should().BeNull("這一輪結束了");
        h.Items(id).Count(i => i.Kind == AgentItemKind.Tool).Should().Be(1, "沒有多出重複的卡片");
        State(h, id).Should().BeNull();
    }

    [Fact]
    public async Task 新版啟動_接不回去_這一輪收尾_照被中斷的一輪接續()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, _) = RunningTool(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = Resume(h);
        var sentBefore = SentTexts(h).Count;

        var host = await RestartWithResumeAsync(h, file, s => s.FailAttach = true);
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => SentTexts(h).Count == sentBefore + 1, "接不回去：照上次結束時還在執行的一輪接續");

        var text = SentTexts(h).Last();
        text.Should().StartWith("（系統通知：Clode Myriagent");
        text.Should().Contain("npm test", "接續訊息列出被打斷的工具");
        Card(h, id).Status.Should().Be(AgentItemStatus.Cancelled, "那個指令跟著程序沒了");
        Reply(h, id).Status.Should().Be(AgentItemStatus.Done);
        h.IsRunning(id).Should().BeTrue("接續的一輪");
        await h.WaitUntilAsync(() => host.Discarded.Count == 1, "留在主機的那個程序沒有人要了");
        h.Sessions[^1].Attaches.Count.Should().Be(1);
    }

    [Fact]
    public async Task 放棄這次更新_就地接回_這一輪照舊執行()
    {
        var (h, host) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = RunningTool(h);
        var cardId = Card(h, id).Id;
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.HasLiveProcess.Should().BeFalse();
        var sentBefore = SentTexts(h).Count;

        // 放開著的時候按停止：停不了，提示稍後再按
        var mark = h.Main.Mark();
        h.Command(h.Main, "stop", new { conversationId = id });
        s.Interrupts.Should().Be(0);
        h.Main.Messages("toast", mark).Should().ContainSingle().Which.Value<string>("text").Should().Contain("正在更新");

        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("使用者取消"));
        await h.Ui.DrainAsync();

        s.Attaches.All.Should().ContainSingle().Which.State.Value<bool>("Running").Should().BeTrue();
        s.HasLiveProcess.Should().BeTrue();
        s.IsRunning.Should().BeTrue();
        h.IsRunning(id).Should().BeTrue();
        SentTexts(h).Should().HaveCount(sentBefore, "這一輪沒有中斷");
        h.OnUi(() => h.Hub.DetachedProcessCount).Should().Be(0);
        host.Discarded.Count.Should().Be(0);
        Marker(h, id)!.ClosedByApp.Should().BeFalse();
        State(h, id).Should().BeNull();

        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        var card = Card(h, id);
        card.Id.Should().Be(cardId);
        card.Status.Should().Be(AgentItemStatus.Done);
        // 接回之後停止照常
        h.Command(h.Main, "stop", new { conversationId = id });
        s.Interrupts.Should().Be(1);
    }

    [Fact]
    public async Task 放棄這次更新_就地接不回去_這一輪收尾後就地接續()
    {
        var (h, host) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = RunningTool(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.FailAttach = true;
        var sentBefore = SentTexts(h).Count;

        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("使用者取消"));
        await h.WaitUntilAsync(() => SentTexts(h).Count == sentBefore + 1, "接不回去：就地接續被中斷的一輪");

        var text = SentTexts(h).Last();
        text.Should().StartWith("（系統通知：Clode Myriagent 為了套用自身的更新而停下了 Claude Code，現在重新啟動了它");
        text.Should().Contain("npm test");
        Card(h, id).Status.Should().Be(AgentItemStatus.Cancelled);
        h.IsRunning(id).Should().BeTrue();
        await h.WaitUntilAsync(() => host.Discarded.Count == 1, "留在主機的那個程序結束掉");
    }

    /// <summary>另一個對話的指令做完、到了安全點（放不開的那種：照原本的規則這時停下）</summary>
    private static void FinishTool(HubHarness h, FakeSession session)
    {
        h.Raise(session, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        session.SafePoint = true;
        h.Raise(session, new AgentSafePointChangedEvent(false, false, false));
    }

    [Fact]
    public async Task 交棒時會話一時放不開_等它一下_放得開了照常放開()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = RunningTool(h);
        var (_, holder) = RunningTool(h, "d1", detachable: false);
        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        // 記成交棒時放開之後，會話有控制請求在途；其他對話這時都停好了
        s.DetachBlocked = true;
        FinishTool(h, holder);
        await Task.Delay(150);
        prepare.IsCompleted.Should().BeFalse();
        s.Detaches.Should().Be(0);
        s.Restarts.Count.Should().Be(0, "只是一時放不開：不改成停下");
        State(h, id).Should().BeNull();

        s.DetachBlocked = false;
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.Detaches.Should().Be(1);
        Resume(h).Attached.Select(a => a.Id).Should().Equal(id);
    }

    [Fact]
    public async Task 交棒時會話一直放不開或放開失敗_照原本的規則到安全點停下()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        h.Ui.Invoke(() => h.Hub.DetachReadyWait = TimeSpan.FromMilliseconds(60));
        var (blockedId, blocked) = RunningTool(h, "b1");
        var (failId, fail) = RunningTool(h, "f1");
        fail.FailDetach = true;
        var (holderId, holder) = RunningTool(h, "d1", detachable: false);
        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        blocked.DetachBlocked = true;
        FinishTool(h, holder);

        await h.WaitUntilAsync(() => State(h, blockedId) == "waitingTool", "一直放不開：照原本的規則等指令做完");
        blocked.Detaches.Should().Be(0);
        prepare.IsCompleted.Should().BeFalse();
        fail.Detaches.Should().Be(0, "還有對話沒停好：不先放開別的");

        blocked.DetachBlocked = false;
        FinishTool(h, blocked);
        blocked.Restarts.Count.Should().Be(1, "這次排空不再試著放開它：在安全點停下");
        blocked.Detaches.Should().Be(0);

        // 輪到另一個：放開失敗，同樣照原本的規則
        await h.WaitUntilAsync(() => fail.Detaches == 1 && State(h, failId) == "waitingTool", "放開失敗：照原本的規則等指令做完");
        h.IsRunning(failId).Should().BeTrue();
        Marker(h, failId)!.ClosedByApp.Should().BeFalse("沒有放開：快照改回平常的");
        FinishTool(h, fail);

        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        preparation.Ready.Should().BeTrue();
        preparation.Held.Should().Be(3);
        fail.Detaches.Should().Be(1);
        var file = Resume(h);
        file.Attached.Should().BeEmpty();
        file.Conversations.Select(c => c.Id).Should().BeEquivalentTo(new[] { blockedId, failId, holderId });
    }

    [Fact]
    public async Task 記成交棒時放開之後這一輪結束了_沒有背景工作_照閒置的關掉()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = RunningTool(h);
        var (_, other) = RunningTool(h, "d1", detachable: false);
        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        s.Detaches.Should().Be(0);

        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        s.SafePoint = null;
        h.CompleteTurn(s);
        s.IsRunning = false;
        await h.WaitUntilAsync(() => s.Closes == 1, "這一輪做完了、沒有背景工作：照閒置的關掉，不必放開");

        h.Raise(other, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        other.SafePoint = true;
        h.Raise(other, new AgentSafePointChangedEvent(false, false, false));
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.Detaches.Should().Be(0);
        Resume(h).Attached.Should().BeEmpty();
        h.IsRunning(id).Should().BeFalse();
    }

    [Fact]
    public async Task 放不開的原因記進背景活動日誌_同一個原因只記一次()
    {
        // 2026-11-26 實機：0.2.1028 換 0.2.1030 時執行中的 Claude 對話沒有放開，日誌裡看不出為什麼
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var journal = new FakeJournal();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundJournal(journal));
        var (id, s) = RunningTool(h, "c1");
        s.DetachBlocked = true;
        s.DetachBlockerText = "control:watch-3";

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();

        var refused = journal.Of("restart.keep").Should().ContainSingle("同一個原因只記一次").Subject;
        refused.Value<string>("conv").Should().Be(id);
        refused.Value<bool>("keep").Should().BeFalse();
        refused.Value<string>("why").Should().Be("control:watch-3");
        refused.Value<string>("situation").Should().Be("ToolRunning");

        // 原因變了：再記一筆
        s.DetachBlockerText = "outputTooLarge";
        h.Raise(s, new AgentToolOutputEvent("t1", "more"));
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        journal.Of("restart.keep").Select(e => e.Value<string>("why")).Should().Equal("control:watch-3", "outputTooLarge");
        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("測試"));
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeFalse();
    }

    [Fact]
    public async Task 等你回應的對話放不開_照原本的規則停下()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = RunningTool(h);
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        h.Raise(s, new AgentApprovalEvent("req1", "執行指令", "bash", "git push", null, null, CanAllowForSession: false));
        h.Raise(s, new AgentSafePointChangedEvent(ToolRunning: false, WaitingForUser: true, HasLiveBackgroundWork: false));

        var preparation = await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5));

        preparation.Ready.Should().BeTrue();
        s.Detaches.Should().Be(0, "帶著等回應的請求放不開");
        s.Restarts.Count.Should().Be(1, "app 更新把等你回應當成安全點：停下，新版請代理重新提出");
        Resume(h).Conversations.Select(c => c.Id).Should().Equal(id);
        Resume(h).Attached.Should().BeEmpty();
    }

    [Fact]
    public async Task 執行中又帶著背景工作的對話_不擋更新()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var tool = new AgentBackgroundTool("執行指令", "bash", "npm run dev", null, StopId: "bg1");
        var (id, s) = RunningTool(h);
        s.HasLiveBackgroundWork = true;
        s.LiveBackgroundTools = [tool];
        h.Raise(s, new AgentBackgroundWorkEvent(1, [tool]));
        h.OnUi(() => h.Hub.BackgroundWorkRunning).Should().BeFalse("放得開：背景工作與這一輪都撐得過更新");

        var (_, direct) = RunningTool(h, "d1", detachable: false);
        direct.HasLiveBackgroundWork = true;
        direct.LiveBackgroundTools = [tool];
        h.Raise(direct, new AgentBackgroundWorkEvent(1, [tool]));
        h.OnUi(() => h.Hub.BackgroundWorkRunning).Should().BeTrue("直接開的留不住：照舊等背景工作");
        h.IsRunning(id).Should().BeTrue();
    }
}
