using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Host;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 代理主機的 H1（設計 docs/plans/2026-11-26-agent-host.md 3.1、3.3）：app 放開程序、交棒之後關掉連線，主機與程序都還在；
/// 新的用戶端（等於新版的 app）找到那個主機、從記下的位移接回——放開前後的輸出不重複、不遺漏，半行不會被切開，stdin 照樣寫得進去。
/// 沒交棒就斷線、交棒逾時沒人接、暫存不夠的都照舊結束程序。真的主機 exe 與真的程序（cmd、findstr、ping）
/// </summary>
public sealed class AgentHostHandoffTests
{
    private static readonly AgentLauncher Cmd = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>());

    private static string HostExecutable => Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName);

    /// <summary>把 stdin 的每一行立刻原樣印到 stdout、stdin 關了就結束的程序（只用 ASCII：PowerShell 的主控台字碼頁不是 UTF-8）</summary>
    private static readonly AgentLauncher Echo = new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), Array.Empty<string>());

    private static readonly string[] EchoArguments =
    {
        "-NoProfile", "-NonInteractive", "-Command",
        "while (($line = [Console]::In.ReadLine()) -ne $null) { [Console]::Out.WriteLine($line) }",
    };

    private static AgentHostClient NewClient(TempDirectory temp) => new(HostExecutable, temp.Root, temp.Combine("host.log"));

    private sealed class Lines
    {
        private readonly List<string> lines = new();

        public Lines(AgentProcess process)
        {
            process.LineReceived += (_, line) => { lock (lines) lines.Add(line); };
            process.Exited += (_, code) => { Exited = true; ExitCode = code; };
        }

        public bool Exited { get; private set; }

        public int? ExitCode { get; private set; }

        public List<string> Snapshot()
        {
            lock (lines)
                return lines.ToList();
        }

        public async Task<List<string>> WaitAsync(int count, int seconds = 15)
        {
            for (int i = 0; i < seconds * 20; i++)
            {
                var now = Snapshot();
                if (now.Count >= count)
                    return now;
                await Task.Delay(50);
            }
            throw new TimeoutException($"只收到 {Snapshot().Count} 行，要 {count} 行：{string.Join("｜", Snapshot())}");
        }
    }

    [Fact]
    public async Task 放開_交棒_新的用戶端接回_輸出不重複不遺漏_stdin照樣可寫()
    {
        using var temp = new TempDirectory("agent-host-h1");
        var first = NewClient(temp);
        // 把 stdin 的每一行立刻原樣印到 stdout（findstr 接管線時整塊才輸出，不能用）：之後要驗接回後 stdin 還通
        var process = AgentProcess.Start(Echo, EchoArguments, temp.Root, null, host: first);
        var before = new Lines(process);
        process.BeginReading();
        await process.WriteLineAsync("line one");
        await process.WriteLineAsync("line two");
        (await before.WaitAsync(2, seconds: 30)).Should().Equal("line one", "line two");
        int hostPid = first.HostProcessId!.Value;
        int childPid = process.ProcessId;
        string instance = process.InstanceId;
        process.CanDetach.Should().BeTrue();

        var state = await process.DetachAsync(TimeSpan.FromSeconds(10));

        state.Should().NotBeNull();
        state!.ProcessId.Should().Be(childPid);
        state.InstanceId.Should().Be(instance);
        state.OutOffset.Should().BePositive();
        before.Exited.Should().BeFalse("放開不是程序結束");
        await FluentActions.Awaiting(() => process.WriteLineAsync("x")).Should().ThrowAsync<IOException>("放開之後這個物件不能再用");
        first.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        process.Dispose();
        await Task.Delay(300);
        IsAlive(hostPid).Should().BeTrue("交棒：主機留著");
        IsAlive(childPid).Should().BeTrue("程序沒有被結束");

        // 新的 app：找到同一個主機，接回同一個程序
        using var second = NewClient(temp);
        second.TryAdopt().Should().BeTrue();
        second.HostProcessId.Should().Be(hostPid, "沿用上一個 app 留下的主機，不開第二個");
        second.ListDetached().Should().ContainSingle(p => p.ProcessId == childPid && !p.Exited);
        using var attached = AgentProcess.Attach(second, state);
        var after = new Lines(attached);
        attached.InstanceId.Should().Be(instance);
        attached.ProcessId.Should().Be(childPid);
        attached.BeginReading();
        await attached.WriteLineAsync("after attach");
        (await after.WaitAsync(1)).Should().Equal(new[] { "after attach" }, "放開之前處理過的不會再來一次");
        ChildProcessJob.Contains(childPid).Should().BeTrue();

        await attached.CloseAsync(TimeSpan.FromSeconds(10));
        (await attached.ExitTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(0);
    }

    [Fact]
    public async Task 沒有app接著的期間的輸出_接回時照順序補上_半行不被切開_程序在那段期間結束也補報()
    {
        using var temp = new TempDirectory("agent-host-h1");
        var first = NewClient(temp);
        // 每秒印一行、共 6 行後以結束碼 5 結束；最後一行沒有換行（set /p 不印換行）
        var script = "(for /l %i in (1,1,6) do @(echo line-%i& ping -n 2 127.0.0.1 >nul))& <nul set /p =tail-without-newline& exit /b 5";
        var process = AgentProcess.Start(Cmd, new[] { "/c", script }, temp.Root, null, host: first);
        var before = new Lines(process);
        process.BeginReading();
        var seen = await before.WaitAsync(2);

        var state = await process.DetachAsync(TimeSpan.FromSeconds(10));
        state.Should().NotBeNull();
        seen = before.Snapshot();
        first.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        process.Dispose();
        // 沒有 app 接著的這幾秒程序照常印、然後結束
        await WaitGoneAsync(state!.ProcessId);

        using var second = NewClient(temp);
        second.ListDetached().Should().ContainSingle(p => p.ProcessId == state.ProcessId && p.Exited && p.ExitCode == 5);
        using var attached = AgentProcess.Attach(second, state);
        var after = new Lines(attached);
        attached.BeginReading();
        (await attached.ExitTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(5, "那段期間結束的，接回後補報結束碼");
        await Task.Delay(100);

        var all = seen.Concat(after.Snapshot()).ToList();
        all.Should().Equal("line-1", "line-2", "line-3", "line-4", "line-5", "line-6", "tail-without-newline");
        second.ListDetached().Should().BeEmpty("結束已經回報，主機不再留著它");
    }

    [Fact]
    public async Task 放開時行寫到一半_新的用戶端從整行的邊界重讀()
    {
        using var temp = new TempDirectory("agent-host-h1");
        var first = NewClient(temp);
        // 先印沒有換行的半行，等 stdin 的一行之後才把它補完
        var process = AgentProcess.Start(Cmd, new[] { "/c", "echo whole& <nul set /p =half-& set /p x=& echo done" }, temp.Root, null, host: first);
        var before = new Lines(process);
        process.BeginReading();
        (await before.WaitAsync(1)).Should().Equal("whole");
        await Task.Delay(500);   // 半行已經到了，但沒有換行、還沒交出去

        var state = await process.DetachAsync(TimeSpan.FromSeconds(10));
        state.Should().NotBeNull();
        before.Snapshot().Should().Equal(new[] { "whole" }, "半行不交出");
        first.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        process.Dispose();

        using var second = NewClient(temp);
        using var attached = AgentProcess.Attach(second, state!);
        var after = new Lines(attached);
        attached.BeginReading();
        await attached.WriteLineAsync("go");
        (await attached.ExitTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(0);
        await Task.Delay(100);
        after.Snapshot().Should().Equal(new[] { "half-done" }, "半行和後半接成完整的一行，只出現一次");
    }

    [Fact]
    public async Task 沒交棒就斷線_照舊結束全部程序()
    {
        using var temp = new TempDirectory("agent-host-h1");
        var first = NewClient(temp);
        var process = AgentProcess.Start(Cmd, new[] { "/c", "ping", "-n", "60", "127.0.0.1" }, temp.Root, null, host: first);
        process.BeginReading();
        int hostPid = first.HostProcessId!.Value;
        int childPid = process.ProcessId;
        (await process.DetachAsync(TimeSpan.FromSeconds(10))).Should().NotBeNull();

        first.Dispose();   // 一般的結束：請主機結束

        await WaitGoneAsync(hostPid);
        await WaitGoneAsync(childPid);
        process.Dispose();
        using var second = NewClient(temp);
        second.TryAdopt().Should().BeFalse("主機不在了");
    }

    [Fact]
    public async Task 交棒之後沒有人接_時間到主機結束程序與自己()
    {
        using var temp = new TempDirectory("agent-host-h1");
        var first = NewClient(temp);
        var process = AgentProcess.Start(Cmd, new[] { "/c", "ping", "-n", "60", "127.0.0.1" }, temp.Root, null, host: first);
        process.BeginReading();
        int hostPid = first.HostProcessId!.Value;
        int childPid = process.ProcessId;
        (await process.DetachAsync(TimeSpan.FromSeconds(10))).Should().NotBeNull();

        first.Handoff(TimeSpan.FromSeconds(2)).Should().BeTrue();
        process.Dispose();

        await WaitGoneAsync(hostPid);
        await WaitGoneAsync(childPid);
    }

    [Fact]
    public async Task 沒放開的程序在交棒時當成結束_新的用戶端可以照PID收掉留下的()
    {
        using var temp = new TempDirectory("agent-host-h1");
        var first = NewClient(temp);
        var kept = AgentProcess.Start(Cmd, new[] { "/c", "ping", "-n", "60", "127.0.0.1" }, temp.Root, null, host: first);
        var keptLines = new Lines(kept);
        kept.BeginReading();
        int childPid = kept.ProcessId;

        first.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();

        // 這個 app 沒有放開它就交棒了：對這個 app 來說程序結束了（結束碼不明），主機那邊它其實還在
        (await kept.ExitTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();
        kept.Dispose();
        using var second = NewClient(temp);
        var left = second.ListDetached();
        left.Should().ContainSingle(p => p.ProcessId == childPid);
        // 新的 app 不認得它（沒有放開的狀態）：照 PID 結束整棵樹
        using (var orphan = Process.GetProcessById(childPid))
            orphan.Kill(entireProcessTree: true);
        await WaitGoneAsync(childPid);
        keptLines.Exited.Should().BeTrue();
    }

    [Fact]
    public async Task 接不回去的情況_位移不對_程序不在_都丟主機用不了()
    {
        using var temp = new TempDirectory("agent-host-h1");
        var first = NewClient(temp);
        var process = AgentProcess.Start(Cmd, new[] { "/c", "echo a& echo b& ping -n 60 127.0.0.1 >nul" }, temp.Root, null, host: first);
        var lines = new Lines(process);
        process.BeginReading();
        await lines.WaitAsync(2);
        var state = (await process.DetachAsync(TimeSpan.FromSeconds(10)))!;
        first.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        process.Dispose();

        using var second = NewClient(temp);
        FluentActions.Invoking(() => AgentProcess.Attach(second, state with { OutOffset = state.OutOffset + 1000 }))
            .Should().Throw<AgentHostUnavailableException>("位移超過主機讀到的");
        FluentActions.Invoking(() => AgentProcess.Attach(second, state with { Handle = state.Handle + 99 }))
            .Should().Throw<AgentHostUnavailableException>("沒有這個程序");
        // 正確的照樣接得回
        using var attached = AgentProcess.Attach(second, state);
        attached.BeginReading();
        attached.Kill();
        await attached.ExitTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task 直接開的程序放不開()
    {
        // 整批走主機的模式（HostedProcessMode）下沒指定主機的程序也經主機開：這項只在平常的模式有意義
        if (HostedProcessMode.Enabled)
            return;
        using var temp = new TempDirectory("agent-host-h1");
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "findstr", "x" }, temp.Root, null);
        process.BeginReading();
        process.CanDetach.Should().BeFalse();
        (await process.DetachAsync(TimeSpan.FromSeconds(2))).Should().BeNull();
        await process.WriteLineAsync("x 還能用");
        await process.CloseAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void 逐行讀取_記下處理到的位移_放開時半行不交出_結束時交出()
    {
        var reader = new HostedLineReader(startOffset: 100);
        reader.Add("ab\r\nc"u8.ToArray());
        reader.Add("d\n半"u8.ToArray());
        reader.ReadLine().Should().Be("ab");
        reader.ConsumedOffset.Should().Be(104);
        reader.ReadLine().Should().Be("cd");
        reader.ConsumedOffset.Should().Be(107);
        reader.CompleteDetached();
        reader.ReadLine().Should().BeNull("半行留在主機那裡");
        reader.ConsumedOffset.Should().Be(107);

        var ended = new HostedLineReader();
        ended.Add("x\n尾"u8.ToArray());
        ended.Complete();
        ended.ReadLine().Should().Be("x");
        ended.ReadLine().Should().Be("尾", "程序結束時最後沒有換行的半行也交出");
        ended.ReadLine().Should().BeNull();
        ended.ConsumedOffset.Should().Be(2 + 3);

        // 多位元組的字被切在兩段中間也不會壞
        var split = new HostedLineReader();
        var bytes = "中文\n"u8.ToArray();
        split.Add(bytes[..2]);
        split.Add(bytes[2..]);
        split.ReadLine().Should().Be("中文");
    }

    [Fact]
    public void 第2版的本文編解碼來回不變()
    {
        HostProtocol.DecodeOffsets(HostProtocol.EncodeOffsets(7, 5_000_000_000L, 12), out var handle, out var outOffset, out var errOffset);
        (handle, outOffset, errOffset).Should().Be((7, 5_000_000_000L, 12L));
        HostProtocol.DecodeAttach(HostProtocol.EncodeAttach(3, 7, 1, 2), out var request, out handle, out outOffset, out errOffset);
        (request, handle, outOffset, errOffset).Should().Be((3, 7, 1L, 2L));
        HostProtocol.DecodeAttachResult(HostProtocol.EncodeAttachResult(3, false, "不行"), out request, out var ok, out var error);
        (request, ok, error).Should().Be((3, false, "不行"));
        HostProtocol.DecodeAttachResult(HostProtocol.EncodeAttachResult(4, true, null), out request, out ok, out error);
        (request, ok, error).Should().Be((4, true, null));
        var list = HostProtocol.DecodeListResult(HostProtocol.EncodeListResult(9, new List<HostProcessInfo>
        {
            new() { Handle = 1, ProcessId = 100, Exited = true, ExitCode = -5, OutEnd = 10, ErrEnd = 20, Overflow = true, Attached = false },
            new() { Handle = 2, ProcessId = 200, Attached = true },
        }), out request);
        request.Should().Be(9);
        list.Select(p => (p.Handle, p.ProcessId, p.Exited, p.ExitCode, p.OutEnd, p.ErrEnd, p.Overflow, p.Attached)).Should().Equal(
            (1, 100, true, (int?)-5, 10L, 20L, true, false), (2, 200, false, (int?)null, 0L, 0L, false, true));
        HostProtocol.DecodeHandoff(HostProtocol.EncodeHandoff(600)).Should().Be(600);
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitGoneAsync(int processId)
    {
        for (int i = 0; i < 300; i++)
        {
            if (!IsAlive(processId))
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"程序 {processId} 沒有結束");
    }
}
