using Myriagent.Agents;
using Myriagent.Media;

namespace Myriagent.Files;

/// <summary>
/// 瀏覽器上傳的檔下載到這台時的規則（WP4.7 計畫 D22；純函式）：暫存檔名、暫存資料夾的界線、磁碟空間。
/// 上傳的檔只會成為附件、永遠不會被執行，也不解壓（沒有 zip slip 的問題）；檔名照樣當成不可信的輸入
/// </summary>
internal static class UploadNameRules
{
    /// <summary>下載之後這台的磁碟至少還要剩這麼多（合計大小之外，D22）</summary>
    public const long DiskMarginBytes = 1L << 30;

    /// <summary>清不出任何字時的檔名</summary>
    public const string FallbackName = "上傳的檔案";

    /// <summary>Windows 的保留裝置名稱（主檔名是這些的檔，在很多 API 底下會開到裝置而不是檔案）</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>
    /// 暫存檔名：只留檔名（最後一段，<c>..\..\evil.exe</c> 變 <c>evil.exe</c>）、去控制字元與格式字元、<c>:</c> 與其他 Windows 不收的字元換成 <c>_</c>、
    /// 去結尾的點與空白（<see cref="MediaHeaderRules.CleanFileName"/>）；主檔名是保留裝置名稱的前面加 <c>_</c>（<c>CON.txt</c> 變 <c>_CON.txt</c>）；
    /// 清不出字的用 <see cref="FallbackName"/>。最後照附件的規則（<see cref="AgentAttachmentRules.MakeStoredFileName"/>：≤120 字、和
    /// <paramref name="taken"/> 重名加序號，不分大小寫）決定，並把結果加進 <paramref name="taken"/>
    /// </summary>
    public static string SafeName(string? name, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var clean = MediaHeaderRules.CleanFileName(name) ?? FallbackName;
        var stem = clean.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            clean = "_" + clean;
        var result = AgentAttachmentRules.MakeStoredFileName(clean, candidate => taken.Any(t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase)));
        taken.Add(result);
        return result;
    }

    /// <summary>
    /// <paramref name="fullPath"/> 正規化之後在 <paramref name="directory"/> 裡面（不是它本身；不分大小寫，以「資料夾＋分隔字元」為界）。
    /// 只做字串的正規化（<see cref="Path.GetFullPath(string)"/>，不碰檔案系統）；UNC 與裝置路徑、正規化不了的一律 false
    /// </summary>
    public static bool IsUnder(string? fullPath, string? directory)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(directory))
            return false;
        if (!AgentAttachmentRules.IsLocalDrivePath(fullPath) || !AgentAttachmentRules.IsLocalDrivePath(directory))
            return false;
        try
        {
            var file = Path.GetFullPath(fullPath);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return file.Length > root.Length + 1
                   && file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>磁碟空間夠不夠下載：可用空間要不小於合計大小加 <see cref="DiskMarginBytes"/>；量不到（null）當成不夠</summary>
    public static bool HasDiskRoom(long? freeBytes, long totalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        return freeBytes is { } free && free >= 0 && free - totalBytes >= DiskMarginBytes;
    }
}
