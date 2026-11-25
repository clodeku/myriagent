using System.Globalization;
using System.Text.RegularExpressions;
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

    /// <summary>PushNotification 工具的通知內容最多留多長（工具說明要代理 200 字以內，手機會截）</summary>
    public const int MaxNotifyLength = 500;

    /// <summary>PushNotification {message, status}：代理要通知使用者的一句話（空白併成一個、太長截短）；沒有內容回 null</summary>
    public static string? NotifyMessage(JObject? input)
    {
        if (input == null || Str(input, "message") is not { } raw)
            return null;
        var text = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MaxNotifyLength)
            text = text[..(char.IsHighSurrogate(text[MaxNotifyLength - 1]) ? MaxNotifyLength - 1 : MaxNotifyLength)] + "…";
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// model_refusal_fallback {original_model, fallback_model, scope, direction, api_refusal_category, retracted_message_uuids…}：安全機制攔下了回覆、
    /// 改用別的模型重答。scope 是 session（舊版沒有這個欄位，也當 session）＝整個對話換過去；local＝只有子代理或旁支提問那一次。
    /// direction 是 revert（回到原本的模型）時不算換過去
    /// </summary>
    public static AgentModelRefusalEvent RefusalFallback(JObject message)
    {
        var original = Str(message, "original_model");
        var fallback = Str(message, "fallback_model");
        bool reverted = Str(message, "direction") == "revert";
        bool session = Str(message, "scope") != "local";
        static string Label(string? model) => model != null ? ModelLabel(model) : "另一個模型";
        var why = RefusalCategory(Str(message, "api_refusal_category"));
        string head;
        if (reverted)
            head = $"Claude 已換回原本的模型{(original != null ? " " + ModelLabel(original) : string.Empty)}";
        else if (session)
            head = $"Claude 的安全機制攔下了這次回覆{why}，改用 {Label(fallback)} 接著回答（原本是 {(original != null ? ModelLabel(original) : "選定的模型")}）。"
                   + "這種判斷偶爾會誤攔一般的程式或資安工作；之後的回覆也是接手的模型，桌面版按輸入框上方的「換回」可以換回去";
        else
            head = $"Claude 的安全機制攔下了子代理或旁支提問的回覆{why}，那一次改用 {Label(fallback)} 回答，對話本身的模型沒有變";
        return new AgentModelRefusalEvent(original, reverted ? null : fallback, SessionSwitched: session && !reverted && fallback != null, head, Reverted: reverted);
    }

    /// <summary>model_refusal_no_fallback：安全機制攔下了回覆，而且沒有可以接手的模型，這次就停在這裡</summary>
    public static AgentModelRefusalEvent RefusalWithoutFallback(JObject message)
        => new(Str(message, "original_model"), null, SessionSwitched: false,
            $"Claude 的安全機制攔下了這次回覆{RefusalCategory(Str(message, "api_refusal_category"))}，沒有可以接手的模型，這次停在這裡。可以換個說法再問，或先換別的模型");

    private static readonly Regex ModelId = new(@"^claude-(?<family>[a-z]+)-(?<major>\d{1,2})(?:-(?<minor>\d{1,2}))?(?:-\d{8})?(?:\[[^\]]*\])?$", RegexOptions.CultureInvariant);

    /// <summary>模型的顯示名稱：「claude-fable-5-2」→「Fable 5.2」；不是這種樣子的照原文，沒有是「原本的模型」</summary>
    public static string ModelLabel(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "原本的模型";
        var match = ModelId.Match(id.Trim());
        if (!match.Success)
            return id.Trim();
        var family = match.Groups["family"].Value;
        var version = match.Groups["minor"].Success ? match.Groups["major"].Value + "." + match.Groups["minor"].Value : match.Groups["major"].Value;
        return char.ToUpperInvariant(family[0]) + family[1..] + " " + version;
    }

    /// <summary>同一個模型（不看 [1m] 這類視窗的後綴與大小寫）</summary>
    public static bool SameModel(string? a, string? b)
    {
        static string Bare(string? s)
        {
            var t = (s ?? string.Empty).Trim();
            var i = t.IndexOf('[');
            return i > 0 ? t[..i] : t;
        }
        return string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>被攔的類別（開放的字串）：認得的寫中文，不認得的照原文</summary>
    private static string RefusalCategory(string? category) => category switch
    {
        null => string.Empty,
        "cyber" => "（類別：資安）",
        "bio" => "（類別：生物）",
        _ => $"（類別：{category}）",
    };

    /// <summary>撤回的訊息 uuid（model_refusal_fallback 的 retracted_message_uuids、assistant 的 supersedes）；沒有回空的</summary>
    public static IReadOnlyList<string> MessageUuids(JToken? token)
        => token is JArray array
            ? array.OfType<JValue>().Where(v => v.Type == JTokenType.String && v.ToString().Length > 0).Select(v => v.ToString()).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// informational {content, level, tool_use_id}：CLI 給使用者看的說明（hook 為什麼擋下、安全機制先停下再試一次…）；
    /// level 是 info 的只在終端機的逐字稿模式顯示，不畫
    /// </summary>
    public static string? Informational(JObject message)
        => Str(message, "level") is "notice" or "warning" or "suggestion" && Str(message, "content") is { } text ? Append("Claude 說明", text) : null;

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
