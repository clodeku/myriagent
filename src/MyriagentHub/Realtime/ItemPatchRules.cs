using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary><c>itemPatch</c> 的一項：新值＝基底的前 <paramref name="At"/> 個碼元＋<paramref name="S"/>，長度 <paramref name="N"/>（網頁套用後核對）</summary>
internal sealed record ItemPatchOp(string F, int At, string S, int N);

/// <summary>一則 <c>itemPatch</c> 的內容：<paramref name="SetJson"/> 是渲染好的 Item 去掉 <see cref="ItemPatchRules.Fields"/> 的其餘欄位（一律帶）</summary>
internal sealed record ItemPatch(string SetJson, IReadOnlyList<ItemPatchOp> Ops);

/// <summary>
/// 伺服器→瀏覽器的即時更新增量（<c>patch1</c>；docs/plans/2026-10-23-live-delta-and-replica-live.md 3.2、3.4、3.7）的純函式規則。
/// 在<b>渲染、淨化之後</b>的兩版 Item 之間算：長欄位（<c>html</c>、<c>detailHtml</c>、<c>detail</c>、<c>output</c>）各算共同前綴（退一格避開代理對），
/// 其餘欄位整份帶（<c>set</c>）。網頁拼出來的字串就是伺服器那一刻要送的整筆，<c>innerHTML</c> 的輸入與送整筆相同。
/// 長欄位「有／沒有」的組合和基底不同、增量不比整筆省一半，就不算（送整筆）
/// </summary>
internal static class ItemPatchRules
{
    public const string HtmlField = "html";

    public const string DetailHtmlField = "detailHtml";

    public const string DetailField = "detail";

    public const string OutputField = "output";

    public static IReadOnlyList<string> Fields { get; } = [HtmlField, DetailHtmlField, DetailField, OutputField];

    /// <summary>從 <paramref name="baseJson"/> 到 <paramref name="json"/> 的增量（兩者都是同一筆項目渲染好的 Item JSON）；不值得或算不出回 null</summary>
    public static ItemPatch? Compute(string baseJson, string json)
    {
        ArgumentNullException.ThrowIfNull(baseJson);
        ArgumentNullException.ThrowIfNull(json);
        if (Parse(baseJson) is not { } basis || Parse(json) is not { } current)
            return null;
        if (current["id"] is not JValue { Type: JTokenType.String } id || !JToken.DeepEquals(basis["id"], id))
            return null;

        var set = (JObject)current.DeepClone();
        var ops = new List<ItemPatchOp>();
        long payload = 0;
        foreach (var field in Fields)
        {
            set.Remove(field);
            var before = basis[field];
            var after = current[field];
            if ((before is null) != (after is null))
                return null;
            if (after is null)
                continue;
            if (before is not JValue { Type: JTokenType.String, Value: string b } || after is not JValue { Type: JTokenType.String, Value: string a })
                return null;
            if (string.Equals(a, b, StringComparison.Ordinal))
                continue;
            var at = LiveDeltaRules.CommonPrefix(b, a);
            ops.Add(new ItemPatchOp(field, at, a[at..], a.Length));
            payload += a.Length - at;
        }

        var setJson = set.ToString(Formatting.None);
        return (setJson.Length + payload) * 2 > json.Length ? null : new ItemPatch(setJson, ops);
    }

    /// <summary>網頁的做法（<c>liverules.js</c> 的 <c>applyItemPatch</c>）：以基底套用增量，回傳新的 Item（測試核對「重組＝整筆」用）；對不上回 null</summary>
    public static JObject? Apply(string baseJson, ItemPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Parse(baseJson) is not { } basis || Parse(patch.SetJson) is not { } next)
            return null;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in patch.Ops)
        {
            if (!Fields.Contains(op.F) || !touched.Add(op.F))
                return null;
            var old = basis[op.F] is JValue { Type: JTokenType.String, Value: string s } ? s : string.Empty;
            if (op.At < 0 || op.At > old.Length)
                return null;
            var value = old[..op.At] + op.S;
            if (value.Length != op.N)
                return null;
            next[op.F] = value;
        }
        foreach (var field in Fields)
        {
            if (!touched.Contains(field) && basis[field] is { } kept)
                next[field] = kept.DeepClone();
        }
        return next;
    }

    /// <summary>解析渲染好的 Item（日期字串不轉成 DateTime：<c>set</c> 要原樣送回網頁）</summary>
    private static JObject? Parse(string json)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal };
            return JToken.ReadFrom(reader) as JObject;
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }
}
