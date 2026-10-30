using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>Codex 對話控制的輸入驗證與協定轉換；只接受介面提供的動作，不讓網頁指定任意 RPC。</summary>
internal static class CodexControlRules
{
    public static readonly string[] Actions = ["inspect", "goalSet", "goalPause", "goalResume", "goalClear", "skillSelect", "speed"];

    public static JObject GoalParams(string thread, string action, JObject input)
    {
        var p = new JObject { ["threadId"] = thread, ["origin"] = "user" };
        p["status"] = action == "goalPause" ? "paused" : "active";
        if (action == "goalSet")
        {
            var objective = input.Value<string>("objective")?.Trim();
            if (string.IsNullOrEmpty(objective) || objective.Length > 4000)
                throw new ArgumentException("目標請填 1～4,000 個字");
            p["objective"] = objective;
            if (input["tokenBudget"] is { Type: not JTokenType.Null } budget)
            {
                if (budget.Type != JTokenType.Integer || budget.Value<long>() <= 0)
                    throw new ArgumentException("token 預算必須是正整數；留空表示不設定上限");
                p["tokenBudget"] = budget.DeepClone();
            }
        }
        return p;
    }

    public static JArray Skills(JObject? response)
        => new((response?["data"] as JArray ?? []).OfType<JObject>()
            .SelectMany(e => (e["skills"] as JArray ?? []).OfType<JObject>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Value<string>("name")) && !string.IsNullOrWhiteSpace(s.Value<string>("path")))
            .GroupBy(s => s.Value<string>("path"), StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(s => s.Value<string>("name"), StringComparer.Ordinal)
            .Select(s => new JObject
            {
                ["name"] = s["name"], ["path"] = s["path"], ["description"] = s["description"],
                ["scope"] = s["scope"], ["enabled"] = s.Value<bool?>("enabled") ?? false,
            }));

    public static JArray SpeedTiers(JObject? response, string? model)
    {
        var rows = (response?["data"] as JArray ?? []).OfType<JObject>().ToList();
        var chosen = CodexAgentSession.NormalizeModel(model);
        var row = chosen == null ? rows.FirstOrDefault(m => m.Value<bool?>("isDefault") == true)
            : rows.FirstOrDefault(m => m.Value<string>("model") == chosen || m.Value<string>("id") == chosen);
        // 只使用 CLI 回報的 id；0.164.0 的 Fast 是 priority，不能照顯示名稱送 fast。
        return new JArray((row?["serviceTiers"] as JArray ?? []).OfType<JObject>()
            .Where(t => !string.IsNullOrWhiteSpace(t.Value<string>("id")))
            .Select(t => new JObject { ["id"] = t["id"], ["name"] = t["name"], ["description"] = t["description"] }));
    }

    public static bool AllowsTier(JArray tiers, string? tier)
        => tier == null || tier == "default" || tiers.OfType<JObject>().Any(t => t.Value<string>("id") == tier);

    public static AgentMcpServer McpServer(JObject row)
    {
        var runtime = row["runtimeStatus"] as JObject;
        var state = runtime?.Value<string>("status") ?? runtime?.Value<string>("type") ?? (row["runtimeStatus"] is JValue ? row.Value<string>("runtimeStatus") : null);
        var error = row.Value<string>("toolsError") ?? runtime?.Value<string>("error");
        var auth = row.Value<string>("authStatus");
        var status = state == "disabled" ? "disabled" : auth == "notLoggedIn" || state == "authenticationRequired" ? "needs-auth" : error != null ? "failed" : state switch
        {
            "failed" or "cancelled" => "failed", "notStarted" or "starting" or "connecting" => "pending",
            _ => "connected",
        };
        return new AgentMcpServer(row.Value<string>("name") ?? "MCP", status, error,
            row.Value<string>("httpOrigin") != null ? "http" : null,
            row.Value<string>("pluginId") != null ? "plugin" : "user", (row["tools"] as JObject)?.Count);
    }

    /// <summary>手動操作經一般送出流程管理執行狀態，但送到 CLI 的是專用 RPC。</summary>
    public static (string Method, JObject Params)? SpecialTurn(string text, string thread)
    {
        var value = text.Trim();
        if (value == "/compact")
            return ("thread/compact/start", new JObject { ["threadId"] = thread });
        if (value != "/review" && !value.StartsWith("/review ", StringComparison.Ordinal))
            return null;
        var argument = value.Length > 7 ? value[8..].Trim() : string.Empty;
        JObject target;
        if (argument.Length == 0)
            target = new JObject { ["type"] = "uncommittedChanges" };
        else if (argument.StartsWith("commit ", StringComparison.Ordinal))
        {
            var sha = argument[7..].Trim();
            if (sha.Length is < 7 or > 64 || !sha.All(Uri.IsHexDigit))
                throw new ArgumentException("請提供有效的 commit 雜湊（7～64 位十六進位字元）");
            target = new JObject { ["type"] = "commit", ["sha"] = sha };
        }
        else if (argument.StartsWith("base ", StringComparison.Ordinal))
        {
            var branch = argument[5..].Trim();
            if (branch.Length == 0 || branch.Length > 250 || branch.StartsWith('-') || branch.Any(char.IsControl))
                throw new ArgumentException("請提供有效的比較分支名稱");
            target = new JObject { ["type"] = "baseBranch", ["branch"] = branch };
        }
        else
            target = new JObject { ["type"] = "custom", ["instructions"] = argument };
        return ("review/start", new JObject { ["threadId"] = thread, ["delivery"] = "inline", ["target"] = target });
    }
}
