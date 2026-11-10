using Myriagent.Agents;
using Myriagent.Cli;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

public sealed class CodexNewControlsTests
{
    private static JObject Form() => JObject.Parse("""
        {"mode":"form","serverName":"測試","message":"填寫資料","requestedSchema":{"type":"object","required":["count","enabled","tags"],"properties":{
        "count":{"type":"integer","minimum":1,"maximum":5},"enabled":{"type":"boolean"},
        "tags":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"string","enum":["a","b"]}},
        "mail":{"type":"string","format":"email"}}}}
        """);

    [Fact]
    public void 表單回覆保留數字布林多選型別_選填可略過()
    {
        var (response, error) = CodexElicitationRules.BuildResponse(Form(), new Dictionary<string, IReadOnlyList<string>>
        { ["count"] = ["3"], ["enabled"] = ["否"], ["tags"] = ["a", "b"], ["mail"] = ["不填"] });
        error.Should().BeNull();
        response["content"]!["count"]!.Type.Should().Be(JTokenType.Integer);
        response["content"]!["enabled"]!.Value<bool>().Should().BeFalse();
        response["content"]!["tags"]!.Values<string>().Should().Equal("a", "b");
        response["content"]!["mail"].Should().BeNull();
    }

    [Theory]
    [InlineData("0", "不得小於")]
    [InlineData("6", "不得大於")]
    [InlineData("1.5", "整數")]
    [InlineData("", "必填")]
    public void 不合格答案要求修正(string count, string expected)
    {
        var result = CodexElicitationRules.BuildResponse(Form(), new Dictionary<string, IReadOnlyList<string>>
        { ["count"] = [count], ["enabled"] = ["是"], ["tags"] = ["a"] });
        result.Error.Should().Contain(expected).And.NotContain("已婉拒");
    }

    [Fact]
    public void 網址確認只開HTTP網址_確認略過與取消有不同回應()
    {
        var p = JObject.Parse("""{"mode":"url","serverName":"測試","message":"登入","url":"https://example.test/login"}""");
        CodexElicitationRules.Parse(p).Url.Should().Be("https://example.test/login");
        var result = CodexElicitationRules.BuildResponse(p, new Dictionary<string, IReadOnlyList<string>> { ["__url"] = ["已完成"] });
        result.Response.Value<string>("action").Should().Be("accept");
        result.Response["content"].Should().BeNull();
        p["url"] = "file:///C:/secret";
        CodexElicitationRules.Parse(p).Unsupported.Should().NotBeNull();
    }

    [Fact]
    public void 診斷即使回非零仍呈現有效報告_格式不符不回顯輸出()
    {
        var report = CodexDoctorRules.Report(new CliRunResult(1,
            """{"schemaVersion":1,"overallStatus":"error","codexVersion":"0.167.0","checks":{"auth.test":{"status":"error","summary":"測試失敗","details":{"token":"<redacted>"}}}}""", "", false));
        report["checks"]!.Should().HaveCount(1);
        Action invalid = () => CodexDoctorRules.Report(new CliRunResult(1, "機密不應回顯", "不應回顯", false));
        invalid.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("機密");
    }

    [Fact]
    public void 計畫全文覆蓋串流_整輪差異保留輪次()
    {
        var parser = new CodexEventParser();
        parser.Parse("""{"method":"item/plan/delta","params":{"itemId":"p","delta":"草稿"}}""").Events
            .Should().Contain(new AgentCodexPlanEvent("p", "草稿", false));
        parser.Parse("""{"method":"item/completed","params":{"item":{"type":"plan","id":"p","text":"完成計畫"}}}""").Events
            .Should().Contain(new AgentCodexPlanEvent("p", "完成計畫", true));
        parser.Parse("""{"method":"turn/diff/updated","params":{"turnId":"t","diff":"差異"}}""").Events
            .Should().Contain(new AgentTurnDiffEvent("t", "差異"));
    }
}
