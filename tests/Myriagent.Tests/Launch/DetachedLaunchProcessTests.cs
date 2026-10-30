using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Myriagent.Launch;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Launch;

/// <summary>
/// 執行列和 app 脫鉤的執行（真的 cmd.exe 與 ping）：輸出經輸出檔讀到、結束碼；停止結束整棵樹；暫停與繼續；Dispose 不結束程式（app 結束的情形）；
/// 以記下的程序接回（輸出檔從頭讀起）、接回後暫停與停止；建立時間對不上不接回；找不到執行檔丟例外、不留輸出檔；輸出檔超過上限時清空、輸出照樣讀到
/// </summary>
public sealed class DetachedLaunchProcessTests : IDisposable
{
    private static readonly string Cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private readonly TempDirectory temp = new("launch-detached");

    /// <summary>測試開過的程序：結束時一律收掉（測試失敗也不留下 ping）</summary>
    private readonly ConcurrentBag<LaunchProcessRef> seen = [];

    private static LaunchCommand Shell(string script) => new(Cmd, ["/d", "/s", "/c", script], Path.GetTempPath());

    private string NewLog() => temp.Combine(LaunchRules.LogFileName(LaunchRules.TargetId(temp.Combine(Guid.NewGuid().ToString("N"))), DateTime.UtcNow));

    /// <summary>檔案目前的大小（開檔問：別的程序還開著寫時，目錄項目記的大小可能還沒更新）</summary>
    private static long LiveLength(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

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

    /// <summary>等到程序清單至少有 count 個（cmd 開出 ping 要一點時間），記下來給 Dispose 收</summary>
    private async Task<IReadOnlyList<LaunchProcessRef>> ProcessesAsync(ILaunchProcess process, int count)
    {
        IReadOnlyList<LaunchProcessRef> refs = [];
        await WaitUntilAsync(() => (refs = process.Processes()).Count >= count, $"程序清單有 {count} 個");
        foreach (var r in refs)
            seen.Add(r);
        return refs;
    }

    private static bool AllGone(IEnumerable<LaunchProcessRef> refs) => refs.All(r => LaunchNative.OpenIfSame(r) is not { } h || Dispose(h));

    private static bool Dispose(IDisposable d)
    {
        d.Dispose();
        return false;
    }

    public void Dispose()
    {
        foreach (var r in LaunchNative.WithDescendants(seen.ToList()))
            LaunchNative.TerminateIfSame(r);
        temp.Dispose();
    }

    [Fact]
    public async Task 輸出經輸出檔讀到_結束碼_主控台字碼頁的中文也解得對()
    {
        var lines = new ConcurrentQueue<string>();
        using var process = DetachedLaunchProcess.Start(Shell("echo hello& echo 中文輸出& exit /b 3"), NewLog(), lines.Enqueue);
        var code = await process.Exited.WaitAsync(TimeSpan.FromSeconds(20));
        code.Should().Be(3);
        lines.Should().Contain("hello");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage);
        if (oem.GetString(oem.GetBytes("中文輸出")) == "中文輸出")
            lines.Should().Contain("中文輸出");
    }

    [Fact]
    public async Task 輸出在專用的背景執行緒上_不在執行緒集區()
    {
        var threads = new ConcurrentQueue<(bool Pool, bool Background, string? Name)>();
        using var process = DetachedLaunchProcess.Start(Shell("echo out& echo err 1>&2"), NewLog(),
            _ => threads.Enqueue((Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.IsBackground, Thread.CurrentThread.Name)));
        (await process.Exited.WaitAsync(TimeSpan.FromSeconds(20))).Should().Be(0);
        threads.Should().HaveCount(2, "stdout 與 stderr 寫進同一個輸出檔");
        threads.Should().OnlyContain(t => !t.Pool && t.Background && t.Name == $"launch {process.ProcessId} log");
    }

    [Fact]
    public async Task 停止_結束整個程序樹()
    {
        var lines = new ConcurrentQueue<string>();
        using var process = DetachedLaunchProcess.Start(Shell("echo started& ping -n 60 127.0.0.1"), NewLog(), lines.Enqueue);
        await WaitUntilAsync(() => lines.Any(l => l.Contains("127.0.0.1")), "ping 開始輸出");
        var refs = await ProcessesAsync(process, 2);
        process.Kill();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        // TerminateJobObject 是非同步的：主程序結束了，ping 可能晚一點點
        await WaitUntilAsync(() => AllGone(refs), "cmd 與 ping 都結束", 3000);
    }

    [Fact]
    public async Task 暫停時沒有輸出_繼續後恢復()
    {
        var lines = new ConcurrentQueue<string>();
        using var process = DetachedLaunchProcess.Start(Shell("for /l %i in (1,1,120) do @(echo tick %i& ping -n 2 127.0.0.1 >nul)"), NewLog(), lines.Enqueue);
        try
        {
            await WaitUntilAsync(() => lines.Count(l => l.StartsWith("tick", StringComparison.Ordinal)) >= 2, "每秒一個 tick");
            process.Suspend().Should().BeTrue();
            await Task.Delay(500);   // 暫停前已經寫出的讀完
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
    public async Task Dispose不結束程式_接回後讀得到之前的輸出_停止結束整棵樹()
    {
        var log = NewLog();
        var lines = new ConcurrentQueue<string>();
        var process = DetachedLaunchProcess.Start(Shell("echo started& ping -n 60 127.0.0.1"), log, lines.Enqueue);
        await WaitUntilAsync(() => lines.Any(l => l.Contains("127.0.0.1")), "ping 開始輸出");
        var refs = await ProcessesAsync(process, 2);
        refs[0].Pid.Should().Be(process.ProcessId, "主程序在第一個");

        process.Dispose();   // app 結束
        await Task.Delay(700);
        foreach (var r in refs)
            LaunchNative.Describe(r.Pid).Should().Be(r, "app 結束後程式照樣執行");
        process.Exited.IsCompleted.Should().BeFalse("Dispose 之後不會完成");

        var again = new ConcurrentQueue<string>();
        using var reattached = DetachedLaunchProcess.Reattach(refs, log, again.Enqueue);
        reattached.Should().NotBeNull();
        await WaitUntilAsync(() => again.Contains("started"), "輸出檔從頭讀起：app 不在時寫的也看得到");
        reattached!.Processes().Select(r => r.Pid).Should().Contain(refs.Select(r => r.Pid));

        reattached.Kill();
        await reattached.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => AllGone(refs), "接回後停止：cmd 與 ping 都結束", 5000);
    }

    [Fact]
    public async Task 接回後_暫停與繼續_主程序結束時Exited完成()
    {
        var log = NewLog();
        var process = DetachedLaunchProcess.Start(Shell("for /l %i in (1,1,6) do @(echo tick %i& ping -n 2 127.0.0.1 >nul)"), log, _ => { });
        var refs = await ProcessesAsync(process, 2);
        process.Dispose();

        var lines = new ConcurrentQueue<string>();
        using var reattached = DetachedLaunchProcess.Reattach(refs, log, lines.Enqueue)!;
        await WaitUntilAsync(() => lines.Count(l => l.StartsWith("tick", StringComparison.Ordinal)) >= 2, "接著讀到 tick");
        reattached.Suspend().Should().BeTrue();
        await Task.Delay(500);
        var paused = lines.Count;
        await Task.Delay(2200);
        lines.Count.Should().Be(paused, "接回的程序樹也暫停得了");
        reattached.Resume().Should().BeTrue();
        (await reattached.Exited.WaitAsync(TimeSpan.FromSeconds(20))).Should().Be(0);
        lines.Should().Contain("tick 6", "結束前的輸出都讀完");
    }

    [Fact]
    public async Task 建立時間對不上_不接回_都不在了回null()
    {
        using var process = DetachedLaunchProcess.Start(Shell("ping -n 60 127.0.0.1"), NewLog(), _ => { });
        var refs = await ProcessesAsync(process, 1);
        DetachedLaunchProcess.Reattach([refs[0] with { Created = refs[0].Created + 1 }], null, _ => { })
            .Should().BeNull("PID 被別的程序重複使用時不能當成同一個");
        DetachedLaunchProcess.Reattach([new LaunchProcessRef(0, 0), new LaunchProcessRef(int.MaxValue - 3, 1)], null, _ => { }).Should().BeNull();
        process.Kill();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void 找不到執行檔_丟例外_不留輸出檔()
    {
        var log = NewLog();
        var act = () => DetachedLaunchProcess.Start(new LaunchCommand("myriagent-no-such-tool-" + Guid.NewGuid().ToString("N"), [], Path.GetTempPath()), log, _ => { });
        act.Should().Throw<InvalidOperationException>().WithMessage("*找不到或無法執行*");
        File.Exists(log).Should().BeFalse();
    }

    [Fact]
    public async Task 輸出檔超過上限就清空_輸出照樣依序讀到()
    {
        var log = NewLog();
        var lines = new ConcurrentQueue<string>();
        // 每秒一批 40 行（約 1.8 KB），上限 1 KB：每批讀完就清空，程式接著從開頭寫
        using var process = DetachedLaunchProcess.Start(
            Shell("for /l %i in (1,1,4) do @((for /l %j in (1,1,40) do @echo line %i-%j padding-padding-padding)& ping -n 2 127.0.0.1 >nul)"),
            log, lines.Enqueue, maxLogBytes: 1024);
        (await process.Exited.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0);
        var expected = Enumerable.Range(1, 4).SelectMany(i => Enumerable.Range(1, 40).Select(j => $"line {i}-{j} padding-padding-padding")).ToList();
        lines.Where(l => l.StartsWith("line ", StringComparison.Ordinal)).Should().Equal(expected);
        LiveLength(log).Should().BeLessThan(1024 + 2048, "前面幾批讀完就清掉了");
    }
}
