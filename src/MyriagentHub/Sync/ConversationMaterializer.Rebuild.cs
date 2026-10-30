using Myriagent.Sync;
using MyriagentHub.Data;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>重建一個對話的一塊（一個寫入工作）的結果：<paramref name="Events"/>＝這一塊讀了幾筆事件</summary>
internal readonly record struct RebuildStep(bool Finished, int Events);

/// <summary>
/// 物化表的分塊重建（WP4.2 計畫步 5；D14）。每個對話：
/// <list type="number">
/// <item>第一塊（<see cref="RebuildStart"/>）：讀這個對話全部的 meta 類事件，以 <see cref="ConversationRebuildRules.FoldMeta"/> 重寫 <c>conversations</c> 列，
/// 刪掉它的 <c>items</c> 與 <c>conversation_runtime</c>、<c>item_count</c> 與 <c>max_ord</c> 歸 0，登記成「重建中」。</item>
/// <item>之後每塊（<see cref="RebuildNext"/>）：先以這段期間新到的 meta 類事件推進清除點（新的降級與刪除照常由增量物化清掉內容；增量的路徑標了 MetaDirty 才讀），
/// 再依 seq 重播最多 <c>RebuildChunkEvents</c> 筆 item 類事件（清除點之前的、已刪除的對話只更新 <c>max_ord</c>）；
/// 取到的比上限少就是追上了，同一個工作解除「重建中」、送 <see cref="ConversationChangeKind.Rebuilt"/>。</item>
/// </list>
/// 重建中的對話，新到的 meta 類事件照常物化（列已經是最新的），item 類只寫 events、由重播補上（<see cref="IsRebuilding"/>）。
/// 重播是可信任模式：只摺疊 events 存下的版本（命中過 DLP 的是遮蔽版），不重跑授權、驗證與 DLP；解析失敗與毒事件類的例外記錄後略過那一筆。
/// 「重建中」的進度只在寫入執行緒上存取；任何一塊失敗（含交易提交失敗）時由 <see cref="ConversationRebuild"/> 以 <see cref="RebuildReset"/> 放棄、整份從頭再做（冪等）。
/// </summary>
internal sealed partial class ConversationMaterializer
{
    private readonly Dictionary<string, RebuildProgress> rebuilding = new(StringComparer.Ordinal);

    /// <summary>這個對話正在重建（第一塊之後、最後一塊之前）：新到的 item 類事件只寫 events（只在寫入執行緒上呼叫）</summary>
    internal bool IsRebuilding(string conversationId) => rebuilding.ContainsKey(conversationId);

    /// <summary>放棄所有進行中的重建（寫入工作裡呼叫；之後的 item 類事件照常物化，下一次重建從頭再做）</summary>
    internal void RebuildReset(SyncWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        rebuilding.Clear();
    }

    /// <summary>重建的第一塊（寫入工作裡）：重寫 <c>conversations</c> 列、清掉內容、登記成重建中；這個對話沒有任何 created／meta 時刪掉它的列</summary>
    internal RebuildStep RebuildStart(SyncWriteContext context, string conversationId)
    {
        // 移除電腦時伺服器刪的對話（設計 14.22）：沒有事件能推回這個狀態，照事件重寫會讓它復活
        if (IsDeletedByServer(context, conversationId))
            return KeepServerTombstone(context, conversationId, 0);
        var stream = SyncStreams.Conversation(conversationId);
        var events = new List<RebuildMetaEvent>();
        var metaDone = 0L;
        var read = 0;
        using (var cmd = context.Command(
            "SELECT seq, author, kind, epoch, enc, payload, created_at FROM events WHERE stream = $s AND cls = $cls ORDER BY seq", ("$s", stream), ("$cls", SyncClasses.Meta)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                read++;
                var seq = reader.GetInt64(0);
                metaDone = seq;
                var kind = reader.GetString(2);
                if (TryParseStored(reader.GetString(4), reader.GetString(5), seq, kind) is { } parsed)
                {
                    events.Add(new RebuildMetaEvent(seq, reader.GetString(1), kind, reader.IsDBNull(3) ? ConversationIds.DefaultEpoch : reader.GetInt64(3),
                        reader.GetInt64(6), parsed.Json, parsed.Payload));
                }
            }
        }

        var meta = ConversationRebuildRules.FoldMeta(events);
        context.Execute("DELETE FROM items WHERE conversation_id = $c", ("$c", conversationId));
        context.Execute("DELETE FROM conversation_runtime WHERE conversation_id = $c", ("$c", conversationId));
        if (meta is null)
        {
            // 沒有任何 created／meta：不該有列（被拒的事件不在 events）
            context.Execute("DELETE FROM conversations WHERE conversation_id = $c", ("$c", conversationId));
            rebuilding.Remove(conversationId);
            context.AfterCommit(() =>
            {
                directory.Remove(conversationId);
                directory.MarkRebuildDone(conversationId);
            });
            return new RebuildStep(true, read);
        }

        WriteRebuiltRow(context, conversationId, meta);
        rebuilding[conversationId] = new RebuildProgress(meta.Cursor, metaDone, 0, MetaDirty: false);
        var entry = ReadEntry(context, conversationId);
        context.AfterCommit(() =>
        {
            directory.Apply(entry);
            directory.MarkRebuildActive(conversationId);
        });
        return new RebuildStep(false, read);
    }

    /// <summary>重建的下一塊（寫入工作裡）：推進清除點、依 seq 重播最多 <paramref name="chunk"/> 筆 item 類事件；追上了就解除重建中</summary>
    internal RebuildStep RebuildNext(SyncWriteContext context, string conversationId, int chunk)
    {
        if (!rebuilding.TryGetValue(conversationId, out var progress))
            throw new InvalidOperationException("這個對話沒有在重建");
        // 重建途中被移除電腦刪掉的（DeleteOwnedBy）：收尾、不再重播
        if (IsDeletedByServer(context, conversationId))
            return KeepServerTombstone(context, conversationId, 0);
        var stream = SyncStreams.Conversation(conversationId);

        // 1. 這段期間新到的 meta 類事件（已照常物化）：只推進政策、刪除與清除點。只在增量的路徑標過 MetaDirty 時才讀：
        //    ix_events_stream 沒有 cls 欄，從最後一筆 meta 類事件之後掃會經過這個對話之後全部的 item 類事件（回補的對話是一筆 created 加上大量項目，每塊都掃就是 O(N)）
        var cursor = progress.Cursor;
        var metaDone = progress.MetaDoneSeq;
        if (progress.MetaDirty)
        {
            using var cmd = context.Command("SELECT seq, kind, enc, payload FROM events WHERE stream = $s AND cls = $cls AND seq > $after ORDER BY seq",
                ("$s", stream), ("$cls", SyncClasses.Meta), ("$after", metaDone));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var seq = reader.GetInt64(0);
                var kind = reader.GetString(1);
                metaDone = seq;
                string? policy = null;
                if (kind is ConversationEventKinds.Created or ConversationEventKinds.Meta)
                {
                    if (TryParseStored(reader.GetString(2), reader.GetString(3), seq, kind) is not { } parsed)
                        continue;
                    policy = (string?)parsed.Payload["syncPolicy"];
                }
                cursor = ConversationRebuildRules.Track(cursor, seq, kind, policy);
            }
        }

        // 2. 依 seq 重播 item 類事件
        var itemDone = progress.ItemDoneSeq;
        var count = 0;
        using (var cmd = context.Command(
            "SELECT seq, author, kind, epoch, enc, payload FROM events WHERE stream = $s AND cls = $cls AND seq > $after ORDER BY seq LIMIT $limit",
            ("$s", stream), ("$cls", SyncClasses.Item), ("$after", itemDone), ("$limit", chunk)))
        using (var reader = cmd.ExecuteReader())
        {
            var rows = new List<(long Seq, string Author, string Kind, long? Epoch, string Enc, string Payload)>();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4), reader.GetString(5)));
            reader.Close();
            foreach (var row in rows)
            {
                count++;
                itemDone = row.Seq;
                Replay(context, conversationId, stream, row.Seq, row.Author, row.Kind, row.Epoch, row.Enc, row.Payload, cursor);
            }
        }

        var finished = ConversationRebuildRules.IsLastChunk(count, chunk);
        var entry = ReadEntry(context, conversationId);
        if (finished)
        {
            rebuilding.Remove(conversationId);
            context.AfterCommit(() =>
            {
                directory.Apply(entry);
                directory.MarkRebuildDone(conversationId);
                feed.Post(new ConversationChange(conversationId, ConversationChangeKind.Rebuilt, entry.LastSeq, entry.OwnerDeviceId ?? string.Empty));
            });
        }
        else
        {
            rebuilding[conversationId] = new RebuildProgress(cursor, metaDone, itemDone, MetaDirty: false);
            context.AfterCommit(() => directory.Apply(entry));
        }
        return new RebuildStep(finished, count);
    }

    /// <summary>重播一筆 item 類事件（可信任模式，在自己的 SAVEPOINT 裡：解析失敗與毒事件類的例外記錄後略過這一筆）</summary>
    private void Replay(SyncWriteContext context, string conversationId, string stream, long seq, string author, string kind, long? epoch, string enc,
        string payload, RebuildCursor cursor)
    {
        if (TryParseStored(enc, payload, seq, kind) is not { } parsed)
            return;
        context.Execute("SAVEPOINT rb");
        try
        {
            if (ConversationRebuildRules.ReplayOf(seq, cursor) == RebuildReplay.OrdOnly)
            {
                // 清除點之前、或已刪除：內容不放回，只有 Ord 只增不減
                if (kind == ConversationEventKinds.Item)
                {
                    context.Execute("UPDATE conversations SET max_ord = MAX(max_ord, $ord) WHERE conversation_id = $c",
                        ("$ord", (long)parsed.Payload["ord"]!), ("$c", conversationId));
                }
            }
            else
            {
                var stored = new SyncEvent(0, stream, kind, enc, string.Empty, payload, conversationId, epoch);
                ApplyItemClass(context, author, stored, parsed.Json, parsed.Payload, seq, fresh: false);
            }
            context.Execute("RELEASE rb");
        }
        catch (Exception ex) when (ConversationFaultRules.IsPoison(ex))
        {
            context.Execute("ROLLBACK TO rb");
            context.Execute("RELEASE rb");
            log.LogError("重建時略過一筆無法套用的對話事件：seq {Seq} {Kind} {ExceptionType}", seq, kind, ex.GetType().FullName);
        }
    }

    /// <summary>解碼並解析 events 存下的 payload；失敗時記錄（只記 seq 與種類，不記內容）後回 null</summary>
    private (string Json, JObject Payload)? TryParseStored(string enc, string payload, long seq, string kind)
    {
        if (PayloadRules.TryDecode(enc, payload, SyncLimits.Default.MaxInlinePayload, out var json)
            && ConversationEventRules.TryParseStrict(json, out var token) && token is JObject parsed)
            return (json, parsed);
        log.LogError("重建時略過一筆解不開的對話事件：seq {Seq} {Kind}", seq, kind);
        return null;
    }

    /// <summary>重建的第一塊寫回 <c>conversations</c> 列：事件推得出的欄位全部重寫；<c>lease_state</c> 與第 4 階段不寫的欄位（header、狀態文字、逐字稿、分支）保留</summary>
    private static void WriteRebuiltRow(SyncWriteContext context, string conversationId, RebuildMeta meta)
    {
        var logical = LogicalAccount(context, meta.OwnerDeviceId, meta.LocalAccountId);
        (string, object?)[] values =
        [
            ("$c", conversationId), ("$provider", meta.Provider), ("$logical", logical), ("$owner", meta.OwnerDeviceId), ("$epoch", meta.Epoch),
            ("$meta", meta.MetaJson), ("$title", meta.Title), ("$created", meta.CreatedAt), ("$updated", meta.UpdatedAt), ("$seq", meta.LastSeq),
            ("$running", meta.Running ? 1 : 0), ("$waiting", meta.Waiting ? 1 : 0), ("$turnStarted", meta.TurnStartedAt), ("$lastTurn", meta.LastTurn),
            ("$policy", meta.Cursor.Policy), ("$deletedAt", meta.DeletedAt), ("$deletedBy", meta.DeletedBy),
        ];
        var updated = context.Execute(
            "UPDATE conversations SET provider = $provider, logical_account_id = $logical, origin_device_id = $owner, owner_device_id = $owner, epoch = $epoch, "
            + "meta_json = $meta, title = $title, created_at = $created, updated_at = $updated, last_seq = $seq, item_count = 0, max_ord = 0, running = $running, "
            + "waiting = $waiting, turn_started_at = $turnStarted, last_turn = $lastTurn, sync_policy = $policy, deleted_at = $deletedAt, deleted_by = $deletedBy "
            + "WHERE conversation_id = $c", values);
        if (updated == 0)
        {
            context.Execute(
                "INSERT INTO conversations(conversation_id, provider, logical_account_id, origin_device_id, owner_device_id, epoch, meta_json, title, created_at, "
                + "updated_at, last_seq, running, waiting, turn_started_at, last_turn, sync_policy, deleted_at, deleted_by) "
                + "VALUES($c, $provider, $logical, $owner, $owner, $epoch, $meta, $title, $created, $updated, $seq, $running, $waiting, $turnStarted, $lastTurn, "
                + "$policy, $deletedAt, $deletedBy)", values);
        }
    }

    /// <summary>
    /// 重建中的對話被增量物化了一筆 created／meta／deleted（寫入執行緒上、交易提交之前）：下一塊要重讀 meta 類事件推進政策與清除點。
    /// 交易被回滾時標記照樣留著，只是下一塊多讀一次（保守、不會漏）
    /// </summary>
    private void MarkRebuildMetaDirty(string conversationId)
    {
        if (rebuilding.TryGetValue(conversationId, out var progress) && !progress.MetaDirty)
            rebuilding[conversationId] = progress with { MetaDirty = true };
    }

    /// <summary>一個重建中的對話的進度（只在寫入執行緒上存取）</summary>
    /// <param name="MetaDoneSeq">已經看過的最後一筆 meta 類事件</param>
    /// <param name="ItemDoneSeq">已經重播到的最後一筆 item 類事件</param>
    /// <param name="MetaDirty">上一塊之後有新的 created／meta／deleted 被增量物化（<see cref="MarkRebuildMetaDirty"/>）</param>
    private sealed record RebuildProgress(RebuildCursor Cursor, long MetaDoneSeq, long ItemDoneSeq, bool MetaDirty);
}
