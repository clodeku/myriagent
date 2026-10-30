using System.Globalization;

namespace Myriagent.Media;

/// <summary>HTTP <c>Range</c> 標頭解析的結果（WP4.7 計畫 D40）</summary>
internal enum RangeKind
{
    /// <summary>沒帶、不合格或不支援（多區間、不是 bytes）：照 RFC 9110 忽略 Range，回整份（200）</summary>
    Full,

    /// <summary>單一區間 <c>bytes=start-end</c> 或 <c>bytes=start-</c>（206）</summary>
    Satisfiable,

    /// <summary>起點在結尾之後（416，<c>Content-Range: bytes */len</c>）；只有知道長度時才判斷得出來</summary>
    Unsatisfiable,

    /// <summary>後綴範圍 <c>bytes=-N</c>：最後 N 個位元組（206）</summary>
    Suffix,
}

/// <summary>
/// 位元組範圍：<see cref="Start"/> 起（含），到 <see cref="End"/> 為止（含）。長度未知時，
/// 開放式範圍的結尾與後綴範圍的起訖是 <see cref="OpenEnd"/>（待定，向來源問到總長之後再算）
/// </summary>
internal readonly record struct ByteRange(long Start, long End)
{
    /// <summary>待定（長度未知）</summary>
    public const long OpenEnd = -1;

    /// <summary>結尾待定</summary>
    public bool IsOpenEnded => End < 0;

    /// <summary>位元組數（結尾待定時沒有意義，丟例外）</summary>
    public long Length => IsOpenEnded
        ? throw new InvalidOperationException("範圍的結尾待定")
        : End - Start + 1;
}

/// <summary>
/// HTTP Range 的三態解析（Full／Satisfiable／Unsatisfiable，另加 Suffix），伺服器的媒體轉送與 blob 端點、
/// 桌面的 WebView2 本機媒體（<c>RangeRules.Parse</c> 委派這裡）共用同一份。只支援單一區間；純函式
/// </summary>
internal static class HttpRangeRules
{
    /// <summary>
    /// 解析 <c>bytes=start-end</c>、<c>bytes=start-</c>、<c>bytes=-suffix</c>（前綴不分大小寫、數字前後可以有空白）。
    /// <paramref name="length"/> 是表示的總長；未知時傳 null：開放式範圍回 Satisfiable 但 End 是 <see cref="ByteRange.OpenEnd"/>，
    /// 後綴範圍回 Suffix、Range 的起訖都是 <see cref="ByteRange.OpenEnd"/>，起點在結尾之後的判斷交給知道長度的一方。
    /// 長度已知時：結尾超過總長就縮到最後一個位元組、後綴比總長長就從 0 開始；長度 0 一律 Full（206 沒辦法表示空範圍）。
    /// <c>SuffixLength</c> 只在 Suffix 時有值（其他是 0）
    /// </summary>
    public static (RangeKind Kind, ByteRange Range, long SuffixLength) Parse(string? header, long? length)
    {
        if (length is < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "長度不能是負的");
        var full = (RangeKind.Full, default(ByteRange), 0L);
        if (string.IsNullOrWhiteSpace(header) || length == 0)
            return full;
        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return full;
        var spec = value[6..].Trim();
        if (spec.Contains(','))
            return full;
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return full;
        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();

        if (left.Length == 0)
        {
            // 最後 N 個位元組；N 是 0 或不是數字一律忽略
            if (!TryNumber(right, out var suffix) || suffix <= 0)
                return full;
            if (length is not { } known)
                return (RangeKind.Suffix, new ByteRange(ByteRange.OpenEnd, ByteRange.OpenEnd), suffix);
            return (RangeKind.Suffix, new ByteRange(Math.Max(0, known - suffix), known - 1), suffix);
        }

        if (!TryNumber(left, out var start))
            return full;
        long? end = null;
        if (right.Length > 0)
        {
            if (!TryNumber(right, out var last) || last < start)
                return full;
            end = last;
        }

        if (length is not { } total)
            return (RangeKind.Satisfiable, new ByteRange(start, end ?? ByteRange.OpenEnd), 0);
        if (start >= total)
            return (RangeKind.Unsatisfiable, default, 0);
        return (RangeKind.Satisfiable, new ByteRange(start, Math.Min(end ?? total - 1, total - 1)), 0);
    }

    /// <summary>206 的 <c>Content-Range</c>：<c>bytes start-end/length</c></summary>
    public static string ContentRange(ByteRange range, long length)
    {
        if (range.Start < 0 || range.End < range.Start || range.End >= length)
            throw new ArgumentOutOfRangeException(nameof(range), "範圍不在 0～length-1 之內");
        return string.Create(CultureInfo.InvariantCulture, $"bytes {range.Start}-{range.End}/{length}");
    }

    /// <summary>416 的 <c>Content-Range</c>：<c>bytes */length</c></summary>
    public static string Unsatisfied(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return string.Create(CultureInfo.InvariantCulture, $"bytes */{length}");
    }

    /// <summary>
    /// 解析 206 的 <c>Content-Range: bytes start-end/length</c>（<see cref="ContentRange"/> 的反向；裝置回覆轉送、桌面複本讀伺服器的回應用）。
    /// 單位只收 <c>bytes</c>（不分大小寫）、數字只收十進位、總長要知道（不收 <c>*</c>），而且 0 ≤ start ≤ end &lt; length；其他一律 false
    /// </summary>
    public static bool TryParseContentRange(string? header, out ByteRange range, out long length)
    {
        range = default;
        length = 0;
        if (string.IsNullOrWhiteSpace(header))
            return false;
        var value = header.Trim();
        if (!value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
            return false;
        var spec = value[6..].Trim();
        var slash = spec.IndexOf('/');
        var dash = spec.IndexOf('-');
        if (dash <= 0 || slash < dash)
            return false;
        if (!TryNumber(spec[..dash].Trim(), out var start) || !TryNumber(spec[(dash + 1)..slash].Trim(), out var end)
            || !TryNumber(spec[(slash + 1)..].Trim(), out var total))
            return false;
        if (end < start || end >= total)
            return false;
        range = new ByteRange(start, end);
        length = total;
        return true;
    }

    /// <summary>只收十進位數字（沒有正負號、空白在外面已經去掉）；超出 long 的不合格</summary>
    private static bool TryNumber(string text, out long value)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
