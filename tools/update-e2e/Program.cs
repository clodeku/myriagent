using System.Text;

namespace MyriagentTools.UpdateE2e;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 輸出被導向（管線、記錄檔）時一律 UTF-8；直接在主控台時沿用主控台的字碼頁
        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

        UpdateE2eOptions options;
        try
        {
            options = UpdateE2eOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(UpdateE2eOptions.Usage);
            return 2;
        }
        if (options.Help)
        {
            Console.WriteLine(UpdateE2eOptions.Usage);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return await new UpdateScenario(options).RunAsync(cts.Token);
    }
}

/// <summary>命令列參數</summary>
internal sealed class UpdateE2eOptions
{
    public const string Usage = """
        用法：update-e2e [選項]
          --repo <資料夾>     repo 根目錄（預設從本程式的位置往上找 Myriagent.slnx）
          --work <資料夾>     這次執行的工作資料夾，必須不存在或是空的（預設 %TEMP%\myriagent-tools\update-e2e\<時間>；結果檔 result.json 在裡面）
          --timeout <秒>      等換版、等回滾的上限（預設 300）
          --keep              通過後也保留工作資料夾（預設通過就刪；有失敗一律保留）
        結束碼：0 全部通過、1 有檢查失敗、2 參數錯誤或環境不符。
        """;

    public bool Help { get; private init; }

    public required string RepoRoot { get; init; }

    public required string WorkDir { get; init; }

    public TimeSpan Timeout { get; private init; } = TimeSpan.FromSeconds(300);

    public bool Keep { get; private init; }

    public static UpdateE2eOptions Parse(IReadOnlyList<string> args)
    {
        string? repo = null, work = null;
        int timeout = 300;
        bool keep = false, help = false;
        for (int i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} 少了值");
            switch (args[i])
            {
                case "--repo":
                    repo = Next();
                    break;
                case "--work":
                    work = Next();
                    break;
                case "--timeout":
                    timeout = int.TryParse(Next(), out var t) && t > 0 ? t : throw new ArgumentException("--timeout 要是正整數");
                    break;
                case "--keep":
                    keep = true;
                    break;
                case "-h":
                case "--help":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"不認得的參數：{args[i]}");
            }
        }
        repo ??= FindRepoRoot(AppContext.BaseDirectory) ?? throw new ArgumentException("找不到 repo 根目錄（Myriagent.slnx），請用 --repo 指定");
        work ??= Path.Combine(Path.GetTempPath(), "myriagent-tools", "update-e2e", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        return new UpdateE2eOptions
        {
            RepoRoot = Path.GetFullPath(repo),
            WorkDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(work)),
            Timeout = TimeSpan.FromSeconds(timeout),
            Keep = keep,
            Help = help,
        };
    }

    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Myriagent.slnx")))
                return dir.FullName;
        }
        return null;
    }
}
