using System.Text;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Sync;

/// <summary>一條裝置連線的上行狀態（Hello 時定；對帳模式下 <see cref="ResyncFrom"/> 不是 null）</summary>
internal sealed record UpstreamContext(string DeviceId, long? ResyncFrom, OutboxPosition? HelloOutbox);

/// <summary>一批的處理結果：<see cref="Ack"/> 在交易提交之後才送；<see cref="ResyncDone"/>＝這批讓對帳模式解除了</summary>
internal sealed record BatchOutcome(BatchAck Ack, bool ResyncDone);

/// <summary>拉取的游標格式錯誤（hub 回 invalid）</summary>
internal sealed class PullRequestException(string message) : Exception(message);

/// <summary>裝置已撤銷：這一批不寫入（批次幫浦中斷連線，不回 BatchAck）</summary>
internal sealed class DeviceRevokedException(string deviceId) : Exception("裝置已撤銷：" + deviceId);

/// <summary>
/// 持久事件的寫入與拉取（設計 4.3、sync-protocol 第 5 節）：
/// <list type="bullet">
/// <item>上行：在 <see cref="SyncWriter"/> 的交易裡依到達順序處理一批——每筆以 <see cref="OutboxRules.ClassifyIncoming"/> 判斷，接受的配發 seq、
/// 被擋的寫進 rejected_events（仍佔 deviceSeq），目錄的物化（<see cref="Catalog"/>）與事件同一個交易；交易提交（FULL）之後才回 BatchAck。
/// 整批錯誤（gap、seq_regression、格式）時這一批沒有任何一筆生效。</item>
/// <item>下行：依類別或串流的游標拉，回應有筆數與位元組上限，各游標拿到的一定是它待拉事件的前綴。</item>
/// <item>第 4 階段的對話事件（WP4.2）：<c>conversations</c>（<see cref="ConversationMaterializer"/>）不是 null 時交給 <see cref="ApplyConversation"/>（EventLog.Conversations.cs）；
/// 純函式的檢查在 <see cref="AppendBatchAsync"/> 呼叫寫入者之前做完。直接 new、沒有它的（第 3 階段的測試）照舊一律 invalid。</item>
/// </list>
/// </summary>
internal sealed partial class EventLog(SyncWriter writer, Catalog catalog, DeviceDirectory devices, ISyncNotifier notifier, AuditLog audit,
    ConversationMaterializer? conversations = null)
{
    public const int MaxPullCursors = 16;

    public const int MaxStreamLength = 200;

    /// <summary>每筆拉取事件在回應裡的固定開銷估計（欄位名稱、seq、時間）</summary>
    public const int PulledEventOverheadBytes = 200;

    private long pulledRows;

    public SyncLimits Limits { get; } = SyncLimits.Default;

    /// <summary>Pull 讀進記憶體的列數（累計；測試用：每個游標的位元組預算讓大事件不必整批讀進來）</summary>
    internal long PulledRows => Interlocked.Read(ref pulledRows);

    /// <summary>
    /// 寫入一批：對話種類的事件先在呼叫端的執行緒做完預先檢查（解碼、雜湊、驗證、第二道 DLP 與再遮蔽；WP4.2 D3），
    /// 結果以索引對應交給寫入者——一筆 128 KB、接近命中的事件不會拖住全站的寫入
    /// </summary>
    public Task<BatchOutcome> AppendBatchAsync(UpstreamContext upstream, Batch batch, CancellationToken cancellationToken = default)
    {
        var prechecks = conversations?.Precheck(batch, Limits);
        return writer.WriteAsync(context => AppendBatch(context, upstream, batch, prechecks), cancellationToken);
    }

    /// <summary>在寫入者裡處理一批（對話種類的事件現場做預先檢查；只給直接呼叫的測試用）</summary>
    internal BatchOutcome AppendBatch(SyncWriteContext context, UpstreamContext upstream, Batch batch) => AppendBatch(context, upstream, batch, null);

    /// <param name="prechecks">對話種類的事件的預先檢查（以索引對應 <c>batch.Events</c>）；null＝在寫入者裡現場檢查</param>
    internal BatchOutcome AppendBatch(SyncWriteContext context, UpstreamContext upstream, Batch batch, IReadOnlyList<ConversationPrecheck?>? prechecks)
    {
        var deviceId = upstream.DeviceId;
        // 撤銷先標目錄、才把 RevokeDevice 排進寫入佇列：在寫入者上檢查，撤銷之前已排隊的批次（連線斷了幫浦照樣做完）
        // 不會在 device.revoked 之後才提交、把帳戶連結或偏好改回來
        if (devices.Get(deviceId) is not { Revoked: false })
            throw new DeviceRevokedException(deviceId);
        Catalog.EnsureStream(context, deviceId);
        var next = (long)context.Scalar("SELECT next_device_seq FROM device_streams WHERE device_id = $d", ("$d", deviceId))!;
        var batchId = batch?.BatchId ?? string.Empty;
        if (OutboxRules.CheckBatchShape(batch, Limits) is { } shapeError)
            return new BatchOutcome(new BatchAck(batchId, next - 1, [], new BatchError(shapeError)), false);

        var start = next;
        var mark = context.AfterCommitMark;
        context.Execute("SAVEPOINT batch");
        var results = new List<BatchEventResult>(batch!.Events.Count);
        var audits = new List<AuditEntry>();
        for (var index = 0; index < batch.Events.Count; index++)
        {
            var e = batch.Events[index];
            var precheck = prechecks is not null && index < prechecks.Count ? prechecks[index] : null;
            var identity = OutboxRules.EventIdentity(e.Kind ?? string.Empty, e.Stream ?? string.Empty, e.ConversationId, e.Epoch, e.PayloadSha256);
            var existing = FindExisting(context, deviceId, e.DeviceSeq);
            var kind = OutboxRules.ClassifyIncoming(next, e.DeviceSeq, existing?.Identity, identity, upstream.ResyncFrom);
            switch (kind)
            {
                case IncomingEventKind.Duplicate:
                    results.Add(existing!.Result);
                    continue;
                case IncomingEventKind.Regression:
                case IncomingEventKind.Gap:
                {
                    context.Execute("ROLLBACK TO batch");
                    context.Execute("RELEASE batch");
                    context.DiscardAfterCommit(mark);
                    if (kind == IncomingEventKind.Regression)
                    {
                        // 裝置的 sync.db 倒退了（被還原或複製到別台）：停止同步、重新配對成新的裝置身分
                        var regression = new AuditEntry(AuditActors.Device, deviceId, null, AuditActions.SyncSeqRegression, null, AuditOutcomes.Denied,
                            new { deviceSeq = e.DeviceSeq, kind = e.Kind });
                        context.AuditAfterCommit(audit, regression);
                        return new BatchOutcome(new BatchAck(batchId, start - 1, [], new BatchError(SyncErrorCodes.SeqRegression)), false);
                    }
                    return new BatchOutcome(new BatchAck(batchId, start - 1, [], new BatchError(SyncErrorCodes.Gap, start)), false);
                }
                case IncomingEventKind.GapAccepted:
                    // 備份比這台的保留窗舊、缺口補不回來（設計 10.2 第 2 步）：記稽核，從這筆接下去；缺的內容由本機狀態的重送補齊
                    audits.Add(new AuditEntry(AuditActors.Device, deviceId, null, AuditActions.SyncResync, null, AuditOutcomes.Ok,
                        new { verdict = "gap_accepted", from = next, to = e.DeviceSeq }));
                    next = e.DeviceSeq;
                    results.Add(Apply(context, upstream, e, precheck));
                    next++;
                    continue;
                case IncomingEventKind.Next:
                    results.Add(Apply(context, upstream, e, precheck));
                    next++;
                    continue;
                case IncomingEventKind.Missing:
                    // 對帳模式下備份裡沒有的舊事件：當成新事件套用（唯一索引保證不重複）
                    audits.Add(new AuditEntry(AuditActors.Device, deviceId, null, AuditActions.SyncResync, null, AuditOutcomes.Ok,
                        new { verdict = "missing", deviceSeq = e.DeviceSeq, kind = e.Kind }));
                    results.Add(Apply(context, upstream, e, precheck));
                    continue;
                default:
                    // Stale：伺服器的期待值說它處理過，紀錄卻不在（資料不一致）——不套用、回成已提交（不帶 seq）
                    audits.Add(new AuditEntry(AuditActors.Device, deviceId, null, AuditActions.SyncResync, null, AuditOutcomes.Error,
                        new { verdict = "stale", deviceSeq = e.DeviceSeq, expected = next, kind = e.Kind }));
                    results.Add(new BatchEventResult(e.DeviceSeq));
                    continue;
            }
        }

        context.Execute("UPDATE device_streams SET next_device_seq = $n WHERE device_id = $d", ("$n", next), ("$d", deviceId));
        context.Execute("RELEASE batch");
        var resyncDone = false;
        if (upstream.ResyncFrom is not null && upstream.HelloOutbox is { } hello && OutboxRules.IsResyncDone(next, hello))
        {
            context.Execute("UPDATE device_streams SET needs_resync = 0 WHERE device_id = $d", ("$d", deviceId));
            resyncDone = true;
        }
        foreach (var entry in audits)
            context.AuditAfterCommit(audit, entry);
        return new BatchOutcome(new BatchAck(batchId, batch.Events[^1].DeviceSeq, results), resyncDone);
    }

    /// <summary>
    /// 拉取（Invoke）：每個游標（類別或串流擇一）各取最多 maxEvents＋1 筆、累計超過位元組上限就停讀（越界的那一筆留著，<see cref="PullRules.CursorDone"/>），
    /// 依 seq 合併後取前綴（筆數與位元組上限、至少一筆，<see cref="PullRules.SelectPrefix"/>）——
    /// 各游標拿到的一定是它待拉事件的前綴，裝置照每筆的 cls／stream 前進游標不會跳過事件。<c>high</c>＝目前最大的 seq。
    /// 已刪除、或目前政策不是 full 的對話不回 item 類事件（<see cref="ConversationPullRules"/>，WP4.2 D17；第 3 階段的類別不受影響，
    /// 過濾在同一個唯讀交易裡做，看到的是同一個 seq 前綴的狀態）。
    /// </summary>
    public PullResponse Pull(PullRequest? request)
    {
        if (request?.Cursors is not { Count: > 0 and <= MaxPullCursors } cursors)
            throw new PullRequestException("cursors 不正確");
        foreach (var cursor in cursors)
        {
            if (cursor is null || cursor.After < 0 || (cursor.Cls is null) == (cursor.Stream is null)
                || (cursor.Cls is not null && !SyncClasses.IsKnown(cursor.Cls))
                || (cursor.Stream is not null && (cursor.Stream.Length is 0 or > MaxStreamLength)))
                throw new PullRequestException("游標格式不正確");
        }
        var maxEvents = Math.Clamp(request.MaxEvents, 1, Limits.MaxPullEvents);
        var maxBytes = Math.Clamp(request.MaxBytes, 1, Limits.MaxPullBytes);

        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        var merged = new SortedDictionary<long, PulledEvent>();
        foreach (var cursor in cursors)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = cursor.Cls is not null
                ? "SELECT seq, stream, cls, author, kind, enc, payload, created_at, conversation_id, epoch FROM events WHERE cls = $v AND seq > $after"
                  + ConversationPullRules.VisibleSql + " ORDER BY seq LIMIT $limit"
                : "SELECT seq, stream, cls, author, kind, enc, payload, created_at, conversation_id, epoch FROM events WHERE stream = $v AND seq > $after"
                  + ConversationPullRules.VisibleSql + " ORDER BY seq LIMIT $limit";
            cmd.Parameters.AddWithValue("$v", cursor.Cls ?? cursor.Stream);
            cmd.Parameters.AddWithValue("$after", cursor.After);
            cmd.Parameters.AddWithValue("$limit", maxEvents + 1);
            using var reader = cmd.ExecuteReader();
            var cursorRows = 0;
            long cursorBytes = 0;
            while (reader.Read())
            {
                var seq = reader.GetInt64(0);
                var payload = reader.GetString(6);
                merged.TryAdd(seq, new PulledEvent(seq, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    payload, reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt64(9)));
                Interlocked.Increment(ref pulledRows);
                // 每個游標的位元組預算（合併前審查 P4SRV-MAT-01）：之後的列不可能被選上，越界的這一列留著（PullRules.CursorDone）。
                // 不然每個游標都把最多 maxEvents＋1 筆完整的 payload 讀進記憶體（大的 conv.item 一個游標就上百 MB）
                cursorRows++;
                cursorBytes += Encoding.UTF8.GetByteCount(payload) + PulledEventOverheadBytes;
                if (PullRules.CursorDone(cursorRows, cursorBytes, maxBytes))
                    break;
            }
        }

        var candidates = merged.Values.ToList();
        var selected = PullRules.SelectPrefix(candidates.Select(e => (long)Encoding.UTF8.GetByteCount(e.Payload) + PulledEventOverheadBytes), maxEvents, maxBytes);
        var events = candidates.GetRange(0, selected);
        long high;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT IFNULL(MAX(seq), 0) FROM events";
            high = (long)cmd.ExecuteScalar()!;
        }
        return new PullResponse(events, merged.Count > events.Count, high);
    }

    /// <summary>
    /// 套用一筆（Next、GapAccepted、Missing）：種類必須是裝置能寫的、串流與種類相符、第 3 階段沒有對話欄位；payload 解碼後雜湊相符；
    /// 再交給 <see cref="Catalog"/> 物化。被擋的寫進 rejected_events（仍佔 deviceSeq）。
    /// 對話種類（有 <see cref="ConversationMaterializer"/> 時）交給 <see cref="ApplyConversation"/>（WP4.2 D3）。
    /// </summary>
    private BatchEventResult Apply(SyncWriteContext context, UpstreamContext upstream, SyncEvent e, ConversationPrecheck? precheck)
    {
        var deviceId = upstream.DeviceId;
        if (conversations is not null && ConversationEventKinds.IsConversationKind(e.Kind))
            return ApplyConversation(context, conversations, upstream, e, precheck ?? ConversationPrecheckRules.Check(e, Limits));
        var placement = SyncEventKinds.Placement(e.Kind);
        if (!SyncEventKinds.IsDeviceAuthored(e.Kind) || placement is not { } where || e.Stream != where.Stream || e.ConversationId is not null || e.Epoch is not null)
            return Reject(context, deviceId, e, SyncErrorCodes.Invalid);
        var check = CatalogRules.CheckPayload(e.Enc, e.Payload, Limits.MaxInlinePayload, out var json);
        if (check != PayloadCheck.Ok)
            return Reject(context, deviceId, e, check == PayloadCheck.TooLarge ? SyncErrorCodes.TooLarge : SyncErrorCodes.Invalid);
        if (PayloadRules.Sha256Hex(json) != e.PayloadSha256)
            return Reject(context, deviceId, e, SyncErrorCodes.Invalid);

        // 事件先寫（目錄發布的伺服器事件 seq 在它之後：因在前、果在後）；物化拒絕時連事件一起回滾、改記成被擋
        var mark = context.AfterCommitMark;
        context.Execute("SAVEPOINT ev");
        var seq = SyncEventStore.InsertDevice(context, notifier, deviceId, e, where.Cls);
        var outcome = catalog.ApplyDeviceEvent(context, deviceId, e.Kind, json);
        if (outcome.Rejected is { } reason)
        {
            context.Execute("ROLLBACK TO ev");
            context.Execute("RELEASE ev");
            context.DiscardAfterCommit(mark);
            return Reject(context, deviceId, e, reason);
        }
        context.Execute("RELEASE ev");
        return new BatchEventResult(e.DeviceSeq, seq);
    }

    private static BatchEventResult Reject(SyncWriteContext context, string deviceId, SyncEvent e, string reason) =>
        Reject(context, deviceId, e, reason, keepPayload: reason != SyncErrorCodes.TooLarge);

    /// <param name="keepPayload">
    /// 存不存原文：第 3 階段的事件除了 too_large（可能接近 1 MB）都留著供鑑識；對話種類只在預先檢查的 DlpClean 為真時才存（WP4.2 D8、提案 C6：
    /// 原文命中權杖——包括遮蔽後接受、又被授權擋下的——解碼失敗、too_large 一律存 null）
    /// </param>
    private static BatchEventResult Reject(SyncWriteContext context, string deviceId, SyncEvent e, string reason, bool keepPayload, long? epoch = null,
        string? owner = null)
    {
        context.Execute(
            "INSERT INTO rejected_events(author, device_seq, payload_sha256, stream, conversation_id, epoch, kind, payload, enc, reason, created_at) "
            + "VALUES($author, $dseq, $sha, $stream, $conv, $epoch, $kind, $payload, $enc, $reason, $now)",
            ("$author", deviceId), ("$dseq", e.DeviceSeq), ("$sha", Convert.FromHexString(e.PayloadSha256)), ("$stream", e.Stream ?? string.Empty),
            ("$conv", e.ConversationId), ("$epoch", e.Epoch), ("$kind", e.Kind ?? string.Empty),
            ("$payload", keepPayload && reason != SyncErrorCodes.TooLarge ? e.Payload : null), ("$enc", e.Enc), ("$reason", reason), ("$now", context.Now));
        return new BatchEventResult(e.DeviceSeq, Rejected: reason, Epoch: epoch, Owner: owner);
    }

    private sealed record ExistingEvent(string Identity, BatchEventResult Result);

    /// <summary>同一個 (author, deviceSeq) 已有的紀錄（events 或 rejected_events）：身分與當時的結果</summary>
    private static ExistingEvent? FindExisting(SyncWriteContext context, string deviceId, long deviceSeq)
    {
        using (var cmd = context.Command("SELECT seq, kind, stream, conversation_id, epoch, payload_sha256 FROM events WHERE author = $a AND device_seq = $s", ("$a", deviceId), ("$s", deviceSeq)))
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
                return new ExistingEvent(Identity(reader, 1), new BatchEventResult(deviceSeq, reader.GetInt64(0)));
        }
        using (var cmd = context.Command("SELECT reason, kind, stream, conversation_id, epoch, payload_sha256 FROM rejected_events WHERE author = $a AND device_seq = $s", ("$a", deviceId), ("$s", deviceSeq)))
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
                return new ExistingEvent(Identity(reader, 1), new BatchEventResult(deviceSeq, Rejected: reader.GetString(0)));
        }
        return null;
    }

    private static string Identity(SqliteDataReader reader, int start) =>
        OutboxRules.EventIdentity(
            reader.GetString(start),
            reader.GetString(start + 1),
            reader.IsDBNull(start + 2) ? null : reader.GetString(start + 2),
            reader.IsDBNull(start + 3) ? null : reader.GetInt64(start + 3),
            Convert.ToHexStringLower((byte[])reader[start + 4]));
}
