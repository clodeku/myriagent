using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>index.html 送出前依目前主題換掉根元素的 data-theme（淺色主題不先閃深色）</summary>
public class WebAssetRulesTests
{
    private const string Html = "<!doctype html>\n<html lang=\"zh-Hant-TW\" data-theme=\"dark\">\n<head></head>\n<body></body>\n</html>\n";

    [Fact]
    public void ApplyInitialTheme_淺色_換掉Html標籤的DataTheme()
        => WebAssetRules.ApplyInitialTheme(Html, dark: false)
            .Should().Be("<!doctype html>\n<html lang=\"zh-Hant-TW\" data-theme=\"light\">\n<head></head>\n<body></body>\n</html>\n");

    [Fact]
    public void ApplyInitialTheme_深色_原樣回傳()
        => WebAssetRules.ApplyInitialTheme(Html, dark: true).Should().BeSameAs(Html);

    [Theory]
    [InlineData("<html lang=\"zh\"><body data-theme=\"dark\"></body></html>")]
    [InlineData("<body data-theme=\"dark\"></body>")]
    [InlineData("<html data-theme=\"dark\"")]
    [InlineData("")]
    public void ApplyInitialTheme_只改Html開始標籤_找不到就不動(string html)
        => WebAssetRules.ApplyInitialTheme(html, dark: false).Should().Be(html);

    [Fact]
    public void ApplyInitialTheme_嵌入的IndexHtml_淺色時根元素是Light()
    {
        using var stream = WebAssets.Open("index.html");
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        var light = WebAssetRules.ApplyInitialTheme(html, dark: false);
        light.Should().Contain("<html lang=\"zh-Hant-TW\" data-theme=\"light\">", "index.html 的根元素要帶 data-theme=\"dark\"，宿主才換得到");
        light.Should().NotContain("data-theme=\"dark\"");
    }
}
