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
/// app 更新時不關還在等背景工作的對話的程序（代理主機 H1，AgentHub.Detach.cs；設計 docs/plans/2026-11-26-agent-host.md）：
/// 排空碰到「閒置、背景工作在跑、放得開」的對話就放開程序（不等、不停），resume.json 的 Attached 讓新版接回；
/// 放棄更新時就地接回；放不開、接不回去都退回原本的做法（等背景工作／照 running.json 請代理確認）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubDetachTests
{
    private static readonly CliBinaryIdentity ClaudeCli = CliIdentities.Claude("2.1.295", fileId: 1);

    private static readonly AgentBackgroundTool DevServer = new("執行指令", "bash", "npm run dev", null, StopId: "b1");

    /// <summary>記下中樞要主機結束哪些沒人接回的程序（假會話不經主機開程序，Spawn／Attach 用不到）</summary>
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
            h.Hub.AttachProcessHost(host);
        });
        return (h, host);
    }

    /// <summary>一個回覆完、還在等背景工作（npm run dev）的 Claude 對話；detachable＝它的程序是經代理主機開的</summary>
    private static (string Id, FakeSession Session) WaitingBackground(HubHarness h, bool detachable = true, string instance = "a1")
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude, "開開發伺服器");
        h.ProcessStarted(s, instance, ClaudeCli);
        s.SucceedRestarts("a");
        s.Detachable = detachable;
        s.HasLiveBackgroundWork = true;
        s.LiveBackgroundTools = [DevServer];
        h.Raise(s, new AgentBackgroundWorkEvent(1, [DevServer]));
        h.Raise(s, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));
        h.CompleteTurn(s);
        s.IsRunning = false;
        return (id, s);
    }

    private static Task<AppUpdatePreparation> Prepare(HubHarness h) => h.OnUi(() => h.Hub.PrepareForAppUpdateAsync(CancellationToken.None));

    private static RunningTurnRecord? Marker(HubHarness h, string id) => RunningTurnStore.TryLoad(h.Store.GetConversationDirectory(id)).Record;

    private static string? State(HubHarness h, string id) => h.OnUi(() => h.Hub.RenderHeaderRestartForTest(id)) is JObject o ? o.Value<string>("state") : null;

    private static AppResumeFile Resume(HubHarness h) => h.OnUi(() => h.Hub.BuildAppResume("tok", "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));

    private static List<string> SentTexts(HubHarness h) => h.Sessions.SelectMany(s => s.Sent.All).Select(s => s.Input.Text).ToList();

    /// <summary>新的 app：重建中樞（同一個資料夾）、接上主機、排定 resume.json 的接續，新建的會話先照 configure 設定</summary>
    private static async Task<RecordingHost> RestartWithResumeAsync(HubHarness h, AppResumeFile? file, Action<FakeSession>? configure = null)
    {
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
    public async Task 放得開的等背景工作的對話_不擋更新_排空時放開程序_不等也不停_resume列出要接回的()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var journal = new FakeJournal();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundJournal(journal));
        var (id, s) = WaitingBackground(h);
        h.OnUi(() => h.Hub.BackgroundWorkRunning).Should().BeFalse("放得開：背景工作撐得過更新，不必為它擋住更新");
        Marker(h, id)!.ClosedByApp.Should().BeFalse();

        var preparation = await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5));

        preparation.Ready.Should().BeTrue("不必等背景工作");
        s.Detaches.Should().Be(1);
        s.Restarts.Count.Should().Be(0, "沒有停下它");
        s.Closes.Should().Be(0, "也沒有關掉它");
        h.OnUi(() => h.Hub.DetachedProcessCount).Should().Be(1);
        var attached = Resume(h).Attached.Should().ContainSingle().Subject;
        attached.Id.Should().Be(id);
        attached.State!.Value<string>("Instance").Should().Be("a1", "會話給的狀態原樣寫進去");
        Resume(h).Conversations.Should().BeEmpty("沒有停下待接續的一輪");
        // 接不回去時新版要照這份快照請代理確認：記成 app 停掉的、列著背景工作；之後不被沒有程序時的快照蓋掉
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        var marker = Marker(h, id)!;
        marker.ClosedByApp.Should().BeTrue();
        marker.WaitingBackground.Should().BeTrue();
        marker.BackgroundTasks.Should().Equal("執行指令：npm run dev");
        journal.Of("restart.detach").Should().ContainSingle().Which.Value<string>("conv").Should().Be(id);
        journal.Of("restart.detached").Should().ContainSingle().Which.Value<bool>("ok").Should().BeTrue();
        journal.Of("restart.call").Should().BeEmpty();
    }

    [Fact]
    public async Task 放不開的_照舊等背景工作_擋住更新()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (directId, direct) = WaitingBackground(h, detachable: false, instance: "d1");
        h.OnUi(() => h.Hub.BackgroundWorkRunning).Should().BeTrue("直接開的程序留不住：照舊等背景工作");
        var (failId, fail) = WaitingBackground(h, instance: "f1");
        fail.FailDetach = true;

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();

        prepare.IsCompleted.Should().BeFalse();
        direct.Detaches.Should().Be(0);
        State(h, directId).Should().Be("waitingBackground");
        fail.Detaches.Should().Be(1, "試過一次，放不開就不再試");
        State(h, failId).Should().Be("waitingBackground");
        fail.HasLiveProcess.Should().BeTrue();
        Marker(h, failId)!.ClosedByApp.Should().BeFalse("沒有放開：快照改回平常的等背景工作");
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        fail.Detaches.Should().Be(1);
        h.OnUi(() => h.Hub.DetachedProcessCount).Should().Be(0);
        Resume(h).Attached.Should().BeEmpty();
        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("測試"));
    }

    [Fact]
    public async Task 新版啟動_接回程序_背景工作沒有中斷_不送接續訊息_之後照常等背景工作()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, _) = WaitingBackground(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = Resume(h);
        var sentBefore = SentTexts(h).Count;

        var host = await RestartWithResumeAsync(h, file);
        Marker(h, id)!.ClosedByApp.Should().BeTrue("結束時沒有被沒有程序的快照蓋掉");
        Marker(h, id)!.BackgroundTasks.Should().Equal("執行指令：npm run dev");
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => host.DiscardUnclaimedCalls == 1, "接回都有結果之後清掉主機上沒人認領的程序");

        var resumed = h.Sessions[^1];
        var attach = resumed.Attaches.All.Should().ContainSingle().Subject;
        attach.State.Value<string>("Instance").Should().Be("a1");
        attach.Settings.ProcessHost.Should().BeSameAs(host, "接回的程序是主機的：設定帶著主機");
        resumed.HasLiveProcess.Should().BeTrue();
        SentTexts(h).Should().HaveCount(sentBefore, "背景工作沒有中斷：不必請代理確認");
        h.IsRunning(id).Should().BeFalse();
        host.Discarded.Count.Should().Be(0);
        var marker = Marker(h, id)!;
        marker.ClosedByApp.Should().BeFalse("接回了：改回平常的等背景工作");
        marker.WaitingBackground.Should().BeTrue();
        marker.BackgroundTasks.Should().Equal("執行指令：npm run dev");
        h.OnUi(() => h.Hub.BackgroundWorkRunning).Should().BeFalse("還是放得開");

        // 背景工作跑完，CLI 自己開一輪回報：照常
        resumed.HasLiveBackgroundWork = false;
        resumed.LiveBackgroundTools = [];
        h.Raise(resumed, new AgentBackgroundWorkEvent(0, []));
        h.Raise(resumed, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        h.IsRunning(id).Should().BeTrue();
        h.CompleteTurn(resumed);
        RunningTurnStore.TryLoad(h.Store.GetConversationDirectory(id)).Record.Should().BeNull("這一輪結束、沒有背景工作了");

        // 網頁重新載入：不再接回、不再清
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();
        resumed.Attaches.Count.Should().Be(1);
        host.DiscardUnclaimedCalls.Should().Be(1);
    }

    [Fact]
    public async Task 新版啟動_接不回去_結束留在主機的程序_照running請代理確認背景工作()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, _) = WaitingBackground(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = Resume(h);
        var sentBefore = SentTexts(h).Count;

        var host = await RestartWithResumeAsync(h, file, s => s.FailAttach = true);
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => SentTexts(h).Count == sentBefore + 1, "接不回去：照上次結束時停掉了背景工作接續");

        var text = SentTexts(h).Last();
        text.Should().StartWith("（系統通知：Clode Myriagent 上次結束時停掉了 Claude Code 還在跑的背景工作");
        text.Should().Contain("正在等背景工作〈執行指令：npm run dev〉回報");
        h.IsRunning(id).Should().BeTrue();
        await h.WaitUntilAsync(() => host.Discarded.Count == 1, "留在主機的那個程序沒有人要了");
        host.Discarded.All.Single().Should().Be(new AgentProcessDetachState(7, 4242, 10, 0, "a1", 0));
        await h.WaitUntilAsync(() => host.DiscardUnclaimedCalls == 1, "最後清掉沒人認領的");
    }

    [Fact]
    public async Task 接回途中的送出_先排隊_接回之後用接回的程序送()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, _) = WaitingBackground(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = Resume(h);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await RestartWithResumeAsync(h, file, s => s.AttachGate = gate);
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();
        var resumed = h.Sessions[^1];
        resumed.Attaches.Count.Should().Be(1);

        h.Send(h.Main, id, "接回途中送出");
        await h.Ui.DrainAsync();
        resumed.Sent.Count.Should().Be(0, "會話這時沒有程序：送出會另開一個，和主機那邊的舊程序搶同一個對話");
        h.Queue(id).Should().Equal("接回途中送出");

        gate.SetResult();
        await h.WaitUntilAsync(() => resumed.Sent.Count == 1, "接回之後照常送出");
        resumed.Sent.All.Single().Input.Text.Should().Be("接回途中送出");
        h.Sessions.Count(s => s.Attaches.Count > 0).Should().Be(1, "沒有另開會話");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public async Task 放棄這次更新_就地接回_背景工作照跑_不送接續訊息()
    {
        var (h, host) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = WaitingBackground(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.HasLiveProcess.Should().BeFalse();
        var sentBefore = SentTexts(h).Count;

        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("使用者取消"));
        await h.Ui.DrainAsync();

        s.Attaches.All.Should().ContainSingle().Which.State.Value<string>("Instance").Should().Be("a1");
        s.HasLiveProcess.Should().BeTrue();
        s.HasLiveBackgroundWork.Should().BeTrue();
        SentTexts(h).Should().HaveCount(sentBefore, "背景工作沒有中斷");
        h.IsRunning(id).Should().BeFalse();
        h.OnUi(() => h.Hub.DetachedProcessCount).Should().Be(0);
        host.Discarded.Count.Should().Be(0);
        host.DiscardUnclaimedCalls.Should().Be(0, "放棄更新不清主機：只有新的 app 啟動時清");
        var marker = Marker(h, id)!;
        marker.ClosedByApp.Should().BeFalse();
        marker.BackgroundTasks.Should().Equal("執行指令：npm run dev");
        State(h, id).Should().BeNull("沒有排定的換版了");

        // 下一次更新照樣放得開
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.Detaches.Should().Be(2);
    }

    [Fact]
    public async Task 放棄這次更新_就地接不回去_請代理確認被停掉的背景工作()
    {
        var (h, host) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = WaitingBackground(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        s.FailAttach = true;
        var sentBefore = SentTexts(h).Count;

        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("使用者取消"));
        await h.WaitUntilAsync(() => SentTexts(h).Count == sentBefore + 1, "接不回去：就地請代理確認背景工作");

        var text = SentTexts(h).Last();
        text.Should().StartWith("（系統通知：Clode Myriagent 為了套用自身的更新而停下了 Claude Code，現在重新啟動了它");
        text.Should().Contain("正在等背景工作〈執行指令：npm run dev〉回報");
        h.IsRunning(id).Should().BeTrue();
        await h.WaitUntilAsync(() => host.Discarded.Count == 1, "留在主機的那個程序結束掉");
    }

    [Fact]
    public async Task 放開途中放棄更新_放開有結果後馬上接回()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, s) = WaitingBackground(h);
        s.DetachGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var prepare = Prepare(h);
        await h.Ui.DrainAsync();
        prepare.IsCompleted.Should().BeFalse("還在放開");
        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("使用者取消"));
        await h.Ui.DrainAsync();
        s.Attaches.Count.Should().Be(0);

        s.DetachGate.SetResult();
        await h.WaitUntilAsync(() => s.Attaches.Count == 1, "放開有了結果、更新已經放棄：馬上接回");
        await h.Ui.DrainAsync();
        s.HasLiveProcess.Should().BeTrue();
        h.OnUi(() => h.Hub.DetachedProcessCount).Should().Be(0);
        Marker(h, id)!.ClosedByApp.Should().BeFalse();
        SentTexts(h).Should().ContainSingle("只有一開始那一則：沒有送接續訊息");
    }

    /// <summary>一個回覆完、背景指令（npm test，工具卡 bg1）還在跑的 Codex 對話</summary>
    private static (string Id, FakeSession Session) CodexWaitingBackground(HubHarness h)
    {
        var tool = new AgentBackgroundTool("執行指令", "bash", "npm test", "npm test", StopId: "bg1");
        var (id, s) = h.StartConversation(h.Main, h.Codex, "跑測試");
        h.ProcessStarted(s, "c1", CliIdentities.Codex("0.160.1", fileId: 1));
        s.SucceedRestarts("c");
        s.Detachable = true;
        h.Raise(s, new AgentToolStartedEvent("bg1", "執行指令", "bash", "npm test", "npm test"));
        s.HasLiveBackgroundWork = true;
        s.LiveBackgroundTools = [tool];
        s.LiveBackgroundToolIds = ["bg1"];
        h.Raise(s, new AgentBackgroundWorkEvent(1, [tool], ["bg1"]));
        h.Raise(s, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));
        h.CompleteTurn(s);
        s.IsRunning = false;
        return (id, s);
    }

    private static AgentChatItem Card(HubHarness h, string id) => h.Items(id).Single(i => i.Kind == AgentItemKind.Tool && i.Title == "npm test");

    [Fact]
    public async Task Codex的背景指令_卡片跨版留在執行中_放開期間跑完的結果補進卡片_請代理接著處理()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, _) = CodexWaitingBackground(h);
        Card(h, id).Status.Should().Be(AgentItemStatus.Running, "背景指令的卡片留在執行中");
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = Resume(h);
        var cardId = Card(h, id).Id;
        file.Attached.Should().ContainSingle().Which.Cards.Should().Equal(new Dictionary<string, string> { ["bg1"] = cardId });
        var sentBefore = SentTexts(h).Count;

        // 新版接上時，放開期間跑完的 item/completed 馬上補送進來
        var host = await RestartWithResumeAsync(h, file, s => s.AfterAttach = attached =>
        {
            attached.HasLiveBackgroundWork = false;
            attached.LiveBackgroundTools = [];
            attached.Raise(new AgentToolCompletedEvent("bg1", AgentItemStatus.Done, "all passed", Array.Empty<string>()));
            attached.Raise(new AgentBackgroundWorkEvent(0, [], []));
            attached.Raise(new AgentBackgroundCommandFinishedEvent("bg1", "npm test", AgentItemStatus.Done, 0, TimeSpan.FromSeconds(4)));
        });
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => SentTexts(h).Count == sentBefore + 1, "背景指令跑完：請代理接著處理");

        var card = Card(h, id);
        card.Id.Should().Be(cardId);
        card.Status.Should().Be(AgentItemStatus.Done, "不是「被中斷、結果未知」：結果寫進了原本那張卡");
        card.Output.Should().Be("all passed");
        SentTexts(h).Last().Should().Contain("npm test");
        h.Sessions[^1].Attaches.Count.Should().Be(1);
        host.Discarded.Count.Should().Be(0);
    }

    [Fact]
    public async Task Codex的背景指令_接回後還在跑_卡片是執行中_接不回去的照舊算被中斷()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var (id, _) = CodexWaitingBackground(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var file = Resume(h);

        var host = await RestartWithResumeAsync(h, file);
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => host.DiscardUnclaimedCalls == 1, "接回有了結果");
        Card(h, id).Status.Should().Be(AgentItemStatus.Running, "背景指令還在跑");
        var resumed = h.Sessions[^1];

        // 之後跑完：照常結束那張卡
        resumed.HasLiveBackgroundWork = false;
        resumed.LiveBackgroundTools = [];
        h.Raise(resumed, new AgentToolCompletedEvent("bg1", AgentItemStatus.Failed, "1 failed", Array.Empty<string>()));
        h.Raise(resumed, new AgentBackgroundWorkEvent(0, [], []));
        Card(h, id).Status.Should().Be(AgentItemStatus.Failed);
        RunningTurnStore.TryLoad(h.Store.GetConversationDirectory(id)).Record.Should().BeNull("背景指令都結束了：不留等背景工作的標記");

        // 另一個對話：接不回去
        var (lostId, _) = CodexWaitingBackground(h);
        (await Prepare(h).WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();
        var second = Resume(h);
        second.Attached.Select(a => a.Id).Should().Equal(lostId);
        await RestartWithResumeAsync(h, second, s => s.FailAttach = true);
        var sentBefore = SentTexts(h).Count;
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => SentTexts(h).Count == sentBefore + 1, "接不回去：請代理確認背景工作");
        Card(h, lostId).Status.Should().Be(AgentItemStatus.Cancelled, "那個指令跟著程序沒了");
    }

    [Fact]
    public async Task 沒有要接回的_新的app啟動時清掉主機上沒人認領的程序_只做一次()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var host = await RestartWithResumeAsync(h, file: null);

        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => host.DiscardUnclaimedCalls == 1, "一般啟動：上一個 app 留下的主機手上的程序都沒人要");
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();
        await Task.Delay(100);
        host.DiscardUnclaimedCalls.Should().Be(1);
    }

    [Fact]
    public async Task resume列的對話不在了_那個程序結束掉()
    {
        var (h, _) = await NewHarnessAsync();
        using var _h = h;
        var file = new AppResumeFile
        {
            Token = "tok", From = "0.2.1-aaaaaa1", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attached =
            {
                new ResumeAttached
                {
                    Id = "gone",
                    State = new JObject { ["Process"] = JObject.FromObject(new AgentProcessDetachState(3, 99, 0, 0, "x1", 0)) },
                },
                new ResumeAttached { Id = "broken", State = null },
            },
        };
        var host = await RestartWithResumeAsync(h, file);

        h.Command(h.Main, "ready");

        await h.WaitUntilAsync(() => host.Discarded.Count == 1 && host.DiscardUnclaimedCalls == 1, "對話已刪除：留在主機的程序沒有人要");
        host.Discarded.All.Single().ProcessId.Should().Be(99);
    }
}
