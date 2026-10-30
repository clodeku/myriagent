using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;
using static Myriagent.Tests.Integration.HubIntegration;

namespace Myriagent.Tests.Integration;

/// <summary>
/// IT-H3（設計 11.2）版本切換沙箱：真的中樞、真的會話、真的 CliManager（watcher 以實際的去抖常數監看沙箱）。
/// <list type="bullet">
/// <item>Claude：把 %USERPROFILE%\.local\share\claude\versions 裡最新的兩個版本複製進沙箱（研究 E1 作法：HOME／USERPROFILE 指向沙箱的
/// .local\bin、versions、locks），對話用沙箱的 claude.exe 跑；照原生安裝程式的順序換檔（.new → 舊檔改名 .old → .new 改名成 claude.exe）。
/// 確認 10 秒內偵測到已穩定的新身分、閒置的程序 10 秒內關閉且不接續、工具執行中的在工具結束後 5 秒內以新版重開並自動接續、
/// 新程序（system/init）回報新版、已完成的寫檔不重做。只有一個版本時，以同一版本另一份複本模擬換檔（FileVersion 相同、檔案識別碼不同）</item>
/// <item>Codex：把 npm 安裝的 @openai/codex 整包複製到隔離的 npm 前綴（研究 E2 作法），手動更新走 7.3.1 的全體屏障——
/// 假的 npm 以預先複製好的另一份換掉套件（同一版本、不同的執行檔身分）。確認閒置的關掉、工具執行中的在安全點停下、
/// 更新後 UpdateFinished 帶新身分（不是「沒有可用的版本」）、停下的那一輪以新版接續完成</item>
/// </list>
/// 需要登入的部分用系統預設帳戶（使用者既有的 ~/.claude、~/.codex；同 IT-H1、IT-H2，不改設定、只多一筆對話紀錄），沙箱的空設定資料夾只給 --version。
/// 會用到訂閱額度（Claude haiku 約 3 輪、Codex gpt-6-luna 約 3 輪，推理強度 low）。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~Integration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class CliHotUpdateIntegrationTests : IDisposable
{
    private const string Reason = "以本機 Claude Code／Codex 的複本在沙箱換檔並跑真的對話（會用到訂閱額度）";
    private const string ClaudeModel = "haiku";
    private const string CodexModel = "gpt-6-luna";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-hot-update");

    public CliHotUpdateIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    // ───────────────────────────── 共用 ─────────────────────────────

    /// <summary>以不擋 CLI 換檔與清理的方式複製（FileShare.ReadWrite｜Delete；File.Copy 不帶 FILE_SHARE_DELETE）</summary>
    private static void CopyShared(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        using var copy = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        input.CopyTo(copy);
    }

    private static void CopyTreeShared(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            CopyShared(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyTreeShared(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private void Dump(HubHarness h, string label, string conversationId)
    {
        foreach (var item in h.Items(conversationId))
            output.WriteLine($"[{label}] {item.Kind} {item.Status} {item.ToolName} {ClaudeStreamRules.FirstLine(item.Text ?? item.Output ?? string.Empty)}");
    }

    /// <summary>失敗時的診斷：對話的項目、標頭的換版狀態、會話的事件序列、app 的記錄檔（只在測試的暫存資料夾）</summary>
    private void Diagnose(HubHarness h, string label, string conversationId, Recorded recorded)
    {
        Dump(h, label, conversationId);
        output.WriteLine($"[{label}] 標頭 restart：{h.OnUi(() => h.Hub.RenderHeaderRestartForTest(conversationId)).ToString(Newtonsoft.Json.Formatting.None)}");
        foreach (var (at, ev) in recorded.Of<AgentEvent>())
            output.WriteLine($"[{label}] {at:HH:mm:ss.fff} {ev.GetType().Name} {Describe(ev)}");
        WriteLogTail(output);
    }

    private static string Describe(AgentEvent ev) => ev switch
    {
        AgentTextCompletedEvent t => ClaudeStreamRules.FirstLine(t.Text),
        AgentToolStartedEvent t => $"{t.ToolName} {t.Title}",
        AgentToolCompletedEvent t => $"{t.Status} {ClaudeStreamRules.FirstLine(t.Output ?? string.Empty)}",
        AgentTurnCompletedEvent c => $"{c.Outcome} {c.ErrorMessage}",
        AgentProcessStartedEvent p => $"{p.ProcessInstance} {p.Cli?.Version} {p.Cli?.FileId:x}",
        AgentProcessExitedEvent p => $"{p.ProcessInstance} 結束碼 {p.ExitCode} Expected={p.Expected}",
        AgentRunningVersionEvent v => $"{v.ProcessInstance} {v.Version}",
        AgentSafePointChangedEvent sp => $"tool={sp.ToolRunning} user={sp.WaitingForUser} background={sp.HasLiveBackgroundWork}",
        AgentTurnStartedByCliEvent t => t.Kind.ToString(),
        AgentNoticeEvent n => n.Message,
        _ => string.Empty,
    };

    // ───────────────────────────── Claude ─────────────────────────────

    /// <summary>versions 資料夾裡最新的兩個版本（只有一個時兩個都用它：以另一份複本模擬換檔）</summary>
    private static (string OldFile, string NewFile) PickClaudeVersions()
    {
        var versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "claude", "versions");
        Directory.Exists(versions).Should().BeTrue("這台電腦要有 Claude Code 原生安裝（{0}）", versions);
        var files = Directory.GetFiles(versions)
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d+\.\d+\.\d+$"))
            .OrderBy(f => Path.GetFileName(f), Comparer<string>.Create(CliVersionRules.Compare))
            .ToList();
        files.Should().NotBeEmpty();
        return files.Count >= 2 ? (files[^2], files[^1]) : (files[^1], files[^1]);
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_H3_Claude_沙箱換檔_偵測閒置關閉執行中重開_新程序回報新版()
    {
        var (oldFile, newFile) = PickClaudeVersions();
        var oldVersion = CliBinaryIdentityRules.ReadClaudeFileVersion(oldFile)!;
        var newVersion = CliBinaryIdentityRules.ReadClaudeFileVersion(newFile)!;
        output.WriteLine($"換檔：{oldVersion}（{oldFile}）→ {newVersion}（{newFile}）{(oldFile == newFile ? "：只有一個版本，以另一份複本模擬換檔" : string.Empty)}");

        var home = temp.Combine("claude-home");
        var exe = Path.Combine(home, ".local", "bin", "claude.exe");
        CopyShared(oldFile, exe);
        var staged = temp.Combine("claude-staging", "claude.exe");
        CopyShared(newFile, staged);
        Directory.CreateDirectory(Path.Combine(home, ".local", "share", "claude", "versions"));
        Directory.CreateDirectory(Path.Combine(home, ".local", "state", "claude", "locks"));
        var installation = new CliInstallation(AgentProvider.Claude, new AgentLauncher(exe, Array.Empty<string>()), CliInstallKind.Native, exe) { NativeExecutable = exe };
        var sandboxConfig = temp.CreateDirectory("claude-sandbox-config");

        // CliManager：watcher 以沙箱的 HOME 推 versions／locks；--version 也在沙箱的 HOME、設定資料夾裡跑（研究 E1：不碰真實安裝）
        Func<string, string?> environment = name => name is "HOME" or "USERPROFILE" ? home : null;
        CliCommandRunner run = (launcher, arguments, configure, timeout, killWithApp, started, cancellationToken) =>
            CliRunner.RunAsync(launcher, arguments, env =>
            {
                configure?.Invoke(env);
                env["HOME"] = home;
                env["USERPROFILE"] = home;
                env["CLAUDE_CONFIG_DIR"] = sandboxConfig;
                env["DISABLE_UPDATES"] = "1";
            }, timeout, cancellationToken, killWithApp: killWithApp, started: started);
        var options = new CliManagerOptions
        {
            Locate = p => p == AgentProvider.Claude ? installation : null,
            Run = run,
            Watcher = new CliWatcherOptions { Environment = environment, Providers = new[] { AgentProvider.Claude } },
            FetchLatestOnStart = false,
            FirstAutoUpdateDelay = Timeout.InfiniteTimeSpan,
        };
        var sessions = new Sessions();
        using var h = new HubHarness(createCli: http => new CliManager(http, _ => false, () => false, startBackgroundChecks: true, options), sessionFactory: sessions.Create);
        var changes = new ConcurrentQueue<(DateTime At, CliVersionChange Change)>();
        h.Cli.VersionChanged += change => changes.Enqueue((DateTime.UtcNow, change));
        // 這裡驗「閒置的程序換版時關閉」：開著 Remote Control 的閒置程序照設計不關（等下次送出才用新版，見 AgentHubRestartTests），先關掉
        h.Command(h.Main, "setting", new { key = "claudeRemoteControl", value = false });

        await WaitUntilAsync(() => h.Cli.GetIdentity(AgentProvider.Claude)?.Version == oldVersion && !h.Cli.Get(AgentProvider.Claude).Checking,
            "啟動時第一次讀到的身分（中樞接上屏障時開始監看）", TimeSpan.FromSeconds(60));
        var startup = h.Cli.GetIdentity(AgentProvider.Claude)!;
        output.WriteLine($"啟動身分：{startup}");

        // 1. 閒置的對話（程序還活著）
        var idleId = h.NewConversation(h.Main, h.Claude);
        SetOptions(h, idleId, ClaudeModel, "low");
        h.Send(h.Main, idleId, "Reply with exactly the word OK and nothing else.");
        await WaitUntilAsync(() => sessions.Count == 1 && !h.IsRunning(idleId), "閒置對話的第一輪", TurnTimeout);
        var idle = sessions[0];
        idle.Session.HasLiveProcess.Should().BeTrue("上一輪做完，程序還活著（等換版時關閉）");
        idle.Session.RunningVersion.Should().Be(oldVersion);

        // 2. 工具執行中的對話
        var busyId = h.NewConversation(h.Main, h.Claude);
        SetOptions(h, busyId, ClaudeModel, "low");
        var workspace = h.Store.GetDefaultWorkingDirectory(busyId);
        // 執行完再寫幾句話：工具結束後的安全點重開時，這一輪還在產生回覆（重開後由 CLI 自動接續）
        h.Send(h.Main, busyId, $"請用 PowerShell 工具執行一次 `{GuardedWrite}`，執行完用三句話說明這個指令做了什麼，最後一行只寫 DONE。");
        await WaitUntilAsync(() => sessions.Count == 2 && sessions[1].Of<AgentToolStartedEvent>().Count > 0, "開始執行工具", TurnTimeout);
        var busy = sessions[1];

        // 3. 照原生安裝程式的順序換檔
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pid = 424242;
        var stagedNew = $"{exe}.new.{pid}.{ms}.1a2b3c4d";
        File.Move(staged, stagedNew);
        File.Move(exe, $"{exe}.old.{ms}.{pid}");
        File.Move(stagedNew, exe);
        var swappedAt = DateTime.UtcNow;

        await WaitUntilAsync(() => changes.Any(c => c.Change.IsVersionSwitch), "偵測到已穩定的新身分", TimeSpan.FromSeconds(30));
        var (detectedAt, switched) = changes.First(c => c.Change.IsVersionSwitch);
        var detection = detectedAt - swappedAt;
        output.WriteLine($"S2 換檔 → 已穩定的新身分：{detection.TotalSeconds:0.0} 秒（{switched.FromVersion} → {switched.ToVersion}，{switched.Origin}）");
        detection.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10), "S2：換版 10 秒內判定已穩定的新身分");
        switched.Old.Should().Be(startup);
        switched.New!.Version.Should().Be(newVersion);
        switched.Origin.Should().Be(CliChangeOrigin.External, "更新者 PID 不是 app 的程序");
        changes.Should().Contain(c => !c.Change.Stable, "換檔期間先發「安裝正在變動」");

        try
        {
            await AfterClaudeSwapAsync(h, idleId, idle, busyId, busy, workspace, detectedAt, switched, newVersion);
        }
        catch (Exception)
        {
            Diagnose(h, "busy", busyId, busy);
            Diagnose(h, "idle", idleId, idle);
            throw;
        }
    }

    private async Task AfterClaudeSwapAsync(HubHarness h, string idleId, Recorded idle, string busyId, Recorded busy, string workspace, DateTime detectedAt,
        CliVersionChange switched, string newVersion)
    {
        // 4. 閒置的：10 秒內關閉、不送接續
        await WaitUntilAsync(() => !idle.Session.HasLiveProcess, "閒置的程序關閉", TimeSpan.FromSeconds(15));
        var idleClosed = DateTime.UtcNow - detectedAt;
        output.WriteLine($"S3 偵測 → 閒置程序關閉：{idleClosed.TotalSeconds:0.0} 秒");
        idleClosed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10), "S3：閒置 10 秒內關閉");

        // 5. 工具執行中的：工具結束後 5 秒內以新版重開並自動接續、新程序回報新版、寫檔不重做
        await WaitUntilAsync(() => busy.Of<AgentProcessStartedEvent>().Any(e => e.Event.Cli != null && e.Event.Cli.Equals(switched.New)), "以新版重開", TurnTimeout);
        await WaitUntilAsync(() => !h.IsRunning(busyId), "接續的一輪做完", TurnTimeout);
        Dump(h, "busy", busyId);
        var toolDone = busy.Of<AgentToolCompletedEvent>().First();
        var restarted = busy.Of<AgentProcessStartedEvent>().First(e => e.Event.Cli != null && e.Event.Cli.Equals(switched.New));
        var afterTool = restarted.At - toolDone.At;
        output.WriteLine($"S3 工具結束 → 新程序：{afterTool.TotalSeconds:0.0} 秒");
        afterTool.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5), "S3：工具結束後 5 秒內以新版重開");
        restarted.At.Should().BeAfter(toolDone.At, "工具執行中不打斷，等它結束");
        busy.Of<AgentRunningVersionEvent>().Should().Contain(e => e.Event.ProcessInstance == restarted.Event.ProcessInstance && e.Event.Version == newVersion,
            "新程序的 system/init 回報新版");
        busy.Of<AgentTurnStartedByCliEvent>().Should().NotBeEmpty("自動接續被打斷的一輪");
        h.Items(busyId).Where(i => i.NoticeKind == AgentNoticeKind.CliUpdate).Select(i => i.Text).Should().Contain(t => t != null && t.Contains("自動接續"));
        File.ReadAllText(Path.Combine(workspace, "done.txt")).Trim().Should().Be("done");
        busy.Of<AgentToolCompletedEvent>().Count(e => e.Event.Output?.Contains("written") == true).Should().Be(1, "S3：已完成的寫檔 0 次重做");

        idle.Of<AgentTurnStartedByCliEvent>().Should().BeEmpty("S3：閒置的 0 則接續訊息");
        h.Items(idleId).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.CliUpdate);

        // 6. 閒置的那個對話下次送出自然以新版續接
        h.Send(h.Main, idleId, "Reply with exactly the word OK again.");
        await WaitUntilAsync(() => idle.Session.RunningVersion == newVersion && !h.IsRunning(idleId), "閒置對話下次送出用新版", TurnTimeout);
        idle.Of<AgentProcessStartedEvent>().Last().Event.Cli.Should().Be(switched.New);
    }

    // ───────────────────────────── Codex ─────────────────────────────

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_H3_Codex_隔離npm前綴_手動更新走全體屏障_停下的對話以新版接續()
    {
        var real = CliLocator.Locate(AgentProvider.Codex);
        real.Should().NotBeNull("這台電腦要安裝 Codex CLI");
        real!.Kind.Should().Be(CliInstallKind.Npm, "這個測試以 npm 版的套件複製出隔離的 npm 前綴");
        var realPackage = CliLocator.NpmPackageRoot(AgentProvider.Codex, CliLocator.GetNpmPrefix(real)!);

        var prefix = temp.Combine("npm");
        var scope = Path.Combine(prefix, "node_modules", "@openai");
        var package = Path.Combine(scope, "codex");
        var staging = temp.Combine("codex-staging", "codex");
        CopyTreeShared(realPackage, package);
        CopyTreeShared(realPackage, staging);   // 「npm install」要放上去的那一份（同一版本、不同的執行檔身分）
        foreach (var shim in new[] { "codex", "codex.cmd", "codex.ps1" })
            File.WriteAllText(Path.Combine(prefix, shim), "codex shim");
        var installation = new CliInstallation(AgentProvider.Codex, new AgentLauncher(real.Launcher.FileName, new[] { Path.Combine(package, "bin", "codex.js") }),
            CliInstallKind.Npm, Path.Combine(prefix, "codex.cmd"))
        {
            NativeExecutable = CliLocator.CodexNativeExecutable(package, File.Exists),
        };

        int npmRuns = 0;
        CliCommandRunner run = (launcher, arguments, configure, timeout, killWithApp, started, cancellationToken) =>
        {
            if (!launcher.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
                return CliRunner.RunAsync(launcher, arguments, configure, timeout, cancellationToken, killWithApp: killWithApp, started: started);
            // 假的 npm install -g（研究 E2 的順序）：retire 改名 → 放新版 → 刪 retire → 重寫殼
            Interlocked.Increment(ref npmRuns);
            var retired = Path.Combine(scope, ".codex-ITH3test");
            Directory.Move(package, retired);
            Directory.Move(staging, package);
            DirectoryRemover.TryDelete(retired, attempts: 10, retryDelayMs: 300).Should().BeTrue("屏障之後沒有程序在用舊版");
            foreach (var shim in new[] { "codex", "codex.cmd", "codex.ps1" })
                File.WriteAllText(Path.Combine(prefix, shim), "codex shim (rewritten)");
            return Task.FromResult(new CliRunResult(0, "changed 1 package in 1s", string.Empty, false));
        };
        var options = new CliManagerOptions
        {
            Locate = p => p == AgentProvider.Codex ? installation : null,
            LocateNpm = () => Path.Combine(prefix, "npm.cmd"),
            Run = run,
            Watcher = new CliWatcherOptions { Environment = _ => null, Providers = new[] { AgentProvider.Codex } },
            FetchLatestOnStart = false,
            FirstAutoUpdateDelay = Timeout.InfiniteTimeSpan,
        };
        var sessions = new Sessions();
        using var h = new HubHarness(createCli: http => new CliManager(http, _ => false, () => false, startBackgroundChecks: true, options), sessionFactory: sessions.Create);
        var codexAccount = new AgentAccount { Id = "acc-codex-default", Provider = AgentProvider.Codex, Label = "Codex 本機" };   // 系統預設帳戶（~/.codex）
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { codexAccount }, AccountStatus.Ready));
        var changes = new ConcurrentQueue<CliVersionChange>();
        h.Cli.VersionChanged += changes.Enqueue;

        await WaitUntilAsync(() => h.Cli.GetIdentity(AgentProvider.Codex) != null && !h.Cli.Get(AgentProvider.Codex).Checking,
            "啟動時第一次讀到的身分", TimeSpan.FromSeconds(60));
        var startup = h.Cli.GetIdentity(AgentProvider.Codex)!;
        output.WriteLine($"啟動身分：{startup}");

        // 閒置的對話與工具執行中的對話
        var idleId = h.NewConversation(h.Main, codexAccount);
        SetOptions(h, idleId, CodexModel, "low");
        h.Send(h.Main, idleId, "Reply with exactly the word OK and nothing else.");
        await WaitUntilAsync(() => sessions.Count == 1 && !h.IsRunning(idleId), "閒置對話的第一輪", TurnTimeout);
        var idle = sessions[0];
        idle.Session.HasLiveProcess.Should().BeTrue();

        var busyId = h.NewConversation(h.Main, codexAccount);
        SetOptions(h, busyId, CodexModel, "low");
        var workspace = h.Store.GetDefaultWorkingDirectory(busyId);
        h.Send(h.Main, busyId, $"Run this PowerShell command exactly once: {GuardedWrite} Then explain in three sentences what it did, and end with a line containing only DONE.");
        await WaitUntilAsync(() => sessions.Count == 2 && sessions[1].Of<AgentToolStartedEvent>().Count > 0, "開始執行工具", TurnTimeout);
        var busy = sessions[1];

        // 手動更新（使用者按「更新」）：Normal 排空 → 等程序結束 → npm → 驗證 → 發布 → UpdateFinished
        var update = Task.Run(() => h.Cli.UpdateAsync(AgentProvider.Codex));
        await WaitUntilAsync(() => h.Cli.Get(AgentProvider.Codex).UpdatePhase == CliUpdatePhase.Draining || update.IsCompleted, "開始排空", TimeSpan.FromSeconds(30));
        await update.WaitAsync(TurnTimeout);
        var state = h.Cli.Get(AgentProvider.Codex);
        output.WriteLine($"更新結束：{state.ActionError ?? "沒有錯誤"}，npm 執行 {npmRuns} 次");
        state.ActionError.Should().BeNull();
        npmRuns.Should().Be(1);
        state.UpdatePhase.Should().Be(CliUpdatePhase.None);
        var fresh = h.Cli.GetIdentity(AgentProvider.Codex)!;
        fresh.Should().NotBe(startup, "換上另一份執行檔（檔案識別碼不同）");
        changes.Should().Contain(c => c.IsVersionSwitch && c.Origin == CliChangeOrigin.App);

        idle.Session.HasLiveProcess.Should().BeFalse("屏障先關掉閒置的程序");
        idle.Of<AgentTurnStartedByCliEvent>().Should().BeEmpty("閒置的 0 則接續訊息");

        // 停下的那一輪：工具結束後才停（安全點），更新後以新身分重開並接續
        await WaitUntilAsync(() => busy.Of<AgentProcessStartedEvent>().Any(e => e.Event.Cli != null && e.Event.Cli.Equals(fresh)), "以新版重開停下的那一輪", TurnTimeout);
        await WaitUntilAsync(() => !h.IsRunning(busyId), "接續的一輪做完", TurnTimeout);
        Dump(h, "busy", busyId);
        busy.Of<AgentTurnStartedByCliEvent>().Should().Contain(e => e.Event.Kind == AgentAutoTurnKind.Continuation);
        h.Items(busyId).Should().NotContain(i => i.Text != null && i.Text.Contains("沒有可用的版本"), "整合員的第 1 條：UpdateFinished 帶穩定後的身分");
        h.Items(busyId).Where(i => i.NoticeKind == AgentNoticeKind.CliUpdate).Select(i => i.Text).Should().Contain(t => t != null && t.Contains("自動接續"));
        File.ReadAllText(Path.Combine(workspace, "done.txt")).Trim().Should().Be("done");
        busy.Of<AgentToolCompletedEvent>().Count(e => e.Event.Output?.Contains("written") == true).Should().Be(1, "已完成的寫檔 0 次重做");
        busy.Of<AgentRunningVersionEvent>().Last().Event.Version.Should().Be(fresh.Version);
    }
}
