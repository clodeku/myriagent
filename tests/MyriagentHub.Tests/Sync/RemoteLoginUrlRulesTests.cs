using Myriagent.Remote;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 遠端登入驗證網址的主機白名單（WP4.5 計畫步 2；D20、契約 D18）：伺服器只把 https、沒有帳密、預設埠、主機在白名單裡的裝置碼網址轉給瀏覽器
/// </summary>
public sealed class RemoteLoginUrlRulesTests
{
    [Theory]
    [InlineData("https://auth.openai.com/codex/device")]
    [InlineData("https://AUTH.OPENAI.COM/codex/device")]
    [InlineData("https://auth.openai.com")]
    [InlineData("https://auth.openai.com:443/codex/device?x=1")]
    public void 白名單的主機可以(string url) => RemoteLoginUrlRules.IsAllowedVerificationUrl(url).Should().BeTrue(url);

    [Theory]
    [InlineData("http://auth.openai.com/codex/device")]
    [InlineData("https://auth.openai.com.evil.test/codex/device")]
    [InlineData("https://evil.test/auth.openai.com")]
    [InlineData("https://user@auth.openai.com/codex/device")]
    [InlineData("https://user:pass@auth.openai.com/codex/device")]
    [InlineData("https://auth.openai.com:8443/codex/device")]
    [InlineData("https://openai.com/codex/device")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/codex/device")]
    [InlineData("auth.openai.com/codex/device")]
    [InlineData("不是網址 %%%")]
    [InlineData("")]
    [InlineData(null)]
    public void 其他都不行(string? url) => RemoteLoginUrlRules.IsAllowedVerificationUrl(url).Should().BeFalse(url);

    [Fact]
    public void 白名單是Codex與Grok的主機()
    {
        // 完整清單由 WP4.8 決定（K10）；Grok 的遠端登入（設計 2026-10-22-grok-antigravity-sync 的 P3）加了 accounts.x.ai
        RemoteLoginUrlRules.VerificationHosts.Should().BeEquivalentTo(["auth.openai.com", "accounts.x.ai"]);
    }
}
