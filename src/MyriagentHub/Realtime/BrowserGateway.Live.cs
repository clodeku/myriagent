using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器閘道的即時更新（WP4.2 計畫步 11；R13、R14，D24、D27、提案 C4）：在閘道迴圈裡、對話的變更之前取走即時疊加層的即時更新，
/// 之後依提交順序讓疊加層看過每一筆持久變更，最後推送：
/// <list type="bullet">
/// <item>收下的即時更新、以及持久版本推過之後仍然有效的疊加（Running 的檢查點沒有作廢它）：以 <c>item</c> 推給開著那個對話的連線（不比快照的 seq：即時更新沒有持久的 seq；
/// 與持久的 item 共用合併鍵，佇列裡後到的取代先到的）。</item>
/// <item>被清掉的疊加（持久版本作廢它、owner 連線斷了、同一台有更新的連線 Prove、淘汰、open 的快照說它已經 final）：改推那個項目目前的持久版本——
/// 這一輪已經收到比自己的快照新的持久版本的連線不必；其他的讀一次 <c>items</c>（沒有就 <c>removeItem</c>，例如只在即時更新出現過的項目）。</item>
/// <item>open：快照入列之後補上持久版本的標記、清掉已經 final 的，再把這個對話的疊加推給這條連線。</item>
/// </list>
/// 渲染只在這裡（D24）：每一格只渲染一次、各連線共用。
/// </summary>
internal sealed partial class BrowserGateway
{
    private static readonly IReadOnlySet<string> NoConversations = new HashSet<string>();

    private static readonly IReadOnlyDictionary<string, ConversationRoundWork> NoWork = new Dictionary<string, ConversationRoundWork>();

    /// <summary>沒有連線時：照樣取走即時更新、看過持久變更（<c>drained</c>），不推；之前有變更被丟掉時先清空</summary>
    private void DiscardLive(bool refresh)
    {
        var live = overlay!;
        if (refresh)
            live.Clear();
        live.TakeRemoved();
        live.Drain(time.GetUtcNow().ToUnixTimeMilliseconds());
        foreach (var change in drained)
            live.OnDurable(change, watched: false);
        live.PruneMarks(NoConversations);
    }

    /// <summary>一輪的開始：取走連線事件清掉的疊加與排著的即時更新；整份重讀時先清空（漏掉的持久變更可能已經作廢它們）。沒有疊加層回 null</summary>
    private LiveRoundState? BeginLiveRound(List<BrowserConnection> live, bool refresh, long nowMs)
    {
        if (overlay is null)
            return null;
        if (refresh)
            overlay.Clear();
        var state = new LiveRoundState(WatchedConversations(live));
        state.Removed.AddRange(overlay.TakeRemoved());
        var drain = overlay.Drain(nowMs);
        state.Accepted.AddRange(drain.Accepted);
        state.Removed.AddRange(drain.Removed);
        return state;
    }

    /// <summary>一輪的結尾（持久變更推過之後）：推收下的與仍然有效的疊加、對被清掉的改推持久版本、只留有人在看的持久版本標記</summary>
    private void FinishLiveRound(List<BrowserConnection> live, LiveRoundState state, ConversationRound round, long nowMs, ref bool resync)
    {
        var works = round.Conversations.Count == 0 ? NoWork : round.Conversations.ToDictionary(w => w.ConversationId, StringComparer.Ordinal);
        var pushed = new HashSet<LiveSlot>(ReferenceEqualityComparer.Instance);
        foreach (var slot in state.Accepted.Concat(state.Kept))
        {
            if (pushed.Add(slot) && overlay!.IsCurrent(slot))
                PushSlot(live, slot, nowMs, ref resync);
        }
        PushReverts(live, state.Removed, works, nowMs, ref resync);
        overlay!.PruneMarks(state.Watched);
    }

    /// <summary>open 的快照入列之後：補標記、清掉已經 final 的（其他開著的連線改推持久版本），再把這個對話的疊加推給這條連線</summary>
    private void AfterOpen(IReadOnlyList<BrowserConnection> all, BrowserConnection connection, string conversationId, IReadOnlyList<(long Ord, bool Final)> items,
        long nowMs, ref bool resync)
    {
        if (overlay is null || !connection.TryGetOpenConversation(conversationId, out _))
            return;
        var gone = overlay.ReconcileOpen(conversationId, items);
        if (gone.Count > 0)
            PushReverts(all.Where(c => c.InitSent && !ReferenceEquals(c, connection)).ToList(), gone, NoWork, nowMs, ref resync);
        var slots = overlay.SlotsOf(conversationId);
        if (slots.Count == 0 || Views.Header(conversationId) is not { } view)
            return;
        // 合併前審查 F1：這個對話的疊加一次推出（最多 64 格、每格渲染後最多 256 KB）之前先加總，超過送出佇列剩餘空間的一半就不推
        // （之後的即時更新會再推那一格；網頁先看快照裡的持久版本）
        var messages = new List<ClientOutboxMessage>(slots.Count);
        long bytes = 0;
        var patch1 = connection.State is { Patch1: true };
        foreach (var slot in slots)
        {
            if (!patch1 && slot.Entry.Bytes > LiveRules.MaxLiveBytes)
                continue;
            slot.Rendered ??= Views.LiveItemJson(slot, view.Header);
            var message = ConversationGatewayRules.LiveItemMessage(conversationId, slot.Entry.ItemId, slot.Rendered, slot.Age);
            messages.Add(message);
            bytes += message.Bytes;
        }
        var browser = options.Value.Browser;
        if (!ConversationGatewayRules.ShouldPushSlotsAfterOpen(messages.Count, bytes, connection.Outbox.Count, connection.Outbox.Bytes, browser.OutboxMaxMessages,
                browser.OutboxMaxBytes))
            return;
        foreach (var message in messages)
            Update(connection, message, null, nowMs, ref resync);
    }

    /// <summary>
    /// 一格疊加推給開著那個對話的連線（第一次要送時才渲染；對話不在了不送）。宣告了 <c>patch1</c> 的連線手上是上一格的版本、佇列裡沒有同一筆還沒送的訊息時
    /// 送 <c>itemPatch</c>（每格最多算一次、各連線共用），否則送整筆（帶 <c>rv</c>）。沒宣告的舊分頁照今天：整筆、只送 ≤64 KB 的格
    /// </summary>
    private void PushSlot(IReadOnlyList<BrowserConnection> live, LiveSlot slot, long nowMs, ref bool resync)
    {
        var entry = slot.Entry;
        var targets = live.Where(c => c.TryGetOpenConversation(entry.ConversationId, out _)).ToList();
        if (targets.Count == 0)
            return;
        if (slot.Rendered is null)
        {
            if (Views.Header(entry.ConversationId) is not { } view)
                return;
            slot.Rendered = Views.LiveItemJson(slot, view.Header);
        }
        var message = ConversationGatewayRules.LiveItemMessage(entry.ConversationId, entry.ItemId, slot.Rendered, slot.Age);
        var legacyFits = entry.Bytes <= LiveRules.MaxLiveBytes;
        foreach (var connection in targets)
        {
            if (connection.State is not { Patch1: true })
            {
                if (legacyFits)
                    Update(connection, message, null, nowMs, ref resync);
                continue;
            }
            var patch = slot.PrevAge is { } baseRv && connection.Outbox.CanPatch(message.Key!, baseRv) ? PatchOf(slot) : null;
            Update(connection, patch ?? message, null, nowMs, ref resync);
        }
    }

    /// <summary>這一格的 <c>itemPatch</c>（第一次要用時才算、各連線共用；算過就放掉基底的渲染結果）</summary>
    private static ClientOutboxMessage? PatchOf(LiveSlot slot)
    {
        if (!slot.PatchComputed)
        {
            slot.PatchComputed = true;
            if (slot.PrevRendered is { } basis && slot.PrevAge is { } baseRv && ItemPatchRules.Compute(basis, slot.Rendered!) is { } patch)
                slot.PatchMessage = ConversationGatewayRules.ItemPatchMessage(slot.Entry.ConversationId, slot.Entry.ItemId, slot.Age, baseRv, patch);
            slot.PrevRendered = null;
        }
        return slot.PatchMessage;
    }

    /// <summary>
    /// 被清掉的疊加改推持久版本（提案 C4 的 c、D27）：同一格又收下了同一個項目的新即時更新就不必；這一輪已經推了比自己的快照新的持久版本的連線不必；
    /// 其他的讀一次目前的持久版本（每個項目最多讀一次）
    /// </summary>
    private void PushReverts(IReadOnlyList<BrowserConnection> live, IReadOnlyList<LiveEntry> removed, IReadOnlyDictionary<string, ConversationRoundWork> works,
        long nowMs, ref bool resync)
    {
        if (removed.Count == 0)
            return;
        var done = new HashSet<(string, string)>();
        foreach (var entry in removed)
        {
            if (!done.Add((entry.ConversationId, entry.ItemId)) || overlay!.Covers(entry.ConversationId, entry.Ord, entry.ItemId))
                continue;
            long? durableSeq = works.TryGetValue(entry.ConversationId, out var work)
                ? work.Items.LastOrDefault(c => string.Equals(c.ItemId, entry.ItemId, StringComparison.Ordinal))?.Seq
                : null;
            ClientOutboxMessage? message = null;
            var read = false;
            foreach (var connection in live)
            {
                if (!connection.TryGetOpenConversation(entry.ConversationId, out var snapshotSeq) || durableSeq > snapshotSeq)
                    continue;
                if (!read)
                {
                    message = Views.CurrentItemMessage(entry.ConversationId, entry.ItemId);
                    read = true;
                }
                if (message is null)
                    break;
                Update(connection, message, null, nowMs, ref resync);
            }
        }
    }

    /// <summary>現在有人開著或看得到的對話（開著的、visible 指令的清單、ClientState 的 openConversationIds）：疊加層只為它們（與有疊加的）保留持久版本的標記</summary>
    private static HashSet<string> WatchedConversations(List<BrowserConnection> live)
    {
        var watched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in live)
        {
            watched.UnionWith(connection.OpenConversationIds());
            watched.UnionWith(connection.VisibleConversationIds());
            if (connection.State is { } state)
                watched.UnionWith(state.OpenConversationIds.Where(ConversationIds.IsWireId));
        }
        return watched;
    }

    private static (ClientOutboxMessage Message, long? Seq, IReadOnlyList<(long Ord, bool Final)> Items) WithoutItems((ClientOutboxMessage Message, long? Seq) response) =>
        (response.Message, response.Seq, []);

    /// <summary>一輪的疊加層狀態（閘道迴圈裡）</summary>
    private sealed class LiveRoundState(HashSet<string> watched)
    {
        public HashSet<string> Watched { get; } = watched;

        /// <summary>這一輪收下的即時更新</summary>
        public List<LiveSlot> Accepted { get; } = [];

        /// <summary>持久版本推過之後仍然有效、要再推一次的疊加</summary>
        public List<LiveSlot> Kept { get; } = [];

        /// <summary>被清掉、要改推持久版本的</summary>
        public List<LiveEntry> Removed { get; } = [];

        public void Apply(LiveDurableEffect effect)
        {
            Removed.AddRange(effect.Removed);
            Kept.AddRange(effect.Kept);
        }
    }
}
