using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>
/// 告訴 CLI 這個對話有沒有人在看（IAgentSession.SetWatched；Claude 的 set_has_subscribers，2026-11-25）：沒人看時 CLI 把逐字的小片段合併成每秒一行，
/// 有人看照舊約每 50 毫秒一行（ClaudeStreamRules 的環境變數）。內容不會少，只是行數少，
/// CLI 少寫、app 少讀少解析（好幾個對話同時在看不到的分頁裡執行時）。
/// 有人看＝這台有視窗顯示它（還沒回報可見的視窗當成看得到，同 HiddenFlushRules）、或瀏覽器版正看著它（引擎推來的觀看集合，政策 full）。
/// 變成看得到立刻送；變成看不到等 <see cref="StreamUnwatchDelay"/>（切來切去不必每次都送：CLI 每收到一次就結束正在等的那一批）
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>最近一次告訴會話的：有沒有人在看（新的會話當成有）</summary>
        public bool StreamWatched = true;

        /// <summary>從什麼時候起沒人看（Environment.TickCount64）；有人看是 null</summary>
        public long? UnwatchedAtMs;
    }

    /// <summary>沒人看多久才告訴 CLI（測試改成 0）</summary>
    internal TimeSpan StreamUnwatchDelay { get; set; } = TimeSpan.FromSeconds(3);

    private bool streamWatchScheduled;

    private bool IsStreamWatched(ConversationState c)
        => c.VisibleViewIds.Count > 0
           || c.ViewIds.Any(id => !viewsReportingVisibility.Contains(id))
           || (remoteWatched.Contains(c.Meta.Id) && EffectiveSyncPolicy(c) == ConversationSyncPolicies.Full);

    /// <summary>重看每個有會話的對話有沒有人在看（可見、開關分頁、視窗關閉、觀看集合、開會話之後）</summary>
    private void RefreshStreamWatch()
    {
        if (disposed)
            return;
        var now = Environment.TickCount64;
        bool pending = false;
        foreach (var c in conversations.Values)
        {
            if (c.IsReplica || c.Session is not { } session)
                continue;
            if (IsStreamWatched(c))
            {
                c.UnwatchedAtMs = null;
                if (!c.StreamWatched)
                {
                    c.StreamWatched = true;
                    session.SetWatched(true);
                }
                continue;
            }
            if (!c.StreamWatched)
                continue;
            c.UnwatchedAtMs ??= now;
            if (now - c.UnwatchedAtMs.Value < (long)StreamUnwatchDelay.TotalMilliseconds)
            {
                pending = true;
                continue;
            }
            c.StreamWatched = false;
            session.SetWatched(false);
        }
        if (pending)
            ScheduleStreamWatch();
    }

    private void ScheduleStreamWatch()
    {
        if (streamWatchScheduled)
            return;
        streamWatchScheduled = true;
        _ = Task.Delay(StreamUnwatchDelay + TimeSpan.FromMilliseconds(50)).ContinueWith(_ => host.BeginInvoke(() =>
        {
            streamWatchScheduled = false;
            RefreshStreamWatch();
        }), TaskScheduler.Default);
    }

    /// <summary>新的會話從「有人看」開始（會話自己也是）：接著照現在的情況更新</summary>
    private void OnSessionCreated(ConversationState c)
    {
        c.StreamWatched = true;
        c.UnwatchedAtMs = null;
        RefreshStreamWatch();
    }
}
