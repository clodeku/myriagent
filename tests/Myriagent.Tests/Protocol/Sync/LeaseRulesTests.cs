using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 租約與交接的純函式（第 5 階段計畫 5.4、7.1、D7、WP5.1）：失聯（60 秒邊界、UI 卡住 120 秒、撤銷、重啟寬限只對重啟前在線的裝置）、
/// HandoffStart 的每個代碼、狀態機的整張表、還原後的認領、期限、伺服器以線上名稱判斷代理種類
/// </summary>
public sealed class LeaseRulesTests
{
    private const long Started = 1_791_000_000_000;

    private const long Second = 1000;

    // ── 失聯（D7） ──

    [Fact]
    public void 沒有連線_最後一次有連線滿60秒才算失聯()
    {
        var now = Started + 3600 * Second;
        LeaseRules.IsOrphaned(now, connected: false, lastSeenAt: now - 59_999, uiAliveAt: null, Started, revoked: false).Should().BeFalse("還不到 60 秒");
        LeaseRules.IsOrphaned(now, connected: false, lastSeenAt: now - 60 * Second, uiAliveAt: null, Started, revoked: false).Should().BeTrue("滿 60 秒");
        LeaseRules.IsOrphaned(now, connected: false, lastSeenAt: null, uiAliveAt: null, Started, revoked: false).Should().BeTrue("從沒見過");
    }

    [Fact]
    public void 有連線_UI卡住超過120秒才算失聯_還沒有心跳的不算()
    {
        var now = Started + 3600 * Second;
        LeaseRules.IsOrphaned(now, connected: true, lastSeenAt: now, uiAliveAt: now - 120 * Second, Started, revoked: false).Should().BeFalse("剛好 120 秒");
        LeaseRules.IsOrphaned(now, connected: true, lastSeenAt: now, uiAliveAt: now - 120 * Second - 1, Started, revoked: false).Should().BeTrue("超過 120 秒");
        LeaseRules.IsOrphaned(now, connected: true, lastSeenAt: now, uiAliveAt: null, Started, revoked: false).Should().BeFalse("剛連上、還沒收到心跳");
        LeaseRules.IsOrphaned(now, connected: true, lastSeenAt: null, uiAliveAt: now, Started, revoked: false).Should().BeFalse("有連線就不看 lastSeenAt");
    }

    [Fact]
    public void 被撤銷的裝置立即算失聯()
    {
        var now = Started + 10 * Second;
        LeaseRules.IsOrphaned(now, connected: true, lastSeenAt: now, uiAliveAt: now, Started, revoked: true).Should().BeTrue();
        LeaseRules.IsOrphaned(now, connected: false, lastSeenAt: now - Second, uiAliveAt: null, Started, revoked: true).Should().BeTrue("重啟寬限也不算");
    }

    [Fact]
    public void 重啟寬限180秒_只給重啟前還在線的裝置()
    {
        var onlineBefore = Started - 120 * Second;
        LeaseRules.IsOrphaned(Started + 179_999, connected: false, onlineBefore, uiAliveAt: null, Started, revoked: false).Should().BeFalse("寬限內");
        LeaseRules.IsOrphaned(Started + 180 * Second, connected: false, onlineBefore, uiAliveAt: null, Started, revoked: false).Should().BeTrue("寬限結束，最後一次有連線早就滿 60 秒");
        LeaseRules.IsOrphaned(Started + 10 * Second, connected: false, Started - 30 * Second, uiAliveAt: null, Started, revoked: false).Should().BeFalse();

        var offlineBefore = Started - 120 * Second - 1;
        LeaseRules.IsOrphaned(Started + 10 * Second, connected: false, offlineBefore, uiAliveAt: null, Started, revoked: false).Should().BeTrue("重啟前就離線的不必等");
        LeaseRules.IsOrphaned(Started + 10 * Second, connected: false, lastSeenAt: null, uiAliveAt: null, Started, revoked: false).Should().BeTrue();
    }

    [Fact]
    public void 寬限內重連又斷了_照一般的60秒()
    {
        // 重連後在 Started+100 秒斷線：lastSeenAt 是斷線的時間（在啟動之後，也算「重啟前還在線」的條件），寬限內仍不算失聯
        LeaseRules.IsOrphaned(Started + 170 * Second, connected: false, Started + 100 * Second, uiAliveAt: null, Started, revoked: false).Should().BeFalse();
        // 寬限結束後照一般規則：斷線滿 60 秒
        LeaseRules.IsOrphaned(Started + 181 * Second, connected: false, Started + 100 * Second, uiAliveAt: null, Started, revoked: false).Should().BeTrue();
        LeaseRules.IsOrphaned(Started + 181 * Second, connected: false, Started + 150 * Second, uiAliveAt: null, Started, revoked: false).Should().BeFalse();
    }

    // ── 代理種類 ──

    [Fact]
    public void 伺服器以線上名稱判斷_大小寫要完全相同_不認得的不能()
    {
        LeaseRules.SupportsHandoff("Claude").Should().BeTrue();
        LeaseRules.SupportsHandoff("Codex").Should().BeTrue();
        LeaseRules.SupportsHandoff("Grok").Should().BeFalse("擁有者決定 1");
        LeaseRules.SupportsHandoff("Antigravity").Should().BeFalse();
        LeaseRules.SupportsHandoff("claude").Should().BeFalse();
        LeaseRules.SupportsHandoff("0").Should().BeFalse("數字不是名稱");
        LeaseRules.SupportsHandoff("Gemini").Should().BeFalse();
        LeaseRules.SupportsHandoff(null).Should().BeFalse();
    }

    // ── HandoffStart 的檢查（7.1） ──

    private const string DevA = "d_7K3M0000000000000000000000";

    private const string DevB = "d_8P2Q0000000000000000000000";

    private static HandoffStartSnapshot Ok(bool force = false) => new(
        ConversationExists: true,
        Deleted: false,
        Provider: "Claude",
        SyncPolicy: "full",
        HasActiveHandoff: false,
        OwnerDeviceId: DevA,
        ToDeviceId: DevB,
        TargetOnline: true,
        TargetHasHand1: true,
        TargetHasAccount: true,
        TargetAllowsControl: true,
        InitiatedByTarget: false,
        Force: force,
        OwnerOrphaned: force,
        OwnerHasHand1: true,
        OwnerAllowsControl: true,
        InitiatedByOwner: false);

    private static List<(string Because, HandoffStartSnapshot Snapshot, string? Expected)> StartCases()
    {
        var ok = Ok();
        var force = Ok(force: true);
        return new List<(string, HandoffStartSnapshot, string?)>
        {
            ("全部通過", ok, null),
            ("強制接手通過", force, null),
            ("對話不存在", ok with { ConversationExists = false, Deleted = true }, HandoffCodes.NotFound),
            ("已刪除", ok with { Deleted = true }, HandoffCodes.Deleted),
            ("Grok", ok with { Provider = "Grok" }, HandoffCodes.UnsupportedProvider),
            ("Antigravity", ok with { Provider = "Antigravity" }, HandoffCodes.UnsupportedProvider),
            ("不認得的代理", ok with { Provider = null }, HandoffCodes.UnsupportedProvider),
            ("metadata", ok with { SyncPolicy = "metadata" }, HandoffCodes.NotFull),
            ("paused", ok with { SyncPolicy = "paused" }, HandoffCodes.NotFull),
            ("沒設過的政策是 full", ok with { SyncPolicy = null }, null),
            ("進行中", ok with { HasActiveHandoff = true }, HandoffCodes.Busy),
            ("強制接手不被進行中的擋", force with { HasActiveHandoff = true }, null),
            ("目標是擁有者", ok with { ToDeviceId = DevA }, HandoffCodes.SameDevice),
            ("目標離線", ok with { TargetOnline = false, TargetHasHand1 = false }, HandoffCodes.TargetOffline),
            ("目標太舊", ok with { TargetHasHand1 = false }, HandoffCodes.TargetOutdated),
            ("目標沒有帳戶", ok with { TargetHasAccount = false }, HandoffCodes.NoAccount),
            ("目標不允許遠端操作", ok with { TargetAllowsControl = false }, HandoffCodes.Policy),
            ("目標自己發起不查自己的政策", ok with { TargetAllowsControl = false, InitiatedByTarget = true }, null),
            ("一般交接但擁有者失聯", ok with { OwnerOrphaned = true }, HandoffCodes.OwnerOffline),
            ("擁有者太舊", ok with { OwnerHasHand1 = false }, HandoffCodes.TargetOutdated),
            ("擁有者不允許遠端操作", ok with { OwnerAllowsControl = false }, HandoffCodes.Policy),
            ("擁有者自己發起不查自己的政策", ok with { OwnerAllowsControl = false, InitiatedByOwner = true }, null),
            ("強制接手但擁有者在線", force with { OwnerOrphaned = false }, HandoffCodes.OwnerOnline),
            ("強制接手不問擁有者", force with { OwnerHasHand1 = false, OwnerAllowsControl = false }, null),
            ("依序：先代理種類再政策", ok with { Provider = "Grok", SyncPolicy = "metadata" }, HandoffCodes.UnsupportedProvider),
            ("依序：先目標再擁有者", ok with { TargetHasAccount = false, OwnerOrphaned = true }, HandoffCodes.NoAccount),
        };
    }

    [Fact]
    public void HandoffStart的檢查_依序_每個代碼()
    {
        var cases = StartCases();
        cases.Should().HaveCountGreaterThan(20);
        foreach (var (because, snapshot, expected) in cases)
        {
            var code = LeaseRules.CheckStart(snapshot);
            code.Should().Be(expected, because);
            if (code is not null)
                HandoffCodes.IsKnown(code).Should().BeTrue(code);
        }
    }

    // ── 狀態機（7.1） ──

    [Fact]
    public void 狀態機的整張表()
    {
        var expected = new Dictionary<(string, HandoffEvent), string>
        {
            [(HandoffStates.Requested, HandoffEvent.SourceAccepted)] = HandoffStates.Accepted,
            [(HandoffStates.Requested, HandoffEvent.AcceptTimeout)] = HandoffStates.Expired,
            [(HandoffStates.Requested, HandoffEvent.DeadlineReached)] = HandoffStates.Expired,
            [(HandoffStates.Requested, HandoffEvent.Cancel)] = HandoffStates.Cancelled,
            [(HandoffStates.Requested, HandoffEvent.SourceAborted)] = HandoffStates.Aborted,
            [(HandoffStates.Requested, HandoffEvent.ServerRestart)] = HandoffStates.Aborted,
            [(HandoffStates.Accepted, HandoffEvent.SourceDraining)] = HandoffStates.Draining,
            [(HandoffStates.Accepted, HandoffEvent.DeadlineReached)] = HandoffStates.Expired,
            [(HandoffStates.Accepted, HandoffEvent.Cancel)] = HandoffStates.Cancelled,
            [(HandoffStates.Accepted, HandoffEvent.SourceAborted)] = HandoffStates.Aborted,
            [(HandoffStates.Accepted, HandoffEvent.ServerRestart)] = HandoffStates.Aborted,
            [(HandoffStates.Draining, HandoffEvent.Ready)] = HandoffStates.Ready,
            [(HandoffStates.Draining, HandoffEvent.DeadlineReached)] = HandoffStates.Expired,
            [(HandoffStates.Draining, HandoffEvent.Cancel)] = HandoffStates.Cancelled,
            [(HandoffStates.Draining, HandoffEvent.SourceAborted)] = HandoffStates.Aborted,
            [(HandoffStates.Draining, HandoffEvent.ServerRestart)] = HandoffStates.Aborted,
            [(HandoffStates.Ready, HandoffEvent.VerifyPassed)] = HandoffStates.Committed,
            [(HandoffStates.Ready, HandoffEvent.VerifyFailed)] = HandoffStates.Aborted,
            [(HandoffStates.Ready, HandoffEvent.ServerRestart)] = HandoffStates.Ready,
            [(HandoffStates.Ready, HandoffEvent.Cancel)] = HandoffStates.Cancelled,
            [(HandoffStates.Ready, HandoffEvent.SourceAborted)] = HandoffStates.Aborted,
        };

        foreach (var state in HandoffStates.All.Append("future").Append(null))
        {
            foreach (var e in Enum.GetValues<HandoffEvent>())
            {
                var want = state is not null && expected.TryGetValue((state, e), out var next) ? next : null;
                LeaseRules.Transition(state, e).Should().Be(want, $"{state ?? "null"} × {e}");
            }
        }
    }

    [Fact]
    public void 結束的狀態不再變_進行中的狀態對得上唯一索引()
    {
        foreach (var state in HandoffStates.All.Where(HandoffStates.IsTerminal))
        {
            foreach (var e in Enum.GetValues<HandoffEvent>())
                LeaseRules.Transition(state, e).Should().BeNull($"{state} × {e}");
        }

        HandoffStates.Active.Should().Equal("requested", "accepted", "draining", "ready");
        HandoffStates.All.Where(HandoffStates.IsActive).Should().Equal(HandoffStates.Active);
        HandoffStates.All.Where(s => !HandoffStates.IsActive(s)).Should().OnlyContain(s => HandoffStates.IsTerminal(s));
    }

    [Fact]
    public void 期限_afterTurn30分_now3分_接受15秒()
    {
        LeaseRules.DeadlineFor(HandoffModes.AfterTurn, Started).Should().Be(Started + 30 * 60 * Second);
        LeaseRules.DeadlineFor(HandoffModes.Now, Started).Should().Be(Started + 3 * 60 * Second);
        LeaseRules.AcceptDeadline(Started).Should().Be(Started + 15 * Second);
        FluentActions.Invoking(() => LeaseRules.DeadlineFor("later", Started)).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── 還原後的認領（5.4 第 2 點） ──

    [Fact]
    public void 還原後的認領_recovering的created_作者不同而且epoch較大()
    {
        LeaseRules.IsRecoveringClaim(true, ConversationEventKinds.Created, DevA, rowEpoch: 1, author: DevB, eventEpoch: 2).Should().BeTrue();
        LeaseRules.IsRecoveringClaim(true, ConversationEventKinds.Created, DevA, rowEpoch: 1, author: DevB, eventEpoch: 5).Should().BeTrue("只要變大");
        LeaseRules.IsRecoveringClaim(true, ConversationEventKinds.Created, DevA, rowEpoch: 2, author: DevB, eventEpoch: 2).Should().BeFalse("epoch 相同照舊 not_owner");
        LeaseRules.IsRecoveringClaim(true, ConversationEventKinds.Created, DevA, rowEpoch: 3, author: DevB, eventEpoch: 2).Should().BeFalse("較小的在這之前就是 fenced");
        LeaseRules.IsRecoveringClaim(true, ConversationEventKinds.Created, DevA, rowEpoch: 1, author: DevA, eventEpoch: 2).Should().BeFalse("作者是擁有者：照舊解除 recovering");
        LeaseRules.IsRecoveringClaim(false, ConversationEventKinds.Created, DevA, rowEpoch: 1, author: DevB, eventEpoch: 2).Should().BeFalse("不是 recovering");
        LeaseRules.IsRecoveringClaim(true, ConversationEventKinds.Meta, DevA, rowEpoch: 1, author: DevB, eventEpoch: 2).Should().BeFalse("只有 conv.created");
        LeaseRules.IsRecoveringClaim(true, ConversationEventKinds.Created, null, rowEpoch: 1, author: DevB, eventEpoch: 2).Should().BeTrue("列上沒有擁有者也能認領");
    }
}
