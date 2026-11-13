using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Myriagent.Remote;
using Microsoft.Win32.SafeHandles;

namespace MyriagentDesk;

/// <summary>這個程序是誰</summary>
internal static class ProcessIdentity
{
    /// <summary>以 LocalSystem 執行（服務控制管理員啟動的服務）</summary>
    public static bool IsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
    }

    /// <summary>有系統管理員的權限（安裝器要）</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string? UserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }
}

/// <summary>
/// 真的查管道另一端：工作階段問管道（<c>GetNamedPipeClientSessionId</c>），使用者以「識別」層級短暫模擬用戶端讀它的權杖
/// （只讀 SID，不以它的身分做任何事），那個工作階段登入的人問 Windows（<c>WTSQueryUserToken</c>，要系統權限）。
/// 前景模式沒有系統權限，問不了工作階段的使用者：拿自己當答案（管道本來就只開給自己）
/// </summary>
internal sealed class NativeClientIdentifier(bool system) : IDeskClientIdentifier
{
    public DeskServiceClientInfo Identify(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "查不到管道用戶端的工作階段");
        string? user = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            user = identity?.User?.Value;
        });
        return new DeskServiceClientInfo((int)session, user, system ? SessionUser((int)session) : ProcessIdentity.UserSid());
    }

    /// <summary>那個工作階段登入的使用者的 SID；沒有人登入、問不到回 null</summary>
    internal static string? SessionUser(int sessionId)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var token))
            return null;
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>目前的輸入桌面（鍵盤滑鼠現在送到哪個桌面）</summary>
internal static class InputDesktop
{
    /// <summary>
    /// 名稱（Default、Winlogon、Screen-saver）；開不了回 null 與 Win32 錯誤碼——一般權限的程序在安全桌面時是 5（存取被拒），
    /// 系統權限的程序開得了
    /// </summary>
    public static (string? Name, int Error) Read()
    {
        var desktop = Open(DesktopReadObjects);
        if (desktop == IntPtr.Zero)
            return (null, Marshal.GetLastWin32Error());
        try
        {
            return NameOf(desktop) is { } name ? (name, 0) : (null, Marshal.GetLastWin32Error());
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>開目前的輸入桌面（呼叫的人負責 <c>CloseDesktop</c>）；開不了回 0，錯誤碼在 <see cref="Marshal.GetLastWin32Error"/></summary>
    public static IntPtr Open(uint access) => OpenInputDesktop(0, false, access);

    /// <summary>桌面的名稱；問不到回 null（錯誤碼在 <see cref="Marshal.GetLastWin32Error"/>）</summary>
    public static string? NameOf(IntPtr desktop)
    {
        var buffer = new byte[512];
        if (!GetUserObjectInformationW(desktop, UoiName, buffer, buffer.Length, out var needed))
            return null;
        var chars = Math.Max(0, Math.Min(needed, buffer.Length) / 2 - 1);
        return Encoding.Unicode.GetString(buffer, 0, chars * 2).TrimEnd('\0');
    }

    /// <summary>這個程序的視窗站裡有哪些桌面（列不出來回空的）</summary>
    public static IReadOnlyList<string> List()
    {
        var names = new List<string>();
        EnumDesktopsDelegate collect = (name, _) =>
        {
            if (!string.IsNullOrEmpty(name) && names.Count < 32)
                names.Add(name);
            return true;
        };
        EnumDesktopsW(GetProcessWindowStation(), collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        return names;
    }

    private const uint DesktopReadObjects = 0x0001;

    private const int UoiName = 2;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate bool EnumDesktopsDelegate(string name, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopsW(IntPtr windowStation, EnumDesktopsDelegate callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, byte[] info, int length, out int needed);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}

/// <summary>
/// 以這個程序的權杖（LocalSystem）在使用者的工作階段開一個程序、收它的標準輸出。服務自己在工作階段 0，那裡沒有使用者的桌面：
/// 要看或操作某個工作階段的安全桌面，只能在那個工作階段裡開一個系統權限的程序（遠端桌面軟體的服務都是這樣做）。
/// 做法：複製自己的權杖、把權杖的工作階段改成目標（要 SeTcbPrivilege，LocalSystem 有），再以它建立程序
/// </summary>
internal static class SessionProcess
{
    /// <summary>幫手的輸出最多讀這麼多</summary>
    private const int MaxOutputBytes = 64 * 1024;

    /// <summary>執行並等它結束；逾時就結束它、丟 <see cref="TimeoutException"/>。回結束碼與標準輸出（UTF-8）</summary>
    public static (int ExitCode, string Output) Run(int sessionId, string exePath, string arguments, TimeSpan timeout)
    {
        var read = IntPtr.Zero;
        var write = IntPtr.Zero;
        var process = default(ProcessInformation);
        try
        {
            var inheritable = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            if (!CreatePipe(out read, out write, ref inheritable, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "建不了收輸出的管線");
            // 讀的這一端不給子程序繼承（不然子程序握著它，讀不到結尾）
            SetHandleInformation(read, HandleFlagInherit, 0);
            process = Create(sessionId, exePath, arguments, write);

            // 寫的這一端只留給子程序：這裡關掉，子程序結束時讀的那端才會讀到結尾
            CloseHandle(write);
            write = IntPtr.Zero;
            using var output = new FileStream(new SafeFileHandle(read, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            read = IntPtr.Zero;
            var reading = Task.Run(() => ReadAll(output));
            if (WaitForSingleObject(process.Process, (uint)Math.Max(1, timeout.TotalMilliseconds)) != WaitObject0)
            {
                TerminateProcess(process.Process, 1);
                reading.Wait(TimeSpan.FromSeconds(2));
                throw new TimeoutException($"工作階段 {sessionId} 的幫手逾時");
            }

            GetExitCodeProcess(process.Process, out var exit);
            var text = reading.Wait(TimeSpan.FromSeconds(5)) ? reading.Result : string.Empty;
            return ((int)exit, text);
        }
        finally
        {
            Close(process.Thread);
            Close(process.Process);
            Close(write);
            Close(read);
        }
    }

    /// <summary>
    /// 開了就回、不等它結束（擷取用的幫手：一直開著，沒人連了自己結束）。標準輸出入都不接。回程序的控制代碼（呼叫的人負責釋放）與程序 id
    /// </summary>
    public static (SafeProcessHandle Process, int ProcessId) Start(int sessionId, string exePath, string arguments)
    {
        var process = Create(sessionId, exePath, arguments, IntPtr.Zero);
        Close(process.Thread);
        return (new SafeProcessHandle(process.Process, ownsHandle: true), process.ProcessId);
    }

    /// <summary>以複製出來、換到 <paramref name="sessionId"/> 的權杖建立程序；<paramref name="output"/> 不是 0 時當它的標準輸出與標準錯誤（要是可繼承的）</summary>
    private static ProcessInformation Create(int sessionId, string exePath, string arguments, IntPtr output)
    {
        // 這三個權限 LocalSystem 的權杖裡都有，但不一定是啟用的：先啟用（已經啟用、或不是系統權限就沒有作用）
        EnablePrivilege("SeTcbPrivilege");
        EnablePrivilege("SeAssignPrimaryTokenPrivilege");
        EnablePrivilege("SeIncreaseQuotaPrivilege");

        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId, out var own))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "開不了自己的權杖");
        var token = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(own, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "複製權杖失敗");
            // 權杖本來就在目標的工作階段時不必改（改工作階段要 SeTcbPrivilege）：服務在工作階段 0、目標一定不同，一定會改；
            // 測試在自己的工作階段跑這一段（沒有系統權限），驗的是建立程序、接輸出與逾時
            var session = (uint)sessionId;
            if (!(GetTokenInformation(token, TokenSessionId, out var current, sizeof(uint), out _) && current == session)
                && !SetTokenInformation(token, TokenSessionId, ref session, sizeof(uint)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"權杖換不到工作階段 {sessionId}");

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                // 互動的視窗站；幫手再自己切到目前的輸入桌面
                Desktop = @"WinSta0\Default",
                Flags = StartfUseStdHandles,
                StdOutput = output,
                StdError = output,
            };
            var commandLine = new StringBuilder("\"" + exePath + "\" " + arguments);
            // 只有收輸出的那一端是可繼承的（.NET 開的控制代碼預設都不繼承）
            if (!CreateProcessAsUserW(token, exePath, commandLine, IntPtr.Zero, IntPtr.Zero, output != IntPtr.Zero, CreateNoWindow,
                    IntPtr.Zero, Path.GetDirectoryName(exePath), ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"在工作階段 {sessionId} 開不了幫手");
            return process;
        }
        finally
        {
            Close(token);
            Close(own);
        }
    }

    private static string ReadAll(Stream stream)
    {
        var buffer = new byte[MaxOutputBytes];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0)
                    break;
                total += read;
            }
        }
        catch (IOException)
        {
            // 子程序被結束：有多少算多少
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid))
                return;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const uint HandleFlagInherit = 0x00000001;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint WaitObject0 = 0;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    // TOKEN_PRIVILEGES 是 4 位元組對齊（LUID 是兩個 32 位元）：不指定 Pack 的話 long 會讓 Count 後面多 4 個位元組
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(IntPtr token, int informationClass, ref uint information, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out uint information, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// 看一個工作階段的輸入桌面：以系統權限執行時在那個工作階段開幫手（<c>session-probe</c>）；前景模式沒有系統權限，
/// 直接在自己的工作階段開（只看得到自己看得到的：安全桌面時是存取被拒）
/// </summary>
internal sealed class HelperSessionProbe(bool system, string exePath) : IDeskSessionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public DeskServiceProbe Probe(int sessionId)
    {
        var (exit, output) = system ? SessionProcess.Run(sessionId, exePath, "session-probe", Timeout) : RunDirect();
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return DeskServiceRules.ParseHelperProbe(line, sessionId)
               ?? throw new InvalidOperationException($"幫手的回覆看不懂（結束碼 {exit}）：{Clip(output)}");
    }

    private (int ExitCode, string Output) RunDirect()
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo(exePath, "session-probe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        process.Start();
        var reading = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("幫手逾時");
        }
        return (process.ExitCode, reading.Wait(TimeSpan.FromSeconds(5)) ? reading.Result : string.Empty);
    }

    private static string Clip(string text) => text.Length <= 200 ? text.Trim() : text[..200].Trim() + "…";
}
