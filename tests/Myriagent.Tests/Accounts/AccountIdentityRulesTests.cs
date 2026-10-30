using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Accounts;

/// <summary>帳戶目錄（WP3.4）的身分規則：登入到期、檔案的 orgId、orgId 的來源優先序、登入帶的 --email、「在這台登入」的檢查</summary>
public class AccountIdentityRulesTests
{
    private const string Org = "00000000-0000-0000-0000-000000000000";

    private const string ValidLogical = "la_0123456789ABCDEFGHJKMNPQRS";

    private static string Credentials(string refreshTokenExpiresAt)
        => $$$"""{"claudeAiOauth":{"accessToken":"sk-ant-oat01-x","expiresAt":1791600000000,"refreshTokenExpiresAt":{{{refreshTokenExpiresAt}}}}}""";

    [Fact]
    public void ReadClaudeLoginExpiresAt_讀refreshTokenExpiresAt_缺少型別錯誤或超出範圍為null()
    {
        AccountIdentityRules.ReadClaudeLoginExpiresAt(RealSamples.ClaudeCredentials).Should().Be(1794000000000);
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials(AccountIdentityRules.MaxUnixMs.ToString())).Should().Be(AccountIdentityRules.MaxUnixMs, "上限本身可以");
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials("1")).Should().Be(1);

        AccountIdentityRules.ReadClaudeLoginExpiresAt("""{"claudeAiOauth":{"accessToken":"x","expiresAt":1791600000000}}""").Should().BeNull("缺欄位");
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials("\"1794000000000\"")).Should().BeNull("字串");
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials("1794000000000.5")).Should().BeNull("不是整數");
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials("0")).Should().BeNull("0");
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials("-5")).Should().BeNull("負數");
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials((AccountIdentityRules.MaxUnixMs + 1).ToString())).Should().BeNull("超過 9999 年");
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials("123456789012345678901234567890")).Should().BeNull("超過 long");
        AccountIdentityRules.ReadClaudeLoginExpiresAt(Credentials("null")).Should().BeNull();
        AccountIdentityRules.ReadClaudeLoginExpiresAt("""{"refreshTokenExpiresAt":1794000000000}""").Should().BeNull("只看 claudeAiOauth 裡的");
        AccountIdentityRules.ReadClaudeLoginExpiresAt("""{"claudeAiOauth":{"refreshTokenExpiresAt":1794000000000""").Should().BeNull("寫到一半");
        AccountIdentityRules.ReadClaudeLoginExpiresAt("""{"claudeAiOauth":null}""").Should().BeNull();
        AccountIdentityRules.ReadClaudeLoginExpiresAt("[1]").Should().BeNull();
        AccountIdentityRules.ReadClaudeLoginExpiresAt("  ").Should().BeNull();
        AccountIdentityRules.ReadClaudeLoginExpiresAt(null).Should().BeNull();
    }

    [Fact]
    public void ShownLoginExpiresAt_只有已登入才給_其他狀態與超出範圍為null()
    {
        AccountIdentityRules.ShownLoginExpiresAt(AccountStatus.Ready, 1794000000000).Should().Be(1794000000000);
        AccountIdentityRules.ShownLoginExpiresAt(AccountStatus.Ready, AccountIdentityRules.MaxUnixMs).Should().Be(AccountIdentityRules.MaxUnixMs);
        AccountIdentityRules.ShownLoginExpiresAt(AccountStatus.Ready, null).Should().BeNull("Codex、Grok、Antigravity 沒有到期資料");

        // 登入失效時 CLI 清掉權杖、憑證檔的 refreshTokenExpiresAt 還在：照送的話網頁會對失效的登入顯示「N 天後到期」
        foreach (var status in new[] { AccountStatus.Checking, AccountStatus.NotLoggedIn, AccountStatus.ApiKey, AccountStatus.NotInstalled, AccountStatus.Error })
            AccountIdentityRules.ShownLoginExpiresAt(status, 1794000000000).Should().BeNull(status.ToString());

        AccountIdentityRules.ShownLoginExpiresAt(AccountStatus.Ready, 0).Should().BeNull();
        AccountIdentityRules.ShownLoginExpiresAt(AccountStatus.Ready, -1).Should().BeNull();
        AccountIdentityRules.ShownLoginExpiresAt(AccountStatus.Ready, AccountIdentityRules.MaxUnixMs + 1).Should().BeNull();
    }

    [Fact]
    public void ReadClaudeCredentialOrgId_只讀最上層organizationUuid()
    {
        AccountIdentityRules.ReadClaudeCredentialOrgId(RealSamples.ClaudeCredentials).Should().Be(Org);
        AccountIdentityRules.ReadClaudeCredentialOrgId("""{"claudeAiOauth":{"organizationUuid":"org-inner"}}""").Should().BeNull("claudeAiOauth 裡同名的欄位不算");
        AccountIdentityRules.ReadClaudeCredentialOrgId("""{"organizationUuid":"org 1"}""").Should().BeNull("含空白");
        AccountIdentityRules.ReadClaudeCredentialOrgId($$"""{"organizationUuid":"{{new string('a', 129)}}"}""").Should().BeNull("超過 128 字");
        AccountIdentityRules.ReadClaudeCredentialOrgId($$"""{"organizationUuid":"{{new string('a', 128)}}"}""").Should().Be(new string('a', 128));
        AccountIdentityRules.ReadClaudeCredentialOrgId("""{"organizationUuid":""}""").Should().BeNull();
        AccountIdentityRules.ReadClaudeCredentialOrgId("""{"organizationUuid":123}""").Should().BeNull("不是字串");
        AccountIdentityRules.ReadClaudeCredentialOrgId("""{"organizationUuid":"org-1" """).Should().BeNull("寫到一半");
        AccountIdentityRules.ReadClaudeCredentialOrgId(null).Should().BeNull();
    }

    [Fact]
    public void ResolveClaudeOrgId_優先序與閘門()
    {
        // 1. auth status 優先
        AccountIdentityRules.ResolveClaudeOrgId("org-auth", "org-cache", "a@b.c", "a@b.c", "org-cred", "org-cfg", true, true).Should().Be("org-auth");
        // 2. auth status 不合格（含空白）就往下找：快取的 email 相同（大小寫不同算相同）
        AccountIdentityRules.ResolveClaudeOrgId("org auth", "org-cache", "A@B.c", " a@b.C ", "org-cred", "org-cfg", true, true).Should().Be("org-cache");
        // 3. 快取的 email 不同：不用快取，閘門打開時用憑證檔
        AccountIdentityRules.ResolveClaudeOrgId(null, "org-cache", "a@b.c", "x@b.c", "org-cred", "org-cfg", true, true).Should().Be("org-cred");
        // 4. 憑證檔閘門關著：用 CLI 設定（閘門打開）
        AccountIdentityRules.ResolveClaudeOrgId(null, "org-cache", "a@b.c", "x@b.c", "org-cred", "org-cfg", false, true).Should().Be("org-cfg");
        // 5. 兩個閘門都關著：檔案來源都不用
        AccountIdentityRules.ResolveClaudeOrgId(null, "org-cache", "a@b.c", "x@b.c", "org-cred", "org-cfg", false, false).Should().BeNull();
        AccountIdentityRules.ResolveClaudeOrgId(null, null, null, "a@b.c", "org-cred", "org-cfg", false, false).Should().BeNull();
        // 6. email 不知道：無法確認是同一個登入，不沿用快取
        AccountIdentityRules.ResolveClaudeOrgId(null, "org-cache", "a@b.c", null, null, null, true, true).Should().BeNull();
        AccountIdentityRules.ResolveClaudeOrgId(null, "org-cache", null, "a@b.c", null, null, true, true).Should().BeNull();
        AccountIdentityRules.ResolveClaudeOrgId(null, "org-cache", null, null, null, null, true, true).Should().BeNull();
        // 7. 快取本身不合格：往下找
        AccountIdentityRules.ResolveClaudeOrgId(null, "", "a@b.c", "a@b.c", "org-cred", null, true, true).Should().Be("org-cred");
        // 8. 憑證檔不合格、CLI 設定合格
        AccountIdentityRules.ResolveClaudeOrgId(null, null, null, null, "org\tcred", "org-cfg", true, true).Should().Be("org-cfg");
        // 9. 憑證檔優先於 CLI 設定
        AccountIdentityRules.ResolveClaudeOrgId(null, null, null, null, "org-cred", "org-cfg", true, true).Should().Be("org-cred");
        // 10. 都沒有
        AccountIdentityRules.ResolveClaudeOrgId(null, null, null, null, null, null, true, true).Should().BeNull();
    }

    [Fact]
    public void ClaudeOrgIdHint_org不明_CLI設定合格而且email相同才給()
    {
        AccountIdentityRules.ClaudeOrgIdHint(null, "org-P", "A@x.test", " a@X.test ").Should().Be("org-P", "email 相同（不分大小寫）");
        AccountIdentityRules.ClaudeOrgIdHint("org-T", "org-P", "a@x.test", "a@x.test").Should().BeNull("orgId 已知：不需要提示");
        AccountIdentityRules.ClaudeOrgIdHint(null, "org P", "a@x.test", "a@x.test").Should().BeNull("不合格的 organizationUuid");
        AccountIdentityRules.ClaudeOrgIdHint(null, null, "a@x.test", "a@x.test").Should().BeNull();
        AccountIdentityRules.ClaudeOrgIdHint(null, "org-P", "b@x.test", "a@x.test").Should().BeNull("CLI 設定是別的帳號");
        AccountIdentityRules.ClaudeOrgIdHint(null, "org-P", null, "a@x.test").Should().BeNull("CLI 設定的 email 不知道");
        AccountIdentityRules.ClaudeOrgIdHint(null, "org-P", "a@x.test", null).Should().BeNull("帳戶的 email 不知道");
    }

    [Fact]
    public void ResolveClaudeOrgId_閘門預設關閉()
    {
        AccountIdentityRules.CredentialsOrgConfirmed.Should().BeFalse("E-13 證明相等之前不用憑證檔的 organizationUuid（D4）");
        AccountIdentityRules.GlobalConfigOrgConfirmed.Should().BeFalse("E-13 證明相等之前不用 CLI 設定的 organizationUuid（D4）");
    }

    [Theory]
    [InlineData("a@b.c")]
    [InlineData("first.last+tag@ex-ample.com")]
    [InlineData("A_B@EXAMPLE.ORG")]
    public void LoginEmailArgument_只收單純信箱_可以(string email)
        => AccountIdentityRules.LoginEmailArgument(email).Should().Be(email);

    [Theory]
    [InlineData("a b@c")]
    [InlineData("\"x\"@y")]
    [InlineData("a@b&calc")]
    [InlineData("a@b^c")]
    [InlineData("%x%@y")]
    [InlineData("!x!@y")]
    [InlineData("a,b@c")]
    [InlineData("a;b@c")]
    [InlineData("a=b@c")]
    [InlineData("(a)@b")]
    [InlineData("a|b@c")]
    [InlineData("a<b@c")]
    [InlineData("a>b@c")]
    [InlineData("-x@y")]
    [InlineData("a@b_c")]
    [InlineData("a@b+c")]
    [InlineData("a@@b")]
    [InlineData("a@b@c")]
    [InlineData("@b.c")]
    [InlineData("a@")]
    [InlineData("ab.c")]
    [InlineData("é@b.c")]
    [InlineData("a@例子.tw")]
    [InlineData("")]
    [InlineData(null)]
    public void LoginEmailArgument_只收單純信箱_不合格為null(string? email)
        => AccountIdentityRules.LoginEmailArgument(email).Should().BeNull();

    [Fact]
    public void LoginEmailArgument_換行與長度()
    {
        var lf = ((char)10).ToString();
        var cr = ((char)13).ToString();
        AccountIdentityRules.LoginEmailArgument("a@b.c" + lf).Should().BeNull("結尾的換行");
        AccountIdentityRules.LoginEmailArgument("a" + lf + "@b.c").Should().BeNull("中間的換行");
        AccountIdentityRules.LoginEmailArgument("a@b.c" + cr + lf + "calc").Should().BeNull();
        AccountIdentityRules.LoginEmailArgument(" a@b.c").Should().BeNull("前後空白不修剪，直接不帶");

        var max = new string('a', AccountIdentityRules.MaxLoginEmailLength - 5) + "@b.cd";
        max.Length.Should().Be(254);
        AccountIdentityRules.LoginEmailArgument(max).Should().Be(max);
        AccountIdentityRules.LoginEmailArgument("a" + max).Should().BeNull("255 字");
    }

    [Fact]
    public void CheckCatalogClaim_各種結果()
    {
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, AgentProvider.Claude, false, false).Should().Be(CatalogClaimCheck.Ok);
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, AgentProvider.Codex, false, false).Should().Be(CatalogClaimCheck.Ok);
        AccountIdentityRules.CheckCatalogClaim("la_PROBE0000000000000000000000", true, AgentProvider.Claude, false, false).Should().Be(CatalogClaimCheck.InvalidId,
            "本體 27 字而且含 O");
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, false, AgentProvider.Claude, false, false).Should().Be(CatalogClaimCheck.NotPaired);
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, null, false, false).Should().Be(CatalogClaimCheck.NotInCatalog);
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, AgentProvider.Antigravity, false, false).Should().Be(CatalogClaimCheck.UnsupportedProvider);
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, AgentProvider.Grok, false, false).Should().Be(CatalogClaimCheck.Ok, "Grok 比照 Codex（2026-10-22 起進目錄）");
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, AgentProvider.Claude, true, false).Should().Be(CatalogClaimCheck.AlreadyLinkedHere);
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, AgentProvider.Claude, false, true).Should().Be(CatalogClaimCheck.AlreadyClaimedHere);

        var unknown = () => AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, (AgentProvider)999, false, false);
        unknown.Should().Throw<ArgumentOutOfRangeException>("新種類要明確分類，不能靜默落進其中一邊");
    }

    [Fact]
    public void CheckCatalogClaim_檢查的先後順序()
    {
        AccountIdentityRules.CheckCatalogClaim("la_x", false, null, true, true).Should().Be(CatalogClaimCheck.InvalidId, "格式最先檢查");
        AccountIdentityRules.CheckCatalogClaim(null, false, null, true, true).Should().Be(CatalogClaimCheck.InvalidId);
        AccountIdentityRules.CheckCatalogClaim("d_0123456789ABCDEFGHJKMNPQRS", true, AgentProvider.Claude, false, false).Should().Be(CatalogClaimCheck.InvalidId, "裝置 id 不是邏輯帳戶");
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, false, null, true, true).Should().Be(CatalogClaimCheck.NotPaired, "其次是配對");
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, null, true, true).Should().Be(CatalogClaimCheck.NotInCatalog);
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, AgentProvider.Antigravity, true, true).Should().Be(CatalogClaimCheck.UnsupportedProvider, "種類在連結之前");
        AccountIdentityRules.CheckCatalogClaim(ValidLogical, true, AgentProvider.Codex, true, true).Should().Be(CatalogClaimCheck.AlreadyLinkedHere, "連結在宣告之前");
    }

    [Fact]
    public void DescribeClaimFailure_每個失敗都有文字()
    {
        var texts = Enum.GetValues<CatalogClaimCheck>().Where(c => c != CatalogClaimCheck.Ok)
            .Select(AccountIdentityRules.DescribeClaimFailure).ToList();
        texts.Should().HaveCount(Enum.GetValues<CatalogClaimCheck>().Length - 1);
        texts.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t));
        texts.Should().OnlyHaveUniqueItems();

        var ok = () => AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.Ok);
        ok.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AgentAccount_LogicalAccountId_舊版accounts_json讀成null_存檔往返保留()
    {
        using var temp = new TempDirectory("identity-rules");
        var legacy = temp.WriteFile("legacy.json", """[{"Id":"abc","Provider":"Claude","Label":"舊版","ConfigDirectory":"C:/x"}]""");
        var (status, read, _) = JsonFile.TryRead<List<AgentAccount>>(legacy);
        status.Should().Be(JsonReadStatus.Ok);
        read!.Single().LogicalAccountId.Should().BeNull("舊版沒有這個欄位");

        var path = temp.Combine("accounts.json");
        JsonFile.Write(path, new List<AgentAccount>
        {
            new() { Id = "a1", Provider = AgentProvider.Codex, Label = "宣告", LogicalAccountId = ValidLogical },
            new() { Id = "a2", Provider = AgentProvider.Claude, Label = "一般" },
        });
        File.ReadAllText(path).Should().Contain("\"LogicalAccountId\": \"" + ValidLogical + "\"");
        var (again, back, _) = JsonFile.TryRead<List<AgentAccount>>(path);
        again.Should().Be(JsonReadStatus.Ok);
        back!.Select(a => a.LogicalAccountId).Should().Equal(ValidLogical, null);
    }
}
