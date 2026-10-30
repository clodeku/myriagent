using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Sync;

// WP4.3 計畫步 11（D19、D21；設計 4.4、4.5；docs/sync-protocol.md 14.3）：
// - 執行中的項目：PublishTimingRules.Decide 決定持久（第一次、變回 Running、滿 10 秒的檢查點）與即時（被觀看、live1、每個對話 ≤maxLiveHz）。
//   被擋下的（檢查點還沒到、被頻率擋下的即時更新）留最新的快照，到時再判斷一次。計時器只在有要再判斷的執行中項目時才開（閒置時沒有，S6）。
// - 觀看：伺服器的 WatchChanged 只是第一道（被入侵的伺服器可以列出任何 id），用的時候一律以本機的政策再過濾（PublishPlanRules.LiveAllowed）；
//   推給 UI 的集合（IsSeenRemotely 與發布者的合併延遲用）再加上 created 已確認。連線丟掉就清空、推空集合。
// - 即時更新：投影（MaskItem、TryItem、Fits、CheckRelay）之後經自己的 PostLive 排回引擎執行緒，OnLiveRelay 再檢查一次條件才以連線的 LiveRelayAsync 送出；
//   沒有 live1、沒人看就丟（即時更新可以丟，持久事件才是準）。
// - 增量（live2，sync-protocol 14.3、docs/plans/2026-10-23-live-delta-and-replica-live.md 3.3）：每一筆（對話, ord）記最後送出的那一版（遮蔽之後的長欄位），
//   下一則只送長欄位從共同前綴之後的新內容；增量不比整則省一半就送整則。基底屬於這條連線（伺服器以連線為鍵），連線關了就清空。
//   伺服器的 LiveResync：base／evicted＝忘掉基底、補送一則整則；remask＝這一筆到離開 Running 為止只送整則。頻率依整則大小降（LiveRules.DeltaIntervalMs）。
// 時間一律是單調時鐘（不比較電腦的時鐘）。狀態只在引擎執行緒改、不加鎖
internal sealed partial class SyncEngine
{
    /// <summary>伺服器的 WatchChanged 一次最多收幾個對話（WP4.2 的上限；多的略過）</summary>
    internal const int MaxWatchedConversations = 500;

    private static readonly IReadOnlySet<string> NoWatched = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>這條連線最後一次 WatchChanged 的對話（線上 id，只留格式合格的；用的時候一律再以本機的政策過濾）</summary>
    private HashSet<string> serverWatch = new(StringComparer.Ordinal);

    /// <summary>最後推給 UI 的觀看集合（本機 id；IsSeenRemotely 與發布者的合併延遲用）</summary>
    private IReadOnlySet<string> uiWatched = NoWatched;

    /// <summary>執行中的項目（以（對話, 項目）為鍵）：最新的快照、上一次持久的時間、還沒寫或還沒送的變化</summary>
    private readonly Dictionary<(string Wire, string ItemId), RunningItem> runningItems = new();

    /// <summary>每個對話上一則即時更新的時間（單調時鐘毫秒）</summary>
    private readonly Dictionary<string, long> lastLiveAt = new(StringComparer.Ordinal);

    /// <summary><c>live2</c>：每個對話上一則即時更新整則的 UTF-8 位元組數（依大小降頻）</summary>
    private readonly Dictionary<string, int> lastLiveBytes = new(StringComparer.Ordinal);

    /// <summary><c>live2</c>：每一筆（對話, ord）最後送出的那一版（伺服器以它重組下一則增量）；屬於 <see cref="liveBasesConn"/> 這條連線</summary>
    private readonly Dictionary<(string Wire, long Ord), LiveBase> liveBases = new();

    private SyncConnection? liveBasesConn;

    /// <summary>一筆的基底：<paramref name="FullOnly"/>＝伺服器判定外洩（<c>remask</c>），這一筆到離開 Running 為止只送整則</summary>
    private sealed record LiveBase(long Epoch, long Ver, LiveLongFields Fields, bool FullOnly);

    private ITimer? runningTimer;
    private long? runningTimerDue;
    private long? monoOrigin;
    private bool watchOverflowLogged;

    /// <summary>一筆執行中的項目</summary>
    private sealed class RunningItem(AgentChatItem snapshot)
    {
        /// <summary>最新的快照（盒子裡的複本，引擎自己的）</summary>
        public AgentChatItem Snapshot { get; set; } = snapshot;

        /// <summary>上一次寫進 outbox（已提交）的持久版本的時間；null＝這次啟動還沒寫過 Running 的持久版本</summary>
        public long? DurableMs { get; set; }

        /// <summary>有還沒寫的變化（等檢查點）</summary>
        public bool DurableDirty { get; set; }

        /// <summary>有還沒送的即時更新（被頻率擋下）</summary>
        public bool LiveDirty { get; set; }

        /// <summary>下次要再判斷的時間（單調時鐘毫秒）；null＝不用</summary>
        public long? RecheckAt { get; set; }
    }

    // ───────────────────────────── 登記點 ─────────────────────────────

    partial void OnWatchChanged(SyncConnection conn, WatchChanged changed)
    {
        if (Conversations is null || store is null || !store.ConversationTablesReady || !IsCurrent(conn))
            return;
        try
        {
            ApplyWatch(changed);
        }
        catch (Exception ex)
        {
            Log.Error("處理伺服器的觀看清單失敗", ex);
        }

        // 觀看清單變了：記活動、需要時預熱、排閒置釋放的計時器（WP4.9 步 5、合併前審查 DLP-2）。觀看期間 OnDlpTrimDue 不釋放、會再排；
        // 清空之後靜置期滿才釋放並做一次積極回收（只看不改的對話不經 HandleConversationSignal，不排的話預熱建的樣式一直留著）
        NoteDlpActivity();
    }

    partial void OnLiveRelay(LiveRelay relay)
    {
        if (Conversations is null || store is null || !store.ConversationTablesReady)
            return;
        // 沒有 live1（或沒連線）、沒人看、政策不允許：丟掉（即時更新只是提示）；live2 的基底跟著忘掉（伺服器沒收到這一版）
        if (current is not { Proven: true } conn || !LiveNegotiated(conn))
            return;
        var wire = relay.ConversationId;
        if (LiveRules.CheckRelay(relay, out var detail, LiveRules.MaxItemBytes(Live2Negotiated(conn))) is { } code)
        {
            Log.Warn($"即時更新不合格（{code}：{detail}）：不送");
            ForgetBase(wire, relay.Ord);
            return;
        }

        if (!serverWatch.Contains(wire) || !lastMeta.TryGetValue(wire, out var meta) || !Rows().TryGetValue(wire, out var row)
            || !LiveAllowedFor(wire, meta, row))
        {
            ForgetBase(wire, relay.Ord);
            return;
        }
        Spawn(async () =>
        {
            try
            {
                await conn.Session!.LiveRelayAsync(relay, conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                Log.Warn($"伺服器拒收對話 {wire} 的即時更新（{ex.Failure.Code}）：只記錄、不重連");
            }
            catch (SyncTransportException ex)
            {
                Fail(conn, ex.Failure);
            }
        }, conn);
    }

    /// <summary>增量（<see cref="SendLive"/> 排進來的；引擎執行緒）：條件同 <see cref="OnLiveRelay"/>，不送就忘掉基底（下一則送整則）</summary>
    private void DeliverLiveDelta(LiveDelta delta)
    {
        if (!opened || stopping || Conversations is null || store is null || !store.ConversationTablesReady)
            return;
        var wire = delta.ConversationId;
        if (current is not { Proven: true } conn || !Live2Negotiated(conn) || !ReferenceEquals(conn, liveBasesConn) || !serverWatch.Contains(wire)
            || !lastMeta.TryGetValue(wire, out var meta) || !Rows().TryGetValue(wire, out var row) || !LiveAllowedFor(wire, meta, row))
        {
            ForgetBase(wire, delta.Ord);
            return;
        }

        Spawn(async () =>
        {
            try
            {
                await conn.Session!.LiveDeltaAsync(delta, conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                Log.Warn($"伺服器拒收對話 {wire} 的即時更新增量（{ex.Failure.Code}）：只記錄、不重連");
            }
            catch (SyncTransportException ex)
            {
                Fail(conn, ex.Failure);
            }
        }, conn);
        Probe(nameof(DeliverLiveDelta), delta);
    }

    partial void OnLiveResync(SyncConnection conn, LiveResync resync)
    {
        if (Conversations is null || store is null || !store.ConversationTablesReady || !IsCurrent(conn) || !ReferenceEquals(conn, liveBasesConn))
            return;
        if (resync is null || !ConversationIds.IsWireId(resync.ConversationId) || resync.Ord < 1 || !LiveResyncReasons.IsKnown(resync.Reason))
            return;
        var wire = resync.ConversationId;
        if (resync.Reason == LiveResyncReasons.Remask)
        {
            // 伺服器已經轉送了遮蔽版、不留基底：這一筆之後只送整則（伺服器每則照樣重遮），不必補送
            liveBases[(wire, resync.Ord)] = new LiveBase(0, 0, LiveLongFields.Empty, FullOnly: true);
            return;
        }

        // base／evicted：伺服器丟了上一則（或基底被擠掉）。這一筆還在執行中就照頻率補送一則整則，觀看者不會停在舊的內容直到下一個片段
        ForgetBase(wire, resync.Ord);
        var now = MonoMs();
        var any = false;
        foreach (var ((itemWire, _), state) in runningItems)
        {
            if (itemWire != wire || state.Snapshot.Ord != resync.Ord)
                continue;
            state.LiveDirty = true;
            UpdateRecheck(wire, state, now);
            any = true;
        }

        if (any)
            ScheduleRunningTimer();
    }

    /// <summary>忘掉一筆的基底（下一則送整則）；<c>remask</c> 的只送整則標記留著</summary>
    private void ForgetBase(string wire, long ord)
    {
        if (liveBases.TryGetValue((wire, ord), out var last) && !last.FullOnly)
            liveBases.Remove((wire, ord));
    }

    partial void OnConnectionClosedForConversations(SyncConnection conn)
    {
        if (Conversations is null)
            return;
        // 觀看清單屬於那條連線：清空、推空集合到 UI（之後本機照常提醒）；還沒送的即時更新丟掉，檢查點照舊；增量的基底也屬於那條連線
        serverWatch = new HashSet<string>(StringComparer.Ordinal);
        liveBases.Clear();
        liveBasesConn = null;
        var now = MonoMs();
        foreach (var ((wire, _), state) in runningItems)
        {
            state.LiveDirty = false;
            UpdateRecheck(wire, state, now);
        }

        ScheduleRunningTimer();
        RefreshWatched();
    }

    // ───────────────────────────── 執行中的項目（Emit 呼叫） ─────────────────────────────

    /// <summary>
    /// 掛鉤來的一筆項目變化要不要寫持久版本（回傳）；即時更新在這裡就送。不是 Running 的一律持久、不再送即時；
    /// Running 的照 <see cref="PublishTimingRules.Decide"/>，沒寫、沒送的留最新的快照，到時再判斷
    /// </summary>
    private bool DecideItem(string wire, long epoch, PublishMetaSnapshot meta, AgentChatItem item, PublishMetaRow row)
    {
        if (item.Status != AgentItemStatus.Running)
        {
            DropRunning(wire, item.Id);
            return true;
        }

        var now = MonoMs();
        var key = (wire, item.Id);
        runningItems.TryGetValue(key, out var state);
        var liveOk = LiveOk(wire, meta, row);
        var decision = PublishTimingRules.Decide(AgentItemStatus.Running, state?.DurableMs is null ? null : AgentItemStatus.Running, state?.DurableMs, now,
            liveOk, LastLive(wire), limits.MaxLiveHz, LiveIntervalFor(wire));
        if (state is null)
        {
            state = new RunningItem(item);
            runningItems[key] = state;
        }

        state.Snapshot = item;
        state.DurableDirty = !decision.Durable;
        state.LiveDirty = liveOk && !decision.Live;
        if (decision.Live)
            SendLive(wire, epoch, item);
        UpdateRecheck(wire, state, now);
        ScheduleRunningTimer();
        return decision.Durable;
    }

    /// <summary>
    /// 寫了一筆持久的項目：Running 的記下時間（提交之後，檢查點從這裡算）；不是 Running 的不必再記。
    /// 分頁快照（<paramref name="fromWalk"/>）寫的 Running 項目也記，之後的變化才照檢查點的間隔
    /// </summary>
    private void NoteDurable(string wire, AgentChatItem item, bool fromWalk, ConversationPass pass)
    {
        var key = (wire, item.Id);
        if (item.Status != AgentItemStatus.Running)
        {
            if (fromWalk)
                DropRunning(wire, item.Id);
            return;
        }

        if (fromWalk && !runningItems.ContainsKey(key))
            runningItems[key] = new RunningItem(item);
        var at = MonoMs();
        pass.AfterCommit.Add(() => MarkDurable(key, at));
    }

    private void MarkDurable((string Wire, string ItemId) key, long at)
    {
        if (runningItems.TryGetValue(key, out var state))
            state.DurableMs = at;
    }

    /// <summary>
    /// 投影一則即時更新並經自己的 PostLive 送出（OnLiveRelay 再檢查一次條件）；太大或不合格的不送。協商出 <c>live2</c> 時上限是 256 KB，
    /// 有基底而且增量比整則省一半以上就改送增量（同一個佇列排進引擎執行緒，順序和整則一樣）
    /// </summary>
    private void SendLive(string wire, long epoch, AgentChatItem item)
    {
        var live2 = current is { Proven: true } conn && Live2Negotiated(conn);
        if (projector.Live(wire, epoch, item, ++nextVer, LiveRules.MaxItemBytes(live2), out var projected) is not { } relay)
            return;
        lastLiveAt[wire] = MonoMs();
        if (!live2 || projected is null)
        {
            PostLive(relay);
            return;
        }

        lastLiveBytes[wire] = Encoding.UTF8.GetByteCount(relay.Item);
        if (!ReferenceEquals(liveBasesConn, current))
        {
            liveBases.Clear();
            liveBasesConn = current;
        }

        var key = (wire, relay.Ord);
        liveBases.TryGetValue(key, out var last);
        liveBases[key] = new LiveBase(epoch, relay.Ver, LiveDeltaRules.FieldsOf(projected), last?.FullOnly ?? false);
        if (last is { FullOnly: false, Ver: > 0 } basis && basis.Epoch == epoch)
        {
            var delta = LiveDeltaRules.Encode(wire, epoch, relay.Ord, relay.Ver, basis.Ver, basis.Fields, projected);
            if (!LiveDeltaRules.ShouldSendFull(delta, relay.Item.Length))
            {
                thread.Post(() => DeliverLiveDelta(delta));
                return;
            }
        }

        PostLive(relay);
    }

    /// <summary>計時器到了：到期的執行中項目再判斷一次（寫檢查點、送被頻率擋下的最新版本）</summary>
    private void OnRunningTimer()
    {
        CancelTimer(ref runningTimer);
        runningTimerDue = null;
        if (!opened || stopping || store is null || Conversations is null || !store.ConversationTablesReady)
            return;
        try
        {
            ProcessRunningDue();
        }
        catch (Exception ex)
        {
            Log.Error("處理執行中項目的檢查點與即時更新失敗", ex);
        }

        ScheduleRunningTimer();
    }

    /// <summary>到期的執行中項目：檢查點只寫有沒寫的變化的、即時只送有沒送的變化的（沒變就不必再寫、再送）</summary>
    private void ProcessRunningDue()
    {
        var now = MonoMs();
        var pass = new ConversationPass();
        var appends = new List<OutboxAppend>();
        var canPublish = CanPublish();
        foreach (var (key, state) in runningItems.ToList())
        {
            if (state.RecheckAt is not { } at || at > now)
                continue;
            var wire = key.Wire;
            var row = RowOf(pass, wire);
            if (!lastMeta.TryGetValue(wire, out var meta) || !PublishesItems(wire, meta, row))
            {
                runningItems.Remove(key);
                continue;
            }

            if (!canPublish)
            {
                // 沒有 conv1（D13）：不寫新的事件；已知的對話記成要重新發布，下一次 conv1 連線補送
                pass.Redo[wire] = true;
                runningItems.Remove(key);
                continue;
            }

            var epoch = ConversationWire.EpochOf(meta.Meta);
            var liveOk = LiveOk(wire, meta, row);
            var decision = PublishTimingRules.Decide(AgentItemStatus.Running, state.DurableMs is null ? null : AgentItemStatus.Running, state.DurableMs, now,
                liveOk, LastLive(wire), limits.MaxLiveHz, LiveIntervalFor(wire));
            if (decision.Live && state.LiveDirty)
            {
                state.LiveDirty = false;
                SendLive(wire, epoch, state.Snapshot);
            }

            if (!liveOk)
                state.LiveDirty = false;
            if (decision.Durable && state.DurableDirty)
            {
                state.DurableDirty = false;
                if (projector.Item(wire, epoch, state.Snapshot, ConversationProjector.NothingScrubbed, ++nextVer) is { } projected)
                {
                    Add(pass, appends, projected.Append);
                    pass.Items.Add(projected.Record);
                    NoteDurable(wire, state.Snapshot, fromWalk: false, pass);
                }
            }

            UpdateRecheck(wire, state, now);
        }

        WriteConversation(pass, appends);
    }

    /// <summary>這個對話的項目現在可以寫（已知、沒有封鎖或墓碑、不在寫伺服器刪除的旁檔、政策發 item 類、不是草稿或複本）</summary>
    private bool PublishesItems(string wire, PublishMetaSnapshot meta, PublishMetaRow row)
        => row.CreatedWritten && row.DeletedAt is null && row.Blocked is null && !tombstoning.Contains(wire) && !meta.IsDraft && !meta.IsReplica
           && ConversationSyncPolicies.Publishes(PolicyOf(meta), ConversationEventKinds.Item);

    /// <summary>下次要再判斷的時間：有沒寫的變化看檢查點、有沒送的即時看頻率；都沒有就不用</summary>
    private void UpdateRecheck(string wire, RunningItem state, long now)
    {
        long? at = null;
        if (state.DurableDirty)
            at = state.DurableMs is { } durable ? durable + PublishTimingRules.CheckpointIntervalMs : now;
        if (state.LiveDirty)
        {
            var liveAt = lastLiveAt.TryGetValue(wire, out var last) ? last + LiveIntervalFor(wire) : now;
            at = at is { } a ? Math.Min(a, liveAt) : liveAt;
        }

        state.RecheckAt = at;
    }

    /// <summary>計時器排到最早要再判斷的時間（沒有就關掉：閒置時沒有計時器）</summary>
    private void ScheduleRunningTimer()
    {
        long? due = null;
        foreach (var state in runningItems.Values)
        {
            if (state.RecheckAt is { } at && (due is null || at < due))
                due = at;
        }

        if (due is not { } next)
        {
            CancelTimer(ref runningTimer);
            runningTimerDue = null;
            return;
        }

        if (runningTimer is not null && runningTimerDue == next)
            return;
        CancelTimer(ref runningTimer);
        runningTimerDue = next;
        runningTimer = CreateTimer(TimeSpan.FromMilliseconds(Math.Max(0, next - MonoMs())), OnRunningTimer);
    }

    /// <summary>不再追這個對話（或其中一筆項目）的執行中狀態：封鎖、墓碑、政策降級、刪除、移除</summary>
    private void DropRunning(string wire, string? itemId = null)
    {
        if (runningItems.Count == 0)
            return;
        if (itemId is not null)
        {
            if (!runningItems.Remove((wire, itemId), out var state))
                return;
            if (state.Snapshot.Ord is { } ord)
                liveBases.Remove((wire, ord));
        }
        else
        {
            var keys = runningItems.Keys.Where(k => k.Wire == wire).ToList();
            if (keys.Count == 0)
                return;
            foreach (var key in keys)
                runningItems.Remove(key);
            foreach (var key in liveBases.Keys.Where(k => k.Wire == wire).ToList())
                liveBases.Remove(key);
            lastLiveBytes.Remove(wire);
        }

        ScheduleRunningTimer();
    }

    private void ResetRunning()
    {
        liveBases.Clear();
        if (runningItems.Count == 0)
            return;
        runningItems.Clear();
        ScheduleRunningTimer();
    }

    // ───────────────────────────── 觀看 ─────────────────────────────

    /// <summary>
    /// 伺服器的觀看清單（整份取代，D19）：只留格式合格的 id（最多 <see cref="MaxWatchedConversations"/> 個），用的時候再以本機政策過濾。
    /// 這次啟動還沒看過中繼資料的已知對話先要中繼資料；剛被看的對話的執行中項目現在就送
    /// </summary>
    private void ApplyWatch(WatchChanged changed)
    {
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in changed.ConversationIds ?? [])
        {
            if (!ConversationIds.IsWireId(id))
                continue;
            if (next.Count >= MaxWatchedConversations)
            {
                if (!watchOverflowLogged)
                {
                    watchOverflowLogged = true;
                    Log.Warn($"伺服器的觀看清單超過 {MaxWatchedConversations} 個對話：多的略過");
                }

                break;
            }

            next.Add(id);
        }

        var added = new HashSet<string>(next.Where(id => !serverWatch.Contains(id)), StringComparer.Ordinal);
        serverWatch = next;

        var rows = Rows();
        var need = next.Where(id => !lastMeta.ContainsKey(id) && !metaRequestedAt.ContainsKey(id) && !tombstoning.Contains(id)
                                    && rows.TryGetValue(id, out var r) && r.CreatedWritten && r.Blocked is null && r.DeletedAt is null)
            .Order(StringComparer.Ordinal).ToList();
        RequestMetaFor(need);

        if (added.Count > 0)
        {
            var now = MonoMs();
            var any = false;
            foreach (var ((wire, _), state) in runningItems)
            {
                if (!added.Contains(wire))
                    continue;
                state.LiveDirty = true;
                state.RecheckAt = now;
                any = true;
            }

            if (any)
                ProcessRunningDue();
        }

        ScheduleRunningTimer();
        RefreshWatched();
    }

    /// <summary>
    /// 推給 UI 的觀看集合（本機 id）重算，變了才推：伺服器的觀看清單裡、以本機政策判斷可以送即時更新（LiveAllowed）、而且 created 已確認
    /// （確認與封鎖只有引擎知道，D19）。沒有協商出 conv1＋live1 的連線一律是空的
    /// </summary>
    private void RefreshWatched()
    {
        if (Conversations is null || store is null || (serverWatch.Count == 0 && uiWatched.Count == 0))
            return;
        var next = new HashSet<string>(StringComparer.Ordinal);
        if (store.ConversationTablesReady && current is { Proven: true } conn && LiveNegotiated(conn))
        {
            var rows = Rows();
            foreach (var wire in serverWatch)
            {
                if (lastMeta.TryGetValue(wire, out var meta) && rows.TryGetValue(wire, out var row) && row.CreatedAcked && LiveAllowedFor(wire, meta, row))
                    next.Add(meta.Meta.Id);
            }
        }

        if (next.SetEquals(uiWatched) || Conversations.UiPort is not { } port)
            return;
        uiWatched = next;
        port.SetWatched(next);
    }

    /// <summary>這個對話現在可以送即時更新：連線協商出 conv1＋live1、伺服器說有人看、本機的政策允許</summary>
    private bool LiveOk(string wire, PublishMetaSnapshot meta, PublishMetaRow row)
        => current is { Proven: true } conn && LiveNegotiated(conn) && serverWatch.Contains(wire) && LiveAllowedFor(wire, meta, row);

    /// <summary>本機的政策允許（D21、D19）：政策 full、已知、沒有封鎖與墓碑、沒有伺服器刪除的旁檔、不在寫旁檔、不是草稿或複本</summary>
    private bool LiveAllowedFor(string wire, PublishMetaSnapshot meta, PublishMetaRow row)
        => !tombstoning.Contains(wire) && PublishPlanRules.LiveAllowed(meta.EffectivePolicy, Knowledge(row, meta), meta.IsDraft, meta.IsReplica);

    private static bool LiveNegotiated(SyncConnection conn)
        => DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1) && DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Live1);

    /// <summary>這條連線也協商出 <c>live2</c>（增量、256 KB、依大小降頻）</summary>
    private static bool Live2Negotiated(SyncConnection conn) => LiveNegotiated(conn) && DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Live2);

    /// <summary>這個對話即時更新的最小間隔：<c>live2</c> 依上一則整則的大小降頻（<see cref="LiveRules.DeltaIntervalMs"/>），否則 1000／MaxLiveHz</summary>
    private int LiveIntervalFor(string wire)
        => current is { Proven: true } conn && Live2Negotiated(conn) && limits.MaxLiveDeltaHz is > 0 and var hz
            ? LiveRules.DeltaIntervalMs(lastLiveBytes.GetValueOrDefault(wire), hz)
            : PublishTimingRules.LiveIntervalMs(Math.Max(1, limits.MaxLiveHz));

    private long? LastLive(string wire) => lastLiveAt.TryGetValue(wire, out var last) ? last : null;

    /// <summary>單調時鐘的毫秒（從第一次用起算；只用來算間隔，不和電腦的時鐘比）</summary>
    private long MonoMs()
    {
        monoOrigin ??= time.GetTimestamp();
        return (long)time.GetElapsedTime(monoOrigin.Value).TotalMilliseconds;
    }
}
