using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;

namespace Myriagent.Tests.Hub;

/// <summary>拒絕附言的來源（WP4.4 計畫步 8、D22；契約 D13）：合成一則訊息時帶最嚴的遠端來源，取出時照樣重查</summary>
public sealed class DenyFeedbackRulesTests
{
    private static DeniedOperation Local(string feedback = "本機的說明") => new("執行指令", "rm -rf tmp", feedback);

    private static DeniedOperation Remote(string cid, int tier, bool fullAccess, AgentPermissionMode? mode = AgentPermissionMode.Ask)
        => new("執行指令", "rm -rf tmp", "遠端的說明", new RemoteOrigin(cid, mode, tier, fullAccess));

    [Fact]
    public void 全部是本機的_或沒有附言_是null()
    {
        DenyFeedbackRules.OriginOf([Local(), Local("第二則")]).Should().BeNull();
        DenyFeedbackRules.OriginOf([]).Should().BeNull();
        DenyFeedbackRules.OriginOf(null!).Should().BeNull();
    }

    [Fact]
    public void 一則遠端_就是那一則的來源()
    {
        var remote = Remote("01J9Q0000000000000000000AA", 1, false);

        DenyFeedbackRules.OriginOf([remote]).Should().Be(remote.RemoteOrigin);
    }

    [Fact]
    public void 兩則遠端_取層級高的那則_FullAccess取OR()
    {
        var low = Remote("01J9Q0000000000000000000AA", 1, true);
        var high = Remote("01J9Q0000000000000000000AB", 2, false);

        var origin = DenyFeedbackRules.OriginOf([low, high])!;

        origin.CommandId.Should().Be("01J9Q0000000000000000000AB", "層級最高的那一則");
        origin.Tier.Should().Be(2);
        origin.FullAccess.Should().BeTrue("任何一則要「允許遠端完全自動」就要");
    }

    [Fact]
    public void 同層級_取先出現的那則()
    {
        var first = Remote("01J9Q0000000000000000000AA", 1, false);
        var second = Remote("01J9Q0000000000000000000AB", 1, false);

        DenyFeedbackRules.OriginOf([first, second])!.CommandId.Should().Be("01J9Q0000000000000000000AA");
    }

    [Fact]
    public void 本機加遠端_是遠端的來源()
    {
        var remote = Remote("01J9Q0000000000000000000AB", 1, false);

        DenyFeedbackRules.OriginOf([Local(), remote, Local("之後的")]).Should().Be(remote.RemoteOrigin);
    }

    [Fact]
    public void 模式取最不寬鬆的_有不明的就是null()
    {
        var plan = Remote("01J9Q0000000000000000000AA", 1, false, AgentPermissionMode.Plan);
        var auto = Remote("01J9Q0000000000000000000AB", 2, false, AgentPermissionMode.Auto);

        DenyFeedbackRules.OriginOf([auto, plan])!.EffectiveMode.Should().Be(AgentPermissionMode.Plan, "入列時越嚴，取出時模式一被調高就留下");
        DenyFeedbackRules.OriginOf([auto, Remote("01J9Q0000000000000000000AC", 1, false, null)])!.EffectiveMode.Should().BeNull("不明＝取出時一律留下");
        DenyFeedbackRules.OriginOf([auto, Remote("01J9Q0000000000000000000AC", 1, false, (AgentPermissionMode)99)])!.EffectiveMode.Should().BeNull();
    }

    [Fact]
    public void 合成的來源_取出時照CanDequeue重查()
    {
        var origin = DenyFeedbackRules.OriginOf([Remote("01J9Q0000000000000000000AA", 1, false), Remote("01J9Q0000000000000000000AB", 1, false)]);

        RemoteQueueRules.CanDequeue(origin, AgentPermissionMode.Ask, false, new(RemotePolicyRules.Control, false)).Should().BeTrue();
        RemoteQueueRules.CanDequeue(origin, AgentPermissionMode.FullAccess, false, new(RemotePolicyRules.Control, true)).Should().BeFalse("模式被調高");
    }
}
