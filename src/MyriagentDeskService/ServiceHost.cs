using System.ComponentModel;
using System.Runtime.InteropServices;
using Myriagent.Remote;
using Myriagent.Update;

namespace MyriagentDesk;

/// <summary>
/// 服務模式：由 Windows 的服務控制管理員啟動（<c>MyriagentDesk.exe service</c>）。直接呼叫 Win32 的服務介面，不用套件——
/// 這個程序以 LocalSystem 執行，相依愈少愈好（多一個組件就多一個要複製、要驗的檔）。
/// 啟動後開管道（<see cref="DeskPipeServer"/>），收到停止或關機就關掉管道、回報已停止
/// </summary>
internal static class ServiceHost
{
    // 委派要留著：交給 Windows 之後它隨時會呼叫，被回收就當掉
    private static readonly ServiceMainDelegate MainCallback = ServiceMain;
    private static readonly HandlerExDelegate HandlerCallback = Handler;
    private static readonly ManualResetEventSlim StopRequested = new(false);

    private static IntPtr statusHandle;
    private static string serviceName = DeskServiceRules.ServiceName;

    /// <summary>交給服務控制管理員（不是它啟動的會失敗：1063）；服務停止後才返回</summary>
    public static int Run()
    {
        var name = Marshal.StringToHGlobalUni(string.Empty);
        try
        {
            var table = new[]
            {
                new ServiceTableEntry { Name = name, Proc = Marshal.GetFunctionPointerForDelegate(MainCallback) },
                new ServiceTableEntry { Name = IntPtr.Zero, Proc = IntPtr.Zero },
            };
            if (StartServiceCtrlDispatcherW(table))
                return 0;
            var error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine(error == ErrorFailedServiceControllerConnect
                ? "service 模式只能由 Windows 的服務控制管理員啟動（開發時用 run --pipe <名稱>）"
                : new Win32Exception(error).Message);
            return 1;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        // 第一個參數是服務名稱（測試的暫時服務和真的用同一個 exe）
        if (argc > 0 && argv != IntPtr.Zero && Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv)) is { } given && DeskServiceRules.IsServiceName(given))
            serviceName = given;
        statusHandle = RegisterServiceCtrlHandlerExW(serviceName, HandlerCallback, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero)
            return;

        Report(StateStartPending, 0, waitHintMs: 10_000);
        DeskPipeServer? server = null;
        SessionAgents? agents = null;
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("不知道自己的執行檔在哪裡");
            // 記錄只寫在安裝資料夾底下（執行檔在 <安裝資料夾>\p-xxxx\，往上一層）
            ServiceLog.UseDirectory(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, "logs"));
            var identity = SelfVersion.Current;
            var system = ProcessIdentity.IsSystem();
            agents = new SessionAgents(exe, serviceName);
            server = new DeskPipeServer(DeskServiceRules.PipeNameOf(serviceName), system, new NativeClientIdentifier(system),
                new ServicePipeHandler(identity, system, new HelperSessionProbe(system, exe), agents));
            server.Start();
            ServiceLog.Info($"服務 {serviceName} 啟動：{identity.Release}，系統權限 {system}");
            Report(StateRunning, AcceptStop | AcceptShutdown);
            StopRequested.Wait();
            Report(StateStopPending, 0, waitHintMs: 10_000);
            ServiceLog.Info($"服務 {serviceName} 停止");
        }
        catch (Exception ex)
        {
            ServiceLog.Error("服務啟動失敗", ex);
        }
        finally
        {
            server?.Dispose();
            // 服務停了：還在的擷取幫手一起結束（它們是系統權限的程序，不留著）
            agents?.Dispose();
            Report(StateStopped, 0);
        }
    }

    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                Report(StateStopPending, 0, waitHintMs: 10_000);
                StopRequested.Set();
                return 0;
            case ControlInterrogate:
                return 0;
            default:
                return ErrorCallNotImplemented;
        }
    }

    private static void Report(uint state, uint accepts, uint waitHintMs = 0)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = accepts,
            WaitHint = waitHintMs,
        };
        SetServiceStatus(statusHandle, ref status);
    }

    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint StateStopped = 1;
    private const uint StateStartPending = 2;
    private const uint StateStopPending = 3;
    private const uint StateRunning = 4;
    private const uint AcceptStop = 0x00000001;
    private const uint AcceptShutdown = 0x00000004;
    private const uint ControlStop = 1;
    private const uint ControlInterrogate = 4;
    private const uint ControlShutdown = 5;
    private const uint ErrorCallNotImplemented = 120;
    private const int ErrorFailedServiceControllerConnect = 1063;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ServiceMainDelegate(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint HandlerExDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr Name;
        public IntPtr Proc;
    }

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

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerExDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}

/// <summary>這個 exe 是哪一版（組件的 InformationalVersion，和桌面同一套規則）</summary>
internal static class SelfVersion
{
    public static ReleaseIdentity Current { get; } = Read();

    private static ReleaseIdentity Read()
    {
        var assembly = typeof(SelfVersion).Assembly;
        var informational = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        return ReleaseIdentity.FromInformationalVersion(informational, assembly.GetName().Version);
    }
}
