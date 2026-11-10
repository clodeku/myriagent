using System.Globalization;
using System.Net.Mail;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>Codex 的 MCP 表單轉成共用問卷；回覆前驗證原始 schema，錯誤時保留請求供使用者修改。</summary>
internal static class CodexElicitationRules
{
    public static bool IsForm(CodexServerRequest request)
        => request.Method == "mcpServer/elicitation/request" && CodexAppServerRules.ParseMcpToolApproval(request.Params) == null;

    public static McpElicitation Parse(JObject p)
    {
        var normalized = new JObject
        {
            ["mcp_server_name"] = p["serverName"]?.DeepClone(),
            ["message"] = p["message"]?.DeepClone(),
            ["mode"] = p.Value<string>("mode") is "openai/form" or "openaiForm" ? "form" : p["mode"]?.DeepClone(),
            ["url"] = p["url"]?.DeepClone(),
            ["requested_schema"] = p["requestedSchema"]?.DeepClone(),
        };
        var parsed = McpElicitationRules.Parse(normalized);
        if (parsed.Mode == "form" && (p["requestedSchema"] is not JObject schema || schema.Value<string>("type") != "object" || schema["properties"] is not JObject))
            return parsed with { Unsupported = "表單缺少物件欄位定義" };
        return parsed;
    }

    public static (JObject Response, string? Error) BuildResponse(JObject p, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var form = Parse(p);
        if (form.Unsupported != null) return (McpElicitationRules.Decline(), form.Unsupported);
        var (response, error) = McpElicitationRules.BuildResponse(form, answers);
        if (error != null) return (response, error.Replace("，已婉拒。", "，請修正後再送出。", StringComparison.Ordinal));
        if (response["content"] is not JObject content || p["requestedSchema"] is not JObject schema || schema["properties"] is not JObject properties)
            return (response, null);
        foreach (var field in form.Fields)
        {
            if (content[field.Name] is not { } value || properties[field.Name] is not JObject rule) continue;
            string? reason = null;
            if (value.Type == JTokenType.String)
            {
                var text = value.Value<string>()!;
                var length = text.EnumerateRunes().Count();
                if (rule.Value<int?>("minLength") is { } min && length < min) reason = $"至少要 {min} 個字";
                if (rule.Value<int?>("maxLength") is { } max && length > max) reason = $"最多 {max} 個字";
                var validFormat = rule.Value<string>("format") switch
                {
                    "email" => MailAddress.TryCreate(text, out var address) && address.Address == text,
                    "uri" => Uri.TryCreate(text, UriKind.Absolute, out _),
                    "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    _ => true,
                };
                if (!validFormat) reason = $"格式必須是 {rule.Value<string>("format")}";
            }
            if (value.Type is JTokenType.Integer or JTokenType.Float)
            {
                var number = value.Value<double>();
                if (rule.Value<double?>("minimum") is { } min && number < min) reason = $"不得小於 {min}";
                if (rule.Value<double?>("maximum") is { } max && number > max) reason = $"不得大於 {max}";
            }
            if (value is JArray array)
            {
                if (rule.Value<int?>("minItems") is { } min && array.Count < min) reason = $"至少選 {min} 項";
                if (rule.Value<int?>("maxItems") is { } max && array.Count > max) reason = $"最多選 {max} 項";
                if (array.Values<string>().Distinct(StringComparer.Ordinal).Count() != array.Count) reason = "不可重複選取";
            }
            if (reason != null) return (McpElicitationRules.Decline(), $"「{field.Title}」{reason}，請修正後再送出");
        }
        return (response, null);
    }
}
