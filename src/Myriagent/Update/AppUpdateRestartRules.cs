using Myriagent.Cli;

namespace Myriagent.Update;

/// <summary>為什麼現在不開始套用（設計 9.1 的條件 2，加上取消與放棄後的冷卻，以及條件 1 裡開著 Remote Control 的對話在用）</summary>
internal enum AppRestartBlocker
{
    None,
    CliUpdating,
    LoginInProgress,
    Installing,
    Snoozed,
    RetryLater,

    /// <summary>
    /// 開著 Claude Remote Control 的對話可能正從手機或 claude.ai 在用（IsRemoteInUse）：列出來讓橫幅與設定頁說明原因，
    /// 不然它們會一直承諾「閒置時自動重啟套用」。不是系統工作：按「立即重啟」照樣開始
    /// </summary>
    RemoteInUse,

    /// <summary>
    /// 執行列正在建置（或停止中、接著要重新建置）：結束 app 會中斷建置，之後也不會執行。執行中的程式不算——Release 模式的和 app 脫鉤，app 重啟後接回；
    /// Debug 模式的是 <see cref="LaunchDebugging"/>。列出來讓橫幅與設定頁說明原因；按「立即重啟」照樣開始（建置被中斷）
    /// </summary>
    LaunchBuilding,

    /// <summary>
    /// 執行列有 Debug 模式執行中（或暫停中）的程式：它是 app 的子程序，app 重啟會一起結束它（使用者可能正在偵錯）。
    /// 列出來讓橫幅與設定頁說明原因；按「立即重啟」照樣開始（程式被結束）
    /// </summary>
    LaunchDebugging,

    /// <summary>
    /// 其他電腦的代理正在這台執行遠端指令（docs/sync-protocol.md 14.11；AgentHub.RemoteExecRunning）：重啟會結束它，發起的一方拿不到結果。
    /// 列出來讓橫幅與設定頁說明原因；按「立即重啟」照樣開始（指令被結束）
    /// </summary>
    RemoteExecRunning,

    /// <summary>
    /// 這台正在被遠端操控（遠端桌面，docs/sync-protocol.md 14.16；AgentHub.RemoteDeskActive）：重啟會中斷看的一方。
    /// 列出來讓橫幅與設定頁說明原因；按「立即重啟」照樣開始
    /// </summary>
    RemoteDeskActive,

    /// <summary>
    /// 這台正在和其他電腦傳檔案（AgentHub.FileTransferActive：電腦狀態面板的下載、上傳、替別台送檔案）：重啟會切斷傳輸
    /// （2026-11-10：下載另一台的 9.8 GB 資料夾時，來源那台在途中自動換版重開，一個檔失敗）。
    /// 列出來讓橫幅與設定頁說明原因；按「立即重啟」照樣開始（下載端會從斷掉的位置接著試三分鐘）
    /// </summary>
    FileTransfer,

    /// <summary>
    /// 這台有對話的背景工作還在跑（AgentHub.BackgroundWorkRunning：CLI 追蹤的 run_in_background、Codex 讓出的指令…）：重啟會停掉它們。
    /// 2026-10-28 使用者要求桌面版換版一直等到背景工作都完成（以前排空最多等 30 分鐘就停掉）。按「立即重啟」也不略過：
    /// 記著，背景工作都結束就馬上開始；排空開始之後才出現的背景工作由協調器放棄這次排空、就地接續
    /// </summary>
    BackgroundWork,
}

/// <param name="Staged">有已備妥的新版</param>
/// <param name="AnyWindowForeground">app 有視窗在前景（主視窗或彈出視窗是作用中的視窗）</param>
/// <param name="SinceLastInput">最後一次鍵盤滑鼠輸入到現在（GetLastInputInfo）</param>
/// <param name="SnoozedUntil">使用者取消這次之後，這個時間前不自動開始</param>
/// <param name="RetryAfter">放棄這次（排空逾時、幫手沒有回應）之後，這個時間前不自動開始</param>
/// <param name="UserRequested">使用者按了「立即重啟」（等於條件 1 成立，也略過冷卻）</param>
/// <param name="LaunchBuilding">執行列正在建置（AgentHub.LaunchBuilding）：同 RemoteInUse，按「立即重啟」照樣開始</param>
/// <param name="LaunchDebugging">執行列有 Debug 模式執行中的程式（AgentHub.LaunchDebugging）：同 RemoteInUse，按「立即重啟」照樣開始</param>
/// <param name="RemoteExecRunning">其他電腦的代理正在這台執行遠端指令（AgentHub.RemoteExecRunning）：同 RemoteInUse，按「立即重啟」照樣開始</param>
/// <param name="RemoteDeskActive">這台正在被遠端操控（AgentHub.RemoteDeskActive）：同 RemoteInUse，按「立即重啟」照樣開始</param>
/// <param name="FileTransfer">這台正在和其他電腦傳檔案（AgentHub.FileTransferActive）：同 RemoteInUse，按「立即重啟」照樣開始</param>
/// <param name="BackgroundWork">有對話的背景工作還在跑（AgentHub.BackgroundWorkRunning）：按「立即重啟」也等它們都結束，按「強制更新」不等</param>
/// <param name="Forced">使用者按了「強制更新」（UserRequested 也是 true）：不等背景工作，排空中滿 ForceStopAfter 還沒停下的照立即套用停下</param>
/// <param name="RemoteInUse">
/// 開著 Claude Remote Control 的對話可能正從 claude.ai 或 Claude App 在用（IsRemoteInUse）：同使用者在用（條件 1 不成立），
/// 桌面沒人時正是手機最可能在用的時候。列成阻擋原因 RemoteInUse（橫幅與設定頁說明），按「立即重啟」照樣開始
/// </param>
internal sealed record AppRestartSituation(
    bool Staged,
    bool AnyWindowForeground,
    TimeSpan SinceLastInput,
    bool CliUpdating,
    bool LoginInProgress,
    bool InstallInProgress,
    DateTime Now,
    DateTime? SnoozedUntil,
    DateTime? RetryAfter,
    bool UserRequested,
    bool RemoteInUse = false,
    bool LaunchBuilding = false,
    bool LaunchDebugging = false,
    bool RemoteExecRunning = false,
    bool BackgroundWork = false,
    bool Forced = false,
    bool RemoteDeskActive = false,
    bool FileTransfer = false);

internal enum AppExitAction
{
    Plain,

    /// <summary>結束時換版（幫手 --no-relaunch，只切換、不重新啟動）</summary>
    ApplyOnExit,
}

/// <summary>app 更新什麼時候開始套用、各種時限（設計 9.1、9.2、8.5）</summary>
internal static class AppUpdateRestartRules
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(2);

    /// <summary>開著 Remote Control 的對話多久沒有動靜才算沒人從手機或 claude.ai 在用（看回覆、打字比在桌面慢，比 IdleThreshold 長）</summary>
    public static readonly TimeSpan RemoteIdleThreshold = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 開著 Remote Control 的對話在等回應或有工具在執行時，多久沒有動靜就不再算在用：不設上限的話，一張沒人回的核准卡或一個不會結束的前景指令
    /// 讓自動更新永遠不開始。超過就交給排空的既有規則（開著 Remote Control 的等回應排空滿 10 分鐘照立即套用停下、卡片作廢、新版重新提出；
    /// 工具等它跑完，最多到排空上限 35 分鐘）
    /// </summary>
    public static readonly TimeSpan RemoteWaitThreshold = TimeSpan.FromHours(1);
    /// <summary>
    /// 排空最多多久，超過就放棄這次更新（RetryAfterAbort 後再試）。比背景工作的上限（CliRestartRules.BackgroundWaitLimit，30 分鐘）多 5 分鐘：
    /// 等背景工作的對話滿 30 分鐘才停下，要留時間讓它停下、排空做完，不然一律在停下之前就被放棄（2026-10-24 由 30 分鐘改成 35 分鐘）
    /// </summary>
    public static readonly TimeSpan DrainLimit = TimeSpan.FromMinutes(35);
    public static readonly TimeSpan RetryAfterAbort = TimeSpan.FromHours(1);

    /// <summary>
    /// 排空中有對話開始了背景工作、放棄這次之後多久內不自動再開始（AppRestartBlocker.BackgroundWork 另外擋到背景工作都結束）：
    /// 背景工作一結束就再排空的話，同一個對話又開背景工作時，執行中的對話每次都被停下又就地接續
    /// </summary>
    public static readonly TimeSpan RetryAfterBackground = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 「強制更新」按下後多久，還沒停下的對話（在等背景工作、前景的指令、你的回應）一律照「立即套用」停下（2026-10-28 使用者要求：
    /// 背景工作超過 10 分鐘還在跑就強制重啟）。停掉的背景工作記進 running.json，新版馬上請代理確認
    /// </summary>
    public static readonly TimeSpan ForceStopAfter = TimeSpan.FromMinutes(10);

    /// <summary>換版幫手啟動不了或 10 秒內沒寫 .armed：多半是環境問題（防毒、權限），6 小時後再試（設計第 12 節）</summary>
    public static readonly TimeSpan RetryAfterHelperFailure = TimeSpan.FromHours(6);
    public static readonly TimeSpan SnoozeAfterCancel = TimeSpan.FromHours(6);
    public static readonly TimeSpan ArmedTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DraftSnapshotTimeout = TimeSpan.FromSeconds(2);

    /// <summary>端到端測試（MYRIAGENT_UPDATE_E2E_FAST=1）的檢查週期</summary>
    public static readonly TimeSpan E2ECheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>CLI 正在安裝、更新、排空或安裝正在變動：不開始 app 更新（CLI 的 npm 換到一半不能被 app 重啟打斷）</summary>
    public static bool IsCliUpdating(CliState state)
        => state.Busy || state.UpdatePhase != CliUpdatePhase.None || state.InstallChanging;

    public static bool IsUserIdle(bool anyWindowForeground, TimeSpan sinceLastInput, TimeSpan threshold)
        => !anyWindowForeground || sinceLastInput >= threshold;

    /// <summary>
    /// 一個對話是不是可能正從 claude.ai 或 Claude App 在用（設計 9.1 條件 1；合併 main 的 Remote Control 時加的）：程序開著 Remote Control，而且
    /// idleThreshold 內有動靜（遠端的送出、回覆與核准都經過程序），或在等回應（手機上可能正要核准）、有工具在執行（遠端開始的一輪跑很久的指令時沒有其他動靜）
    /// 而且 waitThreshold 內有動靜。等回應與工具也有上限：沒人回的卡、不會結束的前景指令不能讓自動更新永遠不開始，超過就交給排空的既有規則。
    /// 存活的背景工作不在這裡算：它另外是阻擋原因 AppRestartBlocker.BackgroundWork（2026-10-28 起換版一直等到背景工作都完成）
    /// </summary>
    public static bool IsRemoteInUse(bool remoteControl, bool waitingForUser, bool toolRunning, DateTime lastActivity, DateTime now,
        TimeSpan idleThreshold, TimeSpan waitThreshold)
    {
        if (!remoteControl)
            return false;
        var quiet = now - lastActivity;
        return quiet < idleThreshold || ((waitingForUser || toolRunning) && quiet < waitThreshold);
    }

    public static AppRestartBlocker Blocker(AppRestartSituation s)
    {
        if (s.CliUpdating)
            return AppRestartBlocker.CliUpdating;
        if (s.LoginInProgress)
            return AppRestartBlocker.LoginInProgress;
        if (s.InstallInProgress)
            return AppRestartBlocker.Installing;
        // 「立即重啟」也等背景工作：重啟會停掉它們（2026-10-28 使用者要求一直等到背景工作都完成）；「強制更新」不等
        if (s.UserRequested)
            return s.BackgroundWork && !s.Forced ? AppRestartBlocker.BackgroundWork : AppRestartBlocker.None;
        if (s.SnoozedUntil is { } until && s.Now < until)
            return AppRestartBlocker.Snoozed;
        // 背景工作比放棄後的冷卻更能說明為什麼還沒重啟：排空中有對話開始了背景工作而放棄的，冷卻只有 RetryAfterBackground
        if (s.BackgroundWork)
            return AppRestartBlocker.BackgroundWork;
        if (s.RetryAfter is { } retry && s.Now < retry)
            return AppRestartBlocker.RetryLater;
        // 冷卻期間本來就不自動套用、也比它久：先說明冷卻
        if (s.RemoteInUse)
            return AppRestartBlocker.RemoteInUse;
        if (s.LaunchBuilding)
            return AppRestartBlocker.LaunchBuilding;
        if (s.LaunchDebugging)
            return AppRestartBlocker.LaunchDebugging;
        if (s.RemoteExecRunning)
            return AppRestartBlocker.RemoteExecRunning;
        if (s.RemoteDeskActive)
            return AppRestartBlocker.RemoteDeskActive;
        if (s.FileTransfer)
            return AppRestartBlocker.FileTransfer;
        return AppRestartBlocker.None;
    }

    /// <summary>
    /// 可以開始排空：有備妥的版本、沒有阻擋（沒有人從 Remote Control 在用也在這裡）、使用者沒在用（桌面閒置；或按了立即重啟）。
    /// 執行中的對話不算阻擋（交給排空）
    /// </summary>
    public static bool CanBegin(AppRestartSituation s, TimeSpan idleThreshold)
        => s.Staged && Blocker(s) == AppRestartBlocker.None
           && (s.UserRequested || IsUserIdle(s.AnyWindowForeground, s.SinceLastInput, idleThreshold));

    public static bool DrainExpired(DateTime startedAt, DateTime now) => now - startedAt >= DrainLimit;

    public static AppExitAction OnExit(bool staged, bool windowsShuttingDown)
        => staged && !windowsShuttingDown ? AppExitAction.ApplyOnExit : AppExitAction.Plain;

    public static string DescribeBlocker(AppRestartBlocker blocker) => blocker switch
    {
        AppRestartBlocker.CliUpdating => "CLI 正在更新，完成後再重啟",
        AppRestartBlocker.LoginInProgress => "登入視窗開著，登入完成後再重啟",
        AppRestartBlocker.Installing => "正在安裝到這台電腦",
        AppRestartBlocker.Snoozed => "已取消這次自動重啟，6 小時內不再自動套用",
        AppRestartBlocker.RetryLater => "上次沒有完成，稍後再試",
        AppRestartBlocker.RemoteInUse => "有開著 Remote Control 的對話正在使用或等你回應，空下來後再重啟",
        AppRestartBlocker.LaunchBuilding => "執行列正在建置，建置完再重啟",
        AppRestartBlocker.LaunchDebugging => "執行列有 Debug 模式執行中的程式（重啟會一起關掉它），停止後再重啟",
        AppRestartBlocker.RemoteExecRunning => "其他電腦的代理正在這台執行遠端指令，結束後再重啟",
        AppRestartBlocker.RemoteDeskActive => "這台正在被遠端操控，結束後再重啟",
        AppRestartBlocker.FileTransfer => "正在和其他電腦傳檔案，傳完再重啟",
        AppRestartBlocker.BackgroundWork => "有對話的背景工作還在跑，全部結束後再重啟",
        _ => string.Empty,
    };
}
