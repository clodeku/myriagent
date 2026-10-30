using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>回溯對話的分支點（ClaudeRewindRules.Find）</summary>
internal enum ClaudeRewindPointKind
{
    /// <summary>逐字稿裡找不到這則訊息（舊的訊息、不是在這個 app 送的、逐字稿讀不到）或剛好在壓縮之後：不能回溯對話</summary>
    NotFound,

    /// <summary>這則就是第一則：開新的 session</summary>
    First,

    /// <summary>從 AssistantUuid 之後分支（--resume-session-at 那則 --fork-session）</summary>
    At,
}

internal sealed record ClaudeRewindPoint(ClaudeRewindPointKind Kind, string? AssistantUuid = null);

/// <summary>
/// 回溯對話：在逐字稿找到這則使用者訊息（uuid 是送出時給的），沿 parentUuid 往前找最近的 assistant——以 --resume-session-at 它 --fork-session
/// 啟動就是「這則之前」的對話（實測 2.1.310：給使用者訊息本身會連那則一起留著、代理接著回答它；分支保留每則的 uuid、檔案還原點也跟過去）。
/// docs/protocol-notes.md「Claude：回溯、插入的訊息取消、背景工作的輸出、主代理與輸出風格、備援模型」
/// </summary>
internal static class ClaudeRewindRules
{
    public static ClaudeRewindPoint Find(IEnumerable<string> lines, string userUuid)
    {
        var entries = new Dictionary<string, (string? Type, string? Subtype, string? Parent)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] != '{')
                continue;
            JObject o;
            try
            {
                o = JObject.Parse(line);
            }
            catch (Exception)
            {
                continue;
            }
            if (o["uuid"] is not JValue { Type: JTokenType.String } u)
                continue;
            entries[u.ToString()] = (Str(o, "type"), Str(o, "subtype"), Str(o, "parentUuid"));
        }
        if (!entries.TryGetValue(userUuid, out var self) || self.Type != "user")
            return new ClaudeRewindPoint(ClaudeRewindPointKind.NotFound);
        var parent = self.Parent;
        // 防循環：最多走過全部的筆數
        for (int guard = 0; guard <= entries.Count; guard++)
        {
            if (parent == null)
                return new ClaudeRewindPoint(ClaudeRewindPointKind.First);
            if (!entries.TryGetValue(parent, out var e))
                return new ClaudeRewindPoint(ClaudeRewindPointKind.NotFound);
            if (e.Type == "assistant")
                return new ClaudeRewindPoint(ClaudeRewindPointKind.At, parent);
            // 壓縮的分界（parentUuid 是 null）：前面的內容已經換成摘要，分不出「這則之前」
            if (e.Type == "system" && e.Subtype == "compact_boundary")
                return new ClaudeRewindPoint(ClaudeRewindPointKind.NotFound);
            parent = e.Parent;
        }
        return new ClaudeRewindPoint(ClaudeRewindPointKind.NotFound);
    }

    /// <summary>讀整份逐字稿的行（不擋 CLI 寫入）；讀不到回空清單</summary>
    public static IReadOnlyList<string> ReadLines(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return [];
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>CLI 的錯誤 → 給人看的說明</summary>
    public static string DescribeFilesError(string? error)
    {
        var e = error?.Trim() ?? string.Empty;
        if (e.Contains("not enabled", StringComparison.OrdinalIgnoreCase))
            return "這個 Claude 程序沒有開檔案還原點（app 更新之前開的程序）：送一則訊息讓程序重開之後，新的訊息才有還原點";
        if (e.Contains("No file checkpoint", StringComparison.OrdinalIgnoreCase))
            return "這則訊息沒有檔案還原點（app 開始記還原點之前送的訊息）";
        return e.Length == 0 ? "檔案還原不了" : e;
    }

    private static string? Str(JObject o, string key) => o[key] is JValue { Type: JTokenType.String } v ? v.ToString() : null;
}
