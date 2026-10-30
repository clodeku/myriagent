using System.Diagnostics;
using System.Runtime.InteropServices;
using Myriagent.Agents;

namespace Myriagent.Background;

/// <summary>背景程式的程序操作（BackgroundProgramMonitor 用；測試換成假的）。程序一律以 PID＋建立時間（FILETIME 刻度）認，PID 會被重複使用</summary>
internal interface IProgramProcessControl : IDisposable
{
    /// <summary>
    /// 開始追蹤：拿著它的 handle，之後才讀得到結束碼（程序結束後 PID 也不會被別人拿走）。已經結束、或那個 PID 現在是別的程序回 false；
    /// 打不開（沒有權限）時照樣回 true，只是之後讀不到結束碼
    /// </summary>
    bool Watch(int pid, long createdAt);

    /// <summary>追蹤中的程序還在執行（結束了但還有人握著 handle 的程序仍在系統的程序清單裡）；沒有追蹤的回 true</summary>
    bool IsAlive(int pid, long createdAt);

    /// <summary>放開追蹤，回結束碼與結束時間（FILETIME 刻度）；還在執行、沒有追蹤或讀不到的是 null</summary>
    (int? ExitCode, long? ExitedAt) Release(int pid, long createdAt);

    /// <summary>結束這個程式與它的子孫（使用者按了停止）；那個 PID 已經不是它、已經結束或結束不了回 false</summary>
    bool KillTree(int pid, long createdAt);
}

/// <summary>
/// Win32 的背景程式操作：追蹤以 PROCESS_QUERY_LIMITED_INFORMATION 開 handle（同一個使用者的程序都開得了，不需要管理員）；
/// 停止先以 GetProcessTimes 確認建立時間，再擷取它的程序樹（Toolhelp32，ProcessTreeSnapshot）逐一結束——和執行列的停止一樣是直接結束，不送 Ctrl+C
/// </summary>
internal sealed class NativeProgramControl : IProgramProcessControl
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint STILL_ACTIVE = 259;
    private const int ERROR_INVALID_PARAMETER = 87;

    private readonly object gate = new();
    private readonly Dictionary<(int Pid, long CreatedAt), IntPtr> handles = new();

    public bool Watch(int pid, long createdAt)
    {
        lock (gate)
        {
            if (handles.ContainsKey((pid, createdAt)))
                return true;
        }
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return Marshal.GetLastWin32Error() != ERROR_INVALID_PARAMETER;   // 87＝沒有這個 PID；其他（沒有權限）照算、不追蹤
        if (CreatedAt(handle) != createdAt || !IsActive(handle))
        {
            CloseHandle(handle);
            return false;
        }
        lock (gate)
        {
            if (!handles.TryAdd((pid, createdAt), handle))
                CloseHandle(handle);
        }
        return true;
    }

    public bool IsAlive(int pid, long createdAt)
    {
        lock (gate)
            return !handles.TryGetValue((pid, createdAt), out var handle) || IsActive(handle);
    }

    public (int? ExitCode, long? ExitedAt) Release(int pid, long createdAt)
    {
        IntPtr handle;
        lock (gate)
        {
            if (!handles.Remove((pid, createdAt), out handle))
                return (null, null);
        }
        try
        {
            if (!GetExitCodeProcess(handle, out var code) || code == STILL_ACTIVE)
                return (null, null);
            long? exitedAt = GetProcessTimes(handle, out _, out var exit, out _, out _) && exit > 0 ? exit : null;
            return (unchecked((int)code), exitedAt);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public bool KillTree(int pid, long createdAt)
    {
        var handle = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            if (CreatedAt(handle) != createdAt || !IsActive(handle))
                return false;
            // 先擷取樹（根還活著，子孫才接得上），再結束根與子孫
            var tree = ProcessTreeSnapshot.Capture(pid);
            var killed = TerminateProcess(handle, 1);
            tree.KillAlive();
            return killed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NativeProgramControl] 結束程序 {pid} 失敗：{ex.Message}");
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var handle in handles.Values)
                CloseHandle(handle);
            handles.Clear();
        }
    }

    private static long CreatedAt(IntPtr handle) => GetProcessTimes(handle, out var created, out _, out _, out _) ? created : -1;

    private static bool IsActive(IntPtr handle) => GetExitCodeProcess(handle, out var code) && code == STILL_ACTIVE;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
