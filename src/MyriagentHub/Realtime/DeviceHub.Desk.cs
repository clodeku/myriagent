using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 裝置 hub 的遠端桌面（docs/sync-protocol.md 14.16，<c>desk1</c>；契約 D50：服務以方法參數注入）。只收已證明、協商出 <c>desk1</c> 的連線（<see cref="DeskRelays"/> 檢查）
/// </summary>
internal sealed partial class DeviceHub
{
    /// <summary>裝置 → 伺服器（Invoke，<c>desk1</c>）：被看的電腦送影格與狀態、看的電腦送輸入與確認；伺服器轉給另一邊。<c>keep</c> 是 false 時送的一方停止</summary>
    public Task<Myriagent.Sync.DeskUpResult> DeskUp(Myriagent.Sync.DeskPacket? packet, ConnectionRegistry registry, DeskRelays relays) =>
        relays.ReceiveFromDeviceAsync(registry.Get(Context.ConnectionId), packet, Context.ConnectionAborted);
}
