using System.Text;
using Myriagent.Core;
using Newtonsoft.Json;

namespace Myriagent.Schedules;

/// <summary>排程是誰建立或改過的（來源有證據的才記；排程頁的「誰建立的」、分類成「代理建立」）</summary>
internal sealed class ScheduleOriginEntry
{
    /// <summary>myriagent＝Myriagent 對話的工具指令；transcript＝外部 CLI 的逐字稿</summary>
    public string Source { get; set; } = "myriagent";

    /// <summary>created／changed：第一次看到它被代理建立或修改</summary>
    public string Verb { get; set; } = "created";

    public DateTime At { get; set; }

    public string? ConversationId { get; set; }

    public string? Title { get; set; }

    /// <summary>代理種類（AgentProvider 的名稱）</summary>
    public string? Provider { get; set; }

    public string? TranscriptPath { get; set; }

    /// <summary>外部工作階段的帳戶、工作階段 id、工作資料夾（S2：匯入成 Myriagent 對話、匯入之後改連過去）</summary>
    public string? AccountId { get; set; }

    public string? SessionId { get; set; }

    public string? Cwd { get; set; }
}

/// <summary>一筆變更紀錄（建立、修改、刪除、啟用、停用；看得出是哪個對話做的就連過去）</summary>
internal sealed class ScheduleChangeEntry
{
    public string Path { get; set; } = "";

    public DateTime At { get; set; }

    /// <summary>created、changed、deleted、enabled、disabled</summary>
    public string Kind { get; set; } = "";

    public string? ConversationId { get; set; }

    public string? Title { get; set; }

    public string? Provider { get; set; }
}

/// <summary>一次自動診斷（只為了限制次數）</summary>
internal sealed class ScheduleDiagnoseEntry
{
    public string Path { get; set; } = "";

    public DateTime At { get; set; }

    public string? ConversationId { get; set; }
}

/// <summary>
/// &lt;資料根目錄&gt;\schedules\state.json：來源、變更紀錄、定義的指紋（偵測變更，app 重開也接得上）、管理對話的連結、使用者的選擇、自動診斷。
/// 新檔（前一版不讀）；只加欄位
/// </summary>
internal sealed class ScheduleStateFile
{
    /// <summary>已經有第一次掃描的指紋（之前的掃描只建基準，不把每個排程都記成「建立」）</summary>
    public bool Baseline { get; set; }

    public Dictionary<string, ScheduleOriginEntry> Origins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ScheduleChangeEntry> Changes { get; set; } = new();

    /// <summary>定義的指紋（不含啟用狀態）</summary>
    public Dictionary<string, string> Fingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次看到的啟用狀態</summary>
    public Dictionary<string, bool> EnabledStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>排程 → 管理對話的 id</summary>
    public Dictionary<string, string> Links { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Ignored { get; set; } = new();

    public List<string> Adopted { get; set; } = new();

    /// <summary>個別關掉自動診斷的排程</summary>
    public List<string> NoAutoDiagnose { get; set; } = new();

    /// <summary>排程頁上次用的帳戶</summary>
    public string? AccountId { get; set; }

    public List<ScheduleDiagnoseEntry> Diagnoses { get; set; } = new();

    /// <summary>每個排程最後一次不能自動診斷的原因（排程頁明細顯示；成功診斷就清掉）</summary>
    public Dictionary<string, string> DiagnoseBlocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 排程的本機紀錄（&lt;資料根目錄&gt;\schedules\）：state.json＋runs.jsonl（Myriagent 觀察到的執行紀錄，只附加，載入時每個排程留最近 200 筆、
/// 檔案多出太多就重寫）。載入在背景執行緒（第一次掃描前），之後只在 UI 執行緒讀寫（檔案小，寫入是暫存檔換名或附加一行）
/// </summary>
internal sealed class ScheduleStore
{
    public const int ChangesPerTask = 50;
    public const int ChangesTotal = 1000;

    private readonly string directory;
    private readonly Dictionary<string, List<ScheduleRunRecord>> runs = new(StringComparer.OrdinalIgnoreCase);
    private int appendedSinceCompact;

    private ScheduleStore(string directory, ScheduleStateFile state)
    {
        this.directory = directory;
        State = state;
    }

    public ScheduleStateFile State { get; }

    private string StatePath => Path.Combine(directory, "state.json");

    private string RunsPath => Path.Combine(directory, "runs.jsonl");

    /// <summary>讀進來（背景執行緒）；讀不到的檔當成空的（JsonFile 會把壞掉的改名留著）</summary>
    public static ScheduleStore Load(string directory)
    {
        var state = JsonFile.Read<ScheduleStateFile>(Path.Combine(directory, "state.json")) ?? new ScheduleStateFile();
        // Newtonsoft 可能換掉字典：比對一律不分大小寫
        state.Origins = new Dictionary<string, ScheduleOriginEntry>(state.Origins ?? new(), StringComparer.OrdinalIgnoreCase);
        state.Fingerprints = new Dictionary<string, string>(state.Fingerprints ?? new(), StringComparer.OrdinalIgnoreCase);
        state.EnabledStates = new Dictionary<string, bool>(state.EnabledStates ?? new(), StringComparer.OrdinalIgnoreCase);
        state.Links = new Dictionary<string, string>(state.Links ?? new(), StringComparer.OrdinalIgnoreCase);
        state.DiagnoseBlocks = new Dictionary<string, string>(state.DiagnoseBlocks ?? new(), StringComparer.OrdinalIgnoreCase);
        state.Changes ??= new();
        state.Ignored ??= new();
        state.Adopted ??= new();
        state.NoAutoDiagnose ??= new();
        state.Diagnoses ??= new();
        var store = new ScheduleStore(directory, state);
        store.LoadRuns();
        return store;
    }

    private void LoadRuns()
    {
        if (!File.Exists(RunsPath))
            return;
        int lines = 0;
        try
        {
            using var stream = new FileStream(RunsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                lines++;
                if (line.Length == 0)
                    continue;
                try
                {
                    var record = JsonConvert.DeserializeObject<ScheduleRunRecord>(line, JsonFile.Settings);
                    if (record is { Path.Length: > 0 })
                        AddToMemory(record);
                }
                catch (JsonException)
                {
                    // 寫到一半的那一行（當機）略過
                }
            }
        }
        catch (IOException ex)
        {
            Log.Error("排程：讀執行紀錄失敗", ex);
            return;
        }
        if (lines > runs.Values.Sum(r => r.Count) + 2000)
            Compact();
    }

    private void AddToMemory(ScheduleRunRecord record)
    {
        if (!runs.TryGetValue(record.Path, out var list))
            runs[record.Path] = list = new List<ScheduleRunRecord>();
        list.Add(record);
        if (list.Count > ScheduleRunRules.KeepPerTask)
            list.RemoveRange(0, list.Count - ScheduleRunRules.KeepPerTask);
    }

    public IReadOnlyList<ScheduleRunRecord> RunsOf(string path) => runs.TryGetValue(path, out var list) ? list : Array.Empty<ScheduleRunRecord>();

    public ScheduleRunRecord? LastRunOf(string path) => runs.TryGetValue(path, out var list) && list.Count > 0 ? list[^1] : null;

    public void AppendRun(ScheduleRunRecord record)
    {
        AddToMemory(record);
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(RunsPath, JsonConvert.SerializeObject(record, Formatting.None, JsonFile.Settings) + "\n", new UTF8Encoding(false));
        }
        catch (IOException ex)
        {
            Log.Error("排程：寫執行紀錄失敗", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("排程：寫執行紀錄失敗", ex);
        }
        if (++appendedSinceCompact > 5000)
            Compact();
    }

    /// <summary>重寫成記憶體裡留著的（每個排程最近 200 筆）</summary>
    private void Compact()
    {
        appendedSinceCompact = 0;
        try
        {
            Directory.CreateDirectory(directory);
            var temp = RunsPath + ".tmp";
            using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
            {
                foreach (var record in runs.Values.SelectMany(r => r).OrderBy(r => r.ObservedAt))
                    writer.Write(JsonConvert.SerializeObject(record, Formatting.None, JsonFile.Settings) + "\n");
            }
            JsonFile.ReplaceWithRetry(temp, RunsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("排程：整理執行紀錄失敗", ex);
        }
    }

    /// <summary>加一筆變更紀錄（每個排程留 50 筆、全部留 1000 筆）</summary>
    public void AddChange(ScheduleChangeEntry change)
    {
        State.Changes.Add(change);
        var mine = State.Changes.Where(c => c.Path.Equals(change.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count > ChangesPerTask)
        {
            foreach (var old in mine.Take(mine.Count - ChangesPerTask))
                State.Changes.Remove(old);
        }
        if (State.Changes.Count > ChangesTotal)
            State.Changes.RemoveRange(0, State.Changes.Count - ChangesTotal);
    }

    public void Save()
    {
        try
        {
            JsonFile.Write(StatePath, State);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("排程：存 state.json 失敗", ex);
        }
    }
}
