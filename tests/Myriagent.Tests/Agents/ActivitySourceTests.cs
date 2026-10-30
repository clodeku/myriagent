using Myriagent.Agents;
using Myriagent.Background;
using Myriagent.Hub;
using Myriagent.SystemStatus;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 對話框燈號的真實來源（不是假的）：AgentProcess 的管線位元組（stdout 讀到的＋stdin 寫入的）、ProcessTreeIo 量得到
/// 子程序（cmd 開的 PowerShell）寫檔的位元組與忙著計算時的 CPU 使用率、第一次只取基準、根程序結束後回 0；整台電腦的網路計數器只增不減、
/// 只問記住的網卡和整張表一樣；程序表列得到自己開的子程序
/// </summary>
public sealed class ActivitySourceTests(ITestOutputHelper output)
{
    private static readonly AgentLauncher Cmd = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>());

    [Fact]
    public async Task 管線位元組_stdout讀到的加上stdin寫入的_每行含換行()
    {
        // findstr 把 stdin 裡含 x 的行原樣印到 stdout
        using var process = AgentProcess.Start(Cmd, new[] { "/c", "findstr", "x" }, Path.GetTempPath(), null);
        var lines = new List<string>();
        process.LineReceived += (_, line) => { lock (lines) lines.Add(line); };
        process.BeginReading();
        process.PipeBytes.Should().Be(0);
        process.Activity.Should().NotBeNull();
        process.Activity!.Value.ProcessId.Should().Be(process.ProcessId);
        process.Activity!.Value.Instance.Should().Be(process.InstanceId);

        await process.WriteLineAsync("xyz");
        await process.WriteLineAsync("中文x");
        await process.CloseAsync(TimeSpan.FromSeconds(10));
        await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(10));
        lock (lines)
            lines.Should().Equal("xyz", "中文x");
        // stdin：4＋8（「中文」各 3 位元組＋x＋換行）；stdout 照樣各一次
        process.PipeBytes.Should().Be(2 * (4 + 8));
        process.Activity.Should().BeNull("程序已結束");
    }

    [Fact]
    public async Task 程序樹的IO_量得到子程序寫的檔_第一次只取基準_根程序結束後回0()
    {
        var file = Path.Combine(Path.GetTempPath(), "Myriagent.Tests", "activity-" + Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        const int size = 3_000_000;
        try
        {
            // cmd（根）→ PowerShell（子程序）寫 3MB 的檔，再停 3 秒（第二次取樣時它還活著）
            var script = $"[IO.File]::WriteAllBytes('{file}', (New-Object byte[] {size})); Start-Sleep -Seconds 3";
            using var root = AgentProcess.Start(Cmd, new[] { "/c", "powershell", "-NoProfile", "-NonInteractive", "-Command", script }, Path.GetTempPath(), null);
            root.BeginReading();
            var tree = new ProcessTreeIo();
            var baseline = tree.Sample(root.ProcessId, ProcessTreeSnapshot.ReadProcessTable());
            baseline.Io.Should().Be(0, "第一次只取基準");
            baseline.Cpu.Should().BeNull("第一次只取基準");
            baseline.Mem.Should().BePositive("記憶體不是增量，取基準那次就有");

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!(File.Exists(file) && new FileInfo(file).Length == size) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            new FileInfo(file).Length.Should().Be(size, "PowerShell 寫完了");
            var delta = tree.Sample(root.ProcessId, ProcessTreeSnapshot.ReadProcessTable());
            delta.Io.Should().BeGreaterThanOrEqualTo(size, "子程序寫檔的位元組算進這個對話的程序樹");

            await root.ExitTask.WaitAsync(TimeSpan.FromSeconds(30));
            tree.Sample(root.ProcessId, ProcessTreeSnapshot.ReadProcessTable()).Should().Be(new TreeActivity(0, 0), "根程序結束：程序樹是空的（AgentProcess 還握著它的 handle，已結束的程序不算記憶體）");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// 機器很忙（整套測試一起跑）時 PowerShell 要好幾秒才起來，固定等 0.7 秒、只空轉 3 秒會量到 0（2026-10-27 起連續三次完整測試在同檔的另一個測試失敗；
    /// 以 36 個空轉程序吃滿 CPU 可重現）：一直空轉到測完才收掉，等到程序表看得到它才取基準，之後 <see cref="BusyWait"/> 內量得到 CPU 就算
    /// </summary>
    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(30);

    /// <summary>一直空轉（吃滿一顆核心）直到被收掉的 PowerShell 指令</summary>
    private const string SpinForever = "$x = 0; while ($true) { $x++ }";

    [Fact]
    public async Task 程序樹的CPU_子程序忙著計算時大於0_不超過100()
    {
        // cmd（根）→ PowerShell（子程序）一直空轉：吃滿一顆核心（整台電腦的 1／邏輯處理器數）
        using var root = AgentProcess.Start(Cmd, new[] { "/c", "powershell", "-NoProfile", "-NonInteractive", "-Command", SpinForever }, Path.GetTempPath(), null);
        root.BeginReading();
        try
        {
            var tree = new ProcessTreeIo();
            var deadline = DateTime.UtcNow + BusyWait;
            while (!ProcessTreeSnapshot.ReadProcessTable().Any(p => p.ParentId == root.ProcessId))
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "PowerShell 啟動、程序表看得到它");
                await Task.Delay(100);
            }
            tree.Sample(root.ProcessId, ProcessTreeSnapshot.ReadProcessTable()).Cpu.Should().BeNull("第一次只取基準");
            TreeActivity busy;
            do
            {
                await Task.Delay(1_000);
                busy = tree.Sample(root.ProcessId, ProcessTreeSnapshot.ReadProcessTable());
            }
            while (busy.Cpu is not > 0 && DateTime.UtcNow < deadline);
            output.WriteLine($"CPU {busy.Cpu}%（{Environment.ProcessorCount} 個邏輯處理器）");
            busy.Cpu.Should().NotBeNull();
            busy.Cpu!.Value.Should().BeGreaterThan(0, "子程序在空轉").And.BeLessThanOrEqualTo(100);
        }
        finally
        {
            (await root.KillTreeAsync(TimeSpan.FromSeconds(30))).Should().BeTrue("收掉空轉的 PowerShell");
        }
    }

    [Fact]
    public void 脫離CLI的程序_算進CPU_不算進IO_同一個程序只算一次()
    {
        // 把測試主機自己當成這個對話脫離 CLI 的背景程式（沒有 CLI 程序：PID 0），空轉 300 ms
        var self = ProcessTreeIo.ReadProcess(Environment.ProcessId)!.Value;
        var stamp = new ProcessStamp(Environment.ProcessId, self.Start);
        var tree = new ProcessTreeIo();
        var first = tree.Sample(0, [stamp], Array.Empty<(int, int)>());
        first.Cpu.Should().BeNull("第一次只取基準");
        first.Mem.Should().BePositive("記憶體取基準那次就有");
        var until = DateTime.UtcNow.AddMilliseconds(300);
        var x = 0L;
        while (DateTime.UtcNow < until)
            x++;
        var busy = tree.Sample(0, [stamp, stamp], Array.Empty<(int, int)>());
        output.WriteLine($"CPU {busy.Cpu}%（空轉 {x} 次）");
        busy.Io.Should().Be(0, "脫離 CLI 的程序不算進硬碟燈");
        busy.Cpu.Should().BeGreaterThan(0);
        var single = ProcessTreeIo.ReadProcess(Environment.ProcessId)!.Value.Mem;
        output.WriteLine($"記憶體 {busy.Mem / 1048576.0:0.0} MB（單獨讀 {single / 1048576.0:0.0} MB）");
        busy.Mem!.Value.Should().BeLessThan((long)(single * 1.5), "同一個程序給了兩次也只算一次記憶體");
        new ProcessTreeIo().Sample(0, [], Array.Empty<(int, int)>()).Should().Be(new TreeActivity(0, null), "沒有 CLI 也沒有背景程式");
    }

    [Fact]
    public async Task 正式的程序表_帶這個對話標記又脫離CLI的程序_算進那個對話的CPU()
    {
        // 測試主機直接開的 PowerShell（不在 app 的工作物件、也不在任何 CLI 底下）帶著對話標記一直空轉＝代理以 Start-Process 開的背景程式
        var marker = Guid.NewGuid().ToString("N");
        var psi = new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -Command \"{SpinForever}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment[BackgroundProgramRules.MarkerVariable] = marker;
        using var busy = System.Diagnostics.Process.Start(psi)!;
        try
        {
            var now = 1_000L;
            var sampler = new ActivitySampler(clock: () => now);
            var root = new ActivityRoot(marker, 0, string.Empty);
            TreeActivity Sample()
            {
                now += ActivityRules.ProcessTableRefreshMs; // 每次都重列程序表（連標記一起讀）
                return sampler.SampleTrees([root])[marker];
            }

            Sample().Cpu.Should().BeNull("第一次只取基準");
            // 等 PowerShell 啟動、環境區塊讀得到（看到之前沒有程序：記憶體是 null、CPU 是 0）
            var deadline = DateTime.UtcNow + BusyWait;
            TreeActivity sample;
            while ((sample = Sample()).Mem is null)
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "帶標記的 PowerShell 出現在程序表");
                await Task.Delay(100);
            }
            do
            {
                await Task.Delay(1_000);
                sample = Sample();
            }
            while (sample.Cpu is not > 0 && DateTime.UtcNow < deadline);
            output.WriteLine($"CPU {sample.Cpu}%（{Environment.ProcessorCount} 個邏輯處理器）");
            sample.Cpu.Should().NotBeNull();
            sample.Cpu!.Value.Should().BeGreaterThan(0, "帶標記、脫離 CLI 的 PowerShell 在空轉").And.BeLessThanOrEqualTo(100);
            sample.Io.Should().Be(0);
            output.WriteLine($"記憶體 {sample.Mem / 1048576.0:0.0} MB");
            sample.Mem.Should().BeGreaterThan(10L << 20, "PowerShell 的私人工作集算進那個對話的記憶體");
        }
        finally
        {
            try { busy.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await busy.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void 取樣來源_程序表3秒內沿用_出現新的根程序馬上重列_所有對話共用一份()
    {
        var reads = 0;
        var now = 1_000L;
        var sampler = new ActivitySampler(() => { reads++; return Array.Empty<(int, int)>(); }, () => now);
        var self = Environment.ProcessId;
        var a = new ActivityRoot("c1", self, "i1");
        var b = new ActivityRoot("c2", self, "i2");

        sampler.SampleTrees([a]);
        reads.Should().Be(1, "第一次要列");
        for (var i = 0; i < 10; i++)
        {
            now += ActivityRules.BusyIntervalMs;
            sampler.SampleTrees([a]);
        }
        reads.Should().Be(1, "2.5 秒內每 250 ms 取樣都沿用上次的表（以前每次都列，877 個程序一次 45 ms）");

        now += 100;
        sampler.SampleTrees([a, b]);
        reads.Should().Be(2, "剛開始執行的對話：馬上重列，找得到它的子孫");
        now += 100;
        sampler.SampleTrees([a, b]);
        reads.Should().Be(2, "兩個對話共用同一份");

        now += ActivityRules.ProcessTableRefreshMs;
        sampler.SampleTrees([a]);
        reads.Should().Be(3, "滿 3 秒重列（期間才開出的工具這時看到）");
        sampler.SampleTrees([]);
        reads.Should().Be(3, "沒有執行中的對話：不列");
    }

    [Fact]
    public void 讀程序_不存在的PID回null()
    {
        ProcessTreeIo.ReadProcess(0).Should().BeNull();
        ProcessTreeIo.ReadProcess(-5).Should().BeNull();
        var self = ProcessTreeIo.ReadProcess(Environment.ProcessId);
        self.Should().NotBeNull("自己一定讀得到");
        self!.Value.Start.Should().BeLessThanOrEqualTo(DateTime.UtcNow.Ticks);
        self.Value.Cpu.Should().BePositive("測試主機自己一定用過 CPU");
        self.Value.Mem.Should().BeGreaterThan(1UL << 20, "私人工作集（同工作管理員的記憶體欄）");
    }

    [Fact]
    public void 整台電腦的網路_讀得到時只增不減()
    {
        var first = NetworkTotals.Read();
        if (first is not { } a)
            return; // 沒有已連線的硬體網卡（例如某些虛擬機）：不量
        var b = NetworkTotals.Read();
        b.Should().NotBeNull();
        b!.Value.Rx.Should().BeGreaterThanOrEqualTo(a.Rx);
        b.Value.Tx.Should().BeGreaterThanOrEqualTo(a.Tx);
    }

    [Fact]
    public void 整台電腦的網路_只問記住的硬體網卡和整張表加總的一樣_問不到的網卡改回整張重列()
    {
        var (before, hardware) = NetworkTotals.ReadTable();
        if (before is not { } a || hardware is null)
            return; // 沒有已連線的硬體網卡：不量
        hardware.Should().NotBeEmpty();
        var listed = NetworkTotals.ReadListed(hardware);
        var (after, again) = NetworkTotals.ReadTable();
        listed.Should().NotBeNull();
        // 計數器只增不減：只問那幾張的結果夾在前後兩次整張表之間（條件相同，不會多算篩選器或少算哪一張）
        listed!.Value.Rx.Should().BeInRange(a.Rx, after!.Value.Rx);
        listed.Value.Tx.Should().BeInRange(a.Tx, after.Value.Tx);
        again.Should().Equal(hardware);

        // 記住的網卡有一張不在了（LUID 對不到）：回 null，Read 改以整張表重列
        NetworkTotals.ReadListed([.. hardware, 0x7FFF_0000_0000_0001]).Should().BeNull();
        NetworkTotals.ReadListed([]).Should().BeNull();
    }

    /// <summary>
    /// 量這台電腦上每種讀法一次要多少 CPU（不是正規測試）：<c>MYRIAGENT_ACTIVITY_BENCH=1</c>。程序表（NtQuerySystemInformation）對照以前的 Toolhelp32，
    /// 網路總量只問記住的硬體網卡（GetIfEntry2）對照整張表（GetIfTable2）。2026-10-25 實測結果寫在提交說明與程式的說明
    /// </summary>
    [EnvFact("MYRIAGENT_ACTIVITY_BENCH", "量燈號取樣的系統呼叫")]
    public void 量測_燈號取樣的系統呼叫()
    {
        static double Cpu()
        {
            GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
            return (kernel + user) / 10_000.0;
        }
        void Bench(string name, int n, Action action)
        {
            action();
            var cpu = Cpu();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
                action();
            output.WriteLine($"{name}：CPU {(Cpu() - cpu) / n:0.00} ms、實際 {sw.Elapsed.TotalMilliseconds / n:0.00} ms");
        }

        output.WriteLine($"程序 {ProcessTreeSnapshot.ReadProcessTable().Count} 個、執行緒 {System.Diagnostics.Process.GetProcesses().Sum(p => p.Threads.Count)} 條");
        Bench("程序表 NtQuerySystemInformation", 30, () => ProcessTreeSnapshot.ReadProcessTable());
        Bench("程序表 Toolhelp32（以前）", 30, () => ToolhelpTable());
        // 2026-10-26 起正式的程序表連對話標記一起讀（ActivitySampler.NativeScan）：第一次讀每個程序的環境變數，之後只讀新出現的
        Bench("程序表＋對話標記 第一次（每個程序都讀環境變數）", 3, () => ActivitySampler.NativeScan()());
        var scan = ActivitySampler.NativeScan();
        Bench("程序表＋對話標記 之後（只讀新出現的程序）", 30, () => scan());
        var (_, hardware) = NetworkTotals.ReadTable();
        Bench("網路 GetIfTable2 整張表", 200, () => NetworkTotals.ReadTable());
        if (hardware is { Length: > 0 })
            Bench($"網路 GetIfEntry2 只問 {hardware.Length} 張", 200, () => NetworkTotals.ReadListed(hardware));
    }

    /// <summary>以前的 Toolhelp32 程序表（只給量測對照）</summary>
    private static List<(int, int)> ToolhelpTable()
    {
        var table = new List<(int, int)>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        try
        {
            var entry = new ProcessEntry { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                    table.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
                while (Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return table;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [Fact]
    public async Task 程序表_列得到自己開的子程序與它的父程序_和系統的程序數相近()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 5 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var table = ProcessTreeSnapshot.ReadProcessTable();
            table.Should().Contain((child.Id, Environment.ProcessId));
            table.Should().Contain(p => p.Id == Environment.ProcessId);
            var system = System.Diagnostics.Process.GetProcesses().Length;
            table.Count.Should().BeInRange(system / 2, system * 2);

            // 以這張表找子孫：cmd 開的 ping 也在樹上
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (ProcessTreeSnapshot.Capture(child.Id).Processes.Count < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            ProcessTreeSnapshot.Capture(child.Id).Processes.Should().HaveCountGreaterThanOrEqualTo(2, "cmd 與它開的 ping");
        }
        finally
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 已經結束
            }
        }
    }
}
