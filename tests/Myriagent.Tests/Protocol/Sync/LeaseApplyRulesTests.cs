using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 電腦端套用 conv.lease（第 5 階段計畫 5.2 的表、WP5.1）：新擁有者是自己→匯入；本機擁有、換給別台：自己交出的→變複本，否則→圍欄；
/// 複本→更新擁有者與 epoch；不認得→只影響清單。作者不是伺服器、epoch 不比已知的新一律不理
/// </summary>
public sealed class LeaseApplyRulesTests
{
    private const string Self = "d_7K3M0000000000000000000000";

    private const string Other = "d_8P2Q0000000000000000000000";

    private const string Third = "d_9R4S0000000000000000000000";

    private static ConversationLeasePayload Lease(string owner, long epoch = 3, string prevOwner = Third) =>
        new(owner, epoch, prevOwner, epoch - 1, LeaseReasons.Transfer, HandoffId: "01J9QZ3K8M5R7T2V4W6X8Y0A1B", Mode: HandoffModes.AfterTurn);

    private static LeaseApplyContext Context(LeaseLocalRole role, long? known = 2, bool matches = false, string? author = SyncAuthors.Server) =>
        new(Self, author, role, known, matches);

    [Fact]
    public void 新擁有者是自己_匯入()
    {
        foreach (var role in Enum.GetValues<LeaseLocalRole>())
            LeaseApplyRules.Decide(Context(role, known: role == LeaseLocalRole.Unknown ? null : 2), Lease(Self)).Should().Be(LeaseApplyAction.Import, role.ToString());
    }

    [Fact]
    public void 本機擁有_換給別台_自己交出的變複本_否則圍欄()
    {
        LeaseApplyRules.Decide(Context(LeaseLocalRole.Owned, matches: true), Lease(Other, prevOwner: Self)).Should().Be(LeaseApplyAction.BecomeReplica);
        LeaseApplyRules.Decide(Context(LeaseLocalRole.Owned, matches: false), Lease(Other, prevOwner: Self)).Should().Be(LeaseApplyAction.Fence, "強制接手，或這台不知道的交接");
    }

    [Fact]
    public void 複本更新擁有者_不認得的只影響清單()
    {
        LeaseApplyRules.Decide(Context(LeaseLocalRole.Replica), Lease(Other)).Should().Be(LeaseApplyAction.UpdateReplica);
        LeaseApplyRules.Decide(Context(LeaseLocalRole.Unknown, known: null), Lease(Other)).Should().Be(LeaseApplyAction.ListOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Other)]
    [InlineData("Server")]
    public void 作者不是伺服器_不理(string? author)
    {
        foreach (var role in Enum.GetValues<LeaseLocalRole>())
            LeaseApplyRules.Decide(Context(role, author: author), Lease(Self)).Should().Be(LeaseApplyAction.Ignore, $"{role}：裝置送的 conv.lease 不算數");
    }

    [Fact]
    public void epoch不比已知的新_不理()
    {
        LeaseApplyRules.Decide(Context(LeaseLocalRole.Owned, known: 3), Lease(Self, epoch: 3)).Should().Be(LeaseApplyAction.Ignore, "已經匯入（重拉）");
        LeaseApplyRules.Decide(Context(LeaseLocalRole.Replica, known: 4), Lease(Other, epoch: 3)).Should().Be(LeaseApplyAction.Ignore, "比複本已知的舊");
        LeaseApplyRules.Decide(Context(LeaseLocalRole.Owned, known: 2, matches: true), Lease(Other, epoch: 3)).Should().Be(LeaseApplyAction.BecomeReplica);
    }
}
