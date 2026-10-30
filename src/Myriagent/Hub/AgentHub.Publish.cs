using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Sync;

namespace Myriagent.Hub;

// 對話發布在中樞這一側（WP4.3）：項目 Ord 的配發與保留（計畫步 5，D8、D9）、同步政策指令與伺服器刪除的旁檔（步 6，D25、D35）、
// 給發布者的快照來源（步 7，D1、D12）。
// 擁有者：WP4.3（本檔）。只在 UI 執行緒；不參考 SignalR、SQLite 或同步引擎的型別（S6：未配對時這裡的程式一行都不會寫檔）
internal sealed partial class AgentHub : IConversationPublishSource
{
    /// <summary>Ord 的配發狀態（D8、D9）：<see cref="PrepareOrdsForPublish"/> 準備好之前一律不配</summary>
    private sealed partial class ConversationState
    {
        /// <summary>下一個要配的 Ord（<see cref="OrdsReady"/> 為 true 時才有意義）</summary>
        public long OrdNext;

        /// <summary>已寫進旁檔的保留上限（不含）：<see cref="OrdNext"/> 到了它就要先保留下一段才能配</summary>
        public long OrdReserved;

        /// <summary>已依存檔、旁檔與 published_items 規劃過 Ord（引擎第一次要這個對話的快照時準備），之後新增的項目由 AddItem 接著配</summary>
        public bool OrdsReady;

        /// <summary>
        /// 有沒有伺服器刪除的旁檔（null＝還沒查；第一次問時查一次檔案，之後用這個值）。旁檔只會新增、不會拿掉，
        /// 所以寫旁檔一律經 <see cref="MarkServerDeleted"/>（它把這裡設成 true）
        /// </summary>
        public bool? ServerDeleted;
    }

    /// <summary>
    /// 新增的項目配 Ord（AddItem 呼叫，D8）：掛著接收者、不是草稿或複本、還在清單裡、項目已載入、這個對話已準備過 Ord，五個都成立才配，
    /// 否則留 null（之後準備時依清單順序補）。先保留、後配發：下一個到了保留上限就先寫旁檔與 meta.json；保留失敗就不配，
    /// 而且這個對話回到「沒準備過」，之後新增的也先不配（下次準備時一起依清單順序補，Ord 才會照清單順序遞增）
    /// </summary>
    private void AssignOrd(ConversationState c, AgentChatItem item)
    {
        if (conversationSink == null || item.Ord is >= 1 || !c.OrdsReady || !c.ItemsLoaded || c.IsReplica || !IsLive(c))
            return;
        if (OrdAssignRules.NeedsReservation(c.OrdNext, c.OrdReserved) && !TryReserveOrds(c, c.OrdNext))
        {
            c.OrdsReady = false;
            return;
        }
        item.Ord = c.OrdNext++;
    }

    /// <summary>
    /// 可以為這個對話寫 Ord 的保留（D9）：不是草稿或複本、仍在清單裡（同一個狀態物件）、掛著接收者、meta.json 已存在。
    /// 草稿與已刪除的對話因此不會多出只有旁檔的資料夾
    /// </summary>
    private bool CanReserveOrds(ConversationState c)
        => conversationSink != null && !c.IsReplica && IsLive(c) && store.MetaExists(c.Meta.Id);

    /// <summary>
    /// 從 <paramref name="next"/> 起保留下一段（D9）：先寫旁檔 sync-ord.json，再把上限記進 meta.json 的 <c>NextOrd</c> 存檔。
    /// 旁檔寫不成功回 false（不配發）；meta.json 存檔失敗由 SaveConversation 提示，保留仍以旁檔為準（載入時取兩者較大的）
    /// </summary>
    private bool TryReserveOrds(ConversationState c, long next)
    {
        if (!CanReserveOrds(c))
            return false;
        var reserved = OrdAssignRules.NewReservation(next);
        if (!store.TrySaveOrdReservation(c.Meta.Id, reserved))
            return false;
        c.OrdReserved = reserved;
        c.Meta.NextOrd = reserved;
        SaveConversation(c, saveItems: false);
        return true;
    }

    /// <summary>
    /// 引擎要這個對話的快照之前準備 Ord（UI 執行緒，D8、D9）：載入項目，以存檔的 Ord、meta.json 的 <c>NextOrd</c>、旁檔與
    /// <paramref name="hint"/>（sync.db 的 published_items）規劃，先保留、後配；有補配就標記項目未存檔。
    /// 已準備過而且每個項目都有 Ord 時不再讀寫檔案。回傳 false＝不能準備（找不到、草稿、複本、沒掛接收者、meta.json 不在、
    /// 項目讀不到、保留失敗），這次的快照不能用
    /// </summary>
    internal bool PrepareOrdsForPublish(string conversationId, OrdHint? hint)
    {
        if (!conversations.TryGetValue(conversationId, out var c) || !CanReserveOrds(c))
            return false;
        var items = EnsureItems(c, notifyUnreadable: false);
        if (!c.ItemsLoaded)
            return false;
        if (c.OrdsReady && items.TrueForAll(i => i.Ord is >= 1))
            return true;

        var known = c.OrdsReady ? Math.Max(c.Meta.NextOrd ?? 0, c.OrdNext) : c.Meta.NextOrd ?? 0;
        var plan = OrdAssignRules.PlanLoad(items.ConvertAll(i => (i.Id, i.Ord)), known >= 1 ? known : null, store.ReadOrdReservation(c.Meta.Id), hint);
        if (plan.Stripped)
            Log.Warn($"對話 {c.Meta.Id} 的項目沒有 Ord、meta.json 也沒有 NextOrd，但旁檔或發布紀錄有值（可能中間跑過舊版）：依旁檔與發布紀錄重配");
        if (!TryReserveOrds(c, plan.Next))
            return false;

        for (var i = 0; i < items.Count; i++)
            items[i].Ord = plan.Ords[i];
        c.OrdNext = plan.Next;
        c.OrdsReady = true;
        if (plan.Changed)
            c.ItemsUnsaved = true;
        return true;
    }

    // ───────────────────────────── 同步政策與伺服器刪除（計畫步 6，D25、D35） ─────────────────────────────

    /// <summary>
    /// 指令 syncSetConversationPolicy（協調者轉來，UI 執行緒；D35）：照 <see cref="ConversationPolicyCommandRules"/> 檢查，合格就經
    /// SaveConversation 存（先寫同步政策的旁檔再寫 meta.json）；發布端由 OnConversationSaved 反應（D25）。已從伺服器刪除的對話選
    /// full／metadata 時跳一則 toast；草稿、複本、找不到的對話與其他不合格的值只記錄（docs/web-protocol.md 沒有定回覆）。回傳是否存了
    /// </summary>
    internal bool SetConversationSyncPolicy(string? conversationId, string? policy)
    {
        if (conversationId == null || !conversations.TryGetValue(conversationId, out var c))
        {
            Log.Info($"syncSetConversationPolicy：找不到對話 {conversationId}，不處理");
            return false;
        }
        var syncable = SyncProviderRules.TryWireName(c.Meta.Provider, out _);
        var value = ConversationPolicyCommandRules.Check(policy, syncable, c.IsDraft, c.IsReplica, IsServerDeleted(c), out var reason);
        if (value == null)
        {
            if (reason == ConversationPolicyCommandRules.ServerDeletedReason)
                Toast(reason, isError: true);
            Log.Info($"syncSetConversationPolicy：對話 {conversationId} 不改成 {policy}（{reason}）");
            return false;
        }
        c.Meta.SyncPolicy = value;
        SaveConversation(c, saveItems: false);
        RefreshSyncHeader(c);
        return true;
    }

    /// <summary>
    /// 寫伺服器刪除的旁檔（UI 執行緒；D25：發布者先寫它，成功了才送 conv.deleted）。草稿、複本、找不到的對話與 meta.json 不在的
    /// 回 false、不建資料夾；已經有旁檔回 true、不重寫（保留第一次的時間）
    /// </summary>
    internal bool MarkServerDeleted(string conversationId, long atMs)
    {
        if (!conversations.TryGetValue(conversationId, out var c) || c.IsReplica || !IsLive(c))
            return false;
        if (IsServerDeleted(c))
            return true;
        if (!store.TryWriteServerDeleted(conversationId, atMs))
            return false;
        c.ServerDeleted = true;
        RefreshSyncHeader(c);
        return true;
    }

    /// <summary>這個對話實際的同步政策（UI 執行緒）：有伺服器刪除的旁檔一律 localOnly，否則照存檔與代理種類（找不到是 null）</summary>
    internal string? GetEffectiveSyncPolicy(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? EffectiveSyncPolicy(c) : null;

    /// <summary>有伺服器刪除的旁檔一律 localOnly（換回 Claude 帳戶也一樣，D25）；否則 SyncProviderRules.ConversationPolicy</summary>
    private string EffectiveSyncPolicy(ConversationState c)
        => IsServerDeleted(c) ? ConversationSyncPolicies.LocalOnly : SyncProviderRules.ConversationPolicy(c.Meta);

    /// <summary>有沒有伺服器刪除的旁檔：第一次查檔案（讀不到也算有，fail-closed），之後用記下的值</summary>
    private bool IsServerDeleted(ConversationState c)
    {
        if (c.ServerDeleted is { } known)
            return known;
        if (c.IsDraft || c.IsReplica)
            return false;
        var deleted = store.IsServerDeleted(c.Meta.Id);
        c.ServerDeleted = deleted;
        return deleted;
    }

    /// <summary>
    /// 接收者換了或拿掉（AttachConversationSink）：所有對話回到「沒準備過」。沒掛接收者的期間新增的項目沒有 Ord、接在清單最後，
    /// 下次準備時依清單順序補配，之後新增的才接著配（不會出現清單後面的項目 Ord 反而比較小）
    /// </summary>
    private void ResetOrdsReady()
    {
        foreach (var c in conversations.Values)
            c.OrdsReady = false;
    }

    // ───────────────────────────── 發布者的快照來源（計畫步 7，D1、D8、D12、D14、D19） ─────────────────────────────

    private static readonly IReadOnlySet<string> NoRemoteWatched = new HashSet<string>(StringComparer.Ordinal);

    private IReadOnlySet<string> remoteWatched = NoRemoteWatched;

    /// <summary>引擎判斷「其他地方看得到」的對話（發布者經 SetWatched 推來，整份取代；IsSeenRemotely 用，步 11）。UI 執行緒</summary>
    internal IReadOnlySet<string> RemoteWatched => remoteWatched;

    string IConversationPublishSource.ConversationsRoot => store.RootDirectory;

    void IConversationPublishSource.BeginInvoke(Action action) => host.BeginInvoke(action);

    IReadOnlyList<string> IConversationPublishSource.ConversationIds()
    {
        var ids = new List<string>(conversations.Count);
        foreach (var c in conversations.Values)
        {
            if (!c.IsDraft && !c.IsReplica)
                ids.Add(c.Meta.Id);
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    IReadOnlyList<PublishMetaSnapshot> IConversationPublishSource.SnapshotMeta(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > PublishTimingRules.MetaSlice)
            throw new ArgumentOutOfRangeException(nameof(ids), ids.Count, $"一次最多 {PublishTimingRules.MetaSlice} 個對話（UI 執行緒的預算）");
        var result = new List<PublishMetaSnapshot>(ids.Count);
        foreach (var id in ids)
        {
            if (conversations.TryGetValue(id, out var c))
            {
                result.Add(new PublishMetaSnapshot(ChatItemCopyRules.CopyMeta(c.Meta), EffectiveSyncPolicy(c), c.Running, AwaitsUser(c), c.ItemsLoaded,
                    c.IsDraft, c.IsReplica, IsServerDeleted(c), c.IsReplica ? null : ModelOptions(c)));
            }
        }

        return result;
    }

    PublishItemsPage? IConversationPublishSource.SnapshotItems(string conversationId, long afterOrd, int take, OrdHint? hint)
    {
        // 沒載入的不在這裡讀檔（中樞不卸載項目，300 個對話全載入記憶體會超過 S6）：引擎改在執行緒集區唯讀讀檔（D10）
        if (!conversations.TryGetValue(conversationId, out var c) || c.IsDraft || c.IsReplica || !c.ItemsLoaded)
            return null;
        if (!PrepareOrdsForPublish(conversationId, hint))
            return new PublishItemsPage(conversationId, Array.Empty<AgentChatItem>(), null, afterOrd, More: false, OrdsReady: false);

        var items = c.Items;
        var page = PublishPageRules.Select(items, OrdOf, afterOrd, take);
        var copies = new List<AgentChatItem>(page.Indices.Count);
        foreach (var index in page.Indices)
            copies.Add(ChatItemCopyRules.Copy(items[index]));
        var allIds = afterOrd <= 0 ? items.ConvertAll(IdOf) : null;
        // 載入時損毀的項目檔已被改名成 .corrupt-*（清單只剩一筆錯誤項目，之後存的新檔也不完整）：要靠檔案判斷，重開之後記憶體的旗標就沒了
        var trusted = allIds is null || !ConversationStore.ItemsCorruptBackupExists(store.GetConversationDirectory(conversationId));
        return new PublishItemsPage(conversationId, copies, allIds, page.LastOrd, page.More, OrdsReady: true, RemovalsTrusted: trusted);
    }

    private static long? OrdOf(AgentChatItem item) => item.Ord;

    private static string IdOf(AgentChatItem item) => item.Id;

    PublishQueueSnapshot? IConversationPublishSource.SnapshotQueue(string conversationId)
    {
        if (!conversations.TryGetValue(conversationId, out var c) || c.IsDraft || c.IsReplica)
            return null;
        var entries = new List<(string Id, string DisplayText)>(c.Queue.Count);
        foreach (var queued in c.Queue)
            entries.Add((queued.Id, SecretAnswerScrubRules.QueuedDisplay(queued.AnswerFor, queued.DisplayText, queued.Text)));
        return new PublishQueueSnapshot(conversationId, entries);
    }

    void IConversationPublishSource.SetWatched(IReadOnlySet<string> conversationIds)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        remoteWatched = conversationIds;
    }

    /// <summary>接收者換了或拿掉（AttachConversationSink；解除配對）：之前推來的觀看集合不再算數，本機照常提醒</summary>
    private void ResetRemoteWatched() => remoteWatched = NoRemoteWatched;

    /// <summary>
    /// 其他地方（瀏覽器）看得到這個對話（NotifyAttention 問，計畫步 11，D19）：在引擎推來的觀看集合裡（伺服器說看得到、引擎以本機政策過濾、
    /// created 已確認、沒有封鎖），而且這裡再看一次本機的政策是 full、不是草稿或複本。被入侵的伺服器因此壓不下 localOnly、metadata 對話的提醒
    /// </summary>
    partial void IsSeenRemotely(string conversationId, ref bool seen)
    {
        if (seen || remoteWatched.Count == 0 || !remoteWatched.Contains(conversationId))
            return;
        if (!conversations.TryGetValue(conversationId, out var c) || c.IsDraft || c.IsReplica)
            return;
        if (EffectiveSyncPolicy(c) == ConversationSyncPolicies.Full)
            seen = true;
    }

    void IConversationPublishSource.PauseForDlp(string conversationId)
    {
        if (!conversations.TryGetValue(conversationId, out var c) || c.IsReplica || !IsLive(c))
        {
            Log.Info($"伺服器以 dlp 擋下對話 {conversationId} 的事件，但這個對話已不在（或是草稿、副本）：不處理");
            return;
        }

        if (!ConversationPolicyCommandRules.PausesForDlp(EffectiveSyncPolicy(c)))
            return;
        Log.Warn($"伺服器以 dlp 擋下對話 {conversationId} 的內容：同步政策改成 paused");
        c.Meta.SyncPolicy = ConversationSyncPolicies.Paused;
        SaveConversation(c, saveItems: false);
        RefreshSyncHeader(c);
    }

    bool IConversationPublishSource.MarkServerDeleted(string conversationId, long atMs) => MarkServerDeleted(conversationId, atMs);

    void IConversationPublishSource.ShowPublishNotice(PublishNoticeKind kind, int count)
    {
        var text = PublishNoticeRules.Text(kind, count);
        Log.Warn($"對話發布：{text}");
        Toast(text, isError: true);
    }

    void IConversationPublishSource.ApplyReplicaDelta(ReplicaDelta delta) => ApplyReplicaDelta(delta);
}
