using System.Text.RegularExpressions;

namespace Myriagent.Schedules;

/// <summary>
/// 排程的來源分類（排程頁的篩選、要不要列出、要不要自動診斷）。Agent：有證據是代理建立或改過的；Adopted：使用者按了「納入管理」；
/// Suspected：沒有證據但看起來是自己開發的；Other：系統、廠商、更新程式；Ignored：使用者按了「不再列出」。
/// 值以名稱存（prefs.json、網頁），新增要讓前一版忽略得了
/// </summary>
internal enum ScheduleOrigin
{
    Agent,
    Adopted,
    Suspected,
    Other,
    Ignored,
}

/// <summary>判斷「疑似代理建立」的輸入（桌面在背景執行緒準備好；路徑在不在 git repo 要讀檔案系統，這裡只吃結果）</summary>
internal sealed record ScheduleClassifyInput(
    string Path,
    string? Author,
    IReadOnlyList<ScheduleAction> Actions,
    bool AnyPathInRepo,
    ScheduleEnvironment Environment);

/// <summary>
/// 判斷用的這台電腦的資料夾（展開環境變數、比對廠商位置）。WorkRoots＝雖然在廠商位置底下、但是使用者工作的資料夾
/// （Myriagent 對話自己的工作區在 %LOCALAPPDATA%\Myriagent\conversations\ 底下，代理在那裡寫的腳本不是廠商的）
/// </summary>
internal sealed record ScheduleEnvironment(
    string UserName,
    string MachineName,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<string>? WorkRoots = null)
{
    /// <summary>廠商與系統的位置（小寫、結尾有反斜線）：Program Files、Windows、ProgramData、AppData</summary>
    public IReadOnlyList<string> VendorRoots { get; } = new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "SystemRoot", "windir", "ProgramData", "APPDATA", "LOCALAPPDATA" }
        .Select(k => Variables.TryGetValue(k, out var v) ? v : null)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v!.TrimEnd('\\') + "\\")
        .Select(v => v.ToLowerInvariant())
        .Distinct()
        .ToList();
}

internal static class ScheduleClassifyRules
{
    /// <summary>腳本直譯器與殼層：執行它們的工作，看參數裡的腳本在哪</summary>
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "pythonw", "py", "pyw", "node", "powershell", "pwsh", "cmd", "bash", "sh", "wsl", "wscript", "cscript", "dotnet",
        "deno", "bun", "uv", "uvx", "npx", "npm", "php", "ruby", "perl", "java", "javaw", "Rscript",
    };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".py", ".pyw", ".bat", ".cmd", ".js", ".mjs", ".cjs", ".ts", ".sh", ".vbs", ".rb", ".pl", ".php",
    };

    private static readonly Regex AbsolutePath = new(@"(?:[A-Za-z]:\\|\\\\)[^""'|<>\r\n]*?(?=\s+-|\s+/|""|'|$|\s+[A-Za-z]:\\)", RegexOptions.Compiled);

    private static readonly Regex EnvVar = new(@"%([^%]+)%", RegexOptions.Compiled);

    /// <summary>Microsoft 的系統工作（根目錄底下的 \Microsoft\ 整個資料夾）：讀取時不進去</summary>
    public static bool IsSystemFolder(string folderPath)
        => folderPath.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase)
           || folderPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    /// <summary>沒有證據時的分類：Suspected 或 Other</summary>
    public static ScheduleOrigin Classify(ScheduleClassifyInput input)
    {
        var execs = input.Actions.Where(a => a.Kind == "Exec" && !string.IsNullOrWhiteSpace(a.Command)).ToList();
        if (execs.Count == 0)
            return ScheduleOrigin.Other;
        if (input.AnyPathInRepo)
            return ScheduleOrigin.Suspected;
        if (IsVendorAuthor(input.Author, input.Environment))
            return ScheduleOrigin.Other;
        foreach (var a in execs)
        {
            var command = Expand(a.Command!, input.Environment);
            if (command == null)
                continue;
            var name = FileStem(command);
            bool isScript = ScriptExtensions.Contains(Extension(command));
            var paths = PathsIn(a.Arguments, input.Environment).ToList();
            if (a.WorkingDirectory != null && Expand(a.WorkingDirectory, input.Environment) is { } wd)
                paths.Add(wd);
            if (Interpreters.Contains(name))
            {
                // 直譯器：參數或工作資料夾有一個不在廠商位置就算；完全沒有路徑（例如 cmd /c 一行指令）時作者是這個使用者才算
                if (paths.Count == 0 ? IsUserAuthor(input.Author, input.Environment) : paths.Any(p => !IsVendorPath(p, input.Environment)))
                    return ScheduleOrigin.Suspected;
                continue;
            }
            if (isScript || IsAbsolute(command))
            {
                if (!IsVendorPath(command, input.Environment))
                    return ScheduleOrigin.Suspected;
            }
        }
        return ScheduleOrigin.Other;
    }

    /// <summary>要檢查在不在 git repo 的路徑：指令本身、參數裡的絕對路徑、工作資料夾（展開環境變數；UNC 與展不開的不列）</summary>
    public static IReadOnlyList<string> CandidatePaths(IReadOnlyList<ScheduleAction> actions, ScheduleEnvironment env)
    {
        var list = new List<string>();
        foreach (var a in actions.Where(a => a.Kind == "Exec"))
        {
            if (a.Command != null && Expand(a.Command, env) is { } c && IsAbsolute(c))
                list.Add(c);
            list.AddRange(PathsIn(a.Arguments, env));
            if (a.WorkingDirectory != null && Expand(a.WorkingDirectory, env) is { } wd && IsAbsolute(wd))
                list.Add(wd);
        }
        return list.Where(p => !p.StartsWith(@"\\", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>排程的「工作資料夾」（排程頁顯示、交給代理時的資料夾）：動作的工作資料夾，沒有就是參數裡第一個腳本或指令所在的資料夾</summary>
    public static string? WorkFolder(IReadOnlyList<ScheduleAction> actions, ScheduleEnvironment env)
    {
        foreach (var a in actions.Where(a => a.Kind == "Exec"))
        {
            if (a.WorkingDirectory != null && Expand(a.WorkingDirectory, env) is { } wd && IsAbsolute(wd))
                return wd.TrimEnd('\\');
        }
        foreach (var a in actions.Where(a => a.Kind == "Exec"))
        {
            var script = PathsIn(a.Arguments, env).FirstOrDefault(p => !IsVendorPath(p, env));
            if (script != null)
                return DirectoryOf(script);
            if (a.Command != null && Expand(a.Command, env) is { } c && IsAbsolute(c) && !IsVendorPath(c, env))
                return DirectoryOf(c);
        }
        return null;
    }

    /// <summary>一行顯示的動作：指令＋參數（多個動作以「；」接起來）</summary>
    public static string ActionText(IReadOnlyList<ScheduleAction> actions)
        => string.Join("；", actions.Select(a => a.Kind == "Exec"
            ? (Quote(a.Command ?? "") + (string.IsNullOrWhiteSpace(a.Arguments) ? "" : " " + a.Arguments)).Trim()
            : a.Kind switch { "ComHandler" => "COM 元件", "SendEmail" => "寄電子郵件", "ShowMessage" => "顯示訊息", _ => a.Kind }));

    private static string Quote(string command) => command.Contains(' ') && !command.StartsWith('"') ? $"\"{command}\"" : command;

    internal static IEnumerable<string> PathsIn(string? arguments, ScheduleEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            yield break;
        var expanded = EnvVar.Replace(arguments, m => env.Variables.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        foreach (Match m in AbsolutePath.Matches(expanded))
        {
            var p = m.Value.Trim().TrimEnd('\\', ' ');
            if (p.Length > 3)
                yield return p;
        }
    }

    /// <summary>展開 %變數%；有展不開的變數是 null（不知道在哪）</summary>
    internal static string? Expand(string path, ScheduleEnvironment env)
    {
        var p = path.Trim().Trim('"');
        bool unknown = false;
        var expanded = EnvVar.Replace(p, m =>
        {
            if (env.Variables.TryGetValue(m.Groups[1].Value, out var v))
                return v;
            unknown = true;
            return m.Value;
        });
        return unknown ? null : expanded;
    }

    internal static bool IsVendorPath(string path, ScheduleEnvironment env)
    {
        var lower = path.ToLowerInvariant().Replace('/', '\\');
        if (env.WorkRoots is { } work && work.Any(w => lower.StartsWith(w.TrimEnd('\\').ToLowerInvariant() + "\\", StringComparison.Ordinal)))
            return false;
        return env.VendorRoots.Any(r => lower.StartsWith(r, StringComparison.Ordinal) || lower + "\\" == r);
    }

    /// <summary>作者是公司或程式名稱（不是「電腦\使用者」、不是 SID、不是這個使用者）</summary>
    internal static bool IsVendorAuthor(string? author, ScheduleEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(author))
            return false;
        return !IsUserAuthor(author, env) && !author.Contains('\\') && !author.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUserAuthor(string? author, ScheduleEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(author))
            return true;
        var a = author.Trim();
        return a.Equals(env.UserName, StringComparison.OrdinalIgnoreCase)
               || a.Equals($"{env.MachineName}\\{env.UserName}", StringComparison.OrdinalIgnoreCase)
               || a.EndsWith("\\" + env.UserName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAbsolute(string path) => path.Length > 2 && ((char.IsLetter(path[0]) && path[1] == ':') || path.StartsWith(@"\\", StringComparison.Ordinal));

    private static string FileStem(string command)
    {
        var name = command.Replace('/', '\\');
        int slash = name.LastIndexOf('\\');
        if (slash >= 0)
            name = name[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string Extension(string command)
    {
        int slash = command.LastIndexOfAny(new[] { '\\', '/' });
        int dot = command.LastIndexOf('.');
        return dot > slash ? command[dot..] : "";
    }

    private static string DirectoryOf(string path)
    {
        int slash = path.LastIndexOf('\\');
        return slash > 2 ? path[..slash] : path;
    }
}
