namespace MyriagentHub.Sync;

/// <summary>
/// 已提交事件的通知來源：記下最高水位，並轉給訂閱者（裝置 hub 的 Notify 合併器、瀏覽器閘道、用量指派）。
/// 由 <see cref="Data.SyncWriter"/> 在提交之後於寫入執行緒呼叫——訂閱者只能記下來、不可阻塞。
/// </summary>
internal sealed class SyncNotifier : ISyncNotifier
{
    private long high;

    /// <summary>目前已提交的最大 seq（啟動時由 events 表初始化）</summary>
    public long High => Interlocked.Read(ref high);

    public event Action<string, long>? EventCommitted;

    public void Initialize(long currentHigh) => Interlocked.Exchange(ref high, currentHigh);

    public void Committed(string cls, long seq)
    {
        long current;
        while (seq > (current = Interlocked.Read(ref high)) && Interlocked.CompareExchange(ref high, seq, current) != current)
        {
        }
        EventCommitted?.Invoke(cls, seq);
    }
}
