using System.ComponentModel;
using System.Diagnostics;
using Myriagent.Core;
using Myriagent.UI;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

/// <summary>
/// 換版幫手真正的平台（以真的子程序驗證）：PID 被別的程序重用時不能誤判、更不能誤殺。
/// 幫手啟動的新版一律用 Launch 時留下的控制代碼（持有控制代碼時 Windows 不會重用這個 PID）；
/// 舊 app 是幫手的父程序、一定比幫手早啟動，同一個 PID 上比幫手晚啟動的程序不是它。
/// 測試主機就是這裡的「幫手」：測試自己開的程序都比它晚啟動
/// </summary>
public sealed class WindowsApplierPlatformTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>約 30 秒的 cmd /c ping；輸出丟掉（cmd 自己解析 &gt;nul）</summary>
    private static readonly string[] PingArgs = { "/d", "/c", "ping", "-n", "30", "127.0.0.1", ">nul" };

    private static void WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("10 秒內沒有等到：" + because);
            Thread.Sleep(50);
        }
    }

    [Fact]
    public void 幫手啟動的程序結束後_HasExited為真_KillTree不丟例外()
    {
        using var platform = new WindowsApplierPlatform();

        var pid = platform.Launch(Cmd, new[] { "/d", "/c", "exit", "0" });

        pid.Should().NotBeNull();
        WaitUntil(() => platform.HasExited(pid!.Value), "cmd /c exit 0 結束");
        platform.Invoking(p => p.KillTree(pid!.Value)).Should().NotThrow("新版已經結束：回滾照常往下走");
    }

    [Fact]
    public void 幫手啟動的程序的結束碼_還在跑或不是它啟動的回null()
    {
        using var platform = new WindowsApplierPlatform();

        var exited = platform.Launch(Cmd, new[] { "/d", "/c", "exit", UpdateApplier.AnotherInstanceExitCode.ToString() })!.Value;
        WaitUntil(() => platform.HasExited(exited), "cmd /c exit 4 結束");
        platform.ExitCode(exited).Should().Be(UpdateApplier.AnotherInstanceExitCode, "新版遇到別的實例而結束：幫手要讀得到這個結束碼");

        var running = platform.Launch(Cmd, PingArgs)!.Value;
        try
        {
            platform.ExitCode(running).Should().BeNull("還在跑");
        }
        finally
        {
            platform.KillTree(running);
        }
        platform.ExitCode(Environment.ProcessId).Should().BeNull("不是幫手啟動的程序");
    }

    [Fact]
    public void 這個資料夾有app實例時_IsInstanceRunning為真_和Program用同一個單一實例名稱()
    {
        // 暫存資料夾：後綴是它的雜湊，不會碰到使用者正在執行的 app（預設資料夾沒有後綴）
        var root = Path.Combine(Path.GetTempPath(), "myriagent-applier-" + Guid.NewGuid().ToString("N"));
        using var platform = new WindowsApplierPlatform();
        platform.IsInstanceRunning(root).Should().BeFalse("還沒有實例");

        using (var instance = SingleInstance.Acquire(ShellRules.InstanceSuffix(root, AppPaths.DefaultRoot)))
        {
            instance.Should().NotBeNull();
            platform.IsInstanceRunning(root).Should().BeTrue("Program.Main 以同一個名稱取得單一實例");
            platform.IsInstanceRunning(root + "\\").Should().BeTrue("同一個資料夾換個寫法也一樣");
        }

        platform.IsInstanceRunning(root).Should().BeFalse("實例結束後就沒有");
    }

    [Fact]
    public void 幫手啟動的程序還在跑_HasExited為假_KillTree結束它()
    {
        using var platform = new WindowsApplierPlatform();
        var pid = platform.Launch(Cmd, PingArgs)!.Value;
        try
        {
            platform.HasExited(pid).Should().BeFalse();

            platform.KillTree(pid);

            platform.HasExited(pid).Should().BeTrue("KillTree 等它結束才回來");
        }
        finally
        {
            if (!platform.HasExited(pid))
                platform.KillTree(pid);
        }
    }

    [Fact]
    public void 同一個PID上比幫手晚啟動的程序不是舊app_WaitForExit當成已結束_Kill不動它()
    {
        // 模擬舊 app 結束之後 PID 被別的程序拿走：這個程序比測試主機（幫手）晚啟動。測試持有它的控制代碼，PID 不會再被重用
        var info = new ProcessStartInfo(Cmd) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in PingArgs)
            info.ArgumentList.Add(a);
        using var unrelated = Process.Start(info)!;
        try
        {
            using var platform = new WindowsApplierPlatform();
            var watch = Stopwatch.StartNew();

            platform.WaitForExit(unrelated.Id, TimeSpan.FromSeconds(3)).Should().BeTrue("比幫手晚啟動，一定不是幫手的父程序：舊 app 已經結束");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "不必等到逾時");
            platform.Kill(unrelated.Id);

            unrelated.HasExited.Should().BeFalse("不相干的程序絕不能被結束");
        }
        finally
        {
            if (!unrelated.HasExited)
            {
                unrelated.Kill(entireProcessTree: true);
                unrelated.WaitForExit(5000);
            }
        }
    }

    [Fact]
    public void 不是幫手啟動而且打不開的程序_HasExited當成已結束_不丟例外()
    {
        // PID 4 是 System：一般權限打不開（Win32Exception 存取被拒），等於 PID 被系統或其他帳戶的程序重用
        bool inaccessible;
        try
        {
            using var system = Process.GetProcessById(4);
            _ = system.HasExited;
            inaccessible = false;
        }
        catch (Win32Exception)
        {
            inaccessible = true;
        }
        using var platform = new WindowsApplierPlatform();

        platform.Invoking(p => p.HasExited(4)).Should().NotThrow("例外會跳過回滾，讓使用者卡在當掉的新版上");
        if (inaccessible)
            platform.HasExited(4).Should().BeTrue("打不開的程序不是幫手啟動的那個新版");
    }
}
