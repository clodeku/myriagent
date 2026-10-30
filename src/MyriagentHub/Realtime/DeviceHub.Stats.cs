using Myriagent.Sync;
using Microsoft.AspNetCore.SignalR;

namespace MyriagentHub.Realtime;

/// <summary>
/// 裝置 hub 的各電腦即時狀態（sync-protocol 14.8，<c>stat1</c>；服務以方法參數注入，同 DeviceHub.Peer.cs）。型別寫完整名稱：方法和型別同名
/// </summary>
internal sealed partial class DeviceHub
{
    /// <summary>
    /// 裝置 → 伺服器（Invoke，<c>stat1</c>）：這台這一刻的狀態放上看板（只在記憶體），回現在有沒有別人在看。
    /// 連線沒協商出 stat1、內容不合格回 <c>invalid</c>；太密的不收、照樣回 watched（裝置以這次呼叫的往返時間當延遲）
    /// </summary>
    public Myriagent.Sync.StatsPublishResult StatsPublish(Myriagent.Sync.StatsPublish? request, ConnectionRegistry registry, DeviceStatsBoard board)
    {
        if (registry.Get(Context.ConnectionId) is not { IsProven: true } connection || !connection.Allows(DeviceHubMethods.StatsPublish))
            throw new HubException(SyncErrorCodes.Invalid);
        var problem = request is null ? "沒有內容" : DeviceStatsRules.Check(request.Stats);
        if (request is null || problem is not null)
        {
            log.LogDebug("裝置 {DeviceId} 的 StatsPublish 不合格：{Problem}", connection.DeviceId, problem);
            throw new HubException(SyncErrorCodes.Invalid);
        }
        var outcome = board.Publish(connection.ConnectionId, connection.DeviceId, request.Stats, request.Watch);
        return new Myriagent.Sync.StatsPublishResult(outcome.Watched);
    }
}
