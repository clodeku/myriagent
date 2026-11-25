using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 將 Antigravity ACP（<c>agy acp --stdio</c>）的 <c>session/update</c> 通知轉成 Myriagent 共同事件（<see cref="AgentEvent"/>），
/// 並可選伴讀 <c>transcript_full.jsonl</c> 補齊媒體路徑與子代理軌跡。每個程序實例一個，於讀取執行緒或鎖保護下依序呼叫。
/// </summary>
internal sealed partial class AntigravityAcpUpdateParser
{
    private sealed record PlanReviewCandidate(string? Path, string? Summary, string? InlineMarkdown = null);
    private sealed record BackgroundEntry(string Key, AgentBackgroundTool Tool, bool IsOneShotTimer, bool IsDaemon);

    private sealed class ToolState
    {
        public JObject Call = new();
        public bool Announced;
        public bool Hidden;
        public bool EmittedPlanSteps;
        public bool Finished;
        public string Kind = "other";
        public string? ToolName;
        public string? DisplayName;
        public string? Title;
        public string? Detail;
        public string? FilePath;
        public JObject? Args;
        public PlanReviewCandidate? PlanReview;
        public string Output = string.Empty;
    }

    private readonly string prefix;
    private readonly AntigravityTranscriptReader transcript;
    private readonly Func<string, string?>? transcriptPathResolver;
    private readonly Func<string, string?> artifactFileReader;
    private int blockSequence;
    private int turn;
    private string? textBlock;
    private readonly StringBuilder text = new();
    private string? thoughtBlock;
    private readonly Dictionary<string, ToolState> tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BackgroundEntry> liveBackgroundTasks = new(StringComparer.OrdinalIgnoreCase);
    private PlanReviewCandidate? pendingPlanReview;
    private bool isPlanMode;

    public AntigravityAcpUpdateParser(
        string prefix = "agy",
        Func<string, string?>? transcriptPathResolver = null,
        Func<string, string?>? artifactFileReader = null)
    {
        this.prefix = prefix;
        this.transcriptPathResolver = transcriptPathResolver;
        this.artifactFileReader = artifactFileReader ?? AntigravityTranscriptRules.ReadArtifactFileSafe;
        transcript = new AntigravityTranscriptReader(transcriptPathResolver);
    }

    public bool HasRunningTools => tools.Values.Any(t => !t.Finished && !t.Hidden);

    public bool HasLiveBackgroundWork => liveBackgroundTasks.Count > 0;

    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools
        => liveBackgroundTasks.Count == 0 ? Array.Empty<AgentBackgroundTool>() : liveBackgroundTasks.Values.Select(e => e.Tool).ToArray();

    public string? LastGeneratedTitle { get; private set; }

    public void BindSession(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            transcript.BindConversation(sessionId);
    }

    public void BeginTurn(bool planMode = false)
    {
        turn++;
        isPlanMode = planMode;
        textBlock = null;
        thoughtBlock = null;
        text.Clear();
        tools.Clear();
        pendingPlanReview = null;
    }

    public JObject? GetToolCall(string toolCallId)
        => tools.TryGetValue(toolCallId, out var state) ? state.Call : null;

    public List<AgentEvent> HandleUpdate(JObject update)
    {
        var events = new List<AgentEvent>();
        var kind = update.Value<string>("sessionUpdate");
        switch (kind)
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
            case "usage_update":
            {
                if (AntigravityAcpRules.TryParseUsageUpdate(update, out var used, out var window))
                    events.Add(new AgentContextUsageEvent(used, window));
                break;
            }
            case "session_info_update":
            {
                if (AntigravityAcpRules.TryParseSessionInfoUpdate(update, out var title, out _) && !string.IsNullOrWhiteSpace(title))
                    LastGeneratedTitle = title;
                break;
            }
            case "available_commands_update":
            {
                var reported = GrokAcpRules.ParseCommands(update["availableCommands"]);
                if (reported.Count > 0)
                {
                    var merged = AntigravityWorkspaceScanner.MergeCommands(AntigravityStreamRules.BuiltinWorkflowCommands, reported);
                    events.Add(new AgentCommandsEvent(merged));
                }
                break;
            }
        }
        return events;
    }

    /// <summary>核准請求到達時若工具卡尚未發出，先發出工具開始事件，確保核准卡能關聯至該工具卡</summary>
    public (List<AgentEvent> Events, AntigravityStepRules.ToolDescription Description, IReadOnlyList<AgentDiffLine>? Diff) AnnounceForPermission(JObject? requestToolCall)
    {
        var events = new List<AgentEvent>();
        if (requestToolCall == null)
        {
            var fallback = new AntigravityStepRules.ToolDescription("工具", "other", "需要核准的操作", null, null);
            return (events, fallback, null);
        }

        var id = requestToolCall.Value<string>("toolCallId");
        if (string.IsNullOrEmpty(id))
        {
            var (rawName, rawArgs) = AntigravityAcpRules.ExtractToolNameAndArgs(requestToolCall);
            var rawDiff = AntigravityAcpRules.ExtractToolDiff(requestToolCall);
            var desc = DescribeCall(rawName, rawArgs, requestToolCall, rawDiff);
            return (events, desc, rawDiff);
        }

        CloseText(events);
        thoughtBlock = null;
        var state = GetOrCreate(id, requestToolCall);
        Merge(state.Call, requestToolCall);
        var diff = Announce(id, state, events);
        var d = new AntigravityStepRules.ToolDescription(
            state.DisplayName ?? "工具",
            state.Kind,
            state.Title ?? "需要核准的操作",
            state.Detail,
            state.FilePath);
        return (events, d, diff);
    }

    public List<AgentEvent> FinishTurn(
        AgentTurnOutcome outcome = AgentTurnOutcome.Completed,
        AgentItemStatus unfinishedStatus = AgentItemStatus.Cancelled,
        string? unfinishedOutput = AntigravityAcpRules.CancelledToolOutput)
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
            events.Add(new AgentToolCompletedEvent(id, unfinishedStatus, output, Array.Empty<string>()));
        }

        if (outcome == AgentTurnOutcome.Completed && pendingPlanReview is { } planReview)
        {
            var question = AntigravityStreamRules.BuildPlanFeedbackQuestion(planReview.Path, planReview.Summary);
            var localPlanPath = AntigravityTranscriptRules.TryResolveLocalPath(planReview.Path) ?? planReview.Path;
            var planMarkdown = (!string.IsNullOrWhiteSpace(localPlanPath) ? artifactFileReader(localPlanPath) : null) ?? planReview.InlineMarkdown;
            events.Add(new AgentQuestionEvent(
                $"{prefix}-{turn}-plan",
                new[] { question },
                AnswerAsMessage: true,
                Detail: planMarkdown,
                FilePath: localPlanPath));
            pendingPlanReview = null;
        }

        if (liveBackgroundTasks.Count > 0)
        {
            var nonDaemonKeys = liveBackgroundTasks.Values
                .Where(e => !e.IsDaemon && !e.Key.StartsWith("sched-", StringComparison.Ordinal))
                .Select(e => e.Key)
                .ToList();
            if (nonDaemonKeys.Count > 0)
            {
                foreach (var k in nonDaemonKeys)
                    liveBackgroundTasks.Remove(k);
                EmitBackgroundWorkEvent(events);
            }
        }

        return events;
    }

    public List<AgentEvent> FinishTextOnly()
    {
        var events = new List<AgentEvent>();
        CloseText(events);
        thoughtBlock = null;
        return events;
    }

    public int ClearBackgroundTasks()
    {
        int had = liveBackgroundTasks.Count;
        liveBackgroundTasks.Clear();
        return had;
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
        TryEmitUpdateSteps(state, events);
        if (state.Hidden)
        {
            if (AntigravityAcpRules.ClassifyToolEndStatus(update.Value<string>("status")) != null)
                state.Finished = true;
            return;
        }
        Announce(id, state, events);
        if (AntigravityAcpRules.ClassifyToolEndStatus(update.Value<string>("status")) != null)
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
        TryEmitUpdateSteps(state, events);
        if (state.Hidden || state.Finished)
        {
            if (AntigravityAcpRules.ClassifyToolEndStatus(update.Value<string>("status")) != null)
                state.Finished = true;
            return;
        }

        Announce(id, state, events);

        var rawStatus = update.Value<string>("status");
        if (string.IsNullOrEmpty(rawStatus))
            return;

        var (output, exitCode) = AntigravityAcpRules.ExtractToolOutput(update);
        var end = AntigravityAcpRules.ClassifyToolEndStatus(rawStatus);
        if (end == null)
        {
            if (!string.IsNullOrEmpty(output) && output.Length > state.Output.Length && output.StartsWith(state.Output, StringComparison.Ordinal))
            {
                events.Add(new AgentToolOutputEvent(id, output[state.Output.Length..]));
                state.Output = output;
            }
            return;
        }

        state.Finished = true;
        if (AntigravityAcpRules.IsRejectedToolUpdate(rawStatus, output))
        {
            events.Add(new AgentToolCompletedEvent(id, AgentItemStatus.Declined, null, Array.Empty<string>()));
            return;
        }

        var finalStatus = end == AgentItemStatus.Done && exitCode is { } code && code != 0
            ? AgentItemStatus.Failed
            : end.Value;

        string? doneArtifact = null;
        if (finalStatus == AgentItemStatus.Done)
            AntigravityTranscriptRules.TryExtractArtifactMarkdown(state.ToolName, state.Args, state.FilePath, artifactFileReader, out doneArtifact);
        if (finalStatus == AgentItemStatus.Done && state.PlanReview is { } review)
            pendingPlanReview = doneArtifact != null ? review with { InlineMarkdown = doneArtifact } : review;

        string? finalOutput = finalStatus == AgentItemStatus.Done && state.Kind is "write" or "edit" && AntigravityAcpRules.ExtractToolDiff(state.Call) != null
            ? null
            : output ?? (string.IsNullOrEmpty(state.Output) ? null : state.Output);

        var mediaPaths = finalStatus == AgentItemStatus.Done && state.Kind != "read"
            ? ExtractMediaPathsFromOutput(finalOutput)
            : Array.Empty<string>();

        events.Add(new AgentToolCompletedEvent(id, finalStatus, finalOutput, mediaPaths, doneArtifact));
        if (finalStatus == AgentItemStatus.Done)
            UpdateBackgroundTasksFromTool(id, state, finalOutput, events);
    }

    private void TryEmitUpdateSteps(ToolState state, List<AgentEvent> events)
    {
        var (name, args) = AntigravityAcpRules.ExtractToolNameAndArgs(state.Call);
        if (!string.Equals(name, "update_steps", StringComparison.OrdinalIgnoreCase) || state.EmittedPlanSteps)
            return;
        var steps = AntigravityStepRules.ParseUpdateSteps(args?["steps"]);
        if (steps.Count > 0)
        {
            state.EmittedPlanSteps = true;
            events.Add(new AgentPlanEvent(null, steps));
        }
    }

    private ToolState GetOrCreate(string id, JObject first)
    {
        if (!tools.TryGetValue(id, out var state))
        {
            var (name, _) = AntigravityAcpRules.ExtractToolNameAndArgs(first);
            state = new ToolState
            {
                ToolName = name,
                Hidden = string.Equals(name, "update_steps", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(name, "ask_question", StringComparison.OrdinalIgnoreCase),
            };
            tools[id] = state;
        }
        return state;
    }

    private IReadOnlyList<AgentDiffLine>? Announce(string id, ToolState state, List<AgentEvent> events)
    {
        var (name, args) = AntigravityAcpRules.ExtractToolNameAndArgs(state.Call);
        var diff = AntigravityAcpRules.ExtractToolDiff(state.Call);
        if (state.Announced || state.Hidden)
            return diff;

        state.Announced = true;
        var d = DescribeCall(name, args, state.Call, diff);
        state.ToolName = name;
        state.DisplayName = d.Name;
        state.Kind = d.Kind;
        state.Title = d.Title;
        state.Detail = d.Detail;
        state.FilePath = d.FilePath;
        state.Args = args;

        AntigravityTranscriptRules.TryExtractArtifactMarkdown(name, args, d.FilePath, readFile: null, out var startArtifact);
        if (AntigravityTranscriptRules.TryExtractPlanReview(name, args, d.FilePath, isPlanMode, out var planPath, out var summary))
        {
            var inlinePlan = startArtifact ?? AntigravityTranscriptRules.FindString(args, "Content", "CodeContent");
            state.PlanReview = new PlanReviewCandidate(planPath, summary, inlinePlan);
        }

        events.Add(new AgentToolStartedEvent(id, d.Name, d.Kind, d.Title, d.Detail, d.FilePath, diff, startArtifact));
        return diff;
    }

    private static AntigravityStepRules.ToolDescription DescribeCall(
        string? toolName,
        JObject? args,
        JObject call,
        IReadOnlyList<AgentDiffLine>? diff)
    {
        JObject? infoWrapper = args != null ? new JObject { ["parameters"] = args } : null;
        var desc = AntigravityStepRules.DescribeTool(toolName, infoWrapper, subagentInfo: null, hasDiff: diff is { Count: > 0 });
        if (string.IsNullOrEmpty(toolName))
        {
            var rawTitle = call.Value<string>("title")?.Trim();
            if (!string.IsNullOrEmpty(rawTitle))
                return desc with { Title = rawTitle };
        }
        return desc;
    }

    private void UpdateBackgroundTasksFromTool(string id, ToolState state, string? output, List<AgentEvent> events)
    {
        var rawTool = state.ToolName ?? string.Empty;
        var args = state.Args;

        if (rawTool.Equals("run_command", StringComparison.OrdinalIgnoreCase))
        {
            bool isDaemon = AntigravityTranscriptRules.FindToken(args, "IsDaemon") is JValue { Type: JTokenType.Boolean, Value: true };
            bool backgrounded = !string.IsNullOrWhiteSpace(output)
                && (output.Contains("running in the background", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("background task", StringComparison.OrdinalIgnoreCase)
                    || (output.Contains("background", StringComparison.OrdinalIgnoreCase) && TaskIdPattern().IsMatch(output)));
            if (!isDaemon && !backgrounded)
                return;
            var key = ExtractTaskKey(output) ?? $"bg-{id}";
            liveBackgroundTasks[key] = new BackgroundEntry(
                key,
                new AgentBackgroundTool(state.DisplayName ?? "執行指令", "bash", state.Title, state.Detail),
                IsOneShotTimer: false,
                IsDaemon: isDaemon);
            EmitBackgroundWorkEvent(events);
            return;
        }

        if (rawTool.Equals("run_workflow", StringComparison.OrdinalIgnoreCase))
        {
            bool backgrounded = !string.IsNullOrWhiteSpace(output)
                && (output.Contains("running in the background", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("background task", StringComparison.OrdinalIgnoreCase)
                    || (output.Contains("background", StringComparison.OrdinalIgnoreCase) && TaskIdPattern().IsMatch(output)));
            if (!backgrounded)
                return;
            var key = ExtractTaskKey(output) ?? $"wf-{id}";
            liveBackgroundTasks[key] = new BackgroundEntry(
                key,
                new AgentBackgroundTool(state.DisplayName ?? "執行工作流", "agent", state.Title, state.Detail),
                IsOneShotTimer: false,
                IsDaemon: false);
            EmitBackgroundWorkEvent(events);
            return;
        }

        if (rawTool.Equals("schedule", StringComparison.OrdinalIgnoreCase))
        {
            bool isDaemon = AntigravityTranscriptRules.FindToken(args, "IsDaemon") is JValue { Type: JTokenType.Boolean, Value: true };
            bool hasCron = !string.IsNullOrWhiteSpace(AntigravityTranscriptRules.FindString(args, "CronExpression"));
            var key = ExtractTaskKey(output) ?? $"sched-{id}";
            var prompt = AntigravityTranscriptRules.FindString(args, "Prompt");
            var title = !string.IsNullOrWhiteSpace(prompt) ? ClaudeStreamRules.FirstLine(prompt.Trim()) : (state.Title ?? "排程計時");
            liveBackgroundTasks[key] = new BackgroundEntry(
                key,
                new AgentBackgroundTool(state.DisplayName ?? "排程計時", "other", title, state.Detail),
                IsOneShotTimer: !isDaemon && !hasCron,
                IsDaemon: isDaemon);
            EmitBackgroundWorkEvent(events);
            return;
        }

        if (rawTool.Equals("manage_task", StringComparison.OrdinalIgnoreCase))
        {
            var action = AntigravityTranscriptRules.FindString(args, "Action");
            var taskId = AntigravityTranscriptRules.FindString(args, "TaskId")?.Trim();
            if (string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(taskId))
            {
                bool removed = liveBackgroundTasks.Remove(taskId);
                if (!removed && liveBackgroundTasks.Keys.FirstOrDefault(k => k.EndsWith(taskId, StringComparison.OrdinalIgnoreCase)) is { } matchKey)
                    removed = liveBackgroundTasks.Remove(matchKey);
                if (removed)
                    EmitBackgroundWorkEvent(events);
            }
        }
    }

    private void EmitBackgroundWorkEvent(List<AgentEvent> events)
        => events.Add(new AgentBackgroundWorkEvent(
            liveBackgroundTasks.Count,
            liveBackgroundTasks.Values.Select(e => e.Tool).ToArray()));

    private static IReadOnlyList<string> ExtractMediaPathsFromOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<string>();
        return Hub.AgentHubRules.FindMediaToShow(output, File.Exists);
    }

    private static string? ExtractTaskKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = TaskIdPattern().Match(text);
        return m.Success ? m.Value : null;
    }

    private static void Merge(JObject target, JObject update)
    {
        foreach (var property in update.Properties())
        {
            if (property.Name is "sessionUpdate" || property.Value.Type == JTokenType.Null)
                continue;
            if (property.Value is JObject inner && target[property.Name] is JObject existingInner)
            {
                existingInner.Merge(inner, new JsonMergeSettings
                {
                    MergeArrayHandling = MergeArrayHandling.Replace,
                    MergeNullValueHandling = MergeNullValueHandling.Ignore,
                });
            }
            else
            {
                target[property.Name] = property.Value.DeepClone();
            }
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

    private string NextBlock(string kind) => $"{prefix}-{turn}-{kind}{++blockSequence}";

    private static string? ChunkText(JObject update)
        => update["content"] is JObject content && content.Value<string>("type") == "text" ? content.Value<string>("text") : null;

    private static string JoinOutput(string output, string? note)
        => string.IsNullOrEmpty(note) ? output : output.TrimEnd() + "\n\n" + note;

    [GeneratedRegex(@"\btask-[A-Za-z0-9_-]+\b", RegexOptions.IgnoreCase)]
    private static partial Regex TaskIdPattern();
}
