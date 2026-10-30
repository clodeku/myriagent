using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;

namespace MyriagentHub.Sync;

/// <summary>持久事件提交之後的通知（裝置 hub 以 ≤每 200 ms 合併一次的 Notify 送給在線的裝置）；在寫入執行緒上呼叫，不可阻塞</summary>
internal interface ISyncNotifier
{
    void Committed(string cls, long seq);
}

/// <summary>裝置目前是否在線（裝置 hub 的連線登記；device.upsert 的 online 欄位、用量指派的候選）</summary>
internal interface IDevicePresence
{
    bool IsOnline(string deviceId);

    /// <summary>這台目前在線的連線協商出的功能（device.upsert 的 <c>features</c>，sync-protocol 14.1）；不在線或沒有協商出任何功能是空的</summary>
    IReadOnlyList<string> FeaturesOf(string deviceId);
}

/// <summary>events 表的寫入（都在 <see cref="SyncWriter"/> 的交易裡；提交之後才通知）</summary>
internal static class SyncEventStore
{
    public const string ServerAuthor = SyncAuthors.Server;

    /// <summary>伺服器寫的事件：payload 以 <see cref="SyncJson"/> 序列化、依大小決定編碼；回傳配到的 seq</summary>
    public static long InsertServer(SyncWriteContext context, ISyncNotifier notifier, string kind, object payload)
    {
        var (cls, stream) = SyncEventKinds.Placement(kind) ?? throw new ArgumentException("不認得的事件種類：" + kind, nameof(kind));
        var (enc, encoded) = PayloadRules.Encode(SyncJson.Serialize(payload));
        context.Execute(
            "INSERT INTO events(stream, cls, author, kind, payload, enc, created_at) VALUES($stream, $cls, $author, $kind, $payload, $enc, $now)",
            ("$stream", stream), ("$cls", cls), ("$author", ServerAuthor), ("$kind", kind), ("$payload", encoded), ("$enc", enc), ("$now", context.Now));
        var seq = LastSeq(context);
        context.AfterCommit(() => notifier.Committed(cls, seq));
        return seq;
    }

    /// <summary>裝置寫的事件（payload 與 enc 存原樣的字串，拉取時發出去的是同一串）</summary>
    public static long InsertDevice(SyncWriteContext context, ISyncNotifier notifier, string deviceId, SyncEvent e, string cls)
    {
        context.Execute(
            "INSERT INTO events(stream, cls, conversation_id, author, device_seq, payload_sha256, epoch, kind, payload, enc, created_at) "
            + "VALUES($stream, $cls, $conv, $author, $dseq, $sha, $epoch, $kind, $payload, $enc, $now)",
            ("$stream", e.Stream), ("$cls", cls), ("$conv", e.ConversationId), ("$author", deviceId), ("$dseq", e.DeviceSeq),
            ("$sha", Convert.FromHexString(e.PayloadSha256)), ("$epoch", e.Epoch), ("$kind", e.Kind), ("$payload", e.Payload), ("$enc", e.Enc), ("$now", context.Now));
        var seq = LastSeq(context);
        context.AfterCommit(() => notifier.Committed(cls, seq));
        return seq;
    }

    private static long LastSeq(SyncWriteContext context) => (long)context.Scalar("SELECT last_insert_rowid()")!;
}

/// <summary>同步寫入交易裡的稽核：提交之後才寫，丟到執行緒池（稽核要等 hub.db 的寫入鎖，不能佔住 sync.db 的寫入執行緒）</summary>
internal static class SyncAudit
{
    public static void AuditAfterCommit(this SyncWriteContext context, AuditLog audit, AuditEntry entry) =>
        context.AfterCommit(() => _ = Task.Run(() => audit.WriteAsync(entry)));
}
