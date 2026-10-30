namespace MyriagentHub.Sync;

/// <summary>排序用的一個邏輯帳戶（logical_accounts 的 id、sort_order、created_at）</summary>
internal sealed record AccountOrder(string LogicalAccountId, int SortOrder, long CreatedAt);

/// <summary>
/// 帳戶目錄的順序（瀏覽器的 <c>accountReorder</c>；web-protocol 第六節第 10 點）。
/// 顯示順序一律 sort_order → created_at → id（Ordinal），created_at 是伺服器寫入的時間，只用來讓同順序的帳戶有固定的先後。
/// </summary>
internal static class AccountOrderRules
{
    /// <summary>sort_order → created_at → id（Ordinal）</summary>
    public static IReadOnlyList<AccountOrder> Sort(IEnumerable<AccountOrder> accounts) =>
        accounts
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.CreatedAt)
            .ThenBy(a => a.LogicalAccountId, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 新的順序：請求裡列到而且存在的先排（照請求的順序；重複的只算第一次）、沒列到的照原本的順序接在後面、已不存在的 id 略過；
    /// 新順序是 0..n-1，只回順序有變的帳戶
    /// </summary>
    public static IReadOnlyList<(string Id, int SortOrder)> Plan(IReadOnlyList<AccountOrder> current, IReadOnlyList<string> requested)
    {
        var sorted = Sort(current);
        var byId = new Dictionary<string, AccountOrder>(StringComparer.Ordinal);
        foreach (var account in sorted)
            byId.TryAdd(account.LogicalAccountId, account);

        var placed = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<AccountOrder>(sorted.Count);
        foreach (var id in requested)
        {
            if (byId.TryGetValue(id, out var account) && placed.Add(id))
                order.Add(account);
        }
        foreach (var account in sorted)
        {
            if (placed.Add(account.LogicalAccountId))
                order.Add(account);
        }

        var changes = new List<(string Id, int SortOrder)>();
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].SortOrder != i)
                changes.Add((order[i].LogicalAccountId, i));
        }
        return changes;
    }
}
