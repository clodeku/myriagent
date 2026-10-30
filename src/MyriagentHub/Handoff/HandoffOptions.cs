namespace MyriagentHub.Handoff;

/// <summary>
/// 租約與交接的設定（appsettings 的 <c>Hub:Handoff</c>；第 5 階段計畫 4.1、D1）。WP5.1 只有開關；期限等由 WP5.5 視需要加在這裡
/// </summary>
internal sealed class HandoffOptions
{
    /// <summary>
    /// 宣告 <c>hand1</c>（預設 false＝休眠：不登記功能，瀏覽器的 takeover／handoffCancel／handoffPreview 回 not_available、標頭的 handoff.can 是 false）。
    /// 只在 <c>Hub:Logs:Declare</c> 也打開時才宣告（hand1 要靠日誌驗證逐字稿）。計畫 WP5.10 改預設
    /// </summary>
    public bool Declare { get; set; }
}
