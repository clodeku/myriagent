using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using static Myriagent.Tests.Cli.CliManagerTestKit;

namespace Myriagent.Tests.Cli;

/// <summary>
/// WinGet 安裝的 Grok Build 的更新（CliManager 的 WingetBarrier；假 winget、暫存資料夾裡的假套件、不跑 watcher）：先問 WinGet 有沒有新版、
/// 沒有就不排空任何對話；有程序在用就不排空；排空後跑 winget upgrade、以換檔後的版本驗證；給所有使用者的安裝與找不到 winget 不跑；
/// 不跑 grok update（WinGet 安裝時它只印提示不換檔）
/// </summary>
public sealed class CliWingetUpdateTests
{
    private const string Old = "1.0.67";
    private const string New = "1.0.70";

    private const string ListWithUpgrade =
        "名稱       識別碼        版本   可用   來源\r\n---------------------------------------------\r\nGrok Build xAI.GrokBuild 1.0.67 1.0.70 winget\r\n1 個升級可供使用。\r\n";

    private sealed class GrokWingetSetup : IDisposable
    {
        public GrokWingetSetup(Func<CliManagerOptions, CliManagerOptions>? configure = null, bool machineScope = false, string latest = New)
        {
            Temp = new TempDirectory("cli-winget");
            PackageDirectory = Temp.CreateDirectory(@"WinGet\Packages\xAI.GrokBuild_Microsoft.Winget.Source_8wekyb3d8bbwe");
            Exe = Path.Combine(PackageDirectory, "grok.exe");
            File.WriteAllText(Exe, Old);
            var found = new CliInstallation(AgentProvider.Grok, new AgentLauncher(Exe, Array.Empty<string>()), CliInstallKind.Other, Exe) { NativeExecutable = Exe };
            Installation = CliWingetRules.WithWinget(found, _ => null, machineScope ? new[] { Temp.Root } : Array.Empty<string>())!;
            Installation.Winget.Should().NotBeNull();
            Commands = new FakeCliCommands(_ => $"grok {File.ReadAllText(Exe)} (2fe064390d07) [stable]");
            Commands.Winget = (pid, arguments) =>
            {
                Log!.Add("winget " + arguments[0]);
                return arguments[0] == "list" ? List(arguments) : Upgrade(pid, arguments);
            };
            Http = new HttpClient(new FakeRegistry());
            Cli = Create(Http, p => p == AgentProvider.Grok ? Installation : null, Commands, new FakeProcesses(), AgentProvider.Grok, background: false,
                o => (configure?.Invoke(o) ?? o) with
                {
                    FetchText = (url, _) => Task.FromResult(url == CliManager.GrokStableChannelUrl ? latest : string.Empty),
                });
            Log = new CliEventLog(Cli, AgentProvider.Grok);
            Participant = new RecordingParticipant(Log);
            Cli.UpdateParticipant = Participant;
        }

        public TempDirectory Temp { get; }

        public string PackageDirectory { get; }

        public string Exe { get; }

        public CliInstallation Installation { get; }

        public FakeCliCommands Commands { get; }

        public HttpClient Http { get; }

        public CliManager Cli { get; }

        public CliEventLog Log { get; }

        public RecordingParticipant Participant { get; }

        /// <summary>winget list --upgrade-available 的結果（預設：WinGet 上有 1.0.70）</summary>
        public Func<IReadOnlyList<string>, CliRunResult> List { get; set; } = _ => new CliRunResult(0, ListWithUpgrade, string.Empty, false);

        /// <summary>winget upgrade 的結果（預設：換成 1.0.70、結束碼 0）</summary>
        public Func<int, IReadOnlyList<string>, CliRunResult> Upgrade { get; set; } = null!;

        /// <summary>跑 winget upgrade 時的更新階段</summary>
        public CliUpdatePhase? UpgradePhase { get; set; }

        /// <summary>照 portable 套件的換檔：刪掉舊檔、放新檔（FileId 會變）</summary>
        public void Replace(string version)
        {
            File.Delete(Exe);
            File.WriteAllText(Exe, version);
        }

        public int Count(string command) => Log.Entries.Count(e => e == "winget " + command);

        public void Dispose()
        {
            Cli.Dispose();
            Http.Dispose();
            Temp.Dispose();
        }
    }

    private static GrokWingetSetup Setup(Func<CliManagerOptions, CliManagerOptions>? configure = null, bool machineScope = false)
    {
        var s = new GrokWingetSetup(configure, machineScope);
        s.Upgrade = (_, _) =>
        {
            s.UpgradePhase = s.Cli.Get(AgentProvider.Grok).UpdatePhase;
            s.Replace(New);
            return new CliRunResult(0, "找到 Grok Build [xAI.GrokBuild] 版本 1.0.70\r\n已成功安裝\r\n", string.Empty, false);
        };
        return s;
    }

    [Fact]
    public async Task 手動更新_先問WinGet有新版_排空後跑winget_upgrade_換檔後先發布新身分再UpdateFinished_不跑grok_update()
    {
        using var s = Setup();
        await s.Cli.CheckAsync(AgentProvider.Grok);
        var before = s.Cli.GetIdentity(AgentProvider.Grok)!;
        before.Version.Should().Be(Old);
        s.Cli.Get(AgentProvider.Grok).UpdateAvailable.Should().BeTrue();

        await s.Cli.UpdateAsync(AgentProvider.Grok);

        s.Participant.Prepares.All.Should().Equal((AgentProvider.Grok, CliUpdateUrgency.Normal));
        var entries = s.Log.Entries;
        entries.IndexOf("winget list").Should().BeLessThan(entries.IndexOf("Prepare Grok Normal"), "先確定 WinGet 有新版才打擾對話");
        entries.IndexOf("winget upgrade").Should().BeGreaterThan(entries.IndexOf("Prepare Grok Normal"), "執行中的 grok.exe 會讓 WinGet 刪不掉舊檔：排空之後才換");
        s.UpgradePhase.Should().Be(CliUpdatePhase.Installing);
        s.Log.Phases.SkipWhile(p => p == CliUpdatePhase.None).Should().Equal(
            CliUpdatePhase.Draining, CliUpdatePhase.WaitingForProcesses, CliUpdatePhase.Installing, CliUpdatePhase.Verifying, CliUpdatePhase.None);
        s.Commands.Calls.All.Should().Contain(string.Join(" ", CliWingetRules.ListUpgradeArguments("xAI.GrokBuild")))
            .And.Contain(string.Join(" ", CliWingetRules.UpgradeArguments("xAI.GrokBuild")))
            .And.NotContain("update", "WinGet 安裝時 grok update 只印提示不換檔");

        var finished = s.Participant.Finished.All.Should().ContainSingle().Subject;
        finished.Outcome.Should().Be(CliUpdateOutcome.Updated);
        finished.Identity!.Version.Should().Be(New);
        var switchIndex = s.Log.Entries.FindIndex(e => e.StartsWith("VersionChanged") && e.Contains("switch=True"));
        s.Log.Entries.IndexOf("Finished Updated 1.0.70").Should().BeGreaterThan(switchIndex);
        var switched = s.Log.Changes.Single(c => c.Change.IsVersionSwitch).Change;
        switched.Old.Should().Be(before);
        switched.Origin.Should().Be(CliChangeOrigin.App);
        var state = s.Cli.Get(AgentProvider.Grok);
        state.Busy.Should().BeFalse();
        state.ActionError.Should().BeNull();
        state.UpdateAvailable.Should().BeFalse();
        state.Installation!.Winget.Should().NotBeNull();
    }

    [Fact]
    public async Task WinGet上還沒有新版_不排空不換檔_手動說明原因_自動安靜略過而且之後照樣再問()
    {
        using var s = Setup();
        s.List = _ => new CliRunResult(0, "未找到符合輸入條件的已安裝套件。\r\n", string.Empty, false);
        await s.Cli.CheckAsync(AgentProvider.Grok);

        await s.Cli.UpdateAsync(AgentProvider.Grok);

        s.Participant.Prepares.Count.Should().Be(0, "WinGet 沒有新版：跑 winget upgrade 也只會回「找不到可用的升級」，不必停任何對話");
        s.Participant.Finished.Count.Should().Be(0);
        s.Count("upgrade").Should().Be(0);
        var state = s.Cli.Get(AgentProvider.Grok);
        state.ActionError.Should().Be(CliWingetRules.DescribeNotYetOnWinget(AgentProvider.Grok, Old, New));
        state.Busy.Should().BeFalse();
        state.Progress.Should().BeNull();

        await s.Cli.UpdateAsync(AgentProvider.Grok, automatic: true);
        s.Cli.Get(AgentProvider.Grok).ActionError.Should().BeNull("自動更新安靜地略過");
        await s.Cli.UpdateAsync(AgentProvider.Grok, automatic: true);
        s.Count("list").Should().Be(3, "不記成「沒有效果」：WinGet 過幾天才上架，之後的自動更新照樣再問");
        s.Participant.Prepares.Count.Should().Be(0);
    }

    [Fact]
    public async Task 有app以外的grok在用_手動不排空並列出PID_自動連WinGet都不問就延後()
    {
        const int foreignPid = 90068;
        string? exe = null;
        using var s = Setup(o => o with
        {
            AppProcessIds = () => Array.Empty<int>(),
            AllProcessIds = () => new[] { foreignPid },
            ProcessImagePath = pid => pid == foreignPid ? exe : null,
        });
        exe = s.Exe;   // 系統管理員終端機開的 grok（背景的 leader 也是）
        await s.Cli.CheckAsync(AgentProvider.Grok);

        await s.Cli.UpdateAsync(AgentProvider.Grok);

        s.Participant.Prepares.Count.Should().Be(0, "WinGet 換不了執行中的檔案：不必停任何對話");
        s.Count("list").Should().Be(1, "手動的先問 WinGet：沒有新版就不必請使用者關程式");
        s.Count("upgrade").Should().Be(0);
        s.Cli.Get(AgentProvider.Grok).ActionError.Should().Contain("PID 90068").And.Contain("grok leader kill");

        await s.Cli.UpdateAsync(AgentProvider.Grok, automatic: true);
        s.Count("list").Should().Be(1, "自動的先做便宜的檢查：背景的 leader 可能一掛好幾天");
        s.Participant.Prepares.Count.Should().Be(0);
        s.Cli.Get(AgentProvider.Grok).ActionError.Should().BeNull("自動更新安靜地延後");
    }

    [Fact]
    public async Task 排空期間才開的終端機grok_換檔前再確認一次_不跑upgrade_停下的對話以目前的版本接續()
    {
        const int foreignPid = 4243;
        var foreign = false;
        string? exe = null;
        using var s = Setup(o => o with
        {
            AppProcessIds = () => Array.Empty<int>(),
            AllProcessIds = () => foreign ? new[] { foreignPid } : Array.Empty<int>(),
            ProcessImagePath = pid => pid == foreignPid ? exe : null,
        });
        exe = s.Exe;
        await s.Cli.CheckAsync(AgentProvider.Grok);
        var current = s.Cli.GetIdentity(AgentProvider.Grok);
        s.Participant.OnPrepare = (_, _, _) =>
        {
            foreign = true;   // 等對話停下的這段期間，使用者在終端機開了 grok
            return Task.FromResult(new CliUpdatePreparation(Ready: true, ClosedIdle: 1, Held: 0, Busy: 0));
        };

        await s.Cli.UpdateAsync(AgentProvider.Grok);

        s.Count("upgrade").Should().Be(0);
        var finished = s.Participant.Finished.All.Should().ContainSingle().Subject;
        finished.Outcome.Should().Be(CliUpdateOutcome.Cancelled);
        finished.Identity.Should().Be(current, "沒有換檔：停下的對話以目前的版本接續");
        finished.Error.Should().Contain("PID 4243");
    }

    [Fact]
    public async Task winget換檔失敗_說明原因_UpdateFinished以仍可用的舊版結束()
    {
        using var s = Setup();
        s.Upgrade = (_, _) => new CliRunResult(unchecked((int)0x8A150052),
            "正在啟動套件安裝...\r\nremove: Access is denied.: \"" + s.Exe + "\"\r\n安裝程式失敗，結束代碼為: 0x8a150003 : 執行命令失敗\r\n", string.Empty, false);
        await s.Cli.CheckAsync(AgentProvider.Grok);
        var current = s.Cli.GetIdentity(AgentProvider.Grok);

        await s.Cli.UpdateAsync(AgentProvider.Grok);

        var finished = s.Participant.Finished.All.Should().ContainSingle().Subject;
        finished.Outcome.Should().Be(CliUpdateOutcome.Failed);
        finished.Identity.Should().Be(current, "舊檔完好：停下的對話以它接續");
        s.Cli.Get(AgentProvider.Grok).ActionError.Should().StartWith("WinGet 更新失敗（結束碼 0x8A150052）：remove: Access is denied.")
            .And.EndWith("（執行檔可能還在使用中：請關閉所有的 Grok Build 再更新）");
        s.Cli.Get(AgentProvider.Grok).Version.Should().Be(Old);
    }

    [Fact]
    public async Task winget_upgrade回找不到可用的升級_算沒有效果並說明WinGet還沒有新版()
    {
        using var s = Setup();
        s.Upgrade = (_, _) => new CliRunResult(CliWingetRules.UpdateNotApplicable, "找不到可用的升級。\r\n", string.Empty, false);
        await s.Cli.CheckAsync(AgentProvider.Grok);

        await s.Cli.UpdateAsync(AgentProvider.Grok);

        s.Participant.Finished.All.Single().Outcome.Should().Be(CliUpdateOutcome.Unchanged);
        s.Cli.Get(AgentProvider.Grok).ActionError.Should().Be(
            $"更新指令已執行，但版本仍是 {Old}：{CliWingetRules.DescribeNotYetOnWinget(AgentProvider.Grok, Old, New)}");
    }

    [Fact]
    public async Task 裝給所有使用者的WinGet套件_不提權不跑winget_手動說明怎麼更新_自動安靜略過()
    {
        using var s = Setup(machineScope: true);
        await s.Cli.CheckAsync(AgentProvider.Grok);

        await s.Cli.UpdateAsync(AgentProvider.Grok);

        s.Commands.Calls.All.Should().OnlyContain(c => c == "--version");
        s.Participant.Prepares.Count.Should().Be(0);
        s.Cli.Get(AgentProvider.Grok).ActionError.Should().Be(CliWingetRules.DescribeMachineScope(AgentProvider.Grok, s.Installation.Winget!));

        await s.Cli.UpdateAsync(AgentProvider.Grok, automatic: true);
        s.Cli.Get(AgentProvider.Grok).ActionError.Should().BeNull();
        s.Commands.Calls.All.Should().OnlyContain(c => c == "--version");
    }

    [Fact]
    public async Task 找不到winget_不排空_說明原因()
    {
        using var s = Setup(o => o with { LocateWinget = () => null });
        await s.Cli.CheckAsync(AgentProvider.Grok);

        await s.Cli.UpdateAsync(AgentProvider.Grok);

        s.Participant.Prepares.Count.Should().Be(0);
        s.Cli.Get(AgentProvider.Grok).ActionError.Should().Be(CliWingetRules.DescribeMissingWinget(AgentProvider.Grok));
        s.Commands.Calls.All.Should().NotContain(c => c.Contains("update", StringComparison.Ordinal), "沒有 winget 也不退回 grok update（它只會印提示）");
    }
}
