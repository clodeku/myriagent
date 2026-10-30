using Myriagent.Agents;
using Myriagent.Rendering;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 背景工作的顯示（AgentHub.Background.cs）：標頭的 background、完成通知的標題與 running.json 列出的工作（AgentHub.InterruptedTurns.cs）。
/// 背景工作是代理以 run_in_background 開始的指令或背景子代理；這一輪結束了它們還在跑，完成後 CLI 會自己開一輪告訴模型
/// </summary>
internal static class BackgroundWorkRules
{
    /// <summary>標頭最多列幾個背景工作的說明（數量另外給，網頁寫「等 N 個背景工作回報」）</summary>
    internal const int MaxTasks = 5;

    /// <summary>
    /// 標頭的 <c>background {count, startedAt, tasks:[{key, tool, title, startedAt, taskId?, stopId?}]}</c>：count 是存活的背景工作數；tasks 是對到工具的那些（開始的順序、
    /// 同一個說明只列一次、最多 <see cref="MaxTasks"/> 個），tool 是工具的顯示名稱（執行指令、子代理…），title 是一行摘要（TextRules.FirstLine），
    /// 沒有摘要的是 null。startedAt 是開始的時間（Unix 毫秒，網頁顯示已經跑了多久；starts 與 tools 同順序，見 <see cref="Starts"/>）：
    /// 工作的是它的開始時間（同一個說明的取最早的），外層的是最早開始、還在跑的那一個（列不下的也算；都對不到工具時用 since），不明是 null。
    /// count 小於 1 回 null（沒有背景工作）
    /// </summary>
    public static JObject? View(int count, IReadOnlyList<AgentBackgroundTool> tools, IReadOnlyList<DateTime>? starts = null, DateTime? since = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (count < 1)
            return null;
        var tasks = new JArray();
        var listed = new Dictionary<string, JObject>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTime? earliest = null;
        for (int i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            DateTime? start = starts != null && i < starts.Count ? starts[i] : null;
            if (start is { } s && (earliest == null || s < earliest))
                earliest = s;
            var title = TextRules.FirstLine(tool.Title);
            var key = tool.ToolName + "\n" + title;
            if (!seen.Add(key))
            {
                // 同一個說明只列一次：開始時間取最早的
                if (listed.TryGetValue(key, out var task) && start is { } later && (task.Value<long?>("startedAt") is not { } ms || ProtocolValues.ToUnixMs(later) < ms))
                    task["startedAt"] = ProtocolValues.ToUnixMs(later);
                continue;
            }
            // 列滿了照樣看完：外層的開始時間要算到列不下的
            if (tasks.Count == MaxTasks)
                continue;
            var item = new JObject
            {
                // 「詳細」以它問這一個（taskDetail；BackgroundTaskDetailRules.Key 和這裡的「同一個說明」用同一組欄位）
                ["key"] = BackgroundTaskDetailRules.Key(tool),
                ["tool"] = tool.ToolName,
                ["title"] = title.Length == 0 ? null : title,
                ["startedAt"] = ProtocolValues.ToUnixMs(start),
            };
            // 能以 get_task_output 看輸出的（Claude 的背景指令與 Monitor）
            if (!string.IsNullOrEmpty(tool.TaskId))
                item["taskId"] = tool.TaskId;
            // 能單獨停止的（Claude 的 task_id、Codex 的工具卡 id）：網頁的「停止」以它送 taskStop；沒有的只能全部停止
            if (!string.IsNullOrEmpty(tool.StopId))
                item["stopId"] = tool.StopId;
            tasks.Add(item);
            listed[key] = item;
        }
        return new JObject
        {
            ["count"] = count,
            ["startedAt"] = ProtocolValues.ToUnixMs(earliest ?? since),
            ["tasks"] = tasks,
        };
    }

    /// <summary>
    /// 各背景工作的開始時間（和 tools 同順序，給 <see cref="View"/>）：上一次回報就有的沿用（previous 與 previousStarts 同順序；說明相同的依序對上，
    /// 重複的各對各的）；新出現的用它的工具卡建立的時間（cardStart 給第幾個工具的卡片時間、對不到卡片回 null：Codex 的指令讓出給模型之後才算背景工作，
    /// 一輪結束時才回報的可能早就開始了），沒有卡片、或卡片時間比現在晚就是現在（會話回報的時候，Claude 的 run_in_background 開始時就回報）
    /// </summary>
    public static List<DateTime> Starts(IReadOnlyList<AgentBackgroundTool> previous, IReadOnlyList<DateTime> previousStarts,
        IReadOnlyList<AgentBackgroundTool> tools, DateTime now, Func<int, DateTime?>? cardStart = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(previousStarts);
        ArgumentNullException.ThrowIfNull(tools);
        var used = new bool[previous.Count];
        var starts = new List<DateTime>(tools.Count);
        for (int i = 0; i < tools.Count; i++)
        {
            int match = -1;
            for (int k = 0; k < previous.Count && k < previousStarts.Count; k++)
            {
                if (!used[k] && previous[k] == tools[i])
                {
                    match = k;
                    break;
                }
            }
            if (match >= 0)
            {
                used[match] = true;
                starts.Add(previousStarts[match]);
                continue;
            }
            starts.Add(cardStart?.Invoke(i) is { } card && card <= now ? card : now);
        }
        return starts;
    }

    /// <summary>
    /// running.json 與接續訊息列出的背景工作：「執行指令：npm test」（沒有摘要就只有工具名稱），開始的順序、同一個說明只列一次、
    /// 最多 <see cref="MaxTasks"/> 個
    /// </summary>
    public static List<string> Labels(IEnumerable<AgentBackgroundTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var labels = new List<string>();
        foreach (var tool in tools)
        {
            var name = string.IsNullOrWhiteSpace(tool.ToolName) ? "工具" : tool.ToolName.Trim();
            var title = TextRules.FirstLine(tool.Title);
            var label = title.Length == 0 ? name : $"{name}：{title}";
            if (labels.Contains(label, StringComparer.Ordinal))
                continue;
            labels.Add(label);
            if (labels.Count == MaxTasks)
                break;
        }
        return labels;
    }

    /// <summary>
    /// 使用者強制停止背景工作之後對話裡的提示（noticeKind backgroundTask；AgentHub.TaskStop.cs）：「已強制停止背景工作：執行指令：npm test（代理不會自動接著處理）。」
    /// count 是停止的背景工作數（全部停止時是標頭的 count，可能比對到工具的多），tools 是它們之中對到工具的
    /// </summary>
    public static string StoppedNotice(int count, IReadOnlyList<AgentBackgroundTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var labels = Labels(tools);
        var head = count > 1 ? $"已強制停止 {count} 個背景工作" : "已強制停止背景工作";
        var list = labels.Count == 0 ? "" : "：" + string.Join("、", labels) + (count > labels.Count ? " 等" : "");
        return head + list + "（代理不會自動接著處理）。";
    }

    /// <summary>
    /// 一輪正常結束時的通知標題：背景工作還在跑就不是「已完成」（代理回一句「等測試跑完」就結束這一輪，完成後 CLI 自己接著開的那一輪結束時才是完成）。
    /// 照樣通知：背景跑的可能是一直不會結束的開發伺服器，代理的回覆（網址、說明）使用者要看得到
    /// </summary>
    public static string CompletedTitle(bool backgroundWork) => backgroundWork ? "代理已回覆，背景工作還在執行" : "代理已完成";

    // ───── Codex 的背景指令跑完時接著做（Codex 不會自己開一輪；protocol-notes「Codex 的背景指令」） ─────

    /// <summary>沒有使用者送出的訊息之間，最多連續自動接續幾次（代理每次接續又開新的背景指令時不會無限循環）</summary>
    internal const int MaxAutoContinues = 10;

    /// <summary>一個背景指令的一行說明：「`npm test`（結束碼 1，執行了 2 分 5 秒）」；被拒絕的寫「沒有執行」</summary>
    public static string Describe(AgentBackgroundCommandFinishedEvent finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        var title = string.IsNullOrWhiteSpace(finished.Title) ? "背景指令" : $"`{TextRules.FirstLine(finished.Title)}`";
        var parts = new List<string>();
        if (finished.Status == AgentItemStatus.Declined)
            parts.Add("沒有執行");
        else if (finished.ExitCode is { } code)
            parts.Add($"結束碼 {code}");
        else
            parts.Add(finished.Status == AgentItemStatus.Failed ? "失敗" : "已結束");
        if (finished.Duration is { } d && d > TimeSpan.Zero)
            parts.Add("執行了 " + DurationText(d));
        return $"{title}（{string.Join("，", parts)}）";
    }

    /// <summary>對話裡那則提示（noticeKind backgroundTask；緊接著的一輪是隱藏送出的接續訊息）</summary>
    public static string ContinuationNotice(IReadOnlyList<AgentBackgroundCommandFinishedEvent> finished)
        => "背景指令已結束：" + string.Join("、", (finished ?? throw new ArgumentNullException(nameof(finished))).Select(Describe)) + "，代理接著處理。";

    /// <summary>
    /// 給代理的接續訊息（隱藏送出）：哪些背景指令跑完了、還有哪些在跑。請它查看結果、接著完成原本的工作（同 Claude 背景工作完成時 CLI 告訴模型的那一輪）
    /// </summary>
    public static string ContinuationText(IReadOnlyList<AgentBackgroundCommandFinishedEvent> finished, IReadOnlyList<AgentBackgroundTool> stillRunning)
    {
        ArgumentNullException.ThrowIfNull(finished);
        ArgumentNullException.ThrowIfNull(stillRunning);
        var lines = new List<string>
        {
            "（系統通知：你先前放到背景執行的指令已經結束，這不是使用者送的訊息。）",
            "已結束：" + string.Join("、", finished.Select(Describe)) + "。",
        };
        var running = stillRunning.Select(t => TextRules.FirstLine(t.Title)).Where(t => t.Length > 0).ToList();
        if (running.Count > 0)
            lines.Add("還在背景執行：" + string.Join("、", running.Select(t => $"`{t}`")) + "（結束時會再通知你，不必等它）。");
        lines.Add("請查看已結束的指令的輸出與結果，接著完成原本的工作；如果不需要再做什麼，簡短說明結果即可。");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 背景指令跑完但沒有自動接續時的提示（排隊的訊息暫停中、或已連續自動接續太多次）：說明原因，要代理處理就自己送出
    /// </summary>
    public static string NotContinuedNotice(IReadOnlyList<AgentBackgroundCommandFinishedEvent> finished, bool queuePaused)
        => "背景指令已結束：" + string.Join("、", (finished ?? throw new ArgumentNullException(nameof(finished))).Select(Describe)) + "。"
           + (queuePaused
               ? "排隊的訊息暫停中，這次沒有讓代理自動接著處理，按送出繼續。"
               : $"代理已連續自動接著處理 {MaxAutoContinues} 次，這次沒有自動接續；要它處理就送一則訊息。");

    private static string DurationText(TimeSpan d)
        => d.TotalHours >= 1 ? $"{(int)d.TotalHours} 小時 {d.Minutes} 分"
            : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} 分 {d.Seconds} 秒"
            : $"{Math.Max(1, (int)Math.Round(d.TotalSeconds))} 秒";
}
