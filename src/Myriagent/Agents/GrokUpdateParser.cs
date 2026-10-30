using System.Text;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 把 grok 的 session/update（與 _x.ai/session_notification 的 plan 類更新）轉成共同事件。每個程序一個（同一個執行緒依序呼叫，不加鎖）。
/// <list type="bullet">
/// <item>文字與思考：agent_message_chunk／agent_thought_chunk 沒有區塊 id，中間插進工具或另一種內容就開新區塊；文字區塊結束時發完整內容</item>
/// <item>工具：tool_call 只有工具名稱與參數，緊接著的 tool_call_update 才帶 ACP 的 kind、格式化的標題與差異（實測約 1 毫秒後）。
/// 中樞建立後不會更新工具卡，所以等第一個 tool_call_update（或核准請求、這一輪結束）才發 AgentToolStartedEvent</item>
/// <item>停止後 grok 不再送執行中工具的結束更新：FinishTurn 把還在執行的工具結束成「已停止」</item>
/// </list>
/// </summary>
internal sealed class GrokUpdateParser
{
    private readonly string prefix;
    private int blockSequence;
    private string? textBlock;
    private readonly StringBuilder text = new();
    private string? thoughtBlock;
    private readonly Dictionary<string, ToolState> tools = new(StringComparer.Ordinal);

    private sealed class ToolState
    {
        public JObject Call = new();
        public bool Announced;
        public bool Hidden;
        public bool Finished;
        public string Output = string.Empty;
    }

    public GrokUpdateParser(string prefix = "grok")
    {
        this.prefix = prefix;
    }

    /// <summary>執行中（已開始、還沒結束）的工具 id（含還沒發 AgentToolStartedEvent 的；不含不畫成卡片的提問與計畫確認）</summary>
    public IReadOnlyList<string> RunningToolIds => tools.Where(t => !t.Value.Finished && !t.Value.Hidden).Select(t => t.Key).ToList();

    public bool HasRunningTools => tools.Values.Any(t => !t.Finished && !t.Hidden);

    /// <summary>這個工具呼叫合併到目前為止的內容（核准卡用）；不知道回 null</summary>
    public JObject? GetToolCall(string toolCallId) => tools.TryGetValue(toolCallId, out var state) ? state.Call : null;

    /// <summary>新的一輪開始：區塊重新起算（上一輪留下的工具狀態清掉）</summary>
    public void BeginTurn()
    {
        textBlock = null;
        thoughtBlock = null;
        text.Clear();
        tools.Clear();
    }

    /// <summary>session/update 的 update 物件 → 事件</summary>
    public List<AgentEvent> HandleUpdate(JObject update)
    {
        var events = new List<AgentEvent>();
        switch (update.Value<string>("sessionUpdate"))
        {
            case "agent_message_chunk":
            {
                var delta = ChunkText(update);
                if (string.IsNullOrEmpty(delta))
                    break;
                thoughtBlock = null;
                textBlock ??= NextBlock("t");
                text.Append(delta);
                events.Add(new AgentTextDeltaEvent(textBlock, delta));
                break;
            }
            case "agent_thought_chunk":
            {
                var delta = ChunkText(update);
                if (string.IsNullOrEmpty(delta))
                    break;
                CloseText(events);
                thoughtBlock ??= NextBlock("r");
                events.Add(new AgentThinkingDeltaEvent(thoughtBlock, delta));
                break;
            }
            case "tool_call":
                OnToolCall(update, events);
                break;
            case "tool_call_update":
                OnToolCallUpdate(update, events);
                break;
            case "plan":
            {
                var steps = GrokAcpRules.ParsePlanEntries(update["entries"]);
                if (steps.Count > 0)
                    events.Add(new AgentPlanEvent(null, steps));
                break;
            }
            case "available_commands_update":
                events.Add(new AgentCommandsEvent(GrokAcpRules.ParseCommands(update["availableCommands"])));
                break;
        }
        return events;
    }

    /// <summary>核准請求到了：工具卡還沒發就先發（卡片要在核准卡之前），並合併請求裡的工具內容</summary>
    public List<AgentEvent> AnnounceForRequest(JObject? requestToolCall)
    {
        var events = new List<AgentEvent>();
        var id = requestToolCall?.Value<string>("toolCallId");
        if (string.IsNullOrEmpty(id))
            return events;
        var state = GetOrCreate(id, requestToolCall!);
        Merge(state.Call, requestToolCall!);
        Announce(id, state, events);
        return events;
    }

    /// <summary>
    /// 這一輪結束：結束文字區塊；還沒發的工具補發開始，還在執行的工具結束成 status（停止＝Cancelled「已停止」；程序結束＝Cancelled「被中斷」）
    /// </summary>
    public List<AgentEvent> FinishTurn(AgentItemStatus unfinishedStatus = AgentItemStatus.Cancelled, string? unfinishedOutput = GrokAcpRules.CancelledToolOutput)
    {
        var events = new List<AgentEvent>();
        CloseText(events);
        thoughtBlock = null;
        foreach (var (id, state) in tools)
        {
            if (state.Finished || state.Hidden)
                continue;
            Announce(id, state, events);
            state.Finished = true;
            var output = string.IsNullOrEmpty(state.Output) ? unfinishedOutput : JoinOutput(state.Output, unfinishedOutput);
            // 停止、程序中斷的收尾文字不是工具產出，不從裡面挖路徑
            events.Add(new AgentToolCompletedEvent(id, unfinishedStatus, output, Array.Empty<string>()));
        }
        return events;
    }

    /// <summary>只結束文字區塊（重開打斷的一輪：工具卡由中樞收尾）</summary>
    public List<AgentEvent> FinishTextOnly()
    {
        var events = new List<AgentEvent>();
        CloseText(events);
        thoughtBlock = null;
        return events;
    }

    private void OnToolCall(JObject update, List<AgentEvent> events)
    {
        var id = update.Value<string>("toolCallId");
        if (string.IsNullOrEmpty(id))
            return;
        CloseText(events);
        thoughtBlock = null;
        var state = GetOrCreate(id, update);
        Merge(state.Call, update);
        // tool_call 本身就帶了結束狀態（重播、很快完成的工具）：照 tool_call_update 處理
        if (GrokAcpRules.ToolEndStatus(update.Value<string>("status")) != null)
            OnToolCallUpdate(update, events);
    }

    private void OnToolCallUpdate(JObject update, List<AgentEvent> events)
    {
        var id = update.Value<string>("toolCallId");
        if (string.IsNullOrEmpty(id))
            return;
        CloseText(events);
        thoughtBlock = null;
        var state = GetOrCreate(id, update);
        Merge(state.Call, update);
        if (state.Hidden || state.Finished)
            return;
        Announce(id, state, events);

        // 沒有 status 的第一個更新（補上 kind、標題、差異）content 放的是工具的說明，不是輸出（實測）
        var status = update.Value<string>("status");
        if (string.IsNullOrEmpty(status))
            return;
        var output = GrokAcpRules.ParseToolOutput(update);
        var end = GrokAcpRules.ToolEndStatus(status);
        if (end == null)
        {
            // 執行中的更新帶的是到目前為止的完整輸出：只送新增的部分
            if (!string.IsNullOrEmpty(output) && output.Length > state.Output.Length && output.StartsWith(state.Output, StringComparison.Ordinal))
            {
                events.Add(new AgentToolOutputEvent(id, output[state.Output.Length..]));
                state.Output = output;
            }
            return;
        }

        state.Finished = true;
        // 使用者在核准卡拒絕：核准卡已經顯示決定，工具卡只標「已拒絕」（grok 的說明是英文的 User rejected the execution）
        if (GrokAcpRules.IsRejectedUpdate(update))
        {
            events.Add(new AgentToolCompletedEvent(id, AgentItemStatus.Declined, null, Array.Empty<string>()));
            return;
        }
        var finalStatus = end == AgentItemStatus.Done && GrokAcpRules.CommandExitCode(update) is { } code && code != 0 ? AgentItemStatus.Failed : end.Value;
        var kind = GrokAcpRules.DescribeTool(state.Call).Kind;
        // 編輯類成功時有差異就夠了（輸出只是「已修改檔案」）；失敗一律附上原因
        string? finalOutput = finalStatus == AgentItemStatus.Done && kind is "write" or "edit" && state.Call["content"] is JArray content
                && content.OfType<JObject>().Any(c => c.Value<string>("type") == "diff")
            ? null
            : output ?? (string.IsNullOrEmpty(state.Output) ? null : state.Output);
        CompleteTool(events, id, finalStatus, finalOutput, update);
    }

    /// <summary>工具正常結束：本機成片路徑進燈箱；零資料保留的英文錯誤改發中文說明</summary>
    private static void CompleteTool(List<AgentEvent> events, string id, AgentItemStatus status, string? output, JObject update)
    {
        events.Add(new AgentToolCompletedEvent(id, status, output, GrokAcpRules.ExtractDisplayMedia(output, update)));
        if (GrokAcpRules.DescribeZeroRetention(output) is { } notice)
            events.Add(new AgentNoticeEvent(notice, IsError: true));
    }

    private ToolState GetOrCreate(string id, JObject first)
    {
        if (!tools.TryGetValue(id, out var state))
        {
            state = new ToolState();
            var name = ((first["_meta"] as JObject)?["x.ai/tool"] as JObject)?.Value<string>("name") ?? first.Value<string>("title");
            state.Hidden = GrokAcpRules.IsHiddenTool(name);
            tools[id] = state;
        }
        return state;
    }

    private void Announce(string id, ToolState state, List<AgentEvent> events)
    {
        if (state.Announced || state.Hidden)
            return;
        state.Announced = true;
        var d = GrokAcpRules.DescribeTool(state.Call);
        events.Add(new AgentToolStartedEvent(id, d.Name, d.Kind, d.Title, d.Detail, d.FilePath, d.Diff));
    }

    /// <summary>
    /// 合併工具更新：後到的欄位蓋過先到的；_meta["x.ai/tool"] 逐欄合併（tool_call_update 的版本多了 input，但可能少了 name）；
    /// sessionUpdate 不留
    /// </summary>
    private static void Merge(JObject target, JObject update)
    {
        foreach (var property in update.Properties())
        {
            if (property.Name is "sessionUpdate")
                continue;
            if (property.Name == "_meta" && property.Value is JObject meta && target["_meta"] is JObject existingMeta)
            {
                foreach (var m in meta.Properties())
                {
                    if (m.Value is JObject inner && existingMeta[m.Name] is JObject existingInner)
                        existingInner.Merge(inner, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Ignore });
                    else
                        existingMeta[m.Name] = m.Value.DeepClone();
                }
                continue;
            }
            if (property.Value.Type == JTokenType.Null)
                continue;
            target[property.Name] = property.Value.DeepClone();
        }
    }

    private void CloseText(List<AgentEvent> events)
    {
        if (textBlock == null)
            return;
        events.Add(new AgentTextCompletedEvent(textBlock, text.ToString()));
        textBlock = null;
        text.Clear();
    }

    private string NextBlock(string kind) => $"{prefix}-{kind}{++blockSequence}";

    private static string? ChunkText(JObject update)
        => update["content"] is JObject content && content.Value<string>("type") == "text" ? content.Value<string>("text") : null;

    private static string JoinOutput(string output, string? note)
        => string.IsNullOrEmpty(note) ? output : output.TrimEnd() + "\n\n" + note;
}
