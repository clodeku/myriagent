namespace Myriagent.Security;

/// <summary>
/// Hello 時記下的伺服器時鐘（<see cref="ServerClockRules.HelloClock"/> 的結果）：伺服器在 HelloResponse 給的 <c>serverTime</c>，
/// 加上送出 Hello 與收到回應時的單調時鐘（<c>Stopwatch.GetTimestamp()</c>）
/// </summary>
/// <param name="ServerTimeMs">HelloResponse 的 serverTime（Unix 毫秒）</param>
/// <param name="SentTimestamp">送出 Hello 時的單調時鐘</param>
/// <param name="ReceivedTimestamp">收到 HelloResponse 時的單調時鐘</param>
/// <param name="Frequency">單調時鐘每秒的刻度數（<c>Stopwatch.Frequency</c>）</param>
internal sealed record ServerClock(long ServerTimeMs, long SentTimestamp, long ReceivedTimestamp, long Frequency);

/// <summary>伺服器現在時間的下界與上界（Unix 毫秒）：真正的伺服器時間一定落在兩者之間</summary>
internal readonly record struct ServerTimeBounds(long Lower, long Upper);

/// <summary>
/// 裝置以伺服器的時間判斷遠端指令的信封（設計 14.11 D4、docs/sync-protocol.md 14.4）：裝置不用自己的 UTC 時鐘（可能被調、可能不準），
/// 只用 Hello 時伺服器給的 <c>serverTime</c> 加上單調時鐘經過的時間推出界線。伺服器寫 <c>serverTime</c> 的時刻在「送出 Hello」與
/// 「收到回應」之間，所以：
/// <list type="bullet">
/// <item><c>Lower = serverTime ＋（現在 − 收到回應）</c>（無條件捨去到毫秒）</item>
/// <item><c>Upper = serverTime ＋（現在 − 送出 Hello）</c>（無條件進位到毫秒）</item>
/// </list>
/// 上下界的差就是往返時間；往返超過 <see cref="MaxRttMs"/> 時界線太寬，視為不可用（信封除了 <c>stop</c> 一律 <c>expired</c>）。
/// 睡眠或休眠恢復後同步引擎一律重新 Hello（12.1），之前推出的界線不再使用（計畫 R7）。全部是純函式：時鐘的讀數由呼叫端傳入
/// </summary>
internal static class ServerClockRules
{
    /// <summary>Hello 的往返超過它（毫秒）就不可用</summary>
    public const long MaxRttMs = 30_000;

    /// <summary>
    /// 記下 Hello 的時鐘；往返超過 <see cref="MaxRttMs"/>（剛好等於算可用）、收到早於送出、頻率不是正數時回 null（不可用）
    /// </summary>
    public static ServerClock? HelloClock(long serverTimeMs, long sentTimestamp, long receivedTimestamp, long frequency)
    {
        if (frequency <= 0 || receivedTimestamp < sentTimestamp)
            return null;
        var rtt = CeilingMs((Int128)receivedTimestamp - sentTimestamp, frequency);
        return rtt <= MaxRttMs ? new ServerClock(serverTimeMs, sentTimestamp, receivedTimestamp, frequency) : null;
    }

    /// <summary>Hello 的往返時間（毫秒，無條件進位）；時鐘不合格時回 null</summary>
    public static long? RttMs(ServerClock? clock)
    {
        if (clock is not { Frequency: > 0 } c || c.ReceivedTimestamp < c.SentTimestamp)
            return null;
        var rtt = CeilingMs((Int128)c.ReceivedTimestamp - c.SentTimestamp, c.Frequency);
        return rtt <= long.MaxValue ? (long)rtt : null;
    }

    /// <summary>
    /// 現在（單調時鐘 <paramref name="nowTimestamp"/>）伺服器時間的界線。時鐘是 null、不可用（往返太長、頻率不合理）、
    /// 現在早於收到回應（單調時鐘倒退）或算出來超出 long 時回 null
    /// </summary>
    public static ServerTimeBounds? Bounds(ServerClock? clock, long nowTimestamp)
    {
        if (clock is not { } c || RttMs(c) is not { } rtt || rtt > MaxRttMs || nowTimestamp < c.ReceivedTimestamp)
            return null;
        var lower = c.ServerTimeMs + FloorMs((Int128)nowTimestamp - c.ReceivedTimestamp, c.Frequency);
        var upper = c.ServerTimeMs + CeilingMs((Int128)nowTimestamp - c.SentTimestamp, c.Frequency);
        if (lower < long.MinValue || upper > long.MaxValue)
            return null;
        return new ServerTimeBounds((long)lower, (long)upper);
    }

    private static Int128 FloorMs(Int128 ticks, long frequency) => ticks * 1000 / frequency;

    private static Int128 CeilingMs(Int128 ticks, long frequency) => (ticks * 1000 + frequency - 1) / frequency;
}
