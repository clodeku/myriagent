using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 伺服器草稿換成正式對話（WP4.6 計畫 D17、D41；WP4.5 計畫 K8 的「新對話出現時由閘道換掉草稿」）：第一次 send 時 WP4.5 在草稿記下新對話的 id
/// （<see cref="BrowserDraft.PendingConversationId"/>，列提交之前就記下），那個對話在伺服器出現時，對草稿的工作階段的每一條連線送
/// <c>conversationClosed</c> 帶 <c>replacedBy</c>（<see cref="DraftSwapRules.ClosedReplaced"/>），並丟掉草稿。三個時機：
/// <list type="number">
/// <item>即時：閘道迴圈取走變更之後、<see cref="ConversationGatewayRules.Fold"/> 之前，這一輪的 <c>conv.created</c>／<c>conv.meta</c>（<see cref="SwapCreatedDrafts"/>）。</item>
/// <item>通知溢出（整份重讀）：變更可能被丟掉，改查目錄（<see cref="BrowserDrafts.TakePending"/>）。</item>
/// <item>open 草稿時：對話已經在目錄裡（沒有連線時變更被丟掉、那條連線當時還沒送過 init），改成換掉（<see cref="TrySwapDraft"/>）。</item>
/// </list>
/// 還沒送過 init 的連線收不到（<see cref="SendAfterInit"/>）、斷掉還沒移除的連線收到也沒用：草稿拿走時留一筆墓碑（<see cref="BrowserDrafts.TryGetReplaced"/>，
/// 合併前審查 LIVE-1），網頁重連後再 open 草稿時改回 <c>replacedBy</c>（墓碑過期之後才只會關掉草稿，WP4.6 計畫 1.2、風險 R16）
/// </summary>
internal sealed partial class BrowserGateway
{
    /// <summary>D41 的 1、2：在閘道迴圈裡、<see cref="ConversationGatewayRules.Fold"/> 之前（<c>drafts</c> 不是 null 時才呼叫）</summary>
    private void SwapCreatedDrafts(IReadOnlyList<ConversationChange> drained, bool refresh)
    {
        var store = drafts ?? throw new InvalidOperationException("沒有伺服器草稿");
        foreach (var conversationId in DraftSwapRules.CreatedIds(drained))
        {
            foreach (var (sessionId, draftId) in store.TakeCreated(conversationId))
                SendReplaced(store, sessionId, draftId, conversationId);
        }
        if (!refresh)
            return;
        // 有變更因為通知滿了被丟掉：記著 Pending 的草稿改查目錄（含已刪除的墓碑：換過去之後 open 會照常回 conversationClosed）
        var views = Views;
        foreach (var (sessionId, draftId, conversationId) in store.TakePending(id => views.OwnerOf(id) is not null))
            SendReplaced(store, sessionId, draftId, conversationId);
    }

    /// <summary>
    /// D41 的 3：open 草稿時，第一次送出建立的對話已經在目錄裡（呼叫端以 <c>IConversationDirectory.Find</c> 確認過）：換掉草稿，
    /// 這個工作階段的每一條連線都收到 <c>replacedBy</c>。任何執行緒都能呼叫；回 true＝已換，呼叫端不再回草稿。
    /// 草稿剛好被閘道迴圈先換掉時，發問的這條連線再收一次（網頁對已經不在的草稿什麼都不做）
    /// </summary>
    internal bool TrySwapDraft(BrowserConnection connection, BrowserDraft draft)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(draft);
        if (drafts is not { } store || draft.PendingConversationId is not { } conversationId)
            return false;
        var taken = store.TakeCreated(conversationId);
        foreach (var (sessionId, draftId) in taken)
            SendReplaced(store, sessionId, draftId, conversationId);
        if (taken.Count == 0)
            SendAfterInit(connection, DraftSwapRules.ClosedReplaced(draft.Id, conversationId));
        return true;
    }

    /// <summary>草稿換成了 <paramref name="conversationId"/>：這個工作階段的每一條連線（同一個登入的其他分頁可能也開著它）</summary>
    private void SendReplaced(BrowserDrafts store, string sessionId, string draftId, string conversationId)
    {
        var message = DraftSwapRules.ClosedReplaced(draftId, conversationId);
        foreach (var connection in store.ConnectionsOf(sessionId))
            SendAfterInit(connection, message);
        log.LogInformation("伺服器草稿換成正式對話：{Draft} → {Conversation}", draftId, conversationId);
    }
}
