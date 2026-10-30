using Myriagent.Agents;

namespace FakeDevice;

/// <summary>假 app-server 啟動器要檢查的環境：TEMP、System32 的 Windows PowerShell、使用者資料夾（測試給假的值）</summary>
/// <param name="TempRoot">暫存資料夾（<see cref="Path.GetTempPath"/>）</param>
/// <param name="SystemPowerShell"><c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c></param>
/// <param name="UserProfile">使用者資料夾（真的 <c>.codex</c>、<c>.claude</c> 在它底下）</param>
internal sealed record FakeLauncherEnvironment(string TempRoot, string SystemPowerShell, string UserProfile)
{
    /// <summary>這台電腦的值</summary>
    public static FakeLauncherEnvironment Current() =>
        new(Path.GetTempPath(), FakeLauncherRules.SystemPowerShell, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// <summary>
/// 假 Codex app-server 的啟動器規則（WP4.9 步 13，D11）：帳戶管理員找 CLI 的接縫（AccountManager.ResolveLauncher）只依代理種類分派、分辨不出子指令，
/// 所以 Codex 一律交出同一個假啟動器（System32 的 Windows PowerShell 跑 TEMP 底下的 <see cref="FakeCodexAppServerScript"/>），由腳本依 argv 分派
/// app-server 與 <c>logout</c>；其他代理一律沒有 CLI。只在三件事都成立時交出：代理是 Codex、腳本是 TEMP 底下的 .ps1（正規化之後比，<c>..</c> 走出去的不算）、
/// 執行檔是 System32 的 Windows PowerShell。子程序的環境剝掉 <see cref="StrippedVariables"/>（從 PowerShell 7 繼承的 PSModulePath 會讓 5.1 載錯模組）：
/// 啟動器帶不了環境，fake-device 啟動時從自己的程序環境剝掉（子程序繼承）。測試的 FakeCodexAppServer 也經這裡建啟動器
/// </summary>
internal static class FakeLauncherRules
{
    /// <summary><c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c>（同測試的 CodexAgentSessionTests.PowerShell）</summary>
    public static string SystemPowerShell { get; } = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>假 app-server 的子程序不繼承的環境變數</summary>
    public static IReadOnlyList<string> StrippedVariables { get; } = ["PSModulePath"];

    /// <summary>fake-device 資料根底下放假 app-server 腳本、calls.txt 與 complete.flag 的資料夾名稱</summary>
    public const string FolderName = "fake-codex";

    /// <summary>這個環境變數名稱要不要剝掉（Windows 的環境變數不分大小寫）</summary>
    public static bool IsStrippedVariable(string name) => StrippedVariables.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>剝掉 <see cref="StrippedVariables"/>（環境變數的字典；不分大小寫比對鍵）</summary>
    public static void StripEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.Where(IsStrippedVariable).ToList())
            environment.Remove(key);
    }

    /// <summary>PowerShell 的參數（同搬移前的測試）：不載入設定檔、不互動、這次執行略過執行原則，最後是腳本的完整路徑</summary>
    public static IReadOnlyList<string> PrefixArguments(string scriptPath) =>
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath];

    /// <summary>這台電腦的環境下的 <see cref="Launcher(AgentProvider, string, string, FakeLauncherEnvironment)"/></summary>
    public static AgentLauncher? Launcher(AgentProvider provider, string scriptPath, string powershellPath) =>
        Launcher(provider, scriptPath, powershellPath, FakeLauncherEnvironment.Current());

    /// <summary>
    /// 三件事都成立（<see cref="Check"/> 回 null）時回假啟動器（執行檔照給的路徑、腳本是正規化之後的完整路徑），否則回 null（其他代理一律 null）
    /// </summary>
    public static AgentLauncher? Launcher(AgentProvider provider, string scriptPath, string powershellPath, FakeLauncherEnvironment environment) =>
        Check(provider, scriptPath, powershellPath, environment) is null
            ? new AgentLauncher(powershellPath, PrefixArguments(Path.GetFullPath(scriptPath)))
            : null;

    /// <summary>
    /// 交不交出假啟動器；可以交出回 null，否則回原因。代理種類一律 switch、未知值丟例外。
    /// <list type="bullet">
    /// <item>代理是 Codex（Claude、Antigravity、Grok 沒有假的 CLI）</item>
    /// <item>腳本是完整的本機路徑（不收 UNC、裝置路徑、替代資料流、萬用字元）、副檔名 .ps1、在 TEMP 底下（不含 TEMP 本身）</item>
    /// <item>TEMP 不是磁碟根目錄、也沒有包住使用者資料夾（否則使用者真的 .codex 也會算在 TEMP 底下）</item>
    /// <item>執行檔是 System32 的 Windows PowerShell（正規化之後不分大小寫比）</item>
    /// </list>
    /// </summary>
    public static string? Check(AgentProvider provider, string scriptPath, string powershellPath, FakeLauncherEnvironment environment)
    {
        switch (provider)
        {
            case AgentProvider.Codex:
                break;
            case AgentProvider.Claude:
            case AgentProvider.Antigravity:
            case AgentProvider.Grok:
                return $"{provider} 沒有假的 CLI（只有 Codex 的 app-server 有假的）";
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "不認得的代理種類");
        }

        if (CheckLocalPath(scriptPath, "腳本") is { } scriptProblem)
            return scriptProblem;
        if (!scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            return $"腳本只收 .ps1：{scriptPath}";
        if (CheckLocalPath(environment.TempRoot, "TEMP") is { } tempProblem)
            return tempProblem;
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.TempRoot));
        if (string.Equals(Path.GetPathRoot(temp)?.TrimEnd('\\', '/'), temp.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return $"TEMP 是磁碟根目錄（{temp}）：不交出假啟動器";
        if (FakeControlRules.IsUnder(environment.UserProfile, temp) || SamePath(environment.UserProfile, temp))
            return $"TEMP（{temp}）包住了使用者資料夾（{environment.UserProfile}）：使用者真的設定資料夾也會算在 TEMP 底下";
        if (!FakeControlRules.IsUnder(scriptPath, temp))
            return $"腳本要在 TEMP（{temp}）底下：{Path.GetFullPath(scriptPath)}";
        if (CheckLocalPath(powershellPath, "powershell") is { } shellProblem)
            return shellProblem;
        if (!SamePath(powershellPath, environment.SystemPowerShell))
            return $"執行檔要是 System32 的 Windows PowerShell（{environment.SystemPowerShell}）：{powershellPath}";
        return null;
    }

    /// <summary>
    /// 帳戶管理員的 ResolveLauncher：Codex 交出 <paramref name="codex"/>（<c>--fake-codex</c> 時建好、檢查過的假啟動器；沒有就是 null），
    /// 其他代理一律 null（fake-device 不執行任何真的 CLI）；未知的代理種類丟例外
    /// </summary>
    public static AgentLauncher? Resolve(AgentProvider provider, AgentLauncher? codex) => provider switch
    {
        AgentProvider.Codex => codex,
        AgentProvider.Claude or AgentProvider.Antigravity or AgentProvider.Grok => null,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "不認得的代理種類"),
    };

    /// <summary>
    /// <c>--fake-codex</c> 的資料根：要在 TEMP 底下（腳本的 logout 只在 TEMP 底下的 CODEX_HOME 留標記，隔離帳戶的 CODEX_HOME 在資料根底下，
    /// 資料根不在 TEMP 底下時每次登出都 exit 4）；合格回 null
    /// </summary>
    public static string? CheckDataRoot(string dataRoot, string tempRoot) =>
        CheckLocalPath(dataRoot, "資料根") ?? (FakeControlRules.IsUnder(dataRoot, tempRoot)
            ? null
            : $"--fake-codex 要資料根在 TEMP（{Path.GetFullPath(tempRoot)}）底下：假 app-server 的 logout 只在 TEMP 底下的 CODEX_HOME 留標記（資料根 {dataRoot}）");

    /// <summary>資料根底下放假 app-server 的資料夾（腳本、calls.txt、complete.flag；同一個資料根重新啟動時沿用，登入完成的旗標也留著）</summary>
    public static string FolderOf(string dataRoot) => Path.Combine(dataRoot, FolderName);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>完整的本機磁碟路徑：不是空的、不是 UNC 或裝置路徑、是完整路徑、磁碟代號之後沒有冒號（替代資料流）、沒有萬用字元與控制字元</summary>
    private static string? CheckLocalPath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"{name} 是空的";
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith(@"\/", StringComparison.Ordinal)
            || path.StartsWith(@"/\", StringComparison.Ordinal))
            return $"{name} 不收 UNC 與裝置路徑：{path}";
        if (!Path.IsPathFullyQualified(path))
            return $"{name} 要是完整路徑：{path}";
        if (path.IndexOf(':', 2) >= 0)
            return $"{name} 不收替代資料流：{path}";
        if (path.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0 || path.Any(char.IsControl))
            return $"{name} 有不合法的字元：{path}";
        return null;
    }
}
