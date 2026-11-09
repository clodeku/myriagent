using System.Globalization;
using System.Text;
using Myriagent.Core;
using Myriagent.Remote;

namespace Myriagent.Hub;

/// <summary>遠端動作在這台的通知方式（WP4.4 計畫 D30）</summary>
internal enum RemoteNotifyKind
{
    /// <summary>不通知（只進紀錄）</summary>
    None,

    /// <summary>對話正開在前景的視窗：只對那些視窗顯示一行提示</summary>
    ViewToast,

    /// <summary>系統匣通知（每分鐘最多一則）</summary>
    Tray,
}

/// <summary>
/// 遠端動作的本機通知與啟動提醒（契約 D8、D26；計畫 D30）：層級 1、2 的遠端動作完成（done）而且「通知遠端操作」開著時通知（網頁自動送的
/// listFiles、fileSearch、listFolders 除外）——
/// 對話正開在前景視窗只在那些視窗提示一行，否則系統匣、每分鐘最多一則（其餘只記錄，不彙總）。程式啟動時遠端操作不是「不接受」就提醒一次
/// （不看通知開關：讓「被別的程式改了設定」看得見）。擁有者登入網頁的通知（ownerLogin）依擁有者 2026-10-19 指示不做（計畫 1.3）
/// </summary>
internal static class RemoteNotifyRules
{
    /// <summary>系統匣通知的最小間隔（毫秒）</summary>
    public const long TrayIntervalMs = 60_000;

    /// <summary>遠端動作的系統匣通知標題</summary>
    public const string ActionTitle = "遠端操作";

    /// <summary>啟動提醒的標題</summary>
    public const string StartupReminderTitle = "遠端操作已開啟";

    /// <summary>通知裡的對象名稱（對話標題、帳戶名稱）最多幾個字</summary>
    public const int MaxSubjectLength = 40;

    /// <summary>沒有發起者標籤時的稱呼</summary>
    public const string UnknownIssuer = "瀏覽器";

    /// <summary>發起者稱呼的上限：標籤 64 字，另一台桌面再加「電腦「」」</summary>
    private const int MaxIssuerLength = 70;

    /// <summary>
    /// 發起者的稱呼（通知、遠端操作紀錄、回收區）：瀏覽器是標籤本身（「Chrome・Windows」）；另一台桌面發起的（<c>issuer.kind = device</c>，
    /// 桌面遠端操作 sync-protocol 14.7）是「電腦「DEMO-PC-3」」，和瀏覽器分得出來
    /// </summary>
    public static string IssuerDisplay(string? kind, string? label)
        => string.Equals(kind, Security.CommandIssuerKinds.Device, StringComparison.Ordinal)
            ? "電腦「" + (string.IsNullOrWhiteSpace(label) ? "另一台電腦" : label) + "」"
            : string.Equals(kind, Security.CommandIssuerKinds.Collaborator, StringComparison.Ordinal)
                ? "協作人「" + (string.IsNullOrWhiteSpace(label) ? "未命名" : label) + "」"
                : label ?? string.Empty;

    /// <summary>
    /// 網頁自動送的唯讀指令（@ 提示每停 120 ms 送一次 fileSearch、抽屜每換一層送一次 listFiles）：只記錄、不通知
    /// （和伺服器 <c>BrowserCommandRules.SilentCommands</c> 的檔案部分一致；WP4.7 合併前審查 INT-5）。fileFetch、addUploaded 是使用者的操作，照常通知
    /// </summary>
    internal static readonly IReadOnlySet<string> SilentCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "listFiles", "fileSearch", RemoteCommandCatalog.ListFolders,
        // 電腦狀態面板的檔案傳輸（2026-11-09）：瀏覽每換一層送一次、多選下載每個檔送一次，逐一通知會洗版；收檔（devicePut）做完由處理者提示一次
        RemoteCommandCatalog.DeviceBrowse, RemoteCommandCatalog.DeviceFetch, RemoteCommandCatalog.DevicePut,
        RemoteCommandCatalog.DeviceTree, RemoteCommandCatalog.DeviceFetchMany,
    };

    /// <param name="cmd">型錄的指令名稱（<see cref="SilentCommands"/> 不通知）</param>
    /// <param name="outcome">閘門的結果（層級是最終的）</param>
    /// <param name="notifyEnabled">本機設定「通知遠端操作」（SyncSettings.NotifyRemoteActions）</param>
    /// <param name="shownInForeground">對話正開在前景的視窗（作用中分頁、視窗在前景）</param>
    /// <param name="lastTrayAtMs">上一則遠端動作的系統匣通知（單調時鐘的毫秒；沒有是 null）</param>
    /// <param name="nowMs">現在（同一個單調時鐘）</param>
    public static RemoteNotifyKind Decide(string cmd, RemoteOutcome outcome, bool notifyEnabled, bool shownInForeground, long? lastTrayAtMs, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(outcome);
        if (SilentCommands.Contains(cmd) || !notifyEnabled || !string.Equals(outcome.State, RemoteResultStates.Done, StringComparison.Ordinal) || outcome.Tier < 1)
            return RemoteNotifyKind.None;
        if (shownInForeground)
            return RemoteNotifyKind.ViewToast;
        if (lastTrayAtMs is { } last && nowMs - last < TrayIntervalMs)
            return RemoteNotifyKind.None;
        return RemoteNotifyKind.Tray;
    }

    /// <summary>
    /// 一行說明，例如「Safari・iOS 在『修正登入流程』送出了訊息」：對話的指令寫「在『對話標題』」，帳戶、CLI 與開新對話把對象放在動作後面
    /// （「登出了帳戶『Claude 主帳戶』」）；<paramref name="subject"/> 是 null 或空白就不寫對象
    /// </summary>
    /// <param name="cmd">型錄的指令名稱</param>
    /// <param name="issuerLabel">發起的瀏覽器與作業系統（信封的 issuer.label，WP4.5 的「瀏覽器・作業系統」）</param>
    /// <param name="subject">對話標題、帳戶名稱或代理種類</param>
    public static string Describe(string cmd, string issuerLabel, string? subject)
    {
        var who = Clean(issuerLabel, MaxIssuerLength) ?? UnknownIssuer;
        var (action, inConversation) = ActionOf(cmd);
        if (Clean(subject, MaxSubjectLength) is not { } name)
            return $"{who} {action}";
        return inConversation ? $"{who} 在『{name}』{action}" : $"{who} {action}『{name}』";
    }

    /// <summary>型錄裡每個電腦執行的指令都有自己的說法（測試逐一核對）；不認得的寫指令名稱</summary>
    internal static (string Action, bool InConversation) ActionOf(string cmd) => cmd switch
    {
        // 對話（路由 Owner）
        "send" => ("送出了訊息", true),
        "cancelQueued" => ("取消了一則排隊的訊息", true),
        RemoteCommandCatalog.Stop => ("停止了代理", true),
        "answer" => ("回答了代理的問題", true),
        "skipQuestion" => ("略過了代理的問題", true),
        "approve" => ("回應了核准要求", true),
        "approvePlan" => ("回應了代理的計畫", true),
        "rename" => ("改了對話名稱", true),
        "delete" => ("刪除了對話（已移到回收區）", true),
        "setOption" => ("改了對話的設定", true),
        "setAccount" => ("換了對話的帳戶", true),
        "setFolder" => ("換了工作資料夾", true),
        "resetFolder" => ("把工作資料夾改回預設", true),
        "removeAttachment" => ("移除了一個附件", true),
        "addUploaded" => ("加入了上傳的附件", true),
        "voiceMessage" => ("送出了語音訊息", true),
        "listFiles" => ("瀏覽了工作資料夾的檔案", true),
        "fileSearch" => ("搜尋了工作資料夾的檔案", true),
        "fileFetch" => ("取得了一個檔案", true),

        // 帳戶、CLI 與開新對話（路由 Device）
        "accountAdd" => ("新增了帳戶", false),
        "accountLogin" => ("開始登入帳戶", false),
        "accountLoginCancel" => ("取消了帳戶的登入", false),
        "accountLogout" => ("登出了帳戶", false),
        "accountRemove" => ("移除了帳戶（資料夾保留）", false),
        "accountRefresh" => ("重新整理了帳戶的狀態", false),
        "accountLoginDevice" => ("開始在這台登入目錄的帳戶", false),
        RemoteCommandCatalog.AccountLoginCode => ("送來了 Claude 登入的授權碼", false),
        "cliCheck" => ("檢查了 CLI 的版本", false),
        "cliInstall" => ("安裝了 CLI", false),
        "cliUpdate" => ("更新了 CLI", false),
        RemoteCommandCatalog.ConversationCreate => ("開了新對話", false),
        RemoteCommandCatalog.ListFolders => ("瀏覽了最近資料夾（替新對話挑工作資料夾）", false),
        RemoteCommandCatalog.RemoteExec => ("執行了主控台指令", false),
        RemoteCommandCatalog.ScheduleAction => ("操作了排程", false),
        RemoteCommandCatalog.ScheduleAgent => ("把排程交給代理", false),
        RemoteCommandCatalog.DeviceBrowse => ("瀏覽了這台的資料夾", false),
        RemoteCommandCatalog.DeviceFetch => ("下載了這台的檔案", false),
        RemoteCommandCatalog.DevicePut => ("把檔案傳到這台", false),
        RemoteCommandCatalog.DeviceTree => ("列出了這台整個資料夾的檔案", false),
        RemoteCommandCatalog.DeviceFetchMany => ("下載了這台整個資料夾的檔案", false),
        RemoteCommandCatalog.DeskOpen => ("開始遠端操控這台電腦", false),
        _ => ($"執行了遠端操作 {cmd}", false),
    };

    /// <summary>
    /// 程式啟動時的提醒（契約 D26）：<paramref name="level"/> 是 Hello 回報的政策（暫停中已是 Off）；Off 或不認得的層級回 null（不提醒）
    /// </summary>
    public static string? StartupReminder(string? level, bool allowFullAccess) => level switch
    {
        SyncSettings.LevelView =>
            "其他電腦與網頁可以看這台電腦的對話，但不能做任何操作，連停止代理也不行（遠端操作：只能查看）。要關閉，請到設定的「多電腦同步」，或從系統匣暫停遠端操作",
        SyncSettings.LevelControl =>
            "其他電腦與網頁可以操作這台電腦的對話：送訊息、核准、回答與開新對話（遠端操作：可以操作" + (allowFullAccess ? "，允許完全自動" : "")
            + "）。要關閉，請到設定的「多電腦同步」，或從系統匣暫停遠端操作",
        _ => null,
    };

    /// <summary>
    /// 單行（換行、控制字元與連續空白換成一個空白）、拿掉格式字元（能改寫顯示方向的 U+202E 等）、去掉前後空白、
    /// 超過上限截斷加「…」（不切開代理字對）；空的回 null
    /// </summary>
    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = true;
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                continue;
            if (space && sb.Length > 0)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }

        var line = sb.ToString();
        if (line.Length == 0)
            return null;
        if (line.Length <= max)
            return line;
        var cut = max - 1;
        if (char.IsHighSurrogate(line[cut - 1]))
            cut--;
        return line[..cut] + "…";
    }
}
