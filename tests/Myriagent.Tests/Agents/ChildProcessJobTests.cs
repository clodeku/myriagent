using System.ComponentModel;
using System.Diagnostics;
using Myriagent.Agents;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 熱更新契約：app 工作物件的 PID 清單與歸屬（Contains／GetProcessIds）、程序樹快照與「結束並確認子孫都已結束」，以真的子程序驗證。
/// 子程序是 cmd /c ping（約 30 秒）：cmd 由 app 直接啟動、在工作物件裡；cmd 開的 ping 因 SILENT_BREAKAWAY_OK 不在工作物件裡，但仍在程序樹上
/// </summary>
public sealed class ChildProcessJobTests
{
    private static readonly AgentLauncher Cmd = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>());

    private static AgentProcess StartPing()
    {
        var process = AgentProcess.Start(Cmd, new[] { "/c", "ping", "-n", "30", "127.0.0.1" }, Path.GetTempPath(), null);
        process.BeginReading();
        return process;
    }

    /// <summary>等 cmd 開出 ping（程序樹上出現 PING.EXE）</summary>
    private static async Task<(ProcessTreeSnapshot Tree, int PingId)> WaitForPingAsync(AgentProcess cmd)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var tree = cmd.CaptureTree();
            var ping = tree.Processes.Skip(1).FirstOrDefault(p => NameOf(p.Id).Equals("PING", StringComparison.OrdinalIgnoreCase));
            if (ping != default)
                return (tree, ping.Id);
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("10 秒內 cmd 沒有開出 ping：" + string.Join("、", tree.Processes.Select(p => $"{p.Id}:{NameOf(p.Id)}")));
            await Task.Delay(50);
        }
    }

    private static string NameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "（已結束）";
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("10 秒內沒有等到：" + because);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task 直接啟動的程序在工作物件裡_它的孫程序不在但在程序樹上_結束後兩者都消失()
    {
        using var cmd = StartPing();
        var (tree, pingId) = await WaitForPingAsync(cmd);

        ChildProcessJob.IsCreated.Should().BeTrue("第一次啟動 CLI 程序時建立");
        ChildProcessJob.Contains(cmd.ProcessId).Should().BeTrue("app 直接啟動的程序加入工作物件");
        ChildProcessJob.GetProcessIds().Should().Contain(cmd.ProcessId);
        ChildProcessJob.Contains(pingId).Should().BeFalse("SILENT_BREAKAWAY_OK：一般程序開的子程序不綁進 app 的工作物件");
        ChildProcessJob.GetProcessIds().Should().NotContain(pingId);
        tree.Processes[0].Id.Should().Be(cmd.ProcessId, "根在第一個");
        tree.Processes.Select(p => p.Id).Should().Contain(pingId, "不在工作物件裡的孫程序仍是這棵樹的一部分，重開前要確認它也結束了");
        tree.GetAlive().Select(p => p.Id).Should().Contain(new[] { cmd.ProcessId, pingId });

        (await cmd.KillTreeAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("結束整個程序樹並確認子孫都已結束");

        tree.GetAlive().Should().BeEmpty();
        (await tree.WaitForExitAsync(TimeSpan.Zero)).Should().BeTrue();
        await WaitUntilAsync(() => !ChildProcessJob.GetProcessIds().Contains(cmd.ProcessId), "結束的程序移出工作物件的 PID 清單");
        ChildProcessJob.Contains(cmd.ProcessId).Should().BeFalse();
        (await cmd.ExitTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().NotBe(0, "被結束的程序結束碼不是 0");
    }

    [Fact]
    public async Task 根程序先結束時_KillAlive逐一結束樹上剩下的程序()
    {
        using var cmd = StartPing();
        var (tree, pingId) = await WaitForPingAsync(cmd);

        // 只結束根程序（不含子孫）：ping 的父程序鏈斷了，之後再擷取的樹接不上它，只有先前的快照找得到。
        // （ping 繼承了 stdout 管線，ExitTask 要等它也結束才會完成，所以這裡改看程序本身）
        using (var root = Process.GetProcessById(cmd.ProcessId))
            root.Kill(entireProcessTree: false);
        await WaitUntilAsync(() => tree.GetAlive().All(p => p.Id != cmd.ProcessId), "根程序結束");
        tree.GetAlive().Select(p => p.Id).Should().Contain(pingId, "根已結束、孫程序還在");
        cmd.CaptureTree().Processes.Should().BeEmpty("根已結束就擷取不到樹");
        ChildProcessJob.Contains(cmd.ProcessId).Should().BeFalse("已結束（AgentProcess 還握著 handle）不算在工作物件裡");
        ChildProcessJob.GetProcessIds().Should().NotContain(cmd.ProcessId);

        tree.KillAlive().Should().BeGreaterThanOrEqualTo(1);
        (await tree.WaitForExitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        tree.GetAlive().Should().BeEmpty();
    }

    [Fact]
    public void 工作物件查詢_不存在的PID回false()
    {
        ChildProcessJob.Contains(0).Should().BeFalse();
        ChildProcessJob.Contains(-1).Should().BeFalse();
        ChildProcessJob.Contains(Environment.ProcessId).Should().BeFalse("測試程序自己不在 app 的工作物件裡");
        ProcessTreeSnapshot.Capture(0).Processes.Should().BeEmpty();
        ProcessTreeSnapshot.Empty.GetAlive().Should().BeEmpty();
    }

    [Fact]
    public async Task 執行檔路徑_以QueryFullProcessImageName取得_打不開回null()
    {
        ProcessImage.TryGetPath(Environment.ProcessId).Should().BeEquivalentTo(Environment.ProcessPath, "測試程序自己的執行檔");
        ProcessImage.TryGetPath(0).Should().BeNull();
        ProcessImage.TryGetPath(-5).Should().BeNull();

        using var cmd = StartPing();
        var (_, pingId) = await WaitForPingAsync(cmd);
        ProcessImage.TryGetPath(cmd.ProcessId).Should().BeEquivalentTo(Cmd.FileName);
        Path.GetFileName(ProcessImage.TryGetPath(pingId)).Should().BeEquivalentTo("PING.EXE");
        (await cmd.KillTreeAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
    }

    [Fact]
    public void 程序樹_依父程序串起_建立時間早於父程序的是PID被重複使用前的別的程序()
    {
        // 10 是根（建立於 t=100）；11、12 是它的子程序；13 是 11 的子程序；
        // 20 的父程序 PID 也是 10，但建立於 t=50（比根早）：那是 PID 10 被重複使用前那個程序的子程序，不屬於這棵樹；它的子程序 21 也不算
        var table = new List<(int Id, int ParentId)> { (10, 1), (11, 10), (12, 10), (13, 11), (20, 10), (21, 20), (30, 99), (0, 0), (4, 0) };
        var starts = new Dictionary<int, long> { [10] = 100, [11] = 110, [12] = 120, [13] = 130, [20] = 50, [21] = 60, [30] = 300, [4] = 1 };

        var tree = ProcessTreeSnapshot.FindDescendants(10, 100, table, id => starts.TryGetValue(id, out var t) ? t : null);

        tree.Select(p => p.Id).Should().Equal(10, 11, 12, 13);
        tree.Should().Contain(new ProcessStamp(13, 130));
        ProcessTreeSnapshot.FindDescendants(10, 100, table, id => id == 12 ? null : starts.GetValueOrDefault(id))
            .Select(p => p.Id).Should().Equal(new[] { 10, 11, 13 }, "查不到建立時間（已結束、沒有權限）的不算");
        ProcessTreeSnapshot.FindDescendants(0, 0, table, id => starts.TryGetValue(id, out var t) ? t : null)
            .Select(p => p.Id).Should().Equal(new[] { 0, 4 }, "PID 0 的自我迴圈不會讓搜尋停不下來");
    }

    [Fact]
    public async Task 啟動重試_換檔空窗的暫時失敗依間隔重試_其他錯誤立即丟出()
    {
        var delays = new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) };

        int attempts = 0;
        var value = await AgentProcess.RetryTransientStartAsync(() => ++attempts < 3 ? throw new Win32Exception(2) : "已啟動", delays, CancellationToken.None);
        value.Should().Be("已啟動");
        attempts.Should().Be(3, "找不到執行檔（Claude 換檔的兩次改名之間）就等一下再試");

        attempts = 0;
        var notTransient = () => AgentProcess.RetryTransientStartAsync<string>(() => { attempts++; throw new Win32Exception(5); }, delays, CancellationToken.None);
        await notTransient.Should().ThrowAsync<Win32Exception>();
        attempts.Should().Be(1, "沒有權限不是換檔空窗，重試也沒用");

        attempts = 0;
        var always = () => AgentProcess.RetryTransientStartAsync<string>(() => { attempts++; throw new Win32Exception(3); }, delays, CancellationToken.None);
        await always.Should().ThrowAsync<Win32Exception>();
        attempts.Should().Be(delays.Length + 1, "間隔用完後的失敗原樣丟出");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancel = () => AgentProcess.RetryTransientStartAsync<string>(() => throw new Win32Exception(32), delays, cancelled.Token);
        await cancel.Should().ThrowAsync<OperationCanceledException>();

        AgentProcess.StartRetryDelays.Select(d => d.TotalMilliseconds).Should().Equal(500, 1000, 2000);
    }

    [Fact]
    public void 啟動不存在的執行檔是暫時失敗_每個程序實例的識別都不同()
    {
        var missing = new AgentLauncher(Path.Combine(Path.GetTempPath(), "Myriagent.Tests", Guid.NewGuid().ToString("N"), "claude.exe"), Array.Empty<string>());
        var start = () => AgentProcess.Start(missing, Array.Empty<string>(), Path.GetTempPath(), null);
        var thrown = start.Should().Throw<Win32Exception>().Which;
        AgentProcess.IsTransientStartFailure(thrown).Should().BeTrue("CLI 換檔的空窗裡就是這個錯誤");
        AgentProcess.IsTransientStartFailure(new InvalidOperationException()).Should().BeFalse();
        AgentProcess.IsTransientStartFailure(new Win32Exception(5)).Should().BeFalse();

        using var a = AgentProcess.Start(Cmd, new[] { "/c", "exit", "0" }, Path.GetTempPath(), null);
        using var b = AgentProcess.Start(Cmd, new[] { "/c", "exit", "0" }, Path.GetTempPath(), null);
        a.InstanceId.Should().MatchRegex("^[0-9a-f]{32}$", "Guid N 格式");
        b.InstanceId.Should().NotBe(a.InstanceId, "每次啟動都換新的實例");
    }
}
