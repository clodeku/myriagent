using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 桌面帳戶卡的 token 計數加上其他電腦的（tok2；docs/sync-protocol.md 14.10、web-protocol 的 Tokens〔同步〕）：中樞照舊只算這台，
/// 協調者收到伺服器的其他電腦合計（<see cref="TokensUpdate"/>）之後，送網頁之前以這裡就地加上去
/// </summary>
internal static class TokenViewRules
{
    /// <summary>
    /// <paramref name="tokens"/>（中樞的 Tokens：本機帳戶 → 這台的 today／week／month）就地改成所有電腦的合計：
    /// 連結到邏輯帳戶的本機帳戶（<paramref name="localToLogical"/>），三個數字都加上其他電腦這個邏輯帳戶的格（天數依 <paramref name="today"/>＝這台的本機日期），
    /// 另加 <c>here</c>＝這台今天的（原本的 today）；沒連結的照舊只有這台、沒有 here。
    /// <paramref name="remoteOnlyLogicalIds"/>（目錄有、這台沒有本機帳戶的邏輯帳戶）在其他電腦有格時以邏輯帳戶 id 加一筆（here 是 0）
    /// </summary>
    public static void Decorate(JObject tokens, IReadOnlyList<TokenTallyRow> others, IReadOnlyDictionary<string, string> localToLogical,
        IEnumerable<string> remoteOnlyLogicalIds, DateOnly today)
    {
        if (tokens["accounts"] is not JObject accounts)
            return;
        var byLogical = others
            .GroupBy(r => r.LogicalAccountId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var (localId, entry) in accounts.Properties().Select(p => (p.Name, p.Value as JObject)).ToList())
        {
            if (entry is null || !localToLogical.TryGetValue(localId, out var logical))
                continue;
            var rows = byLogical.GetValueOrDefault(logical) ?? [];
            entry["here"] = entry["today"] is JObject todayHere ? todayHere.DeepClone() : Count(default);
            entry["today"] = Add(entry["today"], TokenTallySyncRules.Sum(rows, logical, today, 1));
            entry["week"] = Add(entry["week"], TokenTallySyncRules.Sum(rows, logical, today, 7));
            entry["month"] = Add(entry["month"], TokenTallySyncRules.Sum(rows, logical, today, TokenTallySyncRules.RetentionDays));
        }

        foreach (var logical in remoteOnlyLogicalIds)
        {
            if (accounts.ContainsKey(logical) || !byLogical.TryGetValue(logical, out var rows))
                continue;
            accounts[logical] = new JObject
            {
                ["today"] = Count(TokenTallySyncRules.Sum(rows, logical, today, 1)),
                ["week"] = Count(TokenTallySyncRules.Sum(rows, logical, today, 7)),
                ["month"] = Count(TokenTallySyncRules.Sum(rows, logical, today, TokenTallySyncRules.RetentionDays)),
                ["here"] = Count(default),
            };
        }
    }

    /// <summary>一份 TokenCount 加上其他電腦的；原本不是物件、數字不是整數的當 0</summary>
    private static JObject Add(JToken? local, (long Input, long Cached, long Output) other)
    {
        var c = local as JObject;
        return Count((Value(c, "input") + other.Input, Value(c, "cached") + other.Cached, Value(c, "output") + other.Output));
    }

    private static long Value(JObject? count, string key)
        => count?[key] is JValue { Type: JTokenType.Integer } v ? Math.Max(0, v.Value<long>()) : 0;

    private static JObject Count((long Input, long Cached, long Output) c)
        => new() { ["input"] = c.Input, ["cached"] = c.Cached, ["output"] = c.Output };
}
