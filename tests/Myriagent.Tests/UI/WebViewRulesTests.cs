using Myriagent.Agents;
using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>WebView2 宿主的小規則：媒體網址的查詢參數、嵌入資源白名單、CSP</summary>
public class WebViewRulesTests
{
    [Fact]
    public void GetQueryValue_百分比編碼的加號還原成加號_字面加號是空白()
    {
        AgentWebView.GetQueryValue(new Uri("https://myriagent.local/media?path=C%3A%5Cdir%5Ca%2Bb.png"), "path").Should().Be(@"C:\dir\a+b.png");
        AgentWebView.GetQueryValue(new Uri("https://myriagent.local/thumb?path=a+b.png&size=160"), "path").Should().Be("a b.png");
    }

    [Theory]
    [InlineData(@"C:\資料\a+b c.png")]
    [InlineData(@"D:\影片\第 1 集 (完整版) #2 & 3%.mp4")]
    [InlineData(@"C:\x\=;?.jpg")]
    public void GetQueryValue_與MediaUrl來回一致(string path)
        => AgentWebView.GetQueryValue(new Uri(AgentMarkdownRenderer.MediaUrl(path)), "path").Should().Be(path);

    [Fact]
    public void GetQueryValue_取其他參數_名稱不分大小寫_找不到回Null()
    {
        var uri = new Uri(AgentMarkdownRenderer.ThumbUrl(@"C:\a.png", 240));
        AgentWebView.GetQueryValue(uri, "size").Should().Be("240");
        AgentWebView.GetQueryValue(uri, "PATH").Should().Be(@"C:\a.png");
        AgentWebView.GetQueryValue(uri, "missing").Should().BeNull();
        AgentWebView.GetQueryValue(new Uri("https://myriagent.local/media"), "path").Should().BeNull();
        AgentWebView.GetQueryValue(new Uri("https://myriagent.local/media?flag&path="), "flag").Should().BeEmpty("只有名稱沒有值");
        AgentWebView.GetQueryValue(new Uri("https://myriagent.local/media?flag&path="), "path").Should().BeEmpty();
    }

    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("app.js", "text/javascript; charset=utf-8")]
    [InlineData("app.css", "text/css; charset=utf-8")]
    [InlineData("icons.svg", "image/svg+xml")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("tool.exe", null)]
    [InlineData("noext", null)]
    public void WebAssets_ContentType白名單(string name, string? expected)
        => WebAssets.ContentType(name).Should().Be(expected);

    [Fact]
    public void WebAssets_介面網頁有嵌入_名稱不分大小寫()
    {
        WebAssets.Exists("index.html").Should().BeTrue();
        WebAssets.Exists("app.js").Should().BeTrue();
        using (var stream = WebAssets.Open("INDEX.HTML"))
            stream.Should().NotBeNull();
        WebAssets.Open("沒有這個.js").Should().BeNull();
    }

    [Fact]
    public void ContentSecurityPolicy_禁止InlineScript與外站資源()
    {
        AgentWebView.ContentSecurityPolicy.Should().Contain("script-src 'self'");
        AgentWebView.ContentSecurityPolicy.Should().NotContain("'unsafe-eval'");
        AgentWebView.ContentSecurityPolicy.Should().Contain("default-src 'none'");
        AgentWebView.ContentSecurityPolicy.Should().Contain("img-src 'self' data: blob:");
        AgentWebView.ContentSecurityPolicy.Split(';').Single(p => p.Trim().StartsWith("script-src", StringComparison.Ordinal))
            .Should().NotContain("unsafe-inline", "inline script 要被 CSP 擋下");
    }
}
