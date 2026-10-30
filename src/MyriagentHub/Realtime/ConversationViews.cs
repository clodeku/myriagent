using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Myriagent.Sync;
using MyriagentHub.Rendering;
using MyriagentHub.Sync;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>一份對話清單（某個時區、某個當地日期）：<c>items</c> 陣列的 JSON、它的雜湊與快取鍵（init 的快取也看它）</summary>
internal sealed record ConversationListView(string Key, string ItemsJson, string Hash);

/// <summary>渲染快取的鍵（D24）：同一個項目、同一個 last_seq、同一個工作資料夾的渲染結果相同</summary>
internal readonly record struct ConversationRenderKey(string ConversationId, string ItemId, long Seq, string? WorkingDirectory);

/// <summary>
/// 閘道的對話讀取與渲染門面（WP4.2 計畫步 10；D20、D22、D24、D38）：清單、標頭、輸入區、open 的快照與 loadOlder 的一頁、變更的項目。
/// <b>只給閘道迴圈用</b>（在 <c>runGate</c> 之下，D24）：快取都是一般的集合、不加鎖。讀取一律經 <see cref="ConversationReader"/>（短的唯讀交易），
/// 渲染一律經 <see cref="HubItemRenderer"/>（每次 new 一個 ItemRenderer）。
/// <list type="bullet">
/// <item>清單：讀一次（<see cref="MarkListStale"/> 之後才重讀），每個 (時區, 當地日期) 組一次、各連線共用同一份字串；上線狀態或未讀改變只重組不重讀
/// （<c>unread</c> 取自伺服器的未讀集合，步 12A）。</item>
/// <item>標頭：以對話快取讀到的列（含裝置名稱）；標頭類的變更、刪除、resync 時由閘道作廢。裝置的上線狀態與重建中在組 JSON 時才看（讀取時的投影）。</item>
/// <item>項目：依 (對話, 項目, last_seq, 工作資料夾) 快取渲染結果，LRU、上限 <c>RenderCacheMaxBytes</c>（以 UTF-16 字數×2 估計）。</item>
/// </list>
/// </summary>
internal sealed class ConversationViews(
    ConversationReader reader,
    HubItemRenderer renderer,
    ConversationDirectoryCache directory,
    DeviceDirectory devices,
    IDevicePresence presence,
    ConversationChangeFeed feed,
    ConversationCounters counters,
    IOptions<HubOptions> options,
    ConversationUnread? unread = null)
{
    /// <summary>標頭快取最多幾個對話（超過就整個清掉；開著的對話通常遠少於此）</summary>
    public const int MaxCachedHeaders = 256;

    /// <summary>清單快取最多幾份（各連線的時區與日期組合；超過就整個清掉）</summary>
    public const int MaxCachedLists = 64;

    private readonly Dictionary<string, ConversationHeaderRead> headers = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Tz, long Day), ConversationListView> lists = [];
    private readonly Dictionary<(string User, int Tz, long Day), (string Stamp, ConversationListView View)> collaboratorLists = [];
    private readonly RenderCache cache = new(Math.Max(0, options.Value.Conversations.RenderCacheMaxBytes));
    private ConversationListRead? list;
    private bool listStale = true;
    private long listVersion;
    private long devicesVersion;
    private long unreadVersion;

    /// <summary>變更通知（閘道每一輪取走）</summary>
    public ConversationChangeFeed Feed => feed;

    /// <summary>伺服器記的未讀（步 12A；null＝沒有未讀：清單的 <c>unread</c> 一律 false）</summary>
    public ConversationUnread? Unread => unread;

    /// <summary>營運計數（送出佇列的溢出）</summary>
    public ConversationCounters Counters => counters;

    /// <summary>渲染快取目前的估計位元組（測試用）</summary>
    internal long RenderCacheBytes => cache.Bytes;

    /// <summary>渲染快取目前的筆數（測試用）</summary>
    internal int RenderCacheCount => cache.Count;

    private Hosting.ConversationSettings Settings => options.Value.Conversations;

    // ───── 作廢 ─────

    /// <summary>清單的資料變了（meta、turn、刪除、清除、重建、整份重讀）：下次用到時重讀</summary>
    public void MarkListStale() => listStale = true;

    /// <summary>有裝置上線或離線（D39）：清單的 <c>deviceOnline</c>／<c>running</c> 要重組（不必重讀）</summary>
    public void MarkPresenceChanged()
    {
        devicesVersion++;
        lists.Clear();
    }

    /// <summary>裝置目錄變了（改名、撤銷）：清單重讀（名稱在 <c>device_streams</c>）、標頭快取清掉（名稱跟著標頭一起讀）</summary>
    public void MarkDevicesChanged()
    {
        MarkPresenceChanged();
        listStale = true;
        headers.Clear();
    }

    /// <summary>這個對話的標頭要重讀</summary>
    public void InvalidateHeader(string conversationId) => headers.Remove(conversationId);

    /// <summary>所有標頭都要重讀（整份重讀、讀取失敗之後）</summary>
    public void ClearHeaders() => headers.Clear();

    /// <summary>
    /// 變更通知溢出或整份重讀之後（合併前審查 P4SRV-MAT-04）：未讀集合裡已刪除或目錄裡已經沒有的對話拿掉——被丟掉的 Deleted 不會再取到，
    /// 不補的話集合（ops/status 的 <c>unread</c>）一直多算到重開服務。目錄在同一個 AfterCommit 裡先於通知更新，所以這時已經是刪除後的狀態。
    /// 以 <see cref="ConversationUnread.Clear"/> 拿掉（也刪 sync.db 的列；刪除的交易已經刪掉的列，DELETE 不影響任何列）。回傳拿掉幾個
    /// </summary>
    public int ReconcileUnread()
    {
        if (unread is null)
            return 0;
        var removed = 0;
        foreach (var conversationId in unread.Ids())
        {
            if ((!directory.TryGet(conversationId, out var entry) || entry.Deleted) && unread.Clear(conversationId))
                removed++;
        }
        return removed;
    }

    // ───── 清單（D22） ─────

    /// <summary>
    /// 某個時區、這一刻的清單（同一個當地日期共用一份）；資料過時就先重讀（一個唯讀交易）。未讀的集合改了（<see cref="ConversationUnread.Version"/>）只重組、不重讀（步 12A）
    /// </summary>
    public ConversationListView ListFor(int tzOffsetMinutes, long nowMs)
    {
        if (list is null || listStale)
        {
            list = reader.List();
            listStale = false;
            listVersion++;
            lists.Clear();
        }
        if (unread is not null && unread.Version != unreadVersion)
        {
            unreadVersion = unread.Version;
            lists.Clear();
        }
        var day = ConversationGatewayRules.LocalDay(nowMs, tzOffsetMinutes);
        if (lists.TryGetValue((tzOffsetMinutes, day), out var cached))
            return cached;
        var items = new JArray();
        foreach (var row in list.Rows)
        {
            items.Add(ConversationViewRules.Summary(row, nowMs, tzOffsetMinutes, DeviceOf(row.OwnerDeviceId, list.DeviceNames),
                unread: unread?.Contains(row.ConversationId) ?? false));
        }
        var json = items.ToString(Formatting.None);
        if (lists.Count >= MaxCachedLists)
            lists.Clear();
        var view = new ConversationListView(
            string.Create(CultureInfo.InvariantCulture, $"{listVersion}.{devicesVersion}.{unreadVersion}.{tzOffsetMinutes}.{day}"), json, ConversationGatewayRules.ListHash(json));
        lists[(tzOffsetMinutes, day)] = view;
        return view;
    }

    /// <summary>
    /// 協作人的清單（多人協作）：只有分享給他的對話（<paramref name="shared"/>，線上的 id）、未讀一律 false（未讀是擁有者的）。
    /// 每位協作人、每個 (時區, 當地日期) 快取一份，清單、上線狀態或分享（<paramref name="sharesVersion"/>）變了才重組
    /// </summary>
    public ConversationListView ListForCollaborator(string collaboratorId, IReadOnlySet<string> shared, long sharesVersion, int tzOffsetMinutes, long nowMs)
    {
        if (list is null || listStale)
        {
            list = reader.List();
            listStale = false;
            listVersion++;
            lists.Clear();
        }
        var day = ConversationGatewayRules.LocalDay(nowMs, tzOffsetMinutes);
        var stamp = string.Create(CultureInfo.InvariantCulture, $"c.{collaboratorId}.{listVersion}.{devicesVersion}.{sharesVersion}.{tzOffsetMinutes}.{day}");
        if (collaboratorLists.TryGetValue((collaboratorId, tzOffsetMinutes, day), out var cached) && cached.Stamp == stamp)
            return cached.View;
        var items = new JArray();
        foreach (var row in list.Rows)
        {
            if (shared.Contains(row.ConversationId))
                items.Add(ConversationViewRules.Summary(row, nowMs, tzOffsetMinutes, DeviceOf(row.OwnerDeviceId, list.DeviceNames), unread: false));
        }
        var json = items.ToString(Formatting.None);
        if (collaboratorLists.Count >= MaxCachedLists)
            collaboratorLists.Clear();
        var view = new ConversationListView(stamp, json, ConversationGatewayRules.ListHash(json));
        collaboratorLists[(collaboratorId, tzOffsetMinutes, day)] = (stamp, view);
        return view;
    }

    // ───── 標頭與輸入區（D21） ─────

    /// <summary>標頭的資料列（快取；不存在或已刪除回 null）</summary>
    public ConversationHeaderRead? Header(string conversationId)
    {
        if (headers.TryGetValue(conversationId, out var cached))
            return cached;
        if (reader.ReadHeaderView(conversationId) is not { } view)
            return null;
        Remember(view);
        return view;
    }

    /// <summary>Header 的 JSON：裝置的上線狀態與重建中在這時候才看（讀取時的投影）</summary>
    public string HeaderJson(ConversationHeaderRead view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var row = view.Header;
        return ConversationViewRules.Header(row, DeviceOf(row.OwnerDeviceId, view.DeviceNames), directory.IsRebuilding(row.ConversationId)).ToString(Formatting.None);
    }

    /// <summary>Composer 的 JSON（讀一次 <c>conversation_runtime</c>；沒有那一列是空的輸入區）</summary>
    public string ComposerJson(string conversationId) => ConversationViewRules.Composer(reader.ReadRuntime(conversationId).Runtime).ToString(Formatting.None);

    /// <summary>notify 的標題與裝置名稱（D28）；對話不在了回 null</summary>
    public (string Title, string? DeviceName)? NotifyInfo(string conversationId)
    {
        if (Header(conversationId) is not { } view)
            return null;
        return (view.Header.Title, DeviceOf(view.Header.OwnerDeviceId, view.DeviceNames).DeviceName);
    }

    /// <summary>目錄裡這個對話的擁有者（上線狀態改變時找要重送標頭的對話）；不認得回 null</summary>
    public string? OwnerOf(string conversationId) => directory.TryGet(conversationId, out var entry) ? entry.OwnerDeviceId : null;

    // ───── 項目（D20、D24） ─────

    /// <summary>
    /// 一筆 <c>Item</c> 變更的 Item JSON：變更附了 body（16 KB 以內）就直接渲染，否則讀一次 <c>items</c>（那時的最新版本）；項目不在了回 null
    /// （之後的 ItemRemoved 或刪除會送到）
    /// </summary>
    public string? ChangeItemJson(ConversationChange change, ConversationHeaderRow header)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(header);
        if (change.ItemId is not { } itemId)
            return null;
        if (change.Body is { } body && change.Ord is { } ord)
        {
            var key = new ConversationRenderKey(change.ConversationId, itemId, change.Seq, header.WorkingDirectory);
            if (cache.TryGet(key, out var json))
                return json;
            json = renderer.Render(new HubItemSource(change.ConversationId, header.OwnerDeviceId ?? string.Empty, header.WorkingDirectory, itemId, ord, body,
                PayloadRules.EncJson, change.ReceivedAt), Settings.RenderedItemMaxBytes);
            cache.Put(key, json);
            return json;
        }
        var (row, _) = reader.ReadItem(change.ConversationId, itemId);
        return row is null ? null : RowJson(header, row);
    }

    // ───── open 與 loadOlder（D38） ─────

    /// <summary>
    /// 即時疊加層的一格的 Item JSON（步 11、D24：渲染只在閘道迴圈）：同持久項目的渲染（擁有者、工作資料夾取自標頭），不放進渲染快取
    /// （即時更新沒有持久的 seq；結果存在那一格，各連線共用）
    /// </summary>
    public string LiveItemJson(LiveSlot slot, ConversationHeaderRow header)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(header);
        var entry = slot.Entry;
        // 即時的格以 live2 的上限解碼（收件時 CheckRelay 已驗過 ≤256 KB；持久 payload 的 128 KB 會讓更大的串流中項目變成佔位）
        return renderer.Render(new HubItemSource(entry.ConversationId, header.OwnerDeviceId ?? string.Empty, header.WorkingDirectory, entry.ItemId, entry.Ord, entry.Item,
            PayloadRules.EncJson, slot.ReceivedAt, LiveRules.MaxDeltaItemBytes), Settings.RenderedItemMaxBytes);
    }

    /// <summary>
    /// 一個項目目前的持久版本（疊加被清掉、要改推持久版本時；步 11）：讀一次 <c>items</c>，有就回 <c>item</c>，沒有（還沒持久化、已移除、內容被清掉）回 <c>removeItem</c>；
    /// 對話不在了回 null（之後的刪除或 resync 會處理）
    /// </summary>
    public ClientOutboxMessage? CurrentItemMessage(string conversationId, string itemId)
    {
        if (Header(conversationId) is not { } view)
            return null;
        var (row, _) = reader.ReadItem(conversationId, itemId);
        return row is null
            ? ConversationGatewayRules.RemoveItemMessage(conversationId, itemId)
            : ConversationGatewayRules.ItemMessage(conversationId, itemId, RowJson(view.Header, row));
    }

    /// <summary>
    /// open 的回應：同一個唯讀交易讀標頭、最後一頁與輸入區，渲染後依 <c>PageMaxBytes</c> 少放（至少一筆），回 <c>conversation</c>、快照的 seq（D38 的 S）
    /// 與讀到的項目的 (ord, final)（即時疊加層補持久版本的標記，步 11）。對話不存在或已刪除回 <c>conversationClosed</c>、null 與空的清單
    /// </summary>
    public (ClientOutboxMessage Message, long? Seq, IReadOnlyList<(long Ord, bool Final)> Items) Snapshot(string conversationId)
    {
        if (reader.ReadSnapshot(conversationId, ConversationViewRules.PageItems) is not { } snapshot)
        {
            headers.Remove(conversationId);
            return (ConversationGatewayRules.ClosedMessage(conversationId), null, []);
        }
        var view = new ConversationHeaderRead(snapshot.Header, snapshot.DeviceNames, snapshot.Seq);
        Remember(view);
        var rendered = snapshot.Page.Items.Select(row => RowJson(snapshot.Header, row)).ToList();
        var (items, hasMore) = ConversationGatewayRules.PageOf(rendered, snapshot.Page.HasMore, ConversationViewRules.PageItems, Settings.PageMaxBytes);
        var composer = ConversationViewRules.Composer(snapshot.Runtime).ToString(Formatting.None);
        return (ConversationGatewayRules.ConversationMessage(conversationId, HeaderJson(view), items, hasMore, composer), snapshot.Seq,
            snapshot.Page.Items.Select(row => (row.Ord, row.Final)).ToList());
    }

    /// <summary>loadOlder 的回應：<paramref name="beforeId"/> 之前最多 80 筆（找不到它時是最後一頁，web-protocol）；對話不在了回 <c>conversationClosed</c></summary>
    public (ClientOutboxMessage Message, long? Seq) Older(string conversationId, string beforeId)
    {
        ArgumentNullException.ThrowIfNull(beforeId);
        if (Header(conversationId) is not { } view)
            return (ConversationGatewayRules.ClosedMessage(conversationId), null);
        var (page, seq) = reader.ReadPage(conversationId, beforeId, ConversationViewRules.PageItems);
        var rendered = page.Items.Select(row => RowJson(view.Header, row)).ToList();
        var (items, hasMore) = ConversationGatewayRules.PageOf(rendered, page.HasMore, ConversationViewRules.PageItems, Settings.PageMaxBytes);
        return (ConversationGatewayRules.OlderItemsMessage(conversationId, beforeId, items, hasMore), seq);
    }

    private string RowJson(ConversationHeaderRow header, ConversationItemRow row)
    {
        var key = new ConversationRenderKey(header.ConversationId, row.ItemId, row.LastSeq, header.WorkingDirectory);
        if (cache.TryGet(key, out var json))
            return json;
        json = renderer.Render(new HubItemSource(header.ConversationId, header.OwnerDeviceId ?? string.Empty, header.WorkingDirectory, row.ItemId, row.Ord, row.Body,
            row.Enc, row.ReceivedAt), Settings.RenderedItemMaxBytes);
        cache.Put(key, json);
        return json;
    }

    private void Remember(ConversationHeaderRead view)
    {
        if (headers.Count >= MaxCachedHeaders && !headers.ContainsKey(view.Header.ConversationId))
            headers.Clear();
        headers[view.Header.ConversationId] = view;
    }

    /// <summary>擁有者裝置的欄位（P5）：名稱取 <c>device_streams.name</c>，沒有時用配對時的名稱；在線＝有連線、沒有撤銷</summary>
    private ConversationDeviceInfo DeviceOf(string? owner, IReadOnlyDictionary<string, string> names) =>
        owner is null
            ? ConversationDeviceInfo.None
            : ConversationViewRules.Device(owner, devices.Get(owner), names.TryGetValue(owner, out var name) ? name : null, presence.IsOnline(owner));

    /// <summary>LRU 的渲染快取（只在閘道迴圈裡用，不加鎖）；單筆超過上限的不放</summary>
    private sealed class RenderCache(long maxBytes)
    {
        private readonly Dictionary<ConversationRenderKey, LinkedListNode<(ConversationRenderKey Key, string Json)>> map = [];
        private readonly LinkedList<(ConversationRenderKey Key, string Json)> lru = new();

        public long Bytes { get; private set; }

        public int Count => map.Count;

        public bool TryGet(ConversationRenderKey key, [MaybeNullWhen(false)] out string json)
        {
            if (map.TryGetValue(key, out var node))
            {
                lru.Remove(node);
                lru.AddFirst(node);
                json = node.Value.Json;
                return true;
            }
            json = null;
            return false;
        }

        public void Put(ConversationRenderKey key, string json)
        {
            var size = SizeOf(json);
            if (size > maxBytes)
                return;
            if (map.Remove(key, out var old))
            {
                lru.Remove(old);
                Bytes -= SizeOf(old.Value.Json);
            }
            map[key] = lru.AddFirst((key, json));
            Bytes += size;
            while (Bytes > maxBytes && lru.Last is { } last)
            {
                lru.RemoveLast();
                map.Remove(last.Value.Key);
                Bytes -= SizeOf(last.Value.Json);
            }
        }

        private static long SizeOf(string json) => json.Length * 2L;
    }
}
