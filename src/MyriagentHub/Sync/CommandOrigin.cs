using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>
/// 遠端指令的發起者（WP4.5 起只有瀏覽器；桌面遠端操作加上裝置，sync-protocol 14.7、擁有者 2026-10-22 決定「已配對就能操作」）：
/// 路由、稽核與回覆依它決定——瀏覽器的回覆入那條連線的送出佇列，裝置的以 <c>PeerReply</c> 送回那台電腦（<see cref="CommandReplies"/>）
/// </summary>
/// <param name="ConnectionId">發起的連線（瀏覽器 hub 或裝置 hub 的 SignalR 連線 id）</param>
/// <param name="Ip">連線時的用戶端 IP（稽核）</param>
internal abstract record CommandOrigin(string ConnectionId, string? Ip)
{
    /// <summary>稽核的 actor（<see cref="AuditActors"/>）</summary>
    public abstract string AuditActor { get; }

    /// <summary>稽核的 actor id（瀏覽器是工作階段的短 id、裝置是 deviceId）</summary>
    public abstract string? AuditActorId { get; }

    /// <summary>發起的電腦（裝置發起時）；瀏覽器是 null</summary>
    public virtual string? DeviceId => null;
}

/// <summary>擁有者的瀏覽器（cookie 工作階段）</summary>
internal sealed record BrowserCommandOrigin(BrowserConnection Connection) : CommandOrigin(Connection.ConnectionId, Connection.Ip)
{
    /// <summary>協作人的瀏覽器（多人協作）記成 collaborator</summary>
    public override string AuditActor => Connection.IsCollaborator ? AuditActors.Collaborator : AuditActors.Owner;

    public override string? AuditActorId => SessionRules.ShortId(Connection.SessionId);
}

/// <summary>另一台已配對的桌面（裝置 hub 的 <c>PeerCmd</c>；連線已證明、協商出 <c>peer1</c>）</summary>
internal sealed record DeviceCommandOrigin(string Device, string Connection, string? DeviceIp) : CommandOrigin(Connection, DeviceIp)
{
    public override string AuditActor => AuditActors.Device;

    public override string? AuditActorId => Device;

    public override string? DeviceId => Device;
}
