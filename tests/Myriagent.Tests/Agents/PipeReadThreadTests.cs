using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Launch;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 讀子程序輸出不佔執行緒集區（PipeReadThread）：代理 CLI 程序與執行列的程式，輸出、錯誤輸出與結束事件都在專用的背景執行緒上收到。
/// 以前 ReadLineAsync／Task.Run 裡的同步讀在程序的整個生命期各佔一條集區執行緒，十幾個閒置的 CLI 程序就把集區佔滿，
/// 送出的訊息要好幾秒才上畫面（2026-10-23）
/// </summary>
public sealed class PipeReadThreadTests
{
    private static readonly AgentLauncher Cmd = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>());

    private sealed record ThreadInfo(bool Pool, bool Background, string? Name);

    private static ThreadInfo Current() => new(Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.IsBackground, Thread.CurrentThread.Name);

    [Fact]
    public async Task 代理程序的輸出_錯誤輸出與結束事件都在專用的背景執行緒上_不在執行緒集區()
    {
        var stdout = new TaskCompletionSource<ThreadInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new TaskCompletionSource<ThreadInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<ThreadInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = AgentProcess.Start(Cmd, new[] { "/d", "/c", "echo", "hello&", "echo", "oops", "1>&2" }, Path.GetTempPath(), null);
        process.LineReceived += (_, line) =>
        {
            if (line.Contains("hello", StringComparison.Ordinal))
                stdout.TrySetResult(Current());
        };
        process.StderrReceived += (_, line) =>
        {
            if (line.Contains("oops", StringComparison.Ordinal))
                stderr.TrySetResult(Current());
        };
        process.Exited += (_, _) => exited.TrySetResult(Current());
        process.BeginReading();

        var timeout = TimeSpan.FromSeconds(20);
        var outInfo = await stdout.Task.WaitAsync(timeout);
        var errInfo = await stderr.Task.WaitAsync(timeout);
        var exitInfo = await exited.Task.WaitAsync(timeout);
        (await process.ExitTask.WaitAsync(timeout)).Should().Be(0);

        outInfo.Should().Be(new ThreadInfo(false, true, $"agent {process.ProcessId} stdout"));
        errInfo.Should().Be(new ThreadInfo(false, true, $"agent {process.ProcessId} stderr"));
        exitInfo.Should().Be(new ThreadInfo(false, true, $"agent {process.ProcessId} stdout"), "結束事件在 stdout 讀完之後、同一條執行緒上觸發");
    }

    [Fact]
    public async Task 執行列的程式的輸出在專用的背景執行緒上_不在執行緒集區()
    {
        var infos = new ConcurrentQueue<ThreadInfo>();
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        using var process = JobLaunchProcess.Start(new LaunchCommand(cmd, ["/d", "/s", "/c", "echo out& echo err 1>&2"], Path.GetTempPath()),
            _ => infos.Enqueue(Current()));
        (await process.Exited.WaitAsync(TimeSpan.FromSeconds(20))).Should().Be(0);

        infos.Should().HaveCount(2);
        infos.Should().OnlyContain(i => !i.Pool && i.Background && i.Name != null && i.Name.StartsWith("launch ", StringComparison.Ordinal));
        infos.Select(i => i.Name).Should().OnlyHaveUniqueItems("stdout 與 stderr 各一條");
    }

    [Fact]
    public async Task 執行緒上沒接住的例外被記錄_工作照樣完成_程序不會當掉()
    {
        var task = PipeReadThread.Start("test throws", () => throw new InvalidOperationException("測試用的例外"));
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task 回傳的工作在讀取結束之後才完成()
    {
        var gate = new ManualResetEventSlim();
        var task = PipeReadThread.Start("test waits", () => gate.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(100);
        task.IsCompleted.Should().BeFalse("還在讀");
        gate.Set();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
