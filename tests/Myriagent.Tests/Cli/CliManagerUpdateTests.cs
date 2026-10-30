using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using static Myriagent.Tests.Cli.CliManagerTestKit;

namespace Myriagent.Tests.Cli;

/// <summary>
/// CliManager 的更新流程（設計 7.3、7.3.1；假指令、暫存安裝、不跑 watcher）：Claude 原生的 claude update 不經屏障並以 FileVersionInfo 驗證、
/// npm 版的全體屏障（階段、緊急程度、先發布再 UpdateFinished 且帶身分、延後、取消、app 結束、三方不一致重跑一次）、受管 Codex 不經屏障
/// </summary>
public sealed class CliManagerUpdateTests
{
    private static readonly CliUpdatePhase[] NpmPhases =
    {
        CliUpdatePhase.Draining, CliUpdatePhase.WaitingForProcesses, CliUpdatePhase.Installing, CliUpdatePhase.Verifying, CliUpdatePhase.None,
    };

    /// <summary>更新開始之後的階段（第一個不是 None 起）</summary>
    private static List<CliUpdatePhase> PhasesOfUpdate(CliEventLog log) => log.Phases.SkipWhile(p => p == CliUpdatePhase.None).ToList();

    // ───── Claude 原生：claude update，不經屏障 ─────

    private sealed class ClaudeSetup : IDisposable
    {
        public ClaudeSetup(string latest)
        {
            Temp = new TempDirectory("cli-update");
            Claude = new ClaudeLayout(Temp);
            Commands = FakeCliCommands.For(Claude);
            Http = new HttpClient(new FakeRegistry { Latest = latest });
            Cli = Create(Http, p => p == AgentProvider.Claude ? Claude.Installation : null, Commands, new FakeProcesses(), AgentProvider.Claude, background: false);
            Log = new CliEventLog(Cli, AgentProvider.Claude);
            Participant = new RecordingParticipant(Log);
            Cli.UpdateParticipant = Participant;
        }

        public TempDirectory Temp { get; }

        public ClaudeLayout Claude { get; }

        public FakeCliCommands Commands { get; }

        public HttpClient Http { get; }

        public CliManager Cli { get; }

        public CliEventLog Log { get; }

        public RecordingParticipant Participant { get; }

        public void Dispose()
        {
            Cli.Dispose();
            Http.Dispose();
            Temp.Dispose();
        }
    }

    [Fact]
    public async Task Claude原生更新_不經屏障_解析輸出_以FileVersionInfo驗證_發布App來源的新身分()
    {
        using var s = new ClaudeSetup(latest: ClaudeLayout.NewVersion);
        await s.Cli.CheckAsync(AgentProvider.Claude);
        var before = s.Cli.GetIdentity(AgentProvider.Claude)!;
        s.Cli.Get(AgentProvider.Claude).UpdateAvailable.Should().BeTrue();
        CliUpdatePhase? phaseDuringCommand = null;
        s.Commands.Update = pid =>
        {
            phaseDuringCommand = s.Cli.Get(AgentProvider.Claude).UpdatePhase;
            s.Claude.Swap(pid, ClaudeLayout.NewSource);   // claude update 以它自己的 PID 命名暫存檔
            return new CliRunResult(0, $"Current version: {ClaudeLayout.OldVersion}\nSuccessfully updated from {ClaudeLayout.OldVersion} to version {ClaudeLayout.NewVersion}\n", string.Empty, false);
        };

        await s.Cli.UpdateAsync(AgentProvider.Claude);

        s.Participant.Prepares.Count.Should().Be(0, "Claude 原生安裝換檔用改名，執行中的舊程序照常回應：不需要屏障");
        s.Participant.Finished.Count.Should().Be(0, "沒有呼叫 Prepare 就不呼叫 UpdateFinished");
        phaseDuringCommand.Should().Be(CliUpdatePhase.Installing);
        PhasesOfUpdate(s.Log).Should().Equal(CliUpdatePhase.Installing, CliUpdatePhase.Verifying, CliUpdatePhase.None);
        var switched = s.Log.Changes.Select(c => c.Change).Single(c => c.IsVersionSwitch);
        switched.Old.Should().Be(before);
        switched.New!.Version.Should().Be(ClaudeLayout.NewVersion);
        switched.Origin.Should().Be(CliChangeOrigin.App);
        var state = s.Cli.Get(AgentProvider.Claude);
        state.Busy.Should().BeFalse();
        state.ActionError.Should().BeNull();
        state.UpdateStartedAt.Should().BeNull();
        state.UpdateAvailable.Should().BeFalse();
        s.Commands.Calls.All.Should().Contain("update");
    }

    [Fact]
    public async Task Claude原生更新_版本鎖被占住_結束碼0也不算成功_說明30分鐘後再試_不算沒有效果()
    {
        using var s = new ClaudeSetup(latest: ClaudeLayout.NewVersion);
        await s.Cli.CheckAsync(AgentProvider.Claude);
        s.Commands.Update = _ => new CliRunResult(0, $"Current version: {ClaudeLayout.OldVersion}\nAnother Claude process (PID 4242) is currently running. Please try again in a moment.\n", string.Empty, false);

        await s.Cli.UpdateAsync(AgentProvider.Claude);

        var state = s.Cli.Get(AgentProvider.Claude);
        state.ActionError.Should().Be("另一個 Claude 程序（PID 4242）正在更新 Claude Code，30 分鐘後再試");
        state.Version.Should().Be(ClaudeLayout.OldVersion);
        s.Log.Changes.Should().NotContain(c => c.Change.IsVersionSwitch);
    }

    [Fact]
    public async Task Claude原生更新_回報成功卻沒換檔_以FileVersionInfo驗證算失敗()
    {
        using var s = new ClaudeSetup(latest: ClaudeLayout.NewVersion);
        await s.Cli.CheckAsync(AgentProvider.Claude);
        s.Commands.Update = _ => new CliRunResult(0, $"Successfully updated from {ClaudeLayout.OldVersion} to version {ClaudeLayout.NewVersion}\n", string.Empty, false);

        await s.Cli.UpdateAsync(AgentProvider.Claude);

        s.Cli.Get(AgentProvider.Claude).ActionError.Should().Contain($"回報已更新到 {ClaudeLayout.NewVersion}").And.Contain($"仍是 {ClaudeLayout.OldVersion}");
        s.Log.Changes.Should().NotContain(c => c.Change.IsVersionSwitch);
    }

    [Fact]
    public async Task Claude原生更新_已是最新版卻仍有新版_說明原因並記下不再自動重試()
    {
        using var s = new ClaudeSetup(latest: ClaudeLayout.NewVersion);
        await s.Cli.CheckAsync(AgentProvider.Claude);
        s.Commands.Update = _ => new CliRunResult(0, "Current version: x\nClaude is managed by winget.\nTo update, run:\n  winget upgrade Anthropic.ClaudeCode\n", string.Empty, false);

        await s.Cli.UpdateAsync(AgentProvider.Claude);

        s.Cli.Get(AgentProvider.Claude).ActionError.Should().Be($"更新指令已執行，但版本仍是 {ClaudeLayout.OldVersion}：Claude is managed by winget. To update, run: winget upgrade Anthropic.ClaudeCode");
    }

    [Fact]
    public async Task 換版時清掉上次安裝或更新失敗的原因_版本沒變仍有新版時保留()
    {
        using var s = new ClaudeSetup(latest: ClaudeLayout.NewVersion);
        await s.Cli.CheckAsync(AgentProvider.Claude);
        s.Cli.Update(AgentProvider.Claude, st => st with { ActionError = "更新失敗：網路中斷" });

        await s.Cli.CheckAsync(AgentProvider.Claude);
        s.Cli.Get(AgentProvider.Claude).ActionError.Should().Be("更新失敗：網路中斷", "版本沒變、仍有新版：保留");

        s.Claude.Swap(updaterPid: 4242, ClaudeLayout.NewSource);   // 使用者在終端機更新了
        await s.Cli.CheckAsync(AgentProvider.Claude);
        s.Cli.Get(AgentProvider.Claude).ActionError.Should().BeNull("換了版：上次的失敗原因已經過時（同 KeepActionError 的「版本變了就不再顯示」）");
        s.Cli.Get(AgentProvider.Claude).LastChange!.Origin.Should().Be(CliChangeOrigin.External);
    }

    [Fact]
    public async Task 自動更新_有對話在跑時Claude原生也延後_不打擾()
    {
        using var temp = new TempDirectory("cli-update");
        var claude = new ClaudeLayout(temp);
        var commands = FakeCliCommands.For(claude);
        using var http = new HttpClient(new FakeRegistry { Latest = ClaudeLayout.NewVersion });
        using var cli = Create(http, _ => claude.Installation, commands, new FakeProcesses(), AgentProvider.Claude, background: false, runningSessions: _ => true);
        await cli.CheckAsync(AgentProvider.Claude);

        await cli.UpdateAsync(AgentProvider.Claude, automatic: true);

        commands.Count("update").Should().Be(0, "自動更新不打擾執行中的對話：延後再試");
        cli.Get(AgentProvider.Claude).ActionError.Should().BeNull();
        cli.Get(AgentProvider.Claude).Busy.Should().BeFalse();
    }

    // ───── npm 版：7.3.1 的全體屏障 ─────

    private sealed class CodexSetup : IDisposable
    {
        public CodexSetup(Func<CliManagerOptions, CliManagerOptions>? configure = null)
        {
            Temp = new TempDirectory("cli-update");
            Codex = new CodexNpmLayout(Temp, "0.160.1");
            Commands = FakeCliCommands.For(Codex);
            Http = new HttpClient(new FakeRegistry { Latest = "0.162.0" });
            Cli = Create(Http, p => p == AgentProvider.Codex ? Codex.Installation : null, Commands, Processes, AgentProvider.Codex, background: false, configure);
            Log = new CliEventLog(Cli, AgentProvider.Codex);
            Participant = new RecordingParticipant(Log);
            Cli.UpdateParticipant = Participant;
            Commands.Npm = (_, _) =>
            {
                NpmPhase = Cli.Get(AgentProvider.Codex).UpdatePhase;
                NpmInstalling = Cli.Get(AgentProvider.Codex).NpmInstalling;
                InstallVersion(Next());
                return new CliRunResult(0, "changed 2 packages in 3s", string.Empty, false);
            };
        }

        public TempDirectory Temp { get; }

        /// <summary>假的程序查詢（安裝判定看得到的 npm 程序、版本鎖的持有者）</summary>
        public FakeProcesses Processes { get; } = new();

        public CodexNpmLayout Codex { get; }

        public FakeCliCommands Commands { get; }

        public HttpClient Http { get; }

        public CliManager Cli { get; }

        public CliEventLog Log { get; }

        public RecordingParticipant Participant { get; }

        public CliUpdatePhase? NpmPhase { get; private set; }

        public bool NpmInstalling { get; private set; }

        /// <summary>每次 npm 要放的（package.json、vendor）版本；用完後重複最後一個</summary>
        public Queue<(string Package, string Vendor)> Versions { get; } = new(new[] { ("0.162.0", "0.162.0") });

        private (string, string) last = ("0.162.0", "0.162.0");

        private (string Package, string Vendor) Next() => last = Versions.Count > 0 ? Versions.Dequeue() : last;

        /// <summary>照 npm install -g 的順序換掉套件（研究 E2）：retire 改名 → 解壓新版 → 刪 retire → 重寫殼</summary>
        public void InstallVersion((string Package, string Vendor) version)
        {
            Codex.Retire();
            Codex.WritePackage(version.Package, version.Vendor, extraFiles: 3);
            Codex.RemoveTrash();
            Codex.RewriteShims();
        }

        public void Dispose()
        {
            Cli.Dispose();
            Http.Dispose();
            Temp.Dispose();
        }
    }

    [Fact]
    public async Task Npm手動更新_Normal排空_階段依序_換檔後先發布新身分再UpdateFinished帶新身分()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var before = s.Cli.GetIdentity(AgentProvider.Codex)!;
        CliState? atPrepare = null;
        s.Participant.OnPrepare = (_, _, _) =>
        {
            atPrepare = s.Cli.Get(AgentProvider.Codex);
            return Task.FromResult(new CliUpdatePreparation(Ready: true, ClosedIdle: 1, Held: 1, Busy: 0));
        };

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Participant.Prepares.All.Should().Equal((AgentProvider.Codex, CliUpdateUrgency.Normal));
        atPrepare!.UpdatePhase.Should().Be(CliUpdatePhase.Draining);
        atPrepare.UpdateStartedAt.Should().NotBeNull("CLI 卡的「已等待 N 分鐘」");
        atPrepare.Busy.Should().BeTrue();
        s.NpmPhase.Should().Be(CliUpdatePhase.Installing);
        s.NpmInstalling.Should().BeTrue("npm 換檔期間結束 app 要提示「CLI 安裝不完整」（AppControllerRules.ExitWarnings）");
        PhasesOfUpdate(s.Log).Should().Equal(NpmPhases);
        s.Commands.Calls.All.Should().Contain(@"/d /c call C:\fake npm\npm.cmd install -g @openai/codex@latest", "跑使用者實際會用的 npm（cmd /c npm.cmd）");

        var finished = s.Participant.Finished.All.Should().ContainSingle().Subject;
        finished.Outcome.Should().Be(CliUpdateOutcome.Updated);
        finished.Identity.Should().NotBeNull("帶穩定後的新身分：停下的對話以它重開並接續（不是「沒有可用的版本」）");
        finished.Identity!.Version.Should().Be("0.162.0");
        finished.Identity.Should().Be(s.Cli.GetIdentity(AgentProvider.Codex));
        finished.Error.Should().BeNull();

        var entries = s.Log.Entries;
        var switchIndex = entries.FindIndex(e => e.StartsWith("VersionChanged") && e.Contains("switch=True"));
        switchIndex.Should().BeGreaterThan(entries.IndexOf("Prepare Codex Normal"));
        entries.IndexOf("Finished Updated 0.162.0").Should().BeGreaterThan(switchIndex, "有換版時 UpdateFinished 在 VersionChanged 之後");
        var switched = s.Log.Changes.Single(c => c.Change.IsVersionSwitch).Change;
        switched.Old.Should().Be(before);
        switched.Origin.Should().Be(CliChangeOrigin.App);
        var state = s.Cli.Get(AgentProvider.Codex);
        state.Busy.Should().BeFalse("UpdateFinished 之前就不再 Busy：中樞解除屏障時排隊的送出才送得出去");
        state.UpdatePhase.Should().Be(CliUpdatePhase.None);
        state.ActionError.Should().BeNull();
    }

    [Fact]
    public async Task Npm自動更新_Background_有對話在跑就延後_什麼都不換_UpdateFinished帶目前身分()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var current = s.Cli.GetIdentity(AgentProvider.Codex);
        s.Participant.OnPrepare = (_, _, _) => Task.FromResult(new CliUpdatePreparation(Ready: false, ClosedIdle: 0, Held: 0, Busy: 2));

        await s.Cli.UpdateAsync(AgentProvider.Codex, automatic: true);

        s.Participant.Prepares.All.Should().Equal((AgentProvider.Codex, CliUpdateUrgency.Background));
        s.Commands.Count("install").Should().Be(0, "Ready=false：還有程序在用舊檔，npm 一定 EBUSY");
        s.Participant.Finished.All.Should().ContainSingle().Which.Should().Be(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, current));
        s.Cli.Get(AgentProvider.Codex).ActionError.Should().BeNull("自動更新安靜地延後");
        s.Cli.Get(AgentProvider.Codex).Busy.Should().BeFalse();
    }

    [Fact]
    public async Task Npm手動更新_使用者取消_不換檔_UpdateFinished帶目前身分_說明原因()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var current = s.Cli.GetIdentity(AgentProvider.Codex);
        s.Participant.OnPrepare = (_, _, _) => Task.FromResult(new CliUpdatePreparation(Ready: false, ClosedIdle: 1, Held: 1, Busy: 1, "已取消更新"));

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Commands.Count("install").Should().Be(0);
        s.Participant.Finished.All.Should().ContainSingle().Which.Should().Be(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, current, "已取消更新"),
            "停下的對話以目前的版本接續");
        s.Cli.Get(AgentProvider.Codex).ActionError.Should().Be("已取消更新");
    }

    [Fact]
    public async Task Npm手動更新_還有對話沒停下又沒有原因_依Busy說明_不換檔()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        s.Participant.OnPrepare = (_, _, _) => Task.FromResult(new CliUpdatePreparation(Ready: false, ClosedIdle: 0, Held: 0, Busy: 2));

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Commands.Count("install").Should().Be(0);
        s.Cli.Get(AgentProvider.Codex).ActionError.Should().Contain("還有 2 個 Codex 對話");
        s.Participant.Finished.All.Single().Outcome.Should().Be(CliUpdateOutcome.Cancelled);
    }

    [Fact]
    public async Task 排空沒有時限_只有app結束會取消_取消後不跑npm_仍呼叫UpdateFinished()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var current = s.Cli.GetIdentity(AgentProvider.Codex);
        s.Participant.OnPrepare = async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new CliUpdatePreparation(true, 0, 0, 0);
        };

        var update = Task.Run(() => s.Cli.UpdateAsync(AgentProvider.Codex));
        await WaitAsync(() => s.Participant.Prepares.Count == 1, "開始排空");
        await Task.Delay(TimeSpan.FromSeconds(16));
        update.IsCompleted.Should().BeFalse("拿掉 15 秒的上限：排空可能要等工具跑完（中樞超過 10 分鐘會問使用者）");

        s.Cli.Dispose();   // app 結束
        await update.WaitAsync(TimeSpan.FromSeconds(10));

        s.Commands.Count("install").Should().Be(0, "取消的排空不能往下跑 npm");
        s.Participant.Finished.All.Should().ContainSingle().Which.Should().Be(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, current));
    }

    [Fact]
    public async Task 中樞作廢屏障_Prepare以取消結束_不跑npm()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var pending = new TaskCompletionSource<CliUpdatePreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Participant.OnPrepare = (_, _, _) => pending.Task;

        var update = Task.Run(() => s.Cli.UpdateAsync(AgentProvider.Codex));
        await WaitAsync(() => s.Participant.Prepares.Count == 1, "開始排空");
        pending.TrySetCanceled();   // AgentHub.Dispose → CancelUpdateBarriers
        await update.WaitAsync(TimeSpan.FromSeconds(10));

        s.Commands.Count("install").Should().Be(0);
        s.Participant.Finished.All.Single().Outcome.Should().Be(CliUpdateOutcome.Cancelled);
        s.Cli.Get(AgentProvider.Codex).Busy.Should().BeFalse();
    }

    [Fact]
    public async Task 三方不一致重跑一次npm_仍不一致就是安裝損壞_不重開任何對話()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var current = s.Cli.GetIdentity(AgentProvider.Codex);
        s.Versions.Clear();
        s.Versions.Enqueue(("0.162.0", "0.160.1"));   // npm rollback 留下的混版，兩次都一樣

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Commands.Count("install").Should().Be(2, "不一致重跑一次");
        PhasesOfUpdate(s.Log).Should().Equal(CliUpdatePhase.Draining, CliUpdatePhase.WaitingForProcesses, CliUpdatePhase.Installing, CliUpdatePhase.Verifying,
            CliUpdatePhase.Installing, CliUpdatePhase.Verifying, CliUpdatePhase.None);
        var broken = s.Log.Changes.Select(c => c.Change).Should().ContainSingle(c => c.InstallBroken).Subject;
        broken.IsVersionSwitch.Should().BeFalse();
        var finished = s.Participant.Finished.All.Should().ContainSingle().Subject;
        finished.Outcome.Should().Be(CliUpdateOutcome.Failed);
        finished.Identity.Should().BeNull("安裝損壞：停下的對話不重開，以提示收尾");
        finished.Error.Should().Contain("安裝不一致");
        var state = s.Cli.Get(AgentProvider.Codex);
        state.InstallBroken.Should().BeTrue();
        state.Identity.Should().Be(current, "身分保留最後可用的");
        state.ActionError.Should().Contain("安裝不一致");
    }

    [Fact]
    public async Task 三方不一致_重跑npm後一致_照常算更新成功()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        s.Versions.Clear();
        s.Versions.Enqueue(("0.162.0", "0.160.1"));
        s.Versions.Enqueue(("0.162.0", "0.162.0"));

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Commands.Count("install").Should().Be(2);
        var finished = s.Participant.Finished.All.Single();
        finished.Outcome.Should().Be(CliUpdateOutcome.Updated);
        finished.Identity!.Version.Should().Be("0.162.0");
        s.Cli.Get(AgentProvider.Codex).InstallBroken.Should().BeFalse();
    }

    [Fact]
    public async Task Npm失敗而且回滾到舊版_UpdateFinished以仍可用的舊身分結束()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var current = s.Cli.GetIdentity(AgentProvider.Codex);
        s.Commands.Npm = (_, _) => new CliRunResult(1, string.Empty, "npm error code ENOTFOUND\nnpm error network request to https://registry.npmjs.org/@openai%2fcodex failed", false);

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        var finished = s.Participant.Finished.All.Single();
        finished.Outcome.Should().Be(CliUpdateOutcome.Failed);
        finished.Identity.Should().Be(current, "失敗時帶仍可用的舊版：停下的對話照樣接續");
        finished.Error.Should().StartWith("npm 更新失敗：npm error network request");
    }

    [Fact]
    public async Task 屏障之後app自己的這種CLI程序最多等設定的時間_還沒結束就不換檔()
    {
        var remaining = true;
        string? vendorExe = null;
        using var s = new CodexSetup(o => o with
        {
            AppProcessIds = () => remaining ? new[] { 777 } : Array.Empty<int>(),
            ProcessImagePath = pid => pid == 777 ? vendorExe : null,
            AppProcessExitWait = TimeSpan.FromMilliseconds(500),
        });
        vendorExe = s.Codex.Installation.NativeExecutable;
        await s.Cli.CheckAsync(AgentProvider.Codex);

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Commands.Count("install").Should().Be(0, "帳戶查詢的 app-server、登入程序還在用舊檔");
        s.Cli.Get(AgentProvider.Codex).ActionError.Should().Contain("還有 1 個 app 自己的 Codex 程序");
        s.Participant.Finished.All.Single().Outcome.Should().Be(CliUpdateOutcome.Cancelled);

        // 進行中的查詢在時限內結束：照常換檔
        var gone = Task.Delay(200).ContinueWith(_ => remaining = false);
        await s.Cli.UpdateAsync(AgentProvider.Codex);
        await gone;
        s.Commands.Count("install").Should().Be(1);
        s.Participant.Finished.All.Last().Outcome.Should().Be(CliUpdateOutcome.Updated);
    }

    [Fact]
    public async Task 沒有中樞_仍照常換檔_不呼叫屏障()
    {
        using var temp = new TempDirectory("cli-update");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var commands = FakeCliCommands.For(codex);
        commands.Npm = (_, _) =>
        {
            codex.Retire();
            codex.WritePackage("0.162.0", "0.162.0", 3);
            codex.RemoveTrash();
            return new CliRunResult(0, string.Empty, string.Empty, false);
        };
        using var http = new HttpClient(new FakeRegistry { Latest = "0.162.0" });
        using var cli = Create(http, _ => codex.Installation, commands, new FakeProcesses(), AgentProvider.Codex, background: false);
        await cli.CheckAsync(AgentProvider.Codex);

        await cli.UpdateAsync(AgentProvider.Codex);

        cli.GetIdentity(AgentProvider.Codex)!.Version.Should().Be("0.162.0");
    }

    // ───── app 自管的 Codex：換資料夾用改名，不需要屏障 ─────

    [Fact]
    public async Task 受管Codex更新不經屏障()
    {
        using var temp = new TempDirectory("cli-update");
        var exe = temp.WriteFile(@"managed\codex\codex.exe", "managed codex");
        var managed = new CliInstallation(AgentProvider.Codex, new AgentLauncher(exe, Array.Empty<string>()), CliInstallKind.Managed, exe) { NativeExecutable = exe };
        var commands = new FakeCliCommands(_ => "codex-cli 0.160.1");
        using var http = new HttpClient(new FakeRegistry { Latest = "0.162.0" });   // GitHub 的最新版資訊回 404：下載失敗
        using var cli = Create(http, _ => managed, commands, new FakeProcesses(), AgentProvider.Codex, background: false);
        var log = new CliEventLog(cli, AgentProvider.Codex);
        var participant = new RecordingParticipant(log);
        cli.UpdateParticipant = participant;
        await cli.CheckAsync(AgentProvider.Codex);
        cli.GetIdentity(AgentProvider.Codex)!.Version.Should().Be("0.160.1");

        await cli.UpdateAsync(AgentProvider.Codex);

        participant.Prepares.Count.Should().Be(0, "受管 Codex 舊資料夾改名再放新版（執行中的 exe 所在資料夾可以改名）：不排空對話");
        participant.Finished.Count.Should().Be(0);
        PhasesOfUpdate(log).Should().Equal(CliUpdatePhase.Installing, CliUpdatePhase.None);
        cli.Get(AgentProvider.Codex).ActionError.Should().StartWith("更新失敗：讀取 Codex 最新版本資訊失敗");
    }

    // ───── 安裝 ─────

    [Fact]
    public async Task 安裝Codex_沒有既有的npm版_不經屏障_裝好後重新找並發布身分()
    {
        using var temp = new TempDirectory("cli-update");
        CodexNpmLayout? codex = null;
        var commands = new FakeCliCommands(_ => codex == null ? null : FakeCliCommands.For(codex).Version(null!));
        commands.Npm = (_, _) =>
        {
            codex = new CodexNpmLayout(temp, "0.162.0");
            return new CliRunResult(0, "added 2 packages", string.Empty, false);
        };
        using var http = new HttpClient(new FakeRegistry { Latest = "0.162.0" });
        using var cli = Create(http, p => p == AgentProvider.Codex ? codex?.Installation : null, commands, new FakeProcesses(), AgentProvider.Codex, background: false);
        var log = new CliEventLog(cli, AgentProvider.Codex);
        var participant = new RecordingParticipant(log);
        cli.UpdateParticipant = participant;
        cli.Get(AgentProvider.Codex).Installation.Should().BeNull();

        await cli.InstallAsync(AgentProvider.Codex);

        participant.Prepares.Count.Should().Be(0, "還沒有 npm 版：沒有程序在用要被換掉的檔案");
        var first = log.Changes.Select(c => c.Change).First(c => c.New != null);
        first.Old.Should().BeNull();
        first.New!.Version.Should().Be("0.162.0");
        var state = cli.Get(AgentProvider.Codex);
        state.Installed.Should().BeTrue();
        state.Installation.Should().BeSameAs(codex!.Installation);
        state.ActionError.Should().BeNull();
    }

    [Fact]
    public async Task 重新安裝既有的npm版Codex_與更新一樣走屏障()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);

        await s.Cli.InstallAsync(AgentProvider.Codex);

        s.Participant.Prepares.All.Should().Equal((AgentProvider.Codex, CliUpdateUrgency.Normal));
        s.Participant.Finished.All.Single().Identity!.Version.Should().Be("0.162.0");
    }

    // ───── 審查後的修正（cli-update-1～5、accounts-ui-3） ─────

    private const string ExternalNpm = "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js\" i -g @openai/codex";

    [Fact]
    public async Task 外部npm正在改同一個套件_手動更新不排空_不刪對方的退役資料夾_不跑第二個npm_說明原因()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        // 終端機的 npm i -g @openai/codex 正在換檔：舊版已退役成 .codex-<雜湊>（它回滾要用的備份），新版還在解壓
        s.Processes.StartNpm(4242, ExternalNpm);
        s.Codex.Retire();
        var retired = Path.Combine(s.Codex.Scope, ".codex-nsHu5pVi");

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Participant.Prepares.Count.Should().Be(0, "排空之前就知道不能更新：不停任何對話");
        s.Participant.Finished.Count.Should().Be(0);
        s.Commands.Count(" install -g ").Should().Be(0, "兩個 npm 同時改同一個套件，失敗的那個回滾後 CLI 可能從電腦上消失");
        Directory.Exists(retired).Should().BeTrue("對方 npm 回滾要用的退役資料夾不能刪");
        var state = s.Cli.Get(AgentProvider.Codex);
        state.ActionError.Should().Be("Codex 的安裝正在被其他程式安裝或更新（npm 正在安裝或更新全域套件（PID 4242）），這次沒有更新；請等它完成後再更新");
        state.Busy.Should().BeFalse();
        state.UpdatePhase.Should().Be(CliUpdatePhase.None);

        // 自動更新：安靜地延後
        await s.Cli.UpdateAsync(AgentProvider.Codex, automatic: true);
        s.Participant.Prepares.Count.Should().Be(0);
        s.Commands.Count(" install -g ").Should().Be(0);
        s.Cli.Get(AgentProvider.Codex).ActionError.Should().BeNull();
        PhasesOfUpdate(s.Log).Should().NotContain(CliUpdatePhase.Draining);
    }

    [Fact]
    public async Task 排空期間才開始的外部npm_換檔前再確認一次_不清殘留不跑npm_停下的對話以目前的版本接續()
    {
        using var s = new CodexSetup();
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var current = s.Cli.GetIdentity(AgentProvider.Codex);
        s.Participant.OnPrepare = (_, _, _) =>
        {
            s.Processes.StartNpm(4243, ExternalNpm);   // 等對話停下的這段期間，使用者在終端機跑了 npm
            return Task.FromResult(new CliUpdatePreparation(Ready: true, ClosedIdle: 0, Held: 1, Busy: 0));
        };

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Commands.Count(" install -g ").Should().Be(0);
        var finished = s.Participant.Finished.All.Should().ContainSingle().Subject;
        finished.Outcome.Should().Be(CliUpdateOutcome.Cancelled);
        finished.Identity.Should().Be(current, "沒有換檔：停下的對話以目前的版本接續");
        finished.Error.Should().Contain("PID 4243");
    }

    [Fact]
    public async Task 自動更新_有app以外的程序在用安裝_排空之前就延後_不關任何對話程序()
    {
        const int foreignPid = 16628;
        string? foreignImage = null;
        using var s = new CodexSetup(o => o with
        {
            AppProcessIds = () => Array.Empty<int>(),
            AllProcessIds = () => new[] { foreignPid },
            ProcessImagePath = pid => pid == foreignPid ? foreignImage : null,
        });
        foreignImage = s.Codex.Installation.NativeExecutable;   // 終端機開著 npm 安裝的 codex
        await s.Cli.CheckAsync(AgentProvider.Codex);

        await s.Cli.UpdateAsync(AgentProvider.Codex, automatic: true);

        s.Participant.Prepares.Count.Should().Be(0, "Background 排空會先關掉所有閒置的對話程序：每 30 分鐘重試一次就關一次，結果還是延後");
        s.Participant.Finished.Count.Should().Be(0);
        s.Commands.Count(" install -g ").Should().Be(0);
        PhasesOfUpdate(s.Log).Should().NotContain(CliUpdatePhase.Draining);
        s.Cli.Get(AgentProvider.Codex).ActionError.Should().BeNull("自動更新安靜地延後");
    }

    private static void WriteClaudePackageJson(string packageRoot, string version)
        => File.WriteAllText(Path.Combine(packageRoot, "package.json"), "{\"name\": \"@anthropic-ai/claude-code\", \"version\": \"" + version + "\"}");

    [Fact]
    public async Task Npm版Claude從cliJs的舊配置更新到binClaudeExe_換檔後重新找安裝再驗證_不誤判損壞也不白跑第二次npm()
    {
        using var temp = new TempDirectory("cli-update");
        var prefix = temp.Combine("npm");
        var package = Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(prefix, "claude.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(prefix, "node.exe"), "fake node");
        var cliJs = Path.Combine(package, "cli.js");
        File.WriteAllText(cliJs, "// cli.js 2.1.112");
        WriteClaudePackageJson(package, "2.1.112");
        var nowhere = temp.Combine("none");
        CliInstallation? Locate(AgentProvider p) => p == AgentProvider.Claude
            ? CliLocator.Resolve(p, new[] { prefix }, File.Exists, nowhere, prefix, nowhere, path => new FileInfo(path).Length)
            : null;
        Locate(AgentProvider.Claude)!.NativeExecutable.Should().Be(cliJs, "2.1.112 以前的配置以 cli.js 代表原生檔");
        var commands = new FakeCliCommands(launcher =>
        {
            // node cli.js --version 讀 package.json；bin\claude.exe --version 讀版本資訊
            if (launcher.PrefixArguments.Count > 0)
                return File.Exists(launcher.PrefixArguments[0]) ? CliBinaryIdentityRules.ReadPackageVersion(Path.Combine(package, "package.json")) + " (Claude Code)" : null;
            return CliBinaryIdentityRules.ReadClaudeFileVersion(launcher.FileName) is { } v ? v + " (Claude Code)" : null;
        });
        commands.Npm = (_, _) =>
        {
            // 2.1.113 起：套件裡沒有 cli.js，postinstall 把原生檔放到 bin\claude.exe
            File.Delete(cliJs);
            var exe = Path.Combine(package, "bin", "claude.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.Copy(ClaudeLayout.NewSource, exe, overwrite: true);
            WriteClaudePackageJson(package, ClaudeLayout.NewVersion);
            return new CliRunResult(0, "changed 3 packages", string.Empty, false);
        };
        using var http = new HttpClient(new FakeRegistry { Latest = "99.0.0" });
        using var cli = Create(http, Locate, commands, new FakeProcesses(), AgentProvider.Claude, background: false);
        var participant = new RecordingParticipant();
        cli.UpdateParticipant = participant;
        await cli.CheckAsync(AgentProvider.Claude);
        cli.GetIdentity(AgentProvider.Claude)!.Version.Should().Be("2.1.112");

        await cli.UpdateAsync(AgentProvider.Claude);

        commands.Count(" install -g ").Should().Be(1, "不是安裝不一致：不必再跑一次 npm");
        var finished = participant.Finished.All.Should().ContainSingle().Subject;
        finished.Outcome.Should().Be(CliUpdateOutcome.Updated, "新版完好：不能以「找不到 cli.js」收尾停下的對話");
        finished.Identity!.Version.Should().Be(ClaudeLayout.NewVersion);
        finished.Identity.NativeExecutable.Should().EndWith(Path.Combine("bin", "claude.exe"));
        var state = cli.Get(AgentProvider.Claude);
        state.InstallBroken.Should().BeFalse();
        state.ActionError.Should().BeNull();
        state.Installation!.NativeExecutable.Should().EndWith(Path.Combine("bin", "claude.exe"));
    }

    [Fact]
    public async Task Version暫時失敗而判定損壞_稍後自動再判定_好了就清掉()
    {
        using var temp = new TempDirectory("cli-update");
        var claude = new ClaudeLayout(temp);
        var failing = new System.Runtime.CompilerServices.StrongBox<bool>(true);
        var commands = new FakeCliCommands(_ => failing.Value ? null : CliBinaryIdentityRules.ReadClaudeFileVersion(claude.Exe) + " (Claude Code)");
        using var http = new HttpClient(new FakeRegistry());
        using var cli = Create(http, _ => claude.Installation, commands, new FakeProcesses(), AgentProvider.Claude, background: false,
            o => o with { VersionFailureRecheck = TimeSpan.FromMilliseconds(300) });
        await cli.CheckAsync(AgentProvider.Claude);
        cli.Get(AgentProvider.Claude).InstallBroken.Should().BeTrue("--version 兩次都失敗（開機剛登入、防毒正在掃描）");

        failing.Value = false;   // 暫時性的原因消失：檔案沒變、沒有新的訊號，也沒人按立即檢查

        await WaitAsync(() => !cli.Get(AgentProvider.Claude).InstallBroken, "稍後自動再判定一次，好了就清掉損壞", seconds: 15);
        cli.GetIdentity(AgentProvider.Claude)!.Version.Should().Be(ClaudeLayout.OldVersion);
    }

    [Fact]
    public async Task Version一直失敗_自動再判定有次數上限()
    {
        using var temp = new TempDirectory("cli-update");
        var claude = new ClaudeLayout(temp);
        var commands = new FakeCliCommands(_ => null);
        using var http = new HttpClient(new FakeRegistry());
        using var cli = Create(http, _ => claude.Installation, commands, new FakeProcesses(), AgentProvider.Claude, background: false,
            o => o with { VersionFailureRecheck = TimeSpan.FromMilliseconds(200), VersionFailureRechecks = 2 });
        await cli.CheckAsync(AgentProvider.Claude);
        commands.Count("--version").Should().Be(2, "失敗先等 1 秒再試一次");

        await WaitAsync(() => commands.Count("--version") >= 6, "自動再判定兩次（每次兩次 --version）", seconds: 20);
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        commands.Count("--version").Should().Be(6, "連續失敗到上限就不再自動判定（等檔案變動、立即檢查或重開 app）");
        cli.Get(AgentProvider.Claude).InstallBroken.Should().BeTrue();
    }

    [Fact]
    public async Task app結束時還在等其他程序結束_更新取消_不再啟動npm_仍呼叫UpdateFinished()
    {
        CliManager? manager = null;
        using var s = new CodexSetup(o => o with
        {
            // 排空完成、正在等 app 自己這種 CLI 的程序結束時，使用者確認結束 app
            AppProcessIds = () =>
            {
                manager!.BeginShutdown();
                return Array.Empty<int>();
            },
        });
        manager = s.Cli;
        await s.Cli.CheckAsync(AgentProvider.Codex);
        var current = s.Cli.GetIdentity(AgentProvider.Codex);

        await s.Cli.UpdateAsync(AgentProvider.Codex);

        s.Commands.Count(" install -g ").Should().Be(0, "結束確認框說的是「還沒開始換檔，結束會取消這次更新」：不能在中樞收尾的 8 秒內開始換檔");
        s.Participant.Finished.All.Should().ContainSingle().Which.Should().Be(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Cancelled, current));
        s.Cli.Get(AgentProvider.Codex).UpdatePhase.Should().Be(CliUpdatePhase.None);
    }

    [Fact]
    public async Task 在app裡重新安裝Claude_安裝視窗的子程序換檔_watcher先宣告也算app的更新()
    {
        using var temp = new TempDirectory("cli-install-origin");
        var claude = new ClaudeLayout(temp);
        var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        // 官方安裝程式：安裝視窗（PowerShell）開的 claude install 以它自己的 PID 命名暫存檔再換檔（同 claude update），之後視窗停 5 秒才關（這裡縮短）
        var child = temp.WriteFile("child.ps1", string.Join("\n",
            "param([string]$exe, [string]$src)",
            "Start-Sleep -Milliseconds 1200",
            "$ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()",
            "$staged = $exe + '.new.' + $PID + '.' + $ms + '.abcd1234'",
            "[System.IO.File]::Copy($src, $staged)",
            "[System.IO.File]::Move($exe, $exe + '.old.' + $ms + '.' + $PID)",
            "[System.IO.File]::Move($staged, $exe)"));
        var installer = temp.WriteFile("installer.ps1", string.Join("\n",
            "param([string]$ps, [string]$child, [string]$exe, [string]$src)",
            "& $ps -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $child $exe $src",
            "Start-Sleep -Milliseconds 1500",
            "exit 0"));
        System.Diagnostics.Process StartInstaller()
        {
            var psi = new System.Diagnostics.ProcessStartInfo(powerShell) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", installer, powerShell, child, claude.Exe, ClaudeLayout.NewSource })
                psi.ArgumentList.Add(arg);
            return System.Diagnostics.Process.Start(psi)!;
        }
        using var http = new HttpClient(new FakeRegistry { Latest = ClaudeLayout.NewVersion });
        using var cli = Create(http, _ => claude.Installation, FakeCliCommands.For(claude), new FakeProcesses(), AgentProvider.Claude, background: true,
            o => o with { StartClaudeInstaller = StartInstaller });
        var log = new CliEventLog(cli, AgentProvider.Claude);
        cli.UpdateParticipant = new RecordingParticipant(log);
        await WaitAsync(() => cli.GetIdentity(AgentProvider.Claude) != null, "啟動時第一次讀到身分");

        await cli.InstallAsync(AgentProvider.Claude);

        var switched = await log.WaitForChangeAsync(c => c.IsVersionSwitch, "重新安裝換了版");
        switched.ToVersion.Should().Be(ClaudeLayout.NewVersion);
        switched.Origin.Should().Be(CliChangeOrigin.App, "換檔的是 app 開的安裝視窗的子程序：不是外部更新");
        cli.Get(AgentProvider.Claude).LastChange!.Origin.Should().Be(CliChangeOrigin.App);
    }
}
