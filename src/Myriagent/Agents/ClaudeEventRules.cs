using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>子代理（Agent 工具）的即時用量：task_progress 每做一步送一次，task_notification 結束時帶最後的數字</summary>
/// <param name="ToolUseId">主對話那張 Agent 工具卡的 tool_use_id</param>
/// <param name="TotalTokens">子代理到目前為止的 token 合計</param>
/// <param name="ToolUses">子代理呼叫工具的次數</param>
/// <param name="DurationMs">已經跑了多久</param>
/// <param name="LastTool">最近一次呼叫的工具（結束時是 null）</param>
/// <param name="Done">子代理結束了（task_notification）</param>
internal sealed record SubagentUsage(string ToolUseId, long TotalTokens, int ToolUses, long DurationMs, string? LastTool, bool Done);

/// <summary>
/// Claude 一輪中的系統事件（2.1.310 實測，docs/protocol-notes.md「Claude：CLI 代查用量、控制請求登入、標題產生、健康度清單與一輪中的事件」，
/// 探針 tools/probes/claude_cli7_probe.py --mode events）：備援模型接手（model_fallback）、權限擋下（permission_denied）、
/// CLI 的通知（notification）、存進記憶（memory_saved）、子代理的進度（task_progress）。CLI 給的說明是英文，前面加上中文的一句
/// </summary>
internal static class ClaudeEventRules
{
    /// <summary>model_fallback {trigger, original_model, fallback_model, content}：主模型不能用、改用備援模型（設定頁的 claudeFallbackModel）</summary>
    public static string ModelFallback(JObject message)
    {
        var original = Str(message, "original_model");
        var fallback = Str(message, "fallback_model");
        var why = Str(message, "trigger") switch
        {
            "model_not_found" => "找不到這個模型",
            "overloaded" => "過載",
            "unavailable" => "暫時不能用",
            null => null,
            var other => other,
        };
        var head = fallback != null
            ? $"主模型{(original != null ? $" {original}" : string.Empty)}{(why != null ? $"（{why}）" : "不能用")}，Claude 改用備援模型 {fallback} 接手"
            : "主模型不能用，Claude 改用備援模型接手";
        return Append(head, Str(message, "content"));
    }

    /// <summary>
    /// permission_denied {tool_name, decision_reason_type, decision_reason, message}：權限規則、自動模式的判斷或 hook 擋下了一個工具
    /// （實測拒絕規則時 decision_reason_type 是 subcommandResults、只有 message）
    /// </summary>
    public static string PermissionDenied(JObject message)
    {
        var tool = Str(message, "tool_name") ?? "工具";
        var by = Str(message, "decision_reason_type") switch
        {
            "rule" or "subcommandResults" => "權限規則",
            "classifier" => "自動模式的判斷",
            "hook" => "hook 設定",
            "mode" or "permissionPromptTool" => "權限模式",
            "sandboxOverride" or "sandbox" => "沙箱",
            "safetyCheck" => "安全檢查",
            _ => null,
        };
        var head = by != null ? $"{by}擋下了「{tool}」" : $"Claude 擋下了「{tool}」";
        return Append(head, Str(message, "decision_reason") ?? Str(message, "message"));
    }

    /// <summary>notification {key, text, priority, color}：CLI 要使用者知道的事（Fast 點數用完、模型被政策拒絕…）；color 是 error 時當成錯誤</summary>
    public static (string Text, bool IsError)? Notification(JObject message)
    {
        var text = Str(message, "text");
        if (text == null)
            return null;
        return (Append("Claude 通知", text), Str(message, "color") == "error");
    }

    /// <summary>memory_saved {writtenPaths}：代理把東西存進記憶（只列檔名；實測 -p 模式沒有送，照樣接上）</summary>
    public static string? MemorySaved(JObject message)
    {
        var paths = (message["writtenPaths"] ?? message["written_paths"]) as JArray;
        var names = (paths ?? new JArray()).OfType<JValue>().Where(v => v.Type == JTokenType.String)
            .Select(v => Path.GetFileName(v.ToString().TrimEnd('\\', '/'))).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        return names.Count == 0 ? "Claude 已把這件事存進記憶" : "Claude 已存進記憶：" + string.Join("、", names);
    }

    /// <summary>
    /// task_progress（執行中）與 task_notification（結束）帶 usage 的子代理：主對話 Agent 工具卡的 tool_use_id 與用量；
    /// 沒有 tool_use_id 或 usage 的（背景指令、Monitor）回 null
    /// </summary>
    public static SubagentUsage? Subagent(JObject message, bool done)
    {
        var toolUseId = Str(message, "tool_use_id");
        if (toolUseId == null || message["usage"] is not JObject usage)
            return null;
        return new SubagentUsage(toolUseId, Long(usage, "total_tokens"), (int)Math.Min(int.MaxValue, Long(usage, "tool_uses")), Long(usage, "duration_ms"),
            done ? null : Str(message, "last_tool_name"), done);
    }

    /// <summary>子代理用量在工具卡上的晶片文字：「15.5K tokens・2 次工具」</summary>
    public static string UsageLabel(SubagentUsage usage)
        => FormatTokens(usage.TotalTokens) + " tokens・" + usage.ToolUses.ToString(CultureInfo.InvariantCulture) + " 次工具";

    public static string FormatTokens(long tokens)
        => tokens >= 1_000_000 ? (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : tokens >= 1_000 ? (tokens / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K"
            : tokens.ToString(CultureInfo.InvariantCulture);

    private static string Append(string head, string? detail)
    {
        var d = detail?.Trim();
        if (string.IsNullOrEmpty(d))
            return head;
        if (d.Length > 600)
            d = d[..600] + "…";
        return head + "：" + d;
    }

    private static string? Str(JObject o, string key) => o[key] is JValue { Type: JTokenType.String } v && v.ToString().Trim().Length > 0 ? v.ToString().Trim() : null;

    private static long Long(JObject o, string key) => o[key] is JValue { Type: JTokenType.Integer or JTokenType.Float } v ? Math.Max(0, Convert.ToInt64(v.Value, CultureInfo.InvariantCulture)) : 0;
}
