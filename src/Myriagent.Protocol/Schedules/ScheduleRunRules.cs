namespace Myriagent.Schedules;

/// <summary>
/// Myriagent 觀察到的一次執行（runs.jsonl 的一行）。At＝開始時間（工作排程器的 LastRunTime，本機時間）；
/// Gap＝和上一筆之間還有預定的執行沒有觀察到（兩次掃描之間跑了好幾次，或 app 沒開時跑過），畫面標出「中間有沒觀察到的執行」
/// </summary>
internal sealed record ScheduleRunRecord(string Path, DateTime At, int Code, DateTime ObservedAt, bool Gap);

/// <summary>一次自動診斷（diagnose.json）：限制次數用</summary>
internal sealed record ScheduleDiagnoseRecord(string Path, DateTime At, string? ConversationId);

/// <summary>
/// 執行紀錄與失敗的規則：什麼時候記一筆、連續失敗幾次、什麼時候通知（只在新的一串失敗開始時）、能不能自動請代理診斷。
/// Windows 的 TaskScheduler/Operational 記錄預設關閉（開要系統管理員），所以紀錄是 Myriagent 每次掃描比對 LastRunTime／LastTaskResult 自己記的
/// </summary>
internal static class ScheduleRunRules
{
    /// <summary>每個排程留幾筆執行紀錄</summary>
    public const int KeepPerTask = 200;

    /// <summary>多久以前的失敗不再通知（app 剛開、第一次看到時的舊結果）</summary>
    public static readonly TimeSpan AlertWindow = TimeSpan.FromHours(12);

    /// <summary>同一個排程多久內只自動診斷一次</summary>
    public static readonly TimeSpan DiagnosePerTask = TimeSpan.FromHours(24);

    /// <summary>每台電腦每天最多自動診斷幾次</summary>
    public const int DiagnosePerDay = 10;

    /// <summary>每週額度剩不到這個百分比就不自動診斷</summary>
    public const double DiagnoseMinWeeklyRemaining = 10;

    /// <summary>
    /// 這次掃描看到的上次執行要不要記成新的一筆：有執行過、結果是最後結果（不是執行中）、比上一筆新。
    /// 和上一筆之間還有預定的執行（前後各留一分鐘的誤差）就標 Gap
    /// </summary>
    public static ScheduleRunRecord? Observe(string path, ScheduleRunRecord? previous, DateTime? lastRun, int code, IReadOnlyList<ScheduleTrigger> triggers, DateTime now)
    {
        if (lastRun is not { } at || at.Year < 2000)
            return null;
        if (!ScheduleResultRules.IsFinal(code))
            return null;
        at = Truncate(at);
        if (previous != null && at <= previous.At)
            return null;
        bool gap = previous != null
                   && previous.At.AddMinutes(1) < at.AddMinutes(-1)
                   && ScheduleTriggerRules.Occurrences(triggers, previous.At.AddMinutes(1), at.AddMinutes(-1), 1).Count > 0;
        return new ScheduleRunRecord(path, at, code, now, gap);
    }

    /// <summary>從最後一筆往前數連續失敗幾次</summary>
    public static int ConsecutiveFailures(IReadOnlyList<ScheduleRunRecord> runs)
    {
        int n = 0;
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            if (ScheduleResultRules.Classify(runs[i].Code).Level != ScheduleResultLevel.Fail)
                break;
            n++;
        }
        return n;
    }

    /// <summary>新的一筆要不要通知：是失敗、上一筆不是失敗（新的一串失敗才通知）、開始時間在 12 小時內</summary>
    public static bool ShouldAlert(ScheduleRunRecord run, ScheduleRunRecord? previous, DateTime now)
    {
        if (ScheduleResultRules.Classify(run.Code).Level != ScheduleResultLevel.Fail)
            return false;
        if (previous != null && ScheduleResultRules.Classify(previous.Code).Level == ScheduleResultLevel.Fail)
            return false;
        return now - run.At <= AlertWindow;
    }

    /// <summary>只有列入管理的（代理建立、使用者納入管理）才通知與自動診斷；疑似的可能根本不是自己的程式</summary>
    public static bool IsManaged(ScheduleOrigin origin) => origin is ScheduleOrigin.Agent or ScheduleOrigin.Adopted;

    /// <summary>
    /// 能不能自動請代理診斷；不行時回原因（排程頁的明細顯示）。<paramref name="weeklyRemaining"/>＝要用的帳戶每週額度剩下的百分比（不知道是 null）
    /// </summary>
    public static string? DiagnoseBlock(string path, ScheduleOrigin origin, bool settingOn, bool taskOptOut, IReadOnlyList<ScheduleDiagnoseRecord> log,
        DateTime now, bool accountAvailable, double? weeklyRemaining)
    {
        if (!settingOn)
            return "設定頁關掉了自動診斷";
        if (taskOptOut)
            return "這個排程關掉了自動診斷";
        if (!IsManaged(origin))
            return "只有代理建立或納入管理的排程才自動診斷";
        if (log.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && now - r.At < DiagnosePerTask))
            return "24 小時內已經自動診斷過";
        if (log.Count(r => r.At.Date == now.Date) >= DiagnosePerDay)
            return $"今天已經自動診斷 {DiagnosePerDay} 次";
        if (!accountAvailable)
            return "沒有可用的帳戶";
        if (weeklyRemaining is { } remaining && remaining < DiagnoseMinWeeklyRemaining)
            return $"每週額度剩不到 {DiagnoseMinWeeklyRemaining:0}%";
        return null;
    }

    /// <summary>診斷紀錄只留最近 7 天</summary>
    public static List<ScheduleDiagnoseRecord> Prune(IEnumerable<ScheduleDiagnoseRecord> log, DateTime now)
        => log.Where(r => now - r.At < TimeSpan.FromDays(7)).OrderBy(r => r.At).ToList();

    /// <summary>時間只到秒（COM 的 DATE 有毫秒誤差，比對前後兩次會以為是不同的執行）</summary>
    public static DateTime Truncate(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind);
}
