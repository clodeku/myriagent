using Myriagent.Agents;
using Myriagent.Background;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 背景活動日誌在中樞的掛鉤（AgentHub.Journal.cs，docs/background-journal.md）：背景工作、子代理、Claude 的工作、Codex 的背景指令、程序結束、
/// 一輪的開始與結束、背景程式、app 結束。換版排空的部分在 AgentHubAppUpdateTests
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubJournalTests
{
    private static (HubHarness H, FakeJournal Journal) Create()
    {
        var h = new HubHarness();
        var journal = new FakeJournal();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundJournal(journal));
        return (h, journal);
    }

    private static readonly AgentBackgroundTool Dev = new("執行指令", "bash", "npm run dev", "npm run dev -- --port 5173", "b1", "b1");
    private static readonly AgentBackgroundTool Watch = new("執行指令", "bash", "until grep -q done x; do sleep 5; done", null, "b2", "b2");

    [Fact]
    public async Task 背景工作的開始與結束_帶對話與跑了多久_一輪的開始與結束帶還在跑的背景工作()
    {
        var (h, journal) = Create();
        using var _ = h;
        var (id, s) = h.StartConversation(h.Main, h.Claude, "開伺服器");
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(1, [Dev]));
        h.CompleteTurn(s);
        h.Raise(s, new AgentBackgroundWorkEvent(2, [Dev, Watch]));
        await Task.Delay(30);
        h.Raise(s, new AgentBackgroundWorkEvent(1, [Watch]));
        s.HasLiveBackgroundWork = false;
        h.Raise(s, new AgentBackgroundWorkEvent(0, []));
        await h.Ui.DrainAsync();

        var starts = journal.Of("bg.start");
        starts.Select(e => e.Value<string>("taskId")).Should().Equal("b1", "b2");
        starts[0].Value<string>("conv").Should().Be(id);
        starts[0].Value<string>("prov").Should().Be("Claude");
        starts[0].Value<string>("detail").Should().Be("npm run dev -- --port 5173");
        starts[0].Value<bool>("running").Should().BeTrue("這一輪還在跑時開始的");
        starts[1].Value<bool>("running").Should().BeFalse();
        var ends = journal.Of("bg.end");
        ends.Select(e => e.Value<string>("taskId")).Should().Equal("b1", "b2");
        ends[0].Value<double>("sec").Should().BeGreaterThanOrEqualTo(0);
        ends[1].Value<int>("count").Should().Be(0);

        var turnEnd = journal.Of("turn.end").Should().ContainSingle().Subject;
        turnEnd.Value<string>("outcome").Should().Be("Completed");
        turnEnd.Value<int>("bgCount").Should().Be(1, "這一輪結束時還在等 npm run dev");
        turnEnd["bg"]![0]!.Value<string>("title").Should().Be("npm run dev");
        journal.Events("turn.", "bg.").Should().Equal("turn.start", "bg.start", "turn.end", "bg.start", "bg.end", "bg.end");
    }

    [Fact]
    public async Task 對不到工具的背景工作只有數量變了_記bg_count()
    {
        var (h, journal) = Create();
        using var _ = h;
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(1, []));
        h.Raise(s, new AgentBackgroundWorkEvent(2, []));
        h.Raise(s, new AgentBackgroundWorkEvent(2, []));
        await h.Ui.DrainAsync();
        journal.Of("bg.count").Select(e => (e.Value<int>("before"), e.Value<int>("count"))).Should().Equal((0, 1), (1, 2));
        journal.Of("bg.start").Should().BeEmpty();
    }

    [Fact]
    public async Task 子代理的開始與結束_程序結束時還開著的記成跟著結束_帶錯誤輸出()
    {
        var (h, journal) = Create();
        using var _ = h;
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentToolStartedEvent("t1", "子代理", "agent", "研究 API", "請讀 docs 並整理"));
        h.Raise(s, new AgentToolStartedEvent("t2", "執行指令", "bash", "ls", "ls"));
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "整理好了", []));
        h.Raise(s, new AgentSubagentUsageEvent(new SubagentUsage("t1", 12_345, 7, 4_500, "Read", Done: true)));
        h.Raise(s, new AgentToolStartedEvent("t3", "子代理", "agent", "寫測試", "補測試"));
        h.Raise(s, new AgentProcessExitedEvent(1, "fatal: boom", Expected: false, "p1"));
        await h.Ui.DrainAsync();

        journal.Of("sub.start").Select(e => e.Value<string>("toolId")).Should().Equal("t1", "t3");
        journal.Of("sub.start")[0].Value<string>("prompt").Should().Be("請讀 docs 並整理");
        var ends = journal.Of("sub.end");
        ends.Select(e => (e.Value<string>("toolId"), e.Value<string>("status"))).Should().Equal(("t1", "Done"), ("t3", "processExited"));
        ends[0].Value<string>("result").Should().Be("整理好了");
        var usage = journal.Of("sub.usage").Should().ContainSingle().Subject;
        (usage.Value<long>("tokens"), usage.Value<int>("toolUses"), usage.Value<double>("sec")).Should().Be((12_345L, 7, 4.5));
        var exit = journal.Of("proc.exit").Should().ContainSingle().Subject;
        exit.Value<bool>("expected").Should().BeFalse();
        exit.Value<string>("stderr").Should().Be("fatal: boom");
        exit["subagents"]!.Values<string>().Should().Equal("寫測試");
    }

    /// <summary>
    /// 中樞自己關掉程序（2026-10-27：會話這時不發 AgentProcessExitedEvent，以前日誌裡只看得到之後 CLI 重開時回報「被上一個程序丟下的背景工作」）：
    /// 閒置回收、換帳戶、刪除各記一筆 proc.close 與原因；沒有活著的程序不記；還開著的子代理記成跟著結束
    /// </summary>
    [Fact]
    public async Task 中樞自己關掉程序_記proc_close與原因_沒有活著的程序不記()
    {
        var (h, journal) = Create();
        using var _ = h;
        var (idleId, idle) = h.StartConversation(h.Main, h.Claude, "閒置");
        h.CompleteTurn(idle);
        var (switchId, switching) = h.StartConversation(h.Main, h.Claude, "換帳戶");
        h.Raise(switching, new AgentToolStartedEvent("t1", "子代理", "agent", "研究 API", "請讀 docs"));
        h.CompleteTurn(switching);
        var (deleteId, deleting) = h.StartConversation(h.Main, h.Claude, "刪掉");
        h.CompleteTurn(deleting);
        deleting.LiveProcess = false;

        h.Command(h.Main, "setAccount", new { conversationId = switchId, accountId = h.Claude2.Id });
        h.Command(h.Main, "delete", new { conversationId = deleteId });
        h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now.AddMinutes(20)));
        await h.Ui.DrainAsync();

        var closes = journal.Of("proc.close");
        closes.Select(e => (e.Value<string>("conv"), e.Value<string>("why"))).Should().Equal((switchId, "accountSwitch"), (idleId, "idle"));
        closes[0].Value<string>("to").Should().Be(h.Claude2.Id);
        closes[0]["subagents"]!.Values<string>().Should().Equal("研究 API");
        closes[1].Value<double>("idleSec").Should().BeGreaterThanOrEqualTo(19 * 60, "閒置多久照維護的現在時間算");
        journal.Of("sub.end").Should().ContainSingle(e => e.Value<string>("toolId") == "t1" && e.Value<string>("status") == "processClosed");
        idle.Disposed.Should().BeFalse("閒置回收是 CloseAsync");
    }

    [Fact]
    public async Task Claude的工作與Codex的背景指令結束_CLI自己開的一輪()
    {
        var (h, journal) = Create();
        using var _ = h;
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Raise(s, new AgentTaskLifecycleEvent("b9", Ended: false, "local_bash", "跑測試", "tu1", Backgrounded: true, null, null));
        h.Raise(s, new AgentTaskLifecycleEvent("b9", Ended: true, "local_bash", null, "tu1", Backgrounded: true, "killed", "Orphaned by a previous Claude Code process exit"));
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        h.CompleteTurn(s);
        var (_, codex) = h.StartConversation(h.Main, h.Codex);
        h.Raise(codex, new AgentBackgroundCommandFinishedEvent("c1", "npm test", AgentItemStatus.Failed, 1, TimeSpan.FromSeconds(83.26)));
        await h.Ui.DrainAsync();

        var start = journal.Of("task.start").Should().ContainSingle().Subject;
        (start.Value<string>("taskId"), start.Value<string>("type"), start.Value<string>("desc"), start.Value<bool>("background")).Should().Be(("b9", "local_bash", "跑測試", true));
        var end = journal.Of("task.end").Should().ContainSingle().Subject;
        (end.Value<string>("status"), end.Value<string>("summary")).Should().Be(("killed", "Orphaned by a previous Claude Code process exit"));
        journal.Of("turn.cliStart").Should().ContainSingle().Which.Value<bool>("afterBackground").Should().BeTrue();
        journal.Of("turn.start").Should().Contain(e => e.Value<bool?>("byCli") == true);
        var finished = journal.Of("bg.finished").Should().ContainSingle().Subject;
        (finished.Value<string>("prov"), finished.Value<string>("status"), finished.Value<int>("exitCode"), finished.Value<double>("sec")).Should().Be(("Codex", "Failed", 1, 83.3));
    }

    [Fact]
    public async Task 背景程式_分類後記found_結束記exit_沒分類的短命程序不記()
    {
        var (h, journal) = Create();
        using var _ = h;
        var source = new JournalProgramSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, s) = h.StartConversation(h.Main, h.Claude, "開伺服器");
        var created = DateTime.UtcNow.AddMinutes(-3).ToFileTimeUtc();
        var server = new BackgroundProgram(4321, "node.exe", "node server.js --token=" + "ghp_" + new string('B', 36), created, 99);
        var blip = new BackgroundProgram(55, "timeout.exe", "timeout 5", DateTime.UtcNow.ToFileTimeUtc(), 99);
        source.Publish(h, Scan(id, server, blip));
        h.CompleteTurn(s);
        source.Publish(h, Scan(id, server));
        await h.Ui.DrainAsync();

        var found = journal.Of("prog.found").Should().ContainSingle().Subject;
        (found.Value<string>("conv"), found.Value<int>("pid"), found.Value<string>("class")).Should().Be((id, 4321, "confirmed"));
        found.Value<string>("cmd").Should().StartWith("node server.js").And.NotContain(new string('B', 36), "命令列的權杖遮掉");

        source.Publish(h, new BackgroundScan(new Dictionary<string, IReadOnlyList<BackgroundProgram>>(),
        [
            new BackgroundProgramExit(id, server, 0, DateTime.UtcNow.ToFileTimeUtc()),
            new BackgroundProgramExit(id, blip, 0, blip.CreatedAt + TimeSpan.FromSeconds(5).Ticks),
        ]));
        await h.Ui.DrainAsync();
        var exit = journal.Of("prog.exit").Should().ContainSingle("沒分類、只跑了 5 秒的不記").Subject;
        (exit.Value<int>("pid"), exit.Value<string>("class"), exit.Value<int>("exitCode")).Should().Be((4321, "confirmed", 0));
        exit.Value<double>("sec").Should().BeInRange(170, 200);
    }

    [Fact]
    public async Task app結束時記還在執行與在等背景工作的對話()
    {
        var (h, journal) = Create();
        var (running, _) = h.StartConversation(h.Main, h.Claude, "還在跑");
        var (waiting, s) = h.StartConversation(h.Main, h.Codex, "等背景");
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(1, [Dev]));
        h.CompleteTurn(s);
        var (idle, idleSession) = h.StartConversation(h.Main, h.Claude, "閒置");
        h.CompleteTurn(idleSession);
        await h.Ui.DrainAsync();

        h.Dispose();

        var exit = journal.Of("app.exit").Should().ContainSingle().Subject;
        var conversations = (JArray)exit["conversations"]!;
        conversations.Select(c => c.Value<string>("conv")).Should().BeEquivalentTo([running, waiting], "閒置、沒有背景工作的不列");
        _ = idle;
        var bg = conversations.Single(c => c.Value<string>("conv") == waiting);
        bg.Value<bool>("running").Should().BeFalse();
        bg["bg"]![0]!.Value<string>("taskId").Should().Be("b1");
    }

    [Fact]
    public async Task 沒接日誌時什麼都不記也不出錯()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(1, [Dev]));
        h.Raise(s, new AgentToolStartedEvent("t1", "子代理", "agent", "研究", null));
        h.CompleteTurn(s);
        await h.Ui.DrainAsync();
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
    }

    private static BackgroundScan Scan(string conversationId, params BackgroundProgram[] programs)
        => new(new Dictionary<string, IReadOnlyList<BackgroundProgram>> { [conversationId] = programs });

    private sealed class JournalProgramSource : IBackgroundProgramSource
    {
        private Action<BackgroundScan>? callback;

        public void Start(Action<BackgroundScan> onScan) => callback = onScan;

        public void RequestScan()
        {
        }

        public Task<bool> StopAsync(BackgroundProgram program) => Task.FromResult(true);

        public void Publish(HubHarness h, BackgroundScan scan)
        {
            Task.Run(() => callback!(scan)).Wait();
            h.Ui.DrainAsync().Wait();
        }

        public void Dispose()
        {
        }
    }
}
