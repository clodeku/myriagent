using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>一頁複本變化裡的一個寫入（依 seq 的順序，<see cref="SyncStore.ApplyReplicaPage"/> 在一個交易裡照順序做）</summary>
internal enum ReplicaOpKind
{
    /// <summary>新增或整列取代 replica_meta</summary>
    UpsertMeta,

    /// <summary>政策不是 full：丟掉這個複本的項目、保留 meta（WP4.2 的 P10）</summary>
    DropItems,

    /// <summary>conv.deleted：丟掉整個複本（meta 與項目）</summary>
    DropReplica,

    /// <summary>以 (對話, 項目 id) 新增或取代（只取代 seq 較小的）</summary>
    UpsertItem,

    /// <summary>conv.itemRemoved</summary>
    DeleteItem,
}

internal sealed record ReplicaOp(ReplicaOpKind Kind, string ConversationId, ReplicaMetaRow? Meta = null, ReplicaItemRow? Item = null, string? ItemId = null);

/// <summary>
/// 一頁拉到的對話事件的套用計畫：<see cref="Ops"/> 在提交之後寫進 sync.db、<see cref="Delta"/> 在 UI 執行緒交給中樞、
/// <see cref="Metas"/> 是這一頁之後每個動到的複本的 meta（null＝丟掉了），寫成了才放進快取
/// </summary>
internal sealed record ReplicaPagePlan(IReadOnlyList<ReplicaOp> Ops, IReadOnlyDictionary<string, ReplicaMetaRow?> Metas, ReplicaDelta Delta, int Invalid, int Skipped);

/// <summary>
/// 引擎端的複本（其他電腦的對話，WP4.3 計畫步 14，D26）：replica_meta 的快取、中樞顯示中的複本（中樞打開時 <see cref="OpenStream"/>、
/// 沒有視窗開著時 <see cref="Close"/>）與輪流拉的串流、「這個 id 是不是本機的對話」的快取，以及一頁事件的套用計畫（<see cref="PlanPage"/>）。
/// 只在引擎執行緒用、不加鎖；快取只在寫進 sync.db 之後才更新（<see cref="Committed"/>）
/// </summary>
internal sealed class ConversationReplicas
{
    /// <summary>顯示中的複本超過這個數時記警告（照樣拉；串流游標跟著它們，每次有新事件時輪流的頁數跟著變多）</summary>
    public const int MaxOpen = 50;

    /// <summary>一次拉取最多帶幾個複本的串流（D26、F29）</summary>
    public const int MaxStreamsPerPull = 12;

    /// <summary>伺服器一次拉取收的游標上限（MyriagentHub 的 EventLog.MaxPullCursors）：第 3 階段兩個＋meta 一個＋串流 ≤12</summary>
    public const int MaxPullCursors = 16;

    private Dictionary<string, ReplicaMetaRow>? metas;

    // 打開的複本（最近打開的在最後）與要拉串流的（依輪到的順序）；這一頁要的串流
    private readonly List<string> open = new();
    private readonly List<string> pending = new();
    private readonly HashSet<string> pendingSet = new(StringComparer.Ordinal);
    private List<string> pulling = new();
    private readonly HashSet<string> pendedDuringPage = new(StringComparer.Ordinal);
    private int pullingGeneration = -1;
    private bool overOpenWarned;

    // 對話 id → 本機有這個對話的資料夾（每個 id 查一次）
    private readonly Dictionary<string, bool> localFolders = new(StringComparer.Ordinal);

    /// <summary>打開的複本（最近打開的在最後）</summary>
    public IReadOnlyList<string> Open => open;

    /// <summary>還有要拉串流的複本</summary>
    public bool HasPending => pendingSet.Count > 0;

    /// <summary>replica_meta（第一次用時讀）</summary>
    public IReadOnlyDictionary<string, ReplicaMetaRow> Metas(Func<IReadOnlyDictionary<string, ReplicaMetaRow>> load)
        => metas ??= new Dictionary<string, ReplicaMetaRow>(load(), StringComparer.Ordinal);

    /// <summary>伺服器換實例、表升級完成：快取全部丟掉（複本已在 BeginServerChange 的交易裡清掉，打開的也不再有內容）</summary>
    public void Reset()
    {
        metas = null;
        open.Clear();
        pending.Clear();
        pendingSet.Clear();
        pulling = new List<string>();
        pendedDuringPage.Clear();
        pullingGeneration = -1;
        overOpenWarned = false;
        localFolders.Clear();
    }

    /// <summary>
    /// 本機的對話資料夾在不在（每個 id 查一次並快取）：根目錄不明或查不了一律當成在（不建副本，SEC-27 的 fail-closed）
    /// </summary>
    public bool IsLocalFolder(string wire, string? root)
    {
        if (localFolders.TryGetValue(wire, out var cached))
            return cached;
        bool exists;
        try
        {
            exists = root is null || Directory.Exists(Path.Combine(root, wire));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"查本機的對話資料夾 {wire} 失敗（當成本機的對話）：{ex.Message}");
            exists = true;
        }

        localFolders[wire] = exists;
        return exists;
    }

    /// <summary>
    /// 打開一個複本：移到最近、排進要拉串流的。顯示中的超過 <see cref="MaxOpen"/> 個時記一次警告、照樣拉（不無聲拿掉顯示中的；
    /// 關掉的由中樞通知 <see cref="Close"/>，不佔名額。合併前審查 R3）
    /// </summary>
    public void OpenStream(string wire)
    {
        open.Remove(wire);
        open.Add(wire);
        if (open.Count <= MaxOpen)
        {
            overOpenWarned = false;
        }
        else if (!overOpenWarned)
        {
            overOpenWarned = true;
            Log.Warn($"顯示中的複本超過 {MaxOpen} 個（{open.Count}）：照樣拉，每次有新事件要多拉幾頁");
        }

        Pend(wire);
    }

    /// <summary>複本被刪除了、或中樞不再顯示它：不再拉它的串流</summary>
    public void Close(string wire)
    {
        open.Remove(wire);
        Unpend(wire);
    }

    /// <summary>中樞不再顯示任何複本（關掉顯示）：打開的串流全部不再拉（游標與 meta 的快取留著）</summary>
    public void CloseAll()
    {
        open.Clear();
        pending.Clear();
        pendingSet.Clear();
        pendedDuringPage.Clear();
    }

    /// <summary>有新的 item 類事件（Notify）或換了連線：打開的串流都要拉</summary>
    public void PendAll()
    {
        foreach (var wire in open)
            Pend(wire);
    }

    /// <summary>
    /// 這個複本還打開著就再拉一次它的串流（WP4.7 步 12：blob 這一頁放不下或暫時取不到時）。正在拉它的頁裡呼叫時，這一頁結束後照樣待拉
    /// </summary>
    public void Repend(string wire)
    {
        if (open.Contains(wire))
            Pend(wire);
    }

    /// <summary>
    /// 這個串流先不拉（WP4.7 步 12：blob 暫時取不到，等 <see cref="ReplicaBlobRules.RetryAfter"/> 之後 <see cref="Repend"/>）：這一頁結束時即使伺服器還有 more
    /// 也不排回去（不然下一頁馬上又取一次）。打開狀態不變；之後有新的 item 類事件（<see cref="PendAll"/>）照樣會拉
    /// </summary>
    public void Defer(string wire)
    {
        Unpend(wire);
        pendedDuringPage.Remove(wire);
    }

    /// <summary>
    /// 這一頁要拉的串流（最多 <paramref name="max"/> 個，依輪到的順序）；新的連線先把打開的全部排進來（斷線期間的事件）
    /// </summary>
    public IReadOnlyList<string> TakeStreams(int generation, int max)
    {
        if (generation != pullingGeneration)
        {
            pullingGeneration = generation;
            PendAll();
        }

        pendedDuringPage.Clear();
        pulling = pending.Take(Math.Max(0, max)).ToList();
        return pulling;
    }

    /// <summary>
    /// 一頁結束：沒有 more＝這一頁要的串流都拉到底了，不再待拉（拉的途中又有新事件的照樣待拉）；還有 more 的排到最後（下一頁輪到別的）
    /// </summary>
    public void EndPage(bool more)
    {
        foreach (var wire in pulling)
        {
            if (!pendingSet.Contains(wire))
                continue;
            pending.Remove(wire);
            if (more || pendedDuringPage.Contains(wire))
                pending.Add(wire);
            else
                pendingSet.Remove(wire);
        }

        pulling = new List<string>();
        pendedDuringPage.Clear();
    }

    /// <summary>寫進 sync.db 之後：快取換成這一頁之後的 meta，丟掉的複本不再拉</summary>
    public void Committed(ReplicaPagePlan plan)
    {
        if (metas is null)
            return;
        foreach (var (id, row) in plan.Metas)
        {
            if (row is null)
            {
                metas.Remove(id);
                Close(id);
            }
            else
            {
                metas[id] = row;
            }
        }
    }

    /// <summary>
    /// 一頁對話事件（依 seq）的套用計畫（D26）：每一筆以 <see cref="PayloadRules"/> 解碼、<see cref="ConversationEventRules.CheckEvent"/>／
    /// <see cref="ConversationEventRules.Validate(string?, string?)"/> 驗證，不合格的略過（游標照樣前進，<see cref="ReplicaPagePlan.Invalid"/>）；
    /// 這台寫的、本機已有的對話（<paramref name="isLocal"/>）、作者不是複本擁有者的一律略過（SEC-27）。
    /// created／meta 依 seq 新增或取代 meta（政策不是 full 時丟掉項目）；deleted 丟掉整個複本；item、itemRemoved 依 seq 套用到政策是 full 的複本；
    /// 一輪、提醒、佇列複本不用。不改任何狀態（<paramref name="current"/> 是快取，計畫裡的新值寫成了才由 <see cref="Committed"/> 放進去）。
    /// <paramref name="blobs"/>：WP4.7 步 12 預取到的 blob（以 SHA-256 取）；item 的 blob 欄位取得到就換回內容再存（<see cref="ReplicaBlobRules.Inline"/>）
    /// </summary>
    public static ReplicaPagePlan PlanPage(IReadOnlyList<PulledEvent> events, string selfDeviceId, Func<string, bool> isLocal,
        IReadOnlyDictionary<string, ReplicaMetaRow> current, Func<string, byte[]?>? blobs = null)
    {
        var working = new Dictionary<string, ReplicaMetaRow?>(StringComparer.Ordinal);
        var ops = new List<ReplicaOp>();
        var delta = new ReplicaDeltaBuilder();
        var invalid = 0;
        var skipped = 0;

        ReplicaMetaRow? RowOf(string id) => working.TryGetValue(id, out var row) ? row : current.GetValueOrDefault(id);

        foreach (var e in events)
        {
            if (e is null || !ConversationEventKinds.IsConversationKind(e.Kind))
                continue;
            if (!PayloadRules.TryDecode(e.Enc, e.Payload, ConversationEventRules.MaxPayloadBytes, out var json)
                || ConversationEventRules.CheckEvent(e.Kind, e.Stream, e.ConversationId, e.Epoch) is not null
                || ConversationEventRules.Validate(e.Kind, json) is not null)
            {
                invalid++;
                continue;
            }

            var id = e.ConversationId!;
            // SEC-27：這台寫的、本機已有的對話（自己擁有的、重新配對前的舊身分的）一律不當成複本，也不碰本機的資料
            if (string.Equals(e.Author, selfDeviceId, StringComparison.Ordinal) || isLocal(id))
            {
                skipped++;
                continue;
            }

            var row = RowOf(id);
            if (row is not null && !string.Equals(row.OwnerDeviceId, e.Author, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            switch (e.Kind)
            {
                case ConversationEventKinds.Created or ConversationEventKinds.Meta:
                {
                    // 串流也會帶到 meta 類（meta 的游標已經拉過）：seq 沒有比較新的不重做
                    if (row is not null && e.Seq <= row.Seq)
                        break;
                    var next = new ReplicaMetaRow(id, e.Author, e.Epoch!.Value, json, false, e.Seq);
                    working[id] = next;
                    ops.Add(new ReplicaOp(ReplicaOpKind.UpsertMeta, id, Meta: next));
                    if (!ReplicaRules.HasItems(json))
                    {
                        ops.Add(new ReplicaOp(ReplicaOpKind.DropItems, id));
                        if (row is not null && ReplicaRules.HasItems(row.MetaJson))
                            delta.Reset(id);
                    }

                    delta.Meta(next);
                    break;
                }

                case ConversationEventKinds.Deleted:
                    working[id] = null;
                    ops.Add(new ReplicaOp(ReplicaOpKind.DropReplica, id));
                    delta.Delete(id);
                    break;
                case ConversationEventKinds.Item:
                {
                    if (row is null || !ReplicaRules.HasItems(row.MetaJson))
                    {
                        skipped++;
                        break;
                    }

                    var o = JObject.Parse(json);
                    var itemId = o.Value<string>("id")!;
                    var stored = ReplicaBlobRules.Inline(o, json, blobs);
                    ops.Add(new ReplicaOp(ReplicaOpKind.UpsertItem, id, Item: new ReplicaItemRow(id, itemId, o.Value<long>("ord"), stored, e.Seq)));
                    delta.Item(id, itemId, ReplicaRules.ToItem(stored));
                    break;
                }

                case ConversationEventKinds.ItemRemoved:
                {
                    if (row is null)
                    {
                        skipped++;
                        break;
                    }

                    var itemId = JObject.Parse(json).Value<string>("id")!;
                    ops.Add(new ReplicaOp(ReplicaOpKind.DeleteItem, id, ItemId: itemId));
                    delta.Removed(id, itemId);
                    break;
                }

                default:
                    // conv.turn、conv.attention、conv.queue：複本不用（執行狀態在 meta 裡）
                    break;
            }
        }

        return new ReplicaPagePlan(ops, working, delta.Build(), invalid, skipped);
    }

    /// <summary>
    /// 丟掉 <paramref name="owners"/>（已移除的電腦，<see cref="DeviceRemovalRules"/>）擁有的複本的計畫（設計 14.22）：伺服器已刪掉那些對話、Pull 不再回它們的事件，
    /// 不會再有 conv.deleted。和一頁的 deleted 相同（丟掉 meta 與項目、中樞拿掉），依 id 排序；沒有要丟的回 null
    /// </summary>
    public static ReplicaPagePlan? PlanDropOwners(IReadOnlyDictionary<string, ReplicaMetaRow> current, IReadOnlySet<string> owners)
    {
        if (owners.Count == 0)
            return null;
        var ids = current.Values.Where(r => owners.Contains(r.OwnerDeviceId)).Select(r => r.ConversationId).Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return null;
        var metas = new Dictionary<string, ReplicaMetaRow?>(StringComparer.Ordinal);
        var ops = new List<ReplicaOp>();
        var delta = new ReplicaDeltaBuilder();
        foreach (var id in ids)
        {
            metas[id] = null;
            ops.Add(new ReplicaOp(ReplicaOpKind.DropReplica, id));
            delta.Delete(id);
        }

        return new ReplicaPagePlan(ops, metas, delta.Build(), 0, 0);
    }

    /// <summary>一頁的中樞變化（每個複本的 meta 只留最後一版、項目依 id 合併、刪除蓋掉之前的變化）</summary>
    private sealed class ReplicaDeltaBuilder
    {
        private readonly Dictionary<string, ReplicaMetaRow> metas = new(StringComparer.Ordinal);
        private readonly List<string> metaOrder = new();
        private readonly HashSet<string> deleted = new(StringComparer.Ordinal);
        private readonly List<string> deletedOrder = new();
        private readonly Dictionary<string, Items> items = new(StringComparer.Ordinal);
        private readonly List<string> itemOrder = new();

        private sealed class Items
        {
            public bool Reset;
            public readonly Dictionary<string, AgentChatItem> Upserts = new(StringComparer.Ordinal);
            public readonly HashSet<string> Removed = new(StringComparer.Ordinal);
        }

        public void Meta(ReplicaMetaRow row)
        {
            if (!metas.ContainsKey(row.ConversationId))
                metaOrder.Add(row.ConversationId);
            metas[row.ConversationId] = row;
            if (deleted.Remove(row.ConversationId))
                deletedOrder.Remove(row.ConversationId);
        }

        public void Delete(string id)
        {
            if (metas.Remove(id))
                metaOrder.Remove(id);
            if (items.Remove(id))
                itemOrder.Remove(id);
            if (deleted.Add(id))
                deletedOrder.Add(id);
        }

        public void Reset(string id)
        {
            var entry = Of(id);
            entry.Reset = true;
            entry.Upserts.Clear();
            entry.Removed.Clear();
        }

        /// <summary>轉不成桌面項目的（不認得的種類、blob 還取不到）當成移除：中樞不顯示舊的版本</summary>
        public void Item(string id, string itemId, AgentChatItem? item)
        {
            var entry = Of(id);
            if (item is null)
            {
                entry.Upserts.Remove(itemId);
                entry.Removed.Add(itemId);
                return;
            }

            entry.Removed.Remove(itemId);
            entry.Upserts[itemId] = item;
        }

        public void Removed(string id, string itemId)
        {
            var entry = Of(id);
            entry.Upserts.Remove(itemId);
            entry.Removed.Add(itemId);
        }

        public ReplicaDelta Build()
        {
            var metaList = new List<ReplicaMeta>();
            foreach (var id in metaOrder)
            {
                if (ReplicaRules.ToMeta(metas[id]) is { } meta)
                    metaList.Add(meta);
            }

            var itemList = itemOrder.Select(id =>
            {
                var entry = items[id];
                var upserts = entry.Upserts.Values.OrderBy(i => i.Ord ?? long.MaxValue).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
                return new ReplicaItems(id, entry.Reset, upserts, entry.Removed.Order(StringComparer.Ordinal).ToList());
            }).ToList();
            return new ReplicaDelta(metaList, deletedOrder.ToList(), itemList);
        }

        private Items Of(string id)
        {
            if (!items.TryGetValue(id, out var entry))
            {
                entry = new Items();
                items[id] = entry;
                itemOrder.Add(id);
            }

            return entry;
        }
    }

    private void Pend(string wire)
    {
        if (pendingSet.Add(wire))
            pending.Add(wire);
        else if (pulling.Contains(wire))
            pendedDuringPage.Add(wire);
    }

    private void Unpend(string wire)
    {
        if (pendingSet.Remove(wire))
            pending.Remove(wire);
    }
}
