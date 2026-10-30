using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.Isolation;

/// <summary>子程序探針的一次結果</summary>
/// <param name="LogLines">中樞情境（baseline、unpaired、unpaired-commands）結束時記錄檔的行數；其他情境是 0</param>
/// <param name="LogProblems">其中的 WARN 與 ERROR</param>
internal sealed record IsolationProbeResult(string Scenario, IReadOnlyList<string> Assemblies, IReadOnlyList<string> NativeModules, int LogLines, IReadOnlyList<string> LogProblems)
{
    public IReadOnlyList<string> ForbiddenAssemblies => Assemblies.Where(AssemblyIsolationScanner.IsForbidden).ToList();

    public bool LoadedNativeSqlite =>
        NativeModules.Any(m => string.Equals(m, AssemblyIsolationScanner.ForbiddenNativeModule, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 啟動測試組件的自訂 Main（<see cref="IsolationProbeProgram"/>）跑一個情境：30 秒逾時、解析 stdout 最後一行 JSON。
/// 不繼承 <c>MYRIAGENT_SYNC_*</c> 環境變數（不讓開發者的伺服器覆寫影響基準；要連的伺服器由情境自己給），資料根用新的暫存資料夾；子程序加進桌面的工作物件
/// （<see cref="ChildProcessJob"/>，KILL_ON_JOB_CLOSE），測試主機當掉時一起結束。
/// </summary>
internal static class IsolationProbeRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <param name="prepare">啟動前在資料根準備檔案（例如 paired-offline 的 device.json、sync.db）</param>
    public static IsolationProbeResult Run(string scenario, Action<string>? prepare = null)
    {
        var testAssembly = typeof(IsolationProbeRunner).Assembly.Location;
        using var data = new TempDirectory("isolation-probe");
        prepare?.Invoke(data.Root);
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(testAssembly)!,
        };
        var apphost = Path.ChangeExtension(testAssembly, ".exe");
        if (File.Exists(apphost))
        {
            start.FileName = apphost;
        }
        else
        {
            start.FileName = DotnetHost();
            start.ArgumentList.Add(testAssembly);
        }

        start.ArgumentList.Add(IsolationProbeProgram.Flag);
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add("--data");
        start.ArgumentList.Add(data.Root);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MYRIAGENT_SYNC_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(key);
        start.Environment["MYRIAGENT_AGENT_DATA"] = data.Root;

        using var process = Process.Start(start) ?? throw new XunitException($"探針 {scenario} 啟動失敗：{start.FileName}");
        ChildProcessJob.TryAssign(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 剛好結束
            }

            throw new XunitException($"探針 {scenario} {Timeout.TotalSeconds:0} 秒內沒有結束");
        }

        process.WaitForExit();
        var output = stdout.GetAwaiter().GetResult();
        var errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new XunitException($"探針 {scenario} 結束碼 {process.ExitCode}：{errors}");
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{\"assemblies\"", StringComparison.Ordinal))
            ?? throw new XunitException($"探針 {scenario} 沒有輸出結果：{output}{errors}");
        var json = JObject.Parse(line);
        return new IsolationProbeResult(
            scenario,
            json["assemblies"]!.Values<string>().OfType<string>().ToList(),
            json["nativeModules"]!.Values<string>().OfType<string>().ToList(),
            json.Value<int?>("logLines") ?? 0,
            json["logProblems"]?.Values<string>().OfType<string>().ToList() ?? []);
    }

    /// <summary>執行階段資料夾（…\dotnet\shared\Microsoft.NETCore.App\{版本}\）往上三層的 dotnet.exe；找不到就靠 PATH</summary>
    private static string DotnetHost()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var host = Path.Combine(root, "dotnet.exe");
        return File.Exists(host) ? host : "dotnet";
    }
}
