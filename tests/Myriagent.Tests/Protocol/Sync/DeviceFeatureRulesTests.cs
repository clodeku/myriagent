using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 功能宣告（docs/sync-protocol.md 14.1、設計 14.11 D45）：Hello 的 features ≤32 項、每項 1～32 個 [a-z0-9]；
/// 雙方啟用的是交集（依裝置宣告的順序、去重），任一方沒宣告就是空的、不認得的名稱不啟用。
/// </summary>
public sealed class DeviceFeatureRulesTests
{
    [Fact]
    public void 這版認得的功能()
    {
        DeviceFeatureRules.Known.Should().Equal("conv1", "live1", "live2", "cmd1", "media1", "update1", "peer1", "prov1", "stat1", "tok1", "tok2", "log1", "hand1", "exec1", "sched1", "collab1", "xfer1", "xfer2", "desk1");
        DeviceFeatureRules.Known.Should().AllSatisfy(name => DeviceFeatureRules.IsValidName(name).Should().BeTrue());
    }

    [Theory]
    [InlineData("conv1", true)]
    [InlineData("a", true)]
    [InlineData("0", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Conv1", false)]
    [InlineData("conv-1", false)]
    [InlineData("conv_1", false)]
    [InlineData("conv 1", false)]
    [InlineData("ｃｏｎｖ1", false)]
    public void 名稱只能是1到32個小寫英數字(string? name, bool valid)
        => DeviceFeatureRules.IsValidName(name).Should().Be(valid);

    [Fact]
    public void Hello的features_null可以_最多32項_有一項不合格整份不合格()
    {
        DeviceFeatureRules.IsValid(null).Should().BeTrue("沒宣告");
        DeviceFeatureRules.IsValid([]).Should().BeTrue();
        DeviceFeatureRules.IsValid(Names(32)).Should().BeTrue();
        DeviceFeatureRules.IsValid(Names(33)).Should().BeFalse();
        DeviceFeatureRules.IsValid(["conv1", "conv1"]).Should().BeTrue("重複的照收，協商時去掉");
        DeviceFeatureRules.IsValid(["conv1", "CMD1"]).Should().BeFalse();
        DeviceFeatureRules.IsValid(["conv1", null!]).Should().BeFalse();
    }

    [Fact]
    public void 協商_交集_依裝置的順序_去重()
    {
        DeviceFeatureRules.Negotiate(["cmd1", "conv1", "live1", "cmd1"], ["conv1", "media1", "cmd1"]).Should().Equal("cmd1", "conv1");
        DeviceFeatureRules.Negotiate(["conv1"], ["conv1"]).Should().Equal("conv1");
        DeviceFeatureRules.Negotiate(["conv2", "conv1"], ["conv1", "conv2"]).Should().Equal(new[] { "conv2", "conv1" }, "雙方都宣告了，這版不認得也照協商（用不用由各自的程式決定）");
        DeviceFeatureRules.Negotiate(["future9"], ["conv1"]).Should().BeEmpty("只有一方宣告的不啟用");
    }

    [Fact]
    public void 協商_任一方沒宣告就是空的_不合格的名稱略過_最多32項()
    {
        DeviceFeatureRules.Negotiate(null, ["conv1"]).Should().BeEmpty("第 3 階段的裝置");
        DeviceFeatureRules.Negotiate(["conv1"], null).Should().BeEmpty("第 3 階段的伺服器");
        DeviceFeatureRules.Negotiate([], ["conv1"]).Should().BeEmpty();
        DeviceFeatureRules.Negotiate(["CONV1", "conv1", null!], ["CONV1", "conv1"]).Should().Equal("conv1");

        var many = Names(40);
        DeviceFeatureRules.Negotiate(many, many).Should().Equal(many.Take(32));
    }

    [Fact]
    public void 有沒有某一項_區分大小寫()
    {
        IReadOnlyList<string> negotiated = DeviceFeatureRules.Negotiate(["conv1", "cmd1"], ["cmd1", "conv1"]);

        DeviceFeatureRules.Has(negotiated, DeviceFeatureRules.Cmd1).Should().BeTrue();
        DeviceFeatureRules.Has(negotiated, DeviceFeatureRules.Live1).Should().BeFalse();
        DeviceFeatureRules.Has(negotiated, "CMD1").Should().BeFalse();
        DeviceFeatureRules.Has(null, DeviceFeatureRules.Cmd1).Should().BeFalse();
    }

    [Fact]
    public void 每個方法要的功能_第3階段的不用_第4階段的照14點3與14點6()
    {
        var expected = new Dictionary<string, string?>
        {
            [DeviceHubMethods.Hello] = null,
            [DeviceHubMethods.Prove] = null,
            [DeviceHubMethods.PublishBatch] = null,
            [DeviceHubMethods.Pull] = null,
            [DeviceHubMethods.Heartbeat] = null,
            [DeviceHubMethods.UsagePublish] = null,
            [DeviceHubMethods.GetTrust] = null,
            [DeviceHubMethods.RefreshAccessToken] = null,
            [DeviceHubMethods.BatchAck] = null,
            [DeviceHubMethods.Notify] = null,
            [DeviceHubMethods.UsageAssign] = null,
            [DeviceHubMethods.TrustChanged] = null,
            [DeviceHubMethods.ServerNotice] = null,
            [DeviceHubMethods.SnapshotRequest] = DeviceFeatureRules.Conv1,
            [DeviceHubMethods.LiveRelay] = DeviceFeatureRules.Live1,
            [DeviceHubMethods.WatchChanged] = DeviceFeatureRules.Live1,
            [DeviceHubMethods.LiveDelta] = DeviceFeatureRules.Live2,
            [DeviceHubMethods.LiveResync] = DeviceFeatureRules.Live2,
            [DeviceHubMethods.CommandDelivery] = DeviceFeatureRules.Cmd1,
            [DeviceHubMethods.CommandResult] = DeviceFeatureRules.Cmd1,
            [DeviceHubMethods.CommandOutput] = DeviceFeatureRules.Cmd1,
            [DeviceHubMethods.MediaRequest] = DeviceFeatureRules.Media1,
            [DeviceHubMethods.PeerCmd] = DeviceFeatureRules.Peer1,
            [DeviceHubMethods.PeerReply] = DeviceFeatureRules.Peer1,
            [DeviceHubMethods.StatsPublish] = DeviceFeatureRules.Stat1,
            [DeviceHubMethods.StatsUpdate] = DeviceFeatureRules.Stat1,
            [DeviceHubMethods.TokensPublish] = DeviceFeatureRules.Tok1,
            [DeviceHubMethods.TokensUpdate] = DeviceFeatureRules.Tok2,
            [DeviceHubMethods.HandoffPreview] = DeviceFeatureRules.Hand1,
            [DeviceHubMethods.HandoffStart] = DeviceFeatureRules.Hand1,
            [DeviceHubMethods.HandoffCancel] = DeviceFeatureRules.Hand1,
            [DeviceHubMethods.HandoffProgress] = DeviceFeatureRules.Hand1,
            [DeviceHubMethods.HandoffReady] = DeviceFeatureRules.Hand1,
            [DeviceHubMethods.HandoffRequest] = DeviceFeatureRules.Hand1,
            [DeviceHubMethods.HandoffUpdate] = DeviceFeatureRules.Hand1,
            [DeviceHubMethods.LogRepair] = DeviceFeatureRules.Log1,
            [DeviceHubMethods.ExecOutput] = DeviceFeatureRules.Exec1,
            [DeviceHubMethods.ExecCancel] = DeviceFeatureRules.Exec1,
            [DeviceHubMethods.ExecReply] = DeviceFeatureRules.Exec1,
            [DeviceHubMethods.ExecAbort] = DeviceFeatureRules.Exec1,
            [DeviceHubMethods.DeskUp] = DeviceFeatureRules.Desk1,
            [DeviceHubMethods.DeskDown] = DeviceFeatureRules.Desk1,
            [DeviceHubMethods.SchedulesPublish] = DeviceFeatureRules.Sched1,
            [DeviceHubMethods.SchedulesUpdate] = DeviceFeatureRules.Sched1,
            [DeviceHubMethods.CollabCmd] = DeviceFeatureRules.Collab1,
            [DeviceHubMethods.CollabUpdate] = DeviceFeatureRules.Collab1,
        };

        DeviceHubMethods.DeviceToServer.Concat(DeviceHubMethods.ServerToDevice).Should().BeEquivalentTo(expected.Keys, "每個方法都列在這張表");
        foreach (var (method, feature) in expected)
            DeviceFeatureRules.RequiredFor(method).Should().Be(feature, method);
        FluentActions.Invoking(() => DeviceFeatureRules.RequiredFor("Watch")).Should().Throw<ArgumentOutOfRangeException>("新方法一定要決定功能");
        FluentActions.Invoking(() => DeviceFeatureRules.RequiredFor("commandDelivery")).Should().Throw<ArgumentOutOfRangeException>("區分大小寫");
    }

    [Fact]
    public void 第5階段的功能_log1與hand1_hand1要有log1()
    {
        DeviceFeatureRules.Log1.Should().Be("log1");
        DeviceFeatureRules.Hand1.Should().Be("hand1");
        string[] both = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Log1, DeviceFeatureRules.Hand1];

        DeviceFeatureRules.Negotiate(both, both).Should().Equal("conv1", "log1", "hand1");
        DeviceFeatureRules.Negotiate(both, [DeviceFeatureRules.Conv1, DeviceFeatureRules.Hand1]).Should().Equal(new[] { "conv1" }, "伺服器沒有 log1：hand1 跟著拿掉");
        DeviceFeatureRules.Negotiate([DeviceFeatureRules.Hand1], both).Should().BeEmpty("裝置沒有 log1：hand1 跟著拿掉");
        DeviceFeatureRules.Negotiate([DeviceFeatureRules.Log1], both).Should().Equal("log1");
        DeviceFeatureRules.Negotiate([DeviceFeatureRules.Hand1, DeviceFeatureRules.Log1], both).Should().Equal(new[] { "hand1", "log1" }, "順序照裝置宣告的，只要交集裡有 log1");
    }

    [Fact]
    public void update1不是任何方法要的功能()
    {
        DeviceFeatureRules.Update1.Should().Be("update1");
        foreach (var method in DeviceHubMethods.DeviceToServer.Concat(DeviceHubMethods.ServerToDevice))
        {
            DeviceFeatureRules.RequiredFor(method).Should().NotBe(DeviceFeatureRules.Update1,
                $"{method}：update1 只決定這台能不能拿到只更新的連線（docs/sync-protocol.md 1.4），不開放任何 hub 方法");
        }
    }

    [Fact]
    public void update1照一般規則協商()
    {
        DeviceFeatureRules.Negotiate([DeviceFeatureRules.Update1], [DeviceFeatureRules.Update1]).Should().Equal("update1");
        DeviceFeatureRules.Negotiate([DeviceFeatureRules.Update1], null).Should().BeEmpty("伺服器沒宣告");
        DeviceFeatureRules.Negotiate(null, [DeviceFeatureRules.Update1]).Should().BeEmpty("沒宣告任何功能的裝置（第 3 階段的桌面）");
        DeviceFeatureRules.Negotiate([DeviceFeatureRules.Conv1], [DeviceFeatureRules.Update1]).Should().BeEmpty("只有一方宣告的不啟用");
        DeviceFeatureRules.Has([DeviceFeatureRules.Conv1, DeviceFeatureRules.Update1], DeviceFeatureRules.Update1).Should().BeTrue();
        DeviceFeatureRules.Has(null, DeviceFeatureRules.Update1).Should().BeFalse();
        DeviceFeatureRules.Has(["UPDATE1"], DeviceFeatureRules.Update1).Should().BeFalse("區分大小寫");
        DeviceFeatureRules.IsValid([DeviceFeatureRules.Conv1, DeviceFeatureRules.Update1]).Should().BeTrue();
    }

    private static string[] Names(int count) => Enumerable.Range(0, count).Select(i => "f" + i).ToArray();
}
