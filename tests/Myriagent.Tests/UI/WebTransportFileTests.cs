using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.UI;

/// <summary>
/// 網頁的傳輸抽象（WP3.5，web-protocol 第六節第 13 點）：桌面也嵌入 Web/ 的每一個檔，所以 transport.js 不得帶任何連線程式與 hub 程式庫
/// （「未配對不載入 SignalR」在網頁的對應；hub 的程式只在伺服器插入的 transport-hub.js），store.js 只經 transport.js 收送
/// </summary>
public class WebTransportFileTests
{
    private static string WebFile(string name) => File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web", name));

    [Fact]
    public void transport_js不含SignalR與網路程式()
    {
        var source = WebFile("transport.js");
        source.Should().Contain("myriagentHubTransport", "要讀得到確認讀的是傳輸抽象本身");
        foreach (var banned in new[] { "signalR", "SignalR", "fetch(", "WebSocket", "HubConnection", "XMLHttpRequest", "import ", "EventSource" })
            source.Should().NotContain(banned, $"transport.js 桌面也會載入，不得出現 {banned}");
    }

    [Fact]
    public void store_js只經transport收送()
    {
        var source = WebFile("store.js");
        source.Should().Contain("from './transport.js'");
        source.Should().NotContain("chrome.webview", "橋接只在 transport.js");
        source.Should().NotContain("myriagentHubTransport", "橋接只在 transport.js");
    }

    [Fact]
    public void 其他網頁模組不直接碰橋接()
    {
        var dir = Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web");
        var files = Directory.GetFiles(dir, "*.js").Where(f => Path.GetFileName(f) != "transport.js").ToList();
        files.Should().HaveCountGreaterThan(10, "要讀得到 Web/ 的模組");
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            source.Should().NotContain("chrome.webview", $"{Path.GetFileName(file)} 要經 store.js 的 post／onHostMessage");
            source.Should().NotContain("window.myriagentHubTransport", $"{Path.GetFileName(file)} 要經 store.js 的 post／onHostMessage");
        }
    }

    [Fact]
    public void 網路請求只在hubapi_js()
    {
        // WP4.6 步 11：瀏覽器的上傳（E4）也只在 hubapi.js（帶 X-Myriagent-Csrf、401 回登入頁），其他模組經它或傳輸層
        var dir = Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web");
        var files = Directory.GetFiles(dir, "*.js").Where(f => Path.GetFileName(f) != "hubapi.js").ToList();
        files.Should().HaveCountGreaterThan(10, "要讀得到 Web/ 的模組");
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var banned in new[] { "fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "sendBeacon" })
                source.Should().NotContain(banned, $"{Path.GetFileName(file)} 不直接發網路請求，要經 hubapi.js");
        }
        var api = WebFile("hubapi.js");
        api.Should().Contain("new XMLHttpRequest()", "上傳要有進度，用 XMLHttpRequest");
        api.Should().Contain("'/api/v1/uploads/' + encodeURIComponent(", "uploadId 要編碼後才放進路徑");
        api.Should().Contain("setRequestHeader('X-Myriagent-Csrf', '1')", "E4 要求 CSRF 標頭");
    }
}
