using Myriagent.Core;
using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>
/// 代理主動通知使用者（Claude 的 PushNotification 工具，AgentNotifyUserEvent；2026-11-25 使用者要求）：CLI 自己只送終端機通知，以及開著
/// Remote Control 時的手機推播，app 的對話兩者通常都沒有，有時限的事（例如人工驗證）使用者就看不到。這裡照自己的通知送：
/// 系統通知、語音、閃爍（NotifyAttention），同步出去給瀏覽器版與其他電腦（conv.attention 的 notice）
/// </summary>
internal sealed partial class AgentHub
{
    private void OnNotifyUser(ConversationState c, string message)
    {
        Log.Info($"對話 {c.Meta.Id} 的代理通知使用者（{message.Length} 字）");
        NotifyAttention(c, "代理通知你", message, ConversationAttentionKinds.Question, itemId: null,
            VoiceNotifyRules.Line(full => VoiceNotifyRules.Notice(c.Meta.Title, full ? message : null)) with { Notice = true });
    }

    /// <summary>
    /// 其他電腦的對話的代理通知使用者（複本，ReplicaDelta.Notices）：這台也跳系統通知、閃那個對話。不念——那台的喇叭已經念了，
    /// 兩台放在一起會疊在一起。太舊的（離線期間累積的、剛配對拉到的歷史）不跳
    /// </summary>
    private void ApplyReplicaNotice(ReplicaNotice notice)
    {
        if (!conversations.TryGetValue(notice.ConversationId, out var c) || !c.IsReplica
            || !ReplicaNoticeRules.IsFresh(notice.CreatedAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            return;
        FlashConversation(c.Meta.Id, speaking: false);
        if (c.VisibleViewIds.Any(host.IsViewForeground))
            return;
        host.RequestAttention(c.ViewIds);
        if (settings.Notifications)
            host.Notify("其他電腦的代理通知你", notice.Text, c.Meta.Id);
    }
}

/// <summary>其他電腦的代理通知要不要在這台跳</summary>
internal static class ReplicaNoticeRules
{
    /// <summary>伺服器收到通知之後這段時間內才跳（兩邊的時鐘可能差一點，所以前後都算）</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public static bool IsFresh(long createdAtMs, long nowMs) => Math.Abs(nowMs - createdAtMs) <= (long)MaxAge.TotalMilliseconds;
}
