using System.Diagnostics.CodeAnalysis;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 伺服器端的第二道權杖 DLP（WP4.2 計畫 D8；設計 8.8）：走遍解析後 payload 的每個字串值與屬性名稱跑 <see cref="CredentialLeakRules.HasLeak"/>。
/// 對解析後的值掃，而不是對 JSON 原文：<c>\u</c> 跳脫寫法的權杖在原文裡比對不到。另外兩條（與 <see cref="ConversationRemaskRules"/> 遮得掉的範圍相同）：
/// <list type="bullet">
/// <item>每個「屬性名稱＋字串值」組成 <c>"名稱":"值"</c> 再掃一次（<see cref="PairText"/>）：<c>{"access_token":"…"}</c> 這種欄位名稱＋值類的樣式，
/// 名稱與值分開掃都比對不到。</item>
/// <item>整段是一個 JSON 值的字串（工具的輸入輸出），解碼後的每個字串與屬性名稱也掃一次（一層，同 <see cref="CredentialLeakRules.MaskJsonStrings"/>）：
/// 字串裡以 <c>\u</c> 跳脫寫成的權杖在字串本身比對不到。</item>
/// </list>
/// 遮蔽過的副本（<see cref="CredentialLeakRules.Replacement"/> 不含 ASCII）不會被誤判。純函式、執行緒安全；命中時由 <see cref="ConversationRemaskRules"/>
/// 再遮蔽（答 5）。Newtonsoft 接受的 <c>new 名稱(…)</c> 建構式不在走遍的範圍裡（名稱只做兜底的檢查）：預先檢查與 LiveRelay 以
/// <see cref="HasNonStandardToken"/> 先拒收（合併前審查 DLP-CTOR-1）。
/// </summary>
internal static class ConversationDlpRules
{
    /// <summary>字串裡的 JSON 的解析深度上限（同 CredentialLeakRules 的工具輸入輸出）</summary>
    private const int EmbeddedJsonMaxDepth = 64;

    /// <summary>任何一個字串值、屬性名稱、「屬性名稱＋字串值」，或字串裡的 JSON 解碼後的字串疑似權杖</summary>
    public static bool HasLeak(JToken root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return HasLeak(root, embedded: true);
    }

    /// <summary>
    /// JSON 解析失敗時（這時 Validate 已經不過）：解碼後的整段文字跑一次，再以讀取器逐一讀出能讀到的屬性名稱與字串值（重複的鍵也各讀一次、
    /// 讀到語法錯誤為止）照 <see cref="HasLeak(JToken)"/> 的規則檢查——跳脫寫法的權杖在原文裡比對不到。命中就不是「DLP 乾淨」，被拒事件的 payload 不存
    /// </summary>
    public static bool HasLeakText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (CredentialLeakRules.HasLeak(text))
            return true;
        try
        {
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = null };
            string? name = null;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonToken.PropertyName when reader.Value is string property:
                        if (CredentialLeakRules.HasLeak(property))
                            return true;
                        name = property;
                        continue;
                    case JsonToken.String when reader.Value is string value:
                        if (StringLeaks(value, embedded: true) || (name is not null && CredentialLeakRules.HasLeak(PairText(name, value))))
                            return true;
                        break;
                    // 建構式的名稱（原文的檢查已經看得到它：名稱只有英數字、沒有跳脫寫法；這裡是兜底）
                    case JsonToken.StartConstructor when reader.Value is string constructorName && CredentialLeakRules.HasLeak(constructorName):
                        return true;
                }

                name = null;
            }
        }
        catch (JsonException)
        {
            // 讀到語法錯誤為止：前面讀得到的都檢查過了
        }

        return false;
    }

    /// <summary>
    /// 解析結果裡有標準 JSON 沒有的節點（Newtonsoft 接受的 <c>new 名稱(…)</c> 建構式、<c>undefined</c>；JRaw 讀取時不會出現，一併列入）：
    /// 產生端（SyncJson）不會寫出這些，走遍檢查也不看建構式的名稱（AWS 存取金鑰這類只有英數字的權杖寫成名稱就漏掉），所以預先檢查與 LiveRelay 一律拒收
    /// （預先檢查回 invalid、payload 不存）
    /// </summary>
    internal static bool HasNonStandardToken(JToken root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var token in root is JContainer container ? container.DescendantsAndSelf() : [root])
        {
            if (token.Type is JTokenType.Constructor or JTokenType.Undefined or JTokenType.Raw)
                return true;
        }

        return false;
    }

    /// <summary>「屬性名稱＋字串值」組成的 JSON 片段（<c>"名稱":"值"</c>；不跳脫：比對的是解碼後的內容）</summary>
    internal static string PairText(string name, string value) => "\"" + name + "\":\"" + value + "\"";

    /// <summary>整段是一個 JSON 物件或陣列的字串（前後可以有空白，後面不能有別的內容）：解析出來（同 CredentialLeakRules 的工具輸入輸出）</summary>
    internal static bool TryParseEmbeddedJson(string text, [NotNullWhen(true)] out JToken? token)
    {
        token = null;
        var start = text.AsSpan().TrimStart();
        if (start.IsEmpty || start[0] is not ('{' or '['))
            return false;
        try
        {
            using var reader = new JsonTextReader(new StringReader(text))
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal,
                MaxDepth = EmbeddedJsonMaxDepth,
            };
            var loaded = JToken.ReadFrom(reader);
            if (reader.Read())
                return false;
            token = loaded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasLeak(JToken root, bool embedded)
    {
        foreach (var token in root is JContainer container ? container.DescendantsAndSelf() : [root])
        {
            switch (token)
            {
                case JValue { Type: JTokenType.String, Value: string text } when StringLeaks(text, embedded):
                    return true;
                // 建構式的名稱（預先檢查與 LiveRelay 已經以 HasNonStandardToken 拒收；這裡是兜底）
                case JConstructor { Name: { } constructorName } when CredentialLeakRules.HasLeak(constructorName):
                    return true;
                case JProperty property when CredentialLeakRules.HasLeak(property.Name)
                                             || (property.Value is JValue { Type: JTokenType.String, Value: string value }
                                                 && CredentialLeakRules.HasLeak(PairText(property.Name, value))):
                    return true;
            }
        }

        return false;
    }

    /// <summary>一個字串值：本身；<paramref name="embedded"/> 時整段是 JSON 的再看解碼後的內容（只一層）</summary>
    private static bool StringLeaks(string text, bool embedded) =>
        CredentialLeakRules.HasLeak(text) || (embedded && TryParseEmbeddedJson(text, out var inner) && HasLeak(inner, embedded: false));
}
