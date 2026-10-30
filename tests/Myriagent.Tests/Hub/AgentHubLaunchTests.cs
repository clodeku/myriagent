using System.Collections.Concurrent;
using Myriagent.Core;
using Myriagent.Launch;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 執行列（web-protocol 的 launchStatus／launchAction／launchSelect／launchLog／launchRescan）：沒掛不送；開分頁先送快取的再偵測；
/// 建置成功才執行、失敗記第一個錯誤；停止、重新建置並執行、暫停與繼續；輸出經 launchLog 拿；不認得的目標不處理；
/// 只有建置算 LaunchBuilding；中樞關閉時結束建置。執行模式：Debug（預設）的程式是 app 的子程序（中樞關閉時結束、不記進 runs.json、算 LaunchDebugging），
/// Release 的程式不結束（記進 runs.json，下次接回、重新建置並執行時先結束它）；選的專案與每個專案的模式記在 prefs.json，app 重新啟動後照舊。
/// 偵測與程序都是假的（不跑 dotnet）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubLaunchTests
{
    private sealed class FakeScanner : ILaunchScanner
    {
        public ConcurrentQueue<string> Scans { get; } = new();

        public Func<string, IReadOnlyList<LaunchTarget>> Targets { get; set; } = folder => [Target(folder)];

        public Task<IReadOnlyList<LaunchTarget>> ScanAsync(string folder, CancellationToken ct)
        {
            Scans.Enqueue(folder);
            return Task.FromResult(Targets(folder));
        }
    }

    private sealed class FakeProcess(LaunchCommand command, Action<string> onLine, string? logPath = null) : ILaunchProcess
    {
        private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LaunchCommand Command { get; } = command;

        /// <summary>執行的程式的輸出檔（建置與 Debug 模式是 null：經管線）</summary>
        public string? LogPath { get; } = logPath;

        /// <summary>Debug 模式執行的程式（RunAttached）</summary>
        public bool Attached { get; init; }

        /// <summary>Processes() 回的程序</summary>
        public IReadOnlyList<LaunchProcessRef> Refs { get; set; } = [];

        public bool IsBuild => Command.Arguments.FirstOrDefault() == "build";

        public int Kills;
        public bool Suspended;
        public bool CanSuspend = true;
        public volatile bool Disposed;

        public Task<int> Exited => exited.Task;

        public void Write(string line) => onLine(line);

        public void Exit(int code) => exited.TrySetResult(code);

        public void Kill()
        {
            Interlocked.Increment(ref Kills);
            exited.TrySetResult(1);
        }

        public bool Suspend()
        {
            Suspended = CanSuspend;
            return CanSuspend;
        }

        public bool Resume()
        {
            Suspended = false;
            return CanSuspend;
        }

        public IReadOnlyList<LaunchProcessRef> Processes() => Disposed || exited.Task.IsCompleted ? [] : Refs;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeStarter : ILaunchProcessStarter
    {
        private int nextPid = 4000;

        public ConcurrentQueue<FakeProcess> Started { get; } = new();

        public Exception? Fail { get; set; }

        /// <summary>接回時還在執行的（預設：都在）</summary>
        public Func<IReadOnlyList<LaunchProcessRef>, bool> Alive { get; set; } = _ => true;

        public ConcurrentQueue<(IReadOnlyList<LaunchProcessRef> Processes, string? LogPath, FakeProcess? Process)> Reattached { get; } = new();

        public ILaunchProcess Build(LaunchCommand command, Action<string> onLine) => Add(new FakeProcess(command, onLine));

        public ILaunchProcess RunAttached(LaunchCommand command, Action<string> onLine) => Add(new FakeProcess(command, onLine) { Attached = true });

        public ILaunchProcess Run(LaunchCommand command, string logPath, Action<string> onLine)
        {
            var p = Add(new FakeProcess(command, onLine, logPath));
            File.WriteAllText(logPath, string.Empty);
            var pid = Interlocked.Increment(ref nextPid);
            p.Refs = [new LaunchProcessRef(pid, pid * 10L)];
            return p;
        }

        public ILaunchProcess? Reattach(IReadOnlyList<LaunchProcessRef> processes, string? logPath, Action<string> onLine)
        {
            var p = Alive(processes) ? new FakeProcess(new LaunchCommand("reattached", [], string.Empty), onLine, logPath) { Refs = processes } : null;
            Reattached.Enqueue((processes, logPath, p));
            return p;
        }

        private FakeProcess Add(FakeProcess p)
        {
            if (Fail is { } ex)
                throw ex;
            Started.Enqueue(p);
            return p;
        }
    }

    private static LaunchTarget Target(string folder, string name = "App")
    {
        var path = Path.Combine(folder, name, name + ".csproj");
        return new LaunchTarget(LaunchRules.TargetId(path), LaunchKind.DotNet, name, path, Path.Combine(name, name + ".csproj"), "WinForms · net10.0-windows");
    }

    private static (HubHarness H, FakeScanner Scanner, FakeStarter Starter) Create(string? stateDirectory = null, FakeStarter? starter = null)
    {
        var h = new HubHarness();
        var scanner = new FakeScanner();
        starter ??= new FakeStarter();
        var state = stateDirectory ?? h.Temp.CreateDirectory("launch-state");
        h.Ui.Invoke(() => h.Hub.AttachLaunch(scanner, starter, state));
        return (h, scanner, starter);
    }

    private static LaunchRunsFile? ReadState(string stateDirectory)
        => JsonFile.Read<LaunchRunsFile>(Path.Combine(stateDirectory, LaunchRules.StateFileName));

    /// <summary>在 folder 開對話（兩個中樞共用的專案資料夾），等執行列出現</summary>
    private static async Task<(string Id, JObject Launch)> OpenInAsync(HubHarness h, string folder, Func<JObject, bool>? until = null)
    {
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);
        var shown = await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("folder") == folder && (until == null || until(l)));
        return (id, (JObject)shown["launch"]!);
    }

    private static List<string> LogLines(HubHarness h, string id, JObject launch)
    {
        var mark = h.Main.Mark();
        h.Command(h.Main, "launchLog", new { conversationId = id, targetId = launch.Value<string>("selected"), epoch = -1, after = 0 });
        return h.Main.Messages("launchLog", mark, id).Single()["lines"]!.Values<string>().Select(l => l!).ToList();
    }

    private static Task<JObject> WaitLaunchAsync(FakeView view, string conversationId, Func<JObject?, bool> predicate, int since = 0)
        => view.WaitForAsync("launchStatus", m => predicate(m["launch"] as JObject), since, timeoutMs: 4000, conversationId: conversationId);

    private static async Task<(string Id, JObject Launch)> OpenAsync(HubHarness h)
    {
        var id = h.NewConversation(h.Main, h.Claude);
        var shown = await WaitLaunchAsync(h.Main, id, l => l != null);
        return (id, (JObject)shown["launch"]!);
    }

    private static async Task<FakeProcess> NextAsync(HubHarness h, FakeStarter starter, int count)
    {
        await h.WaitUntilAsync(() => starter.Started.Count >= count, $"第 {count} 個程序");
        return starter.Started.ElementAt(count - 1);
    }

    private static void Act(HubHarness h, string id, JObject launch, string action)
        => h.Command(h.Main, "launchAction", new { conversationId = id, targetId = launch.Value<string>("selected"), action });

    /// <summary>選的目標換執行模式（launchSelect 帶 mode），等宿主送回</summary>
    private static async Task<JObject> SetModeAsync(HubHarness h, string id, JObject launch, string mode)
    {
        var mark = h.Main.Mark();
        h.Command(h.Main, "launchSelect", new { conversationId = id, targetId = launch.Value<string>("selected"), mode });
        return (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("mode") == mode, mark))["launch"]!;
    }

    [Fact]
    public void 沒掛_不送launchStatus()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "launchRescan", new { conversationId = id });
        h.Command(h.Main, "launchAction", new { conversationId = id, targetId = "t1", action = "start" });
        h.Main.Messages("launchStatus").Should().BeEmpty();
    }

    [Fact]
    public async Task 開分頁先送快取的_偵測完送目標()
    {
        var (h, scanner, _) = Create();
        using var dispose = h;
        var mark = h.Main.Mark();
        var id = h.NewConversation(h.Main, h.Claude);
        var all = h.Main.All().Skip(mark).Select(m => m.Value<string>("type")).ToList();
        all.IndexOf("launchStatus").Should().BeGreaterThan(all.IndexOf("conversation"), "conversation 之後才送");
        h.Main.Messages("launchStatus", mark, id)[0]["launch"]!.Type.Should().Be(JTokenType.Null, "還沒偵測過");

        var shown = (JObject)(await WaitLaunchAsync(h.Main, id, l => l != null, mark))["launch"]!;
        var folder = scanner.Scans.Single();
        folder.Should().StartWith(h.Temp.Root, "草稿的預設工作區");
        shown.Value<string>("folder").Should().Be(folder);
        shown.Value<string>("phase").Should().Be("idle");
        shown.Value<string>("selected").Should().Be(Target(folder).Id);
        shown["targets"]![0]!.Value<string>("name").Should().Be("App");

        // 再開一個視窗：直接送快取的，30 秒內不重新偵測
        var other = h.AddView("other");
        h.Command(other, "open", new { conversationId = id });
        other.Messages("launchStatus", 0, id).Should().ContainSingle().Which["launch"]!.Type.Should().Be(JTokenType.Object);
        await h.Ui.DrainAsync();
        scanner.Scans.Should().HaveCount(1);
    }

    [Fact]
    public async Task 沒有目標_launch是null_重新偵測找到了就送()
    {
        var (h, scanner, _) = Create();
        using var dispose = h;
        var found = false;
        scanner.Targets = folder => found ? [Target(folder)] : [];
        var id = h.NewConversation(h.Main, h.Claude);
        await h.WaitUntilAsync(() => scanner.Scans.Count == 1, "第一次偵測");
        await h.Ui.DrainAsync();
        h.Main.Messages("launchStatus", 0, id).Should().OnlyContain(m => m["launch"]!.Type == JTokenType.Null);

        found = true;
        var mark = h.Main.Mark();
        h.Command(h.Main, "launchRescan", new { conversationId = id });
        await WaitLaunchAsync(h.Main, id, l => l != null, mark);
    }

    [Fact]
    public async Task 建置成功才執行_程式結束記結束碼_輸出經launchLog拿()
    {
        var (h, _, starter) = Create();
        using var dispose = h;
        var (id, launch) = await OpenAsync(h);
        launch.Value<string>("mode").Should().Be("debug", "沒選過的專案是 Debug 模式");
        await SetModeAsync(h, id, launch, "release");
        var mark = h.Main.Mark();
        Act(h, id, launch, "start");

        var build = await NextAsync(h, starter, 1);
        build.IsBuild.Should().BeTrue();
        build.Command.Arguments.Should().ContainInConsecutiveOrder("-c", "Release");
        build.LogPath.Should().BeNull("建置經管線，是 app 的子程序");
        var building = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "building", mark))["launch"]!;
        building["since"]!.Type.Should().Be(JTokenType.Integer);
        h.OnUi(() => h.Hub.LaunchBuilding).Should().BeTrue("建置中 app 自我更新不自動重啟");

        build.Write("建置成功。");
        build.Exit(0);
        var run = await NextAsync(h, starter, 2);
        run.IsBuild.Should().BeFalse();
        run.Command.Arguments.Should().StartWith(["run", "--project"]);
        run.LogPath.Should().NotBeNull("Release 模式執行的程式和 app 脫鉤，輸出寫進輸出檔");
        run.Attached.Should().BeFalse();
        run.Command.Arguments.Should().ContainInConsecutiveOrder("-c", "Release");
        LaunchRules.IsLogFileName(Path.GetFileName(run.LogPath)).Should().BeTrue();
        var running = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", mark))["launch"]!;
        running.Value<string>("runMode").Should().Be("release");
        build.Disposed.Should().BeTrue();
        h.OnUi(() => h.Hub.LaunchBuilding).Should().BeFalse("執行中的程式不受 app 重啟影響");
        h.OnUi(() => h.Hub.LaunchDebugging).Should().BeFalse("Release 模式和 app 脫鉤");

        run.Write("hello from app");
        var withLog = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<long>("logSeq") >= 4, mark))["launch"]!;
        var logMark = h.Main.Mark();
        h.Command(h.Main, "launchLog", new { conversationId = id, targetId = launch.Value<string>("selected"), epoch = -1, after = 0 });
        var log = h.Main.Messages("launchLog", logMark, id).Single();
        log.Value<bool>("reset").Should().BeTrue();
        log["lines"]!.Values<string>().Should().Equal(
            "> dotnet " + string.Join(' ', build.Command.Arguments), "建置成功。", "> dotnet " + string.Join(' ', run.Command.Arguments), "hello from app");
        log.Value<long>("epoch").Should().Be(withLog.Value<long>("logEpoch"));

        run.Write("second");
        await WaitLaunchAsync(h.Main, id, l => l?.Value<long>("logSeq") >= 5, mark);
        logMark = h.Main.Mark();
        h.Command(h.Main, "launchLog", new { conversationId = id, targetId = launch.Value<string>("selected"), epoch = log.Value<long>("epoch"), after = log.Value<long>("seq") });
        var more = h.Main.Messages("launchLog", logMark, id).Single();
        more.Value<bool>("reset").Should().BeFalse();
        more["lines"]!.Values<string>().Should().Equal("second");

        run.Exit(2);
        var ended = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "idle" && l["last"] is JObject, mark))["launch"]!;
        ended["last"]!.Value<string>("kind").Should().Be("exited");
        ended["last"]!.Value<int>("code").Should().Be(2);
        ended["since"]!.Type.Should().Be(JTokenType.Null);
        File.Exists(run.LogPath).Should().BeFalse("程式結束了：輸出檔刪掉（輸出還在記憶體裡）");
        h.OnUi(() => h.Hub.LaunchBuilding).Should().BeFalse();
    }

    [Fact]
    public async Task 建置失敗_不執行_記第一個錯誤()
    {
        var (h, _, starter) = Create();
        using var dispose = h;
        var (id, launch) = await OpenAsync(h);
        var mark = h.Main.Mark();
        Act(h, id, launch, "start");
        var build = await NextAsync(h, starter, 1);
        build.Write(@"C:\x\Form1.cs(3,7): error CS1002: 必須是 ; [C:\x\App.csproj]");
        build.Exit(1);
        var failed = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?["last"] is JObject, mark))["launch"]!;
        failed.Value<string>("phase").Should().Be("idle");
        failed["last"]!.Value<string>("kind").Should().Be("buildFailed");
        failed["last"]!.Value<string>("text").Should().Be("Form1.cs(3,7): CS1002 必須是 ;");
        await h.Ui.DrainAsync();
        starter.Started.Should().HaveCount(1, "建置失敗不執行");
    }

    [Fact]
    public async Task 停止_建置中與執行中都記成已停止()
    {
        var (h, _, starter) = Create();
        using var dispose = h;
        var (id, launch) = await OpenAsync(h);

        var mark = h.Main.Mark();
        Act(h, id, launch, "start");
        var build = await NextAsync(h, starter, 1);
        Act(h, id, launch, "stop");
        build.Kills.Should().Be(1);
        var stopped = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?["last"] is JObject, mark))["launch"]!;
        stopped["last"]!.Value<string>("kind").Should().Be("stopped", "被停掉的建置不是建置失敗");
        await h.Ui.DrainAsync();
        starter.Started.Should().HaveCount(1);

        mark = h.Main.Mark();
        Act(h, id, launch, "start");
        (await NextAsync(h, starter, 2)).Exit(0);
        var run = await NextAsync(h, starter, 3);
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", mark);
        Act(h, id, launch, "start");   // 執行中按啟動：不做事
        await h.Ui.DrainAsync();
        starter.Started.Should().HaveCount(3);
        Act(h, id, launch, "stop");
        run.Kills.Should().Be(1);
        var stopped2 = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?["last"] is JObject, mark))["launch"]!;
        stopped2["last"]!.Value<string>("kind").Should().Be("stopped");
        stopped2["last"]!["code"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task 重新建置並執行_停掉後重新建置()
    {
        var (h, _, starter) = Create();
        using var dispose = h;
        var (id, launch) = await OpenAsync(h);
        var mark = h.Main.Mark();
        Act(h, id, launch, "start");
        (await NextAsync(h, starter, 1)).Exit(0);
        var run = await NextAsync(h, starter, 2);
        run.Write("first run");
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", mark);

        Act(h, id, launch, "restart");
        run.Kills.Should().Be(1);
        var rebuild = await NextAsync(h, starter, 3);
        rebuild.IsBuild.Should().BeTrue();
        var building = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "building" && l.Value<long>("logEpoch") == 2, mark))["launch"]!;
        building["last"]!.Type.Should().Be(JTokenType.Null, "重新開始時清掉上一次的結果");

        // 上一次晚到的輸出不混進這一次
        run.Write("late output");
        rebuild.Exit(0);
        await NextAsync(h, starter, 4);
        var logMark = h.Main.Mark();
        h.Command(h.Main, "launchLog", new { conversationId = id, targetId = launch.Value<string>("selected"), epoch = -1, after = 0 });
        h.Main.Messages("launchLog", logMark, id).Single()["lines"]!.Values<string>().Should().NotContain(["first run", "late output"]);
    }

    [Fact]
    public async Task 暫停與繼續_失敗時提示()
    {
        var (h, _, starter) = Create();
        using var dispose = h;
        var (id, launch) = await OpenAsync(h);
        var mark = h.Main.Mark();
        Act(h, id, launch, "start");
        (await NextAsync(h, starter, 1)).Exit(0);
        var run = await NextAsync(h, starter, 2);
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", mark);

        Act(h, id, launch, "pause");
        run.Suspended.Should().BeTrue();
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "paused", mark);
        h.OnUi(() => h.Hub.LaunchBuilding).Should().BeFalse("暫停中不是建置");
        h.OnUi(() => h.Hub.LaunchDebugging).Should().BeTrue("Debug 模式暫停中的程式也會跟著 app 結束");

        var resumeMark = h.Main.Mark();
        Act(h, id, launch, "start");   // 暫停中按啟動（F5）＝繼續
        run.Suspended.Should().BeFalse();
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", resumeMark);

        run.CanSuspend = false;
        var toastMark = h.Main.Mark();
        Act(h, id, launch, "pause");
        h.Main.Messages("toast", toastMark).Should().ContainSingle().Which.Value<string>("text").Should().Contain("無法暫停");
        await h.Ui.DrainAsync();
        h.Main.Last("launchStatus", id)!["launch"]!.Value<string>("phase").Should().Be("running");
    }

    [Fact]
    public async Task 開不起來_記成無法啟動()
    {
        var (h, _, starter) = Create();
        using var dispose = h;
        var (id, launch) = await OpenAsync(h);
        starter.Fail = new InvalidOperationException("找不到或無法執行 dotnet");
        var mark = h.Main.Mark();
        Act(h, id, launch, "start");
        var failed = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?["last"] is JObject, mark))["launch"]!;
        failed.Value<string>("phase").Should().Be("idle");
        failed["last"]!.Value<string>("kind").Should().Be("startFailed");
        failed["last"]!.Value<string>("text").Should().Contain("找不到或無法執行 dotnet");
        h.OnUi(() => h.Hub.LaunchBuilding).Should().BeFalse();
    }

    [Fact]
    public async Task 不認得的目標_不處理()
    {
        var (h, _, starter) = Create();
        using var dispose = h;
        var (id, _) = await OpenAsync(h);
        h.Command(h.Main, "launchAction", new { conversationId = id, targetId = LaunchRules.TargetId(@"C:\evil\x.csproj"), action = "start" });
        h.Command(h.Main, "launchAction", new { conversationId = id, action = "start" });
        h.Command(h.Main, "launchAction", new { conversationId = "nope", targetId = "t1", action = "start" });
        await h.Ui.DrainAsync();
        starter.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task 選目標_同一個資料夾的對話一起換()
    {
        var (h, scanner, _) = Create();
        using var dispose = h;
        scanner.Targets = folder => [Target(folder, "A"), Target(folder, "B")];
        var folder = h.Temp.CreateDirectory("two-apps");
        var a = h.NewConversation(h.Main, h.Claude);
        var b = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, a, folder);
        await h.SetFolderAsync(h.Main, b, folder);
        var first = (JObject)(await WaitLaunchAsync(h.Main, a, l => l?.Value<string>("folder") == folder))["launch"]!;
        first.Value<string>("selected").Should().Be(Target(folder, "A").Id);

        var mark = h.Main.Mark();
        h.Command(h.Main, "launchSelect", new { conversationId = a, targetId = Target(folder, "B").Id });
        await WaitLaunchAsync(h.Main, a, l => l?.Value<string>("selected") == Target(folder, "B").Id, mark);
        await WaitLaunchAsync(h.Main, b, l => l?.Value<string>("selected") == Target(folder, "B").Id, mark);
    }

    [Fact]
    public async Task 換資料夾_先藏起來再偵測新資料夾()
    {
        var (h, scanner, _) = Create();
        using var dispose = h;
        var (id, _) = await OpenAsync(h);
        var folder = h.Temp.CreateDirectory("other-app");
        var mark = h.Main.Mark();
        await h.SetFolderAsync(h.Main, id, folder);
        h.Main.Messages("launchStatus", mark, id)[0]["launch"]!.Type.Should().Be(JTokenType.Null, "新資料夾還沒偵測過：先藏起舊的");
        var shown = await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("folder") == folder, mark);
        shown["launch"]!["targets"]![0]!.Value<string>("path").Should().Be(Target(folder).RelativePath);
        scanner.Scans.Should().Contain(folder);
    }

    [Fact]
    public async Task 這一輪結束_重新偵測_連續很快結束時不重複偵測()
    {
        var (h, scanner, _) = Create();
        using var dispose = h;
        var now = DateTime.UtcNow;
        h.Ui.Invoke(() => h.Hub.LaunchClock = () => now);
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        await WaitLaunchAsync(h.Main, id, l => l != null);
        var scans = scanner.Scans.Count;

        h.CompleteTurn(session);
        await h.Ui.DrainAsync();
        scanner.Scans.Should().HaveCount(scans, "剛偵測過（3 秒內）");

        now = now.AddSeconds(5);
        h.Send(h.Main, id, "再一則");
        h.CompleteTurn(session);
        await h.WaitUntilAsync(() => scanner.Scans.Count > scans, "這一輪結束重新偵測（代理可能剛建立了專案）");
    }

    [Fact]
    public async Task 中樞關閉_結束建置()
    {
        var (h, _, starter) = Create();
        var (id, launch) = await OpenAsync(h);
        Act(h, id, launch, "start");
        var build = await NextAsync(h, starter, 1);
        h.Dispose();
        build.Kills.Should().Be(1, "被打斷的建置之後也不會執行");
        build.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task 中樞關閉_執行中的程式不結束_開起來就記進runsjson_之後開的子程序維護時補記()
    {
        using var shared = new TempDirectory("launch-detach");
        var state = shared.CreateDirectory("state");
        var (h, _, starter) = Create(state);
        var (id, launch) = await OpenAsync(h);
        await SetModeAsync(h, id, launch, "release");
        var mark = h.Main.Mark();
        Act(h, id, launch, "start");
        (await NextAsync(h, starter, 1)).Exit(0);
        var run = await NextAsync(h, starter, 2);
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", mark);
        var saved = ReadState(state)!.Runs.Should().ContainSingle("開起來就記下").Subject;
        saved.TargetId.Should().Be(launch.Value<string>("selected"));
        saved.Processes.Should().Equal(run.Refs);
        saved.LogFile.Should().Be(Path.GetFileName(run.LogPath));
        saved.Paused.Should().BeFalse();
        Path.GetDirectoryName(run.LogPath).Should().Be(state, "輸出檔在狀態資料夾");

        var child = new LaunchProcessRef(9001, 90010);
        run.Refs = [run.Refs[0], child];
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        ReadState(state)!.Runs.Single().Processes.Should().Equal(run.Refs[0], child);

        h.Dispose();
        run.Kills.Should().Be(0, "執行的程式和 app 脫鉤，app 結束不影響它");
        run.Disposed.Should().BeTrue("只放開控制代碼");
        ReadState(state)!.Runs.Single().Processes.Should().Equal(run.Refs[0], child);
        File.Exists(run.LogPath).Should().BeTrue("下次接回時從輸出檔接著讀");
    }

    [Fact]
    public async Task app重新啟動_接回還在執行的程式_重新建置並執行時先結束它()
    {
        using var shared = new TempDirectory("launch-reattach");
        var state = shared.CreateDirectory("state");
        var folder = shared.CreateDirectory("proj");
        FakeProcess run;
        long since;
        {
            var (h1, _, starter1) = Create(state);
            using var dispose1 = h1;
            var (id1, launch1) = await OpenInAsync(h1, folder);
            await SetModeAsync(h1, id1, launch1, "release");
            var mark = h1.Main.Mark();
            Act(h1, id1, launch1, "start");
            (await NextAsync(h1, starter1, 1)).Exit(0);
            run = await NextAsync(h1, starter1, 2);
            since = (await WaitLaunchAsync(h1.Main, id1, l => l?.Value<string>("phase") == "running", mark))["launch"]!.Value<long>("since");
        }
        run.Kills.Should().Be(0);
        var oldLog = run.LogPath!;

        var starter = new FakeStarter();
        var (h, _, _) = Create(state, starter);
        using var dispose = h;
        var reattached = starter.Reattached.Should().ContainSingle().Subject;
        reattached.Processes.Should().Equal(run.Refs);
        reattached.LogPath.Should().Be(oldLog, "從上次的輸出檔接著讀");
        var (id, launch) = await OpenInAsync(h, folder, l => l.Value<string>("phase") == "running");
        launch.Value<long>("since").Should().Be(since, "開始的時間照舊");
        launch.Value<string>("runMode").Should().Be("release", "接回的一定是 Release 模式（和 app 脫鉤）");
        launch.Value<string>("mode").Should().Be("release", "模式記在 prefs.json");
        h.OnUi(() => h.Hub.LaunchBuilding).Should().BeFalse();
        h.OnUi(() => h.Hub.LaunchDebugging).Should().BeFalse();

        var process = reattached.Process!;
        process.Write("after restart");
        await WaitLaunchAsync(h.Main, id, l => l?.Value<long>("logSeq") >= 2);
        LogLines(h, id, launch).Should().Equal(LaunchRules.ReattachedNote, "after restart");

        var mark2 = h.Main.Mark();
        Act(h, id, launch, "restart");
        process.Kills.Should().Be(1, "重新建置並執行：先結束 app 重新啟動前開的那個程式");
        var rebuild = await NextAsync(h, starter, 1);
        rebuild.IsBuild.Should().BeTrue("等它結束了才建置（否則它鎖著 exe）");
        File.Exists(oldLog).Should().BeFalse("那個程式結束了，輸出檔刪掉");
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "building", mark2);
        rebuild.Exit(0);
        var rerun = await NextAsync(h, starter, 2);
        rerun.LogPath.Should().NotBe(oldLog, "每次執行換一個輸出檔");
        await h.WaitUntilAsync(() => ReadState(state)?.Runs.SingleOrDefault()?.LogFile == Path.GetFileName(rerun.LogPath), "記下新開的程式");
    }

    [Fact]
    public async Task 暫停中關閉_接回後仍是暫停_按繼續才恢復()
    {
        using var shared = new TempDirectory("launch-reattach");
        var state = shared.CreateDirectory("state");
        var folder = shared.CreateDirectory("proj");
        {
            var (h1, _, starter1) = Create(state);
            using var dispose1 = h1;
            var (id1, launch1) = await OpenInAsync(h1, folder);
            await SetModeAsync(h1, id1, launch1, "release");
            var mark = h1.Main.Mark();
            Act(h1, id1, launch1, "start");
            (await NextAsync(h1, starter1, 1)).Exit(0);
            await NextAsync(h1, starter1, 2);
            await WaitLaunchAsync(h1.Main, id1, l => l?.Value<string>("phase") == "running", mark);
            Act(h1, id1, launch1, "pause");
            await WaitLaunchAsync(h1.Main, id1, l => l?.Value<string>("phase") == "paused", mark);
            ReadState(state)!.Runs.Single().Paused.Should().BeTrue("程序在 app 結束後仍然是暫停的");
        }

        var starter = new FakeStarter();
        var (h, _, _) = Create(state, starter);
        using var dispose = h;
        var (id, launch) = await OpenInAsync(h, folder, l => l.Value<string>("phase") == "paused");
        var process = starter.Reattached.Single().Process!;
        process.Suspended = true;
        var mark2 = h.Main.Mark();
        Act(h, id, launch, "start");   // 暫停中按啟動（F5）＝繼續
        process.Suspended.Should().BeFalse();
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", mark2);
        ReadState(state)!.Runs.Single().Paused.Should().BeFalse();
    }

    [Fact]
    public async Task Debug模式_預設_以Debug組態建置_程式是app的子程序_不記進runsjson_中樞關閉時結束它()
    {
        using var shared = new TempDirectory("launch-debug");
        var state = shared.CreateDirectory("state");
        var (h, _, starter) = Create(state);
        var (id, launch) = await OpenAsync(h);
        var mark = h.Main.Mark();
        Act(h, id, launch, "start");
        var build = await NextAsync(h, starter, 1);
        build.Command.Arguments.Should().ContainInConsecutiveOrder("-c", "Debug");
        var building = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "building", mark))["launch"]!;
        building.Value<string>("runMode").Should().Be("debug");
        build.Exit(0);

        var run = await NextAsync(h, starter, 2);
        run.Attached.Should().BeTrue("Debug 模式的程式是 app 的子程序");
        run.LogPath.Should().BeNull("輸出經管線");
        run.Command.Arguments.Should().ContainInConsecutiveOrder("-c", "Debug");
        var running = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", mark))["launch"]!;
        running.Value<string>("runMode").Should().Be("debug");
        h.OnUi(() => h.Hub.LaunchDebugging).Should().BeTrue("app 重啟會一起結束它：自我更新不自動重啟");
        h.OnUi(() => h.Hub.LaunchBuilding).Should().BeFalse();
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        File.Exists(Path.Combine(state, LaunchRules.StateFileName)).Should().BeFalse("不記進 runs.json（下次不接回）");

        run.Write("hello");
        await WaitLaunchAsync(h.Main, id, l => l?.Value<long>("logSeq") >= 3, mark);
        LogLines(h, id, launch).Should().EndWith("hello");

        h.Dispose();
        run.Kills.Should().Be(1, "Debug 模式的程式跟著 app 結束");
        run.Disposed.Should().BeTrue();
        File.Exists(Path.Combine(state, LaunchRules.StateFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task 換模式_記在專案上_執行中換的下一次才套用_重新建置並執行改用新模式()
    {
        using var shared = new TempDirectory("launch-mode");
        var state = shared.CreateDirectory("state");
        var folder = shared.CreateDirectory("proj");
        var (h, _, starter) = Create(state);
        using var dispose = h;
        var (id, launch) = await OpenInAsync(h, folder);
        var other = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, other, folder);
        await WaitLaunchAsync(h.Main, other, l => l?.Value<string>("folder") == folder);

        var mark = h.Main.Mark();
        Act(h, id, launch, "start");
        (await NextAsync(h, starter, 1)).Exit(0);
        var debugRun = await NextAsync(h, starter, 2);
        debugRun.Attached.Should().BeTrue();
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running", mark);

        var modeMark = h.Main.Mark();
        var changed = await SetModeAsync(h, id, launch, "release");
        changed.Value<string>("runMode").Should().Be("debug", "這一次照開始時的模式");
        changed.Value<string>("phase").Should().Be("running", "換模式不影響執行中的程式");
        debugRun.Kills.Should().Be(0);
        await WaitLaunchAsync(h.Main, other, l => l?.Value<string>("mode") == "release", modeMark);
        JsonFile.Read<LaunchPrefsFile>(Path.Combine(state, LaunchRules.PrefsFileName))!.Targets.Single().Mode.Should().Be("release");

        // 不認得的模式不理（選的沒變：也不重送）
        var bogusMark = h.Main.Mark();
        h.Command(h.Main, "launchSelect", new { conversationId = id, targetId = launch.Value<string>("selected"), mode = "profile" });
        await h.Ui.DrainAsync();
        h.Main.Messages("launchStatus", bogusMark, id).Should().NotContain(m => m["launch"]!.Value<string>("mode") != "release");
        JsonFile.Read<LaunchPrefsFile>(Path.Combine(state, LaunchRules.PrefsFileName))!.Targets.Single().Mode.Should().Be("release");

        Act(h, id, launch, "restart");
        debugRun.Kills.Should().Be(1);
        var rebuild = await NextAsync(h, starter, 3);
        rebuild.Command.Arguments.Should().ContainInConsecutiveOrder("-c", "Release");
        rebuild.Exit(0);
        var releaseRun = await NextAsync(h, starter, 4);
        releaseRun.Attached.Should().BeFalse();
        releaseRun.LogPath.Should().NotBeNull();
        var rerun = (JObject)(await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("phase") == "running" && l.Value<string>("runMode") == "release", mark))["launch"]!;
        rerun.Value<string>("mode").Should().Be("release");
        h.OnUi(() => h.Hub.LaunchDebugging).Should().BeFalse();
        ReadState(state)!.Runs.Should().ContainSingle("Release 模式開起來就記下");
    }

    [Fact]
    public async Task 選的專案與模式_app重新啟動後照舊_自動選的第一個不記()
    {
        using var shared = new TempDirectory("launch-prefs");
        var state = shared.CreateDirectory("state");
        var folder = shared.CreateDirectory("two-apps");
        var untouched = shared.CreateDirectory("untouched");
        var b = Target(folder, "B");
        {
            var (h1, scanner1, _) = Create(state);
            using var dispose1 = h1;
            scanner1.Targets = f => [Target(f, "A"), Target(f, "B")];
            var (id1, launch1) = await OpenInAsync(h1, folder);
            launch1.Value<string>("selected").Should().Be(Target(folder, "A").Id, "第一次：第一個");
            var mark = h1.Main.Mark();
            h1.Command(h1.Main, "launchSelect", new { conversationId = id1, targetId = b.Id, mode = "release" });
            await WaitLaunchAsync(h1.Main, id1, l => l?.Value<string>("selected") == b.Id && l.Value<string>("mode") == "release", mark);
            await OpenInAsync(h1, untouched);
        }

        var prefs = JsonFile.Read<LaunchPrefsFile>(Path.Combine(state, LaunchRules.PrefsFileName))!;
        prefs.Folders.Should().ContainSingle("只記使用者選過的資料夾").Which.Selected.Should().Be(b.Id);

        var (h, scanner, _) = Create(state);
        using var dispose = h;
        scanner.Targets = f => [Target(f, "A"), Target(f, "B")];
        var (id, launch) = await OpenInAsync(h, folder);
        launch.Value<string>("selected").Should().Be(b.Id, "延用上次選的專案");
        launch.Value<string>("mode").Should().Be("release", "延用這個專案的模式");

        // 記的專案這次沒偵測到：選第一個，但不改掉記的
        scanner.Targets = f => [Target(f, "A")];
        var mark2 = h.Main.Mark();
        h.Command(h.Main, "launchRescan", new { conversationId = id });
        await WaitLaunchAsync(h.Main, id, l => l?.Value<string>("selected") == Target(folder, "A").Id, mark2);
        JsonFile.Read<LaunchPrefsFile>(Path.Combine(state, LaunchRules.PrefsFileName))!.Folders.Single().Selected.Should().Be(b.Id);
    }

    [Fact]
    public void 接回時程式已經不在_忘掉_刪掉輸出檔_壞的紀錄略過()
    {
        using var shared = new TempDirectory("launch-reattach");
        var state = shared.CreateDirectory("state");
        var target = Target(shared.CreateDirectory("proj"));
        var logFile = LaunchRules.LogFileName(target.Id, DateTime.UtcNow);
        var stray = LaunchRules.LogFileName(LaunchRules.TargetId(@"C:\other\Other.csproj"), DateTime.UtcNow);
        File.WriteAllText(Path.Combine(state, logFile), "old output");
        File.WriteAllText(Path.Combine(state, stray), "left over");
        File.WriteAllText(Path.Combine(state, "notes.txt"), "not ours");
        var unknownKind = LaunchRules.ToRecord(target, DateTime.UtcNow, paused: false, null, [new LaunchProcessRef(7, 8)]);
        unknownKind.Kind = "qt";
        var wrongId = LaunchRules.ToRecord(target, DateTime.UtcNow, paused: false, null, [new LaunchProcessRef(9, 10)]);
        wrongId.TargetId = LaunchRules.TargetId(@"C:\elsewhere\X.csproj");
        JsonFile.Write(Path.Combine(state, LaunchRules.StateFileName), new LaunchRunsFile
        {
            Runs = [unknownKind, wrongId, LaunchRules.ToRecord(target, DateTime.UtcNow, paused: false, logFile, [new LaunchProcessRef(123, 456)])],
        });

        var starter = new FakeStarter { Alive = _ => false };
        var (h, _, _) = Create(state, starter);
        using var dispose = h;
        starter.Reattached.Should().ContainSingle("種類不認得、鍵和專案檔對不上的紀錄略過").Which.Processes.Should().Equal(new LaunchProcessRef(123, 456));
        File.Exists(Path.Combine(state, LaunchRules.StateFileName)).Should().BeFalse("沒有開著的程式");
        File.Exists(Path.Combine(state, logFile)).Should().BeFalse();
        File.Exists(Path.Combine(state, stray)).Should().BeFalse("沒被記得的輸出檔也刪");
        File.Exists(Path.Combine(state, "notes.txt")).Should().BeTrue("不是執行列的檔案不動");
        h.OnUi(() => h.Hub.LaunchBuilding).Should().BeFalse();
    }
}
