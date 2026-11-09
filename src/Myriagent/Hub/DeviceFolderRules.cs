namespace Myriagent.Hub;

/// <summary>
/// 電腦狀態面板的整個資料夾傳輸（2026-11-09 擁有者要求「選擇資料夾直接下載或上傳整個資料夾」）的規則：
/// 相對路徑的檢查（下載用：從 deviceTree 來的，一律照原樣、不合格就不收）與清理（上傳用：瀏覽器給的資料夾名稱，換掉 Windows 不收的字元），以及上限。純函式
/// </summary>
internal static class DeviceFolderRules
{
    /// <summary>deviceTree 最多列幾個檔（超過標 truncated）</summary>
    public const int MaxTreeFiles = 5000;

    /// <summary>deviceTree 最多列幾個資料夾</summary>
    public const int MaxTreeDirs = 5000;

    /// <summary>deviceTree 最多花多久列（遠端指令要在 30 秒內回）</summary>
    public static readonly TimeSpan TreeBudget = TimeSpan.FromSeconds(18);

    /// <summary>deviceFetchMany 一次最多幾個檔</summary>
    public const int MaxFetchMany = 200;

    /// <summary>相對路徑的長度上限</summary>
    public const int MaxRelativeLength = 1024;

    private static readonly char[] InvalidChars = ['<', '>', '"', '|', '?', '*', ':'];

    /// <summary>
    /// 下載用的相對路徑（<c>a\b\c.txt</c>，斜線換成反斜線）：不是空的、不是根目錄開頭、沒有 <c>.</c>／<c>..</c>、沒有替代資料流與不合法的字元、
    /// 每一段不以點或空白結尾；合格回整理過的，否則 null
    /// </summary>
    public static string? CheckRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > MaxRelativeLength)
            return null;
        var normalized = relative.Replace('/', '\\');
        if (normalized.StartsWith('\\') || Path.IsPathRooted(normalized))
            return null;
        var parts = normalized.Split('\\');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => char.IsControl(c) || Array.IndexOf(InvalidChars, c) >= 0))
                return null;
        }

        return string.Join('\\', parts);
    }

    /// <summary>
    /// 上傳用的子資料夾（瀏覽器的 webkitRelativePath 去掉檔名的那一段）：每一段以 <see cref="PeerFileRules.SafeFileName"/> 清理（不合法的字元換成底線、
    /// 保留名稱前面加底線），空的段落略過；<c>.</c>、<c>..</c> 一律不收（回 null）；全部空的回空字串（＝就是 folder 本身）
    /// </summary>
    public static string? CleanSubfolder(string? subfolder)
    {
        if (subfolder is null)
            return string.Empty;
        if (subfolder.Length > MaxRelativeLength)
            return null;
        var parts = subfolder.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Any(p => p is "." or ".."))
            return null;
        return string.Join('\\', parts.Select(p => PeerFileRules.SafeFileName(p)));
    }

    /// <summary>完整路徑 <paramref name="full"/> 相對於 <paramref name="root"/> 的路徑（不在底下回 null）</summary>
    public static string? RelativeTo(string root, string full)
    {
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && full.Length > prefix.Length ? full[prefix.Length..] : null;
    }

    /// <summary>資料夾的顯示名稱（磁碟根目錄是「C」、網路分享是分享名稱）</summary>
    public static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
        {
            // 網路分享的根（\\nas\share）：GetFileName 是空的，取最後一段；磁碟根目錄取磁碟代號
            var last = trimmed.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            name = trimmed.StartsWith(@"\\", StringComparison.Ordinal) && last is not null ? last
                : trimmed.Length >= 1 ? trimmed[..1] : "資料夾";
        }
        return PeerFileRules.SafeFileName(name);
    }
}
