using MyriagentHub.Data;

namespace MyriagentHub.Sync;

/// <summary>
/// 移除電腦時刪掉它擁有的對話（設計 14.22）。伺服器不寫對話事件（D19），刪除只記在物化表：<c>deleted_at</c> 與 <c>deleted_by</c>＝<see cref="SyncEventStore.ServerAuthor"/>
/// （擁有者裝置寫的 conv.deleted 記的是裝置 id，兩者分得開）。這樣標的對話：
/// <list type="bullet">
/// <item>內容與未讀照「在伺服器上刪除」清掉（同一個交易），之後的事件照樣回 deleted（<see cref="ConversationApplyRules"/> 看 <c>deleted_at</c>）；</item>
/// <item>Pull 不回它的任何事件（<see cref="ConversationPullRules"/>，連 meta 類也不回）；</item>
/// <item>重建不從事件把它推回來（<see cref="RebuildStart"/>／<see cref="RebuildNext"/> 保留墓碑，沒有事件能表達這個狀態）；</item>
/// <item>媒體參照與項目的 blob 引用由 <c>MediaRefIndexer</c> 的對帳刪掉（索引從 events 增量維護，這裡沒有事件）。</item>
/// </list>
/// 冪等：只動還沒刪除的列，重做（重送移除、啟動時補做）不會再改。只在寫入執行緒上呼叫
/// </summary>
internal sealed partial class ConversationMaterializer
{
    /// <summary>刪掉 <paramref name="deviceId"/> 目前擁有、還沒刪除的對話；回傳這次刪的（依 id 排序）</summary>
    public IReadOnlyList<string> DeleteOwnedBy(SyncWriteContext context, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var targets = new List<string>();
        using (var cmd = context.Command(
            "SELECT conversation_id FROM conversations WHERE owner_device_id = $d AND deleted_at IS NULL ORDER BY conversation_id", ("$d", deviceId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                targets.Add(reader.GetString(0));
        }

        foreach (var conversationId in targets)
        {
            context.Execute("UPDATE conversations SET deleted_at = $now, deleted_by = $by WHERE conversation_id = $c",
                ("$now", context.Now), ("$by", SyncEventStore.ServerAuthor), ("$c", conversationId));
            PurgeContent(context, conversationId);
            context.Execute("DELETE FROM conversation_unread WHERE conversation_id = $c", ("$c", conversationId));
            // 重建中的不在這裡拿掉：下一塊（RebuildNext）看到墓碑就收尾，重建的迴圈照常往下走
            var entry = ReadEntry(context, conversationId);
            context.AfterCommit(() =>
            {
                directory.Apply(entry);
                feed.Post(new ConversationChange(conversationId, ConversationChangeKind.Deleted, entry.LastSeq, SyncEventStore.ServerAuthor));
            });
        }

        return targets;
    }

    /// <summary>這個對話是移除電腦時伺服器刪的（<c>deleted_by</c>＝<see cref="SyncEventStore.ServerAuthor"/>）</summary>
    internal static bool IsDeletedByServer(SyncWriteContext context, string conversationId) =>
        context.Scalar("SELECT deleted_by FROM conversations WHERE conversation_id = $c AND deleted_at IS NOT NULL", ("$c", conversationId)) is string by
        && by == SyncEventStore.ServerAuthor;

    /// <summary>重建碰到伺服器刪的對話：保留墓碑、不重播（內容在刪除時已清），解除「重建中」</summary>
    private RebuildStep KeepServerTombstone(SyncWriteContext context, string conversationId, int read)
    {
        PurgeContent(context, conversationId);
        rebuilding.Remove(conversationId);
        var entry = ReadEntry(context, conversationId);
        context.AfterCommit(() =>
        {
            directory.Apply(entry);
            directory.MarkRebuildDone(conversationId);
        });
        return new RebuildStep(true, read);
    }
}
