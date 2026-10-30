using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Schedules;

/// <summary>對話紀錄裡一筆碰到排程的指令（S2：以前在終端機、IDE 或 Myriagent 裡讓代理建立的排程找得到出處）</summary>
internal sealed class TranscriptHit
{
    /// <summary>代理種類（AgentProvider 的名稱）</summary>
    public string Provider { get; set; } = "";

    public string? AccountId { get; set; }

    /// <summary>CLI 的工作階段 id（Claude 的檔名、Codex 的 thread id…）：對得上 Myriagent 對話的 SessionId 時連到那個對話</summary>
    public string SessionId { get; set; } = "";

    public string FilePath { get; set; } = "";

    public DateTime At { get; set; }

    /// <summary>created、changed、deleted（ScheduleVerb 的 Create、Change、Delete；其他不記）</summary>
    public string Verb { get; set; } = "";

    /// <summary>工作名稱（可帶資料夾）</summary>
    public string Name { get; set; } = "";

    /// <summary>那一行記的工作資料夾（Claude 的 cwd）</summary>
    public string? Cwd { get; set; }
}

/// <summary>
/// 從對話紀錄的一行（JSONL）找出代理執行過、碰到排程的指令：只看工具呼叫的指令參數（名稱是 command、commandLine、cmd、script、arguments、input 的字串，
/// arguments 是 JSON 字串時再往裡面找），不看工具的輸出與訊息文字——輸出常是文件或 schtasks /query 的列表，會把範例當成真的建立。
/// 純函式；先用 <see cref="ScheduleOriginRules.MayTouch"/> 篩掉大部分的行
/// </summary>
internal static class TranscriptOriginRules
{
    private static readonly HashSet<string> CommandKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "commandLine", "cmd", "script", "arguments", "input", "CommandLine",
    };

    /// <summary>不往裡面找的鍵（工具的輸出、訊息文字）</summary>
    private static readonly HashSet<string> SkipKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "output", "stdout", "stderr", "result", "content_text", "aggregated_output", "formatted_output", "toolUseResult", "tool_use_result",
    };

    /// <summary>一行裡碰到排程的指令（建立、修改、刪除，看得出名稱的）；不是 JSON、沒有的回空</summary>
    public static IReadOnlyList<(ScheduleVerb Verb, string Name)> HitsInLine(string line)
    {
        var result = new List<(ScheduleVerb, string)>();
        if (string.IsNullOrEmpty(line) || !ScheduleOriginRules.MayTouch(line))
            return result;
        JToken root;
        try
        {
            root = JToken.Parse(line);
        }
        catch (JsonException)
        {
            return result;
        }
        // tool_result 類的整行（Claude 的 user 行帶工具結果）不看
        if (root is JObject o && IsToolResultLine(o))
            return result;
        foreach (var text in CommandStrings(root, 0))
        {
            foreach (var hit in ScheduleOriginRules.Parse(text))
            {
                if (hit.Name == null || hit.Verb is not (ScheduleVerb.Create or ScheduleVerb.Change or ScheduleVerb.Delete))
                    continue;
                if (!result.Contains((hit.Verb, hit.Name)))
                    result.Add((hit.Verb, hit.Name));
            }
        }
        return result;
    }

    private static bool IsToolResultLine(JObject o)
    {
        if (o["toolUseResult"] != null)
            return true;
        if (o["message"] is JObject m && m["content"] is JArray content && content.Count > 0
            && content.All(c => c is JObject x && x.Value<string>("type") == "tool_result"))
            return true;
        return o["payload"] is JObject p && p.Value<string>("type") is "function_call_output" or "custom_tool_call_output";
    }

    /// <summary>指令參數的字串（遞迴，最多 12 層）</summary>
    internal static IEnumerable<string> CommandStrings(JToken token, int depth)
    {
        if (depth > 12)
            yield break;
        switch (token)
        {
            case JObject o when o.Value<string>("type") == "tool_use" && o["input"] is JObject toolInput:
                // Claude 的工具呼叫：連寫檔工具寫進去的內容都算（代理常先寫一個註冊排程的腳本再執行它）
                foreach (var s in AllStrings(toolInput, depth + 1))
                    yield return s;
                break;
            case JObject o:
                foreach (var p in o.Properties())
                {
                    if (SkipKeys.Contains(p.Name))
                        continue;
                    if (CommandKeys.Contains(p.Name))
                    {
                        foreach (var s in Strings(p.Value, depth + 1))
                            yield return s;
                        continue;
                    }
                    if (p.Value is JObject or JArray)
                    {
                        foreach (var s in CommandStrings(p.Value, depth + 1))
                            yield return s;
                    }
                }
                break;
            case JArray a:
                foreach (var item in a)
                {
                    foreach (var s in CommandStrings(item, depth + 1))
                        yield return s;
                }
                break;
        }
    }

    /// <summary>物件裡所有的字串（工具呼叫的參數）</summary>
    private static IEnumerable<string> AllStrings(JToken token, int depth)
    {
        if (depth > 12)
            yield break;
        switch (token)
        {
            case JValue { Type: JTokenType.String } v:
                foreach (var s in Strings(v, depth))
                    yield return s;
                break;
            case JObject o:
                foreach (var p in o.Properties())
                {
                    foreach (var s in AllStrings(p.Value, depth + 1))
                        yield return s;
                }
                break;
            case JArray a:
                if (a.Count > 0 && a.All(t => t.Type == JTokenType.String))
                {
                    foreach (var s in Strings(a, depth))
                        yield return s;
                    break;
                }
                foreach (var item in a)
                {
                    foreach (var s in AllStrings(item, depth + 1))
                        yield return s;
                }
                break;
        }
    }

    /// <summary>指令參數本身：字串（是 JSON 物件就再往裡面找）、字串陣列接成一行、物件往裡面找</summary>
    private static IEnumerable<string> Strings(JToken value, int depth)
    {
        switch (value)
        {
            case JValue { Type: JTokenType.String } v:
            {
                var s = v.Value<string>() ?? "";
                var trimmed = s.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    JToken? inner = null;
                    try
                    {
                        inner = JToken.Parse(s);
                    }
                    catch (JsonException)
                    {
                    }
                    if (inner != null)
                    {
                        foreach (var x in CommandStrings(inner, depth + 1))
                            yield return x;
                        if (inner is JArray arr && arr.All(t => t.Type == JTokenType.String))
                            yield return string.Join(' ', arr.Values<string>());
                        yield break;
                    }
                }
                yield return s;
                break;
            }
            case JArray a when a.All(t => t.Type == JTokenType.String):
                yield return string.Join(' ', a.Values<string>().Select(x => x != null && x.Contains(' ') ? "\"" + x + "\"" : x));
                break;
            case JObject or JArray:
                foreach (var s in CommandStrings(value, depth + 1))
                    yield return s;
                break;
        }
    }

    /// <summary>那一行的時間（timestamp、time、ts 的 ISO 字串）；沒有是 null</summary>
    public static DateTime? TimeOf(string line)
    {
        foreach (var key in new[] { "\"timestamp\":\"", "\"time\":\"", "\"ts\":\"" })
        {
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0)
                continue;
            int start = i + key.Length;
            int end = line.IndexOf('"', start);
            if (end > start && DateTimeOffset.TryParse(line[start..end], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return t.LocalDateTime;
        }
        return null;
    }

    /// <summary>那一行的工作資料夾（Claude 的 "cwd"）；沒有是 null</summary>
    public static string? CwdOf(string line)
    {
        int i = line.IndexOf("\"cwd\":\"", StringComparison.Ordinal);
        if (i < 0)
            return null;
        int start = i + 7;
        int end = start;
        while (end < line.Length && (line[end] != '"' || line[end - 1] == '\\'))
            end++;
        if (end >= line.Length)
            return null;
        try
        {
            return JsonConvert.DeserializeObject<string>("\"" + line[start..end] + "\"");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string VerbName(ScheduleVerb verb) => verb switch
    {
        ScheduleVerb.Create => "created",
        ScheduleVerb.Change => "changed",
        ScheduleVerb.Delete => "deleted",
        _ => "changed",
    };

    /// <summary>
    /// 一個排程的出處：對得上名稱的紀錄裡，最近一次建立（沒有建立就最近一次修改）之後沒有被刪掉的那一筆；
    /// 刪掉又以同名重建的取重建那次。找不到回 null
    /// </summary>
    public static TranscriptHit? OriginFor(string taskPath, IEnumerable<TranscriptHit> hits)
    {
        var mine = hits.Where(h => ScheduleOriginRules.NameMatches(h.Name, taskPath)).OrderBy(h => h.At).ToList();
        if (mine.Count == 0)
            return null;
        var lastDelete = mine.LastOrDefault(h => h.Verb == "deleted");
        var after = lastDelete == null ? mine : mine.Where(h => h.At >= lastDelete.At && h.Verb != "deleted").ToList();
        return after.LastOrDefault(h => h.Verb == "created") ?? after.LastOrDefault(h => h.Verb == "changed");
    }
}
