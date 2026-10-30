using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// app 結束（關閉、當掉、Windows 登出或重新開機）時還在執行的一輪，下次啟動怎麼處理（AgentHub.InterruptedTurns.cs；設計見
/// docs/plans/2026-10-23-interrupted-turn-resume.md）。不自動接續的條件與 CLI 熱更新、app 更新相同（CliRestartRules.WhyNotContinue）：
/// 中斷超過 1 小時、被打斷的是高風險操作；另外連續自動接續達上限也不再自動接續。純函式
/// </summary>
internal static class InterruptedTurnRules
{
    /// <summary>同一輪最多連續自動接續幾次（每次重新啟動都在完成前又被中斷：多半是 app 或代理一直當掉，不再自動重來）</summary>
    public const int MaxAutoResumes = 3;

    /// <summary>按「繼續」時加的提示（重新啟動後沒有自動接續的對話）</summary>
    public const string ManualContinueNotice = "已請代理從中斷處繼續（app 重新啟動後的接續訊息，不是你送的）";

    private static readonly long MinUnixMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();

    private static readonly long MaxUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static AppRestartKind KindOf(RunningTurnRecord record) => record.ClosedByApp ? AppRestartKind.Closed : AppRestartKind.Unexpected;

    /// <summary>這一輪被中斷的時間（UTC）：最後一次確認還在執行的時間（沒有就是開始的時間）；手改或損毀超出可表示範圍的回 null</summary>
    public static DateTime? InterruptedAtUtc(RunningTurnRecord record)
    {
        var ms = Math.Max(record.StartedAt, record.SeenAt);
        if (ms <= 0 || ms < MinUnixMilliseconds || ms > MaxUnixMilliseconds)
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    }

    /// <summary>
    /// 為什麼不自動接續；可以自動接續回 null。順序同 CliRestartRules.WhyNotContinue（中斷太久、高風險操作），
    /// 連續自動接續達上限算「上一次接續沒有成功」；時間不明（手改、損毀）當成太舊
    /// </summary>
    public static CliNoContinueReason? WhyNotContinue(RunningTurnRecord record, DateTime nowUtc)
    {
        if (InterruptedAtUtc(record) is not { } at)
            return CliNoContinueReason.TooOld;
        return CliRestartRules.WhyNotContinue(userStopped: false, turnRunning: true, at, nowUtc,
            previousTurnFailed: false, continuationFailed: record.AutoResumes >= MaxAutoResumes, riskyTool: !string.IsNullOrWhiteSpace(record.RiskOperation));
    }

    /// <summary>接續訊息與「繼續」按鈕用的內容（工具、等回應的請求、背景工作照快照；版本是記下的與目前的 CLI）</summary>
    public static ContinuationContext Context(AgentProvider provider, RunningTurnRecord record, string? currentCliVersion)
        => new(provider, record.CliVersion, currentCliVersion, record.InterruptedTools.ToList(), record.PendingRequests.ToList(),
            record.BackgroundWork || record.WaitingBackground, AppRestart: KindOf(record),
            WaitingBackground: record.WaitingBackground, BackgroundTasks: record.BackgroundTasks.ToList());

    /// <summary>自動接續時加的提示（隱藏送出的那一則）</summary>
    /// <param name="abandonedUpdate">放棄 app 更新時就地接續排空停掉的背景工作（app 沒有重新開啟）</param>
    public static string ContinuedNotice(RunningTurnRecord record, bool abandonedUpdate = false) => (record.WaitingBackground, abandonedUpdate) switch
    {
        (_, true) => $"{Prefix(record, abandonedUpdate)}，已請代理確認背景工作做到哪裡、接著做",
        (true, false) => $"{Prefix(record, abandonedUpdate)}，重新開啟後已請代理確認背景工作做到哪裡、接著做",
        (false, false) => $"{Prefix(record, abandonedUpdate)}，重新開啟後已請代理從中斷處繼續",
    };

    /// <summary>沒有自動接續的提示</summary>
    /// <param name="markdown">true：對話裡的 Notice（Markdown，操作摘要跳脫）；false：標頭 HeaderRestart.detail（純文字）</param>
    public static string DescribeNotContinued(RunningTurnRecord record, CliNoContinueReason reason, bool markdown, bool abandonedUpdate = false)
    {
        var operation = reason == CliNoContinueReason.RiskyTool && record.RiskOperation != null
            ? markdown ? CliRestartRules.EscapeMarkdown(record.RiskOperation) : record.RiskOperation
            : null;
        var why = reason == CliNoContinueReason.ContinuationFailed
            ? $"已連續自動接續 {record.AutoResumes} 次都在完成前被中斷，請確認狀況後按「繼續」或送出新訊息"
            : CliRestartRules.DescribeNoContinue(reason, operation);
        return $"{Prefix(record, abandonedUpdate)}，這個對話沒有自動接續：{why}";
    }

    /// <summary>CLI 正在更新、這時接續一定啟動失敗：改成沒有自動接續（標頭給「繼續」）時的說明</summary>
    public static string DescribeCliUpdating(RunningTurnRecord record, AgentProvider provider, bool abandonedUpdate = false)
        => $"{Prefix(record, abandonedUpdate)}，這個對話沒有自動接續：{CliRestartRules.ProductName(provider)} CLI 正在更新，請等更新完成後按「繼續」";

    /// <summary>系統匣通知（有自動接續或沒接續的才通知）</summary>
    public static string? Summary(int continued, int notContinued)
    {
        if (continued <= 0 && notContinued <= 0)
            return null;
        var parts = new List<string>();
        if (continued > 0)
            parts.Add($"上次中斷的 {continued} 個對話已自動接續");
        if (notContinued > 0)
            parts.Add($"{notContinued} 個上次中斷的對話沒有自動接續，請到對話裡確認後按「繼續」");
        return string.Join("；", parts);
    }

    private static string Prefix(RunningTurnRecord record, bool abandonedUpdate)
    {
        if (abandonedUpdate)
            return "Clode Myriagent 為了套用更新停掉了這個對話還在跑的背景工作（這次更新已放棄）";
        return (record.ClosedByApp, record.WaitingBackground) switch
        {
            (true, false) => "Clode Myriagent 上次結束時這個對話還在執行",
            (false, false) => "Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），這個對話被中斷",
            (true, true) => "Clode Myriagent 上次結束時停掉了這個對話還在跑的背景工作",
            (false, true) => "Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），這個對話還在跑的背景工作跟著停了",
        };
    }
}
