using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>帳戶的純函式規則：子程序環境、設定資料夾、登入狀態判讀（claude auth status／Codex account/read 實測輸出）、方案、預設名稱與顏色</summary>
public class AccountRulesTests
{
    private static AgentAccount SystemDefault(AgentProvider provider) => new() { Provider = provider, Label = "本機" };

    private static AgentAccount Isolated(AgentProvider provider, string dir) => new() { Provider = provider, Label = "隔離", ConfigDirectory = dir };

    /// <summary>從 Claude Code session 裡啟動 app 時會繼承到的環境（值是假的）</summary>
    private static Dictionary<string, string?> InheritedEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = @"C:\Windows",
        ["ANTHROPIC_API_KEY"] = "sk-ant-api",
        ["ANTHROPIC_AUTH_TOKEN"] = "token",
        ["OPENAI_API_KEY"] = "sk-openai",
        ["CODEX_API_KEY"] = "sk-codex",
        ["CLAUDECODE"] = "1",
        ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
        ["CLAUDE_CODE_SESSION_ID"] = "parent-session",
        ["CLAUDE_EFFORT"] = "max",
        ["CLAUDE_CODE_MESSAGING_TOKEN"] = "secret",
        ["CLAUDE_CODE_GIT_BASH_PATH"] = @"C:\Git\bin\bash.exe",
        ["CLAUDE_CONFIG_DIR"] = @"D:\my-claude",
        ["CODEX_HOME"] = @"D:\my-codex",
        ["CODEX_COMPANION_SESSION_ID"] = "x",
    };

    [Fact]
    public void ApplyEnvironment_剝除APIKey與父Session變數_保留GitBash路徑()
    {
        var env = InheritedEnvironment();

        AccountRules.ApplyEnvironment(SystemDefault(AgentProvider.Claude), env);

        env.Keys.Should().NotContain(new[]
        {
            "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "CODEX_API_KEY",
            "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_EFFORT", "CLAUDE_CODE_MESSAGING_TOKEN",
        });
        env["CLAUDE_CODE_GIT_BASH_PATH"].Should().Be(@"C:\Git\bin\bash.exe");
        env["PATH"].Should().Be(@"C:\Windows");
        env["CODEX_COMPANION_SESSION_ID"].Should().Be("x", "不是 CLAUDE 開頭的其他變數不動");
    }

    [Theory]
    [InlineData(nameof(AgentProvider.Claude))]
    [InlineData(nameof(AgentProvider.Codex))]
    public void ApplyEnvironment_系統預設帳戶保留使用者自己設的設定資料夾(string provider)
    {
        var env = InheritedEnvironment();

        AccountRules.ApplyEnvironment(SystemDefault(Enum.Parse<AgentProvider>(provider)), env);

        env["CLAUDE_CONFIG_DIR"].Should().Be(@"D:\my-claude");
        env["CODEX_HOME"].Should().Be(@"D:\my-codex");
    }

    [Fact]
    public void ApplyEnvironment_隔離的Claude帳戶指定CLAUDE_CONFIG_DIR()
    {
        var env = InheritedEnvironment();

        AccountRules.ApplyEnvironment(Isolated(AgentProvider.Claude, @"C:\data\accounts\a1\claude"), env);

        env["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\data\accounts\a1\claude", "蓋掉繼承來的設定資料夾");
        env.Keys.Should().NotContain("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void ApplyEnvironment_隔離的Codex帳戶指定CODEX_HOME()
    {
        var env = InheritedEnvironment();

        AccountRules.ApplyEnvironment(Isolated(AgentProvider.Codex, @"C:\data\accounts\a2\codex"), env);

        env["CODEX_HOME"].Should().Be(@"C:\data\accounts\a2\codex");
        env.Keys.Should().NotContain(new[] { "OPENAI_API_KEY", "CODEX_API_KEY" });
    }

    [Fact]
    public void ApplyToolEnvironment_只剝除APIKey與父Session變數_不指定設定資料夾()
    {
        var env = InheritedEnvironment();

        AccountRules.ApplyToolEnvironment(env);

        env.Keys.Should().BeEquivalentTo(new[] { "PATH", "CLAUDE_CODE_GIT_BASH_PATH", "CLAUDE_CONFIG_DIR", "CODEX_HOME", "CODEX_COMPANION_SESSION_ID" });
        env["CLAUDE_CONFIG_DIR"].Should().Be(@"D:\my-claude", "查版本、更新不分帳戶，維持繼承值");
    }

    [Fact]
    public void ApplyToolEnvironment_剝掉繼承的背景程式標記()
    {
        // app 自己是從某個對話開出來的（開發時）：查詢、登入這些子程序不算那個對話的背景程式
        var env = InheritedEnvironment();
        env[Myriagent.Background.BackgroundProgramRules.MarkerVariable.ToLowerInvariant()] = "0123456789abcdef0123456789abcdef";

        AccountRules.ApplyToolEnvironment(env);

        env.Keys.Should().NotContain(k => k.Equals(Myriagent.Background.BackgroundProgramRules.MarkerVariable, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyCommandEnvironment_帳戶環境之外另帶DisableAutoUpdater()
    {
        var env = InheritedEnvironment();
        AccountRules.ApplyCommandEnvironment(Isolated(AgentProvider.Claude, @"C:\data\accounts\a1\claude"), env);
        env["DISABLE_AUTOUPDATER"].Should().Be("1", "app 跑的 claude 子指令一律不在背景自動更新（claude update 不受影響，只看 DISABLE_UPDATES）");
        env["CLAUDE_CONFIG_DIR"].Should().Be(@"C:\data\accounts\a1\claude");
        env.Keys.Should().NotContain(new[] { "ANTHROPIC_API_KEY", "CLAUDE_CODE_SESSION_ID" });

        var tool = InheritedEnvironment();
        AccountRules.ApplyCommandEnvironment(null, tool);
        tool["DISABLE_AUTOUPDATER"].Should().Be("1");
        tool["CLAUDE_CONFIG_DIR"].Should().Be(@"D:\my-claude", "不分帳戶的指令（--version、update）維持繼承的設定資料夾");
    }

    [Fact]
    public void ApplyEnvironment_變數名稱不分大小寫()
    {
        var env = new Dictionary<string, string?> { ["anthropic_api_key"] = "x", ["Claude_Code_Entrypoint"] = "cli", ["claude_code_git_bash_path"] = "bash" };

        AccountRules.ApplyEnvironment(SystemDefault(AgentProvider.Claude), env);

        env.Keys.Should().Equal("claude_code_git_bash_path");
    }

    [Fact]
    public void 設定資料夾與憑證檔位置()
    {
        var claude = Isolated(AgentProvider.Claude, @"C:\data\accounts\a1\claude");
        var codex = Isolated(AgentProvider.Codex, @"C:\data\accounts\a2\codex");

        AccountRules.ResolveConfigDirectory(claude).Should().Be(@"C:\data\accounts\a1\claude");
        AccountRules.GetCredentialFile(claude).Should().Be(@"C:\data\accounts\a1\claude\.credentials.json");
        AccountRules.GetCredentialFile(codex).Should().Be(@"C:\data\accounts\a2\codex\auth.json");
        claude.IsSystemDefault.Should().BeFalse();
        new AgentAccount { ConfigDirectory = " " }.IsSystemDefault.Should().BeTrue();
    }

    // ───── claude auth status --json ─────

    [Fact]
    public void EvaluateClaudeAuthStatus_實測已登入訂閱()
    {
        var (status, email, plan) = AccountRules.EvaluateClaudeAuthStatus(RealSamples.ClaudeAuthStatusLoggedIn);
        status.Should().Be(AccountStatus.Ready);
        email.Should().Be("user@example.com");
        plan.Should().Be("Max");
    }

    [Fact]
    public void EvaluateClaudeAuthStatus_實測空設定資料夾是未登入()
    {
        var (status, email, plan) = AccountRules.EvaluateClaudeAuthStatus(RealSamples.ClaudeAuthStatusEmptyConfig);
        status.Should().Be(AccountStatus.NotLoggedIn);
        email.Should().BeNull();
        plan.Should().BeNull();
    }

    [Fact]
    public void EvaluateClaudeAuthStatus_Console登入是APIKey計費()
    {
        var (status, email, plan) = AccountRules.EvaluateClaudeAuthStatus("""{"loggedIn":true,"authMethod":"console","apiProvider":"firstParty","email":"dev@example.com"}""");
        status.Should().Be(AccountStatus.ApiKey);
        email.Should().Be("dev@example.com");
        plan.Should().Be("console", "沒有訂閱方案時顯示登入方式");

        AccountRules.EvaluateClaudeAuthStatus("""{"loggedIn":true,"authMethod":"api_key"}""").Status.Should().Be(AccountStatus.ApiKey);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{ 壞掉的 JSON")]
    [InlineData("\n執行逾時")]
    [InlineData("""{"error":"沒有 loggedIn 欄位"}""")]
    public void EvaluateClaudeAuthStatus_讀不到結果是檢查失敗_不是未登入(string? output)
        => AccountRules.EvaluateClaudeAuthStatus(output).Status.Should().Be(AccountStatus.Error,
            "逾時、啟動失敗、輸出被打斷都是這次檢查失敗；當成未登入會清掉用量、擋住送出，之後也不會自己重查");

    [Fact]
    public void EvaluateClaudeAuthStatus_JSON前後有其他輸出也讀得到()
    {
        var output = "Warning: something\n" + RealSamples.ClaudeAuthStatusLoggedIn + "\n(stderr) done";
        AccountRules.EvaluateClaudeAuthStatus(output).Status.Should().Be(AccountStatus.Ready);
    }

    [Fact]
    public void EvaluateClaudeAuthStatus_前後的警告含大括號也讀得到()
    {
        var stderrAfter = RealSamples.ClaudeAuthStatusLoggedIn + "\n" + """Warning: ignoring unknown setting {"foo":1}""";
        AccountRules.EvaluateClaudeAuthStatus(stderrAfter).Status.Should().Be(AccountStatus.Ready, "stdout 的 JSON 之後是 stderr 的警告");

        var warningBefore = """Warning: {"deprecated":true}""" + "\n" + RealSamples.ClaudeAuthStatusEmptyConfig;
        AccountRules.EvaluateClaudeAuthStatus(warningBefore).Status.Should().Be(AccountStatus.NotLoggedIn, "跳過不含 loggedIn 的物件");
    }

    [Fact]
    public void DescribeClaudeAuthStatusFailure_逾時_stderr第一行_或讀不到結果()
    {
        AccountRules.DescribeClaudeAuthStatusFailure("執行逾時", timedOut: true).Should().Be("claude auth status 執行逾時");
        AccountRules.DescribeClaudeAuthStatusFailure("\n  系統找不到指定的檔案。\n其他", timedOut: false).Should().Be("系統找不到指定的檔案。");
        AccountRules.DescribeClaudeAuthStatusFailure(new string('錯', 200), timedOut: false).Should().HaveLength(161).And.EndWith("…");
        AccountRules.DescribeClaudeAuthStatusFailure(" ", timedOut: false).Should().Be("讀不到 claude auth status 的結果");
        AccountRules.DescribeClaudeAuthStatusFailure(null, timedOut: false).Should().Be("讀不到 claude auth status 的結果");
    }

    // ───── 短命 claude 的統一守門與啟動身分快取（設計 6.5，S4） ─────

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

    /// <summary>捏造的憑證（權杖是假的字串；expiresAt 為 null＝不寫這個欄位）</summary>
    private static string Credentials(DateTimeOffset? expiresAt, string? refreshToken = "sk-ant-ort01-test", string accessToken = "sk-ant-oat01-test")
    {
        var oauth = new JObject { ["accessToken"] = accessToken, ["subscriptionType"] = "max" };
        if (refreshToken != null)
            oauth["refreshToken"] = refreshToken;
        if (expiresAt.HasValue)
            oauth["expiresAt"] = expiresAt.Value.ToUnixTimeMilliseconds();
        return new JObject { ["claudeAiOauth"] = oauth }.ToString();
    }

    [Theory]
    [InlineData(-60 * 60, false)]
    [InlineData(4 * 60, false)]
    [InlineData(9 * 60 + 59, false)]
    [InlineData(10 * 60, false)]
    [InlineData(10 * 60 + 1, true)]
    [InlineData(180 * 60, true)]
    public void CanRunShortLivedClaude_權杖距到期不到10分鐘就不跑_9分59秒與10分01秒(int secondsLeft, bool expected)
    {
        AccountRules.CanRunShortLivedClaude(Credentials(Now.AddSeconds(secondsLeft)), Now).Should().Be(expected,
            "實測 auth status 遇到 5 分鐘內到期的權杖會開始換發卻不等結果就結束（可能把 refresh token 用掉），所以留 10 分鐘餘裕");
    }

    [Fact]
    public void CanRunShortLivedClaude_空權杖_沒有ExpiresAt_讀不懂都不跑_沒有OAuth權杖才照常()
    {
        AccountRules.CanRunShortLivedClaude(Credentials(Now.AddHours(3), accessToken: ""), Now).Should().BeFalse("空權杖＝CLI 換發被拒後清空的登入");
        AccountRules.CanRunShortLivedClaude(Credentials(Now.AddHours(3), accessToken: "  "), Now).Should().BeFalse();
        AccountRules.CanRunShortLivedClaude(Credentials(null), Now).Should().BeFalse("有權杖卻不知道何時到期");
        AccountRules.CanRunShortLivedClaude("""{"claudeAiOauth":{"accessToken":"t","refreshToken":"r","expiresAt":"不是數字"}}""", Now).Should().BeFalse();
        AccountRules.CanRunShortLivedClaude("""{"claudeAiOauth":{"accessToken":"t","expiresAt":null}}""", Now).Should().BeFalse();
        AccountRules.CanRunShortLivedClaude("{壞掉", Now).Should().BeFalse("讀不懂（CLI 可能正在寫入）");
        AccountRules.CanRunShortLivedClaude(Credentials(Now.AddHours(-1), refreshToken: null), Now).Should().BeFalse("過期的權杖一律不跑（refresh token 可能在別處）");

        AccountRules.CanRunShortLivedClaude(null, Now).Should().BeTrue("沒有憑證檔：沒有權杖可換發");
        AccountRules.CanRunShortLivedClaude("""{"mcpOAuth":{}}""", Now).Should().BeTrue("沒有 claudeAiOauth（API Key 登入等）");
        AccountRules.CanRunShortLivedClaude("""{"claudeAiOauth":{"subscriptionType":"max"}}""", Now).Should().BeTrue("沒有 accessToken 欄位");
        AccountRules.CanRunShortLivedClaude(RealSamples.ClaudeCredentials, DateTimeOffset.FromUnixTimeMilliseconds(1791600000000).AddMinutes(-30))
            .Should().BeTrue("實測憑證形狀：還有 30 分鐘");
        AccountRules.CanRunShortLivedClaude(RealSamples.ClaudeCredentials, DateTimeOffset.FromUnixTimeMilliseconds(1791600000000).AddMinutes(-5))
            .Should().BeFalse("實測憑證形狀：剩 5 分鐘");
    }

    [Fact]
    public void InspectClaudeToken_分辨各種權杖狀況()
    {
        AccountRules.InspectClaudeToken(Credentials(Now.AddHours(3)), Now).Should().Be(ClaudeTokenState.Fresh);
        AccountRules.InspectClaudeToken(Credentials(Now.AddMinutes(9)), Now).Should().Be(ClaudeTokenState.Expiring);
        AccountRules.InspectClaudeToken(Credentials(null), Now).Should().Be(ClaudeTokenState.MissingExpiry);
        AccountRules.InspectClaudeToken(Credentials(Now.AddHours(3), accessToken: ""), Now).Should().Be(ClaudeTokenState.Empty);
        AccountRules.InspectClaudeToken("""{"claudeAiOauth":{"accessToken":"","refreshToken":"","expiresAt":0}}""", Now)
            .Should().Be(ClaudeTokenState.Empty, "研究實測：claude -p 換發被拒後 accessToken／refreshToken 變成空字串、expiresAt 變 0");
        AccountRules.InspectClaudeToken("""{"claudeAiOauth":{"accessToken":123}}""", Now).Should().Be(ClaudeTokenState.Empty, "不是字串的權杖當成沒有用的權杖");
        AccountRules.InspectClaudeToken("[1]", Now).Should().Be(ClaudeTokenState.Unreadable);
        AccountRules.InspectClaudeToken(" ", Now).Should().Be(ClaudeTokenState.None);
        AccountRules.ReadClaudeExpiresAt(RealSamples.ClaudeCredentials).Should().Be(1791600000000);
        AccountRules.ReadClaudeExpiresAt(Credentials(null)).Should().BeNull();
        AccountRules.ReadClaudeExpiresAt("{壞掉").Should().BeNull();
    }

    private static readonly DateTime Written = new(2026, 10, 10, 7, 30, 0, DateTimeKind.Utc);

    private static ClaudeCredentialInfo FileWith(string json, long? size = null, DateTime? written = null)
        => new(true, size ?? json.Length, written ?? Written, json);

    private static AccountIdentityCache CacheFor(ClaudeCredentialInfo credentials, AccountStatus status = AccountStatus.Ready)
        => AccountRules.BuildIdentityCache(status, "user@example.com", "Max 20x", credentials, Written.ToLocalTime())!;

    [Fact]
    public void BuildIdentityCache_只在已登入時記下憑證檔的大小時間與ExpiresAt()
    {
        var file = FileWith(Credentials(Now.AddHours(3)));
        var cache = CacheFor(file);
        cache.Status.Should().Be(AccountStatus.Ready);
        cache.Email.Should().Be("user@example.com");
        cache.CredentialSize.Should().Be(file.Size);
        cache.CredentialWriteTicks.Should().Be(Written.Ticks);
        cache.ExpiresAt.Should().Be(Now.AddHours(3).ToUnixTimeMilliseconds());
        AccountRules.BuildIdentityCache(AccountStatus.ApiKey, null, "API Key", file, DateTime.Now)!.Status.Should().Be(AccountStatus.ApiKey);
        AccountRules.BuildIdentityCache(AccountStatus.NotLoggedIn, null, null, file, DateTime.Now).Should().BeNull("未登入就清掉快取");
        AccountRules.BuildIdentityCache(AccountStatus.Error, null, null, file, DateTime.Now).Should().BeNull();
        var credman = AccountRules.BuildIdentityCache(AccountStatus.Ready, "a@b.c", null, ClaudeCredentialInfo.Missing, DateTime.Now)!;
        credman.Status.Should().Be(AccountStatus.Ready, "憑證改存在 Windows 認證管理員：仍記下曾經登入（保守模式沿用它的狀態與方案）");
        credman.CredentialSize.Should().Be(0);
        AccountRules.IsIdentityCacheValid(credman, ClaudeCredentialInfo.Missing).Should().BeFalse("沒有憑證檔可以比對：啟動時不能直接當成已登入");
        AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, credman, ClaudeStatusReason.Startup, Now, CredentialManagerConfig).Should().Be(ClaudeStatusDecision.Conservative);
    }

    /// <summary>CLI 設定：啟用了 Windows 認證管理員、還有 OAuth 登入（權杖在認證管理員）</summary>
    private static readonly ClaudeGlobalConfigInfo CredentialManagerConfig = new(true, CredentialManager: true, HasOAuthAccount: true, "user@example.com");

    [Fact]
    public void IsIdentityCacheValid_憑證檔的大小_修改時間_ExpiresAt任一改變就失效()
    {
        var json = Credentials(Now.AddHours(3));
        var file = FileWith(json);
        var cache = CacheFor(file);

        AccountRules.IsIdentityCacheValid(cache, file).Should().BeTrue();
        AccountRules.IsIdentityCacheValid(cache, file with { Size = file.Size + 1 }).Should().BeFalse("CLI 換發權杖會改寫檔案");
        AccountRules.IsIdentityCacheValid(cache, file with { LastWriteUtc = Written.AddMilliseconds(1) }).Should().BeFalse();
        AccountRules.IsIdentityCacheValid(cache, FileWith(Credentials(Now.AddHours(4)), size: file.Size)).Should().BeFalse("expiresAt 變了＝換過權杖");
        AccountRules.IsIdentityCacheValid(cache, ClaudeCredentialInfo.Missing).Should().BeFalse();
        AccountRules.IsIdentityCacheValid(null, file).Should().BeFalse();
        AccountRules.IsIdentityCacheValid(new AccountIdentityCache { Status = AccountStatus.NotLoggedIn, CredentialSize = file.Size, CredentialWriteTicks = Written.Ticks }, file)
            .Should().BeFalse("只認曾經確認登入的快取");
    }

    [Fact]
    public void DecideClaudeStatusCheck_啟動與定期重查時快取有效就不跑AuthStatus_手動與憑證檔變動才跑()
    {
        var file = FileWith(Credentials(Now.AddHours(3)));
        var cache = CacheFor(file);

        AccountRules.DecideClaudeStatusCheck(file, cache, ClaudeStatusReason.Startup, Now).Should().Be(ClaudeStatusDecision.UseCache, "S4：啟動時身分快取有效就 0 次 auth status");
        AccountRules.DecideClaudeStatusCheck(file, cache, ClaudeStatusReason.Periodic, Now).Should().Be(ClaudeStatusDecision.UseCache);
        AccountRules.DecideClaudeStatusCheck(file, cache, ClaudeStatusReason.Manual, Now).Should().Be(ClaudeStatusDecision.RunAuthStatus, "使用者手動要求且權杖新鮮");
        AccountRules.DecideClaudeStatusCheck(file, cache, ClaudeStatusReason.CredentialsChanged, Now).Should().Be(ClaudeStatusDecision.RunAuthStatus, "憑證檔變動且權杖新鮮");
        AccountRules.DecideClaudeStatusCheck(file, null, ClaudeStatusReason.Startup, Now).Should().Be(ClaudeStatusDecision.RunAuthStatus, "身分未知且權杖新鮮");
        AccountRules.DecideClaudeStatusCheck(file with { Size = file.Size + 5 }, cache, ClaudeStatusReason.Startup, Now).Should().Be(ClaudeStatusDecision.RunAuthStatus, "憑證檔變了：快取失效");
    }

    [Fact]
    public void DecideClaudeStatusCheck_權杖不新鮮時任何理由都不跑()
    {
        var expiring = FileWith(Credentials(Now.AddMinutes(9).AddSeconds(59)));
        var cache = CacheFor(expiring);
        foreach (var reason in Enum.GetValues<ClaudeStatusReason>())
        {
            AccountRules.DecideClaudeStatusCheck(expiring, cache, reason, Now).Should().Be(ClaudeStatusDecision.TokenPending, $"S4：到期前 10 分鐘內 0 次（{reason}）");
            AccountRules.DecideClaudeStatusCheck(FileWith(Credentials(null)), null, reason, Now).Should().Be(ClaudeStatusDecision.TokenPending, "沒有 expiresAt");
            AccountRules.DecideClaudeStatusCheck(FileWith(Credentials(Now.AddHours(3), accessToken: "")), cache, reason, Now)
                .Should().Be(ClaudeStatusDecision.LoginExpired, "空權杖＝登入已失效，不跑 CLI");
        }
        AccountRules.DecideClaudeStatusCheck(new ClaudeCredentialInfo(true, 0, default, null), cache, ClaudeStatusReason.Manual, Now)
            .Should().Be(ClaudeStatusDecision.CredentialsUnreadable, "憑證檔存在卻讀不到（被占用）：不能判斷權杖，不跑");
        AccountRules.DecideClaudeStatusCheck(FileWith("{壞掉"), null, ClaudeStatusReason.Startup, Now)
            .Should().Be(ClaudeStatusDecision.CredentialsUnreadable, "讀不懂（CLI 可能正在寫入）：不能當成已登入，也不跑");
    }

    [Fact]
    public void DecideClaudeStatusCheck_憑證檔不存在而權杖在認證管理員_任何理由都不跑_有沒有快取都一樣()
    {
        var cache = CacheFor(FileWith(Credentials(Now.AddHours(3))));
        var apiKey = CacheFor(FileWith(Credentials(Now.AddHours(3))), AccountStatus.ApiKey);
        foreach (var reason in Enum.GetValues<ClaudeStatusReason>())
        {
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, cache, reason, Now, CredentialManagerConfig).Should().Be(ClaudeStatusDecision.Conservative,
                $"權杖在 Windows 認證管理員：看不到期限，對話一輪剛結束時也可能只剩 5 分鐘出頭——短命的 claude 一律不跑（{reason}）");
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, null, reason, Now, CredentialManagerConfig).Should().Be(ClaudeStatusDecision.Conservative,
                $"升級後第一次啟動、還原的帳戶沒有快取：以 CLI 設定的 oauthAccount 判定，也不跑（{reason}）");
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, apiKey, reason, Now, CredentialManagerConfig).Should().Be(ClaudeStatusDecision.Conservative);
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, cache, reason, Now).Should().Be(ClaudeStatusDecision.ConfigUnreadable,
                "沒讀 CLI 設定＝無法判斷，不跑");
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, cache, reason, Now, ClaudeGlobalConfigInfo.Unreadable).Should().Be(ClaudeStatusDecision.ConfigUnreadable,
                "CLI 設定正在寫入、讀不懂：不知道權杖在哪，不跑、稍後再查");
        }
    }

    [Fact]
    public void DecideClaudeStatusCheck_憑證檔不存在而權杖不在任何地方_照常跑_外部登出與APIKey才判斷得出來()
    {
        var cache = CacheFor(FileWith(Credentials(Now.AddHours(3))));
        var apiKey = CacheFor(FileWith(Credentials(Now.AddHours(3))), AccountStatus.ApiKey);
        var loggedOut = CredentialManagerConfig with { HasOAuthAccount = false, OAuthEmail = null };
        foreach (var reason in Enum.GetValues<ClaudeStatusReason>())
        {
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, cache, reason, Now, ClaudeGlobalConfigInfo.Missing).Should().Be(ClaudeStatusDecision.RunAuthStatus,
                $"沒有啟用認證管理員＝CLI 只用憑證檔，憑證檔不在就沒有權杖可換發（在終端機登出會刪掉憑證檔）：照常跑才看得出已登出（{reason}）");
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, cache, reason, Now, loggedOut).Should().Be(ClaudeStatusDecision.RunAuthStatus,
                $"啟用了認證管理員但 oauthAccount 被清掉（CLI 登出會清）：權杖也已刪掉，照常跑（{reason}）");
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, apiKey, reason, Now, new ClaudeGlobalConfigInfo(true, false, true, "x@example.com"))
                .Should().Be(ClaudeStatusDecision.RunAuthStatus, "API Key 或第三方雲端登入本來就沒有 OAuth 權杖檔：照常跑，維持 API 計費的說明");
            AccountRules.DecideClaudeStatusCheck(ClaudeCredentialInfo.Missing, null, reason, Now, ClaudeGlobalConfigInfo.Missing).Should().Be(ClaudeStatusDecision.RunAuthStatus,
                "從沒登入過（新的隔離帳戶）：沒有權杖可換發，照常判斷未登入或 API Key 登入");
        }
        AccountRules.DecideClaudeStatusCheck(FileWith("""{"mcpOAuth":{}}"""), cache, ClaudeStatusReason.Startup, Now).Should().Be(ClaudeStatusDecision.RunAuthStatus,
            "憑證檔在、沒有 OAuth 權杖（API Key 登入）：不必看 CLI 設定");
    }

    [Fact]
    public void ParseClaudeGlobalConfig_只看認證管理員旗標與oauthAccount_讀不懂就無法判斷()
    {
        var full = """
            {
              "numStartups": 12,
              "projects": { "C:\\work": { "history": [ { "display": "{\"cachedGrowthBookFeatures\":{\"tengu_windows_credman\":true}}" } ] } },
              "cachedGrowthBookFeatures": { "tengu_other": [1, 2, {"a": null}], "tengu_windows_credman": true },
              "oauthAccount": { "accountUuid": "u", "emailAddress": " me@example.com ", "organizationName": "Org" },
              "userID": "abc"
            }
            """;
        AccountRules.ParseClaudeGlobalConfig(full).Should().Be(new ClaudeGlobalConfigInfo(true, true, true, "me@example.com"));
        AccountRules.ParseClaudeGlobalConfig("""{"cachedGrowthBookFeatures":{"tengu_windows_credman":false},"oauthAccount":{"emailAddress":"a@b.c"}}""")
            .Should().Be(new ClaudeGlobalConfigInfo(true, false, true, "a@b.c"));
        AccountRules.ParseClaudeGlobalConfig("""{"cachedGrowthBookFeatures":{"tengu_windows_credman":"true"}}""")
            .Should().Be(new ClaudeGlobalConfigInfo(true, false, false, null), "CLI 只認 === true");
        AccountRules.ParseClaudeGlobalConfig("""{"projects":{"x":{"cachedGrowthBookFeatures":{"tengu_windows_credman":true},"oauthAccount":{}}}}""")
            .Should().Be(new ClaudeGlobalConfigInfo(true, false, false, null), "只看最上層的欄位");
        AccountRules.ParseClaudeGlobalConfig("""{"oauthAccount":null,"cachedGrowthBookFeatures":null}""")
            .Should().Be(new ClaudeGlobalConfigInfo(true, false, false, null), "登出後 oauthAccount 被清掉");
        AccountRules.ParseClaudeGlobalConfig("{}").Should().Be(new ClaudeGlobalConfigInfo(true, false, false, null));

        AccountRules.ParseClaudeGlobalConfig("""{"oauthAccount":{"emailAddress":"a@b.c"},"cachedGrowthBookFeatures":{"tengu_windows_credman":true""")
            .Should().Be(ClaudeGlobalConfigInfo.Unreadable, "CLI 寫到一半");
        AccountRules.ParseClaudeGlobalConfig("""{"oauthAccount":{"emailAddress":"a@b.c"}""").Should().Be(ClaudeGlobalConfigInfo.Unreadable, "停在內層物件的結尾＝沒寫完");
        AccountRules.ParseClaudeGlobalConfig("[1]").Should().Be(ClaudeGlobalConfigInfo.Unreadable);
        AccountRules.ParseClaudeGlobalConfig("{壞掉").Should().Be(ClaudeGlobalConfigInfo.Unreadable);
        AccountRules.ParseClaudeGlobalConfig("  ").Should().Be(ClaudeGlobalConfigInfo.Unreadable);
        AccountRules.ParseClaudeGlobalConfig(null).Should().Be(ClaudeGlobalConfigInfo.Unreadable);
    }

    [Fact]
    public void ParseClaudeGlobalConfig_oauthAccount的organizationUuid()
    {
        AccountRules.ParseClaudeGlobalConfig("""{"oauthAccount":{"emailAddress":"a@b.c","organizationUuid":"org-1"}}""")
            .Should().Be(new ClaudeGlobalConfigInfo(true, false, true, "a@b.c", "org-1"));
        AccountRules.ParseClaudeGlobalConfig("""{"oauthAccount":{"organizationUuid":"org 1"}}""")
            .Should().Be(new ClaudeGlobalConfigInfo(true, false, true, null, null), "含空白不合格");
        AccountRules.ParseClaudeGlobalConfig("""{"oauthAccount":{"organizationUuid":42}}""")
            .Should().Be(new ClaudeGlobalConfigInfo(true, false, true, null, null), "不是字串");
        AccountRules.ParseClaudeGlobalConfig("""{"organizationUuid":"org-top","projects":{"x":{"oauthAccount":{"organizationUuid":"org-x"}}}}""")
            .Should().Be(new ClaudeGlobalConfigInfo(true, false, false, null, null), "只看最上層 oauthAccount 裡的");
        AccountRules.ParseClaudeGlobalConfig("""{"oauthAccount":{"emailAddress":"a@b.c","organizationUuid":"org-1"}""")
            .Should().Be(ClaudeGlobalConfigInfo.Unreadable, "寫到一半");

        // 好幾 MB 的 projects 能略過
        var history = string.Join(",", Enumerable.Range(0, 40_000).Select(i => $$$"""{"display":"第 {{{i}}} 筆","oauthAccount":{"organizationUuid":"org-{{{i}}}"}}"""));
        var big = $$$"""{"projects":{"C:/work":{"history":[{{{history}}}]}},"oauthAccount":{"emailAddress":"me@example.com","organizationUuid":"org-real"}}""";
        big.Length.Should().BeGreaterThan(2_000_000);
        AccountRules.ParseClaudeGlobalConfig(big).Should().Be(new ClaudeGlobalConfigInfo(true, false, true, "me@example.com", "org-real"));
    }

    [Fact]
    public void IsValidOrgId_同ReadClaudeOrgId的檢查()
    {
        AccountRules.IsValidOrgId("00000000-0000-0000-0000-000000000000").Should().BeTrue();
        AccountRules.IsValidOrgId(new string('a', 128)).Should().BeTrue();
        AccountRules.IsValidOrgId(new string('a', 129)).Should().BeFalse();
        AccountRules.IsValidOrgId("").Should().BeFalse();
        AccountRules.IsValidOrgId(null).Should().BeFalse();
        AccountRules.IsValidOrgId("org 1").Should().BeFalse();
        AccountRules.IsValidOrgId("org" + (char)9 + "1").Should().BeFalse();
        AccountRules.IsValidOrgId("org" + (char)0xD800).Should().BeFalse("單獨的代理字元");
    }

    [Fact]
    public void GetClaudeGlobalConfigPaths_同CLI_隔離帳戶在設定資料夾_舊的config檔在設定資料夾()
    {
        var dir = Path.Combine(Path.GetTempPath(), "myriagent-cfg");
        var (legacy, config) = AccountRules.GetClaudeGlobalConfigPaths(Isolated(AgentProvider.Claude, dir));
        legacy.Should().Be(Path.Combine(dir, ".config.json"));
        config.Should().Be(Path.Combine(dir, ".claude.json"), "CLAUDE_CONFIG_DIR 指向它：.claude.json 在設定資料夾裡");

        var (defaultLegacy, defaultConfig) = AccountRules.GetClaudeGlobalConfigPaths(SystemDefault(AgentProvider.Claude));
        defaultLegacy.Should().Be(Path.Combine(AccountRules.ResolveConfigDirectory(SystemDefault(AgentProvider.Claude)), ".config.json"));
        var inherited = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        defaultConfig.Should().Be(Path.Combine(string.IsNullOrWhiteSpace(inherited) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : inherited, ".claude.json"),
            "系統預設帳戶沒設 CLAUDE_CONFIG_DIR 時在使用者資料夾（不是 .claude 資料夾裡）");
    }

    [Fact]
    public void CanRunShortLivedClaude_憑證檔不在而權杖可能在認證管理員_一律不跑()
    {
        AccountRules.CanRunShortLivedClaude(null, Now, CredentialManagerConfig).Should().BeFalse("權杖在認證管理員：看不到期限");
        AccountRules.CanRunShortLivedClaude(null, Now, CredentialManagerConfig with { HasOAuthAccount = false }).Should().BeFalse("啟用了認證管理員：保守起見都不跑");
        AccountRules.CanRunShortLivedClaude(null, Now, ClaudeGlobalConfigInfo.Unreadable).Should().BeFalse("CLI 設定讀不到：無法判斷");
        AccountRules.CanRunShortLivedClaude(null, Now, ClaudeGlobalConfigInfo.Missing).Should().BeTrue("沒有啟用認證管理員、也沒有憑證檔：沒有權杖可換發");
        AccountRules.CanRunShortLivedClaude(Credentials(Now.AddHours(3)), Now, CredentialManagerConfig).Should().BeTrue("憑證檔在：照權杖期限判斷");
        AccountRules.CanRunShortLivedClaude(Credentials(Now.AddMinutes(5)), Now, ClaudeGlobalConfigInfo.Missing).Should().BeFalse();
    }

    [Fact]
    public void StrongerStatusReason_要跑AuthStatus的理由優先()
    {
        AccountRules.StrongerStatusReason(ClaudeStatusReason.Periodic, ClaudeStatusReason.CredentialsChanged).Should().Be(ClaudeStatusReason.CredentialsChanged);
        AccountRules.StrongerStatusReason(ClaudeStatusReason.CredentialsChanged, ClaudeStatusReason.Periodic).Should().Be(ClaudeStatusReason.CredentialsChanged);
        AccountRules.StrongerStatusReason(ClaudeStatusReason.Manual, ClaudeStatusReason.CredentialsChanged).Should().Be(ClaudeStatusReason.Manual);
        AccountRules.StrongerStatusReason(ClaudeStatusReason.AfterTurn, ClaudeStatusReason.Startup).Should().Be(ClaudeStatusReason.AfterTurn);
        AccountRules.StrongerStatusReason(ClaudeStatusReason.Startup, ClaudeStatusReason.Periodic).Should().Be(ClaudeStatusReason.Startup);
        AccountRules.StrongerStatusReason(ClaudeStatusReason.Periodic, ClaudeStatusReason.Periodic).Should().Be(ClaudeStatusReason.Periodic);
    }

    // ───── 本機預設帳戶的 Id ─────

    [Fact]
    public void SystemDefaultId_固定且不會和隔離帳戶撞名()
    {
        AccountRules.SystemDefaultId(AgentProvider.Claude).Should().Be("local-claude");
        AccountRules.SystemDefaultId(AgentProvider.Codex).Should().Be("local-codex");
        AccountRules.SystemDefaultId(AgentProvider.Claude).Should().NotMatchRegex("^[0-9a-fA-F]{32}$", "隔離帳戶的 Id 是 32 碼十六進位");
    }

    [Fact]
    public void GuessProviderFromUsageKeys_依時間窗的Key判斷代理()
    {
        AccountRules.GuessProviderFromUsageKeys(new[] { "session", "weekly", "weekly:Fable" }).Should().Be(AgentProvider.Claude);
        AccountRules.GuessProviderFromUsageKeys(new[] { "weekly:Opus" }).Should().Be(AgentProvider.Claude);
        AccountRules.GuessProviderFromUsageKeys(new[] { "codex:10080", "codex_sol:300" }).Should().Be(AgentProvider.Codex);
        AccountRules.GuessProviderFromUsageKeys(new[] { "codex:primary" }).Should().Be(AgentProvider.Codex);
        AccountRules.GuessProviderFromUsageKeys(new[] { "agy-gemini-5h", "agy-3p-weekly" }).Should().Be(AgentProvider.Antigravity);
        AccountRules.GuessProviderFromUsageKeys(new[] { "agy-gemini-5h", "session" }).Should().BeNull("兩種都像就不猜");
        AccountRules.GuessProviderFromUsageKeys(new[] { "session", "codex:10080" }).Should().BeNull("兩種都像就不猜");
        AccountRules.GuessProviderFromUsageKeys(new[] { "monthly" }).Should().BeNull();
        AccountRules.GuessProviderFromUsageKeys(Array.Empty<string>()).Should().BeNull();
    }

    // ───── 方案名稱 ─────

    [Theory]
    [InlineData("max", "default_claude_max_20x", "Max 20x")]
    [InlineData("max", "default_claude_max_5x", "Max 5x")]
    [InlineData("max", null, "Max")]
    [InlineData("pro", "default_claude_pro", "Pro")]
    [InlineData(" Team ", null, "Team")]
    [InlineData("enterprise", null, "Enterprise")]
    [InlineData("free", null, "Free")]
    [InlineData("ultra", null, "Ultra")]
    [InlineData(null, "default_claude_max_20x", null)]
    [InlineData("", null, null)]
    public void DescribeClaudePlan_方案加用量級距(string? subscriptionType, string? tier, string? expected)
        => AccountRules.DescribeClaudePlan(subscriptionType, tier).Should().Be(expected);

    [Fact]
    public void DescribeClaudePlan_Initialize回報的Claude前綴要去掉並保留大寫()
    {
        AccountRules.DescribeClaudePlan("Claude Max", null).Should().Be("Max");
        AccountRules.DescribeClaudePlan("Claude Pro", null).Should().Be("Pro");
        AccountRules.DescribeClaudePlan("Claude Max", "default_claude_max_20x").Should().Be("Max 20x");
    }

    [Fact]
    public void ReadClaudePlanFromCredentials_實測憑證形狀()
    {
        AccountRules.ReadClaudePlanFromCredentials(RealSamples.ClaudeCredentials).Should().Be("Max 20x");
        AccountRules.ReadClaudePlanFromCredentials("""{"claudeAiOauth":{"subscriptionType":"pro"}}""").Should().Be("Pro");
        AccountRules.ReadClaudePlanFromCredentials("""{"mcpOAuth":{}}""").Should().BeNull();
        AccountRules.ReadClaudePlanFromCredentials("{壞掉").Should().BeNull();
        AccountRules.ReadClaudePlanFromCredentials(" ").Should().BeNull();
        AccountRules.ReadClaudePlanFromCredentials(null).Should().BeNull();
    }

    // ───── Codex account/read ─────

    [Fact]
    public void EvaluateCodexAccount_實測ChatGPT登入()
    {
        var (status, email, plan) = AccountRules.EvaluateCodexAccount(JObject.Parse(RealSamples.CodexAccountRead));
        status.Should().Be(AccountStatus.Ready);
        email.Should().Be("user@example.com");
        plan.Should().Be("Pro");
    }

    [Fact]
    public void EvaluateCodexAccount_沒有帳戶是未登入_APIKey與其他方式走API計費()
    {
        AccountRules.EvaluateCodexAccount(JObject.Parse("""{"account":null,"requiresOpenaiAuth":true}""")).Status.Should().Be(AccountStatus.NotLoggedIn);
        AccountRules.EvaluateCodexAccount(null).Status.Should().Be(AccountStatus.NotLoggedIn);
        AccountRules.EvaluateCodexAccount(new JObject()).Status.Should().Be(AccountStatus.NotLoggedIn);
        AccountRules.EvaluateCodexAccount(JObject.Parse("""{"account":{"type":"apiKey"}}""")).Should().Be((AccountStatus.ApiKey, (string?)null, (string?)"API Key"));
        AccountRules.EvaluateCodexAccount(JObject.Parse("""{"account":{"type":"azure"}}""")).Should().Be((AccountStatus.ApiKey, (string?)null, (string?)"azure"));
    }

    [Theory]
    [InlineData("plus", "Plus")]
    [InlineData("pro", "Pro")]
    [InlineData("prolite", "Pro Lite")]
    [InlineData("promax", "Pro Max")]
    [InlineData("team", "Team")]
    [InlineData("business", "Business")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("edu", "Edu")]
    [InlineData("free", "Free")]
    [InlineData("go", "Go")]
    [InlineData("newplan", "newplan")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DescribeCodexPlan_方案中文顯示名稱(string? planType, string? expected)
        => AccountRules.DescribeCodexPlan(planType).Should().Be(expected);

    [Theory]
    [InlineData(nameof(AgentProvider.Claude), nameof(AccountStatus.NotLoggedIn), "尚未登入 Claude 帳戶")]
    [InlineData(nameof(AgentProvider.Codex), nameof(AccountStatus.NotLoggedIn), "尚未登入 ChatGPT 帳戶")]
    [InlineData(nameof(AgentProvider.Claude), nameof(AccountStatus.NotInstalled), "這台電腦尚未安裝 Claude Code CLI")]
    [InlineData(nameof(AgentProvider.Codex), nameof(AccountStatus.NotInstalled), "這台電腦尚未安裝 Codex CLI")]
    [InlineData(nameof(AgentProvider.Claude), nameof(AccountStatus.Ready), "已登入")]
    [InlineData(nameof(AgentProvider.Codex), nameof(AccountStatus.ApiKey), "以 API Key 登入，會走 API 計費而不是訂閱額度")]
    [InlineData(nameof(AgentProvider.Codex), nameof(AccountStatus.Checking), "檢查中…")]
    [InlineData(nameof(AgentProvider.Codex), nameof(AccountStatus.Error), "檢查失敗")]
    public void DescribeStatus_狀態說明(string provider, string status, string expected)
        => AccountRules.DescribeStatus(Enum.Parse<AgentProvider>(provider), Enum.Parse<AccountStatus>(status)).Should().Be(expected);

    // ───── 預設名稱與顏色 ─────

    [Fact]
    public void DefaultLabel_同類已有幾個就往後編_不分大小寫()
    {
        AccountRules.DefaultLabel(AgentProvider.Claude, Array.Empty<AgentAccount>()).Should().Be("Claude 帳戶");
        AccountRules.DefaultLabel(AgentProvider.Claude, new[] { new AgentAccount { Label = "claude 帳戶" } }).Should().Be("Claude 帳戶 2");
        AccountRules.DefaultLabel(AgentProvider.Claude, new[] { new AgentAccount { Label = "Claude 帳戶" }, new AgentAccount { Label = "Claude 帳戶 2" } }).Should().Be("Claude 帳戶 3");
        AccountRules.DefaultLabel(AgentProvider.Codex, new[] { new AgentAccount { Label = "Claude 帳戶" } }).Should().Be("Codex 帳戶");
        AccountRules.DefaultLabel(AgentProvider.Codex, new[] { new AgentAccount { Label = "Codex 帳戶 2" } }).Should().Be("Codex 帳戶", "空出來的名稱優先");
    }

    [Fact]
    public void DefaultColor_Claude暖色Codex冷色_避開已用的()
    {
        AccountRules.DefaultColor(AgentProvider.Claude, Array.Empty<AgentAccount>()).Should().Be("#D97757");
        AccountRules.DefaultColor(AgentProvider.Claude, new[] { new AgentAccount { Color = "#d97757" } }).Should().Be("#E5A04B", "比對不分大小寫");
        AccountRules.DefaultColor(AgentProvider.Codex, Array.Empty<AgentAccount>()).Should().Be("#19C3A6");
        AccountRules.DefaultColor(AgentProvider.Codex, new[] { new AgentAccount { Color = "#19C3A6" }, new AgentAccount { Color = "#4FA3F7" } }).Should().Be("#3FB950");

        var warmUsed = new[] { "#D97757", "#E5A04B", "#E06C75", "#C678DD" }.Select(c => new AgentAccount { Color = c }).ToList();
        AccountRules.DefaultColor(AgentProvider.Claude, warmUsed).Should().Be("#7C8CF8", "偏好色都用過就從調色盤找");
        var allUsed = AccountRules.Palette.Select(c => new AgentAccount { Color = c }).ToList();
        AccountRules.DefaultColor(AgentProvider.Codex, allUsed).Should().Be("#19C3A6", "全部用過就回第一個偏好色");
    }

    [Theory]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData(" #A1b2C3 ", "#A1B2C3")]
    [InlineData("#abc", null)]
    [InlineData("abcdef", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("red", null)]
    [InlineData("#abcdef00", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeColor_只接受RRGGBB(string? color, string? expected)
        => AccountRules.NormalizeColor(color).Should().Be(expected);

    // ───── 多電腦同步的掛鉤（WP3.3 第 10 步） ─────

    [Fact]
    public void ReadClaudeOrgId_有orgId回傳_沒有或含空白回null()
    {
        AccountRules.ReadClaudeOrgId(RealSamples.ClaudeAuthStatusLoggedIn).Should().Be("00000000-0000-0000-0000-000000000000");
        var newline = ((char)10).ToString();
        AccountRules.ReadClaudeOrgId("Warning {x}" + newline + RealSamples.ClaudeAuthStatusLoggedIn + newline).Should().Be("00000000-0000-0000-0000-000000000000",
            "和 EvaluateClaudeAuthStatus 一樣找含 loggedIn 的 JSON 物件");
        AccountRules.ReadClaudeOrgId(RealSamples.ClaudeAuthStatusEmptyConfig).Should().BeNull("未登入沒有 orgId");
        AccountRules.ReadClaudeOrgId("""{"orgId":"org-1"}""").Should().BeNull("不是 auth status 的物件（沒有 loggedIn）");
        AccountRules.ReadClaudeOrgId("""{"loggedIn":true,"orgId":"org 1"}""").Should().BeNull("含空白");
        AccountRules.ReadClaudeOrgId("""{"loggedIn":true,"orgId":"org\t1"}""").Should().BeNull("含控制字元");
        AccountRules.ReadClaudeOrgId("""{"loggedIn":true,"orgId":""}""").Should().BeNull();
        AccountRules.ReadClaudeOrgId("""{"loggedIn":true,"orgId":123}""").Should().BeNull("不是字串");
        AccountRules.ReadClaudeOrgId($$"""{"loggedIn":true,"orgId":"{{new string('a', 129)}}"}""").Should().BeNull("超過 128 字");
        AccountRules.ReadClaudeOrgId($$"""{"loggedIn":true,"orgId":"{{new string('a', 128)}}"}""").Should().Be(new string('a', 128));
        AccountRules.ReadClaudeOrgId(null).Should().BeNull();
    }

    [Fact]
    public void BuildIdentityCache_帶orgId()
    {
        var file = FileWith(Credentials(Now.AddHours(3)));

        AccountRules.BuildIdentityCache(AccountStatus.Ready, "user@example.com", "Max", file, DateTime.Now, "org-1")!.OrgId.Should().Be("org-1");
        CacheFor(file).OrgId.Should().BeNull("沒給就是 null（舊的呼叫端不變）");
        AccountRules.BuildIdentityCache(AccountStatus.NotLoggedIn, null, null, file, DateTime.Now, "org-1").Should().BeNull();
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData(" #A1b ", "#AA11BB")]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData("#abcd", null)]
    [InlineData("abc", null)]
    [InlineData("#ggg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeCatalogColor_三碼展開_大寫(string? color, string? expected)
        => AccountRules.NormalizeCatalogColor(color).Should().Be(expected);

    [Fact]
    public void CleanCatalogLabel_60字以文字元素截斷()
    {
        AccountRules.CleanCatalogLabel(new string('名', 61)).Should().Be(new string('名', 60));
        AccountRules.CleanCatalogLabel(new string('名', 60)).Should().Be(new string('名', 60));
        var emoji = char.ConvertFromUtf32(0x1F600);
        var label = new string('a', 59) + emoji;
        AccountRules.CleanCatalogLabel(label).Should().Be(new string('a', 59), "不切斷代理字元對");
        AccountRules.CleanCatalogLabel("  名稱" + (char)0x202E + "  ").Should().Be("名稱", "去掉格式字元與頭尾空白");
        AccountRules.CleanCatalogLabel(" " + (char)1 + " ").Should().BeNull("清理後是空的");
        AccountRules.CleanCatalogLabel(null).Should().BeNull();
    }

    [Fact]
    public void CodexProbePlan_不查用量時只送account_read()
    {
        AccountRules.CodexProbePlan(pollUsage: false, needModels: false).Should().Be((true, false, false));
        AccountRules.CodexProbePlan(pollUsage: true, needModels: false).Should().Be((true, true, false));
        AccountRules.CodexProbePlan(pollUsage: false, needModels: true).Should().Be((true, false, true), "模型清單不受用量閘門影響");
    }

    [Fact]
    public void AntigravityProbePlan_模型已知且查用量時只跑usage_不查用量時只跑models()
    {
        AccountRules.AntigravityProbePlan(pollUsage: true, needModels: true).Should().Be((true, true));
        AccountRules.AntigravityProbePlan(pollUsage: true, needModels: false).Should().Be((false, true), "日常輪詢只起一次 agy -p /usage");
        AccountRules.AntigravityProbePlan(pollUsage: false, needModels: true).Should().Be((true, false));
        AccountRules.AntigravityProbePlan(pollUsage: false, needModels: false).Should().Be((true, false), "同步由別台查用量時仍以 agy models 檢查本機登入狀態");
    }

    [Fact]
    public void ShouldRecheckStatusOnTick_未登入_未安裝_檢查失敗都重查狀態()
    {
        AccountRules.ShouldRecheckStatusOnTick(AccountStatus.NotLoggedIn).Should().BeTrue();
        AccountRules.ShouldRecheckStatusOnTick(AccountStatus.NotInstalled).Should().BeTrue();
        AccountRules.ShouldRecheckStatusOnTick(AccountStatus.Error).Should().BeTrue("上次檢查失敗（逾時或暫時錯誤）在輪詢到期時一併重查狀態");
        AccountRules.ShouldRecheckStatusOnTick(AccountStatus.Ready).Should().BeFalse();
        AccountRules.ShouldRecheckStatusOnTick(AccountStatus.ApiKey).Should().BeFalse();
        AccountRules.ShouldRecheckStatusOnTick(AccountStatus.Checking).Should().BeFalse();
    }

    [Fact]
    public void NextPollDelay_檢查失敗時以1分鐘起指數退避並以原本間隔為上限_正常狀態照原本間隔()
    {
        var idle = TimeSpan.FromMinutes(15);
        AccountRules.NextPollDelay(AccountStatus.Ready, 3, idle).Should().Be(idle);
        AccountRules.NextPollDelay(AccountStatus.NotLoggedIn, 1, idle).Should().Be(idle);

        AccountRules.NextPollDelay(AccountStatus.Error, 1, idle).Should().Be(TimeSpan.FromMinutes(1));
        AccountRules.NextPollDelay(AccountStatus.Error, 2, idle).Should().Be(TimeSpan.FromMinutes(2));
        AccountRules.NextPollDelay(AccountStatus.Error, 3, idle).Should().Be(TimeSpan.FromMinutes(4));
        AccountRules.NextPollDelay(AccountStatus.Error, 4, idle).Should().Be(TimeSpan.FromMinutes(8));
        AccountRules.NextPollDelay(AccountStatus.Error, 5, idle).Should().Be(idle, "不超過原本的輪詢間隔");
        AccountRules.NextPollDelay(AccountStatus.Error, 100, idle).Should().Be(idle, "大量連續失敗也不溢位");
        AccountRules.NextPollDelay(AccountStatus.Error, 1, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
    }
}
