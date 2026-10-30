using System.Globalization;
using System.Text.RegularExpressions;

namespace FakeDevice;

/// <summary>
/// 串流文字裡的時間標記（WP4.9 計畫 D8；tools/hub-e2e/lib/stats-rules.mjs 的 parseStamps 讀同一種）：全形的〔〕夾住剛好 13 位數字的 UTC 毫秒。
/// 全形括號不是權杖字元（<c>CredentialLeakRules.SafeLength</c> 不會把結尾的標記當成還沒傳完的權杖扣住），13 位數字比 base64 樣式的下限短、
/// 也不是任何權杖的樣式，所以 DLP 不改它；瀏覽器端從項目文字解析出產生的時間，算即時與持久兩條路的延遲
/// </summary>
internal static partial class StampRules
{
    public const char Open = '〔';

    public const char Close = '〕';

    /// <summary>一個標記的長度（括號＋13 位數字）</summary>
    public const int Length = 15;

    /// <summary>時間 → 標記；UTC 毫秒不是 13 位數字（2001-09-09 之前、2286-11-20 之後）丟例外</summary>
    public static string Format(DateTimeOffset at)
    {
        var ms = at.ToUnixTimeMilliseconds();
        if (ms is < 1_000_000_000_000 or > 9_999_999_999_999)
            throw new ArgumentOutOfRangeException(nameof(at), at, "時間標記只收 13 位數字的 UTC 毫秒");
        return Open + ms.ToString("D13", CultureInfo.InvariantCulture) + Close;
    }

    /// <summary>文字裡的時間標記（UTC 毫秒，依出現順序）；不是剛好 13 位數字的不算</summary>
    public static IReadOnlyList<long> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var result = new List<long>();
        foreach (Match m in StampPattern().Matches(text))
            result.Add(long.Parse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture));
        return result;
    }

    // 只認 ASCII 數字（\d 在 .NET 也收全形與其他文字的數字）
    [GeneratedRegex("〔([0-9]{13})〕", RegexOptions.CultureInvariant)]
    private static partial Regex StampPattern();
}
