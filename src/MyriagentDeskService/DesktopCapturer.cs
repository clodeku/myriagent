using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Myriagent.Remote;

namespace MyriagentDesk;

/// <summary>
/// 擷取目前的輸入桌面（幫手用；只看、不輸入）。每次擷取先看鍵盤滑鼠現在送到哪個桌面，換了就把擷取執行緒切過去
/// （<c>SetThreadDesktop</c>：執行緒在哪個桌面，<c>GetDC(NULL)</c> 拿到的就是那個桌面的畫面），再以 GDI 把要的範圍縮成要的大小讀回來。
/// 系統權限的幫手切得到安全桌面（鎖定畫面、UAC）；一般權限的（前景模式、測試）只切得到使用者的桌面。
/// 一律在自己的專用執行緒做：切桌面是跟著執行緒的，而且那條執行緒不能有視窗
/// </summary>
internal sealed class DesktopCapturer : IDesktopCapturer, IDisposable
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    private readonly BlockingCollection<Job> jobs = new();
    private readonly Thread thread;

    // 以下只在擷取執行緒用
    private IntPtr desktop;
    private string? desktopName;
    private IntPtr memDc;
    private IntPtr bitmap;
    private IntPtr oldBitmap;
    private IntPtr bits;
    private (int W, int H) dibSize;

    public DesktopCapturer()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "desk-capture" };
        thread.Start();
    }

    public DeskServiceCaptureHeader Capture(DeskServiceCaptureRequest request, byte[] buffer)
    {
        var job = new Job(request, buffer);
        jobs.Add(job);
        // 等不到就丟例外；那一筆之後才做完也沒關係——擷取一次只做一個、照順序，下一筆排在它後面
        if (!job.Done.Wait(CaptureTimeout))
            throw new TimeoutException("擷取逾時");
        return job.Result ?? Failed(null, 0);
    }

    private void Run()
    {
        foreach (var job in jobs.GetConsumingEnumerable())
        {
            try
            {
                job.Result = CaptureCore(job.Request, job.Buffer);
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
            {
                ServiceLog.Warn("擷取時發生錯誤：" + ex.Message);
                job.Result = Failed(desktopName, 0);
            }
            finally
            {
                job.Done.Set();
            }
        }

        ReleaseDib();
        if (desktop != IntPtr.Zero)
            CloseDesktop(desktop);
    }

    private DeskServiceCaptureHeader CaptureCore(DeskServiceCaptureRequest request, byte[] buffer)
    {
        var bytes = DeskServiceRules.CaptureBytes(request.OutW, request.OutH);
        if (buffer.Length < bytes)
            return Failed(desktopName, 0);
        var (name, error) = FollowInputDesktop();
        if (name is null)
            return Failed(null, error);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return Failed(name, Marshal.GetLastWin32Error());
        try
        {
            if (!EnsureDib(screenDc, request.OutW, request.OutH))
                return Failed(name, Marshal.GetLastWin32Error());
            SetStretchBltMode(memDc, Halftone);
            SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
            if (!StretchBlt(memDc, 0, 0, request.OutW, request.OutH, screenDc, request.X, request.Y, request.W, request.H, SrcCopy))
                return Failed(name, Marshal.GetLastWin32Error());
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        GdiFlush();
        Marshal.Copy(bits, buffer, 0, bytes);
        return new DeskServiceCaptureHeader(DeskServiceRules.CaptureOk, bytes, name, DeskServiceRules.IsSecureDesktop(name), 0);
    }

    /// <summary>讓擷取執行緒待在目前的輸入桌面；回它的名稱，開不了或切不過去回 null 與錯誤碼</summary>
    private (string? Name, int Error) FollowInputDesktop()
    {
        var input = InputDesktop.Open(MaximumAllowed);
        if (input == IntPtr.Zero)
            return (null, Marshal.GetLastWin32Error());
        var name = InputDesktop.NameOf(input);
        if (name is null)
        {
            var error = Marshal.GetLastWin32Error();
            CloseDesktop(input);
            return (null, error);
        }

        if (desktop != IntPtr.Zero && name == desktopName)
        {
            // 還在同一個桌面：不必切
            CloseDesktop(input);
            return (name, 0);
        }

        // 換桌面：這條執行緒的 GDI 物件先放掉（記憶體 DC 是照舊桌面的螢幕建的）
        ReleaseDib();
        if (!SetThreadDesktop(input))
        {
            var error = Marshal.GetLastWin32Error();
            CloseDesktop(input);
            return (null, error);
        }

        if (desktop != IntPtr.Zero)
            CloseDesktop(desktop);
        desktop = input;
        desktopName = name;
        ServiceLog.Info($"擷取切到桌面 {name}");
        return (name, 0);
    }

    private bool EnsureDib(IntPtr screenDc, int width, int height)
    {
        if (memDc != IntPtr.Zero && dibSize == (width, height))
            return true;
        ReleaseDib();
        memDc = CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
            return false;
        var info = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            // 負的高度＝由上而下
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        bitmap = CreateDIBSection(memDc, ref info, 0, out bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            ReleaseDib();
            return false;
        }

        oldBitmap = SelectObject(memDc, bitmap);
        dibSize = (width, height);
        return true;
    }

    private void ReleaseDib()
    {
        if (memDc != IntPtr.Zero)
        {
            if (oldBitmap != IntPtr.Zero)
                SelectObject(memDc, oldBitmap);
            DeleteDC(memDc);
        }

        if (bitmap != IntPtr.Zero)
            DeleteObject(bitmap);
        memDc = IntPtr.Zero;
        bitmap = IntPtr.Zero;
        oldBitmap = IntPtr.Zero;
        bits = IntPtr.Zero;
        dibSize = (0, 0);
    }

    private static DeskServiceCaptureHeader Failed(string? name, int error)
        => new(DeskServiceRules.CaptureFailed, 0, name, DeskServiceRules.IsSecureDesktop(name), error);

    public void Dispose()
    {
        jobs.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// 這個程序看到的是實際的像素（每個螢幕各自的縮放）：要求裡的螢幕範圍是 app 以實際像素給的，沒有宣告的程序拿到的是縮放過的座標。
    /// 程序一開始、還沒碰任何視窗與螢幕之前呼叫
    /// </summary>
    public static void UsePhysicalPixels() => SetProcessDpiAwarenessContext(new IntPtr(-4));

    private sealed class Job(DeskServiceCaptureRequest request, byte[] buffer)
    {
        public DeskServiceCaptureRequest Request { get; } = request;

        public byte[] Buffer { get; } = buffer;

        public ManualResetEventSlim Done { get; } = new(false);

        public DeskServiceCaptureHeader? Result { get; set; }
    }

    private const uint MaximumAllowed = 0x02000000;
    private const int Halftone = 4;
    private const uint SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
