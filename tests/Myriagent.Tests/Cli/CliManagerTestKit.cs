using System.Net;
using System.Text;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using static Myriagent.Tests.Cli.CliWatcherTestKit;

namespace Myriagent.Tests.Cli;

/// <summary>
/// 假的 CLI 子指令（CliManagerOptions.Run）：--version 回報暫存安裝裡實際的版本（Claude 讀 claude.exe 的版本資訊、npm 版 Codex 讀 vendor 的
/// codex-package.json，模擬執行檔自己回報）；update 與 npm（cmd /c call npm.cmd install -g …）由測試決定做什麼。每次呼叫都記下參數、給一個假 PID
/// </summary>
internal sealed class FakeCliCommands
{
    private int nextPid = 41000;

    public FakeCliCommands(Func<AgentLauncher, string?> version) => Version = version;

    /// <summary>每次呼叫的參數（以空白連接）</summary>
    public Recorder<string> Calls { get; } = new();

    /// <summary>--version 的輸出；null＝結束碼 1（執行檔壞了）</summary>
    public Func<AgentLauncher, string?> Version { get; set; }

    /// <summary>設定後 --version 要等它（模擬執行檔很慢、驗證卡住）</summary>
    public TaskCompletionSource? VersionGate { get; set; }

    /// <summary>claude update（參數 update）：給 PID，回傳結果</summary>
    public Func<int, CliRunResult> Update { get; set; } = _ => new CliRunResult(0, "Claude Code is up to date", string.Empty, false);

    /// <summary>npm（cmd.exe 的參數）：給 PID 與參數，回傳結果</summary>
    public Func<int, IReadOnlyList<string>, CliRunResult> Npm { get; set; } = (_, _) => new CliRunResult(0, "changed 1 package", string.Empty, false);

    /// <summary>winget（執行檔是 FakeWinget；list 與 upgrade）：給 PID 與參數，回傳結果。預設是 WinGet 上沒有新版</summary>
    public Func<int, IReadOnlyList<string>, CliRunResult> Winget { get; set; } = (_, arguments) => arguments[0] == "list"
        ? new CliRunResult(0, "未找到符合輸入條件的已安裝套件。\r\n", string.Empty, false)
        : new CliRunResult(CliWingetRules.UpdateNotApplicable, "找不到可用的升級。\r\n", string.Empty, false);

    /// <summary>CliManagerTestKit.Create 給的 winget 路徑（不是真的 winget）</summary>
    public const string FakeWinget = @"C:\fake\WindowsApps\winget.exe";

    public int Count(string fragment) => Calls.All.Count(c => c.Contains(fragment, StringComparison.Ordinal));

    public CliCommandRunner Runner => async (launcher, arguments, environment, timeout, killWithApp, started, cancellationToken) =>
    {
        var pid = Interlocked.Increment(ref nextPid);
        Calls.Add(string.Join(" ", arguments));
        started?.Invoke(pid);
        if (string.Equals(launcher.FileName, FakeWinget, StringComparison.OrdinalIgnoreCase))
            return Winget(pid, arguments) with { ProcessId = pid };
        if (arguments.Contains("--version"))
        {
            if (VersionGate != null)
                await VersionGate.Task.WaitAsync(cancellationToken);
            var output = Version(launcher);
            return output == null ? new CliRunResult(1, string.Empty, "壞掉的執行檔", false) { ProcessId = pid } : new CliRunResult(0, output, string.Empty, false) { ProcessId = pid };
        }
        if (arguments.Contains("update"))
            return Update(pid) with { ProcessId = pid };
        if (arguments.Contains("install"))
            return Npm(pid, arguments) with { ProcessId = pid };
        throw new InvalidOperationException("沒有預期的指令：" + string.Join(" ", arguments));
    };

    /// <summary>Claude 原生安裝：--version 回報 claude.exe 的版本資訊（「x.y.z (Claude Code)」）</summary>
    public static FakeCliCommands For(ClaudeLayout claude)
        => new(_ => CliBinaryIdentityRules.ReadClaudeFileVersion(claude.Exe) is { } v ? $"{v} (Claude Code)" : null);

    /// <summary>npm 版 Codex：--version 回報 vendor 的 codex-package.json（「codex-cli x.y.z」；執行檔是假的，以它代表執行檔自己的版本）</summary>
    public static FakeCliCommands For(CodexNpmLayout codex)
        => new(_ => CliBinaryIdentityRules.ReadPackageVersion(Path.Combine(CliLocator.CodexVendorRootCandidates(codex.PackageRoot)[0], "codex-package.json")) is { } v
            ? $"codex-cli {v}"
            : null);
}

/// <summary>依序記下 CliManager 的事件與屏障呼叫（Changed、VersionChanged、Prepare、Finished），檢查發布順序</summary>
internal sealed class CliEventLog
{
    private readonly object gate = new();
    private readonly List<string> entries = new();
    private readonly List<(CliVersionChange Change, CliState State)> changes = new();
    private readonly List<CliBinaryIdentity?> identitiesAtChanged = new();
    private readonly List<CliUpdatePhase> phases = new();

    public CliEventLog(CliManager cli, AgentProvider provider)
    {
        cli.Changed += () =>
        {
            var state = cli.Get(provider);
            lock (gate)
            {
                entries.Add("Changed");
                if (identitiesAtChanged.Count == 0 || !Equals(identitiesAtChanged[^1], state.Identity) || identitiesAtChanged[^1]?.Version != state.Identity?.Version)
                    identitiesAtChanged.Add(state.Identity);
                if (phases.Count == 0 || phases[^1] != state.UpdatePhase)
                    phases.Add(state.UpdatePhase);
            }
        };
        cli.VersionChanged += change =>
        {
            if (change.Provider != provider)
                return;
            var state = cli.Get(provider);
            lock (gate)
            {
                entries.Add($"VersionChanged stable={change.Stable} broken={change.InstallBroken} switch={change.IsVersionSwitch} {change.ToVersion}");
                changes.Add((change, state));
            }
        };
    }

    public void Add(string entry)
    {
        lock (gate)
            entries.Add(entry);
    }

    public List<string> Entries
    {
        get
        {
            lock (gate)
                return entries.ToList();
        }
    }

    /// <summary>每次 VersionChanged 與觸發當下的狀態（契約：觸發前狀態已更新）</summary>
    public List<(CliVersionChange Change, CliState State)> Changes
    {
        get
        {
            lock (gate)
                return changes.ToList();
        }
    }

    /// <summary>每次 Changed 時看到的身分（連續相同的只記一次）：身分只經 PublishVersionChange 改，所以每次改變都對應一個 VersionChanged</summary>
    public List<CliBinaryIdentity?> IdentitiesAtChanged
    {
        get
        {
            lock (gate)
                return identitiesAtChanged.ToList();
        }
    }

    /// <summary>UpdatePhase 依序的變化（連續相同的只記一次）</summary>
    public List<CliUpdatePhase> Phases
    {
        get
        {
            lock (gate)
                return phases.ToList();
        }
    }

    public async Task<CliVersionChange> WaitForChangeAsync(Func<CliVersionChange, bool> match, string because, int skip = 0, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var found = Changes.Skip(skip).Select(c => c.Change).FirstOrDefault(match);
            if (found != null)
                return found;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{seconds} 秒內沒有等到：{because}；收到的事件：" + string.Join("、", Entries));
            await Task.Delay(25);
        }
    }
}

/// <summary>假的更新屏障參與者（中樞）：記下呼叫並寫進事件記錄；Prepare 的回覆由測試決定（預設全部停下）</summary>
internal sealed class RecordingParticipant : ICliUpdateParticipant
{
    private readonly CliEventLog? log;

    public RecordingParticipant(CliEventLog? log = null) => this.log = log;

    public Recorder<(AgentProvider Provider, CliUpdateUrgency Urgency)> Prepares { get; } = new();

    public Recorder<CliUpdateCompletion> Finished { get; } = new();

    public Func<AgentProvider, CliUpdateUrgency, CancellationToken, Task<CliUpdatePreparation>> OnPrepare { get; set; }
        = (_, _, _) => Task.FromResult(new CliUpdatePreparation(Ready: true, ClosedIdle: 1, Held: 0, Busy: 0));

    public Task<CliUpdatePreparation> PrepareForUpdateAsync(AgentProvider provider, CliUpdateUrgency urgency, CancellationToken cancellationToken)
    {
        Prepares.Add((provider, urgency));
        log?.Add($"Prepare {provider} {urgency}");
        return OnPrepare(provider, urgency, cancellationToken);
    }

    public void UpdateFinished(CliUpdateCompletion completion)
    {
        Finished.Add(completion);
        log?.Add($"Finished {completion.Outcome} {completion.Identity?.Version}");
    }
}

/// <summary>npm registry 的假回應：最新版固定是 Latest（不連網）</summary>
internal sealed class FakeRegistry : HttpMessageHandler
{
    public string Latest { get; set; } = "9.9.9";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host == "registry.npmjs.org")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"version\":\"{Latest}\"}}", Encoding.UTF8, "application/json") });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }
}

internal static class CliManagerTestKit
{
    /// <summary>
    /// 以暫存安裝與假指令建立 CliManager：watcher 用假的程序查詢、沒有環境變數、縮短的去抖（background＝false 就不建 watcher），
    /// 不查 registry、不排定定期檢查、不碰這台電腦真的 CLI、npm、winget 與網路
    /// </summary>
    public static CliManager Create(HttpClient http, Func<AgentProvider, CliInstallation?> locate, FakeCliCommands commands, FakeProcesses processes,
        AgentProvider provider, bool background, Func<CliManagerOptions, CliManagerOptions>? configure = null, Func<AgentProvider, bool>? runningSessions = null)
    {
        var options = new CliManagerOptions
        {
            Locate = locate,
            LocateNpm = () => @"C:\fake npm\npm.cmd",
            LocateWinget = () => FakeCliCommands.FakeWinget,
            Run = commands.Runner,
            Watcher = Fast(processes, provider),
            FetchLatestOnStart = false,
            FirstAutoUpdateDelay = Timeout.InfiniteTimeSpan,
            ActivityWait = TimeSpan.FromSeconds(2),
            AppProcessExitWait = TimeSpan.FromSeconds(1),
        };
        return new CliManager(http, runningSessions ?? (_ => false), () => false, background, configure?.Invoke(options) ?? options);
    }

    /// <summary>等條件成立（背景執行緒的發布）</summary>
    public static Task WaitAsync(Func<bool> condition, string because, double seconds = 10) => WaitUntilAsync(condition, because, seconds);
}
