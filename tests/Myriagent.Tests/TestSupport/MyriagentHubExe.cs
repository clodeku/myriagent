using System.Reflection;

namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 測試用的 MyriagentHub.exe（D4：子程序，不用專案參考——專案參考擋不住 MyriagentHub 的 NuGet 套件與 ASP.NET Core 共用架構傳遞進測試專案）。
/// exe 來自 slnx 的建置輸出 <c>src/MyriagentHub/bin/&lt;組態&gt;/net10.0/win-x64/</c>（<c>MYRIAGENT_HUB_EXE</c> 優先）；第一次用時把整個資料夾複製到
/// <c>%TEMP%\Myriagent.Tests\myriagenthub-&lt;pid&gt;</c> 再執行，殘留的子程序不會鎖住 bin（下次建置 MSB3027）
/// </summary>
internal static class MyriagentHubExe
{
    public const string PathVariable = "MYRIAGENT_HUB_EXE";

    private static readonly Lazy<string> Copied = new(CopyToTemp, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>另外的 MyriagentHub.exe（混合版本的情境，例如 MYRIAGENT_SYNC_OLD_HUB）：名稱 → 複製後的 exe</summary>
    private static readonly Dictionary<string, (string Source, string Copied)> Alternates = new(StringComparer.Ordinal);

    /// <summary>複製後的 MyriagentHub.exe</summary>
    public static string Path => Copied.Value;

    /// <summary>複製目的地的資料夾名稱（每個測試程序一個；金鑰檔的 CreatorProcessName 以它認出是本程序的 MyriagentHub 建的）</summary>
    public static string CopyDirectoryName => $"myriagenthub-{Environment.ProcessId}";

    /// <summary>另外的 exe 複製目的地的資料夾名稱（<c>myriagenthub-{名稱}-{pid}</c>：不含 <see cref="CopyDirectoryName"/>＋「\MyriagentHub.exe」，金鑰檔分得出是哪一個建的）</summary>
    public static string AlternateDirectoryName(string name) => $"myriagenthub-{name}-{Environment.ProcessId}";

    /// <summary>金鑰檔記的建立者路徑裡、認得出這個資料夾的 MyriagentHub.exe 的字串（<see cref="UserKeyStore.FilesNaming"/>）</summary>
    public static string KeyNeedle(string copyDirectoryName) => copyDirectoryName + System.IO.Path.DirectorySeparatorChar + "MyriagentHub.exe";

    /// <summary>
    /// 把另一個 MyriagentHub.exe 所在的資料夾整個複製到 <c>%TEMP%\Myriagent.Tests\myriagenthub-{名稱}-{pid}</c>（每個名稱只複製一次；同一個名稱換了來源就丟例外），
    /// 回傳複製後的 exe。理由同 <see cref="Path"/>：殘留的子程序不鎖住來源資料夾
    /// </summary>
    public static string CopyAlternate(string name, string sourceExe)
    {
        if (!File.Exists(sourceExe))
            throw new FileNotFoundException("另外的 MyriagentHub.exe 不存在", sourceExe);
        var source = System.IO.Path.GetFullPath(sourceExe);
        lock (Alternates)
        {
            if (Alternates.TryGetValue(name, out var existing))
            {
                return string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase)
                    ? existing.Copied
                    : throw new InvalidOperationException($"{name} 已經複製了 {existing.Source}，不能再換成 {source}");
            }

            var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Myriagent.Tests", AlternateDirectoryName(name));
            TempDirectory.DeleteWithRetry(target);
            CopyDirectory(System.IO.Path.GetDirectoryName(source)!, target);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => TempDirectory.DeleteWithRetry(target);
            var copied = System.IO.Path.Combine(target, "MyriagentHub.exe");
            Alternates[name] = (source, copied);
            return copied;
        }
    }

    public static string BuildConfiguration =>
        typeof(MyriagentHubExe).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildConfiguration")?.Value ?? "Release";

    /// <summary>從測試輸出資料夾往上找 Myriagent.slnx 所在的 repo 根目錄</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Myriagent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException($"從 {AppContext.BaseDirectory} 往上找不到 Myriagent.slnx");
    }

    private static string SourceExe()
    {
        var configured = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new FileNotFoundException($"{PathVariable} 指的 MyriagentHub.exe 不存在", configured);
        }

        var exe = System.IO.Path.Combine(RepoRoot(), "src", "MyriagentHub", "bin", BuildConfiguration, "net10.0", "win-x64", "MyriagentHub.exe");
        return File.Exists(exe)
            ? exe
            : throw new FileNotFoundException($"找不到 MyriagentHub.exe：先執行 dotnet build Myriagent.slnx -c {BuildConfiguration}", exe);
    }

    private static string CopyToTemp()
    {
        var source = System.IO.Path.GetDirectoryName(SourceExe())!;
        var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Myriagent.Tests", CopyDirectoryName);
        TempDirectory.DeleteWithRetry(target);
        CopyDirectory(source, target);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TempDirectory.DeleteWithRetry(target);
        return System.IO.Path.Combine(target, "MyriagentHub.exe");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, System.IO.Path.Combine(target, System.IO.Path.GetFileName(dir)));
    }
}
