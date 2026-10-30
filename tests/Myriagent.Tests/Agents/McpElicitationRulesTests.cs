using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>MCP 的 elicitation 對應到問卷卡（請求的形狀取自 2.1.310 實錄，tools/probes/claude_cli3_probe.py --mode mcp）</summary>
public class McpElicitationRulesTests
{
    private const string FormRequest = """
        {"subtype":"elicitation","mcp_server_name":"good","message":"probe form: please fill in","mode":"form",
         "requested_schema":{"type":"object","properties":{
           "name":{"type":"string","title":"Name","description":"your name"},
           "agree":{"type":"boolean","title":"Agree"},
           "color":{"type":"string","enum":["red","blue"],"enumNames":["Red","Blue"]},
           "count":{"type":"integer","minimum":1,"maximum":5}},"required":["name"]},
         "property_order":["name","agree","color","count"]}
        """;

    private const string UrlRequest = """
        {"subtype":"elicitation","mcp_server_name":"good","message":"probe url: open the page","mode":"url","url":"https://example.test/authorize?x=1","elicitation_id":"el-1"}
        """;

    private static Dictionary<string, IReadOnlyList<string>> Answers(params (string Id, string[] Values)[] answers)
        => answers.ToDictionary(a => a.Id, a => (IReadOnlyList<string>)a.Values);

    [Fact]
    public void 表單_每個欄位一題_布林與列舉是選項_選填多一個不填()
    {
        var e = McpElicitationRules.Parse(JObject.Parse(FormRequest));
        e.Unsupported.Should().BeNull();
        e.Server.Should().Be("good");
        var questions = McpElicitationRules.ToQuestions(e);

        questions.Select(q => q.Id).Should().Equal("name", "agree", "color", "count");
        questions[0].Header.Should().Be("Name");
        questions[0].Text.Should().Be("your name");
        questions[0].Options.Should().BeEmpty("必填的文字欄位只能自行輸入");
        questions[0].AllowOther.Should().BeTrue();
        questions[1].Options.Select(o => o.Label).Should().Equal("是", "否", "不填");
        questions[1].AllowOther.Should().BeFalse();
        questions[1].Text.Should().Be("Agree（選填）");
        questions[2].Options.Select(o => o.Label).Should().Equal("Red", "Blue", "不填");
        questions[3].Text.Should().Be("count（整數，選填）");
        questions[3].Options.Select(o => o.Label).Should().Equal("不填");
        questions[3].AllowOther.Should().BeTrue();

        McpElicitationRules.Detail(e).Should().Be("MCP 伺服器「good」要你填寫：\n\nprobe form: please fill in");
    }

    [Fact]
    public void 表單的回覆_依型別轉成JSON值_選填的不填就省略()
    {
        var e = McpElicitationRules.Parse(JObject.Parse(FormRequest));
        var (response, error) = McpElicitationRules.BuildResponse(e, Answers(("name", ["探針"]), ("agree", ["是"]), ("color", ["Blue"]), ("count", [" 2 "])));
        error.Should().BeNull();
        response.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"action":"accept","content":{"name":"探針","agree":true,"color":"blue","count":2}}""");

        (response, error) = McpElicitationRules.BuildResponse(e, Answers(("name", ["只填名字"]), ("agree", ["不填"]), ("color", ["不填"]), ("count", ["不填"])));
        error.Should().BeNull();
        response.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"action":"accept","content":{"name":"只填名字"}}""");
    }

    [Fact]
    public void 表單的回覆_必填沒填或數字填了文字_婉拒並說明原因()
    {
        var e = McpElicitationRules.Parse(JObject.Parse(FormRequest));
        var (response, error) = McpElicitationRules.BuildResponse(e, Answers(("agree", ["否"])));
        response.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"action":"decline"}""");
        error.Should().Be("MCP 伺服器「good」的表單沒有送出：「Name」必填，已婉拒。");

        (response, error) = McpElicitationRules.BuildResponse(e, Answers(("name", ["a"]), ("count", ["三"])));
        response["action"]!.ToString().Should().Be("decline");
        error.Should().Contain("「count」要填整數");
    }

    [Fact]
    public void 網址模式_一題已完成或不要_網址列在說明裡()
    {
        var e = McpElicitationRules.Parse(JObject.Parse(UrlRequest));
        e.Unsupported.Should().BeNull();
        e.Url.Should().Be("https://example.test/authorize?x=1");
        var q = McpElicitationRules.ToQuestions(e).Single();
        q.Id.Should().Be(McpElicitationRules.UrlQuestionId);
        q.Options.Select(o => o.Label).Should().Equal("已完成", "不要");
        q.AllowOther.Should().BeFalse();
        McpElicitationRules.Detail(e).Should().Be("MCP 伺服器「good」要你開啟網址：\n\nprobe url: open the page\n\n<https://example.test/authorize?x=1>");

        McpElicitationRules.BuildResponse(e, Answers((McpElicitationRules.UrlQuestionId, ["已完成"]))).Response
            .ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"action":"accept"}""");
        McpElicitationRules.BuildResponse(e, Answers((McpElicitationRules.UrlQuestionId, ["不要"]))).Response["action"]!.ToString().Should().Be("decline");
    }

    [Theory]
    [InlineData("""{"mode":"url","url":"file:///C:/x"}""", "網址不是 http 或 https")]
    [InlineData("""{"mode":"url"}""", "網址不是 http 或 https")]
    [InlineData("""{"mode":"other"}""", "不認得的模式 other")]
    [InlineData("""{"mode":"form","requested_schema":{"properties":{"addr":{"type":"object","title":"地址"}}}}""", "欄位「地址」的型別（object）不支援")]
    public void 畫不出來的請求_整個婉拒(string request, string reason)
    {
        var e = McpElicitationRules.Parse(JObject.Parse(request));
        e.Unsupported.Should().Be(reason);
        McpElicitationRules.DescribeUnsupported(e).Should().EndWith($"但{reason}，已婉拒。");
    }

    [Fact]
    public void 有標題的列舉與多選_沒有欄位的表單只問同不同意()
    {
        var e = McpElicitationRules.Parse(JObject.Parse("""
            {"mode":"form","requested_schema":{"properties":{
              "size":{"type":"string","oneOf":[{"const":"s","title":"小"},{"const":"l","title":"大"}]},
              "tags":{"type":"array","items":{"anyOf":[{"const":"a","title":"甲"},{"const":"b","title":"乙"}]}},
              "ratio":{"type":["number","null"],"default":0.5}},"required":["size","tags","ratio"]}}
            """));
        e.Unsupported.Should().BeNull();
        var questions = McpElicitationRules.ToQuestions(e);
        questions[0].Options.Select(o => o.Label).Should().Equal("小", "大");
        questions[1].MultiSelect.Should().BeTrue();
        questions[2].Text.Should().Be("ratio（數字，預設：0.5）");
        var (response, error) = McpElicitationRules.BuildResponse(e, Answers(("size", ["大"]), ("tags", ["甲", "乙"]), ("ratio", ["1.25"])));
        error.Should().BeNull();
        response.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"action":"accept","content":{"size":"l","tags":["a","b"],"ratio":1.25}}""");

        var empty = McpElicitationRules.Parse(JObject.Parse("""{"mcp_server_name":"s","message":"可以嗎？","mode":"form","requested_schema":{"type":"object","properties":{}}}"""));
        McpElicitationRules.ToQuestions(empty).Single().Options.Select(o => o.Label).Should().Equal("同意", "拒絕");
        McpElicitationRules.BuildResponse(empty, Answers((McpElicitationRules.ConfirmQuestionId, ["同意"]))).Response
            .ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"action":"accept","content":{}}""");
    }

    [Fact]
    public void 停止與略過_回cancel與decline()
    {
        McpElicitationRules.Cancel().ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"action":"cancel"}""");
        McpElicitationRules.Decline().ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"action":"decline"}""");
    }
}
