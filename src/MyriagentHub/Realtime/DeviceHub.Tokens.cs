using Myriagent.Sync;
using MyriagentHub.Sync;
using Microsoft.AspNetCore.SignalR;

namespace MyriagentHub.Realtime;

/// <summary>
/// 裝置 hub 的各帳戶 token 計數（sync-protocol 14.9，<c>tok1</c>；服務以方法參數注入，同 DeviceHub.Stats.cs）。型別寫完整名稱：方法和型別同名
/// </summary>
internal sealed partial class DeviceHub
{
    /// <summary>
    /// 裝置 → 伺服器（Invoke，<c>tok1</c>）：這台各邏輯帳戶、各日期的計數寫進 token_tallies（full 整份取代、否則逐格取代）。
    /// 連線沒協商出 tok1、內容不合格回 <c>invalid</c>（裝置只記錄、不重連）
    /// </summary>
    public async Task TokensPublish(Myriagent.Sync.TokensPublish? request, ConnectionRegistry registry, TokenTallies tallies)
    {
        if (registry.Get(Context.ConnectionId) is not { IsProven: true } connection || !connection.Allows(DeviceHubMethods.TokensPublish))
            throw new HubException(SyncErrorCodes.Invalid);
        if (TokenTallySyncRules.Check(request) is { } problem)
        {
            log.LogDebug("裝置 {DeviceId} 的 TokensPublish 不合格：{Problem}", connection.DeviceId, problem);
            throw new HubException(SyncErrorCodes.Invalid);
        }
        await tallies.PublishAsync(connection.DeviceId, request!, Context.ConnectionAborted);
    }
}
