using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class UpdateApplierTests : IDisposable
{
    private const string From = "0.2.1-aaaaaa1";
    private const string To = "0.2.2-bbbbbb2";
    private static readonly string Token = new('c', 64);
    private readonly TempDirectory temp = new("applier");
    private readonly InstallLayout layout;
    private readonly string data;
    private readonly FakePlatform platform;

    public UpdateApplierTests()
    {
        layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));
        data = temp.CreateDirectory("data");
        foreach (var r in new[] { "0.2.0-0000000", From, To })
            temp.WriteFile(Path.Combine("Programs", "Myriagent", "versions", r, "Myriagent.exe"), r);
        Junction.Create(layout.Current, layout.VersionDirectory(From));
        UpdaterStateStore.Update(layout, s =>
        {
            s.Current = From;
            s.Previous = "0.2.0-0000000";
            s.Staged = To;
            return s;
        });
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"v\":1}");
        File.WriteAllText(Path.Combine(data, "accounts.json"), "[]");
        platform = new FakePlatform(layout);
    }

    public void Dispose() => temp.Dispose();

    /// <summary>假的平台：虛擬時鐘、記錄啟動；新版啟動後依設定寫健康訊號、提早結束或什麼都不做</summary>
    private sealed class FakePlatform : IApplierPlatform
    {
        private readonly InstallLayout layout;

        public FakePlatform(InstallLayout layout) => this.layout = layout;

        public DateTime UtcNow { get; private set; } = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

        public bool OldProcessExits { get; set; } = true;

        /// <summary>舊 app 結束後，這個資料夾已經有別的實例（使用者在換版的空窗中自己重新開 app）</summary>
        public bool OtherInstanceRunning { get; set; }

        /// <summary>IsInstanceRunning 被問過的資料夾</summary>
        public List<string> InstanceProbes { get; } = new();

        /// <summary>新版的行為：healthy＝寫健康訊號、crash＝提早結束、hang＝不回應、another-instance＝遇到別的實例、叫醒它就以專屬結束碼結束</summary>
        public string NewVersion { get; set; } = "healthy";

        /// <summary>新版啟動時改寫設定檔（模擬新版寫了舊版看不懂的資料）</summary>
        public Action? OnNewVersionStarted { get; set; }

        /// <summary>Target：啟動當下 current 的 junction 目標（current 被換成一般資料夾時為 null）</summary>
        public List<(string Exe, string? Target, IReadOnlyList<string> Args)> Launches { get; } = new();

        public List<int> Killed { get; } = new();

        public List<int> KilledTrees { get; } = new();

        private readonly HashSet<int> exited = new();
        private readonly Dictionary<int, int> exitCodes = new();

        public void Sleep(TimeSpan t) => UtcNow += t;

        public bool IsInstanceRunning(string dataRoot)
        {
            InstanceProbes.Add(dataRoot);
            return OtherInstanceRunning;
        }

        public int? ExitCode(int pid) => exited.Contains(pid) && exitCodes.TryGetValue(pid, out var code) ? code : null;

        public bool WaitForExit(int pid, TimeSpan timeout)
        {
            if (OldProcessExits)
                return true;
            UtcNow += timeout;
            return false;
        }

        public void Kill(int pid) => Killed.Add(pid);

        public bool WaitForWebViewRelease(string userDataFolder, TimeSpan timeout) => true;

        public int? Launch(string executable, IReadOnlyList<string> arguments)
        {
            int pid = 1000 + Launches.Count;
            Launches.Add((executable, Junction.GetTarget(layout.Current), arguments));
            if (arguments.Contains("--after-update"))
            {
                OnNewVersionStarted?.Invoke();
                if (NewVersion == "healthy")
                    LaunchGuard.MarkHealthy(layout, Path.GetFileName(Junction.GetTarget(layout.Current)!), arguments[^1]);
                else if (NewVersion is "crash" or "another-instance")
                {
                    exited.Add(pid);
                    exitCodes[pid] = NewVersion == "crash" ? 3 : UpdateApplier.AnotherInstanceExitCode;
                }
            }
            return pid;
        }

        public bool HasExited(int pid) => exited.Contains(pid);

        public void KillTree(int pid) => KilledTrees.Add(pid);
    }

    private StartupOptions Options(bool noRelaunch = false)
        => new() { ApplyUpdate = true, To = To, WaitPid = 42, Token = Token, NoRelaunch = noRelaunch };

    [Fact]
    public void 成功_切換junction_啟動新版_通過健康閘門後更新狀態並清理()
    {
        var outcome = new UpdateApplier(layout, data, platform).Run(Options());

        outcome.Should().Be(ApplyOutcome.Switched);
        File.Exists(layout.ArmedFile(Token)).Should().BeFalse("通過後清掉訊號檔");
        File.Exists(layout.HealthFile(Token)).Should().BeFalse();
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(To));
        var launch = platform.Launches.Should().ContainSingle().Subject;
        launch.Exe.Should().Be(layout.CurrentExecutable);
        launch.Args.Should().Equal("--after-update", Token);
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be(To);
        state.Previous.Should().Be(From);
        state.Staged.Should().BeNull();
        File.ReadAllText(Path.Combine(data, "backups", $"pre-{To}", "settings.json")).Should().Be("{\"v\":1}");
        Directory.Exists(layout.VersionDirectory("0.2.0-0000000")).Should().BeTrue("保留目前版與前兩版");
    }

    [Fact]
    public void 新版提早結束_切回舊版_還原設定_以失敗旗標啟動舊版_第一次不列壞版()
    {
        platform.NewVersion = "crash";
        platform.OnNewVersionStarted = () => File.WriteAllText(Path.Combine(data, "settings.json"), "{\"v\":2,\"新欄位\":true}");

        var outcome = new UpdateApplier(layout, data, platform).Run(Options());

        outcome.Should().Be(ApplyOutcome.RolledBack);
        platform.KilledTrees.Should().Equal(1000);
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(From));
        File.ReadAllText(Path.Combine(data, "settings.json")).Should().Be("{\"v\":1}");
        platform.Launches.Should().HaveCount(2);
        platform.Launches[1].Target.Should().BeEquivalentTo(layout.VersionDirectory(From));
        platform.Launches[1].Args.Should().Equal("--after-update-failed", Token);
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be(From);
        state.Bad.Should().BeEmpty("第一次失敗只回滾");
        state.GateFailures[To].Should().Be(1);
        state.LastResult.Should().Contain("退回");
        File.Exists(layout.ArmedFile(Token)).Should().BeFalse("回滾後也清掉訊號檔");
    }

    [Fact]
    public void 新版90秒內沒有健康訊號_同樣回滾()
    {
        platform.NewVersion = "hang";
        var start = platform.UtcNow;

        var outcome = new UpdateApplier(layout, data, platform).Run(Options());

        outcome.Should().Be(ApplyOutcome.RolledBack);
        (platform.UtcNow - start).Should().BeGreaterThanOrEqualTo(UpdateApplier.HealthGate);
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(From));
    }

    [Fact]
    public void 舊程序60秒沒結束_強制結束_不切換_以失敗旗標啟動舊版()
    {
        platform.OldProcessExits = false;

        var outcome = new UpdateApplier(layout, data, platform).Run(Options());

        outcome.Should().Be(ApplyOutcome.OldProcessHung);
        platform.Killed.Should().Equal(42);
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(From));
        platform.Launches.Should().ContainSingle().Which.Args.Should().Equal("--after-update-failed", Token);
        UpdaterStateStore.Load(layout).Current.Should().Be(From);
    }

    [Fact]
    public void 結束時換版_只切換不啟動_下次打開就是新版()
    {
        var outcome = new UpdateApplier(layout, data, platform).Run(Options(noRelaunch: true));

        outcome.Should().Be(ApplyOutcome.SwitchedNoRelaunch);
        platform.Launches.Should().BeEmpty();
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(To));
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be(To);
        state.Previous.Should().Be(From);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 換版的空窗中使用者自己開了app_不切換_保留備妥_不啟動任何版本(bool noRelaunch)
    {
        // 舊 app 結束後、切換前，使用者從捷徑重新開啟的實例（從 current＝舊版執行）已經拿到單一實例
        platform.OtherInstanceRunning = true;

        var outcome = new UpdateApplier(layout, data, platform).Run(Options(noRelaunch));

        outcome.Should().Be(ApplyOutcome.Aborted);
        platform.InstanceProbes.Should().Contain(data, "以這個資料夾的單一實例判斷");
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(From), "不能在執行中的實例底下改指向 current");
        platform.Launches.Should().BeEmpty("使用者的實例已經在跑（也讀過 resume.json）：新版只會叫醒它就結束，舊版也不必再開");
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be(From);
        state.Previous.Should().Be("0.2.0-0000000");
        state.Staged.Should().Be(To, "保留備妥，之後再套用");
        state.GateFailures.Should().BeEmpty("新版根本沒有啟動，不是閘門失敗");
        state.LastResult.Should().Contain("另一個 Clode Myriagent 正在執行");
        Directory.Exists(Path.Combine(data, "backups")).Should().BeFalse("切換前就放棄，不備份");
        File.Exists(layout.ArmedFile(Token)).Should().BeFalse("放棄這次也清掉訊號檔");
    }

    [Fact]
    public void 新版遇到使用者自己開的實例就結束_不算健康閘門失敗_不還原設定_不切回()
    {
        // 切換之後、新版拿到單一實例之前，使用者自己開的實例搶先拿到，而且已經在用（改了設定）；新版叫醒它就以專屬結束碼結束
        platform.NewVersion = "another-instance";
        platform.OnNewVersionStarted = () => File.WriteAllText(Path.Combine(data, "settings.json"), "{\"v\":2}");

        var outcome = new UpdateApplier(layout, data, platform).Run(Options());

        outcome.Should().Be(ApplyOutcome.Switched);
        platform.KilledTrees.Should().BeEmpty();
        platform.Launches.Should().ContainSingle("不再啟動舊版：使用者的實例已經在跑");
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(To), "使用者的實例多半就是從切換後的 current 啟動的");
        File.ReadAllText(Path.Combine(data, "settings.json")).Should().Be("{\"v\":2}", "使用者的實例正在用：不能把備份蓋回去");
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be(To);
        state.Previous.Should().Be(From);
        state.Staged.Should().BeNull();
        state.GateFailures.Should().NotContainKey(To, "叫醒別的實例就結束不是新版壞掉，不能累計到列壞版");
        state.Bad.Should().BeEmpty();
    }

    [Fact]
    public void 目標版本不存在_不動任何東西()
    {
        var outcome = new UpdateApplier(layout, data, platform).Run(Options() with { To = "0.2.9-9999999" });

        outcome.Should().Be(ApplyOutcome.Invalid);
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(From));
        platform.Launches.Should().ContainSingle().Which.Args.Should().Equal("--after-update-failed", Token);
    }

    [Fact]
    public void 備份只留最近3份()
    {
        foreach (var old in new[] { "pre-0.1.1-1111111", "pre-0.1.2-2222222", "pre-0.1.3-3333333" })
        {
            Directory.CreateDirectory(Path.Combine(data, "backups", old));
            Thread.Sleep(20);
        }

        new UpdateApplier(layout, data, platform).Run(Options());

        Directory.EnumerateDirectories(Path.Combine(data, "backups")).Select(Path.GetFileName)
            .Should().BeEquivalentTo(new[] { "pre-0.1.2-2222222", "pre-0.1.3-3333333", $"pre-{To}" });
    }

    [Fact]
    public void 換版前備份失敗_不切換_以失敗旗標啟動舊版()
    {
        ApplyOutcome outcome;
        using (new FileStream(Path.Combine(data, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => new UpdateApplier(layout, data, platform).Run(Options());
            outcome = act.Should().NotThrow().Subject;
        }

        outcome.Should().Be(ApplyOutcome.Aborted);
        platform.Launches.Should().ContainSingle().Which.Args.Should().Equal("--after-update-failed", Token);
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory(From));
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be(From);
        state.LastResult.Should().Contain("放棄");
        File.Exists(layout.ArmedFile(Token)).Should().BeFalse("放棄這次也清掉訊號檔");
    }

    [Fact]
    public void 回滾時切不回舊版_直接啟動舊版資料夾_閃退保護下次會退回()
    {
        platform.NewVersion = "crash";
        platform.OnNewVersionStarted = () =>
        {
            // current 換成一般資料夾：切回舊版時 Junction.Retarget 丟「不是 junction」
            Directory.Delete(layout.Current);
            Directory.CreateDirectory(layout.Current);
        };

        var outcome = new UpdateApplier(layout, data, platform).Run(Options());

        outcome.Should().Be(ApplyOutcome.RolledBack);
        platform.Launches.Should().HaveCount(2);
        platform.Launches[1].Exe.Should().Be(layout.Executable(From));
        platform.Launches[1].Args.Should().Equal("--after-update-failed", Token);
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be(To);
        state.Previous.Should().Be(From);
        state.GateFailures[To].Should().Be(1);
        state.LaunchFailures[To].Should().Be(UpdaterStateRules.LaunchFailureThreshold);
        state.LastResult.Should().NotContain("已更新到", "新版沒有通過健康閘門");
        UpdaterStateRules.OnLaunch(state, To).Action.Should().Be(LaunchAction.RollBack, "下次從捷徑啟動新版時，閃退保護切回舊版");
    }
}
