using FakeDevice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// 假裝置控制通道的純函式規則（WP4.9 計畫步 11）：一行一個 JSON 指令的解析、每個 op 只收認得的參數、回覆的格式，
/// 回收區一定在資料根底下、XSS 語料檔只收 repo 的 Golden 與 TEMP、放 XSS 檔名的資料夾在 TEMP 底下且在資料根以外，以及 Windows 合法的檔名
/// </summary>
public sealed class FakeControlRulesTests
{
    private const string ConversationId = "0123456789abcdef0123456789abcdef";

    private static readonly string Temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fd-control-rules"));

    private static readonly string Golden = Path.GetFullPath(@"C:\repo\x\tests\MyriagentHub.Tests\Rendering\Golden");

    private static readonly IReadOnlyList<string> Roots = [Temp, Golden];

    private static FakeControlParse Parse(string line) => FakeControlRules.Parse(line);

    [Fact]
    public void 未知的op回錯誤_id照樣帶回()
    {
        var parsed = Parse("""{"id":3,"op":"bogus"}""");
        parsed.Command.Should().BeNull();
        parsed.Id!.Value<int>().Should().Be(3);
        parsed.Op.Should().Be("bogus");
        parsed.Error.Should().Contain("不認得的 op").And.Contain("restoreFromTrash");

        var reply = FakeControlRules.Fail(parsed.Id, parsed.Op, "原因");
        reply.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"id":3,"op":"bogus","ok":false,"error":"原因"}""");
        FakeControlRules.Fail(null, null, "x").ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"id":null,"op":null,"ok":false,"error":"x"}""");
        FakeControlRules.Ok("a", "status", new JObject { ["x"] = 1 }).ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("""{"id":"a","op":"status","ok":true,"result":{"x":1}}""");
        FakeControlRules.Ok(null, "quit", new JObject()).ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"id":null,"op":"quit","ok":true,"result":{}}""");
    }

    [Theory]
    [InlineData("", "空白")]
    [InlineData("   ", "空白")]
    [InlineData("not json", "不是 JSON")]
    [InlineData("[1,2]", "JSON 物件")]
    [InlineData("""{"op":"status"} {"op":"quit"}""", "不是 JSON")]
    [InlineData("""{"id":{"a":1},"op":"status"}""", "id 要是整數或字串")]
    [InlineData("""{"id":1}""", "缺少 op")]
    [InlineData("""{"id":1,"op":""}""", "缺少 op")]
    [InlineData("""{"op":7}""", "缺少 op")]
    [InlineData("""{"op":"status","extra":1}""", "不認得的參數：extra")]
    [InlineData("""{"op":"quit","now":true}""", "不收參數")]
    public void 不合格的一行(string line, string error)
    {
        var parsed = Parse(line);
        parsed.Command.Should().BeNull();
        parsed.Error.Should().Contain(error);
    }

    [Fact]
    public void 合格的指令_參數不含id與op()
    {
        var parsed = Parse("""{"id":"s1","op":"stream","conversation":"工作","segments":4,"delayMs":0,"stamp":true,"completeEachSegment":false,"wait":true}""");
        parsed.Error.Should().BeNull();
        parsed.Command!.Op.Should().Be("stream");
        parsed.Command.Id!.Value<string>().Should().Be("s1");
        parsed.Command.Args.Properties().Select(p => p.Name).Should().BeEquivalentTo("conversation", "segments", "delayMs", "stamp", "completeEachSegment", "wait");
        Parse("""{"op":"status"}""").Command!.Id.Should().BeNull();
        Parse("""{"op":"quit","id":null}""").Command.Should().NotBeNull();
        foreach (var op in FakeControlRules.Ops)
            (FakeControlRules.Validate(op, new JObject()) ?? string.Empty).Should().NotContain("不認得的 op", $"{op} 是認得的 op");
    }

    [Theory]
    [InlineData("""{"op":"stream"}""", "缺少 conversation")]
    [InlineData("""{"op":"stream","conversation":""}""", "conversation 要是非空字串")]
    [InlineData("""{"op":"stream","conversation":"a","segments":0}""", "segments 要是 1～1000")]
    [InlineData("""{"op":"stream","conversation":"a","segments":1001}""", "segments 要是 1～1000")]
    [InlineData("""{"op":"stream","conversation":"a","segments":99999999999999999999}""", "segments 要是 1～1000")]
    [InlineData("""{"op":"stream","conversation":"a","delayMs":60001}""", "delayMs")]
    [InlineData("""{"op":"stream","conversation":"a","stamp":"yes"}""", "stamp 要是 true 或 false")]
    [InlineData("""{"op":"stream","conversation":"a","seconds":0}""", "seconds")]
    [InlineData("""{"op":"nextRemoteTurn"}""", "turn 要是劇本")]
    [InlineData("""{"op":"nextRemoteTurn","turn":[{"nothing":1}]}""", "turn：script 第 1 步")]
    [InlineData("""{"op":"setTitle","conversation":"a"}""", "缺少 title")]
    [InlineData("""{"op":"setPolicy","level":"Full"}""", "level 要是 Off、View、Control")]
    [InlineData("""{"op":"setPolicy","level":"View","notify":1}""", "notify 要是 true 或 false")]
    [InlineData("""{"op":"ords"}""", "缺少 conversation")]
    [InlineData("""{"op":"remoteAudit","limit":0}""", "limit 要是 1～500")]
    [InlineData("""{"op":"remoteAudit","limit":501}""", "limit 要是 1～500")]
    [InlineData("""{"op":"remoteAudit","beforeAt":-1}""", "beforeAt 要是非負")]
    [InlineData("""{"op":"remoteAudit","beforeSeq":5}""", "beforeSeq 要和 beforeAt 一起給")]
    [InlineData("""{"op":"restoreFromTrash"}""", "缺少 conversation")]
    [InlineData("""{"op":"restoreFromTrash","conversation":"..\\x"}""", "32 個十六進位字元")]
    [InlineData("""{"op":"restoreFromTrash","conversation":"0123456789abcdef0123456789abcde"}""", "32 個十六進位字元")]
    [InlineData("""{"op":"xssFill","fields":["text"]}""", "缺少 payloadFile")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":[]}""", "fields 要是非空的字串陣列")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":["text","text"]}""", "fields 不能重複")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":["html"]}""", "fields 不認得 html")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":["title"]}""", "要給 account")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":["accountLabel"]}""", "要給 account")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":["fileNames"]}""", "fileNames 要給 folder")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":["text"],"count":0}""", "count 要是 1～10000")]
    [InlineData("""{"op":"localCommand"}""", "缺少 cmd")]
    [InlineData("""{"op":"localCommand","cmd":"delete","args":{"conversationId":"0123456789abcdef0123456789abcdef"}}""", "cmd 不收 delete")]
    [InlineData("""{"op":"localCommand","cmd":"syncUnpair"}""", "cmd 不收 syncUnpair")]
    [InlineData("""{"op":"localCommand","cmd":"rename","args":"x"}""", "args 要是物件")]
    [InlineData("""{"op":"localCommand","cmd":"rename","args":{"cmd":"delete"}}""", "args 不能有 cmd")]
    [InlineData("""{"op":"localCommand","cmd":"rename","conversationId":"a"}""", "不認得的參數：conversationId")]
    [InlineData("""{"op":"replicas","conversation":"..\\x"}""", "32 個十六進位字元")]
    [InlineData("""{"op":"replicas","open":true}""", "不認得的參數：open")]
    [InlineData("""{"op":"unpair","timeout":5}""", "不認得的參數：timeout")]
    public void 參數不合格(string line, string error)
    {
        var parsed = Parse(line);
        parsed.Command.Should().BeNull();
        parsed.Error.Should().Contain(error);
    }

    [Theory]
    [InlineData("""{"op":"setPolicy","level":"Control","fullAccess":false,"notify":true}""")]
    [InlineData("""{"op":"remoteAudit","beforeAt":1760000000000,"beforeSeq":3,"limit":500}""")]
    [InlineData("""{"op":"restoreFromTrash","conversation":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"op":"nextRemoteTurn","turn":{"steps":[{"text":"好","segments":2}],"outcome":"completed"}}""")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":["text","tool","question","approval","toast","notifyTitle","fileRefError","deviceName"]}""")]
    [InlineData("""{"op":"xssFill","payloadFile":"C:\\t\\x.json","fields":["title","accountLabel","fileNames"],"account":"a","folder":"C:\\t\\f","from":3,"count":10}""")]
    [InlineData("""{"op":"localCommand","cmd":"syncPause","args":{"paused":true}}""")]
    [InlineData("""{"op":"localCommand","cmd":"syncSetConversationPolicy","args":{"conversationId":"0123456789abcdef0123456789abcdef","policy":"metadata"}}""")]
    [InlineData("""{"op":"localCommand","cmd":"open"}""")]
    [InlineData("""{"op":"replicas"}""")]
    [InlineData("""{"op":"replicas","conversation":"0123456789ABCDEF0123456789abcdef"}""")]
    [InlineData("""{"op":"unpair"}""")]
    public void 參數合格(string line) => Parse(line).Error.Should().BeNull();

    [Fact]
    public void localCommand只收全流程用得到的本機指令_都是中樞認得的()
    {
        FakeControlRules.LocalCommands.Should().Equal("open", "close", "rename", "send", "syncSetConversationPolicy", "syncPause");
        // 會刪檔、解除配對、改帳戶或跑 CLI 的指令不收（假裝置的控制通道不是任意的中樞指令入口）
        foreach (var cmd in new[] { "delete", "syncUnpair", "syncPair", "accountLogin", "cliUpdate", "setFolder", "restoreFromTrash" })
            FakeControlRules.Validate("localCommand", new JObject { ["cmd"] = cmd }).Should().Contain("cmd 不收", cmd);
    }

    // ───────────────────────────── 路徑 ─────────────────────────────

    [Fact]
    public void restoreFromTrash_回收區在資料根以外被拒()
    {
        var data = Path.Combine(Temp, "root");
        FakeControlRules.CheckTrashRoot(Path.Combine(data, "conversations-trash"), data).Should().BeNull();
        FakeControlRules.CheckTrashRoot(Path.Combine(data, "a", "b"), data + Path.DirectorySeparatorChar).Should().BeNull("結尾斜線不影響");
        FakeControlRules.CheckTrashRoot(Path.Combine(Temp, "other", "conversations-trash"), data).Should().Contain("不在資料根");
        FakeControlRules.CheckTrashRoot(Path.Combine(data, "..", "conversations-trash"), data).Should().Contain("不在資料根", "..走出去");
        FakeControlRules.CheckTrashRoot(data + "X" + Path.DirectorySeparatorChar + "conversations-trash", data).Should().Contain("不在資料根", "前綴相同的別的資料夾");
        FakeControlRules.CheckTrashRoot(data, data).Should().Contain("不在資料根", "資料根本身不算底下");
        FakeControlRules.CheckTrashRoot(@"\\server\share\conversations-trash", data).Should().Contain("UNC");
        FakeControlRules.CheckTrashRoot("conversations-trash", data).Should().Contain("完整路徑");
    }

    [Fact]
    public void payloadFile_不在repo的Golden或TEMP底下被拒()
    {
        FakeControlRules.CheckPayloadFile(Path.Combine(Golden, "e2e-fixtures.json"), Roots).Should().BeNull();
        FakeControlRules.CheckPayloadFile(Path.Combine(Temp, "x", "payloads.JSON"), Roots).Should().BeNull("副檔名不分大小寫");
        FakeControlRules.CheckPayloadFile(@"C:\Users\someone\secret.json", Roots).Should().Contain("Golden 資料夾或 TEMP");
        FakeControlRules.CheckPayloadFile(Path.Combine(Temp, "..", "x.json"), Roots).Should().Contain("Golden 資料夾或 TEMP", "..走出去");
        FakeControlRules.CheckPayloadFile(Golden + "2" + Path.DirectorySeparatorChar + "x.json", Roots).Should().Contain("Golden 資料夾或 TEMP", "前綴相同的別的資料夾");
        FakeControlRules.CheckPayloadFile(Path.Combine(Temp, "x.txt"), Roots).Should().Contain("只收 .json");
        FakeControlRules.CheckPayloadFile(@"\\server\share\x.json", Roots).Should().Contain("UNC");
        FakeControlRules.CheckPayloadFile(@"\\?\C:\t\x.json", Roots).Should().Contain("UNC");
        FakeControlRules.CheckPayloadFile(@"//server/share/x.json", Roots).Should().Contain("UNC");
        FakeControlRules.CheckPayloadFile(@"x.json", Roots).Should().Contain("完整路徑");
        FakeControlRules.CheckPayloadFile(@"C:x.json", Roots).Should().Contain("完整路徑");
        FakeControlRules.CheckPayloadFile(Path.Combine(Temp, "x.json") + ":evil", Roots).Should().Contain("替代資料流");
        FakeControlRules.CheckPayloadFile(Path.Combine(Temp, "*.json"), Roots).Should().Contain("不合法的字元");
        FakeControlRules.CheckPayloadFile("", Roots).Should().Contain("空的");
    }

    [Fact]
    public void XSS檔名的資料夾_在TEMP底下而且在資料根以外()
    {
        var data = Path.Combine(Temp, "device");
        FakeControlRules.CheckXssFolder(Path.Combine(Temp, "xss-files"), Temp, data).Should().BeNull();
        FakeControlRules.CheckXssFolder(Path.Combine(data, "xss-files"), Temp, data).Should().Contain("不能在資料根");
        FakeControlRules.CheckXssFolder(data, Temp, data).Should().Contain("不能在資料根");
        FakeControlRules.CheckXssFolder(@"C:\repo\xss-files", Temp, data).Should().Contain("要在 TEMP");
        FakeControlRules.CheckXssFolder(@"\\server\x", Temp, data).Should().Contain("UNC");
    }

    // ───────────────────────────── 檔名、串流文字、XSS 劇本 ─────────────────────────────

    [Theory]
    [InlineData("<img src=x onerror=alert(1)>", 0, "000-_img src=x onerror=alert(1)_.txt")]
    [InlineData("a/b\\c:d*e?f\"g|h", 7, "007-a_b_c_d_e_f_g_h.txt")]
    [InlineData("   ", 12, "012-_.txt")]
    [InlineData("CON", 1, "001-CON.txt")]
    public void 檔名只留Windows合法的字元_序號前綴(string payload, int index, string expected) =>
        FakeControlRules.SafeFileName(payload, index).Should().Be(expected);

    [Fact]
    public void 檔名_控制字元與落單的代理字元換掉_代理字對不切開_長度有上限()
    {
        var invalid = Path.GetInvalidFileNameChars();
        // 控制字元與落單的代理字元以碼位組出來（原始碼裡不放看不見的字元）
        var tricky = "a" + (char)0x01 + "b" + (char)0x1f + "c" + (char)0x7f + "d" + (char)0xd83d + "e" + (char)0xde00 + "🙂";
        var name = FakeControlRules.SafeFileName(tricky, 3);
        name.Should().Be("003-a_b_c_d_e_🙂.txt");
        name.IndexOfAny(invalid).Should().Be(-1);

        var longName = FakeControlRules.SafeFileName(new string('x', FakeControlRules.MaxFileNameStem - 1) + "🙂🙂", 5);
        longName.Should().Be("005-" + new string('x', FakeControlRules.MaxFileNameStem - 1) + ".txt", "放不下整個代理字對就不放");
        FakeControlRules.SafeFileName(new string('y', 500), 999).Should().HaveLength(4 + FakeControlRules.MaxFileNameStem + 4);
        FakeControlRules.SafeFileName("x", 1).Should().NotBe(FakeControlRules.SafeFileName("x", 2));
        FluentActions.Invoking(() => FakeControlRules.SafeFileName("x", -1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 預設的串流文字_每段剛好一句()
    {
        var text = FakeControlRules.StreamText(12);
        var parts = ScriptedSession.Split(text, 12);
        parts.Should().HaveCount(12);
        parts[0].Should().Be("第0001段。");
        parts[11].Should().Be("第0012段。");
        Myriagent.Sync.CredentialLeakRules.Mask(text).Should().Be(text, "DLP 不改它");
        FakeControlRules.StreamSeconds(1000, 250).Should().Be(530);
        FakeControlRules.StreamSeconds(1, 0).Should().Be(30);
        FakeControlRules.StreamSeconds(1000, 60_000).Should().Be(3600);
    }

    [Fact]
    public void XSS卡片的劇本_每筆依序放要的欄位_問卷答它自己_核准拒絕()
    {
        var turn = FakeControlRules.XssTurn(["<a>", "<b>"], 10, ["question", "text", "approval"]);
        turn.Steps.Select(s => s.GetType().Name).Should().Equal(
            nameof(ScriptedText), nameof(ScriptedQuestion), nameof(ScriptedApproval),
            nameof(ScriptedText), nameof(ScriptedQuestion), nameof(ScriptedApproval));
        var question = (ScriptedQuestion)turn.Steps[4];
        question.Questions.Single().Should().Match<Myriagent.Agents.AgentQuestion>(q => q.Id == "x11" && q.Text == "<b>" && q.Options.Single().Label == "<b>");
        question.Answer!["x11"].Should().Equal("<b>");
        ((ScriptedApproval)turn.Steps[2]).Should().Match<ScriptedApproval>(a => a.Title == "<a>" && a.Detail == "<a>" && a.ToolName == "<a>" && a.Decision == "deny");
        var tool = (ScriptedTool)FakeControlRules.XssTurn(["<t>"], 0, ["tool"]).Steps.Single();
        tool.Should().Match<ScriptedTool>(t => t.Name == "<t>" && t.Title == "<t>" && t.Detail == "<t>" && t.Output == "<t>" && !t.Failed);
        FluentActions.Invoking(() => FakeControlRules.XssTurn(["<a>"], 0, ["title"])).Should().Throw<ArgumentException>();
    }
}
