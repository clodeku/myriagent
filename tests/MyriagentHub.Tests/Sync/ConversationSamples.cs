using System.Globalization;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 對話事件測試的樣本。權杖一律在執行時組字串產生（契約 D44 的慣例：repo 裡不出現看起來像權杖的字面值）；
/// JSON 的 <c>\u</c> 跳脫也在執行時組（反斜線以 <see cref="Backslash"/> 產生）。
/// </summary>
internal static class ConversationSamples
{
    public const string Conv = "0123456789abcdef0123456789abcdef";

    public const string OtherConv = "fedcba9876543210fedcba9876543210";

    public static readonly string Backslash = ((char)92).ToString();

    /// <summary>GitHub 的個人權杖（有長度下限的樣式：前綴之後 ≥20 個英數字）</summary>
    public static string GitHub(char fill = 'a') => "gh" + "p_" + new string(fill, 36);

    /// <summary>GitHub 權杖的前綴加 <paramref name="length"/> 個字（不到 20 個時樣式比對不到）</summary>
    public static string GitHubFragment(int length) => "gh" + "p_" + new string('x', length);

    /// <summary>沒有長度下限的前綴（sk-proj-）：本身就算命中</summary>
    public static string ScopedOpenAiPrefix() => "sk-" + "proj-";

    /// <summary>AWS 的存取金鑰（AKIA 加 16 個大寫英數字；只有英數字，可以寫成 Newtonsoft 的建構式名稱）</summary>
    public static string AwsAccessKey() => "AK" + "IA" + "QWERTYUIOPASDFGH";

    /// <summary>
    /// 合格的 conv.item 原文（Assistant、id item1、ord／ver 1），<paramref name="text"/> 與 <paramref name="tail"/> 照原樣接進 JSON
    /// （放 SyncJson 不會寫出的寫法，例如 <c>new 名稱(1)</c> 建構式）
    /// </summary>
    public static string RawItem(string text, string tail = "", string status = "Done") =>
        "{\"id\":\"item1\",\"ord\":1,\"ver\":1,\"kind\":\"Assistant\",\"status\":\"" + status + "\",\"text\":" + text + tail + "}";

    public static string PemBegin() => "-----BEGIN " + "RSA PRIVATE" + " KEY-----";

    public static string PemEnd() => "-----END " + "RSA PRIVATE" + " KEY-----";

    public static string PemBody(char fill = 'A') => "MIIEow" + new string(fill, 30) + "abc123" + new string(fill, 22);

    /// <summary>字串的第一個字元改寫成 JSON 的 <c>\u</c> 跳脫（放進 JSON 原文用）：解析後才看得到原本的字</summary>
    public static string EscapeFirst(string text) =>
        Backslash + "u" + ((int)text[0]).ToString("x4", CultureInfo.InvariantCulture) + text[1..];

    /// <summary>開頭被截掉的工具輸出前綴（AgentHubRules 的「…（前 N 字省略）」＋換行）</summary>
    public static string OmittedPrefix() => "…（前 1,234 字省略）\n";

    /// <summary>合格的 conv.item（Assistant、Done）</summary>
    public static JObject Item(string? text = "你好", string status = "Done", string id = "item1", long ord = 1, long ver = 1)
    {
        var item = new JObject
        {
            ["id"] = id,
            ["ord"] = ord,
            ["ver"] = ver,
            ["kind"] = "Assistant",
            ["status"] = status,
            ["canAllowForSession"] = true,
            ["answerAsMessage"] = false,
        };
        if (text is not null)
            item["text"] = text;
        return item;
    }

    /// <summary>合格的 conv.created／conv.meta</summary>
    public static JObject Meta(string title = "標題", string policy = ConversationSyncPolicies.Full) => new()
    {
        ["provider"] = "Claude",
        ["localAccountId"] = "acc-1",
        ["title"] = title,
        ["model"] = "opus",
        ["customFolder"] = false,
        ["syncPolicy"] = policy,
        ["createdAt"] = 1_700_000_000_000L,
        ["updatedAt"] = 1_700_000_000_000L,
        ["running"] = false,
        ["waiting"] = false,
    };

    /// <summary>合格的 conv.queue</summary>
    public static JObject Queue(params string[] texts) => new()
    {
        ["entries"] = new JArray(texts.Select((t, i) => new JObject { ["id"] = "q" + i.ToString(CultureInfo.InvariantCulture), ["displayText"] = t })),
        ["more"] = 0,
    };

    public static string Json(JToken payload) => ConversationWire.Serialize(payload);

    /// <summary>一筆對話事件（payload 照 PayloadRules 編碼、雜湊是原文的）</summary>
    public static SyncEvent Event(string kind, string json, string conversationId = Conv, long? epoch = 1, string? stream = null, long deviceSeq = 1,
        string? sha = null)
    {
        var (enc, payload) = PayloadRules.Encode(json);
        return new SyncEvent(deviceSeq, stream ?? SyncStreams.Conversation(conversationId), kind, enc, sha ?? PayloadRules.Sha256Hex(json), payload,
            conversationId, epoch);
    }

    public static SyncEvent Event(string kind, JToken payload) => Event(kind, Json(payload));
}
