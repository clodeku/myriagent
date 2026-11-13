using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 伺服器草稿上的指令（WP4.5 計畫步 9；D33、K8；草稿本身是 WP4.2 的 <see cref="IBrowserDrafts"/>）。Owner 路由的 <c>conversationId</c> 是草稿 id（<c>d…</c>）時：
/// <list type="bullet">
/// <item>找不到（別的工作階段的、過期、被丟、已關閉）：<c>Cmd invalid</c>，另送 <c>conversationClosed</c>（同 open 不存在的草稿）。</item>
/// <item><c>setOption</c>、<c>setAccount</c>、<c>setFolder</c>、<c>resetFolder</c>、<c>rename</c>：照 <see cref="DraftCommandRules.Apply"/> 改草稿，對這個工作階段的
/// 每一條連線推草稿的 <c>header</c>（同一個登入的其他分頁可能開著它），發起的連線另收 <c>commandResult done</c>；不合格回 <c>Cmd invalid</c>。
/// 只改伺服器記憶體，不寫列、不簽、不稽核。</item>
/// <item>第一次 <c>send</c>：組成 <c>conversationCreate</c>（<see cref="DraftCommandRules.ConversationCreate"/>），以 Device 路由送到草稿的電腦
/// （<c>newConversation</c> 選填的 <c>deviceId</c>）或預設的電腦（<see cref="DraftCommandRules.DefaultDevice"/>）；帳戶 id 換成那台的本機 id、不寫 <c>deviceId</c>，
/// 之後同一般的指令（<c>IssueAsync</c>）。寫列之前在草稿記下 cid 與新對話的 id（<see cref="RemoteConversationIdRules.FromCommandId"/>），寫不進去或 rejected 時清掉，
/// 讓使用者可以再送；done、unknown 保留。草稿已記下時又送 <c>send</c>：立即拒絕 busy（<see cref="DraftCommandRules.CreatingMessage"/>）並還原文字。
/// 被拒時 <c>sendRejected</c> 的 <c>conversationId</c> 是草稿 id。</item>
/// <item><c>listFiles</c> 帶 <c>dirsOnly: true</c>（「瀏覽資料夾…」，2026-10-27）：改成 <c>listFolders</c> 以 Device 路由送到草稿的電腦（挑法同第一次 send），
/// 那台列它的最近資料夾；輸出的 <c>files</c> 換成草稿 id（<see cref="Media.CommandOutputFileRules"/>）。不動草稿。</item>
/// <item>草稿上的其他指令（stop、approve、delete…）：<c>Cmd invalid</c>。</item>
/// </list>
/// </summary>
internal sealed partial class CommandRouter
{
    private async Task<CmdResult> DraftAsync(RouteState route, string draftId, CancellationToken cancellationToken)
    {
        var sessionId = route.Connection.SessionId;
        if (!drafts.TryGet(sessionId, draftId, out var draft))
            return DraftGone(route, draftId);
        var name = route.Spec.Name;
        if (name == Send)
            return await DraftSendAsync(route, draft, cancellationToken);
        if (name == ListFiles && route.Body["dirsOnly"] is JValue { Type: JTokenType.Boolean, Value: true })
            return await DraftListFoldersAsync(route, draft, cancellationToken);
        if (!DraftCommandRules.EditCommands.Contains(name))
        {
            log.LogDebug("草稿上不能做 {Cmd}", name);
            return new CmdResult(false, route.CommandId, BrowserErrorCodes.Invalid);
        }

        // 草稿上附加的檔（2026-11-13）：先確認每個都已上傳到這台伺服器（同一般指令簽章之前的檢查；送出時 IssueAsync 會再查一次）
        if (name == DraftCommandRules.AddUploaded
            && (CommandUploadRules.Claims(route.Body) is not { Count: > 0 } claims || !uploads.TryVerify(claims)))
        {
            log.LogDebug("草稿上的 addUploaded 有沒上傳過的檔");
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, UploadsUnknownMessage), cancellationToken);
        }

        // 先以讀到的那份檢查（不合格就不動草稿），再在草稿的鎖裡對最新的那份套用同一個修改
        var accounts = gateway.DraftAccounts;
        if (DraftCommandRules.Apply(draft, name, route.Body, accounts).Error is { } error)
        {
            log.LogDebug("草稿上的 {Cmd} 不合格", name);
            return new CmdResult(false, route.CommandId, error);
        }
        BrowserDraft? updated = null;
        if (!drafts.TryUpdate(sessionId, draftId, current => updated = DraftCommandRules.Apply(current, name, route.Body, accounts).Draft ?? current))
            return DraftGone(route, draftId);
        var header = ConversationGatewayRules.HeaderMessage(draftId, BrowserDraftRules.Header(updated!).ToString(Formatting.None));
        foreach (var target in browsers.Snapshot().Where(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal)))
            gateway.SendAfterInit(target, header);
        gateway.SendAfterInit(route.Connection, new ClientOutboxMessage(CommandMessageRules.CommandResultType,
            CommandMessageRules.CommandResultJson(route.CommandId!, RemoteResultStates.Done, null, null, null)));
        return new CmdResult(true, route.CommandId);
    }

    /// <summary>草稿的第一次 send（D33）：選目標、換算帳戶之後交給 <see cref="IssueAsync"/></summary>
    private async Task<CmdResult> DraftSendAsync(RouteState send, BrowserDraft draft, CancellationToken cancellationToken)
    {
        var spec = RemoteCommandCatalog.Find(RemoteCommandCatalog.ConversationCreate)!;
        var text = RemoteCommandShapeRules.IsString(send.Body["text"], out var value) ? value : null;
        var route = new RouteState(send.Origin, spec, send.CommandId, send.Body)
        {
            BrowserConversationId = draft.Id,
            RestoreText = text,
        };
        if (text is null)
            return CmdError(route, BrowserErrorCodes.Invalid);
        var projected = DraftCommandRules.ConversationCreate(draft, text);
        route.Command = projected;
        if (draft.SentCommandId is not null)
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Busy, CmdError: false, DraftCommandRules.CreatingMessage), cancellationToken);
        if (RemoteCommandShapeRules.Check(projected) is { } shape)
        {
            log.LogDebug("草稿的第一次 send 組出的 conversationCreate 形狀不合格：{Reason}", shape);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }

        var (target, links, refused) = await DraftTargetAsync(route, draft, cancellationToken);
        if (refused is not null)
            return refused;
        route.TargetDeviceId = target;
        route.BrowserAccountId = CommandRouteRules.BrowserAccountId(spec, projected);

        // 不寫 deviceId（型錄沒有這個欄位）、帳戶 id 換成那台的本機 id、再檢查一次（D6、D8）
        var (mapped, mapRejection) = CommandRouteRules.MapAccounts(CommandRouteRules.WithTarget(projected, spec, target), spec, target, links);
        if (mapRejection is not null || mapped is null)
            return await RejectAsync(route, mapRejection ?? new RouteRejection(BrowserErrorCodes.Invalid, CmdError: true), cancellationToken);
        route.Command = mapped;
        if (RemoteCommandShapeRules.Check(mapped) is { } again)
        {
            log.LogError("草稿的 conversationCreate 改寫帳戶之後形狀不合格（程式錯誤）：{Reason}", again);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }
        return await IssueAsync(route, target!, mapped, new DraftSend(send.Connection.SessionId, draft.Id), cancellationToken);
    }

    /// <summary>
    /// 草稿的電腦（D33）：草稿指定的電腦（要存在而且未撤銷，同 D5（a）），或預設的電腦；草稿的帳戶已從目錄刪除時拒絕。
    /// 回目標與帳戶的連結，或已經回給瀏覽器的結果（<c>Refused</c> 不是 null）
    /// </summary>
    private async Task<(string? Target, IReadOnlyList<AccountLinkFact> Links, CmdResult? Refused)> DraftTargetAsync(RouteState route, BrowserDraft draft,
        CancellationToken cancellationToken)
    {
        var exists = false;
        IReadOnlyList<AccountLinkFact> links = [];
        if (SyncIds.IsLogicalAccountId(draft.AccountId))
        {
            using var read = writer.OpenRead();
            (exists, links) = store.ReadAccount(read, draft.AccountId);
        }
        if (!exists)
            return (null, links, await RejectAsync(route, new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, DraftCommandRules.AccountGoneMessage), cancellationToken));
        if (draft.DeviceId is { } chosen)
            return DeviceFactOf(chosen) is { Revoked: false } ? (chosen, links, null) : (null, links, CmdError(route, BrowserErrorCodes.Invalid));
        var spki = identity.Spki;
        var choice = DraftCommandRules.DefaultDevice(draft.AccountId, links, DeviceFactOf, id => ConnectionFacts(registry.ConnectionsOf(id), spki));
        if (choice.Rejection is { } rejection)
            return (null, links, await RejectAsync(route, rejection, cancellationToken));
        return (choice.DeviceId!, links, null);
    }

    /// <summary>
    /// 草稿上的「瀏覽資料夾…」（listFiles dirsOnly）：組成 <c>listFolders</c>（requestId、path 照抄）送到草稿的電腦，之後同一般的指令（<see cref="IssueAsync"/>）；
    /// 輸出以草稿 id 回給瀏覽器（<see cref="RouteState.BrowserConversationId"/>）
    /// </summary>
    private async Task<CmdResult> DraftListFoldersAsync(RouteState list, BrowserDraft draft, CancellationToken cancellationToken)
    {
        var spec = RemoteCommandCatalog.Find(RemoteCommandCatalog.ListFolders)!;
        var route = new RouteState(list.Origin, spec, list.CommandId, list.Body) { BrowserConversationId = draft.Id };
        var projected = new JObject
        {
            ["cmd"] = spec.Name,
            ["requestId"] = list.Body["requestId"]?.DeepClone(),
            ["path"] = list.Body["path"]?.DeepClone() ?? JValue.CreateNull(),
        };
        var (target, _, refused) = await DraftTargetAsync(route, draft, cancellationToken);
        if (refused is not null)
            return refused;
        route.TargetDeviceId = target;
        var mapped = CommandRouteRules.WithTarget(projected, spec, target!);
        route.Command = mapped;
        if (RemoteCommandShapeRules.Check(mapped) is { } again)
        {
            log.LogError("草稿的 listFolders 寫入目標之後形狀不合格（程式錯誤）：{Reason}", again);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }
        return await IssueAsync(route, target!, mapped, draft: null, cancellationToken);
    }

    /// <summary>
    /// 草稿不在了：<c>Cmd invalid</c>，另送 <c>conversationClosed</c>（網頁關掉分頁；不還原文字）。已經換成正式對話（墓碑，WP4.6 合併前審查 LIVE-1）
    /// 而且那個對話還在時帶 <c>replacedBy</c>（網頁在同一個位置換成新對話；這個指令照舊不執行）
    /// </summary>
    private CmdResult DraftGone(RouteState route, string draftId)
    {
        var closed = drafts.TryGetReplaced(route.Connection.SessionId, draftId, out var replacedBy) && directory.Find(replacedBy) is not null
            ? DraftSwapRules.ClosedReplaced(draftId, replacedBy)
            : ConversationGatewayRules.ClosedMessage(draftId);
        gateway.SendAfterInit(route.Connection, closed);
        return new CmdResult(false, route.CommandId, BrowserErrorCodes.Invalid);
    }

    /// <summary>寫列之前在草稿記下 cid 與新對話的 id（D33）：在草稿的鎖裡確認還沒記下別的，同一份草稿同時兩個 send 只有一個記得上</summary>
    private DraftReservation ReserveDraft(DraftSend draft, string cid, string pendingConversationId)
    {
        var reserved = false;
        if (!drafts.TryUpdate(draft.SessionId, draft.DraftId, current =>
            {
                if (current.SentCommandId is not null)
                    return current;
                reserved = true;
                return current with { SentCommandId = cid, PendingConversationId = pendingConversationId };
            }))
            return DraftReservation.Gone;
        return reserved ? DraftReservation.Reserved : DraftReservation.Busy;
    }

    /// <summary>清掉草稿記下的 cid（還是這一個時；寫不進去時）</summary>
    private void ReleaseDraft(DraftSend draft, string cid) => DraftCommandOutcomes.Release(drafts, draft.SessionId, draft.DraftId, cid);

    /// <summary>草稿的第一次 send：草稿所屬的工作階段與草稿 id</summary>
    private sealed record DraftSend(string SessionId, string DraftId);

    private enum DraftReservation
    {
        Reserved,

        /// <summary>草稿已經記下別的 cid（新對話還在建立中）</summary>
        Busy,

        /// <summary>草稿不在了（記下之前被關掉或丟掉）</summary>
        Gone,
    }
}

/// <summary>草稿第一次 send 的結果對草稿的影響（D33；路由與 <see cref="CommandResults"/> 共用）</summary>
internal static class DraftCommandOutcomes
{
    /// <summary>
    /// 清掉草稿記下的 <c>SentCommandId</c> 與 <c>PendingConversationId</c>（寫不進去、rejected：電腦什麼都沒建，使用者可以再送）；
    /// 草稿記的已經不是這個 cid、或草稿不在了就什麼都不做
    /// </summary>
    public static void Release(IBrowserDrafts drafts, string sessionId, string draftId, string cid)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        drafts.TryUpdate(sessionId, draftId, current => string.Equals(current.SentCommandId, cid, StringComparison.Ordinal)
            ? current with { SentCommandId = null, PendingConversationId = null }
            : current);
    }
}
