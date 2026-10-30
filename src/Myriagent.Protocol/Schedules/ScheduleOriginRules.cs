using System.Text;
using System.Text.RegularExpressions;

namespace Myriagent.Schedules;

/// <summary>指令對排程做了什麼（Unknown＝看得出在註冊或修改排程、看不出是哪一個，例如名稱是變數、用 COM）</summary>
internal enum ScheduleVerb
{
    Create,
    Change,
    Delete,
    Run,
    End,
    Enable,
    Disable,
    Unknown,
}

/// <summary>一筆：動作＋工作名稱（可帶資料夾，「\資料夾\名稱」；看不出名稱是 null）</summary>
internal sealed record ScheduleCommandHit(ScheduleVerb Verb, string? Name);

/// <summary>
/// 從代理執行的指令文字（工具卡的指令、CLI 逐字稿）找出它對 Windows 排程做了什麼：
/// schtasks 的 /create、/change、/delete、/run、/end（含 Git Bash 的 //create、-create、cmd /c 包著、Python 的串列寫法），
/// PowerShell 的 Register-／Set-／Unregister-／Enable-／Disable-／Start-／Stop-ScheduledTask（-TaskName 或第一個位置參數、-TaskPath、
/// 從 Get-ScheduledTask 管線傳進來的名稱），以及經 COM（Schedule.Service 的 RegisterTaskDefinition）註冊的。
/// 只是找線索：判斷錯了頂多多掃一次、少一個連結，不會因此動到任何排程
/// </summary>
internal static class ScheduleOriginRules
{
    private static readonly Regex Schtasks = new(@"(?<![\w-])schtasks(?:\.exe)?(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Cmdlet = new(@"\b(Register|Set|Unregister|Enable|Disable|Start|Stop)-ScheduledTask\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GetTask = new(@"\bGet-ScheduledTask\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LineContinuation = new(@"[`^\\][ \t]*\r?\n", RegexOptions.Compiled);

    /// <summary>同一段文字裡的變數：PowerShell 的 $x = '…'（含 [string]$x、param 的預設值）、cmd 的 set x=…、bash 的 x=…</summary>
    private static readonly Regex PsAssign = new(@"\$(?:\{)?(?<n>\w+)(?:\})?\s*=\s*(?<q>['""])(?<v>[^'""\r\n$]+)\k<q>", RegexOptions.Compiled);

    private static readonly Regex CmdAssign = new(@"(?im)^\s*set\s+""?(?<n>\w+)=(?<v>[^""\r\n&|]+)""?", RegexOptions.Compiled);

    private static readonly Regex ShAssign = new(@"(?m)(?:^|[\s;])(?<n>[A-Za-z_]\w*)=(?<q>['""]?)(?<v>[^'""\s;]+)\k<q>", RegexOptions.Compiled);

    private static readonly Regex BracedVariable = new(@"\$\{(?<n>\w+)\}", RegexOptions.Compiled);

    private static readonly Regex Com = new(@"Schedule\.Service|RegisterTaskDefinition", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>快速篩選：文字裡有沒有可能碰到排程的字（逐字稿掃描先以這個略過大部分的內容）</summary>
    public static bool MayTouch(string text)
        => text.Contains("schtasks", StringComparison.OrdinalIgnoreCase)
           || text.Contains("ScheduledTask", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Schedule.Service", StringComparison.OrdinalIgnoreCase);

    /// <summary>會改變排程的動作（不含執行、停止）</summary>
    public static bool Mutates(ScheduleVerb verb) => verb is ScheduleVerb.Create or ScheduleVerb.Change or ScheduleVerb.Delete
        or ScheduleVerb.Enable or ScheduleVerb.Disable or ScheduleVerb.Unknown;

    public static IReadOnlyList<ScheduleCommandHit> Parse(string? text)
    {
        var hits = new List<ScheduleCommandHit>();
        if (string.IsNullOrEmpty(text) || !MayTouch(text))
            return hits;
        // 接續到下一行的指令（PowerShell 的反引號、cmd 的 ^、bash 的反斜線）先接成一行
        text = LineContinuation.Replace(text, " ");
        // ${Name} 寫成 $Name（大括號會被切成記號）
        text = BracedVariable.Replace(text, "$$${n}");
        var vars = Variables(text);
        foreach (var statement in Statements(text))
        {
            foreach (Match m in Schtasks.Matches(statement))
            {
                var rest = statement[(m.Index + m.Length)..];
                // 串列寫法 ["schtasks", "/create", …]：名稱自己的收尾引號不算下一個參數的開頭
                if (m.Index > 0 && statement[m.Index - 1] is '"' or '\'' && rest.StartsWith(statement[m.Index - 1]))
                    rest = rest[1..];
                ParseSchtasks(rest, hits);
            }
            ParseCmdlets(statement, hits);
            if (Com.IsMatch(statement) && statement.Contains("Register", StringComparison.OrdinalIgnoreCase))
                hits.Add(new ScheduleCommandHit(ScheduleVerb.Unknown, null));
        }
        // 名稱是變數的：代入同一段文字裡指定的值（腳本裡常見 $TaskName = 'X'）；不可能是工作名稱的字丟掉
        return hits.Select(h => h.Name != null && Resolve(h.Name, vars) is var resolved ? h with { Name = ValidName(resolved) ? resolved : null } : h)
            .Distinct().ToList();
    }

    private static Dictionary<string, string> Variables(string text)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in PsAssign.Matches(text))
            vars.TryAdd("$" + m.Groups["n"].Value, m.Groups["v"].Value.Trim());
        foreach (Match m in CmdAssign.Matches(text))
            vars.TryAdd("%" + m.Groups["n"].Value + "%", m.Groups["v"].Value.Trim());
        foreach (Match m in ShAssign.Matches(text))
            vars.TryAdd("$" + m.Groups["n"].Value, m.Groups["v"].Value.Trim());
        return vars;
    }

    private static string Resolve(string name, Dictionary<string, string> vars)
    {
        var key = name.Replace("${", "$").Replace("}", "");
        return vars.TryGetValue(key, out var v) ? v : name;
    }

    /// <summary>工作名稱不能有 / : * ? " &lt; &gt; |（反斜線是資料夾）；變數、空白開頭的也不是</summary>
    internal static bool ValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 && name.IndexOfAny(InvalidNameChars) < 0
           && !name.StartsWith('%') && !name.StartsWith('-');

    /// <summary>工作名稱不會有的字：Windows 不准的、沒代入的變數，以及說明文字的中文標點（散文裡提到指令時會被當成名稱）</summary>
    private static readonly char[] InvalidNameChars = { '/', ':', '*', '?', '"', '<', '>', '|', '$', '、', '。', '，', '；', '：', '（', '）', '「', '」', '！', '？' };

    /// <summary>把文字切成一個個陳述（換行、分號、&amp;&amp;、||；引號裡的不切）</summary>
    private static IEnumerable<string> Statements(string text)
    {
        var sb = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                sb.Append(ch);
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                sb.Append(ch);
                continue;
            }
            bool split = ch is '\n' or '\r' or ';' || (ch == '&' && i + 1 < text.Length && text[i + 1] == '&') || (ch == '|' && i + 1 < text.Length && text[i + 1] == '|');
            if (split)
            {
                if (sb.Length > 0)
                    yield return sb.ToString();
                sb.Clear();
                if (ch is '&' or '|')
                    i++;
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }

    /// <summary>以空白、逗號、方括號、圓括號切成記號（引號裡的不切、去掉外層引號；PowerShell 的反引號跳脫拿掉）</summary>
    internal static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        bool any = false;
        foreach (char raw in text)
        {
            char ch = raw;
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                else
                    sb.Append(ch);
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                any = true;
                continue;
            }
            if (char.IsWhiteSpace(ch) || ch is ',' or '[' or ']' or '(' or ')' or '{' or '}')
            {
                if (sb.Length > 0 || any)
                    tokens.Add(sb.ToString());
                sb.Clear();
                any = false;
                continue;
            }
            if (ch == '`')
                continue;
            sb.Append(ch);
            any = true;
        }
        if (sb.Length > 0 || any)
            tokens.Add(sb.ToString());
        return tokens;
    }

    private static string? FlagName(string token)
    {
        if (token.StartsWith("//", StringComparison.Ordinal))
            token = token[2..];
        else if (token.StartsWith('/') || token.StartsWith('-'))
            token = token[1..];
        else
            return null;
        return token.Length > 0 && token.All(char.IsLetter) ? token.ToLowerInvariant() : null;
    }

    private static void ParseSchtasks(string rest, List<ScheduleCommandHit> hits)
    {
        // 管線之後的是另一個指令
        var tokens = Tokens(rest.Split('|')[0]);
        ScheduleVerb? verb = null;
        string? name = null;
        bool enable = false, disable = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            switch (FlagName(tokens[i]))
            {
                case "create":
                    verb = ScheduleVerb.Create;
                    break;
                case "change":
                    verb = ScheduleVerb.Change;
                    break;
                case "delete":
                    verb = ScheduleVerb.Delete;
                    break;
                case "run":
                    verb = ScheduleVerb.Run;
                    break;
                case "end":
                    verb = ScheduleVerb.End;
                    break;
                case "enable":
                    enable = true;
                    break;
                case "disable":
                    disable = true;
                    break;
                case "tn":
                    if (i + 1 < tokens.Count)
                        name = NameOrNull(tokens[++i]);
                    break;
            }
        }
        if (verb == null)
            return;
        if (verb == ScheduleVerb.Change && (enable || disable))
            verb = disable ? ScheduleVerb.Disable : ScheduleVerb.Enable;
        hits.Add(new ScheduleCommandHit(verb.Value, name));
    }

    private static void ParseCmdlets(string statement, List<ScheduleCommandHit> hits)
    {
        if (!Cmdlet.IsMatch(statement))
            return;
        // 同一條管線前面的 Get-ScheduledTask 的名稱：Get-ScheduledTask X | Disable-ScheduledTask
        string? piped = null;
        var segments = SplitPipeline(statement);
        foreach (var segment in segments)
        {
            var get = GetTask.Match(segment);
            if (get.Success)
            {
                piped = TaskNameIn(Tokens(segment[(get.Index + get.Length)..]), out _);
                continue;
            }
            var m = Cmdlet.Match(segment);
            if (!m.Success)
                continue;
            var verb = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "register" => ScheduleVerb.Create,
                "set" => ScheduleVerb.Change,
                "unregister" => ScheduleVerb.Delete,
                "enable" => ScheduleVerb.Enable,
                "disable" => ScheduleVerb.Disable,
                "start" => ScheduleVerb.Run,
                "stop" => ScheduleVerb.End,
                _ => ScheduleVerb.Unknown,
            };
            var name = TaskNameIn(Tokens(segment[(m.Index + m.Length)..]), out var folder) ?? piped;
            if (name != null && folder != null && !name.StartsWith('\\'))
                name = folder.TrimEnd('\\') + "\\" + name;
            hits.Add(new ScheduleCommandHit(verb, name));
            piped = null;
        }
    }

    private static List<string> SplitPipeline(string statement)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (char ch in statement)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                sb.Append(ch);
                continue;
            }
            if (ch is '"' or '\'')
                quote = ch;
            if (ch == '|')
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>cmdlet 後面的參數：-TaskName（或縮寫 -TaskN…）的值，沒有就是第一個位置參數；-TaskPath 另外回傳</summary>
    private static string? TaskNameIn(List<string> tokens, out string? folder)
    {
        folder = null;
        string? named = null, positional = null;
        bool afterFlag = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith('-') && t.Length > 1 && char.IsLetter(t[1]))
            {
                var flag = t[1..].TrimEnd(':').ToLowerInvariant();
                if (flag.StartsWith("taskn", StringComparison.Ordinal) && i + 1 < tokens.Count)
                {
                    named = NameOrNull(tokens[++i]) ?? "";
                    afterFlag = false;
                    continue;
                }
                if (flag.StartsWith("taskp", StringComparison.Ordinal) && i + 1 < tokens.Count)
                {
                    folder = NameOrNull(tokens[++i]);
                    afterFlag = false;
                    continue;
                }
                afterFlag = !IsSwitch(flag);
                continue;
            }
            if (!afterFlag && positional == null && t.Length > 0)
                positional = NameOrNull(t) ?? "";
            afterFlag = false;
        }
        var name = named ?? positional;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static bool IsSwitch(string flag)
        => flag is "force" or "confirm" or "whatif" or "asjob" or "verbose" or "passthru";

    /// <summary>名稱：雜湊表展開（@params）與空字串看不出來；變數（$name、%name%）留著，Parse 最後以同一段文字裡指定的值代入</summary>
    private static string? NameOrNull(string token)
    {
        var t = token.Trim();
        if (t.Length == 0 || t.StartsWith('@') || t.StartsWith('{'))
            return null;
        return t;
    }

    /// <summary>指令裡的名稱對不對得上工作（路徑「\資料夾\名稱」）：帶資料夾的比整個路徑，不帶的比最後一段</summary>
    public static bool NameMatches(string? hitName, string taskPath)
    {
        if (string.IsNullOrWhiteSpace(hitName))
            return false;
        var name = hitName.Trim().Replace('/', '\\');
        if (name.Contains('\\'))
            return taskPath.Equals(name.StartsWith('\\') ? name : "\\" + name, StringComparison.OrdinalIgnoreCase);
        int slash = taskPath.LastIndexOf('\\');
        return taskPath[(slash + 1)..].Equals(name, StringComparison.OrdinalIgnoreCase);
    }
}
