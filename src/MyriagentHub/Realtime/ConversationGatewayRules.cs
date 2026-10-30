using System.Security.Cryptography;
using System.Text;
using Myriagent.Hub;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>一個對話在一輪裡要做的事（<see cref="ConversationGatewayRules.Fold"/> 的結果；依變更第一次出現的順序）</summary>
/// <param name="Deleted">在伺服器上刪除了：開著的連線送 <c>conversationClosed</c>，其他都不做</param>
/// <param name="Resync">內容不連續了（政策降級的清除、重建做完）：開著的連線送 <c>resync</c> 並標成不開著</param>
/// <param name="ResyncSeq">resync 的 seq：快照比它新（含）的連線不必 resync；null＝一律 resync（重建）</param>
/// <param name="HeaderSeq">標頭要重送（造成它的變更裡最大的 seq）；null＝不必</param>
/// <param name="ComposerSeq">輸入區要重送（<c>conv.queue</c> 的最大 seq）；null＝不必</param>
/// <param name="Items">每個項目最後一筆變更（<c>Item</c>／<c>ItemRemoved</c>，依最後一次出現的順序）</param>
/// <param name="Attentions">要送 notify 的 <c>conv.attention</c>（新的上行事件、kind 認得的）</param>
internal sealed record ConversationRoundWork(string ConversationId, bool Deleted, bool Resync, long? ResyncSeq, long? HeaderSeq, long? ComposerSeq,
    IReadOnlyList<ConversationChange> Items, IReadOnlyList<ConversationChange> Attentions);

/// <summary>一輪的變更摺疊之後的結果：各對話要做的事、清單要不要重讀</summary>
internal sealed record ConversationRound(IReadOnlyList<ConversationRoundWork> Conversations, bool ListChanged)
{
    public static ConversationRound Empty { get; } = new([], false);
}

/// <summary>清單對一條連線要做的事（<see cref="ConversationGatewayRules.ListAction"/>）</summary>
internal enum ConversationListAction
{
    /// <summary>內容、時區、下一個當地午夜都沒變</summary>
    None,

    /// <summary>內容變了：重送 <c>conversations</c></summary>
    Send,

    /// <summary>內容沒變（時區換了但分組一樣、或過了午夜但分組沒變）：只更新標記，不送</summary>
    Remark,
}

/// <summary>
/// 閘道的對話訊息（WP4.2 計畫步 10；D22、D25、D28、D30、D38、K4；web-protocol 第一節的 <c>conversations</c>、<c>conversation</c>、<c>olderItems</c>、
/// <c>header</c>／<c>item</c>／<c>removeItem</c>／<c>composer</c>、<c>conversationClosed</c>，〔遠端〕的 <c>resync</c>、<c>notify</c>、<c>clipboard</c>）。
/// 純函式：一輪的變更怎麼摺疊、「看得到」、notify 的 kind、清單要不要重送、open 要不要延到下一輪、分頁與各訊息的組成
/// （已渲染的 Item、Header、Composer JSON 以原文嵌入，不重新解析）
/// </summary>
internal static class ConversationGatewayRules
{
    /// <summary>loadOlder 的回應</summary>
    public const string OlderItemsType = "olderItems";

    /// <summary>對話被刪除（或 open 的對話不存在）</summary>
    public const string ConversationClosedType = "conversationClosed";

    /// <summary>這個對話的內容不連續了，網頁丟掉已載入的項目、重新 open〔遠端〕</summary>
    public const string ResyncType = "resync";

    private const long DayMs = 86_400_000;

    // ───── 一輪的變更（D13、D23、D38） ─────

    /// <summary>
    /// 把一輪取走的變更（依提交順序）摺疊成各對話要做的事：
    /// <list type="bullet">
    /// <item>同一個項目只留最後一筆（<c>Item</c> 與 <c>ItemRemoved</c> 共用，後到的取代；送出佇列也以同一個鍵合併，D23）。</item>
    /// <item><c>Meta</c>、<c>Turn</c> → 標頭與清單；<c>Queue</c> → 輸入區；<c>Attention</c> → notify（只收 <see cref="ConversationChange.Fresh"/>、kind 認得的）。</item>
    /// <item><c>Deleted</c> → 只剩關閉（之後不會再有這個對話的變更）。</item>
    /// <item><c>Purged</c>（政策降級清掉內容）與 <c>Rebuilt</c>（重建做完）→ resync，<b>它之前</b>的項目、標頭、輸入區都不必送（網頁會重新 open）；
    /// 它之後的照常記下，給快照已經比 resync 新、不必 resync 的連線。Purged 的 resync 帶 seq，Rebuilt 一律 resync（重建期間的快照不完整）。</item>
    /// <item><c>Meta</c>、<c>Turn</c>、<c>Deleted</c>、<c>Purged</c>、<c>Rebuilt</c> 讓清單重讀；項目、佇列與注意事件不影響清單的欄位。</item>
    /// </list>
    /// </summary>
    public static ConversationRound Fold(IReadOnlyList<ConversationChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
            return ConversationRound.Empty;
        var order = new List<string>();
        var builders = new Dictionary<string, WorkBuilder>(StringComparer.Ordinal);
        var listChanged = false;
        foreach (var change in changes)
        {
            if (!builders.TryGetValue(change.ConversationId, out var work))
            {
                builders[change.ConversationId] = work = new WorkBuilder();
                order.Add(change.ConversationId);
            }
            if (work.Deleted)
                continue;
            switch (change.Kind)
            {
                case ConversationChangeKind.Meta:
                case ConversationChangeKind.Turn:
                    work.HeaderSeq = Max(work.HeaderSeq, change.Seq);
                    listChanged = true;
                    break;
                case ConversationChangeKind.Attention:
                    if (change.Fresh && NotifyKind(change.AttentionKind) is not null)
                        work.Attentions.Add(change);
                    break;
                case ConversationChangeKind.Deleted:
                    work.Deleted = true;
                    work.Clear();
                    work.Attentions.Clear();
                    listChanged = true;
                    break;
                case ConversationChangeKind.Item:
                case ConversationChangeKind.ItemRemoved:
                    if (change.ItemId is not { } itemId)
                        break;
                    work.Items.Remove(itemId);
                    work.Items.Add(itemId, change);
                    break;
                case ConversationChangeKind.Queue:
                    work.ComposerSeq = Max(work.ComposerSeq, change.Seq);
                    break;
                case ConversationChangeKind.Purged:
                    work.Clear();
                    if (!work.Resync || work.ResyncSeq is not null)
                        work.ResyncSeq = Max(work.ResyncSeq, change.Seq);
                    work.Resync = true;
                    listChanged = true;
                    break;
                case ConversationChangeKind.Rebuilt:
                    work.Clear();
                    work.Resync = true;
                    work.ResyncSeq = null;
                    listChanged = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changes), change.Kind, "未知的對話變更種類");
            }
        }
        return new ConversationRound(order.Select(id => builders[id].Build(id)).ToList(), listChanged);
    }

    // ───── 觀看（D25） ─────

    /// <summary>
    /// 這條連線「看得到」這個對話嗎（D25）：<c>ClientState.visible</c> 為真，而且對話在最後一次 <c>visible</c> 指令的清單裡，
    /// 或在以 <see cref="ConversationIds.IsWireId"/> 過濾過的 <c>openConversationIds</c> 裡。還沒收到 ClientState 一律看不到
    /// </summary>
    public static bool CanSee(BrowserClientState? state, IReadOnlyCollection<string> visibleIds, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(visibleIds);
        if (state is not { Visible: true } || !ConversationIds.IsWireId(conversationId))
            return false;
        return visibleIds.Contains(conversationId, StringComparer.Ordinal) || state.OpenConversationIds.Contains(conversationId, StringComparer.Ordinal);
    }

    /// <summary><c>conv.attention</c> 的 kind → notify 的 kind（D28）：<c>plan</c> 是 <c>approval</c>，其他照名稱；不認得的不送（null）</summary>
    public static string? NotifyKind(string? attentionKind) => attentionKind switch
    {
        ConversationAttentionKinds.Plan => ConversationAttentionKinds.Approval,
        ConversationAttentionKinds.Question or ConversationAttentionKinds.Approval or ConversationAttentionKinds.Completed or ConversationAttentionKinds.Failed => attentionKind,
        _ => null,
    };

    /// <summary>
    /// <c>speak</c> 要念的句子：擁有者帶了就照念（空字串＝不念，回 null），沒帶（舊版桌面、由 conv.turn 帶出的失敗）就組只有「標題＋發生什麼事」的句子；
    /// 不認得的種類回 null
    /// </summary>
    public static string? SpeechText(string? speech, string? attentionKind, string? title)
    {
        if (speech is not null)
            return speech.Length == 0 ? null : speech;
        return VoiceNotifyRules.HeadFor(attentionKind, title);
    }

    // ───── 清單（D22） ─────

    /// <summary>這條連線算分組用的時區：ClientState 的 <c>tzOffsetMinutes</c>；還沒收到 ClientState 用伺服器時區在那一刻的偏移</summary>
    public static int TimezoneOf(BrowserClientState? state, TimeZoneInfo zone, long nowMs) =>
        state?.TzOffsetMinutes ?? ConversationViewRules.FallbackOffset(zone, nowMs);

    /// <summary>當地日期的序號（當地午夜為界）：清單的分組只看日期，同一個當地日期、同一份資料的清單內容相同</summary>
    public static long LocalDay(long utcMs, int tzOffsetMinutes)
    {
        var local = ConversationViewRules.ToLocalMs(utcMs, tzOffsetMinutes);
        var day = local / DayMs;
        return local % DayMs < 0 ? day - 1 : day;
    }

    /// <summary>
    /// 清單對一條連線要做的事：雜湊不同就重送；相同但時區或下一個當地午夜變了只更新標記（D22 的「跨日重送一次」：過了午夜重算，分組變了才送）。
    /// 還沒有標記（沒送過）一律送
    /// </summary>
    public static ConversationListAction ListAction(ConversationListMark? mark, string hash, int tzOffsetMinutes, long nextDayBoundaryMs)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (mark is null || !string.Equals(mark.Hash, hash, StringComparison.Ordinal))
            return ConversationListAction.Send;
        return mark.TzOffsetMinutes != tzOffsetMinutes || mark.NextDayBoundaryMs != nextDayBoundaryMs ? ConversationListAction.Remark : ConversationListAction.None;
    }

    /// <summary>清單內容（<c>items</c> 陣列的 JSON）的雜湊：每條連線只記雜湊，不留整份字串</summary>
    public static string ListHash(string itemsJson)
    {
        ArgumentNullException.ThrowIfNull(itemsJson);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(itemsJson)));
    }

    // ───── open（D38、K4） ─────

    /// <summary>
    /// K4：送出佇列已經超過上限的一半時，這條連線的 open／loadOlder 留到下一輪（一頁最多 1 MB、佇列上限 2 MB：同時重開好幾個大對話時，
    /// 一輪排進去兩份快照就可能溢出 → 重送 init → 網頁再開 → 再溢出）。佇列是空的時單則照收（送出佇列的規則），所以一定會有進展。
    /// 這只是讀快照之前的粗篩：一份快照另有標頭、輸入區與外框，本身就可能超過一半，所以讀好之後閘道再以 <see cref="ClientOutbox.WouldOverflow"/>
    /// 看實際的大小（合併前審查 F2）
    /// </summary>
    public static bool ShouldDeferOpen(long outboxBytes, long outboxMaxBytes) => outboxBytes > outboxMaxBytes / 2;

    /// <summary>
    /// 合併前審查 F1（增量的流量控制）：一條連線這一輪要收的增量（<paramref name="increments"/> 則、渲染後 <paramref name="incomingBytes"/> 位元組；
    /// 還沒渲染時傳 0）改成重開（送一份新的快照）嗎：超過一頁的筆數（快照一定不比它大；整份重新發布、改回 full、回補時一輪就有幾百到幾千筆，
    /// 逐筆渲染入列只會讓佇列溢出、網頁也會丟掉比已載入第一筆早的），或排進去之後送出佇列會超過則數或位元組上限的一半（留空間給快照與其他訊息）。
    /// 重開照常受 K4 與 <see cref="ClientOutbox.WouldOverflow"/> 管，所以慢的用戶端不會反覆溢出
    /// </summary>
    public static bool ShouldReopen(int increments, long incomingBytes, int outboxCount, long outboxBytes, int outboxMaxMessages, long outboxMaxBytes) =>
        increments > ConversationViewRules.PageItems
        || (long)outboxCount + increments > outboxMaxMessages / 2
        || outboxBytes + incomingBytes > outboxMaxBytes / 2;

    /// <summary>
    /// 合併前審查 F1：open 的快照入列之後，這個對話的即時疊加（<paramref name="slots"/> 格、渲染後 <paramref name="slotBytes"/> 位元組）要不要推：
    /// 不超過送出佇列剩餘空間（則數與位元組）的一半才推；不推的等下一則即時更新再推（每格最多每秒 4 則），網頁先看持久版本
    /// </summary>
    public static bool ShouldPushSlotsAfterOpen(int slots, long slotBytes, int outboxCount, long outboxBytes, int outboxMaxMessages, long outboxMaxBytes) =>
        slots <= (outboxMaxMessages - (long)outboxCount) / 2 && slotBytes <= (outboxMaxBytes - outboxBytes) / 2;

    /// <summary>
    /// 一頁要放哪些項目（D20）：<paramref name="chronological"/> 是讀出的一頁（依 ord 由舊到新、已渲染），從最新的一筆往前放，最多 <paramref name="maxItems"/> 筆、
    /// 加起來不超過 <paramref name="maxBytes"/>（至少一筆）；放不下的或讀的那一頁之前還有（<paramref name="hasMoreBefore"/>）就是 hasMore
    /// </summary>
    public static (IReadOnlyList<string> Items, bool HasMore) PageOf(IReadOnlyList<string> chronological, bool hasMoreBefore, int maxItems, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(chronological);
        if (chronological.Count == 0)
            return ([], hasMoreBefore);
        var newestFirst = new List<int>(chronological.Count);
        for (var i = chronological.Count - 1; i >= 0; i--)
            newestFirst.Add(Encoding.UTF8.GetByteCount(chronological[i]));
        var selection = ConversationViewRules.SelectPage(newestFirst, maxItems, maxBytes);
        var items = chronological.Skip(chronological.Count - selection.Count).ToList();
        return (items, hasMoreBefore || selection.HasMore);
    }

    // ───── 訊息（web-protocol 第一節；已渲染的 JSON 以原文嵌入） ─────

    /// <summary><c>{type:"conversations", items}</c>：以 type 合併（整份取代）</summary>
    public static ClientOutboxMessage ConversationsMessage(string itemsJson) =>
        new(ClientOutboxRules.ConversationsType, Write(new JObject { ["type"] = ClientOutboxRules.ConversationsType, ["items"] = new JRaw(itemsJson) }));

    /// <summary>
    /// <c>{type:"conversation", conversationId, paneId, header, items, hasMore, composer, draft:null}</c>：對話層的屏障（D23）。
    /// <paramref name="paneId"/> 只有回應 newConversation 的草稿才帶（步 13），其他是 null
    /// </summary>
    public static ClientOutboxMessage ConversationMessage(string conversationId, string headerJson, IReadOnlyList<string> itemJsons, bool hasMore, string composerJson,
        string? paneId = null) =>
        new(ClientOutboxRules.ConversationType, Write(new JObject
        {
            ["type"] = ClientOutboxRules.ConversationType,
            ["conversationId"] = conversationId,
            ["paneId"] = paneId is null ? JValue.CreateNull() : new JValue(paneId),
            ["header"] = new JRaw(headerJson),
            ["items"] = RawArray(itemJsons),
            ["hasMore"] = hasMore,
            ["composer"] = new JRaw(composerJson),
            ["draft"] = JValue.CreateNull(),
        }), null, conversationId);

    /// <summary>
    /// <c>{type:"olderItems", conversationId, beforeId, items, hasMore}</c>（不是增量：對話屏障不清它）。<paramref name="beforeId"/> 是 loadOlder 帶來的，原樣送回：
    /// 網頁只採用自己還在等的那一頁（loadOlder 之後先到整份 conversation 時，晚到的舊頁不插進新視窗；WP4.6 合併前審查 LIVE-2）
    /// </summary>
    public static ClientOutboxMessage OlderItemsMessage(string conversationId, string beforeId, IReadOnlyList<string> itemJsons, bool hasMore) =>
        new(OlderItemsType, Write(new JObject
        {
            ["type"] = OlderItemsType,
            ["conversationId"] = conversationId,
            ["beforeId"] = beforeId,
            ["items"] = RawArray(itemJsons),
            ["hasMore"] = hasMore,
        }), null, conversationId);

    /// <summary><c>{type:"header", conversationId, header}</c>：以 <see cref="ClientOutboxRules.HeaderKey"/> 合併</summary>
    public static ClientOutboxMessage HeaderMessage(string conversationId, string headerJson) =>
        new(ClientOutboxRules.HeaderType, Write(new JObject { ["type"] = ClientOutboxRules.HeaderType, ["conversationId"] = conversationId, ["header"] = new JRaw(headerJson) }),
            ClientOutboxRules.HeaderKey(conversationId), conversationId);

    /// <summary><c>{type:"item", conversationId, item}</c>：以 <see cref="ClientOutboxRules.ItemKey"/> 合併（與 removeItem 共用）</summary>
    public static ClientOutboxMessage ItemMessage(string conversationId, string itemId, string itemJson) =>
        new(ClientOutboxRules.ItemType, Write(new JObject { ["type"] = ClientOutboxRules.ItemType, ["conversationId"] = conversationId, ["item"] = new JRaw(itemJson) }),
            ClientOutboxRules.ItemKey(conversationId, itemId), conversationId);

    /// <summary>
    /// 疊加層的 <c>{type:"item", conversationId, item, rv}</c>：同 <see cref="ItemMessage"/>，多帶這一格的即時版本 <c>rv</c>（訊息層，<c>Item</c> 形狀不變；
    /// 舊網頁忽略）。之後的 <c>itemPatch</c> 以它為基底
    /// </summary>
    public static ClientOutboxMessage LiveItemMessage(string conversationId, string itemId, string itemJson, long rv) =>
        new(ClientOutboxRules.ItemType, Write(new JObject
        {
            ["type"] = ClientOutboxRules.ItemType,
            ["conversationId"] = conversationId,
            ["item"] = new JRaw(itemJson),
            ["rv"] = rv,
        }), ClientOutboxRules.ItemKey(conversationId, itemId), conversationId) { Rv = rv };

    /// <summary>
    /// <c>{type:"itemPatch", conversationId, id, rv, baseRv, set, ops:[{f, at, s, n}]}</c>（<c>patch1</c>）：不合併（<see cref="ClientOutboxMessage.Key"/> 是 null），
    /// 是對話的增量（<c>conversation</c> 屏障清掉）
    /// </summary>
    public static ClientOutboxMessage ItemPatchMessage(string conversationId, string itemId, long rv, long baseRv, ItemPatch patch) =>
        new(ClientOutboxRules.ItemPatchType, Write(new JObject
        {
            ["type"] = ClientOutboxRules.ItemPatchType,
            ["conversationId"] = conversationId,
            ["id"] = itemId,
            ["rv"] = rv,
            ["baseRv"] = baseRv,
            ["set"] = new JRaw(patch.SetJson),
            ["ops"] = new JArray(patch.Ops.Select(o => new JObject { ["f"] = o.F, ["at"] = o.At, ["s"] = o.S, ["n"] = o.N })),
        }), null, conversationId) { Rv = rv, PatchKey = ClientOutboxRules.ItemKey(conversationId, itemId) };

    /// <summary><c>{type:"removeItem", conversationId, id}</c>：以 <see cref="ClientOutboxRules.ItemKey"/> 合併（與 item 共用）</summary>
    public static ClientOutboxMessage RemoveItemMessage(string conversationId, string itemId) =>
        new(ClientOutboxRules.RemoveItemType, Write(new JObject { ["type"] = ClientOutboxRules.RemoveItemType, ["conversationId"] = conversationId, ["id"] = itemId }),
            ClientOutboxRules.ItemKey(conversationId, itemId), conversationId);

    /// <summary><c>{type:"composer", conversationId, composer}</c>：以 <see cref="ClientOutboxRules.ComposerKey"/> 合併</summary>
    public static ClientOutboxMessage ComposerMessage(string conversationId, string composerJson) =>
        new(ClientOutboxRules.ComposerType, Write(new JObject { ["type"] = ClientOutboxRules.ComposerType, ["conversationId"] = conversationId, ["composer"] = new JRaw(composerJson) }),
            ClientOutboxRules.ComposerKey(conversationId), conversationId);

    /// <summary><c>{type:"conversationClosed", conversationId}</c></summary>
    public static ClientOutboxMessage ClosedMessage(string conversationId) =>
        new(ConversationClosedType, Write(new JObject { ["type"] = ConversationClosedType, ["conversationId"] = conversationId }), null, conversationId);

    /// <summary><c>{type:"resync", conversationId}</c>〔遠端〕</summary>
    public static ClientOutboxMessage ResyncMessage(string conversationId) =>
        new(ResyncType, Write(new JObject { ["type"] = ResyncType, ["conversationId"] = conversationId }), null, conversationId);

    /// <summary><c>{type:"notify", conversationId, kind, title, deviceName}</c>〔遠端〕：通知類（init 屏障保留）</summary>
    public static ClientOutboxMessage NotifyMessage(string conversationId, string kind, string title, string? deviceName) =>
        new(ClientOutboxRules.NotifyType, Write(new JObject
        {
            ["type"] = ClientOutboxRules.NotifyType,
            ["conversationId"] = conversationId,
            ["kind"] = kind,
            ["title"] = title,
            ["deviceName"] = deviceName is null ? JValue.CreateNull() : new JValue(deviceName),
        }), null, conversationId);

    /// <summary><c>{type:"speak", conversationId, id, text}</c>：通知類，給每一條連線；<c>id</c> 是那筆事件的 seq（同一個瀏覽器開好幾個分頁時只念一次）</summary>
    public static ClientOutboxMessage SpeakMessage(string conversationId, long seq, string text) =>
        new(ClientOutboxRules.SpeakType, Write(new JObject
        {
            ["type"] = ClientOutboxRules.SpeakType,
            ["conversationId"] = conversationId,
            ["id"] = seq,
            ["text"] = text,
        }), null, conversationId);

    /// <summary><c>{type:"clipboard", conversationId, itemId, text}</c>〔遠端〕：通知類，只給發起的連線（D30）</summary>
    public static ClientOutboxMessage ClipboardMessage(string conversationId, string itemId, string text) =>
        new(ClientOutboxRules.ClipboardType, Write(new JObject
        {
            ["type"] = ClientOutboxRules.ClipboardType,
            ["conversationId"] = conversationId,
            ["itemId"] = itemId,
            ["text"] = text,
        }), null, conversationId);

    private static JArray RawArray(IReadOnlyList<string> jsons)
    {
        var array = new JArray();
        foreach (var json in jsons)
            array.Add(new JRaw(json));
        return array;
    }

    private static string Write(JObject message) => message.ToString(Formatting.None);

    private static long Max(long? current, long seq) => current is { } c && c > seq ? c : seq;

    private sealed class WorkBuilder
    {
        public bool Deleted { get; set; }

        public bool Resync { get; set; }

        public long? ResyncSeq { get; set; }

        public long? HeaderSeq { get; set; }

        public long? ComposerSeq { get; set; }

        /// <summary>依最後一次出現的順序（移除再加回）</summary>
        public OrderedDictionary<string, ConversationChange> Items { get; } = new(StringComparer.Ordinal);

        public List<ConversationChange> Attentions { get; } = [];

        /// <summary>清掉 resync 或刪除之前的增量</summary>
        public void Clear()
        {
            Items.Clear();
            HeaderSeq = null;
            ComposerSeq = null;
        }

        public ConversationRoundWork Build(string conversationId) =>
            new(conversationId, Deleted, Resync, ResyncSeq, HeaderSeq, ComposerSeq, Items.Values.ToList(), Attentions.ToList());
    }
}
