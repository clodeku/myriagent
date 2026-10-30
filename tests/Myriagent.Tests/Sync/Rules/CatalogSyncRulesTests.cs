using System.Globalization;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Rules;

public sealed class SyncProviderRulesTests
{
    [Fact]
    public void 每個AgentProviders_All都有明確分類()
    {
        // 新增代理種類時這裡會變紅：先決定它進不進帳戶目錄、要不要等伺服器宣告新功能（設計 2026-10-22-grok-antigravity-sync）
        var expected = new Dictionary<AgentProvider, ProviderSyncKind>
        {
            [AgentProvider.Claude] = ProviderSyncKind.Catalog,
            [AgentProvider.Codex] = ProviderSyncKind.Catalog,
            [AgentProvider.Antigravity] = ProviderSyncKind.NeedsProv1,
            [AgentProvider.Grok] = ProviderSyncKind.NeedsProv1,
        };

        AgentProviders.All.Should().BeEquivalentTo(expected.Keys, "每一種代理都要有明確的同步分類");
        foreach (var provider in AgentProviders.All)
        {
            SyncProviderRules.Classify(provider).Should().Be(expected[provider], provider.ToString());
            var name = SyncProviderRules.WireName(provider);
            name.Should().Be(provider.ToString(), "線上名稱同列舉名稱（伺服器的 CatalogRules 與 ConversationEventRules 認這些字）");
            SyncProviderRules.FromWireName(name).Should().Be(provider);
            SyncProviderRules.TryWireName(provider, out var tried).Should().BeTrue();
            tried.Should().Be(name);
        }

        SyncProviderRules.FromWireName("Gemini").Should().BeNull("新版伺服器多出的種類");
        SyncProviderRules.FromWireName("claude").Should().BeNull("大小寫要相同");
    }

    [Fact]
    public void 未知值丟例外()
    {
        FluentActions.Invoking(() => SyncProviderRules.Classify((AgentProvider)999)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => SyncProviderRules.WireName((AgentProvider)999)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => SyncProviderRules.Publishable((AgentProvider)999, true)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => SyncProviderRules.TryWireName((AgentProvider)999, out _)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Grok與Antigravity只在有prov1時送()
    {
        foreach (var provider in new[] { AgentProvider.Claude, AgentProvider.Codex })
        {
            SyncProviderRules.Publishable(provider, prov1: true).Should().BeTrue(provider.ToString());
            SyncProviderRules.Publishable(provider, prov1: false).Should().BeTrue($"{provider} 任何伺服器都收");
        }

        foreach (var provider in new[] { AgentProvider.Grok, AgentProvider.Antigravity })
        {
            SyncProviderRules.Publishable(provider, prov1: true).Should().BeTrue(provider.ToString());
            SyncProviderRules.Publishable(provider, prov1: false).Should().BeFalse($"{provider}：舊伺服器的目錄、對話事件與 cli.state 只收 Claude、Codex");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("full")]
    [InlineData("metadata")]
    [InlineData("paused")]
    [InlineData("localOnly")]
    public void 對話的政策照存檔解讀_四種都一樣_沒有prov1由引擎延後而不是當成localOnly(string? stored)
    {
        foreach (var provider in AgentProviders.All)
            SyncProviderRules.ConversationPolicy(new AgentConversation { Provider = provider, SyncPolicy = stored }).Should().Be(stored ?? "full", provider.ToString());
    }
}

public sealed class CatalogSyncRulesTests
{
    private const string Org = "11111111-2222-3333-4444-555555555555";

    internal static LocalAccountSnapshot Account(
        AgentProvider provider = AgentProvider.Claude, string id = "acc-claude", string status = "ready", string? email = "Me@Example.com",
        string? orgId = Org, string label = "Claude 主帳戶", string color = "#D97757", int sortOrder = 0, bool isSystemDefault = false,
        string? statusText = "已登入", string? plan = "Claude Max", long checkedAt = 1791580000000) =>
        new(id, provider, isSystemDefault, label, color, sortOrder, status, statusText, email, orgId, plan, 1791600000000, false,
            new LinkSessions(1, 0, 0), checkedAt);

    [Fact]
    public void Codex_identityKey小寫email()
    {
        CatalogSyncRules.CodexIdentityKey(" Me@Example.COM ").Should().Be("codex:me@example.com");
        CatalogSyncRules.CodexIdentityKey(null).Should().BeNull();
        CatalogSyncRules.CodexIdentityKey("  ").Should().BeNull();
        CatalogSyncRules.CodexIdentityKey("a b@example.com").Should().BeNull("身分鍵不得含空白");
    }

    [Fact]
    public void Claude_org已知_組成claude冒號org冒號小寫email()
    {
        CatalogSyncRules.ClaudeIdentityKey("Me@Example.com", Org, null).Should().Be($"claude:{Org}:me@example.com");
        CatalogSyncRules.ClaudeIdentityKey("Me@Example.com", Org, "claude:me@example.com").Should().Be($"claude:{Org}:me@example.com");
    }

    [Fact]
    public void Claude_org未知_沒發布過_null()
    {
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, null).Should().BeNull("先送 claude:{email} 再換成含 org 的鍵會被標成身分不符");
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", " ", null).Should().BeNull();
    }

    [Fact]
    public void Claude_org未知_已發布_email相同_沿用()
    {
        var published = $"claude:{Org}:me@example.com";
        CatalogSyncRules.ClaudeIdentityKey("ME@example.com", null, published).Should().Be(published);
    }

    [Fact]
    public void Claude_org未知_已發布_email不同_送claude冒號email()
    {
        CatalogSyncRules.ClaudeIdentityKey("other@example.com", null, $"claude:{Org}:me@example.com").Should().Be("claude:other@example.com");
    }

    [Fact]
    public void Claude_org未知_email未知_沿用()
    {
        var published = $"claude:{Org}:me@example.com";
        CatalogSyncRules.ClaudeIdentityKey(null, null, published).Should().Be(published);
        CatalogSyncRules.ClaudeIdentityKey(null, Org, published).Should().Be(published);
        CatalogSyncRules.ClaudeIdentityKey(null, null, null).Should().BeNull();
    }

    [Fact]
    public void BuildLinkUpsert_子代理數_0不寫進線上_摘要和以前相同_有就寫()
    {
        var plain = Account();
        var link = CatalogSyncRules.BuildLinkUpsert(plain, null, null)!;
        link.Sessions.Subagents.Should().BeNull("0 不寫");
        SyncJson.Serialize(link).Should().NotContain("subagents", "沒有子代理的連結和以前逐位元組相同（摘要不變、不必重送）");
        var zero = CatalogSyncRules.BuildLinkUpsert(plain with { Sessions = new LinkSessions(1, 0, 0, 0, null, 0) }, null, null)!;
        CatalogSyncRules.Digest(zero, linked: true).Should().Be(CatalogSyncRules.Digest(link, linked: true));

        var busy = CatalogSyncRules.BuildLinkUpsert(plain with { Sessions = new LinkSessions(1, 1, 0, 0, null, 3) }, null, null)!;
        busy.Sessions.Subagents.Should().Be(3);
        SyncJson.Serialize(busy).Should().Contain("\"subagents\":3");
        CatalogSyncRules.Digest(busy, linked: true).Should().NotBe(CatalogSyncRules.Digest(link, linked: true), "子代理數變了要重送");
    }

    [Fact]
    public void BuildLinkUpsert_背景程式數_0不寫進線上_摘要和以前相同()
    {
        var plain = Account();
        var link = CatalogSyncRules.BuildLinkUpsert(plain, null, null)!;
        link.Sessions.Programs.Should().BeNull("0 不寫");
        SyncJson.Serialize(link).Should().NotContain("programs", "沒有背景程式的連結和以前逐位元組相同（摘要不變、不必重送）");
        var zero = CatalogSyncRules.BuildLinkUpsert(plain with { Sessions = new LinkSessions(1, 0, 0, 0, 0) }, null, null)!;
        CatalogSyncRules.Digest(zero, linked: true).Should().Be(CatalogSyncRules.Digest(link, linked: true));

        var busy = CatalogSyncRules.BuildLinkUpsert(plain with { Sessions = new LinkSessions(1, 0, 0, 0, 2) }, null, null)!;
        busy.Sessions.Programs.Should().Be(2);
        SyncJson.Serialize(busy).Should().Contain("\"programs\":2");
        CatalogSyncRules.Digest(busy, linked: true).Should().NotBe(CatalogSyncRules.Digest(link, linked: true), "背景程式數變了要重送");
        CatalogSyncRules.BuildLinkUpsert(plain with { Sessions = new LinkSessions(1, 0, 0, 0, 1_000_000) }, null, null)!.Sessions.Programs
            .Should().BeLessThanOrEqualTo(1_000_000).And.BeGreaterThan(0, "照其他對話數的上限夾住");
    }

    [Fact]
    public void BuildLinkUpsert_checking不發布()
    {
        CatalogSyncRules.BuildLinkUpsert(Account(status: "checking"), null, null).Should().BeNull();
    }

    [Fact]
    public void BuildLinkUpsert_Antigravity以agy記錄裡的信箱當身分鍵_伺服器收()
    {
        var link = CatalogSyncRules.BuildLinkUpsert(
            Account(AgentProvider.Antigravity, "local-antigravity", email: "Me@Example.com", orgId: null, isSystemDefault: true), null, null)!;
        link.Provider.Should().Be("Antigravity");
        link.IdentityKey.Should().Be("antigravity:me@example.com");
        link.ConfigKind.Should().Be(CatalogSyncRules.ConfigKindSystemDefault);
        link.LoginExpiresAt.Should().BeNull();
        ServerRulesMirror.ValidateLink(link).Should().BeNull();
        ServerRulesMirror.ValidateLink(link, prov1: false).Should().NotBeNull("舊伺服器不收：所以只在協商出 prov1 時送");
        CatalogSyncRules.BuildLinkUpsert(Account(AgentProvider.Antigravity, "local-antigravity", email: null), null, null)
            .Should().BeNull("記錄裡沒有登入成功的那一行：身分未知、不猜");
    }

    [Fact]
    public void BuildLinkUpsert_Grok以信箱當身分鍵_伺服器收()
    {
        var link = CatalogSyncRules.BuildLinkUpsert(Account(AgentProvider.Grok, "local-grok", email: " Me@X.ai ", orgId: null), "la_0123456789ABCDEFGHJKMNPQRS", null)!;
        link.Provider.Should().Be("Grok");
        link.IdentityKey.Should().Be("grok:me@x.ai");
        link.LoginExpiresAt.Should().BeNull();
        ServerRulesMirror.ValidateLink(link).Should().BeNull();
        CatalogSyncRules.BuildLinkUpsert(Account(AgentProvider.Grok, "local-grok", email: null), null, null)
            .Should().BeNull("X 帳號登入沒有信箱、也沒有連結");
    }

    [Fact]
    public void BuildLinkUpsert_沒有身分也沒有連結_回null()
    {
        CatalogSyncRules.BuildLinkUpsert(Account(orgId: null), null, null).Should().BeNull("Claude 不知道 org、也沒發布過");
        CatalogSyncRules.BuildLinkUpsert(Account(AgentProvider.Codex, "acc-codex", email: null), null, null).Should().BeNull();
        CatalogSyncRules.BuildLinkUpsert(Account(AgentProvider.Codex, "acc-codex", email: null), SyncIds.NewLogicalAccountId(), null)
            .Should().NotBeNull("已連結的照樣回報狀態");
    }

    [Fact]
    public void BuildLinkUpsert_欄位()
    {
        var p = CatalogSyncRules.BuildLinkUpsert(Account(isSystemDefault: true, color: "#abc"), null, null)!;

        p.LocalAccountId.Should().Be("acc-claude");
        p.Provider.Should().Be("Claude");
        p.ConfigKind.Should().Be("systemDefault");
        p.IdentityKey.Should().Be($"claude:{Org}:me@example.com");
        p.LogicalAccountId.Should().BeNull();
        p.Status.Should().Be("ready");
        p.Color.Should().Be("#AABBCC");
        p.LoginExpiresAt.Should().BeNull("快照沒有登入到期");
        CatalogSyncRules.BuildLinkUpsert(Account(), null, null)!.ConfigKind.Should().Be("isolated");
        CatalogSyncRules.BuildLinkUpsert(Account(), "not-a-logical-id", null)!.LogicalAccountId.Should().BeNull("格式不對的連結不送");
        CatalogSyncRules.BuildLinkUpsert(Account(id: "有空白 的id"), null, null).Should().BeNull("伺服器不收的本機帳戶 id");
    }

    [Fact]
    public void BuildLinkUpsert_LoginExpiresAt_Claude照帶_Codex一律null_超出範圍為null()
    {
        CatalogSyncRules.BuildLinkUpsert(Account() with { LoginExpiresAt = 1794000000000 }, null, null)!.LoginExpiresAt.Should().Be(1794000000000);
        CatalogSyncRules.BuildLinkUpsert(Account() with { LoginExpiresAt = AccountIdentityRules.MaxUnixMs }, null, null)!.LoginExpiresAt
            .Should().Be(AccountIdentityRules.MaxUnixMs);
        CatalogSyncRules.BuildLinkUpsert(Account() with { LoginExpiresAt = AccountIdentityRules.MaxUnixMs + 1 }, null, null)!.LoginExpiresAt
            .Should().BeNull("超過 9999 年");
        CatalogSyncRules.BuildLinkUpsert(Account() with { LoginExpiresAt = 0 }, null, null)!.LoginExpiresAt.Should().BeNull();
        CatalogSyncRules.BuildLinkUpsert(Account(status: "notLoggedIn") with { LoginExpiresAt = 1794000000000 }, null, null)!.LoginExpiresAt
            .Should().BeNull("登入已失效時檔案裡的到期時間沒有意義");
        CatalogSyncRules.BuildLinkUpsert(Account(status: "error") with { LoginExpiresAt = 1794000000000 }, null, null)!.LoginExpiresAt.Should().BeNull();
        CatalogSyncRules.BuildLinkUpsert(Account() with { LoginExpiresAt = -1 }, null, null)!.LoginExpiresAt.Should().BeNull();
        CatalogSyncRules.BuildLinkUpsert(Account(AgentProvider.Codex, "acc-codex") with { LoginExpiresAt = 1794000000000 }, null, null)!.LoginExpiresAt
            .Should().BeNull("Codex 沒有登入到期");
    }

    [Fact]
    public void Digest_LoginExpiresAt改變_摘要改變()
    {
        var a = CatalogSyncRules.BuildLinkUpsert(Account() with { LoginExpiresAt = 1794000000000 }, null, null)!;
        var b = CatalogSyncRules.BuildLinkUpsert(Account() with { LoginExpiresAt = 1794000000001 }, null, null)!;
        var none = CatalogSyncRules.BuildLinkUpsert(Account(), null, null)!;

        CatalogSyncRules.Digest(a, linked: true).Should().NotBe(CatalogSyncRules.Digest(b, linked: true));
        CatalogSyncRules.Digest(a, linked: false).Should().NotBe(CatalogSyncRules.Digest(none, linked: false));
    }

    [Fact]
    public void BuildLinkUpsert_通過伺服器驗證規則()
    {
        CatalogSyncRules.MaxLabelLength.Should().Be(ServerRulesMirror.MaxLabelLength);
        CatalogSyncRules.MaxEmailLength.Should().Be(ServerRulesMirror.MaxEmailLength);
        CatalogSyncRules.MaxIdentityKeyLength.Should().Be(ServerRulesMirror.MaxIdentityKeyLength);
        CatalogSyncRules.MaxTextLength.Should().Be(ServerRulesMirror.MaxTextLength);
        CatalogSyncRules.MaxShortTextLength.Should().Be(ServerRulesMirror.MaxShortTextLength);
        CatalogSyncRules.MaxSortOrder.Should().Be(ServerRulesMirror.MaxSortOrder);
        CatalogSyncRules.MaxSessionCount.Should().Be(ServerRulesMirror.MaxSessionCount);

        var control = ((char)0x01).ToString();
        var bidi = ((char)0x202E).ToString();
        var lone = ((char)0xD800).ToString();
        var emoji = char.ConvertFromUtf32(0x1F600);
        var nasty = new[]
        {
            Account(),
            Account(AgentProvider.Codex, "acc-codex", status: "notLoggedIn", email: "Codex@Example.com", orgId: null),
            Account(label: new string('名', 80), statusText: new string('說', 600) + control, plan: new string('P', 200) + lone,
                email: new string('e', 400) + "@x.com", orgId: new string('o', 300)),
            Account(label: bidi + "  " + control, color: "red", sortOrder: int.MaxValue, statusText: control + lone),
            Account(label: string.Concat(Enumerable.Repeat(emoji, 40)), sortOrder: int.MinValue, status: "weird-status!"),
            Account(label: "", email: "  ", statusText: null, plan: null) with { Sessions = new LinkSessions(-5, 999_999, int.MaxValue) },
        };
        foreach (var account in nasty)
        {
            var p = CatalogSyncRules.BuildLinkUpsert(account, SyncIds.NewLogicalAccountId(), null);
            p.Should().NotBeNull();
            ServerRulesMirror.ValidateLink(p).Should().BeNull($"{account.Label} 的 link.upsert 要通過伺服器檢查");
        }
    }

    [Fact]
    public void BuildLinkUpsert_不含任何權杖()
    {
        var oauth = (JObject)JObject.Parse(RealSamples.ClaudeCredentials)["claudeAiOauth"]!;
        var tokens = new[] { oauth.Value<string>("accessToken")!, oauth.Value<string>("refreshToken")! };
        var p = CatalogSyncRules.BuildLinkUpsert(Account(plan: oauth.Value<string>("subscriptionType")), SyncIds.NewLogicalAccountId(), null)!;

        var json = SyncJson.Serialize(p);
        foreach (var token in tokens)
            json.Should().NotContain(token);
        json.Should().NotContain("sk-ant-");
    }

    [Fact]
    public void Digest_只有CheckedAt不同_相同_已連結時LogicalAccountId不同也相同()
    {
        var a = CatalogSyncRules.BuildLinkUpsert(Account(checkedAt: 1), null, null)!;
        var b = CatalogSyncRules.BuildLinkUpsert(Account(checkedAt: 2), null, null)!;
        var c = CatalogSyncRules.BuildLinkUpsert(Account(checkedAt: 3), SyncIds.NewLogicalAccountId(), null)!;

        CatalogSyncRules.Digest(a, linked: false).Should().Be(CatalogSyncRules.Digest(b, linked: false));
        CatalogSyncRules.Digest(a, linked: true).Should().Be(CatalogSyncRules.Digest(c, linked: true), "已連結時邏輯帳戶由目錄決定，換了不必重送");
        CatalogSyncRules.Digest(a, linked: false).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Digest_未連結時含宣告_連結後不含()
    {
        var none = CatalogSyncRules.BuildLinkUpsert(Account(), null, null)!;
        var claimX = CatalogSyncRules.BuildLinkUpsert(Account(), Logical1, null)!;
        var claimY = CatalogSyncRules.BuildLinkUpsert(Account(), Logical2, null)!;

        CatalogSyncRules.Digest(claimX, linked: false).Should().NotBe(CatalogSyncRules.Digest(none, linked: false), "宣告了要重送");
        CatalogSyncRules.Digest(claimX, linked: false).Should().NotBe(CatalogSyncRules.Digest(claimY, linked: false), "宣告改變要重送");
        CatalogSyncRules.Digest(claimX, linked: true).Should().Be(CatalogSyncRules.Digest(claimY, linked: true));
        CatalogSyncRules.Digest(claimX, linked: true).Should().Be(CatalogSyncRules.Digest(none, linked: true));

        // 沒有宣告的值是 null、序列化時省略：既有帳戶的摘要和加這個欄位之前相同（不因升級全部重送）
        var json = JObject.FromObject(none, Newtonsoft.Json.JsonSerializer.Create(SyncJson.Settings));
        json.Remove("checkedAt");
        json.ContainsKey("logicalAccountId").Should().BeFalse();
        CatalogSyncRules.Digest(none, linked: false).Should().Be(PayloadRules.Sha256Hex(json.ToString(Newtonsoft.Json.Formatting.None)));
    }

    private const string Logical1 = "la_0123456789ABCDEFGHJKMNPQRS";

    private const string Logical2 = "la_0123456789ABCDEFGHJKMNPQRT";

    private static readonly IReadOnlySet<string> NoIds = new HashSet<string>(StringComparer.Ordinal);

    private static HashSet<string> Ids(params string[] ids) => new(ids, StringComparer.Ordinal);

    [Fact]
    public void ResolveLinkLogical_目錄的對應優先_宣告要在目錄而且同種類_同台第二個宣告不帶()
    {
        var earlier = Ids();
        // 目錄的對應優先：宣告別的也照對應（和伺服器的順序相同）
        CatalogSyncRules.ResolveLinkLogical(Logical2, Logical1, Ids(Logical1, Logical2), Ids(Logical2), earlier).Should().Be(Logical2);
        earlier.Should().BeEmpty("對應的不算宣告");
        // 沒有宣告、宣告格式不對、不在目錄（含不同種類的目錄帳戶）都是 null
        CatalogSyncRules.ResolveLinkLogical(null, null, Ids(Logical1), NoIds, earlier).Should().BeNull();
        CatalogSyncRules.ResolveLinkLogical(null, "la_PROBE0000000000000000000000", Ids("la_PROBE0000000000000000000000"), NoIds, earlier).Should().BeNull("格式不對");
        CatalogSyncRules.ResolveLinkLogical(null, Logical1, NoIds, NoIds, earlier).Should().BeNull("目錄沒有（或是不同種類的帳戶）");
        // 這台已經有本機帳戶連到它：不帶（伺服器以 (邏輯帳戶, 裝置) 為主鍵，會蓋掉原本的那個）
        CatalogSyncRules.ResolveLinkLogical(null, Logical1, Ids(Logical1), Ids(Logical1), earlier).Should().BeNull();
        earlier.Should().BeEmpty();
        // 第一個宣告者帶、記進 claimedEarlier；第二個不帶
        CatalogSyncRules.ResolveLinkLogical(null, Logical1, Ids(Logical1), NoIds, earlier).Should().Be(Logical1);
        earlier.Should().Equal(Logical1);
        CatalogSyncRules.ResolveLinkLogical(null, Logical1, Ids(Logical1), NoIds, earlier).Should().BeNull("同台只認清單裡第一個宣告者");
        CatalogSyncRules.ResolveLinkLogical(null, Logical2, Ids(Logical1, Logical2), NoIds, earlier).Should().Be(Logical2, "別的邏輯帳戶不受影響");
    }

    [Fact]
    public void ClaudeIdentityKey_org不明而且沒發布過_目錄鍵的email相同回null_不同回claude_email()
    {
        var catalog = $"claude:{Org}:me@example.com";
        CatalogSyncRules.ClaudeIdentityKey("Me@Example.com", null, null, catalog).Should().BeNull("email 相同：不送身分鍵，伺服器當成相符");
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", " ", null, "claude:me@example.com").Should().BeNull("目錄鍵沒有 org 時一樣取最後一段");
        CatalogSyncRules.ClaudeIdentityKey("wrong@example.com", null, null, catalog).Should().Be("claude:wrong@example.com", "登錯帳號要讓伺服器標成身分不符");
        CatalogSyncRules.ClaudeIdentityKey("a b@example.com", null, null, catalog).Should().BeNull("不合格的鍵不送");
        CatalogSyncRules.ClaudeIdentityKey(null, null, null, catalog).Should().BeNull("email 不知道：不送");
        CatalogSyncRules.ClaudeIdentityKey("wrong@example.com", null, null).Should().BeNull("沒有目錄鍵（沒對應也沒宣告）照舊不發布");
        // 其他分支不受目錄鍵影響
        CatalogSyncRules.ClaudeIdentityKey("wrong@example.com", Org, null, catalog).Should().Be($"claude:{Org}:wrong@example.com", "org 已知照舊");
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, catalog, "claude:x:other@example.com").Should().Be(catalog, "發布過的照舊比對發布過的鍵");
    }

    [Fact]
    public void ClaudeIdentityKey_D11_email相同_org提示和目錄鍵的org不同才送claude_email()
    {
        // SEC-1：同一個信箱、不同 org（保守模式的 org 提示）不能連成相符
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, null, "claude:org-T:me@example.com", orgIdHint: "org-P")
            .Should().Be("claude:me@example.com", "org 提示和目錄鍵的 org 不同：讓伺服器標成身分不符");
        CatalogSyncRules.ClaudeIdentityKey("Me@Example.com", null, null, "claude:org-T:me@example.com", orgIdHint: "org-T").Should().BeNull("org 提示相同：照舊");
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, null, "claude:org-T:me@example.com", orgIdHint: null).Should().BeNull("沒有提示：照舊");
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, null, "claude:org-T:me@example.com", orgIdHint: "ORG-T")
            .Should().Be("claude:me@example.com", "org 區分大小寫（身分鍵原樣組成）");
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, null, "claude:org-T:me@example.com", orgIdHint: "org P").Should().BeNull("不合格的提示不算");
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, null, "claude:me@example.com", orgIdHint: "org-P").Should().BeNull("目錄鍵沒有 org 段：無從比對");
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, null, "claude::me@example.com", orgIdHint: "org-P").Should().BeNull("org 段是空的");
        CatalogSyncRules.ClaudeIdentityKey("wrong@example.com", null, null, "claude:org-T:me@example.com", orgIdHint: "org-T")
            .Should().Be("claude:wrong@example.com", "email 不同照舊");
        // 提示絕不寫進身分鍵
        CatalogSyncRules.ClaudeIdentityKey("me@example.com", null, null, null, orgIdHint: "org-P").Should().BeNull("沒有目錄鍵照舊不發布");
        var payload = CatalogSyncRules.BuildLinkUpsert(Account(orgId: null, email: "me@example.com") with { OrgIdHint = "org-P" }, Logical1, null,
            "claude:org-T:me@example.com")!;
        payload.IdentityKey.Should().Be("claude:me@example.com", "BuildLinkUpsert 把提示傳給 D11");
        payload.OrgId.Should().BeNull("payload 的 OrgId 不放提示");
        CatalogSyncRules.BuildLinkUpsert(Account(orgId: null, email: "me@example.com") with { OrgIdHint = "org-T" }, Logical1, null,
            "claude:org-T:me@example.com")!.IdentityKey.Should().BeNull();
    }

    [Fact]
    public void BuildLinkUpsert_宣告的Claude帳戶_org不明_帶邏輯帳戶與D11的身分鍵_Codex不受目錄鍵影響()
    {
        var catalog = $"claude:{Org}:me@example.com";
        var claimed = CatalogSyncRules.BuildLinkUpsert(Account(orgId: null, status: "notLoggedIn", email: null), Logical1, null, catalog)!;
        claimed.LogicalAccountId.Should().Be(Logical1, "還沒登入也送，讓伺服器先連結");
        claimed.IdentityKey.Should().BeNull();
        CatalogSyncRules.BuildLinkUpsert(Account(orgId: null, email: "wrong@example.com"), Logical1, null, catalog)!.IdentityKey.Should().Be("claude:wrong@example.com");
        CatalogSyncRules.BuildLinkUpsert(Account(orgId: null, email: "ME@example.com"), Logical1, null, catalog)!.IdentityKey.Should().BeNull();
        CatalogSyncRules.BuildLinkUpsert(Account(AgentProvider.Codex, "acc-codex", email: "dev@example.com"), Logical1, null, "codex:other@example.com")!
            .IdentityKey.Should().Be("codex:dev@example.com");
    }

    [Fact]
    public void Digest_已連結_名稱顏色順序不同_相同()
    {
        var a = CatalogSyncRules.BuildLinkUpsert(Account(label: "甲", color: "#111111", sortOrder: 1), null, null)!;
        var b = CatalogSyncRules.BuildLinkUpsert(Account(label: "乙", color: "#222222", sortOrder: 2), null, null)!;

        CatalogSyncRules.Digest(a, linked: true).Should().Be(CatalogSyncRules.Digest(b, linked: true));
    }

    [Fact]
    public void Digest_未連結_名稱不同_不同()
    {
        var a = CatalogSyncRules.BuildLinkUpsert(Account(label: "甲"), null, null)!;
        var b = CatalogSyncRules.BuildLinkUpsert(Account(label: "乙"), null, null)!;

        CatalogSyncRules.Digest(a, linked: false).Should().NotBe(CatalogSyncRules.Digest(b, linked: false));
    }

    [Fact]
    public void Digest_status不同_不同()
    {
        var a = CatalogSyncRules.BuildLinkUpsert(Account(status: "ready"), null, null)!;
        var b = CatalogSyncRules.BuildLinkUpsert(Account(status: "notLoggedIn"), null, null)!;

        CatalogSyncRules.Digest(a, linked: true).Should().NotBe(CatalogSyncRules.Digest(b, linked: true));
    }

    [Fact]
    public void RemovedLinks_清單唯讀時為空()
    {
        CatalogSyncRules.RemovedLinks(["a", "b"], [], listReadOnly: true).Should().BeEmpty("accounts.json 讀不到時不能當成全部被移除");
    }

    [Fact]
    public void RemovedLinks_差集()
    {
        CatalogSyncRules.RemovedLinks(["c", "a", "b"], ["b", "x"], listReadOnly: false).Should().Equal("a", "c");
    }

    [Fact]
    public void NormalizeColor_三碼展開_大寫_非法回null()
    {
        CatalogSyncRules.NormalizeColor("#abc").Should().Be("#AABBCC");
        CatalogSyncRules.NormalizeColor(" #d97757 ").Should().Be("#D97757");
        CatalogSyncRules.NormalizeColor("#ABCDEF").Should().Be("#ABCDEF");
        CatalogSyncRules.NormalizeColor("abc").Should().BeNull();
        CatalogSyncRules.NormalizeColor("#abcd").Should().BeNull();
        CatalogSyncRules.NormalizeColor("#ggg").Should().BeNull();
        CatalogSyncRules.NormalizeColor(null).Should().BeNull();
    }

    [Fact]
    public void CleanLabel_空字串給預設名稱_超過60以文字元素截斷_控制字元清掉_不切斷代理字元對()
    {
        CatalogSyncRules.CleanLabel("", AgentProvider.Claude).Should().Be("Claude 帳戶");
        CatalogSyncRules.CleanLabel("   ", AgentProvider.Codex).Should().Be("Codex 帳戶");
        CatalogSyncRules.CleanLabel(null, AgentProvider.Codex).Should().Be("Codex 帳戶");
        CatalogSyncRules.CleanLabel(new string('名', 80), AgentProvider.Claude).Should().Be(new string('名', 60));
        CatalogSyncRules.CleanLabel("工作" + (char)0x01 + "帳" + (char)0x202E + "戶", AgentProvider.Claude).Should().Be("工作帳戶");
        CatalogSyncRules.CleanLabel(((char)0x202E).ToString(), AgentProvider.Claude).Should().Be("Claude 帳戶", "清理後是空的");

        var emoji = char.ConvertFromUtf32(0x1F600);
        var label = "a" + string.Concat(Enumerable.Repeat(emoji, 40));
        var cleaned = CatalogSyncRules.CleanLabel(label, AgentProvider.Claude);
        cleaned.Length.Should().BeLessThanOrEqualTo(60);
        cleaned.Should().Be("a" + string.Concat(Enumerable.Repeat(emoji, 29)), "以文字元素截斷，不切開代理字元對");
        new StringInfo(cleaned).LengthInTextElements.Should().Be(30);
    }

    [Fact]
    public void ReorderUpdates_重新分配既有的sortOrder_只回改變的()
    {
        var linked = new Dictionary<string, (string LogicalAccountId, int SortOrder)>
        {
            ["a"] = ("la_A", 10),
            ["b"] = ("la_B", 20),
            ["c"] = ("la_C", 30),
        };

        CatalogSyncRules.ReorderUpdates(["c", "local-only", "a", "b"], linked)
            .Should().Equal(("la_C", 10), ("la_A", 20), ("la_B", 30));
        CatalogSyncRules.ReorderUpdates(["a", "c", "b"], linked).Should().Equal(("la_C", 20), ("la_B", 30));
        CatalogSyncRules.ReorderUpdates(["a", "b", "c"], linked).Should().BeEmpty("順序沒變");
    }

    [Fact]
    public void ReorderUpdates_sortOrder同分_對調時送出不同的值()
    {
        // 伺服器只在建立時寫 sortOrder（建立那台的本機索引）：兩台各以索引 0 建的帳戶同分
        var linked = new Dictionary<string, (string LogicalAccountId, int SortOrder)>
        {
            ["a"] = ("la_A", 0),
            ["b"] = ("la_B", 0),
        };

        var swapped = CatalogSyncRules.ReorderUpdates(["b", "a"], linked);

        swapped.Should().Equal([("la_A", 1)], "照原值分配的話兩個都是 0、一筆都不送，下一次目錄套用又改回去；推開同分只改需要改的那個");
        CatalogSyncRules.ReorderUpdates(["a", "b"], linked).Should().Equal([("la_B", 1)], "另一個順序推開另一個");

        // 套用之後兩個順序都嚴格遞增
        foreach (string[] order in new[] { new[] { "b", "a" }, new[] { "a", "b" } })
        {
            var after = linked.ToDictionary(p => p.Value.LogicalAccountId, p => p.Value.SortOrder);
            foreach (var (logical, sortOrder) in CatalogSyncRules.ReorderUpdates(order, linked))
                after[logical] = sortOrder;
            order.Select(id => after[linked[id].LogicalAccountId]).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public void ReorderUpdates_同分推開時不超出上限()
    {
        var max = CatalogSyncRules.MaxSortOrder;
        var linked = new Dictionary<string, (string LogicalAccountId, int SortOrder)>
        {
            ["a"] = ("la_A", 5),
            ["b"] = ("la_B", max),
            ["c"] = ("la_C", max),
        };

        var updates = CatalogSyncRules.ReorderUpdates(["c", "a", "b"], linked);

        var slots = updates.ToDictionary(u => u.LogicalAccountId, u => u.SortOrder);
        var order = new[] { "la_C", "la_A", "la_B" }.Select(id => slots.TryGetValue(id, out var v) ? v : linked.Values.Single(l => l.LogicalAccountId == id).SortOrder).ToList();
        order.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        order.Should().OnlyContain(v => v >= -max && v <= max, "伺服器拒收超出範圍的 sortOrder");
    }

    [Fact]
    public void ReorderUpdates_有沒送出的順序修改_沒變也回傳()
    {
        var linked = new Dictionary<string, (string LogicalAccountId, int SortOrder)>
        {
            ["a"] = ("la_A", 10),
            ["b"] = ("la_B", 20),
            ["c"] = ("la_C", 30),
        };

        CatalogSyncRules.ReorderUpdates(["a", "b", "c"], linked, logical => logical == "la_B")
            .Should().Equal([("la_B", 20)], "排回原位：蓋掉 outbox 裡還沒送的那筆");
        CatalogSyncRules.ReorderUpdates(["b", "a", "c"], linked, logical => logical == "la_C")
            .Should().Equal(("la_B", 10), ("la_A", 20), ("la_C", 30));
    }

    [Fact]
    public void 合併鍵()
    {
        CatalogSyncRules.LinkCoalesceKey("acc-1").Should().Be("link:acc-1");
        CatalogSyncRules.DeviceCoalesceKey("d_X").Should().Be("device:d_X");
        CatalogSyncRules.CliCoalesceKey.Should().Be("cli");
    }
}

public sealed class CliStateRulesTests
{
    private static JObject CliView(bool installed = true) => new()
    {
        ["provider"] = "Claude",
        ["installed"] = installed,
        ["checking"] = false,
        ["version"] = "2.1.297",
        ["latest"] = "2.1.300",
        ["updateAvailable"] = true,
        ["installKind"] = "native",
        ["path"] = @"C:\Users\x\.local\bin\claude.exe",
        ["checkedAt"] = 1791580000000L,
        ["installBroken"] = "缺少 claude.exe",
        ["runningOnOldVersion"] = 2,
        ["restartPending"] = 1,
        ["restartWaiting"] = new JArray(),
    };

    [Fact]
    public void FromCliView_讀installBroken_runningOnOldVersion_restartPending()
    {
        var view = CliStateRules.FromCliView(CliView())!;

        view.Should().Be(new DeviceCliView(true, "2.1.297", "native", "2.1.300", true, 2, 1, "缺少 claude.exe", 1791580000000));
        CliStateRules.FromCliView(new JObject { ["provider"] = "Claude" }).Should().BeNull("不是 CliView");
        CliStateRules.FromCliView(null).Should().BeNull();
        var nulls = CliView();
        nulls["version"] = null;
        nulls["installBroken"] = null;
        nulls["checkedAt"] = null;
        nulls["runningOnOldVersion"] = -3;
        var cleaned = CliStateRules.FromCliView(nulls)!;
        cleaned.Version.Should().BeNull();
        cleaned.InstallBroken.Should().BeNull();
        cleaned.CheckedAt.Should().BeNull();
        cleaned.RunningOnOldVersion.Should().Be(0);
    }

    [Fact]
    public void Build_四種照AgentProviders_All的順序_送出前沒有prov1只留Claude與Codex()
    {
        var view = CliStateRules.FromCliView(CliView())!;
        var payload = CliStateRules.Build(new Dictionary<AgentProvider, DeviceCliView>
        {
            [AgentProvider.Antigravity] = view,
            [AgentProvider.Grok] = view,
            [AgentProvider.Codex] = view with { Version = "0.50.0" },
            [AgentProvider.Claude] = view,
        });

        payload.Providers.Keys.Should().Equal("Claude", "Codex", "Antigravity", "Grok");
        CliStateRules.ForServer(payload, prov1: true).Should().BeSameAs(payload);
        ServerRulesMirror.ValidateCli(CliStateRules.ForServer(payload, prov1: true)).Should().BeNull();
        var old = CliStateRules.ForServer(payload, prov1: false);
        old.Providers.Keys.Should().Equal("Claude", "Codex");
        ServerRulesMirror.ValidateCli(old, prov1: false).Should().BeNull("舊伺服器超過 2 種或不認得的種類就整筆拒收");
        CatalogSyncRules.CliDigest(old).Should().NotBe(CatalogSyncRules.CliDigest(payload), "prov1 有無改變時摘要不同，會重送");
        var huge = view with { Version = new string('9', 100), InstallBroken = new string('x', 600) };
        ServerRulesMirror.ValidateCli(CliStateRules.Build(new Dictionary<AgentProvider, DeviceCliView> { [AgentProvider.Claude] = huge }))
            .Should().NotBeNull("Build 不截斷，截斷在 FromCliView");
        var long1 = CliView();
        long1["version"] = new string('9', 100);
        long1["installBroken"] = new string('x', 600);
        ServerRulesMirror.ValidateCli(CliStateRules.Build(new Dictionary<AgentProvider, DeviceCliView> { [AgentProvider.Claude] = CliStateRules.FromCliView(long1)! }))
            .Should().BeNull("FromCliView 依伺服器上限截斷");
    }

    [Fact]
    public void Same_欄位相同為true()
    {
        var view = CliStateRules.FromCliView(CliView())!;
        var a = new CliStatePayload(new Dictionary<string, DeviceCliView> { ["Claude"] = view, ["Codex"] = view });
        var b = new CliStatePayload(new Dictionary<string, DeviceCliView> { ["Codex"] = view with { }, ["Claude"] = view with { } });

        CliStateRules.Same(a, b).Should().BeTrue("字典的順序不算");
        CliStateRules.Same(a, new CliStatePayload(new Dictionary<string, DeviceCliView> { ["Claude"] = view, ["Codex"] = view with { RestartPending = 0 } }))
            .Should().BeFalse();
        CliStateRules.Same(a, new CliStatePayload(new Dictionary<string, DeviceCliView> { ["Claude"] = view })).Should().BeFalse();
        CliStateRules.Same(null, a).Should().BeFalse();
    }
}

public sealed class PrefsSyncRulesTests
{
    [Fact]
    public void Snapshot_fontSize是JSON整數()
    {
        var snapshot = PrefsSyncRules.Snapshot(new Myriagent.Core.AppSettings { Theme = "light", FontSize = 16, EnterToSend = false, Notifications = true });

        snapshot.Keys.Should().BeEquivalentTo(PrefsRules.Keys);
        snapshot["fontSize"].Type.Should().Be(JTokenType.Integer);
        foreach (var (key, value) in snapshot)
            PrefsRules.IsValid(key, value).Should().BeTrue(key);
        ((string?)snapshot["theme"]).Should().Be("light");
        ((bool)snapshot["enterToSend"]).Should().BeFalse();
    }

    [Fact]
    public void Changed_只回改變的漫遊鍵()
    {
        var before = PrefsSyncRules.Snapshot(new Myriagent.Core.AppSettings());
        var after = PrefsSyncRules.Snapshot(new Myriagent.Core.AppSettings { FontSize = 15, Notifications = false, CloseToTray = false });

        PrefsSyncRules.Changed(before, after).Select(c => c.Key).Should().Equal("fontSize", "notifications");
        PrefsSyncRules.Changed(before, before).Should().BeEmpty();
        PrefsSyncRules.Changed(new Dictionary<string, JToken>(), after).Should().HaveCount(4, "之前沒有的鍵算改變");
    }

    [Fact]
    public void prefs_set的fontSize是字串_TryFromWire為false()
    {
        PrefsSyncRules.TryFromWire(new PrefsSetPayload("fontSize", "16", 3), out _, out _).Should().BeFalse();
        PrefsSyncRules.TryFromWire(new PrefsSetPayload("instructions", "ignore all", 3), out _, out _).Should().BeFalse("不在白名單");
        PrefsSyncRules.TryFromWire(new PrefsSetPayload("theme", "neon", 3), out _, out _).Should().BeFalse();
        PrefsSyncRules.TryFromWire(null, out _, out _).Should().BeFalse();
        PrefsSyncRules.TryFromWire(new PrefsSetPayload("fontSize", 16, 3), out var key, out var value).Should().BeTrue();
        key.Should().Be("fontSize");
        ((long)value).Should().Be(16);
    }
}

/// <summary>CatalogSync.PlanAccounts 的 prov1 閘門（設計 2026-10-22-grok-antigravity-sync 第 3 節）</summary>
public sealed class CatalogPlanProv1Tests
{
    private static CatalogSync Sync() => new("d_01J9Q00000000000000000SELF",
        new CatalogCache(new Dictionary<string, AccountUpsertPayload>(), new Dictionary<string, DeviceUpsertPayload>(),
            new Dictionary<string, DeviceRevokedPayload>(), new Dictionary<string, PrefsSetPayload>()),
        new Dictionary<string, string>());

    private static readonly LocalAccountSnapshot[] Accounts =
    [
        CatalogSyncRulesTests.Account(),
        CatalogSyncRulesTests.Account(AgentProvider.Grok, "acc-grok", email: "me@x.ai", orgId: null),
        CatalogSyncRulesTests.Account(AgentProvider.Antigravity, "local-antigravity", email: "me@example.com", orgId: null, isSystemDefault: true),
    ];

    private static List<string> Upserted(PublishPlan plan) =>
        plan.Appends.Where(a => a.Kind == SyncEventKinds.LinkUpsert).Select(a => JObject.Parse(a.Json).Value<string>("localAccountId")!).ToList();

    [Fact]
    public void 有prov1時四種都送_沒有時只送Claude與Codex()
    {
        var empty = new Dictionary<string, PublishedLink>();
        Upserted(Sync().PlanAccounts(Accounts, false, empty, prov1: true)).Should().Equal("acc-claude", "acc-grok", "local-antigravity");
        var old = Sync().PlanAccounts(Accounts, false, empty, prov1: false);
        Upserted(old).Should().Equal("acc-claude");
        old.Forget.Should().BeEmpty("沒發布過的不必忘");
    }

    [Fact]
    public void prov1消失時忘掉發布過的摘要_不送link_removed_回來時整筆重送()
    {
        var sync = Sync();
        var first = sync.PlanAccounts(Accounts, false, new Dictionary<string, PublishedLink>(), prov1: true);
        var published = first.Published.Where(p => p.Link is not null).ToDictionary(p => p.Key, p => p.Link!);

        var lost = sync.PlanAccounts(Accounts, false, published, prov1: false);
        lost.Appends.Should().BeEmpty("帳戶還在：不送 link.removed，Claude 的摘要沒變也不重送");
        lost.Forget.Should().BeEquivalentTo("acc-grok", "local-antigravity");

        foreach (var key in lost.Forget)
            published.Remove(key);
        Upserted(sync.PlanAccounts(Accounts, false, published, prov1: true)).Should().Equal("acc-grok", "local-antigravity");
    }
}
