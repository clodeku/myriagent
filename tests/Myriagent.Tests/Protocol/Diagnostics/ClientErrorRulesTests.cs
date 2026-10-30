using Myriagent.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Diagnostics;

/// <summary>網頁的錯誤回報 clientError（web-protocol 第二節；桌面與伺服器共用的驗證與記錄行）</summary>
public class ClientErrorRulesTests
{
    [Fact]
    public void TryParse_三種種類都收_選填欄位可以省略或null()
    {
        foreach (var kind in new[] { "error", "rejection", "media" })
            ClientErrorRules.TryParse(new JObject { ["kind"] = kind, ["message"] = "m" }).Should().Be(new ClientErrorReport(kind, "m", null, null));

        var full = ClientErrorRules.TryParse(JObject.Parse("""{"cmd":"clientError","kind":"media","message":"img 載入失敗","source":"/t/k?s=320","detail":null}"""));
        full.Should().Be(new ClientErrorReport("media", "img 載入失敗", "/t/k?s=320", null));
    }

    [Theory]
    [InlineData("""{"message":"m"}""")]
    [InlineData("""{"kind":"Error","message":"m"}""")]
    [InlineData("""{"kind":"warn","message":"m"}""")]
    [InlineData("""{"kind":1,"message":"m"}""")]
    [InlineData("""{"kind":"error"}""")]
    [InlineData("""{"kind":"error","message":null}""")]
    [InlineData("""{"kind":"error","message":5}""")]
    [InlineData("""{"kind":"error","message":"m","source":3}""")]
    [InlineData("""{"kind":"error","message":"m","detail":{"a":1}}""")]
    public void TryParse_不合格回null(string json) => ClientErrorRules.TryParse(JObject.Parse(json)).Should().BeNull(json);

    [Fact]
    public void TryParse_null回null() => ClientErrorRules.TryParse(null).Should().BeNull();

    [Fact]
    public void 換行與控制字元不讓一則回報變成好幾行()
    {
        var report = ClientErrorRules.TryParse(new JObject
        {
            ["kind"] = "error",
            ["message"] = "line1\r\nline2\nline3\tend" + (char)0x2028 + "x",
            ["detail"] = "at a (app.js:1:2)\n    at b (items.js:3:4)",
        })!;
        report.Message.Should().Be("line1 ⏎ line2 ⏎ line3 end x");
        report.Detail.Should().Be("at a (app.js:1:2) ⏎     at b (items.js:3:4)");
        ClientErrorRules.Describe(report).Should().NotContainAny("\n", "\r");
    }

    [Fact]
    public void 超過上限截短加省略號_空白的訊息換成說明()
    {
        var report = ClientErrorRules.TryParse(new JObject
        {
            ["kind"] = "error",
            ["message"] = new string('m', 600),
            ["source"] = new string('s', 600),
            ["detail"] = new string('d', 3000),
        })!;
        report.Message.Should().HaveLength(ClientErrorRules.MaxMessageLength + 1).And.EndWith("…");
        report.Source.Should().HaveLength(ClientErrorRules.MaxSourceLength + 1);
        report.Detail.Should().HaveLength(ClientErrorRules.MaxDetailLength + 1);

        ClientErrorRules.TryParse(new JObject { ["kind"] = "error", ["message"] = "  \n " })!.Message.Should().Be("（沒有訊息）");
        ClientErrorRules.TryParse(new JObject { ["kind"] = "error", ["message"] = "m", ["source"] = "   " })!.Source.Should().BeNull();
    }

    [Fact]
    public void 截短不切在代理字元對中間()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var text = new string('a', ClientErrorRules.MaxMessageLength - 1) + emoji + "tail";
        var message = ClientErrorRules.TryParse(new JObject { ["kind"] = "error", ["message"] = text })!.Message;
        message.Should().Be(new string('a', ClientErrorRules.MaxMessageLength - 1) + "…");
    }

    [Fact]
    public void Describe與節流鍵()
    {
        var report = new ClientErrorReport("media", "img 載入失敗", "/t/k?s=320", null);
        ClientErrorRules.Describe(report).Should().Be("媒體載入失敗：img 載入失敗（/t/k?s=320）");
        ClientErrorRules.Describe(new ClientErrorReport("error", "TypeError: x", "app.js:1:2", "at a"))
            .Should().Be("例外：TypeError: x（app.js:1:2）｜at a");
        ClientErrorRules.Describe(new ClientErrorReport("rejection", "Error: e", null, null)).Should().Be("未處理的 Promise 拒絕：Error: e");

        ClientErrorRules.ThrottleKey(report).Should().Be(ClientErrorRules.ThrottleKey(report with { Detail = "另一條堆疊" }), "堆疊不算");
        ClientErrorRules.ThrottleKey(report).Should().NotBe(ClientErrorRules.ThrottleKey(report with { Source = "/t/other" }));
    }
}
