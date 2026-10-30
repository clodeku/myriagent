using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Realtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 所有電腦登出（WP4.5 計畫步 8；D26、D27）：<c>accountLogout</c>、<c>la_</c> 帳戶、沒有 deviceId 時，對連結的每台未撤銷電腦各自判斷（照 id 排序）：
/// <list type="bullet">
/// <item>在線、有 cmd1、能簽：簽信封、寫一列 durable <c>delivered</c>，提交之後立刻送（記下送在哪條連線、登記追蹤標成 fan-out），
/// 並請 <see cref="DurableCommands.Recheck"/> 再看一次那台（選連線之後才做完 Prove 的新連線由它補送）。</item>
/// <item>不在線，或有 cmd1 的連線都不能簽（中斷它們）、簽章與檢查之間輪換了：寫一列 durable <c>pending</c>，提交之後再看一次那台有沒有可以送的連線
/// （<see cref="DurableCommands.Kick"/>），之後由 <see cref="DurableCommands"/> 在它上線時重簽。</item>
/// <item>在線但沒有 cmd1：不寫列，算「不支援」。</item>
/// </list>
/// 每列有自己的 cid、<c>browser_command_id</c> 相同、<c>unsigned_body</c> 是 <see cref="DurableBodyRules.Serialize"/>、<c>expires_at</c> 是 <c>created</c>＋7 天，
/// 所有列在同一個寫入交易裡。瀏覽器只收到一則 <c>commandResult done</c>（<see cref="CommandMessageRules.FanOutText"/>）與同文字的 success toast；
/// 一列都沒寫（全部不支援）時立即拒絕 unsupported。各台的最終結果另以 toast 告訴發起的連線（<see cref="CommandResults"/>）。稽核每台一筆（durable）
/// </summary>
internal sealed partial class CommandRouter
{
    private async Task<CmdResult> FanOutAsync(RouteState route, JObject projected, IReadOnlyList<string> deviceIds, IReadOnlyList<AccountLinkFact> links,
        CancellationToken cancellationToken)
    {
        var spec = route.Spec;
        var name = spec.Name;
        var connection = route.Connection;
        var commandId = route.CommandId!;
        route.BrowserAccountId = CommandRouteRules.BrowserAccountId(spec, projected);
        var issuer = await issuers.ResolveAsync(route.Origin, cancellationToken);
        route.Issuer = issuer;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (Audience(options.Value) is not { } aud)
        {
            log.LogError("伺服器網址（Hub:PublicOrigin／ServerDomain）不合格，遠端指令 {Cmd} 不簽", name);
            return CmdError(route, BrowserErrorCodes.Unavailable);
        }

        // 1. 每台：寫入目標、換算帳戶、再檢查、層級、選連線（D6～D9、D7）
        var spki = identity.Spki;
        var plans = new List<FanOutPlan>(deviceIds.Count);
        var unsupported = 0;
        foreach (var deviceId in deviceIds)
        {
            var (mapped, mapRejection) = CommandRouteRules.MapAccounts(CommandRouteRules.WithTarget(projected, spec, deviceId), spec, deviceId, links);
            if (mapRejection is not null || mapped is null || RemoteCommandShapeRules.Check(mapped) is not null)
            {
                // 目標就是有連結的電腦，換算一定找得到、改寫後一定合格
                log.LogError("所有電腦登出：{Cmd} 對電腦 {DeviceId} 改寫目標與帳戶之後不合格（程式錯誤）", name, deviceId);
                return CmdError(route, BrowserErrorCodes.Invalid);
            }
            var tier = RemoteTierRules.Evaluate(name, mapped, RemoteTierFacts.Unknown, RemoteSide.Server);
            route.Tier ??= tier;
            if (!RemoteAuthRules.IsFresh(tier.Tier, issuer.Auth.UvAt, now))
                return await RejectAsync(route, new RouteRejection(RemoteResultCodes.StepUpRequired, CmdError: false), cancellationToken);

            var candidates = registry.ConnectionsOf(deviceId);
            var choice = CommandRouteRules.ChooseConnection(ConnectionFacts(candidates, spki));
            foreach (var abort in choice.Abort)
                Abort(candidates.First(c => c.ConnectionId == abort), "Hello 時的伺服器身分公鑰不是目前的");
            if (choice.RejectCode == RemoteResultCodes.Unsupported)
            {
                unsupported++;
                continue;
            }
            var device = choice.ConnectionId is { } chosen ? candidates.First(c => c.ConnectionId == chosen) : null;
            plans.Add(new FanOutPlan(deviceId, SyncIds.NewUlid(now), mapped, tier, device));
        }
        if (plans.Count == 0)
            return await RejectAsync(route, new RouteRejection(RemoteResultCodes.Unsupported, CmdError: false), cancellationToken);

        // 2. 簽章與自驗（D13、D14）：能送的那幾台；不能送的只算內容摘要（conn、iat、exp 不在摘要與 unsigned_body 裡）
        foreach (var plan in plans)
        {
            plan.Body = new CommandEnvelopeBody(aud, plan.Cid, plan.DeviceId, plan.Connection?.EnvelopeConn ?? "", null, null, plan.Command,
                plan.Tier.Tier, plan.Tier.FullAccess, Durable: true, Created: now, Iat: now, Exp: CommandStateRules.ResignExp(now, now),
                issuer.Issuer, issuer.Auth);
            if (plan.Connection is not { } device)
                continue;
            if (!CommandEnvelope.TrySign(plan.Body, identity.Sign, out var envelope))
            {
                log.LogError("遠端指令 {Cmd} 的信封簽不出來（太大或有不成對的代理字元）", name);
                return CmdError(route, BrowserErrorCodes.Invalid);
            }
            var verdict = CommandEnvelope.Verify(envelope, plan.Cid, new CommandEnvelopeTarget(device.HelloHubSpki!, aud, plan.DeviceId, device.EnvelopeConn!, device.Allows(DeviceHubMethods.PeerReply)),
                new ServerTimeBounds(now, now));
            if (!verdict.Ok)
            {
                if (verdict.Step != CommandEnvelopeSteps.Signature)
                {
                    log.LogError("遠端指令 {Cmd} 的信封自驗失敗（程式錯誤）：{Step} {Detail}", name, verdict.Step, verdict.Detail);
                    return CmdError(route, BrowserErrorCodes.Invalid);
                }
                // 簽章與檢查之間伺服器身分金鑰輪換了：中斷那條連線，這台改成 pending（重連 Prove 之後重簽）
                Abort(device, "簽章與檢查之間伺服器身分金鑰輪換了");
                plan.Connection = null;
                continue;
            }
            plan.Envelope = envelope;
        }

        // 3. 寫列（D15）：同一個寫入交易；提交之後才登記、送出、寫稽核（D16）
        var issuerJson = IssuerJson(issuer.Issuer);
        foreach (var plan in plans)
        {
            plan.Masked = CommandMaskRules.Mask(plan.Command, spec);
            var delivered = plan.Envelope is not null;
            plan.Row = new CommandRow(plan.Cid, commandId, issuerJson, null, plan.DeviceId, null, name, plan.Masked.ToString(Formatting.None), plan.Tier.Tier,
                plan.Tier.FullAccess, CommandEnvelope.ContentDigest(plan.Body!), Durable: true, UnsignedBody: DurableBodyRules.Serialize(plan.Body!),
                delivered ? CommandStates.Delivered : CommandStates.Pending, CreatedAt: now, DeliveredAt: delivered ? now : null,
                ExpiresAt: CommandStateRules.ExpiresAt(durable: true, now));
        }
        try
        {
            await writer.WriteAsync(context =>
            {
                foreach (var plan in plans)
                    store.Insert(context, plan.Row!);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                log.LogError(ex, "遠端指令 {Cmd}（所有電腦登出）寫不進 sync.db", name);
            return CmdError(route, BrowserErrorCodes.Unavailable);
        }

        var deliveredCount = plans.Count(p => p.Envelope is not null);
        var pendingCount = plans.Count - deliveredCount;
        foreach (var plan in plans.Where(p => p.Envelope is not null))
        {
            tracker.Add(new TrackedCommand(plan.Cid, connection.ConnectionId, commandId, name, plan.DeviceId,
                ExpectedConversationId: null, BrowserConversationId: null, RequestId: null, BrowserAccountId: route.BrowserAccountId, RestoreText: null,
                FanOut: true, DeviceName: devices.Get(plan.DeviceId)?.Name, DeadlineAt: now + CommandStateRules.ResultDeadlineMs));
            durable.MarkSent(plan.Cid, plan.Connection!.ConnectionId);
            // 那台的新連線可能在列提交之前就做完 Prove 的掃描（什麼都沒讀到）：再看一次，有更新的連線就以它重簽補送（P4R-STATE-01）
            durable.Recheck(plan.DeviceId);
        }
        var text = CommandMessageRules.FanOutText(deliveredCount, pendingCount, unsupported);
        gateway.SendAfterInit(connection, new ClientOutboxMessage(CommandMessageRules.CommandResultType,
            CommandMessageRules.CommandResultJson(commandId, RemoteResultStates.Done, null, text, null)));
        if (!BrowserCommandRules.SilentCommands.Contains(name))
            gateway.SendToast(connection, name, new ClientOutboxMessage("toast", BrowserCommandRules.ToastJson(text, CommandMessageRules.SuccessLevel)));
        foreach (var plan in plans)
        {
            counters.Issued();
            if (plan.Envelope is { } envelope)
                _ = DeliverAsync(plan.Connection!.ConnectionId, new CommandDelivery(plan.Cid, envelope), name);
            else
                durable.Kick(plan.DeviceId);
            _ = audit.WriteAsync(AuditEntryOf(route, AuditOutcomes.Ok, plan.Cid, null, plan.Masked!, plan.DeviceId, plan.Tier, durable: true));
        }
        log.LogInformation("所有電腦登出：已送出 {Delivered} 台、排入 {Pending} 台、不支援 {Unsupported} 台", deliveredCount, pendingCount, unsupported);
        return new CmdResult(true, commandId);
    }

    /// <summary>所有電腦登出時一台電腦的計畫：要送的連線（null＝pending）、改寫後的指令、層級，之後依序填上信封內容、信封、遮蔽後的指令與列</summary>
    private sealed class FanOutPlan(string deviceId, string cid, JObject command, RemoteTierResult tier, DeviceConnection? connection)
    {
        public string DeviceId { get; } = deviceId;

        public string Cid { get; } = cid;

        public JObject Command { get; } = command;

        public RemoteTierResult Tier { get; } = tier;

        public DeviceConnection? Connection { get; set; } = connection;

        public CommandEnvelopeBody? Body { get; set; }

        public CommandEnvelope? Envelope { get; set; }

        public JObject? Masked { get; set; }

        public CommandRow? Row { get; set; }
    }
}
