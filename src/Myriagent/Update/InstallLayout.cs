namespace Myriagent.Update;

/// <summary>端到端測試用的環境變數（tools/update-e2e）：全部只在 MYRIAGENT_UPDATE_E2E=1 時生效</summary>
internal static class UpdateEnvironment
{
    public static bool IsE2E => Environment.GetEnvironmentVariable("MYRIAGENT_UPDATE_E2E") == "1";

    public static string? E2ERoot => IsE2E ? NullIfEmpty(Environment.GetEnvironmentVariable("MYRIAGENT_UPDATE_E2E_ROOT")) : null;

    public static string? E2ESource => IsE2E ? NullIfEmpty(Environment.GetEnvironmentVariable("MYRIAGENT_UPDATE_E2E_SOURCE")) : null;

    /// <summary>檢查週期 3 秒、閒置門檻 0（不等使用者閒置）</summary>
    public static bool E2EFast => IsE2E && Environment.GetEnvironmentVariable("MYRIAGENT_UPDATE_E2E_FAST") == "1";

    /// <summary>這個 release 一啟動就結束（測試健康閘門與回滾）</summary>
    public static string? E2ECrashRelease => IsE2E ? NullIfEmpty(Environment.GetEnvironmentVariable("MYRIAGENT_UPDATE_E2E_CRASH_RELEASE")) : null;

    /// <summary>端到端模擬「這個程序的 .NET 已被移除」的標記檔（E2ERoot 底下；內容是「PID 版本」）</summary>
    public const string E2ERuntimeRemovedFileName = "e2e-runtime-removed.txt";

    /// <summary>
    /// 端到端：模擬這個程序的 .NET 已被移除（RegistryRuntimeProbe.RemovedRuntime）。標記檔寫的 PID 是這個程序才算，
    /// 重新啟動後的新程序不算（不然會一直重新啟動）；沒有標記或讀不到是 null
    /// </summary>
    public static string? E2ERuntimeRemoved
    {
        get
        {
            if (E2ERoot is not { } root)
                return null;
            var path = Path.Combine(root, E2ERuntimeRemovedFileName);
            try
            {
                if (!File.Exists(path))
                    return null;
                var parts = File.ReadAllText(path).Split(' ', 2, StringSplitOptions.TrimEntries);
                return parts.Length == 2 && int.TryParse(parts[0], out var pid) && pid == Environment.ProcessId ? NullIfEmpty(parts[1]) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// 受管理的安裝（設計 8.1）：%LOCALAPPDATA%\Programs\Myriagent\ 底下的 versions\&lt;release&gt;\、current（junction）、staging\、update-health\、updater-state.json。
/// 資料不在這裡（AppPaths.Root）
/// </summary>
internal sealed class InstallLayout
{
    public InstallLayout(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public static InstallLayout Default
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Myriagent"));

    /// <summary>端到端測試可以改到暫存資料夾；正常一律是 Default</summary>
    public static InstallLayout Resolve() => UpdateEnvironment.E2ERoot is { } root ? new InstallLayout(root) : Default;

    public string Root { get; }

    public string VersionsDirectory => Path.Combine(Root, "versions");

    public string Current => Path.Combine(Root, "current");

    public string CurrentExecutable => Path.Combine(Current, "Myriagent.exe");

    public string Staging => Path.Combine(Root, "staging");

    public string HealthDirectory => Path.Combine(Root, "update-health");

    public string StateFile => Path.Combine(Root, "updater-state.json");

    /// <summary>updater-state.json 讀－改－寫的具名 mutex（app 與換版幫手是不同程序）；名稱含根目錄的雜湊，端到端測試不會和正式安裝互相等待</summary>
    public string StateMutexName => @"Local\Myriagent.UpdaterState." + ReleaseManifestRules.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(Root.ToUpperInvariant()))[..12];

    public string VersionDirectory(string release) => Path.Combine(VersionsDirectory, release);

    public string PartialVersionDirectory(string release) => Path.Combine(VersionsDirectory, ".partial-" + release);

    public string Executable(string release) => Path.Combine(VersionDirectory(release), "Myriagent.exe");

    public string StagingZip(string release) => Path.Combine(Staging, $"myriagent-{release}.zip.partial");

    public string ArmedFile(string token) => Path.Combine(HealthDirectory, token + ".armed");

    public string HealthFile(string token) => Path.Combine(HealthDirectory, token + ".json");
}
