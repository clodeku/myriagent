namespace MyriagentHub.Logs;

/// <summary>
/// 逐字稿與檔案日誌的設定（appsettings 的 <c>Hub:Logs</c>；第 5 階段計畫 4.1、5.6）。WP5.1 只有開關；上限（單次附加 32 MiB 等）由 WP5.2 加在這裡
/// </summary>
internal sealed class LogOptions
{
    /// <summary>
    /// 宣告 <c>log1</c>、日誌端點回應（預設 false＝休眠：不登記功能、端點一律 404 <c>not_available</c>）。計畫 WP5.3 結束時改預設；
    /// 測試以 <c>HubTestHost</c> 的設定或 <c>MyriagentHubProcess</c> 的環境變數（<c>Hub__Logs__Declare=true</c>）打開
    /// </summary>
    public bool Declare { get; set; }
}
