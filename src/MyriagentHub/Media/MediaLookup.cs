using Myriagent.Media;
using MyriagentHub.Data;
using MyriagentHub.Sync;

namespace MyriagentHub.Media;

/// <summary>媒體請求查到的參照（<c>media_refs</c> 的一列，已通過 <see cref="MediaLookupRules"/>）</summary>
/// <param name="Key">網址裡的鍵 <c>{deviceId}-{ref}</c>（<see cref="MediaKeyRules"/>）</param>
/// <param name="ThumbBlob">已上傳的 320 px 縮圖 blob（SHA-256）；沒有是 null</param>
internal sealed record MediaTarget(string Key, string DeviceId, string Ref, string ConversationId, string Purpose, string? ThumbBlob, long CreatedAt);

/// <summary>
/// 每個媒體請求都查一次（WP4.7 計畫 D7）：短讀 <c>media_refs</c>（唯讀連線、主鍵）＋對話目錄（記憶體），照 <see cref="MediaLookupRules"/>
/// 判斷用途、到期與可見性，不符一律 null（端點回同一種 404）。<c>last_used_at</c> 不更新（讀取不寫，D41）。
/// 查不到列時，索引（<see cref="MediaRefIndexer"/>）還沒追上當下最新的事件就等它（最多 <see cref="IndexerWait"/>）再查一次：
/// 電腦剛送出的項目、網頁馬上要看的媒體不會先 404（R2）
/// </summary>
internal sealed class MediaLookup(SyncWriter writer, IConversationDirectory directory, MediaRefIndexer indexer, TimeProvider time)
{
    /// <summary>查不到時等索引追上的上限</summary>
    public static readonly TimeSpan IndexerWait = TimeSpan.FromSeconds(2);

    public async Task<MediaTarget?> FindAsync(MediaEndpointKind endpoint, string key, CancellationToken cancellationToken)
        => (await LookupAsync(endpoint, key, cancellationToken)).Target;

    /// <summary>
    /// 同 <see cref="FindAsync"/>，另外帶回查不到的原因（<see cref="MediaLookupMisses"/>；給伺服器記錄，回應照樣不分原因）。
    /// 查不到時 <c>Target</c> 是 null、<c>Miss</c> 有值；查到的列不能看時 <c>Row</c> 是那一列（記錄對話 id 用）
    /// </summary>
    public async Task<(MediaTarget? Target, MediaTarget? Row, string? Miss)> LookupAsync(MediaEndpointKind endpoint, string key, CancellationToken cancellationToken)
    {
        if (!MediaKeyRules.TryParse(key, out _, out _))
            return (null, null, MediaLookupMisses.BadKey);
        if (await ReadAsync(key, cancellationToken) is not { } target)
            return (null, null, MediaLookupMisses.NoRow);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var miss = MediaLookupRules.Miss(endpoint, target.Purpose, target.CreatedAt, now, directory.Find(target.ConversationId), target.DeviceId);
        return miss is null ? (target, target, null) : (null, target, miss);
    }

    /// <summary>
    /// 一列參照（鍵不合格回 null；查不到時等索引追上再查一次，給 E8 這類不經 <see cref="FindAsync"/> 規則的端點用）
    /// </summary>
    public async Task<MediaTarget?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!MediaKeyRules.TryParse(key, out var deviceId, out var mediaRef))
            return null;
        if (Read(key, deviceId, mediaRef) is { } found)
            return found;
        return await indexer.CatchUpAsync(IndexerWait, cancellationToken) ? Read(key, deviceId, mediaRef) : null;
    }

    private MediaTarget? Read(string key, string deviceId, string mediaRef)
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT conversation_id, purpose, thumb_blob, created_at FROM media_refs WHERE device_id = $d AND ref = $r";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$r", mediaRef);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var thumb = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new MediaTarget(key, deviceId, mediaRef, reader.GetString(0), reader.GetString(1), BlobIdRules.IsBlobId(thumb) ? thumb : null, reader.GetInt64(3));
    }
}
