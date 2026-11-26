using System.ComponentModel;
using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Host;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 代理主機（設計 docs/plans/2026-11-26-agent-host.md 的 H0）：真的主機 exe（建置輸出裡的 Myriagent.Host.exe）開真的程序（cmd、findstr、ping），
/// 經主機開的 AgentProcess 行為和直接開的一樣——逐行輸出、stdin、結束碼、環境變數、結束整棵樹；主機用不了時退回直接開；
/// app 這一側的連線關了，主機與它開的程序都結束（H0：行為同 CLI 直接由 app 開）
/// </summary>
public sealed class AgentHostClientTests
{
    private static readonly AgentLauncher Cmd = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>());

    private static string HostExecutable => Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName);

    private static AgentHostClient NewClient(TempDirectory temp) => new(HostExecutable, temp.Root, temp.Combine("host.log"));

    private static async Task<(List<string> Out, List<string> Err, int? Code)> RunAsync(AgentProcess process, Func<Task>? interact = null)
    {
        var lines = new List<string>();
        var errors = new List<string>();
        process.LineReceived += (_, line) => { lock (lines) lines.Add(line); };
        process.StderrReceived += (_, line) => { lock (errors) errors.Add(line); };
        process.BeginReading();
        if (interact != null)
            await interact();
        var code = await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(20));
        // 結束是 stdout 讀完就回報（同直接開的程序）；stderr 的那條執行緒可能還有最後幾行沒交出來
        await Task.Delay(200);
        return (lines, errors, code);
    }

    [Fact]
    public void 主機的執行檔跟著建置輸出()
    {
        File.Exists(HostExecutable).Should().BeTrue("桌面專案把 Myriagent.Host.exe 帶進輸出，測試專案跟著有");
    }

    [Fact]
    public async Task 經主機開的程序_逐行輸出與錯誤輸出_結束碼_算是app的CLI程序()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "echo 第一行& echo 第二行& echo 錯誤 1>&2& exit /b 7" }, temp.Root, null, host: client);

        process.Hosted.Should().BeTrue();
        process.ProcessId.Should().BePositive();
        ChildProcessJob.Contains(process.ProcessId).Should().BeTrue("主機的工作物件也算：查「是不是 app 開的 CLI」的地方照樣認得它");
        ChildProcessJob.GetProcessIds().Should().Contain(process.ProcessId);

        var (lines, errors, code) = await RunAsync(process);

        // cmd 的 echo 以主控台字碼頁輸出：這裡只看 ASCII 以外的字不會讓行數錯掉，內容用 stdin 的測試驗 UTF-8
        lines.Should().HaveCount(2);
        errors.Should().ContainSingle();
        code.Should().Be(7);
        process.HasExited.Should().BeTrue();
        client.HostProcessId.Should().NotBeNull("主機還在：下一個程序沿用它");
    }

    [Fact]
    public async Task 經主機開的程序_stdin轉進去_UTF8不變_關閉stdin後正常結束()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        // findstr 把 stdin 裡含 x 的行原樣印到 stdout
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "findstr", "x" }, temp.Root, null, host: client);

        var (lines, _, code) = await RunAsync(process, async () =>
        {
            await process.WriteLineAsync("xyz");
            await process.WriteLineAsync("中文x 與 emoji 🙂x");
            await process.WriteLineAsync("沒有那個字母");
            await process.CloseAsync(TimeSpan.FromSeconds(10));
        });

        lines.Should().Equal("xyz", "中文x 與 emoji 🙂x");
        code.Should().Be(0);
        // 管線位元組照樣算（對話框的資料流燈）：stdin 三行＋stdout 兩行
        process.PipeBytes.Should().BeGreaterThan(30);
    }

    [Fact]
    public async Task 經主機開的程序_環境變數就是app算好的那一份_大小寫與值原樣()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        Environment.SetEnvironmentVariable("HOSTTEST_REMOVED", "should-not-pass");
        try
        {
            using var process = AgentProcess.Start(Cmd, new[] { "/c", "set HOSTTEST_" }, temp.Root, env =>
            {
                env["HOSTTEST_Mixed"] = "a b \"c\" %PATH%";
                env.Remove("HOSTTEST_REMOVED");
            }, host: client);

            var (lines, _, _) = await RunAsync(process);

            lines.Should().Equal("HOSTTEST_Mixed=a b \"c\" %PATH%");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOSTTEST_REMOVED", null);
        }
    }

    [Fact]
    public async Task 經主機開的程序_Kill結束整棵樹_主機照常回報結束()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "ping", "-n", "60", "127.0.0.1" }, temp.Root, null, host: client);
        process.BeginReading();
        // 等 ping 起來（cmd 的子程序）
        var tree = process.CaptureTree();
        for (int i = 0; i < 50 && tree.Processes.Count < 2; i++)
        {
            await Task.Delay(100);
            tree = process.CaptureTree();
        }
        tree.Processes.Count.Should().BeGreaterThanOrEqualTo(2, "cmd 與它開的 ping");

        (await process.KillTreeAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(10));

        tree.GetAlive().Should().BeEmpty();
        await FluentActions.Awaiting(() => process.WriteLineAsync("x")).Should().ThrowAsync<IOException>("程序已結束");
    }

    [Fact]
    public async Task 程序自己關了stdin_寫入馬上失敗_同直接寫已關閉的管線()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        // 把 stdin 接到 NUL：cmd 自己不讀 stdin，等於那一端關了；ping 讓程序活著
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "ping -n 30 127.0.0.1 <nul >nul & exit /b 0" }, temp.Root, null, host: client);
        process.BeginReading();
        // 管線的緩衝區裝得下的小寫入照樣成功（同直接開）；大到裝不下、對方又不讀的寫入要等到程序結束才有結果，不能卡住別的程序
        using var other = AgentProcess.Start(Cmd, new[] { "/c", "findstr", "x" }, temp.Root, null, host: client);
        var lines = new List<string>();
        other.LineReceived += (_, line) => { lock (lines) lines.Add(line); };
        other.BeginReading();
        var big = process.WriteLineAsync(new string('長', 300_000));

        await other.WriteLineAsync("x 沒被卡住");
        await other.CloseAsync(TimeSpan.FromSeconds(10));
        await other.ExitTask.WaitAsync(TimeSpan.FromSeconds(10));
        lock (lines)
            lines.Should().Equal("x 沒被卡住");

        process.Kill();
        await FluentActions.Awaiting(() => big).Should().ThrowAsync<IOException>("程序結束了，那次寫入沒有寫完");
        await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task 找不到執行檔_同直接開的例外_帶Win32錯誤碼_算暫時失敗()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        var missing = new AgentLauncher(temp.Combine("沒有這個.exe"), Array.Empty<string>());

        var act = () => AgentProcess.Start(missing, Array.Empty<string>(), temp.Root, null, host: client);

        var error = act.Should().Throw<Win32Exception>().Which;
        error.NativeErrorCode.Should().Be(2);
        AgentProcess.IsTransientStartFailure(error).Should().BeTrue("CLI 換檔的空窗照樣重試");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 主機用不了_退回直接開程序()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = new AgentHostClient(temp.Combine("沒有主機.exe"), temp.Root);
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "echo ok" }, temp.Root, null, host: client);

        process.Hosted.Should().BeFalse("主機的執行檔不在：對話照樣開得起來");
        var (lines, _, code) = await RunAsync(process);
        lines.Should().Equal("ok");
        code.Should().Be(0);
    }

    [Fact]
    public async Task 不跟著app結束的程序_不經主機()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "echo ok" }, temp.Root, null, joinJob: false, host: client);

        process.Hosted.Should().BeFalse("joinJob＝false 的（CLI 代查用量）照舊直接開");
        client.HostProcessId.Should().BeNull("沒有程序要它開，主機不必啟動");
        await RunAsync(process);
    }

    [Fact]
    public async Task 連線關了_主機與它開的程序都結束_之後再開會啟動新的主機()
    {
        using var temp = new TempDirectory("agent-host");
        var client = NewClient(temp);
        var process = AgentProcess.Start(Cmd, new[] { "/c", "ping", "-n", "60", "127.0.0.1" }, temp.Root, null, host: client);
        process.BeginReading();
        int hostPid = client.HostProcessId!.Value;
        int childPid = process.ProcessId;

        client.Dispose();

        await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitGoneAsync(hostPid);
        await WaitGoneAsync(childPid);
        process.Dispose();

        using var second = NewClient(temp);
        using var again = AgentProcess.Start(Cmd, new[] { "/c", "echo again" }, temp.Root, null, host: second);
        again.Hosted.Should().BeTrue();
        second.HostProcessId.Should().NotBe(hostPid);
        var (lines, _, _) = await RunAsync(again);
        lines.Should().Equal("again");
    }

    [Fact]
    public async Task 主機當掉_程序當成結束_下一次開程序換一個新的主機()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "ping", "-n", "60", "127.0.0.1" }, temp.Root, null, host: client);
        process.BeginReading();
        int hostPid = client.HostProcessId!.Value;
        int childPid = process.ProcessId;

        using (var host = Process.GetProcessById(hostPid))
            host.Kill();

        (await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull("主機不在了，結束碼不明");
        await WaitGoneAsync(childPid);   // 主機的工作物件關閉時系統結束它開的程序

        using var again = AgentProcess.Start(Cmd, new[] { "/c", "echo back" }, temp.Root, null, host: client);
        again.Hosted.Should().BeTrue("連線斷了，下一次重新啟動主機");
        client.HostProcessId.Should().NotBe(hostPid);
        var (lines, _, _) = await RunAsync(again);
        lines.Should().Equal("back");
    }

    [Fact]
    public async Task 很多輸出_順序與內容不變()
    {
        using var temp = new TempDirectory("agent-host");
        using var client = NewClient(temp);
        // 兩個程序同時各印 3000 行：框交錯送來，各自的順序不能亂
        AgentProcess Start() => AgentProcess.Start(Cmd, new[] { "/c", "for /l %i in (1,1,3000) do @echo line-%i-0123456789012345678901234567890123456789" }, temp.Root, null, host: client);
        using var a = Start();
        using var b = Start();

        var results = await Task.WhenAll(RunAsync(a), RunAsync(b));

        foreach (var (lines, _, code) in results)
        {
            code.Should().Be(0);
            lines.Should().HaveCount(3000);
            lines[0].Should().Be("line-1-0123456789012345678901234567890123456789");
            lines[2999].Should().Be("line-3000-0123456789012345678901234567890123456789");
            lines.Select((line, index) => line == $"line-{index + 1}-0123456789012345678901234567890123456789").Should().OnlyContain(ok => ok);
        }
    }

    [Theory]
    [InlineData(new[] { "a", "b c", "" }, "a \"b c\" \"\"")]
    [InlineData(new[] { "say \"hi\"" }, "\"say \\\"hi\\\"\"")]
    [InlineData(new[] { @"C:\dir with space\" }, "\"C:\\dir with space\\\\\"")]
    [InlineData(new[] { @"C:\plain\path\" }, @"C:\plain\path\")]
    [InlineData(new[] { "a\\\"b" }, "\"a\\\\\\\"b\"")]
    [InlineData(new[] { "--flag=值 有空白" }, "\"--flag=值 有空白\"")]
    public void 命令列的組法同Windows的規則(string[] arguments, string expected)
    {
        HostProtocol.BuildCommandLine(arguments).Should().Be(expected);
    }

    [Fact]
    public void 框與本文的編解碼來回不變()
    {
        var request = new HostSpawnRequest { RequestId = 42, FileName = @"C:\x\claude.exe", WorkingDirectory = @"C:\工作 資料夾" };
        request.Arguments.AddRange(new[] { "-p", "中文 參數", "" });
        request.Environment.Add(new("PATH", @"C:\a;C:\b"));
        request.Environment.Add(new("空的", ""));
        var decoded = HostProtocol.DecodeSpawn(HostProtocol.EncodeSpawn(request));
        decoded.RequestId.Should().Be(42);
        decoded.FileName.Should().Be(request.FileName);
        decoded.WorkingDirectory.Should().Be(request.WorkingDirectory);
        decoded.Arguments.Should().Equal(request.Arguments);
        decoded.Environment.Should().Equal(request.Environment);

        var result = HostProtocol.DecodeSpawnResult(HostProtocol.EncodeSpawnResult(new HostSpawnResult { RequestId = 7, Ok = false, Error = "找不到", NativeError = 2 }));
        (result.RequestId, result.Ok, result.Error, result.NativeError).Should().Be((7, false, "找不到", 2));

        var data = new byte[] { 1, 2, 3, 0, 255 };
        var output = HostProtocol.DecodeOutput(HostProtocol.EncodeOutput(9, HostProtocol.StdErr, 5_000_000_000L, data, data.Length));
        (output.Handle, output.Stream, output.Offset).Should().Be((9, HostProtocol.StdErr, 5_000_000_000L));
        output.Data.Should().Equal(data);

        HostProtocol.DecodeExited(HostProtocol.EncodeExited(3, -1073741510), out var handle, out var code);
        (handle, code).Should().Be((3, -1073741510));
        HostProtocol.DecodeExited(HostProtocol.EncodeExited(3, null), out _, out code);
        code.Should().BeNull();

        using var stream = new MemoryStream();
        var frame = HostProtocol.Frame(HostProtocol.Write, HostProtocol.EncodeWrite(5, 77, data));
        stream.Write(frame);
        stream.Write(HostProtocol.Frame(HostProtocol.Shutdown, null));
        stream.Position = 0;
        HostProtocol.ReadFrame(stream, out var type, out var payload).Should().BeTrue();
        type.Should().Be(HostProtocol.Write);
        HostProtocol.DecodeWrite(payload, out handle, out var sequence, out var written);
        (handle, sequence).Should().Be((5, 77));
        written.Should().Equal(data);
        HostProtocol.DecodeWriteResult(HostProtocol.EncodeWriteResult(5, 77, true), out handle, out sequence, out var wrote);
        (handle, sequence, wrote).Should().Be((5, 77, true));
        HostProtocol.ReadFrame(stream, out type, out payload).Should().BeTrue();
        (type, payload.Length).Should().Be((HostProtocol.Shutdown, 0));
        HostProtocol.ReadFrame(stream, out _, out _).Should().BeFalse("讀完了");

        HostProtocol.PipeName(@"C:\Data\Root\").Should().Be(HostProtocol.PipeName(@"c:\data\root"), "同一個資料夾算出同一個名稱");
        HostProtocol.PipeName(@"C:\Data\Other").Should().NotBe(HostProtocol.PipeName(@"C:\Data\Root"));
    }

    private static async Task WaitGoneAsync(int processId)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"程序 {processId} 沒有結束");
    }
}
