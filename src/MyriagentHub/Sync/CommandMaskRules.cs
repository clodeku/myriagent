using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 遠端指令的遮蔽（WP4.5 計畫 D29；契約 D22）：sync.db 的 <c>cmd_json</c> 與 hub.db 的 <c>command.issued</c> 稽核都只存遮蔽後的指令。
/// 送進代理的文字（上限 16,000 字的欄位：<c>send.text</c>、附言、新對話的第一則）只留長度 <c>{len}</c>，
/// <c>answers</c>、<c>uploads</c> 只留數量 <c>{count}</c>，其他字串經 <see cref="CredentialLeakRules.Mask"/>。
/// 不記任何文字的雜湊（短的提示用字典就能反推）。純函式，回傳複本
/// </summary>
internal static class CommandMaskRules
{
    /// <summary>稽核 detail 裡遮蔽後指令的字數上限（整個 detail 要小於 <c>AuditLog</c> 的 2000 字截斷）</summary>
    public const int MaxAuditMaskedChars = 1000;

    private const string CmdKey = "cmd";

    /// <summary>遮蔽一個（投影後的）遠端指令；型錄沒有的鍵照一般字串遮蔽</summary>
    public static JObject Mask(JObject cmd, RemoteCommandSpec spec)
    {
        var fields = spec.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var masked = new JObject();
        foreach (var property in cmd.Properties())
        {
            if (property.Name == CmdKey)
            {
                masked.Add(property.Name, property.Value.DeepClone());
                continue;
            }

            masked.Add(property.Name, MaskValue(fields.GetValueOrDefault(property.Name), property.Value));
        }

        return masked;
    }

    /// <summary>稽核用的字串：不縮排的 JSON；超過 <see cref="MaxAuditMaskedChars"/> 字改成 <c>{"truncated":true,"len":N}</c>（N 是原本的字數）</summary>
    public static string AuditMasked(JObject masked)
    {
        var json = masked.ToString(Formatting.None);
        return json.Length <= MaxAuditMaskedChars
            ? json
            : new JObject { ["truncated"] = true, ["len"] = json.Length }.ToString(Formatting.None);
    }

    private static JToken MaskValue(RemoteField? field, JToken value)
    {
        if (value.Type == JTokenType.Null)
            return JValue.CreateNull();
        switch (field?.Kind)
        {
            case RemoteFieldKind.Text when field.MaxLength >= RemoteCommandShapeRules.MaxAgentTextLength && RemoteCommandShapeRules.IsString(value, out var text):
                return new JObject { ["len"] = text.Length };
            case RemoteFieldKind.Answers:
                return new JObject { ["count"] = value is JObject answers ? answers.Count : 0 };
            case RemoteFieldKind.Uploads:
                return new JObject { ["count"] = value is JArray uploads ? uploads.Count : 0 };
            default:
                return MaskStrings(value);
        }
    }

    /// <summary>字串值經 DLP 遮蔽（物件的鍵名不動），其他原樣複製</summary>
    private static JToken MaskStrings(JToken token) => token switch
    {
        JValue { Type: JTokenType.String, Value: string text } => new JValue(CredentialLeakRules.Mask(text)),
        JObject o => new JObject(o.Properties().Select(p => new JProperty(p.Name, MaskStrings(p.Value)))),
        JArray a => new JArray(a.Select(MaskStrings)),
        _ => token.DeepClone(),
    };
}
