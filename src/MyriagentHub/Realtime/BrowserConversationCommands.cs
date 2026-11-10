using Myriagent.Sync;
using MyriagentHub.Rendering;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器模式下由伺服器的對話儲存處理的指令（WP4.2 計畫步 10；D25、D30、D38；web-protocol 第二節）：<c>open</c>、<c>close</c>、<c>loadOlder</c>、
/// <c>visible</c>、<c>copyItem</c>。在 hub 的呼叫執行緒（每條連線同時一個），只做 O(1) 的檢查與記錄，讀快照與渲染交給閘道迴圈（D38）。
/// <list type="bullet">
/// <item><c>open</c>：對話 id 形狀的才處理；目錄找不到（不存在、已刪除）回 <c>invalid</c> 並送 <c>conversationClosed</c>，否則記下請求、喚醒閘道，閘道回 <c>conversation</c>。
/// 請求排滿了（<see cref="BrowserConnection.MaxPendingConversationRequests"/>）回 <c>rate_limited</c>。記下之後交給 <see cref="SnapshotRequester.OnOpen"/>：
/// 伺服器上還沒有項目的 full 對話請擁有者送整份（D26，步 12）。</item>
/// <item><c>loadOlder</c>：同 open，另要 <c>beforeId</c> 是項目 id；閘道回 <c>olderItems</c>。</item>
/// <item><c>close</c>：標成不開著、丟掉還在等的請求，之後不再推這個對話的增量。</item>
/// <item><c>visible</c>：記下畫面上顯示的對話（D25 的「看得到」；只留對話 id 形狀的）。</item>
/// <item><c>copyItem</c>：只收 Assistant 的項目，讀一次原文（不渲染），以 <c>clipboard</c> 只回給發起的連線；不存在、已刪除、不是 Assistant 回 <c>invalid</c>，
/// 原文讀不到（blob、DLP）回 <c>unavailable</c>（D30）。</item>
/// <item><c>newConversation</c>（步 13、D29；有 <paramref name="drafts"/> 時）：開一份伺服器草稿，回 <c>conversation</c>（<c>header.isDraft=true</c>、沒有項目、
/// <c>paneId</c> 原樣送回；選填的 <c>deviceId</c> 記進草稿，WP4.5 第一次送出時在那台建立）；超過每個工作階段的上限時，被丟的草稿對這個工作階段的每一條連線送 <c>conversationClosed</c>。一個帳戶都沒有回 <c>not_available</c>
/// （同第 3 階段；網頁自動送的，不提示）。草稿 id（<c>d…</c>）的 <c>open</c>：同一個工作階段在寬限內回同一份草稿（第一次送出建立的對話已經在目錄裡時，
/// 改成換掉草稿、送 <c>conversationClosed</c> 帶 <c>replacedBy</c>，WP4.6 D41；已經換掉的草稿在墓碑還在時也再送一次 <c>replacedBy</c>、不是錯誤，
/// 合併前審查 LIVE-1），找不到（別的工作階段的、過期、被丟、墓碑過期）同不存在的對話回 <c>invalid</c> 並送 <c>conversationClosed</c>；<c>close</c>：丟棄並對這個工作階段的每一條連線送 <c>conversationClosed</c>；
/// <c>loadOlder</c>：草稿沒有項目，接受、不處理。</item>
/// <item><c>searchConversations</c>（側欄的對話搜尋，2026-10-23；有 <paramref name="search"/> 時）：<c>requestId</c> 要是 1～64 字的字串、<c>query</c> 要是字串，
/// 否則 <c>invalid</c>；交給 <see cref="BrowserConversationSearch"/> 在背景比對，結果以 <c>conversationSearch</c> 只回給發起的連線。</item>
/// </list>
/// 不是對話 id 也不是草稿 id 形狀的 <c>open</c>／<c>close</c>／<c>loadOlder</c>（第 3 階段網頁的本機 id）與沒帶清單的 <c>visible</c>
/// 照第 3 階段接受、不處理：網頁自動送的這些指令被拒也不提示（BrowserCommandRules.SilentCommands）。
/// </summary>
internal sealed class BrowserConversationCommands(
    BrowserGateway gateway,
    IConversationDirectory directory,
    ConversationReader reader,
    IBlobStore blobs,
    SnapshotRequester? snapshots = null,
    BrowserDrafts? drafts = null,
    BrowserConversationSearch? search = null) : IBrowserConversationCommands
{
    public const string Open = "open";

    public const string Close = "close";

    public const string LoadOlder = "loadOlder";

    public const string Visible = "visible";

    public const string CopyItem = "copyItem";

    public const string NewConversation = "newConversation";

    public const string SearchConversations = "searchConversations";

    /// <summary>臨時對話（2026-11-11）：開一份 ephemeral 的伺服器草稿，嵌進網頁的浮動面板</summary>
    public const string QuickChat = "quickChat";

    public bool Handles(string cmd) => cmd is Open or Close or LoadOlder or Visible or CopyItem || (cmd is NewConversation or QuickChat && drafts is not null)
        || (cmd is SearchConversations && search is not null);

    public Task<string?> HandleAsync(BrowserConnection connection, string cmd, JObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(body);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(cmd switch
        {
            Open => Request(connection, body, ConversationRequestKind.Open),
            LoadOlder => Request(connection, body, ConversationRequestKind.LoadOlder),
            Close => CloseConversation(connection, body),
            Visible => SetVisible(connection, body),
            CopyItem => Copy(connection, body),
            NewConversation when drafts is not null => NewDraft(drafts, connection, body),
            QuickChat when drafts is not null => NewDraft(drafts, connection, body, ephemeral: true),
            SearchConversations when search is not null => Search(search, connection, body),
            _ => throw new ArgumentOutOfRangeException(nameof(cmd), cmd, "不是對話儲存處理的指令"),
        });
    }

    /// <summary>open／loadOlder（D38）：只查目錄（O(1)），讀快照交給閘道迴圈；草稿 id 另外處理（步 13）</summary>
    private string? Request(BrowserConnection connection, JObject body, ConversationRequestKind kind)
    {
        if (Str(body, "conversationId") is not { } conversationId)
            return null;
        if (drafts is not null && BrowserDraftRules.IsDraftId(conversationId))
            return RequestDraft(drafts, connection, conversationId, kind);
        if (!ConversationIds.IsWireId(conversationId))
            return null;
        string? beforeId = null;
        if (kind == ConversationRequestKind.LoadOlder)
        {
            beforeId = Str(body, "beforeId");
            if (!ConversationIds.IsItemId(beforeId))
                return BrowserErrorCodes.Invalid;
        }
        if (directory.Find(conversationId) is null)
        {
            // 網頁照 conversationClosed 關掉分頁（不存在、已刪除；web-protocol 的 conversationClosed）
            connection.CloseConversation(conversationId);
            gateway.SendAfterInit(connection, ConversationGatewayRules.ClosedMessage(conversationId));
            return BrowserErrorCodes.Invalid;
        }
        if (!connection.TryRequestConversation(new ConversationRequest(kind, conversationId, beforeId)))
            return BrowserErrorCodes.RateLimited;
        gateway.WakeConversations();
        if (kind == ConversationRequestKind.Open)
            snapshots?.OnOpen(conversationId);
        return null;
    }

    private string? CloseConversation(BrowserConnection connection, JObject body)
    {
        if (Str(body, "conversationId") is not { } conversationId)
            return null;
        if (ConversationIds.IsWireId(conversationId))
            connection.CloseConversation(conversationId);
        else if (drafts is not null && BrowserDraftRules.IsDraftId(conversationId) && drafts.Remove(connection.SessionId, conversationId))
            SendClosed(drafts, connection.SessionId, [conversationId]);
        return null;
    }

    /// <summary>newConversation（步 13、D29）：開一份草稿，<c>conversation</c> 只回給發起的連線；被丟的草稿先送 <c>conversationClosed</c></summary>
    private string? NewDraft(BrowserDrafts store, BrowserConnection connection, JObject body, bool ephemeral = false)
    {
        // 選填的 deviceId（WP4.5 計畫 K8）：新對話要在哪台電腦建立；不是裝置 id 的形狀當成沒帶（第一次送出時照預設挑一台）
        if (store.Create(connection.SessionId, Str(body, "accountId"), gateway.DraftAccounts, Str(body, "deviceId") is { } deviceId && SyncIds.IsDeviceId(deviceId) ? deviceId : null,
                ephemeral)
            is not { } created)
            return BrowserErrorCodes.NotAvailable;
        SendClosed(store, connection.SessionId, created.Evicted);
        gateway.SendAfterInit(connection, BrowserDraftRules.Message(created.Draft, BrowserDraftRules.PaneId(body["paneId"])));
        return null;
    }

    /// <summary>
    /// 草稿 id 的 open／loadOlder：同一個工作階段找得到就回那份草稿（loadOlder 沒有東西可回）；已經換成正式對話（墓碑，合併前審查 LIVE-1）
    /// 而且那個對話還在，再送一次 <c>replacedBy</c>、不是錯誤；其他找不到的同不存在的對話
    /// </summary>
    private string? RequestDraft(BrowserDrafts store, BrowserConnection connection, string draftId, ConversationRequestKind kind)
    {
        if (!store.TryGet(connection.SessionId, draftId, out var draft))
        {
            // 換的那一則送進了已經斷掉的連線、或那時這個工作階段沒有連線：重連後的網頁照樣在同一個位置換成新對話
            if (store.TryGetReplaced(connection.SessionId, draftId, out var replacedBy) && directory.Find(replacedBy) is not null)
            {
                gateway.SendAfterInit(connection, DraftSwapRules.ClosedReplaced(draftId, replacedBy));
                return null;
            }
            gateway.SendAfterInit(connection, ConversationGatewayRules.ClosedMessage(draftId));
            return BrowserErrorCodes.Invalid;
        }
        // 第一次送出建立的對話已經在目錄裡（變更在沒有連線時被丟掉、這條連線當時還沒送過 init；WP4.6 D41 的 3）：換成正式對話，不再回草稿
        if (draft.PendingConversationId is { } created && directory.Find(created) is not null && gateway.TrySwapDraft(connection, draft))
            return null;
        if (kind == ConversationRequestKind.Open)
            gateway.SendAfterInit(connection, BrowserDraftRules.Message(draft, paneId: null));
        return null;
    }

    /// <summary>丟掉的草稿：這個工作階段的每一條連線都送 <c>conversationClosed</c>（同一個登入的其他分頁可能開著它）</summary>
    private void SendClosed(BrowserDrafts store, string sessionId, IReadOnlyList<string> draftIds)
    {
        if (draftIds.Count == 0)
            return;
        foreach (var target in store.ConnectionsOf(sessionId))
        {
            foreach (var draftId in draftIds)
                gateway.SendAfterInit(target, ConversationGatewayRules.ClosedMessage(draftId));
        }
    }

    private static string? SetVisible(BrowserConnection connection, JObject body)
    {
        if (body["conversationIds"] is JArray ids)
            connection.SetVisibleConversations(ids.Select(t => t.Type == JTokenType.String ? (string?)t : null));
        return null;
    }

    /// <summary>copyItem（D30）：讀一次項目（不渲染），原文以 clipboard 只回給發起的連線</summary>
    private string? Copy(BrowserConnection connection, JObject body)
    {
        if (Str(body, "conversationId") is not { } conversationId || !ConversationIds.IsWireId(conversationId)
            || Str(body, "itemId") is not { } itemId || !ConversationIds.IsItemId(itemId))
            return BrowserErrorCodes.Invalid;
        if (directory.Find(conversationId) is null || reader.ReadItem(conversationId, itemId).Item is not { } item)
            return BrowserErrorCodes.Invalid;
        switch (HubItemRenderer.TryReadText(blobs, item.Enc, item.Body, out var text))
        {
            case ItemTextOutcome.Ok:
                gateway.SendAfterInit(connection, ConversationGatewayRules.ClipboardMessage(conversationId, itemId, text));
                return null;
            case ItemTextOutcome.NotAssistant:
                return BrowserErrorCodes.Invalid;
            case ItemTextOutcome.Unreadable:
                return BrowserErrorCodes.Unavailable;
            default:
                throw new ArgumentOutOfRangeException(nameof(body), "未知的原文讀取結果");
        }
    }

    /// <summary>searchConversations：只驗形狀、交給搜尋服務（hub 的呼叫執行緒不讀資料庫）</summary>
    private static string? Search(BrowserConversationSearch service, BrowserConnection connection, JObject body)
    {
        if (Str(body, "requestId") is not { Length: > 0 and <= BrowserConversationSearch.MaxRequestIdLength } requestId || Str(body, "query") is not { } query)
            return BrowserErrorCodes.Invalid;
        service.Start(connection, requestId, query);
        return null;
    }

    private static string? Str(JObject body, string name) => body[name] is JValue { Type: JTokenType.String } value ? (string?)value : null;
}
