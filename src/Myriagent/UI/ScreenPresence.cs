using System.Diagnostics;
using System.Runtime.InteropServices;
using Myriagent.Core;
using Microsoft.Win32;

namespace Myriagent.UI;

/// <summary>
/// 這個工作階段的畫面現在有沒有人看得到（規則與由來見 <see cref="ScreenPresenceRules"/>）。UI 執行緒建立與使用：
/// 鎖定與連線聽 SystemEvents.SessionSwitch（事件在別的執行緒，轉回 UI 執行緒），螢幕開關由主視窗把電源設定通知轉進 <see cref="OnDisplayState"/>，
/// 啟動當下的狀態以 WTSQuerySessionInformation 問（app 常在鎖定時自己換版重開，沒有事件可等）。
/// 沒有人看得到的期間每 <see cref="RecheckInterval"/> 重看一次（漏掉事件時的保險，只會改回「看得到」）。
/// </summary>
internal sealed class ScreenPresence : IDisposable
{
    /// <summary>沒有人看得到的期間多久重看一次</summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(3);

    private readonly Control owner;
    private readonly Func<bool> beingViewed;
    private readonly System.Windows.Forms.Timer recheck;
    private ScreenPresenceState state;
    private long displayOffAtTick;
    private bool disposed;

    /// <param name="owner">事件轉回它的執行緒（主視窗）</param>
    /// <param name="beingViewed">這台正被 app 的遠端桌面看著（AgentHub.RemoteDeskActive）</param>
    public ScreenPresence(Control owner, Func<bool> beingViewed)
    {
        this.owner = owner;
        this.beingViewed = beingViewed;
        var session = NativeSession.Query();
        // 啟動當下算鎖定要兩個來源都這麼說（系統回報鎖定＋登入畫面的程序在）：只憑一個來源判斷錯，視窗會一直是空白的
        state = new ScreenPresenceState(
            Locked: session is { } s && ScreenPresenceRules.LockedFromSessionFlags(s.Flags) && NativeSession.LogonUiRunning(),
            Detached: session is { } d && ScreenPresenceRules.DetachedFromConnectState(d.ConnectState),
            DisplayOff: false);
        recheck = new System.Windows.Forms.Timer { Interval = (int)RecheckInterval.TotalMilliseconds };
        recheck.Tick += (_, _) => Recheck();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Publish();
    }

    /// <summary>沒有人看得到畫面（UI 執行緒讀）</summary>
    public bool Unseen { get; private set; }

    /// <summary>看不看得到變了（UI 執行緒；參數是新的 <see cref="Unseen"/>）</summary>
    public event Action<bool>? Changed;

    /// <summary>主視窗收到 GUID_SESSION_DISPLAY_STATUS 的通知（登記時系統會先送一次目前的狀態）</summary>
    public void OnDisplayState(int value)
    {
        if (disposed)
            return;
        var off = ScreenPresenceRules.DisplayOff(value);
        if (off && !state.DisplayOff)
            displayOffAtTick = Environment.TickCount64;
        state = state with { DisplayOff = off };
        Publish();
    }

    /// <summary>遠端桌面開始或結束了：重新判斷</summary>
    public void Refresh()
    {
        if (!disposed)
            Publish();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var reason = e.Reason;
        try
        {
            owner.BeginInvoke(() =>
            {
                if (disposed)
                    return;
                state = ScreenPresenceRules.Apply(state, reason);
                Publish();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 視窗已經關了（app 正在結束）
        }
    }

    private void Recheck()
    {
        if (disposed)
            return;
        // 問不到系統的狀態時照舊（事件來了才變）；問得到時只會把鎖定、沒接上改回來，不會改成鎖定（那是事件的事）
        if (NativeSession.Query() is { } session)
        {
            state = ScreenPresenceRules.Recheck(
                state,
                locked: state.Locked && !ScreenPresenceRules.UnlockedFromSessionFlags(session.Flags),
                detached: state.Detached && ScreenPresenceRules.DetachedFromConnectState(session.ConnectState),
                inputSinceDisplayOff: state.DisplayOff && ScreenPresenceRules.InputSinceDisplayOff(displayOffAtTick, NativeSession.LastInputTick()));
        }

        Publish();
    }

    private void Publish()
    {
        var unseen = ScreenPresenceRules.Unseen(state, beingViewed());
        recheck.Enabled = unseen;
        if (unseen == Unseen)
            return;
        Unseen = unseen;
        Log.Info(unseen
            ? $"畫面沒有人看得到，網頁先不畫（鎖定 {state.Locked}、沒接螢幕 {state.Detached}、螢幕關閉 {state.DisplayOff}）"
            : "畫面看得到了，網頁恢復繪製");
        Changed?.Invoke(unseen);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        recheck.Dispose();
    }

    /// <summary>Windows 的工作階段狀態（只讀）</summary>
    internal static class NativeSession
    {
        private const int WtsCurrentSession = -1;
        private const int WtsSessionInfoEx = 25;

        /// <summary>
        /// 這個工作階段的連線狀態（WTS_CONNECTSTATE_CLASS）與鎖定旗標（WTSINFOEX_LEVEL1 的 SessionFlags）；問不到回 null。
        /// WTSINFOEXW 是 { DWORD Level; 聯集 }，聯集裡有 LARGE_INTEGER 所以從位移 8 開始：SessionId 8、SessionState 12、SessionFlags 16
        /// </summary>
        public static (int ConnectState, int Flags)? Query()
        {
            try
            {
                if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, WtsSessionInfoEx, out var buffer, out var bytes))
                    return null;
                try
                {
                    if (buffer == IntPtr.Zero || bytes < 20 || Marshal.ReadInt32(buffer, 0) != 1)
                        return null;
                    return (Marshal.ReadInt32(buffer, 12), Marshal.ReadInt32(buffer, 16));
                }
                finally
                {
                    WTSFreeMemory(buffer);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        /// <summary>登入畫面的程序（LogonUI.exe）在這個工作階段裡：鎖定畫面顯示著的時候它在</summary>
        public static bool LogonUiRunning()
        {
            try
            {
                var session = Process.GetCurrentProcess().SessionId;
                var found = false;
                foreach (var process in Process.GetProcessesByName("LogonUI"))
                {
                    using (process)
                    {
                        try
                        {
                            found |= process.SessionId == session;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            // 程序剛結束
                        }
                    }
                }

                return found;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        /// <summary>這個工作階段最後一次鍵盤滑鼠輸入的時間（Environment.TickCount64 的刻度）；問不到回 0（當成很久以前）</summary>
        public static long LastInputTick()
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
                return 0;
            // dwTime 是 32 位元的 GetTickCount：以和現在的差距換成 64 位元的刻度（差距不會超過 49 天）
            var idle = unchecked((uint)Environment.TickCount - info.Time);
            return Environment.TickCount64 - idle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);
    }
}
