using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyriagentTools.AcmeE2e;

/// <summary>
/// Windows Job Object（JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE）：本程式不論怎麼結束（含被強制結束），
/// 加進來的子程序（Pebble、challtestsrv、MyriagentHub）都會一起結束，不會留下佔著連接埠的孤兒程序。
/// 正常流程仍然先以 admin stop 讓 MyriagentHub 正常結束（強制結束會在使用者金鑰存放區留下孤兒金鑰檔）。
/// </summary>
internal sealed partial class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private nint handle;

    private ChildProcessJob(nint handle)
    {
        this.handle = handle;
    }

    public static ChildProcessJob Create()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0)
            throw new InvalidOperationException("CreateJobObject 失敗：" + Marshal.GetLastPInvokeError());
        var info = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(job);
            throw new InvalidOperationException("SetInformationJobObject 失敗：" + error);
        }
        return new ChildProcessJob(job);
    }

    /// <summary>把子程序加進 job；失敗（例如本程式所在的 job 不允許巢狀）只回 false，流程照常（結束時改為逐一結束）</summary>
    public bool TryAdd(Process process)
    {
        if (handle == 0)
            return false;
        return AssignProcessToJobObject(handle, process.Handle);
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref handle, 0);
        if (h != 0)
            CloseHandle(h);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformationData info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
