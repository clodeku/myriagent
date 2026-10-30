using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 代理回覆裡顯示的圖片與影片「留一份」（2026-10-25 使用者要求：回覆引用的探針截圖之後被別的程式刪掉，對話裡只剩破圖）。
/// 一則回覆或工具完成、帶了要顯示的媒體時，在背景把原檔複製到這個對話資料夾的 <c>attachments\.kept\{雜湊}\{原檔名}</c>
/// （<see cref="MediaKeeper"/>），項目以 <see cref="AgentChatItem.KeptMedia"/> 記下「原路徑 → 副本」；之後畫面一律顯示副本——
/// 原檔被刪、被搬走或被覆寫成別的內容，對話裡看到的還是當時那一張。副本跟著對話資料夾：刪除對話一起刪、進回收區一起搬。
/// 放在 attachments\ 底下是因為遠端讀檔（瀏覽器版、其他電腦）的守門只放行工作資料夾與 attachments\，資料根目錄的其他地方一律不給
/// </summary>
internal static class MediaKeepRules
{
    /// <summary>副本放在 attachments\ 底下的這個資料夾（同 .model，點開頭：附件的列舉都不往下走）</summary>
    public const string FolderName = ".kept";

    /// <summary>單一檔案超過這個大小就不留（大型影片）：照舊直接顯示原檔</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 要留的原檔（正規化後的完整路徑，依出現順序、不重複）：本機磁碟（UNC 一律不認）、畫面能直接顯示的圖片與影片、
    /// 不在這個對話資料夾裡（附件本來就是對話自己的）、還沒留過的
    /// </summary>
    public static List<string> Candidates(IEnumerable<string?> paths, string conversationDirectory, IEnumerable<string>? alreadyKept)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var done = new HashSet<string>(alreadyKept ?? [], StringComparer.OrdinalIgnoreCase);
        var own = Normalize(conversationDirectory);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (Normalize(path) is not { } normalized || !AgentAttachmentRules.IsDisplayableMedia(normalized))
                continue;
            if (own is not null && IsUnder(normalized, own))
                continue;
            if (done.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }

    /// <summary>大小合格才複製（空檔不留：多半是還沒寫完或寫壞的）</summary>
    public static bool ShouldCopy(long length) => length > 0 && length <= MaxBytes;

    /// <summary>
    /// 副本相對於對話資料夾的路徑：<c>attachments\.kept\{雜湊 16 碼}\{原檔名}</c>。雜湊取項目 id 與原路徑（不分大小寫），
    /// 同一個路徑在不同回覆各留各的（例如探針每次重拍同名的截圖），檔名照舊、畫面與另存新檔的名稱不變
    /// </summary>
    public static string RelativePath(string itemId, string normalizedOriginal)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        ArgumentException.ThrowIfNullOrEmpty(normalizedOriginal);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(itemId + "\n" + normalizedOriginal.ToUpperInvariant()));
        var hash = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        return Path.Combine("attachments", FolderName, hash, Path.GetFileName(normalizedOriginal));
    }

    /// <summary>
    /// 項目記的副本（相對路徑）換成完整路徑：只認 <c>attachments\.kept\{雜湊}\{檔名}</c> 這個形狀、組出來一定在對話資料夾裡
    /// （items.json 被改成 <c>..\..\</c> 這類路徑也不會指到別處）；不合格回 null（照舊顯示原檔）
    /// </summary>
    public static string? FullPath(string conversationDirectory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(conversationDirectory))
            return null;
        var parts = relative.Split('\\', '/');
        if (parts.Length != 4 || !parts[0].Equals("attachments", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals(FolderName, StringComparison.OrdinalIgnoreCase) || parts[2].Length != 16 || !parts[2].All(Uri.IsHexDigit)
            || parts[3] is "" or "." or ".." || parts[3].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        if (Normalize(conversationDirectory) is not { } root || Normalize(Path.Combine(root, parts[0], parts[1], parts[2], parts[3])) is not { } full)
            return null;
        return IsUnder(full, root) ? full : null;
    }

    /// <summary>
    /// 原路徑對到的副本（完整路徑）：項目留過這個檔就回副本，沒有或記錄不合格回 null。比對用正規化後的路徑（不分大小寫）；
    /// 副本在不在由呼叫端看（渲染時副本不見了就顯示原檔）
    /// </summary>
    public static string? KeptPath(IReadOnlyDictionary<string, string>? kept, string conversationDirectory, string? original)
    {
        if (kept is not { Count: > 0 } || Normalize(original) is not { } normalized)
            return null;
        if (kept.TryGetValue(normalized, out var relative))
            return FullPath(conversationDirectory, relative);
        // 從 items.json 讀回的字典分大小寫：路徑不分，逐一比
        foreach (var (key, value) in kept)
        {
            if (string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase))
                return FullPath(conversationDirectory, value);
        }

        return null;
    }

    /// <summary>正規化（同 /media 白名單：完整路徑、反斜線）；不是本機磁碟路徑或寫法不合格回 null</summary>
    public static string? Normalize(string? path) => LocalMediaUrls.NormalizePath(path);

    private static bool IsUnder(string path, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
