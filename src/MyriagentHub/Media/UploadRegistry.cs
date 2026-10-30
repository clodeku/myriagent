using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary>
/// 瀏覽器上傳的登記（WP4.7 計畫 D21）：以上傳 id 在記憶體查（一台伺服器、單一實例），完成時寫
/// <c>blob_refs(sha256, 'upload', '{uploadId}@{unixMs}')</c>，啟動時從那裡載回（只載還沒過期的）。過期的列由每日維護刪（步 5）。
/// 同一個 id 同時只能有一個上傳在進行；正在上傳的以宣告的大小（不知道時以單檔上限）預留，待處理合計把它算進去。
/// 實作接縫檔（<c>MediaSeams.cs</c>）的 <see cref="IUploadRegistry"/>：WP4.5 的指令路由在簽 <c>addUploaded</c>、<c>conversationCreate</c> 之前呼叫
/// </summary>
internal sealed class UploadRegistry(SyncWriter writer, IOptions<HubOptions> options, TimeProvider time, ILogger<UploadRegistry> log) : IUploadRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, UploadEntry> entries = new(StringComparer.Ordinal);

    /// <summary>待處理的位元組（正在上傳的預留＋完成而還沒過期的）</summary>
    public long PendingBytes
    {
        get
        {
            lock (gate)
                return UploadRules.PendingBytes(entries.Values, NowMs());
        }
    }

    /// <summary>啟動步驟（sync.db 開好之後）：從 <c>blob_refs</c> 載回還沒過期的上傳；同一個 id 有好幾列時取最新的</summary>
    internal void Initialize()
    {
        var now = NowMs();
        var loaded = 0;
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT r.ref_id, r.sha256, b.size FROM blob_refs r JOIN blobs b ON b.sha256 = r.sha256 WHERE r.ref_kind = $kind";
        cmd.Parameters.AddWithValue("$kind", BlobRefKinds.Upload);
        using var reader = cmd.ExecuteReader();
        lock (gate)
        {
            entries.Clear();
            while (reader.Read())
            {
                if (!UploadRules.TryParseRefId(reader.GetString(0), out var id, out var at) || UploadRules.IsExpired(at, now))
                    continue;
                if (entries.TryGetValue(id, out var known) && known.AtMs >= at)
                    continue;
                entries[id] = new UploadEntry(Completed: true, reader.GetString(1), reader.GetInt64(2), at);
                loaded++;
            }
        }

        if (loaded > 0)
            log.LogInformation("載回待處理的上傳 {Count} 筆", loaded);
    }

    /// <summary>
    /// PUT 開始時（讀本文之前）的判斷（<see cref="UploadRules.Decide"/>）。New 時這個 id 改成「正在上傳」並預留位元組：
    /// 呼叫端之後一定要 <see cref="CompleteAsync"/> 或 <see cref="Abort"/>
    /// </summary>
    public UploadPutDecision Begin(string uploadId, long? declaredLength)
    {
        var o = options.Value.Media;
        var now = NowMs();
        lock (gate)
        {
            Prune(now);
            UploadEntry? existing = entries.TryGetValue(uploadId, out var entry) ? entry : null;
            var decision = UploadRules.Decide(existing, declaredLength, o.MaxUploadBytes, UploadRules.PendingBytes(entries.Values, now), o.MaxPendingUploadBytes, now);
            if (decision.Kind == UploadPutKind.New)
                entries[uploadId] = new UploadEntry(Completed: false, null, declaredLength ?? o.MaxUploadBytes, now);
            return decision;
        }
    }

    /// <summary>New 的上傳失敗：拿掉「正在上傳」與預留（已完成的登記不動）</summary>
    public void Abort(string uploadId)
    {
        lock (gate)
        {
            if (entries.TryGetValue(uploadId, out var entry) && !entry.Completed)
                entries.Remove(uploadId);
        }
    }

    /// <summary>New 的上傳存好 blob 之後：寫 <c>blob_refs</c>（提交之後）再登記成完成；寫入失敗丟例外（呼叫端 <see cref="Abort"/>）</summary>
    public async Task CompleteAsync(string uploadId, string sha256, long size, CancellationToken cancellationToken)
    {
        var at = NowMs();
        var refId = UploadRules.RefId(uploadId, at);
        await writer.WriteAsync(c => c.Execute(
            "INSERT OR IGNORE INTO blob_refs(sha256, ref_kind, ref_id) VALUES($sha, $kind, $ref)",
            ("$sha", sha256), ("$kind", BlobRefKinds.Upload), ("$ref", refId)), cancellationToken);
        lock (gate)
            entries[uploadId] = new UploadEntry(Completed: true, sha256, size, at);
    }

    /// <summary>
    /// 每一筆都已上傳完成、還沒過期（24 小時）、雜湊與大小都相符才回 true；空的清單回 true（沒有要驗的上傳）。
    /// 任何執行緒都可以呼叫
    /// </summary>
    public bool TryVerify(IReadOnlyList<UploadClaim> uploads)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        var now = NowMs();
        lock (gate)
        {
            foreach (var claim in uploads)
            {
                if (claim is null
                    || !entries.TryGetValue(claim.UploadId, out var entry)
                    || !entry.Completed
                    || UploadRules.IsExpired(entry.AtMs, now)
                    || entry.Size != claim.Size
                    || !string.Equals(entry.Sha256, claim.Sha256, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    /// <summary>拿掉過期的完成登記（記憶體；資料庫的列由每日維護刪）</summary>
    private void Prune(long now)
    {
        List<string>? expired = null;
        foreach (var (id, entry) in entries)
        {
            if (entry.Completed && UploadRules.IsExpired(entry.AtMs, now))
                (expired ??= []).Add(id);
        }

        if (expired is null)
            return;
        foreach (var id in expired)
            entries.Remove(id);
    }

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();
}
