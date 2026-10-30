using System.Net;
using System.Net.WebSockets;
using System.Text;
using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Http;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器 hub 的 WebSocket 壓縮（丙；設計 2026-10-23-live-delta 第 5 節）：P0 以真的 Kestrel 確認外層換上的功能真的被 SignalR 用到——
/// 升級回應的 <c>Sec-WebSocket-Extensions</c> 帶 permessage-deflate（預設不保留字典），裝置 hub 不帶，用戶端沒提出就不壓縮
/// </summary>
public sealed class BrowserWebSocketCompressionTests
{
    private const string Extensions = "Sec-WebSocket-Extensions";

    [Fact]
    public void 設定值與適用範圍_不認得的值一律關閉()
    {
        new HubOptions().Browser.WebSocketCompression.Should().Be(WebSocketCompressionRules.NoContextTakeover, "擁有者 2026-10-23 選的 Q4 (b)");
        WebSocketCompressionRules.Parse("noContextTakeover").Should().Be(WebSocketCompressionMode.NoContextTakeover);
        WebSocketCompressionRules.Parse(" NOCONTEXTTAKEOVER ").Should().Be(WebSocketCompressionMode.NoContextTakeover);
        WebSocketCompressionRules.Parse("contextTakeover").Should().Be(WebSocketCompressionMode.ContextTakeover);
        foreach (var value in new[] { "off", "OFF", null, "", "gzip", "true", "no-context-takeover" })
            WebSocketCompressionRules.Parse(value).Should().Be(WebSocketCompressionMode.Off, $"「{value}」");

        WebSocketCompressionRules.Applies(SyncProtocol.BrowserHubPath, WebSocketCompressionMode.NoContextTakeover).Should().BeTrue();
        WebSocketCompressionRules.Applies(new PathString(SyncProtocol.BrowserHubPath + "/negotiate"), WebSocketCompressionMode.ContextTakeover).Should().BeTrue();
        WebSocketCompressionRules.Applies(SyncProtocol.DeviceHubPath, WebSocketCompressionMode.NoContextTakeover).Should().BeFalse("裝置 hub 一律不壓縮");
        WebSocketCompressionRules.Applies("/hubs/v1/browserx", WebSocketCompressionMode.NoContextTakeover).Should().BeFalse();
        WebSocketCompressionRules.Applies(SyncProtocol.BrowserHubPath, WebSocketCompressionMode.Off).Should().BeFalse();

        var given = new WebSocketAcceptContext { SubProtocol = "x", KeepAliveInterval = TimeSpan.FromSeconds(7), KeepAliveTimeout = TimeSpan.FromSeconds(9) };
        var none = WebSocketCompressionRules.Configure(given, WebSocketCompressionMode.NoContextTakeover);
        none.Should().NotBeSameAs(given);
        none.DangerousEnableCompression.Should().BeTrue();
        none.DisableServerContextTakeover.Should().BeTrue();
        none.ServerMaxWindowBits.Should().Be(15);
        none.SubProtocol.Should().Be("x", "SignalR 給的子協定照舊");
        none.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(7));
        none.KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(9));
        given.DangerousEnableCompression.Should().BeFalse("傳進來的不改：SignalR 共用它，改了會漏到之後的每一次升級");
        given.DisableServerContextTakeover.Should().BeFalse();
        var keep = WebSocketCompressionRules.Configure(new WebSocketAcceptContext(), WebSocketCompressionMode.ContextTakeover);
        keep.DangerousEnableCompression.Should().BeTrue();
        keep.DisableServerContextTakeover.Should().BeFalse();
        var off = WebSocketCompressionRules.Configure(given, WebSocketCompressionMode.Off);
        off.Should().NotBeSameAs(given);
        off.DangerousEnableCompression.Should().BeFalse();
        off.SubProtocol.Should().Be("x");
    }

    [Fact]
    public void 複本照抄WebSocketAcceptContext的每個公開欄位()
    {
        // 框架日後在 WebSocketAcceptContext 加欄位時這裡會紅：Configure 要一起照抄，否則 SignalR 給的設定會被丟掉
        var properties = typeof(WebSocketAcceptContext).GetProperties().Where(p => p.CanRead && p.CanWrite).Select(p => p.Name).Order().ToList();
        properties.Should().Equal("DangerousEnableCompression", "DisableServerContextTakeover", "KeepAliveInterval", "KeepAliveTimeout", "ServerMaxWindowBits", "SubProtocol");
        var given = new WebSocketAcceptContext
        {
            SubProtocol = "p", KeepAliveInterval = TimeSpan.FromSeconds(3), KeepAliveTimeout = TimeSpan.FromSeconds(4),
            DangerousEnableCompression = true, DisableServerContextTakeover = true, ServerMaxWindowBits = 10,
        };
        var copy = WebSocketCompressionRules.Configure(given, WebSocketCompressionMode.Off);
        foreach (var property in typeof(WebSocketAcceptContext).GetProperties().Where(p => p.CanRead && p.CanWrite))
            property.GetValue(copy).Should().Be(property.GetValue(given), property.Name);
    }

    [Fact]
    public async Task 預設_瀏覽器hub協商出不保留字典的壓縮_SignalR交握照常_裝置hub與沒提出的用戶端不壓縮()
    {
        await using var host = await HubTestHost.StartAsync(kestrel: true);
        var cookie = await LoginAsync(host);

        using (var ws = await ConnectAsync(host, SyncProtocol.BrowserHubPath, deflate: true, ("Origin", HubTestHost.Origin), ("Cookie", cookie)))
        {
            var offered = ExtensionsOf(ws);
            offered.Should().Contain("permessage-deflate").And.Contain("server_no_context_takeover");
            // SignalR 的交握經過壓縮的連線照常完成（伺服器回空的交握回應）
            (await HandshakeAsync(ws)).Should().Be("{}");
        }

        using (var plain = await ConnectAsync(host, SyncProtocol.BrowserHubPath, deflate: false, ("Origin", HubTestHost.Origin), ("Cookie", cookie)))
        {
            ExtensionsOf(plain).Should().BeEmpty("用戶端沒提出 permessage-deflate");
            (await HandshakeAsync(plain)).Should().Be("{}");
        }

        using var device = await TestDevice.AddAsync(host);
        using (var ws = await ConnectAsync(host, SyncProtocol.DeviceHubPath, deflate: true, ("Authorization", "Bearer " + device.Token)))
            ExtensionsOf(ws).Should().BeEmpty("裝置 hub 一律不壓縮");
    }

    [Theory]
    [InlineData("contextTakeover", true, false)]
    [InlineData("off", false, false)]
    [InlineData("寫錯的值", false, false)]
    public async Task 設定_保留字典或關閉(string setting, bool compressed, bool noContextTakeover)
    {
        await using var host = await HubTestHost.StartAsync(kestrel: true, settings: new Dictionary<string, string?> { ["Hub:Browser:WebSocketCompression"] = setting });
        var cookie = await LoginAsync(host);
        using var ws = await ConnectAsync(host, SyncProtocol.BrowserHubPath, deflate: true, ("Origin", HubTestHost.Origin), ("Cookie", cookie));
        var offered = ExtensionsOf(ws);
        offered.Contains("permessage-deflate", StringComparison.Ordinal).Should().Be(compressed);
        offered.Contains("server_no_context_takeover", StringComparison.Ordinal).Should().Be(noContextTakeover);
        (await HandshakeAsync(ws)).Should().Be("{}");
    }

    private static async Task<string> LoginAsync(HubTestHost host)
    {
        var cookies = new CookieContainer();
        var handler = new CookieHandler(cookies)
        {
            InnerHandler = new SocketsHttpHandler { UseCookies = false, SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } },
        };
        using var http = new HubClient(new HttpClient(handler) { BaseAddress = host.KestrelAddress }, handler);
        await OwnerFlows.LoginOwnerAsync(http);
        return cookies.GetCookieHeader(host.KestrelAddress!);
    }

    private static async Task<ClientWebSocket> ConnectAsync(HubTestHost host, string path, bool deflate, params (string Name, string Value)[] headers)
    {
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        if (deflate)
            ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        foreach (var (name, value) in headers)
            ws.Options.SetRequestHeader(name, value);
        var uri = new UriBuilder(new Uri(host.KestrelAddress!, path)) { Scheme = "wss" }.Uri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(uri, timeout.Token);
        return ws;
    }

    /// <summary>升級回應的 Sec-WebSocket-Extensions（標頭名稱不分大小寫；HTTP/2 是小寫）</summary>
    private static string ExtensionsOf(ClientWebSocket ws) =>
        string.Join(", ", (ws.HttpResponseHeaders ?? new Dictionary<string, IEnumerable<string>>())
            .Where(h => h.Key.Equals(Extensions, StringComparison.OrdinalIgnoreCase)).SelectMany(h => h.Value));

    /// <summary>SignalR 的交握（JSON 協定）：送 {"protocol":"json","version":1} 加 0x1E，回傳伺服器回應去掉 0x1E</summary>
    private static async Task<string> HandshakeAsync(ClientWebSocket ws)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}" + (char)0x1E);
        await ws.SendAsync(request, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count).TrimEnd((char)0x1E);
    }
}
