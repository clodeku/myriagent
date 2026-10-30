using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// WP4.8 步 10（D32；審查 #23）：桌面送出的遠端登入輸出（accountLogin，遠端登入開始時唯一的一則）一定通過伺服器的投影。
/// 樣本是桌面測試（Myriagent.Tests 的 RemoteAccountOutputContractTests）斷言「和桌面的輸出逐項相同、同形狀」的那一份，
/// 這裡從 repo 讀同一個檔（只讀、不改伺服器），以轉送時的同一條路（<c>RemoteResultRules.CheckOutput</c> 再 <c>CommandOutputRules.Project</c>）
/// 處理，斷言整則留下、只有 accountId 換成瀏覽器的 id
/// </summary>
public sealed class RemoteAccountOutputContractTests
{
    private const string CodeShown = "wp48-accountLogin-codeShown.json";

    private const string ConsoleOpened = "wp48-accountLogin-consoleOpened.json";

    private const string UrlShown = "wp48-accountLogin-urlShown.json";

    private const string Cid = "01J9Q0000000000000000000AA";

    private const string DeviceId = "d_" + "0123456789ABCDEFGHJKMNPQRS";

    private static readonly string LogicalAccountId = "la_" + new string('X', 26);

    private static string SamplesDirectory => RepoFiles.Combine("tests", "Myriagent.Tests", "Hub", "Samples");

    private static JObject Sample(string name) => JObject.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, name)));

    /// <summary>
    /// 瀏覽器送的指令與伺服器記下的投影脈絡（照 CommandRouter：BrowserAccountId 由 <c>CommandRouteRules.BrowserAccountId</c> 從換算之前的指令取）。
    /// 網頁一律送 accountLoginDevice（帶 logicalAccountId）；accountLogin 是帶 la_ 的 accountId
    /// </summary>
    private static OutputContext ContextOf(string command)
    {
        var browserCmd = command switch
        {
            "accountLoginDevice" => new JObject { ["cmd"] = command, ["logicalAccountId"] = LogicalAccountId, ["deviceId"] = DeviceId, ["method"] = "deviceCode" },
            "accountLogin" => new JObject { ["cmd"] = command, ["accountId"] = LogicalAccountId, ["deviceId"] = DeviceId },
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
        var spec = RemoteCommandCatalog.Find(command) ?? throw new InvalidOperationException("型錄沒有 " + command);
        return new OutputContext(command, null, null, null, CommandRouteRules.BrowserAccountId(spec, browserCmd));
    }

    /// <summary>轉送時的同一條路：型錄白名單（RemoteResultRules.CheckOutput）過了才投影；null＝整則丟掉</summary>
    private static JObject? Forward(JObject message, string command)
        => RemoteResultRules.CheckOutput(new CommandOutput(Cid, message), command) is null ? CommandOutputRules.Project(message, ContextOf(command)) : null;

    [Fact]
    public void 樣本恰好是這兩份()
        => Directory.GetFiles(SamplesDirectory, "wp48-*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .Should().Equal([CodeShown, ConsoleOpened, UrlShown], "新增樣本時這裡的 Theory 也要涵蓋");

    [Theory]
    [InlineData(CodeShown, "accountLoginDevice")]
    [InlineData(CodeShown, "accountLogin")]
    [InlineData(ConsoleOpened, "accountLoginDevice")]
    [InlineData(ConsoleOpened, "accountLogin")]
    [InlineData(UrlShown, "accountLoginDevice")]
    [InlineData(UrlShown, "accountLogin")]
    public void 樣本經CommandOutputRules_Project整則留下(string name, string command)
    {
        var sample = Sample(name);
        ContextOf(command).BrowserAccountId.Should().Be(LogicalAccountId);

        var projected = Forward(sample, command);

        projected.Should().NotBeNull($"{name} 經 {command} 的投影不能整則丟掉");
        var expected = (JObject)sample.DeepClone();
        expected["accountId"] = LogicalAccountId;
        JToken.DeepEquals(projected, expected).Should().BeTrue($"整則留下、只有 accountId 換成瀏覽器的 id：{projected!.ToString(Formatting.None)}");
        projected.Properties().Select(p => p.Name).Should().Equal(sample.Properties().Select(p => p.Name), "鍵都留下（順序同樣本）");
    }

    [Fact]
    public void 樣本的網址換成evil主機就整則丟掉()
    {
        Forward(Sample(CodeShown), "accountLoginDevice").Should().NotBeNull("陽性對照的前提：原樣本會留下");
        foreach (var url in new[] { "https://auth.openai.com.evil.example/codex/device", "https://evil.example/codex/device", "http://auth.openai.com/codex/device" })
        {
            var evil = Sample(CodeShown);
            evil["deviceCode"]!["verificationUrl"] = url;
            Forward(evil, "accountLoginDevice").Should().BeNull(url + " 不在白名單，整則丟掉（message 文字裡也有網址）");
        }
    }

    [Fact]
    public void 樣本的expiresAt改成字串或小數就整則丟掉()
    {
        var expiresAt = Sample(CodeShown)["deviceCode"]!["expiresAt"]!;
        expiresAt.Type.Should().Be(JTokenType.Integer);
        foreach (var changed in new JToken[] { expiresAt.ToString(), (double)expiresAt + 0.5, JValue.CreateNull() })
        {
            var sample = Sample(CodeShown);
            sample["deviceCode"]!["expiresAt"] = changed;
            Forward(sample, "accountLoginDevice").Should().BeNull($"expiresAt 是 {changed.Type} 時整則丟掉（桌面的形狀不能改成這樣）");
        }
    }

    [Fact]
    public void 樣本不能回給其他指令()
    {
        RemoteResultRules.CheckOutput(new CommandOutput(Cid, Sample(CodeShown)), "send").Should().NotBeNull("send 的輸出白名單沒有 accountLogin");
        RemoteResultRules.CheckOutput(new CommandOutput(Cid, Sample(CodeShown)), "accountLogout").Should().NotBeNull("登出沒有輸出");
    }
}
