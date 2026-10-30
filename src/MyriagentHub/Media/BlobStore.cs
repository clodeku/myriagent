using System.Buffers;
using System.Security.Cryptography;
using Myriagent.Media;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary><see cref="BlobStore.PutStreamingAsync"/> 的結果</summary>
internal enum BlobPutOutcome
{
    /// <summary>新存入（這次寫了檔）</summary>
    Stored,

    /// <summary>同一份內容已經存在（沒有重寫）</summary>
    Exists,

    /// <summary>雜湊或長度和宣告的不符</summary>
    Mismatch,

    /// <summary>超過上限（讀到上限＋1 個位元組就停）</summary>
    TooLarge,

    /// <summary>配額到拒收門檻（507）</summary>
    QuotaExceeded,

    /// <summary>磁碟可用空間低於下限或量不到（507）</summary>
    DiskLow,

    /// <summary>檢查（DLP 等）不通過；原因在 <see cref="BlobPutResult.Error"/></summary>
    Rejected,
}

/// <param name="Sha256">內容的 SHA-256（算出來的；被拒、不符時可能是 null 或宣告的值）</param>
/// <param name="Length">內容的位元組數（被拒時是已讀的位元組數）</param>
/// <param name="Error">Rejected 時檢查給的拒絕代碼</param>
internal sealed record BlobPutResult(BlobPutOutcome Outcome, string? Sha256, long Length, string? Error = null);

/// <summary>
/// <c>blobs.kind</c>：第一次存入時的用途（同一份內容之後以別的用途再存一次不改）。哪些 blob 還在用以 <c>blob_refs</c> 為準（<see cref="BlobRefKinds"/>）
/// </summary>
internal static class BlobKinds
{
    /// <summary>項目欄位（裝置經 <c>PUT /api/v1/device/blobs/{sha256}</c>，≤4 MB、過第二道 DLP）</summary>
    public const string ItemBody = "item-body";

    /// <summary>320 px 縮圖</summary>
    public const string Thumb = "thumb";

    /// <summary>瀏覽器上傳的檔（<c>PUT /api/v1/uploads/{uploadId}</c>，≤512 MB）</summary>
    public const string Upload = "upload";
}

/// <summary><c>blob_refs.ref_kind</c>：誰引用這個 blob</summary>
internal static class BlobRefKinds
{
    /// <summary>瀏覽器上傳：<c>ref_id</c> 是 <c>{uploadId}@{unixMs}</c>（<see cref="UploadRules.RefId"/>），24 小時過期</summary>
    public const string Upload = "upload";

    /// <summary>項目欄位：<c>ref_id</c> 是 <c>{conversationId}:{itemId}</c>（<see cref="MediaRefIndexRules.ItemBlobRefId"/>），只由索引器寫</summary>
    public const string Item = "item";

    /// <summary>縮圖：<c>ref_id</c> 是媒體鍵 <c>{deviceId}-{ref}</c>（E8 寫，參照被刪時索引器移除）</summary>
    public const string Thumb = "thumb";
}

/// <summary><c>blobs</c> 的一列（每日維護判斷能不能刪）</summary>
/// <param name="LastRefAt">最後一次有引用被移除的時間（沒移除過＝<paramref name="CreatedAt"/>；讀取不更新，D41）</param>
internal sealed record BlobRow(string Sha256, long Size, string Kind, long CreatedAt, long LastRefAt);

/// <summary>blob 存不下（配額到拒收門檻、磁碟可用空間不足）：<see cref="IBlobStore.PutAsync"/> 的凍結語意沒有這兩種結果，所以丟例外</summary>
internal sealed class BlobStoreFullException(BlobPutOutcome reason)
    : IOException(reason == BlobPutOutcome.DiskLow ? "磁碟可用空間低於 blob 的下限" : "blob 配額已到拒收門檻")
{
    public BlobPutOutcome Reason { get; } = reason;
}

/// <summary>磁碟可用空間（測試換成固定值）</summary>
internal interface IDiskSpaceProbe
{
    /// <summary><paramref name="path"/> 所在磁碟區給這個帳戶用的可用位元組；量不到回 -1</summary>
    long FreeBytes(string path);
}

internal sealed class DriveSpaceProbe : IDiskSpaceProbe
{
    public long FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}

/// <summary>
/// 伺服器的內容定址 blob 儲存（設計 3.4、WP4.7 計畫 D14、D16、D17、D20）：檔案在資料根（C:）的 <c>blobs\{前 2 碼}\{sha}</c>，
/// 中繼資料在 sync.db 的 <c>blobs</c> 表。寫入順序：<c>tmp\blob-{guid}</c>（WriteThrough，邊寫邊算 SHA-256）→ 驗雜湊與長度 →
/// 檢查（DLP 等）→ <c>File.Move(overwrite:false)</c> → 經 <see cref="SyncWriter"/> 寫 <c>blobs</c> 列（INSERT OR IGNORE）。
/// <see cref="Exists"/> 以列為準：當機留下的「有檔沒列」不算存在（同一份內容再存一次時沿用那個檔並補上列，沒人再存就由每日維護對帳清掉）。
/// 改名與寫列一次只有一個（程序內的鎖；服務鎖保證只有一個實例），同一份內容同時存兩次時一個 Stored、一個 Exists。
/// 存入前查配額與磁碟下限（含進行中的寫入預留的位元組），到拒收門檻或可用空間不足就不收新的 blob（事件與對話照收）。
/// 不加任何 ACL、不碰 D:
/// </summary>
internal sealed class BlobStore(HubPaths paths, SyncWriter writer, IOptions<HubOptions> options, IDiskSpaceProbe disk, ILogger<BlobStore> log) : IBlobStore
{
    /// <summary>暫存檔的前綴（在 <see cref="HubPaths.TmpDir"/>，和 blobs 同一個磁碟區才能原子改名）</summary>
    public const string TmpPrefix = "blob-";

    /// <summary>一次寫入的大小：WriteThrough 每次寫入都直接落盤，先填滿一塊再寫</summary>
    private const int ChunkBytes = 256 * 1024;

    private const int MaxContentTypeLength = 255;

    private readonly Lock gate = new();
    private readonly SemaphoreSlim commit = new(1, 1);
    private long used;
    private long count;
    private long reserved;

    /// <summary>已存的 blob 合計位元組（啟動時以 <c>SUM(size)</c> 載入，之後隨新增與刪除增減）</summary>
    public long UsedBytes => Interlocked.Read(ref used);

    /// <summary>已存的 blob 數（同 <see cref="UsedBytes"/>，ops/status 不查資料庫）</summary>
    public long Count => Interlocked.Read(ref count);

    /// <summary>進行中的寫入預留的位元組（配額與磁碟下限的判斷會算進去）</summary>
    public long ReservedBytes
    {
        get
        {
            lock (gate)
                return reserved;
        }
    }

    /// <summary>目前的配額等級（ops/status 用）</summary>
    public BlobQuotaLevel Level()
    {
        var o = options.Value.Media;
        return BlobQuotaRules.Level(UsedBytes, o.QuotaBytes, o.AlertPercent, o.RefusePercent, disk.FreeBytes(paths.BlobsDir), o.MinFreeBytes);
    }

    /// <summary>
    /// 啟動步驟（sync.db 開好之後）：載入用量、清掉上次留在 tmp 的 <c>blob-*</c>（服務鎖保證沒有別的實例在寫）
    /// </summary>
    internal void Initialize()
    {
        using (var connection = writer.OpenRead())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT IFNULL(SUM(size), 0), COUNT(*) FROM blobs";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            Interlocked.Exchange(ref used, reader.GetInt64(0));
            Interlocked.Exchange(ref count, reader.GetInt64(1));
        }

        var leftovers = 0;
        foreach (var file in Directory.EnumerateFiles(paths.TmpDir, TmpPrefix + "*"))
        {
            if (TryDelete(file))
                leftovers++;
        }

        if (leftovers > 0)
            log.LogWarning("清掉上次留下的 blob 暫存檔 {Count} 個", leftovers);
    }

    public bool Exists(string sha256) => SizeOf(sha256) is not null;

    /// <summary><c>blobs</c> 列記的大小（以列為準，同 <see cref="Exists"/>）；沒有列或不是 blob id 回 null</summary>
    public long? SizeOf(string sha256) => BlobIdRules.IsBlobId(sha256) ? RowSize(sha256) : null;

    /// <summary>
    /// 列與檔都在時的大小（E5 的 HEAD 用，和 <see cref="PutStreamingAsync"/> 的「已存在」同一個條件）；有列沒檔（還原到沒有引用它的備份之後、
    /// 檔被隔離或手動刪掉）回 null，裝置就會 PUT、把檔補回來（合併前審查 DI-2）
    /// </summary>
    public long? SizeOnDisk(string sha256) => SizeOf(sha256) is { } size && File.Exists(PathOf(sha256)) ? size : null;

    /// <summary>開啟讀取：內容寫入前已驗過雜湊、改名之後不再改變，直接開檔（可以和每日維護的刪除並存）</summary>
    public Stream? OpenRead(string sha256)
    {
        if (!BlobIdRules.IsBlobId(sha256))
            return null;
        try
        {
            return new FileStream(PathOf(sha256), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="IBlobStore"/> 的凍結語意：雜湊或長度不符丟 <see cref="InvalidDataException"/>（什麼都不留）、已存在回 false（不重寫）。
    /// 配額或磁碟下限不收時丟 <see cref="BlobStoreFullException"/>
    /// </summary>
    public async Task<bool> PutAsync(string sha256, long length, string kind, string? contentType, Stream content, CancellationToken cancellationToken)
    {
        if (!BlobIdRules.IsBlobId(sha256))
            throw new ArgumentException("不是 blob id（64 個小寫十六進位）", nameof(sha256));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var result = await PutStreamingAsync(sha256, length, length, kind, contentType, content, inspect: null, cancellationToken);
        return result.Outcome switch
        {
            BlobPutOutcome.Stored => true,
            BlobPutOutcome.Exists => false,
            BlobPutOutcome.Mismatch or BlobPutOutcome.TooLarge => throw new InvalidDataException($"blob 的內容和宣告的雜湊或長度（{length}）不符"),
            BlobPutOutcome.QuotaExceeded or BlobPutOutcome.DiskLow => throw new BlobStoreFullException(result.Outcome),
            _ => throw new InvalidOperationException($"不預期的結果：{result.Outcome}"),
        };
    }

    /// <summary>
    /// 邊收邊算雜湊地存入（上傳端點、裝置 blob 端點用）。<paramref name="expectedSha256"/>、<paramref name="expectedLength"/> 事先不知道時傳 null；
    /// 知道雜湊而且已經存在時不讀內容、直接回 Exists。讀到 <paramref name="maxLength"/>（或宣告的長度）＋1 個位元組就停。
    /// <paramref name="inspect"/> 在改名之前以暫存檔的路徑呼叫：回 null＝通過，否則是拒絕代碼（Rejected）。
    /// 失敗、被拒、取消時暫存檔一律刪掉
    /// </summary>
    public async Task<BlobPutResult> PutStreamingAsync(string? expectedSha256, long? expectedLength, long maxLength, string kind,
        string? contentType, Stream content, Func<string, CancellationToken, Task<string?>>? inspect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (expectedSha256 is not null && !BlobIdRules.IsBlobId(expectedSha256))
            throw new ArgumentException("不是 blob id（64 個小寫十六進位）", nameof(expectedSha256));
        if (expectedLength is < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (string.IsNullOrEmpty(kind) || kind.Length > 32 || kind.AsSpan().IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz-") >= 0)
            throw new ArgumentException("blob 的種類只能是 1～32 個小寫英文字母與 -", nameof(kind));

        if (expectedLength > maxLength)
            return new BlobPutResult(BlobPutOutcome.TooLarge, expectedSha256, expectedLength.Value);
        if (expectedSha256 is not null && RowSize(expectedSha256) is { } existing && File.Exists(PathOf(expectedSha256)))
            return new BlobPutResult(BlobPutOutcome.Exists, expectedSha256, existing);

        var limit = expectedLength ?? maxLength;
        if (Reserve(limit) is { } refused)
            return new BlobPutResult(refused, expectedSha256, 0);

        var tmp = Path.Combine(paths.TmpDir, TmpPrefix + Guid.NewGuid().ToString("N"));
        var moved = false;
        try
        {
            var (sha, total, overLimit) = await CopyAsync(content, tmp, limit, ct);
            if (overLimit)
                return new BlobPutResult(expectedLength is null ? BlobPutOutcome.TooLarge : BlobPutOutcome.Mismatch, expectedSha256, total);
            if ((expectedLength is { } declared && total != declared) || (expectedSha256 is not null && sha != expectedSha256))
                return new BlobPutResult(BlobPutOutcome.Mismatch, sha, total);
            if (inspect is not null && await inspect(tmp, ct) is { } error)
                return new BlobPutResult(BlobPutOutcome.Rejected, sha, total, error);

            // 改名與寫列一次只有一個（同一份內容同時存兩次時一個 Stored、一個 Exists）；已經改名的檔不因為取消而留成孤兒，這段不理會取消
            await commit.WaitAsync(CancellationToken.None);
            try
            {
                var target = PathOf(sha);
                if (RowSize(sha) is not null && File.Exists(target))
                    return new BlobPutResult(BlobPutOutcome.Exists, sha, total);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(tmp, target, overwrite: false);
                    moved = true;
                }

                // 有檔沒列（上次在改名與寫列之間當機）時沿用那個檔：檔名就是內容的雜湊，改名前已驗過
                var type = contentType is { Length: > 0 and <= MaxContentTypeLength } ? contentType : null;
                var inserted = await writer.WriteAsync(c => c.Execute(
                    "INSERT OR IGNORE INTO blobs(sha256, size, content_type, kind, created_at, last_ref_at) VALUES($sha, $size, $type, $kind, $now, $now)",
                    ("$sha", sha), ("$size", total), ("$type", type), ("$kind", kind), ("$now", c.Now)), CancellationToken.None);
                if (inserted == 1)
                {
                    Interlocked.Add(ref used, total);
                    Interlocked.Increment(ref count);
                }

                return new BlobPutResult(BlobPutOutcome.Stored, sha, total);
            }
            finally
            {
                commit.Release();
            }
        }
        finally
        {
            lock (gate)
                reserved -= limit;
            if (!moved)
                TryDelete(tmp);
        }
    }

    /// <summary>blob 檔的完整路徑（<c>blobs\{前 2 碼}\{sha}</c>）</summary>
    internal string PathOf(string sha256) => Path.Combine(paths.BlobsDir, BlobIdRules.RelativePath(sha256));

    /// <summary>
    /// 每日維護（D19）：刪掉沒有任何引用（<c>blob_refs</c> 沒有列）而且 <paramref name="deletable"/> 判斷可以刪的 blob。
    /// 在寫入者的交易裡重查一次再刪列，提交之後才刪檔（同 D20 以列為準：刪檔之前當機留下的有檔沒列由對帳清掉）；
    /// 和存入共用同一把鎖，刪除途中不會有同一份內容正在改名或寫列。<paramref name="deletable"/> 在寫入執行緒上呼叫，必須是快的純函式。
    /// 回傳刪掉的列
    /// </summary>
    internal async Task<IReadOnlyList<BlobRow>> DeleteUnreferencedAsync(IReadOnlyList<string> sha256s, Func<BlobRow, bool> deletable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sha256s);
        ArgumentNullException.ThrowIfNull(deletable);
        if (sha256s.Count == 0)
            return [];
        await commit.WaitAsync(cancellationToken);
        try
        {
            var deleted = await writer.WriteAsync(c =>
            {
                var rows = new List<BlobRow>();
                foreach (var sha in sha256s.Distinct(StringComparer.Ordinal))
                {
                    if (ReadUnreferenced(c, sha) is not { } row || !deletable(row))
                        continue;
                    c.Execute("DELETE FROM blobs WHERE sha256 = $s", ("$s", sha));
                    rows.Add(row);
                }

                return rows;
            }, CancellationToken.None);
            foreach (var row in deleted)
                Forget(row.Size, PathOf(row.Sha256));
            return deleted;
        }
        finally
        {
            commit.Release();
        }
    }

    /// <summary>
    /// 每日維護的對帳（D19）：<paramref name="sha256s"/> 裡檔案不見了的列刪掉（不管有沒有引用：列在、檔不在時 <see cref="Exists"/> 會說謊；
    /// E5 的 HEAD 以 <see cref="SizeOnDisk"/> 也看檔案，對帳之前裝置就會重傳）。回傳刪掉的 sha
    /// </summary>
    internal async Task<IReadOnlyList<string>> RemoveRowsWithoutFileAsync(IReadOnlyList<string> sha256s, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sha256s);
        await commit.WaitAsync(cancellationToken);
        try
        {
            // 持鎖期間不會有存入：檔案在不在是穩定的
            var missing = sha256s.Where(sha => BlobIdRules.IsBlobId(sha) && !File.Exists(PathOf(sha))).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count == 0)
                return [];
            var removed = await writer.WriteAsync(c =>
            {
                var rows = new List<(string Sha, long Size)>();
                foreach (var sha in missing)
                {
                    if (c.Scalar("SELECT size FROM blobs WHERE sha256 = $s", ("$s", sha)) is not long size)
                        continue;
                    c.Execute("DELETE FROM blobs WHERE sha256 = $s", ("$s", sha));
                    rows.Add((sha, size));
                }

                return rows;
            }, CancellationToken.None);
            foreach (var (sha, size) in removed)
            {
                Interlocked.Add(ref used, -size);
                Interlocked.Decrement(ref count);
                log.LogWarning("blob 有列沒檔（檔案不見了），刪掉那一列：{Sha}", sha);
            }

            return removed.Select(r => r.Sha).ToList();
        }
        finally
        {
            commit.Release();
        }
    }

    /// <summary>
    /// 每日維護的對帳（D19）：<c>blobs\</c> 裡沒有列的檔（改名之後、寫列之前當機留下的）在 <paramref name="oldEnough"/>（修改時間，UTC）成立時刪掉。
    /// 持鎖重查：正在存入的那一份（剛改名、還沒寫列）不會被刪。回傳有沒有刪
    /// </summary>
    internal async Task<bool> DeleteOrphanFileAsync(string sha256, Func<DateTime, bool> oldEnough, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(oldEnough);
        if (!BlobIdRules.IsBlobId(sha256))
            return false;
        await commit.WaitAsync(cancellationToken);
        try
        {
            var file = PathOf(sha256);
            if (RowSize(sha256) is not null || !File.Exists(file) || !oldEnough(File.GetLastWriteTimeUtc(file)))
                return false;
            if (!TryDelete(file))
                return false;
            log.LogWarning("刪掉 blobs 裡沒有列的檔：{Sha}", sha256);
            return true;
        }
        finally
        {
            commit.Release();
        }
    }

    /// <summary>沒有任何引用的列；有引用或沒有列回 null（寫入者的交易裡）</summary>
    private static BlobRow? ReadUnreferenced(SyncWriteContext c, string sha256)
    {
        using var cmd = c.Command(
            "SELECT size, kind, created_at, last_ref_at FROM blobs WHERE sha256 = $s AND NOT EXISTS (SELECT 1 FROM blob_refs WHERE sha256 = $s)", ("$s", sha256));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new BlobRow(sha256, reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)) : null;
    }

    /// <summary>列已刪：扣回用量與個數、刪檔（刪不掉的由對帳再試）</summary>
    private void Forget(long size, string file)
    {
        Interlocked.Add(ref used, -size);
        Interlocked.Decrement(ref count);
        TryDelete(file);
    }

    /// <summary>
    /// 把內容寫進暫存檔並算 SHA-256；最多讀 <paramref name="limit"/>＋1 個位元組（讀到超過上限就停，回 overLimit）
    /// </summary>
    private static async Task<(string Sha, long Total, bool OverLimit)> CopyAsync(Stream content, string tmp, long limit, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
            long total = 0;
            while (true)
            {
                var want = (int)Math.Min(ChunkBytes, limit + 1 - total);
                var filled = 0;
                while (filled < want)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(filled, want - filled), ct);
                    if (read == 0)
                        break;
                    filled += read;
                }

                if (filled == 0)
                    break;
                total += filled;
                if (total > limit)
                    return (string.Empty, total, true);
                hash.AppendData(buffer, 0, filled);
                await file.WriteAsync(buffer.AsMemory(0, filled), ct);
                if (filled < want)
                    break;
            }

            file.Flush(flushToDisk: true);
            return (Convert.ToHexStringLower(hash.GetHashAndReset()), total, false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>預留 <paramref name="incoming"/> 個位元組；配額或磁碟下限不收回原因</summary>
    private BlobPutOutcome? Reserve(long incoming)
    {
        var o = options.Value.Media;
        var free = disk.FreeBytes(paths.BlobsDir);
        lock (gate)
        {
            var refusal = BlobQuotaRules.Check(UsedBytes + reserved, incoming, o.QuotaBytes, o.RefusePercent, free < 0 ? free : free - reserved, o.MinFreeBytes);
            if (refusal is { } reason)
            {
                log.LogWarning("blob 不收（{Reason}）：用量 {Used}、預留 {Reserved}、可用 {Free}、要存 {Incoming}", reason, UsedBytes, reserved, free, incoming);
                return reason == BlobQuotaRefusal.DiskLow ? BlobPutOutcome.DiskLow : BlobPutOutcome.QuotaExceeded;
            }

            reserved += incoming;
            return null;
        }
    }

    private long? RowSize(string sha256)
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT size FROM blobs WHERE sha256 = $sha";
        cmd.Parameters.AddWithValue("$sha", sha256);
        return cmd.ExecuteScalar() is long size ? size : null;
    }

    private bool TryDelete(string file)
    {
        try
        {
            if (!File.Exists(file))
                return false;
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "blob 檔刪不掉：{File}", file);
            return false;
        }
    }
}
