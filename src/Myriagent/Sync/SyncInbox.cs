namespace Myriagent.Sync;

/// <summary>
/// UI 執行緒→引擎的收件匣（執行緒安全）：快照類（帳戶清單、CLI 狀態、app 更新狀態、每個帳戶的用量）以槽位合併、只留最新，永不溢出；
/// 修改類（改名、重排、偏好、裝置改名、積壓、全部重送）放 FIFO，不丟、照順序。
/// <see cref="Post"/> 回傳「收件匣原本是空的」：呼叫端只在這時喚醒引擎（多個訊號合成一次喚醒）。
/// </summary>
internal sealed class SyncInbox
{
    private readonly object gate = new();
    private readonly Queue<SyncSignal> edits = new();
    private readonly Dictionary<string, SyncSignal.UsageObserved> usage = new(StringComparer.Ordinal);
    private readonly List<string> usageOrder = new();
    private SyncSignal.Accounts? accounts;
    private SyncSignal.Cli? cli;
    private SyncSignal.App? app;

    public int Count
    {
        get
        {
            lock (gate)
                return CountLocked();
        }
    }

    /// <summary>放進收件匣；回傳放之前是不是空的（是的話呼叫端要喚醒引擎）</summary>
    public bool Post(SyncSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        lock (gate)
        {
            var wasEmpty = CountLocked() == 0;
            switch (signal)
            {
                case SyncSignal.Accounts a:
                    accounts = a;
                    break;
                case SyncSignal.Cli c:
                    cli = c;
                    break;
                case SyncSignal.App a:
                    app = a;
                    break;
                case SyncSignal.UsageObserved u:
                    if (!usage.ContainsKey(u.LocalAccountId))
                        usageOrder.Add(u.LocalAccountId);
                    usage[u.LocalAccountId] = u;
                    break;
                default:
                    edits.Enqueue(signal);
                    break;
            }

            return wasEmpty;
        }
    }

    /// <summary>一次取完：快照類（帳戶、CLI、app、各帳戶的用量）在前，修改類照到達順序在後</summary>
    public IReadOnlyList<SyncSignal> TakeAll()
    {
        lock (gate)
        {
            var all = new List<SyncSignal>(CountLocked());
            if (accounts is not null)
                all.Add(accounts);
            if (cli is not null)
                all.Add(cli);
            if (app is not null)
                all.Add(app);
            foreach (var id in usageOrder)
                all.Add(usage[id]);
            all.AddRange(edits);
            accounts = null;
            cli = null;
            app = null;
            usage.Clear();
            usageOrder.Clear();
            edits.Clear();
            return all;
        }
    }

    private int CountLocked() => (accounts is null ? 0 : 1) + (cli is null ? 0 : 1) + (app is null ? 0 : 1) + usage.Count + edits.Count;
}
