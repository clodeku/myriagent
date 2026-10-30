using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Myriagent.Media;

/// <summary>
/// 媒體回應的標頭（設計 4.10:1062、WP4.7 計畫 D9、D12）：媒體專用的 sandbox CSP、內容型別白名單（不收 SVG、HTML）、
/// 檔名的 RFC 8187 編碼（<c>X-File-Name</c> 與 <c>Content-Disposition</c> 的 <c>filename*</c>）。
/// 裝置宣告的型別與檔名都是外部輸入：型別不在白名單就由呼叫端改成 <c>application/octet-stream</c> 加 attachment，
/// 檔名只留最後一段、去掉控制字元與引號。純函式
/// </summary>
internal static class MediaHeaderRules
{
    /// <summary>
    /// <c>/m/</c>、<c>/t/</c>、<c>/f/</c> 與桌面複本代理的 CSP（設計:1062）：不載入任何東西、不執行腳本（sandbox），
    /// 直接打開被宣告成 SVG／HTML 的檔也不會在本站的來源下執行（SEC-23）
    /// </summary>
    public const string MediaCsp = "default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox";

    /// <summary>沒有可用檔名時的 ASCII 後備</summary>
    private const string FallbackStem = "file";

    /// <summary>檔名的長度上限（UTF-16 字元；NTFS 的單一名稱上限）</summary>
    private const int MaxFileNameLength = 255;

    /// <summary>RFC 8187 的 attr-char 裡 ALPHA、DIGIT 以外可以不編碼的字元</summary>
    private const string AttrPunctuation = "!#$&+-.^_`|~";

    /// <summary>
    /// 媒體內容型別的白名單：正好是 <c>AgentAttachmentRules.GetMediaContentType</c> 的值域（可顯示的圖片與可播放的影片）。
    /// 沒有 SVG（可以帶腳本）與任何文字型別
    /// </summary>
    private static readonly FrozenSet<string> MediaContentTypes = new[]
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/x-icon", "image/avif",
        "video/mp4", "video/webm", "video/quicktime",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>裝置宣告的型別在白名單就回標準寫法（小寫、去掉參數），否則 null</summary>
    public static string? SafeMediaContentType(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
            return null;
        var semicolon = declared.IndexOf(';');
        var type = (semicolon < 0 ? declared : declared[..semicolon]).Trim().ToLowerInvariant();
        return MediaContentTypes.TryGetValue(type, out var canonical) ? canonical : null;
    }

    /// <summary>
    /// <c>X-File-Name</c> 的值：<c>UTF-8''</c> 加 UTF-8 位元組的百分比編碼（RFC 8187；HTTP 標頭只能 ASCII，中文檔名照原樣傳會讓整個回應失敗）。
    /// 這裡不清理檔名，清理由解碼的一方做
    /// </summary>
    public static string EncodeFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var builder = new StringBuilder("UTF-8''");
        foreach (var b in Encoding.UTF8.GetBytes(fileName))
        {
            if (IsAttrChar(b))
                builder.Append((char)b);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// 解碼 <c>X-File-Name</c>（<c>UTF-8'語言'值</c>，語言可以空）並清理成單純的檔名：只留最後一段路徑、去控制字元與格式字元、
    /// 引號與 Windows 不收的字元換成 <c>_</c>、去掉結尾的點與空白、≤255 字。不合格（不是 UTF-8、百分比編碼壞掉、清完是空的）回 null
    /// </summary>
    public static string? DecodeFileName(string? header)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("UTF-8'", StringComparison.OrdinalIgnoreCase))
            return null;
        var languageEnd = header.IndexOf('\'', 6);
        if (languageEnd < 0)
            return null;
        var value = header.AsSpan(languageEnd + 1);
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length
                    || !byte.TryParse(value.Slice(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var decoded))
                    return null;
                bytes.Add(decoded);
                i += 2;
            }
            else if (c is > ' ' and < (char)0x7F)
            {
                bytes.Add((byte)c);
            }
            else
            {
                return null;
            }
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        return CleanFileName(text);
    }

    /// <summary>
    /// <c>Content-Disposition</c>：<c>attachment</c> 或 <c>inline</c>，有檔名時加 ASCII 後備的 <c>filename="…"</c>
    /// 與 <c>filename*=UTF-8''…</c>（檔名先經 <see cref="CleanFileName"/>）
    /// </summary>
    public static string ContentDisposition(string? fileName, bool attachment)
    {
        var disposition = attachment ? "attachment" : "inline";
        if (fileName is null || CleanFileName(fileName) is not { } clean)
            return disposition;
        return $"{disposition}; filename=\"{AsciiFallback(clean)}\"; filename*={EncodeFileName(clean)}";
    }

    /// <summary>
    /// 單純的檔名：只留最後一段路徑（<c>\</c>、<c>/</c> 之後）、去掉控制字元與格式字元（含 RTL 覆寫）、
    /// 引號與 Windows 檔名不收的字元換成 <c>_</c>、去掉前後空白與結尾的點、≤255 字（保留副檔名）；清完是空的回 null
    /// </summary>
    public static string? CleanFileName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        var lastSeparator = name.AsSpan().LastIndexOfAny('\\', '/');
        var segment = lastSeparator < 0 ? name : name[(lastSeparator + 1)..];
        var builder = new StringBuilder(segment.Length);
        // 落單的代理字元在列舉時已換成 U+FFFD
        foreach (var rune in segment.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    continue;
            }

            if (rune.Value is '"' or '<' or '>' or ':' or '|' or '?' or '*')
                builder.Append('_');
            else
                builder.Append(rune.ToString());
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length <= MaxFileNameLength ? cleaned : Shorten(cleaned);
    }

    /// <summary>超過 255 字時截短主檔名、保留副檔名（≤16 字的才保留），不切開代理字對</summary>
    private static string Shorten(string name)
    {
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= 16 ? name[dot..] : string.Empty;
        var keep = MaxFileNameLength - extension.Length;
        if (char.IsHighSurrogate(name[keep - 1]))
            keep--;
        return name[..keep].TrimEnd('.', ' ') + extension;
    }

    /// <summary>ASCII 後備：非 ASCII 的字拿掉、<c>%</c> 換成 <c>_</c>（有些瀏覽器會解碼 filename 裡的百分比）；主檔名空了改成 file</summary>
    private static string AsciiFallback(string clean)
    {
        var builder = new StringBuilder(clean.Length);
        foreach (var c in clean)
        {
            if (c == '%')
                builder.Append('_');
            else if (c is >= ' ' and < (char)0x7F)
                builder.Append(c);
        }

        var ascii = builder.ToString().Trim().TrimEnd('.', ' ');
        var dot = ascii.LastIndexOf('.');
        return dot switch
        {
            < 0 when ascii.Length == 0 => FallbackStem,
            0 => FallbackStem + ascii,
            _ => ascii,
        };
    }

    private static bool IsAttrChar(byte b)
        => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
           || (b < 0x80 && AttrPunctuation.Contains((char)b));
}
