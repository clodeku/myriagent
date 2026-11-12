using System.Diagnostics;
using System.Runtime.InteropServices;
using Myriagent.UI;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 這台現在擷取得到什麼（只回報、不判定；docs/sync-protocol.md 14.16）：查「遠端桌面看不到畫面」時在那台跑——
/// <c>tools/desk-probe</c>（獨立的小程式，可以複製到別台執行）與 <c>MYRIAGENT_DESK_IT=1</c> 的測試都呼叫這裡。
/// 回報鎖定、輸入桌面、螢幕開關，再依序以 GDI 直接讀、DXGI（不叫醒螢幕）、DXGI（叫醒螢幕，<c>wake</c> 才做）擷取，
/// 記下每種方式多久拿到第一張、走的是 DXGI 還是 GDI、畫面是不是全黑。除了 <c>wake</c> 的叫醒螢幕之外不送任何輸入
/// </summary>
internal static class DeskDiagnostics
{
    /// <param name="write">一行一行輸出</param>
    /// <param name="wake">最後再以會叫醒螢幕的方式擷取一次（會真的把這台的螢幕叫醒）</param>
    /// <param name="seconds">每種方式擷取多久</param>
    /// <param name="allScreens">每個螢幕都做（預設只做主螢幕）</param>
    public static void Run(Action<string> write, bool wake, int seconds = 3, bool allScreens = false)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var display = new DisplayStateWindow();
        var session = ScreenPresence.NativeSession.Query();
        write($"工作階段：{(session is { } s ? $"連線狀態 {s.ConnectState}、鎖定旗標 {s.Flags}（0 鎖定、1 沒鎖）" : "問不到")}；"
              + $"登入畫面的程序{(ScreenPresence.NativeSession.LogonUiRunning() ? "在" : "不在")}；判斷成鎖定：{DeskSystem.Locked()}");
        write($"輸入桌面是 Default：{GdiDeskScreen.InputDesktopIsDefault()}；螢幕：{display.Read()}");

        using (var gdi = new GdiDeskScreen())
        {
            foreach (var (screen, index) in Chosen(gdi.Screens(), allScreens))
            {
                var (w, h) = gdi.OutputSize(screen.Bounds, 1280);
                var buffer = new byte[w * h * 4];
                var watch = Stopwatch.StartNew();
                var result = gdi.CaptureCore(screen.Bounds, w, h, buffer);
                write($"GDI 直接讀 {Name(screen, index)} → {w}x{h}：{result}（{watch.ElapsedMilliseconds} ms）；{Lit(buffer, result == DeskCapture.Ok)}");
            }
        }

        write($"螢幕：{display.Read()}");
        Dxgi(write, wakeDisplay: false, seconds, allScreens);
        write($"螢幕：{display.Read()}");
        if (!wake)
            return;
        Dxgi(write, wakeDisplay: true, seconds, allScreens);
        write($"螢幕：{display.Read()}");
    }

    private static void Dxgi(Action<string> write, bool wakeDisplay, int seconds, bool allScreens)
    {
        using var screen = new DxgiDeskScreen(wakeDisplay);
        try
        {
            foreach (var (info, index) in Chosen(screen.Screens(), allScreens))
            {
                var (w, h) = screen.OutputSize(info.Bounds, 1280);
                var buffer = new byte[w * h * 4];
                var timeline = new List<string>();
                string? last = null;
                long? firstOk = null;
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < seconds * 1000L)
                {
                    var result = screen.Capture(info.Bounds, w, h, buffer, force: firstOk is null);
                    var step = $"{result}〔{(screen.UsingDxgi ? "DXGI" : "GDI")}〕";
                    if (step != last)
                    {
                        timeline.Add($"{watch.ElapsedMilliseconds} ms {step}");
                        last = step;
                    }

                    if (result == DeskCapture.Ok)
                        firstOk ??= watch.ElapsedMilliseconds;
                    // 擷取的方式換了大小可能不同
                    var size = screen.OutputSize(info.Bounds, 1280);
                    if (size != (w, h))
                    {
                        (w, h) = size;
                        buffer = new byte[w * h * 4];
                    }

                    Thread.Sleep(40);
                }

                write($"DXGI（{(wakeDisplay ? "叫醒螢幕" : "不叫醒")}）{Name(info, index)} → {w}x{h}：{string.Join(" → ", timeline)}；"
                      + (firstOk is { } ms ? $"第一張 {ms} ms；" : string.Empty) + Lit(buffer, firstOk is not null));
            }
        }
        finally
        {
            screen.CaptureEnded();
        }
    }

    private static IEnumerable<(DeskScreenInfo Screen, int Index)> Chosen(IReadOnlyList<DeskScreenInfo> screens, bool all)
        => screens.Select((s, i) => (s, i)).Where(x => all || x.s.Primary);

    private static string Name(DeskScreenInfo screen, int index)
        => $"螢幕 {index}（{screen.Bounds.W}x{screen.Bounds.H}{(screen.Primary ? "、主螢幕" : string.Empty)}）";

    /// <summary>畫面是不是全黑：每 97 個像素取一點</summary>
    private static string Lit(byte[] bgra, bool captured)
    {
        if (!captured)
            return "沒有拿到畫面";
        var lit = 0;
        var total = 0;
        for (var p = 0; p + 3 < bgra.Length; p += 4 * 97)
        {
            total++;
            if (bgra[p] + bgra[p + 1] + bgra[p + 2] > 24)
                lit++;
        }

        return $"取樣 {total} 點裡不是黑的 {lit} 點";
    }

    /// <summary>
    /// 這個工作階段的螢幕開關（GUID_SESSION_DISPLAY_STATUS）：登記通知時系統先送一次目前的狀態，之後變了再送；
    /// 要有視窗收訊息，所以開一個看不到的視窗、讀之前把排著的訊息處理掉
    /// </summary>
    private sealed class DisplayStateWindow : NativeWindow, IDisposable
    {
        private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4ddf-93DB-05FFBD7EFCA5");
        private readonly IntPtr registration;
        private int? state;

        public DisplayStateWindow()
        {
            CreateHandle(new CreateParams());
            var setting = SessionDisplayStatus;
            registration = RegisterPowerSettingNotification(Handle, ref setting, 0);
        }

        public string Read()
        {
            var watch = Stopwatch.StartNew();
            do
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
            while (state is null && watch.ElapsedMilliseconds < 500);
            return state switch
            {
                null => "不知道",
                0 => "關閉",
                1 => "開啟",
                2 => "變暗",
                var other => other.ToString()!,
            };
        }

        protected override void WndProc(ref Message m)
        {
            // WM_POWERBROADCAST＋PBT_POWERSETTINGCHANGE；POWERBROADCAST_SETTING 是 { GUID、DataLength、Data }
            if (m.Msg == 0x0218 && (long)m.WParam == 0x8013 && m.LParam != IntPtr.Zero
                && Marshal.PtrToStructure<Guid>(m.LParam) == SessionDisplayStatus && Marshal.ReadInt32(m.LParam, 16) >= sizeof(int))
            {
                state = Marshal.ReadInt32(m.LParam, 20);
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (registration != IntPtr.Zero)
                UnregisterPowerSettingNotification(registration);
            DestroyHandle();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    }
}
