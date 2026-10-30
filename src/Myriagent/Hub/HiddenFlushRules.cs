namespace Myriagent.Hub;

/// <summary>
/// 背景分頁（對話開著、但沒有顯示在任何視窗：別的分頁在前面、切到儀表板、視窗最小化）的串流更新與標頭節流（各自計時）。顯示中的對話照每 50 ms 合併送一次；
/// 背景的每次送都要重新轉換整則 Markdown、網頁也要整段比對 DOM，同時跑好幾個對話時這是白做的工。最後一次更新一定會送出（留在待送清單、下一輪再看），
/// 切回來時（visible）立刻送
/// </summary>
internal static class HiddenFlushRules
{
    /// <summary>背景分頁每這麼久最多送一次項目更新</summary>
    public const int IntervalMs = 500;

    /// <summary>
    /// 這一輪要不要先不送這個對話的項目更新：只有開著它的每個 view 都回報過自己顯示哪些對話（visible）而且都沒在顯示它，
    /// 距上次送又還不到 <see cref="IntervalMs"/> 時才延後（沒回報過 visible 的 view 不知道看不看得到，照舊每次送）
    /// </summary>
    public static bool Defer(bool shownAnywhere, bool allViewsReportVisibility, long? lastFlushMs, long nowMs, int intervalMs = IntervalMs) =>
        !shownAnywhere && allViewsReportVisibility && lastFlushMs is { } last && nowMs - last < intervalMs;
}
