using Myriagent.Sync;
using MyriagentHub.Sync;
using Microsoft.AspNetCore.SignalR;

namespace MyriagentHub.Realtime;

/// <summary>裝置 hub 的輸入框草稿（draft1，docs/sync-protocol.md 14.17；服務以方法參數注入，同 DeviceHub.Schedules.cs）</summary>
internal sealed partial class DeviceHub
{
    /// <summary>
    /// 裝置 → 伺服器（Invoke，<c>draft1</c>）：這台某個對話輸入框打到一半的字（空白＝清掉）。來源名稱是這台在裝置名冊上的名字。
    /// 連線沒協商出 draft1、內容不合格回 <c>invalid</c>（裝置只記錄、不重連）
    /// </summary>
    public Task ComposerDraftPublish(Myriagent.Sync.ComposerDraftPublish? request, ConnectionRegistry registry, ComposerDraftStore drafts, DeviceDirectory directory)
    {
        if (registry.Get(Context.ConnectionId) is not { IsProven: true } connection || !connection.Allows(DeviceHubMethods.ComposerDraftPublish))
            throw new HubException(SyncErrorCodes.Invalid);
        if (ComposerDraftRules.CheckPublish(request) is { } problem)
        {
            log.LogDebug("裝置 {DeviceId} 的 ComposerDraftPublish 不合格：{Problem}", connection.DeviceId, problem);
            throw new HubException(SyncErrorCodes.Invalid);
        }
        drafts.Publish(request!.ConversationId, request.Text, directory.Get(connection.DeviceId)?.Name, ComposerDraftFanout.DeviceOrigin(connection.ConnectionId));
        return Task.CompletedTask;
    }
}
