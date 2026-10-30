using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Core;

namespace Myriagent.Agents;

/// <summary>啟動代理 CLI 的方式：執行檔＋前置參數（npm 安裝是 node＋js 檔）</summary>
internal sealed record AgentLauncher(string FileName, IReadOnlyList<string> PrefixArguments);

/// <summary>
/// 代理 CLI 子程序：stdin 寫 JSON 行、stdout 逐行回呼、stderr 留尾段給錯誤訊息。
/// stdin 一律無 BOM 的 UTF-8（BOM 會讓第一行 JSON 解析失敗）；換行固定 \n。
/// 程序加入 app 的工作物件（app 結束或當掉時自動結束代理程序），關閉時先關 stdin 讓 CLI 正常收尾、逾時才結束程序樹。
/// </summary>
internal sealed partial class AgentProcess : IDisposable
{
    private const int StderrTailLines = 40;

    private readonly Process process;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly Queue<string> stderrTail = new();
    private readonly TaskCompletionSource<int?> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;
    private long pipeBytes;

    /// <summary>stdout 的每一行（在背景執行緒呼叫）</summary>
    public event Action<AgentProcess, string>? LineReceived;

    /// <summary>stderr 的每一行（在背景執行緒呼叫；同時照常記進錯誤尾段）：agy 沒登入時的提示印在這裡，會話要立刻知道</summary>
    public event Action<AgentProcess, string>? StderrReceived;

    /// <summary>程序結束（stdout 讀完之後才觸發，確保最後幾行已處理）</summary>
    public event Action<AgentProcess, int?>? Exited;

    private AgentProcess(Process process)
    {
        this.process = process;
    }

    public int ProcessId { get; private set; }

    /// <summary>
    /// 這個程序實例的識別（Guid N 格式）：每次啟動都不同，app 重開後也不會和存檔裡的舊值撞號（所以不用遞增的整數）。
    /// 會話以它當 IAgentSession.ProcessInstance；核准卡的有效範圍就是產生它的實例（AgentChatItem.ProcessInstance）
    /// </summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public bool HasExited => exited.Task.IsCompleted;

    public Task<int?> ExitTask => exited.Task;

    /// <summary>
    /// app 與這個程序之間經過管線的位元組累計：stdout、stderr 讀到的加上 stdin 寫入的（UTF-8，每行含換行）。
    /// 對話框的資料流燈；程序樹的 I/O 計數器也含這些，硬碟燈扣掉它（web-protocol 的 activity）。任何執行緒都可讀
    /// </summary>
    public long PipeBytes => Interlocked.Read(ref pipeBytes);

    private void CountPipe(string line) => Interlocked.Add(ref pipeBytes, Encoding.UTF8.GetByteCount(line) + 1);

    /// <summary>燈號來源（IAgentSession.ActivitySource）；程序已結束回 null</summary>
    public AgentActivitySource? Activity => HasExited ? null : new AgentActivitySource(ProcessId, InstanceId, PipeBytes);

    /// <summary>啟動失敗時的重試間隔（CLI 換檔的空窗：Claude 兩次改名之間約 1 毫秒；npm 從舊版退役到新版解壓完成之間）</summary>
    internal static readonly IReadOnlyList<TimeSpan> StartRetryDelays = new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };

    /// <summary>
    /// 同 Start，但遇到 CLI 換檔空窗造成的暫時失敗（IsTransientStartFailure）以 0.5／1／2 秒退避重試；其他錯誤與重試用完照常丟出。
    /// npm 版 Codex 在安裝空窗的「Missing optional dependency」是 node 啟動後才結束，不在這裡處理
    /// </summary>
    public static Task<AgentProcess> StartWithRetryAsync(
        AgentLauncher launcher,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<IDictionary<string, string?>>? configureEnvironment,
        CancellationToken cancellationToken = default,
        bool joinJob = true)
    {
        var args = arguments.ToList();
        return RetryTransientStartAsync(() => Start(launcher, args, workingDirectory, configureEnvironment, joinJob), StartRetryDelays, cancellationToken);
    }

    /// <summary>重試的骨架（測試注入啟動函式與間隔）：暫時失敗就等下一個間隔再試，間隔用完後的失敗原樣丟出</summary>
    internal static async Task<T> RetryTransientStartAsync<T>(Func<T> start, IReadOnlyList<TimeSpan> delays, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return start();
            }
            catch (Exception ex) when (attempt < delays.Count && IsTransientStartFailure(ex))
            {
                Debug.WriteLine($"[AgentProcess] 啟動暫時失敗（{ex.Message}），{delays[attempt].TotalSeconds:0.#} 秒後重試");
            }
            await Task.Delay(delays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>啟動程序的暫時失敗：找不到執行檔（2）或路徑（3）、檔案被其他程序占用（32）——CLI 正在換檔時會短暫出現</summary>
    internal static bool IsTransientStartFailure(Exception ex)
        => ex is Win32Exception { NativeErrorCode: 2 or 3 or 32 };

    /// <summary>
    /// 啟動程序。configureEnvironment 調整子程序的環境變數（帳戶的設定資料夾、剝除 API Key…）。
    /// 事件要在 BeginReading 之前掛好，才不會漏掉最早的輸出。
    /// </summary>
    public static AgentProcess Start(
        AgentLauncher launcher,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<IDictionary<string, string?>>? configureEnvironment,
        bool joinJob = true)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo
        {
            FileName = launcher.FileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var argument in launcher.PrefixArguments.Concat(arguments))
            psi.ArgumentList.Add(argument);
        configureEnvironment?.Invoke(psi.Environment);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        if (!process.Start())
            throw new InvalidOperationException($"無法啟動 {launcher.FileName}");

        var agentProcess = new AgentProcess(process) { ProcessId = process.Id };
        // joinJob＝false：不跟著 app 結束（CLI 代查用量時可能正在換發權杖，ClaudeControlClient）
        if (joinJob)
            ChildProcessJob.TryAssign(process);
        return agentProcess;
    }

    /// <summary>
    /// 開始讀 stdout／stderr（事件掛好後呼叫）。各用一條專用的背景執行緒同步讀（<see cref="PipeReadThread"/>）：
    /// 以前的 ReadLineAsync 在程序的整個生命期各佔一條執行緒集區的執行緒，十幾個閒置的 CLI 程序就把集區佔滿
    /// </summary>
    public void BeginReading()
    {
        PipeReadThread.Start($"agent {ProcessId} stderr", ReadStderr);
        PipeReadThread.Start($"agent {ProcessId} stdout", ReadStdout);
    }

    private void ReadStdout()
    {
        try
        {
            while (true)
            {
                var line = process.StandardOutput.ReadLine();
                if (line == null)
                    break;
                CountPipe(line);
                try
                {
                    LineReceived?.Invoke(this, line);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgentProcess] 處理輸出失敗：{ex}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 程序被結束時管線關閉
        }
        finally
        {
            // 沒預料到的例外（交給 PipeReadThread 記錄）也照樣回報結束：等著它的會話不會永遠等下去
            ReportExit();
        }
    }

    private void ReportExit()
    {
        int? exitCode = null;
        try
        {
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }

        exited.TrySetResult(exitCode);
        try
        {
            Exited?.Invoke(this, exitCode);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AgentProcess] 處理結束事件失敗：{ex}");
        }
    }

    private void ReadStderr()
    {
        try
        {
            while (true)
            {
                var line = process.StandardError.ReadLine();
                if (line == null)
                    break;
                CountPipe(line);
                AppendStderr(line);
                try
                {
                    StderrReceived?.Invoke(this, line);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AgentProcess] 處理錯誤輸出失敗：{ex}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// 把一行記進錯誤尾段：stderr 的每一行（ReadStderr），以及會話轉來的非 JSON stdout 行（CLI 偶爾把說明文字印在 stdout）。
    /// 注意 claude 續接失敗的「No conversation found…」印在 stderr，不在 stdout
    /// </summary>
    public void AppendStderr(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (stderrTail)
        {
            stderrTail.Enqueue(AnsiPattern().Replace(line, string.Empty));
            while (stderrTail.Count > StderrTailLines)
                stderrTail.Dequeue();
        }
    }

    public string GetStderrTail(int maxChars = 1500)
    {
        string text;
        lock (stderrTail)
            text = string.Join("\n", stderrTail);
        return text.Length <= maxChars ? text : "…" + text[^maxChars..];
    }

    /// <summary>寫一行到 stdin（多執行緒安全）；程序已結束丟 IOException</summary>
    public async Task WriteLineAsync(string line)
    {
        try
        {
            await writeLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            // 程序剛結束、物件已釋放：呼叫端（WriteQuietlyAsync 等）只處理 IOException
            throw new IOException("代理程序已結束", ex);
        }
        try
        {
            if (HasExited || Volatile.Read(ref disposed) != 0)
                throw new IOException("代理程序已結束");
            // 同一個 writer 寫完再 flush：寫入途中被 Dispose 時，Process 會先把 StandardInput 清掉，再取一次會丟 InvalidOperationException
            var writer = process.StandardInput;
            await writer.WriteAsync(line + "\n").ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            CountPipe(line);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            throw new IOException("代理程序已結束", ex);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>正常關閉：關 stdin 讓 CLI 收尾，逾時再結束整個程序樹</summary>
    public async Task CloseAsync(TimeSpan gracePeriod)
    {
        try
        {
            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                process.StandardInput.Close();
            }
            finally
            {
                writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        var finished = await Task.WhenAny(exited.Task, Task.Delay(gracePeriod)).ConfigureAwait(false);
        if (finished != exited.Task)
            Kill();
    }

    public void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>擷取這個程序目前的程序樹（根＝這個程序）；要在結束程序之前取：根程序結束後，孫程序的父程序鏈就斷了</summary>
    public ProcessTreeSnapshot CaptureTree() => ProcessTreeSnapshot.Capture(ProcessId);

    /// <summary>
    /// 結束整個程序樹並確認都已結束（熱更新重開前「確認子孫都已結束」）：先擷取程序樹再 Kill，等樹上每個程序都結束（上限 timeout），
    /// 還有殘留就逐一結束、再等最多 2 秒。全部結束回 true。不 Dispose 這個物件（stdout 讀完、Exited 照常觸發）
    /// </summary>
    public async Task<bool> KillTreeAsync(TimeSpan timeout)
    {
        var tree = CaptureTree();
        Kill();
        if (await tree.WaitForExitAsync(timeout).ConfigureAwait(false))
            return true;
        tree.KillAlive();
        return await tree.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Kill();
        // 明確關掉三條管線（Process.Dispose 不會關，要等 GC 才釋放 handle）
        foreach (var close in new Action[] { () => process.StandardInput.Dispose(), () => process.StandardOutput.Dispose(), () => process.StandardError.Dispose() })
        {
            try
            {
                close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }
        try
        {
            process.Dispose();
        }
        catch
        {
        }
        // writeLock 不 Dispose：寫入途中被釋放（程序結束、OnExited 釋放）時，那次寫入的 finally 還要 Release，
        // 釋放過的 SemaphoreSlim 會丟 ObjectDisposedException 蓋掉「代理程序已結束」的 IOException。沒用 AvailableWaitHandle 就沒有核心物件要釋放
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiPattern();
}

/// <summary>
/// app 的工作物件：app 直接啟動的 CLI 程序加入後，app 結束（含當掉）時由系統一併結束。
/// 設 SILENT_BREAKAWAY_OK：一般程序（cmd、PowerShell…）再開的子程序不會被綁進來，代理替使用者開的程式（例如編輯器）不會被一起關掉。
/// 例外是 node（libuv）：它把自己與開出的子程序放進自己的巢狀工作物件，那個工作物件掛在這個底下，
/// 所以 npm 版 Codex（node codex.js → codex.exe）的 codex.exe 也在這個工作物件裡——app 結束時一起結束，PID 清單也列得出它（實測）。
/// </summary>
internal static class ChildProcessJob
{
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;
    private const int ERROR_MORE_DATA = 234;

    /// <summary>JOBOBJECT_BASIC_PROCESS_ID_LIST 的表頭（兩個 DWORD）；PID 陣列（ULONG_PTR）從這裡開始，x64 與 x86 都是 8</summary>
    private const int ProcessIdListOffset = 8;

    private static readonly Lazy<IntPtr> Job = new(CreateJob, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>工作物件已建立（第一次 TryAssign 時才建立；在那之前沒有任何程序在裡面）</summary>
    public static bool IsCreated => Job.IsValueCreated && Job.Value != IntPtr.Zero;

    public static void TryAssign(Process process)
    {
        try
        {
            var job = Job.Value;
            if (job != IntPtr.Zero)
                AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChildProcessJob] 加入工作物件失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 這個程序是不是還在執行、而且在 app 的工作物件裡（含 node 開出、在巢狀工作物件裡的子程序）；已結束或打不開回 false
    /// </summary>
    public static bool Contains(int processId)
    {
        if (!IsCreated || processId <= 0)
            return false;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            return IsStillActive(handle) && IsProcessInJob(handle, Job.Value, out var inJob) && inJob;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// 目前還在執行、在 app 工作物件裡的所有程序 PID（QueryInformationJobObject 的 JobObjectBasicProcessIdList，含巢狀工作物件裡的程序）。
    /// 已結束、但還有人握著它的 handle 的程序仍掛在工作物件上，這裡排除。工作物件還沒建立或查詢失敗回空清單。
    /// 熱更新用來確認某種 CLI 的程序都已結束（呼叫端再依執行檔路徑分類）
    /// </summary>
    public static IReadOnlyList<int> GetProcessIds() => ReadJobProcessIds().Where(IsStillActive).ToList();

    /// <summary>工作物件上登記的所有 PID（含已結束、handle 還沒全部關掉的）</summary>
    private static IReadOnlyList<int> ReadJobProcessIds()
    {
        if (!IsCreated)
            return Array.Empty<int>();
        int capacity = 64;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int size = ProcessIdListOffset + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(Job.Value, JobObjectBasicProcessIdList, buffer, (uint)size, out _))
                {
                    int count = Marshal.ReadInt32(buffer, 4);   // NumberOfProcessIdsInList
                    var ids = new int[count];
                    for (int i = 0; i < count; i++)
                        ids[i] = (int)Marshal.ReadIntPtr(buffer, ProcessIdListOffset + i * IntPtr.Size).ToInt64();
                    return ids;
                }
                if (Marshal.GetLastWin32Error() != ERROR_MORE_DATA)
                    return Array.Empty<int>();
                // 緩衝區不夠：表頭的 NumberOfAssignedProcesses 是目前的總數（查詢期間可能又多了，多留一些）
                capacity = Math.Max(capacity * 2, Marshal.ReadInt32(buffer, 0) + 16);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return Array.Empty<int>();
    }

    /// <summary>程序還在執行（工作物件上的 PID 在程序物件釋放前不會被重複使用，所以開到的一定是同一個程序）</summary>
    private static bool IsStillActive(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            return IsStillActive(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsStillActive(IntPtr processHandle) => GetExitCodeProcess(processHandle, out var code) && code == STILL_ACTIVE;

    private static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
            },
        };
        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr processHandle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>程序的執行檔路徑（QueryFullProcessImageName；不用 WMI——大量查詢曾把 WmiPrvSE 打到 400% CPU）</summary>
internal static class ProcessImage
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// 程序目前的執行檔完整路徑（Win32 格式）；打不開（不存在、沒有權限）回 null。
    /// 研究實測：執行中的 exe 被改名（claude.exe → claude.exe.old.&lt;ms&gt;.&lt;pid&gt;）後，這裡回的是改名後的路徑，
    /// Process.MainModule 與 WMI 仍是舊路徑；被改名的是上層資料夾（npm 把套件退役成 .codex-&lt;hash&gt;）時，這裡也仍是原路徑
    /// </summary>
    public static string? TryGetPath(int processId)
    {
        if (processId <= 0)
            return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new char[32768];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>一個程序的身分：PID 會被重複使用，所以連建立時間（UTC 刻度）一起記；兩者都相同才是同一個程序</summary>
internal readonly record struct ProcessStamp(int Id, long StartTimeUtcTicks);

/// <summary>
/// 某個時間點的程序樹：根程序與它的所有子孫（依父程序 PID 串起；建立時間早於父程序的不算，避免父程序的 PID 被重複使用時認錯）。
/// 熱更新重開前用來確認舊程序的子孫（指令、MCP 伺服器、codex-command-runner…）都已結束：要在結束根程序之前擷取。
/// 不靠工作物件判斷：cmd 等一般程序開的孫程序不在 app 的工作物件裡（SILENT_BREAKAWAY_OK），但仍是這棵樹的一部分。
/// 擷取前中間的程序就已結束時，它的子孫接不上這棵樹（Windows 不會改掛父程序）——那種殘留要另外以 ChildProcessJob.GetProcessIds 檢查
/// </summary>
internal sealed class ProcessTreeSnapshot
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private ProcessTreeSnapshot(IReadOnlyList<ProcessStamp> processes) => Processes = processes;

    public static ProcessTreeSnapshot Empty { get; } = new(Array.Empty<ProcessStamp>());

    /// <summary>樹上的程序：根在第一個，之後依層序；根已結束或打不開時為空</summary>
    public IReadOnlyList<ProcessStamp> Processes { get; }

    /// <summary>擷取以 rootProcessId 為根的程序樹（NtQuerySystemInformation 的程序表，不用 WMI）</summary>
    public static ProcessTreeSnapshot Capture(int rootProcessId)
    {
        if (TryGetStartTicks(rootProcessId) is not { } rootStart)
            return Empty;
        return new ProcessTreeSnapshot(FindDescendants(rootProcessId, rootStart, ReadProcessTable(), TryGetStartTicks));
    }

    /// <summary>
    /// 純函式：從程序表（PID、父程序 PID）找出根與它的子孫。startTicks 查不到的（已結束、沒有權限）不算；
    /// 建立時間早於父程序的不算（那是父程序 PID 被重複使用前就存在的別的程序）
    /// </summary>
    internal static List<ProcessStamp> FindDescendants(int rootId, long rootStartTicks, IReadOnlyList<(int Id, int ParentId)> table, Func<int, long?> startTicks)
    {
        var children = table.Where(p => p.Id != p.ParentId).ToLookup(p => p.ParentId, p => p.Id);
        var root = new ProcessStamp(rootId, rootStartTicks);
        var result = new List<ProcessStamp> { root };
        var seen = new HashSet<int> { rootId };
        var pending = new Queue<ProcessStamp>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var childId in children[parent.Id])
            {
                if (!seen.Add(childId) || startTicks(childId) is not { } start || start < parent.StartTimeUtcTicks)
                    continue;
                var child = new ProcessStamp(childId, start);
                result.Add(child);
                pending.Enqueue(child);
            }
        }
        return result;
    }

    /// <summary>還活著的（同一個 PID、建立時間相同、還沒結束）</summary>
    public IReadOnlyList<ProcessStamp> GetAlive() => Processes.Where(p => TryGetStartTicks(p.Id) == p.StartTimeUtcTicks).ToList();

    /// <summary>等樹上的程序都結束；timeout 內都結束回 true</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (GetAlive().Count == 0)
                return true;
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return false;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>逐一結束樹上還活著的程序（不再依父子關係），回傳結束了幾個</summary>
    public int KillAlive()
    {
        int killed = 0;
        foreach (var stamp in Processes)
        {
            try
            {
                using var process = Process.GetProcessById(stamp.Id);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != stamp.StartTimeUtcTicks)
                    continue;
                process.Kill();
                killed++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // 已經結束或沒有權限
            }
        }
        return killed;
    }

    /// <summary>程序的建立時間（UTC 刻度）；已結束、不存在或打不開回 null</summary>
    private static long? TryGetStartTicks(int processId)
    {
        if (processId <= 0)
            return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 目前所有程序的（PID, 父 PID）：NtQuerySystemInformation(SystemProcessInformation) 一次取（背景程式的 NativeProcessSnapshot 用同一個呼叫與欄位位移）。
    /// 對話框燈號每 3 秒重列一次（ActivityRules.ProcessTableRefreshMs）。以前用 Toolhelp32：核心裡一樣要列所有程序與執行緒，另外每個程序把 260 字的名稱轉成字串，
    /// 2026-10-25 實測 873 個程序、3.3 萬條執行緒時一次約 64 ms，這裡約 47 ms（CPU）。讀不到回空的
    /// </summary>
    internal static List<(int Id, int ParentId)> ReadProcessTable()
    {
        var table = new List<(int Id, int ParentId)>(1024);
        var size = Math.Max(Volatile.Read(ref processTableBytes), 1 << 20);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (256 << 10));
                    continue;
                }
                if (status != 0)
                    return table;
                // 下次直接配夠大（程序與執行緒會變多一點）
                Volatile.Write(ref processTableBytes, needed + (256 << 10));
                var offset = 0L;
                while (true)
                {
                    // SYSTEM_PROCESS_INFORMATION（x64）：0x00 NextEntryOffset、0x50 UniqueProcessId、0x58 InheritedFromUniqueProcessId
                    var entry = buffer + (nint)offset;
                    var next = Marshal.ReadInt32(entry, 0x00);
                    table.Add(((int)Marshal.ReadInt64(entry, 0x50), (int)Marshal.ReadInt64(entry, 0x58)));
                    if (next == 0)
                        break;
                    offset += next;
                }
                return table;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return table;
    }

    /// <summary>上次列程序表用到的緩衝區大小（下次照它配，不必先試一次太小的）</summary>
    private static int processTableBytes;

    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
