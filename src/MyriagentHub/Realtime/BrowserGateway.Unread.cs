using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 閘道的未讀（WP4.2 計畫步 12A；答 6、D45）：只在閘道迴圈（<c>runGate</c> 之下）讀寫 <see cref="ConversationUnread"/>，規則在 <see cref="ConversationUnreadRules"/>。
/// <list type="bullet">
/// <item>取走變更時（有沒有瀏覽器連線都一樣：沒人連線＝沒人看見）：要標未讀的變更、而且沒有任何連線看見那個對話就標；Deleted 從集合拿掉
/// （sync.db 的列已在刪除的同一個交易刪掉）。依提交順序處理，所以同一輪先標後刪的對話最後不在集合裡。</item>
/// <item>ClientState 換了、<c>visible</c> 指令（<see cref="ViewersChanged"/>）之後的一輪：任何連線看見的未讀對話都清掉。</item>
/// <item>未讀改了，清單在同一輪重組（<see cref="ConversationViews.ListFor"/> 看集合的版本），雜湊變了就重送 <c>conversations</c>（步 9 依鍵合併）。</item>
/// </list>
/// 通知（notify）不在這裡：照 D28 只給看不到那個對話的連線（看得到但沒有 focus 的分頁不收 notify，未讀留到它取得 focus）。
/// 變更通知溢出（整份重讀）時，被丟掉的注意事件不會標未讀（取不回來；只影響未讀的點，不影響內容）；被丟掉的 Deleted 照目錄補做
/// （<see cref="ConversationViews.ReconcileUnread"/>：已刪除或目錄裡沒有的對話從集合拿掉，合併前審查 P4SRV-MAT-04）。
/// </summary>
internal sealed partial class BrowserGateway
{
    private int viewersChanged;

    /// <summary>
    /// 有連線的 ClientState 或 <c>visible</c> 指令的清單換了（<see cref="BrowserConnections.StateChanged"/>；任何執行緒、不可阻塞）：
    /// 下一輪重算「看見」、清掉開始被看見的未讀，並喚醒迴圈（時區與「看得到」也跟著重算）
    /// </summary>
    public void ViewersChanged()
    {
        if (views is null)
            return;
        Interlocked.Exchange(ref viewersChanged, 1);
        Wake();
    }

    /// <summary>一輪取走的變更裡的未讀（依提交順序）：標未讀、刪除的從集合拿掉；<paramref name="all"/> 是目前所有的連線（沒有連線＝沒人看見）</summary>
    private void ApplyUnread(IReadOnlyList<ConversationChange> changes, IReadOnlyList<BrowserConnection> all, long nowMs)
    {
        if (Views.Unread is not { } unread)
            return;
        IReadOnlySet<string>? seen = null;
        foreach (var change in changes)
        {
            if (change.Kind == ConversationChangeKind.Deleted)
            {
                unread.Forget(change.ConversationId);
                continue;
            }
            if (!ConversationUnreadRules.MarksUnread(change))
                continue;
            // 未讀是擁有者的（多人協作）：協作人看見不算
            seen ??= ConversationUnreadRules.SeenSet(all.Where(c => !c.IsCollaborator).Select(ViewerState.Of));
            if (!seen.Contains(change.ConversationId))
                unread.Mark(change.ConversationId, change.Seq, nowMs);
        }
    }

    /// <summary>看見的連線換了之後（<see cref="ViewersChanged"/>）：任何連線看見的未讀對話都清掉</summary>
    private void ClearSeenUnread(IReadOnlyList<BrowserConnection> all)
    {
        if (Interlocked.Exchange(ref viewersChanged, 0) == 0 || Views.Unread is not { Count: > 0 } unread)
            return;
        foreach (var conversationId in ConversationUnreadRules.SeenSet(all.Where(c => !c.IsCollaborator).Select(ViewerState.Of)))
            unread.Clear(conversationId);
    }
}
