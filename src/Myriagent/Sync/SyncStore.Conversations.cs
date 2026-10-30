using System.Globalization;
using Myriagent.Core;
using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

/// <summary>
/// <c>publish_meta</c> 的一列（WP4.3 計畫 D11、D14、D22、D25）：擁有的對話發布到哪裡。對話 id 一律是線上格式（小寫 32 個十六進位）
/// </summary>
/// <param name="CreatedWritten">這個對話的 <c>conv.created</c> 寫進 outbox 過（「已知」；不是伺服器已確認）</param>
/// <param name="CreatedAcked">伺服器確認過 <c>conv.created</c>（拒絕碼 invalid 的判斷，D14）</param>
/// <param name="Policy">發布時的同步政策（政策轉換的「之前」，D25）；null＝沒記過</param>
/// <param name="ItemsFileSha256">上次重整時 items.json 的 SHA-256（相同就不投影，步 12）；項目摘要清掉時一起清</param>
/// <param name="Blocked">封鎖的原因（拒絕碼或程式錯誤，D14）；null＝沒有封鎖</param>
/// <param name="DeletedAt">墓碑：送了 conv.deleted 的時間（伺服器換實例也保留，D22）</param>
/// <param name="CreatedRetries">invalid 之後補送 conv.created 的次數（D14）</param>
/// <param name="RetryAt">下一次補送 conv.created 的時間（Unix ms）</param>
/// <param name="EverCreated">曾經對任何一個伺服器實例寫過 conv.created（伺服器換實例不重設；合併前審查 F3）</param>
internal sealed record PublishMetaRow(
    string ConversationId,
    bool CreatedWritten = false,
    bool CreatedAcked = false,
    string? Policy = null,
    string? MetaSha256 = null,
    string? QueueSha256 = null,
    string? ItemsFileSha256 = null,
    string? Blocked = null,
    long? BlockedAt = null,
    long? DeletedAt = null,
    int CreatedRetries = 0,
    long? RetryAt = null,
    bool EverCreated = false);

/// <summary><c>published_items</c> 的一列：項目配到的 Ord（不重用）與摘要（null＝要重送）</summary>
/// <param name="SrcSha256">投影前的摘要（PublishDigestRules.Source）</param>
/// <param name="WireSha256">線上的摘要（PublishDigestRules.Wire）</param>
/// <param name="Truncated">送出的是截斷過的版本（WP4.7 協商出 media1 後重送）</param>
/// <param name="Removed">已送 conv.itemRemoved</param>
internal sealed record PublishedItemRow(string ItemId, long Ord, string? SrcSha256, string? WireSha256, bool Truncated, bool Removed);

/// <summary>
/// 與 outbox 的列同一個交易寫的發布狀態（<see cref="SyncStore.AppendMany"/> 的可選參數，WP4.3 計畫 D11）；寫入順序：
/// 先處理 <paramref name="ScrubPending"/> 的待送列（在寫這一批的列之前），再清 <paramref name="ForgetItems"/> 的項目摘要（政策降級，D25）、
/// <paramref name="ResetRemoved"/>，再寫 <paramref name="Items"/>、整列寫 <paramref name="Metas"/>、最後 <paramref name="NeedsRedo"/>
/// </summary>
/// <param name="Items">項目的發布紀錄（<c>Removed</c> 的只標已移除、清摘要，不新增列）</param>
/// <param name="Metas"><c>publish_meta</c> 的整列（呼叫端算好的新值）</param>
/// <param name="NeedsRedo">對話 → 要不要整份重新發布（<c>publish_state.needs_redo</c>）</param>
/// <param name="ForgetItems">要清掉所有項目摘要的對話（列與 Ord 保留）</param>
/// <param name="ScrubPending">
/// 政策降級（改成 metadata／paused）的對話：outbox 裡它還沒送出（Pending）的 item 類列（<c>conv.item</c>、<c>conv.itemRemoved</c>、<c>conv.queue</c>）
/// 的 payload 換成 <c>{}</c>，所有待送列的合併鍵清掉（之後的列一律接在最後，不會合併回降級之前的位置；合併前審查 P1、F1）
/// </param>
/// <param name="ScrubAllPending">
/// 改成 localOnly（含伺服器刪除的旁檔、中樞沒有這個對話）的對話：outbox 裡它還沒送出的列不論種類一律換成 <c>{}</c>（<c>conv.created</c>、<c>conv.meta</c>
/// 帶著標題與工作資料夾，也不能外送），合併鍵清掉（第二輪合併前審查 PRIV-2）。同一個對話兩份清單都有時以這一份為準
/// </param>
/// <param name="ResetRemoved">
/// 「已送 conv.itemRemoved」歸 0、摘要清掉的項目（在 <paramref name="ForgetItems"/> 之後、<paramref name="Items"/> 之前）：中樞沒有這個對話時待送的刪除換成 <c>{}</c>，
/// 伺服器上那則還在，對話回到中樞時重整才補送得到（第三輪合併前審查 PRIV-R3-1）
/// </param>
internal sealed record ConversationPublishWrite(
    IReadOnlyList<PublishRecord> Items,
    IReadOnlyList<PublishMetaRow> Metas,
    IReadOnlyDictionary<string, bool> NeedsRedo,
    IReadOnlyList<string> ForgetItems,
    IReadOnlyList<string>? ScrubPending = null,
    IReadOnlyList<string>? ScrubAllPending = null,
    IReadOnlyList<(string ConversationId, string ItemId)>? ResetRemoved = null)
{
    public bool IsEmpty => Items.Count == 0 && Metas.Count == 0 && NeedsRedo.Count == 0 && ForgetItems.Count == 0 && ScrubPending is not { Count: > 0 }
                           && ScrubAllPending is not { Count: > 0 } && ResetRemoved is not { Count: > 0 };
}

/// <summary><c>replica_meta</c> 的一列：其他電腦的對話（D26）</summary>
/// <param name="MetaJson">最後一筆 conv.created／conv.meta 的 payload（線上格式）</param>
/// <param name="Deleted">收過 conv.deleted</param>
/// <param name="Seq">套用到的伺服器 seq</param>
internal sealed record ReplicaMetaRow(string ConversationId, string OwnerDeviceId, long Epoch, string MetaJson, bool Deleted, long Seq);

/// <summary><c>replica_items</c> 的一列：複本的一個項目（線上的 conv.item payload，依伺服器 seq 套用）</summary>
internal sealed record ReplicaItemRow(string ConversationId, string ItemId, long Ord, string Payload, long Seq);

// v3 的對話表與 v2 的對話游標、發布與回補狀態（WP4.3 計畫步 8）：只在 ConversationTablesReady 時呼叫（表不在會丟 SqliteException），
// 引擎執行緒使用、不加鎖；每個方法一個交易（或單一陳述式）
internal sealed partial class SyncStore
{
    // ── publish_meta ──

    public IReadOnlyDictionary<string, PublishMetaRow> LoadPublishMeta()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            SELECT conversation_id, created_written, created_acked, policy, meta_sha256, queue_sha256, items_file_sha256,
                   blocked, blocked_at, deleted_at, created_retries, retry_at, ever_created
            FROM publish_meta
            """);
        using var reader = cmd.ExecuteReader();
        var rows = new Dictionary<string, PublishMetaRow>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var row = new PublishMetaRow(
                reader.GetString(0),
                reader.GetInt64(1) != 0,
                reader.GetInt64(2) != 0,
                NullableString(reader, 3),
                NullableString(reader, 4),
                NullableString(reader, 5),
                NullableString(reader, 6),
                NullableString(reader, 7),
                NullableLong(reader, 8),
                NullableLong(reader, 9),
                reader.GetInt32(10),
                NullableLong(reader, 11),
                reader.GetInt64(12) != 0);
            rows[row.ConversationId] = row;
        }

        return rows;
    }

    /// <summary>整列寫入（沒有就新增、有就每個欄位都換成這一列的值）</summary>
    public void UpsertPublishMeta(PublishMetaRow row)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(row);
        UpsertPublishMeta(null, row);
    }

    private void UpsertPublishMeta(SqliteTransaction? tx, PublishMetaRow row)
    {
        using var cmd = Command(tx, """
            INSERT INTO publish_meta (conversation_id, created_written, created_acked, policy, meta_sha256, queue_sha256, items_file_sha256,
                                      blocked, blocked_at, deleted_at, created_retries, retry_at, ever_created)
            VALUES ($id, $written, $acked, $policy, $meta, $queue, $items, $blocked, $blockedAt, $deletedAt, $retries, $retryAt, $ever)
            ON CONFLICT (conversation_id) DO UPDATE SET
              created_written = excluded.created_written, created_acked = excluded.created_acked, policy = excluded.policy,
              meta_sha256 = excluded.meta_sha256, queue_sha256 = excluded.queue_sha256, items_file_sha256 = excluded.items_file_sha256,
              blocked = excluded.blocked, blocked_at = excluded.blocked_at, deleted_at = excluded.deleted_at,
              created_retries = excluded.created_retries, retry_at = excluded.retry_at, ever_created = excluded.ever_created
            """);
        Bind(cmd, ("$id", row.ConversationId), ("$written", row.CreatedWritten ? 1 : 0), ("$acked", row.CreatedAcked ? 1 : 0), ("$policy", row.Policy),
            ("$meta", row.MetaSha256), ("$queue", row.QueueSha256), ("$items", row.ItemsFileSha256), ("$blocked", row.Blocked), ("$blockedAt", row.BlockedAt),
            ("$deletedAt", row.DeletedAt), ("$retries", row.CreatedRetries), ("$retryAt", row.RetryAt), ("$ever", row.EverCreated ? 1 : 0));
        cmd.ExecuteNonQuery();
    }

    /// <summary>封鎖（沒有列就新增；其他欄位不動）</summary>
    public void SetBlocked(string conversationId, string reason, long at)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(reason);
        using var cmd = Command(null, """
            INSERT INTO publish_meta (conversation_id, blocked, blocked_at) VALUES ($id, $reason, $at)
            ON CONFLICT (conversation_id) DO UPDATE SET blocked = excluded.blocked, blocked_at = excluded.blocked_at
            """);
        Bind(cmd, ("$id", conversationId), ("$reason", reason), ("$at", at));
        cmd.ExecuteNonQuery();
    }

    /// <summary>解除封鎖：<paramref name="reason"/> 是 null＝全部，否則只解除這個原因的（例如程式錯誤的封鎖在換版或換實例後解除）。回傳解除了幾個</summary>
    public int ClearBlocks(string? reason)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            UPDATE publish_meta SET blocked = NULL, blocked_at = NULL
            WHERE blocked IS NOT NULL AND ($reason IS NULL OR blocked = $reason)
            """);
        Bind(cmd, ("$reason", reason));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>記墓碑（conv.deleted 寫進 outbox 的時間；已經有就保留第一次的）</summary>
    public void SetDeleted(string conversationId, long at)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            INSERT INTO publish_meta (conversation_id, deleted_at) VALUES ($id, $at)
            ON CONFLICT (conversation_id) DO UPDATE SET deleted_at = COALESCE(publish_meta.deleted_at, excluded.deleted_at)
            """);
        Bind(cmd, ("$id", conversationId), ("$at", at));
        cmd.ExecuteNonQuery();
    }

    // ── published_items ──

    /// <summary>這個對話發布過的項目（依 Ord）</summary>
    public IReadOnlyList<PublishedItemRow> LoadPublishedItems(string conversationId)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            SELECT item_id, ord, src_sha256, wire_sha256, truncated, removed
            FROM published_items WHERE conversation_id = $id ORDER BY ord, item_id
            """);
        Bind(cmd, ("$id", conversationId));
        using var reader = cmd.ExecuteReader();
        var rows = new List<PublishedItemRow>();
        while (reader.Read())
        {
            rows.Add(new PublishedItemRow(reader.GetString(0), reader.GetInt64(1), NullableString(reader, 2), NullableString(reader, 3),
                reader.GetInt64(4) != 0, reader.GetInt64(5) != 0));
        }

        return rows;
    }

    /// <summary>
    /// 清掉項目的摘要（<paramref name="itemId"/> 是 null＝這個對話的全部），下一次重整會重送；列與 Ord 保留（被拒、政策降級，D14、D25）。
    /// items.json 的 SHA-256 一起清（否則「檔案沒變」的捷徑會跳過重送）。<paramref name="resetRemoved"/>＝被擋下的是 <c>conv.itemRemoved</c>：
    /// 「已送 itemRemoved」也清掉，下一次重整的第一頁才會重送這筆刪除（合併前審查 F4）
    /// </summary>
    public void ForgetDigests(string conversationId, string? itemId, bool resetRemoved = false)
    {
        ThrowIfDisposed();
        using var tx = connection.BeginTransaction();
        using (var items = Command(tx, """
                   UPDATE published_items SET src_sha256 = NULL, wire_sha256 = NULL, removed = CASE WHEN $reset != 0 THEN 0 ELSE removed END
                   WHERE conversation_id = $id AND ($item IS NULL OR item_id = $item)
                   """))
        {
            Bind(items, ("$id", conversationId), ("$item", itemId), ("$reset", resetRemoved ? 1 : 0));
            items.ExecuteNonQuery();
        }

        using (var meta = Command(tx, "UPDATE publish_meta SET items_file_sha256 = NULL WHERE conversation_id = $id"))
        {
            Bind(meta, ("$id", conversationId));
            meta.ExecuteNonQuery();
        }

        Fault("ForgetDigests:commit");
        tx.Commit();
    }

    /// <summary>記下已送 conv.itemRemoved（Ord 保留、摘要清掉）；沒有這個項目的列回 false</summary>
    public bool MarkRemoved(string conversationId, string itemId)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            UPDATE published_items SET removed = 1, src_sha256 = NULL, wire_sha256 = NULL
            WHERE conversation_id = $id AND item_id = $item
            """);
        Bind(cmd, ("$id", conversationId), ("$item", itemId));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// 這個對話發布過的 Ord（<see cref="OrdAssignRules.PlanLoad"/> 的 hint，D9、D10）：項目 id → Ord，最大值含已移除的（Ord 不重用）。
    /// 沒有任何列回 null
    /// </summary>
    public OrdHint? OrdHintOf(string conversationId)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT item_id, ord FROM published_items WHERE conversation_id = $id");
        Bind(cmd, ("$id", conversationId));
        using var reader = cmd.ExecuteReader();
        var ords = new Dictionary<string, long>(StringComparer.Ordinal);
        long max = 0;
        while (reader.Read())
        {
            var ord = reader.GetInt64(1);
            ords[reader.GetString(0)] = ord;
            max = Math.Max(max, ord);
        }

        return ords.Count == 0 ? null : new OrdHint(ords, max);
    }

    /// <summary>
    /// 沒有 outbox 的列時單獨寫發布狀態（一個交易；有列時一律經 <see cref="AppendMany"/> 同一個交易寫）。回傳 <see cref="ConversationPublishWrite.ScrubPending"/>
    /// 改過的待送列（提交之後；依 deviceSeq）
    /// </summary>
    public IReadOnlyList<OutboxEntry> WritePublish(ConversationPublishWrite write)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(write);
        if (write.IsEmpty)
            return [];
        var scrubbed = new List<OutboxEntry>();
        long bytes;
        using (var tx = connection.BeginTransaction())
        {
            bytes = ScrubPendingFor(tx, write.ScrubPending, write.ScrubAllPending, scrubbed);
            WritePublish(tx, write);
            Fault("WritePublish:commit");
            tx.Commit();
        }

        AdjustRetainedStats(0, bytes);
        return scrubbed;
    }

    /// <summary>
    /// <see cref="ConversationPublishWrite.ScrubPending"/>、<see cref="ConversationPublishWrite.ScrubAllPending"/>（在呼叫端的交易裡、寫這一批的列之前）：
    /// 這些對話 Pending 的列合併鍵清掉，payload 換成 <c>{}</c>（<paramref name="itemsOnly"/> 只換 item 類，<paramref name="allKinds"/> 不論種類；已經是 <c>{}</c>
    /// 的不重算）；Sent／Retry 的列內容已凍結、不動。改過的列加進 <paramref name="into"/>，回傳 payload 位元組的增減
    /// </summary>
    private long ScrubPendingFor(SqliteTransaction tx, IReadOnlyList<string>? itemsOnly, IReadOnlyList<string>? allKinds, List<OutboxEntry> into)
    {
        // 對話 → 不論種類都換（兩份清單都有時以不論種類為準）
        var targets = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in itemsOnly ?? [])
            targets.TryAdd(id, false);
        foreach (var id in allKinds ?? [])
            targets[id] = true;
        if (targets.Count == 0)
            return 0;
        var (enc, payload) = PayloadRules.Encode(EmptyConversationPayload);
        var sha = PayloadRules.Sha256Hex(EmptyConversationPayload);
        long bytes = 0;
        // Rewrite：要換成的 payload（null＝只清合併鍵）
        var changed = new List<(OutboxEntry Entry, (string Enc, string Payload, string Sha)? Rewrite)>();
        using (var select = Command(tx, """
                   SELECT device_seq, stream, kind, enc, payload, payload_sha256, coalesce_key, state, created_at,
                          server_seq, reject_reason, acked_at, conversation_id, epoch, octet_length(payload)
                   FROM outbox WHERE state = 'pending' AND conversation_id = $id
                   ORDER BY device_seq
                   """))
        {
            foreach (var (id, all) in targets)
            {
                select.Parameters.Clear();
                Bind(select, ("$id", id));
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var entry = ReadEntry(reader);
                    (string Enc, string Payload, string Sha)? rewrite = null;
                    if ((all || ConversationEventKinds.ClassOf(entry.Kind) == SyncClasses.Item) && entry.PayloadSha256 != sha)
                        rewrite = (enc, payload, sha);
                    else if (!all && entry.Kind == ConversationEventKinds.Attention
                             && PayloadRules.TryDecode(entry.Enc, entry.Payload, ConversationEventRules.MaxPayloadBytes, out var json)
                             && ConversationEventRules.AttentionWithoutSpeech(json) is { } stripped)
                    {
                        // 語音句子可能帶內容的簡要說明（完整同步時組的）：拿掉，伺服器改組只有標題的句子
                        var (strippedEnc, strippedPayload) = PayloadRules.Encode(stripped);
                        rewrite = (strippedEnc, strippedPayload, PayloadRules.Sha256Hex(stripped));
                    }
                    if (rewrite is null && entry.CoalesceKey is null)
                        continue;
                    if (rewrite is { } r)
                        bytes += System.Text.Encoding.UTF8.GetByteCount(r.Payload) - reader.GetInt64(14);
                    changed.Add((rewrite is { } w ? entry with { Enc = w.Enc, Payload = w.Payload, PayloadSha256 = w.Sha, CoalesceKey = null } : entry with { CoalesceKey = null }, rewrite));
                }
            }
        }

        using var unkey = Command(tx, "UPDATE outbox SET coalesce_key = NULL WHERE device_seq = $seq AND state = 'pending'");
        using var empty = Command(tx, "UPDATE outbox SET coalesce_key = NULL, enc = $enc, payload = $payload, payload_sha256 = $sha WHERE device_seq = $seq AND state = 'pending'");
        foreach (var (entry, rewrite) in changed)
        {
            var cmd = rewrite is null ? unkey : empty;
            cmd.Parameters.Clear();
            if (rewrite is { } r)
                Bind(cmd, ("$enc", r.Enc), ("$payload", r.Payload), ("$sha", r.Sha), ("$seq", entry.DeviceSeq));
            else
                Bind(cmd, ("$seq", entry.DeviceSeq));
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"outbox 的待送列 {entry.DeviceSeq} 不見了");
            into.Add(entry);
        }

        into.Sort((a, b) => a.DeviceSeq.CompareTo(b.DeviceSeq));
        return bytes;
    }

    /// <summary><see cref="ConversationPublishWrite"/> 的內容（在呼叫端的交易裡）</summary>
    private void WritePublish(SqliteTransaction tx, ConversationPublishWrite write)
    {
        foreach (var id in write.ForgetItems)
        {
            foreach (var sql in new[]
                     {
                         "UPDATE published_items SET src_sha256 = NULL, wire_sha256 = NULL WHERE conversation_id = $id",
                         "UPDATE publish_meta SET items_file_sha256 = NULL WHERE conversation_id = $id",
                     })
            {
                using var cmd = Command(tx, sql);
                Bind(cmd, ("$id", id));
                cmd.ExecuteNonQuery();
            }
        }

        if (write.ResetRemoved is { Count: > 0 } resets)
        {
            using var reset = Command(tx, """
                UPDATE published_items SET removed = 0, src_sha256 = NULL, wire_sha256 = NULL
                WHERE conversation_id = $id AND item_id = $item
                """);
            foreach (var (id, item) in resets)
            {
                reset.Parameters.Clear();
                Bind(reset, ("$id", id), ("$item", item));
                reset.ExecuteNonQuery();
            }
        }

        if (write.Items.Count > 0)
        {
            using var upsert = Command(tx, """
                INSERT INTO published_items (conversation_id, item_id, ord, src_sha256, wire_sha256, truncated, removed)
                VALUES ($id, $item, $ord, $src, $wire, $truncated, 0)
                ON CONFLICT (conversation_id, item_id) DO UPDATE SET ord = excluded.ord, src_sha256 = excluded.src_sha256,
                  wire_sha256 = excluded.wire_sha256, truncated = excluded.truncated, removed = 0
                """);
            using var removed = Command(tx, """
                UPDATE published_items SET removed = 1, src_sha256 = NULL, wire_sha256 = NULL
                WHERE conversation_id = $id AND item_id = $item
                """);
            foreach (var record in write.Items)
            {
                if (record.ItemId is null)
                    throw new ArgumentException("項目的發布紀錄沒有項目 id", nameof(write));
                if (record.Removed)
                {
                    removed.Parameters.Clear();
                    Bind(removed, ("$id", record.ConversationId), ("$item", record.ItemId));
                    removed.ExecuteNonQuery();
                    continue;
                }

                if (record.Ord is not { } ord)
                    throw new ArgumentException("項目的發布紀錄沒有 Ord", nameof(write));
                upsert.Parameters.Clear();
                Bind(upsert, ("$id", record.ConversationId), ("$item", record.ItemId), ("$ord", ord), ("$src", record.SrcSha256), ("$wire", record.WireSha256),
                    ("$truncated", record.Truncated ? 1 : 0));
                upsert.ExecuteNonQuery();
            }
        }

        foreach (var row in write.Metas)
            UpsertPublishMeta(tx, row);

        if (write.NeedsRedo.Count > 0)
        {
            using var redo = Command(tx, """
                INSERT INTO publish_state (conversation_id, needs_redo) VALUES ($id, $value)
                ON CONFLICT (conversation_id) DO UPDATE SET needs_redo = excluded.needs_redo
                """);
            foreach (var (id, value) in write.NeedsRedo)
            {
                redo.Parameters.Clear();
                Bind(redo, ("$id", id), ("$value", value ? 1 : 0));
                redo.ExecuteNonQuery();
            }
        }
    }

    // ── 沒有 conv1 時的待送列（D13、C10） ──

    /// <summary>換過的對話列的 payload（舊伺服器以 invalid 擋下、不含內容；conv.deleted 本來就是它）</summary>
    public const string EmptyConversationPayload = "{}";

    /// <summary>
    /// 沒有 conv1 的連線送出之前：Pending 而且帶 conversationId 的列，payload 原地換成 <c>{}</c>、重算 <c>payload_sha256</c>（一個交易）。
    /// deviceSeq、kind、stream、conversationId、epoch、合併鍵都不變；Sent／Retry 的列內容已凍結、不動。回傳換過的列（依 deviceSeq）
    /// </summary>
    public IReadOnlyList<OutboxEntry> ScrubPendingConversationRows()
    {
        ThrowIfDisposed();
        var (enc, payload) = PayloadRules.Encode(EmptyConversationPayload);
        var sha = PayloadRules.Sha256Hex(EmptyConversationPayload);
        var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        var scrubbed = new List<OutboxEntry>();
        long bytes = 0;
        using (var tx = connection.BeginTransaction())
        {
            using (var select = Command(tx, """
                       SELECT device_seq, stream, kind, enc, payload, payload_sha256, coalesce_key, state, created_at,
                              server_seq, reject_reason, acked_at, conversation_id, epoch, octet_length(payload)
                       FROM outbox WHERE state = 'pending' AND conversation_id IS NOT NULL AND payload_sha256 != $sha
                       ORDER BY device_seq
                       """))
            {
                Bind(select, ("$sha", sha));
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var entry = ReadEntry(reader);
                    bytes += payloadBytes - reader.GetInt64(14);
                    scrubbed.Add(entry with { Enc = enc, Payload = payload, PayloadSha256 = sha });
                }
            }

            if (scrubbed.Count == 0)
                return [];
            using var update = Command(tx, """
                UPDATE outbox SET enc = $enc, payload = $payload, payload_sha256 = $sha
                WHERE device_seq = $seq AND state = 'pending' AND conversation_id IS NOT NULL
                """);
            foreach (var entry in scrubbed)
            {
                update.Parameters.Clear();
                Bind(update, ("$enc", enc), ("$payload", payload), ("$sha", sha), ("$seq", entry.DeviceSeq));
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"outbox 的待送列 {entry.DeviceSeq} 不見了");
            }

            Fault("ScrubPendingConversationRows:commit");
            tx.Commit();
        }

        AdjustRetainedStats(0, bytes);
        return scrubbed;
    }

    // ── publish_state、backfill_state（v2） ──

    /// <summary>記或清「要整份重新發布」（被拒、沒有 conv1、伺服器換實例後的墓碑，D13、D22）</summary>
    public void SetNeedsRedo(string conversationId, bool value)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            INSERT INTO publish_state (conversation_id, needs_redo) VALUES ($id, $value)
            ON CONFLICT (conversation_id) DO UPDATE SET needs_redo = excluded.needs_redo
            """);
        Bind(cmd, ("$id", conversationId), ("$value", value ? 1 : 0));
        cmd.ExecuteNonQuery();
    }

    /// <summary>記了 needs_redo 的對話</summary>
    public IReadOnlySet<string> LoadNeedsRedo()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT conversation_id FROM publish_state WHERE needs_redo != 0");
        using var reader = cmd.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>回補完成（這個對話的事件全部確認，D24）：記時間與剩下的位元組估計</summary>
    public void MarkBackfilled(string conversationId, long at, long? bytesLeft)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            INSERT INTO backfill_state (conversation_id, items_done_at, bytes_left) VALUES ($id, $at, $left)
            ON CONFLICT (conversation_id) DO UPDATE SET items_done_at = excluded.items_done_at, bytes_left = excluded.bytes_left
            """);
        Bind(cmd, ("$id", conversationId), ("$at", at), ("$left", bytesLeft));
        cmd.ExecuteNonQuery();
    }

    /// <summary>回補完成的對話 → 完成的時間</summary>
    public IReadOnlyDictionary<string, long> LoadBackfilled()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT conversation_id, items_done_at FROM backfill_state WHERE items_done_at IS NOT NULL");
        using var reader = cmd.ExecuteReader();
        var done = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            done[reader.GetString(0)] = reader.GetInt64(1);
        return done;
    }

    // ── conversation_cursors（v2；第 3 階段的游標在 v1 的 cursors） ──

    /// <summary>對話的拉取游標：類別 <c>meta</c> 或串流 <c>c:{id}</c> → seq</summary>
    public IReadOnlyDictionary<string, long> GetConversationCursors()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT stream, seq FROM conversation_cursors");
        using var reader = cmd.ExecuteReader();
        var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            cursors[reader.GetString(0)] = reader.GetInt64(1);
        return cursors;
    }

    /// <summary>寫進這些游標（一個交易；沒列到的不動）</summary>
    public void SetConversationCursors(IReadOnlyDictionary<string, long> cursors)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cursors);
        if (cursors.Count == 0)
            return;
        using var tx = connection.BeginTransaction();
        using var cmd = Command(tx, """
            INSERT INTO conversation_cursors (stream, seq) VALUES ($stream, $seq)
            ON CONFLICT (stream) DO UPDATE SET seq = excluded.seq
            """);
        foreach (var (stream, seq) in cursors)
        {
            cmd.Parameters.Clear();
            Bind(cmd, ("$stream", stream), ("$seq", seq));
            cmd.ExecuteNonQuery();
        }

        Fault("SetConversationCursors:commit");
        tx.Commit();
    }

    // ── 複本（replica_meta、replica_items） ──

    public IReadOnlyDictionary<string, ReplicaMetaRow> LoadReplicaMeta()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT conversation_id, owner_device_id, epoch, meta_json, deleted, seq FROM replica_meta");
        using var reader = cmd.ExecuteReader();
        var rows = new Dictionary<string, ReplicaMetaRow>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var row = new ReplicaMetaRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4) != 0, reader.GetInt64(5));
            rows[row.ConversationId] = row;
        }

        return rows;
    }

    public void UpsertReplicaMeta(ReplicaMetaRow row)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(row);
        using var cmd = Command(null, """
            INSERT INTO replica_meta (conversation_id, owner_device_id, epoch, meta_json, deleted, seq) VALUES ($id, $owner, $epoch, $json, $deleted, $seq)
            ON CONFLICT (conversation_id) DO UPDATE SET owner_device_id = excluded.owner_device_id, epoch = excluded.epoch,
              meta_json = excluded.meta_json, deleted = excluded.deleted, seq = excluded.seq
            """);
        Bind(cmd, ("$id", row.ConversationId), ("$owner", row.OwnerDeviceId), ("$epoch", row.Epoch), ("$json", row.MetaJson), ("$deleted", row.Deleted ? 1 : 0),
            ("$seq", row.Seq));
        cmd.ExecuteNonQuery();
    }

    /// <summary>複本的項目（依 Ord）</summary>
    public IReadOnlyList<ReplicaItemRow> LoadReplicaItems(string conversationId)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT item_id, ord, payload, seq FROM replica_items WHERE conversation_id = $id ORDER BY ord, item_id");
        Bind(cmd, ("$id", conversationId));
        using var reader = cmd.ExecuteReader();
        var rows = new List<ReplicaItemRow>();
        while (reader.Read())
            rows.Add(new ReplicaItemRow(conversationId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3)));
        return rows;
    }

    /// <summary>以 (對話, 項目 id) 新增或整筆取代（依 seq 套用：擁有者重送的新事件取代舊的）</summary>
    public void UpsertReplicaItem(ReplicaItemRow row)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(row);
        using var cmd = Command(null, """
            INSERT INTO replica_items (conversation_id, item_id, ord, payload, seq) VALUES ($id, $item, $ord, $payload, $seq)
            ON CONFLICT (conversation_id, item_id) DO UPDATE SET ord = excluded.ord, payload = excluded.payload, seq = excluded.seq
            """);
        Bind(cmd, ("$id", row.ConversationId), ("$item", row.ItemId), ("$ord", row.Ord), ("$payload", row.Payload), ("$seq", row.Seq));
        cmd.ExecuteNonQuery();
    }

    /// <summary>conv.itemRemoved：刪掉複本的一個項目</summary>
    public void DeleteReplicaItem(string conversationId, string itemId)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "DELETE FROM replica_items WHERE conversation_id = $id AND item_id = $item");
        Bind(cmd, ("$id", conversationId), ("$item", itemId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>meta 的政策不是 full：丟掉這個複本的項目、保留 meta（WP4.2 的 P10，D26）</summary>
    public void DropReplicaItems(string conversationId)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "DELETE FROM replica_items WHERE conversation_id = $id");
        Bind(cmd, ("$id", conversationId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>conv.deleted：丟掉整個複本（meta 與項目，一個交易）</summary>
    public void DropReplica(string conversationId)
    {
        ThrowIfDisposed();
        using var tx = connection.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM replica_items WHERE conversation_id = $id", "DELETE FROM replica_meta WHERE conversation_id = $id" })
        {
            using var cmd = Command(tx, sql);
            Bind(cmd, ("$id", conversationId));
            cmd.ExecuteNonQuery();
        }

        Fault("DropReplica:commit");
        tx.Commit();
    }

    /// <summary>
    /// 一頁拉到的複本變化與對話游標（WP4.3 計畫步 14，D26）：一個交易，<paramref name="ops"/> 依順序（伺服器 seq）套用，最後寫游標。
    /// 項目只取代 seq 比較小的（擁有者重送的新事件取代舊的，舊的不會蓋掉新的）
    /// </summary>
    public void ApplyReplicaPage(IReadOnlyList<ReplicaOp> ops, IReadOnlyDictionary<string, long> cursors)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(cursors);
        if (ops.Count == 0 && cursors.Count == 0)
            return;
        using var tx = connection.BeginTransaction();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case ReplicaOpKind.UpsertMeta:
                {
                    var row = op.Meta ?? throw new ArgumentException("UpsertMeta 要有 meta", nameof(ops));
                    using var cmd = Command(tx, """
                        INSERT INTO replica_meta (conversation_id, owner_device_id, epoch, meta_json, deleted, seq) VALUES ($id, $owner, $epoch, $json, $deleted, $seq)
                        ON CONFLICT (conversation_id) DO UPDATE SET owner_device_id = excluded.owner_device_id, epoch = excluded.epoch,
                          meta_json = excluded.meta_json, deleted = excluded.deleted, seq = excluded.seq
                        """);
                    Bind(cmd, ("$id", row.ConversationId), ("$owner", row.OwnerDeviceId), ("$epoch", row.Epoch), ("$json", row.MetaJson), ("$deleted", row.Deleted ? 1 : 0),
                        ("$seq", row.Seq));
                    cmd.ExecuteNonQuery();
                    break;
                }

                case ReplicaOpKind.DropItems:
                    ExecuteFor(tx, "DELETE FROM replica_items WHERE conversation_id = $id", op.ConversationId);
                    break;
                case ReplicaOpKind.DropReplica:
                    ExecuteFor(tx, "DELETE FROM replica_items WHERE conversation_id = $id", op.ConversationId);
                    ExecuteFor(tx, "DELETE FROM replica_meta WHERE conversation_id = $id", op.ConversationId);
                    break;
                case ReplicaOpKind.UpsertItem:
                {
                    var item = op.Item ?? throw new ArgumentException("UpsertItem 要有項目", nameof(ops));
                    using var cmd = Command(tx, """
                        INSERT INTO replica_items (conversation_id, item_id, ord, payload, seq) VALUES ($id, $item, $ord, $payload, $seq)
                        ON CONFLICT (conversation_id, item_id) DO UPDATE SET ord = excluded.ord, payload = excluded.payload, seq = excluded.seq
                        WHERE excluded.seq > replica_items.seq
                        """);
                    Bind(cmd, ("$id", item.ConversationId), ("$item", item.ItemId), ("$ord", item.Ord), ("$payload", item.Payload), ("$seq", item.Seq));
                    cmd.ExecuteNonQuery();
                    break;
                }

                case ReplicaOpKind.DeleteItem:
                {
                    using var cmd = Command(tx, "DELETE FROM replica_items WHERE conversation_id = $id AND item_id = $item");
                    Bind(cmd, ("$id", op.ConversationId), ("$item", op.ItemId ?? throw new ArgumentException("DeleteItem 要有項目 id", nameof(ops))));
                    cmd.ExecuteNonQuery();
                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(ops), op.Kind, "不認得的複本寫入");
            }
        }

        if (cursors.Count > 0)
        {
            using var cursor = Command(tx, """
                INSERT INTO conversation_cursors (stream, seq) VALUES ($stream, $seq)
                ON CONFLICT (stream) DO UPDATE SET seq = excluded.seq
                """);
            foreach (var (stream, seq) in cursors)
            {
                cursor.Parameters.Clear();
                Bind(cursor, ("$stream", stream), ("$seq", seq));
                cursor.ExecuteNonQuery();
            }
        }

        Fault("ApplyReplicaPage:commit");
        tx.Commit();
    }

    private void ExecuteFor(SqliteTransaction tx, string sql, string conversationId)
    {
        using var cmd = Command(tx, sql);
        Bind(cmd, ("$id", conversationId));
        cmd.ExecuteNonQuery();
    }

    // ── 修剪與換實例 ──

    /// <summary>
    /// 刪掉這個對話的 v2、v3 列（游標、租約、副本與發布、回補的狀態、項目的摘要與 Ord、複本），一個交易。
    /// 有墓碑（<c>publish_meta.deleted_at</c>）時那一列只留 <c>deleted_at</c>（其他欄位重設），<c>publish_state</c> 也留著（還沒補送的 conv.deleted
    /// 記在它的 needs_redo）：伺服器換實例或還原之後才補得了 conv.deleted（D22）。遠端稽核與媒體參照不在這裡刪（WP4.4、WP4.7 各自修剪）
    /// </summary>
    public void PruneConversation(string conversationId)
    {
        ThrowIfDisposed();
        using var tx = connection.BeginTransaction();
        bool tombstone;
        using (var check = Command(tx, "SELECT COUNT(*) FROM publish_meta WHERE conversation_id = $id AND deleted_at IS NOT NULL"))
        {
            Bind(check, ("$id", conversationId));
            tombstone = Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        using (var cursor = Command(tx, "DELETE FROM conversation_cursors WHERE stream = $stream"))
        {
            Bind(cursor, ("$stream", SyncStreams.Conversation(conversationId)));
            cursor.ExecuteNonQuery();
        }

        var statements = new List<string>
        {
            "DELETE FROM leases WHERE conversation_id = $id",
            "DELETE FROM replica_state WHERE conversation_id = $id",
            "DELETE FROM backfill_state WHERE conversation_id = $id",
            "DELETE FROM published_items WHERE conversation_id = $id",
            "DELETE FROM replica_items WHERE conversation_id = $id",
            "DELETE FROM replica_meta WHERE conversation_id = $id",
        };
        if (tombstone)
        {
            statements.Add("""
                UPDATE publish_meta SET created_written = 0, created_acked = 0, policy = NULL, meta_sha256 = NULL, queue_sha256 = NULL,
                  items_file_sha256 = NULL, blocked = NULL, blocked_at = NULL, created_retries = 0, retry_at = NULL
                WHERE conversation_id = $id
                """);
        }
        else
        {
            statements.Add("DELETE FROM publish_meta WHERE conversation_id = $id");
            statements.Add("DELETE FROM publish_state WHERE conversation_id = $id");
        }

        foreach (var sql in statements)
        {
            using var cmd = Command(tx, sql);
            Bind(cmd, ("$id", conversationId));
            cmd.ExecuteNonQuery();
        }

        Fault("PruneConversation:commit");
        tx.Commit();
    }

    /// <summary>
    /// 伺服器換實例或還原（<see cref="BeginServerChange"/> 的同一個交易，D22）：只對存在的表做（開啟時升級失敗的檔可能沒有 v2、v3 的表）——
    /// 清空 v2 的 <c>conversation_cursors</c>、<c>replica_state</c>、<c>publish_state</c>、<c>backfill_state</c>；
    /// <c>publish_meta</c> 每一列重設、只保留 <c>deleted_at</c>（墓碑）與 <c>ever_created</c>（還原的伺服器上可能還有它，刪除照送），
    /// 每個墓碑在 <c>publish_state</c> 記 needs_redo（補送 conv.deleted）；
    /// <c>published_items</c> 清掉摘要與「已送 itemRemoved」（新的伺服器什麼都沒有，要重送）但保留 Ord（之後重配會重用）；複本清空（重拉）。
    /// 租約、遠端指令的去重與稽核、媒體參照不動。kv <c>publish_server</c> 記成 <paramref name="serverKey"/>（D36：之後開啟時比對，步 12）
    /// </summary>
    private void ClearConversationTables(SqliteTransaction tx, string serverKey)
    {
        PutKv(tx, SyncStoreSchema.PublishServer, serverKey);
        foreach (var table in new[] { "conversation_cursors", "replica_state", "publish_state", "backfill_state", "replica_items", "replica_meta" })
        {
            if (TableExists(connection, table, tx))
                Execute(connection, tx, $"DELETE FROM {table};");
        }

        if (TableExists(connection, "publish_meta", tx))
        {
            Execute(connection, tx, """
                UPDATE publish_meta SET created_written = 0, created_acked = 0, policy = NULL, meta_sha256 = NULL, queue_sha256 = NULL,
                  items_file_sha256 = NULL, blocked = NULL, blocked_at = NULL, created_retries = 0, retry_at = NULL;
                """);
            if (TableExists(connection, "publish_state", tx))
                Execute(connection, tx, "INSERT INTO publish_state (conversation_id, needs_redo) SELECT conversation_id, 1 FROM publish_meta WHERE deleted_at IS NOT NULL;");
        }

        if (TableExists(connection, "published_items", tx))
            Execute(connection, tx, "UPDATE published_items SET src_sha256 = NULL, wire_sha256 = NULL, removed = 0;");
    }

    // ── outbox 瘦身與磁碟保護（步 9，D31、D24；不需要 v3 的表） ──

    /// <summary>測試接縫：有值時取代 <see cref="RetainedConversationStats"/> 的實際值（正式程式不設）</summary>
    internal (int Rows, long Bytes)? RetainedStatsForTest { get; set; }

    /// <summary>測試接縫：有值時取代 <see cref="FreeDiskBytes"/> 的實際值（正式程式不設）</summary>
    internal long? FreeDiskBytesForTest { get; set; }

    /// <summary>測試接縫：查剩餘空間的函式（參數是 sync.db 的完整路徑；正式是 <see cref="DriveInfo"/>），測試以它模擬讀不到</summary>
    internal Func<string, long>? FreeDiskProbeForTest { get; set; }

    /// <summary>讀不到剩餘空間時記過的警告數（只記第一次）</summary>
    internal int FreeDiskWarnings { get; private set; }

    /// <summary>保留中的對話列數與 payload 位元組（第一次問時掃一次，之後由 AppendMany、DeleteOutbox 依變動增減；null＝還沒掃）</summary>
    private (int Rows, long Bytes)? retainedStats;

    /// <summary>
    /// outbox 的全部列（同 <see cref="LoadOutbox"/>），但已完成的對話列不讀 payload：直接是骨架（<see cref="OutboxSliceRules.Skeleton"/>；
    /// <paramref name="share"/> 共用串流與對話 id 的字串）。第 3 階段的列與未完成的列照原樣
    /// </summary>
    public IReadOnlyList<OutboxEntry> LoadOutboxSlim(Func<string, string>? share = null)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            SELECT device_seq, stream, kind, enc,
                   CASE WHEN conversation_id IS NOT NULL AND state IN ('acked', 'rejected') THEN '' ELSE payload END,
                   CASE WHEN conversation_id IS NOT NULL AND state IN ('acked', 'rejected') THEN '' ELSE payload_sha256 END,
                   CASE WHEN conversation_id IS NOT NULL AND state = 'acked' THEN NULL ELSE coalesce_key END,
                   state, created_at, server_seq, reject_reason, acked_at, conversation_id, epoch
            FROM outbox ORDER BY device_seq
            """);
        using var reader = cmd.ExecuteReader();
        var entries = new List<OutboxEntry>();
        while (reader.Read())
        {
            var entry = ReadEntry(reader);
            entries.Add(OutboxSliceRules.CanSkeleton(entry) ? OutboxSliceRules.Skeleton(entry, share) : entry);
        }

        return entries;
    }

    /// <summary>從 sync.db 讀回這些列的完整內容（骨架要重送時；依 <paramref name="deviceSeqs"/> 的順序）。有一筆不在就丟 <see cref="InvalidOperationException"/></summary>
    public IReadOnlyList<OutboxEntry> LoadOutboxPayloads(IReadOnlyList<long> deviceSeqs)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(deviceSeqs);
        if (deviceSeqs.Count == 0)
            return [];
        Fault("LoadOutboxPayloads");
        using var cmd = Command(null, """
            SELECT device_seq, stream, kind, enc, payload, payload_sha256, coalesce_key, state, created_at,
                   server_seq, reject_reason, acked_at, conversation_id, epoch
            FROM outbox WHERE device_seq = $seq
            """);
        var seq = cmd.Parameters.Add("$seq", SqliteType.Integer);
        var entries = new List<OutboxEntry>(deviceSeqs.Count);
        foreach (var deviceSeq in deviceSeqs)
        {
            seq.Value = deviceSeq;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException($"outbox 沒有 deviceSeq {deviceSeq}（骨架讀不回內容）");
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    /// <summary>
    /// 保留中的對話列（outbox 裡帶 conversationId 的列，不論狀態）的列數與 payload 位元組（存在 sync.db 的 UTF-8 位元組），D24 磁碟保護的輸入。
    /// 第一次呼叫時掃一次，之後隨寫入增減（不重掃）；<see cref="RetainedStatsForTest"/> 有值時回它
    /// </summary>
    public (int Rows, long Bytes) RetainedConversationStats()
    {
        ThrowIfDisposed();
        if (RetainedStatsForTest is { } forTest)
            return forTest;
        if (retainedStats is { } cached)
            return cached;
        using var cmd = Command(null, "SELECT COUNT(*), COALESCE(SUM(octet_length(payload)), 0) FROM outbox WHERE conversation_id IS NOT NULL");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var stats = (checked((int)reader.GetInt64(0)), reader.GetInt64(1));
        retainedStats = stats;
        return stats;
    }

    /// <summary>
    /// sync.db 所在磁碟剩下的位元組（D24 磁碟保護的輸入）。查不到（網路路徑、權限、磁碟不見）時回 <see cref="long.MaxValue"/>（不因為查不到就停回補），
    /// 只在第一次記一行警告；<see cref="FreeDiskBytesForTest"/> 有值時回它
    /// </summary>
    public long FreeDiskBytes()
    {
        ThrowIfDisposed();
        if (FreeDiskBytesForTest is { } forTest)
            return forTest;
        var full = System.IO.Path.GetFullPath(Path);
        try
        {
            return FreeDiskProbeForTest is { } probe ? probe(full) : new DriveInfo(System.IO.Path.GetPathRoot(full)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (FreeDiskWarnings == 0)
            {
                FreeDiskWarnings++;
                Log.Warn($"查不到同步資料庫所在磁碟的剩餘空間，磁碟保護當成空間足夠（之後不再記錄）：{ex.Message}");
            }

            return long.MaxValue;
        }
    }

    /// <summary>寫入提交之後增減 <see cref="retainedStats"/>（還沒掃過就不記，第一次問時再掃）</summary>
    private void AdjustRetainedStats(int rows, long bytes)
    {
        if (retainedStats is { } stats)
            retainedStats = (stats.Rows + rows, stats.Bytes + bytes);
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
