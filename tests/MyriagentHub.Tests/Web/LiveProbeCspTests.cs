using MyriagentHub.Hosting;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Web;

/// <summary>
/// 網頁即時檢視的探針（WP4.6 計畫步 3：tools/ui-probe/hub-page.mjs，live-probe.mjs 與之後的全流程探針都經它開伺服器模式的頁面）
/// 照抄伺服器的 CSP：伺服器改了 CSP，探針要跟著改，不然「CSP 0 違規」測的是另一套規則（做法同 TransportHubFileTests 的 remote_probe的CSP與伺服器相同）
/// </summary>
public sealed class LiveProbeCspTests
{
    [Fact]
    public void LiveProbe的HUB_CSP與伺服器相同()
    {
        var probe = File.ReadAllText(RepoFiles.Combine("tools", "ui-probe", "hub-page.mjs"));
        var csp = SecurityHeaderRules.ContentSecurityPolicy("https://hub.probe.local");
        csp.Should().Contain("wss://hub.probe.local");
        probe.Should().Contain("export const HUB_CSP = \"" + csp + "\";", "hub-page.mjs 的 HUB_CSP 要和 SecurityHeaderRules.ContentSecurityPolicy(\"https://hub.probe.local\") 逐字相同");
    }
}
