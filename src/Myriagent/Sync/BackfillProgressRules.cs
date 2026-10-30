namespace Myriagent.Sync;

/// <summary>
/// 回補因磁碟保護上限暫停的時間（WP4.3 計畫 D24；給 WP4.6 顯示）：<paramref name="SinceMs"/> 是這一次暫停開始的時間（沒有在暫停是 null），
/// <paramref name="TotalMs"/> 是之前各次暫停的合計（不含進行中的這一次）。時間是引擎的單調時鐘，不跨電腦比較
/// </summary>
internal sealed record BackfillPause(BackfillHold Reason, long? SinceMs, long TotalMs);

/// <summary>回補的進度與暫停時間（D24、D42；web-protocol 的 <c>sync.backfill</c>）。純函式</summary>
internal static class BackfillProgressRules
{
    /// <summary>剩下的對話數與約略的位元組；都是 0（回補完成或沒有在回補）回 null（不輸出）。負數當成 0</summary>
    public static SyncBackfillProgress? Progress(int conversationsLeft, long bytesLeft)
    {
        var conversations = Math.Max(0, conversationsLeft);
        var bytes = Math.Max(0, bytesLeft);
        return conversations == 0 && bytes == 0 ? null : new SyncBackfillProgress(conversations, bytes);
    }

    /// <summary>
    /// 依這一輪的磁碟保護判斷更新暫停時間：開始暫停記 <c>SinceMs</c>；暫停中原因改變只換原因、不重設起點；
    /// 恢復時把這一次的長度累加進 <c>TotalMs</c>、<c>SinceMs</c> 歸 null（總長保留）。從沒暫停過而且沒有暫停回 null
    /// </summary>
    public static BackfillPause? Advance(BackfillPause? current, BackfillHold hold, long nowMs)
    {
        if (hold != BackfillHold.None)
        {
            if (current == null)
                return new BackfillPause(hold, nowMs, 0);
            if (current.SinceMs == null)
                return current with { Reason = hold, SinceMs = nowMs };
            return current.Reason == hold ? current : current with { Reason = hold };
        }

        if (current?.SinceMs is not { } since)
            return current;
        return current with { SinceMs = null, TotalMs = current.TotalMs + Math.Max(0, nowMs - since) };
    }
}
