using Myriagent.Agents;

namespace Myriagent.Cli;

/// <summary>
/// WinGet 安裝的 portable 套件（例 xAI.GrokBuild）：Id＝套件識別碼（winget upgrade --id 用）、Directory＝套件資料夾
/// （…\WinGet\Packages\&lt;Id&gt;_Microsoft.Winget.Source_8wekyb3d8bbwe，執行檔在它底下）、MachineScope＝裝給所有使用者（在 Program Files 底下，更新要系統管理員）
/// </summary>
internal sealed record WingetPackage(string Id, string Directory, bool MachineScope);

/// <summary>winget list --upgrade-available 的結果：Available＝WinGet 上有比已安裝的新的版本（Version＝那一版，讀不出來為 null）；Error＝查詢失敗的說明</summary>
internal sealed record WingetUpgradeCheck(bool Available, string? Version, string? Error);

/// <summary>winget upgrade 的結果：Succeeded＝結束碼 0；NotApplicable＝WinGet 上沒有可用的升級；Error＝失敗的說明（給使用者看）</summary>
internal sealed record WingetUpgradeVerdict(bool Succeeded, bool NotApplicable, string? Error);

/// <summary>
/// WinGet 安裝的 CLI 怎麼認、怎麼更新（docs/protocol-notes.md「Grok Build」的「安裝、版本、更新」）：CLI 自己的 update 遇到 WinGet 安裝只印提示不換檔
/// （grok 1.0.67：「Grok Build was installed with WinGet… run: winget upgrade --id xAI.GrokBuild -e」），要改跑 winget upgrade；
/// portable 套件換檔是先刪舊檔，執行中的 exe 會讓它失敗（「remove: Access is denied.」，2026-10-24 實測），所以要先排空對話、等程序結束
/// </summary>
internal static class CliWingetRules
{
    /// <summary>WinGet 社群套件庫（預設來源 winget）的來源識別碼：portable 套件的資料夾名稱是「套件識別碼_來源識別碼」</summary>
    public const string CommunitySourceSuffix = "_Microsoft.Winget.Source_8wekyb3d8bbwe";

    /// <summary>APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND：找不到符合的已安裝套件</summary>
    public const int NoApplicationsFound = unchecked((int)0x8A150014);

    /// <summary>APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE：「找不到可用的升級」</summary>
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);

    /// <summary>
    /// 執行檔是不是 WinGet 的 portable 套件：往上找名稱以 CommunitySourceSuffix 結尾的資料夾（巢狀的封存套件執行檔不在第一層）。
    /// machineRoots＝Program Files 這類資料夾：套件資料夾在它們底下＝裝給所有使用者。識別碼不合 WinGet 規則的（含空白、引號）不認
    /// </summary>
    public static WingetPackage? FindPackage(string? executablePath, IReadOnlyList<string> machineRoots)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;
        var directory = Path.GetDirectoryName(executablePath);
        while (!string.IsNullOrEmpty(directory))
        {
            var name = Path.GetFileName(directory);
            if (name.EndsWith(CommunitySourceSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var id = name[..^CommunitySourceSuffix.Length];
                return IsPackageId(id) ? new WingetPackage(id, directory, CliUpdateRules.IsUnderAny(directory, machineRoots)) : null;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    /// <summary>
    /// 找到的安裝是不是 WinGet 的 portable 套件，是就記在 CliInstallation.Winget（只看「其他」安裝方式：Claude 原生、npm、受管 Codex 不是）。
    /// PATH 上的是 WinGet\Links 的符號連結時（開了開發人員模式）照連結的目的地判斷，原生執行檔也改成目的地
    /// （換版換的是目的地；程序的執行檔路徑也是目的地）。linkTarget：符號連結的最終目的地，不是連結回 null
    /// </summary>
    public static CliInstallation? WithWinget(CliInstallation? installation, Func<string, string?> linkTarget, IReadOnlyList<string> machineRoots)
    {
        if (installation is not { Kind: CliInstallKind.Other })
            return installation;
        var exe = installation.NativeExecutable ?? installation.Launcher.FileName;
        var target = linkTarget(exe);
        var package = FindPackage(target ?? exe, machineRoots);
        if (package == null)
            return installation;
        return installation with { Winget = package, NativeExecutable = target ?? exe };
    }

    /// <summary>WinGet 的套件識別碼（只收英數與 . - _ +，兩段以上）：參數照樣傳給 winget，不收奇怪的字元</summary>
    public static bool IsPackageId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 128 && id.Contains('.') && !id.StartsWith('.') && !id.EndsWith('.')
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    /// <summary>查 WinGet 上有沒有新版（只列這個套件；不更新任何東西）</summary>
    public static IReadOnlyList<string> ListUpgradeArguments(string id)
        => new[] { "list", "--id", id, "--exact", "--upgrade-available", "--accept-source-agreements", "--disable-interactivity" };

    /// <summary>
    /// 更新這個套件：不跳任何提示（--disable-interactivity、--silent、接受來源與套件的合約）；略過相依性（裝的時候已經裝過，
    /// 在背景重新處理相依性可能跳出系統管理員的提示，例如 grok 的 Microsoft.VCRedist.2015+.x64）
    /// </summary>
    public static IReadOnlyList<string> UpgradeArguments(string id)
        => new[]
        {
            "upgrade", "--id", id, "--exact", "--silent", "--disable-interactivity",
            "--accept-source-agreements", "--accept-package-agreements", "--skip-dependencies",
        };

    /// <summary>
    /// 解讀 winget list --id X --exact --upgrade-available：有新版時表格有一列含套件識別碼，後面依序是已安裝的版本、可用的版本、來源；
    /// 沒有新版時沒有那一列——結束碼有時是 0（v1.29：「未找到符合輸入條件的已安裝套件。」），有時是 NoApplicationsFound。欄位名稱會翻譯，只看識別碼那一列
    /// </summary>
    public static WingetUpgradeCheck ParseUpgradeCheck(int exitCode, string? stdout, string? stderr, string id)
    {
        foreach (var line in Lines(stdout))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.FindIndex(tokens, t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                return new WingetUpgradeCheck(true, index + 2 < tokens.Length ? CliVersionRules.ParseVersion(tokens[index + 2]) : null, null);
        }
        if (exitCode == 0 || exitCode == NoApplicationsFound)
            return new WingetUpgradeCheck(false, null, null);
        return new WingetUpgradeCheck(false, null, $"向 WinGet 查詢新版失敗（結束碼 {FormatExitCode(exitCode)}）：{Tail(stdout, stderr)}");
    }

    /// <summary>
    /// 解讀 winget upgrade：0＝成功（仍以換檔後的版本驗證）；UpdateNotApplicable＝沒有可用的升級；其他＝失敗，原因取輸出的最後兩行
    /// （例：「remove: Access is denied.: "…\grok.exe"」「安裝程式失敗，結束代碼為: 0x8a150003 : 執行命令失敗」），
    /// 看得出是執行檔正被使用時另外說明
    /// </summary>
    public static WingetUpgradeVerdict EvaluateUpgrade(AgentProvider provider, int exitCode, string? stdout, string? stderr, string id)
    {
        if (exitCode == 0)
            return new WingetUpgradeVerdict(true, false, null);
        if (exitCode == UpdateNotApplicable)
            return new WingetUpgradeVerdict(false, true, null);
        if (exitCode == NoApplicationsFound)
            return new WingetUpgradeVerdict(false, false, $"WinGet 找不到已安裝的 {id}，請在終端機執行 winget list --id {id} 確認安裝方式");
        var text = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
        var inUse = text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || text.Contains("拒絕存取", StringComparison.Ordinal)
                    || text.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) || text.Contains("0x80070020", StringComparison.OrdinalIgnoreCase);
        var hint = inUse ? $"（執行檔可能還在使用中：請關閉所有的 {CliInstallStabilityRules.DisplayName(provider)} 再更新）" : string.Empty;
        return new WingetUpgradeVerdict(false, false, $"WinGet 更新失敗（結束碼 {FormatExitCode(exitCode)}）：{Tail(stdout, stderr)}{hint}");
    }

    /// <summary>WinGet 上還沒有新版（社群套件庫通常比官方晚幾天）：給使用者看的說明</summary>
    public static string DescribeNotYetOnWinget(AgentProvider provider, string? version, string? latest)
    {
        var name = CliInstallStabilityRules.DisplayName(provider);
        var current = version != null ? $"比 {version} 新的" : string.Empty;
        var published = latest != null ? $"官方已發布 {latest}，" : string.Empty;
        return $"WinGet 上還沒有{current} {name}（{published}新版通常要幾天才會上 WinGet），請過幾天再更新";
    }

    /// <summary>裝給所有使用者的 WinGet 套件：app 不提權，請使用者自己在系統管理員終端機更新</summary>
    public static string DescribeMachineScope(AgentProvider provider, WingetPackage package)
        => $"{CliInstallStabilityRules.DisplayName(provider)} 是以 WinGet 安裝給所有使用者，更新需要系統管理員權限：請關閉所有使用它的程式後，在系統管理員終端機執行 winget upgrade --id {package.Id} -e";

    /// <summary>找不到 winget（Windows 封裝管理員）</summary>
    public static string DescribeMissingWinget(AgentProvider provider)
        => $"找不到 winget（Windows 封裝管理員），無法更新以 WinGet 安裝的 {CliInstallStabilityRules.DisplayName(provider)}；請從 Microsoft Store 安裝「應用程式安裝程式」後再試";

    /// <summary>
    /// app 以外還有程序在執行 WinGet 套件裡的執行檔（終端機、VS Code、系統管理員終端機開的、背景的 leader）：WinGet 換不了執行中的檔案，
    /// 列出 PID（最多 5 個）請使用者先關掉；grok 另外提示 grok leader kill（終端機開過 grok 就會留下背景的 leader）
    /// </summary>
    public static string DescribeForeignProcesses(AgentProvider provider, IReadOnlyList<CliProcessUse> foreign)
    {
        var name = CliInstallStabilityRules.DisplayName(provider);
        var list = string.Join("、", foreign.Take(5).Select(p => $"PID {p.ProcessId}")) + (foreign.Count > 5 ? $" 等 {foreign.Count} 個" : string.Empty);
        var leader = provider == AgentProvider.Grok ? "（背景的 leader 可在終端機執行 grok leader kill 結束；以系統管理員身分開的，要從系統管理員終端機結束）" : string.Empty;
        return $"還有 app 以外的 {name} 程序在使用 WinGet 安裝的執行檔（{list}），WinGet 無法替換執行中的檔案，這次沒有更新；請先關閉終端機或其他程式裡的 {name} 再更新{leader}";
    }

    /// <summary>結束碼以 WinGet 文件的十六進位表示（0x8A15002B）</summary>
    public static string FormatExitCode(int exitCode) => $"0x{unchecked((uint)exitCode):X8}";

    /// <summary>輸出的最後兩行（stdout 優先、沒有就 stderr；最多 300 字）：WinGet 失敗時最後兩行是原因與結束代碼</summary>
    private static string Tail(string? stdout, string? stderr)
    {
        var lines = Lines(stdout);
        if (lines.Count == 0)
            lines = Lines(stderr);
        if (lines.Count == 0)
            return "原因不明";
        var text = string.Join(" ", lines.TakeLast(2));
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    /// <summary>逐行（CRLF）；同一行裡以 \r 覆寫的進度只留最後一段（終端機看到的樣子）；去掉只有轉圈字元或進度條的行</summary>
    private static List<string> Lines(string? text)
        => (text ?? string.Empty).Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.All(c => c is '-' or '\\' or '|' or '/') && !l.Contains('█') && !l.Contains('▒'))
            .ToList();
}
