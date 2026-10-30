using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>表單欄位的種類（MCP elicitation 的 requested_schema 只有平的基本型別與字串列舉）</summary>
internal enum McpFieldKind
{
    Text,
    Number,
    Integer,
    Boolean,
    Choice,
    MultiChoice,
}

/// <summary>列舉的一個選項：畫面上的文字與送回去的值</summary>
internal sealed record McpChoice(string Label, string Value);

/// <summary>表單的一個欄位（Name 是 requested_schema 的屬性名稱，也是問卷題目的 id）</summary>
internal sealed record McpElicitationField(
    string Name,
    McpFieldKind Kind,
    string Title,
    string? Description,
    bool Required,
    IReadOnlyList<McpChoice> Choices,
    string? Default);

/// <summary>
/// MCP 伺服器經 Claude CLI 要使用者輸入（control_request elicitation）。Mode 是 form（填表單）或 url（開網址完成某件事）；
/// Unsupported 不是 null＝有畫不出來的欄位或網址不合格，整個婉拒
/// </summary>
internal sealed record McpElicitation(
    string Server,
    string Message,
    string Mode,
    string? Url,
    IReadOnlyList<McpElicitationField> Fields,
    string? Unsupported);

/// <summary>
/// MCP 的 elicitation 對應到既有的問卷卡（實測 2.1.310，tools/probes/claude_cli3_probe.py --mode mcp；docs/protocol-notes.md「MCP 的 elicitation」）：
/// 表單的每個欄位一題（id＝屬性名稱；布林與列舉是選項、文字與數字自行輸入，選填的多一個「不填」），網址模式一題「已完成／不要」、網址放在卡片的說明裡。
/// 回覆是 {action: accept|decline|cancel, content}：accept 的 content 依欄位型別轉成 JSON 值（數字、布林、字串陣列），CLI 原樣交給 MCP 伺服器
/// </summary>
internal static class McpElicitationRules
{
    public const string SkipLabel = "不填";
    public const string YesLabel = "是";
    public const string NoLabel = "否";
    public const string DoneLabel = "已完成";
    public const string RefuseLabel = "不要";
    public const string AgreeLabel = "同意";
    public const string DisagreeLabel = "拒絕";

    /// <summary>網址模式那一題的 id（不會和屬性名稱撞：屬性名稱不會以兩個底線開頭加中文）</summary>
    public const string UrlQuestionId = "__url";

    /// <summary>沒有欄位的表單（只要使用者同意）那一題的 id</summary>
    public const string ConfirmQuestionId = "__confirm";

    public static McpElicitation Parse(JObject? request)
    {
        var server = Text(request?["mcp_server_name"]) ?? "MCP";
        var message = Text(request?["message"]) ?? string.Empty;
        var mode = Text(request?["mode"]) ?? "form";
        if (mode == "url")
        {
            var raw = Text(request?["url"]);
            if (raw == null || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return new McpElicitation(server, message, mode, null, Array.Empty<McpElicitationField>(), "網址不是 http 或 https");
            return new McpElicitation(server, message, mode, uri.AbsoluteUri, Array.Empty<McpElicitationField>(), null);
        }
        if (mode != "form")
            return new McpElicitation(server, message, mode, null, Array.Empty<McpElicitationField>(), $"不認得的模式 {mode}");

        var schema = request?["requested_schema"] as JObject;
        var properties = schema?["properties"] as JObject;
        var required = (schema?["required"] as JArray)?.Select(Text).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        // CLI 另外給的 property_order（JSON 物件的順序可能在途中被打亂）優先，沒列到的照 schema 的順序接在後面
        var order = new List<string>();
        foreach (var name in (request?["property_order"] as JArray)?.Select(Text).OfType<string>() ?? Enumerable.Empty<string>())
        {
            if (properties?[name] != null && !order.Contains(name, StringComparer.Ordinal))
                order.Add(name);
        }
        foreach (var property in properties?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            if (!order.Contains(property.Name, StringComparer.Ordinal))
                order.Add(property.Name);
        }

        var fields = new List<McpElicitationField>();
        foreach (var name in order)
        {
            if (properties![name] is not JObject p)
                return new McpElicitation(server, message, mode, null, fields, $"欄位「{name}」的定義看不懂");
            var field = ParseField(name, p, required.Contains(name));
            if (field == null)
                return new McpElicitation(server, message, mode, null, fields, $"欄位「{Text(p["title"]) ?? name}」的型別（{TypeOf(p) ?? "沒有寫"}）不支援");
            fields.Add(field);
        }
        return new McpElicitation(server, message, mode, null, fields, null);
    }

    private static McpElicitationField? ParseField(string name, JObject p, bool required)
    {
        var title = Text(p["title"]) ?? name;
        var description = Text(p["description"]);
        var type = TypeOf(p);
        var defaultValue = p["default"] is JValue { Value: not null } d ? Convert.ToString(d.Value, CultureInfo.InvariantCulture) : null;
        if (Choices(p) is { Count: > 0 } choices)
            return new McpElicitationField(name, McpFieldKind.Choice, title, description, required, choices, defaultValue);
        switch (type)
        {
            case "boolean":
                return new McpElicitationField(name, McpFieldKind.Boolean, title, description, required, Array.Empty<McpChoice>(),
                    p["default"] is JValue { Type: JTokenType.Boolean } b ? ((bool)b ? YesLabel : NoLabel) : null);
            case "integer":
                return new McpElicitationField(name, McpFieldKind.Integer, title, description, required, Array.Empty<McpChoice>(), defaultValue);
            case "number":
                return new McpElicitationField(name, McpFieldKind.Number, title, description, required, Array.Empty<McpChoice>(), defaultValue);
            case "string":
                return new McpElicitationField(name, McpFieldKind.Text, title, description, required, Array.Empty<McpChoice>(), defaultValue);
            case "array" when p["items"] is JObject items && Choices(items) is { Count: > 0 } multi:
                return new McpElicitationField(name, McpFieldKind.MultiChoice, title, description, required, multi, null);
            default:
                return null;
        }
    }

    /// <summary>字串列舉：enum（＋舊的 enumNames 當顯示文字），或 oneOf／anyOf 的 {const, title}（MCP 2025-11-25 有標題的列舉）</summary>
    private static List<McpChoice>? Choices(JObject p)
    {
        if (p["enum"] is JArray values)
        {
            var names = p["enumNames"] as JArray;
            var list = new List<McpChoice>();
            for (int i = 0; i < values.Count; i++)
            {
                if (Text(values[i]) is not { } value)
                    return null;
                var label = names != null && names.Count == values.Count ? Text(names[i]) ?? value : value;
                list.Add(new McpChoice(label, value));
            }
            return list;
        }
        if ((p["oneOf"] ?? p["anyOf"]) is JArray options)
        {
            var list = new List<McpChoice>();
            foreach (var option in options)
            {
                if (option is not JObject o || Text(o["const"]) is not { } value)
                    return null;
                list.Add(new McpChoice(Text(o["title"]) ?? value, value));
            }
            return list;
        }
        return null;
    }

    private static string? TypeOf(JObject p)
        => p["type"] switch
        {
            JValue { Type: JTokenType.String } t => t.ToString(),
            JArray types => types.Select(Text).OfType<string>().FirstOrDefault(t => t != "null"),
            _ => null,
        };

    /// <summary>問卷卡的題目（Header 是欄位名稱，Text 是說明；選填、數字、預設值寫在說明後面）</summary>
    public static List<AgentQuestion> ToQuestions(McpElicitation e)
    {
        if (e.Mode == "url")
        {
            return new List<AgentQuestion>
            {
                new()
                {
                    Id = UrlQuestionId,
                    Header = "網址",
                    Text = "在瀏覽器開啟上面的網址、照網頁的指示完成後，按「已完成」",
                    AllowOther = false,
                    Options =
                    {
                        new AgentQuestionOption { Label = DoneLabel, Description = "已經在網頁上完成" },
                        new AgentQuestionOption { Label = RefuseLabel, Description = "不開這個網址（婉拒）" },
                    },
                },
            };
        }
        if (e.Fields.Count == 0)
        {
            return new List<AgentQuestion>
            {
                new()
                {
                    Id = ConfirmQuestionId,
                    Text = "要同意嗎？",
                    AllowOther = false,
                    Options = { new AgentQuestionOption { Label = AgreeLabel }, new AgentQuestionOption { Label = DisagreeLabel } },
                },
            };
        }

        var questions = new List<AgentQuestion>();
        foreach (var f in e.Fields)
        {
            var notes = new List<string>();
            if (f.Kind == McpFieldKind.Integer)
                notes.Add("整數");
            else if (f.Kind == McpFieldKind.Number)
                notes.Add("數字");
            if (!f.Required)
                notes.Add("選填");
            if (f.Default != null)
                notes.Add($"預設：{f.Default}");
            var text = f.Description ?? f.Title;
            var q = new AgentQuestion
            {
                Id = f.Name,
                Header = f.Title,
                Text = notes.Count > 0 ? $"{text}（{string.Join("，", notes)}）" : text,
                MultiSelect = f.Kind == McpFieldKind.MultiChoice,
            };
            switch (f.Kind)
            {
                case McpFieldKind.Boolean:
                    q.AllowOther = false;
                    q.Options.Add(new AgentQuestionOption { Label = YesLabel });
                    q.Options.Add(new AgentQuestionOption { Label = NoLabel });
                    break;
                case McpFieldKind.Choice:
                case McpFieldKind.MultiChoice:
                    q.AllowOther = false;
                    q.Options.AddRange(f.Choices.Select(c => new AgentQuestionOption { Label = c.Label }));
                    break;
                default:
                    q.AllowOther = true;
                    break;
            }
            if (!f.Required)
                q.Options.Add(new AgentQuestionOption { Label = SkipLabel, Description = "不填這一項" });
            questions.Add(q);
        }
        return questions;
    }

    /// <summary>卡片的說明（Markdown，Markdig 停用原始 HTML）：哪個 MCP 伺服器、它的訊息；網址模式另列網址（自動連結，點了用預設瀏覽器開）</summary>
    public static string Detail(McpElicitation e)
    {
        var lines = new List<string> { $"MCP 伺服器「{e.Server}」{(e.Mode == "url" ? "要你開啟網址" : "要你填寫")}：" };
        if (!string.IsNullOrWhiteSpace(e.Message))
            lines.Add(e.Message.Trim());
        if (e.Url != null)
            lines.Add($"<{e.Url}>");
        return string.Join("\n\n", lines);
    }

    /// <summary>婉拒的提示（不支援的欄位、網址不合格）</summary>
    public static string DescribeUnsupported(McpElicitation e)
        => $"MCP 伺服器「{e.Server}」要求{(e.Mode == "url" ? "開啟網址" : "填寫表單")}，但{e.Unsupported}，已婉拒。";

    /// <summary>
    /// 問卷的答案轉成給 CLI 的回覆：accept 帶依型別轉好的 content；網址那一題選「不要」、沒有欄位的表單選「拒絕」是 decline。
    /// 答案轉不成欄位的型別（數字填了文字、必填的選了「不填」）時回 decline 與給使用者看的原因（表單沒有送出）
    /// </summary>
    public static (JObject Response, string? Error) BuildResponse(McpElicitation e, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        if (e.Mode == "url")
            return (First(answers, UrlQuestionId) == DoneLabel ? Accept(null) : Decline(), null);
        if (e.Fields.Count == 0)
            return (First(answers, ConfirmQuestionId) == AgreeLabel ? Accept(new JObject()) : Decline(), null);

        var content = new JObject();
        foreach (var f in e.Fields)
        {
            var values = (answers.TryGetValue(f.Name, out var list) ? list : Array.Empty<string>())
                .Select(v => v.Trim()).Where(v => v.Length > 0 && v != SkipLabel).ToList();
            if (values.Count == 0)
            {
                if (f.Required)
                    return (Decline(), Failed(e, $"「{f.Title}」必填"));
                continue;
            }
            switch (f.Kind)
            {
                case McpFieldKind.Boolean:
                    if (values[0] == YesLabel)
                        content[f.Name] = true;
                    else if (values[0] == NoLabel)
                        content[f.Name] = false;
                    else
                        return (Decline(), Failed(e, $"「{f.Title}」要選是或否"));
                    break;
                case McpFieldKind.Integer:
                    if (!long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                        return (Decline(), Failed(e, $"「{f.Title}」要填整數"));
                    content[f.Name] = integer;
                    break;
                case McpFieldKind.Number:
                    if (!double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                        return (Decline(), Failed(e, $"「{f.Title}」要填數字"));
                    content[f.Name] = number;
                    break;
                case McpFieldKind.Choice:
                    if (ChoiceValue(f, values[0]) is not { } choice)
                        return (Decline(), Failed(e, $"「{f.Title}」要從選項裡選"));
                    content[f.Name] = choice;
                    break;
                case McpFieldKind.MultiChoice:
                {
                    var picked = new JArray();
                    foreach (var v in values)
                    {
                        if (ChoiceValue(f, v) is not { } item)
                            return (Decline(), Failed(e, $"「{f.Title}」要從選項裡選"));
                        picked.Add(item);
                    }
                    content[f.Name] = picked;
                    break;
                }
                default:
                    content[f.Name] = string.Join(", ", values);
                    break;
            }
        }
        return (Accept(content), null);
    }

    private static string? ChoiceValue(McpElicitationField f, string answer)
        => f.Choices.FirstOrDefault(c => c.Label == answer)?.Value ?? f.Choices.FirstOrDefault(c => c.Value == answer)?.Value;

    private static string Failed(McpElicitation e, string reason) => $"MCP 伺服器「{e.Server}」的表單沒有送出：{reason}，已婉拒。";

    private static string? First(IReadOnlyDictionary<string, IReadOnlyList<string>> answers, string id)
        => answers.TryGetValue(id, out var list) ? list.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0) : null;

    public static JObject Accept(JObject? content)
    {
        var response = new JObject { ["action"] = "accept" };
        if (content != null)
            response["content"] = content;
        return response;
    }

    /// <summary>使用者不答應（略過卡片、選「不要」）</summary>
    public static JObject Decline() => new() { ["action"] = "decline" };

    /// <summary>這一輪被停止：沒有回答</summary>
    public static JObject Cancel() => new() { ["action"] = "cancel" };

    private static string? Text(JToken? token) => token is JValue { Type: JTokenType.String } v && v.ToString() is { Length: > 0 } s ? s : null;
}
