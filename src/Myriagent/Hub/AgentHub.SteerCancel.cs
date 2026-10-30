using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 取消插進這一輪的訊息（web-protocol 的 composer.steered、cancelSteer、steerCancelled；2026-10-25）：執行中送出的訊息插進這一輪之後，
/// 輸入區上方留一行「已插入這一輪」可以取消，CLI 還沒讀走（cancel_async_message 回 cancelled）就把那則從對話移除、文字與附件放回輸入框。
/// 只有最近插入的那一則、只有本機送的、只到這一輪結束為止
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>最近插進這一輪、還可以試著取消的那則；這一輪結束、又插了一則、試過取消就清掉</summary>
        public SteerCancelTarget? CancellableSteer;
    }

    /// <param name="ItemId">畫面上那則使用者項目</param>
    /// <param name="Uuid">寫給 CLI 的 uuid（AgentTurnInput.MessageUuid）</param>
    /// <param name="Text">原文（放回輸入框）</param>
    private sealed record SteerCancelTarget(string ItemId, string Uuid, string Text);

    /// <summary>插入成功：記下最近這一則（會話支援取消、本機送的才記）</summary>
    private void RememberSteer(ConversationState c, AgentChatItem item, string uuid, QueuedMessage message)
    {
        c.CancellableSteer = c.Session is { CanCancelSteer: true } && message.RemoteOrigin == null
            ? new SteerCancelTarget(item.Id, uuid, message.Text)
            : null;
    }

    /// <summary>這一輪結束（HookTurnCompleted）：插入的那則一定已經讀走或作廢了</summary>
    private void ForgetSteer(ConversationState c)
    {
        if (c.CancellableSteer == null)
            return;
        c.CancellableSteer = null;
        PostComposer(c);
    }

    private async Task CancelSteerAsync(IAgentView view, ConversationState? c, string? itemId)
    {
        if (c == null || c.CancellableSteer is not { } target || target.ItemId != itemId || c.Session is not { } session)
            return;
        c.CancellableSteer = null;
        PostComposer(c);
        bool cancelled;
        try
        {
            cancelled = await session.CancelSteerAsync(target.Uuid);
        }
        catch (Exception ex)
        {
            Log.Warn($"取消插入的訊息失敗：{ex.Message}");
            cancelled = false;
        }
        if (disposed || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        if (!cancelled)
        {
            Toast("代理已經讀到這則訊息，取消不了", view: view);
            return;
        }
        var item = c.Items.FirstOrDefault(i => i.Id == target.ItemId);
        if (item != null)
        {
            RemoveItem(c, item);
            foreach (var attachment in item.Attachments)
            {
                if (!c.PendingAttachments.Any(a => a.Id == attachment.Id))
                    c.PendingAttachments.Add(attachment);
            }
            SaveConversation(c, saveItems: true);
        }
        PostComposer(c);
        Post(view, new JObject { ["type"] = "steerCancelled", ["conversationId"] = c.Meta.Id, ["text"] = target.Text });
    }

    /// <summary>composer.steered：可以取消的那則（沒有是 null）</summary>
    private static JObject? RenderSteered(ConversationState c)
        => c.CancellableSteer is { } t && c.Running
            ? new JObject { ["id"] = t.ItemId, ["text"] = TextRules.FirstLine(t.Text) }
            : null;
}
