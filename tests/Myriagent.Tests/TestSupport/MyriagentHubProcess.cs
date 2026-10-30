using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Security;
using Myriagent.Tests.Sync.WithHub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 以子程序執行的 MyriagentHub（真的 Kestrel、自簽憑證、Testing 環境、127.0.0.1 的高埠、速率限制關閉、file 憑證庫；參數照
/// MyriagentHub.Tests 的 HubTestHost.Arguments）。一律以 <c>admin stop</c> 正常停止（D5：強制結束會在使用者金鑰存放區留下孤兒金鑰檔），
/// 逾時才整個程序樹強制結束並記下 <see cref="ForcedKill"/>。子程序加進桌面的工作物件（測試主機當掉時一起結束）。
/// 強制結束（<see cref="KillAsync"/>）時伺服器來不及刪自己的 TLS 私鑰檔：每次啟動記下這個實例建的金鑰檔（啟動前後的差集，而且內容記著
/// 本程序複製出來的 MyriagentHub.exe），程序結束後由這裡刪掉。不能只依建立者的路徑刪：重啟後的新實例路徑相同，會刪到正在執行的那個的私鑰
/// </summary>
internal sealed class MyriagentHubProcess : IAsyncDisposable
{
    /// <summary>預設的 <c>Hub:PublicOrigin</c>（擁有者用戶端的 Origin 標頭跟著它）</summary>
    public const string DefaultPublicOrigin = "https://localhost";

    /// <summary>同一個測試程序一次只啟動一個 MyriagentHub（啟動前後的金鑰檔差集才分得出是哪個實例建的）</summary>
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);

    private readonly ConcurrentQueue<string> output = new();
    private readonly TempDirectory? ownedData;
    private readonly HashSet<string> instanceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly string exePath;
    private readonly string keyNeedle;
    private Process? process;

    private MyriagentHubProcess(string dataDir, TempDirectory? ownedData, int port, string exePath, string keyNeedle, bool publicOriginIsBaseAddress)
    {
        DataDir = dataDir;
        this.ownedData = ownedData;
        Port = port;
        this.exePath = exePath;
        this.keyNeedle = keyNeedle;
        var authority = $"https://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        BaseAddress = new Uri(authority + "/");
        PublicOrigin = publicOriginIsBaseAddress ? authority : DefaultPublicOrigin;
    }

    public string DataDir { get; }

    public int Port { get; }

    /// <summary>https://127.0.0.1:埠/</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// 伺服器的 <c>Hub:PublicOrigin</c>：預設 <see cref="DefaultPublicOrigin"/>；遠端指令的情境是配對網址本身（https://127.0.0.1:埠），
    /// 信封的 aud 才等於電腦由配對網址推得的（WP4.4 計畫 D5）
    /// </summary>
    public string PublicOrigin { get; }

    /// <summary>停止時等不到正常結束、強制結束過</summary>
    public bool ForcedKill { get; private set; }

    /// <summary>最近一次啟動子程序的時間點（Stopwatch 時間戳）：補送的耗時從這裡算比從 /healthz 回 200 算保守（探針連不上時要等約 2 秒才重試）</summary>
    public long LaunchedAt { get; private set; }

    public bool IsRunning => process is { HasExited: false };

    /// <summary>目前這個實例建的金鑰檔（使用者金鑰存放區的檔名；正常停止或強制結束刪掉之後清空）</summary>
    public IReadOnlyCollection<string> InstanceKeys => instanceKeys.ToList();

    /// <summary>程序最後的輸出（失敗時附在訊息裡）</summary>
    public string Tail => string.Join(Environment.NewLine, output);

    public string HubDbPath => System.IO.Path.Combine(DataDir, "db", "hub.db");

    public string SyncDbPath => System.IO.Path.Combine(DataDir, "db", "sync.db");

    public string IdentityKeyFile => System.IO.Path.Combine(DataDir, "secrets", "hub-identity.dpapi");

    /// <summary>執行的 MyriagentHub.exe（複製出來的）</summary>
    public string ExePath => exePath;

    /// <summary>這個 exe 建的金鑰檔（使用者金鑰存放區裡記著這個 exe 路徑的；正常停止後應該沒有）</summary>
    public IReadOnlyList<string> KeyFilesOfThisExe() => UserKeyStore.FilesNaming(keyNeedle);

    /// <param name="dataDir">資料根；null＝新的暫存資料夾（結束時刪掉）</param>
    /// <param name="alternate">
    /// 改用另一個 MyriagentHub.exe（混合版本的情境）：（名稱, 來源 exe）。整個資料夾複製到 <c>myriagenthub-{名稱}-{pid}</c>（<see cref="MyriagentHubExe.CopyAlternate"/>），
    /// 金鑰檔以它的路徑認；null＝同一棵樹建出的（<see cref="MyriagentHubExe.Path"/>）
    /// </param>
    /// <param name="publicOriginIsBaseAddress">true＝<c>Hub:PublicOrigin</c> 是 <see cref="BaseAddress"/> 的來源（遠端指令的情境，見 <see cref="PublicOrigin"/>）</param>
    public static async Task<MyriagentHubProcess> StartAsync(string? dataDir = null, (string Name, string SourceExe)? alternate = null, bool publicOriginIsBaseAddress = false)
    {
        var owned = dataDir is null ? new TempDirectory("myriagenthub-data") : null;
        var (exe, needle) = alternate is { } other
            ? (MyriagentHubExe.CopyAlternate(other.Name, other.SourceExe), MyriagentHubExe.KeyNeedle(MyriagentHubExe.AlternateDirectoryName(other.Name)))
            : (MyriagentHubExe.Path, MyriagentHubExe.KeyNeedle(MyriagentHubExe.CopyDirectoryName));
        var hub = new MyriagentHubProcess(dataDir ?? owned!.Root, owned, FreePort(), exe, needle, publicOriginIsBaseAddress);
        try
        {
            await hub.LaunchAsync();
        }
        catch
        {
            await hub.DisposeAsync();
            throw;
        }

        return hub;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>MyriagentHub.Tests 的 HubTestHost.Arguments 的副本（不參考 MyriagentHub.Tests）</summary>
    public static IReadOnlyList<string> Arguments(string dataDir, int port, string publicOrigin = DefaultPublicOrigin)
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
            ["Hub:FileLog:Enabled"] = "false",
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

    /// <summary>以同一個資料根與埠啟動並等 /healthz；記下這個實例啟動時建的金鑰檔</summary>
    public async Task LaunchAsync()
    {
        if (IsRunning)
            throw new InvalidOperationException("MyriagentHub 已經在執行");
        await LaunchGate.WaitAsync();
        try
        {
            var before = UserKeyStore.Snapshot().ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                await LaunchCoreAsync();
            }
            finally
            {
                // TLS 憑證在 /healthz 回 200 之前就載入了（私鑰落地成金鑰檔）；沒就緒被強制結束時也要記下，KillAsync 才刪得掉
                RecordInstanceKeys(before);
            }
        }
        finally
        {
            LaunchGate.Release();
        }
    }

    private void RecordInstanceKeys(HashSet<string> before)
    {
        var mine = KeyFilesOfThisExe().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in UserKeyStore.Snapshot())
        {
            if (!before.Contains(file) && mine.Contains(file))
                instanceKeys.Add(file);
        }

        if (process is { HasExited: true })
            DeleteInstanceKeys();
    }

    /// <summary>程序已經結束：刪掉這個實例建、伺服器來不及刪的金鑰檔（刪不掉只記錄）</summary>
    private void DeleteInstanceKeys()
    {
        foreach (var file in instanceKeys)
        {
            var path = System.IO.Path.Combine(UserKeyStore.Directory, file);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Record($"[test] 刪掉強制結束留下的金鑰檔 {file}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪不掉 MyriagentHub 強制結束留下的金鑰檔 {file}：{ex.Message}");
            }
        }

        instanceKeys.Clear();
    }

    /// <summary>
    /// 下一次 <see cref="LaunchAsync"/> 另外加的設定（例如 <c>Hub:Sync:MinAppVersion</c>；app 自我更新 U3 的只更新情境）：
    /// 排在預設的後面，同名的蓋掉預設值
    /// </summary>
    public Dictionary<string, string> ExtraSettings { get; } = new(StringComparer.OrdinalIgnoreCase);

    private async Task LaunchCoreAsync()
    {
        process?.Dispose();
        var info = NewStartInfo(Arguments(DataDir, Port, PublicOrigin).Concat(ExtraSettings.Select(kv => $"--{kv.Key}={kv.Value}")));
        var started = new Process { StartInfo = info };
        started.OutputDataReceived += (_, e) => Record(e.Data);
        started.ErrorDataReceived += (_, e) => Record(e.Data);
        LaunchedAt = Stopwatch.GetTimestamp();
        started.Start();
        ChildProcessJob.TryAssign(started);
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        process = started;

        using var http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (started.HasExited)
                break;
            try
            {
                using var response = await http.GetAsync(new Uri(BaseAddress, "/healthz"));
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(50);
        }

        var tail = Tail;
        await KillAsync();
        throw new TimeoutException($"MyriagentHub 子程序沒有就緒（{watch.Elapsed.TotalSeconds:0.0} 秒，exited={started.HasExited}）：{Environment.NewLine}{tail}");
    }

    /// <summary>正常停止（admin stop --wait 20）並等程序結束；逾時才強制結束</summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
            return;
        var (code, text, error) = await RunAdminAsync("stop", "--wait", "20");
        if (code != 0)
            Record($"[test] admin stop 結束碼 {code}：{text}{error}");
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process!.WaitForExitAsync(wait.Token);
            // 正常結束：伺服器自己刪了金鑰檔（沒刪的話 MyriagentHubFixture 的檢查會抓到，這裡不代刪）
            instanceKeys.Clear();
        }
        catch (OperationCanceledException)
        {
            ForcedKill = true;
            Log.Warn($"MyriagentHub 子程序 30 秒內沒有正常結束，強制結束（刪掉它留下的金鑰檔）：{Tail}");
            await KillAsync();
        }
    }

    /// <summary>
    /// 暫停整個程序（NtSuspendProcess）：連線還在、送來的資料留在核心的緩衝區，伺服器卻處理不了也回不了——
    /// S8 用來保證強制結束的那一刻有批次在途（沒有被處理、也沒有 BatchAck）。之後一定要 <see cref="KillAsync"/>
    /// </summary>
    public void Suspend()
    {
        if (process is not { HasExited: false } running)
            throw new InvalidOperationException("MyriagentHub 沒有在執行");
        var status = NtSuspendProcess(running.Handle);
        if (status != 0)
            throw new InvalidOperationException($"NtSuspendProcess 失敗：0x{status:X8}");
    }

    /// <summary>整個程序樹強制結束（只給刻意測強制結束的測試用）；結束後刪掉這個實例來不及刪的金鑰檔</summary>
    public async Task KillAsync()
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
        }

        if (process.HasExited)
            DeleteInstanceKeys();
        else
            Log.Warn($"MyriagentHub 子程序 10 秒內沒有結束，它的金鑰檔先不刪：{string.Join(", ", instanceKeys)}");
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await LaunchAsync();
    }

    /// <summary><c>MyriagentHub.exe admin &lt;args&gt; --data &lt;資料根&gt;</c>：回傳結束碼、標準輸出（一個 JSON 物件）與標準錯誤</summary>
    public async Task<(int ExitCode, string Output, string Error)> RunAdminAsync(params string[] args)
    {
        var all = new List<string> { "admin" };
        all.AddRange(args);
        all.Add("--data");
        all.Add(DataDir);
        var info = NewStartInfo(all);
        using var admin = Process.Start(info) ?? throw new InvalidOperationException("MyriagentHub admin 啟動失敗");
        ChildProcessJob.TryAssign(admin);
        var stdout = admin.StandardOutput.ReadToEndAsync();
        var stderr = admin.StandardError.ReadToEndAsync();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await admin.WaitForExitAsync(wait.Token);
        return (admin.ExitCode, await stdout, await stderr);
    }

    /// <summary>status\trust.json 的伺服器身分指紋（服務啟動與信任資料變更時寫；本機確認以它為準）</summary>
    public string ReadTrustFingerprint()
    {
        var json = JObject.Parse(File.ReadAllText(System.IO.Path.Combine(DataDir, "status", "trust.json")));
        return json.Value<string>("hubIdentityFingerprint") ?? throw new InvalidDataException("trust.json 沒有 hubIdentityFingerprint");
    }

    /// <summary>直接以 TLS 連一次，回傳伺服器葉憑證的 SPKI 雜湊（HandshakeRules.SpkiSha256）</summary>
    public async Task<string> CertificateSpkiAsync()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Port);
        X509Certificate2? seen = null;
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            seen = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return true;
        });
        await tls.AuthenticateAsClientAsync("localhost");
        using var leaf = seen ?? throw new InvalidOperationException("沒有看到伺服器憑證");
        return HandshakeRules.SpkiSha256(leaf);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        process?.Dispose();
        ownedData?.Dispose();
    }

    private ProcessStartInfo NewStartInfo(IEnumerable<string> args)
    {
        var exe = exePath;
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        // 不繼承測試主機的 ASP.NET Core／.NET 環境變數與同步覆寫
        foreach (var key in info.Environment.Keys
            .Where(k => k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("MYRIAGENT_SYNC_", StringComparison.OrdinalIgnoreCase))
            .ToList())
            info.Environment.Remove(key);
        return info;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    private void Record(string? line)
    {
        if (line is null)
            return;
        output.Enqueue(line);
        while (output.Count > 300)
            output.TryDequeue(out _);
    }
}
