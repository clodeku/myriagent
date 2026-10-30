using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>帳戶提及與本機 MCP 的格式；不把帳戶名稱當成身分，也不把檔案提及當成委派。</summary>
internal static partial class DelegationMcpRules
{
    public const string ServerName = "myriagent_accounts";
    public const int MaxTextLength = 64000;
    public const int MaxWireLength = 512000;

    /// <summary>
    /// 主 AI 等一個委派工作的上限（含子對話等使用者核准的時間）。Codex 的 MCP 工具預設 60 秒逾時、Claude 也有自己的預設，
    /// 兩邊都明確設成同一個值；逾時＝CLI 取消工具呼叫，橋接斷線後子工作跟著停止。
    /// </summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromHours(4);

    /// <summary>使用者 @ 過帳戶就是授權委派：Claude 不再為這兩個工具另外跳核准卡（子對話本身以每步詢問執行）</summary>
    public static readonly IReadOnlyList<string> ClaudeAllowedTools = [$"mcp__{ServerName}__list_accounts", $"mcp__{ServerName}__delegate_task"];

    /// <summary>認得的 MCP 協定版本：client 要求的版本在清單裡就照用，否則回最舊的（client 不支援時自己斷線）</summary>
    private static readonly string[] ProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18"];

    [GeneratedRegex(@"@\[[^\]\r\n]{1,120}\]\(account:([A-Za-z0-9_-]{1,100})\)")]
    private static partial Regex Mention();

    [GeneratedRegex(@"\Amyriagent-delegation-[a-f0-9]{32}\z")]
    private static partial Regex Pipe();

    public static IReadOnlyList<string> MentionedAccounts(string text)
        => Mention().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    public static bool IsPipeName(string name) => Pipe().IsMatch(name);

    /// <summary>JSON-RPC 的字串欄位：物件不存在、欄位沒有、是 JSON null 或不是字串都回 null（不丟例外）</summary>
    public static string? StringField(JToken? token, string name)
        => token is JObject o && o[name] is JValue { Type: JTokenType.String } v ? (string?)v : null;

    public static string NegotiateProtocol(string? requested)
        => requested != null && ProtocolVersions.Contains(requested, StringComparer.Ordinal) ? requested : ProtocolVersions[0];

    /// <summary>附在送給主 AI 的訊息後面（畫面上的使用者訊息不含）：這則有 @ 就交代對象，沒有就只提醒可用、不主動委派</summary>
    public static string DelegationHint(IReadOnlyList<(string Id, string Label, string Provider)> mentioned)
    {
        var body = mentioned.Count > 0
            ? "使用者在這則訊息 @ 了下列 AI 帳戶：" + string.Join("、", mentioned.Select(a => $"「{a.Label}」（{a.Provider}，accountId：{a.Id}）"))
              + "。請用 myriagent_accounts MCP 的 delegate_task 把使用者指定給該帳戶的工作交給它，等待結果後整合並繼續處理。"
            : "這個對話先前 @ 過其他 AI 帳戶，可用 myriagent_accounts MCP 的 list_accounts 與 delegate_task 委派；只有使用者要求交給其他帳戶時才委派。";
        return "\n\n<myriagent_account_delegation>\n" + body
               + "委派的 prompt 要包含完整需求與必要前文（子對話看不到這個對話的紀錄與附件）；子對話共用目前工作資料夾、以每步詢問執行，核准與問卷由使用者在子對話回應。"
               + "訊息裡的 @[名稱](account:id) 是帳戶提及，不是檔案路徑。\n</myriagent_account_delegation>";
    }

    /// <summary>
    /// 子對話最後一輪（最後一則使用者訊息之後）的助理文字；超過上限保留結尾（最後的結論通常在後面）。
    /// 項目以（是使用者訊息、是助理文字、內容）傳入，依畫面順序。
    /// </summary>
    public static (string Text, bool Truncated) TurnAnswer(IEnumerable<(bool IsUser, bool IsAssistant, string Text)> items)
    {
        var turn = new List<string>();
        foreach (var (isUser, isAssistant, text) in items)
        {
            if (isUser)
                turn.Clear();
            else if (isAssistant && !string.IsNullOrWhiteSpace(text))
                turn.Add(text.Trim());
        }
        var answer = string.Join("\n\n", turn);
        return answer.Length > MaxTextLength ? (answer[^MaxTextLength..], true) : (answer, false);
    }

    /// <summary>橋接就是 app 自己的 exe（--delegation-mcp 模式）；Assembly.Location 是空的（單一檔案發佈）時用目前程序</summary>
    public static string BridgeExecutable()
    {
        var location = typeof(DelegationMcpRules).Assembly.Location;
        return string.IsNullOrEmpty(location) ? Environment.ProcessPath ?? "Myriagent.exe" : Path.ChangeExtension(location, ".exe");
    }

    public static JObject StdioConfig(string pipeName) => new()
    {
        ["command"] = BridgeExecutable(),
        ["args"] = new JArray("--delegation-mcp", pipeName),
    };

    public static JObject ClaudeConfig(string pipeName) => new()
    {
        ["mcpServers"] = new JObject { [ServerName] = StdioConfig(pipeName) },
    };

    public static void AddCodexArguments(List<string> args, string pipeName)
    {
        var config = StdioConfig(pipeName);
        var prefix = $"mcp_servers.{ServerName}";
        args.AddRange(["-c", $"{prefix}.command={JsonConvert.SerializeObject(config.Value<string>("command"))}",
            "-c", $"{prefix}.args={config["args"]!.ToString(Formatting.None)}",
            "-c", $"{prefix}.enabled=true", "-c", $"{prefix}.tool_timeout_sec={(int)ToolTimeout.TotalSeconds}"]);
    }

    public static JObject Result(JToken? id, JObject value) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = value,
    };

    public static JObject Error(JToken? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
        ["error"] = new JObject { ["code"] = code, ["message"] = message },
    };

    public static JObject ToolResult(JObject value, bool error = false) => new()
    {
        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = value.ToString(Formatting.None) }),
        ["isError"] = error,
    };

    public static JObject Tools() => new()
    {
        ["tools"] = new JArray(
            Tool("list_accounts", "列出使用者在這個對話以 @ 選取、可委派的本機 AI 帳戶。", new JObject(), []),
            Tool("delegate_task", "交辦工作給使用者 @ 的 AI 訂閱帳戶。建立可查看進度及回應核准的子對話，等待完成並回傳結果。請給完整、獨立的工作說明；子對話只取得你提供的文字，共用目前工作資料夾。", new JObject
            {
                ["accountId"] = new JObject { ["type"] = "string", ["description"] = "list_accounts 回傳的帳戶 id" },
                ["prompt"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxTextLength },
            }, ["accountId", "prompt"])),
    };

    private static JObject Tool(string name, string description, JObject properties, string[] required) => new()
    {
        ["name"] = name, ["description"] = description,
        ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(required), ["additionalProperties"] = false },
    };
}
