using System.Text.RegularExpressions;
using Myriagent.Remote;
using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>真實路徑（GetFinalPathNameByHandle）和要求的資料夾比起來的情形（<see cref="RemoteFolderFinalPathRules.Interpret"/>）</summary>
internal enum RemoteFinalPathKind
{
    /// <summary>和要求的一樣（不分大小寫）：路徑上沒有 junction、符號連結或掛載點</summary>
    Same,

    /// <summary>經過連結（或 subst），實際在另一個「磁碟代號:\」路徑</summary>
    Moved,

    /// <summary>實際在網路上（<c>\\?\UNC\</c>）、沒有磁碟代號的磁碟區（<c>\\?\Volume{…}\</c>）或其他不是「磁碟代號:\」的地方，或取不到</summary>
    NotLocal,

    /// <summary>真實路徑的名稱正規化不了（保留裝置名稱、結尾是點、像 8.3 短名的名稱…）</summary>
    Invalid,
}

/// <param name="Kind">情形</param>
/// <param name="Path">Same 是要求的路徑（原樣）、Moved 是正規化的真實路徑；其他是 null</param>
internal readonly record struct RemoteFinalPath(RemoteFinalPathKind Kind, string? Path);

/// <summary>
/// 遠端選的資料夾（setFolder、conversationCreate 的 folder）的真實路徑規則（設計 8.7 第 3 步在資料夾探針的做法）：字串那一關通過之後，
/// 探針解析資料夾實際所在（路徑上每一層的 junction、符號連結、掛載點與 subst），真實路徑再過一次同樣的正規化、磁碟種類與禁止清單——
/// 代理不用管理員權限就能在工作資料夾裡建一個指到資料根目錄的 junction，字串上卻在允許的子樹裡。解析時不跟進任何連結：
/// 逐層以 FILE_FLAG_OPEN_REPARSE_POINT 讀，遇到連結就讀它的目標（<see cref="LinkTargetPath"/>；磁碟區掛載點先以 <see cref="TryParseVolumeTarget"/>、
/// <see cref="VolumeTargetPath"/> 換成那個磁碟區的磁碟代號，網路、沒有代號的磁碟區一律不跟進），
/// 換到目標重新走，最後才以 GetFinalPathNameByHandle 取正規的名稱。禁止清單另以每個禁止的資料夾自己的真實路徑比對（資料根目錄、
/// <c>~\.claude</c> 這些本身也可能是 junction，直接選它們的目標要一樣擋）。通過時套用的是真實路徑：之後 junction 改指向也影響不到這個對話。
/// 純函式，不碰檔案系統（解析真實路徑由呼叫端給）
/// </summary>
internal static class RemoteFolderFinalPathRules
{
    /// <summary>解析真實路徑時最多經過幾個連結（Windows 自己的上限是 63；超過就當成取不到）</summary>
    public const int MaxLinkHops = 32;

    /// <summary>探針的真實路徑「路徑上有不能跟進的連結」（目標是網路、沒有代號的磁碟區、網路磁碟機…，沒有跟進）：空字串，不會是真的路徑</summary>
    public const string NotFollowed = "";

    /// <summary>
    /// 連結 <paramref name="link"/>（正規化的「X:\…」）的目標原文（FileSystemInfo.LinkTarget，不跟進）→ 接著要走的完整路徑：
    /// 相對的（符號連結）以連結所在的資料夾為準、只有根目錄的（<c>\x</c>）以連結的磁碟為準；結果要是字串層合格的「磁碟代號:\」完整路徑
    /// （8.3 短名可以，走的時候照樣開得到）。UNC、<c>\\?\</c>、<c>\\.\</c>、<c>\??\</c>、沒有分隔字元的磁碟代號相對路徑、替代資料流、
    /// 保留名稱與讀不到（null）一律回 null：不跟進
    /// </summary>
    public static string? LinkTargetPath(string link, string? target)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrEmpty(target) || link.Length < 3)
            return null;
        // 磁碟區掛載點的目標（.NET 的 LinkTarget 拿掉 \??\ 之後是 Volume{GUID}\…）不是相對路徑：接在連結所在的資料夾後面會走到別的地方
        // （合併前審查 PROD-2）。呼叫端先以 TryParseVolumeTarget 換成那個磁碟區的磁碟代號（VolumeTargetPath），換不到就不跟進
        if (TryParseVolumeTarget(target, out _, out _))
            return null;
        // 連結的目標是 NT 路徑，結尾的點與空白不會被拿掉；GetFullPath 會拿掉，走到的就不是同一個資料夾
        if (target.Split('\\', '/').Any(segment => segment is not ("." or "..") && segment.Length > 0 && segment[^1] is '.' or ' '))
            return null;
        string combined;
        if (IsSeparator(target[0]))
        {
            // \\server、//server、\\?\、\\.\、\??\：網路或裝置命名空間
            if (target.Length >= 2 && (IsSeparator(target[1]) || target[1] == '?'))
                return null;
            combined = link[..2] + target;
        }
        else if (target.Length >= 2 && target[1] == ':')
        {
            if (target.Length < 3 || !IsSeparator(target[2]))
                return null;
            combined = target;
        }
        else
        {
            combined = Path.Combine(Path.GetDirectoryName(link) ?? link, target);
        }

        string full;
        try
        {
            full = Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var reason = RemoteFileGuardRules.CheckSyntax(full, out var normalized);
        return reason is null || reason == RemoteFileGuardReasons.ShortName ? normalized : null;
    }

    /// <summary>磁碟區 GUID 路徑（前面可以有 <c>\\?\</c> 或 <c>\??\</c>，後面可以接 <c>\</c> 與剩下的路徑）</summary>
    private static readonly Regex VolumeTarget = new(
        @"^(?:\\\\\?\\|\\\?\?\\)?(Volume\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})(?:\\(.*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex DriveRoot = new(@"^[A-Za-z]:\\$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 連結的目標原文是不是磁碟區 GUID 路徑（掛載點：<c>mountvol</c> 建的、或以 FSCTL_SET_REPARSE_POINT 指到 <c>\??\Volume{GUID}\</c> 的；
    /// 一般使用者不用管理員就建得出來）。.NET 的 <c>LinkTarget</c> 對它回 <c>Volume{GUID}\…</c>（拿掉了 <c>\??\</c>），也收 <c>\\?\</c>、<c>\??\</c> 開頭的。
    /// 是的話 <paramref name="volumeName"/> 是 GetVolumePathNamesForVolumeNameW 收的 <c>\\?\Volume{GUID}\</c>、<paramref name="rest"/> 是後面剩下的路徑（可以是空字串）
    /// </summary>
    public static bool TryParseVolumeTarget(string? target, out string volumeName, out string rest)
    {
        volumeName = "";
        rest = "";
        if (string.IsNullOrEmpty(target) || VolumeTarget.Match(target) is not { Success: true } m)
            return false;
        volumeName = @"\\?\" + m.Groups[1].Value + @"\";
        rest = m.Groups[2].Success ? m.Groups[2].Value : "";
        return true;
    }

    /// <summary>
    /// 掛載點的目標換成那個磁碟區的磁碟代號根目錄 <paramref name="driveRoot"/>（<c>X:\</c>，GetVolumePathNamesForVolumeNameW 的結果）之後要走的完整路徑：
    /// 照「指到另一個磁碟的 junction」接著走（呼叫端照樣查那個磁碟的種類）。<paramref name="driveRoot"/> 不是 <c>X:\</c>、或剩下的路徑不合格回 null（不跟進）
    /// </summary>
    public static string? VolumeTargetPath(string link, string? driveRoot, string rest)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(rest);
        if (driveRoot is null || !DriveRoot.IsMatch(driveRoot) || TryParseVolumeTarget(rest, out _, out _))
            return null;
        return LinkTargetPath(link, char.ToUpperInvariant(driveRoot[0]) + @":\" + rest);
    }

    /// <summary>
    /// <paramref name="finalPath"/>（探針解析的真實路徑：GetFinalPathNameByHandle 的 VOLUME_NAME_DOS 結果 <c>\\?\X:\…</c>，或 <see cref="NotFollowed"/>）
    /// 和 <paramref name="requested"/>（已正規化的「X:\…」）比：只收 <c>\\?\X:\</c> 開頭的（<see cref="RemoteFileGuardRules.FromFinalPath"/>），
    /// 再以 <see cref="RemoteFolderRules.TryNormalize"/> 正規化
    /// </summary>
    public static RemoteFinalPath Interpret(string requested, string? finalPath)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (RemoteFileGuardRules.FromFinalPath(finalPath) is not { } local)
            return new RemoteFinalPath(RemoteFinalPathKind.NotLocal, null);
        if (!RemoteFolderRules.TryNormalize(local, out var normalized))
            return new RemoteFinalPath(RemoteFinalPathKind.Invalid, null);
        return string.Equals(normalized, requested, StringComparison.OrdinalIgnoreCase)
            ? new RemoteFinalPath(RemoteFinalPathKind.Same, requested)
            : new RemoteFinalPath(RemoteFinalPathKind.Moved, normalized);
    }

    /// <summary>真實路徑換了磁碟代號（subst、指到另一個磁碟區的 junction）：呼叫端要另查那個磁碟的種類</summary>
    public static bool ChangesDrive(string requested, RemoteFinalPath final)
        => final.Kind == RemoteFinalPathKind.Moved && char.ToUpperInvariant(final.Path![0]) != char.ToUpperInvariant(requested[0]);

    /// <summary>
    /// 禁止清單之前的判斷：不是本機磁碟代號的路徑 → Policy（指到網路或沒有代號的磁碟區）、正規化不了 → Invalid（同直接選那個名稱）、
    /// 真實路徑的磁碟不是固定或卸除式 → Policy；都沒問題回 null（接著比對禁止清單）
    /// </summary>
    public static RemoteFolderVerdict? Refusal(RemoteFinalPath final, RemoteDriveKind finalDriveKind) => final.Kind switch
    {
        RemoteFinalPathKind.NotLocal => RemoteFolderVerdict.Policy,
        RemoteFinalPathKind.Invalid => RemoteFolderVerdict.Invalid,
        RemoteFinalPathKind.Same or RemoteFinalPathKind.Moved => finalDriveKind is RemoteDriveKind.Fixed or RemoteDriveKind.Removable ? null : RemoteFolderVerdict.Policy,
        _ => throw new ArgumentOutOfRangeException(nameof(final), final.Kind, "不認得的真實路徑情形"),
    };

    /// <summary>
    /// 真實路徑 <paramref name="final"/>（正規化的「X:\…」）在禁止清單裡：先照 <paramref name="expanded"/>（展開後的禁止清單，字串比對用的同一份）判，
    /// 再把清單裡每個禁止的資料夾換成它的真實路徑（<paramref name="resolve"/>：參數是正規化的資料夾，回它的真實路徑，取不到就回原樣）比對。
    /// 資料根目錄的例外照舊：落在這個對話的允許子樹（同樣換成真實路徑，而且要在對話資料夾裡面、不能等於它）裡的不算；其他禁止的資料夾照判。
    /// <paramref name="unusableRoot"/> 是讓禁止清單不完整的資料夾（fail-closed，呼叫端記錄用）
    /// </summary>
    public static bool IsForbidden(string final, RemoteDriveKind finalDriveKind, RemoteFolderContext expanded, Func<string, string> resolve, out string? unusableRoot)
    {
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(expanded);
        ArgumentNullException.ThrowIfNull(resolve);
        if (RemoteFolderRules.IsForbidden(final, finalDriveKind, expanded, out unusableRoot))
            return true;

        var dataRoots = new[] { expanded.AppDataRoot, Join(expanded.LocalAppData, "Myriagent") }
            .Select(root => RemoteFolderRules.TryNormalize(root, out var normalized) ? normalized : null)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = ResolvedAllowedSubtrees(expanded, resolve);
        foreach (var root in RemoteFolderRules.ForbiddenRoots(expanded))
        {
            if (!RemoteFolderRules.IsWithin(final, resolve(root)))
                continue;
            if (dataRoots.Contains(root) && allowed.Any(subtree => RemoteFolderRules.IsWithin(final, subtree)))
                continue;
            return true;
        }

        return false;
    }

    /// <summary>這個對話的允許子樹的真實路徑：子樹與對話資料夾都換成真實路徑之後，子樹仍要在對話資料夾裡面、不能等於它（同 RemoteFolderRules 的條件）</summary>
    private static List<string> ResolvedAllowedSubtrees(RemoteFolderContext expanded, Func<string, string> resolve)
    {
        if (expanded.AllowedDataSubtrees is not { Count: > 0 } subtrees || !RemoteFolderRules.TryNormalize(expanded.ConversationDirectory, out var conversation))
            return [];
        var realConversation = resolve(conversation);
        var result = new List<string>();
        foreach (var subtree in subtrees)
        {
            if (!RemoteFolderRules.TryNormalize(subtree, out var normalized))
                continue;
            var real = resolve(normalized);
            if (RemoteFolderRules.TryNormalize(real, out var realNormalized) && RemoteFolderRules.TryNormalize(realConversation, out var conversationNormalized)
                && !string.Equals(realNormalized, conversationNormalized, StringComparison.OrdinalIgnoreCase)
                && RemoteFolderRules.IsWithin(realNormalized, conversationNormalized))
                result.Add(realNormalized);
        }

        return result;
    }

    private static string? Join(string? folder, string relative)
        => string.IsNullOrEmpty(folder) ? null : folder.TrimEnd('\\', '/') + @"\" + relative;

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
