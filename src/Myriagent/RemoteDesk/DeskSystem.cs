using System.Runtime.InteropServices;
using Myriagent.UI;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 遠端桌面擷取前要知道的系統狀態（只讀；docs/sync-protocol.md 14.16）。2026-11-12 使用者回報從手機開遠端桌面「無法正常執行」：
/// 那台鎖定中，擷取靠「輸入桌面的名稱不是 Default」判斷鎖定——Windows 11 26300 鎖定時輸入桌面照樣是 Default，判斷不出來，
/// DXGI 一張畫面都不給、工作階段一直空轉，看的一方只看到「連線到…」。所以鎖定另外問系統（和 <see cref="ScreenPresence"/> 同一個來源）
/// </summary>
internal static class DeskSystem
{
    /// <summary>多久重問一次系統（擷取每秒二三十次，鎖定不必問得那麼密）</summary>
    public const int RecheckMs = 1000;

    /// <summary>
    /// 這個工作階段現在鎖定中：系統回報鎖定（WTSINFOEX 的 SessionFlags）而且登入畫面的程序在。兩個來源都這麼說才算——
    /// 判斷成鎖定就不擷取，只憑一個來源判斷錯，遠端桌面在那台就永遠是一句「鎖定中」
    /// </summary>
    public static bool Locked()
        => ScreenPresence.NativeSession.Query() is { } session
           && DeskScreenRules.Locked(ScreenPresenceRules.LockedFromSessionFlags(session.Flags), ScreenPresence.NativeSession.LogonUiRunning);

    /// <summary>
    /// 擷取期間螢幕不要關、電腦不要睡（只在擷取執行緒呼叫；要求跟著執行緒）：螢幕關閉時 DWM 不再出畫面，DXGI 等不到新的影格，
    /// 看的一方只看不動時畫面會停在螢幕關閉前的那一張
    /// </summary>
    public static void KeepDisplayOn(bool on)
        => SetThreadExecutionState(on ? EsContinuous | EsDisplayRequired | EsSystemRequired : EsContinuous);

    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsContinuous = 0x80000000;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}

/// <summary>遠端桌面擷取的判斷（純函式）</summary>
internal static class DeskScreenRules
{
    /// <summary>DXGI 建立後連續幾次（每次等 0.5 秒）沒有第一張畫面就叫醒螢幕</summary>
    public const int WakeAfterMisses = 1;

    /// <summary>連續幾次沒有第一張畫面就改用 GDI（叫醒之後再給兩次機會：螢幕醒來 DWM 才開始出畫面）</summary>
    public const int FallBackAfterMisses = 3;

    /// <summary>擷取一直要重來（<see cref="DeskCapture.Retry"/>）連續幾次之後告訴看的一方（每次約 0.5 秒）</summary>
    public const int RetryStatusAfter = 6;

    /// <summary>
    /// 擷取不到的期間多久重送一次說明：這段期間沒有影格也沒有確認，伺服器 <see cref="Myriagent.Remote.DeskRules.IdleForget"/> 沒有封包就忘掉工作階段
    /// </summary>
    public const int StatusRepeatMs = 60_000;

    /// <summary>鎖定：系統回報鎖定，而且登入畫面的程序在（<paramref name="logonUiRunning"/> 只在系統回報鎖定時才問：列程序比較貴）</summary>
    public static bool Locked(bool flagsSayLocked, Func<bool> logonUiRunning) => flagsSayLocked && logonUiRunning();

    /// <summary>沒有第一張畫面的第 <paramref name="misses"/> 次要做什麼</summary>
    public static (bool Wake, bool FallBack) OnNoFirstFrame(int misses)
        => (misses == WakeAfterMisses, misses >= FallBackAfterMisses);

    /// <summary>擷取不到時給看的一方的說明；可以擷取（或只是正在重來、還沒到 <see cref="RetryStatusAfter"/>）回 null</summary>
    public static string? StatusText(DeskCapture captured, int retries) => captured switch
    {
        DeskCapture.Locked => LockedText,
        DeskCapture.Failed => SecureDesktopText,
        DeskCapture.Retry when retries >= RetryStatusAfter => RetryText,
        _ => null,
    };

    public const string LockedText = "那台電腦目前鎖定中：鎖定畫面遠端看不到也操作不了。要有人在那台電腦前解除鎖定，解除之後這裡會自動恢復";

    public const string SecureDesktopText = "那台目前是系統管理員的確認畫面（或其他受保護的畫面），遠端看不到也操作不了；那台的人處理之後就會恢復";

    public const string RetryText = "那台暫時擷取不到畫面（螢幕可能關著或顯示卡正在重設），正在重試";
}
