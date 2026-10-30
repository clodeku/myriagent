using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 只給開發與測試用的瀏覽器網頁調整（BrowserTuningRules）：「重新連線中」的寬限期（init 的 remote.reconnectGraceMs）只在 Development、Testing
/// 環境、而且設定在 1000～60000 之間才送；正式站一律不送，網頁照內建的 60 秒（remote-control.mjs 以它把等待縮成幾秒）
/// </summary>
public sealed class BrowserTuningRulesTests
{
    [Theory]
    [InlineData("Development", 5000, 5000)]
    [InlineData("development", 5000, 5000)]
    [InlineData("Testing", 1000, 1000)]
    [InlineData("Development", 60000, 60000)]
    [InlineData("Production", 5000, null)]
    [InlineData("Console", 5000, null)]
    [InlineData("Service", 5000, null)]
    [InlineData(null, 5000, null)]
    [InlineData("Development", null, null)]
    [InlineData("Development", 999, null)]
    [InlineData("Development", 60001, null)]
    [InlineData("Development", -1, null)]
    public void 寬限期只在開發與測試環境_只能縮短不能加長(string? environment, int? configured, int? expected)
        => BrowserTuningRules.ReconnectGraceMs(environment, configured).Should().Be(expected);

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task init的remote_開發環境有設定時才帶reconnectGraceMs(string environment, bool sent)
    {
        var h = new GatewayHarness(o => o.Browser.ReconnectGraceMs = 5000, environment);
        var a = h.Add("c1");
        h.Gateway.RequestInit(a.Connection, "main");
        await h.RunAsync();
        var init = await a.NextAsync();
        var remote = (JObject)init["remote"]!;
        if (sent)
            remote["reconnectGraceMs"]!.Value<int>().Should().Be(5000);
        else
            remote.ContainsKey("reconnectGraceMs").Should().BeFalse("正式站不送，網頁照內建的 60 秒");
        remote["mode"]!.Value<string>().Should().Be("browser");
    }

    [Fact]
    public async Task 沒有設定_任何環境都不帶()
    {
        var h = new GatewayHarness(environment: "Development");
        var a = h.Add("c1");
        h.Gateway.RequestInit(a.Connection, "main");
        await h.RunAsync();
        ((JObject)(await a.NextAsync())["remote"]!).ContainsKey("reconnectGraceMs").Should().BeFalse();
    }
}
