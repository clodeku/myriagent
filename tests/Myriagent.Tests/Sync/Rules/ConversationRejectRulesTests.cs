using System.Reflection;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 對話事件被拒時的處置（WP4.3 計畫 D14、6.4 的拒絕碼表「桌面」欄）。invalid 的判斷順序：沒有 conv1 一律重整（含 conv.deleted）→
/// conv1 上的 conv.deleted 當成功 → created 未確認時補送 created（1／10／60 分鐘，第 4 次封鎖）→ 其餘是程式錯誤
/// </summary>
public sealed class ConversationRejectRulesTests
{
    private static readonly string[] MetaKindsExceptDeleted =
        [ConversationEventKinds.Created, ConversationEventKinds.Meta, ConversationEventKinds.Turn, ConversationEventKinds.Attention];

    private static readonly string[] ItemKinds = [ConversationEventKinds.Item, ConversationEventKinds.ItemRemoved, ConversationEventKinds.Queue];

    /// <summary>6.4 的表（桌面欄），逐列寫出；不看補送次數（次數另有測試）</summary>
    private static RejectAction Expected(string? code, string kind, bool conv1, bool createdAcked)
    {
        var isDeleted = kind == ConversationEventKinds.Deleted;
        var isItem = ItemKinds.Contains(kind);
        return code switch
        {
            null => RejectAction.Accepted,
            "not_owner" => RejectAction.BlockNotOwner,
            "fenced" => RejectAction.Block,
            "deleted" => isDeleted ? RejectAction.Accepted : RejectAction.Block,
            "dlp" => isItem ? RejectAction.PauseDlp : RejectAction.Block,
            "invalid" when !conv1 => RejectAction.Redo,
            "invalid" when isDeleted => RejectAction.Accepted,
            "invalid" when !createdAcked => RejectAction.RetryCreated,
            "invalid" => RejectAction.Bug,
            "too_large" => conv1 ? RejectAction.Bug : RejectAction.Redo,
            _ => RejectAction.Bug,
        };
    }

    private static IEnumerable<string?> AllCodes()
    {
        var syncErrors = typeof(SyncErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue());
        return ConversationRejectCodes.All.Concat(syncErrors).Concat(["unknown_conversation", "conflict", "recovering", "NOT_OWNER", "whatever", null]).Distinct();
    }

    [Fact]
    public void 表驅動_所有碼乘conv1乘created確認乘三種種類()
    {
        var rows = 0;
        foreach (var code in AllCodes())
        foreach (var conv1 in new[] { false, true })
        foreach (var createdAcked in new[] { false, true })
        foreach (var kind in new[] { ConversationEventKinds.Deleted }.Concat(MetaKindsExceptDeleted).Concat(ItemKinds))
        {
            var decision = ConversationRejectRules.Classify(code, kind, conv1, createdAcked, createdRetries: 0);
            var expected = Expected(code, kind, conv1, createdAcked);
            decision.Action.Should().Be(expected, $"code={code ?? "null"} kind={kind} conv1={conv1} createdAcked={createdAcked}");
            decision.RetryAfterMs.Should().Be(expected == RejectAction.RetryCreated ? 60_000L : null);
            rows++;
        }

        rows.Should().BeGreaterThan(2 * 2 * 8 * 12, "四個拒絕碼、第 3 階段的所有錯誤碼與其他碼都跑過");
    }

    [Fact]
    public void conv1上created未確認的invalid_重送created_第4次封鎖()
    {
        foreach (var kind in MetaKindsExceptDeleted.Concat(ItemKinds))
        {
            ConversationRejectRules.Classify("invalid", kind, true, false, 0).Action.Should().Be(RejectAction.RetryCreated, kind);
            ConversationRejectRules.Classify("invalid", kind, true, false, 1).Action.Should().Be(RejectAction.RetryCreated, kind);
            ConversationRejectRules.Classify("invalid", kind, true, false, 2).Action.Should().Be(RejectAction.RetryCreated, kind);
            ConversationRejectRules.Classify("invalid", kind, true, false, 3)
                .Should().Be(new RejectDecision(RejectAction.BlockNotOwner, null), $"{kind}：第 4 次封鎖並併進彙總提示");
            ConversationRejectRules.Classify("invalid", kind, true, false, 100).Action.Should().Be(RejectAction.BlockNotOwner, "次數有上限");
        }

        ConversationRejectRules.MaxCreatedRetries.Should().Be(3);
    }

    [Fact]
    public void 退避1_10_60分鐘()
    {
        ConversationRejectRules.Classify("invalid", ConversationEventKinds.Item, true, false, 0).Should().Be(new RejectDecision(RejectAction.RetryCreated, 60_000));
        ConversationRejectRules.Classify("invalid", ConversationEventKinds.Item, true, false, 1).Should().Be(new RejectDecision(RejectAction.RetryCreated, 600_000));
        ConversationRejectRules.Classify("invalid", ConversationEventKinds.Item, true, false, 2).Should().Be(new RejectDecision(RejectAction.RetryCreated, 3_600_000));
        ConversationRejectRules.Classify("invalid", ConversationEventKinds.Item, true, false, -1).Should().Be(new RejectDecision(RejectAction.RetryCreated, 60_000), "負的次數當成 0");
        ConversationRejectRules.CreatedRetryDelaysMs.Should().Equal(60_000L, 600_000L, 3_600_000L);
    }

    [Fact]
    public void deleted碼_對conv_deleted是成功_對其他是永久封鎖()
    {
        foreach (var conv1 in new[] { false, true })
        foreach (var acked in new[] { false, true })
        {
            ConversationRejectRules.Classify("deleted", ConversationEventKinds.Deleted, conv1, acked, 0).Action.Should().Be(RejectAction.Accepted);
            foreach (var kind in MetaKindsExceptDeleted.Concat(ItemKinds))
                ConversationRejectRules.Classify("deleted", kind, conv1, acked, 0).Action.Should().Be(RejectAction.Block, kind);
        }
    }

    [Fact]
    public void conv1上conv_deleted的invalid是成功()
    {
        ConversationRejectRules.Classify("invalid", ConversationEventKinds.Deleted, conv1: true, createdAcked: false, 0).Action.Should().Be(RejectAction.Accepted, "伺服器不認得的對話");
        ConversationRejectRules.Classify("invalid", ConversationEventKinds.Deleted, conv1: true, createdAcked: true, 3).Action.Should().Be(RejectAction.Accepted);
    }

    [Fact]
    public void 沒有conv1_invalid與too_large一律Redo_含conv_deleted()
    {
        foreach (var kind in ConversationEventKinds.All)
        foreach (var acked in new[] { false, true })
        foreach (var code in new[] { "invalid", "too_large" })
        {
            ConversationRejectRules.Classify(code, kind, conv1: false, acked, 3).Should().Be(new RejectDecision(RejectAction.Redo, null),
                $"{code} {kind}：舊伺服器擋下的，下一次 conv1 連線補送（回滾期間的刪除不能當成完成）");
        }
    }

    [Fact]
    public void conv1上已確認的invalid與所有too_large是Bug()
    {
        foreach (var kind in MetaKindsExceptDeleted.Concat(ItemKinds))
            ConversationRejectRules.Classify("invalid", kind, true, true, 0).Action.Should().Be(RejectAction.Bug, kind);
        foreach (var kind in ConversationEventKinds.All)
        foreach (var acked in new[] { false, true })
            ConversationRejectRules.Classify("too_large", kind, true, acked, 0).Action.Should().Be(RejectAction.Bug, kind);
    }

    [Fact]
    public void unknown_conversation與conflict是Bug()
    {
        foreach (var code in new[] { "unknown_conversation", "conflict", "gap", "seq_regression", "protocol", "not_proven", "Fenced", "" })
        foreach (var kind in ConversationEventKinds.All)
        foreach (var conv1 in new[] { false, true })
            ConversationRejectRules.Classify(code, kind, conv1, false, 0).Action.Should().Be(RejectAction.Bug, $"{code} {kind} {conv1}");
    }

    [Fact]
    public void dlp_item類暫停_meta類封鎖()
    {
        foreach (var conv1 in new[] { false, true })
        {
            foreach (var kind in ItemKinds)
                ConversationRejectRules.Classify("dlp", kind, conv1, true, 0).Action.Should().Be(RejectAction.PauseDlp, kind);
            foreach (var kind in MetaKindsExceptDeleted.Append(ConversationEventKinds.Deleted))
                ConversationRejectRules.Classify("dlp", kind, conv1, true, 0).Action.Should().Be(RejectAction.Block, $"{kind}：本機已先遮蔽仍被擋，不再送（paused 照樣送 meta）");
        }
    }

    [Fact]
    public void not_owner與fenced_封鎖()
    {
        foreach (var kind in ConversationEventKinds.All)
        {
            ConversationRejectRules.Classify("not_owner", kind, true, true, 0).Action.Should().Be(RejectAction.BlockNotOwner, kind);
            ConversationRejectRules.Classify("fenced", kind, true, true, 0).Action.Should().Be(RejectAction.Block, kind);
        }
    }

    [Fact]
    public void 接受_與不是對話種類()
    {
        foreach (var kind in ConversationEventKinds.All)
            ConversationRejectRules.Classify(null, kind, false, false, 5).Should().Be(new RejectDecision(RejectAction.Accepted, null), kind);
        ConversationRejectRules.Classify("invalid", SyncEventKinds.LinkUpsert, true, false, 0).Action.Should().Be(RejectAction.Bug, "第 3 階段的種類不該進到這裡");
        ConversationRejectRules.Classify("not_owner", "conv.whatever", true, false, 0).Action.Should().Be(RejectAction.Bug);
    }
}
