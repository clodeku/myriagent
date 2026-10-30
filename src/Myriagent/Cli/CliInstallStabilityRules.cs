using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Cli;

/// <summary>另一個程序的基本資料（判斷鎖定檔的持有者還在不在、是不是 Claude；PID 會被重複使用，所以連建立時間一起看）</summary>
internal readonly record struct CliProcessInfo(int Id, DateTime StartTimeUtc, string? ImagePath);

/// <summary>命令列含 npm-cli.js 的 node 程序（npm 正在執行；是不是在改全域套件由 IsGlobalNpmInstall 判斷）</summary>
internal sealed record CliNpmProcess(int ProcessId, string CommandLine);

/// <summary>Claude 換檔與放入新版時，暫存檔名帶的資訊種類</summary>
internal enum CliUpdaterMarkKind
{
    /// <summary>claude.exe.new.&lt;pid&gt;.&lt;ms&gt;.&lt;hex&gt;：複製好、等著改名成 claude.exe 的新版</summary>
    StagedExecutable,

    /// <summary>claude.exe.old.&lt;ms&gt;.&lt;pid&gt;：被換下來的舊版（執行中的舊程序還在用，刪不掉）；更舊的版本只有 .old.&lt;ms&gt;</summary>
    BackupExecutable,

    /// <summary>versions\&lt;版本&gt;.tmp.&lt;pid&gt;.&lt;ms&gt;.&lt;次數&gt;：正在原子搬進 versions 的新版</summary>
    StagedVersion,
}

/// <summary>暫存檔名裡的更新者 PID 與時間戳（PID 是執行更新的那個程序：claude update、互動式 claude 的背景更新）</summary>
internal readonly record struct CliUpdaterMark(CliUpdaterMarkKind Kind, int? ProcessId, DateTime? StampUtc);

/// <summary>
/// Claude 原生安裝的版本鎖（&lt;XDG_STATE_HOME 或 ~\.local\state&gt;\claude\locks\&lt;版本&gt;.lock）：安裝期間存在，
/// 內容是 JSON {pid, version, execPath, acquiredAt}（2.1.296 實測的格式）；IsDirectory＝舊式（proper-lockfile）的資料夾鎖
/// </summary>
internal sealed record ClaudeInstallLock(string Name, bool IsDirectory, int? ProcessId, string? Version, string? ExecutablePath, DateTime? AcquiredAtUtc, DateTime LastWriteUtc);

/// <summary>檢查時看到的鎖與它的持有者（持有者已結束或打不開為 null）</summary>
internal sealed record ClaudeLockObservation(ClaudeInstallLock Lock, CliProcessInfo? Holder);

/// <summary>便宜的指紋（60 秒輪詢只比這個）：原生執行檔的身分欄位＋不必啟動程序就讀得到的版本來源</summary>
internal readonly record struct CliInstallFingerprint(CliFileStamp? Executable, string? FileVersion, string? PackageVersion, string? VendorVersion);

/// <summary>
/// 安裝在某個時間點的樣子：不啟動程序就讀得到的部分（CliInstallInspector.Inspect 讀取，CliInstallStabilityRules.Evaluate 判定）。
/// Kind 為 null＝找不到這種 CLI 的安裝
/// </summary>
internal sealed record CliInstallSnapshot(AgentProvider Provider, CliInstallKind? Kind, string? NativeExecutable, DateTime InspectedAtUtc)
{
    /// <summary>原生執行檔的身分欄位；不存在或讀不到為 null</summary>
    public CliFileStamp? Executable { get; init; }

    /// <summary>執行檔的檔案版本資訊（x.y.z；只有 claude.exe 有，codex.exe 沒有版本資源）</summary>
    public string? FileVersion { get; init; }

    /// <summary>npm：套件的 package.json 版本</summary>
    public string? PackageVersion { get; init; }

    /// <summary>npm 版 Codex：vendor\…\codex-package.json 的版本（沒有這個檔的舊配置是平台套件 package.json 去掉平台字尾）</summary>
    public string? VendorVersion { get; init; }

    /// <summary>npm：套件的 package.json 不在（npm 把舊版退役到新版解壓完成之間、或安裝不完整）</summary>
    public bool PackageMissing { get; init; }

    /// <summary>npm：被退役的套件資料夾（.codex-&lt;雜湊&gt;、.claude-code-&lt;雜湊&gt;；舊程序還在執行時刪不掉，會留成殘留）</summary>
    public IReadOnlyList<string> RetiredPackages { get; init; } = Array.Empty<string>();

    /// <summary>Claude 原生：bin 資料夾裡 claude.exe.* 的檔名（.new 暫存檔、.old 備份）</summary>
    public IReadOnlyList<string> ExecutableEntries { get; init; } = Array.Empty<string>();

    /// <summary>Claude 原生：versions 資料夾裡的 .tmp 暫存檔名</summary>
    public IReadOnlyList<string> VersionEntries { get; init; } = Array.Empty<string>();

    /// <summary>Claude 原生：locks 資料夾裡的鎖與持有者</summary>
    public IReadOnlyList<ClaudeLockObservation> Locks { get; init; } = Array.Empty<ClaudeLockObservation>();

    /// <summary>npm：檢查當下在跑的 npm（node npm-cli.js）程序</summary>
    public IReadOnlyList<CliNpmProcess> NpmProcesses { get; init; } = Array.Empty<CliNpmProcess>();

    public bool Installed => Kind != null;

    public bool ExecutableExists => Executable != null;

    public CliInstallFingerprint Fingerprint => new(Executable, FileVersion, PackageVersion, VendorVersion);

    public static CliInstallSnapshot NotInstalled(AgentProvider provider, DateTime inspectedAtUtc) => new(provider, null, null, inspectedAtUtc);
}

/// <summary>安裝的狀況（CliInstallStabilityRules.Evaluate 的結論）</summary>
internal enum CliInstallCondition
{
    /// <summary>正在變動（版本鎖、換檔暫存檔、npm 正在安裝）：暫緩這種 CLI 的新啟動，等穩定</summary>
    Changing,

    /// <summary>已穩定、執行檔存在、所有讀得到的版本來源一致：可以讀身分宣告換版</summary>
    Stable,

    /// <summary>安裝損壞（版本來源互相矛盾、缺必要的檔）：不重開任何對話，Detail 是給使用者看的說明</summary>
    Broken,

    /// <summary>這種 CLI 已不在（執行檔不存在、也沒有在變動）</summary>
    Missing,
}

/// <summary>安裝判定：狀況、穩定時的版本（x.y.z[-pre]）、給使用者看的說明、從檢查看到的更新者 PID</summary>
internal sealed record CliInstallVerdict(CliInstallCondition Condition, string? Version, string? Detail, int? UpdaterProcessId = null)
{
    public bool IsUsable => Condition == CliInstallCondition.Stable;
}

/// <summary>監看表（設計 7.2.2）的區域種類</summary>
internal enum CliWatchArea
{
    /// <summary>Claude 原生的 %USERPROFILE%\.local\bin（篩選 claude.exe*）：.new 建立 → 舊檔改名 .old → .new 改名成 claude.exe</summary>
    ClaudeBin,

    /// <summary>Claude 原生的 versions（新版本落地、.tmp 暫存）</summary>
    ClaudeVersions,

    /// <summary>Claude 原生的 locks（*.lock：正在安裝，JSON 有 pid）</summary>
    ClaudeLocks,

    /// <summary>npm 的 node_modules\@openai、node_modules\@anthropic-ai（含子資料夾）：retire 改名、大量建立、刪 retire</summary>
    NpmPackages,

    /// <summary>npm 前綴的 codex／codex.cmd／codex.ps1（claude 同理）：被重寫＝npm 收尾</summary>
    NpmShims,

    /// <summary>app 自管 Codex 的上層資料夾（只處理 codex 與 codex.old-*：整個資料夾改名換版）</summary>
    ManagedRoot,

    /// <summary>其他安裝方式（winget、手動放在 PATH）：執行檔所在的資料夾</summary>
    ExecutableDirectory,
}

/// <summary>
/// 一個要監看的資料夾：一律是不會被改名的穩定上層（不直接監看會被 retire 改名的套件資料夾本身——watcher 會跟著跑到 retire 資料夾、刪除時丟 Error）。
/// Name：ClaudeBin／ExecutableDirectory 是執行檔名稱、NpmPackages 是套件名稱（codex、claude-code）、NpmShims 是殼的名稱（codex、claude）、
/// ManagedRoot 是受管 Codex 的資料夾名稱（codex）
/// </summary>
internal sealed record CliWatchTarget(CliWatchArea Area, string Directory, string Filter, bool IncludeSubdirectories, string Name);

/// <summary>和安裝有關的檔案事件：檔名帶的更新者 PID（沒有為 null）與記錄用的說明</summary>
internal readonly record struct CliWatchEvent(int? UpdaterProcessId, string Description);

/// <summary>
/// CLI 安裝變化的純函式規則（設計 7.2.2～7.2.4）：去抖常數、監看表與事件分類、從暫存檔名與鎖定檔取更新者 PID、
/// 「安裝已穩定」與「三方版本不一致＝安裝損壞」的判定，以及給更新協調（WP1.5）把判定轉成 CliVersionChange 的對應。
/// <para>
/// 去抖（CliWatcher 照這些常數）：每個相關事件都重新計時，安靜 3 秒（Claude、受管 Codex、其他）／5 秒（npm）後檢查安裝；
/// 還在變動（鎖、換檔暫存檔、npm 在跑）就每秒再查、不另發訊號，所以一次更新的各階段（下載、放版本、換檔、收尾）只宣告一次。
/// 「30 秒內再有事件就重新計時」：判定是缺檔、混版、不見了（不能用的結論）時，要 30 秒沒有新事件才宣告——
/// npm 下載新版的空檔、連續兩次更新之間不會被當成損壞或移除。等超過 15 分鐘仍在變動就照當時的樣子宣告，免得一直暫緩啟動。
/// </para>
/// </summary>
internal static partial class CliInstallStabilityRules
{
    /// <summary>Claude（原生、其他）與受管 Codex 的去抖：換檔的兩次改名只差約 1 ms，3 秒內沒有新事件就檢查</summary>
    public static readonly TimeSpan ClaudeQuietPeriod = TimeSpan.FromSeconds(3);

    /// <summary>npm 的去抖：retire、解壓大量檔案、刪 retire、重寫殼之間有空檔</summary>
    public static readonly TimeSpan NpmQuietPeriod = TimeSpan.FromSeconds(5);

    /// <summary>判定是不能用（損壞、不見了）時，要這麼久沒有新事件才宣告（30 秒內再有事件就重新計時）</summary>
    public static readonly TimeSpan UnusableQuietPeriod = TimeSpan.FromSeconds(30);

    /// <summary>檢查時還在變動，多久後再查一次</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>備援輪詢的間隔（不啟動程序：Claude 讀 FileVersionInfo＋FileId；Codex 讀 package.json＋vendor exe 的 FileId）</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>一波變動最多等多久：超過就照當時的樣子宣告（不讓卡住的鎖或 npm 讓新啟動一直暫緩）</summary>
    public static readonly TimeSpan MaxSettleWait = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 一波宣告穩定後多久內的檔案事件先查安裝再決定：慢機器上晚到的事件（含緩衝溢位後的全量檢查）不另開一波，
    /// 安裝真的變了或正在變照常馬上宣告
    /// </summary>
    public static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(10);

    /// <summary>.new／.tmp 暫存檔的時間戳在多久內才算「正在換檔」（換檔失敗留下的舊暫存檔由 claude -p 一小時後清掉）</summary>
    public static readonly TimeSpan StagingFreshness = TimeSpan.FromMinutes(10);

    /// <summary>舊式資料夾鎖在建立多久內才算持有中（Claude 自己清理時把它們當成過期）</summary>
    public static readonly TimeSpan LegacyLockFreshness = TimeSpan.FromMinutes(10);

    /// <summary>鎖的持有者建立時間晚於取得鎖的時間超過這個值＝PID 已被別的程序重複使用</summary>
    private static readonly TimeSpan LockStartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>npm 會改動全域套件的指令（含 npm 的縮寫與拼錯的別名）</summary>
    private static readonly HashSet<string> NpmMutatingCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "install", "i", "in", "ins", "inst", "insta", "instal", "isnt", "isnta", "isntal", "isntall", "add",
        "update", "up", "upgrade", "udpate", "uninstall", "unlink", "un", "remove", "rm", "r",
        "ci", "clean-install", "install-clean", "ic", "isntall-clean", "link", "ln", "rebuild", "rb", "dedupe", "ddp", "prune",
    };

    /// <summary>這種安裝方式的去抖時間</summary>
    public static TimeSpan QuietPeriod(CliInstallKind? kind) => kind == CliInstallKind.Npm ? NpmQuietPeriod : ClaudeQuietPeriod;

    /// <summary>給使用者看的 CLI 名稱</summary>
    public static string DisplayName(AgentProvider provider) => AgentProviders.ProductName(provider);

    // ───────────────────────────── 更新者 PID（7.2.4） ─────────────────────────────

    /// <summary>
    /// bin 資料夾裡執行檔的暫存檔名 → 更新者（2.1.296 的檔名規則：「&lt;exe&gt;.new.&lt;pid&gt;.&lt;ms&gt;.&lt;hex&gt;」「&lt;exe&gt;.old.&lt;ms&gt;.&lt;pid&gt;」，
    /// 更舊的版本只有「&lt;exe&gt;.old.&lt;ms&gt;」）；不是這種檔名回 null
    /// </summary>
    public static CliUpdaterMark? ParseExecutableMark(string executableName, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith(executableName + ".", StringComparison.OrdinalIgnoreCase))
            return null;
        var m = ExecutableMarkPattern().Match(fileName[(executableName.Length + 1)..]);
        if (!m.Success)
            return null;
        return m.Groups["oldMs"].Success
            ? new CliUpdaterMark(CliUpdaterMarkKind.BackupExecutable, ParseProcessId(m.Groups["oldPid"]), ParseUnixMilliseconds(m.Groups["oldMs"].Value))
            : new CliUpdaterMark(CliUpdaterMarkKind.StagedExecutable, ParseProcessId(m.Groups["newPid"]), ParseUnixMilliseconds(m.Groups["newMs"].Value));
    }

    /// <summary>versions 資料夾的暫存檔名（「2.1.297.tmp.48780.1791575085123.1」）→ 更新者；不是暫存檔回 null</summary>
    public static CliUpdaterMark? ParseVersionMark(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;
        var m = VersionMarkPattern().Match(fileName);
        return m.Success
            ? new CliUpdaterMark(CliUpdaterMarkKind.StagedVersion, ParseProcessId(m.Groups["pid"]), ParseUnixMilliseconds(m.Groups["ms"].Value))
            : null;
    }

    /// <summary>
    /// 鎖定檔的內容 → 鎖（JSON {pid, version, execPath, acquiredAt}；資料夾鎖、讀不到或格式不符時欄位為 null）。
    /// 2.1.296 以 JSON.stringify(…, null, 2) 寫入：{"pid": 48780, "version": "2.1.296", "execPath": "C:\\…\\claude.exe", "acquiredAt": 1791575085123}
    /// </summary>
    public static ClaudeInstallLock ParseLock(string name, bool isDirectory, string? json, DateTime lastWriteUtc)
    {
        var empty = new ClaudeInstallLock(name, isDirectory, null, null, null, null, ToUtc(lastWriteUtc));
        if (isDirectory || string.IsNullOrWhiteSpace(json))
            return empty;
        try
        {
            if (JToken.Parse(json) is not JObject o)
                return empty;
            int? pid = o["pid"] is JValue { Type: JTokenType.Integer or JTokenType.Float } p && p.Value<double>() is var d
                       && d >= 1 && d <= int.MaxValue && Math.Floor(d) == d
                ? (int)d
                : null;
            var version = o["version"] is JValue { Type: JTokenType.String } v ? (string?)v : null;
            var execPath = o["execPath"] is JValue { Type: JTokenType.String } e ? (string?)e : null;
            var acquired = o["acquiredAt"] is JValue { Type: JTokenType.Integer or JTokenType.Float } a ? ParseUnixMilliseconds(a.Value<double>()) : null;
            return empty with { ProcessId = pid, Version = version, ExecutablePath = execPath, AcquiredAtUtc = acquired };
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return empty;
        }
    }

    /// <summary>
    /// 鎖是不是還被持有（＝Claude 正在安裝），照 2.1.296 自己的判斷：內容要有 pid、version、execPath；持有者還在執行；
    /// 持有者的執行檔路徑查得到時要含「claude」或等於 execPath（否則「is running but does not appear to be Claude」當成過期）。
    /// 另外比 Claude 嚴格一點：持有者在取得鎖之後才啟動＝PID 被重複使用，當成過期。舊式資料夾鎖只在剛建立的 10 分鐘內算持有
    /// </summary>
    public static bool IsLockHeld(ClaudeInstallLock installLock, CliProcessInfo? holder, DateTime nowUtc)
    {
        if (installLock.IsDirectory)
            return ToUtc(nowUtc) - installLock.LastWriteUtc < LegacyLockFreshness;
        if (installLock.ProcessId is not int pid || pid <= 1 || string.IsNullOrEmpty(installLock.Version) || string.IsNullOrEmpty(installLock.ExecutablePath))
            return false;
        if (holder is not { } h || h.Id != pid)
            return false;
        if (installLock.AcquiredAtUtc is { } acquired && ToUtc(h.StartTimeUtc) > acquired + LockStartTolerance)
            return false;
        if (h.ImagePath is { } image
            && image.IndexOf("claude", StringComparison.OrdinalIgnoreCase) < 0
            && image.IndexOf(installLock.ExecutablePath, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    /// <summary>
    /// 這個 node 程序是不是 npm 在改全域套件（「node … npm-cli.js install -g @openai/codex@latest」、npm update -g…）：
    /// 命令列要有 npm-cli.js、全域旗標（-g、--global、--location=global）與會改動套件的指令。npm run／ls 這類長時間或唯讀的不算
    /// </summary>
    public static bool IsGlobalNpmInstall(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;
        var args = SplitCommandLine(commandLine);
        var cli = args.FindIndex(a => a.EndsWith("npm-cli.js", StringComparison.OrdinalIgnoreCase));
        if (cli < 0)
            return false;
        bool global = false;
        string? command = null;
        for (int i = cli + 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-g" || a.Equals("--global", StringComparison.OrdinalIgnoreCase) || a.Equals("--global=true", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--location=global", StringComparison.OrdinalIgnoreCase))
            {
                global = true;
                continue;
            }
            if (a.Equals("--location", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && args[i + 1].Equals("global", StringComparison.OrdinalIgnoreCase))
            {
                global = true;
                i++;
                continue;
            }
            if (a.StartsWith('-'))
                continue;
            command ??= a;
        }
        return global && command != null && NpmMutatingCommands.Contains(command);
    }

    /// <summary>Windows 命令列切成參數（CommandLineToArgvW 的規則：引號分組、引號前的反斜線成對跳脫，其他反斜線照字面）</summary>
    internal static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int count = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    count++;
                    i++;
                }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                        current.Append('"');
                    else
                        inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
                hasToken = true;
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken)
            result.Add(current.ToString());
        return result;
    }

    // ───────────────────────────── 安裝已穩定（7.2.3） ─────────────────────────────

    /// <summary>
    /// 判定安裝的狀況。versionOutput 是 --version 的輸出：監看（CliWatcher）不啟動程序，傳 null＝不比對這一項；
    /// 更新協調（WP1.5）收到 Settled 後再跑一次 --version 帶進來做完整判定（Claude：版本資訊與 --version 一致；
    /// npm 版 Codex：package.json、codex-package.json、--version 三方一致）。順序：
    /// <list type="number">
    /// <item>沒有安裝 → Missing</item>
    /// <item>有進行中的更新（持有中的版本鎖、10 分鐘內的 .new／.tmp 暫存檔、npm 正在改全域套件）→ Changing</item>
    /// <item>執行檔不在：npm 的套件還在（缺 vendor exe）或只剩退役殘留、原生安裝留下 .old（換檔沒完成）→ Broken；否則 Missing</item>
    /// <item>npm 的 package.json 不在、npm 版 Claude 的 bin\claude.exe 還是 postinstall 的佔位腳本 → Broken；
    /// 讀得到的版本來源互相矛盾（含 --version 讀不出版本）→ Broken</item>
    /// <item>其他 → Stable，Version 依序取 --version、package.json、codex-package.json、版本資訊</item>
    /// </list>
    /// 不能用的結論（Broken、Missing）可能只是 npm 下載中的空檔：CliWatcher 要 30 秒沒有新事件才宣告（UnusableQuietPeriod），
    /// 更新協調在宣告損壞前也應再查一次（npm 不一致重跑一次）
    /// </summary>
    public static CliInstallVerdict Evaluate(CliInstallSnapshot snapshot, string? versionOutput)
    {
        var name = DisplayName(snapshot.Provider);
        if (!snapshot.Installed)
            return new CliInstallVerdict(CliInstallCondition.Missing, null, $"找不到 {name}");

        if (FindActivity(snapshot) is { } activity)
            return new CliInstallVerdict(CliInstallCondition.Changing, null, activity.Detail, activity.ProcessId);

        var exeName = string.IsNullOrEmpty(snapshot.NativeExecutable) ? name : Path.GetFileName(snapshot.NativeExecutable);
        if (!snapshot.ExecutableExists)
        {
            if (snapshot.Kind == CliInstallKind.Npm && !snapshot.PackageMissing)
                return new CliInstallVerdict(CliInstallCondition.Broken, null, $"{name} 安裝不完整（找不到 {exeName}），請重新安裝");
            if (snapshot.Kind == CliInstallKind.Npm && snapshot.RetiredPackages.Count > 0)
                return new CliInstallVerdict(CliInstallCondition.Broken, null, $"{name} 安裝不完整（套件不見了，只剩 npm 退役的 {snapshot.RetiredPackages[0]}），請重新安裝");
            var backup = snapshot.ExecutableEntries.FirstOrDefault(e => ParseExecutableMark(exeName, e) is { Kind: CliUpdaterMarkKind.BackupExecutable });
            if (snapshot.Kind == CliInstallKind.Native && backup != null)
                return new CliInstallVerdict(CliInstallCondition.Broken, null, $"{name} 的執行檔不見了（換檔沒有完成，留下 {backup}），請重新安裝");
            return new CliInstallVerdict(CliInstallCondition.Missing, null, $"找不到 {name} 的執行檔（{snapshot.NativeExecutable}）");
        }

        if (snapshot.Kind == CliInstallKind.Npm && snapshot.PackageMissing)
            return new CliInstallVerdict(CliInstallCondition.Broken, null, $"{name} 安裝不完整（找不到套件的 package.json），請重新安裝");
        // npm 版 Claude 的 bin\claude.exe 由 postinstall 換成原生檔；還是約 500 位元組的佔位腳本＝postinstall 沒跑（或還沒跑完）
        if (snapshot.Kind == CliInstallKind.Npm && snapshot.Provider == AgentProvider.Claude && snapshot.Executable is { } stamp
            && exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && stamp.Size < CliLocator.NpmNativeMinBytes)
            return new CliInstallVerdict(CliInstallCondition.Broken, null, $"{name} 安裝不完整（{exeName} 還是 postinstall 沒跑時的佔位腳本），請重新安裝");

        var sources = new List<(string Label, string Version, bool FromFileVersion)>();
        if (snapshot.FileVersion != null)
            sources.Add(("版本資訊", snapshot.FileVersion, true));
        if (snapshot.PackageVersion != null)
            sources.Add(("package.json", snapshot.PackageVersion, false));
        if (snapshot.VendorVersion != null)
            sources.Add(("codex-package.json", snapshot.VendorVersion, false));
        string? reported = null;
        if (versionOutput != null)
        {
            reported = CliVersionRules.ParseVersion(versionOutput);
            if (reported == null)
                return new CliInstallVerdict(CliInstallCondition.Broken, null, $"無法取得 {name} 的版本（--version 的輸出：{FirstLine(versionOutput)}），請重新安裝");
            sources.Add(("執行檔", reported, false));
        }

        for (int i = 0; i < sources.Count; i++)
        {
            for (int j = i + 1; j < sources.Count; j++)
            {
                if (!CliVersionRules.SameRelease(sources[i].Version, sources[j].Version, coreOnly: sources[i].FromFileVersion || sources[j].FromFileVersion))
                {
                    var list = string.Join("、", sources.Select(s => $"{s.Label} {s.Version}"));
                    return new CliInstallVerdict(CliInstallCondition.Broken, null, $"{name} 安裝不一致（{list}），請修復");
                }
            }
        }

        var version = reported ?? snapshot.PackageVersion ?? snapshot.VendorVersion ?? snapshot.FileVersion;
        return new CliInstallVerdict(CliInstallCondition.Stable, version, version != null ? $"{name} {version}" : name);
    }

    /// <summary>進行中的更新跡象（第一個找到的）：說明與更新者 PID</summary>
    private static (string Detail, int? ProcessId)? FindActivity(CliInstallSnapshot snapshot)
    {
        var name = DisplayName(snapshot.Provider);
        foreach (var observation in snapshot.Locks)
        {
            if (IsLockHeld(observation.Lock, observation.Holder, snapshot.InspectedAtUtc))
                return ($"{name} 正在安裝（版本鎖 {observation.Lock.Name}{ProcessText(observation.Lock.ProcessId)}）", observation.Lock.ProcessId);
        }

        if (!string.IsNullOrEmpty(snapshot.NativeExecutable))
        {
            var exeName = Path.GetFileName(snapshot.NativeExecutable);
            foreach (var entry in snapshot.ExecutableEntries)
            {
                if (ParseExecutableMark(exeName, entry) is { Kind: CliUpdaterMarkKind.StagedExecutable } mark && IsFresh(mark.StampUtc, snapshot.InspectedAtUtc))
                    return ($"{name} 正在換檔（{entry}）", mark.ProcessId);
            }
        }

        foreach (var entry in snapshot.VersionEntries)
        {
            if (ParseVersionMark(entry) is { } mark && IsFresh(mark.StampUtc, snapshot.InspectedAtUtc))
                return ($"{name} 正在放入新版（versions 的 {entry}）", mark.ProcessId);
        }

        foreach (var npm in snapshot.NpmProcesses)
        {
            if (IsGlobalNpmInstall(npm.CommandLine))
                return ($"npm 正在安裝或更新全域套件（PID {npm.ProcessId}）", npm.ProcessId);
        }
        return null;
    }

    private static bool IsFresh(DateTime? stampUtc, DateTime nowUtc)
        => stampUtc is { } stamp && (ToUtc(nowUtc) - stamp).Duration() < StagingFreshness;

    private static string ProcessText(int? pid) => pid is int p ? $"，PID {p}" : string.Empty;

    // ───────────────────────────── 監看表（7.2.2） ─────────────────────────────

    /// <summary>
    /// 這個安裝要監看的資料夾（路徑一律由 CliLocator 實際解析的結果推得：npm 前綴、XDG 可能不同）：
    /// <list type="bullet">
    /// <item>Claude 原生：執行檔所在的 bin（claude.exe*）；versions（XDG_DATA_HOME 或 ~\.local\share 底下的 claude\versions）；
    /// locks（XDG_STATE_HOME 或 ~\.local\state 底下的 claude\locks，*.lock）。~ 由 bin＝~\.local\bin 推回，與 Claude 安裝程式相同</item>
    /// <item>npm：前綴的 node_modules\@openai（或 @anthropic-ai，含子資料夾）；前綴本身的 codex*（或 claude*）殼</item>
    /// <item>app 自管 Codex：codex 資料夾的上層（codex*；不監看會被整個改名的 codex 資料夾本身）</item>
    /// <item>其他：執行檔所在的資料夾（執行檔名稱*）</item>
    /// </list>
    /// environment 讀環境變數（HOME、USERPROFILE、XDG_DATA_HOME、XDG_STATE_HOME；測試傳假的）
    /// </summary>
    public static IReadOnlyList<CliWatchTarget> BuildWatchPlan(CliInstallation? installation, Func<string, string?> environment)
    {
        var plan = new List<CliWatchTarget>();
        if (installation == null)
            return plan;
        var exe = installation.NativeExecutable ?? installation.Launcher.FileName;
        var exeDirectory = Path.GetDirectoryName(exe);
        var exeName = Path.GetFileName(exe);
        switch (installation.Kind)
        {
            case CliInstallKind.Native when installation.Provider == AgentProvider.Claude && exeDirectory != null:
            {
                plan.Add(new CliWatchTarget(CliWatchArea.ClaudeBin, exeDirectory, exeName + "*", false, exeName));
                var (versions, locks) = ClaudeDataDirectories(exeDirectory, environment);
                plan.Add(new CliWatchTarget(CliWatchArea.ClaudeVersions, versions, "*", false, "versions"));
                plan.Add(new CliWatchTarget(CliWatchArea.ClaudeLocks, locks, "*.lock", false, "locks"));
                break;
            }
            case CliInstallKind.Npm when Path.GetDirectoryName(installation.DisplayPath) is { } prefix:
            {
                var claude = installation.Provider == AgentProvider.Claude;
                var scope = Path.Combine(prefix, "node_modules", claude ? "@anthropic-ai" : "@openai");
                var shim = claude ? "claude" : "codex";
                plan.Add(new CliWatchTarget(CliWatchArea.NpmPackages, scope, "*", true, claude ? "claude-code" : "codex"));
                plan.Add(new CliWatchTarget(CliWatchArea.NpmShims, prefix, shim + "*", false, shim));
                break;
            }
            case CliInstallKind.Managed when exeDirectory != null && Path.GetDirectoryName(exeDirectory) is { } root:
            {
                var folder = Path.GetFileName(exeDirectory);
                plan.Add(new CliWatchTarget(CliWatchArea.ManagedRoot, root, folder + "*", false, folder));
                break;
            }
            default:
                if (exeDirectory != null)
                    plan.Add(new CliWatchTarget(CliWatchArea.ExecutableDirectory, exeDirectory, exeName + "*", false, exeName));
                break;
        }
        return plan;
    }

    /// <summary>
    /// Claude 原生安裝的 versions 與 locks 資料夾（照 2.1.296 的安裝程式：versions＝(XDG_DATA_HOME ?? ~\.local\share)\claude\versions、
    /// locks＝(XDG_STATE_HOME ?? ~\.local\state)\claude\locks、執行檔固定在 ~\.local\bin）
    /// </summary>
    internal static (string Versions, string Locks) ClaudeDataDirectories(string binDirectory, Func<string, string?> environment)
    {
        var local = Path.GetDirectoryName(binDirectory.TrimEnd('\\', '/'));
        var home = local != null && string.Equals(Path.GetFileName(local), ".local", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(local)
            : null;
        home ??= NonEmpty(environment("HOME")) ?? NonEmpty(environment("USERPROFILE")) ?? local ?? binDirectory;
        var data = NonEmpty(environment("XDG_DATA_HOME")) ?? Path.Combine(home, ".local", "share");
        var state = NonEmpty(environment("XDG_STATE_HOME")) ?? Path.Combine(home, ".local", "state");
        return (Path.Combine(data, "claude", "versions"), Path.Combine(state, "claude", "locks"));
    }

    /// <summary>
    /// 檔案事件和安裝有沒有關係（無關的回 null，不重新計時、不發訊號）。name、oldName 是相對於監看資料夾的路徑（含子資料夾）：
    /// <list type="bullet">
    /// <item>ClaudeBin：claude.exe 本身的任何變化（含改名成 .old、.new 改名成它）、.new 暫存檔的建立與寫入；.old 備份被刪、被改名不算</item>
    /// <item>ClaudeVersions：版本檔與 .tmp 暫存的建立、寫入、改名（刪除舊版是清理，不算）</item>
    /// <item>ClaudeLocks：*.lock 的任何變化（建立＝開始安裝、刪除＝安裝結束）</item>
    /// <item>NpmPackages：套件資料夾（codex）、退役資料夾（.codex-*）、提升到前綴的平台套件（codex-win32-*）底下的任何變化</item>
    /// <item>NpmShims：codex、codex.cmd、codex.ps1 的任何變化</item>
    /// <item>ManagedRoot：codex 資料夾本身的變化、codex.old-* 的出現（刪除舊版不算）</item>
    /// <item>ExecutableDirectory：執行檔本身的變化</item>
    /// </list>
    /// </summary>
    public static CliWatchEvent? ClassifyEvent(CliWatchTarget target, WatcherChangeTypes change, string? name, string? oldName)
    {
        var description = Describe(change, name, oldName);
        switch (target.Area)
        {
            case CliWatchArea.ClaudeBin:
            {
                var exe = target.Name;
                if (SameName(name, exe) || SameName(oldName, exe))
                {
                    var other = SameName(name, exe) ? oldName : name;
                    return new CliWatchEvent(ParseExecutableMark(exe, other)?.ProcessId, description);
                }
                if (change != WatcherChangeTypes.Deleted && ParseExecutableMark(exe, name) is { Kind: CliUpdaterMarkKind.StagedExecutable } staged)
                    return new CliWatchEvent(staged.ProcessId, description);
                return null;
            }
            case CliWatchArea.ClaudeVersions:
            {
                if (change == WatcherChangeTypes.Deleted || (!StartsWithVersion(name) && !StartsWithVersion(oldName)))
                    return null;
                var mark = ParseVersionMark(name) ?? ParseVersionMark(oldName);
                return new CliWatchEvent(mark?.ProcessId, description);
            }
            case CliWatchArea.ClaudeLocks:
                return EndsWithLock(name) || EndsWithLock(oldName) ? new CliWatchEvent(null, description) : null;
            case CliWatchArea.NpmPackages:
                return IsPackageEntry(target.Name, name) || IsPackageEntry(target.Name, oldName) ? new CliWatchEvent(null, description) : null;
            case CliWatchArea.NpmShims:
                return IsShim(target.Name, name) || IsShim(target.Name, oldName) ? new CliWatchEvent(null, description) : null;
            case CliWatchArea.ManagedRoot:
                if (SameName(name, target.Name) || SameName(oldName, target.Name))
                    return new CliWatchEvent(null, description);
                if (change != WatcherChangeTypes.Deleted && name != null && name.StartsWith(target.Name + ".old-", StringComparison.OrdinalIgnoreCase))
                    return new CliWatchEvent(null, description);
                return null;
            default:
                return SameName(name, target.Name) || SameName(oldName, target.Name) ? new CliWatchEvent(null, description) : null;
        }
    }

    /// <summary>
    /// FileSystemWatcher 的 Error 之後它還在不在監看：緩衝溢位（一次幾千個事件，npm 解壓）時 .NET 會繼續監看、只是漏了事件；
    /// 其他錯誤（監看的資料夾被刪、存取被拒）時它已經停了，要重建。兩種都要全量檢查
    /// </summary>
    public static bool WatcherSurvivesError(Exception? error) => error is InternalBufferOverflowException;

    private static string Describe(WatcherChangeTypes change, string? name, string? oldName) => change switch
    {
        WatcherChangeTypes.Created => $"{name} 建立",
        WatcherChangeTypes.Deleted => $"{name} 刪除",
        WatcherChangeTypes.Renamed => $"{oldName} 改名為 {name}",
        _ => $"{name} 寫入",
    };

    private static bool SameName(string? name, string expected) => name != null && name.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithVersion(string? name) => name != null && LeadingVersionPattern().IsMatch(name);

    private static bool EndsWithLock(string? name) => name != null && name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    private static bool IsShim(string shim, string? name)
        => name != null && (SameName(name, shim) || SameName(name, shim + ".cmd") || SameName(name, shim + ".ps1"));

    /// <summary>相對路徑的第一段是套件、退役資料夾（.套件-雜湊）或提升到前綴的平台套件（套件-win32-*）</summary>
    private static bool IsPackageEntry(string package, string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;
        var separator = relativePath.IndexOfAny(new[] { '\\', '/' });
        var first = separator >= 0 ? relativePath[..separator] : relativePath;
        return SameName(first, package)
               || first.StartsWith("." + package + "-", StringComparison.OrdinalIgnoreCase)
               || first.StartsWith(package + "-win32-", StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────── 給更新協調（WP1.5）：判定 → CliVersionChange ─────────────────────────────

    /// <summary>
    /// 誰觸發的（只影響介面文字）：有更新者 PID 時，它是 app 自己的更新程序（CliRunner 回傳的 PID）或在 app 的工作物件裡
    /// （isAppProcess，例如 ChildProcessJob.Contains）＝App；npm 的檔名沒有 PID，就看 app 當時是不是正在跑自己的更新
    /// </summary>
    public static CliChangeOrigin ResolveOrigin(int? updaterProcessId, bool appUpdateInProgress, Func<int, bool> isAppProcess)
        => updaterProcessId is int pid
            ? (isAppProcess(pid) ? CliChangeOrigin.App : CliChangeOrigin.External)
            : (appUpdateInProgress ? CliChangeOrigin.App : CliChangeOrigin.External);

    /// <summary>
    /// 完整判定（含 --version）→ 要發布的變化：Changing → Stable=false；Broken → 安裝損壞（New=null，Detail＝說明）；
    /// Missing → CLI 已移除；Stable → 新身分（identity＝CliBinaryIdentityRules.TryRead 讀到的；讀不到回 null＝這次不發布，下次再查）。
    /// 混版一定是 Broken，所以不會成為換版（IsVersionSwitch＝false，0 次重開）
    /// </summary>
    public static CliVersionChange? BuildVersionChange(AgentProvider provider, CliBinaryIdentity? old, CliInstallVerdict verdict, CliBinaryIdentity? identity,
        CliChangeOrigin origin, DateTime? detectedAtUtc = null)
    {
        CliVersionChange? change = verdict.Condition switch
        {
            CliInstallCondition.Changing => new CliVersionChange(provider, old, null, origin, Stable: false, InstallBroken: false, verdict.Detail),
            CliInstallCondition.Broken => new CliVersionChange(provider, old, null, origin, Stable: true, InstallBroken: true, verdict.Detail),
            CliInstallCondition.Missing => new CliVersionChange(provider, old, null, origin, Stable: true, InstallBroken: false, verdict.Detail),
            _ when identity == null => null,
            _ => new CliVersionChange(provider, old, identity, origin, Stable: true, InstallBroken: false, DescribeSwitch(provider, old, identity!, origin)),
        };
        return change != null && detectedAtUtc is { } at ? change with { DetectedAtUtc = ToUtc(at) } : change;
    }

    /// <summary>換版的說明（「Claude Code 已在 app 外更新為 2.1.297」）；沒有換版（第一次看到、同一份檔案）回 null</summary>
    private static string? DescribeSwitch(AgentProvider provider, CliBinaryIdentity? old, CliBinaryIdentity identity, CliChangeOrigin origin)
    {
        if (CliBinaryIdentity.SameOrUnknown(old, identity))
            return null;
        var name = DisplayName(provider);
        return origin == CliChangeOrigin.External ? $"{name} 已在 app 外更新為 {identity.Version}" : $"{name} 已更新為 {identity.Version}";
    }

    // ───────────────────────────── 小工具 ─────────────────────────────

    private static int? ParseProcessId(Group group)
        => group.Success && int.TryParse(group.Value, out var pid) && pid > 0 ? pid : null;

    private static DateTime? ParseUnixMilliseconds(string text)
        => long.TryParse(text, out var ms) ? ParseUnixMilliseconds((double)ms) : null;

    private static DateTime? ParseUnixMilliseconds(double ms)
        => ms >= 0 && ms <= 253_402_300_799_999 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime : null;

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FirstLine(string text)
    {
        var line = text.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "（空白）";
        return line.Length <= 120 ? line : line[..120] + "…";
    }

    /// <summary>執行檔名稱之後的部分：「old.&lt;ms&gt;[.&lt;pid&gt;]」或「new.&lt;pid&gt;.&lt;ms&gt;.&lt;hex&gt;」（2.1.296 清理時的解析式再加上 new 的 PID）</summary>
    [GeneratedRegex(@"^(?:old\.(?<oldMs>\d+)(?:\.(?<oldPid>\d+))?|new\.(?<newPid>\d+)\.(?<newMs>\d+)\.[0-9a-f]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutableMarkPattern();

    /// <summary>versions 的暫存檔：「&lt;版本&gt;.tmp.&lt;pid&gt;.&lt;ms&gt;[.&lt;次數&gt;]」</summary>
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+?)?\.tmp\.(?<pid>\d+)\.(?<ms>\d+)(?:\.[0-9A-Za-z]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionMarkPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+")]
    private static partial Regex LeadingVersionPattern();
}
