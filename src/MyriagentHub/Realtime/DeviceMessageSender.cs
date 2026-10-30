using Microsoft.AspNetCore.SignalR;

namespace MyriagentHub.Realtime;

/// <summary>
/// 對一條裝置連線送伺服器 → 裝置的訊息（WP4.2 步 12：<c>WatchChanged</c>、<c>SnapshotRequest</c>）。正式的實作經 <see cref="IHubContext{THub}"/>
/// （同 <see cref="DeviceHubRuntime"/> 送 BatchAck 的作法）；測試換成記錄的實作。呼叫端一律先問 <see cref="DeviceConnection.Allows"/>
/// </summary>
internal interface IDeviceMessageSender
{
    Task SendAsync(string connectionId, string method, object message, CancellationToken cancellationToken);
}

/// <summary>經裝置 hub 的 <see cref="IHubContext{THub}"/> 送（連線已經不在時 SignalR 什麼都不做）</summary>
internal sealed class HubDeviceMessageSender(IHubContext<DeviceHub> hub) : IDeviceMessageSender
{
    public Task SendAsync(string connectionId, string method, object message, CancellationToken cancellationToken) =>
        hub.Clients.Client(connectionId).SendAsync(method, message, cancellationToken);
}
