using System.Text;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 一則即時更新的版本（docs/sync-protocol.md 14.3）：同一筆項目以 (<see cref="ConversationId"/>, <see cref="Ord"/>) 認，
/// 新舊比 (<see cref="Epoch"/>, <see cref="Ver"/>)：世代大的勝（第 5 階段換擁有者後 ver 從頭算），同世代比 ver
/// </summary>
internal readonly record struct LiveVersion(string ConversationId, long Epoch, long Ord, long Ver);

/// <summary>
/// 即時更新（<c>LiveRelay</c>，<c>live1</c>）的規則（設計 14.11 D34、D46；sync-protocol 14.3）：即時更新只是提示，持久事件才是準——
/// 可以合併（同一筆只留最新）、可以丟（太大、太頻繁、連線忙的直接不送），持久的 <c>conv.item</c> 到了就以它為準。
/// 擁有者的送出佇列、伺服器轉給觀看者、瀏覽器的疊加層都用同一份規則。
/// <para>ver 只用來比即時更新之間的新舊（與比持久版本有沒有追上）。持久的 <c>conv.item</c> 之間不比 ver：同一筆以伺服器
/// <c>seq</c> 後到的整筆取代先到的（載入對話時修正狀態這類不加 ver 的改變也是一則新的持久事件），所以 ver 不必在持久事件之間單調。</para>
/// </summary>
internal static class LiveRules
{
    /// <summary>一則即時更新的上限（UTF-8；超過就不送這一則，等持久事件）</summary>
    public const int MaxLiveBytes = 64 * 1024;

    /// <summary>
    /// 協商出 <c>live2</c>（增量）時，一則即時更新重組後的整筆上限（UTF-8）：增量讓流量隨長度線性成長，上限可以提高；
    /// 同伺服器渲染的上限（<c>Hub:Conversations:RenderedItemMaxBytes</c> 256 KB）。超過照舊不送、只剩持久的檢查點
    /// </summary>
    public const int MaxDeltaItemBytes = 256 * 1024;

    public static bool Fits(string json) => Fits(json, MaxLiveBytes);

    public static bool Fits(string json, int maxBytes) => Encoding.UTF8.GetByteCount(json) <= maxBytes;

    /// <summary>這條連線的即時更新上限：協商出 <c>live2</c> 是 <see cref="MaxDeltaItemBytes"/>，否則 <see cref="MaxLiveBytes"/></summary>
    public static int MaxItemBytes(bool live2) => live2 ? MaxDeltaItemBytes : MaxLiveBytes;

    /// <summary>
    /// 增量模式（<c>live2</c>）依大小降頻：每則都要對整份文字做遮蔽、驗證、渲染（O(n)），每個串流中的對話每秒處理的文字量固定在約 512 KB。
    /// ≤64 KB 是 1000／<paramref name="maxHz"/>（8 Hz 時 125 ms）、≤128 KB 是 250 ms、更大的 500 ms；<paramref name="maxHz"/> ≤0 回 0（不提供）
    /// </summary>
    public static int DeltaIntervalMs(int itemBytes, int maxHz)
    {
        if (maxHz <= 0)
            return 0;
        var base1 = (1000 + maxHz - 1) / maxHz;
        return itemBytes switch
        {
            <= 64 * 1024 => base1,
            <= 128 * 1024 => Math.Max(base1, 250),
            _ => Math.Max(base1, 500),
        };
    }

    /// <summary>
    /// 一則 <see cref="LiveRelay"/> 合不合格（伺服器收到時、轉給觀看者之前；擁有者送出前也可以自檢）：合格回 null，否則
    /// <see cref="SyncErrorCodes.TooLarge"/>（<c>item</c> 超過 <see cref="MaxLiveBytes"/>）或 <see cref="SyncErrorCodes.Invalid"/>。
    /// 對話 id 是線上格式、epoch／ord／ver ≥1；<c>item</c> 照 <c>conv.item</c> 的共用驗證（<see cref="ConversationEventRules.Validate(string?, string?, out string?, int)"/>：
    /// 只有一種解讀、列舉只收名稱〔例如 <c>risk</c> 不收數字〕、禁止的本機欄位），而且裡面的 <c>ord</c>／<c>ver</c> 等於外層的
    /// </summary>
    /// <param name="detail">不合格的原因（記錄用，不含項目的內容）</param>
    public static string? CheckRelay(LiveRelay? relay, out string? detail) => CheckRelay(relay, out detail, MaxLiveBytes);

    /// <inheritdoc cref="CheckRelay(LiveRelay?, out string?)"/>
    /// <param name="maxBytes">整筆的上限：沒有 <c>live2</c> 是 <see cref="MaxLiveBytes"/>，有是 <see cref="MaxDeltaItemBytes"/>（<see cref="MaxItemBytes"/>）</param>
    public static string? CheckRelay(LiveRelay? relay, out string? detail, int maxBytes)
    {
        detail = null;
        if (relay is null || !ConversationIds.IsWireId(relay.ConversationId) || !ConversationIds.IsEpoch(relay.Epoch) || relay.Ord < 1 || relay.Ver < 1)
        {
            detail = "對話 id、epoch、ord 或 ver 不合格";
            return SyncErrorCodes.Invalid;
        }

        if (relay.Item is null)
        {
            detail = "沒有 item";
            return SyncErrorCodes.Invalid;
        }

        if (!Fits(relay.Item, maxBytes))
        {
            detail = $"item 超過 {maxBytes / 1024} KB";
            return SyncErrorCodes.TooLarge;
        }

        if (ConversationEventRules.Validate(ConversationEventKinds.Item, relay.Item, out detail, maxBytes, Math.Max(maxBytes, ConversationEventRules.MaxPayloadBytes)) is { } code)
            return code;
        if (!ConversationEventRules.TryParseStrict(relay.Item, out var token) || token is not JObject item
            || item["ord"] is not JValue { Value: long ord } || ord != relay.Ord
            || item["ver"] is not JValue { Value: long ver } || ver != relay.Ver)
        {
            detail = "item 的 ord／ver 與外層不同";
            return SyncErrorCodes.Invalid;
        }

        return null;
    }

    public static bool IsSameItem(LiveVersion a, LiveVersion b) => a.Ord == b.Ord && string.Equals(a.ConversationId, b.ConversationId, StringComparison.Ordinal);

    /// <summary><paramref name="candidate"/> 比 <paramref name="current"/> 新（只在同一筆時有意義）：世代大的勝，同世代比 ver；完全相同不算新</summary>
    public static bool IsNewer(LiveVersion candidate, LiveVersion current)
        => candidate.Epoch != current.Epoch ? candidate.Epoch > current.Epoch : candidate.Ver > current.Ver;

    /// <summary>
    /// 合併待送的即時更新：同一筆只留最新的一則（版本完全相同時留後到的），位置是那一筆第一次出現的位置（不同項目之間的順序不變）
    /// </summary>
    public static List<T> Merge<T>(IEnumerable<T> updates, Func<T, LiveVersion> version)
    {
        var result = new List<T>();
        var index = new Dictionary<(string, long), int>();
        foreach (var update in updates)
        {
            var v = version(update);
            var key = (v.ConversationId, v.Ord);
            if (!index.TryGetValue(key, out var at))
            {
                index[key] = result.Count;
                result.Add(update);
            }
            else if (!IsNewer(version(result[at]), v))
            {
                result[at] = update;
            }
        }

        return result;
    }

    /// <summary>
    /// 持久事件已經有了（<paramref name="durableEpoch"/>／<paramref name="durableVer"/> 是那一筆持久 <c>conv.item</c> 的世代與 ver）：
    /// 持久版本不比這則即時更新舊時，這則作廢（不顯示、不轉送）。只比即時更新與持久版本；持久事件之間以 <c>seq</c> 取代、不比 ver
    /// </summary>
    public static bool IsSuperseded(LiveVersion live, long durableEpoch, long durableVer)
        => durableEpoch != live.Epoch ? durableEpoch > live.Epoch : durableVer >= live.Ver;

    /// <summary>頻率：距離這個對話上一則至少 1000 / <paramref name="maxHz"/> 毫秒（單調時鐘）；<paramref name="maxHz"/> ≤0 一律不送</summary>
    /// <param name="lastSentMs">上一則送出的單調時鐘毫秒；還沒送過為 null</param>
    public static bool Allows(long? lastSentMs, long nowMs, int maxHz)
    {
        if (maxHz <= 0)
            return false;
        if (lastSentMs is not { } last)
            return true;
        var interval = (1000 + maxHz - 1) / maxHz;
        return nowMs - last >= interval;
    }
}
