namespace Myriagent.Hub;

/// <summary>
/// 很長的執行中項目（串流中的長回覆、長的思考）依長度降低送畫面的頻率。每送一次，中樞都要把整則文字重新轉成 HTML（Markdig），
/// 2026-10-23 實測一次約是：5 KB 0.3 ms、20 KB 1.4 ms、50 KB 4.3 ms、100 KB 7.4 ms——照合併間隔每秒 20 次，一則 100 KB 的串流就吃掉
/// UI 執行緒每秒約 150 ms，網頁也每次整段重排。短的照舊每次合併都送；結束（不是執行中）的一律馬上送
/// </summary>
internal static class StreamFlushRules
{
    /// <summary>文字（UTF-16 字元）不超過這麼長的，每次合併（50 ms）都送</summary>
    public const int ShortChars = 8_000;

    /// <summary>不超過這麼長的每 100 ms 送一次，再長的每 200 ms</summary>
    public const int MediumChars = 32_000;

    /// <summary>這個長度的執行中項目至少隔多久才再送一次（0＝每次合併都送）</summary>
    public static int IntervalMs(int textChars) => textChars switch
    {
        <= ShortChars => 0,
        <= MediumChars => 100,
        _ => 200,
    };

    /// <summary>這一輪要不要先不送這一筆：執行中、夠長、距上次送還不到 <see cref="IntervalMs"/>（沒送過的照送）</summary>
    public static bool Defer(bool running, int textChars, long? lastFlushMs, long nowMs) =>
        running && lastFlushMs is { } last && nowMs - last < IntervalMs(textChars);
}
