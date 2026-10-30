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
/// 帳戶管理員給多電腦同步的掛鉤（WP3.3 第 10 步）：用量查詢閘門、共享用量與目錄外觀的套用、Claude 的 orgId 與憑證到期時間。
/// 在模擬的 UI 執行緒上跑；CLI 是假的批次檔、用量端點是假的 HTTP，帳戶都是暫存資料夾裡的隔離帳戶（不碰真的 ~/.claude、~/.codex）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AccountManagerSyncHookTests : IDisposable
{
    /// <summary>假的 claude：呼叫記進 calls.txt，印出 stdout.txt（auth status 的輸出）</summary>
    private const string FakeCliScript = """
        @echo off
        >>"%~dp0calls.txt" echo %CLAUDE_CONFIG_DIR%#%*
        if exist "%~dp0stdout.txt" type "%~dp0stdout.txt"
        exit /b 0
        """;

    private readonly TempDirectory temp = new("account-sync-hook");
    private readonly AppPathsScope paths;
    private readonly UiThread ui = new();
    private readonly CountingUsageEndpoint endpoint = new();
    private readonly HttpClient http;
    private readonly CliManager cli;
    private readonly AccountManager manager;
    private readonly string fakeDir;
    private int changed;

    public AccountManagerSyncHookTests()
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
            created.Changed += () => changed++;
            return created;
        });
    }

    public void Dispose()
    {
        try
        {
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

    /// <summary>隔離的 Claude 帳戶（設定資料夾在暫存的 accounts 底下），寫入有這個到期時間的捏造憑證（refreshTokenExpiresAt＝登入的到期）</summary>
    private static AgentAccount NewClaude(DateTimeOffset tokenExpiresAt, long? refreshTokenExpiresAt = null)
    {
        var id = NewId();
        var account = new AgentAccount { Id = id, Provider = AgentProvider.Claude, Label = "測試 " + id[..6], ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, id, "claude") };
        Directory.CreateDirectory(account.ConfigDirectory);
        var oauth = new JObject
        {
            ["accessToken"] = "sk-ant-oat01-test",
            ["refreshToken"] = "sk-ant-ort01-test",
            ["expiresAt"] = tokenExpiresAt.ToUnixTimeMilliseconds(),
            ["subscriptionType"] = "max",
        };
        if (refreshTokenExpiresAt is { } login)
            oauth["refreshTokenExpiresAt"] = login;
        File.WriteAllText(Path.Combine(account.ConfigDirectory, ".credentials.json"), new JObject { ["claudeAiOauth"] = oauth }.ToString());
        return account;
    }

    private AgentAccount SeedClaude(AccountStatus status, DateTimeOffset tokenExpiresAt)
    {
        var account = NewClaude(tokenExpiresAt);
        ui.Invoke(() => manager.Seed(new[] { account }, status));
        return account;
    }

    /// <summary>隔離的 Codex 帳戶（不會被查詢：只用在套用外觀與順序）</summary>
    private AgentAccount SeedCodex(string label)
    {
        var id = NewId();
        var account = new AgentAccount { Id = id, Provider = AgentProvider.Codex, Label = label, Color = "#19C3A6", ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, id, "codex") };
        ui.Invoke(() => manager.Seed(new[] { account }, AccountStatus.Ready));
        return account;
    }

    private void FakeOutput(string stdout) => File.WriteAllText(Path.Combine(fakeDir, "stdout.txt"), stdout);

    private List<string> Calls()
    {
        var file = Path.Combine(fakeDir, "calls.txt");
        if (!File.Exists(file))
            return new List<string>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

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

    private static UsageSnapshot Snapshot(DateTimeOffset fetchedAt, string source, double percent)
        => new(fetchedAt, source, new[] { new UsageWindow("session", "5 小時", percent, null) }, null, null, null, false);

    // ───── 用量查詢閘門（設計 6.6、S12） ─────

    [Fact]
    public async Task 閘門不查_Claude不發HTTP_也不標過期()
    {
        var account = SeedClaude(AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        var cached = Snapshot(DateTimeOffset.UtcNow.AddMinutes(-3), "shared", 42);
        var policy = new FixedPolicy(false);
        ui.Invoke(() =>
        {
            manager.SeedUsage(account.Id, cached);
            manager.UsagePollPolicy = policy;
        });

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: false));

        endpoint.Requests.Should().Be(0, "這台沒被指派查這個身分的用量");
        policy.Asked.Should().Equal(account.Id);
        var usage = ui.Invoke(() => manager.GetUsage(account.Id));
        usage.Should().BeSameAs(cached, "不查時不改用量");
        usage!.Stale.Should().BeFalse("不查時不標過期（畫面顯示共享的用量）");
    }

    [Fact]
    public async Task 閘門null_Claude照常查()
    {
        var account = SeedClaude(AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: false));

        endpoint.Requests.Should().Be(1);
        var usage = ui.Invoke(() => manager.GetUsage(account.Id));
        usage!.Source.Should().Be("api");
        usage.Windows.Should().HaveCount(3);
    }

    [Fact]
    public async Task 閘門放行_Claude照常查()
    {
        var account = SeedClaude(AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        ui.Invoke(() => manager.UsagePollPolicy = new FixedPolicy(true));

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: false));

        endpoint.Requests.Should().Be(1);
        ui.Invoke(() => manager.GetUsage(account.Id))!.Source.Should().Be("api");
    }

    [Fact]
    public async Task 閘門不查_手動更新也不查()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var account = SeedClaude(AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        ui.Invoke(() => manager.UsagePollPolicy = new FixedPolicy(false));

        // 使用者按「更新用量」＝RefreshAsync(includeStatus: true)（AgentHub 的 accountRefresh）
        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().ContainSingle("登入狀態照常確認").Which.Should().EndWith("#auth status --json");
        endpoint.Requests.Should().Be(0, "手動更新也走同一個閘門（Q10）：一按就查會破壞 S12");
        ui.Invoke(() => manager.GetState(account.Id).Status).Should().Be(AccountStatus.Ready);
        ui.Invoke(() => manager.GetUsage(account.Id)).Should().BeNull("沒查、也沒標過期");
    }

    // ───── 共享用量 ─────

    [Fact]
    public void ApplySharedUsage_ReplaceCurrent_歷史點用給定時間_觸發一次Changed()
    {
        var account = SeedClaude(AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        var now = DateTimeOffset.UtcNow;
        var first = Snapshot(now.AddMinutes(-1), "shared", 10);
        var second = Snapshot(now.AddMinutes(-1), "shared", 30);
        var t1 = now.AddHours(-3);
        var t2 = now.AddHours(-1);

        ui.Invoke(() => manager.ApplySharedUsage(new[]
        {
            (account.Id, first, t1, true),
            (account.Id, second, t2, true),
            ("不存在的帳戶", second, t2, true),
        }));

        changed.Should().Be(1, "整批套用完才通知一次");
        ui.Invoke(() => manager.GetUsage(account.Id)).Should().BeSameAs(second, "最後一筆是目前值");
        var points = ui.Invoke(() => manager.GetHistory(account.Id, "session"));
        points.Select(p => (long)p[0]).Should().Equal(t1.ToUnixTimeMilliseconds(), t2.ToUnixTimeMilliseconds());
        points.Select(p => p[1]).Should().Equal(10, 30);
    }

    [Fact]
    public void ApplySharedUsage_不ReplaceCurrent_只記歷史()
    {
        var account = SeedClaude(AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        var local = Snapshot(DateTimeOffset.UtcNow.AddMinutes(-2), "api", 50);
        ui.Invoke(() => manager.SeedUsage(account.Id, local));
        var shared = Snapshot(DateTimeOffset.UtcNow.AddMinutes(-1), "shared", 20);
        var pointTime = DateTimeOffset.UtcNow.AddHours(-2);

        ui.Invoke(() => manager.ApplySharedUsage(new[] { (account.Id, shared, pointTime, false) }));

        ui.Invoke(() => manager.GetUsage(account.Id)).Should().BeSameAs(local, "追趕期間本機有自己查到的值：共享的只進歷史");
        ui.Invoke(() => manager.GetHistory(account.Id, "session")).Should().ContainSingle()
            .Which.Should().Equal(pointTime.ToUnixTimeMilliseconds(), 20);
        changed.Should().Be(1);

        // 最後快照（下次開 app 先顯示的值）也不換成共享的
        ui.Invoke(manager.Dispose);
        File.ReadAllText(Path.Combine(AppPaths.UsageDirectory, account.Id + ".json")).Should().NotContain("shared");
    }

    [Fact]
    public async Task 沒被指派查用量_共享的用量到了重置時間_輪詢時推算歸零()
    {
        // 2026-10-28 夜裡：每台的權杖都過期、沒有一台被指派，這台一直顯示重置前的 100%
        var account = SeedClaude(AccountStatus.Ready, DateTimeOffset.UtcNow.AddHours(3));
        ui.Invoke(() => manager.UsagePollPolicy = new FixedPolicy(false));
        var now = DateTimeOffset.UtcNow;
        var shared = new UsageSnapshot(now.AddHours(-8), "shared", new[]
        {
            new UsageWindow("session", "5 小時", 0, null),
            new UsageWindow("weekly", "每週", 100, now.AddMinutes(-1), Locked: true),
        }, null, null, null, false);
        ui.Invoke(() => manager.SeedUsage(account.Id, shared));
        changed = 0;

        ui.Invoke(manager.Tick);
        await ui.DrainAsync(5);

        var usage = ui.Invoke(() => manager.GetUsage(account.Id))!;
        usage.Windows.Single(w => w.Key == "weekly").Should().Be(new UsageWindow("weekly", "每週", 0, null, false));
        usage.Stale.Should().BeTrue();
        changed.Should().BeGreaterThan(0, "側欄要跟著更新");
        endpoint.Requests.Should().Be(0, "閘門照樣不查官方端點");
        ui.Invoke(() => manager.GetHistory(account.Id, "weekly")).Should().BeEmpty("推算的值不進走勢");
    }

    // ───── 目錄外觀與順序 ─────

    [Fact]
    public void ApplyCatalogAppearance_值相同不存檔不觸發()
    {
        var codex = SeedCodex("我的 Codex");

        ui.Invoke(() => manager.ApplyCatalogAppearance(codex.Id, "我的 Codex", "#19c3a6")).Should().BeFalse();
        ui.Invoke(() => manager.ApplyCatalogAppearance(codex.Id, "\u0001", "不是顏色")).Should().BeFalse("清理後不合格的那一項不改");
        ui.Invoke(() => manager.ApplyCatalogAppearance("不存在", "x", "#123")).Should().BeFalse();

        changed.Should().Be(0);
        File.Exists(AppPaths.AccountsFile).Should().BeFalse("值相同不存檔");
    }

    [Fact]
    public void ApplyCatalogAppearance_三碼顏色展開_60字截斷()
    {
        var codex = SeedCodex("Codex");
        var label = new string('名', 70);

        ui.Invoke(() => manager.ApplyCatalogAppearance(codex.Id, label, "#abc")).Should().BeTrue();

        codex.Label.Should().Be(new string('名', 60), "伺服器的名稱接受 60 字（本機輸入才截到 40 字）");
        codex.Color.Should().Be("#AABBCC");
        changed.Should().Be(1);
        JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single(a => a.Id == codex.Id).Label.Should().Be(new string('名', 60));
    }

    [Fact]
    public void ApplyCatalogOrder_只排存在的帳戶_沒變回false()
    {
        var a = SeedCodex("A");
        var b = SeedCodex("B");
        var c = SeedCodex("C");
        var d = SeedCodex("D");

        ui.Invoke(() => manager.ApplyCatalogOrder(new[] { d.Id, "不存在", b.Id })).Should().BeTrue();

        ui.Invoke(() => manager.Accounts.Select(x => x.Label).ToList()).Should().Equal(new[] { "A", "D", "C", "B" },
            "列到的 B、D 在它們原本的兩個位置依新順序互換，沒列到的 A、C 留在原位");
        changed.Should().Be(1);
        JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.OrderBy(x => x.Order).Select(x => x.Label).Should().Equal("A", "D", "C", "B");

        ui.Invoke(() => manager.ApplyCatalogOrder(new[] { d.Id, b.Id })).Should().BeFalse("順序沒變");
        ui.Invoke(() => manager.ApplyCatalogOrder(Array.Empty<string>())).Should().BeFalse();
        changed.Should().Be(1);
        _ = (a, c);
    }

    [Fact]
    public void 帳戶清單讀不到_IsAccountListReadOnly為true()
    {
        var codex = SeedCodex("Codex");
        ui.Invoke(() => manager.IsAccountListReadOnly).Should().BeFalse();

        ui.Invoke(manager.SeedListReadOnly);

        ui.Invoke(() => manager.IsAccountListReadOnly).Should().BeTrue();
        ui.Invoke(() => manager.ApplyCatalogAppearance(codex.Id, "新名稱", "#123456")).Should().BeTrue("記憶體照樣套用");
        File.Exists(AppPaths.AccountsFile).Should().BeFalse("唯讀時不存檔（不能用這次的清單蓋掉讀不到的原檔）");
    }

    // ───── Claude 的身分資訊 ─────

    [Fact]
    public async Task GetSyncIdentity_auth_status後有orgId與到期時間()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var expires = DateTimeOffset.UtcNow.AddHours(3);
        var account = SeedClaude(AccountStatus.Checking, expires);
        ui.Invoke(() => manager.GetSyncIdentity(account.Id)).Should().Be(new AccountSyncIdentity(null, null, null, null));

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        Calls().Should().ContainSingle();
        ui.Invoke(() => manager.GetSyncIdentity(account.Id))
            .Should().Be(new AccountSyncIdentity("00000000-0000-0000-0000-000000000000", expires.ToUnixTimeMilliseconds(), null, null));
        var saved = JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single(a => a.Id == account.Id);
        saved.LastIdentity!.OrgId.Should().Be("00000000-0000-0000-0000-000000000000", "orgId 隨身分快取存進 accounts.json（下次啟動用快取時也拿得到）");
        File.ReadAllText(AppPaths.AccountsFile).Should().NotContain("sk-ant-", "快取不含權杖");
    }

    [Fact]
    public async Task GetSyncIdentity_UseCache時取快取的orgId()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var expires = DateTimeOffset.UtcNow.AddHours(3);
        var account = NewClaude(expires);
        account.Email = "cached@example.com";
        account.LastIdentity = AccountRules.BuildIdentityCache(AccountStatus.Ready, "cached@example.com", "Max",
            AccountManager.ReadCredentialInfo(Path.Combine(account.ConfigDirectory!, ".credentials.json")), DateTime.Now, "org-cached");
        JsonFile.Write(AppPaths.AccountsFile, new List<AgentAccount> { account });
        ui.Invoke(() => manager.UsagePollPolicy = new FixedPolicy(false));

        ui.Invoke(manager.Initialize);

        await WaitUntilAsync(() => ui.Invoke(() => manager.GetState(account.Id).Status) == AccountStatus.Ready, "以快取判定已登入");
        await WaitUntilAsync(() => ui.Invoke(() => manager.GetState(account.Id).OrgId) != null, "runtime 的 orgId 取自快取");
        Calls().Should().BeEmpty("S4：快取有效時啟動 0 次 auth status");
        ui.Invoke(() => manager.GetSyncIdentity(account.Id)).Should().Be(new AccountSyncIdentity("org-cached", expires.ToUnixTimeMilliseconds(), null, null));
    }

    [Fact]
    public void GetSyncIdentity_沒有runtime值時用身分快取()
    {
        var account = NewClaude(DateTimeOffset.UtcNow.AddHours(3));
        account.Email = "saved@example.com";
        account.LastIdentity = new AccountIdentityCache { Status = AccountStatus.Ready, Email = "Saved@Example.com", OrgId = "org-saved", ExpiresAt = 123 };
        var other = NewClaude(DateTimeOffset.UtcNow.AddHours(3));
        other.Email = "now@example.com";
        other.LastIdentity = new AccountIdentityCache { Status = AccountStatus.Ready, Email = "before@example.com", OrgId = "org-before", ExpiresAt = 456 };
        var unknown = NewClaude(DateTimeOffset.UtcNow.AddHours(3));
        unknown.LastIdentity = new AccountIdentityCache { Status = AccountStatus.Ready, OrgId = "org-unknown", ExpiresAt = 789 };
        ui.Invoke(() => manager.Seed(new[] { account, other, unknown }, AccountStatus.Ready));

        ui.Invoke(() => manager.GetSyncIdentity(account.Id)).Should().Be(new AccountSyncIdentity("org-saved", 123, null, null), "快取的 email 相同（不分大小寫）");
        ui.Invoke(() => manager.GetSyncIdentity(other.Id)).Should().Be(new AccountSyncIdentity(null, 456, null, null), "email 換了：不沿用舊的 org");
        ui.Invoke(() => manager.GetSyncIdentity(unknown.Id)).Should().Be(new AccountSyncIdentity(null, 789, null, null), "email 不知道：無法確認是同一個登入");
        ui.Invoke(() => manager.GetSyncIdentity("不存在")).Should().Be(new AccountSyncIdentity(null, null, null, null));
    }

    [Fact]
    public async Task GetSyncIdentity_帶LoginExpiresAt與宣告()
    {
        FakeOutput(RealSamples.ClaudeAuthStatusLoggedIn);
        var expires = DateTimeOffset.UtcNow.AddHours(3);
        var account = NewClaude(expires, refreshTokenExpiresAt: 1794000000000);
        account.LogicalAccountId = "la_0123456789ABCDEFGHJKMNPQRS";
        ui.Invoke(() => manager.Seed(new[] { account }, AccountStatus.Checking));
        ui.Invoke(() => manager.GetSyncIdentity(account.Id)).Should().Be(new AccountSyncIdentity(null, null, null, "la_0123456789ABCDEFGHJKMNPQRS"),
            "宣告存在帳戶上，還沒查過也帶");

        await RunOnUi(() => manager.RefreshAsync(account.Id, includeStatus: true));

        ui.Invoke(() => manager.GetSyncIdentity(account.Id)).Should().Be(new AccountSyncIdentity("00000000-0000-0000-0000-000000000000",
            expires.ToUnixTimeMilliseconds(), 1794000000000, "la_0123456789ABCDEFGHJKMNPQRS"));
        ui.Invoke(() => manager.GetState(account.Id).LoginExpiresAt).Should().Be(1794000000000);
        File.ReadAllText(AppPaths.AccountsFile).Should().NotContain("1794000000000", "登入到期不存檔");
    }

    [Fact]
    public void 舊版accounts_json沒有OrgId_讀得回來()
    {
        var old = """
            [{"Id":"0123456789abcdef0123456789abcdef","Provider":"Claude","Label":"舊版","Color":"#D97757","ConfigDirectory":null,"SafeMode":false,"Order":0,
              "Email":"old@example.com","Plan":"Max","CreatedAt":"2026-01-01T00:00:00",
              "LastIdentity":{"Status":"Ready","Email":"old@example.com","Plan":"Max","CredentialSize":10,"CredentialWriteTicks":20,"ExpiresAt":30,"CheckedAt":"2026-01-01T00:00:00"}}]
            """;
        File.WriteAllText(AppPaths.AccountsFile, old);

        var read = JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single();

        read.LastIdentity!.OrgId.Should().BeNull();
        read.LastIdentity.Email.Should().Be("old@example.com");
        read.LastIdentity.ExpiresAt.Should().Be(30);

        // 新版寫回時多了 OrgId 欄位（舊版 app 讀到會忽略：Newtonsoft 預設略過不認得的欄位）
        read.LastIdentity.OrgId = "org-new";
        JsonFile.Write(AppPaths.AccountsFile, new List<AgentAccount> { read });
        JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single().LastIdentity!.OrgId.Should().Be("org-new");
    }

    /// <summary>固定回答的用量閘門；記下被問到的帳戶</summary>
    private sealed class FixedPolicy(bool answer) : IUsagePollPolicy
    {
        public List<string> Asked { get; } = new();

        public bool ShouldPoll(AgentAccount account)
        {
            Asked.Add(account.Id);
            return answer;
        }
    }

    /// <summary>假的用量端點：一律回實測的用量回應，記下請求數</summary>
    private sealed class CountingUsageEndpoint : HttpMessageHandler
    {
        private int requests;

        public int Requests => Volatile.Read(ref requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RealSamples.ClaudeUsage, Encoding.UTF8, "application/json") });
        }
    }
}
