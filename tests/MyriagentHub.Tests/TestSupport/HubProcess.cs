using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 以子程序執行的 MyriagentHub（測試輸出資料夾裡的 MyriagentHub.exe，專案參考一併複製過來；真的 Kestrel、與 <see cref="HubTestHost"/> 同一套設定）。
/// 用來測「強制結束」：<see cref="Kill"/> 直接結束程序（不跑正常停止），確認過的資料只能靠提交前的 fsync 留下來。
/// </summary>
internal sealed class HubProcess : IDisposable
{
    private readonly Process process;
    private readonly string exe;
    private readonly string dataDir;
    private readonly ConcurrentQueue<string> output = new();

    private HubProcess(Process process, string exe, string dataDir, Uri baseAddress)
    {
        this.process = process;
        this.exe = exe;
        this.dataDir = dataDir;
        BaseAddress = baseAddress;
    }

    /// <summary>https://127.0.0.1:埠</summary>
    public Uri BaseAddress { get; }

    /// <summary>程序最後的輸出（啟動失敗時附在例外訊息裡）</summary>
    public string Tail => string.Join(Environment.NewLine, output);

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>啟動並等到 <c>/healthz</c> 回 200</summary>
    /// <param name="exe">
    /// 要執行的 MyriagentHub.exe（null＝測試輸出資料夾裡的、這個分支的組建）；回滾測試給 <c>MYRIAGENT_SYNC_OLD_HUB</c> 指的舊組建（WP4.2 計畫步 14），
    /// 工作目錄是它自己的資料夾
    /// </param>
    /// <param name="settings">另外的設定（同 <see cref="HubTestHost.StartAsync"/> 的 settings，例如打開記錄檔）</param>
    public static async Task<HubProcess> StartAsync(string dataDir, int port, TimeSpan? timeout = null, string? exe = null, IDictionary<string, string?>? settings = null)
    {
        exe ??= Path.Combine(AppContext.BaseDirectory, "MyriagentHub.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("找不到 MyriagentHub.exe（測試輸出資料夾的由專案參考複製過來；舊組建見 src/MyriagentHub/README.md 的 MYRIAGENT_SYNC_OLD_HUB）", exe);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
        };
        foreach (var arg in HubTestHost.Arguments(dataDir, port, settings))
            info.ArgumentList.Add(arg);
        // 不繼承測試主機的 ASP.NET Core 環境變數（例如 ASPNETCORE_URLS）
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(key);

        var process = new Process { StartInfo = info };
        var hub = new HubProcess(process, exe, dataDir, new Uri($"https://127.0.0.1:{port}/"));
        process.OutputDataReceived += (_, e) => hub.Record(e.Data);
        process.ErrorDataReceived += (_, e) => hub.Record(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        while (deadline.Elapsed < limit)
        {
            if (process.HasExited)
                break;
            try
            {
                using var response = await http.GetAsync(new Uri(hub.BaseAddress, "/healthz"));
                if (response.StatusCode == HttpStatusCode.OK)
                    return hub;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
            await Task.Delay(50);
        }
        var tail = hub.Tail;
        hub.Kill();
        hub.Dispose();
        throw new TimeoutException($"MyriagentHub 子程序沒有就緒（{deadline.Elapsed.TotalSeconds:0.0} 秒，exited={process.HasExited}）：{Environment.NewLine}{tail}");
    }

    /// <summary>
    /// 正常結束：以同一個 MyriagentHub.exe 執行 <c>admin stop --data … --wait 15</c>，等程序自己結束；<paramref name="timeout"/>（預設 30 秒）內沒結束才強制結束。
    /// file 憑證模式被強制結束會在使用者金鑰存放區留下孤兒金鑰檔（src/MyriagentHub/README.md），不是在測強制結束的測試用這個停。回傳 false＝不是正常結束
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan? timeout = null)
    {
        if (process.HasExited)
            return true;
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
        };
        foreach (var arg in new[] { "admin", "stop", "--data", dataDir, "--wait", "15" })
            info.ArgumentList.Add(arg);
        using (var admin = Process.Start(info) ?? throw new InvalidOperationException("無法執行 admin stop"))
        {
            var stdout = admin.StandardOutput.ReadToEndAsync();
            var stderr = admin.StandardError.ReadToEndAsync();
            await admin.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Record("admin stop：" + (await stdout).Trim() + (await stderr).Trim());
        }
        using var wait = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(wait.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Kill();
            return false;
        }
    }

    /// <summary>強制結束（整個程序樹）並等它真的結束（服務鎖檔、資料庫檔都放開）</summary>
    public void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        process.WaitForExit(10_000);
    }

    public void Dispose()
    {
        Kill();
        process.Dispose();
    }

    private void Record(string? line)
    {
        if (line is null)
            return;
        output.Enqueue(line);
        while (output.Count > 200)
            output.TryDequeue(out _);
    }
}
