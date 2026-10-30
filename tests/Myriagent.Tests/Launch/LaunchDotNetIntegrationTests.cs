using System.Collections.Concurrent;
using System.Diagnostics;
using Myriagent.Launch;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Launch;

/// <summary>
/// 執行列的端到端（真的 dotnet build／dotnet run，在暫存資料夾建一個不開視窗的 WinExe）：偵測得到、Release 模式建置成功後執行、
/// 暫停時沒有輸出、繼續後恢復、放開（app 結束）後程式照樣執行、以記下的程序接回後讀得到之前的輸出、停止後 dotnet 與程式都不在了；
/// Debug 模式的程式在 app 放開（關掉工作物件）時跟著結束；語法錯誤時建置失敗並抓到第一個錯誤。
/// 約 20 秒、不用額度：<c>MYRIAGENT_LAUNCH_IT=1 dotnet test … --filter "FullyQualifiedName~LaunchDotNetIntegration"</c>
/// </summary>
public sealed class LaunchDotNetIntegrationTests
{
    private const string Reason = "真的執行 dotnet build／dotnet run（約 20 秒，不用額度、不開視窗）";

    private static async Task<(int Code, List<string> Lines)> RunToEndAsync(LaunchCommand command)
    {
        var lines = new ConcurrentQueue<string>();
        using var process = JobLaunchProcess.Start(command, lines.Enqueue);
        var code = await process.Exited.WaitAsync(TimeSpan.FromMinutes(3));
        return (code, lines.ToList());
    }

    /// <summary>檔案目前的大小（開檔問：別的程序還開著寫時，目錄項目記的大小可能還沒更新）</summary>
    private static long LiveLength(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException(because);
            await Task.Delay(100);
        }
    }

    [EnvFact("MYRIAGENT_LAUNCH_IT", Reason)]
    public async Task 建置並執行_暫停_繼續_停止_語法錯誤時建置失敗()
    {
        using var temp = new TempDirectory("launch-it");
        var name = "LaunchProbe" + Guid.NewGuid().ToString("N")[..8];
        temp.WriteFile($@"{name}\{name}.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{name}</AssemblyName>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        var program = temp.WriteFile($@"{name}\Program.cs", """
            Console.WriteLine("中文輸出");
            for (var i = 1; i < 600; i++)
            {
                Console.WriteLine($"tick {i}");
                Console.Out.Flush();
                Thread.Sleep(300);
            }
            """);

        var target = LaunchScanner.Scan(temp.Root, CancellationToken.None).Should().ContainSingle().Subject;
        target.Name.Should().Be(name);
        target.Detail.Should().Be("WinExe · net10.0");

        var build = await RunToEndAsync(LaunchRules.BuildCommand(target, LaunchMode.Release)!);
        build.Code.Should().Be(0, string.Join("\n", build.Lines));
        File.Exists(temp.Combine($@"{name}\bin\Release\net10.0\{name}.dll")).Should().BeTrue("Release 組態的輸出");

        var lines = new ConcurrentQueue<string>();
        var log = temp.Combine(LaunchRules.LogFileName(target.Id, DateTime.UtcNow));
        ILaunchProcess run = DetachedLaunchProcess.Start(LaunchRules.RunCommand(target, null, LaunchMode.Release), log, lines.Enqueue);
        try
        {
            await WaitUntilAsync(() => lines.Contains("tick 3"), "程式開始輸出");
            lines.Should().Contain("中文輸出");
            Process.GetProcessesByName(name).Should().NotBeEmpty("dotnet run 開出的程式");

            run.Suspend().Should().BeTrue();
            await Task.Delay(400);
            var paused = lines.Count;
            await Task.Delay(1500);
            lines.Count.Should().Be(paused, "暫停中 dotnet 與程式都不動");
            run.Resume().Should().BeTrue();
            await WaitUntilAsync(() => lines.Count > paused, "繼續後恢復");

            // app 結束：放開控制代碼，dotnet 與程式照樣執行、照樣寫輸出檔
            IReadOnlyList<LaunchProcessRef> refs = [];
            await WaitUntilAsync(() => (refs = run.Processes()).Count >= 2, "dotnet 與它開的程式都記得");
            run.Dispose();
            var size = LiveLength(log);
            await WaitUntilAsync(() => LiveLength(log) > size, "app 不在時程式照樣輸出");
            Process.GetProcessesByName(name).Should().NotBeEmpty();

            var again = new ConcurrentQueue<string>();
            run = DetachedLaunchProcess.Reattach(refs, log, again.Enqueue)!;
            run.Should().NotBeNull("app 重新啟動後接回");
            await WaitUntilAsync(() => again.Contains("中文輸出") && again.Count > lines.Count, "輸出檔從頭讀起，再接著讀");

            run.Kill();
            await run.Exited.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => Process.GetProcessesByName(name).Length == 0, "停止後程式也結束", 5000);
        }
        finally
        {
            run.Kill();
            run.Dispose();
        }

        // Debug 模式：Debug 組態建置，程式是 app 的子程序；app 結束（關掉工作物件）時 dotnet 與程式都跟著結束
        var debugBuild = await RunToEndAsync(LaunchRules.BuildCommand(target, LaunchMode.Debug)!);
        debugBuild.Code.Should().Be(0, string.Join("\n", debugBuild.Lines));
        File.Exists(temp.Combine($@"{name}\bin\Debug\net10.0\{name}.dll")).Should().BeTrue("Debug 組態的輸出");
        var debugLines = new ConcurrentQueue<string>();
        var attached = JobLaunchProcess.Start(LaunchRules.RunCommand(target, null, LaunchMode.Debug), debugLines.Enqueue, killOnJobClose: true);
        try
        {
            await WaitUntilAsync(() => debugLines.Contains("tick 2"), "Debug 模式的程式開始輸出");
            Process.GetProcessesByName(name).Should().NotBeEmpty();
            attached.Dispose();
            await WaitUntilAsync(() => Process.GetProcessesByName(name).Length == 0, "app 結束時 Debug 模式的程式跟著結束", 5000);
        }
        finally
        {
            attached.Kill();
            attached.Dispose();
        }

        File.WriteAllText(program, "Console.WriteLine(\"x\")\n");   // 少了分號
        var broken = await RunToEndAsync(LaunchRules.BuildCommand(target, LaunchMode.Debug)!);
        broken.Code.Should().NotBe(0);
        var end = LaunchRules.BuildFailed(broken.Code, broken.Lines, DateTime.UtcNow);
        end.Text.Should().StartWith("Program.cs(1,").And.Contain("CS1002");
    }
}
