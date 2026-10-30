using System.Buffers;
using Myriagent.Security;

namespace Myriagent.Sync;

/// <summary>
/// 多人協作（docs/plans/2026-10-30-collaborators.md、docs/sync-protocol.md 14.14）的共用規則：協作人帳號與顯示名稱的格式、
/// 協作人能送的指令（伺服器的瀏覽器閘道與擁有者電腦的閘門各查一次）、項目上「誰送的」的清理。純函式，伺服器與桌面共用
/// </summary>
internal static class CollaboratorRules
{
    public const int MinUserNameLength = 3;

    public const int MaxUserNameLength = 32;

    /// <summary>顯示名稱的上限（Unicode 字元）：信封的 <c>issuer.label</c> 直接用它，要在 <see cref="CommandEnvelope.MaxIssuerLabelLength"/> 以內</summary>
    public const int MaxDisplayNameLength = 32;

    /// <summary>協作人帳號的上限（自用：幾位就夠；限制只為了畫面與記憶體不會被灌爆）</summary>
    public const int MaxCollaborators = 100;

    /// <summary>一個對話最多分享給幾位</summary>
    public const int MaxSharesPerConversation = 100;

    /// <summary>擁有者自己的帳號與第 2 階段留下的帳號：不能拿來當協作人</summary>
    public static IReadOnlyList<string> ReservedUserNames { get; } = ["admin", "owner"];

    private static readonly SearchValues<char> UserNameChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789._-@");

    /// <summary>
    /// 協作人帳號：去頭尾空白、轉小寫後 3～32 字的英數字與 <c>._-@</c>，第一個字是英數字，不是保留的名稱；不合格回 null。
    /// 登入不分大小寫（Identity 以正規化的名稱找），存成小寫免得兩個只差大小寫的帳號看起來像同一個
    /// </summary>
    public static string? CleanUserName(string? userName)
    {
        var name = userName?.Trim().ToLowerInvariant();
        if (name is null || name.Length is < MinUserNameLength or > MaxUserNameLength)
            return null;
        if (name.AsSpan().IndexOfAnyExcept(UserNameChars) >= 0 || !char.IsAsciiLetterOrDigit(name[0]))
            return null;
        return ReservedUserNames.Contains(name, StringComparer.Ordinal) ? null : name;
    }

    /// <summary>
    /// 顯示名稱：去頭尾空白、連續空白摺成一個後 1～32 個 Unicode 字元，照信封標籤的字元規則（沒有控制、格式、換行類的字元）；不合格回 null
    /// </summary>
    public static string? CleanDisplayName(string? displayName)
    {
        if (displayName is null)
            return null;
        var collapsed = string.Join(' ', displayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0 || !CommandEnvelope.IsValidIssuerLabel(collapsed) || RuneCount(collapsed) > MaxDisplayNameLength)
            return null;
        return collapsed;
    }

    /// <summary>
    /// 項目上的 <c>author</c>、<c>resolvedBy</c>（線上的投影、讀回、渲染）：空的與不合格的（控制字元、超過信封標籤的長度）當成沒有——
    /// 這些欄位只是顯示，壞的值不能讓整筆項目上不了線
    /// </summary>
    public static string? CleanAuthor(string? author)
    {
        var trimmed = author?.Trim();
        return string.IsNullOrEmpty(trimmed) || !CommandEnvelope.IsValidIssuerLabel(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// 協作人的瀏覽器能送的網頁指令（伺服器的瀏覽器閘道，其他一律 invalid）：連線、畫面的被動通知、自己的介面設定、
    /// 分享給他的對話的讀取與複製，以及對話裡的「對話與核准」（擁有者 2026-10-30 的決定）與檔案的瀏覽、下載
    /// </summary>
    public static IReadOnlySet<string> BrowserCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ready", "clientError", "setting", "layout", "open", "close", "visible", "loadOlder", "copyItem", "searchConversations",
        "send", "voiceMessage", "cancelQueued", "stop", "answer", "skipQuestion", "approve", "approvePlan",
        "addUploaded", "removeAttachment", "listFiles", "fileSearch", "fileFetch",
    };

    /// <summary>
    /// 協作人發起的信封（<c>issuer.kind</c> 是 <see cref="CommandIssuerKinds.Collaborator"/>）能帶的遠端指令：都是型錄路由 Owner 的對話指令。
    /// 擁有者電腦的閘門再查一次（伺服器只是第一道）；路由 Device 的帳戶、CLI、排程、新對話一律不收
    /// </summary>
    public static IReadOnlySet<string> RemoteCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "send", "voiceMessage", "cancelQueued", "stop", "answer", "skipQuestion", "approve", "approvePlan",
        "addUploaded", "removeAttachment", "listFiles", "fileSearch", "fileFetch",
    };

    /// <summary>協作人的介面設定能存的鍵（各自一份，存在伺服器、不同步給電腦）：同漫遊偏好的四個鍵</summary>
    public static IReadOnlyList<string> PrefKeys => PrefsRules.Keys;

    private static int RuneCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }
}

/// <summary>擁有者管理協作人的指令名稱（網頁 → 宿主；瀏覽器版由伺服器處理，桌面經裝置 hub 的 <c>CollabCmd</c>）</summary>
internal static class CollaboratorCommands
{
    public const string Create = "collabCreate";

    public const string Update = "collabUpdate";

    public const string Delete = "collabDelete";

    public const string Share = "collabShare";

    public static IReadOnlyList<string> All { get; } = [Create, Update, Delete, Share];

    public static bool IsCollaboratorCommand(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);
}

/// <summary>管理指令被拒的代碼（網頁照它顯示說明；<c>CollabCmdResult.code</c>）</summary>
internal static class CollaboratorCodes
{
    /// <summary>欄位不合格（帳號、顯示名稱、對話 id、清單太長）</summary>
    public const string Invalid = "invalid";

    /// <summary>帳號已經有人用</summary>
    public const string UserNameTaken = "user_name_taken";

    /// <summary>找不到這位協作人或這個對話（已刪除、不再同步）</summary>
    public const string NotFound = "not_found";

    /// <summary>協作人已達上限</summary>
    public const string TooMany = "too_many";

    public const string PasswordTooShort = "password_too_short";

    public const string PasswordTooLong = "password_too_long";

    public const string PasswordInvalid = "password_invalid";

    /// <summary>伺服器暫時無法處理（停機中、資料庫錯誤）</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>一位協作人（給擁有者看的清單；不含密碼與雜湊）</summary>
/// <param name="Online">目前有瀏覽器連線</param>
internal sealed record CollaboratorView(string Id, string UserName, string DisplayName, bool Disabled, long CreatedAt, long? LastLoginAt, bool Online);

/// <summary>
/// 協作人的整份狀態（瀏覽器版的 <c>collaborators</c>、裝置 hub 的 <c>CollabUpdate</c>）：清單與每個對話分享給誰（線上的對話 id → 協作人 id，依清單的順序）。
/// 只給擁有者（擁有者的瀏覽器、協商出 <c>collab1</c> 的桌面）
/// </summary>
internal sealed record CollaboratorsState(IReadOnlyList<CollaboratorView> List, IReadOnlyDictionary<string, IReadOnlyList<string>> Shares);

/// <summary>
/// 裝置 hub 的 <c>CollabCmd</c>（Invoke，collab1）：桌面替擁有者送的管理指令。欄位照 <see cref="Cmd"/>：
/// <c>collabCreate</c> 帶 UserName、DisplayName、Password；<c>collabUpdate</c> 帶 UserId 與要改的 DisplayName／Disabled／Password；
/// <c>collabDelete</c> 帶 UserId；<c>collabShare</c> 帶 ConversationId（線上的對話 id）與 UserIds（整份取代）
/// </summary>
internal sealed record CollabCmdRequest(
    string Cmd,
    string? UserId = null,
    string? UserName = null,
    string? DisplayName = null,
    string? Password = null,
    bool? Disabled = null,
    string? ConversationId = null,
    IReadOnlyList<string>? UserIds = null);

/// <summary><c>CollabCmd</c> 的結果：<see cref="Ok"/> 為 false 時 <see cref="Code"/> 是 <see cref="CollaboratorCodes"/>；新建的帶 <see cref="UserId"/></summary>
internal sealed record CollabCmdResult(bool Ok, string? Code = null, string? UserId = null);
