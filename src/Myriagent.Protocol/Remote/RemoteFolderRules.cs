using System.Diagnostics.CodeAnalysis;

namespace Myriagent.Remote;

/// <summary>磁碟的種類（呼叫端以 GetDriveType 取得；檢查本身要在背景執行緒、有逾時，設計 8.7 第 2 步）</summary>
internal enum RemoteDriveKind
{
    Unknown,
    Fixed,
    Removable,
    Network,
    CdRom,
    Ram,
    NoRootDirectory,
}

/// <summary>
/// 算禁止清單用的本機資料夾（呼叫端給，規則本身不讀環境變數）。每個資料夾都要先以 GetLongPathName＋GetFullPath 展開成長檔名的完整路徑：
/// 規則不收 8.3 短檔名、相對路徑與結尾是點的名稱，只要有一個非空的資料夾正規化不了，禁止清單就不完整，<see cref="RemoteFolderRules.IsForbidden(string?, RemoteDriveKind, RemoteFolderContext)"/>
/// 一律回 true（fail-closed）
/// </summary>
/// <param name="UserProfile">%USERPROFILE%</param>
/// <param name="LocalAppData">%LOCALAPPDATA%</param>
/// <param name="RoamingAppData">%APPDATA%（不知道是 null）</param>
/// <param name="AppDataRoot">這個程式實際的資料根目錄（預設 %LOCALAPPDATA%\Myriagent；<c>--data</c> 時是那個資料夾）</param>
/// <param name="AccountConfigDirectories">各帳戶的設定資料夾（隔離帳戶的 CLAUDE_CONFIG_DIR／CODEX_HOME）</param>
/// <param name="AllowedDataSubtrees">
/// 目前這個對話在資料根目錄裡可以給遠端的子資料夾（預設的工作資料夾 <c>conversations\{id}\workspace</c> 與 <c>conversations\{id}\attachments</c>）：
/// 只免除「在資料根目錄裡」這一條，其他禁止的資料夾照判；不在 <paramref name="ConversationDirectory"/> 裡面的項目不算
/// </param>
/// <param name="ConversationDirectory">目前這個對話的資料夾 <c>conversations\{id}</c>（它本身與裡面的 queue.json、items.json、meta.json 不免除）</param>
internal sealed record RemoteFolderContext(
    string UserProfile,
    string LocalAppData,
    string? RoamingAppData,
    string AppDataRoot,
    IReadOnlyList<string> AccountConfigDirectories,
    IReadOnlyList<string>? AllowedDataSubtrees = null,
    string? ConversationDirectory = null);

/// <summary>
/// 遠端選資料夾、列檔案的路徑規則（設計 8.7、14.11 D14、D15、Q4-A）：純字串比對，不碰檔案系統（光是檢查存在，UNC 就會對外送 SMB 雜湊）。
/// <list type="bullet">
/// <item>正規化（<see cref="TryNormalize"/>）：只收「磁碟代號:\」開頭的完整路徑，解析 <c>.</c>、<c>..</c>（不能超出根目錄）、
/// 合併重複的分隔字元、去掉結尾的分隔字元；UNC、<c>\\?\</c>、<c>\\.\</c>、<c>\??\</c>、替代資料流、保留裝置名稱、結尾是點或空白的名稱
/// （Windows 會默默去掉，比對會被繞過）、8.3 短檔名（<c>PROGRA~1</c>：不讀檔案系統就不知道對到哪個資料夾，呼叫端先以 GetLongPathName 展開）一律不收。</item>
/// <item>比對一律不分大小寫、以「資料夾＋分隔字元」為界（<c>C:\repo2</c> 不在 <c>C:\repo</c> 裡）。</item>
/// </list>
/// </summary>
internal static class RemoteFolderRules
{
    /// <summary>Windows 的路徑長度上限</summary>
    public const int MaxPathLength = 32_767;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>
    /// 正規化成「大寫磁碟代號:\a\b」（根目錄是「C:\」）；不是本機磁碟的完整路徑、或有上面說的任何一種名稱回 false
    /// </summary>
    public static bool TryNormalize(string? path, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength)
            return false;
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            return false;

        var segments = new List<string>();
        foreach (var segment in path[3..].Split('\\', '/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    return false;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (!IsPlainName(segment))
                return false;
            segments.Add(segment);
        }

        normalized = char.ToUpperInvariant(path[0]) + @":\" + string.Join('\\', segments);
        return true;
    }

    /// <summary><paramref name="path"/> 是 <paramref name="root"/> 本身或在它裡面（兩者都先正規化；任一個不合格回 false）</summary>
    public static bool IsWithin(string? path, string? root)
        => TryNormalize(path, out var p) && TryNormalize(root, out var r) && IsWithinNormalized(p, r);

    /// <summary>
    /// 在任何一個範圍裡（listFiles／fileSearch 的範圍：對話的工作資料夾與對話的 attachments 子資料夾，D14；不是整個對話資料夾——
    /// queue.json、items.json 是 CLI 原文）。範圍內的路徑還要再過 <see cref="IsForbidden(string?, RemoteDriveKind, RemoteFolderContext)"/>
    /// </summary>
    public static bool IsInScope(string? path, IEnumerable<string?> roots) => roots.Any(root => IsWithin(path, root));

    /// <summary>「已知資料夾」：本機使用者選過的（最近資料夾）其中一個，正規化後相同（遠端選的不算，也不寫回最近清單，D14、D15）</summary>
    public static bool IsKnown(string? path, IEnumerable<string?> knownFolders)
        => TryNormalize(path, out var p) && knownFolders.Any(k => TryNormalize(k, out var known) && string.Equals(p, known, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 禁止清單的資料夾（就算在範圍內也不收，設計 8.7 第 5 步的資料夾部分；檔名的拒絕清單在 WP4.7 的 RemoteFileGuardRules）：
    /// 程式的資料根目錄（含各帳戶與同步資料）、%LOCALAPPDATA%\Myriagent、各帳戶的設定資料夾、<c>~\.claude</c>、<c>~\.claude.json</c>、
    /// <c>~\.codex</c>、<c>~\.ssh</c>、Windows 認證管理員的資料夾、各瀏覽器的設定檔資料夾。只列正規化得了的項目（null、空字串略過）；
    /// 要知道清單完不完整用 <see cref="TryGetForbiddenRoots"/>
    /// </summary>
    public static IReadOnlyList<string> ForbiddenRoots(RemoteFolderContext context)
    {
        TryGetForbiddenRoots(context, out var roots, out _);
        return roots;
    }

    /// <summary>
    /// 禁止清單（同 <see cref="ForbiddenRoots"/>）而且完整：有任何一個非空的資料夾正規化不了（8.3 短檔名、相對路徑、UNC…）回 false，
    /// <paramref name="unusable"/> 是第一個這樣的資料夾（呼叫端記錄用）。不完整的清單不能拿來判斷，<see cref="IsForbidden(string?, RemoteDriveKind, RemoteFolderContext)"/> 一律禁止
    /// </summary>
    public static bool TryGetForbiddenRoots(RemoteFolderContext context, out IReadOnlyList<string> roots, out string? unusable)
    {
        var complete = TryCollectRoots(context, out var dataRoots, out var otherRoots, out unusable);
        roots = dataRoots.Concat(otherRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return complete;
    }

    /// <summary>資料根目錄（兩個：實際的與預設的）與其他禁止的資料夾分開正規化；有非空的項目正規化不了回 false</summary>
    private static bool TryCollectRoots(RemoteFolderContext context, out List<string> dataRoots, out List<string> otherRoots, out string? unusable)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? firstUnusable = null;
        List<string> Normalize(IEnumerable<string?> raw)
        {
            var result = new List<string>();
            foreach (var root in raw)
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                if (TryNormalize(root, out var normalized))
                    result.Add(normalized);
                else
                    firstUnusable ??= root;
            }

            return result;
        }

        dataRoots = Normalize([context.AppDataRoot, Join(context.LocalAppData, "Myriagent")]);
        var roots = new List<string?>
        {
            Join(context.UserProfile, ".claude"),
            Join(context.UserProfile, ".claude.json"),
            Join(context.UserProfile, ".codex"),
            Join(context.UserProfile, ".ssh"),
            Join(context.LocalAppData, @"Microsoft\Credentials"),
            Join(context.LocalAppData, @"Google\Chrome\User Data"),
            Join(context.LocalAppData, @"Microsoft\Edge\User Data"),
            Join(context.LocalAppData, @"BraveSoftware\Brave-Browser\User Data"),
        };
        if (context.RoamingAppData is { } roaming)
        {
            roots.Add(Join(roaming, @"Microsoft\Credentials"));
            roots.Add(Join(roaming, @"Mozilla\Firefox"));
        }

        roots.AddRange(context.AccountConfigDirectories ?? []);
        otherRoots = Normalize(roots);
        unusable = firstUnusable;
        return unusable == null;
    }

    /// <summary>
    /// 遠端不能用的路徑（setFolder、listFiles、fileFetch、新對話的資料夾；D14）：正規化不了、磁碟不是固定或卸除式（網路磁碟機代號也是
    /// 「X:\」開頭，<c>IsLocalDrivePath</c> 會放行，所以另判種類）、禁止清單不完整（fail-closed，見 <see cref="TryGetForbiddenRoots"/>）、
    /// 或在禁止清單的任何一個資料夾裡（含它本身）。資料根目錄這一條有例外：落在目前對話的允許子資料夾
    /// （<see cref="RemoteFolderContext.AllowedDataSubtrees"/>，預設工作資料夾與 attachments）裡的不算；其他禁止的資料夾照判
    /// </summary>
    /// <param name="driveKind">這個路徑的磁碟代號的種類（呼叫端查）</param>
    public static bool IsForbidden(string? path, RemoteDriveKind driveKind, RemoteFolderContext context)
        => IsForbidden(path, driveKind, context, out _);

    /// <summary>同 <see cref="IsForbidden(string?, RemoteDriveKind, RemoteFolderContext)"/>；<paramref name="unusableRoot"/> 是讓禁止清單不完整的資料夾（呼叫端記錄用）</summary>
    public static bool IsForbidden(string? path, RemoteDriveKind driveKind, RemoteFolderContext context, out string? unusableRoot)
    {
        ArgumentNullException.ThrowIfNull(context);
        var complete = TryCollectRoots(context, out var dataRoots, out var otherRoots, out unusableRoot);
        if (!TryNormalize(path, out var normalized) || driveKind is not (RemoteDriveKind.Fixed or RemoteDriveKind.Removable))
            return true;
        if (!complete)
            return true;
        if (otherRoots.Any(root => IsWithinNormalized(normalized, root)))
            return true;
        return dataRoots.Any(root => IsWithinNormalized(normalized, root)) && !IsInAllowedDataSubtree(normalized, context);
    }

    /// <summary>在目前對話的允許子資料夾裡：子資料夾要正規化得了、而且在對話資料夾裡面（不是它本身）</summary>
    private static bool IsInAllowedDataSubtree(string normalized, RemoteFolderContext context)
    {
        if (context.AllowedDataSubtrees is not { Count: > 0 } subtrees || !TryNormalize(context.ConversationDirectory, out var conversation))
            return false;
        return subtrees.Any(s => TryNormalize(s, out var subtree)
                                 && !string.Equals(subtree, conversation, StringComparison.OrdinalIgnoreCase)
                                 && IsWithinNormalized(subtree, conversation)
                                 && IsWithinNormalized(normalized, subtree));
    }

    /// <summary>8.3 短檔名：主檔名 ≤8 字、含「~數字」結尾、副檔名 ≤3 字（例如 <c>PROGRA~1</c>、<c>MYRIAGENTA~1.TXT</c>）</summary>
    public static bool IsShortName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var extension = dot < 0 ? string.Empty : name[(dot + 1)..];
        var tilde = stem.LastIndexOf('~');
        return stem.Length <= 8 && tilde >= 1 && tilde < stem.Length - 1 && stem[(tilde + 1)..].All(char.IsAsciiDigit)
               && extension.Length <= 3 && !extension.Contains('.');
    }

    private static bool IsWithinNormalized(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = root.EndsWith('\\') ? root : root + @"\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一般的檔名：沒有不合法的字元與替代資料流、不是保留裝置名稱、結尾不是點或空白、不是 8.3 短檔名</summary>
    private static bool IsPlainName(string segment)
    {
        if (segment.Any(c => c < 0x20 || c is '<' or '>' or '"' or '|' or '?' or '*' or ':'))
            return false;
        if (segment[^1] is '.' or ' ')
            return false;
        var stem = segment.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return false;
        return !IsShortName(segment);
    }

    private static string? Join(string? folder, string relative)
        => string.IsNullOrEmpty(folder) ? null : folder.TrimEnd('\\', '/') + @"\" + relative;
}
