using System.Diagnostics;
using Myriagent.Agents;

namespace Myriagent.Background;

/// <summary>
/// 對話 id → 那個對話開出、還在跑的背景程式（只列有的對話）；<paramref name="Exited"/> 是上一次掃描之後自己結束（或被使用者停止）的
/// （app 啟動後第一次掃描沒有）
/// </summary>
internal sealed record BackgroundScan(IReadOnlyDictionary<string, IReadOnlyList<BackgroundProgram>> Programs, IReadOnlyList<BackgroundProgramExit>? Exited = null)
{
    public static BackgroundScan Empty { get; } = new(new Dictionary<string, IReadOnlyList<BackgroundProgram>>());

    public IReadOnlyList<BackgroundProgramExit> Exits => Exited ?? [];

    /// <summary>
    /// 開始列程序的時間（Stopwatch.GetTimestamp）：中樞只把一輪結束之後才開始的掃描裡還在的程式當成撐過了那一輪
    /// （執行途中拍的掃描晚一點才送到時不會被誤認，AgentHub.BackgroundPrograms.cs）
    /// </summary>
    public long TakenAt { get; init; } = Stopwatch.GetTimestamp();
}

/// <summary>背景程式的來源（中樞以 AttachBackgroundPrograms 接上；測試換成假的）</summary>
internal interface IBackgroundProgramSource : IDisposable
{
    /// <summary>開始定期掃描；每次掃描完在背景執行緒呼叫 <paramref name="onScan"/>（呼叫端自己回 UI 執行緒）。只呼叫一次</summary>
    void Start(Action<BackgroundScan> onScan);

    /// <summary>儘快再掃一次（一輪結束時：代理可能剛開了程式）；任何執行緒</summary>
    void RequestScan();

    /// <summary>
    /// 結束這個背景程式與它的子孫（對話標頭的「停止」，programStop），完成後儘快再掃一次；那個程序已經不是它、已經結束或結束不了回 false。
    /// 它在下一次掃描的 Exited 裡帶 StoppedByUser
    /// </summary>
    Task<bool> StopAsync(BackgroundProgram program);
}

/// <summary>
/// 每 <see cref="BackgroundProgramRules.ScanInterval"/>（中樞要求時 <see cref="BackgroundProgramRules.RescanDelay"/> 後）列一次程序（<see cref="IProcessSnapshotSource"/>），以 app 工作物件裡的程序（CLI 程序）為根分類
/// （<see cref="BackgroundProgramRules.Classify"/>），把結果交給中樞。在自己的計時器執行緒上跑，同一時間只有一次掃描；掃描失敗只記錄、下一次照常。
/// 找到的程式以 <see cref="IProgramProcessControl"/> 追蹤（握著 handle）：結束了但 handle 還在的程序仍在快照裡，這裡以 handle 拿掉；
/// 消失時讀結束碼與結束時間，放進下一次的 <see cref="BackgroundScan.Exited"/>（<see cref="BackgroundProgramRules.Exits"/>：換了根的不算結束）
/// </summary>
internal sealed class BackgroundProgramMonitor(IProcessSnapshotSource snapshots, Func<IReadOnlySet<int>> managedRoots, IProgramProcessControl control) : IBackgroundProgramSource
{
    private readonly object gate = new();
    private System.Threading.Timer? timer;
    private Action<BackgroundScan>? callback;
    private bool scanning;
    private bool disposed;

    /// <summary>上一次掃描的結果（只在掃描中讀寫，掃描同時只有一個）</summary>
    private Dictionary<string, List<BackgroundProgram>> previous = new(StringComparer.Ordinal);

    /// <summary>以 Watch 追蹤中的程式（只在掃描中讀寫）</summary>
    private readonly HashSet<(int Pid, long CreatedAt)> watched = new();

    /// <summary>使用者按了停止的程式（gate）：下一次掃描的 Exited 帶 StoppedByUser</summary>
    private readonly HashSet<(int Pid, long CreatedAt)> stopped = new();

    /// <summary>正式的：程序快照讀 Win32、CLI 程序是 app 工作物件裡的程序</summary>
    public static BackgroundProgramMonitor CreateDefault()
        => new(new NativeProcessSnapshot(), () => ChildProcessJob.GetProcessIds().ToHashSet(), new NativeProgramControl());

    public void Start(Action<BackgroundScan> onScan)
    {
        lock (gate)
        {
            if (disposed || timer != null)
                return;
            callback = onScan;
            timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), BackgroundProgramRules.ScanInterval);
        }
    }

    /// <summary>測試用：只接上結果的去處、不開計時器（測試自己呼叫 Tick）；可以再呼叫一次換掉</summary>
    internal void StartManual(Action<BackgroundScan> onScan)
    {
        lock (gate)
            callback = onScan;
    }

    public void RequestScan()
    {
        lock (gate)
        {
            if (!disposed)
                timer?.Change(BackgroundProgramRules.RescanDelay, BackgroundProgramRules.ScanInterval);
        }
    }

    public Task<bool> StopAsync(BackgroundProgram program)
    {
        var key = (program.Pid, program.CreatedAt);
        return Task.Run(() =>
        {
            lock (gate)
            {
                if (disposed)
                    return false;
                stopped.Add(key);
            }
            var killed = control.KillTree(program.Pid, program.CreatedAt);
            if (!killed)
            {
                lock (gate)
                    stopped.Remove(key);
            }
            RequestScan();
            return killed;
        });
    }

    /// <summary>掃描一次（計時器執行緒；測試直接呼叫）</summary>
    internal void Tick()
    {
        Action<BackgroundScan>? onScan;
        lock (gate)
        {
            if (disposed || scanning)
                return;
            scanning = true;
            onScan = callback;
        }
        try
        {
            var takenAt = Stopwatch.GetTimestamp();
            var programs = Track(BackgroundProgramRules.Classify(snapshots.Snapshot(), managedRoots()));
            var exits = Collect(BackgroundProgramRules.Exits(previous, programs), programs);
            previous = programs;
            onScan?.Invoke(new BackgroundScan(programs.ToDictionary(p => p.Key, p => (IReadOnlyList<BackgroundProgram>)p.Value, StringComparer.Ordinal), exits) { TakenAt = takenAt });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BackgroundProgramMonitor] 掃描失敗：{ex.Message}");
        }
        finally
        {
            lock (gate)
                scanning = false;
        }
    }

    /// <summary>新找到的開始追蹤；已經結束（握著 handle 而還在快照裡、或開的時候就結束了）的拿掉</summary>
    private Dictionary<string, List<BackgroundProgram>> Track(Dictionary<string, List<BackgroundProgram>> programs)
    {
        foreach (var list in programs.Values)
        {
            list.RemoveAll(p =>
            {
                var key = (p.Pid, p.CreatedAt);
                if (watched.Contains(key))
                    return !control.IsAlive(p.Pid, p.CreatedAt);
                if (!control.Watch(p.Pid, p.CreatedAt))
                    return true;
                watched.Add(key);
                return false;
            });
        }
        foreach (var empty in programs.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList())
            programs.Remove(empty);
        return programs;
    }

    /// <summary>結束的讀結束碼；不再列出的（含換了根的）一律放開追蹤</summary>
    private List<BackgroundProgramExit> Collect(List<(string ConversationId, BackgroundProgram Program)> ended, Dictionary<string, List<BackgroundProgram>> programs)
    {
        var exits = new List<BackgroundProgramExit>();
        foreach (var (conversationId, program) in ended)
        {
            var key = (program.Pid, program.CreatedAt);
            var (code, exitedAt) = watched.Remove(key) ? control.Release(program.Pid, program.CreatedAt) : (null, null);
            bool byUser;
            lock (gate)
                byUser = stopped.Remove(key);
            exits.Add(new BackgroundProgramExit(conversationId, program, code, exitedAt, byUser));
        }
        var listed = programs.Values.SelectMany(l => l).Select(p => (p.Pid, p.CreatedAt)).ToHashSet();
        foreach (var key in watched.Where(k => !listed.Contains(k)).ToList())
        {
            watched.Remove(key);
            control.Release(key.Pid, key.CreatedAt);
        }
        return exits;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
            callback = null;
        }
        control.Dispose();
    }
}
