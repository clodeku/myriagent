using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>對話之間互送訊息的規則（ConversationMessageRules；2026-11-15）</summary>
public sealed class ConversationMessageRulesTests
{
    private const string IdA = "0123456789abcdef0123456789abcdef";
    private const string IdB = "fedcba9876543210fedcba9876543210";
    private const string IdC = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly ConversationTarget[] All =
    [
        new(IdA, "報表", "d1", "PC-A", true, false, false, DateTime.Now),
        new(IdB, "部署", "d2", "PC-B", false, false, false, DateTime.Now),
        new(IdC, "部署", "d1", "PC-A", true, false, false, DateTime.Now),
    ];

    private static bool OnB(ConversationTarget t) => t.DeviceId == "d2";

    [Fact]
    public void ID大小寫都收_要在指定的那台()
    {
        ConversationMessageRules.Resolve(All, "PC-B", OnB, IdB.ToUpperInvariant()).Target!.Id.Should().Be(IdB);
        var (target, problem) = ConversationMessageRules.Resolve(All, "PC-B", OnB, IdA);
        target.Should().BeNull();
        problem.Should().Contain("在「PC-A」").And.Contain("請改送到「PC-A」");
        ConversationMessageRules.Resolve(All, "PC-B", OnB, "ffffffffffffffffffffffffffffffff").Problem.Should().Contain("找不到對話");
    }

    [Fact]
    public void 標題只在指定的那台找_不分大小寫()
    {
        ConversationMessageRules.Resolve(All, "PC-B", OnB, " 部署 ").Target!.Id.Should().Be(IdB, "PC-A 也有叫「部署」的，只看 PC-B");
        ConversationMessageRules.Resolve(All, "PC-B", OnB, "報表").Problem.Should().Contain("「PC-A」有一個");
        ConversationMessageRules.Resolve(All, "PC-B", OnB, "沒有這個").Problem.Should().Contain("myriagent-remote conversations");
        ConversationMessageRules.Resolve(All, "PC-B", OnB, " ").Problem.Should().Contain("少了");
    }

    [Fact]
    public void 組出的文字_來源與回覆方式在前_內容在後()
    {
        var text = ConversationMessageRules.Compose("PC-A", "d1", new string('長', 100), IdA, "  內容  ");
        text.Should().StartWith("【其他對話送來的訊息】\n來自：電腦「PC-A」的對話「" + new string('長', 80) + "…」（對話 ID " + IdA + "）\n");
        text.Should().Contain($"myriagent-remote send d1 {IdA} -- <內容>").And.EndWith("\n\n內容");
        ConversationMessageRules.Compose("PC-A", null, "", IdA, "x").Should().Contain("（沒有標題）").And.Contain("myriagent-remote send 這台 ");
    }

    [Theory]
    [InlineData("這台", true)]
    [InlineData("SELF", true)]
    [InlineData(" . ", true)]
    [InlineData("PC-A", false)]
    [InlineData(null, false)]
    public void 這台的寫法(string? query, bool self) => ConversationMessageRules.IsSelfAlias(query).Should().Be(self);
}
