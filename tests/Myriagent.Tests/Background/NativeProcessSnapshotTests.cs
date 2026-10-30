using System.Diagnostics;
using Myriagent.Background;

namespace Myriagent.Tests.Background;

/// <summary>
/// 真的程序（NativeProcessSnapshot）：讀得到子程序環境變數裡的標記、命令列與主控台子系統；父程序結束後留下來的程式（cmd 的 start /b）
/// 照樣帶著標記、而且不在測試程序底下——就是「脫離 CLI 的背景程式」
/// </summary>
public sealed class NativeProcessSnapshotTests
{
    [Fact]
    public async Task 讀到子程序的標記_命令列與子系統_父程序結束後留下的程式算背景程式()
    {
        var marker = Guid.NewGuid().ToString("N");
        var direct = Start("ping.exe", "-n 60 127.0.0.1", marker);
        // cmd 以 start /b 開 ping 之後立刻結束：ping 的父程序已經不在（同代理以 nohup、Start-Process 開出、工具的殼層結束的情形）
        using var launcher = Start("cmd.exe", "/c start \"\" /b ping.exe -n 61 127.0.0.1", marker);
        try
        {
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var snapshot = new NativeProcessSnapshot();
            List<ProcessRow> marked = [];
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                // 主控台宿主（conhost.exe）也繼承標記，分類時才排除：這裡只看 ping
                marked = snapshot.Snapshot().Where(r => r.Marker == marker && r.Name.Equals("PING.EXE", StringComparison.OrdinalIgnoreCase)).ToList();
                if (marked.Count >= 2)
                    break;
                await Task.Delay(200);
            }

            var self = marked.Single(r => r.Pid == direct.Id);
            self.Name.Should().BeEquivalentTo("PING.EXE");
            self.ParentPid.Should().Be(Environment.ProcessId);
            self.IsConsole.Should().BeTrue("ping 是主控台程式");
            self.CommandLine.Should().Contain("-n 60 127.0.0.1");
            var orphan = marked.Single(r => r.Pid != direct.Id);
            orphan.CommandLine.Should().Contain("-n 61");

            var rows = snapshot.Snapshot();
            var programs = BackgroundProgramRules.Classify(rows, new HashSet<int> { Environment.ProcessId })[marker];
            programs.Select(p => p.Pid).Should().Equal(new[] { orphan.Pid }, "直接的子程序還在測試程序（當成 CLI）底下，父程序結束的那個才算");
            BackgroundProgramRules.ShortCommand(programs[0].CommandLine, programs[0].Name).Should().StartWith("ping").And.Contain("-n 61");
            // 測試主機和啟動它的程序切開：dotnet test 一次跑好幾個專案（slnx）時，測試主機在 MSBuild 的工作節點（/nodemode）底下，
            // 建置常駐程序開出的程序不算（IsToolDaemon），直接的子程序就會被當成建置的一部分
            var detached = rows.Select(r => r.Pid == Environment.ProcessId ? r with { ParentPid = 0 } : r).ToList();
            BackgroundProgramRules.Classify(detached, new HashSet<int>())[marker].Should().HaveCount(2, "沒有 CLI 時兩個都算");
            rows.Where(r => r.Marker is not null && r.Marker != marker).Should().NotContain(r => r.Pid == direct.Id);
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("PING"))
            {
                try
                {
                    if (IsOurs(p, direct, marker))
                        p.Kill();
                }
                catch (Exception)
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
            direct.Dispose();
        }
    }

    [Fact]
    public void 沒有標記的程序_Marker是null()
    {
        using var plain = Start("ping.exe", "-n 30 127.0.0.1", marker: null);
        try
        {
            var row = new NativeProcessSnapshot().Snapshot().Single(r => r.Pid == plain.Id);
            row.Marker.Should().BeNull();
            row.CommandLine.Should().BeNull("沒有標記的程序不讀命令列");
        }
        finally
        {
            plain.Kill();
        }
    }

    private static Process Start(string file, string args, string? marker)
    {
        var info = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
        info.Environment.Remove(BackgroundProgramRules.MarkerVariable);
        if (marker is not null)
            info.Environment[BackgroundProgramRules.MarkerVariable] = marker;
        return Process.Start(info)!;
    }

    /// <summary>這個測試開的 ping（直接開的那個，或帶這個標記的那個）：只收自己的，不碰別的測試或使用者的 ping</summary>
    private static bool IsOurs(Process p, Process direct, string marker)
    {
        if (p.Id == direct.Id)
            return true;
        var snapshot = new NativeProcessSnapshot().Snapshot();
        return snapshot.Any(r => r.Pid == p.Id && r.Marker == marker);
    }
}
