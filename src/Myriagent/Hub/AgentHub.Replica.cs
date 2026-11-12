using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Rendering;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 其他電腦的對話在這台的唯讀複本（WP4.3 計畫步 15，D26、D27、F11）。引擎（Sync/SyncEngine.Replicas.cs）從 sync.db 的複本資料與拉到的事件
// 組出變化，經發布者在 UI 執行緒交給 ApplyReplicaDelta。預設不顯示（ShowReplicas＝false，D27；WP4.6 起協調者掛上發布者時打開、拿掉時關掉）。
// 顯示時每個複本是一個 IsReplica 的 ConversationState：不建會話、不存檔（儲存端另外拒寫，第二道）、網頁指令只收看的、項目以不登記本機媒體的
// 渲染器渲染、本機已有的 id 一律不建（第二道，D26）。對話變化的掛鉤照常呼叫，接收者（發布者）略過複本。
// 擁有者：WP4.3（本檔）。只在 UI 執行緒；不參考 SignalR、SQLite 或同步引擎的型別（S6）
internal sealed partial class AgentHub
{
    /// <summary>複本的狀態（只有 <see cref="ConversationState.IsReplica"/> 為 true 時有意義）</summary>
    private sealed partial class ConversationState
    {
        /// <summary>擁有（執行）這個對話的電腦</summary>
        public string? ReplicaOwnerDeviceId;

        /// <summary>擁有者那邊執行中（不是這台的 Running：這台沒有會話，執行中的計數、系統匣與關閉前的確認都不算它）</summary>
        public bool ReplicaRunning;

        /// <summary>擁有者那邊有等使用者回應的卡片</summary>
        public bool ReplicaWaiting;

        /// <summary>擁有者的同步政策是 full（有同步項目）；metadata、paused 只有標題與狀態</summary>
        public bool ReplicaItemsAvailable;

        /// <summary>擁有者那邊是自訂工作資料夾（Meta.WorkingDirectory 一律是 null：擁有者的路徑不當成這台的資料夾）</summary>
        public bool ReplicaCustomFolder;

        /// <summary>擁有者電腦上的自訂工作資料夾，只供標頭與清單顯示</summary>
        public string? ReplicaFolder;

        /// <summary>擁有者那邊這個對話的帳戶可選的模型（標頭的模型選單）；擁有者沒送時 null（用內建的預設選單）</summary>
        public IReadOnlyList<AgentModelOption>? ReplicaModels;

        /// <summary>擁有者電腦上執行這個對話的本機帳戶 id（只在那台有意義；標頭經同步橋換成邏輯帳戶）；不明時 null</summary>
        public string? ReplicaOwnerAccountId;
    }

    /// <summary>
    /// 網頁指令裡複本可以用的（只看、不改）；其他的一律不處理（唯讀。遠端指令另由閘門 5c 拒絕）。
    /// 多視窗的分頁搬移（tabDrag、tabDragEnd、tabMoveIn、moveToNewWindow）只是換一個這台的視窗顯示，不碰對話本身
    /// （2026-10-25 以前擋著：其他電腦的對話拖到別的視窗放開後沒搬過去）。
    /// 輸入框的草稿（composerDraft）也不碰對話本身：只轉給這台的其他視窗，那台同步了內容的再送上伺服器（AgentHub.ComposerDrafts.cs
    /// 的 CanSyncComposerDraft；2026-11-12 以前擋著：在這台對其他電腦的對話打到一半的字，別台與瀏覽器版都看不到，記錄每次打字多一行）。
    /// 「最近的指令」的歷史指令（promptHistory）只讀這台已經有的副本項目（同日以前擋著：網頁等不到回覆，空等 1.5 秒才用已載入的項目開清單）
    /// </summary>
    internal static IReadOnlySet<string> ReplicaCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "open", "close", "loadOlder", "copyItem", "popout",
        "tabDrag", "tabDragEnd", "tabMoveIn", "moveToNewWindow",
        "composerDraft", "promptHistory",
    };

    private bool showReplicas;

    private ItemRenderer? replicaRenderer;

    /// <summary>複本的項目渲染：不登記本機媒體（<see cref="NullMediaUrlBuilder"/>）、相對路徑不當本機檔案；第一次渲染時建立</summary>
    private ItemRenderer ReplicaRenderer => replicaRenderer ??= new ItemRenderer(NullMediaUrlBuilder.Instance, Log.Error, Log.Warn);

    /// <summary>
    /// 顯示其他電腦的對話（UI 執行緒；D27：預設 false，WP4.6 起由協調者的 AttachPublisher 照有沒有發布者設定）。打開時向引擎要整份複本清單，關掉時移除所有複本
    /// </summary>
    internal bool ShowReplicas
    {
        get => showReplicas;
        set
        {
            if (showReplicas == value)
                return;
            showReplicas = value;
            if (value)
                RequestReplica(null);
            else
            {
                RemoveAllReplicas();
                SettleReplicas();
            }
        }
    }

    private bool replicasPending;

    /// <summary>
    /// 其他電腦的對話清單還沒載入（init 與 conversations 的 replicasPending）：同步元件在主視窗載入後約 2.5 秒才啟動，複本又要等引擎從 sync.db 讀出來，
    /// 網頁還原版面時清單裡還沒有它們。這段期間網頁把版面裡不在清單的分頁留著等（不丟掉、不送 open），清單到了再接上，載完仍不存在的才關掉
    /// （2026-10-29：重開機後其他電腦的對話分頁全部不見，少了分頁的版面還馬上存回去）。
    /// 協調者建立時設成 true（<see cref="ExpectReplicas"/>），收到第一份整份清單或確定不會有複本（未配對、讀不到配對資料、沒有發布者）時
    /// <see cref="SettleReplicas"/>。中樞預設 false（沒有同步元件就不等）。UI 執行緒
    /// </summary>
    internal bool ReplicasPending => replicasPending;

    /// <summary>之後會有其他電腦的對話（同步協調者建立時；UI 執行緒）</summary>
    internal void ExpectReplicas()
    {
        if (replicasPending || disposed)
            return;
        replicasPending = true;
        MarkListDirty();
    }

    /// <summary>其他電腦的對話清單已經載入、或確定不會有（UI 執行緒）：重送清單讓網頁收掉留著等的分頁</summary>
    internal void SettleReplicas()
    {
        if (!replicasPending)
            return;
        replicasPending = false;
        if (!disposed)
            MarkListDirty();
    }

    /// <summary>這個對話是其他電腦的對話在這台的複本（UI 執行緒；測試與探針用）</summary>
    internal bool IsReplicaConversation(string conversationId) => conversations.TryGetValue(conversationId, out var c) && c.IsReplica;

    /// <summary>
    /// 引擎交來的複本變化（UI 執行緒，發布者轉來）。<see cref="ShowReplicas"/> 為 false 時什麼都不做。整份清單（<see cref="ReplicaDelta.Full"/>）
    /// 先移除不在裡面的複本；本機已有的 id（清單裡不是複本的、或對話資料夾存在）一律不建、本機的不動；例外照常丟出（引擎的這一頁就不提交、游標不前進）
    /// </summary>
    internal void ApplyReplicaDelta(ReplicaDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (!showReplicas || disposed)
            return;

        var listChanged = false;
        if (delta.Full)
        {
            var keep = new HashSet<string>(delta.Metas.Select(m => m.ConversationId), StringComparer.Ordinal);
            foreach (var c in conversations.Values.Where(c => c.IsReplica && !keep.Contains(c.Meta.Id)).ToList())
            {
                RemoveReplica(c);
                listChanged = true;
            }
        }

        foreach (var meta in delta.Metas)
            listChanged |= UpsertReplica(meta);
        // 第一份整份清單到了：網頁留著等的分頁照這份清單接上或關掉（SettleReplicas 自己重送清單）
        if (delta.Full)
            SettleReplicas();

        foreach (var id in delta.Deleted)
        {
            if (conversations.TryGetValue(id, out var c) && c.IsReplica)
            {
                RemoveReplica(c);
                listChanged = true;
            }
        }

        foreach (var items in delta.Items)
        {
            if (conversations.TryGetValue(items.ConversationId, out var c) && c.IsReplica)
                listChanged |= ApplyReplicaItems(c, items);
        }

        if (listChanged)
            MarkListDirty();
    }

    /// <summary>建立或更新一個複本；回傳清單要不要重送</summary>
    private bool UpsertReplica(ReplicaMeta replica)
    {
        var id = replica.ConversationId;
        if (!ConversationIds.IsWireId(id) || !string.Equals(replica.Meta.Id, id, StringComparison.Ordinal))
        {
            Log.Warn($"複本的 id 不合格（{id}）：不建立");
            return false;
        }

        var created = false;
        if (conversations.TryGetValue(id, out var c))
        {
            if (!c.IsReplica)
            {
                Log.Warn($"其他電腦的對話 {id} 是這台的本機對話：不建複本，本機的不動");
                return false;
            }

            c.Meta = replica.Meta;
        }
        else
        {
            if (IsLocalConversationId(id))
            {
                Log.Warn($"其他電腦的對話 {id} 在這台有資料夾（本機的對話）：不建複本");
                return false;
            }

            // 先登記再放進清單：之後任何呼叫端漏掉複本的檢查，儲存端也不寫（F11 的第二道）
            store.MarkReplica(id);
            c = new ConversationState(replica.Meta) { IsReplica = true, ItemsLoaded = true };
            conversations[id] = c;
            created = true;
        }

        c.ReplicaOwnerDeviceId = replica.OwnerDeviceId;
        c.ReplicaRunning = replica.Running;
        c.ReplicaWaiting = replica.Waiting;
        c.ReplicaItemsAvailable = replica.ItemsAvailable;
        c.ReplicaCustomFolder = replica.CustomFolder;
        c.ReplicaFolder = replica.Folder;
        c.ReplicaModels = replica.Models;
        c.ReplicaOwnerAccountId = replica.OwnerAccountId;
        if (!replica.ItemsAvailable && c.Items.Count > 0)
            ApplyReplicaItems(c, new ReplicaItems(id, Reset: true, [], []));
        if (c.ViewIds.Count > 0)
            MarkHeaderDirty(c);
        // 〔桌面遠端操作〕等著這個副本的遠端草稿換過去（AgentHub.PeerDrafts.cs）
        if (created)
            OnPeerReplicaCreated(id);
        return true;
    }

    /// <summary>本機已有的對話（D26 的第二道）：清單裡有同一個 id（大小寫不分）而且不是複本，或對話資料夾存在（這次啟動沒載入的，例如 meta.json 讀不到）</summary>
    private bool IsLocalConversationId(string wireId)
    {
        if (FindByWireId(wireId) is { IsReplica: false })
            return true;
        try
        {
            return Directory.Exists(store.GetConversationDirectory(wireId));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// 套用一個複本的項目變化（依 Ord 插入；同一個 id 取代、沿用原本的 Seq）。擁有者的政策不是 full 時只收清掉。
    /// 插在中間、整份重來或一次變了超過一頁（<see cref="PageSize"/>）時重新依清單順序編 Seq（只增不減）並重送整個對話給開著它的視窗
    /// （同本機對話只送最後一頁加 hasMore，較早的靠 loadOlder；合併前審查 R1），否則逐筆送。回傳清單要不要重送（等待中的卡片變了）
    /// </summary>
    private bool ApplyReplicaItems(ConversationState c, ReplicaItems change)
    {
        var awaited = AwaitsUser(c);
        var reorder = false;
        var removed = new List<AgentChatItem>();
        var changed = new List<AgentChatItem>();
        var placed = new List<AgentChatItem>();
        if (change.Reset)
        {
            // 整份重來（第一次打開時 sync.db 存著的整份）：一次建好、依 Ord 排好（穩定排序；沒有 Ord 的排在最後），不逐筆找位置
            reorder = c.Items.Count > 0 || change.Upserts.Count > 0;
            c.Items.Clear();
            if (c.ReplicaItemsAvailable)
            {
                // 同一個 id 出現兩次時留後面那一筆（同逐筆取代）
                var latest = new Dictionary<string, AgentChatItem>(change.Upserts.Count, StringComparer.Ordinal);
                var withoutId = new List<AgentChatItem>();
                foreach (var item in change.Upserts)
                {
                    if (item.Id is { } id)
                        latest[id] = item;
                    else
                        withoutId.Add(item);
                }

                c.Items.AddRange(latest.Values.Concat(withoutId).OrderBy(i => i.Ord ?? long.MaxValue));
                placed.AddRange(c.Items);
            }
        }
        else
        {
            if (change.Removed.Count > 0)
            {
                var gone = new HashSet<string>(change.Removed, StringComparer.Ordinal);
                c.Items.RemoveAll(i =>
                {
                    if (i.Id is not { } id || !gone.Remove(id))
                        return false;
                    removed.Add(i);
                    return true;
                });
            }

            if (c.ReplicaItemsAvailable)
            {
                var byId = new Dictionary<string, int>(c.Items.Count, StringComparer.Ordinal);
                for (var i = 0; i < c.Items.Count; i++)
                {
                    if (c.Items[i].Id is { } id)
                        byId.TryAdd(id, i);
                }

                var middle = new List<AgentChatItem>();
                var middleIds = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var item in change.Upserts)
                {
                    if (item.Id is { } id && byId.TryGetValue(id, out var index))
                    {
                        item.Seq = c.Items[index].Seq;
                        c.Items[index] = item;
                        changed.Add(item);
                        placed.Add(item);
                        continue;
                    }

                    if (item.Id is { } again && middleIds.TryGetValue(again, out var pending))
                    {
                        // 同一批裡同一個項目的較新版本（插在中間的還沒排進清單）
                        middle[pending] = item;
                        placed.Add(item);
                        continue;
                    }

                    // 接在最後（Ord 遞增的追趕都是這種）；插在中間的最後一次排序（不逐筆移動、索引不失效）
                    if (InsertIndexByOrd(c.Items, item.Ord) == c.Items.Count)
                    {
                        c.Items.Add(item);
                        if (item.Id is { } added)
                            byId.TryAdd(added, c.Items.Count - 1);
                        item.Seq = ++c.LastSeq;
                        changed.Add(item);
                    }
                    else
                    {
                        if (item.Id is { } inserted)
                            middleIds[inserted] = middle.Count;
                        middle.Add(item);
                        reorder = true;
                    }

                    placed.Add(item);
                }

                if (middle.Count > 0)
                {
                    c.Items.AddRange(middle);
                    var sorted = c.Items.OrderBy(i => i.Ord ?? long.MaxValue).ToList();
                    c.Items.Clear();
                    c.Items.AddRange(sorted);
                }
            }
        }

        // 一次變了超過一頁（串流追趕一頁最多 500 筆）：不逐筆送，整個對話重送一次
        if (changed.Count > PageSize)
            reorder = true;

        // 對話變化的掛鉤照常呼叫：接收者（發布者）看到 IsReplica 就略過，複本不會被當成這台的對話發布
        foreach (var item in removed)
            HookItemRemoved(c, item);
        foreach (var item in placed)
            HookItemChanged(c, item);

        if (reorder)
        {
            foreach (var item in c.Items)
                item.Seq = ++c.LastSeq;
            // 主動重送（不是 open 的回覆）：網頁已關掉這個分頁時丟掉，不會重開（第二輪合併前審查 R1）
            foreach (var view in ViewsOf(c).ToList())
                PostConversation(view, c, null, reload: true);
        }
        else if (c.ViewIds.Count > 0)
        {
            var targets = ViewsOf(c).ToList();
            foreach (var item in removed)
                TryPost(targets, () => new JObject { ["type"] = "removeItem", ["conversationId"] = c.Meta.Id, ["id"] = item.Id });
            foreach (var item in changed)
                TryPost(targets, () => new JObject { ["type"] = "item", ["conversationId"] = c.Meta.Id, ["item"] = RenderItemOf(c, item, null) });
        }

        return AwaitsUser(c) != awaited;
    }

    /// <summary>依 Ord 插入的位置：第一個 Ord 比它大的項目之前（沒有 Ord 的排在最後）</summary>
    private static int InsertIndexByOrd(List<AgentChatItem> items, long? ord)
    {
        if (ord is not { } value)
            return items.Count;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Ord is not { } existing || existing > value)
                continue;
            return i + 1;
        }

        return 0;
    }

    /// <summary>
    /// 移除一個複本（擁有者刪了、整份清單沒有它、關掉顯示、換了接收者）：開著它的分頁與彈出視窗關掉；儲存端的登記留著。
    /// 有視窗開著它時通知引擎不再拉它的串流（<paramref name="closeStream"/>；全部移除時由呼叫端一次通知）
    /// </summary>
    private void RemoveReplica(ConversationState c, bool closeStream = true)
    {
        var id = c.Meta.Id;
        HookConversationDeleting(c);
        var targets = ViewsOf(c).ToList();
        var shown = c.ViewIds.Count > 0;
        conversations.Remove(id);
        dirtyItems.Remove(id);
        dirtyHeaders.Remove(id);
        TryPost(targets, () => new JObject { ["type"] = "conversationClosed", ["conversationId"] = id });
        host.ConversationDeleted(id);
        if (closeStream && shown)
            CloseReplica(id);
    }

    private void RemoveAllReplicas()
    {
        ForgetReplicaMedia();
        var removed = false;
        foreach (var c in conversations.Values.Where(c => c.IsReplica).ToList())
        {
            RemoveReplica(c, closeStream: false);
            removed = true;
        }

        if (removed)
        {
            CloseReplica(null);
            MarkListDirty();
        }
    }

    /// <summary>接收者換了或拿掉（AttachConversationSink；解除配對）：舊引擎的複本不再算數；還要顯示就向新的接收者要整份清單</summary>
    private void OnReplicaSinkChanged()
    {
        RemoveAllReplicas();
        if (showReplicas && conversationSink != null)
            RequestReplica(null);
    }

    /// <summary>打開了對話（Open）：複本要引擎送它存著的項目並開始拉它的串流</summary>
    private void OnConversationOpened(ConversationState c)
    {
        if (c.IsReplica)
            RequestReplica(c.Meta.Id);
    }

    /// <summary>
    /// 一個視窗不再開著這個對話（關分頁、關視窗、網頁重新載入）：複本沒有任何視窗開著時，引擎不再拉它的串流
    /// （之後再打開時重新要；合併前審查 R3）
    /// </summary>
    private void OnConversationViewRemoved(ConversationState c)
    {
        if (c.IsReplica && c.ViewIds.Count == 0)
            CloseReplica(c.Meta.Id);
    }

    /// <summary>向接收者要複本（接收者有實作 <see cref="IReplicaRequester"/> 才有；丟出的例外記錄後吞掉，同其他掛鉤）</summary>
    private void RequestReplica(string? conversationId)
    {
        if (conversationSink is not IReplicaRequester requester)
            return;
        try
        {
            requester.RequestReplica(conversationId);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(IReplicaRequester.RequestReplica), ex);
        }
    }

    /// <summary>告訴接收者不再顯示這個複本（null＝全部；同 <see cref="RequestReplica"/>，例外記錄後吞掉）</summary>
    private void CloseReplica(string? conversationId)
    {
        if (conversationSink is not IReplicaRequester requester)
            return;
        try
        {
            requester.CloseReplica(conversationId);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(IReplicaRequester.CloseReplica), ex);
        }
    }

    /// <summary>網頁指令要不要擋：對複本送的、不是只看的指令（<see cref="ReplicaCommands"/>）一律不處理</summary>
    private bool BlocksReplicaCommand(string? cmd, string? conversationId)
    {
        if (conversationId == null || !conversations.TryGetValue(conversationId, out var c) || !c.IsReplica || (cmd != null && ReplicaCommands.Contains(cmd)))
            return false;
        Log.Info($"對話 {conversationId} 是其他電腦的對話（唯讀）：不處理 {cmd}");
        return true;
    }

    /// <summary>畫面上的工作資料夾，也是項目裡相對路徑的基準：複本是 null（擁有者電腦上的路徑在這台沒有意義，也不算出本機的資料夾）</summary>
    private string? ShownWorkingDirectory(ConversationState c) => c.IsReplica ? null : GetWorkingDirectory(c);

    /// <summary>渲染一個對話的項目：複本用不登記本機媒體的渲染器（有媒體來源時經伺服器取，WP4.7 步 12），其他照 <see cref="RenderItem"/></summary>
    private JObject RenderItemOf(ConversationState c, AgentChatItem item, string? workingDirectory)
        => c.IsReplica ? RenderReplicaItem(c, item) : RenderLocalItem(c, item, workingDirectory ?? GetWorkingDirectory(c));
}
