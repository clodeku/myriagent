namespace Myriagent.Remote;

/// <summary>
/// 遠端指令的速率（設計 14.11 D25）：每台電腦每分鐘層級 0 60 次、層級 1 30 次、層級 2 5 次，各層級分開算。
/// 伺服器先擋（瀏覽器看到 <c>rate_limited</c>），電腦以 <see cref="RemoteRateWindow"/> 再擋。時間一律是單調時鐘（毫秒，例如
/// <c>Stopwatch</c> 換算），不受系統時間調整影響
/// </summary>
internal static class RemoteRateRules
{
    /// <summary>滑動窗的長度</summary>
    public const long WindowMs = 60_000;

    /// <summary>每個滑動窗最多幾次；層級超出 0～2 丟例外</summary>
    public static int LimitFor(int tier) => tier switch
    {
        0 => 60,
        1 => 30,
        2 => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "層級是 0～2"),
    };
}

/// <summary>
/// 一台電腦的滑動窗（每個層級一個）：最近 <see cref="RemoteRateRules.WindowMs"/> 內接受過的次數到了上限就拒絕。
/// 不加鎖：伺服器每台裝置一個、在它自己的鎖裡用；電腦只在中樞閘門的 UI 執行緒用
/// </summary>
internal sealed class RemoteRateWindow
{
    private readonly Queue<long>[] accepted = [new(), new(), new()];

    /// <summary>在 <paramref name="nowMs"/>（單調時鐘）接受一次這個層級的指令；超過上限回 false（不計入）</summary>
    public bool TryAcquire(int tier, long nowMs)
    {
        var limit = RemoteRateRules.LimitFor(tier);
        var queue = accepted[tier];
        while (queue.Count > 0 && nowMs - queue.Peek() >= RemoteRateRules.WindowMs)
            queue.Dequeue();
        if (queue.Count >= limit)
            return false;
        queue.Enqueue(nowMs);
        return true;
    }

    /// <summary>這個層級目前窗內的次數（測試與狀態用）</summary>
    public int CountIn(int tier, long nowMs)
    {
        RemoteRateRules.LimitFor(tier);
        return accepted[tier].Count(at => nowMs - at < RemoteRateRules.WindowMs);
    }
}
