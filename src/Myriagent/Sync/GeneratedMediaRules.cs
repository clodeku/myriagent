using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Sync;

/// <summary>
/// Codex 產生的圖片（WP4.7 計畫 D45，擁有者 Q2 選 A；純函式）：Codex 把圖片寫在它的設定資料夾的 <c>generated_images\</c>
/// （<c>~\.codex</c>、繼承的 CODEX_HOME、隔離帳戶的設定資料夾），那些資料夾在禁止清單裡（裡面有登入憑證）。
/// 不開例外：第一次要用時（縮圖上傳或 <c>/m/</c>）把檔案複製到對話的 <c>attachments\generated\{itemId}-{序號}{副檔名}</c>，
/// 之後只服務複本（原檔刪了照樣看得到）。複本要真的複製位元組，不能用硬連結（守門員拒絕多個硬連結的檔）
/// </summary>
internal static class GeneratedMediaRules
{
    /// <summary>Codex 產生圖片的子資料夾名</summary>
    public const string GeneratedImagesFolder = "generated_images";

    /// <summary>複本放在附件資料夾底下的子資料夾名</summary>
    public const string CopyFolder = "generated";

    /// <summary>複製的上限（20 MB；超過就不複製、回 404）</summary>
    public const long MaxCopyBytes = 20L * 1024 * 1024;

    /// <summary>
    /// 產生圖片的根目錄（原樣；呼叫端正規化）：<c>{UserProfile}\.codex\generated_images</c>、繼承的 CODEX_HOME 與每個帳戶設定資料夾底下的 <c>generated_images</c>。
    /// 空的略過、不重複（不分大小寫）
    /// </summary>
    public static IReadOnlyList<string> Roots(MediaFolderRoots folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var homes = new List<string?> { string.IsNullOrEmpty(folders.UserProfile) ? null : Path.Combine(folders.UserProfile, ".codex"), folders.CodexHome };
        homes.AddRange(folders.AccountConfigDirectories ?? []);
        var roots = new List<string>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home))
                continue;
            var root = Path.Combine(home.TrimEnd('\\', '/'), GeneratedImagesFolder);
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                roots.Add(root);
        }

        return roots;
    }

    /// <summary>
    /// 這個路徑在某個（已正規化的）產生圖片根目錄底下（不含根目錄本身）：只比字串、不碰檔案系統；路徑要是本機磁碟的完整路徑、沒有 <c>.</c>／<c>..</c> 段
    /// </summary>
    public static bool IsGenerated(string? path, IReadOnlyList<string> normalizedRoots)
    {
        ArgumentNullException.ThrowIfNull(normalizedRoots);
        if (!AgentAttachmentRules.IsLocalDrivePath(path) || HasDotSegment(path!))
            return false;
        var normalized = path!.Replace('/', '\\');
        foreach (var root in normalizedRoots)
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var prefix = root.TrimEnd('\\') + "\\";
            if (normalized.Length > prefix.Length && normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 複本的路徑：<c>{attachmentDirectory}\generated\{itemId}-{sequence}{副檔名}</c>（副檔名轉小寫）。項目 id 不合格（含 <c>..</c>）、序號小於 1、
    /// 來源路徑有 <c>.</c>／<c>..</c> 段、副檔名不是白名單（PNG、JPEG、GIF、WebP：內容檢查認得的格式）回 null
    /// </summary>
    public static string? CopyPath(string attachmentDirectory, string itemId, int sequence, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(attachmentDirectory);
        if (string.IsNullOrEmpty(attachmentDirectory) || !ConversationIds.IsItemId(itemId) || sequence < 1 || string.IsNullOrEmpty(sourcePath) || HasDotSegment(sourcePath))
            return null;
        if (AgentAttachmentRules.GetClaudeImageMediaType(sourcePath) is null)
            return null;
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return Path.Combine(attachmentDirectory, CopyFolder, $"{itemId}-{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}{extension}");
    }

    /// <summary>媒體路徑在項目 <see cref="AgentChatItem.MediaPaths"/> 裡的序號（從 1 起；比對同 <see cref="MediaServerRules.Resolve"/>，不分大小寫）；不在回 null</summary>
    public static int? Sequence(IReadOnlyList<string>? mediaPaths, string? mediaPath)
    {
        if (mediaPaths is null || mediaPath is null)
            return null;
        for (var i = 0; i < mediaPaths.Count; i++)
        {
            if (string.Equals(mediaPaths[i], mediaPath, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return null;
    }

    private static bool HasDotSegment(string path)
        => path.Split('\\', '/').Any(segment => segment is "." or "..");
}
