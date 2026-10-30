namespace MyriagentHub.Media;

/// <summary>blob 用量的等級（ops/status 的 <c>blobs.level</c>）</summary>
internal enum BlobQuotaLevel
{
    Ok,

    /// <summary>到告警門檻（預設 80%）：<c>blob_quota</c></summary>
    Warn,

    /// <summary>到拒收門檻（預設 95%）或磁碟可用空間低於下限：新的 blob 一律 507（<c>blob_refuse</c>）</summary>
    Refuse,
}

/// <summary>新的 blob 不收的原因</summary>
internal enum BlobQuotaRefusal
{
    /// <summary>存了之後用量到拒收門檻</summary>
    Quota,

    /// <summary>存了之後可用空間低於下限，或可用空間量不到</summary>
    DiskLow,
}

/// <summary>
/// blob 的配額與磁碟下限（設計 3.4:633、WP4.7 計畫 D17）。純函式；可用空間是負的（量不到）一律當成不足：
/// 寧可拒收新的 blob（ops/status 看得到），也不要在不知道剩多少空間時寫 系統磁碟
/// </summary>
internal static class BlobQuotaRules
{
    /// <summary>目前的等級：用量 ≥ 拒收門檻或可用空間 &lt; 下限是 Refuse，用量 ≥ 告警門檻是 Warn</summary>
    public static BlobQuotaLevel Level(long usedBytes, long quotaBytes, int alertPercent, int refusePercent, long freeBytes, long minFreeBytes)
    {
        if (freeBytes < 0 || freeBytes < minFreeBytes || AtLeast(usedBytes, quotaBytes, refusePercent))
            return BlobQuotaLevel.Refuse;
        return AtLeast(usedBytes, quotaBytes, alertPercent) ? BlobQuotaLevel.Warn : BlobQuotaLevel.Ok;
    }

    /// <summary>
    /// 收得下 <paramref name="incoming"/> 個位元組：存了之後等級仍不是 Refuse（用量低於拒收門檻、可用空間不低於下限）。
    /// 所以已經是 Refuse 時連 0 位元組的也不收
    /// </summary>
    public static bool Admits(long usedBytes, long incoming, long quotaBytes, int refusePercent, long freeBytes, long minFreeBytes)
        => Check(usedBytes, incoming, quotaBytes, refusePercent, freeBytes, minFreeBytes) is null;

    /// <summary>同 <see cref="Admits"/>，另外給不收的原因（磁碟優先）；收得下回 null</summary>
    public static BlobQuotaRefusal? Check(long usedBytes, long incoming, long quotaBytes, int refusePercent, long freeBytes, long minFreeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(incoming);
        if (freeBytes < 0 || (Int128)freeBytes - incoming < minFreeBytes)
            return BlobQuotaRefusal.DiskLow;
        return AtLeast((Int128)usedBytes + incoming, quotaBytes, refusePercent) ? BlobQuotaRefusal.Quota : null;
    }

    /// <summary>usedBytes ≥ quotaBytes × percent ÷ 100（不溢位、不捨入）</summary>
    private static bool AtLeast(Int128 usedBytes, long quotaBytes, int percent)
        => usedBytes * 100 >= (Int128)quotaBytes * percent;
}
