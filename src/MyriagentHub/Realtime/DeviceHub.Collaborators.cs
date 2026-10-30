using Myriagent.Sync;
using Microsoft.AspNetCore.SignalR;

namespace MyriagentHub.Realtime;

/// <summary>裝置 hub 的多人協作（collab1，docs/sync-protocol.md 14.14；服務以方法參數注入）</summary>
internal sealed partial class DeviceHub
{
    /// <summary>
    /// 裝置 → 伺服器（Invoke，<c>collab1</c>）：桌面替擁有者送的協作人管理指令（已配對就是擁有者，擁有者 2026-10-22「已配對就能操作」）。
    /// 連線沒協商出 collab1、請求是 null 回 <c>invalid</c>（HubException）；欄位不合格照名冊的規則回 <see cref="CollabCmdResult"/>
    /// </summary>
    public async Task<CollabCmdResult> CollabCmd(CollabCmdRequest? request, ConnectionRegistry registry, CollaboratorCommandService commands)
    {
        if (registry.Get(Context.ConnectionId) is not { IsProven: true } connection || !connection.Allows(DeviceHubMethods.CollabCmd) || connection.UpdateOnly)
            throw new HubException(SyncErrorCodes.Invalid);
        if (request is null || !CollaboratorCommands.IsCollaboratorCommand(request.Cmd))
            throw new HubException(SyncErrorCodes.Invalid);
        log.LogInformation("裝置 {DeviceId} 送了協作人的管理指令 {Cmd}", connection.DeviceId, request.Cmd);
        return await commands.HandleDeviceAsync(connection.DeviceId, request, Context.ConnectionAborted);
    }
}
