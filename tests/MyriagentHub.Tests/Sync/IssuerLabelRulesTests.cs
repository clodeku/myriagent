using System.Globalization;
using Myriagent.Security;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 信封的簽發者標籤（WP4.5 計畫步 1；D11）：由工作階段的 User-Agent 認出「瀏覽器・作業系統」，一定通過
/// <see cref="CommandEnvelope.IsValidIssuerLabel"/>、不超過 64 字，認不出時是「瀏覽器」
/// </summary>
public sealed class IssuerLabelRulesTests
{
    private const string ChromeWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    [Theory]
    [InlineData(ChromeWindows, "Chrome・Windows")]
    [InlineData(ChromeWindows + " Edg/141.0.0.0", "Edge・Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 OPR/120.0.0.0", "Opera・Linux")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari・iOS")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/141.0.0.0 Mobile/15E148 Safari/604.1", "Chrome・iOS")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15", "Safari・macOS")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14.0; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox・macOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36", "Chrome・Android")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox・Windows")]
    [InlineData("Chrome/141.0.0.0", "Chrome")]
    [InlineData(null, "瀏覽器")]
    [InlineData("", "瀏覽器")]
    [InlineData("curl/8.9.1", "瀏覽器")]
    [InlineData("@@@ ### 亂碼 ％％％", "瀏覽器")]
    public void 認出瀏覽器與作業系統(string? userAgent, string expected) => IssuerLabelRules.Label(userAgent).Should().Be(expected);

    [Fact]
    public void Edge的UA也含Chrome_先認Edge()
    {
        // 突變：先比 Chrome 會讓這裡變成「Chrome・Windows」
        (ChromeWindows + " Edg/141.0.0.0").Should().Contain("Chrome/", "正向對照：Edge 的 UA 也有 Chrome/");
        IssuerLabelRules.Label(ChromeWindows + " Edg/141.0.0.0").Should().StartWith("Edge");
    }

    [Fact]
    public void 所有輸出都通過IsValidIssuerLabel而且不超過64字()
    {
        // 格式字元與換行在執行時組出（原始碼裡不放隱形字元）
        var rlo = ((char)0x202E).ToString();
        var zwsp = ((char)0x200B).ToString();
        var lineSeparator = ((char)0x2028).ToString();
        var corpus = new List<string?>
        {
            null, "", " ", ChromeWindows,
            "Chrome/1 " + rlo + "Windows",
            rlo + ChromeWindows,
            "Edg/" + new string((char)0x200B, 200),
            zwsp + "Firefox/" + zwsp + "Android" + zwsp,
            "Safari/" + lineSeparator + "iPhone",
            "Chrome/1\r\nX-Injected: 1",
            new string('A', 100_000) + " Chrome/141 Windows",
            "Chrome/" + new string('9', 10_000),
            string.Concat(Enumerable.Repeat("Edg/ OPR/ Chrome/ Firefox/ Safari/ Windows Android iPhone Linux ", 500)),
            "\0Chrome/1\0Windows\0",
        };

        foreach (var userAgent in corpus)
        {
            var label = IssuerLabelRules.Label(userAgent);
            CommandEnvelope.IsValidIssuerLabel(label).Should().BeTrue($"UA 長度 {userAgent?.Length}");
            new StringInfo(label).LengthInTextElements.Should().BeLessThanOrEqualTo(64);
            label.Should().NotContain(rlo).And.NotContain(zwsp).And.NotContain(lineSeparator).And.NotContain("\n").And.NotContain("\0");
        }
    }
}
