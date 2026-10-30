namespace MyriagentHub.Realtime;

/// <summary>
/// 權杖桶（瀏覽器的 Cmd 與 ClientState 共用，以工作階段分區；設計 8.5）：一開始有 <paramref name="burst"/> 個，每秒補 <paramref name="ratePerSecond"/> 個、
/// 最多補到 <paramref name="burst"/>。呼叫端自己加鎖。時間倒退時不補也不丟例外，並以倒退後的時間當新的起點（不會因時鐘被往回調而一直拿不到）
/// </summary>
internal sealed class TokenBucket(double ratePerSecond, int burst, long nowMs)
{
    private double tokens = burst;
    private long lastMs = nowMs;

    public bool TryTake(long nowMs)
    {
        if (nowMs > lastMs)
            tokens = Math.Min(burst, tokens + (nowMs - lastMs) * ratePerSecond / 1000.0);
        lastMs = nowMs;
        if (tokens < 1)
            return false;
        tokens -= 1;
        return true;
    }

    /// <summary>到 <paramref name="nowMs"/> 為止補滿了沒（不改狀態；時間倒退時當成沒有經過）。補滿的桶等同新建的</summary>
    public bool IsFull(long nowMs) => tokens + Math.Max(0, nowMs - lastMs) * ratePerSecond / 1000.0 >= burst;
}

/// <summary>送出佇列溢出時怎麼做：重送 init（resync）或中斷連線（慢用戶端）</summary>
internal enum OverflowAction
{
    Resync,
    Abort,
}

/// <summary>瀏覽器連線的上限規則（設計 14.6；計畫 D7、D10）</summary>
internal static class BrowserLimitRules
{
    public const long OverflowWindowMs = 60_000;

    /// <summary>60 秒內第幾次溢出就中斷連線（含這次）</summary>
    public const int OverflowsBeforeAbort = 3;

    /// <summary>還能不能再收一條連線（傳入的是加入前的數目）：每個工作階段與全部各有上限</summary>
    public static bool CanAdmit(int sessionConnections, int totalConnections, int maxPerSession, int maxTotal) =>
        sessionConnections < maxPerSession && totalConnections < maxTotal;

    /// <summary>
    /// 這次溢出要重送 init 還是中斷：60 秒內（含這次）第 3 次就中斷。<paramref name="recentOverflowAtMs"/> 是之前的溢出時間（不含這次），
    /// 剛好 60 秒前的不算；時鐘倒退時比現在還晚的照算（寧可多斷一次，用戶端會重連）
    /// </summary>
    public static OverflowAction OnOverflow(IReadOnlyList<long> recentOverflowAtMs, long nowMs)
    {
        var recent = recentOverflowAtMs.Count(at => nowMs - at < OverflowWindowMs);
        return recent + 1 >= OverflowsBeforeAbort ? OverflowAction.Abort : OverflowAction.Resync;
    }
}

/// <summary>閘道推送的節流規則（設計 4.9.2：最多每 500 ms 一次）</summary>
internal static class BrowserPushRules
{
    /// <summary>
    /// 要不要讀快照：還沒有快照 → 是；距上次讀不到 pushInterval → 否；有髒旗標或距上次讀已達 refresh（保底重讀）→ 是。
    /// 時間一律用 TimeProvider.GetTimestamp（單調時鐘；測試的 ManualTimeProvider 沒覆寫它，所以主機測試是真實時間）
    /// </summary>
    public static bool ReadDue(TimeSpan? sinceLastRead, bool dirty, bool hasSnapshot, TimeSpan pushInterval, TimeSpan refresh)
    {
        if (!hasSnapshot || sinceLastRead is not { } since)
            return true;
        if (since < pushInterval)
            return false;
        return dirty || since >= refresh;
    }
}
