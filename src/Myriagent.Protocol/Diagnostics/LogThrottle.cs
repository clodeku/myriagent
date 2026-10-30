namespace Myriagent.Diagnostics;

/// <summary>
/// 重複事件的記錄節流（桌面與伺服器共用，任何執行緒）：同一個鍵在 <c>window</c> 內只放行第一次，其餘只計數；過了窗口再放行時
/// 帶回這段期間略過的次數（記錄行以 <see cref="SuppressedNote"/> 寫出）。例如網頁每次重畫都再要一次同一張破圖，記錄只留一行加次數。
/// 鍵最多 <c>maxKeys</c> 個：滿了先清掉已過窗口的，還是滿就整個清空（寧可多記幾行，不讓記憶體長大）
/// </summary>
internal sealed class LogThrottle
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeSpan window;
    private readonly TimeProvider time;
    private readonly int maxKeys;

    private sealed class Entry(long since)
    {
        public long Since = since;

        public int Suppressed;
    }

    public LogThrottle(TimeSpan window, TimeProvider time, int maxKeys = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeys, 1);
        this.window = window;
        this.time = time;
        this.maxKeys = maxKeys;
    }

    /// <summary>目前記著的鍵數（測試用）</summary>
    internal int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>這次要不要記：要記時 <paramref name="suppressed"/> 是上一次記錄之後被略過的次數，不記時是 0</summary>
    public bool ShouldLog(string key, out int suppressed)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = time.GetTimestamp();
        lock (gate)
        {
            if (entries.TryGetValue(key, out var entry))
            {
                if (time.GetElapsedTime(entry.Since, now) < window)
                {
                    entry.Suppressed++;
                    suppressed = 0;
                    return false;
                }

                suppressed = entry.Suppressed;
                entry.Since = now;
                entry.Suppressed = 0;
                return true;
            }

            if (entries.Count >= maxKeys)
                Prune(now);
            entries[key] = new Entry(now);
            suppressed = 0;
            return true;
        }
    }

    /// <summary>附在記錄行後面的略過次數；沒有略過是空字串</summary>
    public static string SuppressedNote(int suppressed) => suppressed > 0 ? $"（上次記錄之後另有 {suppressed} 次相同）" : string.Empty;

    private void Prune(long now)
    {
        foreach (var (key, entry) in entries.ToList())
        {
            if (time.GetElapsedTime(entry.Since, now) >= window)
                entries.Remove(key);
        }

        if (entries.Count >= maxKeys)
            entries.Clear();
    }
}
