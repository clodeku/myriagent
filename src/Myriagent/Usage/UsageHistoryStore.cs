using Myriagent.Core;

namespace Myriagent.Usage;

/// <summary>
/// 用量歷史（畫走勢線用）：每個帳戶一個檔 usage\{帳戶}.json，內容是「時間窗 key → [毫秒時間, 百分比]…」。
/// 使用率有變化、或距上一點超過 30 分鐘才記一點；保留 14 天；畫面取最近 7 天，每小時一點（取該小時最後一個值）最多 168 點。
/// 快照也一併存檔（下次開 app 先顯示上次的值）。只在 UI 執行緒讀寫。
/// </summary>
internal sealed class UsageHistoryStore
{
    private sealed class AccountHistory
    {
        public Dictionary<string, List<double[]>> Points { get; set; } = new();

        public UsageSnapshot? Last { get; set; }
    }

    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    public static readonly TimeSpan ViewSpan = TimeSpan.FromDays(7);
    public static readonly TimeSpan MinSampleInterval = TimeSpan.FromMinutes(30);
    public const int MaxViewPoints = 168;

    private readonly string directory;
    private readonly Dictionary<string, AccountHistory> cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirty = new(StringComparer.Ordinal);

    /// <summary>讀不到（被占用）的帳戶：這次執行不寫回，不能用空的歷史蓋掉原檔裡 14 天的走勢</summary>
    private readonly HashSet<string> readOnly = new(StringComparer.Ordinal);

    public UsageHistoryStore(string directory)
    {
        this.directory = directory;
    }

    private AccountHistory Get(string accountId)
    {
        if (!cache.TryGetValue(accountId, out var history))
        {
            var (status, saved, _) = JsonFile.TryRead<AccountHistory>(PathOf(accountId));
            history = saved ?? new AccountHistory();
            if (status == JsonReadStatus.Unreadable)
                readOnly.Add(accountId);
            cache[accountId] = history;
        }
        return history;
    }

    private string PathOf(string accountId) => Path.Combine(directory, $"{accountId}.json");

    public UsageSnapshot? GetLastSnapshot(string accountId) => Get(accountId).Last;

    /// <summary>有用量歷史檔的帳戶 Id（移除帳戶時檔案會一起刪掉；帳戶清單遺失時用來找回本機預設帳戶原本的 Id）</summary>
    public List<string> ListAccountIds()
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList()
                : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"列出用量歷史失敗：{ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>這個帳戶記錄過的時間窗 key（走勢點與最後快照）</summary>
    public HashSet<string> GetKeys(string accountId)
    {
        var history = Get(accountId);
        var keys = new HashSet<string>(history.Points.Keys, StringComparer.Ordinal);
        foreach (var window in history.Last?.Windows ?? Array.Empty<UsageWindow>())
            keys.Add(window.Key);
        return keys;
    }

    /// <summary>記錄一份快照：更新最後快照，並為有變化的時間窗加一點（點的時間＝快照的取得時間）</summary>
    public void Record(string accountId, UsageSnapshot snapshot) => Record(accountId, snapshot, snapshot.FetchedAt);

    /// <summary>
    /// 記錄一份快照，點的時間由呼叫端給（多電腦同步的共享用量用伺服器收到的時間，不用來源電腦的時鐘）。
    /// updateLast＝false 時只加走勢點、不換最後快照（追趕積壓期間本機有自己查到的值：共享的只進歷史）。
    /// 時間不是嚴格遞增的點照樣略過（AddPoint）
    /// </summary>
    public void Record(string accountId, UsageSnapshot snapshot, DateTimeOffset pointTime, bool updateLast = true)
    {
        var history = Get(accountId);
        if (updateLast)
            history.Last = snapshot;
        var t = pointTime.ToUnixTimeMilliseconds();
        foreach (var window in snapshot.Windows)
            AddPoint(history, window.Key, t, window.Percent);
        Trim(history, pointTime);
        dirty.Add(accountId);
    }

    internal static bool AddPoint(Dictionary<string, List<double[]>> points, string key, long timeMs, double percent)
    {
        if (!points.TryGetValue(key, out var list))
        {
            list = new List<double[]>();
            points[key] = list;
        }
        if (list.Count > 0)
        {
            var last = list[^1];
            if (timeMs <= last[0])
                return false;
            bool changed = Math.Abs(last[1] - percent) >= 0.5;
            bool stale = timeMs - last[0] >= MinSampleInterval.TotalMilliseconds;
            if (!changed && !stale)
                return false;
        }
        list.Add(new[] { timeMs, percent });
        return true;
    }

    private static void AddPoint(AccountHistory history, string key, long timeMs, double percent)
        => AddPoint(history.Points, key, timeMs, percent);

    private static void Trim(AccountHistory history, DateTimeOffset now)
    {
        var cutoff = (now - Retention).ToUnixTimeMilliseconds();
        foreach (var list in history.Points.Values)
            list.RemoveAll(p => p[0] < cutoff);
    }

    /// <summary>畫面用的走勢點：最近 7 天，每小時一點（該小時最後一個值）</summary>
    public List<double[]> GetView(string accountId, string key, DateTimeOffset now)
        => Downsample(Get(accountId).Points.TryGetValue(key, out var list) ? list : new List<double[]>(), now);

    internal static List<double[]> Downsample(IReadOnlyList<double[]> points, DateTimeOffset now)
    {
        var from = (now - ViewSpan).ToUnixTimeMilliseconds();
        const double hour = 3_600_000;
        var buckets = new SortedDictionary<long, double[]>();
        foreach (var p in points)
        {
            if (p[0] < from)
                continue;
            buckets[(long)(p[0] / hour)] = p;
        }
        var result = buckets.Values.ToList();
        return result.Count <= MaxViewPoints ? result : result.Skip(result.Count - MaxViewPoints).ToList();
    }

    public void Remove(string accountId)
    {
        cache.Remove(accountId);
        dirty.Remove(accountId);
        readOnly.Remove(accountId);
        try
        {
            File.Delete(PathOf(accountId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>把有變動的帳戶寫回磁碟（定期與關閉時呼叫）；啟動時讀不到的帳戶不寫</summary>
    public void Flush()
    {
        foreach (var accountId in dirty.ToList())
        {
            if (readOnly.Contains(accountId))
            {
                dirty.Remove(accountId);
                continue;
            }
            try
            {
                JsonFile.Write(PathOf(accountId), cache[accountId]);
                dirty.Remove(accountId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"用量歷史存檔失敗：{ex.Message}");
            }
        }
    }
}
