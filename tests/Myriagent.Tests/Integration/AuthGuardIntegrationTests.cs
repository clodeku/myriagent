using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// IT-A1（設計 11.2、6.5，S4）：auth status 守門。隔離的 CLAUDE_CONFIG_DIR＋捏造的過期憑證＋只記主機名稱的本機 CONNECT 代理
/// （研究 tools/probes/auth/exp_auth_status.py 的作法）：app 的帳戶檢查流程（手動更新、憑證檔變動、啟動、能力探查）完全不連 platform.claude.com；
/// 對照組直接跑 claude auth status 會連過去（證明代理看得到換發權杖的連線）。權杖都是捏造的，換發一定被拒：不耗額度、不碰真實帳戶。
/// IT-A2：閒置帳戶自動換發（ClaudeTokenRecoveryRules）刻意走的另一條路——跑一次會等結果的 claude -p 讓 CLI 換發；同樣用捏造的憑證，換發一定被拒。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~Integration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AuthGuardIntegrationTests : IDisposable
{
    private const string Reason = "實際執行本機 Claude CLI（隔離的設定資料夾、捏造的權杖；不耗額度、不碰真實帳戶）";
    private const string ClaudeVariable = "MYRIAGENT_IT_A1_CLAUDE";
    private const string ProxyVariable = "MYRIAGENT_IT_A1_PROXY";

    /// <summary>經代理執行真的 claude 的殼（只寫 ASCII）：記下每次呼叫的參數，HTTPS_PROXY 指向本機代理</summary>
    private const string WrapperScript = "@echo off\r\n"
                                         + ">>\"%~dp0invocations.txt\" echo %*\r\n"
                                         + "set \"HTTPS_PROXY=%" + ProxyVariable + "%\"\r\n"
                                         + "set \"HTTP_PROXY=%" + ProxyVariable + "%\"\r\n"
                                         + "set \"NO_PROXY=\"\r\n"
                                         + "\"%" + ClaudeVariable + "%\" %*\r\n"
                                         + "exit /b %ERRORLEVEL%\r\n";

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-auth-guard");
    private readonly AppPathsScope paths;
    private readonly UiThread ui = new();

    public AuthGuardIntegrationTests(ITestOutputHelper output)
    {
        this.output = output;
        paths = new AppPathsScope(temp.Combine("data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ClaudeVariable, null);
        Environment.SetEnvironmentVariable(ProxyVariable, null);
        ui.Dispose();
        paths.Dispose();
        temp.Dispose();
    }

    /// <summary>捏造的憑證（研究的作法：權杖前綴在執行時才組出來，原始碼不含權杖格式的字面值）</summary>
    private static void WriteFakeCredentials(string configDirectory, DateTimeOffset expiresAt)
    {
        var oauth = new JObject
        {
            ["accessToken"] = "sk-ant-" + "oat01-" + "FAKE-IT-A1-NOT-A-REAL-TOKEN-" + new string('A', 40),
            ["refreshToken"] = "sk-ant-" + "ort01-" + "FAKE-IT-A1-NOT-A-REAL-TOKEN-" + new string('B', 40),
            ["expiresAt"] = expiresAt.ToUnixTimeMilliseconds(),
            ["scopes"] = new JArray("user:inference", "user:profile", "user:sessions:claude_code"),
            ["subscriptionType"] = "max",
            ["rateLimitTier"] = "default_claude_max_20x",
        };
        File.WriteAllText(Path.Combine(configDirectory, ".credentials.json"), new JObject { ["claudeAiOauth"] = oauth }.ToString());
    }

    private List<string> Invocations(string wrapperDirectory)
    {
        var file = Path.Combine(wrapperDirectory, "invocations.txt");
        if (!File.Exists(file))
            return new List<string>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    private void Post(Action action)
    {
        if (ui.IsCurrent)
            action();
        else
            ui.Post(action);
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_A1_捏造的過期憑證_App的帳戶檢查流程完全不連platform_claude_com()
    {
        var real = CliLocator.Locate(AgentProvider.Claude);
        real.Should().NotBeNull("這台電腦要安裝 Claude Code CLI");
        var exe = real!.NativeExecutable ?? real.Launcher.FileName;
        exe.Should().EndWith(".exe", "以原生 claude.exe 執行（npm 2.1.113 起也是 bin\\claude.exe）");

        using var proxy = new HostLoggingProxy();
        var wrapperDirectory = temp.CreateDirectory("wrapper");
        File.WriteAllText(Path.Combine(wrapperDirectory, "claude-via-proxy.cmd"), WrapperScript, Encoding.ASCII);
        Environment.SetEnvironmentVariable(ClaudeVariable, exe);
        Environment.SetEnvironmentVariable(ProxyVariable, $"http://127.0.0.1:{proxy.Port}");
        var launcher = new AgentLauncher(Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", Path.Combine(wrapperDirectory, "claude-via-proxy.cmd") });

        var configDirectory = temp.CreateDirectory("config");
        WriteFakeCredentials(configDirectory, DateTimeOffset.UtcNow.AddHours(-1));
        var account = new AgentAccount { Id = Guid.NewGuid().ToString("N"), Provider = AgentProvider.Claude, Label = "IT-A1", ConfigDirectory = configDirectory };
        var usage = new StubHttpHandler();   // 過期的權杖不查用量：有請求就會丟例外
        using var http = new HttpClient(usage);
        using var cli = new CliManager(http, _ => false, () => false, startBackgroundChecks: false);
        // 這裡守的是帳戶檢查流程（auth status、能力探查）；閒置帳戶自動換發是刻意讓 CLI 換發的另一條路（IT-A2），這裡關掉
        var manager = ui.Invoke(() => new AccountManager(Post, cli, http, _ => 0) { ResolveLauncher = _ => launcher, NeedsCapabilities = _ => true, ClaudeTokenRefreshEnabled = () => false });
        try
        {
            ui.Invoke(() => manager.Seed(new[] { account }, AccountStatus.Checking));

            // app 的帳戶檢查流程：手動更新、憑證檔變動、啟動、能力探查
            await ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: true)).WaitAsync(TimeSpan.FromSeconds(60));
            await ui.Invoke(() => manager.RefreshAsync(account.Id, true, false, ClaudeStatusReason.CredentialsChanged)).WaitAsync(TimeSpan.FromSeconds(60));
            await ui.Invoke(() => manager.RefreshAsync(account.Id, true, false, ClaudeStatusReason.Startup)).WaitAsync(TimeSpan.FromSeconds(60));
            (await CapabilityProbe.ProbeClaudeAsync(launcher, account, CancellationToken.None)).Should().BeNull("能力探查也過同一道守門");
            await Task.Delay(1500);

            output.WriteLine($"app 的檢查：claude 呼叫 {Invocations(wrapperDirectory).Count} 次，代理看到的連線：{string.Join("、", proxy.Hosts)}");
            Invocations(wrapperDirectory).Should().BeEmpty("權杖已過期：app 不跑任何短命的 claude（auth status、能力探查）");
            proxy.Hosts.Should().NotContain(h => h.StartsWith("platform.claude.com", StringComparison.OrdinalIgnoreCase), "S4：app 的帳戶檢查流程完全不連權杖端點的主機");
            usage.Requests.Should().BeEmpty("過期的權杖也不查用量（app 絕不自己換發權杖）");
            var state = ui.Invoke(() => manager.GetState(account.Id));
            state.Status.Should().Be(AccountStatus.Ready);
            state.StatusText.Should().Be("已登入（權杖待更新）");

            // 對照組（app 不會這樣做）：同一份過期的假憑證直接跑 auth status，CLI 連到 platform.claude.com 開始換發（研究 T3）——代理看得到這種連線
            var control = await CliRunner.RunAsync(launcher, new[] { "auth", "status", "--json" }, env => AccountRules.ApplyCommandEnvironment(account, env), TimeSpan.FromSeconds(60));
            await Task.Delay(1500);
            output.WriteLine($"對照組：結束碼 {control.ExitCode}，代理看到的連線：{string.Join("、", proxy.Hosts)}");
            proxy.Hosts.Should().Contain(h => h.StartsWith("platform.claude.com", StringComparison.OrdinalIgnoreCase),
                "直接跑 auth status 會開始換發權杖：證明這個代理看得到 app 該避免的連線（這是 app 守門的理由）");

            // 權杖新鮮（捏造、3 小時後才到期）：app 照常跑 auth status，也不碰權杖端點
            proxy.Clear();
            WriteFakeCredentials(configDirectory, DateTimeOffset.UtcNow.AddHours(3));
            ui.Invoke(() => manager.NeedsCapabilities = _ => false);
            var before = Invocations(wrapperDirectory).Count;
            await ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: true)).WaitAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(1500);
            output.WriteLine($"權杖新鮮：claude 呼叫 {Invocations(wrapperDirectory).Count - before} 次，代理看到的連線：{string.Join("、", proxy.Hosts)}");
            // 手動更新一次＋憑證檔變動（監看）一次：權杖新鮮時兩者都照常查
            Invocations(wrapperDirectory).Skip(before).Should().NotBeEmpty().And.OnlyContain(i => i == "auth status --json");
            proxy.Hosts.Should().NotContain(h => h.StartsWith("platform.claude.com", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ui.Invoke(manager.Dispose);
        }
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_A2_閒置帳戶權杖過期_讓CLI跑一次claude_p換發_被拒後不再重跑()
    {
        var real = CliLocator.Locate(AgentProvider.Claude);
        real.Should().NotBeNull("這台電腦要安裝 Claude Code CLI");
        var exe = real!.NativeExecutable ?? real.Launcher.FileName;

        using var proxy = new HostLoggingProxy();
        var wrapperDirectory = temp.CreateDirectory("wrapper-a2");
        File.WriteAllText(Path.Combine(wrapperDirectory, "claude-via-proxy.cmd"), WrapperScript, Encoding.ASCII);
        Environment.SetEnvironmentVariable(ClaudeVariable, exe);
        Environment.SetEnvironmentVariable(ProxyVariable, $"http://127.0.0.1:{proxy.Port}");
        var launcher = new AgentLauncher(Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", Path.Combine(wrapperDirectory, "claude-via-proxy.cmd") });

        var configDirectory = temp.CreateDirectory("config-a2");
        WriteFakeCredentials(configDirectory, DateTimeOffset.UtcNow.AddHours(-1));
        var account = new AgentAccount { Id = Guid.NewGuid().ToString("N"), Provider = AgentProvider.Claude, Label = "IT-A2", ConfigDirectory = configDirectory };
        var usage = new StubHttpHandler();   // 沒有同身分的登入可以借、捏造的權杖換發不了：有用量請求就會丟例外
        using var http = new HttpClient(usage);
        using var cli = new CliManager(http, _ => false, () => false, startBackgroundChecks: false);
        var manager = ui.Invoke(() => new AccountManager(Post, cli, http, _ => 0) { ResolveLauncher = _ => launcher, ClaudeTokenRefreshEnabled = () => true });
        try
        {
            ui.Invoke(() => manager.Seed(new[] { account }, AccountStatus.Checking));

            await ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: true)).WaitAsync(TimeSpan.FromSeconds(60));
            var deadline = DateTime.UtcNow.AddSeconds(150);
            while (Invocations(wrapperDirectory).Count == 0 || ui.Invoke(() => manager.IsRefreshingClaudeToken(account.Id)))
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "換發要在時限內結束（RefreshTimeout 2 分鐘）");
                await Task.Delay(100);
            }
            await Task.Delay(1500);   // 結束後的重新確認與重查
            var credentials = JObject.Parse(File.ReadAllText(Path.Combine(configDirectory, ".credentials.json")));
            var oauth = (JObject?)credentials["claudeAiOauth"];
            var state = ui.Invoke(() => manager.GetState(account.Id));
            var snapshot = ui.Invoke(() => manager.GetUsage(account.Id));
            output.WriteLine($"呼叫：{string.Join(" | ", Invocations(wrapperDirectory))}");
            output.WriteLine($"代理看到的連線：{string.Join("、", proxy.Hosts)}");
            output.WriteLine($"換發後：accessToken 長度 {((string?)oauth?["accessToken"])?.Length}、refreshToken 長度 {((string?)oauth?["refreshToken"])?.Length}、expiresAt {(long?)oauth?["expiresAt"]}");
            output.WriteLine($"狀態：{state.Status}「{state.StatusText}」；用量說明：{snapshot?.Error}");

            // 2026-10-26 起：不送訊息的控制程序問 get_usage（ClaudeControlClient），CLI 查的時候自己換發
            Invocations(wrapperDirectory).Should().ContainSingle().Which.Should().StartWith("-p --input-format stream-json --output-format stream-json")
                .And.Contain("--no-session-persistence");
            proxy.Hosts.Should().Contain(h => h.StartsWith("platform.claude.com", StringComparison.OrdinalIgnoreCase), "CLI 自己連權杖端點換發");
            usage.Requests.Should().BeEmpty("app 不用捏造的權杖查用量，也沒有自己換發");
            // 2026-10-22 實測 2.1.306：換發被拒後 CLI 清空登入（accessToken、refreshToken 空字串、expiresAt 0，同研究 P1）→ app 判未登入
            ((string?)oauth?["accessToken"]).Should().BeEmpty("CLI 換發被拒後清空登入");
            state.Status.Should().Be(AccountStatus.NotLoggedIn);
            state.StatusText.Should().Be("登入已失效，請重新登入");

            // 再查一次：不重跑（被拒後一小時內不再試、或已判未登入）
            await ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: true)).WaitAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(1500);
            Invocations(wrapperDirectory).Should().ContainSingle("一次換發被拒後不重跑");
        }
        finally
        {
            ui.Invoke(manager.Dispose);
        }
    }
}

/// <summary>
/// 只記主機名稱的本機 CONNECT 代理（研究 exp_auth_status.py 的作法）：記下 CONNECT 的目標，原樣轉送位元組，不解 TLS、不看內容
/// </summary>
internal sealed class HostLoggingProxy : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentQueue<string> hosts = new();

    public HostLoggingProxy()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    /// <summary>CONNECT 的目標（host:port），依連線順序</summary>
    public IReadOnlyList<string> Hosts => hosts.ToList();

    public void Clear() => hosts.Clear();

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(one, stop.Token) == 0)
                        return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n')
                        break;
                }
                var line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0];
                var parts = line.Split(' ');
                if (parts.Length < 2)
                    return;
                hosts.Enqueue(parts[1]);
                if (!string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n"), stop.Token);
                    return;
                }
                var colon = parts[1].LastIndexOf(':');
                using var upstream = new TcpClient();
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(20));
                    await upstream.ConnectAsync(parts[1][..colon], int.Parse(parts[1][(colon + 1)..]), connect.Token);
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), stop.Token);
                var remote = upstream.GetStream();
                await Task.WhenAny(stream.CopyToAsync(remote, stop.Token), remote.CopyToAsync(stream, stop.Token));
            }
            catch (Exception)
            {
                // 連線斷了、對方拒絕：代理只負責記主機名稱
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}
