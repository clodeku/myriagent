using System.Globalization;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Sync;

// WP4.3 計畫步 12（D10、D12、D22～D24、D36、D38）：重整與回補。
// - 重整：每次啟動、伺服器換實例、對話表升級成功之後的第一條 conv1 連線進 Live 時 RequestMeta(null)：每個對話比對 meta 摘要
//   （未知的先寫 conv.created），清單完整（重整的最後一批）之後才排內容；「已知、中樞沒有」的對話在執行緒集區檢查資料夾，
//   ConfirmedDeleted 才送 conv.deleted（Id 不符、損毀、被占用的都不送）。
// - 佇列（ReconcileQueueRules）：一次一個對話。開始時照「整份重新發布」寫 created／meta、處理政策轉換，full 的再依 Ord 一頁一頁比對項目；
//   沒載入的對話在執行緒集區唯讀讀檔（ConversationReconciler），讀完再問一次中樞（途中被載入了就丟掉檔案的結果、改用記憶體）。
//   每一輪（一頁、或檔案的 ≤500 列／≤2 MB）只在 outbox 沒有未完成的列（一般事件優先，也是回補自己的背壓）、沒有磁碟保護的暫停時才排；
//   本機有對話執行中照常排，不限速。
// - 插隊：SnapshotRequest{open} 排最前面（同一個對話 10 分鐘一次）；{restore} 每個（對話, 伺服器世代）一次，還沒重發的排前面、已重發的只補一筆 conv.created。
// - 開啟時：kv 比對中間跑過舊版（D36）與權杖 DLP 換版（D38），掃保留中的被拒列。
// 狀態只在引擎執行緒改；讀檔的結果回到引擎執行緒時，世代或工作換了（換實例、對話被刪）就丟掉
internal sealed partial class SyncEngine
{
    /// <summary>磁碟保護暫停時多久再檢查一次（順便修剪）</summary>
    internal static readonly TimeSpan HoldRecheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>同一個對話的 SnapshotRequest{open} 最多多久處理一次（D23；WP4.2 送的那邊也是 10 分鐘）</summary>
    internal const long OpenRequestIntervalMs = 10 * 60 * 1000;

    /// <summary>重整的中繼資料多久沒回完，下一次進 Live 時重來（發布者在途中換掉的情況）</summary>
    private const long RefreshTimeoutMs = 60_000;

    /// <summary>一個對話的重整走到哪裡</summary>
    private enum ReconcilePhase
    {
        Starting,

        /// <summary>依 Ord 要了一頁，等中樞回</summary>
        Walking,

        /// <summary>這一頁寫完了，下一頁等下一輪</summary>
        WaitingRound,

        /// <summary>沒載入：執行緒集區唯讀讀檔中</summary>
        Reading,

        /// <summary>讀完了，再問一次中樞是不是還沒載入</summary>
        Confirming,

        /// <summary>照檔案的結果一輪一輪寫</summary>
        Chunks,
    }

    /// <summary>佇列裡正在處理的一個對話</summary>
    private sealed class ReconcileJob(ReconcileCandidate candidate, string local, long generation)
    {
        public ReconcileCandidate Candidate { get; } = candidate;

        public string Wire => Candidate.WireId;

        public string Local { get; } = local;

        /// <summary>開始時的重整世代（換實例時遞增：之後才回來的讀檔結果丟掉）</summary>
        public long Generation { get; } = generation;

        public ReconcilePhase Phase { get; set; } = ReconcilePhase.Starting;

        /// <summary>讀檔途中這個對話有變化（中樞載入了、改了）：檔案的結果作廢、重新排</summary>
        public bool Stale { get; set; }

        /// <summary>途中有被拒的（摘要清掉了）：不記 items.json 的 SHA-256、不清 needs_redo</summary>
        public bool Invalidated { get; set; }

        /// <summary>途中又被要求整份重新發布：做完再排一次</summary>
        public bool RedoAfter { get; set; }

        /// <summary>這一頁的寫入還沒提交（收件匣那一輪的交易；寫入失敗時結束這個工作）</summary>
        public bool AwaitingCommit { get; set; }

        /// <summary>寫進 outbox 的最後一個 deviceSeq（全部確認後才記回補完成）</summary>
        public long LastDeviceSeq { get; set; }

        public ReconcileFileResult? File { get; set; }

        public int FileIndex { get; set; }

        public bool FileStarted { get; set; }
    }

    private readonly Dictionary<string, ReconcileCandidate> reconcileQueue = new(StringComparer.Ordinal);
    private ReconcileJob? activeJob;
    private bool refreshNeeded = true;
    private bool refreshing;
    private long refreshStartedAt;
    private HashSet<string>? refreshIds;
    private long reconcileGeneration;
    private bool pumpPosted;
    private ITimer? holdTimer;
    private BackfillPause? backfillPause;
    private (SyncBackfillProgress? Progress, BackfillPause? Pause) reportedProgress;
    private readonly Dictionary<string, long> openRequestedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> restoreHandled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> reconcileEstimates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> backfillAwaitingAck = new(StringComparer.Ordinal);
    private readonly HashSet<string> probeWarned = new(StringComparer.Ordinal);
    private readonly HashSet<string> removalsDistrustWarned = new(StringComparer.Ordinal);

    /// <summary>測試接縫：讀檔之前先等它（模擬讀檔途中對話被載入、被刪除；正式程式不設）</summary>
    internal Func<string, Task>? FileReadGateForTest { get; set; }

    // ───────────────────────────── 開啟時（D36、D38） ─────────────────────────────

    /// <summary>
    /// 開啟 sync.db 之後、任何寫入之前（Startup）：kv <c>publish_server</c> 與目前的伺服器不同＝中間跑過的舊版處理了換實例，照換實例重設；
    /// <c>NextDeviceSeq</c> 比 kv <c>publisher_next_device_seq</c> 大＝中間跑過舊版、寫過 outbox，所有已知的對話清摘要（保留 Ord）、記 needs_redo；
    /// 保留中的被拒對話列清對應的摘要、記 needs_redo（重複處理無害）；權杖 DLP 換版時清來源摘要，重新投影後只送線上摘要改變的。最後記下三個 kv
    /// </summary>
    private void OnStoreOpenedForConversations()
    {
        if (Conversations is null || store is null || !store.ConversationTablesReady)
            return;
        try
        {
            var serverKey = store.PublishServerKey();
            var storedServer = store.GetKv(SyncStoreSchema.PublishServer);
            long? storedSeq = long.TryParse(store.GetKv(SyncStoreSchema.PublisherNextDeviceSeq), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : null;
            var storedDlp = store.GetKv(SyncStoreSchema.PublisherDlpVersion);
            if (storedServer is not null && storedServer != serverKey)
            {
                Log.Warn("中間跑過的舊版處理了伺服器換實例（發布狀態記的伺服器和目前的不同）：重設對話的發布狀態、之後整份重新發布");
                store.ResetConversationsForServerChange(serverKey);
            }
            else if (storedSeq is { } last && store.NextDeviceSeq > last)
            {
                var marked = store.ForgetPublishDigests(keepWire: false);
                Log.Warn($"中間跑過舊版（next_device_seq {last} → {store.NextDeviceSeq}）：{marked} 個對話清掉發布的摘要（Ord 保留）、記成要重新發布");
            }

            var rejected = outbox.Where(e => e.State == OutboxState.Rejected && e.ConversationId is not null && e.Kind != ConversationEventKinds.Deleted).ToList();
            if (rejected.Count > 0)
            {
                var marked = store.ForgetRejectedDigests(rejected);
                Log.Info($"保留中有 {rejected.Count} 筆被擋下的對話列：{marked} 個對話清掉對應的摘要、記成要重新發布");
            }

            var dlp = DlpVersion.ToString(CultureInfo.InvariantCulture);
            if (storedDlp is not null && storedDlp != dlp)
            {
                var marked = store.ForgetPublishDigests(keepWire: true);
                Log.Info($"權杖 DLP 換版（{storedDlp} → {dlp}）：{marked} 個對話重新投影，只送線上摘要改變的項目");
            }

            store.WritePublisherState(serverKey, DlpVersion);
        }
        catch (Exception ex)
        {
            Log.Error("開啟時比對對話發布者的狀態失敗（這次不偵測舊版與 DLP 換版）", ex);
        }
    }

    // ───────────────────────────── 上線、確認、斷線、換實例（Conversations.cs 呼叫） ─────────────────────────────

    /// <summary>conv1 連線進 Live：需要時開始重整（RequestMeta(null)），再排一輪</summary>
    private void OnOnlineForReconcile()
    {
        if (refreshing && MonoMs() - refreshStartedAt > RefreshTimeoutMs)
        {
            Log.Warn("重整的中繼資料沒有回完：重來一次");
            refreshing = false;
            refreshNeeded = true;
        }

        if (refreshNeeded && !refreshing && Conversations?.UiPort is { } port)
        {
            refreshNeeded = false;
            refreshing = true;
            refreshIds = null;
            refreshStartedAt = MonoMs();
            port.RequestMeta(null);
        }

        RequestPump();
    }

    /// <summary>每個 BatchAck 之後：回補完成的記下來、進度、排下一輪</summary>
    private void AfterAckForReconcile()
    {
        CheckBackfillAcks();
        ReportReconcileProgress();
        RequestPump();
    }

    /// <summary>伺服器換實例、對話表升級成功（發布狀態已重設）：佇列與進行中的工作丟掉，下一次 conv1 連線進 Live 時重整、整份重送</summary>
    private void ResetReconcile()
    {
        reconcileQueue.Clear();
        activeJob = null;
        refreshNeeded = true;
        refreshing = false;
        refreshIds = null;
        reconcileGeneration++;
        openRequestedAt.Clear();
        backfillAwaitingAck.Clear();
        CancelTimer(ref holdTimer);
        ReportReconcileProgress();
    }

    /// <summary>對話被刪掉之後（ForgetConversation）：從佇列拿掉，進行中的工作作廢（之後才回來的讀檔結果丟掉）</summary>
    private void DropReconcile(string wire)
    {
        reconcileQueue.Remove(wire);
        openRequestedAt.Remove(wire);
        backfillAwaitingAck.Remove(wire);
        reconcileEstimates.Remove(wire);
        if (activeJob?.Wire == wire)
        {
            activeJob = null;
            RequestPump();
        }
    }

    /// <summary>不再走這個對話的分頁（封鎖、墓碑、政策降級）：掛在上面的工作跟著結束</summary>
    private void DropWalk(string wire)
    {
        if (walks.Remove(wire, out var walk) && walk.Job is { } job)
            EndJob(job, ok: false);
    }

    /// <summary>這個對話有變化（ProcessGroup）：讀檔中的結果作廢（中樞載入了、改了，D10）</summary>
    private void MarkReconcileStale(string wire)
    {
        if (activeJob is { } job && job.Wire == wire && job.Phase is ReconcilePhase.Reading or ReconcilePhase.Confirming or ReconcilePhase.Chunks)
            job.Stale = true;
    }

    /// <summary>這個對話有被拒的（ForgetRejected 清了摘要）：進行中的工作不記檔案的 SHA-256、不清 needs_redo</summary>
    private void InvalidateReconcile(string wire)
    {
        if (activeJob is { } job && job.Wire == wire)
            job.Invalidated = true;
    }

    /// <summary>寫入失敗（收件匣那一輪）：等提交的工作結束（needs_redo 已記，之後重整補送）</summary>
    private void FailPendingReconcile()
    {
        if (activeJob is not { AwaitingCommit: true } job)
            return;
        if (walks.TryGetValue(job.Wire, out var walk) && ReferenceEquals(walk.Job, job))
            walks.Remove(job.Wire);
        EndJob(job, ok: false);
    }

    // ───────────────────────────── 重整（RequestMeta(null) 的結果，OnMetaBatch 呼叫） ─────────────────────────────

    /// <summary>重整的第一批：完整的對話 id 集合（線上格式）</summary>
    private void NoteRefreshIds(IReadOnlyList<string> all)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in all)
        {
            if (ConversationIds.ToWire(id) is { } wire)
                ids.Add(wire);
        }

        refreshIds = ids;
    }

    /// <summary>重整的一個對話（已比對過 meta、未知的已寫 created）：full 的排進佇列（記了 needs_redo 的、已知的、第一次上傳的）</summary>
    private void ConsiderForReconcile(string wire, PublishMetaSnapshot snapshot, bool wasKnown, ConversationPass pass)
    {
        var row = RowOf(pass, wire);
        if (!row.CreatedWritten || row.DeletedAt is not null || row.Blocked is not null || tombstoning.Contains(wire) || snapshot.IsDraft || snapshot.IsReplica
            || PolicyOf(snapshot) != ConversationSyncPolicies.Full)
            return;
        var reason = NeedsRedo(pass, wire) ? ReconcileReason.Redo : wasKnown ? ReconcileReason.Changed : ReconcileReason.Backfill;
        EnqueueReconcile(new ReconcileCandidate(wire, reason, UpdatedAtMs(snapshot), Estimate(wire)));
    }

    /// <summary>
    /// 重整的清單完整了：可以開始排內容（D24）。「已知、中樞沒有」的對話與佇列裡的對話的 items.json 大小（進度的估計）在執行緒集區查，
    /// 確定刪除的才送 conv.deleted（D12）
    /// </summary>
    private void CompleteRefresh()
    {
        refreshing = false;
        var ids = refreshIds ?? new HashSet<string>(StringComparer.Ordinal);
        refreshIds = null;
        var root = Conversations?.ConversationsRoot;
        var missing = Rows().Values
            .Where(r => MayExistOnServer(r) && r.DeletedAt is null && r.Blocked is null && !tombstoning.Contains(r.ConversationId)
                        && !ids.Contains(r.ConversationId) && !lastMeta.ContainsKey(r.ConversationId))
            .Select(r => r.ConversationId).Order(StringComparer.Ordinal).ToList();
        var sized = reconcileQueue.Keys.Select(w => (Wire: w, Local: localIds.GetValueOrDefault(w, w))).ToList();
        RequestPump();
        if (root is null)
        {
            if (missing.Count > 0)
                Log.Warn($"{missing.Count} 個已發布的對話不在中樞的清單裡，但不知道對話資料夾在哪裡：不送刪除");
            return;
        }

        if (missing.Count == 0 && sized.Count == 0)
            return;
        var generation = reconcileGeneration;
        Spawn(async () =>
        {
            var (probes, sizes) = await Task.Run(() => (
                missing.Select(w => (Wire: w, Probe: ConversationReconciler.ProbeFolder(root, w))).ToList(),
                sized.Select(s => (s.Wire, Bytes: ConversationReconciler.ItemsFileBytes(root, s.Local))).ToList()));
            if (stopping || generation != reconcileGeneration)
                return;
            foreach (var (wire, bytes) in sizes)
                reconcileEstimates[wire] = bytes;
            ApplyDeletionProbes(probes);
            ReportReconcileProgress();
            RequestPump();
        });
    }

    /// <summary>資料夾檢查的結果：確定刪除的送 conv.deleted、記墓碑；其他只記警告（每個對話一次）</summary>
    private void ApplyDeletionProbes(IReadOnlyList<(string Wire, FolderProbe Probe)> probes)
    {
        var pass = new ConversationPass();
        var appends = new List<OutboxAppend>();
        foreach (var (wire, probe) in probes)
        {
            var row = RowOf(pass, wire);
            if (!MayExistOnServer(row) || row.DeletedAt is not null || row.Blocked is not null || lastMeta.ContainsKey(wire) || tombstoning.Contains(wire))
                continue;
            if (!ConversationDeletionRules.ConfirmedDeleted(probe))
            {
                if (probeWarned.Add(wire))
                    Log.Warn($"對話 {wire} 已發布、但不在中樞的清單裡，資料夾還在或讀不到（Id 不符、損毀、被占用）：不送刪除");
                continue;
            }

            Log.Info($"對話 {wire} 已發布、資料夾已不在：送 conv.deleted");
            // 沒有中繼資料可看：取這個對話最後送進 outbox 的 epoch（第 5 階段接手後不是 1；計畫 6），outbox 已修剪掉時才是 1
            Add(pass, appends, projector.Deleted(wire, store?.LastConversationEpoch(wire) ?? ConversationIds.DefaultEpoch));
            pass.Metas[wire] = row with { DeletedAt = NowMs() };
            pass.AfterCommit.Add(() => ForgetConversation(wire));
        }

        WriteConversation(pass, appends);
    }

    /// <summary>有伺服器刪除的旁檔、還沒有墓碑的對話（寫旁檔與寫 outbox 之間當機、或換實例之後）：補送 conv.deleted、記墓碑並封鎖（D22、D25）</summary>
    private void EmitServerDeleted(string wire, PublishMetaSnapshot? meta, ConversationPass pass, List<OutboxAppend> appends)
    {
        var row = RowOf(pass, wire);
        if (row.DeletedAt is not null)
            return;
        var epoch = meta is null ? ConversationIds.DefaultEpoch : ConversationWire.EpochOf(meta.Meta);
        // 有旁檔＝使用者選了不上傳：還沒送出的一律換成 {}（合併前審查 P1；不論種類，第二輪 PRIV-2）
        ScrubPendingContent(wire, pass, appends, allKinds: true);
        Add(pass, appends, projector.Deleted(wire, epoch));
        var now = NowMs();
        pass.Metas[wire] = row with { DeletedAt = now, Blocked = BlockedLocalOnly, BlockedAt = now };
        pass.AfterCommit.Add(() => ForgetConversation(wire));
    }

    // ───────────────────────────── 插隊（SnapshotRequest，D23） ─────────────────────────────

    partial void OnSnapshotRequest(SyncConnection conn, SnapshotRequest request)
    {
        if (Conversations is null || store is null || !store.ConversationTablesReady || !IsCurrent(conn) || request is null)
            return;
        try
        {
            HandleSnapshotRequest(request);
        }
        catch (Exception ex)
        {
            Log.Error("處理伺服器的快照請求失敗", ex);
        }
    }

    /// <summary>
    /// open：照 D21 的政策條件，排到佇列最前面、不清摘要，同一個對話 10 分鐘一次。restore（後備）：每個（對話, 伺服器世代）一次；
    /// 還沒重新發布過的（created 沒寫）排到最前面，已重新發布的只補一筆新的 conv.created（新的 deviceSeq 解除伺服器的 recovering），不清摘要
    /// </summary>
    private void HandleSnapshotRequest(SnapshotRequest request)
    {
        var wire = request.ConversationId;
        if (!ConversationIds.IsWireId(wire))
        {
            Log.Warn("伺服器的快照請求帶了不合格的對話 id：略過");
            return;
        }

        Rows().TryGetValue(wire, out var stored);
        var row = stored ?? new PublishMetaRow(wire);
        lastMeta.TryGetValue(wire, out var meta);
        switch (request.Reason)
        {
            case SnapshotReasons.Open:
            {
                if (meta is not null && !LiveAllowedFor(wire, meta, row))
                {
                    Log.Info($"伺服器要對話 {wire} 的快照（open），但本機的政策不允許：不送");
                    return;
                }

                var now = MonoMs();
                if (openRequestedAt.TryGetValue(wire, out var at) && now - at < OpenRequestIntervalMs)
                    return;
                openRequestedAt[wire] = now;
                EnqueueReconcile(new ReconcileCandidate(wire, ReconcileReason.Requested, UpdatedAtMs(meta), Estimate(wire)));
                return;
            }

            case SnapshotReasons.Restore:
            {
                if (meta is not null && !RestoreAllowed(wire, meta, row))
                {
                    Log.Info($"伺服器要對話 {wire} 的快照（restore），但本機的政策不允許：不送");
                    return;
                }

                if (!restoreHandled.Add(wire + "|" + store!.PublishServerKey()))
                    return;
                if (row.CreatedWritten)
                {
                    if (meta is null)
                    {
                        Log.Info($"伺服器要對話 {wire} 的快照（restore），但這次啟動還沒有它的中繼資料：不補 created");
                        return;
                    }

                    var pass = new ConversationPass();
                    var appends = new List<OutboxAppend>();
                    EmitCreated(wire, meta, pass, appends);
                    WriteConversation(pass, appends);
                    return;
                }

                EnqueueReconcile(new ReconcileCandidate(wire, ReconcileReason.Restore, UpdatedAtMs(meta), Estimate(wire)));
                return;
            }

            default:
                Log.Info($"伺服器的快照請求帶了不認得的原因（{CatalogSyncRules.CleanText(request.Reason, 32)}）：略過");
                return;
        }
    }

    /// <summary>restore 的政策條件（D21，但允許還沒重新發布的）：full、沒有封鎖與墓碑、沒有伺服器刪除的旁檔、不在寫旁檔、不是草稿或複本</summary>
    private bool RestoreAllowed(string wire, PublishMetaSnapshot meta, PublishMetaRow row)
        => !tombstoning.Contains(wire) && !meta.IsDraft && !meta.IsReplica && row.Blocked is null && row.DeletedAt is null
           && PolicyOf(meta) == ConversationSyncPolicies.Full;

    // ───────────────────────────── 佇列與一輪 ─────────────────────────────

    /// <summary>排進佇列（同一個對話只留排最前面的那一筆；正在處理的對話不重排，要整份重新發布的做完再排一次）</summary>
    private void EnqueueReconcile(ReconcileCandidate candidate)
    {
        var wire = candidate.WireId;
        // Grok、Antigravity 的對話而沒有 prov1：不排（記下來，prov1 回來時重整；SyncEngine.Prov1.cs）
        if (DeferredForProv1(wire))
            return;
        if (activeJob is { } job && job.Wire == wire)
        {
            if (candidate.Reason == ReconcileReason.Redo)
                job.RedoAfter = true;
            return;
        }

        if (reconcileQueue.TryGetValue(wire, out var existing) && ReferenceEquals(ReconcileQueueRules.Order([existing, candidate])[0], existing))
            return;
        reconcileQueue[wire] = candidate;
        RequestPump();
    }

    private void RequestPump()
    {
        if (pumpPosted || stopping || Conversations is null)
            return;
        pumpPosted = true;
        thread.Post(PumpReconcile);
    }

    private void PumpReconcile()
    {
        pumpPosted = false;
        if (!opened || stopping || store is null || Conversations is null || !store.ConversationTablesReady)
            return;
        try
        {
            PumpCore();
        }
        catch (Exception ex)
        {
            Log.Error("重整與回補的排程失敗", ex);
        }
    }

    /// <summary>
    /// 排一輪（D24、答 3）：conv1 連線、重整的清單完整之後；磁碟保護（保留的列數、位元組、剩餘空間）暫停時記暫停時間、30 秒後再看；
    /// outbox 還有未完成的列就等（一般事件優先，下一個 BatchAck 再排）。本機有對話執行中不暫停，沒有時間或位元組的預算
    /// </summary>
    private void PumpCore()
    {
        if (current is not { Proven: true } conn || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1) || refreshing)
            return;
        if (activeJob is null && reconcileQueue.Count == 0)
        {
            CancelTimer(ref holdTimer);
            backfillPause = BackfillProgressRules.Advance(backfillPause, BackfillHold.None, MonoMs());
            ReportReconcileProgress();
            return;
        }

        var (rows, bytes) = store!.RetainedConversationStats();
        var free = store.FreeDiskBytes();
        var hold = ReconcileQueueRules.Hold(rows, bytes, free);
        backfillPause = BackfillProgressRules.Advance(backfillPause, hold, MonoMs());
        ReportReconcileProgress();
        if (hold != BackfillHold.None)
        {
            holdTimer ??= CreateTimer(HoldRecheckInterval, OnHoldRecheck);
            return;
        }

        CancelTimer(ref holdTimer);
        if (!ReconcileQueueRules.MayStartRound(CountNotDone(), rows, bytes, free))
            return;
        RunRound();
    }

    private void OnHoldRecheck()
    {
        CancelTimer(ref holdTimer);
        if (!opened || stopping)
            return;
        Prune();
        RequestPump();
    }

    /// <summary>一輪：進行中的對話走下一步（下一頁、檔案的下一段）；沒有就從佇列取下一個（開始時寫的 created、meta 也算一輪）</summary>
    private void RunRound()
    {
        if (activeJob is { } job)
        {
            switch (job.Phase)
            {
                case ReconcilePhase.WaitingRound:
                    RequestNextPage(job);
                    return;
                case ReconcilePhase.Chunks:
                    if (!WriteChunk(job))
                        RequestPump();
                    return;
                default:
                    // 等中樞或讀檔
                    return;
            }
        }

        while (activeJob is null && reconcileQueue.Count > 0 && CountNotDone() == 0)
        {
            var next = ReconcileQueueRules.Order(reconcileQueue.Values)[0];
            reconcileQueue.Remove(next.WireId);
            StartJob(next);
        }

        ReportReconcileProgress();
    }

    /// <summary>
    /// 開始一個對話：跳過墓碑、封鎖、寫旁檔中的（D24），插隊與 restore 再照 D21 的政策檢查；照整份重新發布寫 created／meta、處理政策轉換，
    /// full 的接著依 Ord 走項目（這個工作掛在那次分頁上）
    /// </summary>
    private void StartJob(ReconcileCandidate candidate)
    {
        var wire = candidate.WireId;
        var pass = new ConversationPass();
        var row = RowOf(pass, wire);
        if (row.DeletedAt is not null || row.Blocked is not null || tombstoning.Contains(wire))
            return;
        if (!lastMeta.TryGetValue(wire, out var meta))
        {
            Log.Info($"對話 {wire} 不在中樞的清單裡（或還沒有中繼資料）：這次不重整");
            return;
        }

        if (meta.IsDraft || meta.IsReplica)
            return;
        if ((candidate.Reason == ReconcileReason.Requested && !LiveAllowedFor(wire, meta, row))
            || (candidate.Reason == ReconcileReason.Restore && !RestoreAllowed(wire, meta, row)))
        {
            Log.Info($"對話 {wire} 的快照請求：本機的政策不允許，不送");
            return;
        }

        var job = new ReconcileJob(candidate, localIds.GetValueOrDefault(wire, meta.Meta.Id), reconcileGeneration);
        activeJob = job;
        var appends = new List<OutboxAppend>();
        // 整份重送（佇列快照也在 Publish 裡要，不論原因；第二輪合併前審查 F3）
        Publish(wire, job.Local, meta, [], metaIsNew: true, redo: true, pass, appends);
        var walking = walks.TryGetValue(wire, out var walk);
        if (walking)
        {
            walk!.Job = job;
            job.Phase = ReconcilePhase.Walking;
        }

        if (!WriteJob(job, pass, appends))
        {
            if (walking)
                walks.Remove(wire);
            EndJob(job, ok: false);
            return;
        }

        if (!walking)
            EndJob(job, ok: true);
    }

    /// <summary>分頁的下一頁（這一輪）</summary>
    private void RequestNextPage(ReconcileJob job)
    {
        if (!walks.TryGetValue(job.Wire, out var walk) || !ReferenceEquals(walk.Job, job) || Conversations?.UiPort is not { } port)
        {
            EndJob(job, ok: false);
            return;
        }

        job.Phase = ReconcilePhase.Walking;
        port.RequestItems(walk.Local, walk.AfterOrd, PublishTimingRules.ItemsPage, walk.Hint);
    }

    /// <summary>不在收件匣那一輪的寫入（開始、檔案的一段）：成功時記下這個工作最後的 deviceSeq</summary>
    private bool WriteJob(ReconcileJob job, ConversationPass pass, List<OutboxAppend> appends)
    {
        if (!WriteConversation(pass, appends))
            return false;
        if (appends.Count > 0)
            job.LastDeviceSeq = Math.Max(job.LastDeviceSeq, store!.NextDeviceSeq - 1);
        return true;
    }

    /// <summary>
    /// 分頁的一頁寫進這一輪的收件匣交易（OnItemsPage）：提交之後記最後的 deviceSeq，<paramref name="done"/> 時結束這個工作、否則排下一輪；
    /// 寫入失敗時 <see cref="FailPendingReconcile"/> 結束它
    /// </summary>
    private void NotePageWritten(ReconcileJob job, ConversationPass pass, bool done)
    {
        job.AwaitingCommit = true;
        pass.AfterCommit.Add(() =>
        {
            job.AwaitingCommit = false;
            if (pass.Appends > 0 && store is not null)
                job.LastDeviceSeq = Math.Max(job.LastDeviceSeq, store.NextDeviceSeq - 1);
            if (done)
                EndJob(job, ok: true);
            else
                RequestPump();
        });
    }

    /// <summary>
    /// 結束一個工作：成功的回補（或 restore）等 outbox 裡到它最後一列為止都完成後記 <c>backfill_state.items_done_at</c>；途中又被要求整份重新發布的再排一次
    /// </summary>
    private void EndJob(ReconcileJob job, bool ok)
    {
        if (!ReferenceEquals(activeJob, job))
            return;
        activeJob = null;
        job.AwaitingCommit = false;
        if (ok && IsUpload(job.Candidate.Reason))
            backfillAwaitingAck[job.Wire] = job.LastDeviceSeq;
        else
            reconcileEstimates.Remove(job.Wire);
        if (job.RedoAfter)
            EnqueueReconcile(job.Candidate with { Reason = ReconcileReason.Redo });
        CheckBackfillAcks();
        ReportReconcileProgress();
        RequestPump();
    }

    /// <summary>檔案的結果作廢（讀檔途中有變化）：重新排（下次開始時中樞多半已載入，改走記憶體）</summary>
    private void Requeue(ReconcileJob job)
    {
        if (!ReferenceEquals(activeJob, job))
            return;
        activeJob = null;
        if (walks.TryGetValue(job.Wire, out var walk) && ReferenceEquals(walk.Job, job))
            walks.Remove(job.Wire);
        EnqueueReconcile(job.Candidate);
        RequestPump();
    }

    // ───────────────────────────── 未載入的對話（D10） ─────────────────────────────

    /// <summary>沒載入：在執行緒集區唯讀讀檔（不知道資料夾在哪裡回 false）</summary>
    private bool StartFileRead(ReconcileJob job)
    {
        if (Conversations?.ConversationsRoot is not { } root || store is null)
        {
            Log.Warn($"對話 {job.Wire} 沒載入，但不知道對話資料夾在哪裡：之後重整再試");
            return false;
        }

        var wire = job.Wire;
        var dir = Path.Combine(root, job.Local);
        var known = Rows().TryGetValue(wire, out var row) ? row.ItemsFileSha256 : null;
        var nextOrd = lastMeta.TryGetValue(wire, out var meta) ? meta.Meta.NextOrd : null;
        var hint = store.OrdHintOf(wire);
        var gate = FileReadGateForTest;
        job.Phase = ReconcilePhase.Reading;
        Spawn(async () =>
        {
            if (gate is not null)
                await gate(wire);
            var result = await Task.Run(() => ConversationReconciler.Read(dir, known, nextOrd, hint));
            OnFileRead(job, result);
        });
        return true;
    }

    /// <summary>讀檔的結果（引擎執行緒）：工作換了、世代換了就丟掉；途中有變化就重新排；讀到了再問一次中樞是不是還沒載入</summary>
    private void OnFileRead(ReconcileJob job, ReconcileFileResult result)
    {
        if (stopping || !ReferenceEquals(activeJob, job) || job.Generation != reconcileGeneration)
            return;
        if (job.Stale || walks.ContainsKey(job.Wire))
        {
            Requeue(job);
            return;
        }

        var wire = job.Wire;
        switch (result.Status)
        {
            case ReconcileFileStatus.ServerDeleted:
            {
                // 有旁檔：只補送 conv.deleted，不送內容（D22、D24）
                var pass = new ConversationPass();
                var appends = new List<OutboxAppend>();
                EmitServerDeleted(wire, lastMeta.GetValueOrDefault(wire), pass, appends);
                WriteJob(job, pass, appends);
                EndJob(job, ok: false);
                return;
            }

            case ReconcileFileStatus.Unreadable:
            case ReconcileFileStatus.Corrupt:
            {
                var pass = new ConversationPass();
                pass.Redo[wire] = true;
                FlushPass(pass);
                EndJob(job, ok: false);
                return;
            }

            case ReconcileFileStatus.Unchanged:
            {
                // 和上次重整時的檔案相同：不投影（摘要被清過的話 SHA-256 也一起清了）
                if (!job.Invalidated)
                {
                    var pass = new ConversationPass();
                    pass.Redo[wire] = false;
                    FlushPass(pass);
                }

                EndJob(job, ok: !job.Invalidated);
                return;
            }

            case ReconcileFileStatus.Ok:
            {
                if (Conversations?.UiPort is not { } port || store is null)
                {
                    EndJob(job, ok: false);
                    return;
                }

                job.File = result;
                job.FileIndex = 0;
                job.Phase = ReconcilePhase.Confirming;
                var hint = store.OrdHintOf(wire);
                walks[wire] = new PageWalk(job.Local, hint, clearRedo: true) { Job = job };
                port.RequestItems(job.Local, 0, PublishTimingRules.ItemsPage, hint);
                return;
            }

            default:
                throw new InvalidOperationException("不認得的讀檔結果");
        }
    }

    /// <summary>讀完檔再問一次中樞：還是沒載入，照檔案的結果一輪一輪寫</summary>
    private void OnUnloadedConfirmed(ReconcileJob job)
    {
        job.Phase = ReconcilePhase.Chunks;
        RequestPump();
    }

    /// <summary>
    /// 檔案的一段（≤500 列、≤2 MB）：第一段先把這次才配的 Ord 全部記進 published_items（摘要是 null）、送 itemRemoved（published_items 有、
    /// 檔案的完整 id 集合裡沒有的）；項目照摘要比對。最後一段記 items.json 的 SHA-256、清 needs_redo。回傳這一段有沒有寫列
    /// </summary>
    private bool WriteChunk(ReconcileJob job)
    {
        if (job.Stale)
        {
            Requeue(job);
            return false;
        }

        var wire = job.Wire;
        var file = job.File!;
        var pass = new ConversationPass();
        var appends = new List<OutboxAppend>();
        var row = RowOf(pass, wire);
        if (!lastMeta.TryGetValue(wire, out var meta) || !PublishesItems(wire, meta, row))
        {
            EndJob(job, ok: false);
            return false;
        }

        var published = store!.LoadPublishedItems(wire).ToDictionary(r => r.ItemId, StringComparer.Ordinal);
        var epoch = ConversationWire.EpochOf(meta.Meta);
        if (!job.FileStarted)
        {
            job.FileStarted = true;
            foreach (var (id, ord) in file.NewOrds)
            {
                if (!published.ContainsKey(id))
                    pass.Items.Add(new PublishRecord(wire, id, ord, null, null, false));
            }

            var present = new HashSet<string>(file.AllIds, StringComparer.Ordinal);
            if (RemovalFloor(wire, file.RemovalsTrusted, published.Values, present) is { } floor)
                EmitRemovedItems(wire, epoch, published.Values, present, floor, pass, appends);
        }

        long bytes = 0;
        while (job.FileIndex < file.Items.Count && appends.Count < ReconcileQueueRules.RoundMaxRows && bytes < ReconcileQueueRules.RoundMaxBytes)
            EmitReconcileItem(wire, epoch, file.Items[job.FileIndex++], file.Scrubbed, published, pass, appends, ref bytes);

        var done = job.FileIndex >= file.Items.Count;
        if (done && !job.Invalidated)
        {
            pass.Metas[wire] = row with { ItemsFileSha256 = file.ItemsSha256 };
            pass.Redo[wire] = false;
        }

        if (!WriteJob(job, pass, appends))
        {
            EndJob(job, ok: false);
            return false;
        }

        if (done)
            EndJob(job, ok: !job.Invalidated);
        return appends.Count > 0;
    }

    // ───────────────────────────── 項目的比對（分頁與檔案共用） ─────────────────────────────

    /// <summary>
    /// 第一頁或檔案的完整 id 集合可以拿來推斷哪些刪除：回傳 Ord 的下限（不含；<see cref="long.MinValue"/>＝全部），null＝不推斷。
    /// 項目檔損毀過或不在時（合併前審查 F2）清單可能少了損毀之前的項目，不能讓伺服器與其他電腦跟著刪掉；但損毀之後的項目都以 meta 的 NextOrd
    /// 接著配，Ord 一定比損毀時遺失的大，所以只對 Ord 比「目前清單裡已發布的最小 Ord」大的推斷——沒有 conv1 的期間被移除、被擋下的 itemRemoved
    /// 只能靠重整補送（第二輪合併前審查 F2-untrusted）。清單裡沒有已發布的項目時不推斷。不信任時每個對話只記一次警告
    /// </summary>
    private long? RemovalFloor(string wire, bool trusted, IEnumerable<PublishedItemRow> published, IReadOnlySet<string> present)
    {
        if (trusted)
            return long.MinValue;
        if (removalsDistrustWarned.Add(wire))
            Log.Warn($"對話 {wire} 的項目檔損毀過或不在：只對損毀之後才有的項目推斷刪除（Ord 比目前清單裡已發布的最小 Ord 大），其他項目照常比對、發布");
        return published.Where(r => !r.Removed && present.Contains(r.ItemId)).Select(r => (long?)r.Ord).Min();
    }

    /// <summary>
    /// published_items 有、完整的 id 集合裡沒有（而且還沒送過 itemRemoved）、Ord 比 <paramref name="floor"/> 大的送 conv.itemRemoved
    /// （D12；只看第一頁或檔案的完整集合；下限見 <see cref="RemovalFloor"/>）
    /// </summary>
    private void EmitRemovedItems(string wire, long epoch, IEnumerable<PublishedItemRow> published, IReadOnlySet<string> present, long floor, ConversationPass pass,
        List<OutboxAppend> appends)
    {
        foreach (var row in published.OrderBy(r => r.Ord))
        {
            if (row.Removed || row.Ord <= floor || present.Contains(row.ItemId))
                continue;
            DropRunning(wire, row.ItemId);
            if (!ConversationIds.IsItemId(row.ItemId))
                continue;
            Add(pass, appends, projector.ItemRemoved(wire, epoch, row.ItemId));
            pass.Items.Add(new PublishRecord(wire, row.ItemId, null, null, null, false, Removed: true));
        }
    }

    /// <summary>
    /// 一筆項目（分頁快照或讀檔；秘密回答已對整份清單清除）：來源摘要相同的不重送；不同的投影（截斷、遮蔽），線上摘要和送過的相同
    /// （例如 DLP 換版之後重新投影結果沒變）只記新的來源摘要、不寫列，否則寫一筆持久的 conv.item
    /// </summary>
    private void EmitReconcileItem(string wire, long epoch, AgentChatItem? item, IReadOnlyDictionary<string, string> scrubbed,
        Dictionary<string, PublishedItemRow> published, ConversationPass pass, List<OutboxAppend> appends, ref long bytes)
    {
        if (item?.Id is null || item.Ord is null)
            return;
        published.TryGetValue(item.Id, out var known);
        if (known is { Removed: false } && known.SrcSha256 == PublishDigestRules.Source(item) && !(projector.MediaHook?.RefsMissing(wire, item) ?? false))
            return;
        if (projector.Item(wire, epoch, item, scrubbed, ++nextVer) is not { } projected)
            return;
        pass.Items.Add(projected.Record);
        if (known is { Removed: false, WireSha256: { } wireSha } && wireSha == projected.Record.WireSha256)
            return;
        Add(pass, appends, projected.Append);
        NoteDurable(wire, item, fromWalk: true, pass);
        bytes += Encoding.UTF8.GetByteCount(projected.Append.Json);
    }

    // ───────────────────────────── 回補完成與進度 ─────────────────────────────

    /// <summary>回補完成的對話：outbox 裡到它最後一列為止都完成了（確認或被擋）才記 <c>backfill_state.items_done_at</c></summary>
    private void CheckBackfillAcks()
    {
        if (backfillAwaitingAck.Count == 0 || store is null)
            return;
        var first = FirstActive();
        var firstSeq = first < outbox.Count ? outbox[first].DeviceSeq : long.MaxValue;
        var now = NowMs();
        foreach (var (wire, seq) in backfillAwaitingAck.Where(p => p.Value < firstSeq).ToList())
        {
            backfillAwaitingAck.Remove(wire);
            reconcileEstimates.Remove(wire);
            try
            {
                store.MarkBackfilled(wire, now, 0);
            }
            catch (Exception ex)
            {
                Log.Warn($"記錄對話 {wire} 回補完成失敗：{ex.Message}");
            }
        }
    }

    /// <summary>回補的進度（剩幾個要上傳的對話、約多少位元組）與暫停時間，變了才交給 UI（WP4.6 顯示；都完成是 null）</summary>
    private void ReportReconcileProgress()
    {
        if (Conversations?.UiPort is not { } port)
            return;
        var left = 0;
        long bytes = 0;
        foreach (var candidate in reconcileQueue.Values)
        {
            if (IsUpload(candidate.Reason))
            {
                left++;
                bytes += Estimate(candidate.WireId);
            }
        }

        if (activeJob is { } job && IsUpload(job.Candidate.Reason))
        {
            left++;
            bytes += Estimate(job.Wire);
        }

        foreach (var wire in backfillAwaitingAck.Keys)
        {
            left++;
            bytes += Estimate(wire);
        }

        var progress = BackfillProgressRules.Progress(left, bytes);
        if (Equals(reportedProgress.Progress, progress) && Equals(reportedProgress.Pause, backfillPause))
            return;
        reportedProgress = (progress, backfillPause);
        port.ReportProgress(progress, backfillPause);
    }

    /// <summary>要上傳內容的（第一次回補、還原後還沒重發的）才算進回補的進度</summary>
    private static bool IsUpload(ReconcileReason reason) => reason is ReconcileReason.Backfill or ReconcileReason.Restore;

    private long Estimate(string wire) => reconcileEstimates.GetValueOrDefault(wire);

    /// <summary>佇列排序用的最後更新時間（只用來排由新到舊，不跨電腦比較）</summary>
    private static long UpdatedAtMs(PublishMetaSnapshot? meta)
    {
        if (meta is null)
            return 0;
        var at = meta.Meta.UpdatedAt;
        return at <= DateTime.MinValue.AddDays(1) ? 0 : new DateTimeOffset(at.ToUniversalTime()).ToUnixTimeMilliseconds();
    }
}
