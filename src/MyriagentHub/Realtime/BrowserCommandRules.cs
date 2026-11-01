using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>網頁指令在瀏覽器模式的處理方式（web-protocol 第六節第 9 點）</summary>
internal enum BrowserCommandClass
{
    /// <summary>伺服器處理（ready 與擁有者的修改）</summary>
    Server,

    /// <summary>接受、不處理（網頁的被動通知）</summary>
    Ignored,

    /// <summary>只能在電腦上做（desktop_only）</summary>
    DesktopOnly,

    /// <summary>還沒開放（not_available；第 4 階段由各工作包逐一開放）</summary>
    NotAvailable,
}

/// <summary>
/// 瀏覽器送來的 <c>cmd</c> 怎麼處理、被拒時要不要送 toast（設計 14.6；web-protocol 第六節第 9 點）。
/// 名稱比對一律 Ordinal（大小寫有別）；不認得的名稱回 null（呼叫端回 invalid），絕不落到任何會執行的分類。
/// </summary>
internal static class BrowserCommandRules
{
    /// <summary>同一條連線同一個 cmd 被拒時，兩則 toast 至少隔多久</summary>
    public const long ToastIntervalMs = 10_000;

    public const string ToastLevel = "error";

    /// <summary>Owner 路由的指令（轉給對話擁有者的）回 conflict 時的文字：對話不存在、已刪除或不再同步（WP4.5 D4）</summary>
    public const string ConversationGoneToast = "這個對話已經不在了（已刪除或不再同步）";

    private const string SyncPrefix = "sync";

    /// <summary>
    /// 只靠 <c>sync</c> 前綴（後接大寫字母）認得的桌面專屬指令在 toast 紀錄裡共用的鍵：名稱可以是任意長度的字串，
    /// 照原名記的話每條連線的紀錄會被不同的名稱無限撐大（一樣受 10 秒節流）
    /// </summary>
    public const string SyncWildcardToast = "sync*";

    /// <summary>
    /// 伺服器處理的指令；<c>monitor</c> 是這條連線要不要收各電腦的即時狀態（stat1，<c>deviceStats</c>），<c>clientError</c> 是網頁的錯誤回報
    /// （寫進伺服器的記錄，2026-10-23）
    /// </summary>
    public static IReadOnlySet<string> ServerCommands { get; } = Set("ready", "setting", "accountUpdate", "accountReorder", "deviceRename", "monitor", "clientError");

    public static IReadOnlySet<string> IgnoredCommands { get; } = Set("layout", "open", "close", "visible", "loadOlder");

    /// <summary>桌面專屬的指令；<c>sync</c> 後面接大寫字母的名稱另由 <see cref="Classify"/> 一律當成桌面專屬</summary>
    public static IReadOnlySet<string> DesktopOnlyCommands { get; } = Set(
        "syncPair", "syncCancelPair", "syncUnpair", "syncPause", "syncRetry", "syncConfirm", "syncSetPolicy",
        // 第 4 階段的對話同步政策只能在擁有者電腦本機改（設計 14.11 D37）
        "syncSetConversationPolicy",
        "setConversationDefault", "cliApplyNow", "cliContinue", "cliCancelUpdate",
        "chooseFolder", "attach", "addFiles", "pasteImage", "addPathAttachment",
        "openFolder", "openPath", "revealPath", "saveFile", "copyMedia", "docPreview", "openLink", "copyText", "popout", "accountOpenFolder",
        // Codex 的免費重置（2026-10-31）：會真的用掉一次，只在那台的儀表板按（瀏覽器版的帳戶卡只顯示次數）
        "accountResetCredit",
        // 多視窗（2026-10-25）：桌面的開新視窗與分頁在視窗之間搬移（瀏覽器版用瀏覽器自己的分頁與視窗）
        "newWindow", "tabDrag", "tabDragEnd", "tabMoveIn", "moveToNewWindow", "closeWindow",
        // 自動重新命名（標題的右鍵選單）：用這台電腦的帳戶跑 CLI 產生標題（瀏覽器版的選單沒有這一項）
        "autoTitle",
        // 自訂主代理（Antigravity 工作資料夾的 .agents/agents/*.md、Claude 帳戶的代理）與 Claude 的輸出風格：清單是那台的 CLI 回報的
        "setAgent", "setOutputStyle",
        // Codex 的目標、技能與速度：由擁有該 CLI 程序的本機視窗控制。
        "codexControl",
        // Claude 的持續目標：清除那台程序裡的 /goal（瀏覽器版的標頭沒有目標）
        "goalClear",
        // 背景工作的詳細與強制停止：問那台活著的 CLI 程序（瀏覽器版的標頭沒有背景工作）
        "taskDetail", "taskStop",
        // 「最近的指令」的歷史指令：讀那台的對話紀錄（瀏覽器版從已載入的項目找）
        "promptHistory",
        // 取消插進這一輪的訊息：只有那台本機送出的那則可以（瀏覽器版的輸入區沒有這一行）
        "cancelSteer",
        // 回溯：讀那台的 Claude 逐字稿、以那台的程序還原檔案（瀏覽器版的訊息沒有回溯）
        "rewindPreview", "rewind",
        // 取消等額度重置後自動接著做：等待只記在那台（瀏覽器版的標頭沒有）
        "quotaWaitCancel",
        // 匯入本機外部 CLI 歷史對話：唯讀掃描與匯入這台電腦上的 CLI 對話紀錄（瀏覽器版無此按鈕）
        "listExternalConversations", "importExternalConversation",
        // 延伸成新對話：複製那台的對話資料夾與 Claude 逐字稿（瀏覽器版的選單沒有這一項）
        "forkConversation",
        // 工作資料夾的 git 狀態「重新檢查」：讀這台電腦上的 repo（瀏覽器版沒有 git 晶片）
        "gitRefresh",
        // 執行列：在這台電腦建置並執行工作資料夾裡的視窗程式（瀏覽器版沒有執行列）
        "launchAction", "launchSelect", "launchLog", "launchRescan",
        // 停止代理另開的背景程式：結束這台電腦上的程序（瀏覽器版的標頭沒有背景程式）
        "programStop",
        // 儀表板的 MCP：在那台電腦開程序檢查、授權要在那台電腦的瀏覽器完成（瀏覽器版的儀表板沒有 MCP）
        "mcpCheck", "mcpAction",
        // 儀表板的健康度：讀這台電腦各 CLI 的設定資料夾、以這台的帳戶開對話（瀏覽器版沒有健康度）
        "healthCheck", "healthFix",
        // 排程頁：這台電腦的 Windows 排程與以這台的帳戶開的對話（瀏覽器版還沒有排程頁，設計 2026-10-26-schedule-manager 的 S3）
        "scheduleWatch", "scheduleRefresh", "timedSave", "timedAction",
        // 儀表板的每週額度每 5% 的 token：紀錄在這台電腦的資料夾（瀏覽器版的儀表板沒有這一區）
        "usageSegments",
        // app 自我更新是這台電腦自己的（伺服器的 init 沒有 appUpdate，瀏覽器版不顯示橫幅與設定區塊）
        "draftSnapshot", "appUpdateCheck", "appUpdateRestartNow", "appUpdateForce", "appUpdateCancel", "appInstallHere",
        // 桌面遠端操作的附件與檔案下載（sync-protocol 14.7）：桌面的宿主以裝置的存取權杖代傳，瀏覽器版走 E4 與 /f/
        "peerUpload", "peerUploadCancel", "peerFileSave", "draftSetDevice");

    /// <summary>
    /// 第 3 階段回 not_available 的指令。第 4 階段起這是第 4 階段處理者的入口分類（<c>BrowserCommandHandler.TryPhase4Async</c>）：
    /// 型錄路由 Owner／Device 的（含 WP4.7 的 addUploaded、listFiles、fileSearch、fileFetch，WP4.8 的 accountLoginDevice）由 WP4.5 的指令路由轉給電腦，
    /// newConversation、copyItem、searchConversations（側欄的對話搜尋，2026-10-23）由對話指令處理；只有第 5 階段的 takeover、handoffCancel 仍真的回 not_available
    /// </summary>
    public static IReadOnlySet<string> NotAvailableCommands { get; } = Set(
        "newConversation", "send", "cancelQueued", "stop", "answer", "skipQuestion", "approve", "approvePlan",
        "rename", "delete", "setOption", "setAccount", "setFolder", "resetFolder", "removeAttachment",
        "listFiles", "fileSearch", "copyItem", "searchConversations",
        "accountAdd", "accountLogin", "accountLoginCancel", "accountLogout", "accountRemove", "accountRefresh",
        "cliCheck", "cliInstall", "cliUpdate",
        "addUploaded", "fileFetch", "accountLoginDevice", "accountLoginCode", "voiceMessage", "takeover", "handoffCancel",
        // 排程頁（S3）：由那台電腦執行（型錄的 Device 指令，伺服器轉送）
        "scheduleAction", "scheduleAgent",
        // 多人協作（2026-10-30）：擁有者管理協作人與對話的分享，由伺服器處理（CollaboratorCommandService）
        "collabCreate", "collabUpdate", "collabDelete", "collabShare");

    /// <summary>擁有者管理協作人的指令（瀏覽器版由伺服器處理：CollaboratorCommandService；分類照樣是 not_available，由第 4 階段的處理者接手）</summary>
    public static IReadOnlySet<string> CollaboratorServerCommands { get; } = Set(Myriagent.Sync.CollaboratorCommands.All.ToArray());

    /// <summary>
    /// 被拒時不送 toast 的指令：網頁自己會送出的（沒有分頁時的 newConversation、檔案瀏覽器與 @ 提示的 listFiles／fileSearch、電腦狀態面板的 monitor、
    /// 錯誤回報的 clientError、側欄打字時的 searchConversations）與被動通知。
    /// 第 3 階段每次 init 都會讓網頁送 newConversation，提示了等於每次連線都跳錯誤（D17）
    /// </summary>
    public static IReadOnlySet<string> SilentCommands { get; } = Set(
        ["newConversation", "listFiles", "fileSearch", "draftSnapshot", "monitor", "clientError", "searchConversations", .. IgnoredCommands]);

    /// <summary>
    /// 電腦的結果與立即拒絕要不要另送 toast（<paramref name="cmd"/> 是送給電腦的指令名稱）：<see cref="SilentCommands"/> 不送；
    /// 新對話的資料夾挑選（網頁送的 listFiles 由伺服器改成 <c>listFolders</c>，不是網頁指令）同 listFiles 不送，抽屜自己說明
    /// </summary>
    public static bool IsSilent(string cmd) => SilentCommands.Contains(cmd) || cmd == RemoteCommandCatalog.ListFolders;

    /// <summary><c>setting</c> 裡只能在電腦本機改的鍵（設計 8.9、SEC-24）：回 desktop_only，不寫入</summary>
    public static IReadOnlySet<string> DesktopOnlySettingKeys { get; } = Set(
        "instructions", "closeToTray", "autoUpdateCli", "autoUpdateApp", "cliRestartPolicy", "claudeRemoteControl",
        "autoSwitchAccount", "autoSwitchCrossProvider", "shareCrossCliCustomizations", "claudeFallbackModel",
        "claudeIdleResumeWarning", "autoResumeAfterQuota");

    /// <summary>不認得的名稱（含空字串、大小寫不同、<c>__proto__</c>）回 null</summary>
    public static BrowserCommandClass? Classify(string? cmd)
    {
        if (cmd is null)
            return null;
        if (ServerCommands.Contains(cmd))
            return BrowserCommandClass.Server;
        if (IgnoredCommands.Contains(cmd))
            return BrowserCommandClass.Ignored;
        if (DesktopOnlyCommands.Contains(cmd) || IsSyncCommand(cmd))
            return BrowserCommandClass.DesktopOnly;
        if (NotAvailableCommands.Contains(cmd))
            return BrowserCommandClass.NotAvailable;
        return null;
    }

    /// <summary>
    /// 被拒時要送的 toast（level 一律 error）；不提示的指令、限流與沒有對應文字的代碼回 null。
    /// conflict 對型錄路由 Owner 的指令是「對話不在了」，其他（帳戶指令、目錄方法）是「帳戶已刪除」
    /// </summary>
    public static (string Text, string Level)? Toast(string cmd, string error)
    {
        if (SilentCommands.Contains(cmd))
            return null;
        string? text = error switch
        {
            BrowserErrorCodes.DesktopOnly => "這個動作只能在電腦上的 Clode Myriagent 操作",
            BrowserErrorCodes.NotAvailable => "網頁版還不能做這件事",
            BrowserErrorCodes.Conflict when RemoteCommandCatalog.Find(cmd)?.Route == RemoteRoute.Owner => ConversationGoneToast,
            BrowserErrorCodes.Conflict => "這個帳戶已從目錄刪除",
            BrowserErrorCodes.Invalid => "伺服器拒絕了這個要求（格式不正確）",
            BrowserErrorCodes.Unavailable => "伺服器暫時無法處理，請稍後再試",
            _ => null,
        };
        return text is null ? null : (text, ToastLevel);
    }

    /// <summary>同一連線同一 cmd 10 秒內最多一則；時鐘倒退時照送（不讓被往回調的時鐘把提示壓住）</summary>
    public static bool ShouldToast(long? lastToastAtMs, long nowMs) =>
        lastToastAtMs is not { } last || nowMs < last || nowMs - last >= ToastIntervalMs;

    /// <summary><c>{type:"toast", text, level}</c>（web-protocol 第一節）；文字經 JSON 序列化跳脫，網頁以 textContent 顯示</summary>
    public static string ToastJson(string text, string level) =>
        new JObject { ["type"] = "toast", ["text"] = text, ["level"] = level }.ToString(Formatting.None);

    /// <summary>
    /// 目錄方法的拒絕原因（<see cref="CatalogOutcome.Rejected"/>）→ 瀏覽器的錯誤碼：null＝接受；invalid（含帳戶不存在、裝置已撤銷）、
    /// conflict（帳戶已刪除）。新的原因丟例外：要先決定瀏覽器看到什麼，不靜默落進其中一個
    /// </summary>
    public static string? ErrorFor(string? rejected) => rejected switch
    {
        null => null,
        SyncErrorCodes.Invalid => BrowserErrorCodes.Invalid,
        CatalogRejections.Conflict => BrowserErrorCodes.Conflict,
        _ => throw new ArgumentOutOfRangeException(nameof(rejected), rejected, "未知的目錄拒絕原因"),
    };

    /// <summary><c>accountUpdate</c> 的稽核只記改了哪些欄位，不記名稱原文（設計 8.14）</summary>
    public static IReadOnlyList<string> UpdatedFields(string? label, string? color)
    {
        var fields = new List<string>(2);
        if (label is not null)
            fields.Add("label");
        if (color is not null)
            fields.Add("color");
        return fields;
    }

    /// <summary><c>sync</c> 後面接大寫字母（syncPair、syncSetPolicy…）；<c>sync</c>、<c>syncing</c> 不算</summary>
    private static bool IsSyncCommand(string cmd) =>
        cmd.Length > SyncPrefix.Length && cmd.StartsWith(SyncPrefix, StringComparison.Ordinal) && char.IsAsciiLetterUpper(cmd[SyncPrefix.Length]);

    private static IReadOnlySet<string> Set(params string[] names) => names.ToHashSet(StringComparer.Ordinal);
}
