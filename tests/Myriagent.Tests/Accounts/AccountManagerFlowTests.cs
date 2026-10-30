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
/// 帳戶管理員的查詢與登入流程：在模擬的單一 UI 執行緒上跑；CLI 換成假的批次檔（記下每次呼叫、印出預備的輸出，登入時開著設定資料夾裡的檔案等著不結束），
/// 用量端點換成假的 HTTP（可以卡住回應，製造「查詢進行中」）。帳戶都是暫存資料夾裡的隔離帳戶：不碰真的帳戶、不連網。
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AccountManagerFlowTests : IDisposable
{
    /// <summary>
    /// 假的 claude：呼叫記進 calls.txt（設定資料夾#參數）、DISABLE_AUTOUPDATER 記進 env.txt；auth status 印出 stdout.txt／stderr.txt
    /// （hold.flag 存在時先等它被刪掉：模擬跑很久的 auth status）；auth login 開著 login.lock 等兩分鐘
    /// </summary>
    private const string FakeCliScript = """
        @echo off
        >>"%~dp0calls.txt" echo %CLAUDE_CONFIG_DIR%#%*
        >>"%~dp0env.txt" echo DISABLE_AUTOUPDATER=%DISABLE_AUTOUPDATER%
        if "%~1"=="auth" if "%~2"=="login" goto login
        :hold
        if exist "%~dp0hold.flag" (
          ping -n 2 127.0.0.1 >nul
          goto hold
        )
        if exist "%~dp0stdout.txt" type "%~dp0stdout.txt"
        if exist "%~dp0stderr.txt" type "%~dp0stderr.txt" 1>&2
        exit /b 0
        :login
        ping -n 120 127.0.0.1 >"%CLAUDE_CONFIG_DIR%\login.lock"
        exit /b 0
        """;

    private readonly TempDirectory temp = new("account-flow");
    private readonly AppPathsScope paths;
    private readonly UiThread ui = new();
    private readonly FakeUsageEndpoint endpoint = new();
    private readonly HttpClient http;
    private readonly CliManager cli;
    private readonly AccountManager manager;
    private readonly string fakeDir;
    private readonly Recorder<(string Id, string Message, bool Done, bool? Success)> progress = new();

    public AccountManagerFlowTests()
    {
        paths = new AppPathsScope(temp.Root);
        http = new HttpClient(endpoint);
        fakeDir = temp.CreateDirectory("fake-cli");
        File.WriteAllText(Path.Combine(fakeDir, "fake.cmd"), FakeCliScript.ReplaceLineEndings("\r\n"));
        cli = new CliManager(http, _ => false, () => false, startBackgroundChecks: false);
        var launcher = new AgentLauncher(Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", Path.Combine(fakeDir, "fake.cmd") });
        manager = ui.Invoke(() =>
        {
            var created = new AccountManager(Post, cli, http, _ => 0) { ResolveLauncher = _ => launcher };
            created.LoginProgress += (id, message, done, success) => progress.Add((id, message, done, success));
            return created;
        });
    }

    public void Dispose()
    {
        try
        {
            // 保險：還在跑的假登入一律取消（各測試自己也會等登入結束）
            ui.Invoke(() =>
            {
                foreach (var account in manager.Accounts.ToList())
                    manager.CancelLogin(account.Id);
            });
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

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>放入一個隔離的 Claude 帳戶（設定資料夾在暫存的 accounts 底下）；tokenExpiresAt＝寫入有這個到期時間的憑證檔</summary>
    private AgentAccount AddClaude(string id, AccountStatus status, DateTimeOffset? tokenExpiresAt = null, bool createFolder = true)
    {
        var account = new AgentAccount { Id = id, Provider = AgentProvider.Claude, Label = "測試 " + id[..6], ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, id, "claude") };
        if (createFolder)
            Directory.CreateDirectory(account.ConfigDirectory);
        if (tokenExpiresAt is { } expires)
            WriteCredentials(account, expires);
        ui.Invoke(() => manager.Seed(new[] { account }, status));
        return account;
    }

    /// <summary>捏造的憑證（權杖是假的字串）；accessToken 可以給空字串（CLI 換發被拒後清空的樣子）</summary>
    private static void WriteCredentials(AgentAccount account, DateTimeOffset expiresAt, string accessToken = "sk-ant-oat01-test")
    {
        var oauth = new JObject
        {
            ["accessToken"] = accessToken,
            ["refreshToken"] = "sk-ant-ort01-test",
            ["expiresAt"] = expiresAt.ToUnixTimeMilliseconds(),
            ["subscriptionType"] = "max",
        };
        File.WriteAllText(Path.Combine(account.ConfigDirectory!, ".credentials.json"), new JObject { ["claudeAiOauth"] = oauth }.ToString());
    }

    private static string CredentialFile(AgentAccount account) => Path.Combine(account.ConfigDirectory!, ".credentials.json");

    /// <summary>
    /// CLI 的全域設定（隔離帳戶的 CLAUDE_CONFIG_DIR\.claude.json）：credman＝cachedGrowthBookFeatures.tengu_windows_credman，
    /// oauthEmail 不為 null＝有 oauthAccount（OAuth 登入；CLI 登出會清掉）
    /// </summary>
    private static void WriteGlobalConfig(AgentAccount account, bool credman, string? oauthEmail, string? oauthOrgId = null)
    {
        var json = new JObject
        {
            ["numStartups"] = 3,
            ["cachedGrowthBookFeatures"] = new JObject { ["tengu_windows_credman"] = credman },
        };
        if (oauthEmail != null)
        {
            var oauth = new JObject { ["accountUuid"] = "u-1", ["emailAddress"] = oauthEmail };
            if (oauthOrgId != null)
                oauth["organizationUuid"] = oauthOrgId;
            json["oauthAccount"] = oauth;
        }
        File.WriteAllText(Path.Combine(account.ConfigDirectory!, ".claude.json"), json.ToString());
    }

    /// <summary>捏造的憑證，另帶登入的到期（refreshTokenExpiresAt）與最上層的 organizationUuid（RealSamples.ClaudeCredentials 的形狀）</summary>
    private static void WriteCredentialsWithIdentity(AgentAccount account, DateTimeOffset expiresAt, long? refreshTokenExpiresAt, string? organizationUuid)
    {
        var oauth = new JObject
        {
            ["accessToken"] = "sk-ant-oat01-test",
            ["refreshToken"] = "sk-ant-ort01-test",
            ["expiresAt"] = expiresAt.ToUnixTimeMilliseconds(),
            ["subscriptionType"] = "max",
        };
        if (refreshTokenExpiresAt is { } login)
            oauth["refreshTokenExpiresAt"] = login;
        var json = new JObject { ["claudeAiOauth"] = oauth, ["mcpOAuth"] = new JObject() };
        if (organizationUuid != null)
            json["organizationUuid"] = organizationUuid;
        File.WriteAllText(CredentialFile(account), json.ToString());
    }

    /// <summary>上次執行留下的帳戶：憑證有登入到期與 organizationUuid，身分快取有效（orgId＝cachedOrgId）</summary>
    private static AgentAccount SavedClaudeWithCache(string? cachedOrgId, string? credentialOrgId)
    {
        var account = NewClaudeAccount();
        WriteCredentialsWithIdentity(account, DateTimeOffset.UtcNow.AddHours(3), 1794000000000, credentialOrgId);
        account.Email = "cached@example.com";
        account.LastIdentity = AccountRules.BuildIdentityCache(AccountStatus.Ready, "cached@example.com", "Max",
            AccountManager.ReadCredentialInfo(CredentialFile(account)), DateTime.Now, cachedOrgId);
        SaveAccounts(account);
        return account;
    }

    /// <summary>假 CLI 每次被呼叫時的 DISABLE_AUTOUPDATER</summary>
    private List<string> EnvironmentLog()
    {
        var file = Path.Combine(fakeDir, "env.txt");
        if (!File.Exists(file))
            return new List<string>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    /// <summary>寫 accounts.json（模擬上次執行留下的帳戶清單）</summary>
    private static void SaveAccounts(params AgentAccount[] saved) => JsonFile.Write(AppPaths.AccountsFile, saved.ToList());

    /// <summary>隔離的 Claude 帳戶（還沒放進管理員）；設定資料夾已建立</summary>
    private static AgentAccount NewClaudeAccount()
    {
        var id = NewId();
        var account = new AgentAccount { Id = id, Provider = AgentProvider.Claude, Label = "測試 " + id[..6], ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, id, "claude") };
        Directory.CreateDirectory(account.ConfigDirectory);
        return account;
    }

    /// <summary>假 CLI 的 auth status 要印的內容</summary>
    private void FakeOutput(string stdout, string? stderr = null)
    {
        File.WriteAllText(Path.Combine(fakeDir, "stdout.txt"), stdout);
        if (stderr != null)
            File.WriteAllText(Path.Combine(fakeDir, "stderr.txt"), stderr);
    }

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

    private AccountRuntimeState State(AgentAccount account) => ui.Invoke(() => manager.GetState(account.Id));

    private UsageSnapshot? Usage(AgentAccount account) => ui.Invoke(() => manager.GetUsage(account.Id));

    /// <summary>在 UI 執行緒開始一個非同步操作並等它完成</summary>
    private async Task RunOnUi(Func<Task> start, int timeoutMs = 15000)
        => await ui.Invoke(start).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new XunitException($"{timeoutMs} ms 內沒有等到：{because}");
            await Task.Delay(20);
        }
    }

    // ───── 權杖快到期時不跑 claude auth status（accounts-usage#1） ─────

    [Fact]
    public async Task Claude權杖快到期或已過期_不跑AuthStatus_顯示權杖待更新_用量照常處理()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var soon = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddMinutes(5));
        var expired = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddHours(-1));

        await RunOnUi(() => manager.RefreshAsync(soon.Id, includeStatus: true));
        await RunOnUi(() => manager.RefreshAsync(expired.Id, includeStatus: true));

        Calls().Should().BeEmpty("權杖過期或 10 分鐘內到期時 auth status 會開始換發權杖卻不等結果就結束，可能把 refresh token 用掉、之後被登出");
        State(soon).Status.Should().Be(AccountStatus.Ready);
        State(soon).StatusText.Should().Be("已登入（權杖待更新）");
        Usage(soon)!.Windows.Should().HaveCount(3, "權杖還能用（距到期超過 2 分鐘）就照常查用量");
        State(expired).Status.Should().Be(AccountStatus.Ready);
        Usage(expired)!.Stale.Should().BeTrue();
        endpoint.Requests.Should().Be(1, "已過期的權杖不送用量查詢");
    }

    [Fact]
    public async Task Claude權杖還有效_照常跑AuthStatus()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddHours(3));

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().Equal(account.ConfigDirectory + "#auth status --json");
        State(account).Status.Should().Be(AccountStatus.Ready);
        State(account).StatusText.Should().Be("已登入");
        ui.Invoke(() => manager.Find(account.Id)!.Email).Should().Be("user@example.com");
    }

    // ───── 檢查失敗不是未登入（accounts-usage#3） ─────

    [Fact]
    public async Task AuthStatus的stderr有含大括號的警告_仍判定已登入_用量照查()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn, """Warning: ignoring unknown setting {"foo":1}""");
        var account = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddHours(3));

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        State(account).Status.Should().Be(AccountStatus.Ready);
        Usage(account)!.Windows.Should().HaveCount(3);
    }

    [Fact]
    public async Task AuthStatus無法執行_記成檢查失敗_保留用量()
    {
        var account = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        var cached = new UsageSnapshot(DateTimeOffset.UtcNow.AddMinutes(-5), "api", new[] { new UsageWindow("session", "5 小時", 40, null) }, null, null, null, false);
        ui.Invoke(() =>
        {
            manager.SeedUsage(account.Id, cached);
            manager.ResolveLauncher = _ => new AgentLauncher(temp.Combine("不存在的-claude.exe"), Array.Empty<string>());
        });

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        State(account).Status.Should().Be(AccountStatus.Error, "程序起不來是這次檢查失敗，不是未登入");
        State(account).StatusText.Should().StartWith("檢查失敗：").And.NotBe("檢查失敗：");
        Usage(account).Should().BeSameAs(cached, "檢查失敗不清掉用量（未登入才清）");
    }

    [Fact]
    public async Task 未登入的帳戶輪詢到期時重查登入狀態_在外部登入後跟得上()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.NotLoggedIn, DateTimeOffset.UtcNow.AddHours(3));
        ui.Invoke(() => manager.SeedNextPoll(account.Id, DateTimeOffset.UtcNow.AddSeconds(-1)));

        ui.Invoke(manager.Tick);

        await WaitUntilAsync(() => State(account).Status == AccountStatus.Ready, "輪詢到期時重查狀態（例如在終端機執行 claude auth login 之後）");
        Calls().Should().ContainSingle();
        ui.Invoke(() => manager.GetNextPoll(account.Id)).Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(5), "之後回到一般的輪詢間隔");
    }

    // ───── 設定資料夾之後才出現時補建憑證檔監看（accounts-usage#10） ─────

    [Fact]
    public async Task 設定資料夾之後才出現_查詢後補建憑證檔監看_之後的登入看得到()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.NotLoggedIn, createFolder: false);
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: false));
        ui.Invoke(() => manager.IsWatching(account.Id)).Should().BeFalse("資料夾還不存在");

        Directory.CreateDirectory(account.ConfigDirectory!);
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: false));
        ui.Invoke(() => manager.IsWatching(account.Id)).Should().BeTrue("資料夾出現後，下一次查詢就補建監看");

        WriteCredentials(account, DateTimeOffset.UtcNow.AddHours(3));   // 在外部登入：CLI 寫入憑證檔
        await WaitUntilAsync(() => State(account).Status == AccountStatus.Ready, "憑證檔變動後重查狀態");
        Calls().Should().Contain(account.ConfigDirectory + "#auth status --json");
    }

    // ───── 等待中的查詢（accounts-usage#7）、盡快再查（#8）、解析不出用量（#9） ─────

    [Fact]
    public async Task 兩個呼叫端在等同一個查詢_後註冊的也要等到之後那次查詢結束才返回()
    {
        var account = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        endpoint.Gate = new SemaphoreSlim(0);
        var first = ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: false));
        await WaitUntilAsync(() => endpoint.Requests == 1, "第一次查詢已送出");
        var a = ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: false));
        var b = ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: false));

        endpoint.Gate.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => endpoint.Requests == 2, "等待者開始新的一次查詢");
        await ui.DrainAsync(5);

        a.IsCompleted.Should().BeFalse();
        b.IsCompleted.Should().BeFalse("B 也要等到新的那次查詢結束，不能拿呼叫之前就開始的舊結果（例如登入完成時回報「登入沒有完成」）");

        endpoint.Gate.Release();
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        endpoint.Requests.Should().Be(2, "B 等的是 A 開始的那一次，不必再查第三次");
    }

    [Fact]
    public async Task 盡快再查觸發的那次查詢結束後_下次輪詢回到正常間隔()
    {
        var account = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        ui.Invoke(() => manager.PollSoon(account.Id, TimeSpan.Zero));   // 一輪完成後的「盡快再查」已經到期

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: false, skipIfRunning: true));   // 輪詢觸發的那一次

        ui.Invoke(() => manager.GetNextPoll(account.Id)).Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(5),
            "觸發這次查詢的要求已經處理掉，不能再把下次時間設成過去");
        ui.Invoke(manager.Tick);
        await ui.DrainAsync(5);
        endpoint.Requests.Should().Be(1, "下一個輪詢不會馬上又查一次");
    }

    [Fact]
    public async Task 查詢期間才提出的盡快再查_結束時保留()
    {
        var account = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        endpoint.Gate = new SemaphoreSlim(0);
        var refresh = ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: false));
        await WaitUntilAsync(() => endpoint.Requests == 1, "查詢已送出");

        ui.Invoke(() => manager.ReportTurnCompleted(account.Id));   // 查詢期間一輪剛完成
        endpoint.Gate.Release();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        ui.Invoke(() => manager.GetNextPoll(account.Id)).Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(15), "10 秒後還要再查一次");
    }

    [Theory]
    [InlineData("""{"limits":[],"five_hour":null}""")]
    [InlineData("")]
    public async Task 用量回應解析不出時間窗_保留上次的資料並標示過時(string body)
    {
        var account = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        var cached = new UsageSnapshot(DateTimeOffset.UtcNow.AddMinutes(-5), "api", new[] { new UsageWindow("session", "5 小時", 40, null) }, null, null, null, false);
        ui.Invoke(() => manager.SeedUsage(account.Id, cached));
        endpoint.Body = body;

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: false));

        var usage = Usage(account)!;
        usage.Windows.Should().ContainSingle(w => w.Key == "session" && w.Percent == 40, "不能用空的快照蓋掉上次的資料");
        usage.Stale.Should().BeTrue();
        usage.Error.Should().Be("用量回應的格式無法解析");
        ui.Invoke(() => manager.GetHistory(account.Id, "session")).Should().BeEmpty();
    }

    // ───── 取消登入（accounts-usage#4）、移除登入中的帳戶（#2） ─────

    [Fact]
    public async Task 登入可以取消_排隊中的登入先告知在等_取消就不開登入視窗()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusEmptyConfig);
        var a = AddClaude(NewId(), AccountStatus.NotLoggedIn);
        var b = AddClaude(NewId(), AccountStatus.NotLoggedIn);

        var loginA = ui.Invoke(() => manager.LoginAsync(a.Id));
        await WaitUntilAsync(() => Calls().Contains(a.ConfigDirectory + "#auth login --claudeai"), "A 的登入視窗已開啟");
        var loginB = ui.Invoke(() => manager.LoginAsync(b.Id));
        await WaitUntilAsync(() => progress.All.Contains((b.Id, "等待另一個帳戶完成登入…", false, null)), "B 在排隊時先告知在等");
        State(b).LoginBusy.Should().BeTrue();

        ui.Invoke(() => manager.CancelLogin(b.Id));
        await loginB.WaitAsync(TimeSpan.FromSeconds(15));
        Calls().Should().NotContain(b.ConfigDirectory + "#auth login --claudeai", "排隊中取消就不開登入視窗");
        progress.All.Last(p => p.Id == b.Id).Should().Be((b.Id, "已取消登入", true, (bool?)false));
        State(b).LoginBusy.Should().BeFalse();

        ui.Invoke(() => manager.CancelLogin(a.Id));
        await loginA.WaitAsync(TimeSpan.FromSeconds(15));
        progress.All.Last(p => p.Id == a.Id).Should().Be((a.Id, "已取消登入", true, (bool?)false));
        State(a).LoginBusy.Should().BeFalse();
        FluentActions.Invoking(() => File.Delete(Path.Combine(a.ConfigDirectory!, "login.lock"))).Should().NotThrow("登入視窗的程序樹已結束、放開檔案");
    }

    [Fact]
    public async Task 移除登入中的帳戶_取消登入_程序結束後刪除資料夾_不再回報_也不卡住其他帳戶的登入()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusEmptyConfig);
        var removed = AddClaude(NewId(), AccountStatus.NotLoggedIn);
        var other = AddClaude(NewId(), AccountStatus.NotLoggedIn);
        var root = Path.GetDirectoryName(removed.ConfigDirectory)!;

        var login = ui.Invoke(() => manager.LoginAsync(removed.Id));
        await WaitUntilAsync(() => File.Exists(Path.Combine(removed.ConfigDirectory!, "login.lock")), "登入視窗開著設定資料夾裡的檔案");

        ui.Invoke(() => manager.Remove(removed.Id, deleteData: true)).Should().BeTrue();

        await login.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => !Directory.Exists(root) && !File.Exists(root + ".deleting"), "登入程序結束後資料夾刪乾淨、清掉待刪標記");
        progress.All.Where(p => p.Id == removed.Id && p.Done).Should().BeEmpty("帳戶已移除：不回報登入結果，也沒有「檔案使用中沒刪乾淨」");
        ui.Invoke(() => manager.Find(removed.Id)).Should().BeNull();

        var otherLogin = ui.Invoke(() => manager.LoginAsync(other.Id));
        await WaitUntilAsync(() => Calls().Contains(other.ConfigDirectory + "#auth login --claudeai"), "登入鎖已釋放，另一個帳戶的登入馬上開始");
        ui.Invoke(() => manager.CancelLogin(other.Id));
        await otherLogin.WaitAsync(TimeSpan.FromSeconds(15));
    }

    // ───── 設計 6.5：啟動身分快取、短命 claude 的統一守門（S4） ─────

    [Fact]
    public async Task S4_啟動時身分快取有效_0次呼叫CLI_直接以快取判定已登入()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = NewClaudeAccount();
        WriteCredentials(account, DateTimeOffset.UtcNow.AddHours(3));
        account.Email = "cached@example.com";
        account.LastIdentity = AccountRules.BuildIdentityCache(AccountStatus.Ready, "cached@example.com", "Max 20x",
            AccountManager.ReadCredentialInfo(CredentialFile(account)), DateTime.Now);
        SaveAccounts(account);

        ui.Invoke(manager.Initialize);

        await WaitUntilAsync(() => State(account).Status == AccountStatus.Ready, "以快取判定已登入");
        await WaitUntilAsync(() => endpoint.Requests == 1, "用量照常查（HTTP，不是 CLI）");
        await Task.Delay(300);
        Calls().Should().BeEmpty("S4：啟動時身分快取有效（憑證檔大小、修改時間、expiresAt 都沒變）就 0 次 claude auth status");
        State(account).StatusText.Should().Be("已登入");
        ui.Invoke(() => manager.Find(account.Id)!.Email).Should().Be("cached@example.com");
    }

    [Fact]
    public async Task S4_啟動時權杖10分鐘內到期_auth_status與能力探查都是0次()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = NewClaudeAccount();
        WriteCredentials(account, DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(59));
        SaveAccounts(account);
        ui.Invoke(() => manager.NeedsCapabilities = _ => true);

        ui.Invoke(manager.Initialize);

        await WaitUntilAsync(() => State(account).Status == AccountStatus.Ready, "權杖待更新也算已登入");
        await Task.Delay(500);
        Calls().Should().BeEmpty("S4：權杖距到期不到 10 分鐘時 auth status 與能力探查 0 次（不論有沒有快取）");
        State(account).StatusText.Should().Be("已登入（權杖待更新）");

        // 使用者手動按更新也一樣：權杖不新鮮就不跑
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));
        Calls().Should().BeEmpty();
    }

    [Fact]
    public async Task 權杖新鮮_沒有快取時跑一次AuthStatus並記下_下次啟動0次_憑證檔變了才再查()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = NewClaudeAccount();
        WriteCredentials(account, DateTimeOffset.UtcNow.AddHours(3));
        SaveAccounts(account);

        ui.Invoke(manager.Initialize);
        await WaitUntilAsync(() => Calls().Count == 1, "身分未知且權杖新鮮：跑 auth status");
        await WaitUntilAsync(() => ui.Invoke(() => manager.Find(account.Id)!.LastIdentity) != null, "記下身分快取");
        EnvironmentLog().Should().Equal(new[] { "DISABLE_AUTOUPDATER=1" }, "app 跑的 claude 子指令一律帶 DISABLE_AUTOUPDATER=1");
        var saved = JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single();
        saved.LastIdentity!.Status.Should().Be(AccountStatus.Ready);
        saved.LastIdentity.Email.Should().Be("user@example.com");
        saved.LastIdentity.ExpiresAt.Should().NotBeNull();
        File.ReadAllText(AppPaths.AccountsFile).Should().NotContain("sk-ant-", "快取不含權杖");

        // 重新啟動 app（原本的管理員結束、不再監看憑證檔）：快取有效就不跑
        var launcher = ui.Invoke(() => manager.ResolveLauncher);
        ui.Invoke(manager.Dispose);
        var restarted = ui.Invoke(() => new AccountManager(Post, cli, http, _ => 0) { ResolveLauncher = launcher });
        try
        {
            ui.Invoke(restarted.Initialize);
            await WaitUntilAsync(() => ui.Invoke(() => restarted.GetState(account.Id).Status) == AccountStatus.Ready, "重啟後以快取判定已登入");
            await Task.Delay(300);
            Calls().Should().HaveCount(1, "S4：啟動身分快取有效 0 次");

            // CLI 換發了權杖（憑證檔變了、權杖新鮮）：監看觸發，再查一次
            WriteCredentials(account, DateTimeOffset.UtcNow.AddHours(5));
            await WaitUntilAsync(() => Calls().Count == 2, "憑證檔變動且權杖新鮮：再查一次");
        }
        finally
        {
            ui.Invoke(restarted.Dispose);
        }
    }

    [Fact]
    public async Task 能力探查也過同一道守門_權杖新鮮才跑短命claude()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var fresh = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(30));
        ui.Invoke(() => manager.NeedsCapabilities = _ => true);

        await RunOnUi(() => manager.RefreshAsync(fresh.Id, includeStatus: true));
        await WaitUntilAsync(() => Calls().Count == 2, "auth status 之後探查模型清單（短命的 claude -p）");
        Calls()[1].Should().Contain("-p --input-format stream-json");

        var expiring = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(59));
        var launcher = ui.Invoke(() => manager.ResolveLauncher(AgentProvider.Claude))!;
        var probed = await CapabilityProbe.ProbeClaudeAsync(launcher, expiring, CancellationToken.None);
        probed.Should().BeNull();
        Calls().Should().HaveCount(2, "S4：權杖 10 分鐘內到期時能力探查 0 次");
    }

    [Fact]
    public async Task 空權杖_直接判定登入已失效_不跑CLI_清掉快取()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        ui.Invoke(() => manager.Find(account.Id)!.LastIdentity = new AccountIdentityCache { Status = AccountStatus.Ready, Email = "x@example.com" });
        WriteCredentials(account, DateTimeOffset.UtcNow.AddHours(3), accessToken: string.Empty);

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().BeEmpty("CLI 換發被拒後清空的登入：不必跑 CLI 就知道已失效");
        State(account).Status.Should().Be(AccountStatus.NotLoggedIn);
        State(account).StatusText.Should().Be("登入已失效，請重新登入");
        ui.Invoke(() => manager.Find(account.Id)!.LastIdentity).Should().BeNull();
    }

    [Fact]
    public async Task 憑證檔讀不懂_不跑CLI也不當成已登入_記成檢查失敗稍後再查()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Checking);
        File.WriteAllText(CredentialFile(account), "{\"claudeAiOauth\": {\"accessToken\": \"sk-ant-oat01-test\", \"expiresAt\"");   // CLI 寫到一半

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().BeEmpty("讀不懂就不能判斷權杖期限：不跑短命的 claude");
        State(account).Status.Should().Be(AccountStatus.Error, "檢查失敗：保留用量、不擋送出");
        State(account).StatusText.Should().Be("檢查失敗：暫時讀不到登入憑證，稍後再查");
        ui.Invoke(() => manager.GetNextPoll(account.Id)).Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task 權杖在認證管理員_保守模式_對話結束後與手動更新都不跑任何短命claude_外部登出後才照常查()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Checking);
        WriteGlobalConfig(account, credman: true, oauthEmail: "cached@example.com");
        ui.Invoke(() =>
        {
            var a = manager.Find(account.Id)!;
            a.Email = "cached@example.com";
            a.LastIdentity = new AccountIdentityCache { Status = AccountStatus.Ready, Email = "cached@example.com", Plan = "Max" };
            manager.NeedsCapabilities = _ => true;
        });

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().BeEmpty("憑證改存在 Windows 認證管理員：看不到權杖期限，不跑 auth status");
        State(account).Status.Should().Be(AccountStatus.Ready);
        State(account).StatusText.Should().Be(AccountManager.CredentialManagerStatusText);
        ui.Invoke(() => manager.IsConservative(account.Id)).Should().BeTrue();
        Usage(account)!.Error.Should().Be("登入憑證存在 Windows 認證管理員，app 讀不到；用量改由對話回報");

        // 這個帳戶的對話跑完一輪：一輪結束時權杖可能只剩 5 分鐘出頭（CLI 只在剩不到 5 分鐘時才換發），也不跑——只重看 CLI 設定
        await TurnCompletedThenPollAsync(account);
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));   // 使用者手動按更新
        var probe = await CapabilityProbe.ProbeClaudeAsync(ui.Invoke(() => manager.ResolveLauncher(AgentProvider.Claude))!, account, CancellationToken.None);
        probe.Should().BeNull("能力探查也過同一道守門");
        Calls().Should().BeEmpty("保守模式：auth status 與能力探查都是 0 次（對話結束後、手動更新也一樣）");
        State(account).Status.Should().Be(AccountStatus.Ready);

        // 在終端機登出：CLI 刪掉權杖並清掉 oauthAccount——權杖不在任何地方了，下一次對話結束後就照常跑 auth status 判定未登入
        WriteGlobalConfig(account, credman: true, oauthEmail: null);
        FakeOutput(RealSamples.ClaudeAuthStatusEmptyConfig);
        await TurnCompletedThenPollAsync(account);
        Calls().Should().ContainSingle().Which.Should().EndWith("#auth status --json");
        State(account).Status.Should().Be(AccountStatus.NotLoggedIn);
        ui.Invoke(() => manager.IsConservative(account.Id)).Should().BeFalse();
        ui.Invoke(() => manager.Find(account.Id)!.LastIdentity).Should().BeNull();
    }

    /// <summary>這個帳戶的一輪對話結束、輪詢到期：等那次查詢確認過登入狀態（CheckedAt 改變）</summary>
    private async Task TurnCompletedThenPollAsync(AgentAccount account)
    {
        var before = State(account).CheckedAt;
        ui.Invoke(() =>
        {
            manager.ReportTurnCompleted(account.Id);
            manager.SeedNextPoll(account.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
            manager.Tick();
        });
        await WaitUntilAsync(() => State(account).CheckedAt != before, "對話結束後的登入狀態檢查做完");
        await ui.DrainAsync(5);
    }

    [Fact]
    public async Task 權杖在認證管理員而沒有身分快取_升級後第一次啟動也不跑_以CLI設定的oauthAccount判定已登入()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = NewClaudeAccount();
        WriteGlobalConfig(account, credman: true, oauthEmail: "oauth@example.com");
        SaveAccounts(account);   // 從 main 升級：accounts.json 還沒有 LastIdentity
        ui.Invoke(() => manager.NeedsCapabilities = _ => true);

        ui.Invoke(manager.Initialize);

        await WaitUntilAsync(() => State(account).Status == AccountStatus.Ready, "以 CLI 設定的 oauthAccount 判定已登入");
        await Task.Delay(500);
        Calls().Should().BeEmpty("沒有快取也不跑：權杖剛好快到期就會被燒掉");
        State(account).StatusText.Should().Be(AccountManager.CredentialManagerStatusText);
        ui.Invoke(() => manager.Find(account.Id)!.Email).Should().Be("oauth@example.com");
    }

    [Fact]
    public async Task 憑證檔被刪掉而沒有啟用認證管理員_外部登出_照常跑AuthStatus判定未登入_清掉快取()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddHours(3));
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));
        Calls().Should().HaveCount(1);
        ui.Invoke(() => manager.Find(account.Id)!.LastIdentity).Should().NotBeNull();

        // 在終端機執行 claude auth logout：CLI 直接刪掉憑證檔（.claude.json 沒有認證管理員旗標）
        FakeOutput(RealSamples.ClaudeAuthStatusEmptyConfig);
        File.Delete(CredentialFile(account));
        await WaitUntilAsync(() => State(account).Status == AccountStatus.NotLoggedIn, "監看觸發後照常查：沒有權杖可換發");

        Calls().Should().HaveCount(2);
        ui.Invoke(() => manager.IsConservative(account.Id)).Should().BeFalse("不是認證管理員：不能一直顯示已登入");
        ui.Invoke(() => manager.Find(account.Id)!.LastIdentity).Should().BeNull();
    }

    [Fact]
    public async Task APIKey登入沒有憑證檔_照常跑AuthStatus_保留API計費的說明()
    {
        FakeOutput("""{"loggedIn": true, "authMethod": "apiKey", "apiProvider": "firstParty", "apiKeySource": "/login managed key"}""");
        var account = AddClaude(NewId(), AccountStatus.Checking);
        ui.Invoke(() => manager.Find(account.Id)!.LastIdentity = new AccountIdentityCache { Status = AccountStatus.ApiKey, Plan = "apiKey" });

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true, skipIfRunning: false));

        Calls().Should().ContainSingle("API Key 登入沒有 OAuth 權杖：跑 auth status 不會換發");
        State(account).Status.Should().Be(AccountStatus.ApiKey);
        State(account).StatusText.Should().Be(AccountRules.DescribeStatus(AgentProvider.Claude, AccountStatus.ApiKey));
        ui.Invoke(() => manager.IsConservative(account.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task CLI更新期間被暫停的檢查_記下理由_更新完的輪詢以原本的理由補查()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        ui.Invoke(() => manager.Find(account.Id)!.LastIdentity = AccountRules.BuildIdentityCache(AccountStatus.Ready, "user@example.com", "Max",
            AccountManager.ReadCredentialInfo(CredentialFile(account)), DateTime.Now));
        cli.Update(AgentProvider.Claude, s => s with { InstallChanging = true });

        // 互動式 claude 在背景更新（安裝正在變動）時換發了權杖：監看以 CredentialsChanged 要求查
        WriteCredentials(account, DateTimeOffset.UtcNow.AddHours(5));
        await RunOnUi(() => manager.RefreshAsync(account.Id, true, false, ClaudeStatusReason.CredentialsChanged));
        Calls().Should().BeEmpty("CLI 正在更新：不起 claude");
        ui.Invoke(() => manager.GetPendingStatusCheck(account.Id)).Should().Be(ClaudeStatusReason.CredentialsChanged);
        ui.Invoke(() => manager.GetNextPoll(account.Id)).Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(1), "30 秒後再試");

        // 更新完：下一次輪詢以 CredentialsChanged 補查（以前這次只查用量，登入狀態與身分快取一直停在舊的）
        cli.Update(AgentProvider.Claude, s => s with { InstallChanging = false });
        ui.Invoke(() =>
        {
            manager.SeedNextPoll(account.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
            manager.Tick();
        });
        await WaitUntilAsync(() => Calls().Count == 1, "補查登入狀態");
        Calls().Single().Should().EndWith("#auth status --json");
        await WaitUntilAsync(() => ui.Invoke(() => manager.Find(account.Id)!.LastIdentity!.ExpiresAt)
            == AccountRules.ReadClaudeExpiresAt(File.ReadAllText(CredentialFile(account))), "身分快取跟著更新");
        ui.Invoke(() => manager.GetPendingStatusCheck(account.Id)).Should().BeNull("補查開始後清掉");
    }

    [Fact]
    public async Task 補查遇到查詢進行中被略過_理由留著_下次輪詢照樣補()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        cli.Update(AgentProvider.Claude, s => s with { UpdatePhase = CliUpdatePhase.Installing });
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));   // 使用者在 CLI 更新期間按了更新
        ui.Invoke(() => manager.GetPendingStatusCheck(account.Id)).Should().Be(ClaudeStatusReason.Manual);
        cli.Update(AgentProvider.Claude, s => s with { UpdatePhase = CliUpdatePhase.None });

        // 用量查詢卡住（查詢進行中）時輪詢到期：這次被略過
        endpoint.Gate = new SemaphoreSlim(0);
        var stuck = ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: false));
        await WaitUntilAsync(() => endpoint.Requests >= 2, "查詢進行中");
        ui.Invoke(() =>
        {
            manager.SeedNextPoll(account.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
            manager.Tick();
        });
        await ui.DrainAsync(5);
        endpoint.Gate.Release(10);
        await stuck.WaitAsync(TimeSpan.FromSeconds(10));
        Calls().Should().BeEmpty("那次被略過了");
        ui.Invoke(() => manager.GetPendingStatusCheck(account.Id)).Should().Be(ClaudeStatusReason.Manual, "被略過時理由留著");

        ui.Invoke(() =>
        {
            manager.SeedNextPoll(account.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
            manager.Tick();
        });
        await WaitUntilAsync(() => Calls().Count == 1, "下次輪詢照樣補查");
        Calls().Single().Should().EndWith("#auth status --json");
    }

    [Fact]
    public async Task AuthStatus跑的期間CLI開始更新_不起能力探查_留到下次()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddHours(3));
        ui.Invoke(() => manager.NeedsCapabilities = _ => true);
        var hold = Path.Combine(fakeDir, "hold.flag");
        File.WriteAllText(hold, "x");

        var refresh = ui.Invoke(() => manager.RefreshAsync(account.Id, includeStatus: true));
        await WaitUntilAsync(() => Calls().Count == 1, "auth status 開始跑");
        cli.Update(AgentProvider.Claude, s => s with { UpdatePhase = CliUpdatePhase.WaitingForProcesses });
        File.Delete(hold);
        await refresh.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(500);

        Calls().Should().ContainSingle("auth status 跑完時 CLI 已經在更新：不起 claude -p 的能力探查（會擋住 npm 換檔）");
        State(account).Status.Should().Be(AccountStatus.Ready);

        cli.Update(AgentProvider.Claude, s => s with { UpdatePhase = CliUpdatePhase.None });
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));
        await WaitUntilAsync(() => Calls().Count == 3, "更新完、下次確認登入狀態時才探");
        Calls()[2].Should().Contain("-p --input-format stream-json");
    }

    // ───── WP3.4：登入到期與 orgId 的來源（D4、D5；S4 不退步） ─────

    [Fact]
    public async Task 身分快取有效_憑證帶refreshTokenExpiresAt_0次authStatus_LoginExpiresAt有值()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = SavedClaudeWithCache("org-cached", null);

        ui.Invoke(manager.Initialize);

        await WaitUntilAsync(() => State(account).Status == AccountStatus.Ready, "以快取判定已登入");
        await Task.Delay(300);
        Calls().Should().BeEmpty("S4：快取有效時啟動 0 次 auth status；登入到期取自已經讀進記憶體的憑證檔");
        State(account).LoginExpiresAt.Should().Be(1794000000000);
        var identity = ui.Invoke(() => manager.GetSyncIdentity(account.Id));
        identity.OrgId.Should().Be("org-cached");
        identity.LoginExpiresAt.Should().Be(1794000000000);
    }

    [Fact]
    public async Task 閘門關閉_憑證的organizationUuid不當成orgId()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = SavedClaudeWithCache(null, "org-from-credentials");
        ui.Invoke(() => manager.UseCredentialsOrg).Should().BeFalse("E-13 證明之前預設關閉（D4）");
        ui.Invoke(() => manager.UseGlobalConfigOrg).Should().BeFalse();

        ui.Invoke(manager.Initialize);

        await WaitUntilAsync(() => State(account).Status == AccountStatus.Ready, "以快取判定已登入");
        await Task.Delay(300);
        Calls().Should().BeEmpty();
        State(account).OrgId.Should().BeNull("閘門關著：憑證檔的 organizationUuid 不用");
        ui.Invoke(() => manager.GetSyncIdentity(account.Id)).OrgId.Should().BeNull();
    }

    [Fact]
    public async Task 憑證閘門打開_快取沒有orgId_以憑證補上_0次authStatus()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = SavedClaudeWithCache(null, "org-from-credentials");
        ui.Invoke(() => manager.UseCredentialsOrg = true);

        ui.Invoke(manager.Initialize);

        await WaitUntilAsync(() => State(account).OrgId != null, "以憑證檔補上 orgId");
        await Task.Delay(300);
        Calls().Should().BeEmpty("新的身分來源也是 0 次 auth status（S4）");
        State(account).OrgId.Should().Be("org-from-credentials");
        State(account).Status.Should().Be(AccountStatus.Ready);
    }

    [Fact]
    public async Task 設定檔閘門打開_保守模式以oauthAccount的organizationUuid補上_0次CLI()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Checking);
        WriteGlobalConfig(account, credman: true, oauthEmail: "oauth@example.com", oauthOrgId: "org-from-config");

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));
        State(account).OrgId.Should().BeNull("閘門關著：CLI 設定的 organizationUuid 不用");

        ui.Invoke(() => manager.UseGlobalConfigOrg = true);
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().BeEmpty("保守模式：任何理由都不跑短命的 claude");
        ui.Invoke(() => manager.IsConservative(account.Id)).Should().BeTrue();
        State(account).OrgId.Should().Be("org-from-config");
        State(account).OrgIdHint.Should().BeNull("orgId 已知就不需要提示");
        ui.Invoke(() => manager.GetSyncIdentity(account.Id)).OrgId.Should().Be("org-from-config");
    }

    [Fact]
    public async Task 保守模式_閘門關著_CLI設定的organizationUuid只進OrgIdHint_不進OrgId()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Checking);
        WriteGlobalConfig(account, credman: true, oauthEmail: "oauth@example.com", oauthOrgId: "org-from-config");

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().BeEmpty("保守模式：不跑短命的 claude（S4）");
        State(account).OrgId.Should().BeNull("閘門關著（D4）");
        State(account).OrgIdHint.Should().Be("org-from-config", "SEC-1：未確認的 org 只當提示");
        var identity = ui.Invoke(() => manager.GetSyncIdentity(account.Id));
        identity.OrgId.Should().BeNull();
        identity.OrgIdHint.Should().Be("org-from-config");

        // CLI 設定的 organizationUuid 不合格：不當提示（上次的提示也清掉）
        WriteGlobalConfig(account, credman: true, oauthEmail: "oauth@example.com", oauthOrgId: "org bad");
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));
        State(account).OrgIdHint.Should().BeNull("不合格的 organizationUuid 不當提示");

        // 離開保守模式（憑證檔出現、auth status 有結果）就清掉
        WriteGlobalConfig(account, credman: true, oauthEmail: "oauth@example.com", oauthOrgId: "org-from-config");
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));
        State(account).OrgIdHint.Should().Be("org-from-config");
        WriteCredentials(account, DateTimeOffset.UtcNow.AddHours(5));
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));
        Calls().Should().Contain(c => c.EndsWith("#auth status --json"), "憑證檔出現：照常跑 auth status");
        State(account).OrgIdHint.Should().BeNull("auth status 有結果就不用提示");
    }

    [Fact]
    public async Task 權杖待更新與保守模式_快取email相同_沿用快取的orgId()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        // 權杖 10 分鐘內到期（TokenPending）
        var pending = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddMinutes(5));
        // 憑證在 Windows 認證管理員（Conservative）
        var conservative = AddClaude(NewId(), AccountStatus.Checking);
        WriteGlobalConfig(conservative, credman: true, oauthEmail: "Keep@Example.com");
        ui.Invoke(() =>
        {
            var a = manager.Find(pending.Id)!;
            a.Email = "pending@example.com";
            a.LastIdentity = new AccountIdentityCache { Status = AccountStatus.Ready, Email = "Pending@Example.com", OrgId = "org-pending" };
            var b = manager.Find(conservative.Id)!;
            b.Email = "keep@example.com";
            b.LastIdentity = new AccountIdentityCache { Status = AccountStatus.Ready, Email = "keep@example.com", OrgId = "org-keep" };
        });

        await RunOnUi(() => manager.RefreshAsync(pending.Id, includeStatus: true));
        await RunOnUi(() => manager.RefreshAsync(conservative.Id, includeStatus: true));

        Calls().Should().BeEmpty();
        State(pending).StatusText.Should().Be("已登入（權杖待更新）");
        State(pending).OrgId.Should().Be("org-pending", "email 相同（不分大小寫）：沿用快取的 orgId");
        State(conservative).StatusText.Should().Be(AccountManager.CredentialManagerStatusText);
        State(conservative).OrgId.Should().Be("org-keep");
    }

    [Fact]
    public async Task 保守模式_CLI設定的email和快取不同_不沿用快取的orgId()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = AddClaude(NewId(), AccountStatus.Checking);
        WriteGlobalConfig(account, credman: true, oauthEmail: "new@example.com");
        ui.Invoke(() =>
        {
            var a = manager.Find(account.Id)!;
            a.Email = "old@example.com";
            a.LastIdentity = new AccountIdentityCache { Status = AccountStatus.Ready, Email = "old@example.com", OrgId = "org-old" };
        });

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().BeEmpty();
        ui.Invoke(() => manager.Find(account.Id)!.Email).Should().Be("new@example.com", "在外部換了帳號");
        State(account).OrgId.Should().BeNull("外部換了帳號：不能把舊的 org 配上新的 email");
        ui.Invoke(() => manager.GetSyncIdentity(account.Id)).OrgId.Should().BeNull("身分快取的 email 也不同，不能拿快取補");
    }

    // ───── WP3.4：在這台登入（宣告目錄的邏輯帳戶、Claude 登入帶 --email，D12） ─────

    private const string ClaimLogical = "la_0123456789ABCDEFGHJKMNPQRS";

    /// <summary>
    /// 以宣告新增帳戶（管理員裡只有它），等到 loginCall 出現在 calls.txt，取消並等登入結束；回傳新帳戶與假 CLI 的所有呼叫
    /// （「設定資料夾#參數」：Codex 帳戶沒有 CLAUDE_CONFIG_DIR，# 前面是空的）
    /// </summary>
    private async Task<(AgentAccount Account, List<string> Calls)> AddClaimedAndCancelAsync(AgentProvider provider, string label, string color,
        string? expectedEmail, Func<string, bool> loginCall, int timeoutMs = 30000)
    {
        var account = ui.Invoke(() => manager.Add(provider, label, color, new AccountClaim(ClaimLogical, expectedEmail)));
        await WaitUntilAsync(() => Calls().Any(loginCall), "開始登入", timeoutMs);
        ui.Invoke(() => manager.CancelLogin(account.Id));
        await WaitUntilAsync(() => progress.All.Any(p => p.Id == account.Id && p.Done), "登入結束", timeoutMs);
        await ui.DrainAsync(5);
        return (account, Calls());
    }

    [Fact]
    public async Task 在這台登入的Claude帳戶_auth_login帶email()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusEmptyConfig);

        var (account, calls) = await AddClaimedAndCancelAsync(AgentProvider.Claude, "  公司的 Claude  ", "#abc", "me@example.com", c => c.Contains("#auth login"));

        calls.Should().Contain(account.ConfigDirectory + "#auth login --claudeai --email me@example.com");
        account.LogicalAccountId.Should().Be(ClaimLogical);
        account.Label.Should().Be("公司的 Claude", "名稱照目錄的規則清理");
        account.Color.Should().Be("#AABBCC", "目錄的 #RGB 展開成 #RRGGBB");
        account.IsSystemDefault.Should().BeFalse();
        JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single(a => a.Id == account.Id).LogicalAccountId.Should().Be(ClaimLogical);
    }

    [Fact]
    public async Task 在這台登入的名稱_超過60字截斷()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusEmptyConfig);

        var (account, _) = await AddClaimedAndCancelAsync(AgentProvider.Claude, new string('名', 80), "not-a-color", null, c => c.Contains("#auth login"));

        account.Label.Should().Be(new string('名', AccountRules.MaxCatalogLabelLength));
        account.Color.Should().MatchRegex("^#[0-9A-F]{6}$", "顏色不合格時用預設顏色");
    }

    [Theory]
    [InlineData("a&calc@example.com")]
    [InlineData("a@example.com&calc")]
    [InlineData("-x@example.com")]
    [InlineData("\"q\"@example.com")]
    [InlineData("%PATH%@example.com")]
    [InlineData("")]
    public async Task 信箱含命令列特殊字元_不帶email(string email)
    {
        FakeOutput(RealSamples.ClaudeAuthStatusEmptyConfig);

        var (account, calls) = await AddClaimedAndCancelAsync(AgentProvider.Claude, "目錄", "#D97757", email, c => c.Contains("#auth login"));

        calls.Where(c => c.Contains("#auth login", StringComparison.Ordinal)).Should().Equal(account.ConfigDirectory + "#auth login --claudeai");
    }

    [Fact]
    public async Task Codex宣告_登入不帶信箱參數()
    {
        var (account, calls) = await AddClaimedAndCancelAsync(AgentProvider.Codex, "Codex 目錄", "#19C3A6", "dev@example.com", c => c.EndsWith("#login"), 60000);

        account.LogicalAccountId.Should().Be(ClaimLogical);
        account.Provider.Should().Be(AgentProvider.Codex);
        calls.Should().Contain(c => c.EndsWith("#login"), "Codex 的登入跑過假的 CLI（app-server 失敗後退回主控台 login）");
        calls.Should().OnlyContain(c => !c.Contains("--email") && !c.Contains("dev@example.com"), "Codex 不帶任何信箱參數");
    }

    // ───── 設計 7.3.1 第 1 步：CLI 更新期間暫停這種 CLI 的程序 ─────

    [Fact]
    public async Task CLI更新屏障期間_暫停AuthStatus_Codex用量查詢與登入用的AppServer_更新完再查()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var claude = AddClaude(NewId(), AccountStatus.Checking, DateTimeOffset.UtcNow.AddHours(3));
        var codexDir = Path.Combine(AppPaths.AccountsDirectory, NewId(), "codex");
        Directory.CreateDirectory(codexDir);
        var codex = new AgentAccount { Id = NewId(), Provider = AgentProvider.Codex, Label = "Codex", ConfigDirectory = codexDir };
        ui.Invoke(() => manager.Seed(new[] { codex }, AccountStatus.Ready));
        cli.Update(AgentProvider.Claude, s => s with { UpdatePhase = CliUpdatePhase.Draining });
        cli.Update(AgentProvider.Codex, s => s with { UpdatePhase = CliUpdatePhase.Installing });

        await RunOnUi(() => manager.RefreshAsync(claude.Id, includeStatus: true));
        await RunOnUi(() => manager.RefreshAsync(codex.Id, includeStatus: true));
        await RunOnUi(() => manager.LoginAsync(codex.Id));

        Calls().Should().BeEmpty("屏障期間不起 claude、codex 程序（會擋住 npm 換檔、讓更新判斷失準）");
        progress.All.Last(p => p.Id == codex.Id).Should().Be((codex.Id, "Codex CLI 正在更新，請等更新完成後再登入", true, (bool?)false));
        ui.Invoke(() => manager.GetNextPoll(codex.Id)).Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(1), "更新完很快再查");

        cli.Update(AgentProvider.Claude, s => s with { UpdatePhase = CliUpdatePhase.None });
        await RunOnUi(() => manager.RefreshAsync(claude.Id, includeStatus: true));
        Calls().Should().ContainSingle().Which.Should().EndWith("#auth status --json");
    }

    /// <summary>假的用量端點：回應 Body；設定 Gate 後每個請求都要等測試放行</summary>
    private sealed class FakeUsageEndpoint : HttpMessageHandler
    {
        private int requests;

        public int Requests => Volatile.Read(ref requests);

        public string Body { get; set; } = RealSamples.ClaudeUsage;

        public SemaphoreSlim? Gate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            if (Gate != null)
                await Gate.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
