using MyriagentHub.Ops;

namespace MyriagentHub.Media;

/// <summary>
/// 每日媒體清理的純函式規則（WP4.7 計畫 D18、D19、D41）：什麼時候能刪 blob、暫存檔與孤兒檔。
/// 刪除門檻 <c>before</c>＝所有備份（每日、每週、部署前、還原前、手動）中最舊那一份的時間減 1 天：
/// 引用在 <c>before</c> 之前就移除的 blob 不會被任何還在的備份引用，還原哪一份都讀得到內容。讀不到任何備份就不刪（項目與縮圖）
/// </summary>
internal static class MediaMaintenanceRules
{
    /// <summary>刪除門檻比最舊的備份再早的時間</summary>
    public static readonly TimeSpan BackupMargin = TimeSpan.FromDays(1);

    /// <summary>剛存入的 blob（還沒來得及被引用）、暫存檔與孤兒檔至少要這麼舊才刪</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(1);

    /// <summary>
    /// 備份資料夾的時間：每日備份（<c>backups\daily\</c>，<see cref="BackupRules.ParseFolder"/> 解析得了的名稱）以名稱、本機時區；
    /// 其他（部署前 <c>predeploy-*</c>、還原前 <c>pre-restore-*</c>、名稱看不懂的）以資料夾的建立時間（UTC）——寧可把門檻往前推、少刪
    /// </summary>
    public static DateTimeOffset BackupTime(string folderName, bool daily, DateTime createdUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (daily && BackupRules.ParseFolder(folderName) is { } local)
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
        }

        return new DateTimeOffset(DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc));
    }

    /// <summary>刪除門檻：最舊的備份時間減 <see cref="BackupMargin"/>；一份備份都沒有回 null（項目與縮圖一律不刪）</summary>
    public static DateTimeOffset? DeleteBefore(IEnumerable<DateTimeOffset> backupTimes)
    {
        ArgumentNullException.ThrowIfNull(backupTimes);
        DateTimeOffset? oldest = null;
        foreach (var at in backupTimes)
        {
            if (oldest is null || at < oldest)
                oldest = at;
        }

        return oldest - BackupMargin;
    }

    /// <summary>上傳的引用（<c>{uploadId}@{unixMs}</c>）過期了：滿 24 小時；看不懂的 ref_id 當成過期（不會再被任何上傳用到）</summary>
    public static bool UploadExpired(string refId, long nowMs) =>
        !UploadRules.TryParseRefId(refId, out _, out var atMs) || UploadRules.IsExpired(atMs, nowMs);

    /// <summary>
    /// 沒有任何引用的 blob 能不能刪（呼叫端已確認沒有 <c>blob_refs</c>）：存入至少 <see cref="MinAge"/>，而且
    /// <list type="bullet">
    /// <item>從來沒被引用過的上傳（<c>kind=upload</c>、<c>last_ref_at</c> 等於 <c>created_at</c>：上傳的引用過期移除時不更新 <c>last_ref_at</c>）
    /// 不在任何備份的內容裡，不必等保留窗；</item>
    /// <item>其他（項目欄位、縮圖、曾被項目或縮圖引用過的上傳）要 <c>last_ref_at</c> 早於門檻 <paramref name="beforeMs"/>（null＝不刪）。</item>
    /// </list>
    /// </summary>
    public static bool Deletable(BlobRow blob, long nowMs, long? beforeMs)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (nowMs - blob.CreatedAt < (long)MinAge.TotalMilliseconds)
            return false;
        if (blob.Kind == BlobKinds.Upload && blob.LastRefAt == blob.CreatedAt)
            return true;
        return beforeMs is { } before && blob.LastRefAt < before;
    }

    /// <summary>
    /// <c>tmp\</c> 裡這個檔能不能刪：只看 WP4.7 自己的前綴（<c>blob-*</c>、<c>thumb-*</c>；還原與部署的暫存不碰），修改時間至少 <see cref="MinAge"/> 之前
    /// </summary>
    public static bool TmpExpired(string fileName, DateTime lastWriteUtc, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!fileName.StartsWith(BlobStore.TmpPrefix, StringComparison.Ordinal) && !fileName.StartsWith(ThumbCache.TmpPrefix, StringComparison.Ordinal))
            return false;
        return OldEnough(lastWriteUtc, now);
    }

    /// <summary>修改時間（UTC）至少 <see cref="MinAge"/> 之前（孤兒檔的對帳用）</summary>
    public static bool OldEnough(DateTime lastWriteUtc, DateTimeOffset now) =>
        now - new DateTimeOffset(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc)) >= MinAge;
}
