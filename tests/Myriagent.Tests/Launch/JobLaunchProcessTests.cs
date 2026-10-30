using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Myriagent.Launch;

namespace Myriagent.Tests.Launch;

/// <summary>
/// 執行列的建置程序（經管線；真的 cmd.exe 與 ping，不建置、不碰網路以外的東西）：輸出一行一行讀到（主控台字碼頁的中文也解得對）、停止結束整個程序樹、
/// 暫停時不再有輸出、繼續後恢復、開不起來丟例外
/// </summary>
public sealed class JobLaunchProcessTests
{
    private static readonly string Cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private static LaunchCommand Shell(string script)
        => new(Cmd, ["/d", "/s", "/c", script], Path.GetTempPath());

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException(because);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task 讀到輸出與結束碼_主控台字碼頁的中文也解得對()
    {
        var lines = new ConcurrentQueue<string>();
        using var process = JobLaunchProcess.Start(Shell("echo hello& echo 中文輸出& exit /b 3"), lines.Enqueue);
        var code = await process.Exited.WaitAsync(TimeSpan.FromSeconds(20));
        code.Should().Be(3);
        lines.Should().Contain("hello");
        // cmd 的 echo 是主控台字碼頁（不是 UTF-8）：字碼頁裝得下中文的電腦（950、936…）才比對
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage);
        if (oem.GetString(oem.GetBytes("中文輸出")) == "中文輸出")
            lines.Should().Contain("中文輸出");
    }

    [Fact]
    public async Task 停止_結束整個程序樹()
    {
        var lines = new ConcurrentQueue<string>();
        using var process = JobLaunchProcess.Start(Shell("echo started& ping -n 60 127.0.0.1"), lines.Enqueue);
        await WaitUntilAsync(() => lines.Any(l => l.Contains("127.0.0.1")), "ping 開始輸出");
        var sw = Stopwatch.StartNew();
        process.Kill();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(LaunchRules.OutputDrainTimeout.TotalSeconds + 2),
            "ping 也跟著結束（否則它握著輸出管線，要等到逾時）");
    }

    [Fact]
    public async Task 暫停時沒有輸出_繼續後恢復()
    {
        var lines = new ConcurrentQueue<string>();
        using var process = JobLaunchProcess.Start(Shell("for /l %i in (1,1,120) do @(echo tick %i& ping -n 2 127.0.0.1 >nul)"), lines.Enqueue);
        try
        {
            await WaitUntilAsync(() => lines.Count(l => l.StartsWith("tick", StringComparison.Ordinal)) >= 2, "每秒一個 tick");
            process.Suspend().Should().BeTrue();
            await Task.Delay(300);   // 暫停前已經寫出的輸出讀完
            var paused = lines.Count;
            await Task.Delay(2500);
            lines.Count.Should().Be(paused, "暫停中整個程序樹都不動");
            process.Resume().Should().BeTrue();
            await WaitUntilAsync(() => lines.Count > paused, "繼續後恢復輸出");
        }
        finally
        {
            process.Kill();
            await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task 暫停中也能停止()
    {
        using var process = JobLaunchProcess.Start(Shell("ping -n 60 127.0.0.1"), _ => { });
        await Task.Delay(500);
        process.Suspend().Should().BeTrue();
        process.Kill();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>cmd 與它開的 ping（PID＋建立時間）</summary>
    private static async Task<List<LaunchProcessRef>> TreeAsync(JobLaunchProcess process)
    {
        var root = LaunchNative.Describe(process.ProcessId) ?? throw new InvalidOperationException("cmd 不在了");
        var tree = new List<LaunchProcessRef>();
        await WaitUntilAsync(() => (tree = LaunchNative.WithDescendants([root])).Count >= 2, "cmd 開了 ping");
        return tree;
    }

    [Fact]
    public async Task Debug模式_關掉工作物件就結束整個程序樹_建置的不會()
    {
        // Debug 模式（killOnJobClose）：app 結束或當掉時工作物件的最後一個控制代碼關掉，系統結束裡面的程序——Dispose 就是關掉它
        var attached = JobLaunchProcess.Start(Shell("echo started& ping -n 60 127.0.0.1"), _ => { }, killOnJobClose: true);
        var tree = await TreeAsync(attached);
        attached.Dispose();
        await WaitUntilAsync(() => tree.All(r => LaunchNative.OpenIfSame(r) is not { } h || Dispose(h)), "cmd 與 ping 都被系統結束");

        // 建置（沒有 killOnJobClose）：Dispose 只放開控制代碼，程式照樣執行（app 結束時中樞自己結束它）
        var build = JobLaunchProcess.Start(Shell("echo started& ping -n 60 127.0.0.1"), _ => { });
        var buildTree = await TreeAsync(build);
        try
        {
            build.Dispose();
            await Task.Delay(1000);
            buildTree.Should().OnlyContain(r => LaunchNative.Describe(r.Pid) == r, "沒設 KILL_ON_JOB_CLOSE：關掉工作物件不結束程式");
        }
        finally
        {
            foreach (var r in buildTree)
                LaunchNative.TerminateIfSame(r);
        }
    }

    private static bool Dispose(IDisposable handle)
    {
        handle.Dispose();
        return false;
    }

    [Fact]
    public void 找不到執行檔_丟例外()
    {
        var act = () => JobLaunchProcess.Start(new LaunchCommand("myriagent-no-such-tool-" + Guid.NewGuid().ToString("N"), [], Path.GetTempPath()), _ => { });
        act.Should().Throw<InvalidOperationException>().WithMessage("*找不到或無法執行*");
    }
}
