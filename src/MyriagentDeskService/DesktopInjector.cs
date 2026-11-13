using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Myriagent.Remote;

namespace MyriagentDesk;

/// <summary>把輸入注入目前的安全桌面（真的 <c>SendInput</c>；測試以假的換掉）</summary>
internal interface IDesktopInjector
{
    /// <summary>
    /// 注入到目前的輸入桌面——**只在它是安全桌面時**（鎖定畫面、UAC）。座標是相對於 <paramref name="screen"/>（幫手最近一次擷取的那塊螢幕）的比例。
    /// 現在不是安全桌面、切不過去回 false、什麼都不做：使用者的桌面由 app 以一般權限注入，系統權限的幫手不碰
    /// </summary>
    bool Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen);

    /// <summary>放開還按著的按鍵與滑鼠鍵（工作階段結束、換了一個工作階段）</summary>
    void ReleaseAll();
}

/// <summary>
/// 幫手在安全桌面注入輸入。和擷取一樣在自己的專用執行緒：每次先看輸入桌面，是安全桌面才把執行緒切過去、<c>SendInput</c>。
/// 這裡不判斷輸入是誰送的——呼叫的人（<see cref="AgentPipeHandler"/>）只把「以服務驗過的金鑰解得開」的事件交進來
/// </summary>
internal sealed class DesktopInjector : IDesktopInjector, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<Job> jobs = new();
    private readonly Thread thread;

    // 以下只在注入執行緒用
    private IntPtr desktop;
    private string? desktopName;
    private readonly HashSet<int> keysDown = new();
    private readonly HashSet<int> buttonsDown = new();

    public DesktopInjector()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "desk-inject" };
        thread.Start();
    }

    public bool Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen)
    {
        var job = new Job(events, screen);
        jobs.Add(job);
        return job.Done.Wait(Timeout) && job.Injected;
    }

    public void ReleaseAll()
    {
        var job = new Job(null, default);
        jobs.Add(job);
        job.Done.Wait(Timeout);
    }

    private void Run()
    {
        foreach (var job in jobs.GetConsumingEnumerable())
        {
            try
            {
                job.Injected = job.Events is null ? Release() : InjectCore(job.Events, job.Screen);
            }
            catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException)
            {
                ServiceLog.Warn("注入輸入時發生錯誤：" + ex.Message);
            }
            finally
            {
                job.Done.Set();
            }
        }

        if (desktop != IntPtr.Zero)
            CloseDesktop(desktop);
    }

    private bool InjectCore(IReadOnlyList<DeskInputEvent> events, DeskRect screen)
    {
        if (!FollowSecureDesktop())
            return false;
        var virtualScreen = new DeskRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        var inputs = new List<Input>();
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

        Send(inputs);
        return true;
    }

    private bool Release()
    {
        if (keysDown.Count == 0 && buttonsDown.Count == 0)
            return true;
        var inputs = new List<Input>();
        foreach (var key in keysDown)
            inputs.Add(Key(key, true));
        foreach (var button in buttonsDown)
            inputs.Add(Mouse(0, 0, 0, ButtonFlag(button, false)));
        keysDown.Clear();
        buttonsDown.Clear();
        if (!FollowSecureDesktop())
            return false;
        Send(inputs);
        return true;
    }

    /// <summary>目前的輸入桌面是安全桌面就把這條執行緒切過去；不是（或開不了、切不過去）回 false</summary>
    private bool FollowSecureDesktop()
    {
        var input = InputDesktop.Open(MaximumAllowed);
        if (input == IntPtr.Zero)
            return false;
        var name = InputDesktop.NameOf(input);
        if (!DeskServiceRules.IsSecureDesktop(name))
        {
            CloseDesktop(input);
            return false;
        }

        if (desktop != IntPtr.Zero && name == desktopName)
        {
            CloseDesktop(input);
            return true;
        }

        if (!SetThreadDesktop(input))
        {
            CloseDesktop(input);
            return false;
        }

        if (desktop != IntPtr.Zero)
            CloseDesktop(desktop);
        desktop = input;
        desktopName = name;
        ServiceLog.Info($"輸入切到桌面 {name}");
        return true;
    }

    public void Dispose()
    {
        jobs.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class Job(IReadOnlyList<DeskInputEvent>? events, DeskRect screen)
    {
        public IReadOnlyList<DeskInputEvent>? Events { get; } = events;

        public DeskRect Screen { get; } = screen;

        public ManualResetEventSlim Done { get; } = new(false);

        public bool Injected { get; set; }
    }

    /// <summary>延伸鍵（方向鍵、Insert／Delete／Home／End／PageUp／PageDown、右邊的 Ctrl／Alt、數字鍵盤的除號、Windows 鍵、選單鍵）要帶 EXTENDEDKEY</summary>
    internal static bool IsExtendedKey(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E
        or 0x5B or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5 or 0x90 or 0x2C;

    private static void Send(List<Input> inputs)
    {
        if (inputs.Count == 0)
            return;
        var array = inputs.ToArray();
        if (SendInput((uint)array.Length, array, Marshal.SizeOf<Input>()) == 0)
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
        return new Input { Type = 1, U = new InputUnion { Keyboard = new KeyboardInput { Vk = (ushort)vk, Scan = (ushort)MapVirtualKeyW((uint)vk, 0), Flags = flags } } };
    }

    private static Input Unicode(char ch, bool up)
        => new() { Type = 1, U = new InputUnion { Keyboard = new KeyboardInput { Vk = 0, Scan = ch, Flags = 0x0004u | (up ? 0x0002u : 0u) } } };

    private const uint MaximumAllowed = 0x02000000;
    private const uint MouseMove = 0x0001;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHWheel = 0x1000;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
