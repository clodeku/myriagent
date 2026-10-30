using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;

namespace Myriagent.Sync;

// WP4.3 的對話發布（計畫步 10；D3、D5～D7、D11、D13、D14、D20、D25、D38）：盒子裡的變化 → 發布計畫（PublishPlanRules）→ 投影（ConversationProjector）→
// outbox（發布狀態與列同一個交易）；BatchAck 的拒絕碼、政策轉換與伺服器刪除、心跳、沒有 conv1 時把待送的對話列換成 {}。
// 狀態只在引擎執行緒改、不加鎖；要 UI 做的事一律經盒子的 UiPort（它自己排回 UI 執行緒，結果以快照回應放回盒子）。
// 沒有盒子（WP4.1 的契約測試、測試的引擎）時每個入口直接返回；v3 的表不在時只做不需要它們的事（D3）。
// 執行中項目的持久時機、即時更新與觀看集合（步 11）在 SyncEngine.ConversationLive.cs
internal sealed partial class SyncEngine
{
    /// <summary>心跳的執行中、等待中各最多幾個對話（F29；超過時留最近更新的，D20）</summary>
    internal const int MaxHeartbeatConversations = 1_000;

    /// <summary>封鎖的原因：改成只在這台電腦（含換到不進同步的代理種類），伺服器上已刪除（D25）</summary>
    internal const string BlockedLocalOnly = "localOnly";

    /// <summary>封鎖的原因：補送 conv.created 的次數用完（D14，併進 not_owner 的彙總提示）</summary>
    internal const string BlockedCreatedRetries = "created_retries";

    /// <summary>程式錯誤的封鎖原因的前綴（後面接 app 版本：換版之後在下一次 conv1 連線解除，D14）</summary>
    internal const string BlockedBugPrefix = "bug:";

    /// <summary>彙總提示等多久（同一段時間被擋的對話併成一則，D14、D32）</summary>
    internal static readonly TimeSpan PublishNoticeDelay = TimeSpan.FromSeconds(2);

    /// <summary>要過的中繼資料多久沒回就當成沒有（對話不見了），下一次有變化時重要</summary>
    private const long MetaRequestTimeoutMs = 60_000;

    /// <summary>開啟時升級失敗的 sync.db，上線時最多每 10 分鐘再試一次（步 8 的可重試狀態）</summary>
    private const long UpgradeRetryIntervalMs = 10 * 60 * 1000;

    private static readonly string EmptyConversationSha = PayloadRules.Sha256Hex(SyncStore.EmptyConversationPayload);

    /// <summary>
    /// 空佇列的 conv.queue 摘要：伺服器上確定沒有佇列時記這個（第一次建立、改回 full 時降級清掉了），空的不必送；摘要是 null＝伺服器上的佇列不明
    /// （換實例或還原、被擋下、開啟時清掉），空的也送一次蓋掉可能留著的舊佇列（第二輪合併前審查 F3）
    /// </summary>
    private static readonly string EmptyQueueSha =
        PublishDigestRules.Payload(ConversationEventKinds.Queue, ConversationWire.Serialize(ConversationWire.Queue(Array.Empty<(string Id, string DisplayText)>())));

    /// <summary>
    /// 宣告 conv1 時比對的權杖 DLP 版本（D38；正式是 <see cref="CredentialLeakRules.Version"/>）。它是常數、測試改不了，測試在啟動前以這裡換掉
    /// </summary>
    internal int DlpVersion { get; set; } = CredentialLeakRules.Version;

    private ConversationProjector? projectorInstance;

    /// <summary>投影器（第一次用時建：媒體參照的接縫要用引擎的 sync.db，欄位初始化時拿不到 this，WP4.7）</summary>
    private ConversationProjector projector => projectorInstance ??= new ConversationProjector(CreateItemMediaHook());

    // 發布狀態（publish_meta）與 needs_redo 的快取：第一次用時讀；提交之後才更新；伺服器換實例、升級成功時丟掉重讀
    private Dictionary<string, PublishMetaRow>? publishRows;
    private HashSet<string>? redoSet;

    /// <summary>kv conv_armed（上一次進 Live 的連線有沒有 conv1；離線時看它，預設 false，D13）</summary>
    private bool? convArmed;

    /// <summary>ver：每次啟動從 1 起的全域計數，不持久（D7）</summary>
    private long nextVer;

    /// <summary>這次啟動的一輪開始與結束標記的序號（meta 合併鍵的世代，D5）</summary>
    private long markerSeq;

    private string? keyBootId;

    // 以線上的對話 id 為鍵：meta 鍵的世代、最後的中繼資料快照、本機 id（UiPort 用）
    private readonly Dictionary<string, string> generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublishMetaSnapshot> lastMeta = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> localIds = new(StringComparer.Ordinal);

    // 還沒有中繼資料的對話的變化（要過 meta，到了才一起處理）與要過的時間；要 meta 的目的（整份重新發布、補送 created）
    private readonly Dictionary<string, List<ConversationChange>> awaitingMeta = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> metaRequestedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> redoPending = new(StringComparer.Ordinal);
    private readonly HashSet<string> retryPending = new(StringComparer.Ordinal);

    // 依 Ord 一頁一頁取項目快照的對話（D8、D12）；等伺服器刪除旁檔寫好的對話（D25）；已補送過 created 的 retry_at
    private readonly Dictionary<string, PageWalk> walks = new(StringComparer.Ordinal);
    private readonly HashSet<string> tombstoning = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> retriedAt = new(StringComparer.Ordinal);

    private readonly Dictionary<PublishNoticeKind, HashSet<string>> pendingNotices = new();
    private ITimer? createdRetryTimer;
    private ITimer? noticeTimer;
    private bool notOwnerNoticed;
    private bool tablesMissingLogged;
    private long? lastUpgradeTryMs;

    /// <summary>這一輪收件匣的發布狀態（有對話事件時與 outbox 的列同一個交易寫，<see cref="TakeConversationPass"/>）</summary>
    private ConversationPass? convPass;

    /// <summary>
    /// 寫入失敗而沒做成的降級清除（對話 → 不論種類都換，<see cref="ConversationPass.ScrubAll"/>）：送出之前先單獨補寫，補不成就不送（第二輪合併前審查 PRIV-4、F1）
    /// </summary>
    private readonly Dictionary<string, bool> scrubOwed = new(StringComparer.Ordinal);

    // 送出的閘門（第二輪合併前審查 F1）：等中繼資料的對話；有對話的中繼資料到了（排一次送出）。
    // 中樞沒有的對話不另外記放行：待送列已換成 {}，閘門本來就不擋 {}；之後若又有不是 {} 的待送列照樣擋、再要一次
    private readonly HashSet<string> sendGateAsked = new(StringComparer.Ordinal);
    private bool sendGateResolved;

    /// <summary>一次寫入的發布狀態（提交之後才更新快取）</summary>
    private sealed class ConversationPass
    {
        public List<PublishRecord> Items { get; } = new();

        public Dictionary<string, PublishMetaRow> Metas { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, bool> Redo { get; } = new(StringComparer.Ordinal);

        public List<string> Forget { get; } = new();

        /// <summary>政策降級的對話：outbox 裡之前待送的 item 類列換成 {}、合併鍵清掉（同一個交易，<see cref="ConversationPublishWrite.ScrubPending"/>）</summary>
        public List<string> Scrub { get; } = new();

        /// <summary>改成 localOnly 的對話：outbox 裡之前待送的列不論種類換成 {}（<see cref="ConversationPublishWrite.ScrubAllPending"/>，第二輪合併前審查 PRIV-2）</summary>
        public List<string> ScrubAll { get; } = new();

        /// <summary>「已送 conv.itemRemoved」歸 0 的項目（<see cref="ConversationPublishWrite.ResetRemoved"/>，第三輪合併前審查 PRIV-R3-1）</summary>
        public List<(string ConversationId, string ItemId)> ResetRemoved { get; } = new();

        public List<Action> AfterCommit { get; } = new();

        /// <summary>這一次寫了事件的對話（寫入失敗時記 needs_redo）</summary>
        public HashSet<string> Touched { get; } = new(StringComparer.Ordinal);

        public int Appends { get; set; }

        public bool IsEmpty => Items.Count == 0 && Metas.Count == 0 && Redo.Count == 0 && Forget.Count == 0 && Scrub.Count == 0 && ScrubAll.Count == 0
                               && ResetRemoved.Count == 0 && AfterCommit.Count == 0;

        public ConversationPublishWrite ToWrite() => new(Items.ToList(), Metas.Values.ToList(), new Dictionary<string, bool>(Redo, StringComparer.Ordinal), Forget.ToList(),
            Scrub.ToList(), ScrubAll.ToList(), ResetRemoved.ToList());
    }

    /// <summary>一個對話依 Ord 分頁取快照（下一頁從上一頁最後的 Ord 之後接著要）</summary>
    private sealed class PageWalk(string local, OrdHint? hint, bool clearRedo)
    {
        public string Local { get; } = local;

        public OrdHint? Hint { get; } = hint;

        public long AfterOrd { get; set; }

        /// <summary>走完之後清掉 needs_redo（整份重新發布的那一種）</summary>
        public bool ClearRedo { get; set; } = clearRedo;

        /// <summary>走的途中又要走一次（走完再從頭走）</summary>
        public bool Again { get; set; }

        public Dictionary<string, PublishedItemRow>? Published { get; set; }

        /// <summary>秘密回答清除跨頁的狀態（<see cref="SecretAnswerScrubRules.ScrubHistory(IReadOnlyList{AgentChatItem}, SecretScrubState)"/>）</summary>
        public SecretScrubState Scrub { get; } = new();

        /// <summary>重整佇列的工作（步 12）：下一頁等下一輪才要、項目沒載入時改讀檔、走完結束這個工作；null＝一般的分頁</summary>
        public ReconcileJob? Job { get; set; }
    }

    // ───────────────────────────── 登記點 ─────────────────────────────

    partial void ContributeConversationFeatures(List<string> into)
    {
        if (Conversations is null || store is null)
            return;
        if (!PublishPlanRules.DeclaresConv1(true, store.ConversationTablesReady, DlpVersion, ConversationPublisher.RequiredDlpVersion))
        {
            if (store.ConversationTablesReady)
                Log.Warn($"權杖 DLP 的版本 {DlpVersion} 低於宣告 conv1 的必要版本 {ConversationPublisher.RequiredDlpVersion}：這個組建不發布對話");
            return;
        }

        into.Add(DeviceFeatureRules.Conv1);
        into.Add(DeviceFeatureRules.Live1);
        into.Add(DeviceFeatureRules.Live2);
        // Grok、Antigravity 也同步（SyncEngine.Prov1.cs）：協商出來才送這兩種的帳戶、CLI 狀態與對話
        into.Add(DeviceFeatureRules.Prov1);
    }

    partial void ContributeHeartbeat(List<string> running, List<string> waiting)
    {
        if (Conversations is null || store is null || !store.ConversationTablesReady)
            return;
        if (current is not { Proven: true } conn || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1))
            return;
        var rows = Rows();
        var listed = new List<(string Wire, PublishMetaSnapshot Meta)>();
        foreach (var (wire, meta) in lastMeta)
        {
            if ((!meta.Running && !meta.Waiting) || meta.IsDraft || meta.IsReplica)
                continue;
            if (!rows.TryGetValue(wire, out var row) || !row.CreatedWritten || row.Blocked is not null || row.DeletedAt is not null)
                continue;
            if (ConversationSyncPolicies.Publishes(PolicyOf(meta), ConversationEventKinds.Meta))
                listed.Add((wire, meta));
        }

        running.AddRange(Newest(listed.Where(c => c.Meta.Running)));
        waiting.AddRange(Newest(listed.Where(c => c.Meta.Waiting)));
    }

    partial void HandleConversationSignal(SyncSignal signal, List<OutboxAppend> appends, ref bool handled)
    {
        if (signal is not ConversationWake || Conversations is not { } box)
            return;
        handled = true;
        var changes = box.TakeAll();
        if (changes.Count == 0 || store is null)
            return;
        if (!store.ConversationTablesReady)
        {
            if (!tablesMissingLogged)
            {
                tablesMissingLogged = true;
                Log.Warn("同步資料庫的對話表不在（升級失敗）：對話的變化先不發布，之後重整補送");
            }

            return;
        }

        var pass = convPass ??= new ConversationPass();
        try
        {
            ProcessChanges(changes, pass, appends);
        }
        catch (Exception ex)
        {
            Log.Error($"處理對話的變化失敗（{changes.Count} 筆）", ex);
        }

        // 這一輪沒有對話事件：發布狀態單獨寫（有事件時由 Write 與 outbox 同一個交易寫）
        if (pass.Appends == 0)
        {
            convPass = null;
            FlushPass(pass);
        }

        // 中繼資料（政策）可能變了：推給 UI 的觀看集合重算（步 11）
        RefreshWatched();

        // 這一批遮蔽過：排閒置釋放 DLP 樣式的計時器、有回合時先預熱（WP4.9 步 5）
        NoteDlpActivity();

        // 送出的閘門等的中繼資料到了（降級的已在這一輪換成 {}）：排一次送出（這一輪的寫入在計時器之前完成）
        if (sendGateResolved)
        {
            sendGateResolved = false;
            if (current is { Proven: true } conn)
                ScheduleSend(conn);
        }
    }

    partial void OnBatchResultsForConversations(SyncConnection conn, BatchAck ack)
    {
        if (Conversations is null || store is null || !store.ConversationTablesReady)
            return;
        if (ack.Results is { Count: > 0 } results)
        {
            try
            {
                ApplyConversationResults(conn, results);
            }
            catch (Exception ex)
            {
                Log.Error("處理對話事件的確認結果失敗", ex);
            }
        }

        // 重整與回補的下一輪（步 12：outbox 的列都完成之後才排）
        AfterAckForReconcile();
    }

    partial void OnOnlineForConversations(SyncConnection conn)
    {
        if (Conversations is null || store is null)
            return;
        try
        {
            var conv1 = DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1);
            SetArmed(conv1);
            if (!store.ConversationTablesReady && !TryUpgradeTables())
                return;
            if (!conv1)
            {
                // 沒有 conv1 的期間新建或改過的對話：下一條 conv1 連線重跑一次重整（合併前審查 compat F4）
                refreshNeeded = true;
                return;
            }

            ClearStaleBugBlocks();
            ResumeRedo();
            ReconfirmCreated();
            ScheduleCreatedRetry();
            OnOnlineForReconcile();
        }
        catch (Exception ex)
        {
            Log.Error("對話發布的上線處理失敗", ex);
        }
    }

    // ───────────────────────────── 引擎本體呼叫的（Data.cs、Session.cs） ─────────────────────────────

    /// <summary>這一輪收件匣的發布狀態（交給 <see cref="AppendOutbox"/> 與 outbox 的列同一個交易寫）；沒有時 null</summary>
    private ConversationPass? TakeConversationPass()
    {
        var pass = convPass;
        convPass = null;
        return pass;
    }

    /// <summary>
    /// 沒有 conv1 的連線送出之前（D13、C10）：Pending 的對話列換成 <c>{}</c>（舊伺服器以 invalid 擋下、rejected_events 不含內容）。
    /// 換不成就不送這一批、照本機失敗斷線（不能把內容送給不該收的伺服器）。沒有盒子的引擎不換（WP4.1 的行為）
    /// </summary>
    private bool ScrubConversationRowsIfNeeded(SyncConnection conn)
    {
        if (Conversations is null || DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1))
            return true;
        var any = false;
        for (var i = FirstActive(); i < outbox.Count && !any; i++)
        {
            var entry = outbox[i];
            any = entry.State == OutboxState.Pending && entry.ConversationId is not null && entry.PayloadSha256 != EmptyConversationSha;
        }

        if (!any)
            return true;
        IReadOnlyList<OutboxEntry> scrubbed;
        try
        {
            scrubbed = store!.ScrubPendingConversationRows();
        }
        catch (Exception ex)
        {
            Log.Error("沒有 conv1 的連線送出之前，把待送的對話列換成空物件失敗：這一批不送", ex);
            Fail(conn, LocalFailure);
            return false;
        }

        foreach (var entry in scrubbed)
            Upsert(entry);
        if (scrubbed.Count > 0)
            Log.Info($"這條連線沒有協商出 conv1：{scrubbed.Count} 筆待送的對話列換成空物件（伺服器會擋下、不含內容；之後以 conv1 連線補送）");
        return true;
    }

    /// <summary>寫入失敗的那一輪有降級的清除：記下來（<see cref="scrubOwed"/>），之後單獨補寫</summary>
    private void OweScrub(ConversationPass pass)
    {
        foreach (var wire in pass.Scrub)
            scrubOwed.TryAdd(wire, false);
        foreach (var wire in pass.ScrubAll)
            scrubOwed[wire] = true;
    }

    /// <summary>
    /// 寫入失敗留下的降級清除（<see cref="scrubOwed"/>）單獨寫一次：待送列換成 {}、項目摘要清掉、記 needs_redo（降級的事件之後由重整照現在的政策重送），
    /// 成功之後排進重整佇列。<paramref name="conn"/> 不是 null＝送出之前：補不成就照本機失敗斷線、這一批不送（fail-closed，同沒有 conv1 時的清除；
    /// 第二輪合併前審查 PRIV-4、F1）
    /// </summary>
    private bool PayScrubOwed(SyncConnection? conn)
    {
        if (scrubOwed.Count == 0 || store is null || !store.ConversationTablesReady)
            return true;
        var wires = scrubOwed.Keys.Order(StringComparer.Ordinal).ToList();
        var rows = Rows();
        var metas = new List<PublishMetaRow>();
        foreach (var wire in wires)
        {
            if (rows.TryGetValue(wire, out var row))
                metas.Add(row with { ItemsFileSha256 = null, QueueSha256 = null, MetaSha256 = scrubOwed[wire] ? null : row.MetaSha256 });
        }

        var write = new ConversationPublishWrite([], metas, wires.ToDictionary(w => w, _ => true, StringComparer.Ordinal), wires,
            wires.Where(w => !scrubOwed[w]).ToList(), wires.Where(w => scrubOwed[w]).ToList());
        IReadOnlyList<OutboxEntry> scrubbed;
        try
        {
            scrubbed = store.WritePublish(write);
        }
        catch (Exception ex)
        {
            Log.Error(conn is null
                ? $"{wires.Count} 個對話降級時沒換掉的待送列，補寫失敗（送出之前再試）"
                : $"{wires.Count} 個對話降級時沒換掉的待送列，補寫失敗：這一批不送", ex);
            if (conn is not null)
                Fail(conn, LocalFailure);
            return false;
        }

        foreach (var entry in scrubbed)
            Upsert(entry);
        foreach (var row in metas)
            rows[row.ConversationId] = row;
        var redo = RedoSet();
        foreach (var wire in wires)
            redo.Add(wire);
        scrubOwed.Clear();
        Log.Info($"{wires.Count} 個對話降級時沒換掉的待送列已補換成空物件（{scrubbed.Count} 筆），之後照現在的政策重整");
        RequestRedo(wires);
        return true;
    }

    /// <summary>
    /// conv1 連線送出之前（第二輪合併前審查 F1）：這一批最多送到哪裡。這次啟動還沒看過中繼資料的對話（上一次執行留下的待送列：政策可能在上一次執行的最後改過，
    /// 變化還在發布者的計時器或盒子裡就結束了、或寫入失敗），它的第一筆還沒送過、不是 {} 的列之前截斷這一批，先向 UI 要中繼資料；
    /// 到了照政策轉換處理（降級的換成 {}）才送。中樞沒有這個對話時待送列一律換成 {}（<see cref="ResolveSendGate"/>）
    /// </summary>
    private int ConversationSendLimit(SyncConnection conn, IReadOnlyList<OutboxEntry> batch)
    {
        if (Conversations is null || store is null || !store.ConversationTablesReady || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1))
            return batch.Count;
        var limit = batch.Count;
        List<string>? ask = null;
        var now = NowMs();
        for (var i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            if (entry.State != OutboxState.Pending || entry.ConversationId is not { } wire || entry.PayloadSha256 == EmptyConversationSha || SendVerified(wire))
                continue;
            limit = Math.Min(limit, i);
            var asked = !sendGateAsked.Add(wire) && now - metaRequestedAt.GetValueOrDefault(wire) <= MetaRequestTimeoutMs;
            if (!asked && (ask is null || !ask.Contains(wire)))
                (ask ??= []).Add(wire);
        }

        // 發布者沒掛上（要不到中繼資料）時就不送這些列（fail-closed）
        if (ask is not null && Conversations.UiPort is not null)
        {
            Log.Info($"上一次執行留下 {ask.Count} 個對話的待送列：先向中樞要它們現在的政策，處理完才送");
            RequestMetaFor(ask);
        }

        return limit;
    }

    /// <summary>
    /// 這個對話的待送列可以送了：這次啟動看過它的中繼資料（政策轉換已處理）、有墓碑或改成 localOnly 的封鎖（{} 的列在呼叫端就放行）。
    /// 其他封鎖的對話照樣先要中繼資料：降級的話待送列要換成 {}（<see cref="ScrubBlockedOnDowngrade"/>，第三輪合併前審查 PRIV-R3-3）
    /// </summary>
    private bool SendVerified(string wire)
        => lastMeta.ContainsKey(wire) || (Rows().TryGetValue(wire, out var row) && (row.DeletedAt is not null || row.Blocked == BlockedLocalOnly));

    /// <summary>
    /// 封鎖中（不是 localOnly 的墓碑）的對話降級（第三輪合併前審查 PRIV-R3-3）：封鎖之後不再寫它的事件，但封鎖之前待送的列照樣會送出——
    /// 改成 metadata／paused 時待送的 item 類、改成 localOnly 時不論種類都換成 {}、摘要清掉（解除封鎖、改回 full 之後才會重送）。
    /// 發布狀態的政策不改：解除封鎖（換版、換實例）之後的轉換照舊走墓碑或 MetaOnly。也記下中繼資料（送出的閘門以它放行）
    /// </summary>
    private void ScrubBlockedOnDowngrade(string wire, PublishMetaRow row, PublishMetaSnapshot meta, ConversationPass pass, List<OutboxAppend> appends)
    {
        if (row.Blocked is null || row.Blocked == BlockedLocalOnly || row.DeletedAt is not null || meta.IsDraft || meta.IsReplica)
            return;
        lastMeta[wire] = meta;
        var policy = PolicyOf(meta);
        if (policy == ConversationSyncPolicies.Full)
            return;
        var localOnly = policy == ConversationSyncPolicies.LocalOnly;
        if (!HasPendingContent(wire, allKinds: localOnly))
            return;
        Log.Info($"封鎖中的對話 {wire} 改成 {policy}：之前待送的{(localOnly ? "列" : "內容")}換成空物件、不外送");
        ScrubPendingContent(wire, pass, appends, allKinds: localOnly);
        pass.Forget.Add(wire);
        pass.Metas[wire] = row with { ItemsFileSha256 = null, QueueSha256 = null, MetaSha256 = localOnly ? null : row.MetaSha256 };
    }

    /// <summary>outbox 裡這個對話還有待送、不是 {} 的列（<paramref name="allKinds"/> 是 false 時只看 item 類）</summary>
    private bool HasPendingContent(string wire, bool allKinds)
    {
        for (var i = FirstActive(); i < outbox.Count; i++)
        {
            var entry = outbox[i];
            if (entry.State == OutboxState.Pending && string.Equals(entry.ConversationId, wire, StringComparison.Ordinal) && entry.PayloadSha256 != EmptyConversationSha
                && (allKinds || ConversationEventKinds.ClassOf(entry.Kind) == SyncClasses.Item))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 一批中繼資料處理完（OnMetaBatch）：送出的閘門等的對話有中繼資料了就放行；要了卻沒回來的（中樞的清單裡沒有、資料夾不在）政策不明，
    /// 待送列一律換成 {}、記重整（fail-closed）之後放行
    /// </summary>
    private void ResolveSendGate(MetaBatch batch, ConversationPass pass, List<OutboxAppend> appends)
    {
        foreach (var wire in sendGateAsked.Where(lastMeta.ContainsKey).ToList())
        {
            sendGateAsked.Remove(wire);
            sendGateResolved = true;
        }

        foreach (var id in batch.Requested ?? [])
        {
            if (ConversationIds.ToWire(id) is not { } wire || !sendGateAsked.Contains(wire) || lastMeta.ContainsKey(wire))
                continue;
            Log.Warn($"對話 {wire} 有上一次執行留下的待送列，但中樞沒有它（不在清單裡、資料夾不在）：政策不明，待送的換成空物件、不送內容");
            sendGateAsked.Remove(wire);
            ScrubPendingContent(wire, pass, appends, allKinds: true);
            ForgetScrubbedWithoutPolicy(wire, pass);
            pass.Redo[wire] = true;
            pass.AfterCommit.Add(() =>
            {
                if (current is { Proven: true } conn)
                    ScheduleSend(conn);
            });
        }
    }

    /// <summary>
    /// 中樞沒有的對話的待送列換成 {} 之後（<see cref="ResolveSendGate"/>）：伺服器沒有被告知清掉內容（不像降級有帶新政策的 meta、墓碑有 conv.deleted），
    /// 換掉的列卻記成已送——項目、佇列與 meta 的摘要清掉；待送的 conv.itemRemoved 的「已送」歸 0（{} 被擋下時合併鍵已清，ForgetRejected 認不出項目）。
    /// 對話回到中樞時重整照摘要重送、照推斷補送刪除（第三輪合併前審查 PRIV-R3-1、R3-2）
    /// </summary>
    private void ForgetScrubbedWithoutPolicy(string wire, ConversationPass pass)
    {
        for (var i = FirstActive(); i < outbox.Count; i++)
        {
            var entry = outbox[i];
            if (entry.State == OutboxState.Pending && entry.Kind == ConversationEventKinds.ItemRemoved && string.Equals(entry.ConversationId, wire, StringComparison.Ordinal)
                && PublishKeyRules.TryParseItem(entry.CoalesceKey, out _, out var itemId))
                pass.ResetRemoved.Add((wire, itemId));
        }

        // 這一輪排好的 conv.itemRemoved 也換成了 {}：不能在同一個交易裡又記成已送
        pass.Items.RemoveAll(r => r.Removed && string.Equals(r.ConversationId, wire, StringComparison.Ordinal));
        pass.Forget.Add(wire);
        if (pass.Metas.ContainsKey(wire) || Rows().ContainsKey(wire))
            pass.Metas[wire] = RowOf(pass, wire) with { ItemsFileSha256 = null, QueueSha256 = null, MetaSha256 = null };
    }

    /// <summary>寫入成功：發布狀態的快取換成這一次的值</summary>
    private void ConversationWriteCommitted(ConversationPass? pass)
    {
        if (pass is null || store is null || !store.ConversationTablesReady)
            return;
        var rows = Rows();
        foreach (var (id, row) in pass.Metas)
            rows[id] = row;
        var redo = RedoSet();
        foreach (var (id, value) in pass.Redo)
        {
            if (value)
                redo.Add(id);
            else
                redo.Remove(id);
        }

        foreach (var action in pass.AfterCommit)
            action();

        // 確認、封鎖、墓碑都可能改變「其他地方看得到」的對話（步 11）
        RefreshWatched();
    }

    /// <summary>
    /// 寫入失敗：這些對話的事件沒有寫進 outbox，記 needs_redo 讓重整補送（盡力而為；摘要沒記，所以不會以為已送）。
    /// 同一輪有降級的清除時記下來、馬上單獨補寫一次（補不成的在送出之前再補，第二輪合併前審查 PRIV-4、F1）
    /// </summary>
    private void ConversationWriteFailed(ConversationPass? pass, IReadOnlyList<OutboxAppend> items)
    {
        if (pass is null || store is null || !store.ConversationTablesReady)
            return;
        MarkRedoBestEffort(pass.Touched);
        Log.Warn($"{items.Count} 筆事件（含 {pass.Appends} 筆對話事件）沒有寫進 outbox：{pass.Touched.Count} 個對話記成要重新發布");
        FailPendingReconcile();
        PassFailedWithScrub(pass);
    }

    /// <summary>寫入失敗的那一輪有降級的清除：記下來，馬上單獨補寫一次（補不成的在送出之前再補）</summary>
    private void PassFailedWithScrub(ConversationPass pass)
    {
        if (pass.Scrub.Count == 0 && pass.ScrubAll.Count == 0)
            return;
        MarkRedoBestEffort(pass.Scrub.Concat(pass.ScrubAll));
        OweScrub(pass);
        PayScrubOwed(null);
    }

    private void MarkRedoBestEffort(IEnumerable<string> wires)
    {
        foreach (var wire in wires.Distinct(StringComparer.Ordinal).ToList())
        {
            try
            {
                store!.SetNeedsRedo(wire, true);
                RedoSet().Add(wire);
            }
            catch (Exception ex)
            {
                Log.Warn($"記錄對話 {wire} 要重新發布失敗：{ex.Message}");
            }
        }
    }

    /// <summary>伺服器換了實例（BeginServerChange 已在同一個交易重設發布狀態，D22）：丟掉快取與進行中的分頁、補送 created 的計時</summary>
    private void OnServerChangedForConversations()
    {
        if (Conversations is null)
            return;
        ResetConversationCaches();
        // 複本與對話游標也已清掉（步 14）：中樞的複本跟著清掉，之後從頭重拉
        ResetReplicas(notifyUi: true);
    }

    // ───────────────────────────── 盒子的變化 ─────────────────────────────

    /// <summary>依到達順序處理：變更類依對話整理（快照回應之前的先處理掉），快照回應照它在盒子裡的位置</summary>
    private void ProcessChanges(IReadOnlyList<ConversationChange> changes, ConversationPass pass, List<OutboxAppend> appends)
    {
        var groups = new Dictionary<string, List<ConversationChange>>(StringComparer.Ordinal);
        var order = new List<string>();

        void FlushGroups()
        {
            foreach (var wire in order)
                ProcessGroup(wire, groups[wire], pass, appends);
            groups.Clear();
            order.Clear();
        }

        foreach (var change in changes)
        {
            switch (change)
            {
                case MetaBatch batch:
                    FlushGroups();
                    OnMetaBatch(batch, pass, appends);
                    break;
                case ItemsPageChange page:
                    FlushGroups();
                    OnItemsPage(page, pass, appends);
                    break;
                case ServerDeletedWritten written:
                    FlushGroups();
                    OnServerDeletedWritten(written, pass, appends);
                    break;
                case ReplicaRequested request:
                    // 複本（步 14，SyncEngine.Replicas.cs）：和發布無關
                    OnReplicaRequested(request);
                    break;
                case ReplicaClosed closed:
                    OnReplicaClosed(closed);
                    break;
                default:
                    if (ConversationIds.ToWire(change.ConversationId) is not { } wire)
                    {
                        Log.Warn("對話的變化帶了不合格的對話 id：略過（程式錯誤）");
                        break;
                    }

                    localIds[wire] = change.ConversationId!;
                    if (!groups.TryGetValue(wire, out var list))
                    {
                        list = new List<ConversationChange>();
                        groups[wire] = list;
                        order.Add(wire);
                    }

                    list.Add(change);
                    break;
            }
        }

        FlushGroups();
    }

    /// <summary>一個對話這一輪的變化（依掛鉤的順序）</summary>
    private void ProcessGroup(string wire, List<ConversationChange> changes, ConversationPass pass, List<OutboxAppend> appends)
    {
        // 只有佇列的變化（例如引擎自己要的佇列快照）不影響讀檔的項目結果
        if (changes.Any(c => c is not QueueChanged))
            MarkReconcileStale(wire);
        // 閒置釋放 DLP 樣式看的執行中回合：封鎖、墓碑的對話在本機照樣跑回合，在下面 return 之前記（合併前審查 PROD-1）
        if (changes.LastOrDefault(c => c is MetaChanged) is MetaChanged latestMeta)
            NoteDlpRunning(wire, latestMeta.Snapshot);
        if (changes.Any(c => c is DeletingChange))
            dlpRunning.Remove(wire);
        var row = RowOf(pass, wire);
        if (row.Blocked is not null && changes.LastOrDefault(c => c is MetaChanged) is MetaChanged blockedMeta)
            ScrubBlockedOnDowngrade(wire, row, blockedMeta.Snapshot, pass, appends);
        if (row.DeletedAt is not null || row.Blocked is not null || tombstoning.Contains(wire))
            return;
        if (!CanPublish() || DeferredForProv1(wire, (changes.LastOrDefault(c => c is MetaChanged) as MetaChanged)?.Snapshot))
        {
            // 沒有 conv1（D13），或 Grok、Antigravity 的對話而沒有 prov1（SyncEngine.Prov1.cs）：不寫新的事件；
            // 已知的對話記成要重新發布，下一次 conv1／prov1 連線補送。
            // 中繼資料照樣記下最新的（重整佇列開始時以它判斷政策轉換，步 12）
            for (var i = changes.Count - 1; i >= 0; i--)
            {
                if (changes[i] is MetaChanged latest)
                {
                    lastMeta[wire] = latest.Snapshot;
                    break;
                }
            }

            if (changes.Any(c => c is DeletingChange) && MayExistOnServer(row))
            {
                // 沒有 conv1 時刪除（例如伺服器回滾到舊版的期間）：先記墓碑與 needs_redo，下一次 conv1 進 Live 時 ResumeRedo 補送 conv.deleted
                // （中繼資料留著給它取 epoch；合併前審查 compat F4）
                pass.Metas[wire] = row with { DeletedAt = NowMs() };
                pass.Redo[wire] = true;
                return;
            }

            if (row.CreatedWritten)
                pass.Redo[wire] = true;
            return;
        }

        var deleting = changes.Any(c => c is DeletingChange);
        if (awaitingMeta.TryGetValue(wire, out var stash))
        {
            if (deleting)
            {
                awaitingMeta.Remove(wire);
                metaRequestedAt.Remove(wire);
                PublishDeletion(wire, row, DeletionEpoch(changes), pass, appends);
                return;
            }

            stash.AddRange(changes);
            if (NowMs() - metaRequestedAt.GetValueOrDefault(wire) > MetaRequestTimeoutMs)
                RequestMetaFor([wire]);
            return;
        }

        if (changes.Any(c => c is NeedsSnapshot))
        {
            // 盒子溢出過（之前的變化丟了）：重取中繼資料並整份走一次（摘要相同的不重送）
            redoPending.Add(wire);
            awaitingMeta[wire] = changes.Where(c => c is not NeedsSnapshot).ToList();
            RequestMetaFor([wire]);
            return;
        }

        PublishMetaSnapshot? meta = null;
        for (var i = changes.Count - 1; i >= 0 && meta is null; i--)
        {
            if (changes[i] is MetaChanged m)
                meta = m.Snapshot;
        }

        var metaIsNew = meta is not null;
        meta ??= lastMeta.GetValueOrDefault(wire);
        if (meta is null)
        {
            if (deleting)
            {
                PublishDeletion(wire, row, DeletionEpoch(changes), pass, appends);
                return;
            }

            // 這次啟動還沒看過它的中繼資料（政策、epoch 都不知道）：變化先放著，要到了再一起處理
            awaitingMeta[wire] = new List<ConversationChange>(changes);
            RequestMetaFor([wire]);
            return;
        }

        Publish(wire, localIds.GetValueOrDefault(wire, wire), meta, changes, metaIsNew, redo: false, pass, appends);
    }

    /// <summary>
    /// 依政策與發布狀態寫這個對話的事件：先看政策轉換（D25；<paramref name="metaIsNew"/>＝這一輪有新的中繼資料快照），
    /// 再以 <see cref="PublishPlanRules.Plan"/> 整理、逐一投影。<paramref name="redo"/>＝整份重新發布（送 meta，full 的再整份走一次項目）
    /// </summary>
    private void Publish(string wire, string local, PublishMetaSnapshot meta, IReadOnlyList<ConversationChange> changes, bool metaIsNew, bool redo,
        ConversationPass pass, List<OutboxAppend> appends)
    {
        lastMeta[wire] = meta;
        if (meta.IsDraft || meta.IsReplica)
            return;
        var row = RowOf(pass, wire);
        if (row.DeletedAt is not null || row.Blocked is not null)
        {
            ScrubBlockedOnDowngrade(wire, row, meta, pass, appends);
            return;
        }

        var knowledge = Knowledge(row, meta);
        var ensureMeta = redo;
        var walk = redo;
        var republish = false;
        if (metaIsNew)
        {
            switch (PublishPlanRules.Transition(row.Policy, meta.EffectivePolicy, knowledge))
            {
                case PolicyTransition.None:
                    break;
                case PolicyTransition.Tombstone:
                    // 使用者選了只在這台電腦：還沒送出的列現在就不論種類換成 {}（連 created、meta 的標題都不外送），不等旁檔寫好——
                    // 寫旁檔的期間、寫不成功時都可能先連上、先送出（第二輪合併前審查 PRIV-1、PRIV-2）。摘要清掉（不能以為已送）。
                    // 先寫旁檔、收到寫好了才送 conv.deleted（D25）；這段期間的變化都不送
                    ScrubPendingContent(wire, pass, appends, allKinds: true);
                    pass.Forget.Add(wire);
                    pass.Metas[wire] = row with { ItemsFileSha256 = null, QueueSha256 = null, MetaSha256 = null };
                    BeginTombstone(wire, local, pass);
                    return;
                case PolicyTransition.MetaOnly:
                    // 帶新政策的 meta 讓伺服器清掉內容；項目與佇列的摘要清掉（改回 full 時整份重送），列與 Ord 保留。
                    // 還沒送出的內容不再外送：待送的 item 類列換成 {}、合併鍵清掉，meta 換新世代（一定排在所有待送列之後，合併前審查 P1、F1）
                    ScrubPendingContent(wire, pass, appends, allKinds: false);
                    pass.Forget.Add(wire);
                    row = row with { ItemsFileSha256 = null, QueueSha256 = null };
                    DropWalk(wire);
                    DropRunning(wire);
                    NextGeneration(wire);
                    ensureMeta = true;
                    break;
                case PolicyTransition.Republish:
                    // 新世代：帶 full 的 meta 不會合併回降級時那筆待送的 meta（排在清除內容之前）。伺服器降級時清掉了佇列：記成空佇列（之後要的快照空的不送）。
                    // 伺服器降級時也刪了這個對話的縮圖：這次啟動上傳過的忘掉，重新投影時定稿的照常再傳（WP4.7 合併前審查 DI-1）
                    thumbs?.ForgetAllUploaded();
                    row = row with { QueueSha256 = EmptyQueueSha };
                    NextGeneration(wire);
                    ensureMeta = true;
                    walk = true;
                    republish = true;
                    break;
                default:
                    throw new InvalidOperationException("不認得的政策轉換");
            }
        }

        var ops = new List<PublishOp>(changes.Count + 1);
        var data = new Dictionary<PublishOp, ConversationChange>(ReferenceEqualityComparer.Instance);
        if (ensureMeta && !changes.Any(c => c is MetaChanged))
            AddOp(ops, data, new PublishOp(PublishOpKind.Meta), new MetaChanged(local, meta));
        foreach (var change in changes)
        {
            switch (change)
            {
                case ItemChanged c:
                    AddOp(ops, data, new PublishOp(PublishOpKind.Item, c.Item.Id), c);
                    break;
                case ItemRemovedChange c:
                    AddOp(ops, data, new PublishOp(PublishOpKind.ItemRemoved, c.ItemId), c);
                    break;
                case MetaChanged c:
                    AddOp(ops, data, new PublishOp(PublishOpKind.Meta), c);
                    break;
                case QueueChanged c:
                    AddOp(ops, data, new PublishOp(PublishOpKind.Queue), c);
                    break;
                case TurnStartedChange c:
                    AddOp(ops, data, new PublishOp(PublishOpKind.TurnStarted), c);
                    break;
                case TurnCompletedChange c:
                    AddOp(ops, data, new PublishOp(PublishOpKind.TurnCompleted), c);
                    break;
                case AttentionChange c:
                    AddOp(ops, data, new PublishOp(PublishOpKind.Attention), c);
                    break;
                case DeletingChange c:
                    AddOp(ops, data, new PublishOp(PublishOpKind.Deleted), c);
                    break;
                case NeedsSnapshot:
                    walk = true;
                    break;
                default:
                    throw new InvalidOperationException($"不認得的對話變化：{change.GetType().Name}");
            }
        }

        var planned = PublishPlanRules.Plan(meta.EffectivePolicy, knowledge, ops);
        var epoch = ConversationWire.EpochOf(meta.Meta);
        var needWalk = false;
        foreach (var op in planned)
        {
            var change = op.Kind == PublishOpKind.Created ? null : data[op];
            if (!Emit(wire, epoch, meta, op, change, null, ref row, ref needWalk, pass, appends))
                break;
        }

        pass.Metas[wire] = row;
        if (row.DeletedAt is null && ops.Any(o => o.Kind == PublishOpKind.Deleted) && !planned.Any(o => o.Kind == PublishOpKind.Deleted))
        {
            // 伺服器上從來沒有的對話被刪除（Plan 丟掉了刪除）：這次啟動記的狀態丟掉，過時的中繼資料不會再被拿來建立它（合併前審查 F3）
            pass.AfterCommit.Add(() => ForgetConversation(wire));
            return;
        }

        if ((walk || needWalk) && PolicyOf(meta) == ConversationSyncPolicies.Full && row.CreatedWritten && row.DeletedAt is null)
        {
            // 即時的路徑（不是重整）遇到 created 還沒被確認的對話：先不整份走（伺服器可能回 not_owner，內容不該先送出去，D24、D32）；
            // 記 needs_redo，created 被確認時 ApplyConversationResults 排進重整佇列（合併前審查 P4）
            if (!redo && !row.CreatedAcked)
                pass.Redo[wire] = true;
            else
                StartWalk(wire, local, clearRedo: redo);
        }
        else if (redo)
        {
            pass.Redo[wire] = false;
        }

        // 整份重新發布（重整佇列的每一種原因、盒子溢出、改回 full）：向 UI 要一次佇列快照，蓋掉伺服器上可能過時的佇列（摘要相同的不重送；
        // 改回 full 時伺服器降級清掉了佇列、上面記成空佇列，空的不送。合併前審查 F2、第二輪 F3）
        if ((redo || republish) && row is { CreatedWritten: true, DeletedAt: null, Blocked: null } && PolicyOf(meta) == ConversationSyncPolicies.Full
            && !tombstoning.Contains(wire))
            Conversations?.UiPort?.RequestQueue(local);
    }

    /// <summary>
    /// 政策降級（MetaOnly、墓碑）：這一輪前面已經排進 <paramref name="appends"/>、還沒寫的這個對話的 item 類列換成 <c>{}</c>、合併鍵清掉（摘要也清掉，
    /// 不能以為已送）；outbox 裡之前待送的在同一個交易裡做同樣的事（<see cref="ConversationPublishWrite.ScrubPending"/>）。合併前審查 P1、F1。
    /// <paramref name="allKinds"/>＝改成 localOnly：不論種類都換（<see cref="ConversationPublishWrite.ScrubAllPending"/>；created、meta 帶著標題，第二輪合併前審查 PRIV-2）
    /// </summary>
    private static void ScrubPendingContent(string wire, ConversationPass pass, List<OutboxAppend> appends, bool allKinds)
    {
        var scrub = allKinds ? pass.ScrubAll : pass.Scrub;
        if (!scrub.Contains(wire))
            scrub.Add(wire);
        for (var i = 0; i < appends.Count; i++)
        {
            var append = appends[i];
            if (!string.Equals(append.ConversationId, wire, StringComparison.Ordinal))
                continue;
            if (allKinds || ConversationEventKinds.ClassOf(append.Kind) == SyncClasses.Item)
                appends[i] = append with { Json = SyncStore.EmptyConversationPayload, CoalesceKey = null };
            else if (append.Kind == ConversationEventKinds.Attention && ConversationEventRules.AttentionWithoutSpeech(append.Json) is { } stripped)
                appends[i] = append with { Json = stripped, CoalesceKey = null };   // 語音句子可能帶內容的簡要說明
            else if (append.CoalesceKey is not null)
                appends[i] = append with { CoalesceKey = null };
        }

        for (var i = 0; i < pass.Items.Count; i++)
        {
            if (pass.Items[i] is { Removed: false } record && string.Equals(record.ConversationId, wire, StringComparison.Ordinal))
                pass.Items[i] = record with { SrcSha256 = null, WireSha256 = null };
        }
    }

    private static void AddOp(List<PublishOp> ops, Dictionary<PublishOp, ConversationChange> data, PublishOp op, ConversationChange change)
    {
        ops.Add(op);
        data[op] = change;
    }

    /// <summary>
    /// 投影一個操作並加進 outbox；<paramref name="scrubbed"/> 不是 null＝分頁快照（秘密回答已對整份清單清除過）。
    /// 回 false＝寫了 conv.deleted（之後的一律丟掉）
    /// </summary>
    private bool Emit(string wire, long epoch, PublishMetaSnapshot meta, PublishOp op, ConversationChange? change, IReadOnlyDictionary<string, string>? scrubbed,
        ref PublishMetaRow row, ref bool needWalk, ConversationPass pass, List<OutboxAppend> appends)
    {
        switch (op.Kind)
        {
            case PublishOpKind.Created:
                if (projector.Meta(wire, meta, Generation(wire), created: true) is { } created)
                {
                    // 從沒建立過：伺服器上一定沒有它的佇列（空的不必送；曾經建立過的照舊，換實例之後是不明，第二輪合併前審查 F3）
                    Add(pass, appends, created.Append);
                    row = row with
                    {
                        CreatedWritten = true, EverCreated = true, Policy = PolicyOf(meta), MetaSha256 = created.MetaSha256,
                        QueueSha256 = row.EverCreated ? row.QueueSha256 : EmptyQueueSha,
                    };
                }

                return true;
            case PublishOpKind.Meta:
            {
                var snapshot = ((MetaChanged)change!).Snapshot;
                // 內容和上一次寫的相同就不送（標頭常常只是狀態文字變了）
                if (projector.Meta(wire, snapshot, Generation(wire), created: false) is { } projected && projected.MetaSha256 != row.MetaSha256)
                {
                    Add(pass, appends, projected.Append);
                    row = row with { Policy = PolicyOf(snapshot), MetaSha256 = projected.MetaSha256 };
                }

                return true;
            }

            case PublishOpKind.TurnStarted:
                Add(pass, appends, projector.Turn(wire, epoch, started: true, null, null));
                NextGeneration(wire);
                return true;
            case PublishOpKind.TurnCompleted:
            {
                var completed = (TurnCompletedChange)change!;
                Add(pass, appends, projector.Turn(wire, epoch, started: false, completed.Outcome, completed.Usage));
                NextGeneration(wire);
                return true;
            }

            case PublishOpKind.Item:
            {
                var item = ((ItemChanged)change!).Item;
                if (scrubbed is null && (item.Ord is null || IsAnswerMessage(item)))
                {
                    // 沒有 Ord 的不丟掉、改要求重取快照（D8）；「我的回答：」要對著整份清單清除秘密回答（D18，fail-closed）
                    needWalk = true;
                    return true;
                }

                // 執行中的項目：持久寫不寫、即時送不送照時機決定（步 11，D21；即時更新在 DecideItem 裡就送）；分頁快照（重整）一律持久
                var durable = scrubbed is not null || DecideItem(wire, epoch, meta, item, row);
                if (durable && projector.Item(wire, epoch, item, scrubbed ?? ConversationProjector.NothingScrubbed, ++nextVer) is { } projected)
                {
                    Add(pass, appends, projected.Append);
                    pass.Items.Add(projected.Record);
                    NoteDurable(wire, item, fromWalk: scrubbed is not null, pass);
                }

                return true;
            }

            case PublishOpKind.ItemRemoved:
            {
                var itemId = ((ItemRemovedChange)change!).ItemId;
                DropRunning(wire, itemId);
                if (ConversationIds.IsItemId(itemId))
                {
                    Add(pass, appends, projector.ItemRemoved(wire, epoch, itemId));
                    pass.Items.Add(new PublishRecord(wire, itemId, null, null, null, false, Removed: true));
                }

                return true;
            }

            case PublishOpKind.Queue:
            {
                // 摘要相同的不送：伺服器上確定沒有佇列時摘要是空佇列的（EmptyQueueSha），空的不送；不明（null）時空的也送一次，
                // 蓋掉伺服器上可能留著的舊佇列（沒有 conv1 的期間清空、被擋下、換實例或還原之後，第二輪合併前審查 F3）
                if (projector.Queue(wire, epoch, ((QueueChanged)change!).Snapshot) is { } queue)
                {
                    var digest = PublishDigestRules.Payload(ConversationEventKinds.Queue, queue.Json);
                    if (digest != row.QueueSha256)
                    {
                        Add(pass, appends, queue);
                        row = row with { QueueSha256 = digest };
                    }
                }

                return true;
            }

            case PublishOpKind.Attention:
            {
                var attention = (AttentionChange)change!;
                if (ConversationAttentionKinds.IsKnown(attention.Kind))
                    Add(pass, appends, projector.Attention(wire, epoch, attention.Kind, attention.ItemId, VoiceNotifyRules.ForPolicy(attention.Speech, PolicyOf(meta))));
                return true;
            }

            case PublishOpKind.Deleted:
                Add(pass, appends, projector.Deleted(wire, epoch));
                row = row with { DeletedAt = NowMs() };
                pass.AfterCommit.Add(() => ForgetConversation(wire));
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.Kind, "不認得的發布操作");
        }
    }

    /// <summary>
    /// 沒有中繼資料的對話被刪除：伺服器上可能有它的才送 conv.deleted（伺服器上本來沒有的不必送），記墓碑。
    /// <paramref name="epoch"/> 是刪除當下對話的 epoch（<see cref="DeletingChange.Epoch"/>；第 5 階段計畫 6：接手後不是 1）
    /// </summary>
    private void PublishDeletion(string wire, PublishMetaRow row, long epoch, ConversationPass pass, List<OutboxAppend> appends)
    {
        if (!MayExistOnServer(row))
        {
            ForgetConversation(wire);
            return;
        }

        Add(pass, appends, projector.Deleted(wire, epoch));
        pass.Metas[wire] = row with { DeletedAt = NowMs() };
        pass.AfterCommit.Add(() => ForgetConversation(wire));
    }

    /// <summary>這一批變化裡最後一個刪除標記帶的 epoch（中樞刪除時的 LeaseEpoch）</summary>
    private static long DeletionEpoch(IReadOnlyList<ConversationChange> changes)
        => changes.OfType<DeletingChange>().LastOrDefault()?.Epoch ?? ConversationIds.DefaultEpoch;

    /// <summary>
    /// 補一筆新的 conv.created（invalid 之後的補送 D14、上線時補確認與 restore，第二輪合併前審查 F2）。政策轉換還沒處理時（沒有 conv1 的期間改的、
    /// 改政策的變化留在發布者的計時器裡就結束了）先照轉換走（<see cref="Publish"/>：MetaOnly 的清除與 Forget、墓碑、Republish 的整份重送），再補 created：
    /// 直接以現在的政策補的話，發布狀態的政策跟著變成新的，之後的轉換都判成 None——伺服器照 ReplaceMeta 清掉了內容，本機的摘要卻還記著已送，
    /// 改回 full 也不會重送（第三輪合併前審查 LIVE-1）。restore 要的是新的 created（解除 recovering），所以轉換之後照樣補
    /// </summary>
    private void EmitCreated(string wire, PublishMetaSnapshot meta, ConversationPass pass, List<OutboxAppend> appends)
    {
        var row = RowOf(pass, wire);
        if (PublishPlanRules.Transition(row.Policy, meta.EffectivePolicy, Knowledge(row, meta)) != PolicyTransition.None)
        {
            var before = appends.Count;
            Publish(wire, localIds.GetValueOrDefault(wire, meta.Meta.Id), meta, [], metaIsNew: true, redo: false, pass, appends);
            row = RowOf(pass, wire);
            // 改成 localOnly（寫旁檔中）不補；換實例之後還沒重新發布的，Plan 已經先補了一筆 created
            if (tombstoning.Contains(wire) || row.DeletedAt is not null || row.Blocked is not null
                || appends.Skip(before).Any(a => a.Kind == ConversationEventKinds.Created && string.Equals(a.ConversationId, wire, StringComparison.Ordinal)))
                return;
        }

        if (projector.Meta(wire, meta, Generation(wire), created: true) is not { } created)
            return;
        Add(pass, appends, created.Append);
        pass.Metas[wire] = row with { CreatedWritten = true, EverCreated = true, Policy = PolicyOf(meta), MetaSha256 = created.MetaSha256 };
    }

    private static void Add(ConversationPass pass, List<OutboxAppend> appends, OutboxAppend append)
    {
        appends.Add(append);
        pass.Appends++;
        pass.Touched.Add(append.ConversationId!);
    }

    // ───────────────────────────── 快照回應 ─────────────────────────────

    /// <summary>
    /// 要過的中繼資料到了：放著的變化照順序處理；也當成新的中繼資料快照（政策可能在 app 沒開時改過）。
    /// 重整的批次（步 12，D12、D24）另外比對 meta 摘要（未知的寫 conv.created）、有旁檔沒墓碑的補送 conv.deleted、full 的排進重整佇列；
    /// 最後一批到了才開始排內容
    /// </summary>
    private void OnMetaBatch(MetaBatch batch, ConversationPass pass, List<OutboxAppend> appends)
    {
        var refresh = batch.Refresh && refreshing;
        if (refresh && batch.AllIds is { } all)
            NoteRefreshIds(all);
        foreach (var snapshot in batch.Metas)
        {
            if (snapshot?.Meta is null || ConversationIds.ToWire(snapshot.Meta.Id) is not { } wire)
                continue;
            localIds[wire] = snapshot.Meta.Id;
            metaRequestedAt.Remove(wire);
            var stash = awaitingMeta.Remove(wire, out var stashed) ? stashed : null;
            var redo = redoPending.Remove(wire);
            var retry = retryPending.Remove(wire);
            NoteDlpRunning(wire, snapshot);
            if (snapshot.IsDraft || snapshot.IsReplica)
                continue;
            var row = RowOf(pass, wire);
            if (row.DeletedAt is not null || row.Blocked is not null || tombstoning.Contains(wire))
            {
                lastMeta[wire] = snapshot;
                ScrubBlockedOnDowngrade(wire, row, snapshot, pass, appends);
                continue;
            }

            if (!CanPublish())
            {
                lastMeta[wire] = snapshot;
                if (row.CreatedWritten || redo)
                    pass.Redo[wire] = true;
                continue;
            }

            if (retry && !row.CreatedAcked)
                EmitCreated(wire, snapshot, pass, appends);
            if (refresh && snapshot.ServerDeleted && !row.CreatedWritten)
            {
                // 有伺服器刪除的旁檔、沒有墓碑（換實例之後、或寫旁檔與寫 outbox 之間當機）：只補送 conv.deleted（D22、D25）
                lastMeta[wire] = snapshot;
                EmitServerDeleted(wire, snapshot, pass, appends);
                continue;
            }

            // 重整：連同這一份快照比對 meta 摘要（未知的對話由 Plan 補 created，D24 的「先為所有該發布的對話送 conv.created」）
            IReadOnlyList<ConversationChange> changes = refresh
                ? [.. stash ?? [], new MetaChanged(snapshot.Meta.Id, snapshot)]
                : stash ?? [];
            Publish(wire, snapshot.Meta.Id, snapshot, changes, metaIsNew: true, redo, pass, appends);
            if (refresh)
                ConsiderForReconcile(wire, snapshot, row.CreatedWritten, pass);
        }

        if (sendGateAsked.Count > 0)
            ResolveSendGate(batch, pass, appends);
        if (refresh && batch.Complete)
            CompleteRefresh();
    }

    /// <summary>
    /// 一頁項目快照（D12）：摘要相同的不重送；第一頁的完整 id 集合裡沒有、發布過的送 itemRemoved；還有下一頁就接著要。
    /// 重整佇列的分頁（步 12）下一頁等下一輪；項目沒載入時改在執行緒集區唯讀讀檔（D10）
    /// </summary>
    private void OnItemsPage(ItemsPageChange change, ConversationPass pass, List<OutboxAppend> appends)
    {
        if (ConversationIds.ToWire(change.ConversationId) is not { } wire || !walks.TryGetValue(wire, out var walk) || change.AfterOrd != walk.AfterOrd)
            return;
        var row = RowOf(pass, wire);
        if (row.DeletedAt is not null || row.Blocked is not null || tombstoning.Contains(wire))
        {
            walks.Remove(wire);
            if (walk.Job is { } stopped)
                EndJob(stopped, ok: false);
            return;
        }

        // 讀完檔再問一次中樞（D10）：還是沒載入就照檔案的結果寫；途中被載入了就丟掉檔案的結果、改用這一頁（記憶體）
        if (walk.Job is { Phase: ReconcilePhase.Confirming } confirming)
        {
            if (change.Page is null)
            {
                walks.Remove(wire);
                OnUnloadedConfirmed(confirming);
                return;
            }

            confirming.File = null;
            confirming.Phase = ReconcilePhase.Walking;
        }

        if (!CanPublish() || change.Page is not { OrdsReady: true } page || !lastMeta.TryGetValue(wire, out var meta))
        {
            walks.Remove(wire);
            // 項目沒載入：重整佇列的工作改讀檔，一般的分頁排進佇列（步 12）；沒有 conv1、Ord 準備不了：記成要重新發布，之後重整補送
            var unloaded = change.Page is null && CanPublish() && lastMeta.ContainsKey(wire);
            if (unloaded && walk.Job is { } reading && StartFileRead(reading))
                return;
            if (row.CreatedWritten)
                pass.Redo[wire] = true;
            if (walk.Job is { } failed)
                EndJob(failed, ok: false);
            else if (unloaded && row.CreatedWritten)
                EnqueueReconcile(new ReconcileCandidate(wire, ReconcileReason.Redo, UpdatedAtMs(lastMeta[wire]), Estimate(wire)));
            return;
        }

        walk.Published ??= store!.LoadPublishedItems(wire).ToDictionary(r => r.ItemId, StringComparer.Ordinal);
        var epoch = ConversationWire.EpochOf(meta.Meta);
        if (PublishesItems(wire, meta, row))
        {
            if (change.AfterOrd <= 0 && page.AllIds is { } all)
            {
                var present = new HashSet<string>(all, StringComparer.Ordinal);
                if (RemovalFloor(wire, page.RemovalsTrusted, walk.Published.Values, present) is { } floor)
                    EmitRemovedItems(wire, epoch, walk.Published.Values, present, floor, pass, appends);
            }
            var scrubbed = SecretAnswerScrubRules.ScrubHistory(page.Items, walk.Scrub);
            long bytes = 0;
            foreach (var item in page.Items)
                EmitReconcileItem(wire, epoch, item, scrubbed, walk.Published, pass, appends, ref bytes);
        }

        pass.Metas[wire] = row;
        if (page.More && page.LastOrd > walk.AfterOrd && Conversations?.UiPort is { } port)
        {
            walk.AfterOrd = page.LastOrd;
            if (walk.Job is { } gated)
            {
                // 重整佇列的下一頁等下一輪（outbox 的列都完成之後；一般事件優先，D24）
                gated.Phase = ReconcilePhase.WaitingRound;
                NotePageWritten(gated, pass, done: false);
                return;
            }

            port.RequestItems(walk.Local, page.LastOrd, PublishTimingRules.ItemsPage, walk.Hint);
            return;
        }

        walks.Remove(wire);
        if (walk.ClearRedo)
            pass.Redo[wire] = false;
        if (walk.Again)
            StartWalk(wire, walk.Local, clearRedo: false);
        if (walk.Job is { } job)
        {
            // 走的途中又要走一次：這個工作跟著下一次分頁，走完才結束
            if (walks.TryGetValue(wire, out var again))
                again.Job = job;
            NotePageWritten(job, pass, done: again is null);
        }
    }

    /// <summary>伺服器刪除的旁檔寫好了沒有（D25）：寫好才送 conv.deleted、記墓碑並封鎖；沒寫好或現在不能發布就記 needs_redo</summary>
    private void OnServerDeletedWritten(ServerDeletedWritten written, ConversationPass pass, List<OutboxAppend> appends)
    {
        if (ConversationIds.ToWire(written.ConversationId) is not { } wire || !tombstoning.Remove(wire))
            return;
        var row = RowOf(pass, wire);
        if (row.DeletedAt is not null)
            return;
        // 使用者選了不上傳：還沒送出的一律換成 {}（政策轉換時已換過，這裡再換一次是冪等的；不論寫好了沒有，合併前審查 P1、第二輪 PRIV-1、PRIV-2）
        ScrubPendingContent(wire, pass, appends, allKinds: true);
        if (!written.Ok || !CanPublish())
        {
            Log.Warn(written.Ok
                ? $"對話 {wire} 改成只在這台電腦：現在沒有 conv1，下一次 conv1 連線再送伺服器刪除"
                : $"對話 {wire} 改成只在這台電腦：伺服器刪除的旁檔寫不成功，先不送刪除（之後重整再試）");
            pass.Redo[wire] = true;
            return;
        }

        var epoch = lastMeta.TryGetValue(wire, out var meta) ? ConversationWire.EpochOf(meta.Meta) : ConversationIds.DefaultEpoch;
        // conv.deleted 接在換成 {} 的列之後
        Add(pass, appends, projector.Deleted(wire, epoch));
        var now = NowMs();
        pass.Metas[wire] = row with { DeletedAt = now, Blocked = BlockedLocalOnly, BlockedAt = now };
        pass.AfterCommit.Add(() => ForgetConversation(wire));
    }

    // ───────────────────────────── 要 UI 做的事 ─────────────────────────────

    /// <summary>要這些對話的中繼資料（每次最多 MetaSlice 個；回應以 MetaBatch 放回盒子）</summary>
    private void RequestMetaFor(IReadOnlyList<string> wires)
    {
        if (wires.Count == 0)
            return;
        if (Conversations?.UiPort is not { } port)
        {
            foreach (var wire in wires)
            {
                awaitingMeta.Remove(wire);
                redoPending.Remove(wire);
                retryPending.Remove(wire);
            }

            Log.Warn($"對話發布者沒有掛上：{wires.Count} 個對話的中繼資料要不到，這些變化先不發布");
            return;
        }

        var now = NowMs();
        for (var start = 0; start < wires.Count; start += PublishTimingRules.MetaSlice)
        {
            var slice = wires.Skip(start).Take(PublishTimingRules.MetaSlice).ToList();
            foreach (var wire in slice)
                metaRequestedAt[wire] = now;
            port.RequestMeta(slice.Select(w => localIds.GetValueOrDefault(w, w)).ToList());
        }
    }

    /// <summary>從頭依 Ord 走一次這個對話的項目快照（正在走的就走完再走一次）</summary>
    private void StartWalk(string wire, string local, bool clearRedo)
    {
        if (walks.TryGetValue(wire, out var running))
        {
            running.Again = true;
            running.ClearRedo |= clearRedo;
            return;
        }

        if (Conversations?.UiPort is not { } port)
            return;
        var hint = store!.OrdHintOf(wire);
        walks[wire] = new PageWalk(local, hint, clearRedo);
        port.RequestItems(local, 0, PublishTimingRules.ItemsPage, hint);
    }

    /// <summary>改成只在這台電腦（D25）：先經 UI 寫伺服器刪除的旁檔，結果以 ServerDeletedWritten 回來</summary>
    private void BeginTombstone(string wire, string local, ConversationPass pass)
    {
        DropWalk(wire);
        awaitingMeta.Remove(wire);
        DropRunning(wire);
        if (!tombstoning.Add(wire))
            return;
        if (Conversations?.UiPort is not { } port)
        {
            tombstoning.Remove(wire);
            pass.Redo[wire] = true;
            return;
        }

        port.MarkServerDeleted(local);
    }

    // ───────────────────────────── BatchAck 的結果（6.4、D14） ─────────────────────────────

    private void ApplyConversationResults(SyncConnection conn, IReadOnlyList<BatchEventResult> results)
    {
        var conv1 = DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1);
        var now = NowMs();
        var pass = new ConversationPass();
        var redoAfterAck = new List<string>();
        var pausedNow = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (result is null)
                continue;
            var index = IndexOf(result.DeviceSeq);
            if (index < 0)
                continue;
            var entry = outbox[index];
            if (entry.ConversationId is not { } wire || !ConversationEventKinds.IsConversationKind(entry.Kind))
                continue;
            var row = RowOf(pass, wire);
            var code = result.Rejected;
            if (code is not null && (result.Owner is not null || result.Epoch is not null))
            {
                // 只記錄、不拿來判斷（6.4）
                Log.Info($"對話 {wire} 的 {entry.Kind} 被擋（{CatalogSyncRules.CleanText(code, 64)}）：伺服器記的擁有者 {CatalogSyncRules.CleanText(result.Owner, 64)}、epoch {result.Epoch}");
            }

            var decision = conv1 && code is SyncErrorCodes.Invalid or SyncErrorCodes.TooLarge
                           && (entry.Kind != ConversationEventKinds.Deleted && WasScrubbed(entry))
                ? new RejectDecision(RejectAction.Redo, null)
                : ConversationRejectRules.Classify(code, entry.Kind, conv1, row.CreatedAcked, row.CreatedRetries);
            if (code is not null && decision.Action != RejectAction.Accepted)
                row = ForgetRejected(wire, entry, row);
            switch (decision.Action)
            {
                case RejectAction.Accepted:
                    // 伺服器只接受「對話在、作者是擁有者」的事件（不在時只有 created、meta 會建立它）：被接受的任何一筆都等於 created 已確認——
                    // 送 created 的那一批在途時斷線、Rewind 標成已提交而沒有結果，或之後以 conv.meta 建立的（第二輪合併前審查 F2）
                    if (code is null && entry.Kind != ConversationEventKinds.Deleted && !row.CreatedAcked)
                    {
                        row = row with { CreatedAcked = true };
                        if (NeedsRedo(pass, wire))
                            redoAfterAck.Add(wire);
                    }

                    if (entry.Kind == ConversationEventKinds.Deleted)
                        pass.Redo[wire] = false;
                    break;
                case RejectAction.Block:
                    row = Block(wire, row, code!, now);
                    QueueNotice(PublishNoticeKind.Blocked, wire);
                    break;
                case RejectAction.BlockNotOwner:
                    row = Block(wire, row, code == ConversationRejectCodes.NotOwner ? ConversationRejectCodes.NotOwner : BlockedCreatedRetries, now);
                    QueueNotice(PublishNoticeKind.NotOwner, wire);
                    break;
                case RejectAction.PauseDlp:
                    // 保留碼的防禦（伺服器不回 dlp，答 5）：經 UI 改成 paused，之後存檔帶來的 meta 走 MetaOnly
                    if (pausedNow.Add(wire))
                        Conversations!.UiPort?.PauseForDlp(localIds.GetValueOrDefault(wire, wire));
                    QueueNotice(PublishNoticeKind.DlpPaused, wire);
                    break;
                case RejectAction.Redo:
                    pass.Redo[wire] = true;
                    break;
                case RejectAction.RetryCreated:
                    pass.Redo[wire] = true;
                    // 同一個對話在 retry_at 之前收到的其他 invalid 不再加次數
                    if (row.RetryAt is not { } at || now >= at)
                        row = row with { CreatedRetries = row.CreatedRetries + 1, RetryAt = now + decision.RetryAfterMs!.Value };
                    break;
                case RejectAction.Bug:
                    Log.Error($"對話 {wire} 的 {entry.Kind} 被伺服器以 {CatalogSyncRules.CleanText(code, 64)} 擋下（程式錯誤）：封鎖到 app 換版或伺服器換實例");
                    row = Block(wire, row, BugReason, now);
                    QueueNotice(PublishNoticeKind.Blocked, wire);
                    break;
                default:
                    throw new InvalidOperationException("不認得的拒絕處置");
            }

            pass.Metas[wire] = row;
        }

        FlushPass(pass);
        if (redoAfterAck.Count > 0)
            RequestRedo(redoAfterAck);
        ScheduleCreatedRetry();
    }

    /// <summary>被拒的那一筆的摘要清掉（下一次重整重送；項目的列與 Ord 保留，D11、D14）</summary>
    private PublishMetaRow ForgetRejected(string wire, OutboxEntry entry, PublishMetaRow row)
    {
        InvalidateReconcile(wire);
        if (PublishKeyRules.TryParseItem(entry.CoalesceKey, out _, out var itemId))
        {
            // 鍵是 conv.item 與 conv.itemRemoved 共用的：被擋下的是刪除時連「已送 itemRemoved」一起清（合併前審查 F4）
            store!.ForgetDigests(wire, itemId, resetRemoved: entry.Kind == ConversationEventKinds.ItemRemoved);
            return row with { ItemsFileSha256 = null };
        }

        return entry.Kind switch
        {
            ConversationEventKinds.Created or ConversationEventKinds.Meta => row with { MetaSha256 = null },
            ConversationEventKinds.Queue => row with { QueueSha256 = null },
            _ => row,
        };
    }

    /// <summary>沒有 conv1 時或政策降級時換成 {} 的列（之後在 conv1 連線上送出、被 invalid 擋下：不是程式錯誤，記重整）</summary>
    private bool WasScrubbed(OutboxEntry entry)
    {
        if (entry.PayloadSha256 == EmptyConversationSha)
            return true;
        if (!OutboxSliceRules.IsSkeleton(entry))
            return false;
        try
        {
            return store!.LoadOutboxPayloads([entry.DeviceSeq])[0].PayloadSha256 == EmptyConversationSha;
        }
        catch (Exception ex)
        {
            Log.Warn($"讀回被擋的對話列 {entry.DeviceSeq} 失敗：{ex.Message}");
            return false;
        }
    }

    private PublishMetaRow Block(string wire, PublishMetaRow row, string reason, long now)
    {
        DropWalk(wire);
        awaitingMeta.Remove(wire);
        DropRunning(wire);
        Log.Warn($"對話 {wire} 封鎖（{CatalogSyncRules.CleanText(reason, 64)}）：之後不再寫它的事件");
        return row with { Blocked = reason, BlockedAt = now };
    }

    private string BugReason => BlockedBugPrefix + args.AppVersion;

    // ───────────────────────────── 上線、重新發布、補送 created ─────────────────────────────

    /// <summary>
    /// conv1 連線進 Live（D13、D22）：記了 needs_redo 的墓碑補送 conv.deleted（確認後清掉）；其他記了 needs_redo 的對話排進重整佇列
    /// （步 12：整份重新發布、摘要相同的項目不重送；一般事件優先，沒載入的唯讀讀檔）
    /// </summary>
    private void ResumeRedo()
    {
        var rows = Rows();
        var pass = new ConversationPass();
        var appends = new List<OutboxAppend>();
        foreach (var wire in RedoSet().Order(StringComparer.Ordinal).ToList())
        {
            rows.TryGetValue(wire, out var row);
            if (row?.DeletedAt is not null)
            {
                var epoch = lastMeta.TryGetValue(wire, out var meta) ? ConversationWire.EpochOf(meta.Meta) : ConversationIds.DefaultEpoch;
                Add(pass, appends, projector.Deleted(wire, epoch));
                // 送出之後才丟掉記憶體的狀態（沒有 conv1 時記的墓碑，中繼資料留到這裡取 epoch）
                pass.AfterCommit.Add(() => ForgetConversation(wire));
                continue;
            }

            if (row?.Blocked is not null || walks.ContainsKey(wire) || tombstoning.Contains(wire) || redoPending.Contains(wire))
                continue;
            EnqueueReconcile(new ReconcileCandidate(wire, ReconcileReason.Redo, UpdatedAtMs(lastMeta.GetValueOrDefault(wire)), Estimate(wire)));
        }

        WriteConversation(pass, appends);
    }

    /// <summary>
    /// conv1 連線進 Live：created 寫過、還沒被確認、outbox 裡也沒有它還沒完成的 conv.created 的對話補一筆 conv.created（送 created 的那一批在途時斷線、
    /// Rewind 標成已提交而沒有結果，或確認與發布狀態的兩個交易之間當機；伺服器上已有就照 ReplaceMeta 接受，確認之後即時的路徑才整份走，
    /// 第二輪合併前審查 F2）。invalid 之後的補送（retry_at，D14）不在這裡；這次啟動還沒有中繼資料的等重整那一批（或要一次）再補
    /// </summary>
    private void ReconfirmCreated()
    {
        var unconfirmed = Rows().Values
            .Where(r => r.CreatedWritten && !r.CreatedAcked && r.Blocked is null && r.DeletedAt is null && r.RetryAt is null && !tombstoning.Contains(r.ConversationId))
            .Select(r => r.ConversationId).Order(StringComparer.Ordinal).ToList();
        if (unconfirmed.Count == 0)
            return;
        var pendingCreated = new HashSet<string>(StringComparer.Ordinal);
        for (var i = FirstActive(); i < outbox.Count; i++)
        {
            var entry = outbox[i];
            if (!entry.IsDone && entry.Kind == ConversationEventKinds.Created && entry.ConversationId is { } owner)
                pendingCreated.Add(owner);
        }

        var pass = new ConversationPass();
        var appends = new List<OutboxAppend>();
        var request = new List<string>();
        foreach (var wire in unconfirmed)
        {
            if (pendingCreated.Contains(wire))
                continue;
            if (lastMeta.TryGetValue(wire, out var meta))
            {
                if (!meta.IsDraft && !meta.IsReplica)
                    EmitCreated(wire, meta, pass, appends);
            }
            else if (retryPending.Add(wire) && !refreshNeeded)
            {
                request.Add(wire);
            }
        }

        if (appends.Count > 0 || request.Count > 0)
            Log.Info($"{appends.Count + request.Count} 個對話的 conv.created 寫過、伺服器的確認沒收到：補送 conv.created");
        WriteConversation(pass, appends);
        RequestMetaFor(request);
    }

    /// <summary>created 確認了、而且記了 needs_redo 的對話：排進重整佇列（步 12）</summary>
    private void RequestRedo(IReadOnlyList<string> wires)
    {
        foreach (var wire in wires)
        {
            if (!walks.ContainsKey(wire) && !redoPending.Contains(wire))
                EnqueueReconcile(new ReconcileCandidate(wire, ReconcileReason.Redo, UpdatedAtMs(lastMeta.GetValueOrDefault(wire)), Estimate(wire)));
        }
    }

    /// <summary>排下一次補送 conv.created（最早的 retry_at；沒有就不排，閒置時沒有計時器）</summary>
    private void ScheduleCreatedRetry()
    {
        CancelTimer(ref createdRetryTimer);
        if (store is null || !store.ConversationTablesReady)
            return;
        long? due = null;
        foreach (var row in Rows().Values)
        {
            if (IsRetryDue(row) is { } at && (due is null || at < due))
                due = at;
        }

        if (due is not { } next)
            return;
        createdRetryTimer = CreateTimer(TimeSpan.FromMilliseconds(Math.Max(0, next - NowMs())), OnCreatedRetryDue);
    }

    /// <summary>這一列要補送 created 的時間（沒有要補的回 null）</summary>
    private long? IsRetryDue(PublishMetaRow row)
    {
        if (row.CreatedAcked || row.RetryAt is not { } at || row.Blocked is not null || row.DeletedAt is not null)
            return null;
        return retriedAt.TryGetValue(row.ConversationId, out var fired) && fired == at ? null : at;
    }

    private void OnCreatedRetryDue()
    {
        CancelTimer(ref createdRetryTimer);
        if (!opened || stopping || store is null || Conversations is null || !store.ConversationTablesReady)
            return;
        if (!CanPublish())
            return;
        var now = NowMs();
        var pass = new ConversationPass();
        var appends = new List<OutboxAppend>();
        var request = new List<string>();
        foreach (var row in Rows().Values.ToList())
        {
            if (IsRetryDue(row) is not { } at || at > now)
                continue;
            var wire = row.ConversationId;
            retriedAt[wire] = at;
            if (lastMeta.TryGetValue(wire, out var meta))
            {
                Log.Info($"對話 {wire} 的事件被伺服器以 invalid 擋下（還不認得它）：補送 conv.created（第 {row.CreatedRetries} 次）");
                EmitCreated(wire, meta, pass, appends);
            }
            else if (retryPending.Add(wire))
            {
                request.Add(wire);
            }
        }

        WriteConversation(pass, appends);
        RequestMetaFor(request);
        ScheduleCreatedRetry();
    }

    private bool TryUpgradeTables()
    {
        var now = NowMs();
        if (lastUpgradeTryMs is { } last && now - last < UpgradeRetryIntervalMs)
            return false;
        lastUpgradeTryMs = now;
        if (!store!.RetryUpgrade())
            return false;
        Log.Info("同步資料庫的對話表升級完成：下一條連線起宣告 conv1");
        try
        {
            // 開啟時表不在、沒記下發布者的三個 kv：表剛建好、還沒有任何發布狀態，記下目前的值（D36、D38 的偵測下次開啟才有效，合併前審查 F6）
            store.WritePublisherState(store.PublishServerKey(), DlpVersion);
        }
        catch (Exception ex)
        {
            Log.Warn($"對話表升級後記錄發布者的狀態失敗（下次開啟時才補記）：{ex.Message}");
        }

        ResetConversationCaches();
        return true;
    }

    /// <summary>程式錯誤的封鎖：app 換版之後解除（伺服器換實例時 BeginServerChange 已經全部解除）</summary>
    private void ClearStaleBugBlocks()
    {
        var currentReason = BugReason;
        var stale = Rows().Values.Select(r => r.Blocked)
            .Where(b => b is not null && b.StartsWith(BlockedBugPrefix, StringComparison.Ordinal) && b != currentReason)
            .Distinct(StringComparer.Ordinal).ToList();
        if (stale.Count == 0)
            return;
        var cleared = stale.Sum(reason => store!.ClearBlocks(reason));
        publishRows = null;
        Log.Info($"app 換版：解除 {cleared} 個程式錯誤的對話封鎖");
    }

    // ───────────────────────────── 彙總提示 ─────────────────────────────

    /// <summary>
    /// 同一段時間（<see cref="PublishNoticeDelay"/>）被擋的對話依種類併成一則提示、數的是對話數（D14）；
    /// not_owner（重新配對前的舊身分，D32）每次啟動最多一則
    /// </summary>
    private void QueueNotice(PublishNoticeKind kind, string wire)
    {
        if (kind == PublishNoticeKind.NotOwner && notOwnerNoticed)
            return;
        if (!pendingNotices.TryGetValue(kind, out var ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            pendingNotices[kind] = ids;
        }

        ids.Add(wire);
        noticeTimer ??= CreateTimer(PublishNoticeDelay, FlushNotices);
    }

    private void FlushNotices()
    {
        CancelTimer(ref noticeTimer);
        if (!opened || stopping)
            return;
        foreach (var (kind, ids) in pendingNotices.OrderBy(p => p.Key))
        {
            Conversations?.UiPort?.Notify(kind, ids.Count);
            if (kind == PublishNoticeKind.NotOwner)
                notOwnerNoticed = true;
        }

        pendingNotices.Clear();
    }

    // ───────────────────────────── 狀態與快取 ─────────────────────────────

    /// <summary>有 conv1 的連線，或離線時上一次進 Live 的連線有 conv1（D13）</summary>
    private bool CanPublish() => current is { Proven: true } conn ? DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1) : Armed();

    private bool Armed()
    {
        if (convArmed is null)
        {
            try
            {
                convArmed = store?.GetKv(SyncStoreSchema.ConvArmed) == "1";
            }
            catch (Exception ex)
            {
                Log.Warn($"讀取 conv_armed 失敗（當成 false）：{ex.Message}");
                return false;
            }
        }

        return convArmed.Value;
    }

    private void SetArmed(bool value)
    {
        if (Armed() == value && convArmed is not null)
            return;
        try
        {
            store!.SetKv(SyncStoreSchema.ConvArmed, value ? "1" : "0");
            convArmed = value;
        }
        catch (Exception ex)
        {
            Log.Warn($"記錄 conv_armed 失敗：{ex.Message}");
        }
    }

    private Dictionary<string, PublishMetaRow> Rows() => publishRows ??= new Dictionary<string, PublishMetaRow>(store!.LoadPublishMeta(), StringComparer.Ordinal);

    private HashSet<string> RedoSet() => redoSet ??= new HashSet<string>(store!.LoadNeedsRedo(), StringComparer.Ordinal);

    private bool NeedsRedo(ConversationPass pass, string wire) => pass.Redo.TryGetValue(wire, out var value) ? value : RedoSet().Contains(wire);

    /// <summary>這一次寫入裡的新值，沒有就是快取的，再沒有就是預設（未知）</summary>
    private PublishMetaRow RowOf(ConversationPass pass, string wire)
    {
        if (pass.Metas.TryGetValue(wire, out var pending))
            return pending;
        return Rows().TryGetValue(wire, out var row) ? row : new PublishMetaRow(wire);
    }

    private static PublishKnowledge Knowledge(PublishMetaRow row, PublishMetaSnapshot meta)
        => new(row.CreatedWritten, row.Blocked, row.DeletedAt is not null, meta.ServerDeleted, row.EverCreated);

    /// <summary>伺服器上可能有它：這個實例寫過 created，或曾經對別的實例寫過（換實例、還原之後還沒重新發布；合併前審查 F3）</summary>
    private static bool MayExistOnServer(PublishMetaRow row) => row.CreatedWritten || row.EverCreated;

    /// <summary>線上的政策（有伺服器刪除旁檔的一律 localOnly）</summary>
    private static string PolicyOf(PublishMetaSnapshot meta)
        => meta.ServerDeleted ? ConversationSyncPolicies.LocalOnly : ConversationSyncPolicies.Interpret(meta.EffectivePolicy);

    private static bool IsAnswerMessage(AgentChatItem item)
        => item.Kind == AgentItemKind.User && item.Text != null && item.Text.StartsWith(AnswerMessageRules.Prefix, StringComparison.Ordinal);

    private static IEnumerable<string> Newest(IEnumerable<(string Wire, PublishMetaSnapshot Meta)> conversations)
        => conversations.OrderByDescending(c => c.Meta.Meta.UpdatedAt).ThenBy(c => c.Wire, StringComparer.Ordinal).Take(MaxHeartbeatConversations).Select(c => c.Wire);

    private string Generation(string wire) => generations.TryGetValue(wire, out var generation) ? generation : PublishKeyRules.Generation(KeyBootId, 0);

    private void NextGeneration(string wire) => generations[wire] = PublishKeyRules.Generation(KeyBootId, ++markerSeq);

    /// <summary>合併鍵用的 bootId（不可以含冒號）</summary>
    private string KeyBootId => keyBootId ??= string.IsNullOrEmpty(args.BootId) ? "boot" : args.BootId.Replace(':', '-');

    /// <summary>刪除之後：丟掉這個對話在記憶體的狀態（之後才到的結果與變化一律丟，墓碑擋著）</summary>
    private void ForgetConversation(string wire)
    {
        lastMeta.Remove(wire);
        generations.Remove(wire);
        awaitingMeta.Remove(wire);
        metaRequestedAt.Remove(wire);
        redoPending.Remove(wire);
        retryPending.Remove(wire);
        walks.Remove(wire);
        tombstoning.Remove(wire);
        sendGateAsked.Remove(wire);
        DropRunning(wire);
        lastLiveAt.Remove(wire);
        DropReconcile(wire);
    }

    private void ResetConversationCaches()
    {
        publishRows = null;
        redoSet = null;
        walks.Clear();
        retriedAt.Clear();
        CancelTimer(ref createdRetryTimer);
        // 持久的時間點是對舊的伺服器實例算的：之後的第一個變化一律持久（步 11）
        ResetRunning();
        // 重整佇列與進行中的工作丟掉，下一次 conv1 連線進 Live 時重整、整份重送（步 12）
        ResetReconcile();
        // 複本的快取與對話游標（步 14）
        ResetReplicas(notifyUi: false);
    }

    /// <summary>不是收件匣那一輪的寫入（確認結果、上線、計時器）：有事件就與 outbox 同一個交易，沒有就單獨寫。回傳寫成了沒有</summary>
    private bool WriteConversation(ConversationPass pass, List<OutboxAppend> appends)
        => appends.Count > 0 ? AppendOutbox(appends, pass) : FlushPass(pass);

    private bool FlushPass(ConversationPass pass)
    {
        if (pass.IsEmpty || store is null)
            return true;
        IReadOnlyList<OutboxEntry> scrubbed;
        try
        {
            scrubbed = store.WritePublish(pass.ToWrite());
        }
        catch (Exception ex)
        {
            Log.Error("記錄對話的發布狀態失敗", ex);
            FailPendingReconcile();
            // 政策降級（例如改成 localOnly 那一輪沒有新的事件）的清除沒寫成：記重整、馬上補寫，補不成就在送出之前補（第二輪合併前審查 PRIV-1、PRIV-4）
            PassFailedWithScrub(pass);
            return false;
        }

        foreach (var entry in scrubbed)
            Upsert(entry);
        ConversationWriteCommitted(pass);
        return true;
    }
}
