namespace Myriagent.Update;

/// <summary>
/// 檢查或下載暫時失敗之後多久再試（app 自我更新 U3；設計第 12 節第 2 列、第 16 節第 12 項）：伺服器給了 <c>Retry-After</c> 就照它，
/// 否則 1、2、4…分鐘加倍；都夾在 <see cref="MinDelay"/>～<see cref="MaxDelay"/>（60 分鐘）。每 6 小時的定期檢查照舊另外跑
/// </summary>
internal static class AppUpdateRetryRules
{
    public static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(60);

    /// <param name="failures">連續失敗的次數（這次也算，≥1）</param>
    /// <param name="retryAfter">伺服器的 Retry-After（沒有是 null）</param>
    public static TimeSpan NextDelay(int failures, TimeSpan? retryAfter)
    {
        if (retryAfter is { } after)
            return Clamp(after);
        var exponent = Math.Clamp(failures - 1, 0, 6);
        return Clamp(TimeSpan.FromMinutes(1 << exponent));
    }

    /// <summary>伺服器說有新版時（Hello 的 latestApp、ServerNotice{appRelease}）兩次提早檢查之間至少隔多久（設計 7.4）</summary>
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 伺服器說有新版時要不要提早檢查：release 合法、比這台新（開發版一律不提早：不會自動更新）、不是已經備妥的那一版，
    /// 而且距上一次因此檢查至少 <see cref="AnnounceInterval"/>（重連時每次 Hello 都帶 latestApp）
    /// </summary>
    /// <param name="lastCheckMs">上一次因此檢查的單調時鐘毫秒（沒有是 null）</param>
    public static bool ShouldCheckOnAnnounce(string? release, ReleaseIdentity current, string? stagedRelease, long? lastCheckMs, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.IsDev || !ReleaseIdentity.TryParseRelease(release, out var announced) || announced!.Version <= current.Version)
            return false;
        if (string.Equals(release, stagedRelease, StringComparison.Ordinal))
            return false;
        return lastCheckMs is not { } last || nowMs - last >= (long)AnnounceInterval.TotalMilliseconds;
    }

    private static TimeSpan Clamp(TimeSpan delay) => delay < MinDelay ? MinDelay : delay > MaxDelay ? MaxDelay : delay;
}
