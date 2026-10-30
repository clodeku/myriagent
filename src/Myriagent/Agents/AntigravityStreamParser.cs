using System.Globalization;
using System.Text.RegularExpressions;
using Myriagent.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// agy stream-json 輸出的一行解讀結果：要發的事件、這一行回報的對話 id 與模型、這一輪是否結束
/// </summary>
internal sealed class AntigravityParseResult
{
    public List<AgentEvent> Events { get; } = new();

    /// <summary>第一次看到（或改變）的對話 id</summary>
    public string? NewConversationId { get; set; }

    /// <summary>init 回報的模型</summary>
    public string? Model { get; set; }

    /// <summary>這一行是 result（這一輪結束）；事件裡最後一個是 AgentTurnCompletedEvent</summary>
    public bool TurnCompleted { get; set; }

    /// <summary>這一行不是 JSON（例如沒登入時的登入網址提示）：原文，會話自己判斷</summary>
    public string? PlainText { get; set; }
}

/// <summary>
/// 把 agy 的 stream-json 輸出（init、step_update、result）連同磁碟上的 transcript_full.jsonl 轉成共同事件（AgentEvents.cs）。
/// 不執行緒安全：每個程序一個、在讀取執行緒呼叫。
/// <list type="bullet">
/// <item>init 的對話 id 在最外層（{"event":"init","conversation_id":…,"init":{model,cwd,tools,permission_mode}}）；step_update、result 的在各自的物件裡</item>
/// <item>每個步驟以 step_index 區分（整個對話連續編號，續接後接著算）。step_type：agent_response 帶 text_delta，tool 帶 tool_name 與 tool_info；
/// 另即時增量伴讀 brain\&lt;conversationId&gt;\.system_generated\logs\transcript_full.jsonl 補齊 PLANNER_RESPONSE.thinking（思考區塊）、
/// 寫檔／改檔工具的完整參數與紅綠 Diff、非讀取工具產出的媒體路徑（MediaPaths）、以及 ArtifactMetadata.RequestFeedback／plan.md 計畫確認卡</item>
/// <item>用量：每個呼叫模型的步驟在 DONE 時帶這一步的 usage（子代理步驟看 subagent_info.usage）；result 的 usage 是整個對話累計，所以這一輪用量＝本輪各步驟相加</item>
/// <item>result 的 denied_actions 是這個程序被自動拒絕過的操作（累計）：「DONE 但沒有 output」的指令先等 result 判斷是否被拒</item>
/// <item>result 失敗且錯誤訊息符合訂閱額度或點數耗盡時，在 AgentTurnCompletedEvent 之前發出 AgentQuotaExhaustedEvent；
/// 錯誤是 API 伺服器暫時無法服務（503 UNAVAILABLE）時 AgentTurnCompletedEvent.ServerBusy 標成 Unavailable（中樞等一下自動重試）</item>
/// </list>
/// </summary>
internal sealed partial class AntigravityStreamParser
{
    private sealed record PlanReviewCandidate(string? Path, string? Summary, string? InlineMarkdown = null);
    private sealed record BackgroundEntry(string Key, AgentBackgroundTool Tool, bool IsOneShotTimer, bool IsDaemon = false);

    private sealed class StepState
    {
        public required string Id { get; init; }
        public int? NumericIndex { get; init; }
        public bool IsTool { get; set; }
        public bool Started { get; set; }
        public bool DeferredStart { get; set; }
        public bool Done { get; set; }
        public string Kind { get; set; } = "other";
        public string? ToolName { get; set; }
        public string? DisplayToolName { get; set; }
        public string? ToolTitle { get; set; }
        public string? ToolDetail { get; set; }
        public string? FilePath { get; set; }
        public JObject? ArtifactArgs { get; set; }
        public string? PendingToolName { get; set; }
        public JObject? PendingToolInfo { get; set; }
        public JObject? PendingSubagentInfo { get; set; }
        public PlanReviewCandidate? PlanReview { get; set; }
        public bool PermissionDenied { get; set; }

        /// <summary>指令報 DONE 但沒有 output：可能是被拒（見類別說明），完成事件等 result 再發</summary>
        public bool PendingCompletion { get; set; }
        public System.Text.StringBuilder Text { get; } = new();
        public bool TextCompleted { get; set; }
    }

    private readonly Dictionary<string, StepState> steps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentUsage> stepUsage = new(StringComparer.Ordinal);

    /// <summary>這個程序的模型（init 的 model）：token 增量記在這個模型</summary>
    private string? tokenModel;
    private readonly Dictionary<string, BackgroundEntry> liveBackgroundTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> noticedDenials = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> emittedThinkingSteps = new();
    private readonly HashSet<int> processedTranscriptSystemSteps = new();
    private readonly AntigravityTranscriptReader transcript;
    private readonly Func<string, string?>? transcriptPathResolver;
    private readonly Func<string, string?> artifactFileReader;
    private readonly string idPrefix;
    private readonly bool isPlanMode;

    private int turn;
    private int baselineStepIndex;
    private int minTurnStepIndex;
    private bool turnStartLocked;
    private PlanReviewCandidate? pendingPlanReview;

    /// <param name="idPrefix">文字區塊與工具的 id 前綴（每個程序不同：步驟編號在續接的程序裡會和先前的重複）</param>
    /// <param name="transcriptPathResolver">依 conversationId 解析逐字稿路徑（測試注入暫存路徑；null 用預設的使用者目錄）</param>
    /// <param name="initialConversationId">以 --conversation 續接既有對話時的對話 id：先記住既有逐字稿的最大步驟編號，避免重播舊輪次的思考</param>
    /// <param name="permissionMode">目前程序啟動的權限模式（規劃模式寫入 plan.md 時觸發確認卡）</param>
    /// <param name="artifactFileReader">讀取完成後的 Markdown Artifact／計畫檔內容（預設安全唯讀本機磁碟檔）</param>
    public AntigravityStreamParser(
        string idPrefix = "agy",
        Func<string, string?>? transcriptPathResolver = null,
        string? initialConversationId = null,
        AgentPermissionMode permissionMode = AgentPermissionMode.AcceptEdits,
        Func<string, string?>? artifactFileReader = null)
    {
        this.idPrefix = idPrefix;
        this.isPlanMode = permissionMode == AgentPermissionMode.Plan;
        this.transcriptPathResolver = transcriptPathResolver;
        this.artifactFileReader = artifactFileReader ?? AntigravityTranscriptRules.ReadArtifactFileSafe;
        transcript = new AntigravityTranscriptReader(transcriptPathResolver);
        if (!string.IsNullOrWhiteSpace(initialConversationId))
        {
            transcript.BindConversation(initialConversationId);
            transcript.Sync();
            baselineStepIndex = transcript.MaxSeenStepIndex + 1;
            minTurnStepIndex = baselineStepIndex;
        }
    }

    public string? ConversationId { get; private set; }

    /// <summary>有工具已開始（含等待逐字稿 flush 而延後發卡的編輯工具）、還沒結束（安全點判斷）</summary>
    public bool ToolRunning => steps.Values.Any(s => s.IsTool && (s.Started || s.DeferredStart) && !s.Done);

    /// <summary>目前解析器追蹤到還在執行的背景工作／計時排程</summary>
    public bool HasLiveBackgroundWork => liveBackgroundTasks.Count > 0;

    /// <summary>下一輪合理步驟編號下限（供閒置時過濾上一輪遲到的舊 step_update）</summary>
    public int MinNextTurnStepIndex
    {
        get
        {
            int next = baselineStepIndex;
            foreach (var step in steps.Values)
            {
                if (step.NumericIndex is { } idx && idx + 1 > next)
                    next = idx + 1;
            }
            return next;
        }
    }

    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools
        => liveBackgroundTasks.Count == 0 ? Array.Empty<AgentBackgroundTool>() : liveBackgroundTasks.Values.Select(e => e.Tool).ToArray();

    /// <summary>這一輪開始前呼叫：清掉上一輪的步驟與用量，推進逐字稿基準步驟編號</summary>
    public void BeginTurn()
    {
        if (ConversationId != null)
        {
            transcript.Sync();
            if (transcript.MaxSeenStepIndex + 1 > baselineStepIndex)
                baselineStepIndex = transcript.MaxSeenStepIndex + 1;
        }
        foreach (var step in steps.Values)
        {
            if (step.NumericIndex is { } idx && idx + 1 > baselineStepIndex)
                baselineStepIndex = idx + 1;
        }
        steps.Clear();
        stepUsage.Clear();
        minTurnStepIndex = baselineStepIndex;
        turnStartLocked = false;
        pendingPlanReview = null;
        turn++;
    }

    /// <summary>
    /// 程序被結束時還開著的工具與文字：工具改成已停止（附中斷說明）、文字照目前累積的定稿。回傳要發的事件
    /// </summary>
    public List<AgentEvent> AbortOpenSteps(string toolOutput)
    {
        var events = new List<AgentEvent>();
        transcript.Sync();
        foreach (var step in steps.Values)
        {
            if (step.DeferredStart && !step.Started)
                FlushDeferredToolStart(step, events);

            if (step.PendingCompletion)
            {
                step.PendingCompletion = false;
                events.Add(new AgentToolCompletedEvent(step.Id, AgentItemStatus.Done, null, Array.Empty<string>()));
            }
            else if (step.IsTool && step.Started && !step.Done)
            {
                step.Done = true;
                events.Add(new AgentToolCompletedEvent(step.Id, AgentItemStatus.Cancelled, toolOutput, Array.Empty<string>()));
            }
            else if (!step.IsTool && !step.TextCompleted && step.Text.Length > 0)
            {
                step.TextCompleted = true;
                events.Add(new AgentTextCompletedEvent(step.Id, step.Text.ToString()));
            }
        }
        return events;
    }

    public AntigravityParseResult Parse(string line)
    {
        var result = new AntigravityParseResult();
        if (string.IsNullOrWhiteSpace(line))
            return result;
        JObject obj;
        try
        {
            obj = JObject.Parse(line);
        }
        catch (JsonReaderException)
        {
            result.PlainText = line;
            return result;
        }

        var kind = Str(obj["event"]) ?? Str(obj["type"]);
        var payload = kind != null && obj[kind] is JObject inner ? inner : obj;
        switch (kind)
        {
            case "init":
                RememberConversation(Str(obj["conversation_id"]) ?? Str(payload["conversation_id"]), result);
                result.Model = Str(payload["model"]);
                // token 增量記在這個模型（子代理的步驟也是：agy 沒說子代理用哪個模型）
                if (!string.IsNullOrWhiteSpace(result.Model))
                    tokenModel = result.Model;
                break;
            case "step_update":
                RememberConversation(Str(payload["conversation_id"]), result);
                ParseStep(payload, result);
                break;
            case "result":
                RememberConversation(Str(payload["conversation_id"]), result);
                ParseResult(payload, result);
                break;
        }
        return result;
    }

    private void RememberConversation(string? id, AntigravityParseResult result)
    {
        if (!string.IsNullOrWhiteSpace(id) && id != ConversationId)
        {
            ConversationId = id;
            result.NewConversationId = id;
            transcript.BindConversation(id);
        }
    }

    private void ParseStep(JObject payload, AntigravityParseResult result)
    {
        var index = Str(payload["step_index"]) ?? "?";
        bool hasNumericIndex = int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericIndex);
        var stepType = Str(payload["step_type"]);
        var subagentInfo = payload["subagent_info"] as JObject;

        var usageObj = payload["usage"] as JObject ?? subagentInfo?["usage"] as JObject;
        if (usageObj != null)
        {
            // 同一步驟可能再回報一次（用量變大）：token 計數只記比上次多的部分
            var current = AntigravityStepRules.ToUsage(usageObj);
            var added = TokenTallyRules.Increment(stepUsage.GetValueOrDefault(index), current);
            stepUsage[index] = current;
            if (!TokenTallyRules.IsZero(added))
                result.Events.Add(new AgentTokenUsageEvent(added, tokenModel));
        }

        if (string.Equals(stepType, "user_input", StringComparison.OrdinalIgnoreCase))
        {
            if (hasNumericIndex)
            {
                SyncTranscriptBackgroundNotifications(result, numericIndex);
                minTurnStepIndex = Math.Max(minTurnStepIndex, numericIndex + 1);
                turnStartLocked = true;
            }
            return;
        }

        if (AntigravityStepRules.IsHiddenStep(stepType))
            return;

        var toolInfo = payload["tool_info"] as JObject;
        var toolName = Str(payload["tool_name"]) ?? Str(toolInfo?["name"]);
        bool isToolStep = !string.IsNullOrEmpty(toolName) || toolInfo != null || subagentInfo != null;

        if (hasNumericIndex)
        {
            if (!turnStartLocked)
            {
                // 沒收到 user_input（例如測試直接餵步驟）：工具步驟的前一步可能是它的 PLANNER_RESPONSE
                int candidateStart = isToolStep ? Math.Max(0, numericIndex - 1) : numericIndex;
                minTurnStepIndex = Math.Max(baselineStepIndex, candidateStart);
                turnStartLocked = true;
            }
            SyncTranscriptAndEmitThinking(result, numericIndex);
        }

        if (!steps.TryGetValue(index, out var step))
        {
            step = new StepState
            {
                Id = $"{idPrefix}-{turn}-{index}",
                NumericIndex = hasNumericIndex ? numericIndex : null,
            };
            steps[index] = step;
        }

        var state = AntigravityStepRules.ClassifyState(Str(payload["state"]));
        var delta = Str(payload["text_delta"]);

        if (isToolStep)
        {
            step.IsTool = true;
            if (subagentInfo != null)
                step.PendingSubagentInfo = subagentInfo;

            if (!step.Started)
            {
                var call = hasNumericIndex ? transcript.FindToolCall(numericIndex, toolName, minTurnStepIndex) : null;
                var mergedInfo = MergeTranscriptArgs(toolInfo, call?.Args);
                var diff = AntigravityTranscriptRules.ComputeToolDiff(toolName, call?.Args ?? (mergedInfo?["parameters"] as JObject));
                var tool = AntigravityStepRules.DescribeTool(toolName, mergedInfo, subagentInfo, hasDiff: diff is { Count: > 0 });

                // 寫檔與改檔工具若 ACTIVE 瞬間逐字稿還沒 flush 到磁碟（拿不到 Diff），先延後到 DONE 再發 AgentToolStartedEvent（AgentHub 只在 Started 寫入 Diff）
                if (tool.Kind is "write" or "edit" && diff == null && state == AntigravityStepState.Running && transcript.HasResolver && ConversationId != null)
                {
                    step.DeferredStart = true;
                    step.Kind = tool.Kind;
                    step.PendingToolName = toolName;
                    step.PendingToolInfo = toolInfo != null ? (JObject)toolInfo.DeepClone() : null;
                    return;
                }

                EmitToolStarted(step, toolName, mergedInfo, subagentInfo, diff, call?.Args, result.Events);
            }

            if (state != AntigravityStepState.Running && (!step.Done || step.PendingCompletion))
            {
                if (hasNumericIndex)
                    SyncTranscriptAndEmitThinking(result, numericIndex);
                if (step.DeferredStart && !step.Started)
                    FlushDeferredToolStart(step, result.Events, toolName, toolInfo, subagentInfo);

                step.Done = true;
                if (state == AntigravityStepState.Completed && step.Kind == "bash" && toolInfo?["output"] == null)
                {
                    step.PendingCompletion = true;
                    return;
                }
                step.PendingCompletion = false;
                var status = state switch
                {
                    AntigravityStepState.Failed => AgentItemStatus.Failed,
                    AntigravityStepState.Cancelled => AgentItemStatus.Cancelled,
                    _ => AgentItemStatus.Done,
                };
                var error = AntigravityStepRules.ToolError(toolInfo);
                step.PermissionDenied = AntigravityStepRules.IsPermissionDenial(error);
                string? doneArtifact = null;
                if (status == AgentItemStatus.Done)
                    AntigravityTranscriptRules.TryExtractArtifactMarkdown(step.ToolName, step.ArtifactArgs, step.FilePath, artifactFileReader, out doneArtifact);
                if (status == AgentItemStatus.Done && step.PlanReview is { } review)
                    pendingPlanReview = doneArtifact != null ? review with { InlineMarkdown = doneArtifact } : review;

                var mediaPaths = status == AgentItemStatus.Done && step.Kind != "read" && hasNumericIndex
                    ? CollectStepMediaPaths(numericIndex)
                    : Array.Empty<string>();
                var effectiveSubagent = subagentInfo ?? step.PendingSubagentInfo;
                var toolOutput = BuildCompletedToolOutput(step, toolInfo, effectiveSubagent);
                result.Events.Add(new AgentToolCompletedEvent(
                    step.Id,
                    status,
                    toolOutput,
                    mediaPaths,
                    doneArtifact));
                if (status == AgentItemStatus.Done)
                    UpdateBackgroundTasksFromTool(step, toolInfo, result.Events);
            }
            return;
        }

        if (!string.IsNullOrEmpty(delta))
        {
            step.Text.Append(delta);
            result.Events.Add(new AgentTextDeltaEvent(step.Id, delta));
        }
        if (state != AntigravityStepState.Running && !step.TextCompleted && step.Text.Length > 0)
        {
            step.TextCompleted = true;
            result.Events.Add(new AgentTextCompletedEvent(step.Id, step.Text.ToString()));
        }
    }

    private void FlushDeferredToolStart(
        StepState step,
        List<AgentEvent> events,
        string? latestToolName = null,
        JObject? latestToolInfo = null,
        JObject? latestSubagentInfo = null)
    {
        var toolName = latestToolName ?? step.PendingToolName;
        var rawInfo = latestToolInfo ?? step.PendingToolInfo;
        var subInfo = latestSubagentInfo ?? step.PendingSubagentInfo;
        var call = step.NumericIndex is { } idx ? transcript.FindToolCall(idx, toolName, minTurnStepIndex) : null;
        var mergedInfo = MergeTranscriptArgs(rawInfo, call?.Args);
        var diff = AntigravityTranscriptRules.ComputeToolDiff(toolName, call?.Args ?? (mergedInfo?["parameters"] as JObject));
        EmitToolStarted(step, toolName, mergedInfo, subInfo, diff, call?.Args, events);
    }

    private void EmitToolStarted(
        StepState step,
        string? toolName,
        JObject? mergedInfo,
        JObject? subagentInfo,
        IReadOnlyList<AgentDiffLine>? diff,
        JObject? transcriptArgs,
        List<AgentEvent> events)
    {
        step.Started = true;
        step.DeferredStart = false;
        var tool = AntigravityStepRules.DescribeTool(toolName, mergedInfo, subagentInfo, hasDiff: diff is { Count: > 0 });
        step.Kind = tool.Kind;
        step.ToolName = toolName;
        step.DisplayToolName = tool.Name;
        step.ToolTitle = tool.Title;
        step.ToolDetail = tool.Detail;
        step.FilePath = tool.FilePath;
        var argsForPlan = transcriptArgs ?? (mergedInfo?["parameters"] as JObject);
        step.ArtifactArgs = argsForPlan;
        AntigravityTranscriptRules.TryExtractArtifactMarkdown(toolName, argsForPlan, tool.FilePath, readFile: null, out var startArtifact);
        if (AntigravityTranscriptRules.TryExtractPlanReview(toolName, argsForPlan, tool.FilePath, isPlanMode, out var planPath, out var summary))
        {
            var inlinePlan = startArtifact ?? AntigravityTranscriptRules.FindString(argsForPlan, "Content", "CodeContent");
            step.PlanReview = new PlanReviewCandidate(planPath, summary, inlinePlan);
        }
        events.Add(new AgentToolStartedEvent(step.Id, tool.Name, tool.Kind, tool.Title, tool.Detail, tool.FilePath, diff, startArtifact));
    }

    private IReadOnlyList<string> CollectStepMediaPaths(int stepIndex)
    {
        var step = transcript.GetStep(stepIndex);
        return step is { MediaPaths.Count: > 0 } ? step.MediaPaths : Array.Empty<string>();
    }

    private static JObject? MergeTranscriptArgs(JObject? toolInfo, JObject? transcriptArgs)
    {
        if (transcriptArgs == null || transcriptArgs.Count == 0)
            return toolInfo;
        var clone = toolInfo != null ? (JObject)toolInfo.DeepClone() : new JObject();
        var parameters = clone["parameters"] as JObject ?? new JObject();
        foreach (var prop in transcriptArgs.Properties())
        {
            if (parameters[prop.Name] == null)
                parameters[prop.Name] = prop.Value.DeepClone();
        }
        clone["parameters"] = parameters;
        return clone;
    }

    private void SyncTranscriptAndEmitThinking(AntigravityParseResult result, int upToStepIndex)
    {
        if (turn <= 0 && !turnStartLocked)
            return;
        transcript.Sync();
        SyncTranscriptBackgroundNotificationsCore(result, upToStepIndex);
        foreach (var tStep in transcript.StepsFrom(minTurnStepIndex))
        {
            if (tStep.StepIndex > upToStepIndex)
                break;
            if (!string.IsNullOrWhiteSpace(tStep.Thinking) && emittedThinkingSteps.Add(tStep.StepIndex))
                result.Events.Add(new AgentThinkingDeltaEvent($"{idPrefix}-{turn}-think-{tStep.StepIndex}", tStep.Thinking));
        }
    }

    private void SyncTranscriptBackgroundNotifications(AntigravityParseResult result, int upToStepIndex)
    {
        if (liveBackgroundTasks.Count == 0 && turn <= 0 && !turnStartLocked)
            return;
        transcript.Sync();
        SyncTranscriptBackgroundNotificationsCore(result, upToStepIndex);
    }

    private void SyncTranscriptBackgroundNotificationsCore(AntigravityParseResult result, int upToStepIndex)
    {
        if (liveBackgroundTasks.Count == 0)
            return;

        bool changed = false;
        foreach (var tStep in transcript.StepsFrom(Math.Max(0, baselineStepIndex - 1)))
        {
            if (tStep.StepIndex > upToStepIndex)
                break;
            if (string.IsNullOrWhiteSpace(tStep.Content) || !processedTranscriptSystemSteps.Add(tStep.StepIndex))
                continue;
            if (!string.Equals(tStep.Type, "USER_INPUT", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(tStep.Type, "SYSTEM", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(tStep.Type, "SYSTEM_MESSAGE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = tStep.Content;
            bool looksSystemWakeup = content.Contains("<SYSTEM_MESSAGE>", StringComparison.OrdinalIgnoreCase)
                || content.Contains("background task", StringComparison.OrdinalIgnoreCase)
                || content.Contains("timer", StringComparison.OrdinalIgnoreCase)
                || content.Contains("schedule", StringComparison.OrdinalIgnoreCase);
            if (!looksSystemWakeup)
                continue;

            bool indicatesDone = content.Contains("finished", StringComparison.OrdinalIgnoreCase)
                || content.Contains("completed", StringComparison.OrdinalIgnoreCase)
                || content.Contains("exited", StringComparison.OrdinalIgnoreCase)
                || content.Contains("terminated", StringComparison.OrdinalIgnoreCase)
                || content.Contains("killed", StringComparison.OrdinalIgnoreCase);

            foreach (Match m in BackgroundTaskIdRegex().Matches(content))
            {
                var taskId = m.Value;
                if (indicatesDone && liveBackgroundTasks.Remove(taskId))
                    changed = true;
            }

            // 單次計時器（schedule DurationSeconds）到期喚醒時，若沒有明確 task-xxx id，移除最早的單次計時器
            if ((content.Contains("timer", StringComparison.OrdinalIgnoreCase) || content.Contains("schedule", StringComparison.OrdinalIgnoreCase))
                && liveBackgroundTasks.Values.FirstOrDefault(v => v.IsOneShotTimer) is { } timerEntry)
            {
                if (liveBackgroundTasks.Remove(timerEntry.Key))
                    changed = true;
            }
        }

        if (changed)
            EmitBackgroundWorkEvent(result.Events);
    }

    private string? BuildCompletedToolOutput(StepState step, JObject? toolInfo, JObject? effectiveSubagent)
    {
        var baseOutput = AntigravityStepRules.DescribeToolOutput(toolInfo, effectiveSubagent);
        if (effectiveSubagent == null && step.Kind != "agent")
            return baseOutput;

        var subTranscriptPath = AntigravityTranscriptRules.ResolveSubagentTranscriptPath(effectiveSubagent, transcriptPathResolver);
        var formattedTranscript = AntigravityTranscriptRules.FormatSubagentTranscript(subTranscriptPath);
        if (string.IsNullOrWhiteSpace(formattedTranscript))
            return baseOutput;

        // TrimToolOutput 在輸出過長時會裁掉開頭、保留尾端：把子代理過程紀錄放在上半部、結論與統計放在最底部，確保結論永遠完整保留
        if (string.IsNullOrWhiteSpace(baseOutput))
            return formattedTranscript;
        return $"{formattedTranscript}\n\n── 子代理結論 ──\n{baseOutput}";
    }

    private void UpdateBackgroundTasksFromTool(StepState step, JObject? toolInfo, List<AgentEvent> events)
    {
        var rawTool = step.ToolName ?? string.Empty;
        var args = step.ArtifactArgs ?? (toolInfo?["parameters"] as JObject);
        var output = toolInfo?["output"] is JValue { Value: not null } v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : null;
        if (string.IsNullOrWhiteSpace(output) && step.NumericIndex is { } sIdx)
            output = transcript.GetStep(sIdx)?.Content;

        if (rawTool.Equals("run_command", StringComparison.OrdinalIgnoreCase))
        {
            bool isDaemon = AntigravityTranscriptRules.FindToken(args, "IsDaemon") is JValue { Type: JTokenType.Boolean, Value: true };
            bool backgroundedInOutput = !string.IsNullOrWhiteSpace(output)
                && (output.Contains("running in the background", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("background task", StringComparison.OrdinalIgnoreCase)
                    || (output.Contains("background", StringComparison.OrdinalIgnoreCase) && BackgroundTaskIdRegex().IsMatch(output)));
            if (!isDaemon && !backgroundedInOutput)
                return;

            var key = ExtractTaskKey(output) ?? $"bg-{step.Id}";
            var tool = new AgentBackgroundTool(
                step.DisplayToolName ?? "執行指令",
                "bash",
                step.ToolTitle,
                step.ToolDetail);
            liveBackgroundTasks[key] = new BackgroundEntry(key, tool, IsOneShotTimer: false, IsDaemon: isDaemon);
            EmitBackgroundWorkEvent(events);
            return;
        }

        if (rawTool.Equals("schedule", StringComparison.OrdinalIgnoreCase))
        {
            bool isDaemon = AntigravityTranscriptRules.FindToken(args, "IsDaemon") is JValue { Type: JTokenType.Boolean, Value: true };
            bool hasCron = !string.IsNullOrWhiteSpace(AntigravityTranscriptRules.FindString(args, "CronExpression"));
            bool isOneShot = !isDaemon && !hasCron;
            var key = ExtractTaskKey(output) ?? $"sched-{step.Id}";
            var prompt = AntigravityTranscriptRules.FindString(args, "Prompt");
            var title = !string.IsNullOrWhiteSpace(prompt) ? ClaudeStreamRules.FirstLine(prompt.Trim()) : (step.ToolTitle ?? "排程計時");
            var tool = new AgentBackgroundTool(
                step.DisplayToolName ?? "排程計時",
                "other",
                title,
                step.ToolDetail);
            liveBackgroundTasks[key] = new BackgroundEntry(key, tool, IsOneShotTimer: isOneShot, IsDaemon: isDaemon);
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
                if (!removed)
                {
                    var matchKey = liveBackgroundTasks.Keys.FirstOrDefault(k => k.EndsWith(taskId, StringComparison.OrdinalIgnoreCase));
                    if (matchKey != null)
                        removed = liveBackgroundTasks.Remove(matchKey);
                    else if (liveBackgroundTasks.Count == 1)
                    {
                        var soleKey = liveBackgroundTasks.Keys.First();
                        if (soleKey.StartsWith("sched-", StringComparison.Ordinal) || soleKey.StartsWith("bg-", StringComparison.Ordinal))
                            removed = liveBackgroundTasks.Remove(soleKey);
                    }
                }
                if (removed)
                    EmitBackgroundWorkEvent(events);
            }
        }
    }

    private void EmitBackgroundWorkEvent(List<AgentEvent> events)
        => events.Add(new AgentBackgroundWorkEvent(
            liveBackgroundTasks.Count,
            liveBackgroundTasks.Values.Select(e => e.Tool).ToArray()));

    private static string? ExtractTaskKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = BackgroundTaskIdRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\btask-[A-Za-z0-9_-]+\b", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundTaskIdRegex();

    private void ParseResult(JObject payload, AntigravityParseResult result)
    {
        SyncTranscriptAndEmitThinking(result, int.MaxValue);

        foreach (var step in steps.Values.Where(s => s.DeferredStart && !s.Started))
            FlushDeferredToolStart(step, result.Events);

        // 這一輪結束：還沒定稿的文字定稿（沒收到任何文字步驟、只有 response 的，以 response 補一段）
        bool anyText = false;
        foreach (var step in steps.Values.Where(s => !s.IsTool))
        {
            if (step.Text.Length > 0)
                anyText = true;
            if (!step.TextCompleted && step.Text.Length > 0)
            {
                step.TextCompleted = true;
                result.Events.Add(new AgentTextCompletedEvent(step.Id, step.Text.ToString()));
            }
        }
        var response = Str(payload["response"]);
        if (!anyText && !string.IsNullOrWhiteSpace(response))
            result.Events.Add(new AgentTextCompletedEvent($"{idPrefix}-{turn}-response", response));
        var denied = AntigravityStepRules.DeniedActions(payload["denied_actions"] as JArray);
        bool commandDenied = denied.Any(a => string.Equals(a.Action, "command", StringComparison.OrdinalIgnoreCase));
        foreach (var step in steps.Values.Where(s => s.PendingCompletion))
        {
            step.PendingCompletion = false;
            step.PermissionDenied = commandDenied;
            result.Events.Add(commandDenied
                ? new AgentToolCompletedEvent(step.Id, AgentItemStatus.Failed, AntigravityStepRules.DeniedCommandOutput, Array.Empty<string>())
                : new AgentToolCompletedEvent(step.Id, AgentItemStatus.Done, null, Array.Empty<string>()));
        }
        foreach (var step in steps.Values.Where(s => s.IsTool && s.Started && !s.Done))
        {
            step.Done = true;
            AntigravityTranscriptRules.TryExtractArtifactMarkdown(step.ToolName, step.ArtifactArgs, step.FilePath, artifactFileReader, out var doneArtifact);
            if (step.PlanReview is { } review)
                pendingPlanReview = doneArtifact != null ? review with { InlineMarkdown = doneArtifact } : review;
            var mediaPaths = step.Kind != "read" && step.NumericIndex is { } idx
                ? CollectStepMediaPaths(idx)
                : Array.Empty<string>();
            result.Events.Add(new AgentToolCompletedEvent(step.Id, AgentItemStatus.Done, null, mediaPaths, doneArtifact));
        }

        var names = denied.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool newDenial = false;
        foreach (var name in names)
            newDenial |= noticedDenials.Add(name);
        if (names.Count > 0 && (newDenial || steps.Values.Any(s => s.PermissionDenied)))
            result.Events.Add(new AgentNoticeEvent(AntigravityStepRules.DeniedNotice(names), false));

        var status = Str(payload["status"]);
        var outcome = AntigravityStepRules.ClassifyResultStatus(status);
        var error = outcome == AgentTurnOutcome.Failed
            ? AntigravityStepRules.ErrorText(payload["error"]) is { Length: > 0 } e ? e : $"agy 回報這一輪失敗（{status ?? "沒有狀態"}）"
            : null;

        if (outcome == AgentTurnOutcome.Completed && pendingPlanReview is { } planReview)
        {
            var question = AntigravityStreamRules.BuildPlanFeedbackQuestion(planReview.Path, planReview.Summary);
            var localPlanPath = AntigravityTranscriptRules.TryResolveLocalPath(planReview.Path) ?? planReview.Path;
            var planMarkdown = (!string.IsNullOrWhiteSpace(localPlanPath) ? artifactFileReader(localPlanPath) : null) ?? planReview.InlineMarkdown;
            result.Events.Add(new AgentQuestionEvent(
                $"{idPrefix}-{turn}-plan",
                new[] { question },
                AnswerAsMessage: true,
                Detail: planMarkdown,
                FilePath: localPlanPath));
            pendingPlanReview = null;
        }
        else if (outcome == AgentTurnOutcome.Failed && AntigravityStreamRules.IsQuotaExhausted(error))
        {
            result.Events.Add(new AgentQuotaExhaustedEvent(AgentProvider.Antigravity, null));
        }

        // agy 在送出單輪 result 前已等完並清理所有非 IsDaemon 的背景指令，但若使用者主動結束回合（無後續背景任務），這裡自動收斂殘留的非常駐背景項目
        if (liveBackgroundTasks.Count > 0)
        {
            var nonDaemonKeys = liveBackgroundTasks.Values.Where(e => !e.IsDaemon && !e.Key.StartsWith("sched-", StringComparison.Ordinal)).Select(e => e.Key).ToList();
            if (nonDaemonKeys.Count > 0)
            {
                foreach (var k in nonDaemonKeys)
                    liveBackgroundTasks.Remove(k);
                EmitBackgroundWorkEvent(result.Events);
            }
        }

        // API 伺服器暫時無法服務（503 UNAVAILABLE）或底層網路連線瞬斷而失敗：中樞等一下自動重試（AgentHub.OverloadRetry.cs）
        var busy = outcome == AgentTurnOutcome.Failed ? AntigravityStreamRules.ClassifyServerBusy(error) : AgentServerBusy.None;
        var turnUsage = TurnUsage(payload);
        // 沒有任何步驟帶 usage、只有 result 的累計（對話的第一輪）：這一輪的用量只在這裡，自己發增量（中樞不再以一輪的用量補差額）
        if (stepUsage.Count == 0 && turnUsage != null)
            result.Events.Add(new AgentTokenUsageEvent(turnUsage with { CostUsd = null }, tokenModel));
        result.Events.Add(new AgentTurnCompletedEvent(outcome, error, turnUsage) { ServerBusy = busy });
        result.TurnCompleted = true;
        stepUsage.Clear();
    }

    /// <summary>
    /// 這一輪的用量：各步驟 usage 相加。沒有任何步驟帶 usage 時，只有對話的第一輪（num_turns＝1）能直接用 result 的累計值，其他回 null
    /// </summary>
    private AgentUsage? TurnUsage(JObject payload)
    {
        if (stepUsage.Count > 0)
        {
            return new AgentUsage(
                stepUsage.Values.Sum(u => u.InputTokens),
                stepUsage.Values.Sum(u => u.OutputTokens),
                stepUsage.Values.Sum(u => u.CachedInputTokens),
                null);
        }
        if (payload["usage"] is JObject usage && Str(payload["num_turns"]) == "1")
        {
            var total = AntigravityStepRules.ToUsage(usage);
            return total.InputTokens + total.OutputTokens > 0 ? total : null;
        }
        return null;
    }

    private static string? Str(JToken? token) => token is JValue { Value: not null } v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : null;
}

internal enum AntigravityStepState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>agy 步驟與結果的分類規則（純函式）</summary>
internal static class AntigravityStepRules
{
    /// <summary>步驟狀態（實測 ACTIVE／DONE／ERROR；大小寫與前綴不拘，也認 STEP_STATE_DONE、COMPLETED、CANCELED…）</summary>
    public static AntigravityStepState ClassifyState(string? state)
    {
        var s = (state ?? string.Empty).ToUpperInvariant();
        if (s.Contains("ERROR") || s.Contains("FAIL"))
            return AntigravityStepState.Failed;
        if (s.Contains("CANCEL") || s.Contains("INTERRUPT"))
            return AntigravityStepState.Cancelled;
        if (s.Contains("DONE") || s.Contains("COMPLETE") || s.Contains("SUCCESS") || s.Contains("FINISH"))
            return AntigravityStepState.Completed;
        return AntigravityStepState.Running;
    }

    /// <summary>不顯示的步驟：使用者輸入的回顯、續接時的系統訊息</summary>
    public static bool IsHiddenStep(string? stepType)
        => string.Equals(stepType, "user_input", StringComparison.OrdinalIgnoreCase)
           || string.Equals(stepType, "system_message", StringComparison.OrdinalIgnoreCase);

    /// <summary>result 的 status：SUCCESS＝完成；CANCELED／INTERRUPTED＝已停止；其他（ERROR、INVALID…）＝失敗</summary>
    public static AgentTurnOutcome ClassifyResultStatus(string? status) => (status ?? string.Empty).ToUpperInvariant() switch
    {
        "SUCCESS" => AgentTurnOutcome.Completed,
        "CANCELED" or "CANCELLED" or "INTERRUPTED" => AgentTurnOutcome.Interrupted,
        _ => AgentTurnOutcome.Failed,
    };

    /// <summary>
    /// agy 的 usage → AgentUsage：input_tokens 不含 cache_read_tokens（total_tokens＝input＋output），
    /// 照 Claude 的慣例輸入＝兩者相加、快取另列；輸出含 thinking_tokens
    /// </summary>
    public static AgentUsage ToUsage(JObject usage)
    {
        long cached = Long(usage["cache_read_tokens"]);
        return new AgentUsage(Long(usage["input_tokens"]) + cached, Long(usage["output_tokens"]) + Long(usage["thinking_tokens"]), cached, null);
    }

    /// <summary>錯誤欄位：字串（result 的 error）或帶 message 的物件（tool_info.error）</summary>
    public static string? ErrorText(JToken? error) => error switch
    {
        JValue { Value: not null } v => Convert.ToString(v.Value, CultureInfo.InvariantCulture),
        JObject o => Convert.ToString((o["message"] as JValue)?.Value, CultureInfo.InvariantCulture),
        _ => null,
    };

    public static string? ToolError(JObject? info) => ErrorText(info?["error"]) is { Length: > 0 } e ? e : null;

    /// <summary>工具因權限被自動拒絕（實測：permission check failed for command "…": user denied permission to run command）</summary>
    public static bool IsPermissionDenial(string? error)
        => error != null && (error.Contains("permission check failed", StringComparison.OrdinalIgnoreCase)
                             || error.Contains("denied permission", StringComparison.OrdinalIgnoreCase));

    /// <summary>denied_actions：(action, 顯示名稱)，顯示名稱是 display_name、沒有就用 action；兩者都沒有的略過</summary>
    public static IReadOnlyList<(string Action, string Name)> DeniedActions(JArray? denied)
    {
        var result = new List<(string, string)>();
        if (denied == null)
            return result;
        foreach (var item in denied.OfType<JObject>())
        {
            var action = Convert.ToString((item["action"] as JValue)?.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            var name = Convert.ToString((item["display_name"] as JValue)?.Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name))
                name = action;
            if (!string.IsNullOrWhiteSpace(name))
                result.Add((action, name));
        }
        return result;
    }

    /// <summary>被拒、但 agy 只報 DONE 沒附訊息的指令：工具卡的輸出</summary>
    public const string DeniedCommandOutput = "agy 自動拒絕了這個指令（需要核准，但在這裡無法詢問），沒有執行";

    public static string DeniedNotice(IReadOnlyList<string> names)
        => $"agy 自動拒絕了需要核准的操作（{string.Join("、", names)}）：Antigravity 在這裡無法於對話中詢問。要讓代理執行指令，請把權限模式改成「自動（沙箱）」或「完全自動」。";

    public sealed record ToolDescription(string Name, string Kind, string Title, string? Detail, string? FilePath);

    /// <summary>
    /// 已知工具 → 畫面名稱與種類（決定圖示）：init 的 tools 清單（1.3.5），加上 1.4.6 無頭模式實際的步驟名稱 read_file、write_file、edit_file
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Kind)> KnownTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["run_command"] = ("執行指令", "bash"),
        ["command_status"] = ("查看指令狀態", "bash"),
        ["send_command_input"] = ("傳送指令輸入", "bash"),
        ["manage_task"] = ("管理背景工作", "other"),
        ["view_file"] = ("讀取檔案", "read"),
        ["read_file"] = ("讀取檔案", "read"),
        ["read_resource"] = ("讀取資源", "read"),
        ["write_to_file"] = ("寫入檔案", "write"),
        ["write_file"] = ("寫入檔案", "write"),
        ["replace_file_content"] = ("修改檔案", "edit"),
        ["edit_file"] = ("修改檔案", "edit"),
        ["multi_replace_file_content"] = ("修改檔案", "edit"),
        ["sed_file"] = ("修改檔案", "edit"),
        ["notebook_edit"] = ("修改筆記本", "edit"),
        ["list_dir"] = ("列出資料夾", "search"),
        ["find_by_name"] = ("尋找檔案", "search"),
        ["grep_search"] = ("搜尋內容", "search"),
        ["search_web"] = ("網路搜尋", "web"),
        ["read_url_content"] = ("讀取網頁", "web"),
        ["open_browser_url"] = ("開啟網頁", "web"),
        ["read_browser_page"] = ("讀取網頁", "web"),
        ["invoke_subagent"] = ("子代理", "agent"),
        ["define_subagent"] = ("定義子代理", "agent"),
        ["browser_subagent"] = ("瀏覽器子代理", "agent"),
        ["manage_subagents"] = ("管理子代理", "agent"),
        ["send_message"] = ("傳送代理訊息", "agent"),
        ["schedule"] = ("排程計時", "other"),
        ["call_mcp_tool"] = ("MCP 工具", "mcp"),
        ["list_resources"] = ("MCP 資源", "mcp"),
        ["generate_image"] = ("產生圖片", "image"),
    };

    /// <summary>大內容參數不在工具卡「輸入」重複印出（已由紅綠 Diff 呈現）</summary>
    private static readonly string[] DiffBodyParameters =
    [
        "AbsolutePath", "TargetFile", "FilePath", "File", "Path",
        "Content", "CodeContent", "Target", "TargetContent", "Replacement", "ReplacementContent", "ReplacementChunks",
    ];

    /// <summary>工具名稱 → 畫面上的名稱、種類、一行摘要（指令、路徑、子代理角色、查詢…）與細節</summary>
    public static ToolDescription DescribeTool(string? toolName, JObject? info, JObject? subagentInfo = null, bool hasDiff = false)
    {
        var key = toolName ?? (subagentInfo != null ? "invoke_subagent" : string.Empty);
        var (name, kind) = KnownTools.TryGetValue(key, out var known) ? known : GuessTool(key);
        var parameters = info?["parameters"] as JObject;

        if (kind == "agent")
        {
            var role = FirstParameter(parameters, "Role", "TaskName", "Name");
            var subType = FirstParameter(parameters, "TypeName") ?? FirstParameter(subagentInfo, "type", "agent_type");
            var prompt = FirstParameter(parameters, "Prompt", "Task", "Message", "Description");
            var agentTitle = !string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(subType)
                ? $"{role}（{subType}）"
                : role ?? subType ?? prompt ?? (string.IsNullOrEmpty(toolName) ? name : toolName);
            var agentDetail = !string.IsNullOrWhiteSpace(prompt)
                ? ClaudeStreamRules.Truncate(prompt, ClaudeStreamRules.MaxToolDetailLength)
                : parameters is { Count: > 0 }
                    ? ClaudeStreamRules.Truncate(parameters.ToString(Formatting.Indented), ClaudeStreamRules.MaxToolDetailLength)
                    : null;
            var logUri = FirstParameter(subagentInfo, "log_uri", "logUri", "logAbsoluteUri");
            var localLog = AntigravityTranscriptRules.TryResolveLocalPath(logUri);
            var agentFilePath = localLog != null && File.Exists(localLog) ? localLog : null;
            return new ToolDescription(name, kind, ClaudeStreamRules.FirstLine(agentTitle), agentDetail, agentFilePath);
        }

        var path = FirstParameter(parameters, PathParameters);
        var command = FirstParameter(parameters, "CommandLine", "Command");
        var query = FirstParameter(parameters, "Query", "Pattern", "SearchPattern", "Url", "DirectoryPath", "SearchPath", "SearchDirectory");
        var title = command ?? path ?? query ?? FirstParameter(parameters, "Summary", "Description", "Title", "Prompt", "Task", "toolSummary")
            ?? (string.IsNullOrEmpty(toolName) ? name : toolName);
        var filePath = kind is "read" or "write" or "edit" ? path : null;
        // 檔案工具的路徑已是標題與連結、有紅綠 Diff 時編輯內容也已畫出：細節的參數 JSON 拿掉這些欄位，不重複塞滿整段檔案
        var stripNames = hasDiff && kind is "write" or "edit" ? DiffBodyParameters : PathParameters;
        var shown = filePath != null && parameters != null ? WithoutParameters(parameters, stripNames) : parameters;
        var detail = command ?? (shown is { Count: > 0 } ? ClaudeStreamRules.Truncate(shown.ToString(Formatting.Indented), ClaudeStreamRules.MaxToolDetailLength) : null);
        return new ToolDescription(name, kind, ClaudeStreamRules.FirstLine(title), detail, filePath);
    }

    /// <summary>沒列在表上的工具：依名稱猜種類（browser_*、*_subagent…），名稱照原文</summary>
    private static (string Name, string Kind) GuessTool(string key)
    {
        var k = key.ToLowerInvariant();
        var kind = k switch
        {
            _ when k.Contains("subagent") => "agent",
            _ when k.Contains("browser") || k.Contains("url") || k.Contains("web") => "web",
            _ when k.Contains("command") || k.Contains("shell") || k.Contains("terminal") => "bash",
            _ when k.Contains("mcp") => "mcp",
            _ when k.Contains("search") || k.Contains("find") || k.Contains("grep") || k.Contains("list_dir") => "search",
            _ when k.Contains("replace") || k.Contains("edit") => "edit",
            _ when k.Contains("write") => "write",
            _ when k.Contains("view") || k.Contains("read") => "read",
            _ when k.Contains("image") => "image",
            _ => "other",
        };
        return (key.Length == 0 ? "工具" : key, kind);
    }

    /// <summary>工具結束時給畫面看的輸出：tool_info.output（CRLF 換成 LF），沒有就用錯誤訊息；有 subagent_info 時附上子代理對話與軌跡摘要</summary>
    public static string? DescribeToolOutput(JObject? info, JObject? subagentInfo = null)
    {
        var output = info?["output"] is JValue { Value: not null } v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : null;
        if (string.IsNullOrEmpty(output))
            output = ToolError(info);
        var normalized = string.IsNullOrEmpty(output) ? null : output.Replace("\r\n", "\n");
        var subNote = FormatSubagentInfo(subagentInfo);
        if (subNote == null)
            return normalized;
        return string.IsNullOrEmpty(normalized) ? subNote : normalized + "\n\n" + subNote;
    }

    /// <summary>整理 step_update.subagent_info（conversation_id、usage、log_uri 轉本機路徑）供工具卡顯示</summary>
    public static string? FormatSubagentInfo(JObject? subagentInfo)
    {
        if (subagentInfo == null || subagentInfo.Count == 0)
            return null;
        var parts = new List<string>();
        var convId = FirstParameter(subagentInfo, "conversation_id", "conversationId");
        if (!string.IsNullOrWhiteSpace(convId))
            parts.Add($"子代理對話：{convId}");

        if (subagentInfo["usage"] is JObject usageObj)
        {
            long total = Long(usageObj["total_tokens"]);
            if (total <= 0)
                total = Long(usageObj["input_tokens"]) + Long(usageObj["output_tokens"]) + Long(usageObj["thinking_tokens"]);
            if (total > 0)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"耗用 {total:N0} tokens"));
        }

        var logUri = FirstParameter(subagentInfo, "log_uri", "logUri");
        if (!string.IsNullOrWhiteSpace(logUri))
        {
            var localLog = AntigravityTranscriptRules.TryResolveLocalPath(logUri) ?? logUri;
            parts.Add($"軌跡：{localLog}");
        }

        return parts.Count > 0 ? string.Join("・", parts) : null;
    }

    /// <summary>檔案路徑的參數名稱（標題與檔案連結取第一個有值的）</summary>
    private static readonly string[] PathParameters = ["AbsolutePath", "TargetFile", "FilePath", "File", "Path"];

    /// <summary>參數名稱比對：忽略大小寫與底線（PascalCase 與 snake_case 都認）</summary>
    private static bool IsParameter(string property, string name)
        => string.Equals(property.Replace("_", string.Empty), name.Replace("_", string.Empty), StringComparison.OrdinalIgnoreCase);

    /// <summary>拿掉這些參數之後的複本（原本的不動）</summary>
    private static JObject WithoutParameters(JObject parameters, IReadOnlyList<string> names)
    {
        var copy = (JObject)parameters.DeepClone();
        foreach (var property in copy.Properties().Where(p => names.Any(n => IsParameter(p.Name, n))).ToList())
            property.Remove();
        return copy;
    }

    /// <summary>參數（PascalCase，也接受 snake_case 與大小寫不同的寫法）：比對時忽略大小寫與底線</summary>
    private static string? FirstParameter(JObject? parameters, params string[] names)
    {
        if (parameters == null)
            return null;
        foreach (var name in names)
        {
            foreach (var property in parameters.Properties())
            {
                if (IsParameter(property.Name, name)
                    && property.Value is JValue { Value: not null } v
                    && Convert.ToString(v.Value, CultureInfo.InvariantCulture) is { Length: > 0 } s)
                    return s;
            }
        }
        return null;
    }

    private static long Long(JToken? token)
        => token is JValue { Value: not null } v && long.TryParse(Convert.ToString(v.Value, CultureInfo.InvariantCulture), out var n) ? n : 0;
}
