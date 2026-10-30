namespace Myriagent.Sync;

/// <summary>
/// 同步「安定」的快照（WP4.9 計畫 D1、D2：探針的 S6 閒置取樣與 fake-device 的回補等待共用同一組條件）。引擎那一側的欄位由
/// <see cref="ISyncSettleProbe"/> 在引擎執行緒讀；<paramref name="BackfillDone"/> 由呼叫端在 UI 執行緒以發布者的回補進度填
/// （進度是 null＝做完；沒有掛發布者也算做完），引擎回的快照一律是 false
/// </summary>
/// <param name="Live">引擎在 Live</param>
/// <param name="InFlight">送出、還沒等到 BatchAck 的批次數</param>
/// <param name="Refreshing">重整的中繼資料還沒回完</param>
/// <param name="ReconcileActive">重整佇列有正在處理的對話</param>
/// <param name="ReconcileQueue">重整佇列裡還沒開始的對話數</param>
/// <param name="OutboxSettled">outbox 每一列都已確認或被拒</param>
/// <param name="BackfillDone">發布者的回補進度是 null</param>
internal sealed record SyncSettleSnapshot(bool Live, int InFlight, bool Refreshing, bool ReconcileActive, int ReconcileQueue, bool OutboxSettled, bool BackfillDone);

/// <summary>同步安定的判斷（純函式；探針與 fake-device 都只經這裡判斷，條件只有一份）</summary>
internal static class SyncSettleRules
{
    /// <summary>在 Live、沒有在途的批次、不在重整、重整佇列空而且沒有進行中的對話、outbox 全部確認或被拒、回補做完：七個條件全部成立才算安定</summary>
    public static bool IsSettled(SyncSettleSnapshot s) =>
        s.Live && s.InFlight == 0 && !s.Refreshing && !s.ReconcileActive && s.ReconcileQueue == 0 && s.OutboxSettled && s.BackfillDone;

    /// <summary>安定的狀態已經連續維持 <paramref name="quiet"/>（剛好等於也算）</summary>
    public static bool QuietEnough(TimeSpan settledFor, TimeSpan quiet) => settledFor >= quiet;
}
