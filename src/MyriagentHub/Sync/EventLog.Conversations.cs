using Myriagent.Sync;
using MyriagentHub.Data;

namespace MyriagentHub.Sync;

/// <summary>第 4 階段的對話事件（WP4.2 計畫步 3；D3、D4、D8、D13、D16、D37、D44）</summary>
internal sealed partial class EventLog
{
    /// <summary>
    /// 套用一筆對話事件（寫入者裡；預先檢查已在寫入者之外做完）：
    /// <list type="number">
    /// <item>預先檢查有拒絕就照它（invalid、too_large；不會是 dlp：命中時已遮蔽，答 5）。</item>
    /// <item>授權：經交易查 <c>conversations</c> 表（D4），<see cref="ConversationApplyRules.Decide"/>；<c>not_owner</c> 帶目前的擁有者、<c>fenced</c> 帶目前的 epoch。</item>
    /// <item><c>SAVEPOINT ev</c>：寫 events（命中 DLP 時存遮蔽版、<c>payload_sha256</c> 一律是裝置送的，D44）、物化。
    /// 毒事件（<see cref="ConversationFaultRules.IsPoison"/>）回滾這一筆、回 invalid，同批其他事件照收；其他例外照舊往外丟，由批次幫浦中斷連線（D37）。</item>
    /// </list>
    /// 被拒的 <c>rejected_events.payload</c> 只在 <see cref="ConversationPrecheck.DlpClean"/> 為真時存原文（D8、提案 C6）。
    /// </summary>
    private BatchEventResult ApplyConversation(SyncWriteContext context, ConversationMaterializer materializer, UpstreamContext upstream, SyncEvent e,
        ConversationPrecheck pre)
    {
        var deviceId = upstream.DeviceId;
        var kind = e.Kind;
        if (pre.Rejected is { } early)
            return RejectConversation(context, materializer, deviceId, e, early, pre.DlpClean);

        var mark = context.AfterCommitMark;
        context.Execute("SAVEPOINT ev");
        ConversationDecision decision;
        long seq;
        try
        {
            var row = materializer.ReadRow(context, e.ConversationId!);
            var incomingPolicy = kind is ConversationEventKinds.Created or ConversationEventKinds.Meta ? (string?)pre.Parsed!["syncPolicy"] : null;
            decision = ConversationApplyRules.Decide(row, deviceId, kind, e.Epoch!.Value, incomingPolicy);
            if (decision.Rejected is null)
            {
                // 命中 DLP 的事件存遮蔽版（payloadSha256 不變：同一筆重送照樣判成 Duplicate、回原 seq，F50）
                var stored = pre.Remasked ? e with { Payload = pre.StorePayload!, Enc = pre.StoreEnc! } : e;
                seq = SyncEventStore.InsertDevice(context, notifier, deviceId, stored, ConversationEventKinds.ClassOf(kind)!);
                var fresh = ConversationApplyRules.IsFresh(upstream.ResyncFrom, upstream.HelloOutbox?.NextDeviceSeq, e.DeviceSeq);
                // recovering 只由擁有者被接受的新 conv.created 解除（D16；對帳重送的舊 created 照常套用 meta、不解除）
                var clearsRecovering = kind == ConversationEventKinds.Created
                    && ConversationApplyRules.ClearsRecovering(upstream.ResyncFrom, upstream.HelloOutbox?.NextDeviceSeq, e.DeviceSeq);
                materializer.Apply(context, deviceId, stored, pre, seq, decision, clearsRecovering, fresh);
            }
            else
            {
                seq = 0;
            }
        }
        catch (Exception ex) when (ConversationFaultRules.IsPoison(ex))
        {
            context.Execute("ROLLBACK TO ev");
            context.Execute("RELEASE ev");
            context.DiscardAfterCommit(mark);
            materializer.ReportPoison(deviceId, kind, e.DeviceSeq, ex);
            return RejectConversation(context, materializer, deviceId, e, SyncErrorCodes.Invalid, pre.DlpClean);
        }

        context.Execute("RELEASE ev");
        if (decision.Rejected is { } reason)
            return RejectConversation(context, materializer, deviceId, e, reason, pre.DlpClean, decision.Epoch, decision.Owner);
        return new BatchEventResult(e.DeviceSeq, seq);
    }

    private static BatchEventResult RejectConversation(SyncWriteContext context, ConversationMaterializer materializer, string deviceId, SyncEvent e,
        string reason, bool dlpClean, long? epoch = null, string? owner = null)
    {
        materializer.Rejected(context, deviceId, reason, e.Kind);
        return Reject(context, deviceId, e, reason, keepPayload: dlpClean, epoch, owner);
    }
}
