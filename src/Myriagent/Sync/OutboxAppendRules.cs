namespace Myriagent.Sync;

/// <summary>
/// 寫進 outbox 之前的外框檢查（D55；sync-protocol 14.2；<see cref="SyncStore.AppendMany"/> 呼叫，不合格丟 <see cref="ArgumentException"/>、整批不寫）：
/// <list type="bullet">
/// <item>沒有對話的（第 3 階段的事件）：<c>epoch</c> 也是 null，種類不是對話種類。</item>
/// <item>有對話的：外框照 <see cref="ConversationEventRules.CheckEvent"/>（對話種類、線上的對話 id、epoch ≥ 1、串流 <c>c:{conversationId}</c>）；
/// 合併鍵（有的話）要屬於這個對話（<see cref="KeyBelongsTo"/>），不同對話的事件不會互相取代。</item>
/// <item>伺服器寫的種類（第 5 階段的 <c>conv.lease</c>，<see cref="ConversationEventKinds.IsServerAuthored"/>）裝置不能送。</item>
/// </list>
/// 不合格都是寫入端的程式錯誤；payload 的內容另由寫入端（WP4.3）以 <see cref="ConversationEventRules.Validate(string?, string?)"/> 檢查
/// </summary>
internal static class OutboxAppendRules
{
    /// <summary>合格回 null，否則回原因（記錄用，不含 payload）</summary>
    public static string? Problem(OutboxAppend item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ConversationId is null)
        {
            if (item.Epoch is not null)
                return "沒有對話卻帶了 epoch";
            return ConversationEventKinds.IsConversationKind(item.Kind) ? "對話事件沒有 conversationId" : null;
        }

        if (ConversationEventKinds.IsServerAuthored(item.Kind))
            return "伺服器寫的對話事件（conv.lease）裝置不能送";
        if (ConversationEventRules.CheckEvent(item.Kind, item.Stream, item.ConversationId, item.Epoch) is not null)
            return "對話事件的外框不合格（對話種類、32 個小寫十六進位的 conversationId、epoch ≥ 1、串流 c:{conversationId}）";
        if (item.CoalesceKey is { } key && !KeyBelongsTo(key, item.ConversationId))
            return "合併鍵與對話不符";
        return null;
    }

    /// <summary>
    /// 合併鍵屬於這個對話：<c>{用途}:{conversationId}</c> 或 <c>{用途}:{conversationId}:{…}</c>（例如 <c>meta:{id}</c>、<c>item:{id}:{itemId}</c>；
    /// 用途不可為空，比對區分大小寫）
    /// </summary>
    public static bool KeyBelongsTo(string coalesceKey, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(coalesceKey);
        ArgumentNullException.ThrowIfNull(conversationId);
        var colon = coalesceKey.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || conversationId.Length == 0)
            return false;
        var rest = coalesceKey.AsSpan(colon + 1);
        var end = rest.IndexOf(':');
        var segment = end < 0 ? rest : rest[..end];
        return segment.SequenceEqual(conversationId);
    }
}
