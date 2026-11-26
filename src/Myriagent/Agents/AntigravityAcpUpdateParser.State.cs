using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>解析器的狀態（放開程序時帶著走，代理主機 H2）：只加欄位</summary>
internal sealed class AntigravityParserState
{
    public string? Prefix { get; set; }

    public int BlockSequence { get; set; }

    public int Turn { get; set; }

    public string? TextBlock { get; set; }

    public string? Text { get; set; }

    public string? ThoughtBlock { get; set; }

    public bool IsPlanMode { get; set; }

    public string? LastGeneratedTitle { get; set; }

    public Dictionary<string, AntigravityParserTool> Tools { get; set; } = new();

    public List<AntigravityParserBackground> Background { get; set; } = new();

    public AntigravityParserPlanReview? PendingPlanReview { get; set; }
}

internal sealed class AntigravityParserTool
{
    public JObject? Call { get; set; }

    public bool Announced { get; set; }

    public bool Hidden { get; set; }

    public bool EmittedPlanSteps { get; set; }

    public bool Finished { get; set; }

    public string? Kind { get; set; }

    public string? ToolName { get; set; }

    public string? DisplayName { get; set; }

    public string? Title { get; set; }

    public string? Detail { get; set; }

    public string? FilePath { get; set; }

    public JObject? Args { get; set; }

    public AntigravityParserPlanReview? PlanReview { get; set; }

    public string? Output { get; set; }
}

internal sealed class AntigravityParserPlanReview
{
    public string? Path { get; set; }

    public string? Summary { get; set; }

    public string? InlineMarkdown { get; set; }
}

internal sealed class AntigravityParserBackground
{
    public string Key { get; set; } = string.Empty;

    public AgentBackgroundTool? Tool { get; set; }

    public bool IsOneShotTimer { get; set; }

    public bool IsDaemon { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
}

internal sealed partial class AntigravityAcpUpdateParser
{
    /// <summary>
    /// 解析器現在的狀態（app 更新時放開程序、新的 app 接回，代理主機 H2）：區塊與輪次的編號、開著的文字區塊與到目前為止的文字、這一輪的工具、
    /// 背景工作的登記（含單次計時器的到期時間）、等確認的計畫。ACP 的更新沒有跨行累積的串流狀態，直接匯出就夠，不必重播
    /// </summary>
    public AntigravityParserState ExportState() => new()
    {
        Prefix = prefix,
        BlockSequence = blockSequence,
        Turn = turn,
        TextBlock = textBlock,
        Text = text.ToString(),
        ThoughtBlock = thoughtBlock,
        IsPlanMode = isPlanMode,
        LastGeneratedTitle = LastGeneratedTitle,
        Tools = tools.ToDictionary(t => t.Key, t => new AntigravityParserTool
        {
            Call = (JObject)t.Value.Call.DeepClone(),
            Announced = t.Value.Announced,
            Hidden = t.Value.Hidden,
            EmittedPlanSteps = t.Value.EmittedPlanSteps,
            Finished = t.Value.Finished,
            Kind = t.Value.Kind,
            ToolName = t.Value.ToolName,
            DisplayName = t.Value.DisplayName,
            Title = t.Value.Title,
            Detail = t.Value.Detail,
            FilePath = t.Value.FilePath,
            Args = (JObject?)t.Value.Args?.DeepClone(),
            PlanReview = Export(t.Value.PlanReview),
            Output = t.Value.Output,
        }, StringComparer.Ordinal),
        Background = liveBackgroundTasks.Values.Select(b => new AntigravityParserBackground
        {
            Key = b.Key,
            Tool = b.Tool,
            IsOneShotTimer = b.IsOneShotTimer,
            IsDaemon = b.IsDaemon,
            ExpiresAtUtc = b.ExpiresAtUtc,
        }).ToList(),
        PendingPlanReview = Export(pendingPlanReview),
    };

    /// <summary>照 <see cref="ExportState"/> 的內容還原（新的 app 接回放開的程序）</summary>
    public static AntigravityAcpUpdateParser FromState(AntigravityParserState state, Func<string, string?>? transcriptPathResolver = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var parser = new AntigravityAcpUpdateParser(string.IsNullOrEmpty(state.Prefix) ? "agy" : state.Prefix, transcriptPathResolver)
        {
            blockSequence = Math.Max(0, state.BlockSequence),
            turn = Math.Max(0, state.Turn),
            textBlock = state.TextBlock,
            thoughtBlock = state.ThoughtBlock,
            isPlanMode = state.IsPlanMode,
            pendingPlanReview = Restore(state.PendingPlanReview),
            LastGeneratedTitle = state.LastGeneratedTitle,
        };
        parser.text.Append(state.Text ?? string.Empty);
        foreach (var (id, tool) in state.Tools ?? new Dictionary<string, AntigravityParserTool>())
        {
            if (string.IsNullOrEmpty(id) || tool == null)
                continue;
            parser.tools[id] = new ToolState
            {
                Call = tool.Call ?? new JObject(),
                Announced = tool.Announced,
                Hidden = tool.Hidden,
                EmittedPlanSteps = tool.EmittedPlanSteps,
                Finished = tool.Finished,
                Kind = string.IsNullOrEmpty(tool.Kind) ? "other" : tool.Kind,
                ToolName = tool.ToolName,
                DisplayName = tool.DisplayName,
                Title = tool.Title,
                Detail = tool.Detail,
                FilePath = tool.FilePath,
                Args = tool.Args,
                PlanReview = Restore(tool.PlanReview),
                Output = tool.Output ?? string.Empty,
            };
        }
        foreach (var b in state.Background ?? new List<AntigravityParserBackground>())
        {
            if (b?.Tool == null || string.IsNullOrEmpty(b.Key))
                continue;
            parser.liveBackgroundTasks[b.Key] = new BackgroundEntry(b.Key, b.Tool, b.IsOneShotTimer, b.IsDaemon,
                b.ExpiresAtUtc is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null);
        }
        return parser;
    }

    /// <summary>背景工作的清單事件（接回程序時先回報，中樞才知道哪些還在跑）；沒有回 null</summary>
    public AgentBackgroundWorkEvent? SnapshotBackgroundWork()
    {
        var events = new List<AgentEvent>();
        if (liveBackgroundTasks.Count == 0)
            return null;
        EmitBackgroundWorkEvent(events);
        return events.OfType<AgentBackgroundWorkEvent>().LastOrDefault();
    }

    private static AntigravityParserPlanReview? Export(PlanReviewCandidate? review)
        => review == null ? null : new AntigravityParserPlanReview { Path = review.Path, Summary = review.Summary, InlineMarkdown = review.InlineMarkdown };

    private static PlanReviewCandidate? Restore(AntigravityParserPlanReview? review)
        => review == null ? null : new PlanReviewCandidate(review.Path, review.Summary, review.InlineMarkdown);
}
