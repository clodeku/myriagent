using Microsoft.Data.Sqlite;

namespace MyriagentHub.Sync;

/// <summary>
/// 瀏覽器 AccountView 的用量走勢（每小時一點、最近 7 天、最多 168 點；同桌面 UsageHistoryStore.Downsample）的增量快取：
/// usage_points 保留 14 天、每次上傳都可能進點，閘道每 500 ms 讀一次快照時全讀太浪費（Hub 主機可能同時跑著其他重要服務）。
/// 每個 (邏輯帳戶, 時間窗) 記下看過的最大 at，之後以主鍵範圍只讀 at ≥ max(那個 at, now − 7 天) 的點——同一個 at 會被後到的
/// INSERT OR REPLACE 取代，所以重讀 watermark 那一點；以 <see cref="AccountViewRules.MergeHourly"/> 合併。
/// 平常只有閘道迴圈的執行緒會用；仍以鎖保護（測試會直接呼叫建構快照的方法）。
/// </summary>
internal sealed class UsageHistoryCache
{
    private readonly object gate = new();
    private readonly Dictionary<(string LogicalAccountId, string WindowKey), Entry> entries = new();
    private long lastNowMs;

    /// <summary>目前快取了幾個 (帳戶, 時間窗)（測試用）</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>
    /// 在呼叫端的唯讀交易裡更新：<paramref name="windows"/> 是每個帳戶目前顯示的時間窗（usage_latest 的 windows）；
    /// 不在裡面的 (帳戶, 時間窗) 從快取移除。回傳這次讀到的點數
    /// </summary>
    public int Refresh(SqliteConnection read, SqliteTransaction transaction, IReadOnlyDictionary<string, IReadOnlyList<string>> windows, long nowMs)
    {
        lock (gate)
        {
            var from = nowMs - AccountViewRules.HistorySpanMs;
            var wanted = new HashSet<(string, string)>();
            var total = 0;
            using var cmd = read.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT at, percent FROM usage_points WHERE logical_account_id = $la AND window_key = $w AND at >= $from ORDER BY at";
            var la = cmd.Parameters.Add("$la", SqliteType.Text);
            var window = cmd.Parameters.Add("$w", SqliteType.Text);
            var start = cmd.Parameters.Add("$from", SqliteType.Integer);
            foreach (var (accountId, keys) in windows)
            {
                foreach (var key in keys)
                {
                    var id = (accountId, key);
                    if (!wanted.Add(id))
                        continue;
                    if (!entries.TryGetValue(id, out var entry))
                        entries[id] = entry = new Entry();
                    la.Value = accountId;
                    window.Value = key;
                    start.Value = Math.Max(from, entry.Watermark);
                    var points = new List<UsagePoint>();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            points.Add(new UsagePoint(reader.GetInt64(0), reader.GetDouble(1)));
                    }
                    if (points.Count > 0)
                        entry.Watermark = Math.Max(entry.Watermark, points[^1].At);
                    total += points.Count;
                    AccountViewRules.MergeHourly(entry.Buckets, points, nowMs);
                }
            }
            foreach (var stale in entries.Keys.Where(k => !wanted.Contains(k)).ToList())
                entries.Remove(stale);
            lastNowMs = nowMs;
            return total;
        }
    }

    /// <summary>這個帳戶各時間窗的走勢點（以時間窗 key 為鍵；最近一次 <see cref="Refresh"/> 的時間為準）</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<UsagePoint>> Get(string logicalAccountId)
    {
        lock (gate)
        {
            var result = new Dictionary<string, IReadOnlyList<UsagePoint>>(StringComparer.Ordinal);
            foreach (var ((accountId, key), entry) in entries)
            {
                if (accountId == logicalAccountId)
                    result[key] = AccountViewRules.FromBuckets(entry.Buckets, lastNowMs);
            }
            return result;
        }
    }

    private sealed class Entry
    {
        public SortedDictionary<long, UsagePoint> Buckets { get; } = new();

        public long Watermark { get; set; } = long.MinValue;
    }
}
