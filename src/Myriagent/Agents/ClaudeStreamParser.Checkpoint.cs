namespace Myriagent.Agents;

/// <summary>
/// Claude 解析器在「一輪的 result 之後」的跨輪狀態（代理主機 H2，設計 docs/plans/2026-11-26-agent-host.md 第 10 節）：
/// 接回程序時，新的解析器從這份狀態起、把檢查點之後的 stdout 重播一遍，就回到放開當下的狀態——一輪之內的狀態（訊息區塊、工具、用量）
/// 都由重播重建，不必一項一項帶。寫進 resume.json：只加欄位，舊版忽略不認得的。
/// 新增「跨過 result 還要用」的解析器欄位時要加進來（沒加的後果：接回後那一項從空的開始）
/// </summary>
internal sealed class ClaudeParserCheckpoint
{
    /// <summary>存活的背景工作（task_id，依加入順序）、各是哪個工具開始的、種類</summary>
    public List<string> BackgroundTasks { get; set; } = new();

    public Dictionary<string, AgentBackgroundTool> TaskTools { get; set; } = new();

    public Dictionary<string, string> TaskTypes { get; set; } = new();

    /// <summary>前景指令的 task_id（結束時的通知不算背景工作完成）</summary>
    public List<string> ForegroundTasks { get; set; } = new();

    /// <summary>待辦清單（TaskCreate／TaskUpdate 累積的，依建立順序）</summary>
    public List<ClaudeTodoCheckpoint> Todos { get; set; } = new();

    /// <summary>上一則 result 的 modelUsage（程序的累計）；null＝這個程序還沒有基準，空的＝新的 session 從 0 起算</summary>
    public Dictionary<string, long[]>? LastModelUsage { get; set; }

    public string? ContextModel { get; set; }

    public long? LastContextTokens { get; set; }

    public string? ReplyModel { get; set; }

    /// <summary>主對話畫了卡的 Agent 工具（背景子代理的過程在一輪結束後還會接在它們的輸出）</summary>
    public List<string> AgentCards { get; set; } = new();

    public Dictionary<string, string> SubagentRoots { get; set; } = new();

    public Dictionary<string, ClaudeSubagentLogCheckpoint> SubagentLogs { get; set; } = new();

    public bool ApiKeyNoticeShown { get; set; }
}

internal sealed class ClaudeTodoCheckpoint
{
    public string Id { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string ActiveForm { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}

internal sealed class ClaudeSubagentLogCheckpoint
{
    public List<string> Lines { get; set; } = new();

    public int Total { get; set; }
}

internal sealed partial class ClaudeStreamParser
{
    /// <summary>現在的跨輪狀態（會話在每則 result 處理完之後叫：這時一輪之內的狀態都收尾了）</summary>
    public ClaudeParserCheckpoint ExportCheckpoint()
    {
        var background = ExportBackground();
        return new ClaudeParserCheckpoint
        {
            BackgroundTasks = background.Tasks,
            TaskTools = background.Tools,
            TaskTypes = background.Types,
            ForegroundTasks = foregroundTasks.ToList(),
            Todos = tasks.Select(kv => new ClaudeTodoCheckpoint { Id = kv.Key, Content = kv.Value.Content, ActiveForm = kv.Value.ActiveForm, Status = kv.Value.Status }).ToList(),
            LastModelUsage = CloneUsage(lastModelUsage),
            ContextModel = contextModel,
            LastContextTokens = lastContextTokens,
            ReplyModel = replyModel,
            AgentCards = agentCardIds.ToList(),
            SubagentRoots = new Dictionary<string, string>(subagentRoots, StringComparer.Ordinal),
            SubagentLogs = subagentLogs.ToDictionary(kv => kv.Key, kv => new ClaudeSubagentLogCheckpoint { Lines = kv.Value.Lines.ToList(), Total = kv.Value.Total }, StringComparer.Ordinal),
            ApiKeyNoticeShown = ApiKeyNoticeShown,
        };
    }

    /// <summary>從檢查點建立解析器（接回程序）：之後把檢查點之後的 stdout 一行一行交給 Parse 重播，再讀新的</summary>
    public static ClaudeStreamParser FromCheckpoint(ClaudeParserCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var parser = new ClaudeStreamParser { ApiKeyNoticeShown = checkpoint.ApiKeyNoticeShown };
        parser.RestoreBackground(checkpoint.BackgroundTasks, checkpoint.TaskTools, checkpoint.TaskTypes);
        // RestoreBackground 為了和 H1 的格式一致把工具的 TaskId／StopId 清掉；BackgroundTools 回報時會照 task_id 補回
        foreach (var id in checkpoint.ForegroundTasks ?? new List<string>())
        {
            if (!string.IsNullOrEmpty(id))
                parser.foregroundTasks.Add(id);
        }
        foreach (var todo in checkpoint.Todos ?? new List<ClaudeTodoCheckpoint>())
        {
            if (todo is { Id.Length: > 0 })
                parser.tasks[todo.Id] = new TaskEntry(todo.Content ?? string.Empty, todo.ActiveForm ?? string.Empty, todo.Status ?? string.Empty);
        }
        parser.lastModelUsage = CloneUsage(checkpoint.LastModelUsage);
        parser.contextModel = checkpoint.ContextModel;
        parser.lastContextTokens = checkpoint.LastContextTokens;
        parser.replyModel = checkpoint.ReplyModel;
        foreach (var id in checkpoint.AgentCards ?? new List<string>())
        {
            if (!string.IsNullOrEmpty(id))
                parser.agentCardIds.Add(id);
        }
        foreach (var (child, root) in checkpoint.SubagentRoots ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrEmpty(child) && !string.IsNullOrEmpty(root))
                parser.subagentRoots[child] = root;
        }
        foreach (var (card, log) in checkpoint.SubagentLogs ?? new Dictionary<string, ClaudeSubagentLogCheckpoint>())
        {
            if (string.IsNullOrEmpty(card) || log == null)
                continue;
            var restored = new SubagentLog { Total = log.Total };
            foreach (var line in log.Lines ?? new List<string>())
                restored.Lines.Enqueue(line);
            parser.subagentLogs[card] = restored;
        }
        return parser;
    }

    /// <summary>
    /// 上一則 result 之後已記過的 token（放開程序當下的值）：含子代理逐字稿讀到的（AddSubagentUsage）——那部分不在 stdout 裡，重播重建不出來；
    /// 接回的解析器重播完以它為準，這一輪的 result 才不會把已記過的再補一次
    /// </summary>
    public Dictionary<string, long[]> ExportCounted() => CloneUsage(countedSinceResult) ?? new Dictionary<string, long[]>(StringComparer.Ordinal);

    public void RestoreCounted(IReadOnlyDictionary<string, long[]>? counted)
    {
        if (counted == null)
            return;
        countedSinceResult.Clear();
        foreach (var (model, fields) in counted)
        {
            if (!string.IsNullOrEmpty(model) && fields != null)
                countedSinceResult[model] = (long[])fields.Clone();
        }
    }

    private static Dictionary<string, long[]>? CloneUsage(IReadOnlyDictionary<string, long[]>? usage)
        => usage?.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => (long[])kv.Value.Clone(), StringComparer.Ordinal);
}
