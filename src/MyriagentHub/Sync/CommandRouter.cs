using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using MyriagentHub.Trust;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Sync;

/// <summary>
/// 把瀏覽器的遠端指令轉給目標電腦（WP4.5 計畫步 6；設計 8.11、sync-protocol 14.4）。只收 <see cref="BrowserCommandHandler"/> 交來的、
/// 型錄路由 Owner／Device 的 <c>not_available</c> 指令，依序：
/// <list type="number">
/// <item>延後的指令（<see cref="CommandRouteRules.Deferred"/>；WP4.7 合併後是空的）照舊 not_available；沒帶 commandId 是 <c>invalid</c>；
/// Owner 路由的 <c>conversationId</c> 是伺服器草稿（<c>d…</c>）的交給 <c>CommandRouter.Drafts.cs</c>（步 9、D33），其他不是線上 id 的
/// （第 3 階段網頁的本機 id）是 not_available。</item>
/// <item>投影到型錄欄位、套限制、形狀檢查（D8），選目標（Owner：對話目錄的擁有者，D4；Device：帳戶的連結，D5；所有電腦登出是每台一列 durable，
/// 見 <c>CommandRouter.FanOut.cs</c>，D26），寫入目標、換算帳戶 id 後再檢查一次（D6、D8）。</item>
/// <item>帶 <c>uploads</c> 的指令（<c>addUploaded</c>）先以 <see cref="IUploadRegistry.TryVerify"/> 確認上傳都在這台伺服器（WP4.7 計畫 D21），不通過立即拒絕 invalid。
/// 層級取伺服器的判斷（D9）、發起者與 auth 取工作階段（D10、D11）、<c>IsFresh</c>；選連線（D7，中斷身分公鑰不是目前的連線）。</item>
/// <item>產生 cid、簽信封、以那條連線 Hello 時的身分公鑰自驗（D12～D14）；寫列（D15），提交之後才登記追蹤、送 <c>CommandDelivery</c>、寫稽核（D16），回 accepted。</item>
/// </list>
/// <c>Cmd</c> 的錯誤只有伺服器自己判得出的（D2）：invalid、conflict（對話或帳戶已不在；對話的另送 <c>conversationClosed</c>）、unavailable（寫入者停止、
/// 提交失敗、伺服器網址設定不合格）、not_available。離線、不支援、不是擁有者、政策、帳戶沒連結是立即拒絕（D3）：<c>Cmd</c> 回 accepted，
/// 接著送 <c>commandResult rejected</c>、toast、<c>send</c> 的 <c>sendRejected</c>，寫一筆 denied 的稽核；不簽、不寫列。
/// 在 hub 的呼叫執行緒上執行（每條瀏覽器連線同時一個）；送出與稽核不等待（電腦的背壓不會卡住瀏覽器的下一個 <c>Cmd</c>）。
/// 桌面遠端操作（sync-protocol 14.7）：裝置 hub 的 <c>PeerCmd</c> 以 <see cref="DeviceCommandOrigin"/> 走同一條路由，差別只有：伺服器草稿（<c>d…</c>）一律 invalid、
/// 目標是發起的電腦自己立即拒絕 invalid、選到的目標連線沒有 <c>peer1</c> 立即拒絕 unsupported、所有電腦登出（fan-out）不收；
/// 發起者資訊是 <c>{kind:"device", label: 電腦名稱}</c>，回覆經 <see cref="CommandReplies"/> 以 <c>PeerReply</c> 送回那台
/// </summary>
internal sealed partial class CommandRouter(
    SyncWriter writer,
    CommandStore store,
    IConversationDirectory directory,
    DeviceDirectory devices,
    ConnectionRegistry registry,
    HubIdentity identity,
    IOptions<HubOptions> options,
    IDeviceMessageSender sender,
    BrowserGateway gateway,
    CommandTracker tracker,
    CommandIssuerResolver issuers,
    AuditLog audit,
    CommandCounters counters,
    DurableCommands durable,
    IBrowserDrafts drafts,
    BrowserConnections browsers,
    IUploadRegistry uploads,
    CommandReplies replies,
    ExecRelays execRelays,
    DeskRelays deskRelays,
    TimeProvider time,
    ILogger<CommandRouter> log) : IBrowserCommandRouter
{
    private const string ConversationIdField = "conversationId";

    private const string Send = "send";

    private const string ListFiles = "listFiles";

    /// <summary>指令帶的上傳（<c>addUploaded</c>、<c>conversationCreate</c> 的 <c>uploads</c>）在伺服器上查不到、對不上或過期時的說明（WP4.7 計畫 D21）</summary>
    internal const string UploadsUnknownMessage = "上傳的檔案在伺服器上找不到、內容對不上或已過期，請重新上傳";

    public Task<CmdResult> RouteAsync(BrowserConnection connection, CmdParse parsed, RemoteCommandSpec spec, JObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(parsed);
        return RouteAsync(new BrowserCommandOrigin(connection), parsed.CommandId, spec, body, cancellationToken);
    }

    /// <summary>
    /// 任一種發起者的路由（<paramref name="commandId"/> 是發起者帶的 commandId：瀏覽器的或桌面的）。瀏覽器的行為與 WP4.5 相同；
    /// 裝置發起的差別見類別說明
    /// </summary>
    public async Task<CmdResult> RouteAsync(CommandOrigin origin, string? commandId, RemoteCommandSpec spec, JObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(body);
        var name = spec.Name;
        // 延後的指令先判斷（D1）：和第 3 階段一樣是 not_available，不論有沒有帶 commandId（WP4.7 合併後清單是空的，留著給之後的工作包延後用）
        if (CommandRouteRules.Deferred.Contains(name))
            return new CmdResult(false, commandId, BrowserErrorCodes.NotAvailable);
        // 遠端執行指令只由桌面發起（sync-protocol 14.11；瀏覽器的 MessageGuard 本來就不認得，這裡再擋一次）
        if (name == RemoteCommandCatalog.RemoteExec && origin is not DeviceCommandOrigin)
            return new CmdResult(false, commandId, BrowserErrorCodes.NotAvailable);

        var route = new RouteState(origin, spec, commandId, body)
        {
            BrowserConversationId = spec.Route == RemoteRoute.Owner && RemoteCommandShapeRules.IsString(body[ConversationIdField], out var conversation) ? conversation : null,
            RestoreText = RestoreTextOf(name, body),
        };
        if (commandId is null)
            return CmdError(route, BrowserErrorCodes.Invalid);
        if (route.BrowserConversationId is { } browserConversation && !ConversationIds.IsWireId(browserConversation))
        {
            // 伺服器草稿（d…，步 9、D33）見 CommandRouter.Drafts.cs；第 3 階段網頁的本機 id 照舊 not_available。桌面沒有伺服器草稿（14.7）
            if (BrowserDraftRules.IsDraftId(browserConversation))
                return origin is BrowserCommandOrigin ? await DraftAsync(route, browserConversation, cancellationToken) : CmdError(route, BrowserErrorCodes.Invalid);
            return new CmdResult(false, commandId, BrowserErrorCodes.NotAvailable);
        }

        // 1. 投影、限制、形狀（D8）
        var projected = RemoteCommandCatalog.ApplyLimits(CommandRouteRules.Project(body, spec));
        route.Command = projected;
        if (RemoteCommandShapeRules.Check(projected) is { } shape)
        {
            log.LogDebug("瀏覽器的 {Cmd} 形狀不合格：{Reason}", name, shape);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }

        // 2. 選目標（D4、D5）
        IReadOnlyList<AccountLinkFact> links = [];
        bool? accountExists = null;
        if (CommandRouteRules.LogicalAccountRef(spec, projected) is { } logicalAccountId)
        {
            using var read = writer.OpenRead();
            (accountExists, links) = store.ReadAccount(read, logicalAccountId);
        }
        string target;
        switch (spec.Route)
        {
            case RemoteRoute.Owner:
            {
                var conversationId = (string)projected[ConversationIdField]!;
                var ownership = directory.Find(conversationId);
                if (CommandRouteRules.CheckConversation(ownership, ownership is null ? null : DeviceFactOf(ownership.OwnerDeviceId), name) is { } rejection)
                {
                    route.TargetDeviceId = ownership?.OwnerDeviceId;
                    route.ConversationId = conversationId;
                    if (rejection is { CmdError: true, Code: BrowserErrorCodes.Conflict } && origin is BrowserCommandOrigin browserOrigin)
                        gateway.SendAfterInit(browserOrigin.Connection, ConversationGatewayRules.ClosedMessage(conversationId));
                    return await RejectAsync(route, rejection, cancellationToken);
                }
                target = ownership!.OwnerDeviceId;
                route.ConversationId = ownership.ConversationId;
                route.Epoch = ownership.Epoch;
                break;
            }
            case RemoteRoute.Device:
            {
                var targets = CommandRouteRules.ResolveDevices(spec, projected, accountExists, links, DeviceFactOf);
                if (targets.Rejection is { } rejection)
                    return await RejectAsync(route, rejection, cancellationToken);
                switch (targets.Kind)
                {
                    case DeviceTargetKind.Single:
                        target = targets.DeviceIds[0];
                        break;
                    case DeviceTargetKind.FanOut:
                        // 所有電腦登出（D26）：每台一列 durable，見 CommandRouter.FanOut.cs；桌面不收（durable 重簽時才知道目標有沒有 peer1，14.7）
                        if (origin is DeviceCommandOrigin)
                            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false, PeerFanOutMessage), cancellationToken);
                        return await FanOutAsync(route, projected, targets.DeviceIds, links, cancellationToken);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(spec), targets.Kind, "未知的目標種類");
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.Route, "指令路由只處理 Owner／Device");
        }
        route.TargetDeviceId = target;
        route.BrowserAccountId = CommandRouteRules.BrowserAccountId(spec, projected);
        // 桌面遠端操作（14.7）：自己的對話與帳戶在本機操作
        if (origin.DeviceId is { } originDevice && string.Equals(originDevice, target, StringComparison.Ordinal))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, PeerRules.SelfTargetMessage), cancellationToken);

        // 3. 寫入目標、換算帳戶 id、再檢查一次（D6、D8）
        var (mapped, mapRejection) = CommandRouteRules.MapAccounts(CommandRouteRules.WithTarget(projected, spec, target), spec, target, links);
        if (mapRejection is not null || mapped is null)
            return await RejectAsync(route, mapRejection ?? new RouteRejection(BrowserErrorCodes.Invalid, CmdError: true), cancellationToken);
        route.Command = mapped;
        if (RemoteCommandShapeRules.Check(mapped) is { } again)
        {
            log.LogError("瀏覽器的 {Cmd} 改寫目標與帳戶之後形狀不合格（程式錯誤）：{Reason}", name, again);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }
        return await IssueAsync(route, target, mapped, draft: null, cancellationToken);
    }

    /// <summary>
    /// 目標與改寫後的指令都定了之後（步 4～7，草稿的第一次 send 也走這裡）：層級、發起者、選連線、簽章與自驗、寫列、登記、送出、稽核。
    /// <paramref name="draft"/> 不是 null 時是草稿的第一次 send：寫列之前在草稿記下 cid 與新對話的 id（同一份草稿只記得上一個，另一個立即拒絕 busy），
    /// 寫不進去就清掉（D33）
    /// </summary>
    private async Task<CmdResult> IssueAsync(RouteState route, string target, JObject mapped, DraftSend? draft, CancellationToken cancellationToken)
    {
        var origin = route.Origin;
        var spec = route.Spec;
        var name = spec.Name;
        var commandId = route.CommandId!;

        // 問卷的補充說明（2026-11-05 起）：那台的桌面比 QuestionNoteRules.NotesMinAppVersion 舊（回報的版本認得出來才算）就把 notes 併成答案多一筆，舊版照收
        if (name == "answer" && mapped["notes"] is JObject && QuestionNoteRules.DeviceLacksNotes(devices.Get(target)?.AppVersion))
            mapped = QuestionNoteRules.FoldNotesIntoAnswers(mapped);

        // 上傳（WP4.7 計畫 D21、6.3 的 WP4.5 契約點 3）：簽章之前確認每個都已上傳到這台伺服器、雜湊與大小相符、沒過期，否則立即拒絕 invalid
        if (CommandUploadRules.Claims(mapped) is not { } claims || claims.Count > 0 && !uploads.TryVerify(claims))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, UploadsUnknownMessage), cancellationToken);

        // 4. 層級、發起者、驗證時間（D9～D11）
        var tier = RemoteTierRules.Evaluate(name, mapped, RemoteTierFacts.Unknown, RemoteSide.Server);
        route.Tier = tier;
        var issuer = await issuers.ResolveAsync(origin, cancellationToken);
        route.Issuer = issuer;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (!RemoteAuthRules.IsFresh(tier.Tier, issuer.Auth.UvAt, now))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.StepUpRequired, CmdError: false), cancellationToken);
        // 伺服器網址設定不合格時什麼都不做（不中斷任何連線、不簽，D12）
        if (Audience(options.Value) is not { } aud)
        {
            log.LogError("伺服器網址（Hub:PublicOrigin／ServerDomain）不合格，遠端指令 {Cmd} 不簽", name);
            return CmdError(route, BrowserErrorCodes.Unavailable);
        }

        // 5. 選連線（D7）
        var spki = identity.Spki;
        var candidates = registry.ConnectionsOf(target);
        var choice = CommandRouteRules.ChooseConnection(ConnectionFacts(candidates, spki));
        foreach (var abort in choice.Abort)
            Abort(candidates.First(c => c.ConnectionId == abort), "Hello 時的伺服器身分公鑰不是目前的");
        if (choice.RejectCode is { } connectionCode)
            return await RejectAsync(route, new RouteRejection(connectionCode, CmdError: false), cancellationToken);
        var device = candidates.First(c => c.ConnectionId == choice.ConnectionId);
        // 多人協作（14.14）：協作人只能送白名單裡的對話指令（閘道已擋，這裡再擋一次）；舊版桌面沒有 collab1，驗章第 9 步會拒絕 issuer.kind = collaborator
        if (origin is BrowserCommandOrigin { Connection.IsCollaborator: true })
        {
            if (!CollaboratorRules.RemoteCommands.Contains(name) || spec.Route != RemoteRoute.Owner)
                return CmdError(route, BrowserErrorCodes.Invalid);
            if (!device.Allows(DeviceHubMethods.CollabUpdate))
                return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false,
                    "「" + (string.IsNullOrWhiteSpace(devices.Get(target)?.Name) ? PeerRules.UnnamedDevice : devices.Get(target)!.Name!.Trim())
                    + "」的 Clode Myriagent 版本太舊，還不能讓協作人操作；請擁有者先更新那台"), cancellationToken);
        }
        // 桌面遠端操作（14.7）：舊版桌面沒有 peer1，驗章第 9 步會拒絕 issuer.kind = device——不送，直接說明那台要先更新
        if (origin is DeviceCommandOrigin && !device.Allows(DeviceHubMethods.PeerReply))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false, PeerRules.TargetTooOldMessage(devices.Get(target)?.Name)), cancellationToken);
        // 排程頁（14.12）：目標要協商出 sched1（舊版的桌面不認得這兩個指令）
        if (name is RemoteCommandCatalog.ScheduleAction or RemoteCommandCatalog.ScheduleAgent && !device.Allows(DeviceHubMethods.SchedulesPublish))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false,
                "「" + (string.IsNullOrWhiteSpace(devices.Get(target)?.Name) ? PeerRules.UnnamedDevice : devices.Get(target)!.Name!.Trim()) + "」的 Clode Myriagent 版本太舊，還沒有排程頁；請先更新那台"), cancellationToken);
        // 臨時對話（2026-11-11）：conversationCreate 帶 ephemeral 要那台協商出 quick1（舊版的桌面不認得這個欄位，會整則拒絕、使用者看不懂）
        if (name == RemoteCommandCatalog.ConversationCreate && mapped["ephemeral"] is { Type: JTokenType.Boolean } && !DeviceFeatureRules.Has(device.Features, DeviceFeatureRules.Quick1))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false,
                "「" + (string.IsNullOrWhiteSpace(devices.Get(target)?.Name) ? PeerRules.UnnamedDevice : devices.Get(target)!.Name!.Trim()) + "」的 Clode Myriagent 版本太舊，還沒有臨時對話；請先更新那台，或在面板上換一台電腦"), cancellationToken);
        // 定時訊息的遠端設定（2026-11-13）：舊版的桌面在閘門 5a 回 invalid（不在型錄），使用者看不懂——不送，說明那台要先更新
        if (name is RemoteCommandCatalog.TimedSave or RemoteCommandCatalog.TimedAction && !DeviceFeatureRules.Has(device.Features, DeviceFeatureRules.Timed1))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false,
                "「" + (string.IsNullOrWhiteSpace(devices.Get(target)?.Name) ? PeerRules.UnnamedDevice : devices.Get(target)!.Name!.Trim()) + "」的 Myriagent 版本太舊，還不能從其他電腦設定定時訊息；請先更新那台"), cancellationToken);
        // 移除網頁上傳的附件（2026-11-13）：遠端的 removeAttachment 一律是網頁的上傳 id（網頁看不到那台的附件 id）；舊版的桌面找不到這個 id 會回 done 卻沒移除，
        // 使用者以為拿掉了、送出時卻還在——不送，說明那台要先更新
        if (name == RemoteCommandCatalog.RemoveAttachment && !DeviceFeatureRules.Has(device.Features, DeviceFeatureRules.Att1))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false,
                "「" + (string.IsNullOrWhiteSpace(devices.Get(target)?.Name) ? PeerRules.UnnamedDevice : devices.Get(target)!.Name!.Trim()) + "」的 Clode Myriagent 版本太舊，還不能移除已附加的檔案；請先更新那台"), cancellationToken);
        // 電腦狀態面板的檔案傳輸（2026-11-09）：目標要協商出 xfer1（舊版的桌面在閘門 5a 回 invalid，使用者看不懂）
        if (RemoteCommandCatalog.DeviceTransferCommands.Contains(name) && !DeviceFeatureRules.Has(device.Features, DeviceFeatureRules.Xfer1))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false,
                RemoteExecServerRules.TransferTooOldMessage(devices.Get(target)?.Name)), cancellationToken);
        // 整個資料夾（2026-11-09）：deviceTree、deviceFetchMany 與帶 subfolder 的 devicePut 要 xfer2（舊版的桌面不認得，閘門 5a／5b 會回 invalid）
        if ((RemoteCommandCatalog.DeviceFolderCommands.Contains(name)
             || name == RemoteCommandCatalog.DevicePut && mapped[RemoteCommandCatalog.DevicePutSubfolder] is { Type: not JTokenType.Null })
            && !DeviceFeatureRules.Has(device.Features, DeviceFeatureRules.Xfer2))
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false,
                RemoteExecServerRules.FolderTransferTooOldMessage(devices.Get(target)?.Name)), cancellationToken);
        // 遠端桌面（14.16）：目標要協商出 desk1、發起的電腦這條連線也要（瀏覽器一律收得到）；不能看自己；每台同時只能一個工作階段
        if (name == RemoteCommandCatalog.DeskOpen)
        {
            if (!device.Allows(DeviceHubMethods.DeskDown))
                return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false, RemoteExecServerRules.DeskTooOldMessage(devices.Get(target)?.Name)), cancellationToken);
            if (origin is DeviceCommandOrigin deskOrigin)
            {
                if (string.Equals(deskOrigin.Device, target, StringComparison.Ordinal))
                    return CmdError(route, BrowserErrorCodes.Invalid);
                if (registry.Get(origin.ConnectionId) is not { } deskConnection || !deskConnection.Allows(DeviceHubMethods.DeskDown))
                    return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false, RemoteExecServerRules.DeskOriginTooOldMessage), cancellationToken);
            }

            if (deskRelays.CheckOpen(RemoteCommandShapeRules.IsString(mapped["requestId"], out var deskSession) ? deskSession : null, target) is { } deskProblem)
                return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Conflict, CmdError: false, deskProblem), cancellationToken);
        }

        // 遠端執行指令（14.11）：目標要能送輸出、發起的這條連線要能收（兩邊都要協商出 exec1）
        if (name == RemoteCommandCatalog.RemoteExec)
        {
            if (!device.Allows(DeviceHubMethods.ExecOutput))
                return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false, RemoteExecServerRules.TargetTooOldMessage(devices.Get(target)?.Name)), cancellationToken);
            if (registry.Get(origin.ConnectionId) is not { } originConnection || !originConnection.Allows(DeviceHubMethods.ExecReply))
                return CmdError(route, BrowserErrorCodes.NotAvailable);
        }

        // 6. 簽章與自驗（D13、D14）：以那條連線 Hello 時的身分公鑰驗，檢查與簽章之間發生輪換就驗不過
        var cid = SyncIds.NewUlid(now);
        var envelopeBody = new CommandEnvelopeBody(aud, cid, target, device.EnvelopeConn!, route.ConversationId, route.Epoch, mapped,
            tier.Tier, tier.FullAccess, Durable: false, Created: now, Iat: now, Exp: now + CommandEnvelope.MaxLifetimeMs,
            issuer.Issuer, issuer.Auth);
        if (!CommandEnvelope.TrySign(envelopeBody, identity.Sign, out var envelope))
        {
            log.LogError("遠端指令 {Cmd} 的信封簽不出來（太大或有不成對的代理字元）", name);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }
        var verdict = CommandEnvelope.Verify(envelope, cid, new CommandEnvelopeTarget(device.HelloHubSpki!, aud, target, device.EnvelopeConn!,
            AcceptsDeviceIssuer: device.Allows(DeviceHubMethods.PeerReply), AcceptsCollaboratorIssuer: device.Allows(DeviceHubMethods.CollabUpdate)),
            new ServerTimeBounds(now, now));
        if (!verdict.Ok)
        {
            if (verdict.Step == CommandEnvelopeSteps.Signature)
            {
                Abort(device, "簽章與檢查之間伺服器身分金鑰輪換了");
                return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Offline, CmdError: false), cancellationToken);
            }
            log.LogError("遠端指令 {Cmd} 的信封自驗失敗（程式錯誤）：{Step} {Detail}", name, verdict.Step, verdict.Detail);
            return CmdError(route, BrowserErrorCodes.Invalid);
        }

        // sendRejected 輸出要核對的線上 id（D20）：Owner 路由是指令的對話；草稿的第一次 send 是推得的新對話 id（D34），寫列之前先記進草稿（D33）
        var expectedConversation = spec.Route == RemoteRoute.Owner && spec.Outputs.Contains(CommandOutputRules.SendRejected, StringComparer.Ordinal)
            ? route.ConversationId
            : null;
        // 桌面在其他電腦開新對話（CommandRouter.PeerCreate.cs）：新對話的 id 同草稿的第一次 send（D34），輸出的 sendRejected 以它核對
        var peerCreate = origin is DeviceCommandOrigin && name == RemoteCommandCatalog.ConversationCreate;
        if (peerCreate)
            expectedConversation = RemoteConversationIdRules.FromCommandId(cid)!;
        if (draft is not null)
        {
            expectedConversation = RemoteConversationIdRules.FromCommandId(cid)!;
            switch (ReserveDraft(draft, cid, expectedConversation))
            {
                case DraftReservation.Reserved:
                    break;
                case DraftReservation.Busy:
                    return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Busy, CmdError: false, DraftCommandRules.CreatingMessage), cancellationToken);
                case DraftReservation.Gone:
                    return DraftGone(route, draft.DraftId);
                default:
                    throw new InvalidOperationException("未知的草稿記錄結果");
            }
        }

        // 7. 寫列（D15）；提交之後才登記、送出、寫稽核（D16）
        var masked = CommandMaskRules.Mask(mapped, spec);
        var row = new CommandRow(cid, commandId, IssuerJson(issuer.Issuer), route.ConversationId, target, route.Epoch, name,
            masked.ToString(Formatting.None), tier.Tier, tier.FullAccess, CommandEnvelope.ContentDigest(envelopeBody), Durable: false,
            UnsignedBody: null, CommandStates.Delivered, CreatedAt: now, DeliveredAt: now, ExpiresAt: CommandStateRules.ExpiresAt(durable: false, now));
        try
        {
            await writer.WriteAsync(context => store.Insert(context, row), cancellationToken);
        }
        catch (Exception ex)
        {
            // 寫入者停止（同步丟出）、提交失敗、連線斷了而工作還沒開始：都沒有寫進去，什麼都不送；草稿記下的也清掉（使用者可以再送）
            if (draft is not null)
                ReleaseDraft(draft, cid);
            if (ex is not OperationCanceledException)
                log.LogError(ex, "遠端指令 {Cmd} 寫不進 sync.db", name);
            return CmdError(route, BrowserErrorCodes.Unavailable);
        }

        tracker.Add(new TrackedCommand(cid, origin.ConnectionId, commandId, name, target,
            ExpectedConversationId: expectedConversation,
            BrowserConversationId: peerCreate ? expectedConversation : route.BrowserConversationId,
            RequestId: RemoteCommandShapeRules.IsString(mapped["requestId"], out var requestId) ? requestId : null,
            BrowserAccountId: route.BrowserAccountId,
            RestoreText: route.RestoreText,
            FanOut: false,
            DeviceName: devices.Get(target)?.Name,
            DeadlineAt: row.ExpiresAt,
            DraftSessionId: draft?.SessionId,
            OriginDeviceId: origin.DeviceId));
        counters.Issued();
        // 遠端執行指令（14.11）：送出之前登記，那台最早的輸出就找得到發起的電腦
        if (name == RemoteCommandCatalog.RemoteExec && origin.DeviceId is { } execOrigin)
            execRelays.Register(cid, execOrigin, commandId, target, mapped["timeoutSec"] is JValue { Type: JTokenType.Integer, Value: long seconds } ? (int)seconds : RemoteExecRules.DefaultTimeoutSeconds);
        // 遠端桌面（14.16）：送出之前登記，那台的第一個影格就找得到看的一方
        if (name == RemoteCommandCatalog.DeskOpen && RemoteCommandShapeRules.IsString(mapped["requestId"], out var deskSessionId))
            deskRelays.Register(deskSessionId, target, origin);
        if (peerCreate)
            SendPeerCreated(origin, commandId, expectedConversation!);
        _ = DeliverAsync(device.ConnectionId, new CommandDelivery(cid, envelope!), name);
        _ = audit.WriteAsync(AuditEntryOf(route, AuditOutcomes.Ok, cid, null, masked));
        return new CmdResult(true, commandId);
    }

    /// <summary>
    /// 信封的 <c>aud</c>（D12）：<c>NormalizeOrigin(PublicOrigin(options))</c>——ASCII 網域時就是 <c>PublicOrigin</c>，IDN 網域另轉成 punycode，
    /// 與桌面的 <c>SyncCommandRules.Audience(ServerUrl)</c> 同一套規則。網域設定不合格（<c>PublicOrigin</c> 丟例外或正規化不出來）回 null
    /// </summary>
    internal static string? Audience(HubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return HostingRules.NormalizeOrigin(HostingRules.PublicOrigin(options));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 稽核 <c>command.issued</c> 的 detail（D29）：<c>{cmd, device, conv, tier, fullAccess, durable, label, code, masked}</c>。<c>masked</c> 是遮蔽後指令的 JSON 字串
    /// （<see cref="CommandMaskRules.AuditMasked"/>）；序列化後（稽核以 System.Text.Json 寫，非 ASCII 與引號會跳脫成六個字元）整個 detail 不小於
    /// <see cref="AuditLog.MaxDetailChars"/> 字時 <c>masked</c> 改成 <c>{"truncated":true,"len":N}</c>，detail 一定是完整的 JSON（不被截斷）
    /// </summary>
    internal static object AuditDetail(string cmd, string? device, string? conversation, RemoteTierResult? tier, bool durable, string? label, string? code, JObject masked)
    {
        var maskedJson = CommandMaskRules.AuditMasked(masked);
        var detail = Detail(maskedJson);
        if (System.Text.Json.JsonSerializer.Serialize(detail, KvStore.Json).Length < AuditLog.MaxDetailChars)
            return detail;
        return Detail(new JObject { ["truncated"] = true, ["len"] = masked.ToString(Formatting.None).Length }.ToString(Formatting.None));

        object Detail(string value) => new
        {
            cmd,
            device,
            conv = conversation,
            tier = tier?.Tier,
            fullAccess = tier?.FullAccess,
            durable,
            label,
            code,
            masked = value,
        };
    }

    /// <summary>從桌面發起的所有電腦登出（14.7 不收）</summary>
    internal const string PeerFanOutMessage = "桌面不能一次登出所有電腦：請逐台登出，或改用網頁版";

    /// <summary><c>Cmd</c> 回錯誤（D2）；<c>send</c> 的 invalid／unavailable 另把文字還回輸入框（D22）。toast 由 <see cref="BrowserCommandHandler"/> 送</summary>
    private CmdResult CmdError(RouteState route, string error)
    {
        if (CommandMessageRules.RestoresText(route.Spec.Name, error))
            SendRejected(route);
        return new CmdResult(false, route.CommandId, error);
    }

    /// <summary>路由規則的拒絕：<c>Cmd</c> 的錯誤（<see cref="RouteRejection.CmdError"/>）或立即拒絕</summary>
    private Task<CmdResult> RejectAsync(RouteState route, RouteRejection rejection, CancellationToken cancellationToken)
        => rejection.CmdError ? Task.FromResult(CmdError(route, rejection.Code)) : RejectImmediatelyAsync(route, rejection, cancellationToken);

    /// <summary>
    /// 立即拒絕（D3）：不簽、不寫列；<c>Cmd</c> 回 accepted，依序入列 <c>commandResult rejected</c>、toast（D23；照 10 秒節流與不提示的指令）、
    /// <c>send</c> 的 <c>sendRejected</c>（D22），最後寫一筆 denied 的稽核（沒有 cid，target 是 null）
    /// </summary>
    private async Task<CmdResult> RejectImmediatelyAsync(RouteState route, RouteRejection rejection, CancellationToken cancellationToken)
    {
        // 稽核的發起者標籤：目標與連線之前就被拒的還沒查工作階段
        route.Issuer ??= await issuers.ResolveAsync(route.Origin, cancellationToken);
        var name = route.Spec.Name;
        var stepUpTier = rejection.Code == RemoteResultCodes.StepUpRequired ? route.Tier?.Tier : null;
        replies.Send(route.Origin, route.CommandId, new ClientOutboxMessage(CommandMessageRules.CommandResultType,
            CommandMessageRules.CommandResultJson(route.CommandId!, RemoteResultStates.Rejected, rejection.Code, rejection.Message, stepUpTier)));
        if (!BrowserCommandRules.IsSilent(name)
            && CommandMessageRules.ToastText(RemoteResultStates.Rejected, rejection.Code, rejection.Message) is { } text)
            replies.SendToast(route.Origin, route.CommandId, name, new ClientOutboxMessage("toast", BrowserCommandRules.ToastJson(text, BrowserCommandRules.ToastLevel)));
        if (CommandMessageRules.RestoresText(name, RemoteResultStates.Rejected))
            SendRejected(route);
        counters.RejectedAtServer();
        _ = audit.WriteAsync(AuditEntryOf(route, AuditOutcomes.Denied, null, rejection.Code, CommandMaskRules.Mask(route.Command ?? route.Body, route.Spec)));
        return new CmdResult(true, route.CommandId);
    }

    /// <summary><c>sendRejected {conversationId: 瀏覽器的 id, text}</c>：只在知道對話與文字時送</summary>
    private void SendRejected(RouteState route)
    {
        if (route.BrowserConversationId is { } conversation && route.RestoreText is { } text)
            replies.Send(route.Origin, route.CommandId, new ClientOutboxMessage(CommandMessageRules.SendRejectedType, CommandMessageRules.SendRejectedJson(conversation, text)));
    }

    /// <summary>送 <c>CommandDelivery</c>（不等待；失敗只記錄：結果期限到了列會變成 unknown）</summary>
    private async Task DeliverAsync(string connectionId, CommandDelivery delivery, string name)
    {
        try
        {
            await sender.SendAsync(connectionId, DeviceHubMethods.CommandDelivery, delivery, CancellationToken.None);
            counters.Delivered();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "遠端指令 {Cmd}（{CommandId}）送不到電腦的連線 {Connection}", name, delivery.CommandId, connectionId);
        }
    }

    private void Abort(DeviceConnection connection, string reason)
    {
        log.LogInformation("中斷裝置連線 {Connection}（{DeviceId}）：{Reason}", connection.ConnectionId, connection.DeviceId, reason);
        counters.Aborted();
        try
        {
            connection.Abort();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "中斷裝置連線失敗：{Connection}", connection.ConnectionId);
        }
    }

    private DeviceFact? DeviceFactOf(string deviceId) => devices.Get(deviceId) is { } device ? new DeviceFact(device.Id, device.Name, device.Revoked) : null;

    /// <summary>選連線用的快照（D7）：<paramref name="spki"/> 是伺服器目前的身分公鑰</summary>
    private static List<ConnectionFact> ConnectionFacts(IReadOnlyList<DeviceConnection> candidates, string spki) =>
        candidates.Select(c => new ConnectionFact(c.ConnectionId, c.Order, c.IsProven, c.Allows(DeviceHubMethods.CommandDelivery), c.CanSignEnvelopes(spki))).ToList();

    private static AuditEntry AuditEntryOf(RouteState route, string outcome, string? cid, string? code, JObject masked) =>
        AuditEntryOf(route, outcome, cid, code, masked, route.TargetDeviceId, route.Tier, durable: false);

    /// <summary>稽核的一筆（D29）：fan-out 的每台各一筆，帶那台的裝置、層級與 durable</summary>
    private static AuditEntry AuditEntryOf(RouteState route, string outcome, string? cid, string? code, JObject masked, string? device, RemoteTierResult? tier, bool durable) =>
        new(route.Origin.AuditActor, route.Origin.AuditActorId, route.Origin.Ip, AuditActions.CommandIssued, cid, outcome,
            AuditDetail(route.Spec.Name, device, route.ConversationId, tier, durable, route.Issuer?.Issuer.Label, code, masked));

    /// <summary>列的 <c>issuer</c>：<c>{"kind":"browser","label":…}</c>（不縮排）</summary>
    private static string IssuerJson(CommandIssuer issuer) =>
        new JObject { ["kind"] = issuer.Kind, ["label"] = issuer.Label }.ToString(Formatting.None);

    /// <summary>被拒時要還回輸入框的文字（D22）：<c>send</c> 的 <c>text</c>（草稿的第一次 send 也是它，見 <c>CommandRouter.Drafts.cs</c>）</summary>
    private static string? RestoreTextOf(string name, JObject body) =>
        name == Send && RemoteCommandShapeRules.IsString(body["text"], out var text) ? text : null;

    /// <summary>一次路由過程中逐步知道的事（稽核、立即拒絕、還原文字用）</summary>
    private sealed class RouteState(CommandOrigin origin, RemoteCommandSpec spec, string? commandId, JObject body)
    {
        public CommandOrigin Origin { get; } = origin;

        /// <summary>瀏覽器發起的連線（伺服器草稿與所有電腦登出只由瀏覽器發起）</summary>
        public BrowserConnection Connection => Origin is BrowserCommandOrigin browser
            ? browser.Connection
            : throw new InvalidOperationException("這一段只處理瀏覽器發起的指令");

        public RemoteCommandSpec Spec { get; } = spec;

        public string? CommandId { get; } = commandId;

        /// <summary>瀏覽器送來的原始 cmd 物件</summary>
        public JObject Body { get; } = body;

        /// <summary>目前的指令（投影後，之後是改寫目標與帳戶後的）；投影之前是 null</summary>
        public JObject? Command { get; set; }

        /// <summary>瀏覽器看到的對話 id（Owner 路由的 <c>conversationId</c> 字串）</summary>
        public string? BrowserConversationId { get; init; }

        public string? RestoreText { get; init; }

        public string? TargetDeviceId { get; set; }

        /// <summary>信封的 <c>conv</c>（Owner 路由）</summary>
        public string? ConversationId { get; set; }

        public long? Epoch { get; set; }

        public string? BrowserAccountId { get; set; }

        public RemoteTierResult? Tier { get; set; }

        public CommandIssuerFacts? Issuer { get; set; }
    }
}
