using MyriagentHub.Realtime;

namespace MyriagentHub.Tests.Realtime;

/// <summary>即時疊加層的純函式規則（WP4.2 計畫步 11；D27、提案 C4、F30、F38）</summary>
public sealed class LiveOverlayRulesTests
{
    private const string C = "0123456789abcdef0123456789abcdef";

    private static LiveEntry Entry(long ver, long order, long ord = 1, string conversationId = C, int bytes = 100, string itemId = "item1") =>
        new(conversationId, ord, itemId, 1, ver, order, "{}", bytes, "d1");

    // ───── Decide ─────

    [Fact]
    public void durable是final_一律丟()
    {
        var final = new DurableMark(1, true, 0);
        LiveOverlayRules.Decide(null, final, Entry(99, 5)).Should().Be(LiveVerdict.DropFinal);
        LiveOverlayRules.Decide(Entry(1, 1), final, Entry(2, 9)).Should().Be(LiveVerdict.DropFinal, "比已有的疊加新、Order 也大，持久版本 final 照樣丟");
        LiveOverlayRules.Decide(null, new DurableMark(1, true, 5), Entry(99, 5)).Should().Be(LiveVerdict.DropFinal);
    }

    [Fact]
    public void Order較大的連線取代_較小的丟()
    {
        // 擁有者重連時舊連線還沒斷：兩條交替送 20 次，疊加只留 Order 較大那條的最新一則
        LiveEntry? current = null;
        for (var i = 1; i <= 20; i++)
        {
            var incoming = i % 2 == 0 ? Entry(i, 7) : Entry(100 + i, 3);
            var verdict = LiveOverlayRules.Decide(current, null, incoming);
            if (verdict == LiveVerdict.Accept)
                current = incoming;
            if (incoming.ConnectionOrder == 3 && i > 1)
                verdict.Should().Be(LiveVerdict.DropOlderConnection, "較舊的連線 ver 再大也不能蓋掉較新的");
        }
        current.Should().Be(Entry(20, 7));
    }

    [Fact]
    public void 同連線ver較小丟掉()
    {
        LiveOverlayRules.Decide(Entry(5, 3), null, Entry(4, 3)).Should().Be(LiveVerdict.DropNotNewer);
        LiveOverlayRules.Decide(Entry(5, 3), null, Entry(5, 3)).Should().Be(LiveVerdict.DropNotNewer, "一樣的不算新");
        LiveOverlayRules.Decide(Entry(5, 3), null, Entry(6, 3)).Should().Be(LiveVerdict.Accept);
    }

    [Fact]
    public void 持久版本同一條連線提交且ver較大_作廢()
    {
        LiveOverlayRules.Decide(null, new DurableMark(6, false, 3), Entry(5, 3)).Should().Be(LiveVerdict.DropSuperseded);
        LiveOverlayRules.Decide(null, new DurableMark(5, false, 3), Entry(5, 3)).Should().Be(LiveVerdict.DropSuperseded, "ver 相同：持久版本已經有了");
        LiveOverlayRules.Decide(null, new DurableMark(4, false, 3), Entry(5, 3)).Should().Be(LiveVerdict.Accept, "即時更新比持久版本新");
    }

    [Fact]
    public void 擁有者重開_舊持久ver較大_新relay仍顯示()
    {
        // 持久版本是舊連線（Order 3）提交的 ver 40；擁有者重開後新連線（Order 8）的 ver 從 1 起算：不能拿 ver 比（抓「一律比 ver」的突變）
        LiveOverlayRules.Decide(null, new DurableMark(40, false, 3), Entry(1, 8)).Should().Be(LiveVerdict.Accept);
        LiveOverlayRules.Decide(null, new DurableMark(40, false, 0), Entry(1, 8)).Should().Be(LiveVerdict.Accept, "從快照得到的標記不知道提交的連線（0）");
    }

    [Fact]
    public void 已有疊加來自較小的Order_新連線的ver1照收()
    {
        LiveOverlayRules.Decide(Entry(50, 3), null, Entry(1, 8)).Should().Be(LiveVerdict.Accept);
    }

    // ───── 持久版本對疊加的影響（提案 C4） ─────

    [Fact]
    public void 持久版本Waiting_清掉疊加_之後的即時更新照收()
    {
        var waiting = new DurableMark(3, false, 3);
        LiveOverlayRules.ClearedByDurable("Waiting", waiting, Entry(9, 3)).Should().BeTrue("不是 Running 就清，不比 ver（a）");
        LiveOverlayRules.ClearedByDurable("Waiting", new DurableMark(3, false, 1), Entry(9, 3)).Should().BeTrue("不是同一條連線提交的也清");
        LiveOverlayRules.Decide(null, waiting, Entry(10, 3)).Should().Be(LiveVerdict.Accept, "Waiting 不是 final：之後的即時更新照收（b）");
    }

    [Fact]
    public void 持久版本final_之後的即時更新丟掉()
    {
        var done = new DurableMark(3, true, 3);
        LiveOverlayRules.ClearedByDurable("Done", done, Entry(9, 3)).Should().BeTrue();
        LiveOverlayRules.Decide(null, done, Entry(10, 3)).Should().Be(LiveVerdict.DropFinal);
    }

    [Fact]
    public void 持久版本Running_只有同一條連線提交且不比疊加舊才清()
    {
        LiveOverlayRules.ClearedByDurable("Running", new DurableMark(9, false, 3), Entry(9, 3)).Should().BeTrue();
        LiveOverlayRules.ClearedByDurable("Running", new DurableMark(8, false, 3), Entry(9, 3)).Should().BeFalse("疊加比較新：推過持久版本之後再推一次疊加");
        LiveOverlayRules.ClearedByDurable("Running", new DurableMark(40, false, 2), Entry(9, 3)).Should().BeFalse("較舊的連線提交的");
        LiveOverlayRules.ClearedByDurable(null, new DurableMark(1, false, 3), Entry(9, 3)).Should().BeTrue("不知道狀態當成不是 Running");
    }

    // ───── 權杖桶 ─────

    [Fact]
    public void 權杖桶_抖動到達_不丟最後一則()
    {
        // 平均每秒 4 則、±120 ms 抖動（交替提早與延後：相鄰兩則最近只差 10 ms）；最後一則一定收（固定間隔的實作會丟掉它）
        var bucket = new LiveTokenBucket();
        var accepted = new List<int>();
        for (var i = 0; i < 40; i++)
        {
            var jitter = i % 2 == 0 ? 120 : -120;
            if (LiveOverlayRules.TakeToken(ref bucket, 10_000 + i * 250 + jitter, 4, 4))
                accepted.Add(i);
        }
        accepted.Should().Contain(39, "畫面不能停在倒數第二則");
        accepted.Should().HaveCount(40, "平均速率沒超過");
    }

    [Fact]
    public void 權杖桶_持續超速_丟掉超過的部分()
    {
        // 每秒 10 則、送 10 秒：收下的是突發 4 則＋10 秒補的約 40 則
        var bucket = new LiveTokenBucket();
        var accepted = 0;
        for (var i = 0; i < 100; i++)
        {
            if (LiveOverlayRules.TakeToken(ref bucket, 5_000 + i * 100, 4, 4))
                accepted++;
        }
        accepted.Should().BeInRange(40, 44);
    }

    [Fact]
    public void 權杖桶_第一次是滿的_時間倒退不補_上限是突發()
    {
        var bucket = new LiveTokenBucket();
        for (var i = 0; i < 4; i++)
            LiveOverlayRules.TakeToken(ref bucket, 1_000, 4, 4).Should().BeTrue("一開始是滿的");
        LiveOverlayRules.TakeToken(ref bucket, 1_000, 4, 4).Should().BeFalse();
        LiveOverlayRules.TakeToken(ref bucket, 0, 4, 4).Should().BeFalse("時間倒退不補");
        LiveOverlayRules.TakeToken(ref bucket, 1_250, 4, 4).Should().BeTrue("250 ms 補一個");
        LiveOverlayRules.TakeToken(ref bucket, 1_250, 4, 4).Should().BeFalse();

        var idle = new LiveTokenBucket();
        LiveOverlayRules.TakeToken(ref idle, 0, 4, 4).Should().BeTrue();
        var taken = 0;
        while (LiveOverlayRules.TakeToken(ref idle, 3_600_000, 4, 4))
            taken++;
        taken.Should().Be(4, "閒置再久也只補到突發的上限");

        var none = new LiveTokenBucket();
        LiveOverlayRules.TakeToken(ref none, 0, 4, 0).Should().BeFalse("突發 0");
        LiveOverlayRules.TakeToken(ref none, 0, 0, 4).Should().BeFalse("速率 0");
    }

    // ───── 淘汰 ─────

    [Fact]
    public void 每對話64筆_淘汰最舊()
    {
        var other = "fedcba9876543210fedcba9876543210";
        var all = new List<LiveEntry>();
        for (var i = 1; i <= 70; i++)
        {
            all.Add(Entry(1, 1, ord: i));
            if (i <= 3)
                all.Add(Entry(1, 1, ord: i, conversationId: other));
        }
        var evicted = LiveOverlayRules.Evict(all, 64, long.MaxValue);
        evicted.Select(e => (e.ConversationId, e.Ord)).Should().Equal(Enumerable.Range(1, 6).Select(i => (C, (long)i)), "超過的 6 個、依收下的先後；另一個對話不受影響");
        LiveOverlayRules.Evict(all.Take(64).ToList(), 64, long.MaxValue).Should().BeEmpty();
    }

    [Fact]
    public void 全部位元組上限_淘汰最舊()
    {
        var other = "fedcba9876543210fedcba9876543210";
        var all = new List<LiveEntry>
        {
            Entry(1, 1, ord: 1, bytes: 400),
            Entry(1, 1, ord: 1, conversationId: other, bytes: 300),
            Entry(1, 1, ord: 2, bytes: 200),
            Entry(1, 1, ord: 3, bytes: 100),
        };
        LiveOverlayRules.Evict(all, 64, 1_000).Should().BeEmpty("剛好 1,000");
        LiveOverlayRules.Evict(all, 64, 999).Should().Equal(all[0]);
        LiveOverlayRules.Evict(all, 64, 300).Should().Equal(new[] { all[0], all[1] }, "跨對話依收下的先後");
        LiveOverlayRules.Evict(all, 64, 0).Should().Equal(all);
    }

    [Fact]
    public void 淘汰_先每對話上限再位元組_不重複算()
    {
        var all = Enumerable.Range(1, 5).Select(i => Entry(1, 1, ord: i, bytes: 100)).ToList();
        // 每對話 3 個 → 淘汰 ord 1、2；剩 300 位元組，上限 250 → 再淘汰 ord 3
        LiveOverlayRules.Evict(all, 3, 250).Select(e => e.Ord).Should().Equal(1L, 2L, 3L);
    }
}
