using System.Text;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Myriagent.Usage;

/// <summary>
/// 每週額度每 5% 的 token 分段紀錄（規則在 UsageSegmentRules）：資料夾 usage-segments\ 底下
/// <list type="bullet">
/// <item>records.jsonl：結好的每一筆，一行一個 JSON（camelCase；只附加、不改寫，給外部分析工具直接讀）。</item>
/// <item>open.json：各帳戶 × 每週額度窗正在記的那一段（app 重開、換版後接著記），由維護計時器與關閉時寫回。</item>
/// </list>
/// 只在 UI 執行緒讀寫（中樞的 token 增量與 AccountManager 的用量快照都在 UI 執行緒）。open.json 啟動時讀不到（被占用）就不寫回，免得蓋掉原檔；
/// records.jsonl 只附加，讀不到時照樣附加
/// </summary>
internal sealed class UsageSegmentStore
{
    private sealed class OpenFile
    {
        public Dictionary<string, UsageSegmentOpen> Segments { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>records.jsonl 的格式：camelCase、一行、略過 null；模型名稱（字典的鍵）照原樣</summary>
    internal static readonly JsonSerializerSettings RecordSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        DateParseHandling = DateParseHandling.DateTimeOffset,
    };

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string directory;
    private OpenFile? open;
    private bool openReadOnly;
    private bool dirty;
    private List<UsageSegmentRecord>? records;

    public UsageSegmentStore(string directory)
    {
        this.directory = directory;
    }

    public string RecordsPath => Path.Combine(directory, "records.jsonl");

    private string OpenPath => Path.Combine(directory, "open.json");

    private Dictionary<string, UsageSegmentOpen> Segments
    {
        get
        {
            if (open == null)
            {
                var (status, saved, _) = JsonFile.TryRead<OpenFile>(OpenPath);
                open = saved ?? new OpenFile();
                open.Segments ??= new Dictionary<string, UsageSegmentOpen>(StringComparer.Ordinal);
                openReadOnly = status == JsonReadStatus.Unreadable;
            }
            return open.Segments;
        }
    }

    /// <summary>
    /// 看到這個帳戶的一份用量快照：每個每週額度窗各走一次 UsageSegmentRules.Observe，結好的寫進 records.jsonl 並回傳（沒有就是空的）。
    /// 快照裡沒有的額度窗（這次沒回報）不動
    /// </summary>
    public List<UsageSegmentRecord> Observe(AgentAccount account, UsageSnapshot snapshot, DateTimeOffset now, Func<string>? newId = null)
    {
        var closed = new List<UsageSegmentRecord>();
        if (snapshot.Error != null)
            return closed;
        var segments = Segments;
        foreach (var window in snapshot.Windows)
        {
            if (!UsageSegmentRules.IsWeekly(window))
                continue;
            var key = UsageSegmentRules.KeyOf(account.Id, window.Key);
            segments.TryGetValue(key, out var current);
            var (next, record) = UsageSegmentRules.Observe(current, account.Id, window, now, (_, _, _) => new UsageSegmentRecord
            {
                Id = newId?.Invoke() ?? Guid.NewGuid().ToString("N"),
                AccountId = account.Id,
                AccountLabel = account.Label,
                Provider = account.Provider.ToString(),
                Email = account.Email,
            });
            // 沒有跨過邊界也有變（LastPercent、LastSeenAt）
            dirty = true;
            segments[key] = next;
            if (record != null)
            {
                Append(record);
                closed.Add(record);
            }
        }
        return closed;
    }

    /// <summary>一筆增量加到這個帳戶每一個正在記的每週額度窗（還沒看過使用率的帳戶沒有段落，不記）</summary>
    public void AddTokens(string accountId, string? model, AgentUsage delta, long cacheWrite)
    {
        if (string.IsNullOrEmpty(accountId) || TokenTallyRules.IsZero(delta) && cacheWrite <= 0)
            return;
        foreach (var segment in Segments.Values)
        {
            if (!string.Equals(segment.AccountId, accountId, StringComparison.Ordinal))
                continue;
            UsageSegmentRules.AddTokens(segment, model, delta, cacheWrite);
            dirty = true;
        }
    }

    /// <summary>正在記的各段（畫面用；複本）</summary>
    public List<UsageSegmentOpen> OpenSegments() => Segments.Values.ToList();

    /// <summary>結好的全部紀錄（第一次讀 records.jsonl；壞掉的行略過）</summary>
    public IReadOnlyList<UsageSegmentRecord> Records()
    {
        if (records != null)
            return records;
        records = new List<UsageSegmentRecord>();
        try
        {
            if (File.Exists(RecordsPath))
            {
                using var stream = new FileStream(RecordsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8);
                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    try
                    {
                        if (JsonConvert.DeserializeObject<UsageSegmentRecord>(line, RecordSettings) is { } record)
                            records.Add(record);
                    }
                    catch (JsonException)
                    {
                        // 寫到一半的行（當機）：略過
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀不到用量分段紀錄：{ex.Message}");
        }
        return records;
    }

    private void Append(UsageSegmentRecord record)
    {
        // 先讀進記憶體（之後的 Records() 才不會漏掉、也不會重複讀到這一筆）
        Records();
        records!.Add(record);
        try
        {
            Directory.CreateDirectory(directory);
            using var stream = new FileStream(RecordsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(record, RecordSettings) + "\n");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"用量分段紀錄寫入失敗：{ex.Message}");
        }
    }

    /// <summary>移除帳戶：它正在記的段落不再記（已結好的紀錄留著）</summary>
    public void RemoveAccount(string accountId)
    {
        foreach (var key in Segments.Where(kv => kv.Value.AccountId == accountId).Select(kv => kv.Key).ToList())
        {
            Segments.Remove(key);
            dirty = true;
        }
    }

    /// <summary>把 open.json 寫回（維護計時器與關閉時）；啟動時讀不到的不寫</summary>
    public void Flush()
    {
        if (!dirty || open == null)
            return;
        if (openReadOnly)
        {
            dirty = false;
            return;
        }
        try
        {
            JsonFile.Write(OpenPath, open);
            dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"用量分段存檔失敗：{ex.Message}");
        }
    }
}
