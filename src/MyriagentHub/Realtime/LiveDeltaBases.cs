using Myriagent.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 增量（<c>live2</c>）的基底（docs/plans/2026-10-23-live-delta-and-replica-live.md 3.4）：以（連線, 對話, ord）為鍵，記「這條連線最後送來、重組成功的那一版」的
/// 版本與長欄位。和疊加層分開：疊加層因持久版本作廢而丟掉一格、每對話權杖桶超速時，基底照樣前進（桌面認定的基底是「我最後送的那一版」，兩邊才一致）。
/// 上限：每條連線 <see cref="MaxPerConnection"/> 筆、全部 <see cref="MaxTotalBytes"/>（UTF-16 的兩倍計），超過丟最久沒用的（呼叫端對它送 <c>LiveResync{evicted}</c>）；
/// <see cref="IdleExpiry"/> 沒用到的、連線移除時清掉。任何執行緒都可以呼叫（同一條連線的呼叫由 SignalR 依序送來）
/// </summary>
internal sealed class LiveDeltaBases(TimeProvider time)
{
    public const int MaxPerConnection = 32;

    public const long MaxTotalBytes = 16L * 1024 * 1024;

    public static readonly TimeSpan IdleExpiry = TimeSpan.FromSeconds(60);

    internal readonly record struct Key(string ConnectionId, string ConversationId, long Ord);

    private sealed record Entry(long Ver, LiveLongFields Fields, long Bytes, long UsedMs);

    private readonly object gate = new();
    private readonly Dictionary<Key, Entry> entries = new();
    private long totalBytes;

    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    public long Bytes
    {
        get
        {
            lock (gate)
                return totalBytes;
        }
    }

    /// <summary>這一筆的基底（太久沒用的當成沒有、順便清掉）</summary>
    public bool TryGet(Key key, out long ver, out LiveLongFields fields)
    {
        var now = NowMs();
        lock (gate)
        {
            if (entries.TryGetValue(key, out var entry))
            {
                if (now - entry.UsedMs < (long)IdleExpiry.TotalMilliseconds)
                {
                    ver = entry.Ver;
                    fields = entry.Fields;
                    return true;
                }
                RemoveLocked(key, entry);
            }
        }
        ver = 0;
        fields = LiveLongFields.Empty;
        return false;
    }

    /// <summary>換成新的基底；回傳因上限被丟掉的（呼叫端對它們送 <c>LiveResync{evicted}</c>）</summary>
    public IReadOnlyList<Key> Set(Key key, long ver, LiveLongFields fields)
    {
        var bytes = 2L * ((fields.Text?.Length ?? 0) + (fields.Detail?.Length ?? 0) + (fields.Output?.Length ?? 0));
        var now = NowMs();
        List<Key>? evicted = null;
        lock (gate)
        {
            if (entries.TryGetValue(key, out var old))
                RemoveLocked(key, old);
            entries[key] = new Entry(ver, fields, bytes, now);
            totalBytes += bytes;
            // 過期的先清；再依上限丟最久沒用的（不丟剛放進去的這一筆）
            foreach (var (k, e) in entries.Where(p => !p.Key.Equals(key) && now - p.Value.UsedMs >= (long)IdleExpiry.TotalMilliseconds).ToList())
                RemoveLocked(k, e);
            while (entries.Count(p => p.Key.ConnectionId == key.ConnectionId) > MaxPerConnection || totalBytes > MaxTotalBytes)
            {
                var victim = entries.Where(p => !p.Key.Equals(key) && (totalBytes > MaxTotalBytes || p.Key.ConnectionId == key.ConnectionId))
                    .OrderBy(p => p.Value.UsedMs).Select(p => (KeyValuePair<Key, Entry>?)p).FirstOrDefault();
                if (victim is not { } v)
                    break;
                RemoveLocked(v.Key, v.Value);
                (evicted ??= []).Add(v.Key);
            }
        }
        return evicted ?? (IReadOnlyList<Key>)[];
    }

    public void Remove(Key key)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var entry))
                RemoveLocked(key, entry);
        }
    }

    /// <summary>連線移除：它的基底全部清掉</summary>
    public void RemoveConnection(string connectionId)
    {
        lock (gate)
        {
            foreach (var (k, e) in entries.Where(p => p.Key.ConnectionId == connectionId).ToList())
                RemoveLocked(k, e);
        }
    }

    private void RemoveLocked(Key key, Entry entry)
    {
        entries.Remove(key);
        totalBytes -= entry.Bytes;
    }

    private long NowMs() => time.GetTimestamp() * 1000 / time.TimestampFrequency;
}
