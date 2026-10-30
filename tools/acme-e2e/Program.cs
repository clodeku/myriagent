using System.Text;

namespace MyriagentTools.AcmeE2e;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 輸出被導向（管線、記錄檔）時一律 UTF-8；直接在主控台時沿用主控台的字碼頁（不改使用者主控台的設定）
        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

        AcmeE2eOptions options;
        try
        {
            options = AcmeE2eOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(AcmeE2eOptions.Usage);
            return 2;
        }
        if (options.Help)
        {
            Console.WriteLine(AcmeE2eOptions.Usage);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return await new AcmeScenario(options).RunAsync(cts.Token);
    }
}

/// <summary>命令列參數</summary>
internal sealed class AcmeE2eOptions
{
    public const string Usage = """
        用法：acme-e2e [選項]
          --hub <MyriagentHub.exe>      要測的伺服器（預設 <repo>\src\MyriagentHub\bin\Release\net10.0\win-x64\MyriagentHub.exe，先 dotnet build -c Release）
          --work <資料夾>           這次執行的工作資料夾（預設 %TEMP%\myriagent-tools\acme-e2e\<時間>；結果檔 result.json 在裡面）
          --cache <資料夾>          Pebble 與 challtestsrv 的下載快取（預設 %LOCALAPPDATA%\myriagent-tools\downloads；必須在 repo 以外）
          --domain <網域>           伺服器網域＝ACME 識別（預設 hub.myriagent.test；challtestsrv 把它解析到 127.0.0.1／::1）
          --hub-port <埠>           MyriagentHub 的 HTTPS 埠＝Pebble 的 TLS-ALPN-01 驗證埠（預設 0＝自動挑空的高埠）
          --acme-port、--acme-mgmt-port、--dns-port、--dns-mgmt-port <埠>   Pebble 目錄、Pebble 管理、challtestsrv DNS、challtestsrv 管理（預設自動）
          --timeout <秒>            每個等待的上限（預設 60）
          --staging                 不略過 staging：第一次取得先打 staging 目錄、成功才打正式目錄（兩者都是同一個 Pebble，會簽兩次）
        結束碼：0 全部通過、1 有檢查失敗、2 參數錯誤。
        """;

    public bool Help { get; private init; }

    public required string HubExe { get; init; }

    public required string WorkDir { get; init; }

    public required string CacheDir { get; init; }

    public string Domain { get; private init; } = "hub.myriagent.test";

    public int HubPort { get; private init; }

    public int AcmePort { get; private init; }

    public int AcmeManagementPort { get; private init; }

    public int DnsPort { get; private init; }

    public int DnsManagementPort { get; private init; }

    public TimeSpan Timeout { get; private init; } = TimeSpan.FromSeconds(60);

    /// <summary>不略過 staging（預設略過：staging 與正式目錄都是同一個 Pebble）</summary>
    public bool Staging { get; private init; }

    public string? RepoRoot { get; private init; }

    public static AcmeE2eOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var help = false;
        var staging = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help" or "/?")
            {
                help = true;
                continue;
            }
            if (a == "--staging")
            {
                staging = true;
                continue;
            }
            if (!a.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException("看不懂的參數：" + a);
            map[a[2..]] = args[++i];
        }
        var known = new[] { "hub", "work", "cache", "domain", "hub-port", "acme-port", "acme-mgmt-port", "dns-port", "dns-mgmt-port", "timeout" };
        foreach (var key in map.Keys)
        {
            if (!known.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("不認得的選項：--" + key);
        }

        var root = FindRepoRoot(AppContext.BaseDirectory);
        var hub = map.GetValueOrDefault("hub")
            ?? (root is null ? string.Empty : Path.Combine(root, "src", "MyriagentHub", "bin", "Release", "net10.0", "win-x64", "MyriagentHub.exe"));
        var work = map.GetValueOrDefault("work")
            ?? Path.Combine(Path.GetTempPath(), "myriagent-tools", "acme-e2e", DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        var cache = map.GetValueOrDefault("cache")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "myriagent-tools", "downloads");
        cache = Path.GetFullPath(cache);
        if (root is not null && IsInside(cache, root))
            throw new ArgumentException("下載快取必須在 repo 以外：" + cache);

        return new AcmeE2eOptions
        {
            Help = help,
            HubExe = string.IsNullOrEmpty(hub) ? string.Empty : Path.GetFullPath(hub),
            WorkDir = Path.GetFullPath(work),
            CacheDir = cache,
            Domain = map.GetValueOrDefault("domain") ?? "hub.myriagent.test",
            HubPort = Port(map, "hub-port"),
            AcmePort = Port(map, "acme-port"),
            AcmeManagementPort = Port(map, "acme-mgmt-port"),
            DnsPort = Port(map, "dns-port"),
            DnsManagementPort = Port(map, "dns-mgmt-port"),
            Timeout = TimeSpan.FromSeconds(map.TryGetValue("timeout", out var t) ? int.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : 60),
            Staging = staging,
            RepoRoot = root,
        };
    }

    private static int Port(Dictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
            return 0;
        if (!int.TryParse(value, out var port) || port is < 0 or > 65535)
            throw new ArgumentException($"--{key} 不是合法的連接埠：{value}");
        return port;
    }

    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Myriagent.slnx")))
                return dir.FullName;
        }
        return null;
    }

    private static bool IsInside(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
