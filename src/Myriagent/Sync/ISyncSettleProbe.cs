namespace Myriagent.Sync;

/// <summary>
/// 讀同步安定的快照（WP4.9 計畫 D2；<see cref="SyncEngine"/> 實作）。另開一個小介面、不加在 <see cref="ISyncEngine"/>：測試的假引擎與其他工作包都不必跟著改；
/// 探針（ProbeRunner）以 <c>app.Sync.Engine as ISyncSettleProbe</c> 取得，未配對時沒有引擎、不建立任何同步元件。只用 BCL 與桌面的型別
/// </summary>
internal interface ISyncSettleProbe
{
    /// <summary>
    /// 在引擎執行緒讀引擎那一側的欄位（任何執行緒都可以呼叫；只把讀取排進引擎執行緒）。<see cref="SyncSettleSnapshot.BackfillDone"/> 一律是 false，由呼叫端填。
    /// 引擎執行緒已經結束時不等，回一份不在 Live 的快照
    /// </summary>
    Task<SyncSettleSnapshot> SettleSnapshotAsync();
}
