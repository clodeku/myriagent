using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyriagentHub.ProbeClient;

/// <summary>伺服器記錄裡瀏覽器連線被中斷的原因（純函式；MyriagentHub 的 BrowserGateway、BrowserConnections 的記錄文字）</summary>
internal static class HubLogRules
{
    public const string SlowClient = "slow_client";

    public const string SendTimeout = "send_timeout";

    public const string SendFailed = "send_failed";

    /// <summary>溢出後清空並重送 init（還沒中斷）</summary>
    public const string Resync = "resync";

    /// <summary>一行記錄是哪一種（不是這幾種回 null）</summary>
    public static string? Classify(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return null;
        // BrowserGateway：「瀏覽器連線 60 秒內第 3 次溢出，中斷：{Connection}」（原因 slow_client）
        if (line.Contains("次溢出，中斷", StringComparison.Ordinal))
            return SlowClient;
        // BrowserConnections：「瀏覽器連線送出失敗，中斷：{Connection} {Reason}」
        if (line.Contains("送出失敗，中斷", StringComparison.Ordinal))
            return line.Contains(SendTimeout, StringComparison.Ordinal) ? SendTimeout : SendFailed;
        if (line.Contains("送出佇列溢出，清空並重送 init", StringComparison.Ordinal))
            return Resync;
        return null;
    }

    /// <summary>
    /// MyriagentHub 記錄檔（<c>diag\myriagenthub-YYYYMMDD.log</c>，JSON 行、UTF-8）裡各種中斷原因的次數：只看每一行的訊息 <c>m</c>
    /// （不是 JSON、沒有 m 的行略過）。主控台的輸出是主控台碼頁（繁中 cp950），不拿來分類
    /// </summary>
    public static Dictionary<string, int> CountFileLog(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                continue;
            string? message;
            try
            {
                message = JsonNode.Parse(line)?["m"] is JsonValue value && value.TryGetValue<string>(out var m) ? m : null;
            }
            catch (JsonException)
            {
                continue;
            }

            if (Classify(message) is { } reason)
                counts[reason] = counts.GetValueOrDefault(reason) + 1;
        }

        return counts;
    }
}

/// <param name="Graceful">admin stop 之後 30 秒內自己結束</param>
/// <param name="AdminExitCode">admin stop 的結束碼（逾時是 null）</param>
/// <param name="OrphanKeys">停止之後使用者金鑰存放區裡還記著這份複製的 exe 的金鑰檔（正常停止應該沒有）</param>
/// <param name="DeletedKeys">強制結束後由這裡刪掉的（這次啟動期間新增、記著這份複製的）</param>
internal sealed record HubStopResult(bool Graceful, int? AdminExitCode, IReadOnlyList<string> OrphanKeys, IReadOnlyList<string> DeletedKeys);

/// <summary>
/// load 的本機 <c>--hub-exe</c>：把 MyriagentHub.exe 所在的資料夾整個複製到 <c>%TEMP%\myriagent-tools\probe-hub-&lt;pid&gt;-&lt;n&gt;</c>，以自己的資料根、
/// 127.0.0.1、自己挑的高埠、Testing 環境、file 憑證庫執行（參數照 tests 的 MyriagentHubProcess.Arguments，但 <c>Hub:PublicOrigin</c>＝base：
/// 瀏覽器與 fake-device 的 Origin 都是 base）。停止一律 <c>admin stop</c>（經 <see cref="AdminWrapper"/>，帶 --data）；等不到才以 PID 結束整個程序樹，
/// 再刪掉這次啟動期間新增、記著這份複製的 exe 路徑的金鑰檔（file 憑證庫匯入 PFX 時寫進使用者金鑰存放區；記著建立者的 exe 路徑，
/// 所以以複製後的路徑認，同 hub-e2e 的 hub-process.mjs）。主控台的輸出（主控台碼頁）寫進 <c>hub.log</c>；瀏覽器連線的中斷原因在停止之後從
/// MyriagentHub 自己的記錄檔（<c>Hub:FileLog</c> 打開，JSON 行、UTF-8）計數（<see cref="HubLogRules.CountFileLog"/>）
/// </summary>
internal sealed class LocalHub : IAsyncDisposable
{
    private static int copies;

    private readonly object gate = new();
    private Dictionary<string, int>? reasons;
    private readonly HashSet<string> keysBefore;
    private readonly StreamWriter log;
    private readonly Process process;
    private bool stopped;

    private LocalHub(Process process, string copyDir, string dataDir, int port, StreamWriter log, HashSet<string> keysBefore)
    {
        this.process = process;
        CopyDirectory = copyDir;
        ExePath = Path.Combine(copyDir, "MyriagentHub.exe");
        Needle = Path.Combine(Path.GetFileName(copyDir), "MyriagentHub.exe");
        DataDir = dataDir;
        Port = port;
        BaseUrl = "https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        this.log = log;
        this.keysBefore = keysBefore;
    }

    public static string KeysDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto", "Keys");

    public string CopyDirectory { get; }

    public string ExePath { get; }

    /// <summary>金鑰檔裡認得出這份複製的字串（<c>probe-hub-&lt;pid&gt;-&lt;n&gt;\MyriagentHub.exe</c>）</summary>
    public string Needle { get; }

    public string DataDir { get; }

    public int Port { get; }

    /// <summary>https://127.0.0.1:埠（也是 PublicOrigin）</summary>
    public string BaseUrl { get; }

    public int Pid => process.Id;

    /// <summary>程序已經結束時的結束碼（還在跑是 null；load 的轉送階段以它判斷伺服器是不是在期間內結束）</summary>
    public int? ExitCode => process.HasExited ? process.ExitCode : null;

    /// <summary>記錄檔裡各種中斷原因的次數（停止之後才讀；還沒停止或讀不到記錄檔是 null）</summary>
    public IReadOnlyDictionary<string, int>? Reasons
    {
        get
        {
            lock (gate)
                return reasons is null ? null : new Dictionary<string, int>(reasons, StringComparer.Ordinal);
        }
    }

    /// <summary>MyriagentHub 的記錄檔所在（資料根的 diag）</summary>
    public string FileLogDirectory => Path.Combine(DataDir, "diag");

    /// <summary>
    /// tests 的 MyriagentHubProcess.Arguments 的副本（不參考測試專案），PublicOrigin 由呼叫端給；但記錄檔打開（<c>Hub:FileLog:Enabled</c>）：
    /// 中斷原因從 UTF-8 的記錄檔數（主控台輸出是主控台碼頁），而且正式的服務也開著它
    /// </summary>
    public static IReadOnlyList<string> Arguments(string dataDir, int port, string publicOrigin)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hub:ServerDomain"] = "localhost",
            ["Hub:HttpsPort"] = port.ToString(CultureInfo.InvariantCulture),
            ["Hub:BindAddress"] = "127.0.0.1",
            ["Hub:BelowNormalPriority"] = "false",
            ["Hub:SelfTest:Enabled"] = "false",
            ["Hub:Acme:Enabled"] = "false",
            ["Hub:Backup:Enabled"] = "false",
            ["Hub:FileLog:Enabled"] = "true",
            ["Hub:AdminInbox:PollSeconds"] = "1",
            ["Hub:RateLimits:Enabled"] = "false",
            ["Hub:Tls:CertStore"] = "file",
            ["Hub:MemoryFuse:SampleSeconds"] = "3600",
            ["Hub:PublicOrigin"] = publicOrigin,
        };
        var args = new List<string> { "--data", dataDir, "--environment", "Testing" };
        args.AddRange(values.Select(kv => $"--{kv.Key}={kv.Value}"));
        return args;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 使用者金鑰存放區裡、內容含 <paramref name="needle"/>（UTF-16LE 位元組、任何位移、分大小寫）的金鑰檔名。只讀（共用讀寫刪），讀不到的略過（別人的）
    /// </summary>
    public static IReadOnlyList<string> KeyFilesNaming(string needle, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(needle);
        var wanted = Encoding.Unicode.GetBytes(needle);
        var result = new List<string>();
        string[] files;
        try
        {
            files = Directory.GetFiles(directory ?? KeysDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                if (bytes.AsSpan().IndexOf(wanted) >= 0)
                    result.Add(Path.GetFileName(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static HashSet<string> KeySnapshot()
    {
        try
        {
            return Directory.GetFiles(KeysDirectory).Select(f => Path.GetFileName(f)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>複製、啟動並等 /healthz 200；失敗時結束程序、刪掉複製</summary>
    /// <param name="extraArgs">接在 <see cref="Arguments"/> 後面的設定參數（例如 load 的 <c>--relay-cap</c> 給的 <c>--Hub:Media:MaxConcurrentRelays</c>）</param>
    public static async Task<LocalHub> StartAsync(string sourceExe, string dataDir, string logFile, TimeSpan timeout, CancellationToken ct, IReadOnlyList<string>? extraArgs = null)
    {
        if (!File.Exists(sourceExe))
            throw new FileNotFoundException("找不到 MyriagentHub.exe", sourceExe);
        var copyDir = Path.Combine(Path.GetTempPath(), "myriagent-tools", $"probe-hub-{Environment.ProcessId}-{Interlocked.Increment(ref copies)}");
        if (Directory.Exists(copyDir))
            Directory.Delete(copyDir, recursive: true);
        CopyTree(Path.GetDirectoryName(Path.GetFullPath(sourceExe))!, copyDir);
        Directory.CreateDirectory(dataDir);
        var port = FreePort();
        var info = new ProcessStartInfo(Path.Combine(copyDir, "MyriagentHub.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding(),
            StandardErrorEncoding = ConsoleEncoding(),
            WorkingDirectory = copyDir,
        };
        foreach (var arg in Arguments(Path.GetFullPath(dataDir), port, "https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)))
            info.ArgumentList.Add(arg);
        foreach (var arg in extraArgs ?? [])
            info.ArgumentList.Add(arg);
        ChildEnvironment.Apply(info);

        var keysBefore = KeySnapshot();
        var writer = new StreamWriter(logFile, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var process = new Process { StartInfo = info };
        var hub = new LocalHub(process, copyDir, Path.GetFullPath(dataDir), port, writer, keysBefore);
        process.OutputDataReceived += (_, e) => hub.Record(e.Data);
        process.ErrorDataReceived += (_, e) => hub.Record(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await hub.WaitHealthyAsync(timeout, ct);
        }
        catch
        {
            await hub.StopAsync();
            await hub.DisposeAsync();
            throw;
        }

        return hub;
    }

    /// <summary>正常停止（admin stop --wait 20，帶 --data）並等程序結束；30 秒等不到才以 PID 結束整個程序樹，刪掉這次啟動建、伺服器來不及刪的金鑰檔</summary>
    public async Task<HubStopResult> StopAsync()
    {
        lock (gate)
        {
            if (stopped)
                return new HubStopResult(true, null, [], []);
            stopped = true;
        }

        int? adminCode = null;
        var graceful = false;
        if (!process.HasExited)
        {
            var admin = await AdminWrapper.StopAsync(ExePath, DataDir);
            adminCode = admin.ExitCode;
            if (admin.ExitCode != 0)
                Record($"[probe] admin stop 結束碼 {admin.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "逾時"}：{admin.Output}{admin.Error}");
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(wait.Token);
                graceful = true;
            }
            catch (OperationCanceledException)
            {
                Record("[probe] MyriagentHub 30 秒內沒有正常結束，以 PID 強制結束");
                KillTree();
            }
        }
        else
        {
            graceful = process.ExitCode == 0;
        }

        var deleted = new List<string>();
        if (!graceful)
        {
            foreach (var file in KeyFilesNaming(Needle).Where(f => !keysBefore.Contains(f)))
            {
                try
                {
                    File.Delete(Path.Combine(KeysDirectory, file));
                    deleted.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Record($"[probe] 刪不掉強制結束留下的金鑰檔 {file}：{ex.Message}");
                }
            }
        }

        CountReasons();
        return new HubStopResult(graceful, adminCode, KeyFilesNaming(Needle), deleted);
    }

    /// <summary>
    /// 子程序主控台輸出的編碼：CREATE_NO_WINDOW 的子程序有自己的（看不見的）主控台，.NET 以它的碼頁（系統的 OEM 碼頁，繁中 cp950）寫
    /// stdout。只影響 hub.log 好不好讀，中斷原因不從這裡數
    /// </summary>
    private static Encoding ConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new UTF8Encoding(false);
        }
    }

    /// <summary>停止之後從記錄檔數中斷原因（只讀、共用讀寫刪）；讀不到就留 null</summary>
    private void CountReasons()
    {
        try
        {
            var lines = new List<string>();
            foreach (var file in Directory.GetFiles(FileLogDirectory, "myriagenthub-*.log").Order(StringComparer.Ordinal))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                while (reader.ReadLine() is { } line)
                    lines.Add(line);
            }

            var counts = HubLogRules.CountFileLog(lines);
            lock (gate)
                reasons = counts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Record($"[probe] 讀不到 MyriagentHub 的記錄檔（{FileLogDirectory}）：{ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
            await StopAsync();
        process.Dispose();
        lock (gate)
            log.Dispose();
        try
        {
            Directory.Delete(CopyDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 程序剛結束、檔案還鎖著：留著（%TEMP% 底下）
        }
    }

    private async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"MyriagentHub 啟動後就結束了（結束碼 {process.ExitCode}）；記錄見 {((FileStream)log.BaseStream).Name}");
            try
            {
                using var response = await http.GetAsync(BaseUrl + "/healthz", ct);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException($"MyriagentHub {timeout.TotalSeconds:0} 秒內沒有就緒");
    }

    private void KillTree()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Record(string? line)
    {
        if (line is null)
            return;
        lock (gate)
        {
            try
            {
                log.WriteLine(line);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
