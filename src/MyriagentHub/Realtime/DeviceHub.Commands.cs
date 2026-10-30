using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 裝置 hub 的遠端指令方法（WP4.5 計畫步 7；sync-protocol 14.6；契約 D50：服務以方法參數注入，這個 partial 檔只用 <c>Context</c> 與注入的服務，
/// 不碰主檔的欄位）。型別寫完整名稱：方法和型別同名。只收已證明、協商出 <c>cmd1</c> 的連線（<see cref="CommandResults"/> 檢查）
/// </summary>
internal sealed partial class DeviceHub
{
    /// <summary>裝置 → 伺服器（Invoke，<c>cmd1</c>）：遠端指令的最終結果。格式不合格回 <c>invalid</c>；不是這台的 cid 靜靜略過；寫進 sync.db 之後才回</summary>
    public Task CommandResult(Myriagent.Sync.CommandResult? result, ConnectionRegistry registry, CommandResults results) =>
        results.ReceiveResultAsync(registry.Get(Context.ConnectionId), result);

    /// <summary>
    /// 裝置 → 伺服器（Invoke，<c>cmd1</c>）：指令的輸出。不合格的丟掉、不回錯誤；合格的只轉給發起的瀏覽器連線、不保存。
    /// 檔案指令的輸出改寫（WP4.7，登記媒體參照）完成之後才回
    /// </summary>
    public Task CommandOutput(Myriagent.Sync.CommandOutput? output, ConnectionRegistry registry, CommandResults results) =>
        results.ReceiveOutputAsync(registry.Get(Context.ConnectionId), output, Context.ConnectionAborted);
}
