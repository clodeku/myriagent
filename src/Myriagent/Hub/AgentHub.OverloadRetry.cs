using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>
/// API 伺服器暫時無法服務時自動重試（規則在 OverloadRetryRules；2026-10-24 使用者要求）：一輪因為 Claude 的 529 Overloaded 或 Antigravity 的 503 UNAVAILABLE 失敗
/// （AgentTurnCompletedEvent.ServerBusy），CompleteTurn 照常加錯誤，再加一則「60 秒後自動重試」的提示、排定重試；時間到了還沒開始別的一輪，就隱藏送出接續訊息
/// （同換帳戶的接續：不建使用者項目，改加一則提示），請代理從中斷處接著做。等待期間任何一輪開始（使用者送出、CLI 自己開一輪、遠端送來）
/// 都取消這次重試；對話被刪除、app 結束也不送。等待只在記憶體：app 在這 60 秒內重啟就不會重試（running.json 已經在這一輪失敗時刪掉）
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>連續自動重試了幾次（排定時記下；使用者自己送出、或一輪不是因為伺服器暫時無法服務而結束時歸零）</summary>
        public int OverloadRetries;

        /// <summary>排定中的自動重試（0＝沒有）：時間到了只在還是同一張時才送；任何一輪開始（HookTurnStarted）時清掉</summary>
        public int OverloadRetryTicket;
    }

    private int overloadRetrySeq;
    private TimeSpan? overloadRetryDelayOverride;

    /// <summary>這一輪失敗到自動重試之間等多久（測試設值時覆蓋所有種類的等待時間；未設時依 OverloadRetryRules.DelayOf 決定）</summary>
    internal TimeSpan OverloadRetryDelay
    {
        get => overloadRetryDelayOverride ?? OverloadRetryRules.Delay;
        set => overloadRetryDelayOverride = value;
    }

    /// <summary>取得指定忙碌／瞬斷種類實際使用的重試等待時間</summary>
    internal TimeSpan GetOverloadRetryDelay(AgentServerBusy busy)
        => overloadRetryDelayOverride ?? OverloadRetryRules.DelayOf(busy);

    /// <summary>
    /// 這一輪結束（CompleteTurn）時要排第幾次自動重試（Attempt；不重試是 null），與是不是因為連續重試到上限而不再重試（GaveUp，要加提示）。
    /// 順便把連續次數歸零或記下（排定的那一次算進去：等待期間使用者自己送出時 SendCoreAsync 再歸零）
    /// </summary>
    private (int? Attempt, bool GaveUp) PlanOverloadRetry(ConversationState c, AgentTurnCompletedEvent turn, bool stopRequested, bool accountChanging)
    {
        bool failed = turn.Outcome == AgentTurnOutcome.Failed;
        var attempt = OverloadRetryRules.NextAttempt(failed, turn.ServerBusy, stopRequested, accountChanging, c.IsReplica, c.OverloadRetries);
        bool gaveUp = OverloadRetryRules.GaveUp(failed, turn.ServerBusy, stopRequested, accountChanging, c.IsReplica, c.OverloadRetries);
        c.OverloadRetries = attempt ?? 0;
        return (attempt, gaveUp);
    }

    /// <summary>排定第 attempt 次自動重試（CompleteTurn 已加好提示）：等待時間到之後回 UI 執行緒送出</summary>
    private void ScheduleOverloadRetry(ConversationState c, AgentServerBusy busy, int attempt)
    {
        int ticket = ++overloadRetrySeq;
        c.OverloadRetryTicket = ticket;
        var delay = GetOverloadRetryDelay(busy);
        Log.Info($"對話 {c.Meta.Id} 的這一輪因為 API 暫時無法服務或網路瞬斷（{busy}）失敗，{OverloadRetryRules.FormatDelay(delay)}後自動重試（第 {attempt} 次）");
        _ = Task.Delay(delay).ContinueWith(_ => host.BeginInvoke(() => FireOverloadRetry(c, ticket, busy, attempt, delay)), TaskScheduler.Default);
    }

    /// <summary>
    /// 時間到了：這段期間沒有開始別的一輪、對話還在、app 沒在結束，就隱藏送出接續訊息。CLI 正在更新或這個對話正在換版時
    /// 隱藏送出不排隊（DeferSendForCliUpdate、DeferSendDuringRestart），改成再等一次、不算次數
    /// </summary>
    private void FireOverloadRetry(ConversationState c, int ticket, AgentServerBusy busy, int attempt, TimeSpan delay)
    {
        if (c.OverloadRetryTicket != ticket)
            return;
        c.OverloadRetryTicket = 0;
        if (disposed || c.Running || c.IsReplica || !IsLive(c))
            return;
        if (IsCliUpdateBlocking(c.Meta.Provider) || IsRestartInFlight(c))
        {
            Log.Info($"對話 {c.Meta.Id} API 伺服器暫時無法服務後的自動重試（第 {attempt} 次）遇到 CLI 更新或換版，再等一次");
            ScheduleOverloadRetry(c, busy, attempt);
            return;
        }
        Log.Info($"對話 {c.Meta.Id} API 伺服器暫時無法服務（{busy}）後自動重試（第 {attempt} 次）");
        _ = SendCoreAsync(c, OverloadRetryRules.Continuation(busy, attempt, delay), new List<AgentAttachment>(),
            hiddenNotice: OverloadRetryRules.RetryNotice(busy, attempt), hiddenNoticeKind: null);
    }
}
