using Myriagent.Remote;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 電腦的指令輸出轉給瀏覽器之前的投影（WP4.5 計畫步 2；D6、D20、6.4 C11）：只轉 toast、sendRejected、accountLogin（WP4.7 起加上
/// files、fileSearch、fileRef），每種只留已知的鍵；對話 id 對不上、驗證網址不在白名單、型別不對、超過上限都整則丟掉
/// </summary>
public sealed class CommandOutputRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private const string Cid = "01J9Q0000000000000000000AA";

    private const string DraftId = "d01J9Q0000000000000000000BB";

    private const string VerificationUrl = "https://auth.openai.com/codex/device";

    private static readonly string AccountX = "la_" + new string('X', 26);

    private static OutputContext SendContext => new("send", Conv, Conv, null, null);

    [Fact]
    public void sendRejected的conversationId相同就轉_換成瀏覽器的id()
    {
        var output = new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "原本的訊息" };
        CommandOutputRules.Project(output, SendContext)!.ToString(Formatting.None)
            .Should().Be($$"""{"type":"sendRejected","conversationId":"{{Conv}}","text":"原本的訊息"}""");
    }

    [Fact]
    public void sendRejected的conversationId不同就丟()
    {
        var other = "fedcba9876543210fedcba9876543210";
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = other, ["text"] = "x" }, SendContext).Should().BeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv.ToUpperInvariant(), ["text"] = "x" }, SendContext)
            .Should().BeNull("Ordinal 比對：WP4.4 轉出前已換成小寫的線上 id");
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["text"] = "x" }, SendContext).Should().BeNull("沒有 conversationId");
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "x" }, new OutputContext("send", null, null, null, null))
            .Should().BeNull("沒有預期的 id 就不收");
    }

    [Fact]
    public void answer的sendRejected以指令的conversationId核對()
    {
        var context = new OutputContext("answer", Conv, Conv, null, null);
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "回答" }, context).Should().NotBeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = "fedcba9876543210fedcba9876543210", ["text"] = "回答" }, context).Should().BeNull();
    }

    [Fact]
    public void 草稿的conversationId換成草稿id()
    {
        var expected = RemoteConversationIdRules.FromCommandId(Cid)!;
        var context = new OutputContext(RemoteCommandCatalog.ConversationCreate, expected, DraftId, null, null);
        var projected = CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = expected, ["text"] = "第一則" }, context)!;
        projected["conversationId"]!.Value<string>().Should().Be(DraftId);
        projected["text"]!.Value<string>().Should().Be("第一則");
    }

    [Fact]
    public void conversationCreate以推得的id核對()
    {
        var expected = RemoteConversationIdRules.FromCommandId(Cid)!;
        var context = new OutputContext(RemoteCommandCatalog.ConversationCreate, expected, DraftId, null, null);
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = DraftId, ["text"] = "x" }, context)
            .Should().BeNull("電腦不知道草稿 id，填的一定是推得的 id");
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "x" }, context).Should().BeNull();
    }

    [Fact]
    public void sendRejected的text上限16000()
    {
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = new string('字', 16_000) }, SendContext).Should().NotBeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = new string('字', 16_001) }, SendContext).Should().BeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = 1 }, SendContext).Should().BeNull("型別不對");
    }

    [Theory]
    [InlineData("info")]
    [InlineData("error")]
    [InlineData("success")]
    public void toast照轉(string level)
    {
        CommandOutputRules.Project(new JObject { ["type"] = "toast", ["text"] = "工作資料夾不存在", ["level"] = level }, SendContext)!.ToString(Formatting.None)
            .Should().Be($$"""{"type":"toast","text":"工作資料夾不存在","level":"{{level}}"}""");
    }

    [Theory]
    [InlineData("warning")]
    [InlineData("Error")]
    [InlineData("")]
    public void toast的level不合格就丟(string level)
        => CommandOutputRules.Project(new JObject { ["type"] = "toast", ["text"] = "x", ["level"] = level }, SendContext).Should().BeNull();

    [Fact]
    public void toast的文字要1到1000字()
    {
        CommandOutputRules.Project(new JObject { ["type"] = "toast", ["text"] = new string('字', 1000), ["level"] = "error" }, SendContext).Should().NotBeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "toast", ["text"] = new string('字', 1001), ["level"] = "error" }, SendContext).Should().BeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "toast", ["text"] = "", ["level"] = "error" }, SendContext).Should().BeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "toast", ["level"] = "error" }, SendContext).Should().BeNull();
    }

    [Fact]
    public void 多的鍵拿掉()
    {
        var toast = new JObject { ["type"] = "toast", ["text"] = "x", ["level"] = "info", ["html"] = "<b>x</b>", ["conversationId"] = Conv };
        CommandOutputRules.Project(toast, SendContext)!.Properties().Select(p => p.Name).Should().Equal(["type", "text", "level"]);
        var rejected = new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "x", ["attachments"] = new JArray("a") };
        CommandOutputRules.Project(rejected, SendContext)!.Properties().Select(p => p.Name).Should().Equal(["type", "conversationId", "text"]);
        var login = Login("a1", deviceCode: true);
        login["loginId"] = "secret";
        ((JObject)login["deviceCode"]!)["loginId"] = "secret";
        var projected = CommandOutputRules.Project(login, new OutputContext("accountLogin", null, null, null, AccountX))!;
        projected.Properties().Select(p => p.Name).Should().Equal(["type", "accountId", "message", "done", "success", "deviceCode"]);
        ((JObject)projected["deviceCode"]!).Properties().Select(p => p.Name).Should().Equal(["userCode", "verificationUrl", "expiresAt"]);
        projected.ToString(Formatting.None).Should().NotContain("secret");
    }

    [Fact]
    public void 型錄不允許的種類丟掉()
    {
        CommandOutputRules.Project(Login("a1", deviceCode: false), SendContext).Should().BeNull("send 不能回 accountLogin");
        CommandOutputRules.Project(new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "x" }, new OutputContext("stop", Conv, Conv, null, null))
            .Should().BeNull("stop 只能回 toast");
        CommandOutputRules.Project(new JObject { ["type"] = "item", ["item"] = new JObject() }, SendContext).Should().BeNull();
        CommandOutputRules.Project(new JObject { ["text"] = "沒有 type", ["level"] = "info" }, SendContext).Should().BeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "toast", ["text"] = "x", ["level"] = "info" }, new OutputContext("newConversation", null, null, null, null))
            .Should().BeNull("不在型錄的指令一律不行");
    }

    [Fact]
    public void accountLogin的accountId換成瀏覽器的_deviceCode保留()
    {
        var context = new OutputContext("accountLogin", null, null, null, AccountX);
        var projected = CommandOutputRules.Project(Login("a1", deviceCode: true), context)!;
        projected["accountId"]!.Value<string>().Should().Be(AccountX, "信封裡是那台的本機 id，瀏覽器看到的是 la_");
        projected["done"]!.Value<bool>().Should().BeFalse();
        projected["success"]!.Type.Should().Be(JTokenType.Null);
        projected["deviceCode"]!.ToString(Formatting.None)
            .Should().Be($$"""{"userCode":"ABCD-EFGH2","verificationUrl":"{{VerificationUrl}}","expiresAt":1792000900000}""");

        var device = new OutputContext("accountLoginDevice", null, null, null, AccountX);
        CommandOutputRules.Project(Login("a1", deviceCode: true), device)!["accountId"]!.Value<string>().Should().Be(AccountX);
    }

    [Fact]
    public void accountAdd沒有logicalAccountId時accountId原樣()
    {
        var context = new OutputContext("accountAdd", null, null, null, null);
        CommandOutputRules.Project(Login("new1", deviceCode: false), context)!["accountId"]!.Value<string>().Should().Be("new1");
    }

    [Fact]
    public void accountLogin的done與success()
    {
        var context = new OutputContext("accountLogin", null, null, null, AccountX);
        var done = Login("a1", deviceCode: false);
        done["done"] = true;
        done["success"] = true;
        var projected = CommandOutputRules.Project(done, context)!;
        projected["done"]!.Value<bool>().Should().BeTrue();
        projected["success"]!.Value<bool>().Should().BeTrue();
        projected.ContainsKey("deviceCode").Should().BeFalse();

        var missing = Login("a1", deviceCode: false);
        missing.Remove("success");
        CommandOutputRules.Project(missing, context)!["success"]!.Type.Should().Be(JTokenType.Null, "沒帶當成 null");

        var nullCode = Login("a1", deviceCode: false);
        nullCode["deviceCode"] = null;
        CommandOutputRules.Project(nullCode, context)!.ContainsKey("deviceCode").Should().BeFalse();
    }

    [Fact]
    public void accountLogin型別不對或超過上限就丟()
    {
        var context = new OutputContext("accountLogin", null, null, null, AccountX);
        var cases = new List<Action<JObject>>
        {
            m => m["done"] = "false",
            m => m.Remove("done"),
            m => m["success"] = "yes",
            m => m["accountId"] = 1,
            m => m["accountId"] = "",
            m => m["message"] = new string('字', 2001),
            m => m.Remove("message"),
            m => m["deviceCode"] = "ABCD-EFGH2",
            m => ((JObject)m["deviceCode"]!)["userCode"] = new string('A', 33),
            m => ((JObject)m["deviceCode"]!)["userCode"] = "",
            m => ((JObject)m["deviceCode"]!)["expiresAt"] = "1792000900000",
            m => ((JObject)m["deviceCode"]!).Remove("verificationUrl"),
        };
        CommandOutputRules.Project(Login("a1", deviceCode: true), context).Should().NotBeNull("正向對照");
        for (var i = 0; i < cases.Count; i++)
        {
            var message = Login("a1", deviceCode: true);
            cases[i](message);
            CommandOutputRules.Project(message, context).Should().BeNull($"第 {i} 例");
        }

        var limit = Login("a1", deviceCode: true);
        limit["message"] = new string('字', 2000);
        ((JObject)limit["deviceCode"]!)["userCode"] = new string('A', 32);
        CommandOutputRules.Project(limit, context).Should().NotBeNull("剛好在上限");
    }

    [Theory]
    [InlineData("https://auth.openai.com.evil.test/codex/device")]
    [InlineData("http://auth.openai.com/codex/device")]
    [InlineData("https://user@auth.openai.com/codex/device")]
    [InlineData("https://evil.test/device")]
    public void 驗證網址不在白名單_整則丟掉(string url)
    {
        var message = Login("a1", deviceCode: true);
        ((JObject)message["deviceCode"]!)["verificationUrl"] = url;
        CommandOutputRules.Project(message, new OutputContext("accountLogin", null, null, null, AccountX)).Should().BeNull("訊息文字裡也有網址，整則丟掉");
    }

    [Fact]
    public void 檔案輸出交給CommandOutputFileRules_合格的轉_沒有conversationId的丟()
    {
        // WP4.7 合併前一律丟；合併後（WP4.7 計畫步 13）由 CommandOutputFileRules 投影，細節見 MyriagentHub.Tests.Media.CommandOutputFileRulesTests
        RemoteCommandCatalog.AllowsOutput("listFiles", "files").Should().BeTrue();
        RemoteCommandCatalog.AllowsOutput("fileSearch", "fileSearch").Should().BeTrue();
        RemoteCommandCatalog.AllowsOutput("fileFetch", "fileRef").Should().BeTrue();
        var files = new JObject { ["type"] = "files", ["requestId"] = "r1", ["conversationId"] = Conv, ["entries"] = new JArray() };
        CommandOutputRules.Project(files, new OutputContext("listFiles", Conv, Conv, "r1", null))!.ToString(Formatting.None)
            .Should().Be($$"""{"type":"files","requestId":"r1","conversationId":"{{Conv}}","path":null,"parent":null,"root":null,"entries":[],"truncated":false,"error":null}""");
        CommandOutputRules.Project(new JObject { ["type"] = "fileSearch", ["requestId"] = "r1", ["conversationId"] = Conv, ["query"] = "a", ["results"] = new JArray() },
            new OutputContext("fileSearch", Conv, Conv, "r1", null)).Should().NotBeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "fileRef", ["requestId"] = "r1", ["conversationId"] = Conv, ["error"] = "找不到檔案" },
            new OutputContext("fileFetch", Conv, Conv, "r1", null)).Should().NotBeNull();

        CommandOutputRules.Project(new JObject { ["type"] = "files", ["requestId"] = "r1", ["entries"] = new JArray() }, new OutputContext("listFiles", Conv, Conv, "r1", null))
            .Should().BeNull("沒有 conversationId");
        CommandOutputRules.Project(new JObject { ["type"] = "fileSearch", ["requestId"] = "r1", ["results"] = new JArray() }, new OutputContext("fileSearch", Conv, Conv, "r1", null))
            .Should().BeNull();
        CommandOutputRules.Project(new JObject { ["type"] = "fileRef", ["requestId"] = "r1" }, new OutputContext("fileFetch", Conv, Conv, "r1", null)).Should().BeNull();
        CommandOutputRules.Project(files, new OutputContext("fileSearch", Conv, Conv, "r1", null)).Should().BeNull("files 只回 listFiles");
    }

    [Fact]
    public void Claude的登入網址_claude網域的https才轉()
    {
        const string Url = "https://claude.com/cai/oauth/authorize?code=true&state=s1";
        var message = Login("a1", deviceCode: false);
        message["loginUrl"] = Url;
        var projected = CommandOutputRules.Project(message, new OutputContext("accountLoginDevice", null, null, null, AccountX))!;
        projected.Value<string>("loginUrl").Should().Be(Url);
        projected.Value<string>("accountId").Should().Be(AccountX);

        foreach (var bad in new JToken[] { "https://claude.com.evil.test/x", "http://claude.com/x", "https://user@claude.ai/x", "https://platform.claude.com/x", 42,
            "https://claude.com/" + new string('x', CommandOutputRules.MaxLoginUrlLength) })
        {
            var withBad = Login("a1", deviceCode: false);
            withBad["loginUrl"] = bad;
            CommandOutputRules.Project(withBad, new OutputContext("accountLoginDevice", null, null, null, AccountX)).Should().BeNull($"{bad} 整則丟掉");
        }

        var nullUrl = Login("a1", deviceCode: false);
        nullUrl["loginUrl"] = JValue.CreateNull();
        CommandOutputRules.Project(nullUrl, new OutputContext("accountLoginDevice", null, null, null, AccountX))!["loginUrl"].Should().BeNull("沒有網址就不帶這個鍵");
    }

    [Fact]
    public void 不改原物件()
    {
        var message = Login("a1", deviceCode: true);
        var before = message.ToString(Formatting.None);
        var projected = CommandOutputRules.Project(message, new OutputContext("accountLogin", null, null, null, AccountX))!;
        ((JObject)projected["deviceCode"]!)["userCode"] = "changed";
        message.ToString(Formatting.None).Should().Be(before);
    }

    private static JObject Login(string accountId, bool deviceCode)
    {
        var message = new JObject
        {
            ["type"] = "accountLogin",
            ["accountId"] = accountId,
            ["message"] = $"請在手機或其他電腦打開 {VerificationUrl}，輸入驗證碼 ABCD-EFGH2（15 分鐘內有效）",
            ["done"] = false,
            ["success"] = null,
        };
        if (deviceCode)
            message["deviceCode"] = new JObject { ["userCode"] = "ABCD-EFGH2", ["verificationUrl"] = VerificationUrl, ["expiresAt"] = 1_792_000_900_000L };
        return message;
    }
}
