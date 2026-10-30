namespace Myriagent.Sync;

/// <summary>outbox 一列的狀態（桌面 sync.db outbox.state）</summary>
internal enum OutboxState
{
    /// <summary>還沒送過：同一個合併鍵的新內容可以原地取代（保留 deviceSeq）</summary>
    Pending,

    /// <summary>送過但要重送（gap、重連後伺服器要從它開始、還原後的對帳）：內容已凍結，不能再合併</summary>
    Retry,

    /// <summary>在途（批次已送出、還沒收到 BatchAck）</summary>
    Sent,

    /// <summary>伺服器已提交（fsync）；保留 24 小時供伺服器還原後重送</summary>
    Acked,

    /// <summary>伺服器已記下但不套用（仍佔 deviceSeq）；同樣保留 24 小時</summary>
    Rejected,
}

/// <summary>outbox 的一列（持久事件；deviceSeq 從 1 起無間隙）</summary>
internal sealed record OutboxEntry(
    long DeviceSeq,
    string Stream,
    string Kind,
    string Enc,
    string Payload,
    string PayloadSha256,
    string? CoalesceKey,
    OutboxState State,
    long CreatedAt,
    long? ServerSeq = null,
    string? RejectReason = null,
    long? AckedAt = null,
    string? ConversationId = null,
    long? Epoch = null)
{
    public SyncEvent ToEvent() => new(DeviceSeq, Stream, Kind, Enc, PayloadSha256, Payload, ConversationId, Epoch);

    public bool IsDone => State is OutboxState.Acked or OutboxState.Rejected;
}

/// <summary>套用 BatchAck 或 Prove 結果之後要怎麼做</summary>
internal enum OutboxVerdict
{
    Continue,

    /// <summary>裝置的 sync.db 倒退了（被還原或複製到別台）：停止同步、提示重新配對成新的裝置身分（設計 10.3）</summary>
    StopSeqRegression,

    /// <summary>伺服器缺了這台已經刪掉的事件、又不在對帳模式：停止同步並告警（不可能自己補齊）</summary>
    StopServerBehind,

    /// <summary>批次被伺服器整批判成格式錯誤（invalid／too_large）：這是用戶端的錯，停止同步並告警（重送只會一直被拒）</summary>
    StopInvalidBatch,

    /// <summary>不認得的整批錯誤（新版伺服器的代碼）或 gap 沒帶 expected：在途的列改回重送，斷線後照退避重連，由 Prove 重新對齊</summary>
    Reconnect,
}

/// <summary>伺服器端對一筆上行事件的判斷（設計 4.3「伺服器規則」、10.2）</summary>
internal enum IncomingEventKind
{
    /// <summary>deviceSeq＝下一個期待的：驗證後配 seq</summary>
    Next,

    /// <summary>已經有同一筆、內容相同：回原本的結果（冪等）</summary>
    Duplicate,

    /// <summary>已經有同一個 deviceSeq、內容不同：裝置的 sync.db 倒退了</summary>
    Regression,

    /// <summary>比期待的大：整批回 gap，裝置從期待的重送</summary>
    Gap,

    /// <summary>
    /// 對帳模式下比期待的大、期待值還在這台保留窗的起點（<see cref="OutboxRules.ResyncFrom"/>）之前、這筆在起點之後：
    /// 備份比這台的保留窗還舊，中間的缺口補不回來（設計 10.2 第 2 步：這個模式下接受）。記稽核、把期待值拉到它，再當成 <see cref="Next"/> 處理；
    /// 缺的內容由本機狀態的重送補齊。接受一次之後期待值就越過起點，之後的跳號照樣是 <see cref="Gap"/>
    /// </summary>
    GapAccepted,

    /// <summary>對帳模式下比期待的小、伺服器卻沒有紀錄（備份裡沒有這筆）：當成新事件套用並記稽核（唯一索引保證不會重複）</summary>
    Missing,

    /// <summary>
    /// 不在對帳模式、比期待的小、伺服器卻沒有紀錄：伺服器的期待值說它處理過了，紀錄卻不在（資料不一致）。
    /// 不套用（避免重複套用舊事件）、回成已提交（不帶 seq）並記稽核
    /// </summary>
    Stale,
}

/// <summary>
/// outbox 的純函式規則（設計 3.6、4.3、10.2；裝置的 SyncStore 與伺服器的 EventLog 共用判斷）：
/// <list type="bullet">
/// <item>deviceSeq 從 1 起無間隙遞增，先寫進 outbox 才送；批次一律是連續的一段，從伺服器期待的那一筆開始。</item>
/// <item>合併只發生在還沒送過的列（<see cref="OutboxState.Pending"/>）：同一個合併鍵的新內容原地取代、保留 deviceSeq。
/// 送過一次的列內容就凍結——舊連線上的批次可能在重連之後才被提交，改了內容再重送，伺服器會判成倒退而停止同步。
/// 所以 <see cref="NextBatch"/> 選出的列要先以 <see cref="MarkSent"/> 寫回 sync.db（同一個交易），之後才呼叫 PublishBatch。</item>
/// <item>最多 <see cref="SyncLimits.MaxInFlightBatches"/> 批在途；收到確認才標成已提交，已提交與被拒的列保留 24 小時再刪（只刪最前面連續的一段）。</item>
/// </list>
/// </summary>
internal static class OutboxRules
{
    public const long RetentionMs = 24L * 60 * 60 * 1000;

    /// <summary>批次外層（batchId、欄位名稱、SignalR 框架）預留的位元組：批次上限一律不超過單則訊息上限減掉它</summary>
    public const int BatchEnvelopeReserveBytes = 64 * 1024;

    /// <summary>新事件可以合併進這一列嗎（同一個合併鍵、還沒送過）</summary>
    public static bool CanCoalesceInto(OutboxEntry existing) => existing.State == OutboxState.Pending && existing.CoalesceKey is not null;

    /// <summary>
    /// 下一批要送的列：從第一個待送（Pending／Retry）的列起，連續、都待送，數量與位元組不超過上限；在途批次已達上限就回空。
    /// 待送的第一列前面若還有在途的列，接在它們後面送（伺服器依到達順序處理同一連線的呼叫）。
    /// 位元組上限取伺服器給的與單則訊息上限（<see cref="SyncProtocol.DeviceHubMaxMessageBytes"/>）的較小者——超過訊息上限伺服器會關掉整條連線。
    /// </summary>
    public static IReadOnlyList<OutboxEntry> NextBatch(IReadOnlyList<OutboxEntry> ordered, int inFlightBatches, SyncLimits limits)
    {
        if (inFlightBatches >= limits.MaxInFlightBatches)
            return [];
        var start = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].State is OutboxState.Pending or OutboxState.Retry)
            {
                start = i;
                break;
            }
        }

        if (start < 0)
            return [];
        var maxBytes = Math.Min(limits.MaxBatchBytes, SyncProtocol.DeviceHubMaxMessageBytes - BatchEnvelopeReserveBytes);
        var batch = new List<OutboxEntry>();
        var bytes = 0;
        for (var i = start; i < ordered.Count && batch.Count < limits.MaxBatchEvents; i++)
        {
            var entry = ordered[i];
            if (entry.State is not (OutboxState.Pending or OutboxState.Retry))
                break;
            if (batch.Count > 0 && entry.DeviceSeq != batch[^1].DeviceSeq + 1)
                break;
            var size = PayloadRules.WireSize(entry.Payload);
            if (batch.Count > 0 && bytes + size > maxBytes)
                break;
            batch.Add(entry);
            bytes += size;
        }

        return batch;
    }

    /// <summary>
    /// 選進批次的列改成在途（內容從此凍結）。必須在呼叫 PublishBatch **之前**寫回 sync.db：
    /// 送出後才寫的話，送出與寫入之間被合併進來的新內容不是遺失（之後收到確認），就是被判成倒退（重連後合併過的內容重送）
    /// </summary>
    public static OutboxEntry MarkSent(OutboxEntry entry) => entry with { State = OutboxState.Sent };

    /// <summary>
    /// 套用 BatchAck：逐筆標成已提交（帶伺服器 seq）或被拒；整批錯誤時——gap：期待的那筆以前都已提交、從它起重送（同 <see cref="ApplyProve"/>
    /// 的一般模式）；seq_regression：停止同步；invalid／too_large：停止同步並告警；其他（不認得的代碼、gap 沒帶 expected）：在途的改回重送、重連。
    /// results 裡重複的 deviceSeq 只取第一筆。
    /// </summary>
    public static (IReadOnlyList<OutboxEntry> Updated, OutboxVerdict Verdict) ApplyAck(IReadOnlyList<OutboxEntry> ordered, BatchAck ack, long now)
    {
        if (ack.Error is { } error)
        {
            return error.Code switch
            {
                SyncErrorCodes.Gap when error.Expected is { } expected => Rewind(ordered, expected, ordered.Count > 0 ? ordered[0].DeviceSeq : expected, now),
                SyncErrorCodes.SeqRegression => (ordered, OutboxVerdict.StopSeqRegression),
                SyncErrorCodes.Invalid or SyncErrorCodes.TooLarge => (ordered, OutboxVerdict.StopInvalidBatch),
                _ => (ordered.Select(e => e.State == OutboxState.Sent ? e with { State = OutboxState.Retry } : e).ToList(), OutboxVerdict.Reconnect),
            };
        }

        var results = new Dictionary<long, BatchEventResult>();
        foreach (var result in ack.Results ?? [])
        {
            if (result is not null)
                results.TryAdd(result.DeviceSeq, result);
        }

        var updated = ordered.Select(entry =>
        {
            if (!results.TryGetValue(entry.DeviceSeq, out var result) || entry.IsDone)
                return entry;
            return result.Rejected is { } reason
                ? entry with { State = OutboxState.Rejected, RejectReason = reason, ServerSeq = null, AckedAt = now }
                : entry with { State = OutboxState.Acked, ServerSeq = result.Seq, RejectReason = null, AckedAt = now };
        }).ToList();
        return (updated, OutboxVerdict.Continue);
    }

    /// <summary>
    /// 套用 Prove 的結果（每次 Hello 都做，不綁在 SignalR 的 Reconnected 上）：
    /// <list type="bullet">
    /// <item>伺服器期待的比這台發過的還大，或期待的那筆以前還有沒送過的列＝這台的 sync.db 倒退了。</item>
    /// <item>一般模式（<see cref="Rewind"/>）：期待的那筆以前都已提交；從它起全部重送——包括已標成提交、伺服器卻沒有的（內容已凍結，重送不會被判倒退）。
    /// 期待的比這台保留的最小 deviceSeq 還小＝伺服器缺了這台已刪掉的事件，補不回來。</item>
    /// <item>對帳模式（伺服器還原後，<paramref name="resync"/>）：保留的列（含已提交與被拒的）全部重送，已在伺服器的因唯一索引而冪等；
    /// 備份比保留窗還舊時伺服器在第一筆接受缺口（<see cref="IncomingEventKind.GapAccepted"/>）。</item>
    /// </list>
    /// </summary>
    public static (IReadOnlyList<OutboxEntry> Updated, OutboxVerdict Verdict) ApplyProve(
        IReadOnlyList<OutboxEntry> ordered, long nextDeviceSeqExpected, long nextDeviceSeq, bool resync, long now)
    {
        if (nextDeviceSeqExpected > nextDeviceSeq)
            return (ordered, OutboxVerdict.StopSeqRegression);
        if (resync)
            return (ordered.Select(e => e.State == OutboxState.Pending ? e : Resend(e)).ToList(), OutboxVerdict.Continue);
        return Rewind(ordered, nextDeviceSeqExpected, ordered.Count > 0 ? ordered[0].DeviceSeq : nextDeviceSeq, now);
    }

    /// <summary>可以刪掉的列：最前面連續一段「已提交或被拒滿 24 小時」的列（中間有未完成的就停，保留窗一律連續）</summary>
    public static IReadOnlyList<long> Prunable(IReadOnlyList<OutboxEntry> ordered, long now)
    {
        var prunable = new List<long>();
        foreach (var entry in ordered)
        {
            if (!entry.IsDone || entry.AckedAt is not { } at || now - at < RetentionMs)
                break;
            prunable.Add(entry.DeviceSeq);
        }

        return prunable;
    }

    /// <summary>Hello 要帶的 outbox 位置</summary>
    public static OutboxPosition Position(IReadOnlyList<OutboxEntry> ordered, long nextDeviceSeq) =>
        new(
            nextDeviceSeq,
            ordered.FirstOrDefault(e => !e.IsDone)?.DeviceSeq,
            ordered.Count > 0 ? ordered[0].DeviceSeq : null);

    /// <summary>
    /// 伺服器：批次結構檢查（不合格整批回 invalid，不佔 deviceSeq）——至少一筆、deviceSeq 連續遞增、筆數不超過上限、
    /// 每筆的 payloadSha256 格式正確。位元組上限由 SignalR 的訊息上限把關。
    /// </summary>
    public static string? CheckBatchShape(Batch? batch, SyncLimits limits)
    {
        if (batch is null || batch.Events is null || batch.Events.Count == 0 || string.IsNullOrEmpty(batch.BatchId))
            return SyncErrorCodes.Invalid;
        if (batch.Events.Count > limits.MaxBatchEvents)
            return SyncErrorCodes.TooLarge;
        for (var i = 0; i < batch.Events.Count; i++)
        {
            var e = batch.Events[i];
            if (e is null || e.DeviceSeq < 1 || (i > 0 && e.DeviceSeq != batch.Events[i - 1].DeviceSeq + 1) || !PayloadRules.IsSha256Hex(e.PayloadSha256))
                return SyncErrorCodes.Invalid;
        }

        return null;
    }

    /// <summary>
    /// 一筆事件的身分（冪等判斷用）：種類、串流、對話、epoch 與 payload 雜湊都相同才是「同一筆」——只比 payload 的話，
    /// 倒退的裝置用同一個 deviceSeq 發出種類不同、內容碰巧相同的事件會被當成已提交而遺失。伺服器以存下的欄位算既有的那筆。
    /// </summary>
    public static string EventIdentity(string kind, string stream, string? conversationId, long? epoch, string payloadSha256) =>
        string.Join('\n', kind, stream, conversationId ?? string.Empty, epoch?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, payloadSha256);

    public static string EventIdentity(SyncEvent e) => EventIdentity(e.Kind, e.Stream, e.ConversationId, e.Epoch, e.PayloadSha256);

    /// <summary>
    /// 伺服器：一筆上行事件該怎麼處理。<paramref name="existingIdentity"/>＝events 或 rejected_events 裡同 (author, deviceSeq) 那筆的
    /// <see cref="EventIdentity(string, string, string?, long?, string)"/>，沒有為 null；<paramref name="resyncFrom"/>＝伺服器對這台在對帳模式時，
    /// 這次連線的 <see cref="ResyncFrom"/>，不在對帳模式為 null。
    /// </summary>
    public static IncomingEventKind ClassifyIncoming(long nextDeviceSeq, long deviceSeq, string? existingIdentity, string incomingIdentity, long? resyncFrom = null)
    {
        if (existingIdentity is not null)
            return string.Equals(existingIdentity, incomingIdentity, StringComparison.Ordinal) ? IncomingEventKind.Duplicate : IncomingEventKind.Regression;
        if (deviceSeq == nextDeviceSeq)
            return IncomingEventKind.Next;
        if (deviceSeq > nextDeviceSeq)
        {
            // 「≥ 起點」而不是「＝ 起點」：Hello 之後、Prove 之前刪掉的已提交列（Prunable）讓第一筆落在起點之後，也是補不回來的缺口
            return resyncFrom is { } from && nextDeviceSeq <= from && deviceSeq >= from ? IncomingEventKind.GapAccepted : IncomingEventKind.Gap;
        }

        return resyncFrom is not null ? IncomingEventKind.Missing : IncomingEventKind.Stale;
    }

    /// <summary>
    /// 伺服器：對帳模式下這次連線的保留窗起點＝Hello 的 oldestRetainedDeviceSeq；outbox 是空的（閒置超過保留期全刪光）時是 nextDeviceSeq——
    /// 之後重送的本機狀態是新的 deviceSeq，從那裡接受缺口
    /// </summary>
    public static long ResyncFrom(OutboxPosition hello) => hello.OldestRetainedDeviceSeq ?? hello.NextDeviceSeq;

    /// <summary>
    /// 伺服器：可以解除對帳模式了嗎——這台在 Hello 當下有的列都已提交（期待值追上 Hello 的 nextDeviceSeq）。
    /// 不能用「保留的列都提交了」判斷：outbox 是空的時候一開始就成立，之後重送的新事件會被當成 <see cref="IncomingEventKind.Gap"/>
    /// </summary>
    public static bool IsResyncDone(long nextDeviceSeq, OutboxPosition hello) => nextDeviceSeq >= hello.NextDeviceSeq;

    /// <summary>
    /// 從伺服器期待的那筆起重來（一般模式的 Prove 與 gap）：
    /// 之前的在途與重送列都已提交；之前還有 Pending＝伺服器提交了這台從沒送出的序號（MarkSent 在送出前就寫回，Pending 不可能送過），
    /// 也就是 sync.db 倒退了——伺服器存的是倒退前的舊內容，當成已提交會讓新內容悄悄遺失，所以停止。
    /// 從它起：在途的改重送；已提交或被拒的也改重送（伺服器說它沒有，內容凍結、重送安全）；Pending 照舊。
    /// </summary>
    private static (IReadOnlyList<OutboxEntry> Updated, OutboxVerdict Verdict) Rewind(IReadOnlyList<OutboxEntry> ordered, long expected, long oldestRetained, long now)
    {
        if (expected < oldestRetained)
            return (ordered, OutboxVerdict.StopServerBehind);
        if (ordered.Any(e => e.DeviceSeq < expected && e.State == OutboxState.Pending))
            return (ordered, OutboxVerdict.StopSeqRegression);
        var updated = ordered.Select(e =>
        {
            if (e.DeviceSeq < expected)
                return e.IsDone ? e : e with { State = OutboxState.Acked, ServerSeq = null, AckedAt = now };
            return e.State == OutboxState.Pending ? e : Resend(e);
        }).ToList();
        return (updated, OutboxVerdict.Continue);
    }

    private static OutboxEntry Resend(OutboxEntry e) =>
        e.State == OutboxState.Retry ? e : e with { State = OutboxState.Retry, ServerSeq = null, RejectReason = null, AckedAt = null };
}
