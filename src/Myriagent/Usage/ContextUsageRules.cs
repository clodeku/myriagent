using System.Globalization;
using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Usage;

/// <summary>
/// 對話目前的上下文用量（中樞只記在記憶體，同 lastTurn；app 重開後等下一次模型呼叫）：UsedTokens＝最近一次呼叫的輸入（含快取）＋輸出，
/// WindowTokens＝目前模型的上下文視窗，AutoCompactTokens＝CLI 自動壓縮的門檻（不明為 null）
/// </summary>
internal sealed record ContextUsage(long? UsedTokens, long? WindowTokens, long? AutoCompactTokens);

/// <summary>
/// 上下文用量提示（只供顯示，docs/web-protocol.md 的 Header.context）。數字一律來自 CLI 回報的欄位（實測見 docs/protocol-notes.md「上下文用量」）：
/// <list type="bullet">
/// <item>Claude：主對話每次 API 呼叫的 message_start／message_delta usage（input＋cache_creation＋cache_read＋output；/compact 的 pre_tokens 就是這個數），
/// 視窗是 result 的 modelUsage[目前的模型].contextWindow，自動壓縮門檻與 CLI 實際採用的視窗由每輪之後的 get_context_usage 控制請求補上</item>
/// <item>Codex：thread/tokenUsage/updated 的 last.totalTokens（壓縮後那一則只有 totalTokens＝壓縮後的大小）與 modelContextWindow</item>
/// <item>Grok：_x.ai/session_notification 的 response_completed（每次模型呼叫）與 session/prompt 回應 _meta 頂層（最後一次呼叫），
/// 視窗是模型清單裡目前模型的 _meta.totalContextTokens</item>
/// <item>Antigravity：串流沒有視窗大小，不顯示</item>
/// </list>
/// </summary>
internal static class ContextUsageRules
{
    /// <summary>嚴重度門檻：用量對「上限」（有自動壓縮門檻就用它，否則用視窗）的比例</summary>
    public const double WarnRatio = 0.70;
    public const double HighRatio = 0.85;
    public const double MaxRatio = 0.95;

    /// <summary>
    /// 合併一則更新：有帶的欄位取代、沒帶的沿用。Compacted 時清掉舊的 UsedTokens（除非同一則就帶了新的）；
    /// 視窗變了（換模型）而這則沒帶門檻時，舊的門檻作廢；AutoCompactTokens 帶 0＝CLI 回報不會自動壓縮（清掉門檻）
    /// </summary>
    public static ContextUsage? Apply(ContextUsage? current, AgentContextUsageEvent update)
    {
        var used = Valid(update.UsedTokens) ?? (update.Compacted ? null : current?.UsedTokens);
        var window = Valid(update.WindowTokens) ?? current?.WindowTokens;
        long? auto;
        if (update.AutoCompactTokens is { } a)
            auto = a > 0 ? a : null;
        else
            auto = window != current?.WindowTokens ? null : current?.AutoCompactTokens;
        if (auto is { } t && window is { } w && t > w)
            auto = null;
        return used == null && window == null && auto == null ? null : new ContextUsage(used, window, auto);
    }

    /// <summary>給網頁的 Header.context：用量或視窗不明時 null（網頁不顯示）</summary>
    public static JObject? ToView(ContextUsage? usage)
    {
        if (usage is not { UsedTokens: { } used, WindowTokens: { } window } || window <= 0)
            return null;
        var auto = usage.AutoCompactTokens is { } a && a > 0 && a <= window ? a : (long?)null;
        return new JObject
        {
            ["used"] = used,
            ["window"] = window,
            ["percent"] = Math.Round(Math.Min(100.0, used * 100.0 / window), 1),
            ["autoCompactAt"] = auto,
            ["severity"] = Severity(used, window, auto),
        };
    }

    /// <summary>"ok"（未達 70%）| "warn"（70～85%）| "high"（85～95%）| "max"（95% 以上）；比例的分母是自動壓縮門檻，沒有就用視窗</summary>
    public static string Severity(long used, long window, long? autoCompact)
    {
        double limit = autoCompact is { } a && a > 0 ? a : window;
        if (limit <= 0)
            return "ok";
        double ratio = used / limit;
        return ratio >= MaxRatio ? "max" : ratio >= HighRatio ? "high" : ratio >= WarnRatio ? "warn" : "ok";
    }

    // ───────────────────────────── Claude ─────────────────────────────

    /// <summary>
    /// 一次 API 呼叫的上下文大小：ClaudeStreamRules.MergeMessageUsage 的欄位（input、cache_creation、cache_read、output）相加。
    /// 輸入三欄都是 0（舊版 API 的 message_delta 只帶 output）時不明
    /// </summary>
    public static long? ClaudeCallTokens(long[]? fields)
    {
        if (fields is not { Length: >= 4 })
            return null;
        long input = fields[0] + fields[1] + fields[2];
        return input > 0 ? input + fields[3] : null;
    }

    /// <summary>
    /// result 的 modelUsage 裡目前模型的 contextWindow（modelUsage 是這個程序的累計，換過模型或用過子代理會有好幾列）。
    /// 認得的模型（主對話最近一則訊息的 model）找不到時，只有一列有 contextWindow 才用它
    /// </summary>
    public static long? ClaudeWindow(JObject? modelUsage, string? model)
    {
        if (modelUsage == null)
            return null;
        if (!string.IsNullOrWhiteSpace(model))
        {
            var match = modelUsage.Properties().FirstOrDefault(p => string.Equals(p.Name, model, StringComparison.OrdinalIgnoreCase));
            if (match?.Value is JObject row)
                return Positive(row["contextWindow"]);
        }
        var windows = modelUsage.Properties().Select(p => (p.Value as JObject)?["contextWindow"]).Select(Positive).Where(v => v != null).ToList();
        return windows.Count == 1 ? windows[0] : null;
    }

    /// <summary>
    /// get_context_usage 控制請求的回應（2.1.309 實測：totalTokens、maxTokens、rawMaxTokens、percentage、autoCompactThreshold、isAutoCompactEnabled…）：
    /// 視窗取 rawMaxTokens（硬上限，沒有就 maxTokens）；isAutoCompactEnabled 是 false 時門檻回 0（清掉）。totalTokens 是 CLI 自己估的分類合計，不用
    /// </summary>
    public static (long? Window, long? AutoCompact) ParseClaudeContextResponse(JObject? response)
    {
        if (response == null)
            return (null, null);
        var window = Positive(response["rawMaxTokens"]) ?? Positive(response["maxTokens"]);
        long? auto = response["isAutoCompactEnabled"] is JValue { Type: JTokenType.Boolean } enabled && !(bool)enabled.Value!
            ? 0
            : Positive(response["autoCompactThreshold"]);
        return (window, auto);
    }

    // ───────────────────────────── Codex ─────────────────────────────

    /// <summary>
    /// thread/tokenUsage/updated 的 tokenUsage：last.totalTokens（最近一次呼叫的輸入〔含快取〕＋輸出；壓縮後那一則只有 totalTokens）與 modelContextWindow
    /// </summary>
    public static (long? Used, long? Window) ParseCodexTokenUsage(JObject? tokenUsage)
    {
        if (tokenUsage == null)
            return (null, null);
        long? used = tokenUsage["last"] is JObject last ? NonNegative(last["totalTokens"]) : null;
        return (used is > 0 ? used : null, Positive(tokenUsage["modelContextWindow"]));
    }

    // ───────────────────────────── Grok ─────────────────────────────

    /// <summary>
    /// _x.ai/session_notification 的 response_completed（每次模型呼叫）的 usage：input_tokens 不含快取讀取（實測 36,675＋快取 1,664＝回應的 38,339），
    /// 上下文＝input＋cache_read＋cache_creation＋output
    /// </summary>
    public static long? GrokCallTokens(JObject? usage)
    {
        if (usage == null || NonNegative(usage["input_tokens"]) is not { } input)
            return null;
        long total = input + (NonNegative(usage["cache_read_input_tokens"]) ?? 0) + (NonNegative(usage["cache_creation_input_tokens"]) ?? 0)
                     + (NonNegative(usage["output_tokens"]) ?? 0);
        return total > 0 ? total : null;
    }

    /// <summary>session/prompt 回應 _meta 頂層（最後一次模型呼叫；inputTokens 已含快取讀取）的 inputTokens＋outputTokens</summary>
    public static long? GrokPromptTokens(JToken? promptResult)
    {
        if ((promptResult as JObject)?["_meta"] is not JObject meta || NonNegative(meta["inputTokens"]) is not { } input)
            return null;
        long total = input + (NonNegative(meta["outputTokens"]) ?? 0);
        return total > 0 ? total : null;
    }

    /// <summary>
    /// 模型清單（initialize 的 _meta.modelState、session/new 的 models、_x.ai/models/update；{currentModelId, availableModels:[{modelId, _meta:{totalContextTokens}}]}）
    /// 裡各模型的上下文視窗，與清單標的目前模型
    /// </summary>
    public static (string? CurrentModelId, Dictionary<string, long> Windows) ParseGrokWindows(JToken? modelState)
    {
        var windows = new Dictionary<string, long>(StringComparer.Ordinal);
        if (modelState is not JObject state)
            return (null, windows);
        if (state["availableModels"] is JArray models)
        {
            foreach (var model in models.OfType<JObject>())
            {
                if (model.Value<string>("modelId") is { Length: > 0 } id && Positive((model["_meta"] as JObject)?["totalContextTokens"]) is { } window)
                    windows[id] = window;
            }
        }
        return (state["currentModelId"]?.Type == JTokenType.String ? state.Value<string>("currentModelId") : null, windows);
    }

    /// <summary>
    /// 同一份模型清單裡各模型的可選視窗（_meta.contextWindows，例如 [256000, 500000]）。
    /// 目前生效的視窗仍是 totalContextTokens（<see cref="ParseGrokWindows"/>）；ACP 沒有切換方法，這份只給說明用
    /// </summary>
    public static Dictionary<string, long[]> ParseGrokWindowChoices(JToken? modelState)
    {
        var choices = new Dictionary<string, long[]>(StringComparer.Ordinal);
        if (modelState is not JObject state || state["availableModels"] is not JArray models)
            return choices;
        foreach (var model in models.OfType<JObject>())
        {
            if (model.Value<string>("modelId") is not { Length: > 0 } id || (model["_meta"] as JObject)?["contextWindows"] is not JArray array)
                continue;
            var sizes = new List<long>();
            foreach (var token in array)
            {
                if (Positive(token) is { } size && !sizes.Contains(size))
                    sizes.Add(size);
            }
            if (sizes.Count > 0)
                choices[id] = sizes.ToArray();
        }
        return choices;
    }

    private static long? Valid(long? value) => value is >= 0 ? value : null;

    private static long? Positive(JToken? token) => NonNegative(token) is > 0 and var v ? v : null;

    private static long? NonNegative(JToken? token)
        => token is JValue { Type: JTokenType.Integer or JTokenType.Float } v && Convert.ToDouble(v.Value, CultureInfo.InvariantCulture) is var d && d >= 0 && d < long.MaxValue
            ? (long)d
            : null;
}
