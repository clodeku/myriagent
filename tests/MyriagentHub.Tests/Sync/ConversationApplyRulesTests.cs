using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Sync;

/// <summary>對話事件的授權與動作（WP4.2 計畫步 2；D4、D5、D7、D9、D10、D16、D34、P2、提案 C3、C5、C10）</summary>
public sealed class ConversationApplyRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private const string DevA = "dev-A";

    private const string DevB = "dev-B";

    private static ConversationRow Row(string policy = ConversationSyncPolicies.Full, bool deleted = false, long epoch = 1, string? owner = DevA) =>
        new(Conv, owner, epoch, policy, deleted, Recovering: false);

    // ── 建立 ──

    [Theory]
    [InlineData(ConversationEventKinds.Created)]
    [InlineData(ConversationEventKinds.Meta)]
    public void 未知對話_meta當成建立_擁有者是作者(string kind)
    {
        var decision = ConversationApplyRules.Decide(null, DevB, kind, 3, ConversationSyncPolicies.Metadata);
        decision.Rejected.Should().BeNull();
        decision.Action.Should().Be(ConversationAction.Create);
        decision.Owner.Should().Be(DevB, "擁有者＝作者");
        decision.Epoch.Should().Be(3, "epoch 取事件上的值");
        decision.PurgeContent.Should().BeFalse();
    }

    [Theory]
    [InlineData(ConversationEventKinds.Turn)]
    [InlineData(ConversationEventKinds.Attention)]
    [InlineData(ConversationEventKinds.Deleted)]
    [InlineData(ConversationEventKinds.Item)]
    [InlineData(ConversationEventKinds.ItemRemoved)]
    [InlineData(ConversationEventKinds.Queue)]
    public void 未知對話_turn_attention_deleted與item類回invalid_不建立列(string kind)
    {
        var decision = ConversationApplyRules.Decide(null, DevA, kind, 1, null);
        decision.Rejected.Should().Be(SyncErrorCodes.Invalid);
        decision.Action.Should().BeNull("不建立列（P2）");
    }

    [Theory]
    [InlineData(ConversationEventKinds.Created, ConversationSyncPolicies.LocalOnly)]
    [InlineData(ConversationEventKinds.Meta, ConversationSyncPolicies.LocalOnly)]
    [InlineData(ConversationEventKinds.Created, "LocalOnly")]
    [InlineData(ConversationEventKinds.Created, "someday")]
    public void 沒有列_政策localOnly的created_invalid(string kind, string policy)
    {
        var decision = ConversationApplyRules.Decide(null, DevA, kind, 1, policy);
        decision.Rejected.Should().Be(SyncErrorCodes.Invalid, "localOnly（含不認得的值）的對話不應該有任何事件");
        decision.Action.Should().BeNull();
    }

    [Fact]
    public void 不是對話種類_invalid()
    {
        ConversationApplyRules.Decide(null, DevA, "link.upsert", 1, ConversationSyncPolicies.Full).Rejected.Should().Be(SyncErrorCodes.Invalid);
        ConversationApplyRules.Decide(Row(), DevA, "conv.unknown", 1, null).Rejected.Should().Be(SyncErrorCodes.Invalid);
    }

    // ── 授權 ──

    [Fact]
    public void 作者不是擁有者_not_owner並帶目前擁有者()
    {
        foreach (var kind in ConversationEventKinds.DeviceAuthored)
        {
            var decision = ConversationApplyRules.Decide(Row(), DevB, kind, 1, kind is ConversationEventKinds.Created or ConversationEventKinds.Meta ? "full" : null);
            decision.Rejected.Should().Be(ConversationRejectCodes.NotOwner, kind);
            decision.Owner.Should().Be("dev-A");
            decision.Epoch.Should().BeNull("not_owner 不帶 epoch");
            decision.Action.Should().BeNull();
        }
    }

    [Fact]
    public void B送A的id的created_not_owner()
    {
        var decision = ConversationApplyRules.Decide(Row(), DevB, ConversationEventKinds.Created, 1, ConversationSyncPolicies.Full);
        decision.Rejected.Should().Be(ConversationRejectCodes.NotOwner);
        decision.Owner.Should().Be(DevA);
    }

    [Fact]
    public void 列上沒有擁有者_誰都是not_owner()
    {
        var decision = ConversationApplyRules.Decide(Row(owner: null), DevA, ConversationEventKinds.Meta, 1, ConversationSyncPolicies.Full);
        decision.Rejected.Should().Be(ConversationRejectCodes.NotOwner);
        decision.Owner.Should().BeNull();
    }

    [Fact]
    public void 不是擁有者而且epoch比較新_先回not_owner()
    {
        var decision = ConversationApplyRules.Decide(Row(epoch: 1), DevB, ConversationEventKinds.Item, 2, null);
        decision.Rejected.Should().Be(ConversationRejectCodes.NotOwner, "判斷順序：epoch 沒有比較舊時 not_owner 在 fenced 之前");
    }

    [Fact]
    public void epoch比列上的舊_在擁有者檢查之前回fenced()
    {
        // 第 5 階段 D4：交接後原擁有者（不再是擁有者）的遲到事件當成圍欄，不是 not_owner（桌面會封鎖並併成「重新配對」的提示）
        foreach (var kind in ConversationEventKinds.DeviceAuthored)
        {
            var decision = ConversationApplyRules.Decide(Row(epoch: 3, owner: DevB), DevA, kind, 2,
                kind is ConversationEventKinds.Created or ConversationEventKinds.Meta ? ConversationSyncPolicies.Full : null);
            decision.Rejected.Should().Be(ConversationRejectCodes.Fenced, kind);
            decision.Epoch.Should().Be(3, "帶列上的 epoch");
            decision.Owner.Should().BeNull();
        }

        ConversationApplyRules.Decide(Row(epoch: 3, owner: DevB, deleted: true), DevA, ConversationEventKinds.Item, 1, null)
            .Rejected.Should().Be(ConversationRejectCodes.Fenced, "比已刪除更早判斷");
        ConversationApplyRules.Decide(Row(epoch: 3, owner: DevB), DevB, ConversationEventKinds.Item, 3, null)
            .Rejected.Should().BeNull("陽性對照：新擁有者以新 epoch 寫的照收");
        ConversationApplyRules.Decide(Row(epoch: 1), DevA, ConversationEventKinds.Item, 1, null)
            .Rejected.Should().BeNull("第 4 階段 epoch 一律是 1：結果與以前相同");
    }

    [Fact]
    public void 伺服器寫的conv_lease_裝置送一律invalid()
    {
        ConversationApplyRules.Decide(Row(), DevA, ConversationEventKinds.Lease, 1, null).Rejected.Should().Be(SyncErrorCodes.Invalid, "擁有者也不能送");
        ConversationApplyRules.Decide(Row(), DevB, ConversationEventKinds.Lease, 2, null).Rejected.Should().Be(SyncErrorCodes.Invalid);
        ConversationApplyRules.Decide(null, DevA, ConversationEventKinds.Lease, 1, ConversationSyncPolicies.Full).Rejected.Should().Be(SyncErrorCodes.Invalid, "不能拿它建立對話");
    }

    [Fact]
    public void epoch不同_fenced並帶目前epoch()
    {
        var decision = ConversationApplyRules.Decide(Row(epoch: 1), DevA, ConversationEventKinds.Item, 2, null);
        decision.Rejected.Should().Be(ConversationRejectCodes.Fenced);
        decision.Epoch.Should().Be(1);
        decision.Owner.Should().BeNull("fenced 不帶擁有者");
    }

    [Fact]
    public void epoch不對而且已刪除_先回fenced()
    {
        ConversationApplyRules.Decide(Row(epoch: 1, deleted: true), DevA, ConversationEventKinds.Meta, 2, ConversationSyncPolicies.Full)
            .Rejected.Should().Be(ConversationRejectCodes.Fenced);
    }

    [Fact]
    public void 已刪除_連擁有者再送created也是deleted()
    {
        foreach (var kind in ConversationEventKinds.DeviceAuthored)
        {
            var decision = ConversationApplyRules.Decide(Row(deleted: true), DevA, kind, 1,
                kind is ConversationEventKinds.Created or ConversationEventKinds.Meta ? ConversationSyncPolicies.Full : null);
            decision.Rejected.Should().Be(ConversationRejectCodes.Deleted, kind);
            decision.Action.Should().BeNull();
        }
    }

    [Fact]
    public void 已刪除而且metadata的item_先回deleted()
    {
        ConversationApplyRules.Decide(Row(ConversationSyncPolicies.Metadata, deleted: true), DevA, ConversationEventKinds.Item, 1, null)
            .Rejected.Should().Be(ConversationRejectCodes.Deleted, "判斷順序：deleted 在政策之前");
    }

    // ── 政策 ──

    /// <summary>期望表寫死（不以 Publishes 當答案）：item 類只在 full 下收，meta 類三種政策都收</summary>
    [Theory]
    [InlineData(ConversationSyncPolicies.Full, ConversationEventKinds.Item, true)]
    [InlineData(ConversationSyncPolicies.Full, ConversationEventKinds.ItemRemoved, true)]
    [InlineData(ConversationSyncPolicies.Full, ConversationEventKinds.Queue, true)]
    [InlineData(ConversationSyncPolicies.Metadata, ConversationEventKinds.Item, false)]
    [InlineData(ConversationSyncPolicies.Metadata, ConversationEventKinds.ItemRemoved, false)]
    [InlineData(ConversationSyncPolicies.Metadata, ConversationEventKinds.Queue, false)]
    [InlineData(ConversationSyncPolicies.Paused, ConversationEventKinds.Item, false)]
    [InlineData(ConversationSyncPolicies.Paused, ConversationEventKinds.ItemRemoved, false)]
    [InlineData(ConversationSyncPolicies.Paused, ConversationEventKinds.Queue, false)]
    [InlineData(ConversationSyncPolicies.Metadata, ConversationEventKinds.Turn, true)]
    [InlineData(ConversationSyncPolicies.Metadata, ConversationEventKinds.Attention, true)]
    [InlineData(ConversationSyncPolicies.Metadata, ConversationEventKinds.Deleted, true)]
    [InlineData(ConversationSyncPolicies.Paused, ConversationEventKinds.Turn, true)]
    [InlineData(ConversationSyncPolicies.Paused, ConversationEventKinds.Attention, true)]
    [InlineData(ConversationSyncPolicies.Paused, ConversationEventKinds.Deleted, true)]
    [InlineData("bogus", ConversationEventKinds.Item, false)]
    [InlineData("bogus", ConversationEventKinds.Turn, true)]
    public void item類在metadata與paused下invalid_meta類照收(string rowPolicy, string kind, bool accepted)
    {
        var decision = ConversationApplyRules.Decide(Row(rowPolicy), DevA, kind, 1, null);
        if (accepted)
        {
            decision.Rejected.Should().BeNull();
            decision.Action.Should().NotBeNull();
        }
        else
        {
            decision.Rejected.Should().Be(SyncErrorCodes.Invalid);
            decision.Action.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(ConversationSyncPolicies.Metadata)]
    [InlineData(ConversationSyncPolicies.Paused)]
    public void meta類在metadata與paused下照收(string rowPolicy)
    {
        foreach (var incoming in new[] { ConversationSyncPolicies.Full, ConversationSyncPolicies.Metadata, ConversationSyncPolicies.Paused })
        {
            ConversationApplyRules.Decide(Row(rowPolicy), DevA, ConversationEventKinds.Meta, 1, incoming).Action
                .Should().Be(ConversationAction.ReplaceMeta, $"{rowPolicy} → {incoming}");
        }
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("Full")]
    [InlineData(ConversationSyncPolicies.LocalOnly)]
    public void meta帶不認得的政策_invalid(string incoming)
    {
        foreach (var kind in new[] { ConversationEventKinds.Created, ConversationEventKinds.Meta })
        {
            var decision = ConversationApplyRules.Decide(Row(), DevA, kind, 1, incoming);
            decision.Rejected.Should().Be(SyncErrorCodes.Invalid, "Interpret 把不認得的值當成 localOnly；改成不同步一律走 conv.deleted");
            decision.Action.Should().BeNull();
        }
    }

    [Fact]
    public void 不帶政策的種類多傳的政策不算數()
    {
        ConversationApplyRules.Decide(Row(), DevA, ConversationEventKinds.Turn, 1, ConversationSyncPolicies.LocalOnly).Action
            .Should().Be(ConversationAction.Turn);
    }

    [Fact]
    public void created重送_同作者同epoch_整份取代_不拒絕()
    {
        var decision = ConversationApplyRules.Decide(Row(), DevA, ConversationEventKinds.Created, 1, ConversationSyncPolicies.Full);
        decision.Rejected.Should().BeNull();
        decision.Action.Should().Be(ConversationAction.ReplaceMeta, "P3、提案 C3：已存在的對話再送 created 是整份 meta 取代");
        decision.PurgeContent.Should().BeFalse();
    }

    [Theory]
    [InlineData(ConversationSyncPolicies.Full, ConversationSyncPolicies.Metadata, true)]
    [InlineData(ConversationSyncPolicies.Full, ConversationSyncPolicies.Paused, true)]
    [InlineData(ConversationSyncPolicies.Full, ConversationSyncPolicies.Full, false)]
    [InlineData(ConversationSyncPolicies.Metadata, ConversationSyncPolicies.Full, false)]
    [InlineData(ConversationSyncPolicies.Metadata, ConversationSyncPolicies.Paused, false)]
    [InlineData(ConversationSyncPolicies.Paused, ConversationSyncPolicies.Metadata, false)]
    [InlineData(ConversationSyncPolicies.Paused, ConversationSyncPolicies.Full, false)]
    public void full改metadata或paused_PurgeContent為真_其他轉換為假(string from, string to, bool purge)
    {
        foreach (var kind in new[] { ConversationEventKinds.Meta, ConversationEventKinds.Created })
        {
            var decision = ConversationApplyRules.Decide(Row(from), DevA, kind, 1, to);
            decision.Action.Should().Be(ConversationAction.ReplaceMeta);
            decision.PurgeContent.Should().Be(purge, $"{kind}：{from} → {to}（答 4）");
        }
    }

    [Fact]
    public void full改metadata_PurgeContent為真() =>
        ConversationApplyRules.Decide(Row(), DevA, ConversationEventKinds.Meta, 1, ConversationSyncPolicies.Metadata).PurgeContent.Should().BeTrue();

    [Fact]
    public void full改paused_PurgeContent為真() =>
        ConversationApplyRules.Decide(Row(), DevA, ConversationEventKinds.Meta, 1, ConversationSyncPolicies.Paused).PurgeContent.Should().BeTrue();

    [Theory]
    [InlineData(ConversationSyncPolicies.Full)]
    [InlineData(ConversationSyncPolicies.Metadata)]
    [InlineData(ConversationSyncPolicies.Paused)]
    public void deleted_PurgeContent為真(string rowPolicy)
    {
        var decision = ConversationApplyRules.Decide(Row(rowPolicy), DevA, ConversationEventKinds.Deleted, 1, null);
        decision.Action.Should().Be(ConversationAction.Delete);
        decision.PurgeContent.Should().BeTrue("刪除一律清掉內容（D34、答 4）");
    }

    [Fact]
    public void metadata改回full_PurgeContent為假() =>
        ConversationApplyRules.Decide(Row(ConversationSyncPolicies.Metadata), DevA, ConversationEventKinds.Meta, 1, ConversationSyncPolicies.Full)
            .PurgeContent.Should().BeFalse();

    [Fact]
    public void metadata改paused_PurgeContent為假() =>
        ConversationApplyRules.Decide(Row(ConversationSyncPolicies.Metadata), DevA, ConversationEventKinds.Meta, 1, ConversationSyncPolicies.Paused)
            .PurgeContent.Should().BeFalse();

    [Fact]
    public void 其他動作的PurgeContent為假()
    {
        foreach (var kind in new[] { ConversationEventKinds.Turn, ConversationEventKinds.Attention, ConversationEventKinds.Item, ConversationEventKinds.ItemRemoved, ConversationEventKinds.Queue })
            ConversationApplyRules.Decide(Row(), DevA, kind, 1, null).PurgeContent.Should().BeFalse(kind);
    }

    [Fact]
    public void 各種類的動作()
    {
        var expected = new Dictionary<string, ConversationAction>
        {
            [ConversationEventKinds.Created] = ConversationAction.ReplaceMeta,
            [ConversationEventKinds.Meta] = ConversationAction.ReplaceMeta,
            [ConversationEventKinds.Turn] = ConversationAction.Turn,
            [ConversationEventKinds.Attention] = ConversationAction.Attention,
            [ConversationEventKinds.Deleted] = ConversationAction.Delete,
            [ConversationEventKinds.Item] = ConversationAction.UpsertItem,
            [ConversationEventKinds.ItemRemoved] = ConversationAction.RemoveItem,
            [ConversationEventKinds.Queue] = ConversationAction.Queue,
        };
        expected.Keys.Should().BeEquivalentTo(ConversationEventKinds.DeviceAuthored, "裝置能送的每一種（conv.lease 只由伺服器寫）");
        foreach (var (kind, action) in expected)
        {
            var decision = ConversationApplyRules.Decide(Row(), DevA, kind, 1, kind is ConversationEventKinds.Created or ConversationEventKinds.Meta ? ConversationSyncPolicies.Full : null);
            decision.Rejected.Should().BeNull(kind);
            decision.Action.Should().Be(action, kind);
            decision.Epoch.Should().BeNull("接受時不帶 epoch（只有 fenced 與 Create 帶）");
            decision.Owner.Should().BeNull();
        }
    }

    [Fact]
    public void itemRemoved不看項目在不在_回RemoveItem()
    {
        // Decide 沒有項目的資訊：指向不存在的項目照樣接受，物化時什麼都不做（提案 C5，步 3）
        ConversationApplyRules.Decide(Row(), DevA, ConversationEventKinds.ItemRemoved, 1, null).Action.Should().Be(ConversationAction.RemoveItem);
    }

    [Fact]
    public void 空物件的deleted_是真的刪除()
    {
        var e = new SyncEvent(1, SyncStreams.Conversation(Conv), ConversationEventKinds.Deleted, PayloadRules.EncJson, PayloadRules.Sha256Hex("{}"), "{}", Conv, 1);
        ConversationPrecheckRules.Check(e, SyncLimits.Default).Rejected.Should().BeNull("conv.deleted 的 payload 本來就是 {}（提案 C10）");
        var decision = ConversationApplyRules.Decide(Row(), DevA, ConversationEventKinds.Deleted, 1, null);
        decision.Action.Should().Be(ConversationAction.Delete);
        decision.PurgeContent.Should().BeTrue();
    }

    // ── recovering 與新事件 ──

    [Fact]
    public void ClearsRecovering_不在對帳模式_真() => ConversationApplyRules.ClearsRecovering(null, null, 5).Should().BeTrue();

    [Fact]
    public void ClearsRecovering_對帳模式下舊序號_假() => ConversationApplyRules.ClearsRecovering(3, 10, 9).Should().BeFalse();

    [Fact]
    public void ClearsRecovering_對帳模式下Hello之後的序號_真() => ConversationApplyRules.ClearsRecovering(3, 10, 11).Should().BeTrue();

    [Fact]
    public void ClearsRecovering_正好等於NextDeviceSeq_真() => ConversationApplyRules.ClearsRecovering(3, 10, 10).Should().BeTrue("≥，不是 >");

    [Fact]
    public void ClearsRecovering_對帳模式而且Hello沒有outbox位置_假() => ConversationApplyRules.ClearsRecovering(3, null, 100).Should().BeFalse();

    [Fact]
    public void IsFresh與ClearsRecovering同一個判斷()
    {
        long?[] resync = [null, 1, 5];
        long?[] next = [null, 1, 4, 7];
        foreach (var r in resync)
        {
            foreach (var n in next)
            {
                for (long seq = 1; seq <= 8; seq++)
                {
                    var expected = r is null || (n is not null && seq >= n);
                    ConversationApplyRules.IsFresh(r, n, seq).Should().Be(expected, $"resync={r} next={n} seq={seq}");
                    ConversationApplyRules.ClearsRecovering(r, n, seq).Should().Be(expected);
                }
            }
        }
    }

    // ── 時間與種類 ──

    [Fact]
    public void DisplayTime_裝置比收到晚取收到() => ConversationApplyRules.DisplayTime(2_000, 1_500).Should().Be(1_500);

    [Fact]
    public void DisplayTime_沒有裝置時間用收到() => ConversationApplyRules.DisplayTime(null, 1_500).Should().Be(1_500);

    [Fact]
    public void DisplayTime_裝置較早照裝置() => ConversationApplyRules.DisplayTime(1_000, 1_500).Should().Be(1_000);

    [Theory]
    [InlineData("Running", false)]
    [InlineData("Waiting", false)]
    [InlineData("Done", true)]
    [InlineData("Failed", true)]
    [InlineData("Declined", true)]
    [InlineData("Cancelled", true)]
    public void IsFinal_Running與Waiting為假_其他四種為真(string status, bool final)
    {
        Enum.GetNames<Myriagent.Agents.AgentItemStatus>().Should().HaveCount(6, "狀態有新增時這張表要補");
        ConversationApplyRules.IsFinal(status).Should().Be(final);
    }

    [Theory]
    [InlineData(ConversationAttentionKinds.Plan, "approval")]
    [InlineData(ConversationAttentionKinds.Approval, "approval")]
    [InlineData(ConversationAttentionKinds.Question, "question")]
    [InlineData(ConversationAttentionKinds.Completed, "completed")]
    [InlineData(ConversationAttentionKinds.Failed, "failed")]
    [InlineData("mystery", null)]
    [InlineData("Plan", null)]
    public void NotifyKind_plan對到approval_不認得的null(string attention, string? expected) =>
        ConversationApplyRules.NotifyKind(attention).Should().Be(expected);
}
