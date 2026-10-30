using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// Claude 權杖過期後的用量恢復（ClaudeTokenRecoveryRules）：借用同身分的其他登入唯讀查用量、借不到時讓 CLI 換發權杖。
/// CLI 換成假的批次檔（-p 時把預備好的 refresh.json 複製成帳戶的憑證檔＝模擬 CLI 換發後寫回；沒有預備就回錯誤），
/// 用量端點換成假的 HTTP（記下每次用的權杖）。帳戶都在暫存資料夾：不碰真的帳戶、不連網、不花額度
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ClaudeUsageRecoveryTests : IDisposable
{
    /// <summary>
    /// 假的 claude：呼叫記進 calls.txt（設定資料夾#參數）；-p＝不送訊息的控制程序（ClaudeControlClient，2026-10-26 起以 get_usage 代查用量）：
    /// hold.flag 存在時先等它被刪掉（模擬跑很久），讀一行回 initialize（c1）、再讀一行（get_usage，c2，記進 calls.txt 的 stdin#），有 refresh.json 就複製成憑證檔
    /// （＝CLI 查用量時換發了權杖）並回用量，沒有就回錯誤；其他（auth status）印 auth.txt
    /// </summary>
    private const string FakeCliScript = """
        @echo off
        >>"%~dp0calls.txt" echo %CLAUDE_CONFIG_DIR%#%*
        if "%~1"=="-p" goto print
        if exist "%~dp0auth.txt" type "%~dp0auth.txt"
        exit /b 0
        :print
        if exist "%~dp0hold.flag" (
          ping -n 2 127.0.0.1 >nul
          goto print
        )
        set /p l1=
        echo {"type":"control_response","response":{"subtype":"success","request_id":"c1","response":{"commands":[]}}}
        set /p l2=
        >>"%~dp0calls.txt" echo stdin#%l2%
        if not exist "%~dp0refresh.json" goto fail
        copy /y "%~dp0refresh.json" "%CLAUDE_CONFIG_DIR%\.credentials.json" >nul
        echo {"type":"control_response","response":{"subtype":"success","request_id":"c2","response":{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":42.0,"resets_at":"2099-01-01T00:00:00+00:00"},"seven_day":{"utilization":7.0,"resets_at":"2099-01-05T00:00:00+00:00"}}}}}
        exit /b 0
        :fail
        echo {"type":"control_response","response":{"subtype":"error","request_id":"c2","error":"API Error: 500 Internal server error"}}
        exit /b 1
        """;

    private const string Email = "me@example.com";
    private const string Org = "11111111-0000-4000-8000-000000000001";

    private readonly TempDirectory temp = new("usage-recovery");
    private readonly AppPathsScope paths;
    private readonly UiThread ui = new();
    private readonly FakeUsageEndpoint endpoint = new();
    private readonly HttpClient http;
    private readonly CliManager cli;
    private readonly AccountManager manager;
    private readonly string fakeDir;
    private int runningTurns;
    private bool refreshEnabled = true;

    public ClaudeUsageRecoveryTests()
    {
        paths = new AppPathsScope(temp.Root);
        http = new HttpClient(endpoint);
        fakeDir = temp.CreateDirectory("fake-cli");
        File.WriteAllText(Path.Combine(fakeDir, "fake.cmd"), FakeCliScript.ReplaceLineEndings("\r\n"));
        File.WriteAllText(Path.Combine(fakeDir, "auth.txt"), RealSamples.ClaudeAuthStatusLoggedIn);
        cli = new CliManager(http, _ => false, () => false, startBackgroundChecks: false);
        var launcher = new AgentLauncher(Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", Path.Combine(fakeDir, "fake.cmd") });
        manager = ui.Invoke(() => new AccountManager(Post, cli, http, _ => Volatile.Read(ref runningTurns))
        {
            ResolveLauncher = _ => launcher,
            ClaudeTokenRefreshEnabled = () => Volatile.Read(ref refreshEnabled),
            // 預設不借用任何沒登記的資料夾（真的 ~/.claude 一律不碰）；個別測試再換成暫存資料夾
            ExtraClaudeUsageSources = () => Array.Empty<AgentAccount>(),
        });
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path.Combine(fakeDir, "hold.flag"));
            ui.DrainAsync(5).Wait(TimeSpan.FromSeconds(10));
            ui.Invoke(manager.Dispose);
        }
        finally
        {
            ui.Dispose();
            cli.Dispose();
            http.Dispose();
            paths.Dispose();
            temp.Dispose();
        }

        if (!ui.Errors.IsEmpty)
            throw new XunitException("UI 執行緒有未處理的例外：" + string.Join("\n", ui.Errors.Select(e => e.ToString())));
    }

    private void Post(Action action)
    {
        if (ui.IsCurrent)
            action();
        else
            ui.Post(action);
    }

    /// <summary>暫存資料夾裡的隔離 Claude 帳戶（還沒放進管理員）：權杖、到期時間與 CLI 設定的身分</summary>
    private static AgentAccount NewClaude(string label, string token, DateTimeOffset expiresAt, string? email = Email, string? org = Org)
    {
        var id = Guid.NewGuid().ToString("N");
        var account = new AgentAccount { Id = id, Provider = AgentProvider.Claude, Label = label, ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, id, "claude") };
        Directory.CreateDirectory(account.ConfigDirectory);
        File.WriteAllText(CredentialFile(account), Credentials(token, expiresAt).ToString());
        if (email != null)
        {
            var oauth = new JObject { ["accountUuid"] = "u-1", ["emailAddress"] = email };
            if (org != null)
                oauth["organizationUuid"] = org;
            File.WriteAllText(Path.Combine(account.ConfigDirectory, ".claude.json"), new JObject { ["numStartups"] = 3, ["oauthAccount"] = oauth }.ToString());
        }
        return account;
    }

    /// <summary>捏造的憑證（權杖是假的字串）；accessToken、refreshToken 可以給空字串（CLI 換發被拒後清空的樣子）</summary>
    private static JObject Credentials(string accessToken, DateTimeOffset expiresAt, string refreshToken = "sk-ant-ort01-test")
        => new()
        {
            ["claudeAiOauth"] = new JObject
            {
                ["accessToken"] = accessToken,
                ["refreshToken"] = refreshToken,
                ["expiresAt"] = expiresAt.ToUnixTimeMilliseconds(),
                ["subscriptionType"] = "max",
            },
        };

    private static string CredentialFile(AgentAccount account) => Path.Combine(account.ConfigDirectory!, ".credentials.json");

    private void Seed(params AgentAccount[] accounts) => ui.Invoke(() => manager.Seed(accounts, AccountStatus.Ready));

    /// <summary>預備 CLI 換發後要寫回的憑證（假的 claude -p 會複製它）</summary>
    private void PrepareRefresh(JObject credentials) => File.WriteAllText(Path.Combine(fakeDir, "refresh.json"), credentials.ToString());

    /// <summary>假 CLI 被呼叫的紀錄（「設定資料夾#參數」，批次檔可能正在附加，用共用模式讀）</summary>
    private List<string> Calls()
    {
        var file = Path.Combine(fakeDir, "calls.txt");
        if (!File.Exists(file))
            return new List<string>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    private List<string> RefreshCalls(AgentAccount account) => Calls().Where(c => c.StartsWith(account.ConfigDirectory + "#-p ", StringComparison.Ordinal)).ToList();

    private UsageSnapshot? Usage(AgentAccount account) => ui.Invoke(() => manager.GetUsage(account.Id));

    private AccountRuntimeState State(AgentAccount account) => ui.Invoke(() => manager.GetState(account.Id));

    private async Task RefreshUsage(AgentAccount account) => await ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: false)).WaitAsync(TimeSpan.FromSeconds(15));

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new XunitException($"{timeoutMs} ms 內沒有等到：{because}");
            await Task.Delay(20);
        }
    }

    /// <summary>等換發結束、結束後的重查也跑完（畫面上的用量是最後的結果）</summary>
    private async Task WaitRefreshSettledAsync(AgentAccount account, Func<UsageSnapshot?, bool> settled, string because)
    {
        await WaitUntilAsync(() => !ui.Invoke(() => manager.IsRefreshingClaudeToken(account.Id)) && settled(Usage(account)), because);
        await ui.DrainAsync(5);
    }

    // ───── 借用同身分的登入 ─────

    [Fact]
    public async Task 權杖過期_同身分的其他帳戶權杖有效_借它的權杖查用量_不跑CLI()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        var fresh = NewClaude("新鮮的", "tok-fresh", DateTimeOffset.UtcNow.AddHours(5));
        Seed(expired, fresh);

        await RefreshUsage(expired);

        endpoint.Tokens.Should().Equal("tok-fresh");
        Usage(expired)!.Stale.Should().BeFalse();
        Usage(expired)!.Source.Should().Be("api", "借用的也是官方用量端點的資料（同步看 api 才會上傳）");
        Usage(expired)!.Windows.Should().HaveCount(3);
        ui.Invoke(() => manager.GetBorrowedUsageSource(expired.Id)).Should().Be("新鮮的");
        Calls().Should().BeEmpty("借得到就不必讓 CLI 換發（不花額度）");
    }

    [Fact]
    public async Task 權杖過期_沒登記的本機預設設定資料夾是同身分_借它查用量()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        var local = NewClaude("沒登記的", "tok-local", DateTimeOffset.UtcNow.AddHours(2));
        Seed(expired);
        ui.Invoke(() => { manager.ExtraClaudeUsageSources = () => new[] { local }; });

        await RefreshUsage(expired);

        endpoint.Tokens.Should().Equal("tok-local");
        Usage(expired)!.Stale.Should().BeFalse();
        ui.Invoke(() => manager.GetBorrowedUsageSource(expired.Id)).Should().Be("本機預設設定資料夾");
    }

    [Fact]
    public async Task 權杖過期_同email不同org不借_改讓CLI換發後用自己的新權杖查()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        var otherOrg = NewClaude("別的組織", "tok-other", DateTimeOffset.UtcNow.AddHours(5), org: "another-org");
        Seed(expired, otherOrg);
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));

        await RefreshUsage(expired);
        // CLI 代查到的用量先存下（不過時），之後換發好的權杖再查一次用量端點：兩個都等到
        await WaitRefreshSettledAsync(expired, u => u is { Stale: false } && endpoint.Tokens.Contains("tok-renewed"), "CLI 換發後用新權杖查到用量");

        endpoint.Tokens.Should().NotContain("tok-other", "別的組織的用量不是這個帳戶的");
        endpoint.Tokens.Should().Contain("tok-renewed");
        var call = RefreshCalls(expired).Should().ContainSingle().Subject;
        call.Should().Contain("#-p --input-format stream-json --output-format stream-json").And.Contain("--safe-mode")
            .And.Contain("--no-session-persistence").And.NotContain("--model", "不送訊息、不用額度：只問 get_usage");
        Calls().Should().Contain(c => c.StartsWith("stdin#", StringComparison.Ordinal) && c.Contains("\"subtype\":\"get_usage\""), "CLI 代查用量（get_usage）");
        State(expired).Status.Should().Be(AccountStatus.Ready);
        ui.Invoke(() => manager.GetBorrowedUsageSource(expired.Id)).Should().BeNull();
    }

    [Fact]
    public async Task CLI代查到的用量_先存成這個帳戶的_來源是api()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));
        var observed = new ConcurrentQueue<UsageSnapshot>();
        ui.Invoke(() => manager.UsageObserved += (id, snapshot) =>
        {
            if (id == expired.Id)
                observed.Enqueue(snapshot);
        });

        await RefreshUsage(expired);
        await WaitUntilAsync(() => observed.Any(u => u.Windows.Any(w => w.Percent == 42)), "存下 CLI 代查的用量（假 CLI 的 5 小時 42%）");

        var fromCli = observed.First(u => u.Windows.Any(w => w.Percent == 42));
        fromCli.Source.Should().Be("api", "和用量端點同一個形狀：同步照樣上傳");
        fromCli.Stale.Should().BeFalse();
        await WaitRefreshSettledAsync(expired, u => u is { Stale: false } && endpoint.Tokens.Contains("tok-renewed"), "之後照舊以新權杖查用量端點");
    }

    [Fact]
    public async Task 權杖過期_自己的身分不明_不借_讓CLI換發()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3), email: null);
        var fresh = NewClaude("新鮮的", "tok-fresh", DateTimeOffset.UtcNow.AddHours(5));
        Seed(expired, fresh);
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));

        await RefreshUsage(expired);
        await WaitRefreshSettledAsync(expired, u => u is { Stale: false }, "CLI 換發後查到用量");

        endpoint.Tokens.Should().NotContain("tok-fresh", "CLI 設定讀不到自己的 email／org 時不能確定是同一個人");
        RefreshCalls(expired).Should().ContainSingle();
    }

    [Fact]
    public async Task 借用的權杖被用量端點拒絕_改讓CLI換發()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        var fresh = NewClaude("新鮮的", "tok-fresh", DateTimeOffset.UtcNow.AddHours(5));
        Seed(expired, fresh);
        endpoint.Reject("tok-fresh");
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));

        await RefreshUsage(expired);
        await WaitRefreshSettledAsync(expired, u => u is { Stale: false } && endpoint.Tokens.Contains("tok-renewed"), "借用失敗後讓 CLI 換發");

        endpoint.Tokens.Should().StartWith("tok-fresh");
        RefreshCalls(expired).Should().ContainSingle();
        ui.Invoke(() => manager.GetBorrowedUsageSource(expired.Id)).Should().BeNull();
    }

    [Fact]
    public async Task 借用的權杖之後也過期_改讓CLI換發()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        var fresh = NewClaude("新鮮的", "tok-fresh", DateTimeOffset.UtcNow.AddHours(5));
        Seed(expired, fresh);
        await RefreshUsage(expired);
        ui.Invoke(() => manager.GetBorrowedUsageSource(expired.Id)).Should().Be("新鮮的");

        File.WriteAllText(CredentialFile(fresh), Credentials("tok-fresh", DateTimeOffset.UtcNow.AddMinutes(-1)).ToString());
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));
        await RefreshUsage(expired);
        await WaitRefreshSettledAsync(expired, u => u is { Stale: false } && endpoint.Tokens.Contains("tok-renewed"), "改用自己換發的新權杖");

        RefreshCalls(expired).Should().ContainSingle();
        ui.Invoke(() => manager.GetBorrowedUsageSource(expired.Id)).Should().BeNull("用回自己的權杖就不再借用");
    }

    // ───── 讓 CLI 換發 ─────

    [Fact]
    public async Task 設定關掉_不跑CLI_用量標成過時並說明下次使用時更新()
    {
        refreshEnabled = false;
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);

        await RefreshUsage(expired);

        Calls().Should().BeEmpty();
        endpoint.Tokens.Should().BeEmpty("過期的權杖不送用量查詢");
        Usage(expired)!.Stale.Should().BeTrue();
        Usage(expired)!.Error.Should().Be("登入權杖已過期，下次使用這個帳戶時會自動更新");
    }

    [Fact]
    public async Task 換發沒成功_說明原因_一小時內不再跑()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);

        await RefreshUsage(expired);
        await WaitRefreshSettledAsync(expired, u => u?.Error?.Contains("沒有成功") == true, "換發失敗後說明原因");

        Usage(expired)!.Stale.Should().BeTrue();
        Usage(expired)!.Error.Should().StartWith("登入權杖已過期，Claude CLI 換發沒有成功（結束碼 1：API Error: 500 Internal server error）；")
            .And.EndWith("約 1 小時後再試");
        await RefreshUsage(expired);
        RefreshCalls(expired).Should().ContainSingle("距上一次嘗試不到一小時");
    }

    [Fact]
    public async Task 換發被拒_CLI清空登入_改判未登入()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);
        PrepareRefresh(Credentials("", DateTimeOffset.FromUnixTimeMilliseconds(0), refreshToken: ""));

        await RefreshUsage(expired);
        await WaitUntilAsync(() => State(expired).Status == AccountStatus.NotLoggedIn, "CLI 清空登入後判定未登入");

        State(expired).StatusText.Should().Be("登入已失效，請重新登入");
        RefreshCalls(expired).Should().ContainSingle();
    }

    [Fact]
    public async Task 有一輪對話正在跑_不跑CLI_由對話自己換發()
    {
        runningTurns = 1;
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));

        await RefreshUsage(expired);

        Calls().Should().BeEmpty();
        Usage(expired)!.Error.Should().Be("登入權杖已過期，對話中 CLI 會自動換發");
    }

    [Fact]
    public async Task CLI正在更新_不跑CLI()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));
        cli.Update(AgentProvider.Claude, s => s with { UpdatePhase = CliUpdatePhase.Installing });

        await RefreshUsage(expired);

        Calls().Should().BeEmpty("CLI 更新期間不起 claude 程序");
        Usage(expired)!.Error.Should().Be("登入權杖已過期，Claude CLI 更新完會再換發權杖");
    }

    [Fact]
    public async Task 憑證沒有refreshToken_不跑CLI_請重新登入()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        File.WriteAllText(CredentialFile(expired), Credentials("tok-expired", DateTimeOffset.UtcNow.AddHours(-3), refreshToken: "").ToString());
        Seed(expired);

        await RefreshUsage(expired);

        Calls().Should().BeEmpty();
        Usage(expired)!.Error.Should().Contain("請重新登入");
    }

    [Fact]
    public async Task 換發進行中_再查一次不重複跑()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));
        File.WriteAllText(Path.Combine(fakeDir, "hold.flag"), "x");

        await RefreshUsage(expired);
        await WaitUntilAsync(() => RefreshCalls(expired).Count == 1, "換發開始");
        await RefreshUsage(expired);

        Usage(expired)!.Error.Should().Be("登入權杖已過期，正在讓 Claude CLI 換發權杖…");
        File.Delete(Path.Combine(fakeDir, "hold.flag"));
        await WaitRefreshSettledAsync(expired, u => u is { Stale: false }, "換發完成");
        RefreshCalls(expired).Should().ContainSingle();
    }

    [Fact]
    public async Task 換發途中移除帳戶_結束換發程序_不再處理結果()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));
        File.WriteAllText(Path.Combine(fakeDir, "hold.flag"), "x");
        await RefreshUsage(expired);
        await WaitUntilAsync(() => RefreshCalls(expired).Count == 1, "換發開始");

        ui.Invoke(() => manager.Remove(expired.Id, deleteData: true));

        await WaitUntilAsync(() => !Directory.Exists(Path.Combine(AppPaths.AccountsDirectory, expired.Id)), "換發程序結束後刪掉帳戶資料夾");
        await ui.DrainAsync(5);
        ui.Invoke(() => manager.Find(expired.Id)).Should().BeNull();
        endpoint.Tokens.Should().BeEmpty("移除後不再查詢");
    }

    [Fact]
    public async Task 換發途中只從清單移除_資料夾留著_換發照常跑完不被結束()
    {
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);
        PrepareRefresh(Credentials("tok-renewed", DateTimeOffset.UtcNow.AddHours(8)));
        File.WriteAllText(Path.Combine(fakeDir, "hold.flag"), "x");
        await RefreshUsage(expired);
        await WaitUntilAsync(() => RefreshCalls(expired).Count == 1, "換發開始");

        ui.Invoke(() => manager.Remove(expired.Id, deleteData: false));
        await Task.Delay(300);
        File.Delete(Path.Combine(fakeDir, "hold.flag"));

        // 資料夾留著（使用者可能在別處也用同一個登入）：換發途中被結束，refresh token 可能已經用掉卻沒寫回
        await WaitUntilAsync(() => File.ReadAllText(CredentialFile(expired)).Contains("tok-renewed"), "程序沒被結束、照常寫回新的權杖");
        await ui.DrainAsync(5);
        ui.Invoke(() => manager.Find(expired.Id)).Should().BeNull();
        endpoint.Tokens.Should().BeEmpty("帳戶已移除：換發結果不再處理、不查用量");
    }

    [Fact]
    public void 預設不讓CLI換發_也不借用沒登記的資料夾()
    {
        // app 在 AppController 打開；測試或其他用法沒打開就不會對真的帳戶跑 claude -p、不讀真的 ~/.claude
        var plain = ui.Invoke(() => new AccountManager(Post, cli, http, _ => 0));
        try
        {
            plain.ClaudeTokenRefreshEnabled().Should().BeFalse();
            plain.ExtraClaudeUsageSources().Should().BeEmpty();
            AccountManager.SystemDefaultClaudeUsageSources().Should().ContainSingle().Which.IsSystemDefault.Should().BeTrue();
        }
        finally
        {
            ui.Invoke(plain.Dispose);
        }
    }

    [Fact]
    public async Task 打開設定_資料過時的Claude帳戶馬上再查()
    {
        refreshEnabled = false;
        var expired = NewClaude("過期的", "tok-expired", DateTimeOffset.UtcNow.AddHours(-3));
        Seed(expired);
        await RefreshUsage(expired);
        ui.Invoke(() => manager.SeedNextPoll(expired.Id, DateTimeOffset.UtcNow.AddMinutes(10)));

        refreshEnabled = true;
        ui.Invoke(manager.PollStaleClaudeSoon);

        ui.Invoke(() => manager.GetNextPoll(expired.Id)).Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    /// <summary>假的用量端點：回實測樣本（Reject 過的權杖回 401），記下每個請求的 Bearer 權杖</summary>
    private sealed class FakeUsageEndpoint : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> tokens = new();
        private readonly ConcurrentDictionary<string, bool> rejected = new();

        public List<string> Tokens => tokens.ToList();

        public void Reject(string token) => rejected[token] = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            tokens.Enqueue(token);
            if (rejected.ContainsKey(token))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"type\":\"error\"}", Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RealSamples.ClaudeUsage, Encoding.UTF8, "application/json") });
        }
    }
}
