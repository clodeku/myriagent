using Microsoft.Win32;

namespace Myriagent.UI;

/// <summary>這個工作階段的畫面現在的狀態（UI 執行緒）：鎖定、沒有接在任何螢幕上（切換使用者、遠端桌面連線中斷）、螢幕關閉</summary>
internal readonly record struct ScreenPresenceState(bool Locked, bool Detached, bool DisplayOff);

/// <summary>
/// 沒有人看得到畫面時不畫網頁（2026-11-12）。電腦鎖定、螢幕關閉、使用者從別的裝置操作時，這台的 WebView2 以前照樣全速繪製
/// （視窗最小化早就不畫了，AgentWebView.SetMinimized；鎖定與螢幕關閉以前不管）。
/// 判斷來源：工作階段的鎖定與連線（SystemEvents.SessionSwitch，啟動當下以 WTSQuerySessionInformation 問）、
/// 這個工作階段的螢幕開關（GUID_SESSION_DISPLAY_STATUS 的通知：遠端桌面連進來時看的是遠端那邊的螢幕，不是這台實體螢幕）。
/// 判斷錯的代價不對稱：以為看得到而其實沒人看只是多花 CPU，以為沒人看而其實看得到是一片空白——拿不準的一律當成看得到。
/// </summary>
internal static class ScreenPresenceRules
{
    /// <summary>GUID_SESSION_DISPLAY_STATUS 的值：0 關閉、1 開啟、2 變暗。只有 0 算關閉（變暗還看得到；不認得的值當成開啟）</summary>
    public static bool DisplayOff(int state) => state == 0;

    /// <summary>WTSINFOEX 的 SessionFlags：0 鎖定（WTS_SESSIONSTATE_LOCK）、1 沒鎖、0xFFFFFFFF 不知道。只有 0 算鎖定</summary>
    public static bool LockedFromSessionFlags(int flags) => flags == 0;

    /// <summary>系統明確說沒鎖（SessionFlags 是 1）：不知道（其他值）不算，保險只在明確沒鎖時才把鎖定改回來</summary>
    public static bool UnlockedFromSessionFlags(int flags) => flags == 1;

    /// <summary>WTS_CONNECTSTATE_CLASS：4 是 WTSDisconnected（工作階段還在、沒有接在任何螢幕上）。只有 4 算沒接上</summary>
    public static bool DetachedFromConnectState(int state) => state == 4;

    /// <summary>工作階段切換事件之後的狀態（不相干的事件不變）</summary>
    public static ScreenPresenceState Apply(ScreenPresenceState state, SessionSwitchReason reason) => reason switch
    {
        SessionSwitchReason.SessionLock => state with { Locked = true },
        SessionSwitchReason.SessionUnlock => state with { Locked = false },
        SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => state with { Detached = true },
        SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect => state with { Detached = false },
        _ => state,
    };

    /// <summary>
    /// 沒有人看得到：鎖定、沒接在螢幕上，或螢幕關閉。螢幕關閉但這台正被 app 的遠端桌面看著（<paramref name="beingViewed"/>）時算看得到
    /// （對方看到的是擷取的畫面，和實體螢幕開不開無關）；鎖定時遠端桌面擷取不到畫面（只送「鎖定中」的說明），不必畫
    /// </summary>
    public static bool Unseen(ScreenPresenceState state, bool beingViewed)
        => state.Locked || state.Detached || (state.DisplayOff && !beingViewed);

    /// <summary>螢幕關閉之後過多久的鍵盤滑鼠輸入才算「關閉之後有人動過」（關螢幕的那一下按鍵不算）</summary>
    public static readonly TimeSpan InputAfterOffMargin = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 保險（沒有人看得到的期間每幾秒看一次）：鎖定與連線以系統當下回報的為準（漏掉解鎖、重新連線的事件時靠它恢復）；
    /// 沒鎖、接著螢幕，而且螢幕關閉之後有人動過鍵盤滑鼠（<paramref name="inputSinceDisplayOff"/>：輸入會叫醒螢幕），就不當成螢幕關著——
    /// 漏掉螢幕開啟的通知時靠它恢復。寧可多畫，不要讓人對著空白的視窗
    /// </summary>
    public static ScreenPresenceState Recheck(ScreenPresenceState state, bool locked, bool detached, bool inputSinceDisplayOff)
    {
        var next = state with { Locked = locked, Detached = detached };
        if (!locked && !detached && inputSinceDisplayOff)
            next = next with { DisplayOff = false };
        return next;
    }

    /// <summary>螢幕關閉（<paramref name="offAtTick"/>，Environment.TickCount64）之後有沒有輸入：最後一次輸入比關閉晚 InputAfterOffMargin 以上</summary>
    public static bool InputSinceDisplayOff(long offAtTick, long lastInputTick)
        => lastInputTick - offAtTick >= (long)InputAfterOffMargin.TotalMilliseconds;
}
