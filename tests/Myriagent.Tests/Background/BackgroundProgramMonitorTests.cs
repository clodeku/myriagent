using System.Diagnostics;
using Myriagent.Background;

namespace Myriagent.Tests.Background;

/// <summary>
/// 背景程式的掃描（BackgroundProgramMonitor）：找到的程式開始追蹤、結束了（握著 handle 而還在快照裡的也算）放進下一次的 Exited 並帶結束碼，
/// 換了根的不算結束；使用者按停止的帶 StoppedByUser。最後一個測試以真的程序（cmd 的 start /b 開的 ping）跑一遍
/// </summary>
public sealed class BackgroundProgramMonitorTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private static ProcessRow Row(int pid, int parent, long created, string name = "ping.exe", string? cmd = null)
        => new(pid, parent, created, name, Conv, true, cmd ?? name);

    private sealed class FakeSnapshots : IProcessSnapshotSource
    {
        public List<ProcessRow> Rows { get; set; } = [];

        public IReadOnlyList<ProcessRow> Snapshot() => Rows.ToList();
    }

    private sealed class FakeControl : IProgramProcessControl
    {
        public HashSet<(int, long)> Watched { get; } = [];

        public HashSet<(int, long)> Dead { get; } = [];

        public HashSet<(int, long)> Unwatchable { get; } = [];

        public Dictionary<(int, long), int> ExitCodes { get; } = [];

        public List<(int, long)> Killed { get; } = [];

        public bool KillResult { get; set; } = true;

        public bool Watch(int pid, long createdAt)
        {
            if (Unwatchable.Contains((pid, createdAt)))
                return false;
            Watched.Add((pid, createdAt));
            return true;
        }

        public bool IsAlive(int pid, long createdAt) => !Dead.Contains((pid, createdAt));

        public (int? ExitCode, long? ExitedAt) Release(int pid, long createdAt)
        {
            Watched.Remove((pid, createdAt));
            return ExitCodes.TryGetValue((pid, createdAt), out var code) ? (code, createdAt + 50) : (null, null);
        }

        public bool KillTree(int pid, long createdAt)
        {
            Killed.Add((pid, createdAt));
            return KillResult;
        }

        public void Dispose()
        {
        }
    }

    private static (BackgroundProgramMonitor Monitor, List<BackgroundScan> Scans) Create(FakeSnapshots snapshots, FakeControl control)
    {
        var scans = new List<BackgroundScan>();
        var monitor = new BackgroundProgramMonitor(snapshots, () => new HashSet<int>(), control);
        // 不開計時器：測試自己呼叫 Tick
        monitor.StartManual(scans.Add);
        return (monitor, scans);
    }

    [Fact]
    public void 找到的開始追蹤_消失時帶結束碼_第一次掃描沒有結束的()
    {
        var snapshots = new FakeSnapshots { Rows = [Row(10, 999, 100), Row(20, 998, 200)] };
        var control = new FakeControl();
        var (monitor, scans) = Create(snapshots, control);
        using (monitor)
        {
            monitor.Tick();
            scans[^1].Programs[Conv].Select(p => p.Pid).Should().Equal(10, 20);
            scans[^1].Exits.Should().BeEmpty("app 啟動後第一次掃描沒有上一次可比");
            control.Watched.Should().BeEquivalentTo(new[] { (10, 100L), (20, 200L) });

            control.ExitCodes[(10, 100)] = 3;
            snapshots.Rows = [Row(20, 998, 200)];
            monitor.Tick();

            var exit = scans[^1].Exits.Should().ContainSingle().Subject;
            exit.ConversationId.Should().Be(Conv);
            exit.Program.Pid.Should().Be(10);
            exit.ExitCode.Should().Be(3);
            exit.ExitedAt.Should().Be(150);
            exit.StoppedByUser.Should().BeFalse();
            control.Watched.Should().BeEquivalentTo(new[] { (20, 200L) }, "結束的放開追蹤");
        }
    }

    [Fact]
    public void 結束了但還在快照裡的_以追蹤的handle拿掉並算結束()
    {
        // 握著 handle 的程序結束後仍在系統的程序清單裡：不能一直列成還在跑
        var snapshots = new FakeSnapshots { Rows = [Row(10, 999, 100)] };
        var control = new FakeControl();
        var (monitor, scans) = Create(snapshots, control);
        using (monitor)
        {
            monitor.Tick();
            control.Dead.Add((10, 100));
            control.ExitCodes[(10, 100)] = 0;
            monitor.Tick();

            scans[^1].Programs.Should().NotContainKey(Conv);
            scans[^1].Exits.Should().ContainSingle().Which.ExitCode.Should().Be(0);
        }
    }

    [Fact]
    public void 子程序接著當根_不算結束_追蹤換成新的根()
    {
        var snapshots = new FakeSnapshots { Rows = [Row(10, 999, 100, "cmd.exe"), Row(11, 10, 150, "node.exe")] };
        var control = new FakeControl();
        var (monitor, scans) = Create(snapshots, control);
        using (monitor)
        {
            monitor.Tick();
            scans[^1].Programs[Conv].Select(p => p.Pid).Should().Equal(10);

            snapshots.Rows = [Row(11, 10, 150, "node.exe")];
            monitor.Tick();

            scans[^1].Programs[Conv].Select(p => p.Pid).Should().Equal(11);
            scans[^1].Exits.Should().BeEmpty("cmd 結束了、它開的 node 還在跑：同一個程式");
            control.Watched.Should().BeEquivalentTo(new[] { (11, 150L) });
        }
    }

    [Fact]
    public void 一開始就追蹤不到的_已經結束_不列()
    {
        var snapshots = new FakeSnapshots { Rows = [Row(10, 999, 100), Row(20, 998, 200)] };
        var control = new FakeControl();
        control.Unwatchable.Add((10, 100));
        var (monitor, scans) = Create(snapshots, control);
        using (monitor)
        {
            monitor.Tick();
            scans[^1].Programs[Conv].Select(p => p.Pid).Should().Equal(20);
        }
    }

    [Fact]
    public async Task 使用者停止的_下一次掃描帶StoppedByUser_停不了的不帶()
    {
        var snapshots = new FakeSnapshots { Rows = [Row(10, 999, 100), Row(20, 998, 200)] };
        var control = new FakeControl();
        var (monitor, scans) = Create(snapshots, control);
        using (monitor)
        {
            monitor.Tick();
            var programs = scans[^1].Programs[Conv];

            (await monitor.StopAsync(programs[0])).Should().BeTrue();
            control.Killed.Should().Equal((10, 100L));
            control.KillResult = false;
            (await monitor.StopAsync(programs[1])).Should().BeFalse();

            snapshots.Rows = [];
            monitor.Tick();
            scans[^1].Exits.Select(e => (e.Program.Pid, e.StoppedByUser)).Should().BeEquivalentTo(new[] { (10, true), (20, false) });
        }
    }

    [Fact]
    public async Task 真的程序_追蹤結束碼與停止整棵樹()
    {
        var marker = Guid.NewGuid().ToString("N");
        // cmd 以 start /b 開兩個 ping 之後立刻結束（同代理以 Start-Process 另開、工具的殼層結束）：一個自己結束（-n 2），一個一直跑、由我們停止
        using var launcher = Start("cmd.exe", "/c start \"\" /b ping.exe -n 2 127.0.0.1 & start \"\" /b ping.exe -n 120 127.0.0.2", marker);
        var scans = new List<BackgroundScan>();
        var monitor = new BackgroundProgramMonitor(new NativeProcessSnapshot(), () => new HashSet<int> { Environment.ProcessId }, new NativeProgramControl());
        try
        {
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            BackgroundProgram? shortOne = null, longOne = null;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20) && (shortOne == null || longOne == null))
            {
                var scan = ScanOnce(monitor, scans);
                if (scan.Programs.TryGetValue(marker, out var found))
                {
                    shortOne ??= found.FirstOrDefault(p => p.CommandLine?.Contains("127.0.0.1") == true);
                    longOne ??= found.FirstOrDefault(p => p.CommandLine?.Contains("127.0.0.2") == true);
                }
                await Task.Delay(100);
            }
            shortOne.Should().NotBeNull("父程序結束的 ping 算背景程式");
            longOne.Should().NotBeNull();

            // 自己結束的：帶結束碼 0 與結束時間
            BackgroundProgramExit? ended = null;
            deadline.Restart();
            while (ended == null && deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                ended = ScanOnce(monitor, scans).Exits.FirstOrDefault(e => e.Program.Pid == shortOne!.Pid);
                await Task.Delay(200);
            }
            ended.Should().NotBeNull();
            ended!.ExitCode.Should().Be(0);
            ended.ExitedAt.Should().BeGreaterThan(shortOne!.CreatedAt);
            ended.StoppedByUser.Should().BeFalse();

            // 使用者停止的：程序真的結束，下一次掃描帶 StoppedByUser
            (await monitor.StopAsync(longOne!)).Should().BeTrue();
            Process.GetProcesses().Should().NotContain(p => p.Id == longOne!.Pid && !p.HasExited && p.ProcessName.Equals("PING", StringComparison.OrdinalIgnoreCase));
            BackgroundProgramExit? stopped = null;
            deadline.Restart();
            while (stopped == null && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                stopped = ScanOnce(monitor, scans).Exits.FirstOrDefault(e => e.Program.Pid == longOne!.Pid);
                await Task.Delay(100);
            }
            stopped.Should().NotBeNull();
            stopped!.StoppedByUser.Should().BeTrue();
            stopped.ExitCode.Should().Be(1, "TerminateProcess 的結束碼");

            (await monitor.StopAsync(longOne!)).Should().BeFalse("已經結束的停不了");
        }
        finally
        {
            monitor.Dispose();
            foreach (var p in Process.GetProcessesByName("PING"))
            {
                using (p)
                {
                    try
                    {
                        if (new NativeProcessSnapshot().Snapshot().Any(r => r.Pid == p.Id && r.Marker == marker))
                            p.Kill();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }

    /// <summary>掃描一次、回傳結果（Tick 在測試執行緒上同步交出）</summary>
    private static BackgroundScan ScanOnce(BackgroundProgramMonitor monitor, List<BackgroundScan> scans)
    {
        BackgroundScan? result = null;
        monitor.StartManual(scan => result = scan);
        monitor.Tick();
        result.Should().NotBeNull();
        scans.Add(result!);
        return result!;
    }

    private static Process Start(string file, string args, string marker)
    {
        var info = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
        info.Environment[BackgroundProgramRules.MarkerVariable] = marker;
        return Process.Start(info)!;
    }
}
