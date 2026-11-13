using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MyriagentDesk;

/// <summary>服務在 Windows 登記的狀態</summary>
internal enum ServiceRunState
{
    NotInstalled,
    Stopped,
    Running,

    /// <summary>正在啟動或停止</summary>
    Pending,
}

/// <summary>向 Windows 登記、啟動、停止、移除服務（真的要系統管理員；安裝器的測試以假的換掉）</summary>
internal interface IServiceControl
{
    ServiceRunState Query(string name);

    /// <summary>沒有就建立、有就改成這個啟動指令；自動啟動、LocalSystem、當掉自動重新啟動</summary>
    void CreateOrUpdate(string name, string displayName, string description, string binPath);

    /// <summary>啟動並等到執行中；逾時或失敗丟例外</summary>
    void Start(string name, TimeSpan timeout);

    /// <summary>停止並等到停下（沒在執行就不做事）；逾時丟例外</summary>
    void Stop(string name, TimeSpan timeout);

    /// <summary>移除登記（沒有就不做事）</summary>
    void Delete(string name);
}

/// <summary>真的服務控制管理員</summary>
internal sealed class ScmServiceControl : IServiceControl
{
    public ServiceRunState Query(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        using var service = TryOpen(scm, name, ServiceQueryStatus);
        return service is null ? ServiceRunState.NotInstalled : StateOf(service);
    }

    public void CreateOrUpdate(string name, string displayName, string description, string binPath)
    {
        using var scm = OpenManager(ScManagerConnect | ScManagerCreateService);
        var service = TryOpen(scm, name, ServiceAllAccess);
        if (service is null)
        {
            // 帳戶給 null＝LocalSystem
            var created = CreateServiceW(scm.Handle, name, displayName, ServiceAllAccess, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
                binPath, null, IntPtr.Zero, null, null, null);
            if (created == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"建立服務 {name} 失敗");
            service = new ScHandle(created);
        }
        else if (!ChangeServiceConfigW(service.Handle, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal, binPath, null, IntPtr.Zero, null, null, null, displayName))
        {
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            throw new Win32Exception(error, $"更新服務 {name} 的設定失敗");
        }

        using (service)
        {
            SetDescription(service, description);
            SetRestartOnFailure(service);
        }
    }

    public void Start(string name, TimeSpan timeout)
    {
        using var scm = OpenManager(ScManagerConnect);
        using var service = TryOpen(scm, name, ServiceStart | ServiceQueryStatus) ?? throw new InvalidOperationException($"服務 {name} 沒有安裝");
        if (!StartServiceW(service.Handle, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceAlreadyRunning)
                throw new Win32Exception(error, $"啟動服務 {name} 失敗");
        }
        WaitFor(service, ServiceRunState.Running, timeout, $"服務 {name} 啟動逾時");
    }

    public void Stop(string name, TimeSpan timeout)
    {
        using var scm = OpenManager(ScManagerConnect);
        using var service = TryOpen(scm, name, ServiceStop | ServiceQueryStatus);
        if (service is null || StateOf(service) == ServiceRunState.Stopped)
            return;
        if (!ControlService(service.Handle, ServiceControlStop, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceNotActive && error != ErrorServiceCannotAcceptCtrl)
                throw new Win32Exception(error, $"停止服務 {name} 失敗");
        }
        WaitFor(service, ServiceRunState.Stopped, timeout, $"服務 {name} 停止逾時");
    }

    public void Delete(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        using var service = TryOpen(scm, name, Delete_);
        if (service is null)
            return;
        if (!DeleteService(service.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceMarkedForDelete)
                throw new Win32Exception(error, $"移除服務 {name} 失敗");
        }
    }

    private static void WaitFor(ScHandle service, ServiceRunState wanted, TimeSpan timeout, string message)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            var state = StateOf(service);
            if (state == wanted)
                return;
            var elapsed = Environment.TickCount64 - started;
            // 要等到執行中，卻已經回到停止：啟動失敗（服務自己結束了），不必等到逾時
            if (wanted == ServiceRunState.Running && state == ServiceRunState.Stopped && elapsed > 1500)
                throw new InvalidOperationException(message + "（服務啟動後就停了，見安裝資料夾 logs 裡的記錄）");
            if (elapsed > timeout.TotalMilliseconds)
                throw new TimeoutException(message);
            Thread.Sleep(150);
        }
    }

    private static ServiceRunState StateOf(ScHandle service)
    {
        if (!QueryServiceStatus(service.Handle, out var status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "查不到服務的狀態");
        return status.CurrentState switch
        {
            StateStopped => ServiceRunState.Stopped,
            StateRunning => ServiceRunState.Running,
            _ => ServiceRunState.Pending,
        };
    }

    private static void SetDescription(ScHandle service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description);
        try
        {
            var info = new ServiceDescription { Description = text };
            ChangeServiceConfig2W(service.Handle, ServiceConfigDescription, ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>當掉後 5 秒重新啟動（一天內最多連續三次，之後不再試）</summary>
    private static void SetRestartOnFailure(ScHandle service)
    {
        const int count = 3;
        var actions = Marshal.AllocHGlobal(count * 8);
        try
        {
            for (var i = 0; i < count; i++)
            {
                Marshal.WriteInt32(actions, i * 8, i < 2 ? ScActionRestart : ScActionNone);
                Marshal.WriteInt32(actions, i * 8 + 4, 5000);
            }
            var info = new ServiceFailureActions { ResetPeriodSeconds = 86400, ActionCount = count, Actions = actions };
            ChangeServiceConfig2FailureW(service.Handle, ServiceConfigFailureActions, ref info);
        }
        finally
        {
            Marshal.FreeHGlobal(actions);
        }
    }

    private static ScHandle OpenManager(uint access)
    {
        var handle = OpenSCManagerW(null, null, access);
        return handle == IntPtr.Zero
            ? throw new Win32Exception(Marshal.GetLastWin32Error(), "連不上服務控制管理員")
            : new ScHandle(handle);
    }

    private static ScHandle? TryOpen(ScHandle scm, string name, uint access)
    {
        var handle = OpenServiceW(scm.Handle, name, access);
        if (handle != IntPtr.Zero)
            return new ScHandle(handle);
        var error = Marshal.GetLastWin32Error();
        return error == ErrorServiceDoesNotExist ? null : throw new Win32Exception(error, $"開不了服務 {name}");
    }

    private sealed class ScHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose() => CloseServiceHandle(Handle);
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint Delete_ = 0x00010000;
    private const uint ServiceAllAccess = 0x000F01FF;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private const uint StateStopped = 1;
    private const uint StateRunning = 4;
    private const int ServiceConfigDescription = 1;
    private const int ServiceConfigFailureActions = 2;
    private const int ScActionNone = 0;
    private const int ScActionRestart = 1;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceCannotAcceptCtrl = 1061;
    private const int ErrorServiceMarkedForDelete = 1072;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public int ResetPeriodSeconds;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public int ActionCount;
        public IntPtr Actions;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(IntPtr scm, string name, string displayName, uint access, uint serviceType, uint startType, uint errorControl,
        string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath, string? loadOrderGroup,
        IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, int infoLevel, ref ServiceDescription info);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2FailureW(IntPtr service, int infoLevel, ref ServiceFailureActions info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
