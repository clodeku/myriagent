using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Sync;

// WP4.3 的複本（計畫步 14，D26、D28）：只在這條連線協商出 conv1 時拉 cls=meta（全域的對話清單）與打開的複本串流（每次最多 12 個、輪流），
// 其他電腦的對話存成 sync.db 的 replica_meta／replica_items，對話游標存 v2 的 conversation_cursors（v1 的 cursors 只放第 3 階段的類別）。
// 這台寫的、本機已有的對話一律略過（SEC-27），不合格的事件略過、游標照樣前進。中樞的部分只在 UI 執行緒經 IConversationUiPort.ApplyReplicaDelta
// （AgentHub.Replica.cs，步 15）；寫 sync.db 的在提交之後。沒有盒子、表不在、發布者沒掛上時每個入口直接返回
internal sealed partial class SyncEngine
{
    private readonly ConversationReplicas replicas = new();

    /// <summary>conversation_cursors 的快取（第一次用時讀；寫成了才更新；伺服器換實例時丟掉）</summary>
    private IReadOnlyDictionary<string, long>? conversationCursors;

    /// <summary>這一頁拉取要的對話游標與拉到的整頁事件（<see cref="ContributePullCursors"/> 開始、<see cref="EndConversationPage"/> 結束）</summary>
    private ReplicaPullPage? replicaPage;

    /// <summary>寫複本失敗的連線世代：這條連線之後不再拉對話（重連後重拉，不會卡在同一頁無限重拉）</summary>
    private int replicaSuspendedGeneration = -1;

    /// <summary>
    /// 一頁已排進 UI、還沒寫進 sync.db 的期間收到的中樞要求（<see cref="ReplicaRequested"/>，合併前審查 R2）：這時讀 sync.db 會以舊的快照（Reset／Full）
    /// 蓋掉剛套用的那一頁，所以延到這一頁寫完（或放棄）之後再處理；之後到的 <see cref="ReplicaClosed"/> 排在同一份清單裡（照到達的順序）
    /// </summary>
    private List<ConversationChange>? deferredReplicaRequests;

    private sealed class ReplicaPullPage(IReadOnlyList<PullCursor> requested, SyncConnection owner)
    {
        public IReadOnlyList<PullCursor> Requested { get; } = requested;

        /// <summary>拉這一頁的連線（舊連線的拉取迴圈晚一點結束時，不動新連線的頁；第二輪合併前審查 R2）</summary>
        public SyncConnection Owner { get; } = owner;

        public IReadOnlyList<PulledEvent> Events { get; set; } = [];

        /// <summary><see cref="PlanConversationApply"/> 處理了這一頁（游標在它的提交之後寫）</summary>
        public bool Planned { get; set; }

        /// <summary>這一頁的複本寫入做完了（成功或失敗）：之後讀 sync.db 與中樞看到的一致</summary>
        public bool Settled { get; set; }
    }

    // ───────────────────────────── 登記點 ─────────────────────────────

    partial void ContributeHelloCursors(Dictionary<string, long> into)
    {
        // Hello 在協商之前：只看上一次進 Live 的連線有沒有 conv1（D13 的 conv_armed）與這個組建宣告不宣告 conv1
        if (Conversations is null || store is null || !store.ConversationTablesReady)
            return;
        if (!PublishPlanRules.DeclaresConv1(true, true, DlpVersion, ConversationPublisher.RequiredDlpVersion) || !Armed())
            return;
        try
        {
            into[SyncClasses.Meta] = ConversationCursors().GetValueOrDefault(SyncClasses.Meta);
        }
        catch (Exception ex)
        {
            Log.Warn($"讀取對話的游標失敗（Hello 不帶 meta）：{ex.Message}");
        }
    }

    partial void ContributePullCursors(List<PullCursor> into)
    {
        replicaPage = null;
        // 上一頁沒寫完就放棄了（斷線、套用失敗）：延後的要求照 sync.db 現在的內容處理（打開的串流這一頁就拉）
        ReplayReplicaRequests();
        if (current is not { } conn || !ReplicasReady(conn))
            return;
        IReadOnlyDictionary<string, long> cursors;
        try
        {
            cursors = ConversationCursors();
        }
        catch (Exception ex)
        {
            Log.Warn($"讀取對話的游標失敗：這條連線先不拉對話（{ex.Message}）");
            replicaSuspendedGeneration = conn.Generation;
            return;
        }

        var requested = new List<PullCursor> { new(cursors.GetValueOrDefault(SyncClasses.Meta), SyncClasses.Meta) };
        // 第 3 階段的兩個、meta 一個，其餘給打開的複本串流（最多 12 個）
        var room = Math.Min(ConversationReplicas.MaxStreamsPerPull, ConversationReplicas.MaxPullCursors - 2 - requested.Count);
        foreach (var wire in replicas.TakeStreams(conn.Generation, room))
        {
            var stream = SyncStreams.Conversation(wire);
            requested.Add(new PullCursor(cursors.GetValueOrDefault(stream), Stream: stream));
        }

        into.AddRange(requested);
        replicaPage = new ReplicaPullPage(requested, conn);
    }

    partial void OnNotifyForConversations(SyncConnection conn, Notify notify, ref bool pull)
    {
        if (!ReplicasReady(conn))
            return;
        var classes = notify.Cls ?? [];
        if (classes.Contains(SyncClasses.Meta))
            pull = true;
        if (classes.Contains(SyncClasses.Item) && replicas.Open.Count > 0)
        {
            // 不知道是哪個對話：打開的複本串流都要拉一次
            replicas.PendAll();
            pull = true;
        }
    }

    partial void PlanConversationApply(IReadOnlyList<PulledEvent> events, List<Action> uiActions, List<Action> afterCommit)
    {
        if (replicaPage is not { } page || current is not { } conn || !ReplicasReady(conn))
            return;
        // WP4.7 步 12：預取到的 blob 拿來解碼；停住的串流從停住那筆起這一頁不套用（SyncEngine.ReplicaMedia.cs）
        var blobs = TakeReplicaBlobs(page);
        ReplicaPagePlan plan;
        IReadOnlyDictionary<string, long> previous;
        try
        {
            previous = ConversationCursors();
            plan = ConversationReplicas.PlanPage(ReplicaEventsToApply(page, blobs, events), device!.Current.DeviceId, IsLocalConversation, ReplicaMetas(), blobs.Resolve);
        }
        catch (Exception ex)
        {
            // 程式錯誤：這一頁的對話不套用、游標不前進；這條連線先不拉對話（第 3 階段照常）
            Log.Error("規劃拉到的對話事件失敗：這條連線先不拉對話", ex);
            replicaSuspendedGeneration = conn.Generation;
            return;
        }

        page.Planned = true;
        var next = ReplicaBlobRules.CapHeld(PullReduceRules.AdvanceConversationCursors(previous, page.Requested, page.Events), previous, page.Events, blobs.Holds);
        AfterReplicaBlobPlan(conn, blobs);
        var changed = ChangedCursors(previous, next);
        if (plan.Invalid > 0)
            Log.Warn($"拉到 {plan.Invalid} 筆不合格的對話事件：略過（游標照樣前進）");
        if (!plan.Delta.IsEmpty && Conversations!.UiPort is { } port)
        {
            var delta = plan.Delta;
            uiActions.Add(() => port.ApplyReplicaDelta(delta));
        }

        afterCommit.Add(() =>
        {
            CommitReplicaPage(conn, plan, next, changed);
            page.Settled = true;
            ReplayReplicaRequests();
        });
    }

    // ───────────────────────────── 引擎本體呼叫的（Data.cs） ─────────────────────────────

    /// <summary>拉到一頁（化簡之後、套用之前）：記下整頁的事件（對話游標照每筆的類別與串流前進，含不認得的種類）</summary>
    private void BeginConversationPage(IReadOnlyList<PulledEvent> events)
    {
        if (replicaPage is { } page)
            page.Events = events;
    }

    /// <summary>
    /// 一頁提交完：<see cref="PlanConversationApply"/> 沒處理的頁（沒有對話事件、或只有不認得的種類）照樣前進對話游標；
    /// 沒有 more 時這一頁要的串流都拉到底了。回傳要不要再拉一頁（伺服器的 more，或還有打開的串流沒輪到）
    /// </summary>
    private bool EndConversationPage(bool more)
    {
        var page = replicaPage;
        replicaPage = null;
        ReplayReplicaRequests();
        if (page is null || current is not { } conn || !ReplicasReady(conn))
            return more;
        if (!page.Planned)
        {
            try
            {
                var previous = ConversationCursors();
                var next = PullReduceRules.AdvanceConversationCursors(previous, page.Requested, page.Events);
                var changed = ChangedCursors(previous, next);
                if (changed.Count > 0)
                {
                    store!.SetConversationCursors(changed);
                    conversationCursors = next;
                }
            }
            catch (Exception ex)
            {
                Log.Error("寫入對話的游標失敗：這條連線先不拉對話（重連後重拉）", ex);
                replicaSuspendedGeneration = conn.Generation;
                return more;
            }
        }

        replicas.EndPage(more);
        return more || replicas.HasPending;
    }

    // ───────────────────────────── 盒子的要求（ProcessChanges） ─────────────────────────────

    /// <summary>
    /// 中樞要複本（<see cref="ReplicaRequested"/>）：沒有 id＝整份清單；有 id＝打開了這個複本——存著的項目整份交給中樞、開始拉它的串流
    /// （本機已有的 id、不是複本的不理）
    /// </summary>
    private void OnReplicaRequested(ReplicaRequested request)
    {
        if (store is null || !store.ConversationTablesReady || Conversations?.UiPort is not { } port)
            return;
        if (replicaPage is { Planned: true, Settled: false })
        {
            // 這一頁已排進 UI、還沒寫進 sync.db：現在讀會以舊的快照蓋掉剛套用的（合併前審查 R2）
            (deferredReplicaRequests ??= []).Add(request);
            return;
        }

        try
        {
            var metas = ReplicaMetas();
            if (request.ConversationId is null)
            {
                var all = metas.Values.OrderBy(r => r.ConversationId, StringComparer.Ordinal).Select(ReplicaRules.ToMeta).OfType<ReplicaMeta>().ToList();
                PostReplicaDelta(port, new ReplicaDelta(all, [], [], Full: true));
                return;
            }

            if (ConversationIds.ToWire(request.ConversationId) is not { } wire || IsLocalConversation(wire) || !metas.TryGetValue(wire, out var row))
                return;
            replicas.OpenStream(wire);
            var items = ReplicaRules.HasItems(row.MetaJson)
                ? store.LoadReplicaItems(wire).Select(r => ReplicaRules.ToItem(r.Payload)).OfType<AgentChatItem>().ToList()
                : new List<AgentChatItem>();
            PostReplicaDelta(port, new ReplicaDelta([], [], [new ReplicaItems(wire, Reset: true, items, [])]));
            if (current is { Proven: true } conn && ReplicasReady(conn))
                RequestPull(conn);
        }
        catch (Exception ex)
        {
            Log.Error("處理中樞要的複本失敗", ex);
        }
    }

    /// <summary>
    /// 中樞不再顯示這個複本（<see cref="ReplicaClosed"/>；沒有 id＝全部）：不再拉它的串流。游標與存著的項目留著，下次打開時整份交給中樞、
    /// 從游標接著拉（合併前審查 R3）。有延後的要求時排在它們後面（打開又關掉的不會被重播的打開蓋回來）
    /// </summary>
    private void OnReplicaClosed(ReplicaClosed closed)
    {
        if (deferredReplicaRequests is { } deferred)
        {
            deferred.Add(closed);
            return;
        }

        if (closed.ConversationId is null)
            replicas.CloseAll();
        else if (ConversationIds.ToWire(closed.ConversationId) is { } wire)
            replicas.Close(wire);
    }

    // ───────────────────────────── 狀態 ─────────────────────────────

    /// <summary>
    /// 這條連線可以拉複本：有盒子而且發布者掛上了（本機對話的根目錄已知）、v3 的表在、協商出 conv1、寫複本沒有失敗過
    /// </summary>
    private bool ReplicasReady(SyncConnection conn)
        => Conversations is { UiPort: not null, ConversationsRoot: not null } && store is { ConversationTablesReady: true }
           && conn.Proven && DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Conv1) && replicaSuspendedGeneration != conn.Generation;

    /// <summary>
    /// 本機已有的對話（D26、SEC-27）：這次啟動看過它的中繼資料（不是複本）、發布過（publish_meta 有它，含墓碑），或 <c>ConversationsRoot\{id}</c>
    /// 資料夾存在（重新配對前的舊身分的對話；每個 id 查一次並快取）
    /// </summary>
    private bool IsLocalConversation(string wire)
    {
        if ((lastMeta.TryGetValue(wire, out var meta) && !meta.IsReplica) || Rows().ContainsKey(wire))
            return true;
        return replicas.IsLocalFolder(wire, Conversations?.ConversationsRoot);
    }

    private IReadOnlyDictionary<string, long> ConversationCursors() => conversationCursors ??= store!.GetConversationCursors();

    private IReadOnlyDictionary<string, ReplicaMetaRow> ReplicaMetas() => replicas.Metas(store!.LoadReplicaMeta);

    private static Dictionary<string, long> ChangedCursors(IReadOnlyDictionary<string, long> previous, IReadOnlyDictionary<string, long> next)
    {
        var changed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, seq) in next)
        {
            if (!previous.TryGetValue(key, out var before) || before != seq)
                changed[key] = seq;
        }

        return changed;
    }

    /// <summary>提交之後：複本的寫入與對話游標同一個交易；寫成了才更新快取（失敗時這條連線先不拉對話，重連後重拉，依 seq 套用結果相同）</summary>
    private void CommitReplicaPage(SyncConnection conn, ReplicaPagePlan plan, IReadOnlyDictionary<string, long> next, IReadOnlyDictionary<string, long> changed)
    {
        try
        {
            store!.ApplyReplicaPage(plan.Ops, changed);
        }
        catch (Exception ex)
        {
            Log.Error("寫入複本資料與對話的游標失敗：這條連線先不拉對話（重連後重拉）", ex);
            replicaSuspendedGeneration = conn.Generation;
            return;
        }

        conversationCursors = next;
        replicas.Committed(plan);
    }

    /// <summary>
    /// 已移除的電腦（設計 14.22，<see cref="DeviceRemovalRules"/>）擁有的複本丟掉（sync.db 與中樞）：每一頁拉取寫完之後、名冊更新之後呼叫
    /// （名冊在這台升級之前就已經不列它時，也在下一次拉取之後補上）。有一頁已排進 UI、還沒寫進 sync.db 時不做，那一頁寫完時的呼叫會做。
    /// 寫入失敗只記錄（不影響這條連線，下一頁再試）
    /// </summary>
    private void DropRemovedOwnerReplicas(SyncConnection conn)
    {
        if (catalog is null || trust is null || !ReplicasReady(conn) || Conversations!.UiPort is not { } port)
            return;
        if (replicaPage is { Planned: true, Settled: false })
            return;
        var removed = DeviceRemovalRules.Removed(catalog.View(trust.Registry));
        if (removed.Count == 0)
            return;
        try
        {
            if (ConversationReplicas.PlanDropOwners(ReplicaMetas(), removed) is not { } plan)
                return;
            store!.ApplyReplicaPage(plan.Ops, new Dictionary<string, long>());
            replicas.Committed(plan);
            Log.Info($"丟掉已移除電腦的複本：{plan.Ops.Count} 個對話");
            PostReplicaDelta(port, plan.Delta);
        }
        catch (Exception ex)
        {
            Log.Error("丟掉已移除電腦的複本失敗（下一頁再試）", ex);
        }
    }

    /// <summary>引擎執行緒發起的複本變化（要求的回應、伺服器換實例）：經 RunOnUiAsync 交給中樞</summary>
    private void PostReplicaDelta(IConversationUiPort port, ReplicaDelta delta)
    {
        if (delta.IsEmpty)
            return;
        Spawn(async () =>
        {
            try
            {
                await args.Host.RunOnUiAsync(() => port.ApplyReplicaDelta(delta), lifetime.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"把複本交給中樞失敗：{ex.Message}");
            }
        });
    }

    /// <summary>伺服器換實例（BeginServerChange 已清掉複本與對話游標，D22）或表升級完成：快取丟掉；換實例時中樞的複本也清掉（整份清單是空的）</summary>
    private void ResetReplicas(bool notifyUi)
    {
        conversationCursors = null;
        replicaPage = null;
        replicas.Reset();
        if (notifyUi && Conversations?.UiPort is { } port)
            PostReplicaDelta(port, new ReplicaDelta([], [], [], Full: true));
        ReplayReplicaRequests();
    }

    /// <summary>
    /// 拉取迴圈結束（含斷線、套用失敗、停止）：沒寫完的那一頁不會再寫，延後的中樞要求照 sync.db 現在的內容處理
    /// （UI 已套用、沒寫進去的那一頁，下次從原本的游標重拉）。只放棄 <paramref name="conn"/> 自己的頁：新的連線已經開始拉時，
    /// 那一頁是它的，不動、也不重播（延後的要求在那一頁寫完或放棄時重播；第二輪合併前審查 R2）
    /// </summary>
    private void AbandonConversationPage(SyncConnection conn)
    {
        if (replicaPage is { } page && !ReferenceEquals(page.Owner, conn))
            return;
        replicaPage = null;
        ReplayReplicaRequests();
    }

    /// <summary>處理延後的中樞要求（<see cref="deferredReplicaRequests"/>，照到達的順序）</summary>
    private void ReplayReplicaRequests()
    {
        if (deferredReplicaRequests is not { } deferred)
            return;
        deferredReplicaRequests = null;
        foreach (var change in deferred)
        {
            switch (change)
            {
                case ReplicaRequested request:
                    OnReplicaRequested(request);
                    break;
                case ReplicaClosed closed:
                    OnReplicaClosed(closed);
                    break;
                default:
                    throw new InvalidOperationException($"延後的複本要求只有兩種：{change.GetType().Name}");
            }
        }
    }
}
