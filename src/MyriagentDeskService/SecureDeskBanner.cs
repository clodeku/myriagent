using System.ComponentModel;
using System.Runtime.InteropServices;
using Myriagent.Remote;

namespace MyriagentDesk;

/// <summary>安全桌面上的提示列（真的開視窗；測試以假的換掉）</summary>
internal interface IDeskBanner
{
    /// <summary>顯示（或換文字）；之後 <see cref="SecureDeskBanner.KeepAlive"/> 內沒有再叫就自己收掉。任何執行緒</summary>
    void Show(string text);
}

/// <summary>
/// 被看的電腦在安全桌面（鎖定畫面、系統管理員確認畫面）上的提示列：坐在那台前面的人看得到「這台正被遠端桌面看著／操作」。
/// 使用者的桌面上的提示列是 app 的（<c>RemoteDeskBanner</c>）；安全桌面只有系統權限的幫手畫得上去，所以由幫手自己畫。
/// 一條專用的執行緒：有人在看安全桌面時（擷取每次叫 <see cref="Show"/>）跟著輸入桌面切過去、開一個置頂、不搶焦點、滑鼠穿透的小視窗；
/// 輸入桌面換回使用者的桌面、或一陣子沒有人看了就收掉。視窗以 <c>WDA_EXCLUDEFROMCAPTURE</c> 不出現在傳出去的畫面裡。
/// 盡力而為：開不了視窗、切不過去只記一筆，不影響擷取與輸入（還沒有在真的安全桌面上驗過：鎖定畫面的視窗若在更高的層級，它可能被蓋住）
/// </summary>
/// <param name="anyDesktop">不是安全桌面也顯示（只給測試：一般權限的測試程序開不了安全桌面）</param>
/// <param name="left">視窗左上角的 x（測試放到畫面外；null＝主螢幕上方置中）</param>
internal sealed class SecureDeskBanner(bool anyDesktop = false, int? left = null) : IDeskBanner, IDisposable
{
    /// <summary>最後一次 <see cref="Show"/> 之後多久收掉（擷取迴圈有人看時每秒問好幾次）</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(3);

    private const string ClassName = "MyriagentDeskBanner";

    private readonly object gate = new();
    private string? wanted;
    private long wantedUntilTick;
    private Thread? thread;
    private volatile bool stopping;

    // 以下只在視窗的執行緒用
    private IntPtr window;
    private IntPtr desktop;
    private string? desktopName;
    private string shownText = string.Empty;
    private bool failed;
    private volatile bool visible;

    /// <summary>
    /// 視窗程序與字型是整個程序共用的：視窗類別登記了就一直在（指著這個委派），不能跟著某一個提示列被回收。
    /// 文字放在視窗自己身上（視窗的標題），畫的時候再讀
    /// </summary>
    private static readonly WndProc SharedProcedure = Procedure;

    private static IntPtr sharedFont;

    /// <summary>視窗現在開著（測試與診斷用）</summary>
    public bool Visible => visible;

    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        lock (gate)
        {
            wanted = text;
            wantedUntilTick = Environment.TickCount64 + (long)KeepAlive.TotalMilliseconds;
            if (thread is null && !stopping)
            {
                thread = new Thread(Run) { IsBackground = true, Name = "desk-banner" };
                thread.Start();
            }
        }
    }

    /// <summary>馬上收掉（測試用；平常靠 <see cref="KeepAlive"/>）</summary>
    public void Hide()
    {
        lock (gate)
            wantedUntilTick = 0;
    }

    private void Run()
    {
        try
        {
            while (!stopping)
            {
                string? text;
                lock (gate)
                    text = Environment.TickCount64 < wantedUntilTick ? wanted : null;
                Step(text);
                Pump();
                Thread.Sleep(100);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException)
        {
            ServiceLog.Warn("安全桌面的提示列停止：" + ex.Message);
        }
        finally
        {
            Close();
            if (desktop != IntPtr.Zero)
                CloseDesktop(desktop);
        }
    }

    /// <summary>照現在要不要顯示、輸入桌面是哪一個，開、換或收視窗</summary>
    private void Step(string? text)
    {
        if (text is null || failed)
        {
            Close();
            return;
        }

        var input = InputDesktop.Open(MaximumAllowed);
        if (input == IntPtr.Zero)
        {
            Close();
            return;
        }

        var name = InputDesktop.NameOf(input);
        if (!anyDesktop && !DeskServiceRules.IsSecureDesktop(name))
        {
            // 使用者的桌面：那裡的提示列是 app 的
            CloseDesktop(input);
            Close();
            return;
        }

        if (window != IntPtr.Zero && name == desktopName)
        {
            CloseDesktop(input);
        }
        else
        {
            // 換桌面：執行緒有視窗時切不過去，先收掉
            Close();
            if (!anyDesktop && !SetThreadDesktop(input))
            {
                CloseDesktop(input);
                return;
            }

            if (desktop != IntPtr.Zero)
                CloseDesktop(desktop);
            desktop = input;
            desktopName = name;
            Open(text);
        }

        if (window != IntPtr.Zero && text != shownText)
        {
            shownText = text;
            SetWindowTextW(window, text);
            InvalidateRect(window, IntPtr.Zero, true);
        }
    }

    private void Open(string text)
    {
        var instance = GetModuleHandleW(null);
        var windowClass = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(SharedProcedure),
            Instance = instance,
            ClassName = ClassName,
        };
        // 已經登記過（上一次開過）也算成功
        if (RegisterClassExW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
        {
            Fail("登記視窗類別失敗");
            return;
        }

        var scale = Math.Max(96, (int)GetDpiForSystem()) / 96.0;
        var width = (int)(560 * scale);
        var height = (int)(30 * scale);
        var x = left ?? (GetSystemMetrics(0) - width) / 2;
        shownText = text;
        window = CreateWindowExW(ExTopmost | ExToolWindow | ExNoActivate | ExTransparent | ExLayered, ClassName, text, StylePopup,
            x, 0, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            Fail("開不了視窗");
            return;
        }

        if (sharedFont == IntPtr.Zero)
            sharedFont = CreateFontW(-(int)(14 * scale), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft JhengHei UI");
        SetLayeredWindowAttributes(window, 0, 235, LayeredAlpha);
        // 不出現在傳出去的畫面裡（做不到也照樣顯示：看的一方頂多看到這一條）
        SetWindowDisplayAffinity(window, ExcludeFromCapture);
        ShowWindow(window, ShowNoActivate);
        visible = true;
        ServiceLog.Info($"安全桌面的提示列顯示在 {desktopName}");
    }

    private void Fail(string what)
    {
        failed = true;
        ServiceLog.Warn($"安全桌面的提示列：{what}（錯誤 {Marshal.GetLastWin32Error()}），之後不再試");
    }

    private void Close()
    {
        if (window == IntPtr.Zero)
            return;
        DestroyWindow(window);
        window = IntPtr.Zero;
        visible = false;
    }

    private static void Pump()
    {
        while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private static IntPtr Procedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case MessageEraseBackground:
                return 1;
            case MessagePaint:
                var dc = BeginPaint(hwnd, out var paint);
                try
                {
                    GetClientRect(hwnd, out var rect);
                    var brush = CreateSolidBrush(0x00301C14);
                    FillRect(dc, ref rect, brush);
                    DeleteObject(brush);
                    SetBkMode(dc, 1);
                    SetTextColor(dc, 0x00FFFFFF);
                    var previous = sharedFont != IntPtr.Zero ? SelectObject(dc, sharedFont) : IntPtr.Zero;
                    var buffer = new char[256];
                    var length = GetWindowTextW(hwnd, buffer, buffer.Length);
                    DrawTextW(dc, buffer, length, ref rect, DrawCenter | DrawVerticalCenter | DrawSingleLine | DrawEndEllipsis | DrawNoPrefix);
                    if (previous != IntPtr.Zero)
                        SelectObject(dc, previous);
                }
                finally
                {
                    EndPaint(hwnd, ref paint);
                }

                return IntPtr.Zero;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    /// <summary>給看的人的那一句：知道是誰（服務驗過的信封裡的發起者）就寫出來</summary>
    public static string TextFor(string? viewerLabel)
    {
        var label = new string((viewerLabel ?? string.Empty).Where(c => !char.IsControl(c)).Take(40).ToArray()).Trim();
        return label.Length == 0
            ? "這台電腦的畫面正由 Clode Myriagent 遠端桌面檢視"
            : $"這台電腦正由「{label}」經 Clode Myriagent 遠端桌面操作";
    }

    public void Dispose()
    {
        stopping = true;
        Thread? running;
        lock (gate)
            running = thread;
        running?.Join(TimeSpan.FromSeconds(2));
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private const uint MaximumAllowed = 0x02000000;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint StylePopup = 0x80000000;
    private const uint ExTopmost = 0x00000008;
    private const uint ExTransparent = 0x00000020;
    private const uint ExToolWindow = 0x00000080;
    private const uint ExLayered = 0x00080000;
    private const uint ExNoActivate = 0x08000000;
    private const uint LayeredAlpha = 0x00000002;
    private const uint ExcludeFromCapture = 0x00000011;
    private const int ShowNoActivate = 4;
    private const uint MessagePaint = 0x000F;
    private const uint MessageEraseBackground = 0x0014;
    private const uint DrawCenter = 0x0001;
    private const uint DrawVerticalCenter = 0x0004;
    private const uint DrawSingleLine = 0x0020;
    private const uint DrawNoPrefix = 0x0800;
    private const uint DrawEndEllipsis = 0x8000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Dc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message message, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr dc, char[] text, int length, ref Rect rect, uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, [Out] char[] text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(IntPtr hwnd, string text);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr dc, uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}
