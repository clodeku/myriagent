using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Schedules;
using Myriagent.Sync;
using Myriagent.Tests.Schedules;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 排程頁（web-protocol 的 schedules／scheduleWatch／scheduleRefresh／scheduleAction／scheduleAgent，AgentHub.Schedules.cs）：
/// 沒掛讀取器不送；讀到的分類與摘要；第一次只建基準；對話的工具指令碰到排程、一輪結束重讀，新排程記成那個對話建立的；
/// 新的一串失敗念一句、通知、自動請代理診斷（規劃模式、只診斷）、同一串不重複、設定關掉只記原因；快速操作經讀取器、選擇存起來；
/// 交給代理開草稿預先填好說明、之後沿用管理對話。讀取器是假的（不碰這台真的工作排程器）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSchedulesTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 10, 0, 0, DateTimeKind.Local);

    private static readonly ScheduleEnvironment Env = new("User", "TEST-PC", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ProgramFiles"] = @"C:\Program Files",
        ["SystemRoot"] = @"C:\Windows",
        ["APPDATA"] = @"C:\Users\User\AppData\Roaming",
        ["LOCALAPPDATA"] = @"C:\Users\User\AppData\Local",
    });

    private sealed class FakeReader : IScheduleReader
    {
        private readonly object gate = new();
        private List<ScheduledTaskInfo> tasks = new();

        public int Scans;

        public ConcurrentQueue<string> Ops { get; } = new();

        public void SetTasks(params ScheduledTaskInfo[] list)
        {
            lock (gate)
                tasks = list.ToList();
        }

        public ScheduleScan Scan(CancellationToken ct)
        {
            Interlocked.Increment(ref Scans);
            lock (gate)
                return new ScheduleScan(Now, tasks.ToList(), null, 1, Env);
        }

        public void Run(string path) => Ops.Enqueue("run " + path);

        public void Stop(string path) => Ops.Enqueue("stop " + path);

        public void SetEnabled(string path, bool enabled) => Ops.Enqueue((enabled ? "enable " : "disable ") + path);
    }

    private static ScheduledTaskInfo Job(string path, int result = ScheduleResultRules.HasNotRun, DateTime? lastRun = null, string xml = ScheduleRulesTests.TkReportSyncXml,
        bool enabled = true, string? repo = null)
        => new(path, xml, ScheduleXmlRules.Parse(xml), null, enabled ? ScheduledTaskState.Ready : ScheduledTaskState.Disabled, enabled, lastRun, result, Now.AddHours(1), 0, repo);

    private const string VendorXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>Contoso Corporation</Author></RegistrationInfo>
          <Triggers><LogonTrigger /></Triggers>
          <Actions Context="Author"><Exec><Command>C:\Program Files\Contoso\update.exe</Command></Exec></Actions>
        </Task>
        """;

    private static (HubHarness H, FakeReader Reader) Create(params ScheduledTaskInfo[] tasks)
    {
        var h = new HubHarness();
        var reader = new FakeReader();
        reader.SetTasks(tasks);
        h.Ui.Invoke(() =>
        {
            h.Hub.ScheduleClock = () => Now;
            h.Hub.ScheduleMachineName = "TEST-PC";
            h.Hub.AttachSchedules(reader, Path.Combine(h.Temp.Root, "schedules"));
        });
        return (h, reader);
    }

    /// <summary>重讀一次並等它讀完（loaded、不是讀取中、讀過的次數變多）</summary>
    private static async Task<JObject> ScanAsync(HubHarness h, FakeReader reader)
    {
        var before = Volatile.Read(ref reader.Scans);
        var mark = h.Main.Mark();
        h.Command(h.Main, "scheduleRefresh");
        return await WaitScannedAsync(h, reader, before, mark);
    }

    private static async Task<JObject> WaitScannedAsync(HubHarness h, FakeReader reader, int before, int mark)
    {
        var m = await h.Main.WaitForAsync("schedules", x => x["schedules"]?.Value<bool>("loaded") == true && x["schedules"]?.Value<bool>("scanning") == false
                                                             && Volatile.Read(ref reader.Scans) > before, mark, timeoutMs: 4000);
        return (JObject)m["schedules"]!;
    }

    private static JObject Entry(JObject schedules, string path)
        => (JObject)((JArray)schedules["entries"]!).Single(e => e.Value<string>("path") == path);

    [Fact]
    public void 沒掛讀取器_不送schedules_init是null()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "scheduleWatch", new { on = true });
        h.Command(h.Main, "scheduleRefresh");
        h.Main.Messages("schedules").Should().BeEmpty();
        var other = h.AddView("other");
        h.Command(other, "ready");
        other.Last("init")!["schedules"]!.Type.Should().Be(JTokenType.Null);
        other.Last("init")!["settings"]!["scheduleAutoDiagnose"]!.Type.Should().Be(JTokenType.Null, "設定頁不顯示");
    }

    [Fact]
    public async Task 讀取_分類_第一次只建基準()
    {
        var (h, reader) = Create(Job(@"\TkReportSync", 0, Now.AddMinutes(-40), repo: @"C:\repo\sample-project"), Job(@"\Vendor", 0, Now.AddHours(-2), VendorXml));
        using var dispose = h;
        var s = await ScanAsync(h, reader);

        s.Value<string>("machine").Should().Be("TEST-PC");
        var tk = Entry(s, @"\TkReportSync");
        tk.Value<string>("origin").Should().Be("suspected");
        tk.Value<bool>("managed").Should().BeFalse();
        tk.Value<string>("trigger").Should().Be("每小時（00:20 起）");
        tk.Value<string>("workFolder").Should().Be(@"C:\repo\sample-project");
        tk["last"]!.Value<string>("level").Should().Be("ok");
        ((JArray)tk["runs"]!).Should().ContainSingle("第一次看到的上次執行也記一筆");
        ((JArray)tk["upcoming"]!).Should().HaveCount(24);
        ((JArray)tk["changes"]!).Should().BeEmpty("第一次只建基準");
        Entry(s, @"\Vendor").Value<string>("origin").Should().Be("other");
        s.Value<int>("failing").Should().Be(0);

        // init 帶目前的結果；彈出視窗沒有；設定頁有自動診斷的開關
        var other = h.AddView("other");
        h.Command(other, "ready");
        ((JArray)other.Last("init")!["schedules"]!["entries"]!).Should().HaveCount(2);
        other.Last("init")!["settings"]!.Value<bool>("scheduleAutoDiagnose").Should().BeTrue();

        // 刪掉一個：記成刪除
        reader.SetTasks(Job(@"\TkReportSync", 0, Now.AddMinutes(-40)));
        s = await ScanAsync(h, reader);
        ((JArray)s["entries"]!).Should().ContainSingle();
        var state = JObject.Parse(File.ReadAllText(Path.Combine(h.Temp.Root, "schedules", "state.json")));
        state["Changes"]!.Single()["Kind"]!.ToString().Should().Be("deleted");
    }

    [Fact]
    public async Task 對話的指令碰到排程_一輪結束重讀_新排程記成那個對話建立的()
    {
        var (h, reader) = Create(Job(@"\TkReportSync"));
        using var dispose = h;
        await ScanAsync(h, reader);

        var (id, s) = h.StartConversation(h.Main, h.Claude, "幫我建一個每小時的排程");
        reader.SetTasks(Job(@"\TkReportSync"), Job(@"\Myriagent\NewJob"));
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "schtasks /create /tn \"\\Myriagent\\NewJob\" /sc hourly /tr run.cmd", null));
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "成功", Array.Empty<string>()));
        var before = Volatile.Read(ref reader.Scans);
        var mark = h.Main.Mark();
        h.CompleteTurn(s);
        var sched = await WaitScannedAsync(h, reader, before, mark);

        var job = Entry(sched, @"\Myriagent\NewJob");
        job.Value<string>("origin").Should().Be("agent");
        job.Value<bool>("managed").Should().BeTrue();
        job["provenance"]!.Value<string>("conversationId").Should().Be(id);
        job["provenance"]!.Value<string>("provider").Should().Be("Claude");
        job["provenance"]!.Value<bool>("exists").Should().BeTrue();
        var change = (JObject)((JArray)job["changes"]!).Single();
        change.Value<string>("kind").Should().Be("created");
        change.Value<string>("conversationId").Should().Be(id);

        // 一輪結束之後的變更不再算這個對話的（記號清掉了）
        reader.SetTasks(Job(@"\TkReportSync"), Job(@"\Myriagent\NewJob", enabled: false));
        sched = await ScanAsync(h, reader);
        var disabled = (JObject)((JArray)Entry(sched, @"\Myriagent\NewJob")["changes"]!).First();
        disabled.Value<string>("kind").Should().Be("disabled");
        disabled.Value<string>("conversationId").Should().BeNull();
    }

    [Fact]
    public async Task 新的一串失敗_念一句_通知_自動在管理對話請代理診斷_同一串不重複()
    {
        var (h, reader) = Create(Job(@"\TkReportSync", 0, Now.AddHours(-2)));
        using var dispose = h;
        await ScanAsync(h, reader);
        h.Command(h.Main, "scheduleAction", new { path = @"\TkReportSync", action = "adopt" });

        reader.SetTasks(Job(@"\TkReportSync", 1, Now.AddMinutes(-40)));
        var s = await ScanAsync(h, reader);
        var e = Entry(s, @"\TkReportSync");
        e.Value<string>("origin").Should().Be("adopted");
        e["last"]!.Value<string>("level").Should().Be("fail");
        e.Value<int>("streak").Should().Be(1);
        s.Value<int>("failing").Should().Be(1);

        h.Host.Spoken.All.Should().ContainSingle().Which.Should().Be("TEST-PC 的排程 TkReportSync 失敗，結束碼 1");
        h.Host.Notifications.All.Should().ContainSingle().Which.Title.Should().Be("排程失敗：TkReportSync");

        // 自動診斷：開了管理對話（規劃模式）、送出只診斷的說明，畫面上是簡短的那句
        await h.Ui.DrainAsync();
        var sent = await WaitUntilAsync(() => h.Sessions.LastOrDefault()?.Sent.All.FirstOrDefault().Input);
        sent!.Text.Should().Contain("只診斷、不要修改任何檔案或排程");
        sent.Text.Should().Contain(@"`\TkReportSync`");
        sent.Text.Should().Contain("<Task version=\"1.3\"");
        s = await ScanAsync(h, reader);
        var management = Entry(s, @"\TkReportSync")["management"]!;
        var convId = management.Value<string>("conversationId")!;
        management.Value<string>("title").Should().Be("排程 TkReportSync 的管理");
        h.Conversation(convId)!.PermissionMode.Should().Be(AgentPermissionMode.Plan);
        h.Items(convId).First(i => i.Kind == AgentItemKind.User).Text.Should().StartWith("（自動診斷）排程 \\TkReportSync 在 10/26 09:20 執行失敗（結束碼 1）");
        Entry(s, @"\TkReportSync")["lastDiagnosis"]!.Value<string>("conversationId").Should().Be(convId);

        // 同一串的下一次失敗：不念、不再診斷
        reader.SetTasks(Job(@"\TkReportSync", 2, Now.AddMinutes(-10)));
        s = await ScanAsync(h, reader);
        Entry(s, @"\TkReportSync").Value<int>("streak").Should().Be(2);
        h.Host.Spoken.All.Should().ContainSingle();
        h.Sessions.Should().ContainSingle("沒有再開診斷");
    }

    [Fact]
    public async Task 自動診斷關掉_只念與通知_記下原因()
    {
        var (h, reader) = Create(Job(@"\TkReportSync", 0, Now.AddHours(-2)));
        using var dispose = h;
        h.Command(h.Main, "setting", new { key = "scheduleAutoDiagnose", value = false });
        await ScanAsync(h, reader);
        h.Command(h.Main, "scheduleAction", new { path = @"\TkReportSync", action = "adopt" });
        reader.SetTasks(Job(@"\TkReportSync", 1, Now.AddMinutes(-40)));
        var s = await ScanAsync(h, reader);

        h.Host.Spoken.All.Should().ContainSingle();
        h.Sessions.Should().BeEmpty();
        Entry(s, @"\TkReportSync").Value<string>("diagnoseBlock").Should().Be("設定頁關掉了自動診斷");
        s.Value<bool>("autoDiagnose").Should().BeFalse();
    }

    [Fact]
    public async Task 疑似的排程失敗_不通知()
    {
        var (h, reader) = Create(Job(@"\TkReportSync", 0, Now.AddHours(-2)));
        using var dispose = h;
        await ScanAsync(h, reader);
        reader.SetTasks(Job(@"\TkReportSync", 1, Now.AddMinutes(-40)));
        var s = await ScanAsync(h, reader);
        Entry(s, @"\TkReportSync")["last"]!.Value<string>("level").Should().Be("fail");
        s.Value<int>("failing").Should().Be(0, "疑似的不算進頂端的數字");
        h.Host.Spoken.All.Should().BeEmpty();
        h.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task 快速操作_經讀取器做完重讀_選擇存起來()
    {
        var (h, reader) = Create(Job(@"\TkReportSync"), Job(@"\Vendor", xml: VendorXml));
        using var dispose = h;
        await ScanAsync(h, reader);

        var before = Volatile.Read(ref reader.Scans);
        var mark = h.Main.Mark();
        h.Command(h.Main, "scheduleAction", new { path = @"\TkReportSync", action = "run" });
        await WaitScannedAsync(h, reader, before, mark);
        reader.Ops.Should().ContainSingle().Which.Should().Be(@"run \TkReportSync");
        (await h.Main.WaitForAsync("toast", m => m.Value<string>("text") == "已開始執行 TkReportSync", mark)).Should().NotBeNull();

        h.Command(h.Main, "scheduleAction", new { path = @"\TkReportSync", action = "disable" });
        await WaitUntilAsync(() => reader.Ops.Count == 2 ? "ok" : null);
        reader.Ops.Last().Should().Be(@"disable \TkReportSync");

        mark = h.Main.Mark();
        h.Command(h.Main, "scheduleAction", new { path = @"\Vendor", action = "ignore" });
        var s = (JObject)h.Main.Messages("schedules", mark).Last()["schedules"]!;
        Entry(s, @"\Vendor").Value<string>("origin").Should().Be("ignored");
        h.Command(h.Main, "scheduleAction", new { path = @"\TkReportSync", action = "autoDiagnose", on = false });
        s = (JObject)h.Main.Last("schedules")!["schedules"]!;
        Entry(s, @"\TkReportSync").Value<bool>("autoDiagnose").Should().BeFalse();

        mark = h.Main.Mark();
        h.Command(h.Main, "scheduleAction", new { path = @"\NotThere", action = "run" });
        h.Main.Messages("toast", mark).Single().Value<string>("text").Should().Be("找不到這個排程，請重新掃描");

        var state = JObject.Parse(File.ReadAllText(Path.Combine(h.Temp.Root, "schedules", "state.json")));
        state["Ignored"]!.Values<string>().Should().Equal(@"\Vendor");
        state["NoAutoDiagnose"]!.Values<string>().Should().Equal(@"\TkReportSync");
    }

    [Fact]
    public async Task 交給代理_開草稿預先填好說明_之後沿用管理對話()
    {
        var (h, reader) = Create(Job(@"\TkReportSync", 1, Now.AddMinutes(-40), repo: @"C:\repo\sample-project"));
        using var dispose = h;
        await ScanAsync(h, reader);

        var mark = h.Main.Mark();
        h.Command(h.Main, "scheduleAgent", new { path = @"\TkReportSync", task = "diagnose", accountId = h.Claude2.Id, paneId = "p2" });
        var conv = h.Main.Messages("conversation", mark).Single();
        conv.Value<string>("paneId").Should().Be("p2");
        conv["header"]!.Value<bool>("isDraft").Should().BeTrue();
        var draft = conv.Value<string>("draft")!;
        draft.Should().StartWith("這是 Clode Myriagent 排程頁交給你的工作，對象是這台電腦（TEST-PC）的 Windows 排程。");
        draft.Should().Contain("最近一次執行失敗（10/26 09:20 的結束碼 1）");
        draft.Should().Contain("刪除排程、改執行身分或提高權限之前，先問我");
        var id = conv.Value<string>("conversationId")!;
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id);
        h.Conversation(id)!.Title.Should().Be("排程 TkReportSync 的管理");

        // 送出之後是正式對話；再交給代理就開它（不管選哪個帳戶），輸入框填新的說明
        h.Send(h.Main, id, draft);
        mark = h.Main.Mark();
        h.Command(h.Main, "scheduleAgent", new { path = @"\TkReportSync", task = "explain", accountId = h.Claude.Id, paneId = "p1" });
        var again = h.Main.Messages("conversation", mark).Single();
        again.Value<string>("conversationId").Should().Be(id);
        again.Value<string>("draft").Should().Contain("請說明這個排程在做什麼");

        // 新增排程：選的帳戶開草稿、預先填好建立的說明
        mark = h.Main.Mark();
        h.Command(h.Main, "scheduleAgent", new { task = "create", accountId = h.Codex.Id, paneId = "p1" });
        var create = h.Main.Messages("conversation", mark).Single();
        create.Value<string>("draft").Should().Contain("幫我在這台電腦（TEST-PC）建立一個 Windows 排程");
        h.Conversation(create.Value<string>("conversationId")!)!.Provider.Should().Be(AgentProvider.Codex);

        // 自動診斷、未知的種類不收
        mark = h.Main.Mark();
        h.Command(h.Main, "scheduleAgent", new { path = @"\TkReportSync", task = "auto", paneId = "p1" });
        h.Main.Messages("conversation", mark).Should().BeEmpty();
    }

    [Fact]
    public async Task 排程頁開著才每三十秒讀_維護計時器()
    {
        var (h, reader) = Create(Job(@"\TkReportSync"));
        using var dispose = h;
        await ScanAsync(h, reader);
        var t = Now;
        h.Ui.Invoke(() => h.Hub.ScheduleClock = () => t);

        // 沒開排程頁：30 秒後的維護不讀
        t = Now.AddSeconds(31);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        reader.Scans.Should().Be(1);

        // 開著（scheduleWatch）：剛讀過 30 秒內只重送；之後的維護就讀
        var mark = h.Main.Mark();
        t = Now.AddSeconds(5);
        h.Command(h.Main, "scheduleWatch", new { on = true });
        h.Main.Messages("schedules", mark).Should().NotBeEmpty();
        reader.Scans.Should().Be(1);
        t = Now.AddSeconds(31);
        var before = reader.Scans;
        mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await WaitScannedAsync(h, reader, before, mark);

        // 關掉：5 分鐘才讀
        h.Command(h.Main, "scheduleWatch", new { on = false });
        t = Now.AddMinutes(2);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        reader.Scans.Should().Be(2);
        t = Now.AddMinutes(6);
        before = reader.Scans;
        mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await WaitScannedAsync(h, reader, before, mark);
    }

    [Fact]
    public async Task 回溯_對話紀錄裡建立排程的指令_連到工作階段_對得上Myriagent對話的連到對話()
    {
        var (h, reader) = Create(Job(@"\TkReportSync"), Job(@"\FileMirrorSync"));
        using var dispose = h;
        await ScanAsync(h, reader);
        var (id, _) = h.StartConversation(h.Main, h.Claude, "以前建的");
        var session = h.Conversation(id)!.SessionId!;
        var claude = Path.Combine(h.Temp.Root, "claude-config");
        var project = Path.Combine(claude, "projects", "C--repo-x");
        Directory.CreateDirectory(project);
        string Line(string cmd) => "{\"type\":\"assistant\",\"timestamp\":\"2026-07-30T02:27:49Z\",\"cwd\":\"C:\\\\repo\\\\tk\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"" + cmd + "\"}}]}}\n";
        File.WriteAllText(Path.Combine(project, "aaaa-external.jsonl"), Line("schtasks /create /tn TkReportSync /sc hourly"));
        File.WriteAllText(Path.Combine(project, session + ".jsonl"), Line("schtasks /create /tn FileMirrorSync /sc daily"));
        h.Ui.Invoke(() => h.Hub.AttachScheduleTranscripts(() => new[] { new TranscriptRoot(AgentProvider.Claude, h.Claude.Id, claude) }));

        var mark = h.Main.Mark();
        h.Command(h.Main, "scheduleRefresh");
        var m = await h.Main.WaitForAsync("schedules", x => ((JArray)x["schedules"]!["entries"]!).Any(e => e.Value<string>("path") == @"\TkReportSync" && e["provenance"] is JObject), mark, timeoutMs: 6000);
        var s = (JObject)m["schedules"]!;
        var tk = Entry(s, @"\TkReportSync");
        tk.Value<string>("origin").Should().Be("agent");
        tk["provenance"]!.Value<string>("source").Should().Be("transcript");
        tk["provenance"]!.Value<string>("title").Should().Be("Claude 工作階段・tk");
        tk["provenance"]!.Value<bool>("importable").Should().BeTrue();
        var fin = Entry(s, @"\FileMirrorSync");
        fin["provenance"]!.Value<string>("source").Should().Be("myriagent");
        fin["provenance"]!.Value<string>("conversationId").Should().Be(id, "工作階段 id 對得上 Myriagent 的對話");
        File.Exists(Path.Combine(h.Temp.Root, "schedules", "scan-index.json")).Should().BeTrue();
    }

    [Fact]
    public async Task S3_上傳的摘要_其他電腦的_遠端的立即執行與交給代理走過閘門()
    {
        using var rig = new RemoteAccountRig(status: Myriagent.Accounts.AccountStatus.Ready);
        var h = rig.H;
        var reader = new FakeReader();
        reader.SetTasks(Job(@"\TkReportSync", 1, Now.AddMinutes(-40)), Job(@"\Vendor", xml: VendorXml));
        h.Ui.Invoke(() =>
        {
            h.Hub.ScheduleClock = () => Now;
            h.Hub.ScheduleMachineName = "TEST-PC";
            h.Hub.AttachSchedules(reader, Path.Combine(h.Temp.Root, "schedules"));
        });
        await ScanAsync(h, reader);

        var snap = h.Ui.Invoke(() => h.Hub.ScheduleSyncSnapshot())!;
        ((JArray)snap["entries"]!).Select(e => e.Value<string>("path")).Should().Equal(@"\TkReportSync");
        snap.Value<int>("otherCount").Should().Be(1);
        snap["entries"]![0]!["xml"].Should().BeNull();

        var run = await rig.RunAsync(new JObject { ["cmd"] = "scheduleAction", ["deviceId"] = RemoteSamples.DeviceId, ["path"] = @"\TkReportSync", ["action"] = "run" }, tier: 2);
        run.State.Should().Be(RemoteResultStates.Done, run.Message);
        reader.Ops.Should().Contain(@"run \TkReportSync");
        var ignore = await rig.RunAsync(new JObject { ["cmd"] = "scheduleAction", ["deviceId"] = RemoteSamples.DeviceId, ["path"] = @"\TkReportSync", ["action"] = "ignore" }, tier: 2);
        ignore.State.Should().Be(RemoteResultStates.Rejected, "不再列出是這台自己的選擇，遠端不改");
        var missing = await rig.RunAsync(new JObject { ["cmd"] = "scheduleAction", ["deviceId"] = RemoteSamples.DeviceId, ["path"] = @"\Gone", ["action"] = "run" }, tier: 2);
        missing.Code.Should().Be(RemoteResultCodes.Conflict);

        var before = h.Sessions.Count;
        var agent = await rig.RunAsync(new JObject
        {
            ["cmd"] = "scheduleAgent", ["deviceId"] = RemoteSamples.DeviceId, ["path"] = @"\TkReportSync", ["task"] = "diagnose", ["note"] = "順便看一下記錄檔", ["accountId"] = null,
        }, tier: 1);
        agent.State.Should().Be(RemoteResultStates.Done, agent.Message);
        await h.WaitUntilAsync(() => h.Sessions.Count == before + 1 && h.Sessions[^1].Sent.Count == 1, "在管理對話送出");
        var text = h.Sessions[^1].Sent.All[0].Input.Text;
        text.Should().Contain("最近一次執行失敗").And.Contain("順便看一下記錄檔");
        var s = h.Ui.Invoke(() => h.Hub.RenderSchedules())!;
        Entry(s, @"\TkReportSync")["management"]!.Value<string>("title").Should().Be("排程 TkReportSync 的管理");

        h.Ui.Invoke(() => h.Hub.ApplyOtherSchedules(new[] { new DeviceSchedulesView("d_other", 5, new JObject { ["entries"] = new JArray(), ["timed"] = new JArray(), ["failing"] = 2 }) }));
        var devices = (JArray)h.Ui.Invoke(() => h.Hub.RenderSchedules())!["devices"]!;
        devices.Single().Value<string>("deviceId").Should().Be("d_other");
        devices.Single()["snapshot"]!.Value<int>("failing").Should().Be(2);
    }

    private static async Task<T?> WaitUntilAsync<T>(Func<T?> probe, int timeoutMs = 4000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (probe() is { } value)
                return value;
            if (DateTime.UtcNow > deadline)
                return null;
            await System.Threading.Tasks.Task.Delay(20);
        }
    }
}
