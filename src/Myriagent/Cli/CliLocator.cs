using System.Runtime.InteropServices;
using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Cli;

/// <summary>CLI 的安裝方式（決定怎麼更新）</summary>
internal enum CliInstallKind
{
    /// <summary>Claude 官方原生安裝（%USERPROFILE%\.local\bin\claude.exe），以 claude update 更新</summary>
    Native,

    /// <summary>npm 全域安裝（%APPDATA%\npm\*.cmd），以 npm install -g 更新</summary>
    Npm,

    /// <summary>app 自行下載的 Codex（%LOCALAPPDATA%\Myriagent\codex）</summary>
    Managed,

    /// <summary>其他（手動放在 PATH、agy 與 grok 的官方安裝程式、WinGet）：以 CLI 自己的 update 指令更新；WinGet 的 portable 套件（CliInstallation.Winget）改跑 winget upgrade</summary>
    Other,
}

/// <summary>找到的 CLI：啟動方式＋安裝方式＋使用者看的路徑</summary>
internal sealed record CliInstallation(AgentProvider Provider, AgentLauncher Launcher, CliInstallKind Kind, string DisplayPath)
{
    /// <summary>
    /// 實際執行的原生執行檔（身分比對與監看用，見 CliBinaryIdentity）：Claude 原生是 %USERPROFILE%\.local\bin\claude.exe；
    /// npm 版 Claude 是套件裡的 bin\claude.exe；npm 版 Codex 是 @openai\codex\node_modules\@openai\codex-win32-x64\vendor\…\bin\codex.exe
    /// （Launcher.FileName 是 node.exe，比對它沒有意義）；受管 Codex 是 app 資料夾的 codex\codex.exe。
    /// null＝還沒解析（由 Resolve 填入）
    /// </summary>
    public string? NativeExecutable { get; init; }

    /// <summary>WinGet 的 portable 套件（只有 Other 才可能有；CliWingetRules.WithWinget 填入）：更新改跑 winget upgrade、走屏障。null＝不是 WinGet 安裝</summary>
    public WingetPackage? Winget { get; init; }
}

/// <summary>
/// 找本機的 Claude Code 與 Codex CLI。各目錄先找原生 exe，再找 npm 安裝（.cmd 殼不能用 UseShellExecute=false 直接啟動，
/// 改成直接做 .cmd 做的事：npm 版 Claude 2.1.113 起執行套件裡的 bin\claude.exe，更早的 Claude 與 Codex 以 node 執行 node_modules 裡的 js 入口）。
/// 搜尋目錄：Claude 原生安裝位置 → 目前程序 PATH → 登錄檔的使用者／系統 PATH（app 開著時才裝的也找得到）→ npm 全域目錄 → app 自行安裝的 Codex。
/// 找到時一併解析實際執行的原生執行檔（CliInstallation.NativeExecutable）：直接找到的 exe 就是它；npm 版 Claude 是 bin\claude.exe
/// （2.1.112 以前的舊配置沒有原生檔，以入口 cli.js 代表：換版時它一樣會被換掉）；npm 版 Codex 照 codex.js 找 vendor 的 codex.exe
/// （巢狀的平台套件 → 提升到前綴的平台套件 → 套件自己的 vendor；都不存在時用第一個候選，讓監看與身分讀取知道該看哪裡）。
/// </summary>
internal static class CliLocator
{
    public const string ClaudeNpmPackage = "@anthropic-ai/claude-code";
    public const string CodexNpmPackage = "@openai/codex";

    /// <summary>npm 版 Claude Code 2.1.113 起的入口：postinstall 把平台套件的原生檔放到這裡（package.json 的 bin，.cmd 殼直接執行它）</summary>
    private static readonly string ClaudeNpmExeEntry = Path.Combine("node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");

    /// <summary>bin\claude.exe 比這小就是 postinstall 沒跑時留下的佔位腳本（實際約 500 位元組；官方 install.cjs 也以 4 KB 判斷）</summary>
    internal const long NpmNativeMinBytes = 4096;

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string NpmGlobalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");

    public static string ClaudeNativeDirectory => Path.Combine(UserProfile, ".local", "bin");

    /// <summary>agy 官方安裝指令放執行檔的位置（install.ps1 的 %LOCALAPPDATA%\agy\bin\agy.exe）</summary>
    public static string AntigravityInstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin");

    /// <summary>grok 官方 PowerShell 安裝（install.ps1）放執行檔的位置：%USERPROFILE%\.grok\bin\grok.exe</summary>
    public static string GrokInstallDirectory => Path.Combine(UserProfile, ".grok", "bin");

    public static List<string> GetSearchDirectories(AgentProvider provider)
    {
        var (first, last) = provider switch
        {
            AgentProvider.Claude => (new[] { ClaudeNativeDirectory }, new[] { NpmGlobalDirectory }),
            AgentProvider.Codex => (Array.Empty<string>(), new[] { NpmGlobalDirectory, AppPaths.ManagedCodexDirectory }),
            AgentProvider.Antigravity => (new[] { AntigravityInstallDirectory }, Array.Empty<string>()),
            AgentProvider.Grok => (new[] { GrokInstallDirectory }, Array.Empty<string>()),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        return MergeSearchDirectories(
            first.Concat(new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            }),
            last,
            IsNetworkDrive);
    }

    public static CliInstallation? Locate(AgentProvider provider)
        => CliWingetRules.WithWinget(
            Resolve(provider, GetSearchDirectories(provider), File.Exists, ClaudeNativeDirectory, NpmGlobalDirectory, AppPaths.ManagedCodexDirectory, FileLength),
            LinkTarget,
            WingetMachineRoots);

    /// <summary>裝給所有使用者的 WinGet 套件所在（%ProgramFiles%\WinGet\Packages，與 32 位元的 Program Files）</summary>
    private static IReadOnlyList<string> WingetMachineRoots
        => new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p)).ToList();

    /// <summary>
    /// 找 winget（更新 WinGet 安裝的 CLI 用）：應用程式安裝程式放的執行別名 %LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe，
    /// 沒有就找 PATH；都沒有回 null
    /// </summary>
    public static string? LocateWinget()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias))
            return alias;
        return MergeSearchDirectories(new[]
               {
                   Environment.GetEnvironmentVariable("PATH"),
                   Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                   Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
               }, Array.Empty<string>(), IsNetworkDrive)
            .Select(dir => Path.Combine(dir, "winget.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>符號連結的最終目的地（WinGet\Links 的 grok.exe 這類）；不是連結、讀不到回 null</summary>
    private static string? LinkTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>純函式版本（測試用）：依目錄順序找 CLI 並判斷安裝方式；fileLength 為 null＝不檢查 npm 版 Claude 原生檔的大小</summary>
    internal static CliInstallation? Resolve(
        AgentProvider provider,
        IReadOnlyList<string> directories,
        Func<string, bool> fileExists,
        string claudeNativeDirectory,
        string npmDirectory,
        string managedCodexDirectory,
        Func<string, long>? fileLength = null)
    {
        // agy 與 grok 只有原生執行檔（沒有 npm 版）：官方安裝位置或 PATH 上找到的都以各自的 update 指令更新
        if (provider is AgentProvider.Antigravity or AgentProvider.Grok)
        {
            var name = provider == AgentProvider.Antigravity ? "agy.exe" : "grok.exe";
            var found = directories.Select(d => Path.Combine(d, name)).FirstOrDefault(fileExists);
            return found == null ? null : new CliInstallation(provider, new AgentLauncher(found, Array.Empty<string>()), CliInstallKind.Other, found) { NativeExecutable = found };
        }

        var exeName = provider == AgentProvider.Claude ? "claude.exe" : "codex.exe";
        var cmdName = provider == AgentProvider.Claude ? "claude.cmd" : "codex.cmd";
        var jsEntry = provider == AgentProvider.Claude
            ? Path.Combine("node_modules", "@anthropic-ai", "claude-code", "cli.js")
            : Path.Combine("node_modules", "@openai", "codex", "bin", "codex.js");

        foreach (var dir in directories)
        {
            var exe = Path.Combine(dir, exeName);
            if (fileExists(exe))
            {
                var kind = SamePath(dir, claudeNativeDirectory) && provider == AgentProvider.Claude ? CliInstallKind.Native
                    : SamePath(dir, managedCodexDirectory) && provider == AgentProvider.Codex ? CliInstallKind.Managed
                    : CliInstallKind.Other;
                return new CliInstallation(provider, new AgentLauncher(exe, Array.Empty<string>()), kind, exe) { NativeExecutable = exe };
            }

            var cmd = Path.Combine(dir, cmdName);
            if (!fileExists(cmd))
                continue;

            // npm 版 Claude Code 2.1.113 起套件裡沒有 cli.js：.cmd 殼直接執行 bin\claude.exe；只有佔位腳本（postinstall 沒跑）就退回舊配置或找下一個目錄
            if (provider == AgentProvider.Claude)
            {
                var npmExe = Path.Combine(dir, ClaudeNpmExeEntry);
                if (fileExists(npmExe) && (fileLength?.Invoke(npmExe) ?? long.MaxValue) >= NpmNativeMinBytes)
                    return new CliInstallation(provider, new AgentLauncher(npmExe, Array.Empty<string>()), CliInstallKind.Npm, cmd) { NativeExecutable = npmExe };
            }

            var js = Path.Combine(dir, jsEntry);
            if (!fileExists(js))
                continue;

            var bundledNode = Path.Combine(dir, "node.exe");
            var node = fileExists(bundledNode)
                ? bundledNode
                : directories.Select(d => Path.Combine(d, "node.exe")).FirstOrDefault(fileExists);
            if (node != null)
            {
                // .cmd 殼＋node_modules 的配置就是 npm 全域安裝（npm 前綴可能不在預設的 %APPDATA%\npm）；
                // 原生檔：Codex 是 codex.js 會去執行的 vendor codex.exe，舊版 Claude 沒有原生檔、以 cli.js 代表
                var native = provider == AgentProvider.Claude ? js : CodexNativeExecutable(CodexNpmPackageRoot(dir), fileExists);
                return new CliInstallation(provider, new AgentLauncher(node, new[] { js }), CliInstallKind.Npm, cmd) { NativeExecutable = native };
            }
        }

        return null;
    }

    /// <summary>npm 安裝的前綴（.cmd 殼所在的資料夾，node_modules 在它底下）；不是 npm 安裝回 null</summary>
    public static string? GetNpmPrefix(CliInstallation installation)
        => installation.Kind == CliInstallKind.Npm ? Path.GetDirectoryName(installation.DisplayPath) : null;

    /// <summary>npm 版 CLI 的套件根目錄（…\node_modules\@openai\codex、…\node_modules\@anthropic-ai\claude-code）</summary>
    public static string NpmPackageRoot(AgentProvider provider, string npmPrefix) => provider switch
    {
        AgentProvider.Claude => ClaudeNpmPackageRoot(npmPrefix),
        AgentProvider.Codex => CodexNpmPackageRoot(npmPrefix),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "這種 CLI 沒有 npm 版"),
    };

    internal static string CodexNpmPackageRoot(string npmPrefix) => Path.Combine(npmPrefix, "node_modules", "@openai", "codex");

    internal static string ClaudeNpmPackageRoot(string npmPrefix) => Path.Combine(npmPrefix, "node_modules", "@anthropic-ai", "claude-code");

    /// <summary>
    /// npm 版 Codex 的 vendor\&lt;target triple&gt; 候選（照 codex.js 的 findCodexExecutable）：先是這台電腦架構的平台套件
    /// （巢狀在套件的 node_modules 裡 → 提升到前綴的 node_modules\@openai → 套件自己的 vendor），再是另一種架構
    /// </summary>
    internal static IReadOnlyList<string> CodexVendorRootCandidates(string packageRoot, Architecture? architecture = null)
    {
        var targets = (architecture ?? RuntimeInformation.OSArchitecture) == Architecture.Arm64
            ? new[] { ("aarch64-pc-windows-msvc", "codex-win32-arm64"), ("x86_64-pc-windows-msvc", "codex-win32-x64") }
            : new[] { ("x86_64-pc-windows-msvc", "codex-win32-x64"), ("aarch64-pc-windows-msvc", "codex-win32-arm64") };
        var scope = Path.GetDirectoryName(packageRoot);
        var result = new List<string>();
        foreach (var (triple, platformPackage) in targets)
        {
            result.Add(Path.Combine(packageRoot, "node_modules", "@openai", platformPackage, "vendor", triple));
            if (scope != null)
                result.Add(Path.Combine(scope, platformPackage, "vendor", triple));
            result.Add(Path.Combine(packageRoot, "vendor", triple));
        }
        return result;
    }

    /// <summary>實際有 bin\codex.exe 的 vendor\&lt;triple&gt; 目錄（codex.js 會執行的那一份）；都沒有回 null</summary>
    internal static string? FindCodexVendorRoot(string packageRoot, Func<string, bool> fileExists)
        => CodexVendorRootCandidates(packageRoot).FirstOrDefault(root => fileExists(Path.Combine(root, "bin", "codex.exe")));

    /// <summary>npm 版 Codex 的原生執行檔：實際存在的 vendor codex.exe；都不存在（安裝不完整、正在替換）時用第一個候選</summary>
    internal static string CodexNativeExecutable(string packageRoot, Func<string, bool> fileExists)
        => Path.Combine(FindCodexVendorRoot(packageRoot, fileExists) ?? CodexVendorRootCandidates(packageRoot)[0], "bin", "codex.exe");

    /// <summary>找 npm（安裝或更新 npm 版 CLI 用）：回傳 npm.cmd 的完整路徑</summary>
    public static string? LocateNpm()
    {
        foreach (var dir in MergeSearchDirectories(new[]
                 {
                     Environment.GetEnvironmentVariable("PATH"),
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
                 }, new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs") }, IsNetworkDrive))
        {
            var npm = Path.Combine(dir, "npm.cmd");
            if (File.Exists(npm))
                return npm;
        }
        return null;
    }

    /// <summary>合併搜尋目錄並去重（不分大小寫、保留先後）；isNetworkDrive 判定為網路磁碟機的目錄也略過</summary>
    public static List<string> MergeSearchDirectories(IEnumerable<string?> pathValues, IEnumerable<string?> extraDirectories, Func<string, bool>? isNetworkDrive = null)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            // 登錄檔裡的 PATH 可能還帶 %USERPROFILE% 這類變數；網路上的目錄一律略過：UNC 與對應成磁碟代號的網路磁碟機
            // （檢查檔案是否存在會對外發 SMB 連線、離線時還會卡到逾時，而找 CLI 是在 UI 執行緒上做的）
            var d = Environment.ExpandEnvironmentVariables(dir ?? string.Empty).Trim().Trim('"').TrimEnd('\\');
            if (string.IsNullOrEmpty(d) || d.StartsWith(@"\\", StringComparison.Ordinal) || d.StartsWith("//", StringComparison.Ordinal))
                return;
            if (seen.Add(d) && isNetworkDrive?.Invoke(d) != true)
                result.Add(d);
        }

        foreach (var value in pathValues ?? Enumerable.Empty<string?>())
        {
            foreach (var dir in (value ?? string.Empty).Split(';'))
                Add(dir);
        }

        foreach (var dir in extraDirectories ?? Enumerable.Empty<string?>())
            Add(dir);

        return result;
    }

    /// <summary>目錄在對應成磁碟代號的網路磁碟機上（例如 H:\bin）；GetDriveType 只查磁碟代號的對應，不會連線</summary>
    internal static bool IsNetworkDrive(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool SamePath(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
