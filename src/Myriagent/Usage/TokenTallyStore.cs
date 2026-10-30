using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Sync;

namespace Myriagent.Usage;

/// <summary>
/// 各帳戶每天用掉的 token（側欄帳戶卡的計數；規則在 TokenTallyRules）：一個檔 usage-tokens.json，內容是「本機日期 → 帳戶 Id → 輸入、快取、輸出」，
/// 保留 30 天。只記這台電腦上 app 經手的對話（直接在終端機用 CLI 的看不到）。中樞在 UI 執行緒記與讀，同步引擎在自己的執行緒取複本
/// （<see cref="Snapshot"/>，tok1），所以每個公開方法都取同一把鎖；有變動時由維護計時器與關閉時寫回
/// </summary>
internal sealed class TokenTallyStore
{
    internal sealed class TokenDay
    {
        public long Input { get; set; }

        public long Cached { get; set; }

        public long Output { get; set; }
    }

    private sealed class TallyFile
    {
        public Dictionary<string, Dictionary<string, TokenDay>> Days { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly string path;
    private readonly object gate = new();
    private TallyFile? data;
    private bool dirty;

    /// <summary>啟動時讀不到（被占用）：這次執行只在記憶體累加、不寫回，不能用新的資料蓋掉原檔</summary>
    private bool readOnly;

    public TokenTallyStore(string path)
    {
        this.path = path;
    }

    private TallyFile Data
    {
        get
        {
            if (data == null)
            {
                var (status, saved, _) = JsonFile.TryRead<TallyFile>(path);
                data = saved ?? new TallyFile();
                data.Days ??= new Dictionary<string, Dictionary<string, TokenDay>>(StringComparer.Ordinal);
                readOnly = status == JsonReadStatus.Unreadable;
            }
            return data;
        }
    }

    /// <summary>記一筆增量（記在 <paramref name="now"/> 的本機日期）；0 不記</summary>
    public void Add(string accountId, DateTime now, AgentUsage delta)
    {
        if (string.IsNullOrEmpty(accountId) || TokenTallyRules.IsZero(delta))
            return;
        lock (gate)
            AddLocked(accountId, now, delta);
    }

    private void AddLocked(string accountId, DateTime now, AgentUsage delta)
    {
        var key = TokenTallyRules.DayKey(now);
        if (!Data.Days.TryGetValue(key, out var accounts) || accounts == null)
        {
            accounts = new Dictionary<string, TokenDay>(StringComparer.Ordinal);
            Data.Days[key] = accounts;
            foreach (var expired in TokenTallyRules.ExpiredDays(Data.Days.Keys, now))
                Data.Days.Remove(expired);
        }
        if (!accounts.TryGetValue(accountId, out var day) || day == null)
        {
            day = new TokenDay();
            accounts[accountId] = day;
        }
        day.Input += Math.Max(0, delta.InputTokens);
        day.Cached += Math.Max(0, delta.CachedInputTokens);
        day.Output += Math.Max(0, delta.OutputTokens);
        dirty = true;
    }

    /// <summary>某帳戶從 <paramref name="now"/> 往回 <paramref name="days"/> 天（含今天）的合計</summary>
    public AgentUsage Sum(string accountId, DateTime now, int days)
    {
        long input = 0, cached = 0, output = 0;
        lock (gate)
        {
            foreach (var key in TokenTallyRules.DayKeys(now, days))
            {
                if (Data.Days.TryGetValue(key, out var accounts) && accounts != null && accounts.TryGetValue(accountId, out var day) && day != null)
                {
                    input += day.Input;
                    cached += day.Cached;
                    output += day.Output;
                }
            }
        }
        return new AgentUsage(input, output, cached, null);
    }

    /// <summary>全部的格（本機帳戶 × 本機日期）的複本：同步引擎上傳用（tok1，任何執行緒）</summary>
    public List<LocalTokenTally> Snapshot()
    {
        var result = new List<LocalTokenTally>();
        lock (gate)
        {
            foreach (var (key, accounts) in Data.Days)
            {
                if (accounts == null)
                    continue;
                foreach (var (accountId, day) in accounts)
                {
                    if (day != null)
                        result.Add(new LocalTokenTally(accountId, key, day.Input, day.Cached, day.Output));
                }
            }
        }
        return result;
    }

    /// <summary>有變動就寫回磁碟（維護計時器與關閉時呼叫）；啟動時讀不到的不寫</summary>
    public void Flush()
    {
        lock (gate)
            FlushLocked();
    }

    private void FlushLocked()
    {
        if (!dirty || data == null)
            return;
        if (readOnly)
        {
            dirty = false;
            return;
        }
        try
        {
            JsonFile.Write(path, data);
            dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"token 計數存檔失敗：{ex.Message}");
        }
    }
}
