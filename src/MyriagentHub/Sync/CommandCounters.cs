namespace MyriagentHub.Sync;

/// <summary>
/// 遠端指令的營運計數（WP4.5 計畫 D30）：程序啟動以來的累計，只有計數、沒有告警；只用 <see cref="Interlocked"/>。
/// <see cref="Snapshot"/> 就是 <c>ops/status</c> 的 <c>commands</c>（<c>OpsStatusService</c>）
/// </summary>
internal sealed class CommandCounters
{
    private long issued;
    private long rejectedAtServer;
    private long delivered;
    private long done;
    private long rejected;
    private long unknown;
    private long expired;
    private long pendingDurable;
    private long ignoredResults;
    private long droppedOutputs;
    private long resigned;
    private long aborted;

    /// <summary>簽發、寫列的指令（每列一次；fan-out 是每台）</summary>
    public void Issued() => Interlocked.Increment(ref issued);

    /// <summary>伺服器立即拒絕（不簽、不寫列，D3）</summary>
    public void RejectedAtServer() => Interlocked.Increment(ref rejectedAtServer);

    /// <summary>送出 <c>CommandDelivery</c>（含重簽後再送）</summary>
    public void Delivered() => Interlocked.Increment(ref delivered);

    public void Done() => Interlocked.Increment(ref done);

    public void Rejected() => Interlocked.Increment(ref rejected);

    public void Unknown() => Interlocked.Increment(ref unknown);

    public void Expired() => Interlocked.Increment(ref expired);

    /// <summary>最近一次重掃時 pending 的 durable 列數（不是累計）</summary>
    public void SetPendingDurable(long count) => Interlocked.Exchange(ref pendingDurable, count);

    public void IgnoredResult() => Interlocked.Increment(ref ignoredResults);

    public void DroppedOutput() => Interlocked.Increment(ref droppedOutputs);

    public void Resigned() => Interlocked.Increment(ref resigned);

    /// <summary>中斷了身分公鑰不是目前的、協商出 cmd1 的裝置連線（D7、D14）</summary>
    public void Aborted() => Interlocked.Increment(ref aborted);

    public CommandCountersSnapshot Snapshot() => new(
        Interlocked.Read(ref issued), Interlocked.Read(ref rejectedAtServer), Interlocked.Read(ref delivered), Interlocked.Read(ref done),
        Interlocked.Read(ref rejected), Interlocked.Read(ref unknown), Interlocked.Read(ref expired), Interlocked.Read(ref pendingDurable),
        Interlocked.Read(ref ignoredResults), Interlocked.Read(ref droppedOutputs), Interlocked.Read(ref resigned), Interlocked.Read(ref aborted));
}

/// <summary><see cref="CommandCounters"/> 某一刻的值（屬性名稱就是 <c>ops/status</c> 的鍵）</summary>
internal sealed record CommandCountersSnapshot(
    long Issued, long RejectedAtServer, long Delivered, long Done, long Rejected, long Unknown, long Expired, long PendingDurable,
    long IgnoredResults, long DroppedOutputs, long Resigned, long Aborted);
