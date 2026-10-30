using System.Collections.Concurrent;
using System.Diagnostics;
using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;

namespace Myriagent.Tests.RemoteExec;

/// <summary>
/// 被操作的電腦執行遠端指令（真的開 cmd、Windows PowerShell 與 ping；不碰網路、不跳視窗）：輸出與結束碼、UTF-8、逾時與停止結束整棵樹、
/// 高風險的先問（假的確認者）、app 以系統管理員執行時不收
/// </summary>
public sealed class RemoteExecRunnerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly string scripts = Path.Combine(Path.GetTempPath(), "myriagent-rexec-test-" + Guid.NewGuid().ToString("N"));

    private readonly ConcurrentQueue<ExecChunk> sent = new();

    private readonly FakeConfirmer confirmer = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(scripts, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private RemoteExecRunner NewRunner(bool elevated = false) => new(sent.Enqueue, confirmer, scripts, () => elevated);

    [Fact]
    public async Task cmd的輸出與結束碼()
    {
        using var runner = NewRunner();
        var request = new RemoteExecRequest(RemoteExecRules.Cmd, "echo hello-remote& echo oops 1>&2& exit /b 3", null, 30, null);
        runner.Check(request).Should().BeNull();
        runner.Start(Cid(1), request, "電腦「B」");
        var chunks = await ExitOfAsync(Cid(1));
        Out(chunks).Should().Contain("hello-remote");
        Err(chunks).Should().Contain("oops");
        chunks.Select(c => c.Seq).Should().Equal(Enumerable.Range(0, chunks.Count).Select(i => (long)i), "seq 從 0 起連續");
        chunks.First().Should().Match<ExecChunk>(c => c.Kind == RemoteExecRules.KindStatus && c.Code == RemoteExecRules.StatusStarted);
        var exit = chunks.Last();
        exit.ExitCode.Should().Be(3);
        exit.Code.Should().BeNull();
        chunks.Should().OnlyContain(c => RemoteExecRules.IsValidChunk(c));
        runner.RunningCount.Should().Be(0);
        confirmer.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task PowerShell的中文輸出_失敗時回原生程式的結束碼()
    {
        using var runner = NewRunner();
        runner.Start(Cid(2), new RemoteExecRequest(RemoteExecRules.PowerShell, "Write-Output '中文輸出 OK'\ncmd /c exit 7", null, 60, null), "電腦「B」");
        var chunks = await ExitOfAsync(Cid(2));
        Out(chunks).Should().Contain("中文輸出 OK");
        chunks.Last().ExitCode.Should().Be(7);
        Directory.EnumerateFiles(scripts).Should().BeEmpty("暫存腳本執行完就刪");
    }

    [Fact]
    public async Task 工作資料夾()
    {
        var folder = Directory.CreateDirectory(Path.Combine(scripts, "work 資料夾")).FullName;
        using var runner = NewRunner();
        runner.Start(Cid(3), new RemoteExecRequest(RemoteExecRules.Cmd, "cd", folder, 30, null), "電腦「B」");
        var chunks = await ExitOfAsync(Cid(3));
        Out(chunks).Trim().Should().Be(folder);
        chunks.Last().ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task 逾時結束整棵樹()
    {
        using var runner = NewRunner();
        var watch = Stopwatch.StartNew();
        runner.Start(Cid(4), new RemoteExecRequest(RemoteExecRules.Cmd, "ping -n 60 127.0.0.1 >nul", null, 2, null), "電腦「B」");
        var exit = (await ExitOfAsync(Cid(4))).Last();
        exit.Code.Should().Be(RemoteExecRules.ExitTimeout);
        exit.Text.Should().Contain("2 秒");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        runner.RunningCount.Should().Be(0);
    }

    [Fact]
    public async Task 發起的一方要求停止_伺服器不再轉送也停止()
    {
        using var runner = NewRunner();
        runner.Start(Cid(5), new RemoteExecRequest(RemoteExecRules.Cmd, "ping -n 60 127.0.0.1", null, 120, null), "電腦「B」");
        await WaitForAsync(() => sent.Any(c => c.CommandId == Cid(5) && c.Kind == RemoteExecRules.KindStatus && c.Code == RemoteExecRules.StatusStarted));
        runner.RunningCount.Should().Be(1);
        runner.Abort(Cid(5), orphaned: false);
        (await ExitOfAsync(Cid(5))).Last().Code.Should().Be(RemoteExecRules.ExitCancelled);

        runner.Start(Cid(6), new RemoteExecRequest(RemoteExecRules.Cmd, "ping -n 60 127.0.0.1", null, 120, null), "電腦「B」");
        await WaitForAsync(() => sent.Any(c => c.CommandId == Cid(6) && c.Code == RemoteExecRules.StatusStarted));
        runner.Abort(Cid(6), orphaned: true);
        (await ExitOfAsync(Cid(6))).Last().Code.Should().Be(RemoteExecRules.ExitOrphaned);
        runner.Abort("01J9QZ3K8M5R7T2V4W6X8Y0ZZZ", orphaned: false);
    }

    [Fact]
    public async Task 高風險的先問_拒絕就不執行()
    {
        confirmer.Answer = RemoteExecConfirmResult.Denied;
        using var runner = NewRunner();
        var marker = Path.Combine(scripts, "ran.txt");
        Directory.CreateDirectory(scripts);
        // echo 的內容裡有 shutdown：判成高風險，但真的跑了也只是寫一個檔
        runner.Start(Cid(7), new RemoteExecRequest(RemoteExecRules.Cmd, $"echo shutdown > \"{marker}\"", null, 30, "整理"), "電腦「B」");
        var chunks = await ExitOfAsync(Cid(7));
        chunks[0].Should().Match<ExecChunk>(c => c.Kind == RemoteExecRules.KindStatus && c.Code == RemoteExecRules.StatusConfirm);
        chunks[0].Text.Should().Contain(RemoteExecRiskRules.Power);
        var exit = chunks.Last();
        exit.Code.Should().Be(RemoteExecRules.ExitDenied);
        exit.ExitCode.Should().BeNull();
        File.Exists(marker).Should().BeFalse("拒絕了就不執行");
        confirmer.Asked.Should().ContainSingle().Which.Should().Match<RemoteExecConfirmation>(c =>
            c.IssuerLabel == "電腦「B」" && c.Request.Label == "整理" && c.Risk.Category == RemoteExecRiskRules.Power);
    }

    [Fact]
    public async Task 高風險的同意了才執行_時間內沒回應當成拒絕()
    {
        confirmer.Answer = RemoteExecConfirmResult.Allowed;
        using var runner = NewRunner();
        runner.Start(Cid(8), new RemoteExecRequest(RemoteExecRules.Cmd, "echo shutdown-was-only-echoed", null, 30, null), "電腦「B」");
        var allowed = await ExitOfAsync(Cid(8));
        Out(allowed).Should().Contain("shutdown-was-only-echoed");
        allowed.Last().ExitCode.Should().Be(0);

        confirmer.Answer = RemoteExecConfirmResult.TimedOut;
        runner.Start(Cid(9), new RemoteExecRequest(RemoteExecRules.Cmd, "echo shutdown", null, 30, null), "電腦「B」");
        var exit = (await ExitOfAsync(Cid(9))).Last();
        exit.Code.Should().Be(RemoteExecRules.ExitDenied);
        exit.Text.Should().Contain("沒有人");
    }

    [Fact]
    public async Task 等待同意時被要求停止_關掉確認_不執行()
    {
        confirmer.Hold = true;
        using var runner = NewRunner();
        runner.Start(Cid(10), new RemoteExecRequest(RemoteExecRules.Cmd, "echo shutdown", null, 30, null), "電腦「B」");
        await WaitForAsync(() => confirmer.Asked.Count == 1);
        runner.Abort(Cid(10), orphaned: false);
        var exit = (await ExitOfAsync(Cid(10))).Last();
        exit.Code.Should().Be(RemoteExecRules.ExitCancelled);
        confirmer.CancelledCount.Should().Be(1, "確認視窗跟著關掉");
    }

    [Fact]
    public void 執行前的檢查()
    {
        using var elevated = NewRunner(elevated: true);
        elevated.Check(new RemoteExecRequest(RemoteExecRules.Cmd, "dir", null, 30, null))!.Value.Code.Should().Be(RemoteResultCodes.Policy);

        using var runner = NewRunner();
        runner.Check(new RemoteExecRequest(RemoteExecRules.Cmd, "dir", @"C:\沒有這個資料夾-" + Guid.NewGuid().ToString("N"), 30, null))!.Value.Code
            .Should().Be(RemoteResultCodes.Invalid);
        runner.Check(new RemoteExecRequest(RemoteExecRules.Cmd, "dir", @"\\server\share", 30, null))!.Value.Code.Should().Be(RemoteResultCodes.Invalid);
        if (RemoteExecRunner.FindPwsh() is null)
            runner.Check(new RemoteExecRequest(RemoteExecRules.Pwsh, "dir", null, 30, null))!.Value.Message.Should().Contain("pwsh");
    }

    [Fact]
    public async Task app結束時停止還在執行的()
    {
        var runner = NewRunner();
        runner.Start(Cid(11), new RemoteExecRequest(RemoteExecRules.Cmd, "ping -n 60 127.0.0.1", null, 120, null), "電腦「B」");
        await WaitForAsync(() => sent.Any(c => c.CommandId == Cid(11) && c.Code == RemoteExecRules.StatusStarted));
        runner.Dispose();
        var exits = sent.Where(c => c.CommandId == Cid(11) && c.Kind == RemoteExecRules.KindExit).ToList();
        exits.Should().ContainSingle().Which.Code.Should().Be(RemoteExecRules.ExitAppExit);
        runner.Check(new RemoteExecRequest(RemoteExecRules.Cmd, "dir", null, 30, null))!.Value.Code.Should().Be(RemoteResultCodes.Busy);
        await Task.Delay(500);
        sent.Count(c => c.CommandId == Cid(11) && c.Kind == RemoteExecRules.KindExit).Should().Be(1, "結束碼只送一次");
    }

    private async Task<List<ExecChunk>> ExitOfAsync(string commandId)
    {
        await WaitForAsync(() => sent.Any(c => c.CommandId == commandId && c.Kind == RemoteExecRules.KindExit));
        return sent.Where(c => c.CommandId == commandId).OrderBy(c => c.Seq).ToList();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Wait)
                throw new TimeoutException("等不到條件成立");
            await Task.Delay(50);
        }
    }

    private static string Out(IEnumerable<ExecChunk> chunks) => string.Concat(chunks.Where(c => c.Kind == RemoteExecRules.KindOut).Select(c => c.Text));

    private static string Err(IEnumerable<ExecChunk> chunks) => string.Concat(chunks.Where(c => c.Kind == RemoteExecRules.KindErr).Select(c => c.Text));

    private static string Cid(int n) => "01J9QZ3K8M5R7T2V4W6X8Y0" + n.ToString("000");

    private sealed class FakeConfirmer : IRemoteExecConfirmer
    {
        public RemoteExecConfirmResult Answer { get; set; } = RemoteExecConfirmResult.Denied;

        /// <summary>不回答，等取消</summary>
        public bool Hold { get; set; }

        public ConcurrentQueue<RemoteExecConfirmation> AskedQueue { get; } = new();

        public IReadOnlyList<RemoteExecConfirmation> Asked => AskedQueue.ToList();

        public int CancelledCount;

        public Task<RemoteExecConfirmResult> ConfirmAsync(RemoteExecConfirmation confirmation, TimeSpan timeout, CancellationToken cancellationToken)
        {
            AskedQueue.Enqueue(confirmation);
            if (!Hold)
                return Task.FromResult(Answer);
            var done = new TaskCompletionSource<RemoteExecConfirmResult>();
            cancellationToken.Register(() =>
            {
                Interlocked.Increment(ref CancelledCount);
                done.TrySetResult(RemoteExecConfirmResult.Denied);
            });
            return done.Task;
        }
    }
}
