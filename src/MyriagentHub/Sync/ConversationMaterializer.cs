using System.Text;
using Myriagent.Sync;
using MyriagentHub.Data;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 對話事件的物化（WP4.2 計畫步 3；D4、D5、D9、D10、D11、D13、D16、D44）：<see cref="EventLog"/> 在寫入者的交易裡、事件寫進 <c>events</c> 的同一個
/// <c>SAVEPOINT ev</c> 之內呼叫 <see cref="Apply"/>，任何時刻讀到的 <c>conversations</c>、<c>items</c>、<c>conversation_runtime</c> 都對應某個 seq 前綴。
/// 純函式的檢查（<see cref="Precheck"/>）在寫入者之外、呼叫端的執行緒上先做完（D3）；授權（<see cref="ReadRow"/>＋<see cref="ConversationApplyRules.Decide"/>）
/// 經交易查表，不讀目錄快取（D4）。提交之後才更新目錄快取、送變更通知、累加營運計數與彙總稽核（都是 O(1)）。
/// 物化寫的是<b>存下的版本</b>：第二道 DLP 命中時是遮蔽版（D44）。
/// 政策從 full 降成 metadata／paused、或刪除時，同一個交易清掉物化的內容（<see cref="PurgeContent"/>，步 4、答 4）。
/// 物化表可以從 events 分塊重建（ConversationMaterializer.Rebuild.cs，步 5、D14）：重建中的對話，新到的 item 類事件只寫 events、由重建補上。
/// </summary>
internal sealed partial class ConversationMaterializer(ConversationDirectoryCache directory, ConversationChangeFeed feed, ConversationRejectAudit rejects,
    ConversationCounters counters, ILogger<ConversationMaterializer> log)
{
    /// <summary><c>server_meta</c> 裡物化語意版本的鍵（D14：存的值和 <see cref="Version"/> 不相等就重建，步 5）</summary>
    public const string VersionKey = "conv_materializer_version";

    /// <summary>物化的語意每改一次就加 1（D14）</summary>
    public const int Version = 1;

    /// <summary>測試注入點：物化之前呼叫（在 <c>SAVEPOINT ev</c> 之內），讓毒事件與環境錯誤的分支測得到（D37）</summary>
    internal Action<SyncEvent>? FaultForTest { get; set; }

    /// <summary>
    /// 一批的預先檢查（D3）：每筆對話種類的事件一份 <see cref="ConversationPrecheck"/>（以索引對應），其他種類是 null。
    /// 在呼叫端的執行緒上做（不是寫入執行緒）；批次的結構不合格時由寫入者照舊整批拒絕，這裡回 null
    /// </summary>
    public IReadOnlyList<ConversationPrecheck?>? Precheck(Batch? batch, SyncLimits limits)
    {
        if (OutboxRules.CheckBatchShape(batch, limits) is not null)
            return null;
        var results = new ConversationPrecheck?[batch!.Events.Count];
        for (var i = 0; i < results.Length; i++)
        {
            var e = batch.Events[i];
            if (ConversationEventKinds.IsConversationKind(e.Kind))
                results[i] = ConversationPrecheckRules.Check(e, limits);
        }
        return results;
    }

    /// <summary>授權用的列（交易內查表，D4）；沒有這個對話回 null</summary>
    public ConversationRow? ReadRow(SyncWriteContext context, string conversationId)
    {
        using var cmd = context.Command(
            "SELECT owner_device_id, epoch, sync_policy, deleted_at, lease_state FROM conversations WHERE conversation_id = $c", ("$c", conversationId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ConversationRow(conversationId,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            !reader.IsDBNull(3),
            !reader.IsDBNull(4) && reader.GetString(4) == ConversationLeaseStates.Recovering);
    }

    /// <summary>
    /// 物化一筆已接受、已寫進 events 的對話事件（在 <c>SAVEPOINT ev</c> 之內；丟出的例外由呼叫端依 <see cref="ConversationFaultRules"/> 分類）。
    /// </summary>
    /// <param name="stored">寫進 events 的那一筆（命中 DLP 時 payload／enc 是遮蔽版；<c>payloadSha256</c> 一律是裝置送的）</param>
    /// <param name="pre">預先檢查的結果（<see cref="ConversationPrecheck.Json"/>／<see cref="ConversationPrecheck.Parsed"/> 是存下的版本）</param>
    /// <param name="clearsRecovering">擁有者被接受的新 <c>conv.created</c>（D16）：解除 recovering</param>
    /// <param name="fresh">新的上行事件（變更的 <see cref="ConversationChange.Fresh"/>，D13、D45）</param>
    public void Apply(SyncWriteContext context, string author, SyncEvent stored, ConversationPrecheck pre, long seq,
        ConversationDecision decision, bool clearsRecovering, bool fresh)
    {
        ArgumentNullException.ThrowIfNull(pre.Parsed);
        ArgumentNullException.ThrowIfNull(pre.Json);
        FaultForTest?.Invoke(stored);
        var conversationId = stored.ConversationId!;
        var payload = pre.Parsed;
        var now = context.Now;
        ConversationChange change;
        ConversationChange? purged = null;
        switch (decision.Action)
        {
            case ConversationAction.Create:
            case ConversationAction.ReplaceMeta:
                WriteMeta(context, author, conversationId, decision, pre.Json, payload, seq, clearsRecovering);
                change = new ConversationChange(conversationId, ConversationChangeKind.Meta, seq, author, Fresh: fresh);
                if (decision.PurgeContent)
                {
                    // full 降成 metadata／paused（含桌面的 DLP 自動暫停）：同一個交易清掉物化的內容，Pull 不再回 item 類事件（答 4、D7、D17）
                    PurgeContent(context, conversationId);
                    purged = new ConversationChange(conversationId, ConversationChangeKind.Purged, seq, author, Fresh: fresh);
                }
                break;
            case ConversationAction.Turn:
            {
                var phase = (string)payload["phase"]!;
                if (phase == ConversationTurnPhases.Started)
                {
                    context.Execute("UPDATE conversations SET running = 1, turn_started_at = $now, last_seq = MAX(last_seq, $seq) WHERE conversation_id = $c",
                        ("$now", now), ("$seq", seq), ("$c", conversationId));
                }
                else
                {
                    context.Execute("UPDATE conversations SET running = 0, last_turn = $turn, last_seq = MAX(last_seq, $seq) WHERE conversation_id = $c",
                        ("$turn", pre.Json), ("$seq", seq), ("$c", conversationId));
                }
                change = new ConversationChange(conversationId, ConversationChangeKind.Turn, seq, author, TurnPhase: phase, Fresh: fresh);
                break;
            }
            case ConversationAction.Attention:
                TouchConversation(context, conversationId, seq);
                change = new ConversationChange(conversationId, ConversationChangeKind.Attention, seq, author, AttentionKind: (string)payload["kind"]!, Fresh: fresh,
                    Speech: payload["speech"] is JValue { Type: JTokenType.String, Value: string speech } ? speech : null);
                break;
            case ConversationAction.Delete:
                // 在伺服器上刪除（答 4、D34；含改成只在這台電腦）：同一個交易清掉內容，墓碑列保留，之後的事件一律回 deleted、不能改回同步。
                // Deleted 的變更本身就表示內容已清（不另送 Purged）
                context.Execute("UPDATE conversations SET deleted_at = $now, deleted_by = $by, last_seq = MAX(last_seq, $seq) WHERE conversation_id = $c",
                    ("$now", now), ("$by", author), ("$seq", seq), ("$c", conversationId));
                PurgeContent(context, conversationId);
                // 未讀列也在同一個交易刪掉（步 12A、D34）；閘道取到 Deleted 時再從記憶體的集合拿掉。降級（metadata／paused）不清未讀
                context.Execute("DELETE FROM conversation_unread WHERE conversation_id = $c", ("$c", conversationId));
                change = new ConversationChange(conversationId, ConversationChangeKind.Deleted, seq, author, Fresh: fresh);
                break;
            case ConversationAction.UpsertItem:
            case ConversationAction.RemoveItem:
            case ConversationAction.Queue:
                if (IsRebuilding(conversationId))
                {
                    // 重建中（D14）：只寫 events（已寫），由重建依 seq 補上；不換目錄、不送變更（做完送 Rebuilt）
                    AfterAccepted(context, author, stored, seq, now, pre.Remasked, null, null, null);
                    return;
                }
                change = ApplyItemClass(context, author, stored, pre.Json, payload, seq, fresh);
                break;
            default:
                throw new InvalidOperationException("不是被接受的物化動作：" + decision.Action);
        }

        // 重建中的對話換了政策或被刪：重建的下一塊要推進清除點（D14）
        if (decision.Action is ConversationAction.Create or ConversationAction.ReplaceMeta or ConversationAction.Delete)
            MarkRebuildMetaDirty(conversationId);
        AfterAccepted(context, author, stored, seq, now, pre.Remasked, ReadEntry(context, conversationId), change, purged);
    }

    /// <summary>被接受之後的提交後動作：換目錄、送變更（依序）、累加計數與再遮蔽的彙總稽核</summary>
    private void AfterAccepted(SyncWriteContext context, string author, SyncEvent stored, long seq, long now, bool remasked, ConversationEntry? entry,
        ConversationChange? change, ConversationChange? purged)
    {
        var bytes = Encoding.UTF8.GetByteCount(stored.Payload);
        var kind = stored.Kind;
        context.AfterCommit(() =>
        {
            if (entry is not null)
                directory.Apply(entry);
            if (change is not null)
                feed.Post(change);
            if (purged is not null)
                feed.Post(purged);
            counters.Accepted(author, seq, bytes, now, remasked);
            if (remasked)
                rejects.AddRemasked(author, kind);
        });
    }

    /// <summary>
    /// item 類事件的物化（增量與重建的重播共用；D9、提案 C5）：<c>conv.item</c> 整筆 upsert、<c>conv.itemRemoved</c> 設 removed、<c>conv.queue</c> 整份取代。
    /// <c>last_seq</c> 一律取較大的（重建期間 meta 類照常物化、item 類由重播補上，順序可能交錯）
    /// </summary>
    /// <param name="stored">events 存下的那一筆（body／enc 照它）</param>
    private static ConversationChange ApplyItemClass(SyncWriteContext context, string author, SyncEvent stored, string json, JObject payload, long seq, bool fresh)
    {
        var conversationId = stored.ConversationId!;
        switch (stored.Kind)
        {
            case ConversationEventKinds.Item:
                return UpsertItem(context, author, stored, json, payload, seq, fresh);
            case ConversationEventKinds.ItemRemoved:
            {
                var itemId = (string)payload["id"]!;
                var removed = context.Execute(
                    "UPDATE items SET removed = 1, last_seq = $seq WHERE conversation_id = $c AND item_id = $i AND removed = 0",
                    ("$seq", seq), ("$c", conversationId), ("$i", itemId));
                // 已經移除過的那一列也記下 seq；沒有那一列什麼都不做、不新增（提案 C5）
                if (removed == 0)
                    context.Execute("UPDATE items SET last_seq = $seq WHERE conversation_id = $c AND item_id = $i", ("$seq", seq), ("$c", conversationId), ("$i", itemId));
                context.Execute("UPDATE conversations SET item_count = item_count - $n, last_seq = MAX(last_seq, $seq) WHERE conversation_id = $c",
                    ("$n", removed), ("$seq", seq), ("$c", conversationId));
                return new ConversationChange(conversationId, ConversationChangeKind.ItemRemoved, seq, author, ItemId: itemId, Fresh: fresh);
            }
            case ConversationEventKinds.Queue:
                context.Execute(
                    "INSERT INTO conversation_runtime(conversation_id, queue_json, pending_attachments_json, version) VALUES($c, $q, '[]', 1) "
                    + "ON CONFLICT(conversation_id) DO UPDATE SET queue_json = excluded.queue_json, version = conversation_runtime.version + 1",
                    ("$c", conversationId), ("$q", json));
                TouchConversation(context, conversationId, seq);
                return new ConversationChange(conversationId, ConversationChangeKind.Queue, seq, author, Fresh: fresh);
            default:
                throw new InvalidOperationException("不是 item 類的對話事件：" + stored.Kind);
        }
    }

    /// <summary>一筆被拒的對話事件提交之後的計數與彙總稽核（被回滾的不算）</summary>
    public void Rejected(SyncWriteContext context, string author, string reason, string kind) =>
        context.AfterCommit(() =>
        {
            counters.Rejected(reason);
            rejects.Add(author, reason, kind);
        });

    /// <summary>毒事件（D37）的錯誤記錄：只記例外的型別、裝置與種類，不記訊息與內容（例外訊息可能帶 JSON 的路徑與屬性名稱）</summary>
    public void ReportPoison(string author, string kind, long deviceSeq, Exception ex) =>
        log.LogError("對話事件物化失敗（毒事件：只拒收這一筆，同批其他事件照收）：{DeviceId} #{DeviceSeq} {Kind} {ExceptionType}",
            author, deviceSeq, kind, ex.GetType().FullName);

    private static void WriteMeta(SyncWriteContext context, string author, string conversationId, ConversationDecision decision, string json, JObject meta,
        long seq, bool clearsRecovering)
    {
        var now = context.Now;
        var logical = LogicalAccount(context, author, (string?)meta["localAccountId"]);
        var policy = ConversationSyncPolicies.Interpret((string?)meta["syncPolicy"]);
        var updatedAt = ConversationApplyRules.DisplayTime((long?)meta["updatedAt"], now);
        var running = (bool)meta["running"]! ? 1 : 0;
        var waiting = (bool)meta["waiting"]! ? 1 : 0;
        if (decision.Action == ConversationAction.Create)
        {
            context.Execute(
                "INSERT INTO conversations(conversation_id, provider, logical_account_id, origin_device_id, owner_device_id, epoch, meta_json, title, "
                + "created_at, updated_at, last_seq, running, waiting, sync_policy) "
                + "VALUES($c, $provider, $logical, $author, $author, $epoch, $meta, $title, $created, $updated, $seq, $running, $waiting, $policy)",
                ("$c", conversationId), ("$provider", (string)meta["provider"]!), ("$logical", logical), ("$author", decision.Owner ?? author),
                ("$epoch", decision.Epoch ?? ConversationIds.DefaultEpoch), ("$meta", json), ("$title", (string)meta["title"]!),
                ("$created", ConversationApplyRules.DisplayTime((long?)meta["createdAt"], now)), ("$updated", updatedAt), ("$seq", seq),
                ("$running", running), ("$waiting", waiting), ("$policy", policy));
            return;
        }

        // 整份 meta 取代：不改擁有者、來源裝置與建立時間（D5）；擁有者的新 conv.created 解除 recovering（D16）
        context.Execute(
            "UPDATE conversations SET provider = $provider, logical_account_id = $logical, meta_json = $meta, title = $title, updated_at = $updated, "
            + "last_seq = MAX(last_seq, $seq), running = $running, waiting = $waiting, sync_policy = $policy"
            + (clearsRecovering ? ", lease_state = '" + ConversationLeaseStates.Owned + "'" : string.Empty)
            + " WHERE conversation_id = $c",
            ("$provider", (string)meta["provider"]!), ("$logical", logical), ("$meta", json), ("$title", (string)meta["title"]!), ("$updated", updatedAt),
            ("$seq", seq), ("$running", running), ("$waiting", waiting), ("$policy", policy), ("$c", conversationId));
    }

    /// <summary>(擁有者裝置, 本機帳戶) 在交易內對到的邏輯帳戶（D11）；沒有對應或沒帶本機帳戶回 null</summary>
    private static string? LogicalAccount(SyncWriteContext context, string deviceId, string? localAccountId) =>
        localAccountId is null
            ? null
            : context.Scalar("SELECT logical_account_id FROM account_links WHERE device_id = $d AND local_account_id = $l", ("$d", deviceId), ("$l", localAccountId)) as string;

    /// <summary>
    /// <c>conv.item</c>：依 (conversation_id, item_id) 整筆 upsert、不比 ver（D9）；<c>max_ord</c> 只增不減，新列或由 removed 變回時 <c>item_count</c> 加 1。
    /// body／enc 是存下的 payload（同 events，命中 DLP 時是遮蔽版）
    /// </summary>
    private static ConversationChange UpsertItem(SyncWriteContext context, string author, SyncEvent stored, string json, JObject item, long seq, bool fresh)
    {
        var conversationId = stored.ConversationId!;
        var itemId = (string)item["id"]!;
        var ord = (long)item["ord"]!;
        var ver = (long)item["ver"]!;
        var status = (string)item["status"]!;
        var final = ConversationApplyRules.IsFinal(status);
        var wasLive = context.Scalar("SELECT removed FROM items WHERE conversation_id = $c AND item_id = $i", ("$c", conversationId), ("$i", itemId)) is 0L;
        context.Execute(
            "INSERT INTO items(conversation_id, item_id, ord, last_seq, epoch, ver, kind, status, final, origin_device_id, body, enc, removed) "
            + "VALUES($c, $i, $ord, $seq, $epoch, $ver, $kind, $status, $final, $author, $body, $enc, 0) "
            + "ON CONFLICT(conversation_id, item_id) DO UPDATE SET ord = excluded.ord, last_seq = excluded.last_seq, epoch = excluded.epoch, ver = excluded.ver, "
            + "kind = excluded.kind, status = excluded.status, final = excluded.final, origin_device_id = excluded.origin_device_id, body = excluded.body, "
            + "enc = excluded.enc, removed = 0",
            ("$c", conversationId), ("$i", itemId), ("$ord", ord), ("$seq", seq), ("$epoch", stored.Epoch), ("$ver", ver), ("$kind", (string)item["kind"]!),
            ("$status", status), ("$final", final ? 1 : 0), ("$author", author), ("$body", stored.Payload), ("$enc", stored.Enc));
        context.Execute(
            "UPDATE conversations SET max_ord = MAX(max_ord, $ord), item_count = item_count + $n, last_seq = MAX(last_seq, $seq) WHERE conversation_id = $c",
            ("$ord", ord), ("$n", wasLive ? 0 : 1), ("$seq", seq), ("$c", conversationId));
        var body = Encoding.UTF8.GetByteCount(json) <= ConversationChangeFeed.MaxInlineBodyBytes ? json : null;
        // ReceivedAt 同 events.created_at（同一個交易的 context.Now）；重建的重播也走這裡，但重建不送項目的變更（做完只送 Rebuilt）
        return new ConversationChange(conversationId, ConversationChangeKind.Item, seq, author, ItemId: itemId, Ord: ord, Ver: ver, Final: final,
            Body: body, Status: status, Fresh: fresh, ReceivedAt: context.Now);
    }

    /// <summary>
    /// 清掉一個對話物化的內容（答 4；降級與刪除）：<c>items</c>、<c>conversation_runtime</c> 的列，<c>item_count</c> 歸 0；<c>max_ord</c> 不變（Ord 只增不減）。
    /// <c>conversations</c> 列保留；原始事件留到之後的保留期清理。刪除時另在 <see cref="Apply"/> 刪未讀列（步 12A；降級不清未讀）
    /// </summary>
    internal static void PurgeContent(SyncWriteContext context, string conversationId)
    {
        context.Execute("DELETE FROM items WHERE conversation_id = $c", ("$c", conversationId));
        context.Execute("DELETE FROM conversation_runtime WHERE conversation_id = $c", ("$c", conversationId));
        context.Execute("UPDATE conversations SET item_count = 0 WHERE conversation_id = $c", ("$c", conversationId));
    }

    private static void TouchConversation(SyncWriteContext context, string conversationId, long seq) =>
        context.Execute("UPDATE conversations SET last_seq = MAX(last_seq, $seq) WHERE conversation_id = $c", ("$seq", seq), ("$c", conversationId));

    private static ConversationEntry ReadEntry(SyncWriteContext context, string conversationId)
    {
        using var cmd = context.Command("SELECT " + ConversationDirectoryCache.Columns + " FROM conversations WHERE conversation_id = $c", ("$c", conversationId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("物化之後找不到對話列");
        return ConversationDirectoryCache.Read(reader, 0);
    }
}
