using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>桌面遠端操作的伺服器規則（sync-protocol 14.7）</summary>
public sealed class PeerRulesTests
{
    [Theory]
    [InlineData("DEMO-PC-3", "DEMO-PC-3")]
    [InlineData("  辦公室電腦  ", "辦公室電腦")]
    [InlineData("a‮b​c\nd", "abcd")]
    [InlineData(null, PeerRules.UnnamedDevice)]
    [InlineData("", PeerRules.UnnamedDevice)]
    [InlineData("​‮ ", PeerRules.UnnamedDevice)]
    public void DeviceLabel_去掉控制與格式字元(string? name, string expected)
    {
        var label = PeerRules.DeviceLabel(name);
        label.Should().Be(expected);
        CommandEnvelope.IsValidIssuerLabel(label).Should().BeTrue();
    }

    [Fact]
    public void DeviceLabel_截成64個Unicode字元_代理字元對不切開()
    {
        var name = string.Concat(Enumerable.Repeat("😀", 70));
        var label = PeerRules.DeviceLabel(name);
        label.EnumerateRunes().Count().Should().Be(CommandEnvelope.MaxIssuerLabelLength);
        CommandEnvelope.IsValidIssuerLabel(label).Should().BeTrue();
    }

    [Fact]
    public void DeviceLabel_不成對的代理字元去掉()
    {
        PeerRules.DeviceLabel("a\uD800b").Should().Be("ab");
    }

    [Fact]
    public void ReplyConnection_選已證明而且有peer1的連線裡Order最大的()
    {
        PeerRules.ReplyConnection(new[]
        {
            new PeerConnectionFact("old", 1, true, true),
            new PeerConnectionFact("newest-no-peer", 9, true, false),
            new PeerConnectionFact("unproven", 8, false, true),
            new PeerConnectionFact("new", 5, true, true),
        }).Should().Be("new");
        PeerRules.ReplyConnection(new[] { new PeerConnectionFact("x", 1, true, false) }).Should().BeNull();
        PeerRules.ReplyConnection(Array.Empty<PeerConnectionFact>()).Should().BeNull();
    }

    [Fact]
    public void TargetTooOldMessage_帶電腦名稱_沒有名稱時用另一台電腦()
    {
        PeerRules.TargetTooOldMessage("OFFICE-PC").Should().Contain("「OFFICE-PC」").And.Contain("版本太舊");
        PeerRules.TargetTooOldMessage("  ").Should().Contain("「" + PeerRules.UnnamedDevice + "」");
    }

    [Fact]
    public void Fits_超過單則訊息上限的不送()
    {
        PeerRules.Fits(new PeerReply("01J0000000000000000000000A", new JObject { ["type"] = "toast", ["text"] = "hi" })).Should().BeTrue();
        PeerRules.Fits(new PeerReply("01J0000000000000000000000A", new JObject { ["type"] = "files", ["text"] = new string('x', 1_100_000) })).Should().BeFalse();
    }
}
