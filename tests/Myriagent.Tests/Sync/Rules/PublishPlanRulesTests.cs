using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 發布計畫（WP4.3 計畫 D6、D21、D25、D38）：未知的對話先補 created、依政策過濾、刪除之後全部丟掉；政策調小時的轉換；
/// 即時更新的本機條件；conv1 的宣告條件（伺服器沒有開關，DLP 版本不夠就不宣告）
/// </summary>
public sealed class PublishPlanRulesTests
{
    private static readonly PublishKnowledge Unknown = new(CreatedWritten: false, Blocked: null, Deleted: false, ServerDeleted: false);

    private static readonly PublishKnowledge Known = new(CreatedWritten: true, Blocked: null, Deleted: false, ServerDeleted: false);

    private static readonly PublishOpKind[] AllKinds = Enum.GetValues<PublishOpKind>();

    private static PublishOp Op(PublishOpKind kind, string? itemId = null) => new(kind, itemId);

    [Fact]
    public void KindOf_九種操作對到的事件種類()
    {
        AllKinds.Should().HaveCount(9);
        PublishPlanRules.KindOf(PublishOpKind.Created).Should().Be("conv.created");
        PublishPlanRules.KindOf(PublishOpKind.Meta).Should().Be("conv.meta");
        PublishPlanRules.KindOf(PublishOpKind.TurnStarted).Should().Be("conv.turn");
        PublishPlanRules.KindOf(PublishOpKind.TurnCompleted).Should().Be("conv.turn");
        PublishPlanRules.KindOf(PublishOpKind.Item).Should().Be("conv.item");
        PublishPlanRules.KindOf(PublishOpKind.ItemRemoved).Should().Be("conv.itemRemoved");
        PublishPlanRules.KindOf(PublishOpKind.Queue).Should().Be("conv.queue");
        PublishPlanRules.KindOf(PublishOpKind.Attention).Should().Be("conv.attention");
        PublishPlanRules.KindOf(PublishOpKind.Deleted).Should().Be("conv.deleted");
        AllKinds.Select(PublishPlanRules.KindOf).Distinct().Should().BeEquivalentTo(ConversationEventKinds.DeviceAuthored, "裝置寫的每種對話事件都有操作（conv.lease 只由伺服器寫）");
        FluentActions.Invoking(() => PublishPlanRules.KindOf((PublishOpKind)99)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 未知_前面補一個Created_只補一次()
    {
        var ops = new[] { Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Meta), Op(PublishOpKind.Item, "b"), Op(PublishOpKind.Created), Op(PublishOpKind.Queue) };

        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Unknown, ops).Should().Equal(
            Op(PublishOpKind.Created), Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Meta), Op(PublishOpKind.Item, "b"), Op(PublishOpKind.Queue));
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Unknown, [Op(PublishOpKind.Created)]).Should().Equal(Op(PublishOpKind.Created));
        PublishPlanRules.Plan(ConversationSyncPolicies.Metadata, Unknown, [Op(PublishOpKind.Item, "a")]).Should().Equal(
            new[] { Op(PublishOpKind.Created) }, "metadata 的對話也要出現在對話清單裡，內容不發");
    }

    [Fact]
    public void 已知_輸入裡的Created照原位保留()
        => PublishPlanRules.Plan(ConversationSyncPolicies.Full, Known, [Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Created)])
            .Should().Equal(new[] { Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Created) }, "已知的對話重送 created 是冪等的（6.3 的 C3），不再前面補一個");

    [Fact]
    public void 四種政策對九種操作的表()
    {
        var cells = 0;
        foreach (var policy in ConversationSyncPolicies.All)
        {
            foreach (var kind in AllKinds)
            {
                var op = Op(kind, "x");
                var publishes = ConversationSyncPolicies.Publishes(policy, PublishPlanRules.KindOf(kind));

                var known = PublishPlanRules.Plan(policy, Known, [op]);
                if (kind == PublishOpKind.Deleted)
                    known.Should().Equal(new[] { op }, $"{policy}：已知對話的刪除一律送（localOnly 時就是伺服器上的刪除）");
                else
                    known.Should().Equal(publishes ? new[] { op } : [], $"{policy} × {kind}：與 Publishes 相同");

                var unknown = PublishPlanRules.Plan(policy, Unknown, [op]);
                var expected = new List<PublishOp>();
                if (kind != PublishOpKind.Deleted)
                {
                    if (ConversationSyncPolicies.Publishes(policy, ConversationEventKinds.Created))
                        expected.Add(Op(PublishOpKind.Created));
                    if (kind != PublishOpKind.Created && publishes)
                        expected.Add(op);
                }

                unknown.Should().Equal(expected, $"{policy} × {kind}（未知的對話）");
                cells++;
            }
        }

        cells.Should().Be(36);
    }

    [Fact]
    public void localOnly的已知對話_Deleted保留()
    {
        PublishPlanRules.Plan(ConversationSyncPolicies.LocalOnly, Known, [Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Meta), Op(PublishOpKind.Deleted)])
            .Should().Equal(Op(PublishOpKind.Deleted));
        PublishPlanRules.Plan("Full", Known, [Op(PublishOpKind.Meta), Op(PublishOpKind.Deleted)])
            .Should().Equal(new[] { Op(PublishOpKind.Deleted) }, "不認得的政策（大小寫不同）是 localOnly");
    }

    [Fact]
    public void 未知而且只有Deleted_回空()
    {
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Unknown, [Op(PublishOpKind.Deleted)]).Should().BeEmpty("從沒上傳過的對話被刪");
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Unknown, [Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Meta), Op(PublishOpKind.Deleted)])
            .Should().BeEmpty("同一輪就被刪掉的未知對話也不必先建立再刪除（計畫附錄 B 步 2）");
    }

    [Fact]
    public void 換實例後還沒重新發布_曾經建立過的刪除只送Deleted_回傳輸入的那個操作()
    {
        var restored = Unknown with { EverCreated = true };
        var deleted = Op(PublishOpKind.Deleted);

        var planned = PublishPlanRules.Plan(ConversationSyncPolicies.Full, restored, [Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Meta), deleted]);
        planned.Should().Equal(new[] { deleted }, "還原的伺服器上可能還有它：不先建立，只送刪除（合併前審查 F3）");
        planned[0].Should().BeSameAs(deleted, "呼叫端以參考對回變化");
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, restored, [Op(PublishOpKind.Item, "a")]).Should().Equal(
            Op(PublishOpKind.Created), Op(PublishOpKind.Item, "a"));
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Unknown with { ServerDeleted = true }, [Op(PublishOpKind.Meta), deleted])[0].Should().BeSameAs(deleted);
    }

    [Fact]
    public void Transition_換實例後還沒重新發布_改localOnly是Tombstone_降級是MetaOnly_full是None()
    {
        var restored = Unknown with { EverCreated = true };

        PublishPlanRules.Transition(ConversationSyncPolicies.Full, ConversationSyncPolicies.LocalOnly, restored).Should().Be(PolicyTransition.Tombstone,
            "還原的伺服器上可能還有它：localOnly＝在伺服器上刪除（合併前審查 F3）");
        PublishPlanRules.Transition(null, ConversationSyncPolicies.LocalOnly, restored).Should().Be(PolicyTransition.Tombstone);
        PublishPlanRules.Transition(null, ConversationSyncPolicies.Full, restored with { ServerDeleted = true }).Should().Be(PolicyTransition.Tombstone);
        // outbox 可能還有換實例之前寫的待送內容：降級要清掉它們（重新發布時的 created 照樣帶新的政策；第二輪合併前審查 PRIV-3）
        PublishPlanRules.Transition(null, ConversationSyncPolicies.Metadata, restored).Should().Be(PolicyTransition.MetaOnly);
        PublishPlanRules.Transition(null, ConversationSyncPolicies.Paused, restored).Should().Be(PolicyTransition.MetaOnly);
        PublishPlanRules.Transition(ConversationSyncPolicies.Metadata, ConversationSyncPolicies.Metadata, restored).Should().Be(PolicyTransition.MetaOnly,
            "換實例時發布的政策已重設：不知道之前是什麼");
        PublishPlanRules.Transition(null, ConversationSyncPolicies.Full, restored).Should().Be(PolicyTransition.None, "等重新發布");
        PublishPlanRules.Transition(ConversationSyncPolicies.Metadata, ConversationSyncPolicies.Full, restored).Should().Be(PolicyTransition.None);
        PublishPlanRules.Transition(null, ConversationSyncPolicies.LocalOnly, restored with { Deleted = true }).Should().Be(PolicyTransition.None, "已有墓碑");
    }

    [Fact]
    public void 有伺服器刪除旁檔_只送Deleted()
    {
        var unknownDeleted = Unknown with { ServerDeleted = true };
        var knownDeleted = Known with { ServerDeleted = true };

        PublishPlanRules.Plan(ConversationSyncPolicies.Full, unknownDeleted, [Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Deleted)])
            .Should().Equal(new[] { Op(PublishOpKind.Deleted) }, "旁檔的對話一律是 localOnly，不補 created；刪除照送（換實例後補送）");
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, knownDeleted, [Op(PublishOpKind.Meta), Op(PublishOpKind.Item, "a")])
            .Should().BeEmpty("有旁檔就不能再同步，即使呼叫端給的政策是 full");
        PublishPlanRules.Plan(ConversationSyncPolicies.Metadata, knownDeleted, [Op(PublishOpKind.Meta), Op(PublishOpKind.Deleted)])
            .Should().Equal(Op(PublishOpKind.Deleted));
    }

    [Fact]
    public void 保持輸入順序()
    {
        var ops = new[]
        {
            Op(PublishOpKind.Item, "a"), Op(PublishOpKind.TurnStarted), Op(PublishOpKind.Item, "b"), Op(PublishOpKind.Meta),
            Op(PublishOpKind.Item, "a"), Op(PublishOpKind.TurnCompleted), Op(PublishOpKind.Meta), Op(PublishOpKind.Attention),
            Op(PublishOpKind.Queue), Op(PublishOpKind.ItemRemoved, "b"),
        };

        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Known, ops).Should().Equal(ops);
        PublishPlanRules.Plan(ConversationSyncPolicies.Paused, Known, ops).Should().Equal(
            Op(PublishOpKind.TurnStarted), Op(PublishOpKind.Meta), Op(PublishOpKind.TurnCompleted), Op(PublishOpKind.Meta), Op(PublishOpKind.Attention));
    }

    [Fact]
    public void Deleted之後全部丟掉()
    {
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Known,
                [Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Deleted), Op(PublishOpKind.Meta), Op(PublishOpKind.Item, "b"), Op(PublishOpKind.Deleted)])
            .Should().Equal(Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Deleted));
    }

    [Fact]
    public void 封鎖或墓碑_全部丟掉()
    {
        var ops = new[] { Op(PublishOpKind.Created), Op(PublishOpKind.Meta), Op(PublishOpKind.Item, "a"), Op(PublishOpKind.Deleted) };

        foreach (var knowledge in new[]
                 {
                     Known with { Blocked = ConversationRejectCodes.NotOwner },
                     Known with { Blocked = "bug" },
                     Known with { Deleted = true },
                     Unknown with { Deleted = true },
                     Unknown with { Blocked = ConversationRejectCodes.Fenced, ServerDeleted = true },
                 })
        {
            foreach (var policy in ConversationSyncPolicies.All)
                PublishPlanRules.Plan(policy, knowledge, ops).Should().BeEmpty($"{knowledge} {policy}");
        }
    }

    [Fact]
    public void 沒有操作_回空()
    {
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Unknown, []).Should().BeEmpty("沒有變化就不補 created");
        PublishPlanRules.Plan(ConversationSyncPolicies.Full, Known, []).Should().BeEmpty();
    }

    /// <summary>已知、沒有墓碑、沒有旁檔的對話：發布時的政策（null＝沒記錄，當成 full）× 現在的有效政策</summary>
    [Theory]
    [InlineData(null, "full", nameof(PolicyTransition.None))]
    [InlineData(null, "metadata", nameof(PolicyTransition.MetaOnly))]
    [InlineData(null, "paused", nameof(PolicyTransition.MetaOnly))]
    [InlineData(null, "localOnly", nameof(PolicyTransition.Tombstone))]
    [InlineData("full", "full", nameof(PolicyTransition.None))]
    [InlineData("full", "metadata", nameof(PolicyTransition.MetaOnly))]
    [InlineData("full", "paused", nameof(PolicyTransition.MetaOnly))]
    [InlineData("full", "localOnly", nameof(PolicyTransition.Tombstone))]
    [InlineData("metadata", "full", nameof(PolicyTransition.Republish))]
    [InlineData("metadata", "metadata", nameof(PolicyTransition.None))]
    [InlineData("metadata", "paused", nameof(PolicyTransition.MetaOnly))]
    [InlineData("metadata", "localOnly", nameof(PolicyTransition.Tombstone))]
    [InlineData("paused", "full", nameof(PolicyTransition.Republish))]
    [InlineData("paused", "metadata", nameof(PolicyTransition.MetaOnly))]
    [InlineData("paused", "paused", nameof(PolicyTransition.None))]
    [InlineData("paused", "localOnly", nameof(PolicyTransition.Tombstone))]
    [InlineData("localOnly", "full", nameof(PolicyTransition.Republish))]
    [InlineData("localOnly", "metadata", nameof(PolicyTransition.MetaOnly))]
    [InlineData("localOnly", "paused", nameof(PolicyTransition.MetaOnly))]
    [InlineData("localOnly", "localOnly", nameof(PolicyTransition.Tombstone))]
    [InlineData("full", "Full", nameof(PolicyTransition.Tombstone))]
    [InlineData("full", "future", nameof(PolicyTransition.Tombstone))]
    public void Transition表_已知的對話(string? published, string current, string expected)
        => PublishPlanRules.Transition(published, current, Known).Should().Be(Enum.Parse<PolicyTransition>(expected));

    [Fact]
    public void Transition表_未知_墓碑_旁檔()
    {
        string?[] published = [null, .. ConversationSyncPolicies.All];
        foreach (var from in published)
        {
            foreach (var to in ConversationSyncPolicies.All)
            {
                PublishPlanRules.Transition(from, to, Unknown).Should().Be(PolicyTransition.None, $"沒發布過：{from}→{to}");
                PublishPlanRules.Transition(from, to, Unknown with { ServerDeleted = true }).Should().Be(PolicyTransition.None, $"沒發布過（有旁檔）：{from}→{to}");
                PublishPlanRules.Transition(from, to, Known with { Deleted = true }).Should().Be(PolicyTransition.None, $"已有墓碑：{from}→{to}");
                PublishPlanRules.Transition(from, to, Known with { Deleted = true, ServerDeleted = true }).Should().Be(PolicyTransition.None);
                PublishPlanRules.Transition(from, to, Known with { ServerDeleted = true }).Should().Be(PolicyTransition.Tombstone,
                    $"有旁檔、還沒有墓碑（寫旁檔與寫 outbox 之間當機）：{from}→{to}");
            }
        }
    }

    [Fact]
    public void LiveAllowed_政策不是full或封鎖或草稿或複本或未知都是false()
    {
        PublishPlanRules.LiveAllowed(ConversationSyncPolicies.Full, Known, isDraft: false, isReplica: false).Should().BeTrue();

        foreach (var policy in new[] { ConversationSyncPolicies.Metadata, ConversationSyncPolicies.Paused, ConversationSyncPolicies.LocalOnly, "Full", "x" })
            PublishPlanRules.LiveAllowed(policy, Known, false, false).Should().BeFalse(policy);
        PublishPlanRules.LiveAllowed(ConversationSyncPolicies.Full, Known with { Blocked = ConversationRejectCodes.NotOwner }, false, false).Should().BeFalse("封鎖");
        PublishPlanRules.LiveAllowed(ConversationSyncPolicies.Full, Known with { Deleted = true }, false, false).Should().BeFalse("墓碑");
        PublishPlanRules.LiveAllowed(ConversationSyncPolicies.Full, Known with { ServerDeleted = true }, false, false).Should().BeFalse("伺服器刪除的旁檔");
        PublishPlanRules.LiveAllowed(ConversationSyncPolicies.Full, Unknown, false, false).Should().BeFalse("未知");
        PublishPlanRules.LiveAllowed(ConversationSyncPolicies.Full, Known, isDraft: true, isReplica: false).Should().BeFalse("草稿");
        PublishPlanRules.LiveAllowed(ConversationSyncPolicies.Full, Known, isDraft: false, isReplica: true).Should().BeFalse("複本");
    }

    [Fact]
    public void DeclaresConv1_DLP版本低於必要版本是false()
    {
        PublishPlanRules.DeclaresConv1(hasBox: true, tablesReady: true, dlpVersion: 2, requiredDlpVersion: 2).Should().BeTrue("版本相等");
        PublishPlanRules.DeclaresConv1(true, true, 3, 2).Should().BeTrue("版本較新");
        PublishPlanRules.DeclaresConv1(true, true, 1, 2).Should().BeFalse("DLP 版本低於必要版本：伺服器沒有開關，不能讓沒補洞的組建發布");
        PublishPlanRules.DeclaresConv1(true, true, 0, 2).Should().BeFalse();
        PublishPlanRules.DeclaresConv1(hasBox: false, tablesReady: true, 2, 2).Should().BeFalse("沒有盒子");
        PublishPlanRules.DeclaresConv1(hasBox: true, tablesReady: false, 2, 2).Should().BeFalse("v3 的表不在");
        PublishPlanRules.DeclaresConv1(false, false, 1, 2).Should().BeFalse();
    }
}
