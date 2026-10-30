using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary>
/// ops/status（與 <c>/readyz</c>）的 <c>blobs</c> 區塊（WP4.7 計畫 D42；只加不改）：全部取自記憶體（status 本身不查資料庫）。
/// 告警 <c>blob_quota</c>（用量到告警門檻）與 <c>blob_refuse</c>（到拒收門檻或磁碟可用空間低於下限）由 <see cref="Level"/> 交給 <c>HealthRules</c>
/// </summary>
internal sealed class MediaOpsStatus(BlobStore blobs, ThumbCache thumbs, UploadRegistry uploads, MediaRelay relay, IOptions<HubOptions> options)
{
    /// <summary>目前的配額等級（含磁碟可用空間的下限）</summary>
    public BlobQuotaLevel Level() => blobs.Level();

    public object Status() => Block(blobs.Count, blobs.UsedBytes, options.Value.Media.QuotaBytes, Level(), thumbs.Bytes, uploads.PendingBytes, relay.Active, relay.Timeouts);

    /// <summary>區塊的形狀（純函式，測試鎖住欄位名稱）</summary>
    internal static object Block(long count, long bytes, long quotaBytes, BlobQuotaLevel level, long thumbCacheBytes, long uploadsPending, int relaysActive,
        long relayTimeouts) => new
    {
        count,
        bytes,
        quotaBytes,
        level = LevelName(level),
        thumbCacheBytes,
        uploadsPending,
        relaysActive,
        relayTimeouts,
    };

    /// <summary><c>level</c> 的值：<c>ok</c>／<c>warn</c>／<c>refuse</c></summary>
    public static string LevelName(BlobQuotaLevel level) => level switch
    {
        BlobQuotaLevel.Ok => "ok",
        BlobQuotaLevel.Warn => "warn",
        BlobQuotaLevel.Refuse => "refuse",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "不認得的配額等級"),
    };
}
