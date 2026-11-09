using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

/// <summary>
/// <c>media_refs_local</c> 的一列（WP4.7 計畫 D3）：這台發的媒體參照對到哪裡。對話 id 是線上格式。
/// 項目的列恰有一個：附件只有 <see cref="AttachmentId"/>、代理媒體只有 <see cref="MediaPath"/>（＝項目 MediaPaths 的那一項）；
/// 抽屜（<c>@list</c>）與 fileFetch（<c>@file</c>）的列 <see cref="MediaPath"/> 是完整路徑、沒有 <see cref="AttachmentId"/>
/// </summary>
/// <param name="CreatedAt">發參照的時間（Unix ms，本機時鐘；只給本機的修剪用）</param>
internal sealed record LocalMediaRefRow(string Ref, string ConversationId, string ItemId, string? AttachmentId, string? MediaPath, long CreatedAt)
{
    /// <summary>抽屜或 fileFetch 的列（不是項目）</summary>
    public bool IsLooseFile => ItemId is MediaServerRules.ListItemId or MediaServerRules.FileItemId or MediaServerRules.TransferItemId;

    /// <summary>電腦狀態面板的檔案傳輸（deviceFetch）的列：沒有對話（conversation_id 是空字串）</summary>
    public bool IsTransfer => ItemId == MediaServerRules.TransferItemId;

    /// <summary>形狀合不合格：參照、對話、項目都有；項目的列恰有一個來源，抽屜與 fileFetch 的列只有完整路徑</summary>
    public bool IsWellFormed =>
        !string.IsNullOrEmpty(Ref) && (IsTransfer ? ConversationId == string.Empty : !string.IsNullOrEmpty(ConversationId)) && !string.IsNullOrEmpty(ItemId)
        && (IsLooseFile
            ? AttachmentId is null && !string.IsNullOrEmpty(MediaPath)
            : (AttachmentId is null) != (MediaPath is null) && (AttachmentId ?? MediaPath)!.Length > 0);
}

// v2 的 media_refs_local（WP4.7）：只在 ConversationTablesReady 時呼叫，引擎執行緒使用、不加鎖。
// 步 8 查詢（回覆轉送）與新增；步 9 依項目讀、依項目或對話刪（配發與刪除在投影器裡）；修剪在步 11。
// 步 10 另讀 published_items 的截斷記號、清來源摘要（項目欄位 blob 的重送）
internal sealed partial class SyncStore
{
    /// <summary>這個對話（<paramref name="itemId"/> 不是 null 時只要這個項目）的列，依建立時間與參照排序</summary>
    public IReadOnlyList<LocalMediaRefRow> LoadMediaRefs(string conversationId, string? itemId = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(conversationId);
        using var cmd = Command(null, itemId is null
            ? """
              SELECT ref, conversation_id, item_id, attachment_id, media_path, created_at
              FROM media_refs_local WHERE conversation_id = $conversation ORDER BY created_at, ref
              """
            : """
              SELECT ref, conversation_id, item_id, attachment_id, media_path, created_at
              FROM media_refs_local WHERE conversation_id = $conversation AND item_id = $item ORDER BY created_at, ref
              """);
        Bind(cmd, ("$conversation", conversationId));
        if (itemId is not null)
            Bind(cmd, ("$item", itemId));
        using var reader = cmd.ExecuteReader();
        var rows = new List<LocalMediaRefRow>();
        while (reader.Read())
        {
            rows.Add(new LocalMediaRefRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    /// <summary>刪這個對話（<paramref name="itemId"/> 不是 null 時只刪這個項目）的列；回刪掉的列數</summary>
    public int RemoveMediaRefs(string conversationId, string? itemId = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(conversationId);
        using var cmd = Command(null, itemId is null
            ? "DELETE FROM media_refs_local WHERE conversation_id = $conversation"
            : "DELETE FROM media_refs_local WHERE conversation_id = $conversation AND item_id = $item");
        Bind(cmd, ("$conversation", conversationId));
        if (itemId is not null)
            Bind(cmd, ("$item", itemId));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>這個參照的列（沒有回 null）</summary>
    public LocalMediaRefRow? FindMediaRef(string mediaRef)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mediaRef);
        using var cmd = Command(null, """
            SELECT ref, conversation_id, item_id, attachment_id, media_path, created_at
            FROM media_refs_local WHERE ref = $ref
            """);
        Bind(cmd, ("$ref", mediaRef));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new LocalMediaRefRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4),
            reader.GetInt64(5));
    }

    /// <summary>新增（一個交易；同一個參照已經有就不動）。形狀不合格丟 <see cref="ArgumentException"/>，一筆都不寫</summary>
    public void AddMediaRefs(IReadOnlyList<LocalMediaRefRow> rows)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Any(r => r is null || !r.IsWellFormed))
            throw new ArgumentException("媒體參照的列不合格（項目的列要恰有附件或媒體路徑其中一個）", nameof(rows));
        if (rows.Count == 0)
            return;
        using var tx = connection.BeginTransaction();
        foreach (var row in rows)
            Insert(tx, row);
        tx.Commit();
    }

    /// <summary>
    /// 這個對話建立不早於 <paramref name="createdSince"/> 的抽屜列（<c>@list</c>；步 11，D33 的 29 天內重用），依建立時間與參照排序
    /// </summary>
    public IReadOnlyList<LocalMediaRefRow> LoadListRefs(string conversationId, long createdSince)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(conversationId);
        using var cmd = Command(null, """
            SELECT ref, conversation_id, item_id, attachment_id, media_path, created_at
            FROM media_refs_local WHERE conversation_id = $conversation AND item_id = $list AND created_at >= $since ORDER BY created_at, ref
            """);
        Bind(cmd, ("$conversation", conversationId), ("$list", MediaServerRules.ListItemId), ("$since", createdSince));
        using var reader = cmd.ExecuteReader();
        var rows = new List<LocalMediaRefRow>();
        while (reader.Read())
        {
            rows.Add(new LocalMediaRefRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    /// <summary>
    /// 每日修剪（步 11，D33）：抽屜（<c>@list</c>）建立早於 <paramref name="listBefore"/>、fileFetch（<c>@file</c>）建立早於 <paramref name="fileBefore"/> 的列刪掉；
    /// 項目的列不動。回刪掉的列數
    /// </summary>
    public int PruneMediaRefs(long listBefore, long fileBefore, long? xferBefore = null)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            DELETE FROM media_refs_local
            WHERE (item_id = $list AND created_at < $listBefore) OR (item_id = $file AND created_at < $fileBefore)
               OR (item_id = $xfer AND created_at < $xferBefore)
            """);
        Bind(cmd, ("$list", MediaServerRules.ListItemId), ("$listBefore", listBefore), ("$file", MediaServerRules.FileItemId), ("$fileBefore", fileBefore),
            ("$xfer", MediaServerRules.TransferItemId), ("$xferBefore", xferBefore ?? fileBefore));
        return cmd.ExecuteNonQuery();
    }

    // ── 項目欄位 blob（步 10）：published_items 的截斷記號與來源摘要 ──

    /// <summary>送出的是截斷版（<c>published_items.truncated</c>）、沒有移除的項目，依對話與 Ord 排序</summary>
    public IReadOnlyList<(string ConversationId, string ItemId)> LoadTruncatedItems()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            SELECT conversation_id, item_id FROM published_items
            WHERE truncated != 0 AND removed = 0 ORDER BY conversation_id, ord, item_id
            """);
        using var reader = cmd.ExecuteReader();
        var items = new List<(string, string)>();
        while (reader.Read())
            items.Add((reader.GetString(0), reader.GetString(1)));
        return items;
    }

    /// <summary>
    /// 清掉這些項目的**來源**摘要（線上摘要保留：重新投影之後內容沒變就不重送），連同它們的對話的 items.json SHA-256（否則「檔案沒變」的捷徑會跳過），
    /// 一個交易。列、Ord、截斷記號都保留。回清掉的項目數
    /// </summary>
    public int ForgetSourceDigests(IReadOnlyList<(string ConversationId, string ItemId)> items)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return 0;
        using var tx = connection.BeginTransaction();
        var forgotten = 0;
        foreach (var (conversationId, itemId) in items)
        {
            using var cmd = Command(tx, "UPDATE published_items SET src_sha256 = NULL WHERE conversation_id = $id AND item_id = $item");
            Bind(cmd, ("$id", conversationId), ("$item", itemId));
            forgotten += cmd.ExecuteNonQuery();
        }

        foreach (var conversationId in items.Select(i => i.ConversationId).Distinct(StringComparer.Ordinal))
        {
            using var cmd = Command(tx, "UPDATE publish_meta SET items_file_sha256 = NULL WHERE conversation_id = $id");
            Bind(cmd, ("$id", conversationId));
            cmd.ExecuteNonQuery();
        }

        Fault("ForgetSourceDigests:commit");
        tx.Commit();
        return forgotten;
    }

    private void Insert(SqliteTransaction tx, LocalMediaRefRow row)
    {
        using var cmd = Command(tx, """
            INSERT INTO media_refs_local (ref, conversation_id, item_id, attachment_id, media_path, created_at)
            VALUES ($ref, $conversation, $item, $attachment, $path, $at)
            ON CONFLICT (ref) DO NOTHING
            """);
        Bind(cmd, ("$ref", row.Ref), ("$conversation", row.ConversationId), ("$item", row.ItemId), ("$attachment", row.AttachmentId),
            ("$path", row.MediaPath), ("$at", row.CreatedAt));
        cmd.ExecuteNonQuery();
    }
}
