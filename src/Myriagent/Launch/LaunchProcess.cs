using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Myriagent.Core;
using Microsoft.Win32.SafeHandles;

namespace Myriagent.Launch;

/// <summary>執行列開的一個程序（連同它開出的子程序）</summary>
internal interface ILaunchProcess : IDisposable
{
    /// <summary>主程序結束（輸出也讀完）時完成，值是結束碼。Release 模式執行的程式在 Dispose 之後不會完成（程式照樣執行）</summary>
    Task<int> Exited { get; }

    /// <summary>結束整個程序樹（暫停中的也一樣）</summary>
    void Kill();

    /// <summary>暫停整個程序樹；做不到（沒有權限、已經結束）回 false</summary>
    bool Suspend();

    bool Resume();

    /// <summary>現在屬於它、還在執行的程序（主程序在第一個；記進 launch\runs.json，app 重新啟動後接回）。建置一律是空的</summary>
    IReadOnlyList<LaunchProcessRef> Processes();
}

/// <summary>開程序（中樞在 UI 執行緒呼叫，要很快返回；測試換成假的）。輸出一行一行交給 onLine（背景執行緒）</summary>
internal interface ILaunchProcessStarter
{
    /// <summary>建置：app 的子程序、輸出經管線；app 結束時中樞結束它（建置被打斷之後也不會執行）。開不起來丟例外（訊息給使用者看）</summary>
    ILaunchProcess Build(LaunchCommand command, Action<string> onLine);

    /// <summary>執行（Release 模式）：和 app 脫鉤——app 結束、當掉或重新啟動都不影響它；輸出寫進 <paramref name="logPath"/>，這裡跟著讀。開不起來丟例外</summary>
    ILaunchProcess Run(LaunchCommand command, string logPath, Action<string> onLine);

    /// <summary>
    /// 執行（Debug 模式）：app 的子程序、輸出經管線，工作物件設 KILL_ON_JOB_CLOSE——app 結束、當掉或重新啟動時系統結束整個程序樹
    /// （正常結束時中樞也會先結束它）。開不起來丟例外
    /// </summary>
    ILaunchProcess RunAttached(LaunchCommand command, Action<string> onLine);

    /// <summary>
    /// 接回 app 重新啟動前開的程式：<paramref name="processes"/> 裡還在執行、PID 與建立時間都對得上的；一個都不在了回 null。
    /// 輸出檔從頭讀起（app 不在時寫的也看得到）
    /// </summary>
    ILaunchProcess? Reattach(IReadOnlyList<LaunchProcessRef> processes, string? logPath, Action<string> onLine);
}

internal sealed class LaunchProcessStarter : ILaunchProcessStarter
{
    public ILaunchProcess Build(LaunchCommand command, Action<string> onLine) => JobLaunchProcess.Start(command, onLine);

    public ILaunchProcess Run(LaunchCommand command, string logPath, Action<string> onLine) => DetachedLaunchProcess.Start(command, logPath, onLine);

    public ILaunchProcess RunAttached(LaunchCommand command, Action<string> onLine) => JobLaunchProcess.Start(command, onLine, killOnJobClose: true);

    public ILaunchProcess? Reattach(IReadOnlyList<LaunchProcessRef> processes, string? logPath, Action<string> onLine)
        => DetachedLaunchProcess.Reattach(processes, logPath, onLine);
}

/// <summary>把輸出的位元組以換行切開，每行各自判斷編碼（dotnet 是 UTF-8，程式自己的 Console 輸出是主控台字碼頁）；一直沒有換行的（進度列）滿了先送出</summary>
internal sealed class LaunchLineSplitter(Encoding fallback, Action<string> onLine)
{
    private readonly List<byte> pending = new(256);

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b == (byte)'\n')
                Emit();
            else
            {
                pending.Add(b);
                if (pending.Count >= LaunchRules.MaxLineLength * 4)
                    Emit();
            }
        }
    }

    /// <summary>最後一行沒有換行也送出</summary>
    public void Flush()
    {
        if (pending.Count > 0)
            Emit();
    }

    private void Emit()
    {
        var bytes = CollectionsMarshal.AsSpan(pending);
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
            bytes = bytes[..^1];
        try
        {
            onLine(LaunchRules.CleanLine(LaunchRules.DecodeLine(bytes, fallback)));
        }
        catch (Exception ex)
        {
            Log.Warn($"執行列處理輸出失敗：{ex.Message}");
        }
        pending.Clear();
    }

    /// <summary>主控台的字碼頁（OEM，例如 950）；拿不到時用 Latin-1（不丟例外、位元組一對一）</summary>
    public static Encoding ConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)LaunchNative.GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }
}

/// <summary>
/// 建置與 Debug 模式執行的程序：app 的子程序，以自己的工作物件包住程序樹——停止＝TerminateJobObject、暫停＝逐一 NtSuspendProcess 工作物件裡的程序。
/// 建置不設 KILL_ON_JOB_CLOSE（MSBuild 開的編譯伺服器 VBCSCompiler 留著給之後的建置用），按停止與 app 結束時中樞才結束整個程序樹；
/// Debug 模式的執行設 KILL_ON_JOB_CLOSE（<c>killOnJobClose</c>）：app 當掉時系統照樣結束整個程序樹，Dispose（關掉工作物件）也會結束還在裡面的程序。
/// 程序開起來之後才加入工作物件：dotnet 在開子程序之前要先初始化一段時間，實務上不會有子程序漏掉
/// </summary>
internal sealed class JobLaunchProcess : ILaunchProcess
{
    private readonly Process process;
    private readonly SafeKernelHandle? job;
    private readonly bool inJob;
    private int disposed;

    private JobLaunchProcess(Process process, SafeKernelHandle? job, bool inJob, Task stdout, Task stderr)
    {
        this.process = process;
        this.job = job;
        this.inJob = inJob;
        ProcessId = process.Id;
        Exited = WaitAsync(stdout, stderr);
    }

    public Task<int> Exited { get; }

    /// <summary>主程序的 PID（測試用）</summary>
    public int ProcessId { get; }

    public static JobLaunchProcess Start(LaunchCommand command, Action<string> onLine, bool killOnJobClose = false)
    {
        var job = LaunchNative.CreateJob(killOnJobClose);
        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory,
        };
        foreach (var arg in command.Arguments)
            psi.ArgumentList.Add(arg);
        // dotnet 的第一次使用說明與更新提示不要混進輸出
        psi.Environment["DOTNET_NOLOGO"] = "1";

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("程序沒有啟動");
        }
        catch (Win32Exception ex)
        {
            job?.Dispose();
            throw new InvalidOperationException($"找不到或無法執行 {command.FileName}（{ex.Message}）", ex);
        }
        catch
        {
            job?.Dispose();
            throw;
        }
        var inJob = false;
        try
        {
            inJob = job != null && LaunchNative.AssignProcessToJobObject(job, process.SafeHandle);
        }
        catch (Exception ex)
        {
            Log.Warn($"執行列的程序加入工作物件失敗：{ex.Message}");
        }
        // 不給輸入：等待輸入的程式讀到檔案結尾，不會一直卡著
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        var fallback = LaunchLineSplitter.ConsoleEncoding();
        // 專用執行緒同步讀（PipeReadThread）：不佔執行緒集區的執行緒
        var stdout = PipeReadThread.Start($"launch {process.Id} stdout", () => Pump(process.StandardOutput.BaseStream, new LaunchLineSplitter(fallback, onLine)));
        var stderr = PipeReadThread.Start($"launch {process.Id} stderr", () => Pump(process.StandardError.BaseStream, new LaunchLineSplitter(fallback, onLine)));
        return new JobLaunchProcess(process, job, inJob, stdout, stderr);
    }

    private async Task<int> WaitAsync(Task stdout, Task stderr)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(LaunchRules.OutputDrainTimeout)).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void Pump(Stream stream, LaunchLineSplitter splitter)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                splitter.Feed(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        splitter.Flush();
    }

    public void Kill()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        try
        {
            // 暫停中的程序也能直接結束
            if (inJob && LaunchNative.TerminateJobObject(job!, 1))
                return;
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
        {
            // 已經結束
        }
    }

    public bool Suspend() => inJob && Volatile.Read(ref disposed) == 0 && LaunchNative.SuspendOrResume(LaunchNative.JobProcessIds(job!).Select(pid => (pid, (long?)null)), suspend: true);

    public bool Resume() => inJob && Volatile.Read(ref disposed) == 0 && LaunchNative.SuspendOrResume(LaunchNative.JobProcessIds(job!).Select(pid => (pid, (long?)null)), suspend: false);

    public IReadOnlyList<LaunchProcessRef> Processes() => [];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        job?.Dispose();
        try
        {
            process.Dispose();
        }
        catch
        {
        }
    }
}

/// <summary>
/// 執行的程式：和 app 脫鉤（2026-10-23 使用者要求）。以 CreateProcess 直接開——輸出不經管線而是寫進狀態資料夾的輸出檔（只繼承那個檔與 NUL 兩個控制代碼），
/// app 結束後程式寫輸出也不會因為管線斷了而出錯（node／Electron 寫到斷掉的管線會丟 EPIPE）；盡量脫離 app 所在的工作物件；
/// 自己的工作物件不設 KILL_ON_JOB_CLOSE，app 結束時程式照樣執行。這裡另開一條專用執行緒跟著讀輸出檔、每 200 毫秒看主程序結束了沒。
/// 自己開的：工作物件包住整棵樹（先暫停建立、加入工作物件後才開始跑，不會有子程序漏掉），停止＝TerminateJobObject。
/// 接回的（app 重新啟動前開的，<see cref="Reattach"/>）：工作物件沒有名字、app 結束後開不回來，改以程序樹（Toolhelp 快照的父 PID）找子孫；
/// 握著記得的程序的控制代碼，PID 在那之前不會被重複使用。主程序結束時一樣結束剩下的子程序
/// </summary>
internal sealed class DetachedLaunchProcess : ILaunchProcess
{
    private readonly SafeProcessHandle root;
    private readonly LaunchProcessRef rootRef;
    private readonly SafeKernelHandle? job;
    private readonly bool inJob;

    /// <summary>接回時記得的其他程序（自己開的是空的：在工作物件裡）</summary>
    private readonly IReadOnlyList<(SafeProcessHandle Handle, LaunchProcessRef Ref)> others;

    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long maxLogBytes;
    private int disposed;

    private DetachedLaunchProcess(SafeProcessHandle root, LaunchProcessRef rootRef, SafeKernelHandle? job, bool inJob,
        IReadOnlyList<(SafeProcessHandle, LaunchProcessRef)> others, FileStream? log, Action<string> onLine, long maxLogBytes)
    {
        this.root = root;
        this.rootRef = rootRef;
        this.job = job;
        this.inJob = inJob;
        this.others = others;
        this.maxLogBytes = maxLogBytes;
        PipeReadThread.Start($"launch {rootRef.Pid} log", () => Tail(log, onLine));
    }

    public Task<int> Exited => exited.Task;

    public int ProcessId => rootRef.Pid;

    public static DetachedLaunchProcess Start(LaunchCommand command, string logPath, Action<string> onLine, long maxLogBytes = LaunchRules.MaxLogFileBytes)
    {
        var exe = LaunchRules.ResolveExecutable(command.FileName, Environment.GetEnvironmentVariable("PATH"), File.Exists)
                  ?? throw new InvalidOperationException($"找不到或無法執行 {command.FileName}（PATH 上找不到）");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // 讀的這一端先建好輸出檔（也由它清空）；程式拿到的是另外開的、只能附加的控制代碼
        var log = new FileStream(logPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
        var job = LaunchNative.CreateJob();
        try
        {
            var env = new List<KeyValuePair<string, string>>();
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                if (e.Key is string key && e.Value is string value)
                    env.Add(new(key, value));
            }
            // dotnet 的第一次使用說明與更新提示不要混進輸出
            env.Add(new("DOTNET_NOLOGO", "1"));
            var (process, pid, inJob) = LaunchNative.StartDetached(exe, LaunchRules.CommandLine(exe, command.Arguments), command.WorkingDirectory,
                LaunchRules.EnvironmentBlock(env), logPath, job);
            var created = LaunchNative.CreationTime(process) ?? 0;
            return new DetachedLaunchProcess(process, new LaunchProcessRef(pid, created), job, inJob, [], log, onLine, maxLogBytes);
        }
        catch (Exception ex)
        {
            log.Dispose();
            job?.Dispose();
            try
            {
                File.Delete(logPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            if (ex is Win32Exception w)
                throw new InvalidOperationException($"找不到或無法執行 {command.FileName}（{w.Message}）", w);
            throw;
        }
    }

    public static DetachedLaunchProcess? Reattach(IReadOnlyList<LaunchProcessRef> processes, string? logPath, Action<string> onLine,
        long maxLogBytes = LaunchRules.MaxLogFileBytes)
    {
        var alive = new List<(SafeProcessHandle, LaunchProcessRef)>();
        foreach (var r in processes.DistinctBy(p => p.Pid))
        {
            if (LaunchNative.OpenIfSame(r) is { } handle)
                alive.Add((handle, r));
        }
        if (alive.Count == 0)
            return null;
        FileStream? log = null;
        if (logPath != null && File.Exists(logPath))
        {
            try
            {
                log = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"執行列：接回時讀不到輸出檔 {logPath}：{ex.Message}");
            }
        }
        // 主程序不在了、子程序還在（例如 dotnet run 被別人結束）：以第一個還在的當主程序
        var (rootHandle, rootRef) = alive[0];
        return new DetachedLaunchProcess(rootHandle, rootRef, job: null, inJob: false, alive.Skip(1).ToList(), log, onLine, maxLogBytes);
    }

    /// <summary>跟著讀輸出檔，直到主程序結束（之後結束剩下的子程序、讀完最後的輸出才完成 Exited）或 Dispose（不完成）</summary>
    private void Tail(FileStream? log, Action<string> onLine)
    {
        var splitter = new LaunchLineSplitter(LaunchLineSplitter.ConsoleEncoding(), onLine);
        var buffer = new byte[8192];
        var rootExited = false;
        try
        {
            while (Volatile.Read(ref disposed) == 0)
            {
                if (log != null && !ReadAvailable(log, splitter, buffer, truncate: !rootExited))
                {
                    log.Dispose();
                    log = null;
                }
                if (rootExited)
                {
                    splitter.Flush();
                    exited.TrySetResult(LaunchNative.ExitCode(root));
                    return;
                }
                if (LaunchNative.WaitForExit(root, LaunchRules.LogPollInterval))
                {
                    EndLeftovers();
                    rootExited = true;   // 再讀一次到檔尾
                }
            }
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
        {
        }
        finally
        {
            log?.Dispose();
        }
    }

    /// <summary>讀到檔尾；檔案超過上限就清空（程式接著從開頭寫）。讀不了（I/O 錯誤）回 false，之後不再讀、照樣等程式結束</summary>
    private bool ReadAvailable(FileStream log, LaunchLineSplitter splitter, byte[] buffer, bool truncate)
    {
        try
        {
            int read;
            while ((read = log.Read(buffer, 0, buffer.Length)) > 0)
                splitter.Feed(buffer.AsSpan(0, read));
            // 剛讀完到清空之間程式寫的那一點會丟掉（只是給人看的輸出，畫面上本來也只留最後 2000 行）
            if (truncate && log.Position >= maxLogBytes)
            {
                log.SetLength(0);
                log.Position = 0;
            }
            return true;
        }
        catch (IOException ex)
        {
            Log.Warn($"執行列：讀輸出檔失敗（{rootRef.Pid}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>主程序結束：剩下的子程序跟著結束（和停止一樣）</summary>
    private void EndLeftovers()
    {
        if (inJob)
            LaunchNative.TerminateJobObject(job!, 1);
        else
            TerminateTree();
    }

    /// <summary>接回的：記得的程序（含已結束的主程序：握著控制代碼，PID 沒被重複使用）與它們的子孫</summary>
    private void TerminateTree()
    {
        foreach (var r in LaunchNative.WithDescendants([rootRef, .. others.Select(o => o.Ref)]))
            LaunchNative.TerminateIfSame(r);
    }

    public void Kill()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        try
        {
            // 暫停中的程序也能直接結束
            if (inJob && LaunchNative.TerminateJobObject(job!, 1))
                return;
            TerminateTree();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool Suspend() => SuspendOrResume(suspend: true);

    public bool Resume() => SuspendOrResume(suspend: false);

    private bool SuspendOrResume(bool suspend)
    {
        if (Volatile.Read(ref disposed) != 0 || exited.Task.IsCompleted)
            return false;
        try
        {
            return inJob
                ? LaunchNative.SuspendOrResume(LaunchNative.JobProcessIds(job!).Select(pid => (pid, (long?)null)), suspend)
                : LaunchNative.SuspendOrResume(LaunchNative.WithDescendants(AliveRefs()).Select(r => (r.Pid, (long?)r.Created)), suspend);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>記得的程序裡還在執行的</summary>
    private List<LaunchProcessRef> AliveRefs()
    {
        var list = new List<LaunchProcessRef>();
        if (!LaunchNative.WaitForExit(root, TimeSpan.Zero))
            list.Add(rootRef);
        foreach (var (handle, r) in others)
        {
            if (!LaunchNative.WaitForExit(handle, TimeSpan.Zero))
                list.Add(r);
        }
        return list;
    }

    public IReadOnlyList<LaunchProcessRef> Processes()
    {
        if (Volatile.Read(ref disposed) != 0 || exited.Task.IsCompleted)
            return [];
        try
        {
            if (!inJob)
                return LaunchNative.WithDescendants(AliveRefs());
            var list = new List<LaunchProcessRef>();
            if (!LaunchNative.WaitForExit(root, TimeSpan.Zero))
                list.Add(rootRef);
            foreach (var pid in LaunchNative.JobProcessIds(job!))
            {
                if (pid != rootRef.Pid && LaunchNative.Describe(pid) is { } r)
                    list.Add(r);
            }
            return list;
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
    }

    /// <summary>不結束程式：工作物件沒設 KILL_ON_JOB_CLOSE，關掉控制代碼之後程式照樣執行；讀輸出的執行緒在下一次檢查時結束</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        job?.Dispose();
        root.Dispose();
        foreach (var (handle, _) in others)
            handle.Dispose();
    }
}

/// <summary>核心物件的控制代碼（工作物件、Toolhelp 快照）</summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => LaunchNative.CloseHandle(handle);
}

/// <summary>執行列用到的 Win32：工作物件、CreateProcess、程序樹、暫停與繼續</summary>
internal static class LaunchNative
{
    /// <summary>
    /// 工作物件：預設沒有任何限制（不設 KILL_ON_JOB_CLOSE）；<paramref name="killOnJobClose"/> 時最後一個控制代碼關掉（app 結束或當掉）就結束裡面所有的程序
    /// （設定失敗照樣用，只是 app 當掉時程式不會跟著結束）。建立失敗回 null（照樣執行，只是停止與暫停改以程序樹處理不了）
    /// </summary>
    public static SafeKernelHandle? CreateJob(bool killOnJobClose = false)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            Log.Warn($"執行列：建立工作物件失敗（{Marshal.GetLastWin32Error()}）");
            return null;
        }
        if (killOnJobClose && !SetKillOnJobClose(job))
            Log.Warn($"執行列：工作物件設定 KILL_ON_JOB_CLOSE 失敗（{Marshal.GetLastWin32Error()}）：app 當掉時程式不會跟著結束");
        return job;
    }

    private static bool SetKillOnJobClose(SafeKernelHandle job)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            return SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 以 CreateProcess 開程式：stdin 是 NUL、stdout 與 stderr 是 <paramref name="logPath"/> 只能附加的控制代碼（PROC_THREAD_ATTRIBUTE_HANDLE_LIST：只繼承這兩個，
    /// app 其他可繼承的控制代碼——例如 CLI 子程序的管線——不會被它握著）、沒有主控台視窗、先暫停，加進 <paramref name="job"/> 後才開始跑。
    /// 先試著脫離 app 所在的工作物件（如果有；不允許脫離時照樣開）。兩個控制代碼開成可繼承到 CreateProcess 之間，別的執行緒同時開的子程序
    /// 也可能繼承到它們（.NET 的 Process.Start 一律繼承所有可繼承的控制代碼）：只是一個輸出檔與 NUL，那些程序不會去寫，輸出檔也能照常刪
    /// </summary>
    public static (SafeProcessHandle Process, int Pid, bool InJob) StartDetached(string exe, string commandLine, string workingDirectory, string environment,
        string logPath, SafeKernelHandle? job)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        using var output = CreateFileW(logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, ref sa, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (output.IsInvalid)
            throw new Win32Exception();
        using var input = CreateFileW("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa, OPEN_EXISTING, 0, IntPtr.Zero);
        if (input.IsInvalid)
            throw new Win32Exception();

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var handleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
        var env = Marshal.StringToHGlobalUni(environment);   // 字串結尾再補一個 \0：區塊以兩個 \0 結束
        bool initialized = false, outputRef = false, inputRef = false;
        try
        {
            output.DangerousAddRef(ref outputRef);
            input.DangerousAddRef(ref inputRef);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception();
            initialized = true;
            Marshal.WriteIntPtr(handleList, 0, output.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, input.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleList, (IntPtr)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = input.DangerousGetHandle();
            si.StartupInfo.hStdOutput = output.DangerousGetHandle();
            si.StartupInfo.hStdError = output.DangerousGetHandle();
            si.lpAttributeList = attributes;
            const uint flags = CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT;
            // CreateProcessW 可能改寫命令列的緩衝區：每次給新的
            if (!CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags | CREATE_BREAKAWAY_FROM_JOB, env, workingDirectory, ref si, out var pi))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_ACCESS_DENIED)
                    throw new Win32Exception(error);
                Log.Warn("執行列：app 所在的工作物件不允許子程序脫離，程式會跟著那個工作物件結束");
                if (!CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags, env, workingDirectory, ref si, out pi))
                    throw new Win32Exception();
            }
            var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            try
            {
                var inJob = job != null && AssignProcessToJobObject(job, process);
                if (job != null && !inJob)
                    Log.Warn($"執行列的程序加入工作物件失敗（{Marshal.GetLastWin32Error()}）：停止與暫停改以程序樹處理");
                if (ResumeThread(pi.hThread) == uint.MaxValue)
                {
                    var ex = new Win32Exception();
                    TerminateProcess(process, 1);
                    throw ex;
                }
                return (process, pi.dwProcessId, inJob);
            }
            catch
            {
                process.Dispose();
                throw;
            }
            finally
            {
                CloseHandle(pi.hThread);
            }
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(env);
            if (outputRef)
                output.DangerousRelease();
            if (inputRef)
                input.DangerousRelease();
        }
    }

    /// <summary>程序的建立時間（FILETIME）；讀不到回 null</summary>
    public static long? CreationTime(SafeProcessHandle process)
        => GetProcessTimes(process, out var created, out _, out _, out _) ? created : null;

    /// <summary>主程序結束了（等最多 timeout）</summary>
    public static bool WaitForExit(SafeProcessHandle process, TimeSpan timeout)
        => WaitForSingleObject(process, (uint)timeout.TotalMilliseconds) == WAIT_OBJECT_0;

    public static int ExitCode(SafeProcessHandle process) => GetExitCodeProcess(process, out var code) ? (int)code : -1;

    /// <summary>還在執行、建立時間對得上的程序的控制代碼（可以等結束、結束它、暫停）；不在了或對不上回 null</summary>
    public static SafeProcessHandle? OpenIfSame(LaunchProcessRef r)
    {
        if (r.Pid <= 0)
            return null;
        var handle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | PROCESS_SUSPEND_RESUME, false, (uint)r.Pid);
        if (!handle.IsInvalid && CreationTime(handle) == r.Created && !WaitForExit(handle, TimeSpan.Zero))
            return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>還在執行的程序的 PID 與建立時間；不在了或打不開回 null</summary>
    public static LaunchProcessRef? Describe(int pid)
    {
        if (pid <= 0)
            return null;
        using var handle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid || WaitForExit(handle, TimeSpan.Zero) || CreationTime(handle) is not { } created)
            return null;
        return new LaunchProcessRef(pid, created);
    }

    /// <summary>結束程序（建立時間要對得上：PID 可能已被別的程序重複使用）</summary>
    public static void TerminateIfSame(LaunchProcessRef r)
    {
        using var handle = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)r.Pid);
        if (!handle.IsInvalid && CreationTime(handle) == r.Created)
            TerminateProcess(handle, 1);
    }

    /// <summary>
    /// 這些程序加上它們的子孫（Toolhelp 快照的父 PID）。子程序的建立時間不早於父程序才算：父程序結束後，父 PID 可能指到後來重複使用那個 PID 的程序
    /// </summary>
    public static List<LaunchProcessRef> WithDescendants(IReadOnlyList<LaunchProcessRef> roots)
    {
        var result = new List<LaunchProcessRef>();
        var created = new Dictionary<int, long>();
        foreach (var r in roots)
        {
            if (created.TryAdd(r.Pid, r.Created))
                result.Add(r);
        }
        if (result.Count == 0)
            return result;
        var byParent = ProcessParents().ToLookup(p => p.ParentPid, p => p.Pid);
        var queue = new Queue<int>(created.Keys);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in byParent[parent])
            {
                if (child == parent || created.ContainsKey(child) || Describe(child) is not { } r || r.Created < created[parent])
                    continue;
                created[child] = r.Created;
                result.Add(r);
                queue.Enqueue(child);
            }
        }
        return result;
    }

    private static List<(int Pid, int ParentPid)> ProcessParents()
    {
        var list = new List<(int, int)>();
        using var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
            return list;
        var entry = new PROCESSENTRY32W { dwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
        if (!Process32FirstW(snapshot, ref entry))
            return list;
        do
            list.Add((entry.th32ProcessID, entry.th32ParentProcessID));
        while (Process32NextW(snapshot, ref entry));
        return list;
    }

    /// <summary>逐一暫停或繼續（created 不是 null 時建立時間要對得上）；至少一個成功回 true</summary>
    public static bool SuspendOrResume(IEnumerable<(int Pid, long? Created)> processes, bool suspend)
    {
        var any = false;
        foreach (var (pid, created) in processes)
        {
            using var handle = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle.IsInvalid || (created is { } c && CreationTime(handle) != c))
                continue;
            if ((suspend ? NtSuspendProcess(handle) : NtResumeProcess(handle)) >= 0)
                any = true;
        }
        return any;
    }

    /// <summary>工作物件裡的 PID（QueryInformationJobObject 的 JobObjectBasicProcessIdList，含巢狀工作物件裡的）</summary>
    public static IReadOnlyList<int> JobProcessIds(SafeKernelHandle job)
    {
        int capacity = 64;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int size = ProcessIdListOffset + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, (uint)size, out _))
                {
                    int count = Marshal.ReadInt32(buffer, 4);
                    var ids = new int[count];
                    for (int i = 0; i < count; i++)
                        ids[i] = (int)Marshal.ReadIntPtr(buffer, ProcessIdListOffset + i * IntPtr.Size).ToInt64();
                    return ids;
                }
                if (Marshal.GetLastWin32Error() != ERROR_MORE_DATA)
                    return [];
                capacity = Math.Max(capacity * 2, Marshal.ReadInt32(buffer, 0) + 16);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int ProcessIdListOffset = 8;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_MORE_DATA = 234;
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_APPEND_DATA = 0x0004;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeKernelHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(SafeKernelHandle hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(SafeKernelHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeKernelHandle hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeKernelHandle hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeKernelHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeKernelHandle hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeKernelHandle hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern uint GetOEMCP();

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(SafeProcessHandle processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(SafeProcessHandle processHandle);
}
