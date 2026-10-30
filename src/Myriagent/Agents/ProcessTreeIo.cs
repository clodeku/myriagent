using System.Runtime.InteropServices;
using Myriagent.Background;
using Myriagent.Hub;
using Myriagent.SystemStatus;

namespace Myriagent.Agents;

/// <summary>
/// 對話框燈號的取樣來源（AgentHub.Activity.cs）：每個對話 CLI 程序樹的 I/O 增量與 CPU 使用率、整台電腦的網路收送量。
/// 中樞一次只呼叫一個（上一次做完才排下一次），在背景執行緒呼叫
/// </summary>
internal interface IActivitySampler
{
    /// <summary>
    /// 每個根程序（鍵＝對話 id）的程序樹這段時間讀寫的位元組與 CPU 使用率；第一次見到、或程序實例換了的只取基準（Io 0、Cpu null）；
    /// 不在這次清單裡的鍵忘掉
    /// </summary>
    IReadOnlyDictionary<string, TreeActivity> SampleTrees(IReadOnlyList<ActivityRoot> roots);

    /// <summary>整台電腦的網路收送位元組累計；讀不到回 null</summary>
    (ulong Rx, ulong Tx)? ReadNetwork();
}

/// <summary>
/// 要量程序樹的一個對話：鍵（對話 id）、根程序（CLI）的 PID、程序實例（換了就重新取基準）。沒有 CLI 程序（只量它開出、脫離 CLI 的程式）時
/// PID 是 0、實例是空字串
/// </summary>
internal readonly record struct ActivityRoot(string Key, int ProcessId, string Instance);

/// <summary>
/// 一次列出的程序表：每個程序的 (PID, 父 PID)，以及各對話（鍵＝對話 id）脫離 CLI 的程序樹的根——帶這個對話的標記（MYRIAGENT_AGENT_CONVERSATION）、
/// 不在任何 CLI 底下的程序，判斷同背景程式（BackgroundProgramRules.Classify）。這些樹只算進 CPU（2026-10-26 使用者要求：代理開的背景工作、
/// 子代理這些程序都要加總算進這個代理的 CPU），硬碟燈照舊只看 CLI 程序樹
/// </summary>
internal sealed record ProcessTable(IReadOnlyList<(int Id, int ParentId)> Rows, IReadOnlyDictionary<string, IReadOnlyList<ProcessStamp>> Detached)
{
    public static IReadOnlyDictionary<string, IReadOnlyList<ProcessStamp>> NoDetached { get; } = new Dictionary<string, IReadOnlyList<ProcessStamp>>(StringComparer.Ordinal);
}

/// <summary>
/// 一個程序樹一次取樣的結果：這段時間讀寫的位元組、CPU 使用率（%，同工作管理員「處理程序」頁；只取了基準時 null）、
/// 記憶體（位元組：各程序私人工作集的加總，同工作管理員的「記憶體」欄；和 CPU 同一組程序，取基準那次就有；一個程序都沒讀到時 null）
/// </summary>
internal readonly record struct TreeActivity(long Io, double? Cpu, long? Mem = null);

/// <summary>
/// 真的取樣來源：程序樹以 <see cref="ProcessTreeIo"/> 量，網路以 <see cref="NetworkTotals"/> 量。程序表每 <see cref="ActivityRules.ProcessTableRefreshMs"/>
/// （或出現新的根程序時）才重列一次，所有對話共用（ActivityRules.RefreshProcessTable）。正式的程序表連對話標記一起讀（<see cref="NativeScan"/>），
/// 脫離 CLI 的背景程式、父子關係斷掉的工具也算進那個對話的 CPU
/// </summary>
internal sealed class ActivitySampler : IActivitySampler
{
    private readonly Dictionary<string, (string Instance, ProcessTreeIo Tree)> trees = new(StringComparer.Ordinal);
    private readonly Func<ProcessTable> readScan;
    private readonly Func<long> clock;
    private ProcessTable? table;
    private long tableAtMs;
    private HashSet<(string Key, int ProcessId)> tableRoots = new();

    /// <param name="readTable">只列 (PID, 父 PID)、沒有脫離 CLI 的程序（測試用；給了 <paramref name="readScan"/> 時不用）</param>
    /// <param name="clock">單調時鐘的毫秒（測試換成假的；預設 Environment.TickCount64）</param>
    /// <param name="readScan">列程序表連各對話脫離 CLI 的程序（測試換成假的）；兩個都沒給時是 <see cref="NativeScan"/></param>
    public ActivitySampler(Func<IReadOnlyList<(int Id, int ParentId)>>? readTable = null, Func<long>? clock = null, Func<ProcessTable>? readScan = null)
    {
        this.readScan = readScan ?? (readTable is { } rt ? () => new ProcessTable(rt(), ProcessTable.NoDetached) : NativeScan());
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// 正式的程序表：以 <see cref="NativeProcessSnapshot"/> 列這個工作階段的程序，每個程序（PID＋建立時間）第一次出現時讀一次環境變數裡的對話標記；
    /// 各對話脫離 CLI 的程序樹的根照背景程式的同一套判斷（<see cref="BackgroundProgramRules.Classify"/>：主控台程式與背景用的瀏覽器，
    /// 不算建置工具留著的常駐程序——MSBuild 節點重用、VBCSCompiler、LocalDB 這類之後會被別的對話共用）。列不到時退回只列 (PID, 父 PID)
    /// </summary>
    internal static Func<ProcessTable> NativeScan()
    {
        var snapshot = new NativeProcessSnapshot();
        return () =>
        {
            var rows = snapshot.Snapshot();
            if (rows.Count == 0)
                return new ProcessTable(ProcessTreeSnapshot.ReadProcessTable(), ProcessTable.NoDetached);
            var detached = BackgroundProgramRules.Classify(rows, ChildProcessJob.GetProcessIds().ToHashSet())
                .ToDictionary(p => p.Key, p => (IReadOnlyList<ProcessStamp>)p.Value
                    .Select(b => new ProcessStamp(b.Pid, DateTime.FromFileTimeUtc(b.CreatedAt).Ticks)).ToList(), StringComparer.Ordinal);
            return new ProcessTable(rows.Select(r => (r.Pid, r.ParentPid)).ToList(), detached);
        };
    }

    public IReadOnlyDictionary<string, TreeActivity> SampleTrees(IReadOnlyList<ActivityRoot> roots)
    {
        var result = new Dictionary<string, TreeActivity>(StringComparer.Ordinal);
        foreach (var key in trees.Keys.Where(k => roots.All(r => r.Key != k)).ToList())
            trees.Remove(key);
        if (roots.Count == 0)
            return result;
        var now = clock();
        var current = roots.Select(r => (r.Key, r.ProcessId)).ToHashSet();
        if (ActivityRules.RefreshProcessTable(table is not null, tableAtMs, now, newRoot: !current.IsSubsetOf(tableRoots)))
        {
            table = readScan();
            tableAtMs = now;
            tableRoots = current;
        }
        foreach (var root in roots)
        {
            if (!trees.TryGetValue(root.Key, out var t) || t.Instance != root.Instance)
                trees[root.Key] = t = (root.Instance, new ProcessTreeIo());
            var detached = table!.Detached.TryGetValue(root.Key, out var d) ? d : [];
            result[root.Key] = t.Tree.Sample(root.ProcessId, detached, table.Rows);
        }
        return result;
    }

    public (ulong Rx, ulong Tx)? ReadNetwork() => NetworkTotals.Read();
}

/// <summary>
/// 一個對話的 CLI 程序樹（CLI 與它執行的工具，例如 dotnet build）讀寫的 I/O 量（對話框的硬碟燈）與 CPU 使用率（輸入框下方的 CPU 波形）。
/// 每次取樣列出根程序的子孫（程序表＋<see cref="ProcessTreeSnapshot.FindDescendants"/>，PID 重用以啟動時間排除），
/// 每個程序開一次 handle 讀啟動時間、CPU 時間與 I/O 計數器（GetProcessTimes 一次就有啟動時間與 CPU 時間、GetProcessIoCounters），
/// 和上次同一個程序比出增量（<see cref="ActivityRules.TreeDelta"/>、<see cref="ActivityRules.TreeCpuDelta"/>）。
/// app 的工作物件允許子程序脫離（SILENT_BREAKAWAY_OK，代理替你開的程式不會跟著 app 結束），所以不能用工作物件的 I/O 計數器，要自己走程序樹。
/// 計數器含管線（實測：stdout 的 2MB 照樣算進寫入）：app 與 CLI 之間的由中樞扣掉，CLI 與工具之間的（工具的輸出）會算進來；網路 socket 不算（實測）。
/// 兩次取樣之間就結束的程序，最後一段量會漏掉（CPU 也是）
/// </summary>
internal sealed class ProcessTreeIo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Func<long> utcNow;
    private Dictionary<ProcessStamp, ulong> lastIo = new();
    private Dictionary<ProcessStamp, ulong> lastCpu = new();
    private long lastAtUtcTicks;
    private bool primed;

    /// <param name="utcNow">現在的 UTC ticks（和程序的啟動時間比；測試換成假的，預設 DateTime.UtcNow）</param>
    public ProcessTreeIo(Func<long>? utcNow = null)
    {
        this.utcNow = utcNow ?? (() => DateTime.UtcNow.Ticks);
    }

    /// <summary>取一次樣：這次與上次之間程序樹讀寫的位元組與 CPU 使用率（第一次只取基準：0 與 null；根程序已結束：0 與 0）</summary>
    public TreeActivity Sample(int rootProcessId, IReadOnlyList<(int Id, int ParentId)> table) => Sample(rootProcessId, [], table);

    /// <summary>
    /// 同上，另外把 <paramref name="detached"/>（這個對話脫離 CLI 的程序樹的根：背景程式、父子關係斷掉的工具，ProcessTable.Detached）與它們的子孫
    /// 算進 CPU（不算進 I/O：硬碟燈照舊只看 CLI 程序樹）。<paramref name="rootProcessId"/> 是 0 時只量這些。同一個程序只算一次
    /// </summary>
    public TreeActivity Sample(int rootProcessId, IReadOnlyList<ProcessStamp> detached, IReadOnlyList<(int Id, int ParentId)> table)
    {
        var cache = new Dictionary<int, (long Start, ulong Io, ulong Cpu, ulong? Mem)?>();
        (long Start, ulong Io, ulong Cpu, ulong? Mem)? Read(int pid)
        {
            if (!cache.TryGetValue(pid, out var value))
                cache[pid] = value = ReadProcess(pid);
            return value;
        }

        var now = utcNow();
        var io = new Dictionary<ProcessStamp, ulong>();
        var cpu = new Dictionary<ProcessStamp, ulong>();
        ulong mem = 0;
        var alive = 0;
        // CPU 與記憶體算同一組程序（每個程序只算一次）；已經結束、handle 還有人握著的程序只算 CPU（補到它最後一段），不算記憶體
        void Take(ProcessStamp stamp, (long Start, ulong Io, ulong Cpu, ulong? Mem) info)
        {
            if (!cpu.TryAdd(stamp, info.Cpu) || info.Mem is not { } m)
                return;
            mem += m;
            alive++;
        }
        if (rootProcessId > 0 && Read(rootProcessId) is { } root)
        {
            var rootStamp = new ProcessStamp(rootProcessId, root.Start);
            io[rootStamp] = root.Io;
            Take(rootStamp, root);
            foreach (var p in ProcessTreeSnapshot.FindDescendants(rootProcessId, root.Start, table, pid => Read(pid)?.Start))
            {
                if (Read(p.Id) is { } info && info.Start == p.StartTimeUtcTicks)
                {
                    io[p] = info.Io;
                    Take(p, info);
                }
            }
        }
        // 脫離 CLI 的程序樹（代理以 nohup、Start-Process 開的背景程式，Git Bash 經 timeout、bash -c 開而父子關係斷掉的工具）：只算進 CPU 與記憶體
        foreach (var d in detached)
        {
            if (cpu.ContainsKey(d) || Read(d.Id) is not { } top || top.Start != d.StartTimeUtcTicks)
                continue;
            Take(d, top);
            foreach (var p in ProcessTreeSnapshot.FindDescendants(d.Id, top.Start, table, pid => Read(pid)?.Start))
            {
                if (Read(p.Id) is { } info && info.Start == p.StartTimeUtcTicks)
                    Take(p, info);
            }
        }
        var ioDelta = ActivityRules.TreeDelta(lastIo, io, primed);
        double? cpuPercent = primed
            ? ActivityRules.CpuPercent(ActivityRules.TreeCpuDelta(lastCpu, cpu, primed, lastAtUtcTicks, now), now - lastAtUtcTicks, Environment.ProcessorCount)
            : null;
        lastIo = io;
        lastCpu = cpu;
        lastAtUtcTicks = now;
        primed = true;
        return new TreeActivity(ioDelta, cpuPercent, alive == 0 ? null : mem > long.MaxValue ? long.MaxValue : (long)mem);
    }

    /// <summary>
    /// 一個程序的啟動時間（UTC 的 DateTime ticks，同 ProcessTreeSnapshot）、讀＋寫的位元組、用掉的 CPU 時間（核心＋使用者，100 奈秒為單位）、
    /// 記憶體（私人工作集的位元組，同工作管理員的「記憶體」欄；讀不到時 0；程序已經結束、只是 handle 還有人握著時 null——
    /// 那種程序照樣讀得到 CPU 與 I/O，也還剩幾十 KB 的工作集，2026-10-26 測試時被算進記憶體）；開不了（已結束、沒有權限）回 null
    /// </summary>
    internal static (long Start, ulong Io, ulong Cpu, ulong? Mem)? ReadProcess(int processId)
    {
        if (processId <= 0)
            return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            if (!GetProcessTimes(handle, out var creation, out _, out var kernel, out var user) || !GetProcessIoCounters(handle, out var io))
                return null;
            var running = GetExitCodeProcess(handle, out var exitCode) && exitCode == StillActive;
            return (DateTime.FromFileTimeUtc(creation).Ticks, io.ReadTransferCount + io.WriteTransferCount, (ulong)Math.Max(0, kernel) + (ulong)Math.Max(0, user),
                running ? ReadMemory(handle) : null);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// 私人工作集（PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize：Windows 11／Server 2022 起，同一個只能查詢的 handle 就讀得到，2026-10-26 實測）；
    /// 舊的 Windows 不認這個大小時退回私人位元組（PrivateUsage，認可的私人記憶體，通常比工作集大一些）；都讀不到 0
    /// </summary>
    private static ulong ReadMemory(IntPtr handle)
    {
        var counters = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
        if (K32GetProcessMemoryInfo(handle, ref counters, counters.cb))
            return (ulong)counters.PrivateWorkingSetSize;
        counters.cb = (uint)Marshal.OffsetOf<PROCESS_MEMORY_COUNTERS_EX2>(nameof(PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize));
        return K32GetProcessMemoryInfo(handle, ref counters, counters.cb) ? (ulong)counters.PrivateUsage : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint cb);

    /// <summary>GetExitCodeProcess 對還在跑的程序回的值</summary>
    private const uint StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
