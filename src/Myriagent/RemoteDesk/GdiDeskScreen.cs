using System.ComponentModel;
using System.Runtime.InteropServices;
using Myriagent.Remote;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 遠端桌面真的畫面與輸入（docs/sync-protocol.md 14.16）：GDI 的 StretchBlt 擷取（含滑鼠游標，HALFTONE 縮放），<c>SendInput</c> 注入。
/// 一律以 app 的一般權限：輸入桌面不是使用者的桌面（鎖定畫面、UAC 的安全桌面）時不擷取（回 false），以系統管理員執行的視窗 Windows 本來就不讓一般權限的輸入進去——
/// 這是刻意的邊界，不嘗試繞過
/// </summary>
internal sealed class GdiDeskScreen : IDeskScreen
{
    private readonly object inputGate = new();
    private readonly HashSet<int> keysDown = [];
    private readonly HashSet<int> buttonsDown = [];
    private IntPtr memDc;
    private IntPtr dib;
    private IntPtr oldObject;
    private IntPtr bits;
    private int dibWidth;
    private int dibHeight;

    public IReadOnlyList<DeskScreenInfo> Screens()
        => System.Windows.Forms.Screen.AllScreens
            .Select((s, i) => new DeskScreenInfo("螢幕 " + (i + 1), new DeskRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height), s.Primary))
            .ToList();

    public DeskRect VirtualBounds()
    {
        var v = System.Windows.Forms.SystemInformation.VirtualScreen;
        return new DeskRect(v.X, v.Y, v.Width, v.Height);
    }

    public (int W, int H) OutputSize(DeskRect bounds, int? maxWidth) => DeskRules.SendSize(bounds.W, bounds.H, maxWidth);

    public DeskCursorState? Cursor() => DeskCursorReader.Read();

    public DeskCursorImage? CursorImage() => DeskCursorReader.Image();

    /// <summary>GDI 每次都整張讀回（約 50 ms；DXGI 不能用時的備援）</summary>
    public DeskCapture Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force)
    {
        if (bgra.Length < width * height * 4 || !InputDesktopIsDefault())
            return DeskCapture.Failed;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return DeskCapture.Failed;
        try
        {
            EnsureDib(screenDc, width, height);
            SetStretchBltMode(memDc, StretchModeForTest ?? Halftone);
            SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
            if (!StretchBlt(memDc, 0, 0, width, height, screenDc, bounds.X, bounds.Y, bounds.W, bounds.H, SrcCopy))
                return DeskCapture.Failed;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        GdiFlush();
        Marshal.Copy(bits, bgra, 0, width * height * 4);
        return DeskCapture.Ok;
    }

    public void Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen, DeskRect virtualScreen)
    {
        var inputs = new List<Input>();
        lock (inputGate)
        {
            foreach (var e in events)
            {
                switch (e.Type)
                {
                    case "mm":
                        inputs.Add(Move(e, screen, virtualScreen));
                        break;
                    case "md":
                    case "mu":
                        inputs.Add(Move(e, screen, virtualScreen));
                        var down = e.Type == "md";
                        inputs.Add(Mouse(0, 0, 0, ButtonFlag(e.Button, down)));
                        if (down)
                            buttonsDown.Add(e.Button);
                        else
                            buttonsDown.Remove(e.Button);
                        break;
                    case "wh":
                        if (e.Dy != 0)
                            inputs.Add(Mouse(0, 0, unchecked((uint)e.Dy), MouseWheel));
                        if (e.Dx != 0)
                            inputs.Add(Mouse(0, 0, unchecked((uint)e.Dx), MouseHWheel));
                        break;
                    case "kd":
                    case "ku":
                        inputs.Add(Key(e.Key, e.Type == "ku"));
                        if (e.Type == "kd")
                            keysDown.Add(e.Key);
                        else
                            keysDown.Remove(e.Key);
                        break;
                    case "tx":
                        foreach (var ch in e.Text ?? string.Empty)
                        {
                            if (ch == '\n')
                            {
                                inputs.Add(Key(0x0D, false));
                                inputs.Add(Key(0x0D, true));
                            }
                            else if (ch == '\t')
                            {
                                inputs.Add(Key(0x09, false));
                                inputs.Add(Key(0x09, true));
                            }
                            else
                            {
                                inputs.Add(Unicode(ch, false));
                                inputs.Add(Unicode(ch, true));
                            }
                        }

                        break;
                }
            }
        }

        Send(inputs);
    }

    public void ReleaseAll()
    {
        var inputs = new List<Input>();
        lock (inputGate)
        {
            foreach (var key in keysDown)
                inputs.Add(Key(key, true));
            foreach (var button in buttonsDown)
                inputs.Add(Mouse(0, 0, 0, ButtonFlag(button, false)));
            keysDown.Clear();
            buttonsDown.Clear();
        }

        Send(inputs);
    }

    public void Dispose()
    {
        if (memDc != IntPtr.Zero)
        {
            SelectObject(memDc, oldObject);
            DeleteDC(memDc);
            memDc = IntPtr.Zero;
        }

        if (dib != IntPtr.Zero)
        {
            DeleteObject(dib);
            dib = IntPtr.Zero;
        }
    }

    /// <summary>延伸鍵（方向鍵、Insert／Delete／Home／End／PageUp／PageDown、右邊的 Ctrl／Alt、數字鍵盤的除號、Windows 鍵、選單鍵）要帶 EXTENDEDKEY</summary>
    internal static bool IsExtendedKey(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E
        or 0x5B or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5 or 0x90 or 0x2C;

    private static void Send(List<Input> inputs)
    {
        if (inputs.Count == 0)
            return;
        var array = inputs.ToArray();
        if (SendInput((uint)array.Length, array, Marshal.SizeOf<Input>()) == 0 && array.Length > 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static Input Move(DeskInputEvent e, DeskRect screen, DeskRect virtualScreen)
    {
        var (x, y) = DeskRules.ToAbsolute(e.X, e.Y, screen, virtualScreen);
        return Mouse(x, y, 0, MouseMove | MouseAbsolute | MouseVirtualDesk);
    }

    private static uint ButtonFlag(int button, bool down) => button switch
    {
        1 => down ? 0x0020u : 0x0040u,
        2 => down ? 0x0008u : 0x0010u,
        _ => down ? 0x0002u : 0x0004u,
    };

    private static Input Mouse(int x, int y, uint data, uint flags)
        => new() { Type = 0, U = new InputUnion { Mouse = new MouseInput { Dx = x, Dy = y, MouseData = data, Flags = flags } } };

    private static Input Key(int vk, bool up)
    {
        var flags = (up ? 0x0002u : 0u) | (IsExtendedKey(vk) ? 0x0001u : 0u);
        return new Input { Type = 1, U = new InputUnion { Keyboard = new KeyboardInput { Vk = (ushort)vk, Scan = (ushort)MapVirtualKey((uint)vk, 0), Flags = flags } } };
    }

    private static Input Unicode(char ch, bool up)
        => new() { Type = 1, U = new InputUnion { Keyboard = new KeyboardInput { Vk = 0, Scan = ch, Flags = 0x0004u | (up ? 0x0002u : 0u) } } };

    private void EnsureDib(IntPtr screenDc, int width, int height)
    {
        if (memDc != IntPtr.Zero && width == dibWidth && height == dibHeight)
            return;
        Dispose();
        memDc = CreateCompatibleDC(screenDc);
        var info = new BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<BitmapInfo>(),
            Width = width,
            Height = -height, // 由上而下
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };
        dib = CreateDIBSection(screenDc, ref info, 0, out bits, IntPtr.Zero, 0);
        if (memDc == IntPtr.Zero || dib == IntPtr.Zero)
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "建不了擷取用的點陣圖");
        }

        oldObject = SelectObject(memDc, dib);
        dibWidth = width;
        dibHeight = height;
    }

    /// <summary>輸入桌面是使用者的桌面（名稱是 Default）；鎖定畫面與 UAC 的安全桌面（Winlogon）以一般權限開不了或名稱不同</summary>
    internal static bool InputDesktopIsDefault()
    {
        var desk = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desk == IntPtr.Zero)
            return false;
        try
        {
            var buffer = new char[64];
            if (!GetUserObjectInformation(desk, UoiName, buffer, buffer.Length * 2, out _))
                return false;
            var name = new string(buffer).TrimEnd('\0');
            return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desk);
        }
    }

    /// <summary>量效能用：換掉縮放的模式（3＝COLORONCOLOR）</summary>
    internal static int? StretchModeForTest { get; set; }

    private const int Halftone = 4;
    private const uint SrcCopy = 0x00CC0020;
    private const uint MouseMove = 0x0001;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHWheel = 0x1000;
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, [Out] char[] info, int length, out int needed);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
