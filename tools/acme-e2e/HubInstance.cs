using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MyriagentTools.AcmeE2e;

/// <summary>admin 指令的結果（MyriagentHub.exe admin …：JSON 一個物件；結束碼 0 成功、1 失敗、2 用法、3 服務執行中、4 等不到服務）</summary>
internal sealed record AdminCallResult(string Command, int ExitCode, bool Ok, string? Message, string Output, double ElapsedMs);

/// <summary>
/// 以 --data 暫存資料根、高埠啟動的 MyriagentHub（主控台執行，等同 dotnet run 之後的 MyriagentHub.exe）。
/// HTTP 一律連 127.0.0.1:埠、Host 與 SNI＝伺服器網域；/readyz 允許本機迴路匿名讀取。
/// 停止一律先 admin stop（正常結束才會刪掉使用者金鑰存放區裡的暫時金鑰檔），逾時才強制結束並回報。
/// </summary>
internal sealed class HubInstance(string exe, string dataDir, string domain, int port, IReadOnlyList<string> arguments, string logDir)
{
    private HttpClient? http;
    private int starts;

    public ManagedProcess? Process { get; private set; }

    public string DataDir => dataDir;

    public bool IsRunning => Process is { HasExited: false };

    public string Origin => $"https://{domain}:{port}";

    public async Task<double> StartAsync(ChildProcessJob job, TimeSpan timeout, CancellationToken cancellationToken)
    {
        starts++;
        var args = new List<string> { "--data", dataDir };
        args.AddRange(arguments);
        var sw = Stopwatch.StartNew();
        Process?.Dispose();
        Process = ManagedProcess.Start("MyriagentHub", exe, args, Path.GetDirectoryName(exe)!, Path.Combine(logDir, $"hub-console-{starts}.log"), null, job, captureLines: false);
        http?.Dispose();
        http = CreateClient();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.HasExited)
                throw new InvalidOperationException($"MyriagentHub 啟動後立即結束（結束碼 {Process.ExitCode}），看 {Path.Combine(logDir, $"hub-console-{starts}.log")}");
            try
            {
                using var response = await http.GetAsync(Origin + "/healthz", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                    return sw.Elapsed.TotalMilliseconds;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(300, cancellationToken);
        }
        throw new TimeoutException($"MyriagentHub 在 {timeout.TotalSeconds} 秒內沒有回應 /healthz");
    }

    public async Task<JsonElement> HealthzAsync(CancellationToken cancellationToken) => await GetJsonAsync("/healthz", cancellationToken);

    /// <summary>/readyz（本機迴路免認證）；被匿名限流（429）時照 Retry-After 等一次再試</summary>
    public async Task<JsonElement> ReadyzAsync(CancellationToken cancellationToken) => await GetJsonAsync("/readyz", cancellationToken);

    /// <summary>執行 <c>MyriagentHub.exe admin &lt;指令&gt; --data … --wait …</c>（<paramref name="extra"/> 例如 cert-renew 的 --force）</summary>
    public async Task<AdminCallResult> AdminAsync(string command, int waitSeconds, CancellationToken cancellationToken, params string[] extra)
    {
        var sw = Stopwatch.StartNew();
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in new[] { "admin", command, "--data", dataDir, "--wait", waitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Concat(extra))
            info.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("無法執行 admin " + command);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        bool ok = false;
        string? message = null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(output);
            ok = json.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            if (json.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                message = m.GetString();
            else if (json.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                message = e.GetString();
        }
        catch (JsonException)
        {
            message = "輸出不是 JSON：" + output + error;
        }
        return new AdminCallResult(command, process.ExitCode, ok, message, output, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>admin stop 後等程序結束；逾時才強制結束（回傳 false＝不是正常結束）</summary>
    public async Task<(bool Graceful, AdminCallResult? Admin, double ElapsedMs)> StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Process is null || Process.HasExited)
            return (true, null, 0);
        var sw = Stopwatch.StartNew();
        AdminCallResult? admin = null;
        try
        {
            admin = await AdminAsync("stop", 15, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine("admin stop 失敗：" + ex.Message);
        }
        var exited = await Process.WaitForExitAsync(timeout);
        if (!exited)
        {
            Process.Kill();
            await Process.WaitForExitAsync(TimeSpan.FromSeconds(10));
        }
        http?.Dispose();
        http = null;
        return (exited, admin, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>資料根 diag\ 的 JSON 行記錄（t、l、c、m、x）</summary>
    public List<JsonElement> ReadDiagLog()
    {
        var list = new List<JsonElement>();
        var dir = Path.Combine(dataDir, "diag");
        if (!Directory.Exists(dir))
            return list;
        foreach (var file in Directory.GetFiles(dir, "myriagenthub-*.log").Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0)
                    continue;
                try
                {
                    list.Add(JsonSerializer.Deserialize<JsonElement>(line));
                }
                catch (JsonException)
                {
                    // 寫到一半的最後一行
                }
            }
        }
        return list;
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        var client = http ?? throw new InvalidOperationException("MyriagentHub 沒有在執行");
        for (var attempt = 0; ; attempt++)
        {
            using var response = await client.GetAsync(Origin + path, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                await Task.Delay(wait + TimeSpan.FromMilliseconds(200), cancellationToken);
                continue;
            }
            // /readyz 排空中回 503 但內容一樣是狀態
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
                throw new HttpRequestException($"{path} 回應 {(int)response.StatusCode}");
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        // 憑證另外以 TlsProbe 驗證（Pebble 的根）；這裡只是讀狀態
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}
