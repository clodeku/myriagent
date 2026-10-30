using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 伺服器的再遮蔽（WP4.2 計畫 D44；擁有者 2026-10-19 的答 5：第二道 DLP 命中時伺服器遮蔽後**接受**，未遮蔽的權杖不寫進資料庫與記錄）。
/// 只用 <see cref="CredentialLeakRules"/> 的公開成員（那個檔歸 WP4.3）；它的 <c>MaskText</c>、<c>MaskDiff</c> 是 private，所以這裡照同樣的作法寫：
/// <list type="number">
/// <item><b>每個字串</b>（值與屬性名稱）先套伺服器自己的私鑰區塊規則 <see cref="MaskPrivateKeyBlocks"/>。</item>
/// <item><c>conv.item</c>：頂層的 <c>text</c>、<c>detail</c>、<c>output</c> 依序經 <see cref="CredentialLeakRules.MaskTruncated"/>、執行中（status 是 Running）
/// 只留 <see cref="CredentialLeakRules.SafeLength"/> 之前的部分、<see cref="CredentialLeakRules.MaskJsonStrings"/>（同 MaskItem 的 MaskText）；
/// <c>diff</c> 跨行處理私鑰區塊（<see cref="MaskDiffLines"/>）；其他字串經一般的遮蔽。</item>
/// <item>meta 類（<c>conv.created</c>／<c>conv.meta</c>）：<c>title</c>、<c>model</c>、<c>effort</c>、<c>workingDirectory</c> 結尾是「…」的經
/// <see cref="CredentialLeakRules.MaskCut"/>（先去掉「…」前面的權杖字元），其他經一般的遮蔽；其他字串經一般的遮蔽。</item>
/// <item><c>conv.queue</c>：<c>entries[].displayText</c> 同 meta；其他字串經一般的遮蔽。其他種類：每個字串經一般的遮蔽。</item>
/// <item>最後：「屬性名稱＋字串值」組起來仍疑似權杖的（<see cref="ConversationDlpRules.PairText"/>，例如新版多送、名稱是 <c>access_token</c> 的欄位），
/// 整個值換成 Replacement。</item>
/// </list>
/// 「一般的遮蔽」是 <see cref="CredentialLeakRules.MaskJsonStrings"/>：整段是 JSON 時先遮蔽解碼後的字串與屬性名稱，否則同 <see cref="CredentialLeakRules.Mask"/>
/// （第二道看得到的〔<see cref="ConversationDlpRules.HasLeak"/>〕這裡都遮得掉）。
/// 屬性名稱遮蔽後撞名時依序加 <c>#2</c>、<c>#3</c>…（不丟值，也不讓 Validate 因重複的鍵失敗）。<c>ord</c>、<c>ver</c> 不變（數字不遮蔽）。
/// 結果不保證合格：呼叫端（<see cref="ConversationPrecheckRules"/>）再跑 Validate 與 <see cref="ConversationDlpRules.HasLeak"/>，仍命中或不合格就 invalid。
/// 純函式、執行緒安全，不改呼叫端傳入的物件。即時更新的 item 也用 <c>Remask(ConversationEventKinds.Item, …)</c>（步 11）。
/// </summary>
internal static class ConversationRemaskRules
{
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>私鑰區塊的開頭與結尾標記（與 CredentialLeakRules 的樣式相同）</summary>
    private static readonly Regex PrivateKeyBegin = new(@"-----BEGIN[ A-Z0-9]{0,64}PRIVATE KEY(?: BLOCK)?-----", Options);

    private static readonly Regex PrivateKeyEnd = new(@"-----END[ A-Z0-9]{0,64}PRIVATE KEY(?: BLOCK)?-----", Options);

    /// <summary>conv.item 的長文字欄位（開頭截斷、執行中的尾端、JSON 的工具輸入輸出）</summary>
    private static readonly HashSet<string> ItemLongTextFields = new(StringComparer.Ordinal) { "text", "detail", "output" };

    /// <summary>meta 類可能是截斷過（結尾「…」）的短欄位</summary>
    private static readonly HashSet<string> MetaCutFields = new(StringComparer.Ordinal) { "title", "model", "effort", "workingDirectory" };

    /// <summary>
    /// 只在 <see cref="ConversationDlpRules.HasLeak"/> 為真時呼叫；回傳遮蔽後以線上格式（<see cref="ConversationWire.Serialize"/>，不縮排、照原本的鍵順序）
    /// 重新序列化的 JSON。不保證合格：呼叫端再跑 Validate 與走遍檢查
    /// </summary>
    public static string Remask(string kind, JObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var copy = (JObject)payload.DeepClone();
        switch (kind)
        {
            case ConversationEventKinds.Item:
                RemaskItem(copy);
                break;
            case ConversationEventKinds.Created or ConversationEventKinds.Meta:
                RemaskTopLevel(copy, (name, value) => MetaCutFields.Contains(name) ? MaskCutString(value) : null);
                break;
            case ConversationEventKinds.Queue:
                RemaskQueue(copy);
                break;
            default:
                MaskTree(copy);
                break;
        }

        MaskPairs(copy);
        return ConversationWire.Serialize(copy);
    }

    /// <summary>
    /// 伺服器自己的私鑰區塊規則：同一個字串裡從開頭標記到結尾標記（含；沒有結尾就到最後）整段換成 <see cref="CredentialLeakRules.Replacement"/>。
    /// 一個字串裡有幾塊就換幾塊。<c>MaskDiff</c> 的超集合（差異逐行存放時另由 <see cref="MaskDiffLines"/> 跨行處理）
    /// </summary>
    public static string MaskPrivateKeyBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        StringBuilder? output = null;
        var copied = 0;
        var position = 0;
        while (position < text.Length)
        {
            var begin = PrivateKeyBegin.Match(text, position);
            if (!begin.Success)
                break;
            var end = PrivateKeyEnd.Match(text, begin.Index + begin.Length);
            var stop = end.Success ? end.Index + end.Length : text.Length;
            output ??= new StringBuilder(text.Length);
            output.Append(text, copied, begin.Index - copied).Append(CredentialLeakRules.Replacement);
            copied = stop;
            position = stop;
        }

        return output is null ? text : output.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// 差異逐行遮蔽（照 CredentialLeakRules 的 MaskDiff）：私鑰一行一行存，單行的樣式只認得開頭那一行。開頭標記那一行從標記換到行尾，
    /// 之後到結尾標記為止（含）的每一行 <c>s</c> 整行換成 <see cref="CredentialLeakRules.Replacement"/>，沒有結尾就換到最後；同一行開了又關的不算。
    /// 其他欄位（<c>t</c>、新版多送的欄位）與屬性名稱照一般字串遮蔽。回傳新的陣列，不改 <paramref name="diff"/>
    /// </summary>
    public static JArray MaskDiffLines(JArray diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var result = new JArray();
        var inKey = false;
        foreach (var line in diff)
        {
            var copy = line.DeepClone();
            if (copy is JObject o && o["s"] is JValue { Type: JTokenType.String, Value: string s } value)
            {
                if (inKey)
                {
                    value.Value = CredentialLeakRules.Replacement;
                    if (PrivateKeyEnd.IsMatch(s))
                        inKey = false;
                }
                else
                {
                    value.Value = MaskString(s);
                    inKey = OpensPrivateKey(s);
                }
            }

            // s 已經遮蔽過（遮蔽是冪等的），其餘的字串與屬性名稱照一般規則
            MaskTree(copy);
            result.Add(copy);
        }

        return result;
    }

    private static void RemaskItem(JObject item)
    {
        var running = item["status"] is JValue { Type: JTokenType.String, Value: "Running" };
        foreach (var property in item.Properties().ToList())
        {
            if (property.Name == "diff" && property.Value is JArray diff)
            {
                property.Value = MaskDiffLines(diff);
                continue;
            }

            if (ItemLongTextFields.Contains(property.Name) && property.Value is JValue { Type: JTokenType.String, Value: string text } value)
            {
                value.Value = MaskLongText(text, running);
                continue;
            }

            MaskTree(property.Value);
        }

        MaskNames(item);
    }

    /// <summary>項目的長文字欄位：私鑰區塊、開頭截斷的第一行、執行中的尾端，再照 MaskJsonStrings（同 CredentialLeakRules 的 MaskText）</summary>
    private static string MaskLongText(string text, bool running)
    {
        var head = CredentialLeakRules.MaskTruncated(MaskPrivateKeyBlocks(text));
        if (running)
            head = head[..CredentialLeakRules.SafeLength(head)];
        return CredentialLeakRules.MaskJsonStrings(head);
    }

    private static void RemaskQueue(JObject queue)
    {
        if (queue["entries"] is JArray entries)
        {
            foreach (var entry in entries.OfType<JObject>())
            {
                if (entry["displayText"] is JValue { Type: JTokenType.String, Value: string text } value)
                    value.Value = MaskCutString(text);
            }
        }

        // displayText 已經遮蔽過（冪等），其餘照一般規則
        MaskTree(queue);
    }

    /// <summary>頂層欄位：<paramref name="special"/> 回非 null 的字串欄位用它的結果，其他照一般規則；最後遮蔽頂層的屬性名稱</summary>
    private static void RemaskTopLevel(JObject root, Func<string, string, string?> special)
    {
        foreach (var property in root.Properties().ToList())
        {
            if (property.Value is JValue { Type: JTokenType.String, Value: string text } value && special(property.Name, text) is { } masked)
            {
                value.Value = masked;
                continue;
            }

            MaskTree(property.Value);
        }

        MaskNames(root);
    }

    /// <summary>就地遮蔽每個字串值與屬性名稱（一般規則：私鑰區塊，再 Mask）</summary>
    private static void MaskTree(JToken token)
    {
        switch (token)
        {
            case JValue { Type: JTokenType.String, Value: string text } value:
                value.Value = MaskString(text);
                break;
            case JArray array:
                foreach (var child in array)
                    MaskTree(child);
                break;
            case JObject obj:
                foreach (var property in obj.Properties().ToList())
                    MaskTree(property.Value);
                MaskNames(obj);
                break;
        }
    }

    /// <summary>屬性名稱：遮蔽後改了就換名；和既有的撞名時依序加 #2、#3…</summary>
    private static void MaskNames(JObject obj)
    {
        foreach (var property in obj.Properties().ToList())
        {
            var name = MaskString(property.Name);
            if (name == property.Name)
                continue;
            var unique = name;
            for (var n = 2; obj.ContainsKey(unique); n++)
                unique = name + "#" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            property.Replace(new JProperty(unique, property.Value));
        }
    }

    /// <summary>
    /// 最後一步：「屬性名稱＋字串值」組起來仍疑似權杖的（<c>{"access_token":"…"}</c> 這種欄位名稱＋值類的樣式，值本身看不出來），
    /// 整個值換成 <see cref="CredentialLeakRules.Replacement"/>（同 <see cref="ConversationDlpRules.HasLeak"/> 的組法；屬性名稱這時已經遮蔽過）
    /// </summary>
    private static void MaskPairs(JObject root)
    {
        foreach (var property in root.DescendantsAndSelf().OfType<JProperty>())
        {
            if (property.Value is JValue { Type: JTokenType.String, Value: string text } value
                && CredentialLeakRules.HasLeak(ConversationDlpRules.PairText(property.Name, text)))
                value.Value = CredentialLeakRules.Replacement;
        }
    }

    /// <summary>
    /// 一般的字串：私鑰區塊，再 <see cref="CredentialLeakRules.MaskJsonStrings"/>（整段是 JSON 時先遮蔽解碼後的字串，否則同 Mask；
    /// 與 <see cref="ConversationDlpRules.HasLeak"/> 看得到的範圍相同，第二道看得到的這裡都遮得掉）
    /// </summary>
    private static string MaskString(string text) => CredentialLeakRules.MaskJsonStrings(MaskPrivateKeyBlocks(text));

    /// <summary>可能是截斷過的短文字（meta 的標題、模型、強度、工作資料夾，佇列的顯示文字）：結尾是「…」的照 MaskCut，其他照一般的字串</summary>
    private static string MaskCutString(string text)
    {
        var blocks = MaskPrivateKeyBlocks(text);
        return blocks.EndsWith('…') ? CredentialLeakRules.MaskCut(blocks) : MaskString(blocks);
    }

    /// <summary>這一行有私鑰的開頭標記，而且最後一個開頭標記之後沒有結尾標記（區塊延續到下一行）</summary>
    private static bool OpensPrivateKey(string line)
    {
        Match? last = null;
        for (var m = PrivateKeyBegin.Match(line); m.Success; m = m.NextMatch())
            last = m;
        return last != null && !PrivateKeyEnd.Match(line, last.Index + last.Length).Success;
    }
}
