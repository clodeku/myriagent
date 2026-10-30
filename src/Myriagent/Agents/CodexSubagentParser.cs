using System.Text;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

internal sealed partial class CodexEventParser
{
    private sealed class ChildActivity
    {
        public string Card = string.Empty;
        public string Label = string.Empty;
        public bool Completed;
        public int Generation;
        public readonly StringBuilder Output = new();
        public readonly HashSet<string> Streamed = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, ChildActivity> childActivities = new(StringComparer.Ordinal);

    private ChildActivity Child(string thread, string? label, string? model, string? effort, CodexParseResult result, bool restart = false)
    {
        if (!childActivities.TryGetValue(thread, out var child))
        {
            child = new ChildActivity { Label = label ?? "子代理" };
            childActivities[thread] = child;
        }
        if (child.Card.Length == 0 || restart && child.Completed)
        {
            child.Card = $"codex-child:{thread}:{++child.Generation}";
            child.Completed = false;
            child.Output.Clear();
            child.Streamed.Clear();
            if (label != null) child.Label = label;
            var title = child.Label + (model != null ? " · " + model : string.Empty) + (effort != null ? " · " + effort : string.Empty);
            result.Events.Add(new AgentToolStartedEvent(child.Card, "子代理", "agent", title, "展開查看任務、執行過程與結果"));
        }
        return child;
    }

    private static void ChildOutput(ChildActivity child, string? text, CodexParseResult result)
    {
        if (child.Completed || string.IsNullOrEmpty(text)) return;
        child.Output.Append(text);
        // 長任務只保留最近的過程；完整紀錄仍由 Codex 保存。
        if (child.Output.Length > 100_000) child.Output.Remove(0, child.Output.Length - 80_000);
        result.Events.Add(new AgentToolOutputEvent(child.Card, text));
    }

    private static void FinishChild(ChildActivity child, AgentItemStatus status, CodexParseResult result)
    {
        if (child.Completed) return;
        child.Completed = true;
        result.Events.Add(new AgentToolCompletedEvent(child.Card, status, child.Output.ToString(), Array.Empty<string>()));
    }

    private void SubagentActivity(JObject? item, CodexParseResult result)
    {
        if (item?.Value<string>("type") != "subAgentActivity" || item.Value<string>("agentThreadId") is not { } thread) return;
        var kind = item.Value<string>("kind");
        var child = Child(thread, item.Value<string>("agentPath"), item.Value<string>("model"), item.Value<string>("reasoningEffort"), result,
            restart: kind is "started" or "interacted");
        if (kind == "interrupted") FinishChild(child, AgentItemStatus.Cancelled, result);
        if (kind == "completed") FinishChild(child, AgentItemStatus.Done, result);
    }

    private void ChildEvent(string? method, JObject parameters, CodexParseResult result)
    {
        if (parameters.Value<string>("threadId") is not { } thread) return;
        if (method == "item/started") SubagentActivity(parameters["item"] as JObject, result);
        if (method is not ("turn/started" or "turn/completed" or "item/started" or "item/completed" or "item/agentMessage/delta" or "item/commandExecution/outputDelta" or "error")) return;
        var child = Child(thread, null, threadModels.GetValueOrDefault(thread), null, result, restart: method == "turn/started");
        if (method is "item/agentMessage/delta" or "item/commandExecution/outputDelta")
        {
            if (parameters.Value<string>("itemId") is { } id) child.Streamed.Add(id);
            ChildOutput(child, parameters.Value<string>("delta"), result);
        }
        if (parameters["item"] is JObject item)
        {
            var kind = item.Value<string>("type");
            if (method == "item/started")
            {
                var line = kind switch
                {
                    "commandExecution" => "\n執行：" + CodexAppServerRules.DescribeCommand(item) + "\n",
                    "fileChange" => "\n修改：" + DescribeFileChanges(item).Title + "\n",
                    "mcpToolCall" => "\nMCP：" + item.Value<string>("server") + "／" + item.Value<string>("tool") + "\n",
                    "webSearch" => "\n搜尋：" + item.Value<string>("query") + "\n",
                    _ => null,
                };
                ChildOutput(child, line, result);
            }
            if (method == "item/completed")
            {
                if (kind == "agentMessage" && !child.Streamed.Contains(item.Value<string>("id") ?? string.Empty))
                    ChildOutput(child, item.Value<string>("text") + "\n", result);
                if (kind == "userMessage")
                    foreach (var content in (item["content"] as JArray ?? []).OfType<JObject>().Where(c => c.Value<string>("type") == "text"))
                        ChildOutput(child, "\n任務：" + content.Value<string>("text") + "\n", result);
                if (kind == "commandExecution" && !child.Streamed.Contains(item.Value<string>("id") ?? string.Empty) && item.Value<string>("aggregatedOutput") is { Length: > 0 } output)
                    ChildOutput(child, "\n" + output + "\n", result);
            }
        }
        if (method == "error") ChildOutput(child, "\n錯誤：" + (parameters["error"] as JObject)?.Value<string>("message") + "\n", result);
        if (method == "turn/completed")
        {
            var turn = parameters["turn"] as JObject;
            var status = turn?.Value<string>("status");
            if ((turn?["error"] as JObject)?.Value<string>("message") is { } error)
                ChildOutput(child, "\n錯誤：" + error + "\n", result);
            FinishChild(child, status == "failed" ? AgentItemStatus.Failed : status == "interrupted" ? AgentItemStatus.Cancelled : AgentItemStatus.Done, result);
        }
    }
}
