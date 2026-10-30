using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>E10 回的一筆：這台還缺縮圖的項目參照</summary>
internal sealed record MissingThumb(string Ref, string ConversationId, string ItemId);

/// <summary>E10 的一頁（<see cref="Next"/> 是下一頁的 <c>before</c>，null＝沒有了）</summary>
internal sealed record MissingThumbPage(IReadOnlyList<MissingThumb> Refs, string? Next);

/// <summary>
/// 縮圖上傳（WP4.7 計畫 D30 桌面那一半；純函式）：E8 <c>PUT /api/v1/device/thumbs/{ref}</c>（標頭 <c>X-Myriagent-Conversation</c>、PNG ≤512 KB）、
/// E10 <c>GET /api/v1/device/thumbs/missing?before=&amp;limit=</c>（由新到舊、每頁 ≤200）。佇列 ≤<see cref="MaxQueue"/>，滿了丟最舊的（之後由 E10 補）；
/// 佇列還有 <see cref="PullLimit"/> 筆的空位才拉下一頁（E10 由新到舊，一次拉滿會把最新的擠掉）
/// </summary>
internal static class ThumbnailPublishRules
{
    /// <summary>佇列上限</summary>
    public const int MaxQueue = 500;

    /// <summary>E10 一頁要幾筆（伺服器的上限）</summary>
    public const int PullLimit = 200;

    /// <summary>E10 的游標最長幾個字（<c>{createdAt}_{ref}</c>，留餘裕）</summary>
    public const int MaxCursorLength = 64;

    /// <summary>outbox 還有未完成的列時多久再看一次（一般事件優先，F43）</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(2);

    public static string PutPath(string mediaRef)
        => ConversationEventRules.IsMediaRef(mediaRef) ? "/api/v1/device/thumbs/" + mediaRef : throw new ArgumentException("參照不合格", nameof(mediaRef));

    /// <summary>E10 的網址（<paramref name="before"/> 是上一頁的 next；null＝第一頁）</summary>
    public static string MissingPath(string? before)
    {
        var path = "/api/v1/device/thumbs/missing?limit=" + PullLimit.ToString(CultureInfo.InvariantCulture);
        return before is null ? path : path + "&before=" + Uri.EscapeDataString(before);
    }

    /// <summary>還能不能拉下一頁：佇列至少空出一頁</summary>
    public static bool MayPull(int queued) => queued <= MaxQueue - PullLimit;

    /// <summary>
    /// E10 的回應本文 → 一頁；不是 <c>{refs:[…], next}</c> 的形狀回 null。個別不合格的項目（參照、對話 id、項目 id）略過，最多取 <see cref="PullLimit"/> 筆；
    /// <c>next</c> 不是字串或太長當成沒有下一頁
    /// </summary>
    public static MissingThumbPage? ParseMissing(string? json)
    {
        if (string.IsNullOrEmpty(json) || !ConversationEventRules.TryParseStrict(json, out var token) || token is not JObject root || root["refs"] is not JArray array)
            return null;
        var refs = new List<MissingThumb>();
        foreach (var element in array)
        {
            if (refs.Count >= PullLimit)
                break;
            if (element is not JObject entry)
                continue;
            var mediaRef = Text(entry["ref"]);
            var conversationId = Text(entry["conversationId"]);
            var itemId = Text(entry["itemId"]);
            if (ConversationEventRules.IsMediaRef(mediaRef) && ConversationIds.IsWireId(conversationId) && ConversationIds.IsItemId(itemId))
                refs.Add(new MissingThumb(mediaRef!, conversationId!, itemId!));
        }

        var next = Text(root["next"]);
        return new MissingThumbPage(refs, next is { Length: > 0 and <= MaxCursorLength } ? next : null);
    }

    private static string? Text(JToken? token) => token is { Type: JTokenType.String } ? token.Value<string>() : null;

    // ── 原檔已經不在的參照（2026-10-27） ──
    // 伺服器的 E10 一直列出原檔已被刪掉的參照（worktree 的探針截圖、Playwright 的 test-results），桌面每次連線都問中樞、過守門員、
    // 被拒（not_found）一次（10-27 一天 54 次，大多在 app 換版重開之後）。被拒的記進 sync.db 的 kv（SyncStoreSchema.ThumbGone），
    // 一段時間內不再排；伺服器端照舊列著（要讓 E10 不列得改伺服器的資料表），只是桌面不再白做

    /// <summary>原檔不在的參照多久之後再試一次（檔案可能被放回來）</summary>
    public static readonly TimeSpan GoneRetryAfter = TimeSpan.FromDays(7);

    /// <summary>最多記幾個原檔不在的參照（多的丟最早記的）</summary>
    public const int MaxGone = 2_000;

    /// <summary>
    /// kv 的值 → 參照與記下的時間（Unix 毫秒）。不是 JSON 物件回空的；值不是正整數、參照不合格的略過（舊版或壞掉的值不能讓上傳停住）
    /// </summary>
    public static Dictionary<string, long> ParseGone(string? json)
    {
        var gone = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json) || !ConversationEventRules.TryParseStrict(json, out var token) || token is not JObject root)
            return gone;
        foreach (var property in root.Properties())
        {
            if (ConversationEventRules.IsMediaRef(property.Name) && property.Value is { Type: JTokenType.Integer } value && value.Value<long>() > 0)
                gone[property.Name] = value.Value<long>();
        }
        return gone;
    }

    /// <summary>參照與記下的時間 → kv 的值（依參照排序，同樣的內容寫出同樣的字串）</summary>
    public static string SerializeGone(IReadOnlyDictionary<string, long> gone)
    {
        var root = new JObject();
        foreach (var (mediaRef, at) in gone.OrderBy(e => e.Key, StringComparer.Ordinal))
            root[mediaRef] = at;
        return root.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// 這個參照的原檔已知不在、還沒到再試的時間：記下的時間前後 <see cref="GoneRetryAfter"/> 之內（時鐘被調回去的不會一直不試）
    /// </summary>
    public static bool IsGone(IReadOnlyDictionary<string, long> gone, string mediaRef, long nowMs)
        => gone.TryGetValue(mediaRef, out var at) && Math.Abs(nowMs - at) < (long)GoneRetryAfter.TotalMilliseconds;

    /// <summary>丟掉到期的；還超過 <see cref="MaxGone"/> 就丟最早記的（同時間的依參照排序，結果固定）</summary>
    public static void PruneGone(Dictionary<string, long> gone, long nowMs)
    {
        foreach (var mediaRef in gone.Where(e => !IsGone(gone, e.Key, nowMs)).Select(e => e.Key).ToList())
            gone.Remove(mediaRef);
        if (gone.Count <= MaxGone)
            return;
        foreach (var mediaRef in gone.OrderBy(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).Take(gone.Count - MaxGone).Select(e => e.Key).ToList())
            gone.Remove(mediaRef);
    }
}
