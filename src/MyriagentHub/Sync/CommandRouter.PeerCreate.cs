using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 桌面在其他電腦開新對話（sync-protocol 14.7）：瀏覽器的新對話是伺服器草稿（第一次 send 才組 <c>conversationCreate</c>，見 <c>CommandRouter.Drafts.cs</c>），
/// 桌面的草稿在桌面本機，第一次送出時直接送 <c>PeerCmd {cmd: "conversationCreate", deviceId, accountId: "la_…", permissionMode, model?, effort?, folder?, title?, text?}</c>：
/// <list type="bullet">
/// <item><c>deviceId</c> 必帶（型錄沒有這個欄位，投影時去掉）：要存在、沒撤銷、不是發起的電腦；<c>accountId</c> 要是目錄裡的邏輯帳戶，換成那台連結的本機 id。</item>
/// <item>之後同一般的指令（<see cref="IssueAsync"/>：層級、選連線、目標要有 <c>peer1</c>、簽章、寫列）；新對話的 id＝<see cref="RemoteConversationIdRules.FromCommandId"/>，
/// 寫列之後先以 <c>PeerReply</c> 送 <c>peerCreated {commandId, conversationId}</c> 讓桌面把草稿換成它（副本出現時）。</item>
/// </list>
/// </summary>
internal sealed partial class CommandRouter
{
    /// <summary>桌面送來的 <c>conversationCreate</c>（只由 <see cref="PeerCommandHandler"/> 呼叫）</summary>
    public async Task<CmdResult> RouteConversationCreateAsync(DeviceCommandOrigin origin, string commandId, JObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(body);
        var spec = RemoteCommandCatalog.Find(RemoteCommandCatalog.ConversationCreate)!;
        var route = new RouteState(origin, spec, commandId, body);
        if (!RemoteCommandShapeRules.IsString(body["deviceId"], out var target) || !SyncIds.IsDeviceId(target))
            return CmdError(route, BrowserErrorCodes.Invalid);

        var projected = RemoteCommandCatalog.ApplyLimits(CommandRouteRules.Project(body, spec));
        route.Command = projected;
        if (RemoteCommandShapeRules.Check(projected) is { } shape)
        {
            log.LogDebug("桌面的 conversationCreate 形狀不合格：{Reason}", shape);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }
        route.TargetDeviceId = target;
        if (string.Equals(target, origin.Device, StringComparison.Ordinal))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, PeerRules.SelfTargetMessage), cancellationToken);
        if (DeviceFactOf(target) is not { Revoked: false })
            return CmdError(route, BrowserErrorCodes.Invalid);

        // 帳戶：目錄裡的邏輯帳戶，換成那台連結的本機 id（那台沒有連結回 rejected invalid，同瀏覽器）
        if (!RemoteCommandShapeRules.IsString(projected["accountId"], out var logical) || !SyncIds.IsLogicalAccountId(logical))
            return CmdError(route, BrowserErrorCodes.Invalid);
        bool exists;
        IReadOnlyList<AccountLinkFact> links;
        using (var read = writer.OpenRead())
            (exists, links) = store.ReadAccount(read, logical);
        if (!exists)
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, DraftCommandRules.AccountGoneMessage), cancellationToken);
        route.BrowserAccountId = CommandRouteRules.BrowserAccountId(spec, projected);
        var (mapped, mapRejection) = CommandRouteRules.MapAccounts(CommandRouteRules.WithTarget(projected, spec, target), spec, target, links);
        if (mapRejection is not null || mapped is null)
            return await RejectAsync(route, mapRejection ?? new RouteRejection(BrowserErrorCodes.Invalid, CmdError: true), cancellationToken);
        route.Command = mapped;
        if (RemoteCommandShapeRules.Check(mapped) is { } again)
        {
            log.LogError("桌面的 conversationCreate 改寫帳戶之後形狀不合格（程式錯誤）：{Reason}", again);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }
        return await IssueAsync(route, target, mapped, draft: null, cancellationToken);
    }

    /// <summary><c>peerCreated {commandId, conversationId}</c>：桌面草稿的新對話 id（寫列之後、送出之前）</summary>
    private void SendPeerCreated(CommandOrigin origin, string commandId, string conversationId) =>
        replies.Send(origin, commandId, new ClientOutboxMessage(PeerCreatedType,
            new JObject { ["type"] = PeerCreatedType, ["commandId"] = commandId, ["conversationId"] = conversationId }.ToString(Formatting.None)));

    internal const string PeerCreatedType = "peerCreated";
}
