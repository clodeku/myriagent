using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;
using P = Myriagent.Sync.ConversationSyncPolicies;

namespace MyriagentHub.Tests.Sync;

/// <summary>物化表重建的純函式規則（WP4.2 計畫步 5；D14）</summary>
public sealed class ConversationRebuildRulesTests
{
    private const string A = "d_0000000000000000000000000A";

    // ───── 版本比對 ─────

    [Fact]
    public void 版本相等_不重建() => ConversationRebuildRules.NeedsRebuild("1", 1, hasConversations: true).Should().BeFalse();

    [Fact]
    public void 存的比較大_也重建() => ConversationRebuildRules.NeedsRebuild("2", 1, hasConversations: true).Should().BeTrue("從較新的 WP4.2 組建回滾");

    [Fact]
    public void 存的比較小_重建() => ConversationRebuildRules.NeedsRebuild("1", 2, hasConversations: true).Should().BeTrue();

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("01")]
    public void 存的不是同一個數字字串_重建(string stored) => ConversationRebuildRules.NeedsRebuild(stored, 1, hasConversations: true).Should().BeTrue();

    [Fact]
    public void 沒有版本鍵也沒有資料_只寫版本() => ConversationRebuildRules.NeedsRebuild(null, 1, hasConversations: false).Should().BeFalse();

    [Fact]
    public void 沒有版本鍵但有資料_重建() => ConversationRebuildRules.NeedsRebuild(null, 1, hasConversations: true).Should().BeTrue("WP4.2 部署前就有對話資料（不會發生，但要冪等）");

    [Fact]
    public void 版本不同但沒有資料_不重建() => ConversationRebuildRules.NeedsRebuild("2", 1, hasConversations: false).Should().BeFalse();

    // ───── 政策與清除點 ─────

    [Theory]
    [InlineData(P.Full, P.Metadata, true)]
    [InlineData(P.Full, P.Paused, true)]
    [InlineData(P.Full, P.Full, false)]
    [InlineData(P.Metadata, P.Paused, false)]
    [InlineData(P.Paused, P.Metadata, false)]
    [InlineData(P.Metadata, P.Full, false)]
    [InlineData(P.Paused, P.Full, false)]
    [InlineData(P.Full, "未來的政策", false)]
    public void Track_只有full降成metadata或paused才移動清除點(string from, string to, bool purge)
    {
        var cursor = new RebuildCursor(from, false, 3);

        var next = ConversationRebuildRules.Track(cursor, 10, K.Meta, to);

        next.Should().Be(new RebuildCursor(P.Interpret(to), false, purge ? 10 : 3));
    }

    [Fact]
    public void Track_刪除_標成已刪除並移動清除點() =>
        ConversationRebuildRules.Track(new RebuildCursor(P.Full, false, 0), 7, K.Deleted, null).Should().Be(new RebuildCursor(P.Full, true, 7));

    [Theory]
    [InlineData(K.Turn)]
    [InlineData(K.Attention)]
    public void Track_其他meta類_不變(string kind)
    {
        var cursor = new RebuildCursor(P.Metadata, false, 4);

        ConversationRebuildRules.Track(cursor, 9, kind, null).Should().Be(cursor);
    }

    [Fact]
    public void Track_沒帶政策的created當成full() =>
        ConversationRebuildRules.Track(new RebuildCursor(P.Paused, false, 2), 5, K.Created, null).Should().Be(new RebuildCursor(P.Full, false, 2));

    // ───── meta 的摺疊 ─────

    [Fact]
    public void FoldMeta_最後一次降級的seq()
    {
        var meta = ConversationRebuildRules.FoldMeta([
            MetaEvent(1, K.Created, Meta("一", P.Full)),
            MetaEvent(5, K.Meta, Meta("二", P.Metadata)),
            MetaEvent(7, K.Meta, Meta("三", P.Full)),
            MetaEvent(9, K.Meta, Meta("四", P.Paused)),
            MetaEvent(11, K.Meta, Meta("五", P.Metadata)),
        ])!;

        meta.Cursor.Should().Be(new RebuildCursor(P.Metadata, false, 9), "paused 改 metadata 不清除；最後一次從 full 降級是 seq 9");
        (meta.Title, meta.LastSeq).Should().Be(("五", 11L));
    }

    [Fact]
    public void FoldMeta_沒有created或meta_回null() =>
        ConversationRebuildRules.FoldMeta([MetaEvent(1, K.Turn, Turn(ConversationTurnPhases.Started)), MetaEvent(2, K.Deleted, new JObject())]).Should().BeNull();

    [Fact]
    public void FoldMeta_空的_回null() => ConversationRebuildRules.FoldMeta([]).Should().BeNull();

    [Fact]
    public void FoldMeta_第一筆決定擁有者_epoch與建立時間_之後整份取代()
    {
        var first = Meta("第一");
        first["createdAt"] = 1_700_000_000_000L;
        first["updatedAt"] = 1_700_000_001_000L;
        first["localAccountId"] = "acc-1";
        var second = Meta("第二");
        second["createdAt"] = 1_600_000_000_000L;
        second["updatedAt"] = 1_700_000_002_000L;
        second["running"] = true;
        second["waiting"] = true;
        second["provider"] = "codex";
        second["localAccountId"] = "acc-2";

        var meta = ConversationRebuildRules.FoldMeta([
            MetaEvent(3, K.Created, first, epoch: 4),
            MetaEvent(8, K.Created, second, author: "d_0000000000000000000000000B", epoch: 9),
        ])!;

        (meta.OwnerDeviceId, meta.Epoch, meta.CreatedAt).Should().Be((A, 4L, 1_700_000_000_000L), "擁有者、epoch 與建立時間只看第一筆（D5）");
        (meta.Title, meta.Provider, meta.LocalAccountId, meta.UpdatedAt, meta.Running, meta.Waiting, meta.LastSeq)
            .Should().Be(("第二", "codex", (string?)"acc-2", 1_700_000_002_000L, true, true, 8L));
        meta.MetaJson.Should().Be(Json(second));
    }

    [Fact]
    public void FoldMeta_裝置時間比收到的時間晚_用收到的時間()
    {
        var future = Meta();
        future["createdAt"] = 1_900_000_000_000L;
        future["updatedAt"] = 1_900_000_000_000L;

        var meta = ConversationRebuildRules.FoldMeta([MetaEvent(1, K.Created, future, receivedAt: 1_800_000_000_000L)])!;

        (meta.CreatedAt, meta.UpdatedAt).Should().Be((1_800_000_000_000L, 1_800_000_000_000L), "D10：顯示時間不超過伺服器收到的時間");
    }

    [Fact]
    public void FoldMeta_turn與刪除用收到的時間_刪除者是作者()
    {
        var completed = Turn(ConversationTurnPhases.Completed);

        var meta = ConversationRebuildRules.FoldMeta([
            MetaEvent(1, K.Created, Meta()),
            MetaEvent(2, K.Turn, Turn(ConversationTurnPhases.Started), receivedAt: 2_000),
            MetaEvent(3, K.Turn, completed, receivedAt: 3_000),
            MetaEvent(4, K.Attention, new JObject { ["kind"] = "plan" }),
            MetaEvent(6, K.Deleted, new JObject(), receivedAt: 6_000),
        ])!;

        (meta.Running, meta.TurnStartedAt, meta.LastTurn).Should().Be((false, 2_000L, Json(completed)));
        (meta.DeletedAt, meta.DeletedBy, meta.LastSeq).Should().Be((6_000L, A, 6L));
        meta.Cursor.Should().Be(new RebuildCursor(P.Full, true, 6));
    }

    [Fact]
    public void FoldMeta_依seq排序後摺疊() =>
        ConversationRebuildRules.FoldMeta([MetaEvent(9, K.Meta, Meta("後")), MetaEvent(2, K.Created, Meta("前"))])!.Title.Should().Be("後");

    [Fact]
    public void FoldMeta_第一筆created之前的turn略過()
    {
        var meta = ConversationRebuildRules.FoldMeta([MetaEvent(1, K.Turn, Turn(ConversationTurnPhases.Started)), MetaEvent(2, K.Created, Meta())])!;

        (meta.Running, meta.TurnStartedAt, meta.LastSeq).Should().Be((false, (long?)null, 2L));
    }

    // ───── 重播方式與分塊 ─────

    [Theory]
    [InlineData(5, 0, false, false)]
    [InlineData(5, 4, false, false)]
    [InlineData(5, 5, false, true)]
    [InlineData(5, 9, false, true)]
    [InlineData(50, 9, true, true)]
    public void ReplayOf_清除點之前或已刪除只更新max_ord(long seq, long purgeSeq, bool deleted, bool ordOnly) =>
        ConversationRebuildRules.ReplayOf(seq, new RebuildCursor(P.Full, deleted, purgeSeq)).Should().Be(ordOnly ? RebuildReplay.OrdOnly : RebuildReplay.Full);

    [Fact]
    public void IsLastChunk_比上限少才是最後一塊()
    {
        ConversationRebuildRules.IsLastChunk(0, 500).Should().BeTrue();
        ConversationRebuildRules.IsLastChunk(499, 500).Should().BeTrue();
        ConversationRebuildRules.IsLastChunk(500, 500).Should().BeFalse("剛好滿：下一塊可能還有");
        FluentActions.Invoking(() => ConversationRebuildRules.IsLastChunk(1, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ConversationRebuildRules.IsLastChunk(-1, 5)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 分塊不重疊不漏_窮舉小範圍()
    {
        // 照 RebuildNext 的讀法（seq 大於已做到的、依 seq、最多 chunk 筆）模擬，seq 之間有空隙（其他串流的事件）
        for (var total = 0; total <= 23; total++)
        {
            for (var chunk = 1; chunk <= 7; chunk++)
            {
                var seqs = Enumerable.Range(0, total).Select(i => 3L * i + (i % 2) + 1).ToList();
                var done = 0L;
                var seen = new List<long>();
                var jobs = 0;
                while (true)
                {
                    jobs++;
                    var taken = seqs.Where(s => s > done).Take(chunk).ToList();
                    taken.Count.Should().BeLessThanOrEqualTo(chunk);
                    seen.AddRange(taken);
                    if (taken.Count > 0)
                        done = taken[^1];
                    if (ConversationRebuildRules.IsLastChunk(taken.Count, chunk))
                        break;
                    jobs.Should().BeLessThan(100);
                }
                seen.Should().Equal(seqs, $"共 {total} 筆、每塊 {chunk} 筆");
                jobs.Should().Be(total / chunk + 1, "整除時多一個空的最後一塊");
            }
        }
    }

    private static RebuildMetaEvent MetaEvent(long seq, string kind, JObject payload, string author = A, long epoch = 1, long receivedAt = 1_800_000_000_000L)
    {
        var json = Json(payload);
        return new RebuildMetaEvent(seq, author, kind, epoch, receivedAt, json, JObject.Parse(json));
    }

    private static JObject Turn(string phase) => ConversationEventLogTests.Turn(phase);
}
