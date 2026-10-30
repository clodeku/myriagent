using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Accounts;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// WP4.8 步 10（D32；審查 #23）：桌面送給發起的瀏覽器連線的那一則 accountLogin（遠端登入開始時唯一的輸出，D9）以
/// <c>Hub/Samples/wp48-accountLogin-*.json</c> 固定形狀；伺服器那邊（MyriagentHub.Tests 的 RemoteAccountOutputContractTests）以
/// <c>CommandOutputRules.Project</c> 處理同一份樣本、斷言整則留下。兩邊讀的都是 repo 裡的同一個檔，改了任何一邊的形狀都會紅。
/// 遠端登入一律經假 app-server／假 claude（RemoteLoginRig）：不跑真的 CLI、不開瀏覽器、不連網
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class RemoteAccountOutputContractTests
{
    private const int Slow = RemoteLoginRig.Slow;

    public const string CodeShownSample = "wp48-accountLogin-codeShown.json";

    public const string ConsoleOpenedSample = "wp48-accountLogin-consoleOpened.json";

    /// <summary>Claude 的 app 內登入給了登入網址（2026-10-26）</summary>
    public const string UrlShownSample = "wp48-accountLogin-urlShown.json";

    /// <summary>樣本固定的驗證碼到期（2026-10-20T09:15:00Z，毫秒）</summary>
    public const long SampleExpiresAt = 1_792_487_700_000;

    /// <summary>repo 裡的樣本（伺服器的測試讀同一個檔；不從輸出資料夾讀，改樣本不必重新建置）</summary>
    internal static JObject Sample(string name)
        => JObject.Parse(File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "tests", "Myriagent.Tests", "Hub", "Samples", name)));

    private static LoginRequest CodexRemote() =>
        new(LoginMethod.DeviceCode, null, new RemoteLoginExpectation("codex:" + FakeCodexAppServer.Email, FakeCodexAppServer.Email, null));

    private static LoginRequest ClaudeRemote() =>
        new(LoginMethod.Default, "expected@example.test", new RemoteLoginExpectation("claude:expected@example.test", "expected@example.test", null));

    /// <summary>樣本的輸入：假 app-server 的驗證碼與網址、固定的到期；now 在到期前 15 分鐘（message 寫「15 分鐘內有效」）</summary>
    private static (string AccountId, RemoteLoginStart Start, DateTimeOffset Now) SampleInput(string name) => name switch
    {
        CodeShownSample => ("acc-codex",
            new RemoteLoginStart(RemoteLoginMilestone.CodeShown,
                DeviceCode: new CodexDeviceCode(FakeCodexAppServer.LoginId, FakeCodexAppServer.UserCode, new Uri(FakeCodexAppServer.VerificationUrl), SampleExpiresAt)),
            DateTimeOffset.FromUnixTimeMilliseconds(SampleExpiresAt) - CodexDeviceCodeRules.MaxLifetime),
        ConsoleOpenedSample => ("acc-claude2", new RemoteLoginStart(RemoteLoginMilestone.ConsoleOpened), DateTimeOffset.FromUnixTimeMilliseconds(SampleExpiresAt)),
        UrlShownSample => ("acc-claude2",
            new RemoteLoginStart(RemoteLoginMilestone.UrlShown, LoginUrl: new Uri("https://claude.com/cai/oauth/authorize?code=true&client_id=sample&state=sample-state")),
            DateTimeOffset.FromUnixTimeMilliseconds(SampleExpiresAt)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [Theory]
    [InlineData(CodeShownSample)]
    [InlineData(ConsoleOpenedSample)]
    [InlineData(UrlShownSample)]
    public void 樣本的輸入_輸出和樣本逐項相同(string name)
    {
        var (accountId, start, now) = SampleInput(name);
        var output = RemoteAccountRules.LoginOutput(accountId, start, now);

        JToken.DeepEquals(output, Sample(name)).Should().BeTrue($"輸出 {output.ToString(Formatting.None)} 要和樣本 {name} 相同");
        output.ToString(Formatting.None).Should().NotContain(FakeCodexAppServer.LoginId, "loginId 絕不輸出");
    }

    [Fact]
    public void 樣本的形狀_驗證碼三個鍵_expiresAt是整數_沒有loginId()
    {
        var code = Sample(CodeShownSample);
        code.Properties().Select(p => p.Name).Should().Equal(["type", "accountId", "message", "done", "success", "deviceCode"]);
        code["done"]!.Type.Should().Be(JTokenType.Boolean);
        code["success"]!.Type.Should().Be(JTokenType.Null);
        var deviceCode = (JObject)code["deviceCode"]!;
        deviceCode.Properties().Select(p => p.Name).Should().Equal(["userCode", "verificationUrl", "expiresAt"]);
        deviceCode["expiresAt"]!.Type.Should().Be(JTokenType.Integer, "伺服器只收整數的 expiresAt（CommandOutputRules.ProjectDeviceCode）");
        CodexDeviceCodeRules.IsAllowedVerificationUrl((string?)deviceCode["verificationUrl"]).Should().BeTrue();
        code.ToString(Formatting.None).Should().NotContainAny(["loginId", FakeCodexAppServer.LoginId]);

        var console = Sample(ConsoleOpenedSample);
        console.Properties().Select(p => p.Name).Should().Equal(["type", "accountId", "message", "done", "success"], "Claude 的主控台登入沒有驗證碼");
    }

    [Fact]
    public async Task 遠端登入的輸出和樣本同形狀_Codex_本機視窗的那一則也一樣()
    {
        using var rig = new RemoteLoginRig();
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);

        var output = RemoteAccountRules.LoginOutput(rig.Codex, start, DateTimeOffset.UtcNow);
        var sample = Sample(CodeShownSample);
        AssertSameShape(output, sample);
        output["accountId"]!.Value<string>().Should().Be(rig.Codex, "電腦送的是本機 id，伺服器轉給瀏覽器時才換");
        output["deviceCode"]!["userCode"]!.Value<string>().Should().Be(FakeCodexAppServer.UserCode);
        output["deviceCode"]!["verificationUrl"]!.Value<string>().Should().Be(FakeCodexAppServer.VerificationUrl);
        output["deviceCode"]!["expiresAt"]!.Value<long>().Should().Be(start.DeviceCode!.ExpiresAt);
        output.ToString(Formatting.None).Should().NotContain(FakeCodexAppServer.LoginId);
        AssertOutputAllowed(output);

        // 這台的視窗（帳戶卡）收到的那一則：網頁的 accountLogin 處理（W/app.js）兩種來源共用，形狀也要相同
        await rig.H.WaitUntilAsync(() => rig.H.Main.Messages("accountLogin").Any(m => m["deviceCode"] is JObject), "本機視窗收到帶驗證碼的 accountLogin", Slow);
        AssertSameShape(rig.H.Main.Messages("accountLogin").First(m => m["deviceCode"] is JObject), sample);

        await rig.CancelAndWaitAsync(rig.Codex);
    }

    [Fact]
    public async Task 遠端登入的輸出和樣本同形狀_Claude()
    {
        using var rig = new RemoteLoginRig();
        var claude = rig.H.Claude2;
        var start = await rig.StartAsync(claude.Id, ClaudeRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.ConsoleOpened);

        var output = RemoteAccountRules.LoginOutput(claude.Id, start, DateTimeOffset.UtcNow);
        AssertSameShape(output, Sample(ConsoleOpenedSample));
        output["accountId"]!.Value<string>().Should().Be(claude.Id);
        output["message"]!.Value<string>().Should().Be(RemoteAccountRules.ConsoleOpenedMessage);
        AssertOutputAllowed(output);

        await rig.CancelAndWaitAsync(claude.Id);
    }

    [Fact]
    public void 被拒的開始沒有輸出_CodeShown沒帶驗證碼也不組()
    {
        var failed = new RemoteLoginStart(RemoteLoginMilestone.Failed, RemoteResultCodes.Conflict, "那台沒有開始登入");
        FluentActions.Invoking(() => RemoteAccountRules.LoginOutput("acc-codex", failed, DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>("被拒時只 Reject，不送 accountLogin（D9）");
        FluentActions.Invoking(() => RemoteAccountRules.LoginOutput("acc-codex", new RemoteLoginStart(RemoteLoginMilestone.CodeShown), DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => RemoteAccountRules.LoginOutput(" ", new RemoteLoginStart(RemoteLoginMilestone.ConsoleOpened), DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
    }

    /// <summary>電腦與伺服器共用的型錄白名單：兩個遠端登入指令都能回 accountLogin（RemoteResultRules.CheckOutput 是伺服器轉送前的第一道）</summary>
    private static void AssertOutputAllowed(JObject output)
    {
        var commandId = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var command in new[] { "accountLoginDevice", "accountLogin" })
            RemoteResultRules.CheckOutput(new CommandOutput(commandId, output), command).Should().BeNull(command + " 的輸出白名單有 accountLogin");
    }

    /// <summary>鍵的集合與順序、每個值的 JTokenType 都和樣本相同（物件逐層比；值本身不比：驗證碼的到期、message 的分鐘數隨時間）</summary>
    internal static void AssertSameShape(JToken actual, JToken expected, string path = "$")
    {
        actual.Type.Should().Be(expected.Type, path + " 的型別");
        if (expected is not JObject expectedObject)
            return;
        var actualObject = (JObject)actual;
        actualObject.Properties().Select(p => p.Name).Should().Equal(expectedObject.Properties().Select(p => p.Name), path + " 的鍵");
        foreach (var property in expectedObject.Properties())
            AssertSameShape(actualObject[property.Name]!, property.Value, path + "." + property.Name);
    }
}
