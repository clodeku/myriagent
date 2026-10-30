using Myriagent.Agents;

namespace Myriagent.Sync;

/// <summary>
/// 一筆項目改變時要不要寫持久事件、要不要送即時更新（WP4.3 計畫 D21；設計 4.4 的持久化時機；docs/sync-protocol.md 14.3）。
/// <list type="bullet">
/// <item>持久：項目不是 Running（離開 Running、Waiting 卡片建立）立刻寫；還在 Running 時，第一次、狀態從別的變回 Running，或距離上一次持久
/// 滿 <see cref="CheckpointIntervalMs"/> 才寫一筆檢查點。</item>
/// <item>即時：只給 Running 的項目，呼叫端判斷過的條件（<c>liveAllowed</c>：被觀看、協商出 <c>live1</c>、政策允許）成立，
/// 而且照 <see cref="LiveRules.Allows"/> 的頻率（每個對話 ≤ <c>maxLiveHz</c>）。持久與即時互不影響：同一刻兩個都可能成立。</item>
/// <item>暫時不寫、不送的，<see cref="ItemPublishDecision.RecheckAtMs"/> 是下次要再判斷的時間（檢查點到期、或頻率允許的最早時間）。</item>
/// </list>
/// 時間一律是單調時鐘的毫秒，不比較電腦的時鐘
/// </summary>
internal static class PublishTimingRules
{
    /// <summary>被觀看的對話：中樞變更合併後交給引擎的延遲</summary>
    public const int WatchedFlushMs = 50;

    /// <summary>沒被觀看、仍在 Running 的項目：合併後交給引擎的延遲</summary>
    public const int UnwatchedRunningFlushMs = 1_000;

    /// <summary>Running 項目的持久檢查點間隔</summary>
    public const int CheckpointIntervalMs = 10_000;

    /// <summary>重整時中繼資料每批幾個對話</summary>
    public const int MetaSlice = 50;

    /// <summary>重整時項目每頁幾筆（依 Ord 分頁）</summary>
    public const int ItemsPage = 500;

    /// <summary>
    /// 中樞的變化合併多久之後交給引擎（UI 端的發布者）：被觀看的對話、或待送的有要立刻持久的變化（<paramref name="urgent"/>：不是 Running 的項目——
    /// 離開 Running、Waiting 卡片、使用者的訊息——、移除、存檔的中繼資料、佇列）是 <see cref="WatchedFlushMs"/>；沒被觀看、待送的只有執行中項目的串流
    /// 或標頭的變化（狀態文字不上線）是 <see cref="UnwatchedRunningFlushMs"/>
    /// </summary>
    public static int FlushDelayMs(bool watched, bool urgent) => watched || urgent ? WatchedFlushMs : UnwatchedRunningFlushMs;

    /// <param name="status">項目目前的狀態</param>
    /// <param name="lastDurableStatus">上一次寫進 outbox 的持久版本的狀態；沒寫過為 null</param>
    /// <param name="lastDurableMs">上一次寫持久版本的單調時鐘毫秒；沒寫過為 null</param>
    /// <param name="nowMs">現在的單調時鐘毫秒</param>
    /// <param name="liveAllowed">這個對話可以送即時更新（被觀看、<c>live1</c>、政策 full、沒有封鎖、不是草稿或複本、已知）</param>
    /// <param name="lastLiveMs">這個對話上一則即時更新送出的單調時鐘毫秒；沒送過為 null</param>
    /// <param name="maxLiveHz">伺服器給的每個對話即時更新上限（<see cref="SyncLimits.MaxLiveHz"/>）</param>
    /// <param name="liveIntervalMs">即時更新的最小間隔；null＝<see cref="LiveIntervalMs"/>（<paramref name="maxLiveHz"/>）。<c>live2</c> 依項目大小降頻時給
    /// <see cref="LiveRules.DeltaIntervalMs"/></param>
    public static ItemPublishDecision Decide(AgentItemStatus status, AgentItemStatus? lastDurableStatus, long? lastDurableMs, long nowMs,
        bool liveAllowed, long? lastLiveMs, int maxLiveHz, int? liveIntervalMs = null)
    {
        var running = status == AgentItemStatus.Running;
        long? checkpointAt = running && lastDurableStatus == AgentItemStatus.Running && lastDurableMs is { } last
            ? last + CheckpointIntervalMs
            : null;
        var durable = checkpointAt is not { } due || nowMs >= due;

        var wantsLive = running && liveAllowed && maxLiveHz > 0;
        var interval = wantsLive ? liveIntervalMs ?? LiveIntervalMs(maxLiveHz) : 0;
        var live = wantsLive && (lastLiveMs is not { } sent || nowMs - sent >= interval);

        long? recheck = durable ? null : checkpointAt;
        if (wantsLive && !live && lastLiveMs is { } lastLive)
        {
            var liveAt = lastLive + interval;
            recheck = recheck is { } r ? Math.Min(r, liveAt) : liveAt;
        }

        return new ItemPublishDecision(durable, live, recheck);
    }

    /// <summary>即時更新的最小間隔（毫秒，無條件進位）：與 <see cref="LiveRules.Allows"/> 的判斷相同；<paramref name="maxHz"/> ≤0 丟例外</summary>
    public static int LiveIntervalMs(int maxHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHz);
        return (1000 + maxHz - 1) / maxHz;
    }
}

/// <param name="Durable">現在寫一筆持久的 <c>conv.item</c></param>
/// <param name="Live">現在送一則即時更新</param>
/// <param name="RecheckAtMs">之後要再判斷一次的單調時鐘毫秒（還有沒寫的檢查點、或被頻率擋下的即時更新）；不需要為 null</param>
internal readonly record struct ItemPublishDecision(bool Durable, bool Live, long? RecheckAtMs);
