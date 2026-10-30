namespace Myriagent.Sync;

/// <summary>
/// 抽屜（<c>@list</c>）與 fileFetch（<c>@file</c>）的媒體參照在 <c>media_refs_local</c> 的期限與配發（WP4.7 計畫 D33、D36；純函式，時間是本機的 Unix ms，只和本機自己記的比）：
/// 伺服器的 <c>list</c> 參照 30 天、<c>file</c> 15 分鐘到期（D7、設計:1066），這台同一個檔 29 天內重用同一個參照（留一天給兩邊的時間差），
/// 每日修剪超過期限的列
/// </summary>
internal static class LooseMediaRefRules
{
    /// <summary>抽屜的參照重用多久（同一個對話、同一個完整路徑）</summary>
    public static readonly TimeSpan ListReuse = TimeSpan.FromDays(29);

    /// <summary>抽屜的參照留多久（伺服器 30 天後拒絕）</summary>
    public static readonly TimeSpan ListLifetime = TimeSpan.FromDays(30);

    /// <summary>fileFetch 的參照留多久（伺服器 15 分鐘後拒絕）</summary>
    public static readonly TimeSpan FileLifetime = TimeSpan.FromMinutes(15);

    /// <summary>每隔多久修剪一次</summary>
    public static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

    /// <summary>一次最多登記幾個路徑（listFiles 一次最多 3000 筆，參照只給其中的媒體）</summary>
    public const int MaxPathsPerCall = 3000;

    /// <summary><c>@list</c> 或 <c>@file</c>（<see cref="MediaServerRules.ListItemId"/>、<see cref="MediaServerRules.FileItemId"/>）</summary>
    public static bool IsKind(string? kind) => kind is MediaServerRules.ListItemId or MediaServerRules.FileItemId;

    /// <summary>抽屜的列還能重用：建立時間不在未來（本機時鐘倒退過的不信）、離現在不到 <see cref="ListReuse"/></summary>
    public static bool IsReusable(long createdAt, long nowMs) => createdAt <= nowMs && nowMs - createdAt < (long)ListReuse.TotalMilliseconds;

    /// <summary>重用的界線：抽屜建立不早於這個時間的列才可能重用（<see cref="IsReusable"/> 另外擋未來的）</summary>
    public static long ListReuseSince(long nowMs) => nowMs - (long)ListReuse.TotalMilliseconds + 1;

    /// <summary>修剪的界線：抽屜早於這個時間的列刪掉</summary>
    public static long ListPruneBefore(long nowMs) => nowMs - (long)ListLifetime.TotalMilliseconds;

    /// <summary>修剪的界線：fileFetch 早於這個時間的列刪掉</summary>
    public static long FilePruneBefore(long nowMs) => nowMs - (long)FileLifetime.TotalMilliseconds;

    /// <summary>
    /// 每個路徑（和 <paramref name="fullPaths"/> 同順序）要給的參照：
    /// <list type="bullet">
    /// <item><c>@list</c>：<paramref name="existing"/> 裡同一個對話、同一個完整路徑（不分大小寫）、還能重用（<see cref="IsReusable"/>）的列取最新的那一個；
    /// 沒有就以 <paramref name="newRef"/> 配新的。同一次呼叫裡重複的路徑給同一個參照。</item>
    /// <item><c>@file</c>：每次都配新的（15 分鐘的期限從這次算起；同一次呼叫裡重複的路徑一樣給同一個）。</item>
    /// </list>
    /// 新配的列放進 <paramref name="added"/>（建立時間是 <paramref name="nowMs"/>，呼叫端寫進 <c>media_refs_local</c>）。
    /// <paramref name="newRef"/> 回的值和 <paramref name="existing"/> 或這次已配的重複時重抽（一直撞丟 <see cref="InvalidOperationException"/>）。
    /// 種類不對、對話 id 不是線上格式、路徑是空的丟 <see cref="ArgumentException"/>
    /// </summary>
    public static IReadOnlyList<string> Assign(string kind, string conversationId, IReadOnlyList<string> fullPaths, IReadOnlyList<LocalMediaRefRow> existing,
        long nowMs, Func<string> newRef, out List<LocalMediaRefRow> added)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(newRef);
        if (!IsKind(kind))
            throw new ArgumentException("種類只能是 @list 或 @file", nameof(kind));
        if (!ConversationIds.IsWireId(conversationId))
            throw new ArgumentException("對話 id 要是線上格式", nameof(conversationId));
        if (fullPaths.Count > MaxPathsPerCall)
            throw new ArgumentException($"一次最多 {MaxPathsPerCall} 個路徑", nameof(fullPaths));
        if (fullPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("路徑不能是空的", nameof(fullPaths));

        var used = new HashSet<string>(existing.Select(r => r.Ref), StringComparer.Ordinal);
        var reusable = new Dictionary<string, LocalMediaRefRow>(StringComparer.OrdinalIgnoreCase);
        if (kind == MediaServerRules.ListItemId)
        {
            foreach (var row in existing)
            {
                if (row.ItemId != MediaServerRules.ListItemId || !string.Equals(row.ConversationId, conversationId, StringComparison.Ordinal)
                    || row.MediaPath is not { } path || !IsReusable(row.CreatedAt, nowMs))
                    continue;
                if (!reusable.TryGetValue(path, out var current) || row.CreatedAt > current.CreatedAt)
                    reusable[path] = row;
            }
        }

        added = [];
        var thisCall = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<string>(fullPaths.Count);
        foreach (var path in fullPaths)
        {
            if (thisCall.TryGetValue(path, out var same))
            {
                refs.Add(same);
                continue;
            }

            string value;
            if (reusable.TryGetValue(path, out var row))
            {
                value = row.Ref;
            }
            else
            {
                value = Fresh(newRef, used);
                added.Add(new LocalMediaRefRow(value, conversationId, kind, null, path, nowMs));
            }

            thisCall[path] = value;
            refs.Add(value);
        }

        return refs;
    }

    private static string Fresh(Func<string> newRef, HashSet<string> used)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var value = newRef();
            if (ConversationEventRules.IsMediaRef(value) && used.Add(value))
                return value;
        }

        throw new InvalidOperationException("配不出不重複的媒體參照（亂數來源有問題）");
    }
}
