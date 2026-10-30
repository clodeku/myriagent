using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>劇本化一輪的一個步驟（chat 操作的 script，格式見 README）。<paramref name="DelayMs"/>：這一步每一段之前等的毫秒數（真實時間）</summary>
internal abstract record ScriptedStep(int DelayMs);

/// <summary>
/// 代理的回覆：文字切成 <paramref name="Segments"/> 段依序串流（每段一個文字片段事件），最後一則完成事件帶完整內容。
/// <paramref name="Stamp"/>（WP4.9 步 8，D8）：每段開頭插入發出當下的時間標記（<see cref="StampRules.Format"/>），完成事件帶加了標記的全文；
/// <paramref name="CompleteEachSegment"/>：每段各成一個完成的項目（片段之後立刻完成，給「持久」那一條路量延遲），不是一個項目串到底
/// </summary>
internal sealed record ScriptedText(string Text, int Segments, int DelayMs, bool Stamp = false, bool CompleteEachSegment = false) : ScriptedStep(DelayMs);

/// <summary>工具：開始（Running）→ 輸出切成 <paramref name="Segments"/> 段 → 結束（Done；<paramref name="Failed"/> 時是 Failed）</summary>
internal sealed record ScriptedTool(string Name, string Kind, string Title, string? Detail, string Output, int Segments, int DelayMs, bool Failed, string? FilePath)
    : ScriptedStep(DelayMs);

/// <summary>
/// 問卷卡：發出後這一輪停在這裡，等中樞回答或略過才接著做。<paramref name="Answer"/>（題目 id → 答案）或 <paramref name="Skip"/> 是 chat 操作
/// 替使用者回應的方式；兩者都沒有就留著等（chat 操作回報 waiting）
/// </summary>
internal sealed record ScriptedQuestion(IReadOnlyList<AgentQuestion> Questions, IReadOnlyDictionary<string, IReadOnlyList<string>>? Answer, bool Skip, int DelayMs)
    : ScriptedStep(DelayMs);

/// <summary>核准卡：發出後等中樞回應才接著做（拒絕也接著做）。<paramref name="Decision"/> 是 chat 操作替使用者回應的選擇（allow、allowSession、deny；null＝留著等）</summary>
internal sealed record ScriptedApproval(string ToolName, string Kind, string Title, string? Detail, string? Decision, int DelayMs) : ScriptedStep(DelayMs);

/// <summary>代理產出的媒體（本機磁碟上已存在的檔案；中樞只顯示存在的本機路徑）</summary>
internal sealed record ScriptedMedia(string Path, int DelayMs) : ScriptedStep(DelayMs);

/// <summary>思考：切成 <paramref name="Segments"/> 段串流（Grok 的 agent_thought_chunk、Claude 的 thinking；同一段思考一個項目）</summary>
internal sealed record ScriptedThinking(string Text, int Segments, int DelayMs) : ScriptedStep(DelayMs);

/// <summary>待辦清單更新（Grok 的 ACP plan、Claude 的 TodoWrite、Codex 的 turn/plan；狀態 pending、inProgress、completed）</summary>
internal sealed record ScriptedTodo(IReadOnlyList<AgentPlanStep> Steps, string? Explanation, int DelayMs) : ScriptedStep(DelayMs);

/// <summary>
/// 計畫確認卡（AgentApprovalEvent 的 IsPlan）：發出後等中樞回應（approvePlan）才接著做。預設的名稱與標題是 Grok 的 exit_plan_mode 卡
/// （GrokAcpRules：「計畫」「照這個計畫執行？」）；<paramref name="Decision"/>（acceptEdits、manual、keepPlanning；null＝留著等）是 chat 替使用者的回應
/// </summary>
internal sealed record ScriptedPlanApproval(string Detail, string ToolName, string Title, string? Decision, int DelayMs) : ScriptedStep(DelayMs);

/// <summary>
/// 劇本化的一輪（<see cref="ScriptedSession"/> 照它發事件）：依序的步驟與這一輪的結果。JSON 是步驟陣列，或 <c>{"steps":[…],"outcome":"completed|failed"}</c>；
/// 每一步以它帶的鍵決定種類：<c>text</c>、<c>tool</c>、<c>question</c>、<c>approval</c>、<c>media</c>、<c>thinking</c>、<c>todo</c>、<c>planApproval</c>（格式見 README）。
/// 物件形式另可帶 <c>usage</c>（<c>{"input":…,"output":…,"cached":…}</c>）：這一輪結束事件的用量（標頭的 lastTurn、側欄的 token 計數）
/// </summary>
internal sealed record ScriptedTurn(IReadOnlyList<ScriptedStep> Steps, AgentTurnOutcome Outcome = AgentTurnOutcome.Completed, AgentUsage? Usage = null)
{
    public const int MaxSegments = 1_000;

    public const int MaxDelayMs = 60_000;

    /// <summary>只回一段文字（不串流）的一輪</summary>
    public static ScriptedTurn Reply(string text) => new([new ScriptedText(text, 1, 0)]);

    /// <summary>解讀 JSON 劇本；格式不對丟 <see cref="FormatException"/>（訊息給使用者看）</summary>
    public static ScriptedTurn Parse(JToken token)
    {
        var (steps, outcome, usage) = token switch
        {
            JArray array => (array, AgentTurnOutcome.Completed, (AgentUsage?)null),
            JObject { } obj when obj["steps"] is JArray array => (array, ParseOutcome(obj.Value<string>("outcome")), ParseUsage(obj["usage"])),
            _ => throw new FormatException("script 要是步驟陣列，或 {\"steps\":[…]}"),
        };
        var result = new List<ScriptedStep>();
        var index = 0;
        foreach (var item in steps)
        {
            index++;
            if (item is not JObject step)
                throw new FormatException($"script 第 {index} 步不是物件");
            try
            {
                result.Add(ParseStep(step));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"script 第 {index} 步：{ex.Message}");
            }
        }

        return new ScriptedTurn(result, outcome, usage);
    }

    /// <summary>這一輪的用量：<c>{"input","output","cached"}</c>（非負整數；cached 可省略）；沒給是 null</summary>
    private static AgentUsage? ParseUsage(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
            return null;
        if (token is not JObject usage)
            throw new FormatException("usage 要是 {\"input\":…,\"output\":…}");
        long Count(string key, bool required)
        {
            var value = usage[key];
            if (value is null || value.Type == JTokenType.Null)
                return required ? throw new FormatException($"usage.{key} 要是非負整數") : 0;
            return value.Type == JTokenType.Integer && value.Value<long>() >= 0 ? value.Value<long>() : throw new FormatException($"usage.{key} 要是非負整數");
        }

        return new AgentUsage(Count("input", true), Count("output", true), Count("cached", false), null);
    }

    private static ScriptedStep ParseStep(JObject step)
    {
        var delay = Bounded(step, "delayMs", 0, 0, MaxDelayMs);
        if (step["text"] is { } text)
        {
            return new ScriptedText(RequiredText(text, "text"), Bounded(step, "segments", 1, 1, MaxSegments), delay,
                Flag(step, "stamp"), Flag(step, "completeEachSegment"));
        }

        if (step["tool"] is { } tool)
        {
            return new ScriptedTool(
                RequiredText(tool, "tool"),
                step.Value<string>("kind") ?? "other",
                step.Value<string>("title") ?? RequiredText(tool, "tool"),
                step.Value<string>("detail"),
                step.Value<string>("output") ?? string.Empty,
                Bounded(step, "segments", 1, 1, MaxSegments),
                delay,
                step.Value<bool?>("failed") ?? false,
                step.Value<string>("filePath"));
        }

        if (step["question"] is { } question)
            return new ScriptedQuestion(ParseQuestions(question), ParseAnswer(step["answer"]), step.Value<bool?>("skip") ?? false, delay);
        if (step["approval"] is { } approval)
        {
            var decision = step.Value<string>("decision");
            if (decision is not (null or "allow" or "allowSession" or "deny"))
                throw new FormatException($"decision 只能是 allow、allowSession、deny：{decision}");
            return new ScriptedApproval(RequiredText(approval, "approval"), step.Value<string>("kind") ?? "other", step.Value<string>("title") ?? RequiredText(approval, "approval"),
                step.Value<string>("detail"), decision, delay);
        }

        if (step["media"] is { } media)
            return new ScriptedMedia(RequiredText(media, "media"), delay);
        if (step["thinking"] is { } thinking)
            return new ScriptedThinking(RequiredText(thinking, "thinking"), Bounded(step, "segments", 1, 1, MaxSegments), delay);
        if (step["todo"] is { } todo)
            return new ScriptedTodo(ParseTodo(todo), step.Value<string>("explanation"), delay);
        if (step["planApproval"] is { } plan)
        {
            var decision = step.Value<string>("decision");
            if (decision is not (null or "acceptEdits" or "manual" or "keepPlanning"))
                throw new FormatException($"decision 只能是 acceptEdits、manual、keepPlanning：{decision}");
            return new ScriptedPlanApproval(RequiredText(plan, "planApproval"), step.Value<string>("toolName") ?? "計畫", step.Value<string>("title") ?? "照這個計畫執行？",
                decision, delay);
        }

        throw new FormatException("每一步要有 text、tool、question、approval、media、thinking、todo、planApproval 其中一個");
    }

    /// <summary>待辦：<c>[{"text","status"}]</c>，status 是 pending、inProgress、completed（同 AgentPlanStep）</summary>
    private static List<AgentPlanStep> ParseTodo(JToken token)
    {
        if (token is not JArray array || array.Count == 0)
            throw new FormatException("todo 要是至少一項的陣列");
        var steps = new List<AgentPlanStep>();
        foreach (var item in array)
        {
            if (item is not JObject entry)
                throw new FormatException("todo 的每一項要是物件");
            var status = entry.Value<string>("status") ?? "pending";
            if (status is not ("pending" or "inProgress" or "completed"))
                throw new FormatException($"todo[].status 只能是 pending、inProgress、completed：{status}");
            steps.Add(new AgentPlanStep { Text = RequiredText(entry["text"], "todo[].text"), Status = status });
        }

        return steps;
    }

    private static List<AgentQuestion> ParseQuestions(JToken token)
    {
        if (token is not JArray array || array.Count == 0)
            throw new FormatException("question 要是至少一題的陣列");
        var questions = new List<AgentQuestion>();
        foreach (var item in array)
        {
            if (item is not JObject q)
                throw new FormatException("question 的每一題要是物件");
            var text = RequiredText(q["text"], "question[].text");
            questions.Add(new AgentQuestion
            {
                Id = q.Value<string>("id") ?? text,
                Header = q.Value<string>("header"),
                Text = text,
                Options = (q["options"] as JArray ?? []).Select(o => new AgentQuestionOption { Label = o.ToString() }).ToList(),
                MultiSelect = q.Value<bool?>("multiSelect") ?? false,
                AllowOther = q.Value<bool?>("allowOther") ?? true,
                IsSecret = q.Value<bool?>("secret") ?? false,
            });
        }

        return questions;
    }

    private static Dictionary<string, IReadOnlyList<string>>? ParseAnswer(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
            return null;
        if (token is not JObject answer || !answer.HasValues)
            throw new FormatException("answer 要是 {題目 id: [答案…]}");
        return answer.Properties().ToDictionary(
            p => p.Name,
            p => (IReadOnlyList<string>)(p.Value is JArray values ? values.Select(v => v.ToString()).ToList() : [p.Value.ToString()]),
            StringComparer.Ordinal);
    }

    private static AgentTurnOutcome ParseOutcome(string? value) => value switch
    {
        null or "completed" => AgentTurnOutcome.Completed,
        "failed" => AgentTurnOutcome.Failed,
        _ => throw new FormatException($"outcome 只能是 completed、failed：{value}"),
    };

    private static string RequiredText(JToken? token, string name) =>
        token is JValue { Type: JTokenType.String, Value: string text } && text.Length > 0 ? text : throw new FormatException($"{name} 要是非空字串");

    private static bool Flag(JObject step, string key) => step[key] switch
    {
        null or { Type: JTokenType.Null } => false,
        { Type: JTokenType.Boolean } value => value.Value<bool>(),
        _ => throw new FormatException($"{key} 要是 true 或 false"),
    };

    private static int Bounded(JObject step, string key, int fallback, int min, int max)
    {
        var token = step[key];
        if (token is null || token.Type == JTokenType.Null)
            return fallback;
        if (token.Type != JTokenType.Integer)
            throw new FormatException($"{key} 要是整數");
        var value = token.Value<long>();
        return value >= min && value <= max ? (int)value : throw new FormatException($"{key} 要在 {min}～{max} 之間：{value}");
    }
}
