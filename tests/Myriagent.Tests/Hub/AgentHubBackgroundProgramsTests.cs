using Myriagent.Agents;
using Myriagent.Background;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 對話開出的背景程式（AgentHub.BackgroundPrograms.cs）：CLI 的環境帶對話標記；這一輪結束了、但掃描找到這個對話脫離 CLI 的程式，
/// 帳戶卡的 sessions.programs 算它、programs 列出對話與程式；執行中的對話不算（已經算在執行中）。CLI 自己追蹤的背景工作是 sessions.background（AgentHubBackgroundTests），不算在這裡。
/// 對話的標頭 programs 也列出它們（執行中也列），programStop 停止、自己結束時對話裡加一則提示（使用者停止的不加）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubBackgroundProgramsTests
{
    private static JObject Account(JObject message, string accountId) => (JObject)message["accounts"]!.Single(a => a.Value<string>("id") == accountId);

    private static int Programs(JObject message, string accountId) => Account(message, accountId)["sessions"]!.Value<int>("programs");

    private static BackgroundScan Scan(string conversationId, params BackgroundProgram[] programs)
        => new(new Dictionary<string, IReadOnlyList<BackgroundProgram>> { [conversationId] = programs });

    [Fact]
    public void CLI的環境帶這個對話的標記_帳戶自己的環境照舊()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [BackgroundProgramRules.MarkerVariable] = "ffffffffffffffffffffffffffffffff" };
        session.Sent.All[0].Settings.ConfigureEnvironment(env);

        env[BackgroundProgramRules.MarkerVariable].Should().Be(id, "app 自己繼承到的標記換成這個對話的");
    }

    [Fact]
    public async Task 這一輪結束後還有背景程式_帳戶卡算它並列出程式_執行中不算_程式結束就清掉()
    {
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        source.Started.Should().BeTrue();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "跑批次");
        var program = new BackgroundProgram(4321, "bash.exe", "\"C:\\Program Files\\Git\\usr\\bin\\bash.exe\" chain7.sh", 1);

        var mark = h.Main.Mark();
        source.Publish(h, Scan(id, program));
        await Task.Delay(700);   // 帳戶卡最多節流 500 毫秒
        h.Main.Messages("accounts", mark).Should().NotContain(m => Programs(m, h.Claude.Id) != 0, "還在執行中：算在執行中");

        mark = h.Main.Mark();
        h.CompleteTurn(session);
        source.Requests.Should().Be(1, "一輪結束時要求立刻重掃（代理可能剛開了程式）");
        await Task.Delay(700);
        h.Main.Messages("accounts", mark).Should().NotContain(m => Programs(m, h.Claude.Id) != 0, "執行途中的掃描不算撐過了一輪，要等結束之後的掃描");

        mark = h.Main.Mark();
        source.Publish(h, Scan(id, program));
        var shown = await h.Main.WaitForAsync("accounts", m => Programs(m, h.Claude.Id) == 1, mark);
        var detail = (JArray)Account(shown, h.Claude.Id)["programs"]!;
        detail.Should().ContainSingle();
        detail[0].Value<string>("conversationId").Should().Be(id);
        detail[0]["programs"]!.Values<string>().Should().Equal("bash chain7.sh");
        Account(shown, h.Claude.Id)["sessions"]!.Value<int>("background").Should().Be(0, "不是 CLI 追蹤的背景工作，不算等背景工作回報");
        Account(shown, h.Codex.Id)["sessions"]!.Value<int>("programs").Should().Be(0);
        Account(shown, h.Codex.Id).ContainsKey("programs").Should().BeFalse("沒有背景程式的帳戶不帶說明");

        mark = h.Main.Mark();
        source.Publish(h, BackgroundScan.Empty);
        await h.Main.WaitForAsync("accounts", m => Programs(m, h.Claude.Id) == 0 && Account(m, h.Claude.Id)["programs"] == null, mark);
    }

    [Fact]
    public async Task CLI自己追蹤的背景工作只算等背景工作回報_同一個對話另有脫離CLI的程式時兩個都算()
    {
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(session);

        // 同真的會話：有背景工作時 HasLiveBackgroundWork 也是 true（AgentHubBackgroundTests）
        var mark = h.Main.Mark();
        session.HasLiveBackgroundWork = true;
        h.Raise(session, new AgentBackgroundWorkEvent(1, [new("執行指令", "bash", "npm run dev", null)]));
        var shown = await h.Main.WaitForAsync("accounts", m => Account(m, h.Claude.Id)["sessions"]!.Value<int>("background") == 1, mark);
        Programs(shown, h.Claude.Id).Should().Be(0, "CLI 底下的程序不是背景程式");
        Account(shown, h.Claude.Id).ContainsKey("programs").Should().BeFalse();

        mark = h.Main.Mark();
        source.Publish(h, Scan(id, new BackgroundProgram(77, "python.exe", "python worker6.py", 1)));
        shown = await h.Main.WaitForAsync("accounts", m => Programs(m, h.Claude.Id) == 1, mark);
        Account(shown, h.Claude.Id)["sessions"]!.Value<int>("background").Should().Be(1);
        Account(shown, h.Claude.Id)["programs"]![0]!["programs"]!.Values<string>().Should().Equal("python worker6.py");
    }

    private static JArray? HeaderPrograms(JObject message) => message["header"]!["programs"] as JArray;

    [Fact]
    public async Task 標頭列出撐過一輪的程式_之後的輪次執行中也列_沒有了是null()
    {
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude, "開伺服器");
        var created = new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc).ToFileTimeUtc();
        var program = new BackgroundProgram(4321, "node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" server.js --port 5173", created, 99);

        var mark = h.Main.Mark();
        source.Publish(h, Scan(id, program));
        await Task.Delay(150);
        h.Main.Messages("header", mark, id).Should().NotContain(m => HeaderPrograms(m) != null, "這一輪開出的還沒撐過這一輪：可能是前景指令");

        h.CompleteTurn(session);
        mark = h.Main.Mark();
        source.Publish(h, Scan(id, program));
        var header = await h.Main.WaitForAsync("header", m => HeaderPrograms(m) != null, mark, conversationId: id);
        var listed = HeaderPrograms(header)!.Should().ContainSingle().Subject;
        listed.Value<string>("id").Should().Be(program.Id);
        listed.Value<int>("pid").Should().Be(4321);
        listed.Value<string>("name").Should().Be("node.exe");
        listed.Value<string>("command").Should().Be("node server.js --port 5173");
        listed.Value<long>("startedAt").Should().Be(new DateTimeOffset(2026, 10, 24, 1, 2, 3, TimeSpan.Zero).ToUnixTimeMilliseconds());

        await Task.Delay(150);
        mark = h.Main.Mark();
        source.Publish(h, Scan(id, program));
        await Task.Delay(150);
        h.Main.Messages("header", mark, id).Should().BeEmpty("清單沒變不重送標頭");

        mark = h.Main.Mark();
        h.Send(h.Main, id, "第二則");
        source.Publish(h, Scan(id, program));
        header = await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), mark, conversationId: id);
        HeaderPrograms(header).Should().ContainSingle("之前的輪次留下的，執行中也列（只有帳戶卡把執行中的算成執行中）");

        mark = h.Main.Mark();
        source.Publish(h, BackgroundScan.Empty);
        await h.Main.WaitForAsync("header", m => m["header"]!["programs"]!.Type == JTokenType.Null, mark, conversationId: id);
    }

    [Fact]
    public void 程式自己結束_對話裡加提示_使用者停止的不加()
    {
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(session);
        var created = new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var ended = new BackgroundProgram(11, "python.exe", "python worker6.py", created, 9);
        var stopped = new BackgroundProgram(12, "ping.exe", "ping -t 127.0.0.1", created, 9);
        source.Publish(h, Scan(id, ended, stopped));

        source.Publish(h, new BackgroundScan(new Dictionary<string, IReadOnlyList<BackgroundProgram>>(), [
            new BackgroundProgramExit(id, ended, 1, created + TimeSpan.FromMinutes(3).Ticks),
            new BackgroundProgramExit(id, stopped, 1, created + TimeSpan.FromMinutes(4).Ticks, StoppedByUser: true),
            new BackgroundProgramExit("ffffffffffffffffffffffffffffffff", ended, 0, null),
        ]));

        var notices = h.Items(id).Where(i => i.NoticeKind == AgentNoticeKind.BackgroundProgram).ToList();
        notices.Should().ContainSingle("使用者自己停止的不加；不認得的對話忽略")
            .Which.Text.Should().Be("代理另開的程式「python worker6.py」已結束（結束碼 1，執行了 3 分鐘）");
        notices[0].Kind.Should().Be(AgentItemKind.Notice);
        session.Sent.All.Should().HaveCount(1, "不叫回代理（只送過第一則）");
    }

    private static BackgroundScan Exited(string conversationId, BackgroundProgram program, int code)
        => new(new Dictionary<string, IReadOnlyList<BackgroundProgram>>(), [new BackgroundProgramExit(conversationId, program, code, program.CreatedAt + TimeSpan.FromSeconds(20).Ticks)]);

    [Fact]
    public async Task 這一輪開出又在這一輪結束的程式_不列也不提示()
    {
        // 2026-10-24 核對：代理在前景跑 timeout 90 node x.mjs，Git Bash 的 timeout 讓父子關係斷掉，
        // 以前執行途中就列成「另開的程式」，跑完加一則「已結束」（192 則提示裡 132 則在執行途中）
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        var foreground = new BackgroundProgram(31, "node.exe", "node C:/Users/U/AppData/Local/Temp/x/inspect2.mjs", 5000, 30);

        var mark = h.Main.Mark();
        source.Publish(h, Scan(id, foreground));
        source.Publish(h, Exited(id, foreground, 0));
        h.CompleteTurn(session);
        source.Publish(h, BackgroundScan.Empty);
        await Task.Delay(150);

        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundProgram);
        h.Main.Messages("header", mark, id).Should().NotContain(m => HeaderPrograms(m) != null);
    }

    [Fact]
    public async Task 執行途中拍的掃描在這一輪結束後才送到_不算撐過了一輪()
    {
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        var program = new BackgroundProgram(41, "node.exe", "node build.js", 5000, 40);
        var takenWhileRunning = Scan(id, program);

        h.CompleteTurn(session);
        var mark = h.Main.Mark();
        source.Publish(h, takenWhileRunning);
        await Task.Delay(700);
        h.Main.Messages("accounts", mark).Should().NotContain(m => Programs(m, h.Claude.Id) != 0, "那時這一輪還沒結束：可能是前景指令");
        source.Publish(h, Exited(id, program, 0));
        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundProgram);
    }

    [Fact]
    public async Task 對得上CLI背景工作的不算背景程式_結束也不提示()
    {
        // CLI 以 run_in_background 跑 timeout 600 node live-probe.mjs：父子關係斷掉、這一輪結束了還在跑，但它是等背景工作回報（虛線圈），
        // 算成另開的程式會重複計算，結束時 CLI 叫回代理、Myriagent 又加一則「已結束」
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        session.HasLiveBackgroundWork = true;
        h.Raise(session, new AgentBackgroundWorkEvent(1, [new("執行指令", "bash", "cd /c/repo/x/tools/ui-probe && timeout 600 node live-probe.mjs > /tmp/live.log 2>&1", null)]));
        h.CompleteTurn(session);
        var task = new BackgroundProgram(51, "node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" live-probe.mjs", 5000, 50);
        var detached = new BackgroundProgram(52, "python.exe", "python worker6.py", 5001, 50);

        var mark = h.Main.Mark();
        source.Publish(h, Scan(id, task, detached));
        var shown = await h.Main.WaitForAsync("accounts", m => Programs(m, h.Claude.Id) == 1, mark);
        Account(shown, h.Claude.Id)["sessions"]!.Value<int>("background").Should().Be(1);
        Account(shown, h.Claude.Id)["programs"]![0]!["programs"]!.Values<string>().Should().Equal("python worker6.py");

        // CLI 先回報背景工作結束、程式才在下一次掃描消失：照樣不提示
        session.HasLiveBackgroundWork = false;
        h.Raise(session, new AgentBackgroundWorkEvent(0, []));
        source.Publish(h, new BackgroundScan(new Dictionary<string, IReadOnlyList<BackgroundProgram>> { [id] = [detached] },
            [new BackgroundProgramExit(id, task, 0, task.CreatedAt + TimeSpan.FromMinutes(4).Ticks)]));
        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundProgram);
    }

    [Fact]
    public async Task 根結束了_子程序接著當根_執行中也跟著列()
    {
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(session);
        var root = new BackgroundProgram(61, "cmd.exe", "cmd /c npm run dev", 5000, 60);
        var child = new BackgroundProgram(62, "node.exe", "node vite.js", 5001, 61);
        source.Publish(h, Scan(id, root));

        h.Send(h.Main, id, "第二則");
        var mark = h.Main.Mark();
        source.Publish(h, Scan(id, child));
        var header = await h.Main.WaitForAsync("header", m => HeaderPrograms(m) is { Count: 1 } list && list[0].Value<string>("id") == child.Id, mark, conversationId: id);
        header["header"]!.Value<bool>("running").Should().BeTrue();
    }

    [Fact]
    public void CLI還有背景工作在等_或CLI已經叫回代理_程式結束不提示()
    {
        // 2026-10-24 使用者要求：代理開了脫離的程式、再以 CLI 背景工作輪詢它，結束時 Myriagent 先提示、CLI 接著叫回代理，等於反應兩次
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(session);
        var chain = new BackgroundProgram(91, "node.exe", "node verify.js", 5000, 90);
        var other = new BackgroundProgram(92, "python.exe", "python job.py", 5001, 90);
        source.Publish(h, Scan(id, chain, other));

        session.HasLiveBackgroundWork = true;
        h.Raise(session, new AgentBackgroundWorkEvent(1, [new("執行指令", "bash", "until grep -q done /tmp/verify.status; do sleep 10; done", null)]));
        source.Publish(h, new BackgroundScan(new Dictionary<string, IReadOnlyList<BackgroundProgram>> { [id] = [other] },
            [new BackgroundProgramExit(id, chain, 0, chain.CreatedAt + TimeSpan.FromMinutes(9).Ticks)]));
        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundProgram, "CLI 的背景工作在等它：CLI 會叫回代理");

        // 輪詢結束、CLI 自己開一輪叫回代理：這時才看到的結束也不提示
        session.HasLiveBackgroundWork = false;
        h.Raise(session, new AgentBackgroundWorkEvent(0, []));
        h.Raise(session, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        source.Publish(h, Exited(id, other, 1));
        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundProgram, "代理正在處理背景工作的結果");
    }

    [Fact]
    public async Task 刪除對話時勾選一併停止_停止它另開的程式_沒勾不停()
    {
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (kept, s1) = h.StartConversation(h.Main, h.Claude, "留著程式");
        var (stopped, s2) = h.StartConversation(h.Main, h.Claude, "一併停止");
        h.CompleteTurn(s1);
        h.CompleteTurn(s2);
        var p1 = new BackgroundProgram(101, "node.exe", "node a.js", 5000, 100);
        var p2 = new BackgroundProgram(102, "node.exe", "node b.js", 5000, 100);
        var mark = h.Main.Mark();
        source.Publish(h, new BackgroundScan(new Dictionary<string, IReadOnlyList<BackgroundProgram>> { [kept] = [p1], [stopped] = [p2] }));
        var list = await h.Main.WaitForAsync("conversations", m => Count(m, kept) == 1 && Count(m, stopped) == 1, mark);
        Count(list, kept).Should().Be(1, "清單帶 programCount：刪除前問要不要一併停止");

        h.Command(h.Main, "delete", new { conversationId = kept });
        h.Command(h.Main, "delete", new { conversationId = stopped, stopPrograms = true });
        await source.WaitForStopsAsync(1);
        await h.Ui.DrainAsync();
        source.Stopped.Should().Equal(p2);

        static int? Count(JObject m, string id) => m["items"]!.FirstOrDefault(c => c.Value<string>("id") == id)?.Value<int>("programCount");
    }

    [Fact]
    public async Task 閒置中才開出來的程式是別的背景工作的步驟_列出但結束時不提示()
    {
        // 2026-10-24 實測：nohup bash chain.sh 這種 MSYS 的迴圈本身不帶對話標記，看得到的是它一步步開的原生程式（timeout 開的 ping），
        // 每一步都是新的根，以前每一步結束都加一則「已結束」
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(session);
        var step = new BackgroundProgram(71, "PING.EXE", "PING -n 12 127.0.0.1", DateTime.UtcNow.AddSeconds(1).ToFileTimeUtc(), 70);

        var mark = h.Main.Mark();
        source.Publish(h, Scan(id, step));
        await h.Main.WaitForAsync("accounts", m => Programs(m, h.Claude.Id) == 1, mark);
        source.Publish(h, Exited(id, step, 0));
        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundProgram, "步驟結束不是整個背景工作結束");
    }

    [Fact]
    public async Task CLI有背景工作在跑時_閒置中才開出來的程式歸CLI()
    {
        // 代理以 run_in_background 跑 bash verify.sh，裡面的 timeout 600 dotnet test 父子關係斷掉、命令列也對不上說明：
        // 這時開出來的多半是 CLI 背景工作的步驟，算成另開的程式會和虛線圈重複
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        session.HasLiveBackgroundWork = true;
        h.Raise(session, new AgentBackgroundWorkEvent(1, [new("執行指令", "bash", "bash /tmp/verify.sh > /tmp/verify.log 2>&1", null)]));
        h.CompleteTurn(session);
        var step = new BackgroundProgram(81, "dotnet.exe", "dotnet test Shop.Tests.csproj -c Release", DateTime.UtcNow.AddSeconds(1).ToFileTimeUtc(), 80);

        var mark = h.Main.Mark();
        source.Publish(h, Scan(id, step));
        var shown = await h.Main.WaitForAsync("accounts", m => Account(m, h.Claude.Id)["sessions"]!.Value<int>("background") == 1, mark);
        await Task.Delay(700);
        h.Main.Messages("accounts", mark).Should().NotContain(m => Programs(m, h.Claude.Id) != 0);
        source.Publish(h, Exited(id, step, 1));
        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundProgram);
    }

    [Fact]
    public async Task programStop_只停清單裡的_不在清單或停不了時提示()
    {
        using var h = new HubHarness();
        var source = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(session);
        var program = new BackgroundProgram(4321, "node.exe", "node server.js", 1000, 9);
        source.Publish(h, Scan(id, program));

        h.Command(h.Main, "programStop", new { conversationId = id, programId = program.Id });
        await source.WaitForStopsAsync(1);
        source.Stopped.Should().Equal(program);

        var mark = h.Main.Mark();
        h.Command(h.Main, "programStop", new { conversationId = id, programId = "4321-999" });
        (await h.Main.WaitForAsync("toast", null, mark)).Value<string>("text").Should().Contain("已經結束", "建立時間不同：不是清單裡的那個");
        source.Stopped.Should().HaveCount(1);

        mark = h.Main.Mark();
        source.StopResult = false;
        h.Command(h.Main, "programStop", new { conversationId = id, programId = program.Id });
        (await h.Main.WaitForAsync("toast", null, mark)).Value<string>("text").Should().Contain("沒有停止「node server.js」");

        h.Command(h.Main, "programStop", new { conversationId = "ffffffffffffffffffffffffffffffff", programId = program.Id });
        await h.Ui.DrainAsync();
        source.Stopped.Should().HaveCount(2, "不認得的對話不停");
    }

    [Fact]
    public async Task 沒有接掃描器_programs一律是0()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready");
        var init = await h.Main.WaitForAsync("init");
        Account(init, h.Claude.Id)["sessions"]!.Value<int>("programs").Should().Be(0);
    }

    [Fact]
    public void 中樞結束時停止掃描()
    {
        var source = new FakeBackgroundSource();
        using (var h = new HubHarness())
        {
            h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(source));
            h.Ui.Invoke(() => h.Hub.Dispose());
        }
        source.Disposed.Should().BeTrue();
    }

    private sealed class FakeBackgroundSource : IBackgroundProgramSource
    {
        private Action<BackgroundScan>? callback;

        public bool Started => callback != null;

        public bool Disposed { get; private set; }

        public int Requests { get; private set; }

        public void Start(Action<BackgroundScan> onScan) => callback = onScan;

        public void RequestScan() => Requests++;

        public List<BackgroundProgram> Stopped { get; } = [];

        public bool StopResult { get; set; } = true;

        public Task<bool> StopAsync(BackgroundProgram program)
        {
            lock (Stopped)
                Stopped.Add(program);
            return Task.Run(() => StopResult);
        }

        public async Task WaitForStopsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                lock (Stopped)
                {
                    if (Stopped.Count >= count)
                        return;
                }
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("沒有呼叫 StopAsync");
                await Task.Delay(10);
            }
        }

        /// <summary>像計時器執行緒那樣交出結果，等中樞在 UI 執行緒處理完</summary>
        public void Publish(HubHarness h, BackgroundScan scan)
        {
            Task.Run(() => callback!(scan)).Wait();
            h.Ui.DrainAsync().Wait();
        }

        public void Dispose() => Disposed = true;
    }
}
