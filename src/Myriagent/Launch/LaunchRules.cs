using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Launch;

/// <summary>執行列能啟動的專案種類</summary>
internal enum LaunchKind
{
    /// <summary>.NET 的視窗程式（WinExe；WinForms、WPF、Avalonia…）：dotnet build 再 dotnet run --no-build</summary>
    DotNet,

    /// <summary>Electron（package.json 有 electron 與 start 或 dev 指令碼）：npm run</summary>
    Electron,
}

/// <summary>
/// 執行模式（2026-10-25 使用者要求）：Debug＝以 Debug 組態建置，執行的程式是 app 的子程序，app 關閉、當掉或重新啟動時一起結束（可以附加偵錯工具）；
/// Release＝以 Release 組態建置，執行的程式和 app 脫鉤、獨立執行，app 重新啟動後接回。每個專案各自記得（launch\prefs.json）
/// </summary>
internal enum LaunchMode
{
    Debug,
    Release,
}

/// <summary>一個目標現在在做什麼</summary>
internal enum LaunchPhase
{
    Idle,
    Building,
    Running,
    Paused,
    Stopping,
}

/// <summary>上一次怎麼結束的</summary>
internal enum LaunchEndKind
{
    /// <summary>程式自己結束（關掉視窗、當掉）</summary>
    Exited,

    /// <summary>使用者按了停止（建置中或執行中）</summary>
    Stopped,

    BuildFailed,

    /// <summary>程序開不起來（找不到 dotnet、npm…）</summary>
    StartFailed,
}

/// <summary>按鈕要做的事（<see cref="LaunchRules.Plan"/>）</summary>
internal enum LaunchStep
{
    None,

    /// <summary>建置（有建置步驟時）再執行</summary>
    Start,

    Stop,
    Pause,
    Resume,

    /// <summary>停掉（建置中或執行中）、等它結束，再重新建置與執行</summary>
    Restart,
}

/// <summary>什麼時候重新偵測專案（<see cref="LaunchRules.ShouldScan"/>）</summary>
internal enum LaunchScanTrigger
{
    /// <summary>開分頁</summary>
    Open,

    /// <summary>這一輪結束（代理可能剛建立或改了專案）</summary>
    Changed,

    /// <summary>換了工作資料夾、使用者按「重新偵測」</summary>
    User,

    /// <summary>維護計時器（畫面上看得到的對話）</summary>
    Periodic,
}

/// <param name="Id">不透明的鍵（專案檔完整路徑的雜湊）：同一個專案不論從哪個工作資料夾偵測到都是同一個，執行狀態共用</param>
/// <param name="ProjectPath">專案檔（.csproj、package.json）的完整路徑</param>
/// <param name="RelativePath">相對於偵測的工作資料夾（顯示用）</param>
/// <param name="Detail">顯示用的說明（「WinForms · net10.0-windows」「Electron · npm run start」）</param>
/// <param name="Framework">要以 -f 指定的目標架構（多目標的專案）；只有一個時 null</param>
/// <param name="Script">npm 的指令碼名稱（Electron）</param>
internal sealed record LaunchTarget(string Id, LaunchKind Kind, string Name, string ProjectPath, string RelativePath, string Detail,
    string? Framework = null, string? Script = null)
{
    public string Directory => Path.GetDirectoryName(ProjectPath) ?? ProjectPath;
}

/// <summary>要執行的程序：執行檔（PATH 上的名稱或完整路徑）、參數（逐一傳，不經命令列字串）、工作資料夾</summary>
internal sealed record LaunchCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <param name="ExitCode">程序的結束碼（停止、開不起來是 null）</param>
/// <param name="Text">給人看的說明（建置失敗是第一個錯誤）</param>
internal sealed record LaunchEnd(LaunchEndKind Kind, int? ExitCode, string Text, DateTime At);

/// <summary>一個目標的執行狀態（給 <see cref="LaunchRules.Render"/>）</summary>
/// <param name="RunMode">這一次的執行模式（閒置是 null）</param>
internal sealed record LaunchRunSnapshot(LaunchPhase Phase, DateTime? Since, LaunchEnd? Last, long LogSeq, long LogEpoch, LaunchMode? RunMode = null);

/// <summary>
/// 執行列（web-protocol 的 launchStatus）：在對話的工作資料夾找可以「建置並執行」的視窗程式專案、決定按鈕做什麼、組出建置與執行的指令、整理輸出。
/// 只認得視窗程式（主控台程式、網站、測試專案不列）：.NET 的 WinExe（或 Exe 加上 UseWindowsForms／UseWPF；MAUI 不列——要封裝才能執行），
/// 與 package.json 有 electron 而且有 start（其次 dev）指令碼的 Electron 專案
/// </summary>
internal static partial class LaunchRules
{
    /// <summary>往下找幾層資料夾（工作資料夾本身是第 0 層；src\App\App.csproj 在第 2 層）</summary>
    public const int MaxDepth = 3;

    /// <summary>最多看幾個資料夾（很大的資料夾不要每次都掃很久）</summary>
    public const int MaxDirectories = 2000;

    /// <summary>最多列幾個目標</summary>
    public const int MaxTargets = 20;

    /// <summary>專案檔超過這個大小不讀</summary>
    public const long MaxProjectFileBytes = 1024 * 1024;

    /// <summary>每個目標留幾行輸出（舊的丟掉）</summary>
    public const int LogCapacity = 2000;

    /// <summary>一行最多幾個字（超過的截掉）</summary>
    public const int MaxLineLength = 2000;

    /// <summary>有新的輸出時，多久才送一次狀態（logSeq）給網頁</summary>
    public static readonly TimeSpan OutputPostDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>主程序結束後最多再等輸出讀完多久（殘留的子程序可能還握著管線）</summary>
    public static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>開分頁時，偵測結果超過這麼久才重新偵測</summary>
    public static readonly TimeSpan OpenRescanAge = TimeSpan.FromSeconds(30);

    /// <summary>這一輪結束時，偵測結果超過這麼久才重新偵測（連續好幾輪很快結束時不要每輪都掃）</summary>
    public static readonly TimeSpan ChangedRescanAge = TimeSpan.FromSeconds(3);

    /// <summary>畫面上看得到的對話多久重新偵測一次（專案可能是在 app 外面建立的）</summary>
    public static readonly TimeSpan PeriodicRescanAge = TimeSpan.FromSeconds(60);

    /// <summary>不往下找的資料夾（建置輸出、套件、版本控制、編輯器設定）；名稱以點開頭的也不找</summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "dist", "out", "build", "target", "artifacts", "TestResults", "__pycache__",
        "venv", "Library", "Temp", "Logs",
    };

    private static readonly string[] DotNetProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>Electron 依序認的 npm 指令碼</summary>
    private static readonly string[] ElectronScripts = ["start", "dev"];

    // ───── 偵測 ─────

    /// <summary>這個資料夾要不要往下找（工作資料夾本身一律找，不經這裡）</summary>
    public static bool ShouldDescend(string name, FileAttributes attributes)
        => !string.IsNullOrEmpty(name)
           && name[0] != '.'
           && !SkippedDirectories.Contains(name)
           // 重新解析點（junction、符號連結）不跟：可能繞回上層或指到別的磁碟；隱藏與系統資料夾也不找
           && (attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0;

    /// <summary>要讀的檔案：.NET 專案檔或 package.json</summary>
    public static bool IsCandidateFile(string name)
        => IsDotNetProjectFile(name) || string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase);

    public static bool IsDotNetProjectFile(string name)
        => DotNetProjectExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// .NET 專案檔是不是可以執行的視窗程式；不是（主控台、程式庫、網站、測試、MAUI、舊式非 SDK 專案、XML 壞掉）回 null。
    /// 只看沒有 Condition 的屬性（Directory.Build.props 等其他檔案設的不算）
    /// </summary>
    public static LaunchTarget? FromDotNetProject(string projectPath, string relativePath, string xml)
    {
        XElement? root;
        try
        {
            // 不處理 DTD、不解析外部實體（專案檔是工作資料夾裡的任意檔案）
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return null;
        }
        if (root == null || root.Name.LocalName != "Project")
            return null;
        // 舊式（非 SDK）專案不能 dotnet run
        var sdk = (string?)root.Attribute("Sdk");
        var sdkStyle = sdk != null
                       || root.Elements().Any(e => e.Name.LocalName == "Sdk")
                       || root.Elements().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") != null);
        if (!sdkStyle)
            return null;

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && e.Attribute("Condition") == null))
        {
            foreach (var prop in group.Elements().Where(e => e.Attribute("Condition") == null))
                props[prop.Name.LocalName] = prop.Value.Trim();
        }
        var packages = root.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => ((string?)e.Attribute("Include") ?? string.Empty).Trim())
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? Prop(string name) => props.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
        bool Flag(string name) => string.Equals(Prop(name), "true", StringComparison.OrdinalIgnoreCase);

        if (Flag("IsTestProject") || packages.Contains("Microsoft.NET.Test.Sdk") || Flag("UseMaui"))
            return null;
        var winForms = Flag("UseWindowsForms");
        var wpf = Flag("UseWPF");
        var outputType = Prop("OutputType");
        var windowed = string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)
                       || (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) && (winForms || wpf));
        if (!windowed)
            return null;

        // 多目標（TargetFrameworks）要以 -f 指定一個：有 Windows 的優先，其次第一個不是 netstandard 的；含 MSBuild 屬性參照的不猜
        string? framework = null;
        string? shownFramework = Prop("TargetFramework");
        if (Prop("TargetFrameworks") is { } list && !list.Contains("$(", StringComparison.Ordinal))
        {
            var all = list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            framework = all.FirstOrDefault(f => f.Contains("-windows", StringComparison.OrdinalIgnoreCase))
                        ?? all.FirstOrDefault(f => !f.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase));
            shownFramework = framework;
        }
        if (shownFramework != null && shownFramework.Contains("$(", StringComparison.Ordinal))
            shownFramework = null;

        var ui = wpf ? "WPF"
            : winForms ? "WinForms"
            : packages.Any(p => p.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)) ? "Avalonia"
            : "WinExe";
        var name = Prop("AssemblyName") is { } assembly && !assembly.Contains("$(", StringComparison.Ordinal) && assembly.Length <= 80
            ? assembly
            : Path.GetFileNameWithoutExtension(projectPath);
        var detail = shownFramework != null ? $"{ui} · {shownFramework}" : ui;
        return new LaunchTarget(TargetId(projectPath), LaunchKind.DotNet, name, projectPath, relativePath, detail, Framework: framework);
    }

    /// <summary>package.json 是不是 Electron 的桌面程式（dependencies 或 devDependencies 有 electron，而且有 start 或 dev 指令碼）；不是回 null</summary>
    public static LaunchTarget? FromPackageJson(string path, string relativePath, string json)
    {
        JObject package;
        try
        {
            package = JObject.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        bool HasDependency(string group) => package[group] is JObject deps && deps.ContainsKey("electron");
        if (!HasDependency("dependencies") && !HasDependency("devDependencies"))
            return null;
        var scripts = package["scripts"] as JObject;
        var script = ElectronScripts.FirstOrDefault(s => scripts?[s] is JValue { Type: JTokenType.String } v && !string.IsNullOrWhiteSpace((string?)v));
        if (script == null)
            return null;
        var folderName = Path.GetFileName(Path.GetDirectoryName(path) ?? path);
        var name = package["name"] is JValue { Type: JTokenType.String } n && (string?)n is { Length: > 0 and <= 80 } packageName ? packageName : folderName;
        return new LaunchTarget(TargetId(path), LaunchKind.Electron, name, path, relativePath, $"Electron · npm run {script}", Script: script);
    }

    /// <summary>目標的鍵：專案檔完整路徑（不分大小寫）的 SHA-256 前 12 碼</summary>
    public static string TargetId(string projectPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectPath.ToUpperInvariant()));
        return "t" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>排序與上限：淺的在前（工作資料夾本身的專案最前面），同一層依相對路徑</summary>
    public static IReadOnlyList<LaunchTarget> Order(IEnumerable<LaunchTarget> targets)
        => targets
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .OrderBy(t => t.RelativePath.Count(ch => ch is '\\' or '/'))
            .ThenBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTargets)
            .ToList();

    /// <summary>選哪個目標：之前選的還在就留著，否則第一個；沒有目標是 null</summary>
    public static string? Select(IReadOnlyList<LaunchTarget> targets, string? previous)
        => previous != null && targets.Any(t => t.Id == previous) ? previous : targets.FirstOrDefault()?.Id;

    public static bool ShouldScan(LaunchScanTrigger trigger, DateTime? scannedAt, DateTime now)
    {
        if (scannedAt is not { } at)
            return true;
        var age = now - at;
        return trigger switch
        {
            LaunchScanTrigger.User => true,
            LaunchScanTrigger.Open => age >= OpenRescanAge,
            LaunchScanTrigger.Changed => age >= ChangedRescanAge,
            LaunchScanTrigger.Periodic => age >= PeriodicRescanAge,
            _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
        };
    }

    // ───── 按鈕 ─────

    /// <summary>網頁的 action（start、stop、pause、resume、restart）在目前的階段要做什麼；不合時宜（例如執行中按啟動）是 None</summary>
    public static LaunchStep Plan(LaunchPhase phase, string? action) => (action, phase) switch
    {
        ("start", LaunchPhase.Idle) => LaunchStep.Start,
        // 暫停中按啟動（F5）＝繼續，和 VS Code 一樣
        ("start", LaunchPhase.Paused) => LaunchStep.Resume,
        ("stop", LaunchPhase.Building or LaunchPhase.Running or LaunchPhase.Paused) => LaunchStep.Stop,
        ("pause", LaunchPhase.Running) => LaunchStep.Pause,
        ("resume", LaunchPhase.Paused) => LaunchStep.Resume,
        ("restart", LaunchPhase.Idle) => LaunchStep.Start,
        // 停止中按重啟：等它結束後照樣重新建置與執行
        ("restart", LaunchPhase.Building or LaunchPhase.Running or LaunchPhase.Paused or LaunchPhase.Stopping) => LaunchStep.Restart,
        _ => LaunchStep.None,
    };

    // ───── 指令 ─────

    /// <summary>建置的指令（組態照執行模式：Debug 或 Release）；不必建置（Electron 由 npm 指令碼自己處理）回 null</summary>
    public static LaunchCommand? BuildCommand(LaunchTarget target, LaunchMode mode) => target.Kind switch
    {
        // -tl:off：不用終端機記錄器（輸出是一行一行的文字）；-nodeReuse:false：建置完 MSBuild 節點跟著結束，不留著鎖檔
        LaunchKind.DotNet => new LaunchCommand("dotnet",
            ["build", target.ProjectPath, "-c", Configuration(mode), "-nologo", "-tl:off", "-nodeReuse:false", .. FrameworkArgs(target)], target.Directory),
        LaunchKind.Electron => null,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, null),
    };

    /// <summary>
    /// 執行的指令。.NET 用 dotnet run --no-build（照專案的 launchSettings.json、RunWorkingDirectory 與多目標，和在終端機執行一樣；組態和建置相同），
    /// 工作資料夾是專案資料夾；Electron 經 cmd 執行 npm run（npm 是 .cmd；兩種模式的指令相同，差別只在和 app 綁不綁在一起）。<paramref name="comSpec"/> 是 %ComSpec%
    /// </summary>
    public static LaunchCommand RunCommand(LaunchTarget target, string? comSpec, LaunchMode mode) => target.Kind switch
    {
        LaunchKind.DotNet => new LaunchCommand("dotnet",
            ["run", "--project", target.ProjectPath, "-c", Configuration(mode), "--no-build", .. FrameworkArgs(target)], target.Directory),
        // 指令碼名稱只會是 ElectronScripts 裡的固定字串，不會帶進專案檔裡的任何文字
        LaunchKind.Electron => new LaunchCommand(string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec,
            ["/d", "/s", "/c", "npm run " + (ElectronScripts.Contains(target.Script) ? target.Script : ElectronScripts[0])], target.Directory),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, null),
    };

    /// <summary>輸出開頭的那一行（「> dotnet build C:\…\App.csproj -c Debug …」）：含空白的參數加引號，只給人看</summary>
    public static string DisplayCommand(LaunchCommand command)
        => "> " + string.Join(' ', new[] { command.FileName }.Concat(command.Arguments).Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));

    /// <summary>path 是不是 folder 本身或在它底下（不分大小寫；folder 是 FolderKey 的形狀）</summary>
    public static bool IsInside(string path, string folder)
    {
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = folder.EndsWith('\\') ? folder : folder + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] FrameworkArgs(LaunchTarget target) => target.Framework is { } f ? ["-f", f] : [];

    /// <summary>dotnet 的 -c（組態）</summary>
    public static string Configuration(LaunchMode mode) => mode switch
    {
        LaunchMode.Debug => "Debug",
        LaunchMode.Release => "Release",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    // ───── 輸出 ─────

    /// <summary>一行輸出：先試嚴格的 UTF-8（dotnet、npm），不是 UTF-8 就用主控台的字碼頁（程式自己的 Console 輸出是 OEM 字碼頁，例如 950）</summary>
    public static string DecodeLine(ReadOnlySpan<byte> bytes, Encoding fallback)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return fallback.GetString(bytes);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>去掉 ANSI 色碼、行尾的 CR 與其他控制字元（tab 換成空白），太長的截掉</summary>
    public static string CleanLine(string line)
    {
        var text = AnsiPattern().Replace(line, string.Empty);
        if (text.Length > 0 && text[0] == (char)0xFEFF)
            text = text[1..];
        var sb = new StringBuilder(Math.Min(text.Length, MaxLineLength + 1));
        foreach (var ch in text)
        {
            if (sb.Length >= MaxLineLength)
            {
                sb.Append('…');
                break;
            }
            if (ch == '\t')
                sb.Append("    ");
            else if (!char.IsControl(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-_])")]
    private static partial Regex AnsiPattern();

    /// <summary>MSBuild 的錯誤行：「檔案(行,欄): error 代碼: 訊息 [專案]」</summary>
    [GeneratedRegex(@"^\s*(?<file>[^\s].*?)\((?<pos>\d+(?:,\d+)*)\)\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex MsBuildError();

    /// <summary>其他錯誤行（沒有檔案位置的 MSBuild 錯誤、NETSDK、NU 開頭的還原錯誤）</summary>
    [GeneratedRegex(@"(?:^|:\s*)error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex GenericError();

    /// <summary>建置失敗時的說明：第一個錯誤（檔名只留名稱）；找不到錯誤行是 null</summary>
    public static string? BuildErrorSummary(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (MsBuildError().Match(line) is { Success: true } m)
                return $"{Path.GetFileName(m.Groups["file"].Value.Trim())}({m.Groups["pos"].Value}): {m.Groups["code"].Value} {m.Groups["msg"].Value}".TrimEnd();
        }
        foreach (var line in lines)
        {
            if (GenericError().Match(line) is { Success: true } m)
                return $"{m.Groups["code"].Value} {m.Groups["msg"].Value}".TrimEnd();
        }
        return null;
    }

    /// <summary>建置輸出裡的錯誤行（去掉重複：MSBuild 結尾的摘要會把每個錯誤再印一次），最多 <paramref name="max"/> 行</summary>
    public static IReadOnlyList<string> BuildErrorLines(IEnumerable<string> lines, int max = 20)
        => lines.Where(l => MsBuildError().IsMatch(l) || GenericError().IsMatch(l))
            .Select(l => l.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();

    /// <summary>結束碼的寫法：一般的用十進位；負數或很大的（例如 .NET 未處理例外的 0xE0434352）用十六進位</summary>
    public static string FormatExitCode(int code)
        => code is >= 0 and <= 0xFFFF ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0x" + ((uint)code).ToString("X8");

    public static LaunchEnd Exited(int code, DateTime at)
        => new(LaunchEndKind.Exited, code, code == 0 ? "已結束" : $"已結束（結束碼 {FormatExitCode(code)}）", at);

    public static LaunchEnd Stopped(DateTime at) => new(LaunchEndKind.Stopped, null, "已停止", at);

    public static LaunchEnd BuildFailed(int code, IEnumerable<string> lines, DateTime at)
        => new(LaunchEndKind.BuildFailed, code, BuildErrorSummary(lines) ?? $"建置失敗（結束碼 {FormatExitCode(code)}）", at);

    public static LaunchEnd StartFailed(string message, DateTime at) => new(LaunchEndKind.StartFailed, null, "無法啟動：" + message, at);

    // ───── 給網頁的 LaunchView ─────

    public static string KindName(LaunchKind kind) => kind switch
    {
        LaunchKind.DotNet => "dotnet",
        LaunchKind.Electron => "electron",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string ModeName(LaunchMode mode) => mode switch
    {
        LaunchMode.Debug => "debug",
        LaunchMode.Release => "release",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>網頁（launchSelect 的 mode）與 prefs.json 的模式名稱；不認得回 null</summary>
    public static LaunchMode? ParseMode(string? name) => name switch
    {
        "debug" => LaunchMode.Debug,
        "release" => LaunchMode.Release,
        _ => null,
    };

    public static string PhaseName(LaunchPhase phase) => phase switch
    {
        LaunchPhase.Idle => "idle",
        LaunchPhase.Building => "building",
        LaunchPhase.Running => "running",
        LaunchPhase.Paused => "paused",
        LaunchPhase.Stopping => "stopping",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    public static string EndKindName(LaunchEndKind kind) => kind switch
    {
        LaunchEndKind.Exited => "exited",
        LaunchEndKind.Stopped => "stopped",
        LaunchEndKind.BuildFailed => "buildFailed",
        LaunchEndKind.StartFailed => "startFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// launchStatus 的 launch：偵測到的目標（各自的階段）、選的那個的狀態與執行模式。沒有目標回 null（網頁藏起執行列）。
    /// <paramref name="runOf"/> 回目標的執行狀態（沒執行過是 null）、<paramref name="modeOf"/> 回目標選的執行模式；時間是 UTC
    /// </summary>
    public static JObject? Render(string folder, IReadOnlyList<LaunchTarget> targets, string? selected, Func<string, LaunchRunSnapshot?> runOf,
        Func<string, LaunchMode> modeOf)
    {
        if (targets.Count == 0)
            return null;
        var chosen = targets.FirstOrDefault(t => t.Id == selected) ?? targets[0];
        var run = runOf(chosen.Id);
        return new JObject
        {
            ["folder"] = folder,
            ["selected"] = chosen.Id,
            ["targets"] = new JArray(targets.Select(t => new JObject
            {
                ["id"] = t.Id,
                ["name"] = t.Name,
                ["kind"] = KindName(t.Kind),
                ["path"] = t.RelativePath,
                ["detail"] = t.Detail,
                ["phase"] = PhaseName(runOf(t.Id)?.Phase ?? LaunchPhase.Idle),
            })),
            ["phase"] = PhaseName(run?.Phase ?? LaunchPhase.Idle),
            ["mode"] = ModeName(modeOf(chosen.Id)),
            ["runMode"] = run?.Phase is { } p && p != LaunchPhase.Idle && run.RunMode is { } runMode ? ModeName(runMode) : JValue.CreateNull(),
            ["since"] = run?.Since is { } since ? ProtocolUnixMs(since) : null,
            ["last"] = run?.Last is { } last
                ? new JObject
                {
                    ["kind"] = EndKindName(last.Kind),
                    ["code"] = last.ExitCode,
                    ["text"] = last.Text,
                    ["at"] = ProtocolUnixMs(last.At),
                }
                : null,
            ["logSeq"] = run?.LogSeq ?? 0,
            ["logEpoch"] = run?.LogEpoch ?? 0,
        };
    }

    private static long ProtocolUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>launchLog 的回覆：after 之後的行（同一個 epoch、還在緩衝區裡），否則整份（reset）</summary>
    public static JObject RenderLog(string conversationId, string targetId, LaunchLogSlice slice) => new()
    {
        ["type"] = "launchLog",
        ["conversationId"] = conversationId,
        ["targetId"] = targetId,
        ["epoch"] = slice.Epoch,
        ["reset"] = slice.Reset,
        ["seq"] = slice.Seq,
        ["lines"] = new JArray(slice.Lines),
    };
}
