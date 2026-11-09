using Myriagent.Sync;

namespace Myriagent.Media;

/// <summary>
/// 媒體轉送（設計 4.10、WP4.7 計畫 D11）的容量上限與分段：伺服器向來源電腦要的每一段 ≤8 MB、
/// 沒帶 Range 時以多段接成一個 200（總長 ≤64 MB，檔案下載 ≤512 MB）、每台來源同時 ≤20 個轉送。純函式
/// </summary>
internal static class MediaRelayRules
{
    /// <summary>單段上限（設計:1061）</summary>
    public const long MaxSegmentBytes = 8L * 1024 * 1024;

    /// <summary>沒帶 Range 時整份回 200 的總長上限；超過回 413（瀏覽器要改帶 Range）</summary>
    public const long MaxUnrangedBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 檔案下載（<c>/f/</c>）沒帶 Range 時的總長上限：瀏覽器直接下載（開新分頁、<c>&lt;a download&gt;</c>）不帶 Range，簡報這類大檔要整份給；
    /// 照樣每段 ≤8 MB 依序轉送，伺服器不整份放進記憶體。來源電腦另外限制（文字 ≤20 MB、二進位 ≤512 MB）
    /// </summary>
    public const long MaxUnrangedFileBytes = 512L * 1024 * 1024;

    /// <summary>電腦狀態面板的檔案傳輸（<c>xfer</c> 參照，2026-11-09）沒帶 Range 時的總長上限：瀏覽器版以 &lt;a download&gt; 整份下載</summary>
    public const long MaxUnrangedTransferBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>每台來源同時的轉送數（設計:1061）；再來的回 503 加 Retry-After</summary>
    public const int MaxConcurrentPerDevice = 20;

    /// <summary>
    /// 一段要向來源要的範圍：從 <paramref name="start"/> 起最多 <see cref="MaxSegmentBytes"/>；
    /// <paramref name="end"/> 是 null（開放式）或更遠時縮成 8 MB。結尾在起點之前或負的丟例外（開放式要傳 null，不是 -1）
    /// </summary>
    public static MediaRange ClampSegment(long start, long? end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (end is { } last && last < start)
            throw new ArgumentOutOfRangeException(nameof(end), "結尾在起點之前");
        var limit = SegmentEnd(start);
        return new MediaRange(start, end is { } e && e < limit ? e : limit);
    }

    /// <summary>把 <paramref name="start"/>～<paramref name="end"/>（含）切成依序、不重疊、每段 ≤8 MB 的範圍</summary>
    public static IEnumerable<MediaRange> Segments(long start, long end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (end < start)
            throw new ArgumentOutOfRangeException(nameof(end), "結尾在起點之前");
        return Iterate(start, end);

        static IEnumerable<MediaRange> Iterate(long from, long to)
        {
            var s = from;
            while (true)
            {
                var last = Math.Min(to, SegmentEnd(s));
                yield return new MediaRange(s, last);
                if (last == to)
                    yield break;
                s = last + 1;
            }
        }
    }

    /// <summary>從 <paramref name="start"/> 起 8 MB 的最後一個位元組（不溢位）</summary>
    private static long SegmentEnd(long start)
        => start > long.MaxValue - (MaxSegmentBytes - 1) ? long.MaxValue : start + MaxSegmentBytes - 1;
}
