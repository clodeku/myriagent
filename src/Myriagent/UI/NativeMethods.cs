using System.Runtime.InteropServices;

namespace Myriagent.UI;

/// <summary>Windows API：標題列深色模式與顏色（Windows 11 DWM）、工作列閃爍、前景權限、還原最小化的視窗、使用者閒置時間、AppUserModelID、NTFS 替代資料流</summary>
internal static class NativeMethods
{
    /// <summary>鎖定電腦（同 Win+L；開機自動啟動後鎖定，AutoStartRules.LockAfterStart）。失敗回 false</summary>
    public static bool LockWorkstation()
    {
        try
        {
            return LockWorkStation();
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int ASFW_ANY = -1;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    /// <summary>GUID_SESSION_DISPLAY_STATUS：這個工作階段的螢幕開關（0 關閉、1 開啟、2 變暗）。遠端桌面連進來時是遠端那邊的螢幕</summary>
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4ddf-93DB-05FFBD7EFCA5");

    /// <summary>登記這個工作階段的螢幕開關通知（WM_POWERBROADCAST；登記時系統先送一次目前的狀態）。失敗回 Zero（不通知，當成一直開著）</summary>
    public static IntPtr RegisterDisplayStatus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;
        try
        {
            var setting = SessionDisplayStatus;
            return RegisterPowerSettingNotification(hwnd, ref setting, 0);
        }
        catch (EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    public static void UnregisterDisplayStatus(IntPtr registration)
    {
        if (registration != IntPtr.Zero)
            UnregisterPowerSettingNotification(registration);
    }

    /// <summary>
    /// 這則視窗訊息是不是螢幕開關的通知：是就回它的值（0 關閉、1 開啟、2 變暗），不是回 null。
    /// lParam 是 POWERBROADCAST_SETTING：{ GUID PowerSetting; DWORD DataLength; BYTE Data[] }
    /// </summary>
    public static int? DisplayStatusOf(Message m)
    {
        if (m.Msg != WM_POWERBROADCAST || (long)m.WParam != PBT_POWERSETTINGCHANGE || m.LParam == IntPtr.Zero)
            return null;
        if (Marshal.PtrToStructure<Guid>(m.LParam) != SessionDisplayStatus || Marshal.ReadInt32(m.LParam, 16) < sizeof(int))
            return null;
        return Marshal.ReadInt32(m.LParam, 20);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr registration);
    private const int SW_RESTORE = 9;

    /// <summary>
    /// 把前景權限交出去（第二個實例叫醒第一個實例前呼叫）：使用者剛啟動的程序才能把視窗帶到前景，
    /// 在背景的第一個實例自己呼叫 SetForegroundWindow 會被 Windows 擋下，只閃工作列按鈕
    /// </summary>
    public static void AllowAnyForegroundWindow() => AllowSetForegroundWindow(ASFW_ANY);

    /// <summary>還原最小化的視窗：SW_RESTORE 會回到最小化前的狀態（原本最大化就還原成最大化；WindowState = Normal 只會變成一般大小）</summary>
    public static void Restore(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SW_RESTORE);
    }

    /// <summary>標題列配合主題：深色模式＋與網頁相同的底色（Windows 10 沒有顏色屬性就只套深色模式）</summary>
    public static void ApplyTitleBarTheme(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            int enabled = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
            int caption = dark ? ColorRef(0x18, 0x18, 0x1B) : ColorRef(0xF7, 0xF7, 0xF8);
            int text = dark ? ColorRef(0xE6, 0xE6, 0xEA) : ColorRef(0x1F, 0x1F, 0x24);
            int border = dark ? ColorRef(0x2A, 0x2A, 0x30) : ColorRef(0xDD, 0xDD, 0xE2);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    /// <summary>只改視窗外框的顏色（WindowBorderRules；Windows 10 沒有這個屬性，呼叫會失敗、不影響）</summary>
    public static void SetBorderColor(IntPtr hwnd, byte r, byte g, byte b)
    {
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            int border = ColorRef(r, g, b);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>系統目前是否為深色主題（設定為 system 時使用）</summary>
    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static void Flash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 3,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private const uint FLASHW_ALL = 0x00000003;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    /// <summary>最後一次鍵盤滑鼠輸入到現在（整個工作階段；設計 9.1 的「使用者沒在用」）</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        // TickCount 與 dwTime 都是 32 位元毫秒（約 49.7 天繞回）：無號相減處理繞回
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    /// <summary>前景視窗所屬的程序、是否看得見、是否最小化（沒有前景視窗時程序是 0）：app 更新判斷使用者在不在用（AppControllerRules.IsAppForeground）</summary>
    public static (uint ProcessId, bool Visible, bool Minimized) ForegroundWindowInfo()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return (0, false, false);
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        return (processId, IsWindowVisible(hwnd), IsIconic(hwnd));
    }

    /// <summary>螢幕上這一點最上層的頂層視窗（多視窗：拖曳的分頁放在哪個視窗上；WebView2 的子視窗屬於別的程序，往上找到頂層）；沒有回 IntPtr.Zero</summary>
    public static IntPtr RootWindowAt(Point screenPoint)
    {
        var hwnd = WindowFromPoint(new POINT { X = screenPoint.X, Y = screenPoint.Y });
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
    }

    /// <summary>視窗所屬的程序（拿不到回 0）</summary>
    public static uint ProcessIdOfWindow(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    /// <summary>Esc 現在是按著的（拖曳以 Esc 取消時，dragend 送到宿主那一刻鍵通常還沒放開）</summary>
    public static bool IsEscapeDown() => (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

    private const uint GA_ROOT = 2;
    private const int VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>程序的 AppUserModelID（與開始選單捷徑相同）：工作列釘選綁在捷徑而不是版本資料夾裡的 exe（設計 8.1）</summary>
    public static void SetAppUserModelId(string id) => _ = SetCurrentProcessExplicitAppUserModelID(id);

    /// <summary>刪除檔案的 NTFS 替代資料流（例如瀏覽器加的 Zone.Identifier）；不存在也回 true</summary>
    public static bool DeleteAlternateStream(string path, string stream)
        => DeleteFileW($@"\\?\{Path.GetFullPath(path)}:{stream}") || Marshal.GetLastWin32Error() is 2 or 3;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string path);
}
