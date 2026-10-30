using System.Globalization;
using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 額度全用完時等重置後自動接著做（2026-10-26 使用者要求；CLI 的 CLAUDE_CODE_RETRY_WATCHDOG 是同樣的行為，app 自己做才涵蓋四種代理）：
/// 額度用完、又挑不到能接手的帳戶時，記下「等這個帳戶在重置之後接著做」（meta 的 QuotaWait），維護每 30 秒看一次：
/// 等待期間有別的帳戶恢復就照自動換帳戶換過去，時間到了就以同一個帳戶隱藏送出接續訊息。只等 <see cref="MaxWait"/> 以內的；
/// 接著做又馬上用完時最多重來 <see cref="MaxAttempts"/> 次
/// </summary>
internal static class QuotaWaitRules
{
    /// <summary>重置時間超過這麼久就不等（每週額度可能要等好幾天，那時候的情況已經不一樣了）</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(12);

    /// <summary>重置之後多等一下才接著做（伺服器那邊的重置不一定準時）</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>一次「使用者送出」之後，自動接著做又馬上用完最多重來幾次</summary>
    public const int MaxAttempts = 3;

    /// <summary>要等到什麼時候；重置時間不明、太久、已經重來太多次回 null（不等）</summary>
    public static DateTimeOffset? Plan(DateTimeOffset? resetsAt, DateTimeOffset now, int attempts)
    {
        if (resetsAt is not { } r || attempts >= MaxAttempts || r - now > MaxWait)
            return null;
        return (r > now ? r : now) + Grace;
    }

    /// <summary>對話裡的提示：「所有帳戶的額度都用完了：「工作用 Max」14:05 重置後自動接著做…」</summary>
    public static string ScheduledNotice(string? accountLabel, DateTimeOffset until, DateTimeOffset now)
        => $"額度用完了，也沒有能接手的帳戶：等{Label(accountLabel)}在 {When(until, now)} 重置後自動接著做（輸入框上方可以取消；你自己送出訊息也會取消等待）。";

    public static string ResumedNotice(string? accountLabel) => $"{Label(accountLabel)}的額度已重置，自動接著做";

    public const string CancelledNotice = "已取消等額度重置；額度恢復後可以按送出繼續。";

    /// <summary>給代理的接續訊息（同一個帳戶、同一個對話：先前的工具結果代理都看得到）</summary>
    public static string BuildContinuation(IReadOnlyList<string> recentSteps)
    {
        var lines = new List<string> { "（系統通知：這個帳戶的訂閱額度剛才用完，這一輪在中途停下；額度已重置，Clode Myriagent 自動接著做，這不是使用者中斷。" };
        var steps = (recentSteps ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (steps.Count > 0)
            lines.Add("使用者最後一則訊息之後已經做過的步驟（由舊到新）：" + string.Join("、", steps.Select(s => $"〈{s}〉")) + "。");
        lines.Add("已完成的步驟不要重做；先確認目前的實際狀態（例如相關檔案、git 狀態），再從中斷處繼續原本的工作。");
        lines.Add("使用者沒有另外傳送新訊息；請繼續原本的工作並回報。）");
        return string.Join("\n", lines);
    }

    private static string Label(string? label) => string.IsNullOrWhiteSpace(label) ? "這個帳戶" : $"「{label.Trim()}」";

    /// <summary>重置時間：今天的只寫時:分，其他寫月/日 時:分（本機時區）</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
    }
}
