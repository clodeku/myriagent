using System.Globalization;
using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>對話在背景活動日誌裡的身分（每一筆都帶，換版前後以 id 對得上）</summary>
internal sealed record JournalConversation(string Id, string? Title, string Provider, string? AccountId);

/// <summary>
/// 背景活動日誌的純函式規則（docs/background-journal.md）：一筆的形狀、文字的截短與遮蔽、背景工作清單的差異、檔名與保留。
/// 日誌只給除錯看（換版被背景工作卡住、背景工作被強迫中止、無限跑的背景程式…），不影響任何行為
/// </summary>
internal static class BackgroundJournalRules
{
    /// <summary>標題、說明、摘要的上限（字）</summary>
    public const int MaxText = 300;

    /// <summary>命令列、提示詞的上限（字）</summary>
    public const int MaxCommand = 600;

    /// <summary>背景程式沒有被分類（沒撐過一輪、不是 CLI 的背景工作）時，跑了多久以上結束才記（短命的前景指令不記）</summary>
    public static readonly TimeSpan UnclassifiedProgramMinLife = TimeSpan.FromSeconds(30);

    /// <summary>換版排空期間每隔多久記一次還在等誰（restart.snapshot）</summary>
    public static readonly TimeSpan DrainSnapshotInterval = TimeSpan.FromMinutes(2);

    /// <summary>平常每隔多久記一次還在跑的背景工作與背景程式（bg.snapshot；只在有的時候記）</summary>
    public static readonly TimeSpan BackgroundSnapshotInterval = TimeSpan.FromMinutes(10);

    /// <summary>信封的欄位：data 裡同名的欄位改寫成「data.名稱」，不蓋掉信封（例如工具的 title 不能蓋掉對話的標題）</summary>
    public static readonly IReadOnlySet<string> EnvelopeFields = new HashSet<string>(StringComparer.Ordinal) { "t", "ev", "conv", "convTitle", "prov", "acct", "appPid", "ver" };

    /// <summary>
    /// 一筆：t（本機時間＋時差，毫秒）、ev、有對話時 conv、convTitle、prov、acct，接著 data 的欄位（值是 null 的不寫；和信封同名的改寫成「data.名稱」），最後 appPid、ver（app 的程序與版本：換版前後分得出來；不叫 pid，背景程式的 pid 是資料欄位）
    /// </summary>
    public static JObject Entry(string ev, DateTimeOffset at, int pid, string? version, JournalConversation? conversation, JObject? data)
    {
        ArgumentException.ThrowIfNullOrEmpty(ev);
        var entry = new JObject
        {
            ["t"] = at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["ev"] = ev,
        };
        if (conversation != null)
        {
            entry["conv"] = conversation.Id;
            if (Clip(conversation.Title, 80) is { } title)
                entry["convTitle"] = title;
            entry["prov"] = conversation.Provider;
            if (!string.IsNullOrEmpty(conversation.AccountId))
                entry["acct"] = conversation.AccountId;
        }
        if (data != null)
        {
            foreach (var property in data.Properties())
            {
                if (property.Value.Type != JTokenType.Null && !(property.Value is JValue { Value: null }))
                    entry[EnvelopeFields.Contains(property.Name) ? "data." + property.Name : property.Name] = property.Value.DeepClone();
            }
        }
        entry["appPid"] = pid;
        if (!string.IsNullOrEmpty(version))
            entry["ver"] = version;
        return entry;
    }

    /// <summary>
    /// 給日誌的文字：遮掉疑似憑證（<see cref="CredentialLeakRules.Mask"/>）、換行換成「⏎」、去掉前後空白，超過 <paramref name="max"/> 字截短加「…」；
    /// 空的回 null
    /// </summary>
    public static string? Clip(string? text, int max = MaxText)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var masked = CredentialLeakRules.Mask(text) ?? text;
        var flat = masked.Replace("\r\n", " ⏎ ", StringComparison.Ordinal).Replace('\n', '⏎').Replace('\r', '⏎').Trim();
        return flat.Length <= max ? flat : flat[..Math.Max(1, max - 1)] + "…";
    }

    /// <summary>秒數（小數一位）；null 是 null</summary>
    public static double? Seconds(TimeSpan? span) => span is { } s ? Math.Round(Math.Max(0, s.TotalSeconds), 1) : null;

    /// <summary>一個背景工作（AgentBackgroundTool）：工具、種類、標題、說明（命令列，截短）、Claude 的 task_id、停止用的 id</summary>
    public static JObject Tool(AgentBackgroundTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var o = new JObject { ["tool"] = tool.ToolName };
        Put(o, "kind", tool.ToolKind);
        Put(o, "title", Clip(tool.Title));
        Put(o, "detail", Clip(tool.Detail, MaxCommand));
        Put(o, "taskId", tool.TaskId);
        Put(o, "stopId", tool.StopId);
        return o;
    }

    /// <summary>背景工作的清單（給快照與結束時列出還在跑的）</summary>
    public static JArray Tools(IEnumerable<AgentBackgroundTool> tools) => new(tools.Select(Tool));

    /// <summary>對照用的鍵：有 task_id 或停止用的 id 就用它（標題、說明之後可能更新），否則工具＋標題＋說明</summary>
    public static string Key(AgentBackgroundTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.TaskId ?? tool.StopId ?? tool.ToolName + "\u001f" + tool.Title + "\u001f" + tool.Detail;
    }

    /// <summary>
    /// 背景工作清單的差異（同一個會話前後兩次回報）：<paramref name="after"/> 新出現的索引、<paramref name="before"/> 消失的索引。
    /// 以 <see cref="Key"/> 一對一配對（同鍵出現兩次就配兩次），順序照原本的清單
    /// </summary>
    public static (List<int> Started, List<int> Ended) Diff(IReadOnlyList<AgentBackgroundTool> before, IReadOnlyList<AgentBackgroundTool> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var remaining = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var i = 0; i < before.Count; i++)
        {
            var key = Key(before[i]);
            if (!remaining.TryGetValue(key, out var queue))
                remaining[key] = queue = new Queue<int>();
            queue.Enqueue(i);
        }
        var started = new List<int>();
        for (var i = 0; i < after.Count; i++)
        {
            if (remaining.TryGetValue(Key(after[i]), out var queue) && queue.Count > 0)
                queue.Dequeue();
            else
                started.Add(i);
        }
        var ended = remaining.Values.SelectMany(q => q).OrderBy(i => i).ToList();
        return (started, ended);
    }

    /// <summary>工具卡是子代理（Claude 的 Agent／Task、Codex 的子代理與 collabAgentToolCall、Antigravity 的 invoke_subagent 都是 agent 種類）</summary>
    public static bool IsSubagentTool(string? toolKind) => string.Equals(toolKind, "agent", StringComparison.Ordinal);

    /// <summary>
    /// 背景程式結束要不要記：分類過的（撐過一輪、CLI 的背景工作、閒置中開的）一律記；沒分類的（多半是前景指令中途的程序）跑了
    /// <see cref="UnclassifiedProgramMinLife"/> 以上才記
    /// </summary>
    public static bool ShouldLogProgramExit(string? programClass, TimeSpan? lifetime)
        => programClass != null || lifetime is { } life && life >= UnclassifiedProgramMinLife;

    /// <summary>某一天的日誌檔名</summary>
    public static string FileName(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>超過大小上限時改名成的檔名（取代上一份）</summary>
    public static string RolledFileName(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".1.jsonl";

    /// <summary>檔名的日期（yyyy-MM-dd 開頭）早於保留天數；不是日誌的檔名回 false（不刪）</summary>
    public static bool IsExpired(string fileName, DateTime today)
    {
        if (fileName is not { Length: >= 16 } || !fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || !DateTime.TryParseExact(fileName[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return false;
        return day < today.Date.AddDays(-Core.BackgroundJournalFile.RetentionDays);
    }

    private static void Put(JObject o, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            o[key] = value;
    }
}
