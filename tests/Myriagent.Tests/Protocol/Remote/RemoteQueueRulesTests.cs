using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 遠端排進佇列的訊息，輪到它時還能不能送（WP4.1 步 6；設計 8.11、14.11 D13）：模式只能比入列時嚴、層級不能比入列時高、政策照樣要允許
/// </summary>
public sealed class RemoteQueueRulesTests
{
    private static readonly DevicePolicy Control = new(RemotePolicyRules.Control, AllowRemoteFullAccess: false);

    private static readonly DevicePolicy ControlFull = new(RemotePolicyRules.Control, AllowRemoteFullAccess: true);

    private static RemoteOrigin Origin(AgentPermissionMode? mode, int tier = 1, bool fullAccess = false) => new("01J9QZ3K8M5R7T2V4W6X8Y0A1B", mode, tier, fullAccess);

    [Fact]
    public void 本機的訊息一律可以送()
    {
        RemoteQueueRules.CanDequeue(null, null, null, null).Should().BeTrue();
        RemoteQueueRules.CanDequeue(null, AgentPermissionMode.FullAccess, true, new DevicePolicy(RemotePolicyRules.Off, false)).Should().BeTrue();
    }

    [Fact]
    public void 模式沒變_政策允許_可以送()
    {
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), AgentPermissionMode.Ask, false, Control).Should().BeTrue();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Auto), AgentPermissionMode.Auto, false, Control).Should().BeTrue();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.FullAccess, 2, true), AgentPermissionMode.FullAccess, false, ControlFull).Should().BeTrue();
    }

    [Fact]
    public void 模式改得比入列時嚴_可以送_改得比較寬_留在佇列()
    {
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Auto), AgentPermissionMode.Ask, false, Control).Should().BeTrue();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), AgentPermissionMode.Plan, false, Control).Should().BeTrue();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), AgentPermissionMode.AcceptEdits, false, Control).Should().BeFalse();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Plan), AgentPermissionMode.Ask, false, Control).Should().BeFalse();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.AcceptEdits), AgentPermissionMode.Auto, false, Control).Should().BeFalse();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Auto, 2, true), AgentPermissionMode.FullAccess, false, ControlFull)
            .Should().BeFalse("本機改成完全自動之後，遠端在一般模式排的訊息不能直接送");
    }

    [Fact]
    public void 多了預先放行規則_層級變高_留在佇列()
    {
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), AgentPermissionMode.Ask, true, ControlFull).Should().BeFalse();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), AgentPermissionMode.Ask, null, ControlFull).Should().BeFalse("偵測不到算有");
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask, 2, true), AgentPermissionMode.Ask, true, ControlFull).Should().BeTrue("入列時就是 2＋完全自動");
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask, 2, false), AgentPermissionMode.Ask, true, ControlFull)
            .Should().BeFalse("層級夠但入列時沒有允許完全自動");
    }

    [Fact]
    public void 政策不再允許或已暫停_留在佇列()
    {
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), AgentPermissionMode.Ask, false, null).Should().BeFalse();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), AgentPermissionMode.Ask, false, new DevicePolicy(RemotePolicyRules.Off, true)).Should().BeFalse("暫停時是 Off");
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), AgentPermissionMode.Ask, false, new DevicePolicy(RemotePolicyRules.View, true)).Should().BeFalse();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.FullAccess, 2, true), AgentPermissionMode.FullAccess, false, Control)
            .Should().BeFalse("允許遠端完全自動被關掉了");
    }

    [Fact]
    public void 資料不全的一律留在佇列()
    {
        RemoteQueueRules.CanDequeue(Origin(null), AgentPermissionMode.Ask, false, Control).Should().BeFalse("入列時的模式不明");
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), null, false, Control).Should().BeFalse("現在的模式不明");
        RemoteQueueRules.CanDequeue(Origin((AgentPermissionMode)99), AgentPermissionMode.Ask, false, Control).Should().BeFalse("沒定義的模式");
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask), (AgentPermissionMode)99, false, Control).Should().BeFalse();
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask, tier: 3), AgentPermissionMode.Ask, false, ControlFull).Should().BeFalse("層級超出範圍（存檔壞了）");
        RemoteQueueRules.CanDequeue(Origin(AgentPermissionMode.Ask, tier: -1), AgentPermissionMode.Ask, false, ControlFull).Should().BeFalse();
    }

    [Fact]
    public void 模式的寬鬆程度()
    {
        Enum.GetValues<AgentPermissionMode>().Should().OnlyContain(m => RemoteQueueRules.Rank(m) != null, "新增模式時要決定它的寬鬆程度");
        Enum.GetValues<AgentPermissionMode>().OrderBy(m => RemoteQueueRules.Rank(m))
            .Should().Equal(AgentPermissionMode.Plan, AgentPermissionMode.Ask, AgentPermissionMode.AcceptEdits, AgentPermissionMode.Auto, AgentPermissionMode.FullAccess);
        RemoteQueueRules.Rank((AgentPermissionMode)99).Should().BeNull();
    }
}
