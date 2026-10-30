using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;

namespace MyriagentHub.Sync;

/// <summary>
/// 電腦回報的遠端指令結果與輸出（WP4.5 計畫步 7；D17～D23、D26；sync-protocol 14.6、契約 D18）。只收目標電腦已證明、協商出 <c>cmd1</c> 的連線
/// （同一台的任一條都算：重連後才送回的結果合法），只轉給發起的瀏覽器連線（<see cref="CommandTracker"/> 記的那條）：
/// <list type="bullet">
/// <item>結果：格式不合格丟 <c>HubException("invalid")</c>；cid 不存在或不是這台的、連線沒協商 cmd1 的靜靜略過（計數 <c>ignoredResults</c>，
/// 不讓別台試出 cid）。在寫入交易裡核對目標並套 <see cref="CommandStateRules.OnResult"/>，提交之後才轉送：<c>commandResult</c>、
/// rejected／unknown 的 toast（電腦轉來過 toast 就不再送，D23）、<c>send</c> 被拒時的 <c>sendRejected</c>（電腦轉來過就不再送，D22）。
/// 所有電腦登出的列（fan-out）改送那台的 toast（D26）。遲到的結果只記進列。durable 的 busy／expired 不轉送，交給
/// <see cref="DurableCommands.ScheduleRetry"/>（D27）。</item>
/// <item>輸出：要在追蹤裡（伺服器重啟之後的丟掉）、列還沒結束或結束後 <see cref="CommandStateRules.OutputGraceMs"/> 內，依序通過
/// <see cref="RemoteResultRules.CheckOutput"/>、<see cref="CommandOutputRules.Project"/> 與 WP4.7 的 <see cref="ICommandOutputRewriter"/>，只以 <see cref="BrowserGateway.SendAfterInit"/>
/// 送給發起的連線；不寫 sync.db、不寫稽核、不記內容（裝置碼也一樣）。丟掉的計數 <c>droppedOutputs</c>。</item>
/// </list>
/// 結果期限（100 秒）到了的列由 <see cref="CommandMaintenance"/> 標成 unknown，經 <see cref="ForwardFinal"/> 轉送。
/// 桌面發起的（sync-protocol 14.7）經 <see cref="CommandReplies"/> 以 <c>PeerReply</c> 送回那台電腦，內容與瀏覽器收到的相同
/// </summary>
internal sealed class CommandResults(
    SyncWriter writer,
    CommandStore store,
    CommandTracker tracker,
    CommandReplies replies,
    CommandCounters counters,
    DurableCommands durable,
    IBrowserDrafts drafts,
    ICommandOutputRewriter rewriter,
    TimeProvider time,
    ILogger<CommandResults> log)
{
    /// <summary>fan-out 那台的名稱不在追蹤裡時（不應發生）</summary>
    private const string UnnamedDevice = "那台電腦";

    /// <summary>
    /// 裝置 hub 的 <c>CommandResult</c>（<paramref name="connection"/> 是這條裝置連線的登記；不在登記裡是 null）。
    /// 提交之後才完成：電腦拿到回應時結果一定已在 sync.db
    /// </summary>
    /// <exception cref="HubException"><c>invalid</c>：結果的格式不合格（<see cref="RemoteResultRules.Check"/>）</exception>
    public async Task ReceiveResultAsync(DeviceConnection? connection, Myriagent.Sync.CommandResult? result)
    {
        if (connection is null || !connection.IsProven || !connection.Allows(DeviceHubMethods.CommandResult))
        {
            log.LogWarning("沒有協商出 cmd1（或還沒證明）的裝置連線回報遠端指令結果，不理會：{DeviceId} {Connection}", connection?.DeviceId, connection?.ConnectionId);
            counters.IgnoredResult();
            return;
        }
        if (RemoteResultRules.Check(result) is { } problem)
        {
            log.LogWarning("電腦 {DeviceId} 回報的遠端指令結果格式不合格：{Problem}", connection.DeviceId, problem);
            throw new HubException(SyncErrorCodes.Invalid);
        }

        var applied = await writer.WriteAsync(context => store.ApplyResult(context, connection.DeviceId, result!));
        var commandId = result!.CommandId;
        switch (applied.Disposition)
        {
            case ResultDisposition.NotFound:
            case ResultDisposition.OtherDevice:
                log.LogWarning("電腦 {DeviceId} 回報了不是它的（或不存在的）遠端指令 {CommandId}，不理會", connection.DeviceId, commandId);
                counters.IgnoredResult();
                return;
            case ResultDisposition.Applied:
                break;
            default:
                throw new InvalidOperationException("未知的結果處置：" + applied.Disposition);
        }

        var step = applied.Step!;
        if (step.Late)
        {
            log.LogInformation("遠端指令 {CommandId} 已經結束，電腦 {DeviceId} 遲到的結果 {State} 只記進列", commandId, connection.DeviceId, result.State);
            return;
        }
        if (step.Resend)
        {
            // durable 的 busy／expired：電腦什麼都沒執行，列已回到 pending；5 秒後在同一條連線重簽重送，每條連線最多 3 次（D27），之後每 60 秒的重掃再送
            var scheduledRetry = durable.ScheduleRetry(connection.DeviceId, connection.ConnectionId, commandId);
            log.LogInformation("durable 遠端指令 {CommandId} 被電腦 {DeviceId} 以 {Code} 拒絕，回到 pending（{Next}）", commandId, connection.DeviceId, result.Code,
                scheduledRetry ? "5 秒後重送" : "這條連線的 5 秒重送滿了，等下一次 Prove 或 60 秒的重掃");
            return;
        }
        if (applied.Durable)
            durable.Completed(commandId);
        Count(step.State);
        ForwardFinal(commandId, result.State, result.Code, result.Message, result.Tier);
    }

    /// <summary>
    /// 裝置 hub 的 <c>CommandOutput</c>：合格的轉給發起的連線，其他的丟掉（不回錯誤給電腦）。內容一律不記錄。
    /// 投影之後經 WP4.7 的 <see cref="ICommandOutputRewriter"/>（檔案指令的媒體參照換成本站網址、登記 <c>media_refs</c>；回 null 就丟掉），
    /// 改寫完成才送出、才回給電腦（同一條連線的輸出與結果照順序）
    /// </summary>
    public async Task ReceiveOutputAsync(DeviceConnection? connection, Myriagent.Sync.CommandOutput? output, CancellationToken cancellationToken = default)
    {
        if (connection is null || !connection.IsProven || !connection.Allows(DeviceHubMethods.CommandOutput))
        {
            Drop(output, connection, "連線沒有協商出 cmd1");
            return;
        }
        if (output is null || !SyncIds.IsUlid(output.CommandId) || !tracker.TryGet(output.CommandId, out var tracked))
        {
            Drop(output, connection, "不在追蹤裡（不存在、太久以前或伺服器重啟之前的指令）");
            return;
        }
        if (!string.Equals(tracked.TargetDeviceId, connection.DeviceId, StringComparison.Ordinal))
        {
            Drop(output, connection, "不是目標電腦");
            return;
        }
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (tracker.CompletedAt(output.CommandId) is { } completedAt && now - completedAt > CommandStateRules.OutputGraceMs)
        {
            Drop(output, connection, "指令結束超過 30 秒");
            return;
        }
        if (RemoteResultRules.CheckOutput(output, tracked.CmdName) is not null
            || CommandOutputRules.Project(output.Message, new OutputContext(tracked.CmdName, tracked.ExpectedConversationId, tracked.BrowserConversationId,
                tracked.RequestId, tracked.BrowserAccountId)) is not { } projected)
        {
            Drop(output, connection, "不在白名單或形狀不合格");
            return;
        }
        var conversationId = tracked.BrowserConversationId is { } browserConversation && ConversationIds.IsWireId(browserConversation) ? browserConversation : null;
        if (await rewriter.RewriteAsync(connection.DeviceId, tracked.CmdName, conversationId, projected, cancellationToken) is not { } rewritten)
        {
            Drop(output, connection, "改寫器丟掉（對話的擁有者不是這台、媒體參照寫不進去或改寫後太大）");
            return;
        }
        projected = rewritten;
        if (!replies.IsPresent(tracked))
            return;
        var type = (string)projected["type"]!;
        replies.Send(tracked, new ClientOutboxMessage(type, projected.ToString(Formatting.None)));
        if (type is CommandOutputRules.Toast or CommandOutputRules.SendRejected)
            tracker.MarkForwarded(output.CommandId, type);
    }

    /// <summary>
    /// 列到了最終狀態之後轉給發起的連線（有追蹤的話；發起的連線不在了就只寫列），之後 <see cref="CommandStateRules.OutputGraceMs"/> 內還收輸出：
    /// <list type="bullet">
    /// <item>fan-out（所有電腦登出）：只送那台的 toast（<see cref="CommandMessageRules.DeviceToast"/>，不節流：每台各一則），不送 <c>commandResult</c>（D26）。</item>
    /// <item>其他：<c>commandResult</c>（<c>commandId</c> 是瀏覽器的）；rejected／unknown 另送 toast（照 10 秒節流與不提示的指令；電腦轉來過 toast 就不送，D23）；
    /// <c>send</c> 被拒時送 <c>sendRejected</c>（電腦轉來過就不送，D22）。</item>
    /// </list>
    /// </summary>
    /// <param name="state"><see cref="RemoteResultStates"/> 的 done、rejected、unknown</param>
    public void ForwardFinal(string commandId, string state, string? code, string? message, int? tier)
    {
        if (!tracker.TryGet(commandId, out var tracked))
            return;
        // 草稿的第一次 send 被拒（電腦保證什麼都沒建，契約 C17）：先清掉草稿記下的 cid，使用者收到 sendRejected 之後可以馬上再送（D33）；done、unknown 保留
        if (state == RemoteResultStates.Rejected && tracked is { DraftSessionId: { } draftSession, BrowserConversationId: { } draftId })
            DraftCommandOutcomes.Release(drafts, draftSession, draftId, commandId);
        if (replies.IsPresent(tracked))
        {
            if (tracked.FanOut)
                ForwardFanOut(tracked, state, code, message);
            else
                Forward(tracked, state, code, message, tier);
        }
        tracker.MarkCompleted(commandId, time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private void Forward(TrackedCommand tracked, string state, string? code, string? message, int? tier)
    {
        replies.Send(tracked, new ClientOutboxMessage(CommandMessageRules.CommandResultType,
            CommandMessageRules.CommandResultJson(tracked.BrowserCommandId, state, code, message, tier)));
        if (!tracker.WasForwarded(tracked.CommandId, CommandOutputRules.Toast) && !BrowserCommandRules.IsSilent(tracked.CmdName)
            && CommandMessageRules.ToastText(state, code, message) is { } text)
            replies.SendToast(tracked, new ClientOutboxMessage(CommandOutputRules.Toast, BrowserCommandRules.ToastJson(text, BrowserCommandRules.ToastLevel)));
        if (CommandMessageRules.RestoresText(tracked.CmdName, state) && !tracker.WasForwarded(tracked.CommandId, CommandOutputRules.SendRejected)
            && tracked.BrowserConversationId is { } conversation && tracked.RestoreText is { } restore)
            replies.Send(tracked, new ClientOutboxMessage(CommandMessageRules.SendRejectedType, CommandMessageRules.SendRejectedJson(conversation, restore)));
    }

    private void ForwardFanOut(TrackedCommand tracked, string state, string? code, string? message)
    {
        var (text, level) = CommandMessageRules.DeviceToast(tracked.DeviceName ?? UnnamedDevice, state, code, message);
        replies.Send(tracked, new ClientOutboxMessage(CommandOutputRules.Toast, BrowserCommandRules.ToastJson(text, level)));
    }

    private void Count(string state)
    {
        switch (state)
        {
            case CommandStates.Done:
                counters.Done();
                break;
            case CommandStates.Rejected:
                counters.Rejected();
                break;
            case CommandStates.Unknown:
                counters.Unknown();
                break;
            case CommandStates.Expired:
                counters.Expired();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "不是最終狀態");
        }
    }

    /// <summary>丟掉一則輸出（只記 cid 與原因，不記內容）</summary>
    private void Drop(Myriagent.Sync.CommandOutput? output, DeviceConnection? connection, string reason)
    {
        log.LogDebug("丟掉電腦 {DeviceId} 的遠端指令輸出 {CommandId}：{Reason}", connection?.DeviceId, output?.CommandId, reason);
        counters.DroppedOutput();
    }
}
