namespace Myriagent.Sync;

/// <summary>
/// 複本縮圖的記憶體快取（2026-10-23）：以前其他電腦的對話每重畫、往回捲、切換一次分頁，每張縮圖都經伺服器（E9）向擁有者的電腦重抓一次。
/// 只快取縮圖（≤512 KB，PNG／JPEG），原檔不快取；以請求路徑（鍵＋大小）為鍵，<see cref="Ttl"/>（5 分鐘，同瀏覽器版的 <c>private, max-age=300</c>）後過期，
/// 合計超過 <see cref="MaxBytes"/> 時丟掉最久沒用的。任何執行緒都可以呼叫
/// </summary>
internal sealed class ReplicaThumbCache(TimeProvider time, long maxBytes = ReplicaThumbCache.MaxBytes)
{
    /// <summary>合計最多放多少位元組</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>放多久就過期（擁有者換了縮圖，最多晚這麼久才看到）</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private sealed record Entry(string Key, byte[] Body, string ContentType, long ExpiresAt);

    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> order = new();   // 前面＝最近用過
    private long bytes;

    public long Bytes
    {
        get
        {
            lock (gate)
                return bytes;
        }
    }

    public bool TryGet(string key, out byte[] body, out string contentType)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                if (node.Value.ExpiresAt > time.GetUtcNow().ToUnixTimeMilliseconds())
                {
                    order.Remove(node);
                    order.AddFirst(node);
                    body = node.Value.Body;
                    contentType = node.Value.ContentType;
                    return true;
                }
                RemoveNode(node);
            }
        }
        body = [];
        contentType = string.Empty;
        return false;
    }

    public void Put(string key, byte[] body, string contentType)
    {
        if (body.LongLength > maxBytes)
            return;
        lock (gate)
        {
            if (map.TryGetValue(key, out var old))
                RemoveNode(old);
            var node = order.AddFirst(new Entry(key, body, contentType, (time.GetUtcNow() + Ttl).ToUnixTimeMilliseconds()));
            map[key] = node;
            bytes += body.LongLength;
            while (bytes > maxBytes && order.Last is { } last)
                RemoveNode(last);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            map.Clear();
            order.Clear();
            bytes = 0;
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        order.Remove(node);
        map.Remove(node.Value.Key);
        bytes -= node.Value.Body.LongLength;
    }
}
