using MyriagentHub.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace MyriagentHub.Realtime;

/// <summary>
/// 裝置 hub 的桌面遠端操作（sync-protocol 14.7，<c>peer1</c>；契約 D50：服務以方法參數注入，這個 partial 檔只用 <c>Context</c> 與注入的服務）。
/// 型別寫完整名稱：方法和型別同名
/// </summary>
internal sealed partial class DeviceHub
{
    /// <summary>
    /// 裝置 → 伺服器（Invoke，<c>peer1</c>）：桌面對其他電腦下的指令。回應同瀏覽器的 <c>Cmd</c>；結果與輸出之後以 <c>PeerReply</c> 送回這台
    /// </summary>
    public Task<Myriagent.Sync.PeerCmdResult> PeerCmd(Myriagent.Sync.PeerCmd? request, ConnectionRegistry registry, PeerCommandHandler handler) =>
        handler.HandleAsync(registry.Get(Context.ConnectionId), request, Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString(), Context.ConnectionAborted);
}
