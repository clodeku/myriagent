using System.Text.RegularExpressions;
using MyriagentHub.Hosting;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Web;

/// <summary>
/// 瀏覽器版的傳輸層 <c>wwwroot/transport-hub.js</c>（WP3.5；web-protocol 第六節第 2、13 點）的靜態檢查：
/// 伺服器把它當傳統腳本插在 app.js 之前，所以不能是 module；不能用會被 CSP 擋或能注入 HTML 的 API、不連外站。
/// 行為由 tools/ui-probe 的 rules.test.mjs（vm 載入、純規則）與 remote-probe.mjs（真的程式庫＋假的 hub）驗證
/// </summary>
public sealed partial class TransportHubFileTests
{
    private static string Source() => File.ReadAllText(RepoFiles.Combine("src", "MyriagentHub", "wwwroot", "transport-hub.js"));

    [Fact]
    public void 是傳統腳本()
    {
        var source = Source();
        source.Should().Contain("window.myriagentHubTransport", "要讀得到確認讀的是傳輸層本身");
        ModuleSyntax().IsMatch(source).Should().BeFalse("伺服器以 <script src> 插入（不是 type=module），行首不能有 import／export");
    }

    [Fact]
    public void 沒有危險API與外站網址()
    {
        var source = Source();
        foreach (var banned in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "eval(", "new Function", "document.write", "https://", "http://" })
            source.Should().NotContain(banned, $"transport-hub.js（含註解）不得出現 {banned}：CSP 擋、會注入 HTML 或連外站");
    }

    [Fact]
    public void 連線設定照契約()
    {
        // web-protocol 第六節第 3、13 點；計畫 D2、D3：只用 WebSockets＋skipNegotiation（每次連線前先問 auth/me）、
        // 不開 SignalR 的自動重連與 stateful reconnect（自己的退避、每次都建新的 HubConnection）、程式庫不往主控台寫記錄
        var source = Source();
        source.Should().Contain("skipNegotiation: true");
        source.Should().Contain("HttpTransportType.WebSockets");
        source.Should().Contain("LogLevel.None");
        source.Should().Contain("'" + Myriagent.Sync.SyncProtocol.BrowserHubPath + "'", "hub 的路徑要和伺服器的 SyncProtocol.BrowserHubPath 相同");
        source.Should().Contain("'/api/v1/auth/me'", "每次連線前先問登入狀態（skipNegotiation 看不到升級的 401）");
        source.Should().NotContain("withStatefulReconnect");
        source.Should().NotContain("withAutomaticReconnect");
    }

    [Fact]
    public void remote_probe的CSP與伺服器相同()
    {
        // 伺服器模式的介面探針（tools/ui-probe/remote-probe.mjs）照抄伺服器的 CSP：伺服器改了 CSP，探針要跟著改，不然探針測的是另一套規則
        var probe = File.ReadAllText(RepoFiles.Combine("tools", "ui-probe", "remote-probe.mjs"));
        var csp = SecurityHeaderRules.ContentSecurityPolicy("https://hub.probe.local");
        csp.Should().Contain("wss://hub.probe.local");
        probe.Should().Contain("\"" + csp + "\"", "remote-probe.mjs 的 HUB_CSP 要和 SecurityHeaderRules.ContentSecurityPolicy(\"https://hub.probe.local\") 逐字相同");
    }

    [Fact]
    public void conv_probe的CSP與伺服器相同()
    {
        // 伺服器對話的瀏覽器探針（tools/ui-probe/conv-probe.mjs，WP4.2 步 15）一樣照抄伺服器的 CSP：CSP 0 違規要在伺服器真正的規則下才算數
        var probe = File.ReadAllText(RepoFiles.Combine("tools", "ui-probe", "conv-probe.mjs"));
        var csp = SecurityHeaderRules.ContentSecurityPolicy("https://hub.probe.local");
        probe.Should().Contain("\"" + csp + "\"", "conv-probe.mjs 的 HUB_CSP 要和 SecurityHeaderRules.ContentSecurityPolicy(\"https://hub.probe.local\") 逐字相同");
    }

    [GeneratedRegex(@"^\s*(import|export)\s", RegexOptions.Multiline)]
    private static partial Regex ModuleSyntax();
}
