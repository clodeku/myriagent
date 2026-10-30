namespace Myriagent.Background;

/// <summary>
/// 程序快照的一列（<see cref="IProcessSnapshotSource"/>）：<paramref name="CreatedAt"/> 是建立時間（FILETIME 的 100ns 刻度），用來分辨 PID 被重複使用；
/// <paramref name="Marker"/> 是環境變數 <see cref="BackgroundProgramRules.MarkerVariable"/> 的值（對話 id；讀不到或沒有是 null）；
/// <paramref name="IsConsole"/> 是執行檔的子系統是不是主控台（null＝不知道）；<paramref name="CommandLine"/> 只有帶標記的程序才讀
/// </summary>
internal sealed record ProcessRow(int Pid, int ParentPid, long CreatedAt, string Name, string? Marker = null, bool? IsConsole = null, string? CommandLine = null);

/// <summary>
/// 一個背景程式（一棵脫離 CLI 的程序樹的根）。<paramref name="ParentPid"/> 是快照裡的父程序 PID（父程序多半已經結束；
/// 根結束了、子程序接著當根的情形靠它認出來，<see cref="BackgroundProgramRules.Exits"/>）
/// </summary>
internal sealed record BackgroundProgram(int Pid, string Name, string? CommandLine, long CreatedAt, int ParentPid = 0)
{
    /// <summary>給網頁的 id（PID 會被重複使用，連建立時間一起）：對話標頭的 programs[].id、programStop 的 programId</summary>
    public string Id => $"{Pid}-{CreatedAt}";
}

/// <summary>
/// 一個自己結束了的背景程式（掃描之間消失、不是被它的子程序接著當根）：<paramref name="ExitCode"/> 與 <paramref name="ExitedAt"/>（FILETIME 刻度）
/// 讀不到是 null；<paramref name="StoppedByUser"/>＝使用者在 app 裡按了停止
/// </summary>
internal sealed record BackgroundProgramExit(string ConversationId, BackgroundProgram Program, int? ExitCode, long? ExitedAt, bool StoppedByUser = false);

/// <summary>
/// 對話開出的背景程式（2026-10-23 使用者要求：回合結束後代理開的程式還在跑時，帳戶卡也要看得出來）的純規則。
/// app 啟動每個對話的 CLI 時在環境變數 <see cref="MarkerVariable"/> 放對話 id；代理以 nohup、Start-Process 等開出、脫離 CLI 的程序都繼承它
/// （父程序結束、app 重開之後都還在）。分類（<see cref="Classify"/>）：帶標記、主控台程式、不在任何 CLI 程序（app 工作物件裡的程序）底下的才算——
/// 回合中的工具、MCP 伺服器、run_in_background 多半還在 CLI 底下（最後一種由 CLI 自己回報，見 AgentHub.Background.cs 的 BackgroundOf）。執行列的程式由 app 直接啟動、沒有標記，不算。
/// 但「不在 CLI 底下」只是候選：Git Bash 經 timeout、env、bash -c 開的程式父子關係會斷掉，前景指令也像脫離了 CLI（2026-10-24 實測），
/// 所以中樞只把撐過一輪結束的當成背景程式、對得上 CLI 背景工作的（<see cref="IsCliTask"/>）不算（AgentHub.BackgroundPrograms.cs）
/// </summary>
internal static class BackgroundProgramRules
{
    /// <summary>CLI 程序的環境變數：這個程序屬於哪個對話（32 個小寫十六進位）</summary>
    public const string MarkerVariable = "MYRIAGENT_AGENT_CONVERSATION";

    /// <summary>
    /// 平常多久列一次程序（程式結束後最多這麼久標示才消失）。一千個程序的電腦列一次約 70～130 毫秒（同 Process.GetProcesses，成本在系統呼叫本身），
    /// 所以不密集掃；新的背景程式只會在一輪裡被開出來，一輪結束時另外立刻掃一次（<see cref="RescanDelay"/>）
    /// </summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);

    /// <summary>一輪結束時（中樞要求重掃）等多久才掃：連續好幾個對話同時結束時併成一次</summary>
    public static readonly TimeSpan RescanDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>帳戶卡的說明每個對話最多列幾個程式</summary>
    public const int MaxListedPrograms = 4;

    /// <summary>說明裡一個程式的命令列最多幾個字</summary>
    public const int MaxCommandLength = 80;

    /// <summary>往上找父程序最多幾層（防環）</summary>
    private const int MaxDepth = 64;

    /// <summary>對話標頭的清單裡命令列最多幾個字（選單裡顯示，比帳戶卡的長）</summary>
    public const int MaxHeaderCommandLength = 240;

    /// <summary>對話標頭最多列幾個程式</summary>
    public const int MaxHeaderPrograms = 20;

    /// <summary>
    /// 主控台的宿主程序（跟著主控台程式出現）與建置工具留著的常駐程序（不是代理開的程式：MSBuild 的編譯伺服器 VBCSCompiler、
    /// C++ 的 mspdbsrv、VC 的 vctip，閒置十幾分鐘自己結束）
    /// </summary>
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "openconsole.exe", "vbcscompiler.exe", "mspdbsrv.exe", "vctip.exe",
    };

    /// <summary>標記的值合格：32 個小寫十六進位（對話 id 的格式）</summary>
    public static bool IsMarker(string? value)
        => value is { Length: 32 } && value.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// 環境區塊（UTF-16 解碼後，以 \0 分隔的 NAME=VALUE，以空字串結尾）裡的標記；名稱不分大小寫（Windows 的環境變數）。
    /// 沒有、或值不合格回 null
    /// </summary>
    public static string? MarkerOf(string environmentBlock)
    {
        var prefix = MarkerVariable + "=";
        foreach (var entry in environmentBlock.Split('\0'))
        {
            if (entry.Length == 0)
                break;
            if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return IsMarker(entry[prefix.Length..]) ? entry[prefix.Length..] : null;
        }
        return null;
    }

    /// <summary>
    /// 從程序快照找出各對話的背景程式（每棵脫離 CLI 的程序樹只算根）。<paramref name="managedRoots"/> 是 CLI 程序的 PID（app 工作物件裡的程序）：
    /// 它們與活著的子孫都不算。父程序以 PID 對上、而且建立得比子程序早才算活著的父程序（PID 被重複使用時不會誤接）。
    /// 子系統不是主控台的（瀏覽器、編輯器這類視窗程式）不算；子系統不知道的照算
    /// </summary>
    public static Dictionary<string, List<BackgroundProgram>> Classify(IReadOnlyList<ProcessRow> rows, IReadOnlySet<int> managedRoots)
    {
        var byPid = new Dictionary<int, ProcessRow>();
        foreach (var row in rows)
            byPid.TryAdd(row.Pid, row);

        ProcessRow? LiveParent(ProcessRow row)
            => row.ParentPid != row.Pid && byPid.TryGetValue(row.ParentPid, out var parent) && parent.CreatedAt <= row.CreatedAt ? parent : null;

        bool IsManaged(ProcessRow row)
        {
            var current = row;
            for (var depth = 0; depth < MaxDepth; depth++)
            {
                if (managedRoots.Contains(current.Pid))
                    return true;
                if (LiveParent(current) is not { } parent)
                    return false;
                current = parent;
            }
            return false;
        }

        // 建置工具留著的常駐程序開出的程序也不算（MSBuild 節點開的 csc、vstest.console：2026-10-24 核對時被列成背景程式）
        bool UnderBuildDaemon(ProcessRow row)
        {
            var current = row;
            for (var depth = 0; depth < MaxDepth; depth++)
            {
                if (LiveParent(current) is not { } parent)
                    return false;
                if (IsBuildDaemon(parent))
                    return true;
                current = parent;
            }
            return false;
        }

        bool IsCandidate(ProcessRow row)
            => row.Marker is not null && (row.IsConsole != false || IsBackgroundGuiProgram(row.Name, row.CommandLine))
                && !IsBuildDaemon(row) && !IsManaged(row) && !UnderBuildDaemon(row);

        var result = new Dictionary<string, List<BackgroundProgram>>(StringComparer.Ordinal);
        foreach (var row in rows.Where(IsCandidate))
        {
            // 父程序也是同一個對話的背景程式：它是同一棵樹，只算根
            if (LiveParent(row) is { } parent && parent.Marker == row.Marker && IsCandidate(parent))
                continue;
            if (!result.TryGetValue(row.Marker!, out var list))
                result[row.Marker!] = list = [];
            list.Add(new BackgroundProgram(row.Pid, row.Name, row.CommandLine, row.CreatedAt, row.ParentPid));
        }
        foreach (var list in result.Values)
            list.Sort((a, b) => a.CreatedAt != b.CreatedAt ? a.CreatedAt.CompareTo(b.CreatedAt) : a.Pid.CompareTo(b.Pid));
        return result;
    }

    /// <summary>
    /// 建置與版本控制工具自己留著的常駐程序（代理執行 dotnet build、gradle、git 之後脫離 CLI、帶著對話標記，但不是代理開的程式）：
    /// MSBuild 的節點重用（<c>/nodemode:</c>）、以 dotnet 執行的 VBCSCompiler 與 Razor 編譯伺服器、Gradle 與 Kotlin 的 daemon、
    /// git 的 fsmonitor、adb 的伺服器，以及 SQL Server LocalDB 的執行個體（第一次連線時 LocalDB 自己啟動、所有程式共用、閒置後自己結束；
    /// 2026-10-24 核對時被算成連過它的那個對話的背景程式）。命令列讀不到的照算
    /// </summary>
    public static bool IsToolDaemon(string name, string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
            return false;
        bool Has(string text) => commandLine.Contains(text, StringComparison.OrdinalIgnoreCase);
        return name.ToLowerInvariant() switch
        {
            "msbuild.exe" => Has("nodemode:"),
            "dotnet.exe" => Has("VBCSCompiler.dll") || Has("rzc.dll") || (Has("MSBuild.dll") && Has("nodemode:")),
            "java.exe" or "javaw.exe" => Has("GradleDaemon") || Has("KotlinCompileDaemon"),
            "git.exe" => Has("fsmonitor--daemon"),
            "adb.exe" => Has("fork-server"),
            "sqlservr.exe" => Has("LocalDB"),
            _ => false,
        };
    }

    /// <summary>
    /// 不是主控台程式、但也不是開給人看的視窗程式（2026-10-24 使用者要求：這兩種也算背景程式）：沒有視窗的背景執行器（pythonw、javaw、wscript），
    /// 與自動化用的瀏覽器——帶 --remote-debugging-port／--remote-debugging-pipe／--headless 的 Chromium 系列（chrome、msedge、brave、chromium、vivaldi）、
    /// 帶 --marionette／-headless 的 Firefox（例：代理以遠端偵錯開的 Chrome 跑了 5 個多小時，以前因為是視窗程式一律不算）。
    /// 一般開給人看的視窗程式照舊不算；瀏覽器開出的分頁、GPU 等子程序跟著根，不另外列
    /// </summary>
    public static bool IsBackgroundGuiProgram(string name, string? commandLine)
    {
        switch (name.ToLowerInvariant())
        {
            case "pythonw.exe" or "javaw.exe" or "wscript.exe":
                return true;
            case "chrome.exe" or "msedge.exe" or "brave.exe" or "chromium.exe" or "vivaldi.exe":
                return commandLine is { } cmd && (cmd.Contains("--remote-debugging-port", StringComparison.OrdinalIgnoreCase)
                    || cmd.Contains("--remote-debugging-pipe", StringComparison.OrdinalIgnoreCase) || cmd.Contains("--headless", StringComparison.OrdinalIgnoreCase));
            case "firefox.exe":
                return commandLine is { } ff && (ff.Contains("--marionette", StringComparison.OrdinalIgnoreCase) || ff.Contains("-headless", StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    /// <summary>建置工具留著的常駐程序：<see cref="IgnoredNames"/> 裡的編譯伺服器與 <see cref="IsToolDaemon"/>（它們開出的程序也不算，Classify）</summary>
    private static bool IsBuildDaemon(ProcessRow row) => IgnoredNames.Contains(row.Name) || IsToolDaemon(row.Name, row.CommandLine);

    /// <summary>
    /// 這個程式是 CLI 自己追蹤的背景工作（Claude 的 run_in_background、Codex 讓出的指令；<paramref name="taskTexts"/> 是會話回報的背景工作的工具說明與完整指令，
    /// AgentBackgroundTool 的 Title、Detail）：程式的命令列（執行檔去掉路徑與 .exe、加上參數）正規化後出現在其中一個裡面。
    /// 不能靠父程序判斷：Git Bash 經 timeout、env、bash -c 這類 MSYS 程式開的程式，和 CLI 的父子關係會斷掉（MSYS 以 fork＋exec 模擬，
    /// 中間的程序一結束 Windows 的父程序就不在了，2026-10-24 實測）。沒有參數的不比（只有執行檔名太容易誤配）
    /// </summary>
    public static bool IsCliTask(BackgroundProgram program, IEnumerable<string?> taskTexts)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(taskTexts);
        var command = NormalizeCommand(ShortCommand(program.CommandLine, program.Name, int.MaxValue));
        if (!command.Contains(' '))
            return false;
        return taskTexts.Any(text => !string.IsNullOrWhiteSpace(text) && NormalizeCommand(text).Contains(command, StringComparison.Ordinal));
    }

    /// <summary>比對命令列用：小寫、反斜線換斜線、MSYS 的 /c/… 換成 c:/…、拿掉引號、空白（含換行）壓成一個</summary>
    public static string NormalizeCommand(string text)
    {
        var s = text.Replace('\\', '/').Replace("\"", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(^|[\s=(])/([a-z])/", "$1$2:/");
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// 兩次掃描之間自己結束的背景程式：上一次有、這一次沒有，而且不是根結束了、它的子程序接著當根（同一個對話裡有程式的父程序 PID 是它、
    /// 建立得不比它早）——那種只是同一個程式換了根，不算結束
    /// </summary>
    public static List<(string ConversationId, BackgroundProgram Program)> Exits(
        IReadOnlyDictionary<string, List<BackgroundProgram>> previous, IReadOnlyDictionary<string, List<BackgroundProgram>> current)
    {
        var exits = new List<(string, BackgroundProgram)>();
        foreach (var (conversationId, programs) in previous)
        {
            var now = current.TryGetValue(conversationId, out var list) ? list : [];
            foreach (var program in programs)
            {
                if (now.Any(p => p.Pid == program.Pid && p.CreatedAt == program.CreatedAt))
                    continue;
                if (now.Any(p => p.ParentPid == program.Pid && p.CreatedAt >= program.CreatedAt))
                    continue;
                exits.Add((conversationId, program));
            }
        }
        return exits;
    }

    /// <summary>
    /// 程式自己結束時加在對話裡的提示（AgentNoticeKind.BackgroundProgram）：「代理另開的程式「python worker6.py」已結束（結束碼 1，執行了 12 分鐘）」。
    /// 結束碼 -1（被 TerminateProcess、taskkill /F 這類強制結束）寫「被強制結束」、0xC000013A 寫「被中斷（Ctrl+C）」（2026-10-24 使用者要求：
    /// 以前顯示成看不懂的 0xFFFFFFFF）；其他大於 0xFFFF 的（NTSTATUS）以十六進位顯示
    /// </summary>
    public static string ExitNotice(BackgroundProgramExit exit)
    {
        var parts = new List<string>();
        if (exit.ExitCode is { } code)
            parts.Add(ExitCodeText(code));
        if (exit.ExitedAt is { } end && end > exit.Program.CreatedAt)
            parts.Add("執行了 " + DurationText(TimeSpan.FromTicks(end - exit.Program.CreatedAt)));
        var detail = parts.Count == 0 ? string.Empty : "（" + string.Join("，", parts) + "）";
        return $"代理另開的程式「{ShortCommand(exit.Program.CommandLine, exit.Program.Name)}」已結束{detail}";
    }

    /// <summary>結束碼給人看的說法（ExitNotice）</summary>
    public static string ExitCodeText(int code) => unchecked((uint)code) switch
    {
        0xFFFFFFFF => "被強制結束",
        0xC000013A => "被中斷（Ctrl+C）",
        > 0xFFFF and var u => $"結束碼 0x{u:X8}",
        _ => "結束碼 " + code.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>執行了多久：不到 1 分鐘以秒、不到 1 小時以分鐘、之後以小時加分鐘</summary>
    public static string DurationText(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
            return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))} 秒";
        if (duration < TimeSpan.FromHours(1))
            return $"{(int)duration.TotalMinutes} 分鐘";
        var minutes = duration.Minutes;
        return minutes == 0 ? $"{(int)duration.TotalHours} 小時" : $"{(int)duration.TotalHours} 小時 {minutes} 分";
    }

    /// <summary>
    /// 給人看的命令列：第一個參數（執行檔，可能有引號）換成不含路徑與 .exe 的檔名，其他照舊，空白壓成一個，超過 <see cref="MaxCommandLength"/> 字截斷加「…」。
    /// 沒有命令列時用程序名稱（同樣去掉 .exe）
    /// </summary>
    public static string ShortCommand(string? commandLine, string name) => ShortCommand(commandLine, name, MaxCommandLength);

    /// <summary>同上，最多 <paramref name="maxLength"/> 字</summary>
    public static string ShortCommand(string? commandLine, string name, int maxLength)
    {
        var text = (commandLine ?? string.Empty).Trim();
        string exe, rest;
        if (text.Length == 0)
        {
            exe = name;
            rest = string.Empty;
        }
        else if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            exe = end < 0 ? text[1..] : text[1..end];
            rest = end < 0 ? string.Empty : text[(end + 1)..];
        }
        else
        {
            var space = text.IndexOf(' ');
            exe = space < 0 ? text : text[..space];
            rest = space < 0 ? string.Empty : text[space..];
        }

        var file = exe.Replace('/', '\\');
        file = file[(file.LastIndexOf('\\') + 1)..];
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            file = file[..^4];
        var joined = string.Join(' ', (file + " " + rest).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (joined.Length == 0)
            joined = name;
        return joined.Length <= maxLength ? joined : joined[..(maxLength - 1)] + "…";
    }
}
