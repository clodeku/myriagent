using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using static Myriagent.Tests.Cli.CliManagerTestKit;
using static Myriagent.Tests.Cli.CliWatcherTestKit;

namespace Myriagent.Tests.Cli;

/// <summary>
/// CliManager 接上 CliWatcher（WP1.5）：在暫存資料夾的安裝上跑真的 watcher（假的程序查詢、縮短的去抖）與假的 --version，
/// 確認啟動身分的發布時機（中樞接上屏障之後、Old: null）、發布順序（狀態 → Changed → VersionChanged）、Identity 只經發布改變、
/// 變動之後一定再發穩定或損壞、混版不算換版、處理常式不阻塞 watcher、GetLauncher 的檔案檢查、程序回報的版本對不上時重新定位
/// </summary>
public sealed class CliManagerTests
{
    private static HttpClient Http() => new(new FakeRegistry());

    [Fact]
    public async Task 啟動_中樞接上屏障才開始監看_第一次讀到的身分以OldNull發布_發布前狀態已更新()
    {
        using var temp = new TempDirectory("cli-manager");
        var claude = new ClaudeLayout(temp);
        var commands = FakeCliCommands.For(claude);
        using var http = Http();
        using var cli = Create(http, p => p == AgentProvider.Claude ? claude.Installation : null, commands, new FakeProcesses(), AgentProvider.Claude, background: true);
        var log = new CliEventLog(cli, AgentProvider.Claude);

        await Task.Delay(400);
        log.Changes.Should().BeEmpty("中樞接上屏障（訂閱事件）之前不開始監看：啟動時的身分一定要送得到中樞");
        cli.GetIdentity(AgentProvider.Claude).Should().BeNull();
        cli.Get(AgentProvider.Claude).Checking.Should().BeTrue("找到執行檔但還沒判定");

        cli.UpdateParticipant = new RecordingParticipant(log);

        var first = await log.WaitForChangeAsync(c => c.New != null, "啟動時第一次讀到的身分");
        first.Old.Should().BeNull("啟動時第一次讀到的身分以 Old: null 發布");
        first.IsVersionSwitch.Should().BeFalse();
        first.New!.Version.Should().Be(ClaudeLayout.OldVersion);
        first.New.Should().Be(CliBinaryIdentityRules.TryRead(claude.Installation, ClaudeLayout.OldVersion));
        var (_, atEvent) = log.Changes.First(c => ReferenceEquals(c.Change, first));
        atEvent.Identity.Should().Be(first.New, "VersionChanged 觸發前狀態已更新");
        log.Entries.IndexOf("Changed").Should().BeLessThan(log.Entries.FindIndex(e => e.StartsWith("VersionChanged")), "先 Changed 再 VersionChanged");

        await WaitAsync(() => commands.Count("--version") >= 1, "完整判定跑 --version");
        await Task.Delay(300);
        log.Changes.Should().ContainSingle("--version 確認的是同一份檔案：不再發布");
        var state = cli.Get(AgentProvider.Claude);
        state.Checking.Should().BeFalse();
        state.Installed.Should().BeTrue();
        state.Version.Should().Be(ClaudeLayout.OldVersion);
    }

    [Fact]
    public async Task 沒有背景檢查_不監看_不讀這台電腦的CLI()
    {
        using var temp = new TempDirectory("cli-manager");
        var claude = new ClaudeLayout(temp);
        var commands = FakeCliCommands.For(claude);
        using var http = Http();
        using var cli = Create(http, _ => claude.Installation, commands, new FakeProcesses(), AgentProvider.Claude, background: false);
        var log = new CliEventLog(cli, AgentProvider.Claude);

        cli.UpdateParticipant = new RecordingParticipant(log);
        claude.Swap(updaterPid: 1234, ClaudeLayout.NewSource);
        await Task.Delay(600);

        log.Changes.Should().BeEmpty("HubHarness 這類測試以 startBackgroundChecks:false 建立：不能啟動 watcher");
        commands.Calls.Count.Should().Be(0);
        cli.GetIdentity(AgentProvider.Claude).Should().BeNull();
    }

    [Fact]
    public async Task 換檔_先發變動中_安靜後發布已穩定的新身分_身分只經發布改變_來源依更新者PID()
    {
        using var temp = new TempDirectory("cli-manager");
        var claude = new ClaudeLayout(temp);
        var commands = FakeCliCommands.For(claude);
        using var http = Http();
        using var cli = Create(http, _ => claude.Installation, commands, new FakeProcesses(), AgentProvider.Claude, background: true,
            o => o with { IsAppProcess = pid => pid == 48780 });
        var log = new CliEventLog(cli, AgentProvider.Claude);
        cli.UpdateParticipant = new RecordingParticipant(log);
        var startup = await log.WaitForChangeAsync(c => c.New != null, "啟動身分");
        await WaitAsync(() => commands.Count("--version") >= 1, "啟動的完整判定");
        await Task.Delay(200);
        var mark = log.Changes.Count;

        claude.Swap(updaterPid: 48780, ClaudeLayout.NewSource);

        var switched = await log.WaitForChangeAsync(c => c.IsVersionSwitch, "換檔後宣告新身分", skip: mark);
        var burst = log.Changes.Skip(mark).ToList();
        burst[0].Change.Stable.Should().BeFalse("一波變動的第一個訊號：先暫緩新啟動");
        burst[0].State.InstallChanging.Should().BeTrue();
        burst[0].Change.New.Should().BeNull();
        switched.Old.Should().Be(startup.New);
        switched.New!.Version.Should().Be(ClaudeLayout.NewVersion);
        switched.Origin.Should().Be(CliChangeOrigin.App, "更新者 PID（.new.<pid>／.old.<pid>）在 app 的工作物件裡");
        var (_, after) = burst.Single(c => ReferenceEquals(c.Change, switched));
        after.InstallChanging.Should().BeFalse("變動之後一定再發穩定的版本，否則送出會一直排隊");
        after.Identity.Should().Be(switched.New);
        after.LastChange.Should().BeSameAs(switched);

        // 身分每次改變都對應一個 VersionChanged（只經 PublishVersionChange 改）
        var published = log.Changes.Where(c => c.Change.New != null).Select(c => c.Change.New).ToList();
        log.IdentitiesAtChanged.Where(i => i != null).Should().Equal(published);
    }

    [Fact]
    public async Task 外部更新者的PID_來源是外部()
    {
        using var temp = new TempDirectory("cli-manager");
        var claude = new ClaudeLayout(temp);
        using var http = Http();
        using var cli = Create(http, _ => claude.Installation, FakeCliCommands.For(claude), new FakeProcesses(), AgentProvider.Claude, background: true,
            o => o with { IsAppProcess = _ => false });
        var log = new CliEventLog(cli, AgentProvider.Claude);
        cli.UpdateParticipant = new RecordingParticipant(log);
        await log.WaitForChangeAsync(c => c.New != null, "啟動身分");
        var mark = log.Changes.Count;

        claude.Swap(updaterPid: 48780, ClaudeLayout.NewSource);

        var switched = await log.WaitForChangeAsync(c => c.IsVersionSwitch, "換檔", skip: mark);
        switched.Origin.Should().Be(CliChangeOrigin.External, "互動式 claude 的背景更新（不是 app 的程序）");
        cli.Get(AgentProvider.Claude).LastChange!.Origin.Should().Be(CliChangeOrigin.External);
    }

    [Fact]
    public async Task 混版_發布安裝損壞_不是換版_身分保留最後可用的()
    {
        using var temp = new TempDirectory("cli-manager");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        using var http = Http();
        using var cli = Create(http, _ => codex.Installation, FakeCliCommands.For(codex), new FakeProcesses(), AgentProvider.Codex, background: true);
        var log = new CliEventLog(cli, AgentProvider.Codex);
        cli.UpdateParticipant = new RecordingParticipant(log);
        var startup = await log.WaitForChangeAsync(c => c.New != null, "啟動身分");
        var mark = log.Changes.Count;

        // npm rollback 留下的混版：package.json 0.162.0、執行檔還是 0.160.1
        codex.Retire();
        codex.WritePackage("0.162.0", "0.160.1", extraFiles: 3);
        codex.RemoveTrash();
        codex.RewriteShims();

        var broken = await log.WaitForChangeAsync(c => c.InstallBroken, "安裝不一致", skip: mark, seconds: 15);
        broken.IsVersionSwitch.Should().BeFalse("S2：混版 0 次重開");
        broken.Detail.Should().Contain("安裝不一致").And.Contain("0.162.0").And.Contain("0.160.1");
        var state = cli.Get(AgentProvider.Codex);
        state.InstallBroken.Should().BeTrue();
        state.InstallChanging.Should().BeFalse("損壞也是穩定的結論：變動狀態清掉");
        state.Identity.Should().Be(startup.New, "損壞時身分保留最後可用的");
        log.Changes.Skip(mark).Should().NotContain(c => c.Change.IsVersionSwitch);
    }

    [Fact]
    public async Task 處理常式不阻塞watcher_同一種CLI依序處理_卡住的判定之後照順序發布()
    {
        using var temp = new TempDirectory("cli-manager");
        var claude = new ClaudeLayout(temp);
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var claudeCommands = FakeCliCommands.For(claude);
        var codexCommands = FakeCliCommands.For(codex);
        var commands = new FakeCliCommands(launcher => launcher.FileName.EndsWith("claude.exe", StringComparison.OrdinalIgnoreCase)
            ? claudeCommands.Version(launcher)
            : codexCommands.Version(launcher))
        {
            VersionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var http = Http();
        using var cli = Create(http, p => p == AgentProvider.Claude ? claude.Installation : codex.Installation, commands, new FakeProcesses(), AgentProvider.Claude,
            background: true, o => o with { Watcher = o.Watcher! with { Providers = new[] { AgentProvider.Claude, AgentProvider.Codex } } });
        var claudeLog = new CliEventLog(cli, AgentProvider.Claude);
        var codexLog = new CliEventLog(cli, AgentProvider.Codex);
        cli.UpdateParticipant = new RecordingParticipant(claudeLog);

        // 兩種 CLI 的 --version 都卡住：啟動身分仍先以不跑程序的判定發布（中樞在那之前不能啟動任何對話程序）
        var claudeStartup = await claudeLog.WaitForChangeAsync(c => c.New != null, "Claude 的啟動身分");
        await codexLog.WaitForChangeAsync(c => c.New != null, "Codex 的啟動身分（不被 Claude 卡住的判定擋住）");
        await WaitAsync(() => commands.Count("--version") >= 2, "兩種 CLI 都在跑 --version（卡住）");

        // 判定卡住期間換檔：watcher 照常監看（背景執行緒沒被卡住），訊號排在同一種 CLI 的佇列後面
        claude.Swap(updaterPid: 777, ClaudeLayout.NewSource);
        await Task.Delay(Quiet * 4);
        claudeLog.Changes.Should().HaveCount(1, "前一個判定還沒做完：同一種 CLI 依序處理");

        commands.VersionGate.SetResult();
        var switched = await claudeLog.WaitForChangeAsync(c => c.IsVersionSwitch, "放行後依序：變動中 → 新身分");
        switched.Old.Should().Be(claudeStartup.New);
        switched.New!.Version.Should().Be(ClaudeLayout.NewVersion);
        claudeLog.Changes.Select(c => c.Change.Stable).Should().Equal(new[] { true, false, true }, "啟動身分、變動中、新身分；卡住的那次判定看到檔案已換掉就不發布");
        cli.Get(AgentProvider.Claude).InstallChanging.Should().BeFalse();
    }

    [Fact]
    public async Task S2_實際的去抖常數_原生換檔後10秒內發布已穩定的新身分()
    {
        using var temp = new TempDirectory("cli-manager");
        var claude = new ClaudeLayout(temp);
        using var http = Http();
        using var cli = Create(http, _ => claude.Installation, FakeCliCommands.For(claude), new FakeProcesses(), AgentProvider.Claude, background: true,
            o => o with
            {
                Watcher = new CliWatcherOptions
                {
                    PollInterval = Timeout.InfiniteTimeSpan,
                    Environment = NoEnvironment,
                    Inspector = new FakeProcesses().Inspector,
                    Providers = new[] { AgentProvider.Claude },
                },
            });
        var log = new CliEventLog(cli, AgentProvider.Claude);
        cli.UpdateParticipant = new RecordingParticipant(log);
        await log.WaitForChangeAsync(c => c.New != null, "啟動身分");
        var mark = log.Changes.Count;

        claude.Swap(updaterPid: 48780, ClaudeLayout.NewSource);
        var elapsed = Stopwatch.StartNew();
        var switched = await log.WaitForChangeAsync(c => c.IsVersionSwitch, "換檔後發布新身分", skip: mark, seconds: 15);
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10), "S2：換版完成後 10 秒內判定已穩定的新身分（含 --version）");
        switched.New!.Version.Should().Be(ClaudeLayout.NewVersion);
    }

    [Fact]
    public void GetLauncher_檢查原生執行檔與CodexJs還在_不在才重新找()
    {
        using var temp = new TempDirectory("cli-manager");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        // npm 版的啟動器是 node.exe（這台電腦不一定有）：換成一定存在的檔案，只看 codex.js 與原生執行檔
        var installation = codex.Installation with { Launcher = codex.Installation.Launcher with { FileName = typeof(CliManager).Assembly.Location } };
        int locates = 0;
        CliInstallation? current = installation;
        using var http = Http();
        using var cli = Create(http, p => p == AgentProvider.Codex ? (Interlocked.Increment(ref locates) > 0 ? current : null) : null,
            FakeCliCommands.For(codex), new FakeProcesses(), AgentProvider.Codex, background: false);
        var initial = locates;

        cli.GetLauncher(AgentProvider.Codex).Should().BeSameAs(installation.Launcher);
        cli.GetLauncher(AgentProvider.Codex).Should().BeSameAs(installation.Launcher);
        locates.Should().Be(initial, "檔案都在：不重新掃 PATH（UI 執行緒每次送出都會呼叫）");

        File.Delete(installation.Launcher.PrefixArguments[0]);   // codex.js 不見了
        current = null;
        cli.GetLauncher(AgentProvider.Codex).Should().BeNull("codex.js 不在：重新找，找不到就是沒有安裝");
        locates.Should().Be(initial + 1);
        cli.Get(AgentProvider.Codex).Installation.Should().BeNull();

        LauncherCheck(installation, File.Exists).Should().BeFalse();
        LauncherCheck(installation, path => !path.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase)).Should().BeFalse("vendor 的原生執行檔也要在");
        LauncherCheck(installation, _ => true).Should().BeTrue();
    }

    private static bool LauncherCheck(CliInstallation installation, Func<string, bool> exists) => CliManager.LauncherFilesExist(installation, exists);

    [Fact]
    public async Task 程序回報的版本與啟動身分不同_重新定位後再判定一次_換到新的安裝就發布_同一個身分只做一次()
    {
        using var temp = new TempDirectory("cli-manager");
        var npmClaude = new ClaudeLayout(temp);
        var other = new TempDirectory("cli-manager");
        try
        {
            var nativeClaude = new ClaudeLayout(other);
            nativeClaude.Swap(updaterPid: 1, ClaudeLayout.NewSource);
            CliInstallation current = npmClaude.Installation;
            var commands = new FakeCliCommands(launcher => CliBinaryIdentityRules.ReadClaudeFileVersion(launcher.FileName) is { } v ? $"{v} (Claude Code)" : null);
            using var http = Http();
            using var cli = Create(http, _ => current, commands, new FakeProcesses(), AgentProvider.Claude, background: false);
            var log = new CliEventLog(cli, AgentProvider.Claude);
            await cli.CheckAsync(AgentProvider.Claude);
            var started = cli.GetIdentity(AgentProvider.Claude)!;
            started.Version.Should().Be(ClaudeLayout.OldVersion);

            cli.ReportRunningVersion(AgentProvider.Claude, started, ClaudeLayout.OldVersion);
            cli.ReportRunningVersion(AgentProvider.Claude, null, "1.0.0");
            await Task.Delay(300);
            log.Changes.Should().HaveCount(1, "版本一樣、或啟動身分不明：不用重新定位");

            // 啟動器還指向舊的安裝：程序回報的版本對不上 → 重新定位找到新的安裝 → 發布（中樞照一般換版把這個對話重開）
            current = nativeClaude.Installation;
            cli.ReportRunningVersion(AgentProvider.Claude, started, ClaudeLayout.NewVersion);
            var switched = await log.WaitForChangeAsync(c => c.IsVersionSwitch, "重新定位後發布新的安裝");
            switched.New!.NativeExecutable.Should().Be(nativeClaude.Exe);
            cli.GetLauncher(AgentProvider.Claude).Should().BeSameAs(nativeClaude.Installation.Launcher);

            var versionChecks = commands.Count("--version");
            cli.ReportRunningVersion(AgentProvider.Claude, started, ClaudeLayout.NewVersion);
            await Task.Delay(300);
            commands.Count("--version").Should().Be(versionChecks, "同一個身分與版本只重新定位一次");
        }
        finally
        {
            other.Dispose();
        }
    }
}
