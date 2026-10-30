using Myriagent.Agents;
using Myriagent.Remote;

namespace Myriagent.Sync;

/// <summary>遠端存取本機檔案的用途（設計 8.7 第 4 步依用途決定範圍）</summary>
internal enum RemoteFilePurpose
{
    /// <summary>原檔（<c>/m/</c>）：只限這個對話項目登記過、或檔案抽屜列出過的精確檔案，而且是媒體格式</summary>
    Media,

    /// <summary>縮圖（<c>/t/</c>）：範圍同 <see cref="Media"/></summary>
    Thumb,

    /// <summary>listFiles／fileSearch 的項目：對話的工作資料夾子樹與附件資料夾（<see cref="RemoteFileScope.ListRoots"/>）</summary>
    List,

    /// <summary>fileFetch（非媒體的預覽與下載）：範圍同 <see cref="List"/></summary>
    File,
}

/// <summary>
/// 一個對話的遠端檔案範圍：在 UI 執行緒取好的快照，之後在背景用（守門員本身不碰中樞）。所有路徑都要是長檔名的完整路徑
/// （呼叫端先以 <see cref="RemoteFileGuard.NormalizeRootAsync"/> 展開；8.3 短名、UNC 的項目一律對不到任何路徑）
/// </summary>
/// <param name="ConversationId">對話 id（給呼叫端記錄用，規則不看）</param>
/// <param name="ListRoots"><see cref="RemoteFilePurpose.List"/>／<see cref="RemoteFilePurpose.File"/> 的範圍（工作資料夾與 attachments\；dirsOnly 時是最近資料夾）</param>
/// <param name="ExactFiles"><see cref="RemoteFilePurpose.Media"/>／<see cref="RemoteFilePurpose.Thumb"/> 的精確檔案（項目登記的媒體與抽屜列出過的媒體）</param>
/// <param name="Folders">禁止清單用的本機資料夾（含這個對話的允許子樹與對話資料夾）</param>
internal sealed record RemoteFileScope(string ConversationId, IReadOnlyList<string> ListRoots, IReadOnlySet<string> ExactFiles,
    RemoteFolderContext Folders);

/// <summary><see cref="RemoteFileGuard"/> 與 <see cref="RemoteFileGuardRules"/> 的拒絕原因（記錄與測試用，不給遠端看）</summary>
internal static class RemoteFileGuardReasons
{
    /// <summary>UNC（<c>\\server\share</c>、<c>\\?\UNC\</c>）：檢查存在就會對外送 SMB 雜湊</summary>
    public const string Unc = "unc";

    /// <summary>裝置命名空間（<c>\\?\</c>、<c>\\.\</c>、<c>\??\</c>）與保留裝置名稱（CON、NUL、COM1…）</summary>
    public const string Device = "device";

    /// <summary>替代資料流（磁碟代號之後還有 <c>:</c>）</summary>
    public const string Ads = "ads";

    /// <summary>8.3 短檔名（展開之後仍是短名，或展開不了）</summary>
    public const string ShortName = "shortname";

    /// <summary>不是「磁碟代號:\」開頭的完整路徑、不合法的字元、結尾是點或空白、<c>..</c> 超出根目錄、太長</summary>
    public const string Invalid = "invalid";

    /// <summary>磁碟不是固定或卸除式（網路磁碟機、光碟、不明）</summary>
    public const string Drive = "drive";

    /// <summary>檔案系統的呼叫逾時</summary>
    public const string Timeout = "timeout";

    /// <summary>路徑上有符號連結、junction 或掛載點（OneDrive 佔位檔除外）</summary>
    public const string Reparse = "reparse";

    /// <summary>不在這個用途的範圍裡</summary>
    public const string Scope = "scope";

    /// <summary>在禁止清單的資料夾裡、禁止清單不完整（fail-closed），或是對話資料夾本身的檔（queue.json、items.json、meta.json）</summary>
    public const string Forbidden = "forbidden";

    /// <summary>檔名在拒絕清單（憑證、金鑰、設定檔）</summary>
    public const string Name = "name";

    /// <summary>媒體與縮圖不是白名單的媒體格式</summary>
    public const string Type = "type";

    /// <summary>不存在（含路徑上的某一層不是資料夾）</summary>
    public const string NotFound = "not_found";

    /// <summary>要開檔案卻是資料夾</summary>
    public const string NotFile = "not_file";

    /// <summary>要檢查資料夾卻是檔案</summary>
    public const string NotDirectory = "not_directory";

    /// <summary>讀不到屬性或開不了（權限、被獨占）</summary>
    public const string Unreadable = "unreadable";

    /// <summary>開檔後取得的真實路徑不是本機磁碟代號的路徑、或換了磁碟代號（subst、掛載點）</summary>
    public const string FinalPath = "final_path";

    /// <summary>檔案有多個硬連結（另一個名字可能在範圍外或在禁止清單裡）</summary>
    public const string HardLink = "hardlink";

    /// <summary>超過這次允許的大小</summary>
    public const string TooLarge = "too_large";
}

/// <summary>
/// 設計 8.7 RemoteFileGuard 的純函式部分（WP4.7 步 7）：第 1 步的字串層、第 4 步的範圍、第 5 步的禁止清單與檔名拒絕清單，
/// 以及 <c>GetFinalPathNameByHandle</c> 結果的解讀。資料夾的禁止清單與正規化沿用 <see cref="RemoteFolderRules"/>，這裡補上
/// 拒絕原因的分類、精確檔案的範圍、檔名清單與重新解析點的標籤。一律不碰檔案系統
/// </summary>
internal static class RemoteFileGuardRules
{
    /// <summary>
    /// 設計 8.7 第 5 步的檔名拒絕清單（不分大小寫；<c>*</c> 是任意多個字元）：就算在範圍裡也不給內容、不給縮圖、不發參照
    /// （listFiles 照樣列出名稱）。設計列的之外另加幾個常見的憑證檔（<c>.netrc</c>、<c>.pgpass</c>、PuTTY 金鑰、Java 金鑰庫、
    /// AWS 的 <c>credentials</c>、<c>.pypirc</c>、<c>.htpasswd</c>、<c>secrets.json</c>、<c>.dockercfg</c>）
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenFileNamePatterns =
    [
        ".credentials.json",
        "auth.json",
        ".claude.json",
        "*.pfx",
        "*.p12",
        "*.pem",
        "*.key",
        "*.ppk",
        "*.kdbx",
        "*.jks",
        "*.keystore",
        "id_rsa*",
        "id_dsa*",
        "id_ecdsa*",
        "id_ed25519*",
        ".env*",
        "appsettings*.json",
        "*.config",
        "*.publishsettings",
        ".npmrc",
        ".git-credentials",
        ".netrc",
        "_netrc",
        ".pgpass",
        ".pypirc",
        ".htpasswd",
        ".dockercfg",
        "credentials",
        "secrets.json",
    ];

    /// <summary>雲端檔案（OneDrive Files On-Demand 等）的重新解析標籤：IO_REPARSE_TAG_CLOUD 與 CLOUD_1～CLOUD_F（第 12～15 位元是變化）</summary>
    private const uint CloudTag = 0x9000001A;

    private const uint CloudTagMask = 0xFFFF0FFF;

    /// <summary>IO_REPARSE_TAG_ONEDRIVE（舊版 OneDrive）</summary>
    private const uint OneDriveTag = 0x80000021;

    /// <summary>IO_REPARSE_TAG_FILE_PLACEHOLDER（Windows 8.1 的 OneDrive 佔位檔）</summary>
    private const uint FilePlaceholderTag = 0x80000015;

    /// <summary>名稱代理位元：符號連結、junction、掛載點這類「指到別處」的標籤都有</summary>
    private const uint NameSurrogateBit = 0x20000000;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>
    /// 檔名在拒絕清單（傳完整路徑時看最後一段；空的名稱一律拒絕）。完整路徑另外擋 <c>.git</c> 資料夾裡（含子模組的
    /// <c>.git\modules\…</c>）名叫 <c>config</c> 的檔：remote 的網址可能帶帳密（設計 8.13「git remote 裡的帳密」不離開電腦），
    /// 單看檔名分不出來
    /// </summary>
    public static bool IsForbiddenFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return true;
        var cut = fileName.LastIndexOfAny(['\\', '/']);
        var name = cut < 0 ? fileName : fileName[(cut + 1)..];
        if (name.Length == 0)
            return true;
        if (cut > 0 && name.Equals("config", StringComparison.OrdinalIgnoreCase)
            && fileName[..cut].Split('\\', '/').Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return true;
        return ForbiddenFileNamePatterns.Any(pattern => MatchesPattern(name, pattern));
    }

    /// <summary>
    /// 第 1 步（字串層，不碰檔案系統）：null＝通過，<paramref name="normalized"/> 是正規化後的「大寫磁碟代號:\a\b」。
    /// 回 <see cref="RemoteFileGuardReasons.ShortName"/> 時 <paramref name="normalized"/> 也有值（其他都合格、只是有 8.3 短名，
    /// 守門員確認磁碟種類之後以 GetLongPathName 展開再重跑）；其他原因時是 null
    /// </summary>
    public static string? CheckSyntax(string? path, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(path) || path.Length > RemoteFolderRules.MaxPathLength)
            return RemoteFileGuardReasons.Invalid;
        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            // \\?\、\\.\（含 / 的寫法，Windows 一樣認）是裝置命名空間，\\?\UNC\ 與其他兩個分隔字元開頭的都是 UNC
            if (path.Length >= 4 && path[2] is ('?' or '.') && IsSeparator(path[3]))
            {
                return path.Length >= 8 && path.AsSpan(4, 3).Equals("UNC", StringComparison.OrdinalIgnoreCase) && IsSeparator(path[7])
                    ? RemoteFileGuardReasons.Unc
                    : RemoteFileGuardReasons.Device;
            }

            return RemoteFileGuardReasons.Unc;
        }

        if (path.Length >= 4 && IsSeparator(path[0]) && path[1] == '?' && path[2] == '?' && IsSeparator(path[3]))
            return RemoteFileGuardReasons.Device;
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || !IsSeparator(path[2]))
            return RemoteFileGuardReasons.Invalid;
        if (path.IndexOf(':', 2) >= 0)
            return RemoteFileGuardReasons.Ads;

        var segments = new List<string>();
        var hasShortName = false;
        foreach (var segment in path[3..].Split('\\', '/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    return RemoteFileGuardReasons.Invalid;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (segment.Any(c => c < 0x20 || c is '<' or '>' or '"' or '|' or '?' or '*') || segment[^1] is '.' or ' ')
                return RemoteFileGuardReasons.Invalid;
            if (ReservedNames.Contains(segment.Split('.')[0].TrimEnd(' '), StringComparer.OrdinalIgnoreCase))
                return RemoteFileGuardReasons.Device;
            hasShortName |= RemoteFolderRules.IsShortName(segment);
            segments.Add(segment);
        }

        var candidate = char.ToUpperInvariant(path[0]) + @":\" + string.Join('\\', segments);
        if (hasShortName)
        {
            normalized = candidate;
            return RemoteFileGuardReasons.ShortName;
        }

        // 以 WP4.1 的正規化再確認一次（兩邊的規則要一致；不一致時寧可拒絕）
        if (!RemoteFolderRules.TryNormalize(path, out var confirmed) || !string.Equals(confirmed, candidate, StringComparison.Ordinal))
            return RemoteFileGuardReasons.Invalid;
        normalized = confirmed;
        return null;
    }

    /// <summary>
    /// 第 1、4、5 步（檔案）：null＝通過，否則是拒絕原因（<see cref="RemoteFileGuardReasons"/>）。依序：字串層（8.3 短名在這裡就是拒絕，
    /// 守門員要先展開）→ 範圍（媒體與縮圖只收 <see cref="RemoteFileScope.ExactFiles"/>，列檔與 fileFetch 用 <see cref="RemoteFolderRules.IsInScope"/>）
    /// → 磁碟種類（<paramref name="driveKind"/> 由呼叫端在背景查好）→ 禁止清單（含對話資料夾本身的檔）→ 檔名拒絕清單 → 媒體格式
    /// </summary>
    public static string? CheckString(string path, RemoteFilePurpose purpose, RemoteFileScope scope, RemoteDriveKind driveKind)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var reason = CheckSyntax(path, out var normalized);
        if (reason != null)
            return reason;
        reason = CheckScope(normalized!, purpose, scope) ?? CheckDenied(normalized!, purpose, driveKind, scope);
        if (reason != null)
            return reason;
        if (IsForbiddenFileName(normalized))
            return RemoteFileGuardReasons.Name;
        if (purpose is (RemoteFilePurpose.Media or RemoteFilePurpose.Thumb) && AgentAttachmentRules.GetMediaContentType(normalized!) == null)
            return RemoteFileGuardReasons.Type;
        return null;
    }

    /// <summary>
    /// 資料夾（listFiles 列的資料夾、dirsOnly）的第 1、4、5 步：範圍是 <see cref="RemoteFileScope.ListRoots"/>、禁止清單照判，
    /// 不看檔名拒絕清單（那是檔案的名稱）
    /// </summary>
    public static string? CheckDirectoryString(string path, RemoteFileScope scope, RemoteDriveKind driveKind)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var reason = CheckSyntax(path, out var normalized);
        if (reason != null)
            return reason;
        return CheckScope(normalized!, RemoteFilePurpose.List, scope) ?? CheckDenied(normalized!, RemoteFilePurpose.List, driveKind, scope);
    }

    /// <summary>
    /// <c>GetFinalPathNameByHandle</c>（VOLUME_NAME_DOS）的結果 → 「磁碟代號:\…」：只收 <c>\\?\X:\</c> 開頭的，去掉 <c>\\?\</c>；
    /// <c>\\?\UNC\</c>（檔案其實在網路上）、<c>\\?\Volume{…}\</c>、<c>\\.\</c> 與其他前綴一律回 null（拒絕）
    /// </summary>
    public static string? FromFinalPath(string? finalPath)
    {
        const string prefix = @"\\?\";
        if (finalPath is null || !finalPath.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = finalPath[prefix.Length..];
        if (rest.Length < 3 || !char.IsAsciiLetter(rest[0]) || rest[1] != ':' || rest[2] != '\\')
            return null;
        return rest;
    }

    /// <summary>
    /// 第 3 步：放行的重新解析點只有雲端檔案的佔位檔（OneDrive Files On-Demand 的 CLOUD 系列、舊版 OneDrive 與 Windows 8.1 的佔位檔）；
    /// 符號連結、junction、掛載點（名稱代理位元）與其他所有標籤（AppExecLink、重複資料刪除、WCI…）一律拒絕
    /// </summary>
    public static bool IsAllowedReparseTag(uint tag)
    {
        if (IsNameSurrogate(tag))
            return false;
        return (tag & CloudTagMask) == CloudTag || tag is OneDriveTag or FilePlaceholderTag;
    }

    /// <summary>名稱代理（符號連結、junction、掛載點這類「指到別處」的標籤）：路徑走到這一層會被導到連結的目標（遠端選資料夾時要解析，不能直接跟進）</summary>
    public static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;

    /// <summary>第 4 步的範圍：媒體與縮圖只收精確檔案（正規化後不分大小寫相同），列檔與 fileFetch 要在任何一個範圍資料夾裡</summary>
    private static string? CheckScope(string normalized, RemoteFilePurpose purpose, RemoteFileScope scope)
    {
        var inScope = purpose switch
        {
            RemoteFilePurpose.Media or RemoteFilePurpose.Thumb => (scope.ExactFiles ?? Enumerable.Empty<string>()).Any(file =>
                RemoteFolderRules.TryNormalize(file, out var exact) && string.Equals(exact, normalized, StringComparison.OrdinalIgnoreCase)),
            RemoteFilePurpose.List or RemoteFilePurpose.File => RemoteFolderRules.IsInScope(normalized, scope.ListRoots ?? []),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "未知的遠端檔案用途"),
        };
        return inScope ? null : RemoteFileGuardReasons.Scope;
    }

    /// <summary>第 2 步的結果與第 5 步的資料夾部分（磁碟種類先判，禁止清單不完整一律禁止）</summary>
    private static string? CheckDenied(string normalized, RemoteFilePurpose purpose, RemoteDriveKind driveKind, RemoteFileScope scope)
    {
        if (driveKind is not (RemoteDriveKind.Fixed or RemoteDriveKind.Removable))
            return RemoteFileGuardReasons.Drive;
        if (IsConversationMedia(normalized, purpose, scope.Folders))
        {
            // 只免除「在資料根目錄裡」這一條（允許的子資料夾就是這個檔本身）；其他禁止的資料夾與不完整的清單照判
            return RemoteFolderRules.IsForbidden(normalized, driveKind, scope.Folders with { AllowedDataSubtrees = [normalized] })
                ? RemoteFileGuardReasons.Forbidden
                : null;
        }

        if (InConversationFolderOutsideSubtrees(normalized, scope.Folders))
            return RemoteFileGuardReasons.Forbidden;
        return RemoteFolderRules.IsForbidden(normalized, driveKind, scope.Folders) ? RemoteFileGuardReasons.Forbidden : null;
    }

    /// <summary>
    /// 媒體與縮圖要的是這個對話資料夾裡（不是它本身）的媒體檔：代理常把截圖存在對話資料夾（例如 <c>conversations\{id}\screens\</c>），
    /// 設計 8.7 第 4 步的媒體範圍只要求「項目登記過的精確檔案、白名單的媒體格式」，queue.json、items.json、meta.json 與備份檔都不是媒體格式
    /// （2026-10-23：原本連媒體也擋，網頁版與其他電腦看到的是破圖）。列檔與 fileFetch 照舊只給工作資料夾與 attachments\
    /// </summary>
    private static bool IsConversationMedia(string normalized, RemoteFilePurpose purpose, RemoteFolderContext? folders)
    {
        if (purpose is not (RemoteFilePurpose.Media or RemoteFilePurpose.Thumb) || folders is null || AgentAttachmentRules.GetMediaContentType(normalized) == null)
            return false;
        return RemoteFolderRules.TryNormalize(folders.ConversationDirectory, out var conversation)
               && !string.Equals(normalized, conversation, StringComparison.OrdinalIgnoreCase)
               && RemoteFolderRules.IsWithin(normalized, conversation);
    }

    /// <summary>
    /// 在對話資料夾裡、但不在它的允許子樹（工作資料夾、attachments\）裡：queue.json（含秘密回答的 CLI 原文）、items.json（沒經 DLP 遮蔽）、
    /// meta.json 與它們的備份。資料根目錄的禁止規則本來就擋，這裡不依賴資料根目錄再擋一次（對話資料夾不在資料根目錄的設定也擋得住）
    /// </summary>
    private static bool InConversationFolderOutsideSubtrees(string normalized, RemoteFolderContext folders)
    {
        if (folders is null)
            return true;
        if (string.IsNullOrEmpty(folders.ConversationDirectory))
            return false;
        if (!RemoteFolderRules.TryNormalize(folders.ConversationDirectory, out var conversation))
            return true;
        if (!RemoteFolderRules.IsWithin(normalized, conversation))
            return false;
        return !(folders.AllowedDataSubtrees ?? []).Any(subtree =>
            RemoteFolderRules.TryNormalize(subtree, out var allowed)
            && !string.Equals(allowed, conversation, StringComparison.OrdinalIgnoreCase)
            && RemoteFolderRules.IsWithin(allowed, conversation)
            && RemoteFolderRules.IsWithin(normalized, allowed));
    }

    /// <summary>只認 <c>*</c> 的萬用字元比對（不分大小寫）</summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1)
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        if (!name.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
            return false;
        var position = parts[0].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var found = name.IndexOf(parts[i], position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;
            position = found + parts[i].Length;
        }

        var last = parts[^1];
        return name.Length - position >= last.Length && name.EndsWith(last, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
