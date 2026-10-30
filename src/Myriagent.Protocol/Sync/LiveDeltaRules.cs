using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Myriagent.Sync;

/// <summary>一筆即時更新的長欄位（<see cref="LiveDeltaRules.Fields"/>）；null 或空字串＝線上沒有這一欄</summary>
internal readonly record struct LiveLongFields(string? Text, string? Detail, string? Output)
{
    public static LiveLongFields Empty { get; } = new(null, null, null);

    /// <summary>某一欄的值（沒有的回空字串）</summary>
    public string Get(string field) => field switch
    {
        LiveDeltaRules.TextField => Text ?? string.Empty,
        LiveDeltaRules.DetailField => Detail ?? string.Empty,
        LiveDeltaRules.OutputField => Output ?? string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不是長欄位"),
    };

    public LiveLongFields With(string field, string value) => field switch
    {
        LiveDeltaRules.TextField => this with { Text = value },
        LiveDeltaRules.DetailField => this with { Detail = value },
        LiveDeltaRules.OutputField => this with { Output = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不是長欄位"),
    };
}

/// <summary>
/// 即時更新改送增量（<c>live2</c>；sync-protocol 14.3、docs/plans/2026-10-23-live-delta-and-replica-live.md 第 3 節）的純函式規則。
/// 串流中的項目每則都送整筆時，流量隨長度平方成長；增量只送長欄位（<c>text</c>、<c>detail</c>、<c>output</c>）從共同前綴之後的新內容，
/// 加上其餘欄位（<c>head</c>）。位置一律是 UTF-16 碼元（C# 與 JS 的字串相同），切點不在代理對中間。
/// <para>增量一律在<b>遮蔽之後</b>的兩版之間算（呼叫端給的就是投影好的整則），所以增量帶的內容一定是那一刻整則版本的子字串：
/// 今天整則會送出去的才可能送，今天不送的（還沒傳完的權杖尾巴、遮掉的權杖）增量也不送。</para>
/// <para><see cref="Apply"/> 重組的整則以 <see cref="WireItem"/> 的欄位順序組回、以 <see cref="ConversationWire.Serialize"/> 序列化：
/// 與擁有者直接送整則（<see cref="LiveRelay.Item"/>）逐位元組相同，伺服器之後的驗證、DLP、渲染不必區分兩者。</para>
/// </summary>
internal static class LiveDeltaRules
{
    public const string TextField = "text";

    public const string DetailField = "detail";

    public const string OutputField = "output";

    /// <summary>可以送增量的長欄位（線上名稱）</summary>
    public static IReadOnlyList<string> Fields { get; } = [TextField, DetailField, OutputField];

    /// <summary>一筆整則（<see cref="ConversationWire.TryItem"/> 的結果）的長欄位</summary>
    public static LiveLongFields FieldsOf(JObject item)
        => new(item[TextField] as JValue is { Type: JTokenType.String } t ? (string?)t : null,
            item[DetailField] as JValue is { Type: JTokenType.String } d ? (string?)d : null,
            item[OutputField] as JValue is { Type: JTokenType.String } o ? (string?)o : null);

    /// <summary>
    /// 增量：<paramref name="current"/>（這一版的整則）對 <paramref name="basis"/>（這條連線上這一筆最後送出的那一版的長欄位）。
    /// 有變的長欄位各一個 op（新值＝基底的前 <c>at</c> 個碼元＋<c>s</c>），沒變的不列
    /// </summary>
    public static LiveDelta Encode(string conversationId, long epoch, long ord, long ver, long baseVer, LiveLongFields basis, JObject current)
    {
        var head = (JObject)current.DeepClone();
        foreach (var field in Fields)
            head.Remove(field);
        var now = FieldsOf(current);
        var ops = new List<LiveDeltaOp>();
        foreach (var field in Fields)
        {
            var before = basis.Get(field);
            var after = now.Get(field);
            if (string.Equals(before, after, StringComparison.Ordinal))
                continue;
            var at = CommonPrefix(before, after);
            ops.Add(new LiveDeltaOp(field, at, after[at..], after.Length));
        }

        return new LiveDelta(conversationId, epoch, ord, ver, baseVer, ConversationWire.Serialize(head), ops);
    }

    /// <summary>兩個字串共同前綴的長度（UTF-16 碼元）；切點落在代理對中間時退一格（不把一個字拆成兩半）</summary>
    public static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
            i++;
        if (i > 0 && i < b.Length && char.IsHighSurrogate(b[i - 1]))
            i--;
        return i;
    }

    /// <summary>
    /// 這則增量不如送整則（<paramref name="fullItemLength"/> 是整則 JSON 的長度）：<c>head</c> 加上所有新內容超過整則的一半
    /// （例如工具輸出超過上限之後變成滑動視窗、前綴每次都變）
    /// </summary>
    public static bool ShouldSendFull(LiveDelta delta, int fullItemLength)
        => (long)(delta.Head.Length + delta.Ops.Sum(o => (long)o.S.Length)) * 2 > fullItemLength;

    /// <summary>
    /// 重組：基底（<paramref name="basis"/>，版本 <paramref name="basisVer"/>）加上增量 → 整則的 JSON（<see cref="WireItem"/> 的欄位順序，
    /// 與直接送整則逐位元組相同）。不合格回 null：<c>baseVer</c> 不是基底的版本、<c>ver</c> ≤ <c>baseVer</c>、op 的欄位不認得或重複、
    /// <c>at</c> 超出基底或切在代理對中間、<c>n</c> 對不上、<c>head</c> 不是合法的 JSON 物件、含長欄位、<c>ord</c>／<c>ver</c> 和外層不同、有 <see cref="WireItem"/> 沒有的欄位
    /// </summary>
    /// <param name="fields">重組後的長欄位（下一則的基底）</param>
    /// <param name="detail">不合格的原因（記錄用，不含內容）</param>
    public static string? Apply(LiveLongFields basis, long basisVer, LiveDelta delta, out LiveLongFields fields, out string? detail)
    {
        fields = basis;
        detail = null;
        if (delta.BaseVer != basisVer)
            return Fail($"baseVer {delta.BaseVer} 不是基底的版本 {basisVer}", out detail);
        if (delta.Ver <= delta.BaseVer)
            return Fail("ver 沒有比 baseVer 新", out detail);
        if (delta.Ops is null || delta.Head is null)
            return Fail("沒有 head 或 ops", out detail);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in delta.Ops)
        {
            if (op is null || op.F is null || !Fields.Contains(op.F) || !seen.Add(op.F) || op.S is null)
                return Fail("op 的欄位不合格或重複", out detail);
            var before = basis.Get(op.F);
            if (op.At < 0 || op.At > before.Length)
                return Fail("op 的 at 超出基底", out detail);
            if (op.At > 0 && op.At < before.Length && char.IsHighSurrogate(before[op.At - 1]) && char.IsLowSurrogate(before[op.At]))
                return Fail("op 的 at 切在代理對中間", out detail);
            if ((long)op.At + op.S.Length != op.N)
                return Fail("op 的 n 對不上", out detail);
            fields = fields.With(op.F, string.Concat(before.AsSpan(0, op.At), op.S));
        }

        if (!ConversationEventRules.TryParseStrict(delta.Head, out var token) || token is not JObject head)
            return Fail("head 不是合法的 JSON 物件", out detail);
        if (Fields.Any(f => head.ContainsKey(f)))
            return Fail("head 含長欄位", out detail);
        if (head["ord"] is not JValue { Value: long ord } || ord != delta.Ord || head["ver"] is not JValue { Value: long ver } || ver != delta.Ver)
            return Fail("head 的 ord／ver 與外層不同", out detail);

        var order = CanonicalOrder();
        if (head.Properties().Any(p => !order.Contains(p.Name)))
            return Fail("head 有不認得的欄位", out detail);
        var item = new JObject();
        foreach (var name in order)
        {
            if (Fields.Contains(name))
            {
                var value = fields.Get(name);
                if (value.Length > 0)
                    item[name] = value;
            }
            else if (head[name] is { } value)
            {
                item[name] = value.DeepClone();
            }
        }

        return ConversationWire.Serialize(item);
    }

    private static string? Fail(string reason, out string? detail)
    {
        detail = reason;
        return null;
    }

    private static string[]? canonicalOrder;

    /// <summary><see cref="WireItem"/> 在線上的欄位順序（同 <see cref="ConversationWire.TryItem"/> 以 <see cref="SyncJson.Settings"/> 投影的順序）</summary>
    private static string[] CanonicalOrder()
    {
        if (Volatile.Read(ref canonicalOrder) is { } cached)
            return cached;
        var contract = (JsonObjectContract)JsonSerializer.Create(SyncJson.Settings).ContractResolver.ResolveContract(typeof(WireItem));
        var order = contract.Properties.Where(p => !p.Ignored).OrderBy(p => p.Order ?? 0).Select(p => p.PropertyName!).ToArray();
        Volatile.Write(ref canonicalOrder, order);
        return order;
    }
}
