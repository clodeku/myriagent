using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>組接續訊息需要的事（中樞在重開或停下對話時依自己的卡片收集）</summary>
/// <param name="FromVersion">換版前的版本（不明為 null）</param>
/// <param name="ToVersion">換版後的版本（不明為 null）</param>
/// <param name="InterruptedTools">重開時正在執行、被打斷的工具（結果未知）；安全點重開時為空</param>
/// <param name="PendingRequests">
/// 重開時正在等使用者回應的請求（「執行指令：Set-Content e16.txt e16」「問題：要怎麼彙總？」「計畫確認」）：
/// 那些操作還沒有執行（Codex 被直接殺時舊的核准請求也不會被撤回，CLI 接續後要重新提出）
/// </param>
/// <param name="BackgroundWorkStopped">重開時有存活的背景工作（重開後已停止）</param>
/// <param name="UserRequested">使用者看過沒有自動接續的說明後按了「繼續」（不是重開時自動送的）</param>
/// <param name="AppFrom">app 自我更新時換版前的 app 版本（不明為 null）</param>
/// <param name="AppTo">app 自我更新時換版後的 app 版本；有值時第一句改說 app 本身更新（CLI 版本不再提）</param>
/// <param name="AppRestart">
/// app 結束（關閉、當掉、電腦重新開機）時這一輪還在執行、重新開啟後接續（InterruptedTurnRules）；有值時第一句改說 app 結束中斷了這一輪
/// </param>
/// <param name="WaitingBackground">
/// 代理這一輪已經回覆完、只在等背景工作回報（回一句「等測試跑完」就結束了這一輪），背景工作被停掉了：
/// 沒有被打斷的回覆或工具，要說的是背景工作停了、不會再有完成通知（app 結束後重新開啟、app 更新排空停掉了背景工作）
/// </param>
/// <param name="BackgroundTasks">停掉的背景工作（「執行指令：npm test」）；不明為 null 或空的</param>
internal sealed record ContinuationContext(
    AgentProvider Provider,
    string? FromVersion,
    string? ToVersion,
    IReadOnlyList<AgentInterruptedTool> InterruptedTools,
    IReadOnlyList<string> PendingRequests,
    bool BackgroundWorkStopped,
    bool UserRequested = false,
    string? AppFrom = null,
    string? AppTo = null,
    AppRestartKind? AppRestart = null,
    bool WaitingBackground = false,
    IReadOnlyList<string>? BackgroundTasks = null);

/// <summary>app 是怎麼結束、中斷了執行中的一輪（running.json 的 ClosedByApp）</summary>
internal enum AppRestartKind
{
    /// <summary>app 正常結束（使用者結束、Windows 登出、關機或重新開機）：結束前記下了當下在執行的工具與等回應的請求</summary>
    Closed,

    /// <summary>app 沒有正常結束（當掉、被強制結束、斷電）：最後的狀態是最多 30 秒前的快照</summary>
    Unexpected,
}

/// <summary>
/// CLI 熱更新後給代理的接續訊息（設計 7.7；Claude 放進 CLAUDE_CODE_RESUME_PROMPT、Codex 當 turn/start 的文字，按「繼續」時由中樞隱藏送出，
/// 都不顯示成使用者訊息）。寫明五件事：誰為什麼重開（不是使用者拒絕）、重開時在做什麼與結果未知、已完成的步驟不要重做、
/// 背景工作已停止、使用者還沒送新訊息。研究實測：明說是 app 為了更新 CLI 自動中斷的版本，代理會先確認再重跑並完成原本的工作〔RR〕
/// </summary>
internal static class ContinuationRules
{
    /// <summary>每個工具摘要最多幾個字（太長的指令只留第一行的開頭）</summary>
    public const int MaxOperationChars = 160;

    public static string Build(ContinuationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lines = new List<string> { "（系統通知：" + DescribeRestart(context) + "，這不是使用者拒絕或中斷。" };

        var tools = context.InterruptedTools ?? Array.Empty<AgentInterruptedTool>();
        var pending = (context.PendingRequests ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        // 這一輪早就回覆完、只在等背景工作：沒有被打斷的回覆或工具，背景工作停了也不會再有完成通知——不請代理確認，它會以為已經結束
        if (context.WaitingBackground)
        {
            var tasks = (context.BackgroundTasks ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var which = tasks.Count == 0 ? "背景工作" : "背景工作" + string.Join("、", tasks.Select(t => $"〈{Shorten(t)}〉"));
            lines.Add($"上一輪你已經回覆完，正在等{which}回報；它們在重啟時已經停止，結果未知，也不會再有完成通知。"
                      + "請先確認它們做到哪裡（例如輸出檔、程序、git 狀態），需要的話重新以背景執行（可安全重跑才重跑，不可重複的操作例如部署、發布、付款不要直接重跑），再照原本的計畫等它們完成並回報。");
            lines.Add(context.UserRequested
                ? "使用者看過狀況後按了「繼續」，沒有另外傳送新訊息。）"
                : "使用者在重啟後尚未傳送新訊息。）");
            return string.Join("\n", lines);
        }

        if (tools.Count > 0)
        {
            var running = tools.Count == 1
                ? $"〈{ToolLabel(tools[0])}〉，已執行 {CliRestartRules.FormatElapsed(tools[0].Elapsed)}"
                : string.Join("、", tools.Select(t => $"〈{ToolLabel(t)}〉（已執行 {CliRestartRules.FormatElapsed(t.Elapsed)}）"));
            lines.Add($"重啟時你正在執行{running}，結果未知；請先確認它是否已生效（例如檢查相關檔案、git 狀態或程序），可安全重跑才重跑。已完成的步驟不要重做。");
        }

        if (pending.Count > 0)
        {
            var waiting = string.Join("、", pending.Select(p => $"〈{Shorten(p)}〉"));
            lines.Add($"重啟時你正在等使用者回應{waiting}，使用者還沒有回覆，等核准的操作也都還沒有執行；如果仍然需要，請重新提出（會再詢問使用者）。");
        }

        // app 沒有正常結束時的快照最多是 30 秒前的：沒記到工具不代表當時沒有工具在執行
        if (tools.Count == 0 && pending.Count == 0)
            lines.Add(context.AppRestart == AppRestartKind.Unexpected
                ? "最後記錄到的狀態沒有工具在執行，但 app 是意外結束的，最後幾十秒做了什麼沒有記錄；請先確認最後一個操作是否已生效（例如檢查相關檔案、git 狀態或程序），再從中斷處繼續，不要重做已完成的步驟。"
                : "重啟時你正在產生回覆，沒有任何工具在執行；請從中斷處繼續，不要重做已完成的工具步驟。");

        // 背景工作被停在哪一步不知道：同被打斷的工具，先確認再決定（背景跑的部署、發布重跑一次就是做兩次）
        if (context.BackgroundWorkStopped)
            lines.Add("重啟時還在跑的背景工作已停止，結果未知；請先確認它們做到哪裡，可安全重跑才重跑，不可重複的操作（例如部署、發布、付款）不要直接重跑。");

        lines.Add(context.UserRequested
            ? "使用者看過狀況後按了「繼續」，沒有另外傳送新訊息；請從中斷處繼續原本的工作並回報。）"
            : "使用者在重啟後尚未傳送新訊息；請繼續原本的工作並回報。）");
        return string.Join("\n", lines);
    }

    /// <summary>第一句：誰把哪個 CLI 由哪一版換到哪一版；app 自我更新時改說 app 本身由哪一版換到哪一版</summary>
    private static string DescribeRestart(ContinuationContext context)
    {
        var product = CliRestartRules.ProductName(context.Provider);
        switch (context.AppRestart)
        {
            case AppRestartKind.Closed when context.WaitingBackground:
                return $"Clode Myriagent 上次結束時停掉了 {product} 還在跑的背景工作，現在 app 重新開啟、重新啟動了 {product}";
            case AppRestartKind.Unexpected when context.WaitingBackground:
                return $"Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），{product} 還在跑的背景工作跟著停了，現在 app 重新開啟、重新啟動了 {product}";
            case AppRestartKind.Closed:
                return $"Clode Myriagent 上次結束時中斷了 {product} 這一輪的工作，現在 app 重新開啟、重新啟動了 {product}";
            case AppRestartKind.Unexpected:
                return $"Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），中斷了 {product} 這一輪的工作，現在 app 重新開啟、重新啟動了 {product}";
        }
        if (!string.IsNullOrWhiteSpace(context.AppTo))
        {
            var appFrom = string.IsNullOrWhiteSpace(context.AppFrom) ? null : context.AppFrom.Trim();
            var appTo = context.AppTo.Trim();
            // 版本相同：這次更新被放棄（就地重開）或新版啟動失敗退回原版——兩種都是「為了 app 更新停下了 CLI、現在重新啟動」
            return appFrom != null && appFrom != appTo
                ? $"Clode Myriagent 已由 {appFrom} 更新為 {appTo} 並重新啟動（{product} 也跟著重新啟動）"
                : $"Clode Myriagent 為了套用自身的更新而停下了 {product}，現在重新啟動了它";
        }

        var from = string.IsNullOrWhiteSpace(context.FromVersion) ? null : context.FromVersion.Trim();
        var to = string.IsNullOrWhiteSpace(context.ToVersion) ? null : context.ToVersion.Trim();
        if (to != null && from != null && !string.Equals(from, to, StringComparison.Ordinal))
            return $"Clode Myriagent 已把 {product} 由 {from} 更新為 {to} 並重新啟動";
        if (to != null && from == null)
            return $"Clode Myriagent 已把 {product} 更新為 {to} 並重新啟動";
        // 版本沒變（更新失敗或取消，以原本的版本重開）或不明
        return $"Clode Myriagent 為了更新 {product} 而重新啟動了它";
    }

    /// <summary>工具的標籤：「執行指令：npm test」（沒有摘要就只有名稱）</summary>
    private static string ToolLabel(AgentInterruptedTool tool)
    {
        var name = string.IsNullOrWhiteSpace(tool.ToolName) ? "工具" : tool.ToolName.Trim();
        var title = ClaudeStreamRules.FirstLine(tool.Title);
        return Shorten(title.Length == 0 ? name : $"{name}：{title}");
    }

    private static string Shorten(string text)
    {
        var line = ClaudeStreamRules.FirstLine(text);
        return line.Length <= MaxOperationChars ? line : line[..MaxOperationChars] + "…";
    }
}
