using Myriagent.Sync;
using MyriagentHub.Sync;
using Microsoft.AspNetCore.SignalR;

namespace MyriagentHub.Realtime;

/// <summary>裝置 hub 的各電腦的排程（sched1，docs/sync-protocol.md 14.12；服務以方法參數注入，同 DeviceHub.Tokens.cs）</summary>
internal sealed partial class DeviceHub
{
    /// <summary>
    /// 裝置 → 伺服器（Invoke，<c>sched1</c>）：這台排程頁的摘要寫進 device_schedules（整份取代）。
    /// 連線沒協商出 sched1、內容不合格回 <c>invalid</c>（裝置只記錄、不重連）
    /// </summary>
    public async Task SchedulesPublish(Myriagent.Sync.SchedulesPublish? request, ConnectionRegistry registry, DeviceScheduleStore schedules)
    {
        if (registry.Get(Context.ConnectionId) is not { IsProven: true } connection || !connection.Allows(DeviceHubMethods.SchedulesPublish))
            throw new HubException(SyncErrorCodes.Invalid);
        if (ScheduleSyncRules.Check(request) is { } problem)
        {
            log.LogDebug("裝置 {DeviceId} 的 SchedulesPublish 不合格：{Problem}", connection.DeviceId, problem);
            throw new HubException(SyncErrorCodes.Invalid);
        }
        await schedules.PublishAsync(connection.DeviceId, request!, Context.ConnectionAborted);
    }
}
