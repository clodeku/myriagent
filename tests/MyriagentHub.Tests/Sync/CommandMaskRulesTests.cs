using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 遠端指令的遮蔽（WP4.5 計畫步 1；D29）：sync.db 的 <c>cmd_json</c> 與 <c>command.issued</c> 稽核只存遮蔽後的指令——
/// 送進代理的文字只留長度、問卷與上傳只留數量、其他字串經 DLP，不記任何文字的雜湊
/// </summary>
public sealed class CommandMaskRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly string Digest = "rd1:" + new string('0', 64);

    private static RemoteCommandSpec Spec(string name) => RemoteCommandCatalog.Find(name)!;

    [Fact]
    public void send的text只留長度_沒有雜湊()
    {
        var cmd = new JObject { ["cmd"] = "send", ["conversationId"] = Conv, ["text"] = "hello" };
        var masked = CommandMaskRules.Mask(cmd, Spec("send"));

        masked["text"]!.ToString(Formatting.None).Should().Be("""{"len":5}""");
        masked["cmd"]!.Value<string>().Should().Be("send");
        masked["conversationId"]!.Value<string>().Should().Be(Conv);
        var json = masked.ToString(Formatting.None);
        json.Should().NotContain("hello");
        foreach (var text in new[] { "hello", "\"hello\"" })
        {
            var sha = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            json.Should().NotContainEquivalentOf(Convert.ToHexString(sha), "不記文字的雜湊（短的提示用字典就能反推）");
            json.Should().NotContain(Convert.ToBase64String(sha));
        }

        CommandMaskRules.AuditMasked(masked).Should().NotContain("hello");
    }

    [Fact]
    public void approve的message遮蔽_choice與itemId與requestDigest保留()
    {
        var cmd = new JObject
        {
            ["cmd"] = "approve", ["conversationId"] = Conv, ["itemId"] = "item1", ["choice"] = "allow", ["message"] = "請改用另一個方案", ["requestDigest"] = Digest,
        };
        var masked = CommandMaskRules.Mask(cmd, Spec("approve"));

        masked["message"]!.ToString(Formatting.None).Should().Be("""{"len":8}""");
        masked["choice"]!.Value<string>().Should().Be("allow");
        masked["itemId"]!.Value<string>().Should().Be("item1");
        masked["requestDigest"]!.Value<string>().Should().Be(Digest);
        masked.Properties().Select(p => p.Name).Should().Equal(["cmd", "conversationId", "itemId", "choice", "message", "requestDigest"], "鍵的順序不變");
        masked.ToString(Formatting.None).Should().NotContain("方案");
    }

    [Fact]
    public void approve的message是null照樣null()
    {
        var cmd = new JObject { ["cmd"] = "approve", ["conversationId"] = Conv, ["itemId"] = "item1", ["choice"] = "deny", ["message"] = null, ["requestDigest"] = Digest };
        CommandMaskRules.Mask(cmd, Spec("approve"))["message"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void answer只留題數()
    {
        var cmd = new JObject
        {
            ["cmd"] = "answer", ["conversationId"] = Conv, ["itemId"] = "item1",
            ["answers"] = new JObject { ["要用哪個資料庫"] = new JArray("PostgreSQL"), ["秘密題"] = new JArray("我的生日", "我的地址") },
            ["requestDigest"] = Digest,
        };
        var masked = CommandMaskRules.Mask(cmd, Spec("answer"));

        masked["answers"]!.ToString(Formatting.None).Should().Be("""{"count":2}""");
        var json = masked.ToString(Formatting.None);
        foreach (var secret in new[] { "資料庫", "PostgreSQL", "秘密題", "生日", "地址" })
            json.Should().NotContain(secret, "題目與答案都不留");
        masked["requestDigest"]!.Value<string>().Should().Be(Digest);
    }

    [Fact]
    public void conversationCreate的text遮蔽_model保留_uploads只留數量()
    {
        var cmd = new JObject
        {
            ["cmd"] = RemoteCommandCatalog.ConversationCreate, ["accountId"] = "la_" + new string('X', 26), ["permissionMode"] = "Ask",
            ["model"] = "claude-sonnet-x", ["folder"] = @"D:\repo\demo", ["text"] = "第一則訊息",
            ["uploads"] = new JArray(Upload("u1", "合約.pdf"), Upload("u2", "截圖.png")),
        };
        var masked = CommandMaskRules.Mask(cmd, Spec(RemoteCommandCatalog.ConversationCreate));

        masked["text"]!.ToString(Formatting.None).Should().Be("""{"len":5}""");
        masked["uploads"]!.ToString(Formatting.None).Should().Be("""{"count":2}""");
        masked["model"]!.Value<string>().Should().Be("claude-sonnet-x");
        masked["permissionMode"]!.Value<string>().Should().Be("Ask");
        masked["folder"]!.Value<string>().Should().Be(@"D:\repo\demo", "一般字串經 DLP，沒有命中就原樣");
        var json = masked.ToString(Formatting.None);
        json.Should().NotContain("第一則").And.NotContain("合約").And.NotContain("截圖");
    }

    [Fact]
    public void 其他字串經CredentialLeakRules()
    {
        // 正例在執行時以字元組合產生（前綴拆開拼、本體用固定種子的亂數；契約 D44），repo 不放像真權杖的字串
        var random = new Random(20261020);
        var body = new string(Enumerable.Range(0, 36).Select(_ => Alnum[random.Next(Alnum.Length)]).ToArray());
        var token = "gh" + "p_" + body;
        var cmd = new JObject { ["cmd"] = "setFolder", ["conversationId"] = Conv, ["path"] = @"D:\repo\" + token };
        CredentialLeakRules.HasLeak(cmd["path"]!.Value<string>()).Should().BeTrue("正向對照：語料本身要被 DLP 認得");

        var masked = CommandMaskRules.Mask(cmd, Spec("setFolder"));
        var path = masked["path"]!.Value<string>()!;
        path.Should().StartWith(@"D:\repo\").And.Contain(CredentialLeakRules.Replacement);
        masked.ToString(Formatting.None).Should().NotContain(body);
        masked["conversationId"]!.Value<string>().Should().Be(Conv);
    }

    [Fact]
    public void 型錄沒有的鍵照一般字串遮蔽()
    {
        var random = new Random(7);
        var body = new string(Enumerable.Range(0, 36).Select(_ => Alnum[random.Next(Alnum.Length)]).ToArray());
        var cmd = new JObject { ["cmd"] = "rename", ["conversationId"] = Conv, ["title"] = "標題", ["extra"] = new JObject { ["note"] = "gh" + "o_" + body } };
        var masked = CommandMaskRules.Mask(cmd, Spec("rename"));
        masked["title"]!.Value<string>().Should().Be("標題", "上限 4096 的文字欄位不是代理文字，經 DLP 後保留");
        masked.ToString(Formatting.None).Should().NotContain(body);
    }

    [Fact]
    public void AuditMasked超過1000字改成truncated()
    {
        var empty = new JObject { ["cmd"] = "setFolder", ["path"] = "" }.ToString(Formatting.None).Length;
        var exact = new JObject { ["cmd"] = "setFolder", ["path"] = new string('x', CommandMaskRules.MaxAuditMaskedChars - empty) };
        CommandMaskRules.AuditMasked(exact).Should().Be(exact.ToString(Formatting.None), "剛好 1000 字照用");
        CommandMaskRules.AuditMasked(exact).Length.Should().Be(CommandMaskRules.MaxAuditMaskedChars);

        var over = new JObject { ["cmd"] = "setFolder", ["path"] = new string('x', CommandMaskRules.MaxAuditMaskedChars - empty + 1) };
        CommandMaskRules.AuditMasked(over).Should().Be("""{"truncated":true,"len":1001}""");
    }

    [Fact]
    public void 不改原物件()
    {
        var cmd = new JObject
        {
            ["cmd"] = "answer", ["conversationId"] = Conv, ["itemId"] = "item1", ["answers"] = new JObject { ["q1"] = new JArray("a") }, ["requestDigest"] = Digest,
        };
        var before = cmd.ToString(Formatting.None);
        var masked = CommandMaskRules.Mask(cmd, Spec("answer"));
        cmd.ToString(Formatting.None).Should().Be(before);

        masked["conversationId"] = "changed";
        cmd["conversationId"]!.Value<string>().Should().Be(Conv, "回傳的是複本");
    }

    private static JObject Upload(string id, string name)
        => new() { ["uploadId"] = id, ["name"] = name, ["size"] = 10, ["sha256"] = new string('a', 64) };
}
