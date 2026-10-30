using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Realtime;

/// <summary>伺服器記的網頁未讀的純函式規則（WP4.2 計畫步 12A；答 6、D45）</summary>
public sealed class ConversationUnreadRulesTests
{
    private static readonly string C1 = Id(1);

    private static readonly string C2 = Id(2);

    private static string Id(int n) => n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private static BrowserClientState State(bool visible, bool focused, params string[] open) => new(visible, focused, open, 0);

    private static ViewerState Viewer(bool visible, bool focused, string[] open, params string[] visibleIds) => new(State(visible, focused, open), visibleIds);

    private static ConversationChange Attention(string? kind, bool fresh = true) =>
        new(C1, ConversationChangeKind.Attention, 10, "dev", AttentionKind: kind, Fresh: fresh);

    private static ConversationChange Turn(string phase, bool fresh = true) =>
        new(C1, ConversationChangeKind.Turn, 10, "dev", TurnPhase: phase, Fresh: fresh);

    [Fact]
    public void visible與focused都要()
    {
        ConversationUnreadRules.Seen(State(true, true), inVisibleSet: true).Should().BeTrue();
        ConversationUnreadRules.Seen(State(true, false), inVisibleSet: true).Should().BeFalse("分頁看得到但沒有 focus（不是前景視窗）");
        ConversationUnreadRules.Seen(State(false, true), inVisibleSet: true).Should().BeFalse("分頁在背景");
        ConversationUnreadRules.Seen(State(false, false), inVisibleSet: true).Should().BeFalse();
        ConversationUnreadRules.Seen(null, inVisibleSet: true).Should().BeFalse("還沒送過 ClientState");

        ConversationUnreadRules.SeenSet([Viewer(true, true, [C1])]).Should().BeEquivalentTo([C1]);
        ConversationUnreadRules.SeenSet([Viewer(true, false, [C1], C2)]).Should().BeEmpty("沒有 focus 的連線不算");
        ConversationUnreadRules.SeenSet([Viewer(false, true, [C1], C2)]).Should().BeEmpty("看不到的連線不算");
        ConversationUnreadRules.SeenSet([new ViewerState(null, [C1])]).Should().BeEmpty();
        ConversationUnreadRules.SeenSet([Viewer(true, false, [C1]), Viewer(true, true, [], C2)]).Should().BeEquivalentTo([C2], "聯集只收看見的連線");
    }

    [Fact]
    public void 不在清單裡不算看見()
    {
        ConversationUnreadRules.Seen(State(true, true), inVisibleSet: false).Should().BeFalse();
        ConversationUnreadRules.Seen(Viewer(true, true, [], C2), C1).Should().BeFalse("visible 指令的清單與 openConversationIds 都沒有它（只是 open 不算）");
        ConversationUnreadRules.Seen(Viewer(true, true, [C1]), C1).Should().BeTrue("openConversationIds 裡有它");
        ConversationUnreadRules.Seen(Viewer(true, true, [], C1), C1).Should().BeTrue("visible 指令的清單裡有它");
        ConversationUnreadRules.Seen(Viewer(true, true, ["c1"], "d01J9Q0000000000000000000AA"), "c1").Should().BeFalse("不是對話 id 形狀的不算（本機 id、草稿）");
        ConversationUnreadRules.SeenSet([Viewer(true, true, ["c1", C1], "d01J9Q0000000000000000000AA", C2)]).Should().BeEquivalentTo([C1, C2], "只留對話 id");
    }

    [Fact]
    public void 看見的判斷與聯集一致()
    {
        // 隨機狀態逐一比對：Seen(viewer, id) 為真的 id 正好是 SeenSet 的內容
        var rng = new Random(1212);
        var ids = Enumerable.Range(1, 6).Select(Id).ToArray();
        for (var round = 0; round < 300; round++)
        {
            var viewers = Enumerable.Range(0, rng.Next(0, 4)).Select(_ =>
            {
                var open = ids.Where(_ => rng.Next(4) == 0).ToArray();
                var visible = ids.Where(_ => rng.Next(4) == 0).ToArray();
                return rng.Next(5) == 0 ? new ViewerState(null, visible) : Viewer(rng.Next(3) > 0, rng.Next(3) > 0, open, visible);
            }).ToList();
            var expected = ids.Where(id => viewers.Any(v => ConversationUnreadRules.Seen(v, id))).ToList();
            ConversationUnreadRules.SeenSet(viewers).Should().BeEquivalentTo(expected);
        }
    }

    [Fact]
    public void 對帳重送的舊事件_不標()
    {
        ConversationUnreadRules.MarksUnread(Attention("completed", fresh: false)).Should().BeFalse("對帳重送的舊事件與重建都不帶 Fresh");
        ConversationUnreadRules.MarksUnread(Turn(ConversationTurnPhases.Completed, fresh: false)).Should().BeFalse();
        ConversationUnreadRules.MarksUnread(Attention("completed")).Should().BeTrue();
    }

    [Fact]
    public void attention的五種與turn的completed才標_turn的started不標()
    {
        foreach (var kind in new[] { "question", "approval", "plan", "completed", "failed" })
            ConversationUnreadRules.MarksUnread(Attention(kind)).Should().BeTrue(kind);
        ConversationUnreadRules.MarksUnread(Attention("other")).Should().BeFalse("不認得的注意事件");
        ConversationUnreadRules.MarksUnread(Attention(null)).Should().BeFalse();
        ConversationUnreadRules.MarksUnread(Attention("Completed")).Should().BeFalse("大小寫要完全相同");
        ConversationUnreadRules.MarksUnread(Turn(ConversationTurnPhases.Completed)).Should().BeTrue("一輪結束");
        ConversationUnreadRules.MarksUnread(Turn(ConversationTurnPhases.Started)).Should().BeFalse("一輪開始不標");

        foreach (var kind in Enum.GetValues<ConversationChangeKind>().Where(k => k is not ConversationChangeKind.Attention and not ConversationChangeKind.Turn))
        {
            ConversationUnreadRules.MarksUnread(new ConversationChange(C1, kind, 10, "dev", ItemId: "item1", AttentionKind: "completed",
                TurnPhase: ConversationTurnPhases.Completed, Fresh: true)).Should().BeFalse(kind.ToString());
        }
        FluentActions.Invoking(() => ConversationUnreadRules.MarksUnread(new ConversationChange(C1, (ConversationChangeKind)99, 10, "dev", Fresh: true)))
            .Should().Throw<ArgumentOutOfRangeException>("新的變更種類要在規則裡決定（switch，不靜默落進一邊）");
    }
}
