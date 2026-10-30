using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// Claude 的 MCP 伺服器狀態與操作、旁支提問（control_request mcp_status／mcp_reconnect／mcp_toggle／mcp_authenticate、side_question；
/// 實測 2.1.310，tools/probes/claude_cli3_probe.py --mode mcp／btw，docs/protocol-notes.md「MCP 伺服器狀態」「旁支提問」）
/// </summary>
internal static class ClaudeMcpRules
{
    /// <summary>旁支提問的長度上限（CLI 沒有明說；太長的問題當成一般訊息比較合適）</summary>
    public const int MaxSideQuestionLength = 8000;

    public static string BuildStatusRequest(string requestId)
        => ClaudeStreamRules.BuildControlRequest(requestId, new JObject { ["subtype"] = "mcp_status" });

    /// <summary>
    /// 一個伺服器的操作：reconnect＝mcp_reconnect（失敗時回 error 與原因，例如 Connection closed）、
    /// authenticate＝mcp_authenticate（回 authUrl；CLI 在本機開回呼的埠等瀏覽器回來，完成後狀態變成 connected、權杖存進帳戶的 .credentials.json）
    /// </summary>
    public static string BuildActionRequest(string requestId, string server, AgentMcpAction action)
        => ClaudeStreamRules.BuildControlRequest(requestId, action switch
        {
            AgentMcpAction.Reconnect => new JObject { ["subtype"] = "mcp_reconnect", ["serverName"] = server },
            AgentMcpAction.Authenticate => new JObject { ["subtype"] = "mcp_authenticate", ["serverName"] = server },
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "不認得的 MCP 操作"),
        });

    /// <summary>
    /// mcp_status 的回應（{mcpServers: [{name, status, error, transport, scope, source, tools…}]}）。status 是 connected、failed、needs-auth、pending、disabled；
    /// 來源先看 source（外掛的 scope 是 dynamic、source 才是 plugin）；
    /// 沒有名字的略過，欄位不是預期的型別當成沒有。回應不對（舊版、失敗）回 null
    /// </summary>
    public static List<AgentMcpServer>? ParseStatus(JObject? response)
    {
        if (response?["mcpServers"] is not JArray servers)
            return null;
        var list = new List<AgentMcpServer>();
        foreach (var s in servers.OfType<JObject>())
        {
            if (Text(s["name"]) is not { } name)
                continue;
            list.Add(new AgentMcpServer(
                name,
                Text(s["status"]) ?? "pending",
                Text(s["error"]),
                Text(s["transport"]) ?? Text(s["config"]?["type"]),
                Text(s["source"]) ?? Text(s["scope"]),
                s["tools"] is JArray tools ? tools.Count : null));
        }
        return list;
    }

    /// <summary>還在連線中（pending）：過幾秒再問一次</summary>
    public static bool AnyPending(IReadOnlyList<AgentMcpServer> servers) => servers.Any(s => s.Status == "pending");

    /// <summary>mcp_authenticate 的授權網址（只收 http、https；requiresUserAction 是 false 時已經不用使用者做什麼）</summary>
    public static string? ParseAuthUrl(JObject? response)
    {
        if (Text(response?["authUrl"]) is not { } raw || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        return uri.AbsoluteUri;
    }

    /// <summary>
    /// 旁支提問（同互動介面的 /btw）：CLI 以目前對話的上下文問模型，不打斷這一輪、問答不寫進逐字稿也不進之後的上下文；
    /// 執行中、閒置、還沒開始任何一輪、--resume 之後都能問，可以同時問好幾個（實測約 1.5 秒）
    /// </summary>
    public static string BuildSideQuestionRequest(string requestId, string question)
        => ClaudeStreamRules.BuildControlRequest(requestId, new JObject { ["subtype"] = "side_question", ["question"] = question });

    /// <summary>side_question 的回應 {response: 文字或 null, synthetic}：null（CLI 沒有給答案）回 null</summary>
    public static string? ParseSideAnswer(JObject? response) => Text(response?["response"]);

    private static string? Text(JToken? token) => token is JValue { Type: JTokenType.String } v && v.ToString() is { Length: > 0 } s ? s : null;
}
