using System.Diagnostics;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

/// <summary>
/// app 這一側的換版幫手控制代碼（AppController 的 StartHelper／StopHelper，以真的子程序驗證）：放棄交棒時只結束自己啟動、還留著控制代碼的那個幫手，
/// PID 不符就不動作；app 結束時只放開控制代碼、不結束幫手（交棒之後換版要靠它）。
/// 用 ping.exe 當幫手：它沒有子程序，和還沒寫 .armed 的幫手一樣（Stop 只結束程序本身，不結束程序樹）
/// </summary>
public sealed class UpdateHelperProcessTests
{
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    /// <summary>約 30 秒</summary>
    private static readonly string[] PingArgs = { "-n", "30", "127.0.0.1" };

    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>不經 UpdateHelperProcess 另外開的程序（測試持有控制代碼，PID 不會被重用）</summary>
    private static Process StartUnrelated()
    {
        var info = new ProcessStartInfo(Ping) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in PingArgs)
            info.ArgumentList.Add(a);
        return Process.Start(info)!;
    }

    /// <summary>以 PID 另外開一個控制代碼觀察（幫手的控制代碼在 UpdateHelperProcess 裡）</summary>
    private static Process Observe(int pid) => Process.GetProcessById(pid);

    private static void EnsureStopped(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // 已經結束
        }
    }

    [Fact]
    public void PID不符_不動作_自己的幫手與不相干的程序都照常執行()
    {
        using var unrelated = StartUnrelated();
        using var helpers = new UpdateHelperProcess();
        var pid = helpers.Start(Ping, PingArgs);
        pid.Should().NotBeNull();
        using var helper = Observe(pid!.Value);
        try
        {
            helpers.Stop(unrelated.Id);

            unrelated.HasExited.Should().BeFalse("不是自己啟動的程序：絕不結束");
            helper.HasExited.Should().BeFalse("PID 不符：自己的幫手也不動");
        }
        finally
        {
            EnsureStopped(unrelated);
            EnsureStopped(helper);
        }
    }

    [Fact]
    public void PID相符_結束幫手並等它結束()
    {
        using var helpers = new UpdateHelperProcess();
        var pid = helpers.Start(Ping, PingArgs)!.Value;
        using var helper = Observe(pid);
        try
        {
            helper.HasExited.Should().BeFalse();

            helpers.Stop(pid);

            helper.WaitForExit(1000).Should().BeTrue("Stop 等幫手結束才回來");
        }
        finally
        {
            EnsureStopped(helper);
        }
    }

    [Fact]
    public void 幫手已經結束_Stop不丟例外()
    {
        using var helpers = new UpdateHelperProcess();
        var pid = helpers.Start(Cmd, new[] { "/d", "/c", "exit", "0" })!.Value;
        try
        {
            using var helper = Observe(pid);
            helper.WaitForExit(10_000).Should().BeTrue("cmd /c exit 0 結束");
        }
        catch (ArgumentException)
        {
            // 觀察之前就結束了
        }

        helpers.Invoking(h => h.Stop(pid)).Should().NotThrow("幫手提早結束：放棄交棒照常往下走");
        helpers.Invoking(h => h.Stop(pid)).Should().NotThrow("第二次：控制代碼已經放開，什麼都不做");
    }

    [Fact]
    public void 釋放時只放開控制代碼_不結束幫手()
    {
        int pid;
        using (var helpers = new UpdateHelperProcess())
            pid = helpers.Start(Ping, PingArgs)!.Value;
        using var helper = Observe(pid);
        try
        {
            helper.HasExited.Should().BeFalse("交棒之後 app 結束：換版要靠幫手，不能跟著結束");
        }
        finally
        {
            EnsureStopped(helper);
        }
    }

    [Fact]
    public void 啟動不了_回null不丟例外()
    {
        using var helpers = new UpdateHelperProcess();

        helpers.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Myriagent.exe"), Array.Empty<string>()).Should().BeNull();
        helpers.Invoking(h => h.Stop(4242)).Should().NotThrow("沒有幫手：什麼都不做");
    }
}
