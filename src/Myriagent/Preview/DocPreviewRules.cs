using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Myriagent.Preview;

/// <summary>Office 文件由哪個程式轉成 PDF</summary>
internal enum DocPreviewKind
{
    Word,
    Excel,
    PowerPoint,
}

/// <summary>
/// 文件預覽（2026-11-01 使用者要求：PDF、Word、Excel、PowerPoint 在對話裡直接預覽）：PDF 直接交給 WebView2 內建的檢視器，
/// Office 文件在這台以 Office（沒有就 LibreOffice）轉成 PDF 再顯示。轉好的 PDF 依「路徑＋大小＋修改時間」快取在
/// <c>&lt;資料根目錄&gt;\cache\doc-preview\</c>（檔案一變名稱就變），7 天沒用到就刪。純函式
/// </summary>
internal static class DocPreviewRules
{
    /// <summary>轉檔最多等多久（Office 第一次啟動、大的簡報都要好幾秒）；逾時就結束轉檔程序與它開的 Office</summary>
    public static readonly TimeSpan ConvertTimeout = TimeSpan.FromSeconds(120);

    /// <summary>快取的 PDF 多久沒用到就刪</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    public const string NoConverterText = "這台電腦沒有 Microsoft Office 或 LibreOffice，不能預覽這個檔案，請用預設程式開啟";
    public const string NotSupportedText = "這種檔案不能預覽";
    public const string NotShownText = "只能預覽回覆或預覽中出現的本機檔案";
    public const string TimeoutText = "轉換逾時，請用預設程式開啟";
    public const string FailedText = "無法轉換這個檔案（可能有密碼保護或檔案已損毀），請用預設程式開啟";

    /// <summary>Office 文件的種類（只看副檔名）；不是 Office 文件回 null</summary>
    public static DocPreviewKind? KindOf(string? path) => Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".rtf" or ".odt" => DocPreviewKind.Word,
        ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".ods" => DocPreviewKind.Excel,
        ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".odp" => DocPreviewKind.PowerPoint,
        _ => null,
    };

    /// <summary>
    /// 快取檔名：完整路徑（不分大小寫）、大小、修改時間的 SHA-256 前 32 碼。同一個檔改過了就是新的名稱，不會拿到舊的預覽
    /// </summary>
    public static string CacheFileName(string fullPath, long length, DateTime lastWriteUtc)
    {
        var key = string.Join('|', fullPath.ToUpperInvariant(), length.ToString(CultureInfo.InvariantCulture),
            lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + ".pdf";
    }

    /// <summary>快取裡該刪的：最後使用（修改）時間早於 now − <see cref="CacheLifetime"/> 的 PDF</summary>
    public static bool Expired(DateTime lastUsedUtc, DateTime nowUtc) => nowUtc - lastUsedUtc > CacheLifetime;

    /// <summary>轉檔腳本的結束碼 → 給使用者看的說明（0 是成功，回 null）</summary>
    public static string? ErrorOf(int exitCode) => exitCode switch
    {
        0 => null,
        3 => NoConverterText,
        _ => FailedText,
    };

    /// <summary>LibreOffice 可能裝在的位置（依序找）</summary>
    public static IEnumerable<string> LibreOfficeCandidates(string programFiles, string programFilesX86)
    {
        yield return Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe");
        yield return Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.exe");
    }

    /// <summary>Office 的 COM 程式識別碼（裝了 Office 時登記在 HKCR 底下）</summary>
    public static string ProgIdOf(DocPreviewKind kind) => kind switch
    {
        DocPreviewKind.Word => "Word.Application",
        DocPreviewKind.Excel => "Excel.Application",
        DocPreviewKind.PowerPoint => "PowerPoint.Application",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不認得的文件種類"),
    };

    /// <summary>轉檔腳本的 -Kind 參數</summary>
    public static string ScriptKindOf(DocPreviewKind kind) => kind switch
    {
        DocPreviewKind.Word => "word",
        DocPreviewKind.Excel => "excel",
        DocPreviewKind.PowerPoint => "powerpoint",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不認得的文件種類"),
    };
}
