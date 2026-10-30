namespace Myriagent.Sync;

/// <summary>
/// 交接與強制接手的錯誤代碼（第 5 階段計畫 5.9；docs/sync-protocol.md 第 16 節）：<c>HandoffStartResult.error</c>、<c>HandoffCancelResult.error</c>、
/// <c>HandoffAck.error</c>、<c>HandoffProgress.error</c>、<c>HandoffUpdate.error</c>，以及瀏覽器與桌面網頁的 <c>commandResult</c>（網頁的文字對照在
/// <c>Web/handoffrules.js</c>，WP5.9）。新增代碼要一起改網頁的對照與文件；不認得的代碼網頁顯示通用的說明
/// </summary>
internal static class HandoffCodes
{
    /// <summary>這個對話已有進行中的交接（伺服器 <c>ux_handoffs_active</c>）</summary>
    public const string Busy = "busy";

    /// <summary>要強制接手，但擁有者電腦在線（改用一般的交接）</summary>
    public const string OwnerOnline = "owner_online";

    /// <summary>一般交接，但擁有者電腦失聯（改用強制接手）</summary>
    public const string OwnerOffline = "owner_offline";

    public const string TargetOffline = "target_offline";

    /// <summary>目標電腦（或一般交接時的擁有者電腦）沒有協商出 <c>hand1</c>：app 太舊</summary>
    public const string TargetOutdated = "target_outdated";

    /// <summary>目標電腦沒有這個邏輯帳戶 Ready 的連結</summary>
    public const string NoAccount = "no_account";

    /// <summary>被操作的電腦不允許遠端操作（政策不是 Control，或遠端操作暫停中）</summary>
    public const string Policy = "policy";

    /// <summary>這種代理的對話不能換電腦（Grok、Antigravity；擁有者決定 1）</summary>
    public const string UnsupportedProvider = "unsupported_provider";

    /// <summary>對話的同步政策不是 full（伺服器沒有完整的內容）</summary>
    public const string NotFull = "not_full";

    public const string NotFound = "not_found";

    public const string Deleted = "deleted";

    /// <summary>目標就是目前的擁有者</summary>
    public const string SameDevice = "same_device";

    /// <summary>擁有者回報的日誌與伺服器存的不同（長度、鏈雜湊或整檔 SHA-256）</summary>
    public const string LogMismatch = "log_mismatch";

    /// <summary>擁有者這個對話的事件還沒全部到伺服器（<c>finalDeviceSeq</c> 之前的還沒處理）</summary>
    public const string EventsMissing = "events_missing";

    /// <summary>存放的日誌損毀（掃描重算的鏈雜湊或整檔 SHA-256 不符），或接手端下載的與 <c>conv.lease</c> 不符</summary>
    public const string LogDamaged = "log_damaged";

    /// <summary>擁有者 15 秒內沒有接受，或排空超過期限（afterTurn 30 分、now 3 分）</summary>
    public const string Expired = "expired";

    public const string Cancelled = "cancelled";

    /// <summary>伺服器重啟時還沒到 ready 的交接一律中止（D17）</summary>
    public const string ServerRestart = "server_restart";

    /// <summary>伺服器或這條連線沒有這個功能（開關關著、沒協商出 hand1／log1）</summary>
    public const string NotAvailable = "not_available";

    /// <summary>計畫 5.9 的全部代碼（文件與網頁的對照表以它為準）</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Busy, OwnerOnline, OwnerOffline, TargetOffline, TargetOutdated, NoAccount, Policy, UnsupportedProvider, NotFull, NotFound, Deleted,
        SameDevice, LogMismatch, EventsMissing, LogDamaged, Expired, Cancelled, ServerRestart, NotAvailable,
    ];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}
