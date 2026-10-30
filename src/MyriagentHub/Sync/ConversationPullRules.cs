using Myriagent.Sync;

namespace MyriagentHub.Sync;

/// <summary>
/// 拉取時哪些對話事件不回（WP4.2 計畫步 4；D17、答 4）：已在伺服器上刪除、或目前政策不是 <c>full</c> 的對話，不回 item 類事件；meta 類照回
/// （副本才知道它被刪或降級）。只看對話<b>目前</b>的狀態，不加 purge_seq：改回 full 之後新舊 item 類事件都拉得到，副本依 seq 套用、收斂到擁有者的內容（P10）。
/// 被過濾的事件不回，裝置的游標只是停在最後一筆回傳的事件，不會跳過也不會打轉（F29）。第 3 階段的類別不受影響。
/// 例外：移除電腦時伺服器刪掉的對話（<c>deleted_by</c> 是 <see cref="SyncEventStore.ServerAuthor"/>，設計 14.22）連 meta 類也不回——
/// 沒有 conv.deleted 可以讓副本知道它被刪（伺服器不寫對話事件），回了只會讓從頭拉的電腦建出已經不存在的副本。
/// </summary>
internal static class ConversationPullRules
{
    /// <summary>
    /// <see cref="EventLog.Pull"/> 兩種游標共用的 SQL 條件（接在 WHERE 之後）。和 <see cref="Hidden"/> 同一個判斷：
    /// item 類、而且這個對話已刪除或政策不是 full；或這個對話是移除電腦時伺服器刪的（不分類別）。<c>IFNULL</c> 讓 conversation_id 是 null 的事件照回
    /// （第 3 階段的類別都是 null）。<c>sync_policy</c> 物化時已經 <see cref="ConversationSyncPolicies.Interpret"/> 過（只會是 full、metadata、paused）
    /// </summary>
    public const string VisibleSql =
        " AND NOT (cls = '" + SyncClasses.Item + "' AND IFNULL(conversation_id IN (SELECT conversation_id FROM conversations WHERE deleted_at IS NOT NULL OR sync_policy <> '"
        + ConversationSyncPolicies.Full + "'), 0))"
        + " AND NOT IFNULL(conversation_id IN (SELECT conversation_id FROM conversations WHERE deleted_by = '" + SyncEventStore.ServerAuthor + "'), 0)";

    /// <summary>
    /// 這一筆事件拉取時不回嗎（測試的答案與 sync-protocol 14.2 的說明）
    /// </summary>
    /// <param name="cls">事件的類別</param>
    /// <param name="policy">對話目前的政策（列上的值）；null＝沒有這個對話的列</param>
    /// <param name="deleted">對話已在伺服器上刪除</param>
    /// <param name="deletedByServer">移除電腦時伺服器刪的（<c>deleted_by</c> 是 <see cref="SyncEventStore.ServerAuthor"/>；一定也是 <paramref name="deleted"/>）</param>
    public static bool Hidden(string cls, string? policy, bool deleted, bool deletedByServer = false) =>
        deletedByServer
        || (cls == SyncClasses.Item && (deleted || (policy is not null && ConversationSyncPolicies.Interpret(policy) != ConversationSyncPolicies.Full)));
}
