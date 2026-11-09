using System.Globalization;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary>一次每日媒體清理的結果（測試與記錄用）</summary>
/// <param name="DeleteBefore">刪除門檻（沒有任何備份是 null：項目與縮圖的 blob 一律不刪）</param>
/// <param name="BlobsSkipped">索引還沒追上最新的事件，這次不刪 blob（下次再試）</param>
internal sealed record MediaMaintenanceResult(
    int TmpDeleted,
    int UploadRefsExpired,
    int MediaRefsExpired,
    DateTimeOffset? DeleteBefore,
    bool BlobsSkipped,
    int BlobsDeleted,
    long BytesFreed,
    int ThumbCacheTrimmed,
    int RowsWithoutFile,
    int OrphanFiles);

/// <summary>
/// 每日媒體清理（WP4.7 計畫 D19；每日維護在備份之後呼叫，冪等、失敗只記錄）。依序：
/// <list type="number">
/// <item><c>tmp\</c> 裡 WP4.7 自己的暫存檔（<c>blob-*</c>、<c>thumb-*</c>）超過 1 小時的刪掉（啟動時 <see cref="BlobStore"/>、<see cref="ThumbCache"/> 的啟動步驟已整批清過一次）；</item>
/// <item>過期的上傳引用（24 小時）刪掉；過期的抽屜（<c>list</c>，30 天）與 <c>fileFetch</c>（<c>file</c>，15 分鐘）參照刪掉（請求時本來就擋，D7）；</item>
/// <item>沒有任何引用的 blob 依 <see cref="MediaMaintenanceRules.Deletable"/> 刪（門檻＝最舊的備份減 1 天）；索引還沒追上最新的事件時這一步跳過；</item>
/// <item>縮圖快取超過上限時刪最舊的；</item>
/// <item>對帳：有列沒檔的列刪掉並記警告，沒有列、修改時間超過 1 小時的檔刪掉（逐一個前 2 碼的資料夾做，記憶體有上限）。</item>
/// </list>
/// 讀用唯讀短連線，寫經 <see cref="SyncWriter"/> 一批一個交易（每批 ≤<see cref="Batch"/>），不長時間佔住寫入者
/// </summary>
internal sealed class MediaMaintenance(
    HubPaths paths,
    SyncWriter writer,
    BlobStore blobs,
    ThumbCache thumbs,
    MediaRefIndexer indexer,
    IOptions<HubOptions> options,
    TimeProvider time,
    ILogger<MediaMaintenance> log)
{
    /// <summary>一個寫入交易處理幾列</summary>
    public const int Batch = 500;

    /// <summary>刪 blob 之前等索引追上的上限</summary>
    public static readonly TimeSpan IndexerWait = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<MediaMaintenanceResult> RunAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var tmp = CleanTmp();
            var uploads = await ExpireUploadRefsAsync(cancellationToken);
            var refs = await ExpireMediaRefsAsync(cancellationToken);

            var before = DeleteBefore();
            var caughtUp = await indexer.CatchUpAsync(IndexerWait, cancellationToken);
            var (deleted, freed) = caughtUp ? await DeleteBlobsAsync(before, cancellationToken) : (0, 0L);
            if (!caughtUp)
                log.LogWarning("媒體參照的索引還沒追上最新的事件，這次不刪 blob");

            var trimmed = thumbs.Trim(options.Value.Media.ThumbCacheBytes);
            var (rowsWithoutFile, orphans) = await ReconcileAsync(cancellationToken);
            var result = new MediaMaintenanceResult(tmp, uploads, refs, before, !caughtUp, deleted, freed, trimmed, rowsWithoutFile, orphans);
            log.LogInformation(
                "媒體清理：暫存檔 {Tmp}、過期上傳引用 {Uploads}、過期參照 {Refs}、blob {Blobs} 個（{Bytes} 位元組，門檻 {Before}）、縮圖快取 {Trimmed}、有列沒檔 {Rows}、孤兒檔 {Orphans}",
                tmp, uploads, refs, deleted, freed, before?.ToString("O", CultureInfo.InvariantCulture) ?? "無備份", trimmed, rowsWithoutFile, orphans);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>刪除門檻：所有備份資料夾（<c>backups\daily\*</c> 與 <c>backups\</c> 底下其他的，如 <c>predeploy-*</c>、<c>pre-restore-*</c>）中最舊的減 1 天</summary>
    internal DateTimeOffset? DeleteBefore()
    {
        var zone = time.LocalTimeZone;
        var times = new List<DateTimeOffset>();
        try
        {
            if (Directory.Exists(paths.DailyBackupsDir))
            {
                foreach (var dir in new DirectoryInfo(paths.DailyBackupsDir).EnumerateDirectories())
                    times.Add(MediaMaintenanceRules.BackupTime(dir.Name, daily: true, dir.CreationTimeUtc, zone));
            }

            if (Directory.Exists(paths.BackupsDir))
            {
                var daily = Path.GetFullPath(paths.DailyBackupsDir);
                foreach (var dir in new DirectoryInfo(paths.BackupsDir).EnumerateDirectories())
                {
                    if (!string.Equals(Path.GetFullPath(dir.FullName), daily, StringComparison.OrdinalIgnoreCase))
                        times.Add(MediaMaintenanceRules.BackupTime(dir.Name, daily: false, dir.CreationTimeUtc, zone));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列不出備份就當成沒有備份：項目與縮圖的 blob 這次不刪
            log.LogWarning(ex, "列不出備份資料夾，這次不刪項目與縮圖的 blob");
            return null;
        }

        return MediaMaintenanceRules.DeleteBefore(times);
    }

    private int CleanTmp()
    {
        var now = time.GetUtcNow();
        var deleted = 0;
        foreach (var file in new DirectoryInfo(paths.TmpDir).EnumerateFiles())
        {
            if (!MediaMaintenanceRules.TmpExpired(file.Name, file.LastWriteTimeUtc, now))
                continue;
            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "暫存檔刪不掉：{File}", file.Name);
            }
        }

        return deleted;
    }

    /// <summary>過期的上傳引用（<c>blob_refs(sha, 'upload', '{uploadId}@{ms}')</c>）刪掉；<c>last_ref_at</c> 不更新（見 <see cref="MediaMaintenanceRules.Deletable"/>）</summary>
    private async Task<int> ExpireUploadRefsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var expired = new List<(string Sha, string RefId)>();
        using (var connection = writer.OpenRead())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT sha256, ref_id FROM blob_refs WHERE ref_kind = $k";
            cmd.Parameters.AddWithValue("$k", BlobRefKinds.Upload);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var refId = reader.GetString(1);
                if (MediaMaintenanceRules.UploadExpired(refId, now))
                    expired.Add((reader.GetString(0), refId));
            }
        }

        foreach (var chunk in expired.Chunk(Batch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(c =>
            {
                foreach (var (sha, refId) in chunk)
                    c.Execute("DELETE FROM blob_refs WHERE sha256 = $s AND ref_kind = $k AND ref_id = $id", ("$s", sha), ("$k", BlobRefKinds.Upload), ("$id", refId));
            }, CancellationToken.None);
        }

        return expired.Count;
    }

    /// <summary>過期的抽屜與 <c>fileFetch</c> 參照：列、縮圖的引用（有的話）、縮圖快取（含各版本）</summary>
    private async Task<int> ExpireMediaRefsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expired = new List<(string Device, string Ref, string? Thumb)>();
            using (var connection = writer.OpenRead())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT device_id, ref, thumb_blob FROM media_refs WHERE (purpose = $list AND created_at <= $listBefore) "
                                  + "OR (purpose = $file AND created_at <= $fileBefore) OR (purpose = $xfer AND created_at <= $xferBefore) LIMIT $limit";
                cmd.Parameters.AddWithValue("$list", MediaRefPurposes.List);
                cmd.Parameters.AddWithValue("$listBefore", now - (long)MediaLookupRules.ListLifetime.TotalMilliseconds);
                cmd.Parameters.AddWithValue("$file", MediaRefPurposes.File);
                cmd.Parameters.AddWithValue("$fileBefore", now - (long)MediaLookupRules.FileLifetime.TotalMilliseconds);
                cmd.Parameters.AddWithValue("$xfer", MediaRefPurposes.Transfer);
                cmd.Parameters.AddWithValue("$xferBefore", now - (long)MediaLookupRules.XferLifetime.TotalMilliseconds);
                cmd.Parameters.AddWithValue("$limit", Batch);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    expired.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            if (expired.Count == 0)
                return total;
            await writer.WriteAsync(c =>
            {
                foreach (var (device, mediaRef, thumb) in expired)
                {
                    c.Execute("DELETE FROM media_refs WHERE device_id = $d AND ref = $r", ("$d", device), ("$r", mediaRef));
                    if (thumb is null || !SyncIds.IsDeviceId(device) || !ConversationEventRules.IsMediaRef(mediaRef))
                        continue;
                    if (c.Execute("DELETE FROM blob_refs WHERE sha256 = $s AND ref_kind = $k AND ref_id = $id",
                            ("$s", thumb), ("$k", BlobRefKinds.Thumb), ("$id", MediaKeyRules.Format(device, mediaRef))) > 0)
                        c.Execute("UPDATE blobs SET last_ref_at = $now WHERE sha256 = $s", ("$now", c.Now), ("$s", thumb));
                }
            }, CancellationToken.None);
            foreach (var (device, mediaRef, _) in expired)
            {
                if (SyncIds.IsDeviceId(device) && ConversationEventRules.IsMediaRef(mediaRef))
                    thumbs.Delete(MediaKeyRules.Format(device, mediaRef));
            }

            total += expired.Count;
            if (expired.Count < Batch)
                return total;
        }
    }

    /// <summary>沒有任何引用的 blob：依 sha 分批讀出候選，交給 <see cref="BlobStore.DeleteUnreferencedAsync"/>（持鎖、在寫入者的交易裡重查）</summary>
    private async Task<(int Deleted, long Freed)> DeleteBlobsAsync(DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var beforeMs = before?.ToUnixTimeMilliseconds();
        var after = string.Empty;
        var deleted = 0;
        long freed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var candidates = new List<string>();
            var last = after;
            using (var connection = writer.OpenRead())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT b.sha256, b.size, b.kind, b.created_at, b.last_ref_at FROM blobs b WHERE b.sha256 > $after "
                                  + "AND NOT EXISTS (SELECT 1 FROM blob_refs r WHERE r.sha256 = b.sha256) ORDER BY b.sha256 LIMIT $limit";
                cmd.Parameters.AddWithValue("$after", after);
                cmd.Parameters.AddWithValue("$limit", Batch);
                using var reader = cmd.ExecuteReader();
                var read = 0;
                while (reader.Read())
                {
                    read++;
                    var row = new BlobRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4));
                    last = row.Sha256;
                    if (MediaMaintenanceRules.Deletable(row, now, beforeMs))
                        candidates.Add(row.Sha256);
                }

                if (read == 0)
                    return (deleted, freed);
            }

            if (candidates.Count > 0)
            {
                var rows = await blobs.DeleteUnreferencedAsync(candidates, row => MediaMaintenanceRules.Deletable(row, now, beforeMs), cancellationToken);
                deleted += rows.Count;
                freed += rows.Sum(r => r.Size);
            }

            if (string.CompareOrdinal(last, after) <= 0)
                return (deleted, freed);
            after = last;
        }
    }

    /// <summary>對帳：逐一個前 2 碼（<c>00</c>～<c>ff</c>）比對 <c>blobs</c> 列與 <c>blobs\{前 2 碼}\</c> 的檔</summary>
    private async Task<(int RowsWithoutFile, int Orphans)> ReconcileAsync(CancellationToken cancellationToken)
    {
        var rowsWithoutFile = 0;
        var orphans = 0;
        for (var prefix = 0; prefix < 256; prefix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hex = prefix.ToString("x2", CultureInfo.InvariantCulture);
            var rows = new HashSet<string>(StringComparer.Ordinal);
            using (var connection = writer.OpenRead())
            using (var cmd = connection.CreateCommand())
            {
                // 小寫十六進位之後的字元都小於 g：以主鍵的範圍掃這個前綴
                cmd.CommandText = "SELECT sha256 FROM blobs WHERE sha256 >= $lo AND sha256 < $hi";
                cmd.Parameters.AddWithValue("$lo", hex);
                cmd.Parameters.AddWithValue("$hi", hex + "g");
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    rows.Add(reader.GetString(0));
            }

            var dir = Path.Combine(paths.BlobsDir, hex);
            var files = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    if (BlobIdRules.IsBlobId(name) && name.StartsWith(hex, StringComparison.Ordinal))
                        files.Add(name);
                }
            }

            var missing = rows.Where(sha => !files.Contains(sha)).ToList();
            if (missing.Count > 0)
            {
                foreach (var chunk in missing.Chunk(Batch))
                    rowsWithoutFile += (await blobs.RemoveRowsWithoutFileAsync(chunk, cancellationToken)).Count;
            }

            foreach (var sha in files.Where(sha => !rows.Contains(sha)))
            {
                var now = time.GetUtcNow();
                if (await blobs.DeleteOrphanFileAsync(sha, written => MediaMaintenanceRules.OldEnough(written, now), cancellationToken))
                    orphans++;
            }
        }

        return (rowsWithoutFile, orphans);
    }
}
