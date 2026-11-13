using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 第 5 階段的伺服器開關（計畫 4.1、D1；WP5.1）：<c>Hub:Logs:Declare</c>、<c>Hub:Handoff:Declare</c> 預設都關（正式環境休眠：HelloResponse.features 和
/// WP5.1 之前相同）；打開時 log1、hand1 依序排在 tok1、tok2 之後；hand1 只在 log1 也打開時宣告
/// </summary>
public sealed class Phase5SwitchTests
{
    private static readonly string[] Before =
    [
        DeviceFeatureRules.Conv1, DeviceFeatureRules.Prov1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Update1, DeviceFeatureRules.Cmd1,
        DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1, DeviceFeatureRules.Media1, DeviceFeatureRules.Stat1, DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2,
        DeviceFeatureRules.Sched1, DeviceFeatureRules.Timed1, DeviceFeatureRules.Draft1, DeviceFeatureRules.Quick1, DeviceFeatureRules.Att1, DeviceFeatureRules.Collab1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2, DeviceFeatureRules.Desk1,
    ];

    [Fact]
    public void 開關預設都關()
    {
        var options = new HubOptions();
        options.Logs.Declare.Should().BeFalse();
        options.Handoff.Declare.Should().BeFalse();
    }

    [Fact]
    public async Task 預設不宣告log1與hand1()
    {
        await using var host = await HubTestHost.StartAsync();
        host.Services.GetRequiredService<DeviceHubFeatures>().Declared.Should().Equal(Before, "做到一半的第 5 階段在正式環境休眠");
    }

    [Theory]
    [InlineData("true", "false", new[] { "log1" })]
    [InlineData("true", "true", new[] { "log1", "hand1" })]
    [InlineData("false", "true", new string[0])]
    public async Task 打開開關_功能排在tok1之後_hand1要有log1(string logs, string handoff, string[] added)
    {
        await using var host = await HubTestHost.StartAsync(settings: new Dictionary<string, string?>
        {
            ["Hub:Logs:Declare"] = logs,
            ["Hub:Handoff:Declare"] = handoff,
        });

        host.Services.GetRequiredService<DeviceHubFeatures>().Declared.Should().Equal(Before.Concat(added));
    }
}
