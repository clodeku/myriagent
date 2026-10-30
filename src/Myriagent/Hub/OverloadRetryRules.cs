using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// API 伺服器暫時無法服務或網路連線瞬斷時自動重試（AgentHub.OverloadRetry.cs）：
/// Claude 碰到「API Error: 529 Overloaded」（2026-10-24 使用者要求過 60 秒自動重試；CLI 自己會先重試幾次〔Claude Code 預設 10 次、指數退避〕，還是過載才以錯誤結束這一輪），
/// Antigravity 碰到「API error (attempt 1): UNAVAILABLE (code 503): The service is currently unavailable.」（2026-10-24 使用者要求同樣 60 秒後重試、重試三次都失敗就停止；
/// 2026-10-25 使用者要求 529 也改成 3 次），以及 Antigravity 常駐連線池遇到 wsasend／EOF／connection reset 等 API 網路連線中斷（2026-10-26 使用者選擇方案 A：
/// 回收舊 agy 程序清掉連線池、等 10 秒後自動重試，連續最多 3 次）。
/// 會話以 AgentTurnCompletedEvent.ServerBusy 標出原因；中樞在這之後等 DelayOf，隱藏送出接續訊息請代理從中斷處接著做；連續最多 MaxAttemptsOf 次，之後照一般的失敗停下。
/// 使用者按了停止、換帳戶接著做、對話是副本時不重試；等待期間開始了新的一輪（使用者送出、CLI 自己開一輪）就不再重試
/// </summary>
internal static class OverloadRetryRules
{
    /// <summary>伺服器過載（529）與暫時無法服務（503）到自動重試之間等多久</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);

    /// <summary>API 底層網路連線中斷（wsasend／EOF／connection reset 等）到自動重試之間等多久</summary>
    public static readonly TimeSpan NetworkErrorDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 連續自動重試的上限：使用者要求重試三次都失敗就停止（Claude 的 529 以前是 10 次）——還是不行就停下，讓使用者自己決定
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>這種原因預設等多久再自動重試</summary>
    public static TimeSpan DelayOf(AgentServerBusy busy) => busy switch
    {
        AgentServerBusy.None => TimeSpan.Zero,
        AgentServerBusy.Overloaded => Delay,
        AgentServerBusy.Unavailable => Delay,
        AgentServerBusy.NetworkError => NetworkErrorDelay,
        _ => throw new ArgumentOutOfRangeException(nameof(busy), busy, "未知的伺服器忙碌種類"),
    };

    /// <summary>這種原因最多連續自動重試幾次（不是暫時無法服務的是 0）</summary>
    public static int MaxAttemptsOf(AgentServerBusy busy) => busy switch
    {
        AgentServerBusy.None => 0,
        AgentServerBusy.Overloaded => MaxAttempts,
        AgentServerBusy.Unavailable => MaxAttempts,
        AgentServerBusy.NetworkError => MaxAttempts,
        _ => throw new ArgumentOutOfRangeException(nameof(busy), busy, "未知的伺服器忙碌種類"),
    };

    /// <summary>
    /// 這一輪結束後要排第幾次自動重試；不重試回 null。previousAttempts＝在這一輪之前已經連續自動重試了幾次
    /// （使用者自己送出、或任何一輪不是因為伺服器暫時無法服務而結束時歸零）
    /// </summary>
    public static int? NextAttempt(bool failed, AgentServerBusy busy, bool stopRequested, bool accountChanging, bool isReplica, int previousAttempts)
    {
        if (!failed || busy == AgentServerBusy.None || stopRequested || accountChanging || isReplica)
            return null;
        int next = Math.Max(0, previousAttempts) + 1;
        return next <= MaxAttemptsOf(busy) ? next : null;
    }

    /// <summary>
    /// 這一輪因為伺服器暫時無法服務而失敗、但已經連續重試到上限：要加「不再自動重試」的提示（沒到上限、不是這類錯誤、使用者停止或換帳戶都不加）
    /// </summary>
    public static bool GaveUp(bool failed, AgentServerBusy busy, bool stopRequested, bool accountChanging, bool isReplica, int previousAttempts)
        => failed && busy != AgentServerBusy.None && !stopRequested && !accountChanging && !isReplica && previousAttempts >= MaxAttemptsOf(busy);

    /// <summary>失敗的錯誤之後那一則提示：多久後自動重試、第幾次</summary>
    public static string ScheduledNotice(AgentServerBusy busy, int attempt, TimeSpan delay)
        => $"{Cause(busy)}，{FormatDelay(delay)}後自動重試（第 {attempt} 次，最多 {MaxAttemptsOf(busy)} 次）；在這之前送出訊息就改送你的訊息";

    /// <summary>連續重試到上限、這次不再重試的提示</summary>
    public static string GaveUpNotice(AgentServerBusy busy) => busy switch
    {
        AgentServerBusy.Overloaded => $"API 伺服器持續過載，已連續自動重試 {MaxAttempts} 次都失敗，不再自動重試；稍後再送出訊息試試",
        AgentServerBusy.Unavailable => $"API 伺服器持續無法服務（503 UNAVAILABLE），已連續自動重試 {MaxAttempts} 次都失敗，不再自動重試；稍後再送出訊息試試",
        AgentServerBusy.NetworkError => $"API 網路連線持續中斷，已連續自動重試 {MaxAttempts} 次都失敗，不再自動重試；請檢查網路後再送出訊息試試",
        _ => throw new ArgumentOutOfRangeException(nameof(busy), busy, "不是伺服器暫時無法服務"),
    };

    /// <summary>重試那一輪的隱藏送出提示（取代使用者訊息的那一則）</summary>
    public static string RetryNotice(AgentServerBusy busy, int attempt) => busy switch
    {
        AgentServerBusy.Overloaded => $"API 過載後自動重試（第 {attempt} 次）",
        AgentServerBusy.Unavailable => $"API 暫時無法服務後自動重試（第 {attempt} 次）",
        AgentServerBusy.NetworkError => $"API 連線中斷後自動重試（第 {attempt} 次）",
        _ => throw new ArgumentOutOfRangeException(nameof(busy), busy, "不是伺服器暫時無法服務"),
    };

    /// <summary>重試時隱藏送給代理的接續訊息：說明不是使用者中斷、使用者沒有新訊息，請從中斷處接著做</summary>
    public static string Continuation(AgentServerBusy busy, int attempt, TimeSpan delay)
    {
        string what = busy switch
        {
            AgentServerBusy.Overloaded => "上一輪呼叫 API 時伺服器過載（529 Overloaded），重試幾次仍失敗",
            AgentServerBusy.Unavailable => "上一輪呼叫 API 時伺服器暫時無法服務（503 UNAVAILABLE）",
            AgentServerBusy.NetworkError => "上一輪呼叫 API 時網路連線中斷",
            _ => throw new ArgumentOutOfRangeException(nameof(busy), busy, "不是伺服器暫時無法服務"),
        };
        return $"（系統通知：{what}，這一輪中斷了；Clode Myriagent 等了 {FormatDelay(delay)}後自動重試"
               + $"（第 {attempt} 次），這不是使用者中斷。使用者沒有傳送新訊息；請從中斷處接著做原本的工作，已經完成的步驟不要重做。）";
    }

    /// <summary>「60 秒」「2 分鐘」：整分鐘以分鐘說，其他以秒說（不到 1 秒算 1 秒）</summary>
    public static string FormatDelay(TimeSpan delay)
    {
        var seconds = Math.Max(1, (int)Math.Round(delay.TotalSeconds));
        return seconds >= 120 && seconds % 60 == 0 ? $"{seconds / 60} 分鐘" : $"{seconds} 秒";
    }

    /// <summary>提示開頭說的原因</summary>
    private static string Cause(AgentServerBusy busy) => busy switch
    {
        AgentServerBusy.Overloaded => "API 伺服器過載（529 Overloaded）",
        AgentServerBusy.Unavailable => "API 伺服器暫時無法服務（503 UNAVAILABLE）",
        AgentServerBusy.NetworkError => "API 網路連線中斷",
        _ => throw new ArgumentOutOfRangeException(nameof(busy), busy, "不是伺服器暫時無法服務"),
    };
}
