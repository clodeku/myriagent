using System.Diagnostics;
using System.Text;

namespace MyriagentHub.ProbeClient;

/// <param name="ExitCode">結束碼；逾時被結束是 null</param>
internal sealed record AdminResult(int? ExitCode, string Output, string Error, bool TimedOut);

/// <summary>
/// MyriagentHub 的 admin 指令（load 的本機 <c>--hub-exe</c> 模式；WP4.9 計畫 D19）：一律帶 <c>--data &lt;資料根&gt;</c>——admin 沒帶 --data 時
/// 預設是 C:\ProgramData\MyriagentHub（正式的資料根，AdminCommandLine.cs），所以沒有資料根就拒絕，參數裡也不能自己帶 --data（只能由這裡加）
/// </summary>
internal static class AdminWrapper
{
    /// <summary><c>admin &lt;args&gt; --data &lt;資料根&gt;</c>；沒有資料根或 args 已經有 --data 丟例外</summary>
    public static IReadOnlyList<string> Arguments(string? dataDir, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(dataDir))
            throw new ArgumentException("admin 指令一定要帶 --data（不帶時是 C:\\ProgramData\\MyriagentHub＝正式資料根）", nameof(dataDir));
        if (args.Any(a => a.Equals("--data", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--data=", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("--data 只能由 AdminWrapper 加", nameof(args));
        return ["admin", .. args, "--data", Path.GetFullPath(dataDir)];
    }

    /// <summary>執行一次 admin；逾時就以 PID 結束這個 admin 程序（只結束自己啟動的）</summary>
    public static async Task<AdminResult> RunAsync(string exe, string dataDir, TimeSpan timeout, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var arg in Arguments(dataDir, args))
            info.ArgumentList.Add(arg);
        ChildEnvironment.Apply(info);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("MyriagentHub admin 啟動失敗");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var wait = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new AdminResult(null, await stdout, await stderr, true);
        }

        return new AdminResult(process.ExitCode, await stdout, await stderr, false);
    }

    /// <summary><c>admin stop --wait N</c>（一律帶 --data）</summary>
    public static Task<AdminResult> StopAsync(string exe, string dataDir, int waitSeconds = 20) =>
        RunAsync(exe, dataDir, TimeSpan.FromSeconds(waitSeconds + 15), "stop", "--wait", waitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
