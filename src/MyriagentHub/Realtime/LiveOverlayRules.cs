using Myriagent.Agents;
using Myriagent.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 一則通過伺服器檢查的即時更新（WP4.2 計畫 D27；疊加層以 (<see cref="ConversationId"/>, <see cref="Ord"/>) 為鍵）。
/// <see cref="Item"/> 是要轉給觀看者的 <c>conv.item</c> JSON（命中 DLP 時是遮蔽版，D44）；<see cref="Bytes"/> 是它的 UTF-8 位元組
/// </summary>
/// <param name="ConnectionOrder">送出它的裝置連線的 <see cref="DeviceConnection.Order"/>（越大越新，F38）：跨連線以它決定新舊，不比 ver</param>
/// <param name="DeviceId">送出它的裝置（擁有者）：連線斷了、同一台有更新的連線 Prove 時清掉它的疊加</param>
internal sealed record LiveEntry(string ConversationId, long Ord, string ItemId, long Epoch, long Ver, long ConnectionOrder, string Item, int Bytes, string DeviceId);

/// <summary>
/// 一筆持久 <c>conv.item</c> 在疊加層看到的樣子（D27 的 ③）：ver、是否 final、提交時那台裝置最新的已 Prove 連線的 Order
/// （閘道取走變更時查；從 open 的快照得到的不知道是哪條連線提交的，記 0＝不比 ver）
/// </summary>
internal sealed record DurableMark(long Ver, bool Final, long CommittedOnConnectionOrder);

/// <summary>一則即時更新的處置（<see cref="LiveOverlayRules.Decide"/>）</summary>
internal enum LiveVerdict
{
    Accept,

    /// <summary>持久版本已經 final：之後的即時更新一律丟（設計 4.5、D27 ①）</summary>
    DropFinal,

    /// <summary>已有的疊加來自 Order 較大（較新）的連線（D27 ②）</summary>
    DropOlderConnection,

    /// <summary>同一條連線、ver 沒有比已有的疊加新</summary>
    DropNotNewer,

    /// <summary>持久版本是這條連線提交的、ver 不比它舊（D27 ③）</summary>
    DropSuperseded,
}

/// <summary>即時更新的權杖桶（<see cref="LiveOverlayRules.TakeToken"/>；以單調時鐘的毫秒計）</summary>
internal struct LiveTokenBucket
{
    public double Tokens;

    public long LastMs;

    public bool Started;
}

/// <summary>
/// 即時疊加層的純函式規則（WP4.2 計畫步 11；D27、提案 C4、F30、F38）：一則即時更新收不收、權杖桶、超過上限時淘汰哪些。
/// ver 是擁有者這一次啟動內的計數、不跨開機（P8 不採用）：只在同一條連線內比 ver，跨連線以連線的 Order 決定；
/// 持久版本只有在「提交時這條連線是那台裝置最新的已 Prove 連線」時才拿 ver 比（擁有者重開之後新連線的 ver 從 1 起算，舊的持久 ver 比較大也不能擋住它）
/// </summary>
internal static class LiveOverlayRules
{
    /// <summary>
    /// 依序：<paramref name="durable"/> 已經 final → <see cref="LiveVerdict.DropFinal"/>；<paramref name="existing"/> 的 Order 比較大 → <see cref="LiveVerdict.DropOlderConnection"/>；
    /// Order 相同而且 ver 沒有比較新 → <see cref="LiveVerdict.DropNotNewer"/>；持久版本存在、提交時的連線就是這一條、而且 ver 不比它舊
    /// （<see cref="LiveRules.IsSuperseded"/>）→ <see cref="LiveVerdict.DropSuperseded"/>；否則 <see cref="LiveVerdict.Accept"/>（Order 較小的已有疊加被取代）
    /// </summary>
    public static LiveVerdict Decide(LiveEntry? existing, DurableMark? durable, LiveEntry incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (durable is { Final: true })
            return LiveVerdict.DropFinal;
        if (existing is not null)
        {
            if (existing.ConnectionOrder > incoming.ConnectionOrder)
                return LiveVerdict.DropOlderConnection;
            if (existing.ConnectionOrder == incoming.ConnectionOrder && !LiveRules.IsNewer(Version(incoming), Version(existing)))
                return LiveVerdict.DropNotNewer;
        }
        // 第 4 階段每個對話只有一個世代：持久版本的世代就是這則的世代（LiveRelay 收件時已比過目錄的 epoch）
        if (durable is not null && durable.CommittedOnConnectionOrder == incoming.ConnectionOrder
            && LiveRules.IsSuperseded(Version(incoming), incoming.Epoch, durable.Ver))
            return LiveVerdict.DropSuperseded;
        return LiveVerdict.Accept;
    }

    /// <summary>
    /// 一筆持久 <c>conv.item</c> 提交之後，同一格的疊加要不要清掉（提案 C4）：持久版本的 status 不是 Running（包括 Waiting、final 的）一律清、不比 ver（a）；
    /// 仍是 Running 時照 <see cref="Decide"/> 的作廢規則（D27 ③：提交時的連線就是送這則疊加的那條、ver 不比它舊）
    /// </summary>
    public static bool ClearedByDurable(string? status, DurableMark durable, LiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(durable);
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.Equals(status, nameof(AgentItemStatus.Running), StringComparison.Ordinal))
            return true;
        return Decide(null, durable, entry) == LiveVerdict.DropSuperseded;
    }

    /// <summary>
    /// 權杖桶（D27：每台每秒 50 則、每個對話每秒 4 則，突發各自的上限）：第一次用時是滿的；依經過的時間補，最多到 <paramref name="burst"/>；
    /// 有一個以上的權杖就拿一個並回 true。時間倒退時當成沒有經過。<paramref name="burst"/> ≤0 或 <paramref name="ratePerSecond"/> ≤0 時一律 false。
    /// 允許突發而不是要求固定間隔：網路抖動時（平均速率不超過）最後一則不會被丟掉、畫面不會停在舊的內容
    /// </summary>
    public static bool TakeToken(ref LiveTokenBucket bucket, long nowMs, double ratePerSecond, int burst)
    {
        if (burst <= 0 || ratePerSecond <= 0)
            return false;
        if (!bucket.Started)
        {
            bucket.Started = true;
            bucket.Tokens = burst;
            bucket.LastMs = nowMs;
        }
        else if (nowMs > bucket.LastMs)
        {
            bucket.Tokens = Math.Min(burst, bucket.Tokens + (nowMs - bucket.LastMs) * ratePerSecond / 1000.0);
            bucket.LastMs = nowMs;
        }
        if (bucket.Tokens < 1)
            return false;
        bucket.Tokens -= 1;
        return true;
    }

    /// <summary>
    /// 超過上限時淘汰哪些（D27、K5）：<paramref name="all"/> 依收下的先後（最舊的在前）。先讓每個對話最多 <paramref name="maxPerConversation"/> 個 ord
    /// （超過的淘汰那個對話最舊的），再讓全部的位元組不超過 <paramref name="maxBytes"/>（淘汰全部裡最舊的）。回傳要淘汰的（依原本的先後）
    /// </summary>
    public static IReadOnlyList<LiveEntry> Evict(IReadOnlyList<LiveEntry> all, int maxPerConversation, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(all);
        var evicted = new bool[all.Count];
        var perConversation = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in all)
            perConversation[entry.ConversationId] = perConversation.GetValueOrDefault(entry.ConversationId) + 1;
        var limit = Math.Max(0, maxPerConversation);
        for (var i = 0; i < all.Count; i++)
        {
            var id = all[i].ConversationId;
            if (perConversation[id] <= limit)
                continue;
            evicted[i] = true;
            perConversation[id]--;
        }

        long bytes = 0;
        for (var i = 0; i < all.Count; i++)
        {
            if (!evicted[i])
                bytes += all[i].Bytes;
        }
        for (var i = 0; i < all.Count && bytes > maxBytes; i++)
        {
            if (evicted[i])
                continue;
            evicted[i] = true;
            bytes -= all[i].Bytes;
        }

        var result = new List<LiveEntry>();
        for (var i = 0; i < all.Count; i++)
        {
            if (evicted[i])
                result.Add(all[i]);
        }
        return result;
    }

    private static LiveVersion Version(LiveEntry entry) => new(entry.ConversationId, entry.Epoch, entry.Ord, entry.Ver);
}
