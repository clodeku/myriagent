using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>閘道的對話訊息的純函式（WP4.2 計畫步 10；D22、D23、D25、D28、D38、K4）</summary>
public sealed class ConversationGatewayRulesTests
{
    private const string C1 = "0123456789abcdef0123456789abcdef";

    private const string C2 = "fedcba9876543210fedcba9876543210";

    private const string Owner = "dv_owner";

    // ───── Fold ─────

    [Fact]
    public void 同一個項目只留最後一筆_item與removeItem共用_依最後一次出現的順序()
    {
        var round = ConversationGatewayRules.Fold(
        [
            Item(C1, "a", 1), Item(C1, "b", 2), Item(C1, "a", 3), Removed(C1, "b", 4), Item(C1, "c", 5), Item(C1, "b", 6),
        ]);

        var work = round.Conversations.Should().ContainSingle().Subject;
        work.Items.Select(c => (c.ItemId, c.Seq, c.Kind)).Should().Equal(
            ("a", 3L, ConversationChangeKind.Item), ("c", 5L, ConversationChangeKind.Item), ("b", 6L, ConversationChangeKind.Item));
        round.ListChanged.Should().BeFalse("項目不影響清單的欄位");
        work.HeaderSeq.Should().BeNull();
        work.ComposerSeq.Should().BeNull();
    }

    [Fact]
    public void meta與turn重送標頭與清單_queue重送輸入區_對話依第一次出現的順序()
    {
        var round = ConversationGatewayRules.Fold(
        [
            Change(C2, ConversationChangeKind.Queue, 1), Change(C1, ConversationChangeKind.Meta, 2), Change(C1, ConversationChangeKind.Turn, 3),
            Change(C2, ConversationChangeKind.Queue, 4), Change(C1, ConversationChangeKind.Meta, 1),
        ]);

        round.Conversations.Select(w => w.ConversationId).Should().Equal(C2, C1);
        round.ListChanged.Should().BeTrue();
        round.Conversations[0].ComposerSeq.Should().Be(4);
        round.Conversations[0].HeaderSeq.Should().BeNull();
        round.Conversations[1].HeaderSeq.Should().Be(3, "取最大的 seq");

        ConversationGatewayRules.Fold([Change(C1, ConversationChangeKind.Queue, 1)]).ListChanged.Should().BeFalse();
        ConversationGatewayRules.Fold([]).Should().BeSameAs(ConversationRound.Empty);
    }

    [Fact]
    public void attention只收新的上行事件與認得的kind()
    {
        var round = ConversationGatewayRules.Fold(
        [
            Attention(C1, "plan", 1, fresh: true), Attention(C1, "question", 2, fresh: false), Attention(C1, "bogus", 3, fresh: true),
            Attention(C1, "failed", 4, fresh: true),
        ]);

        round.Conversations.Single().Attentions.Select(a => a.Seq).Should().Equal(1, 4);
        round.ListChanged.Should().BeFalse();
    }

    [Fact]
    public void 刪除之後只剩關閉_之前與之後的都不送()
    {
        var round = ConversationGatewayRules.Fold(
        [
            Item(C1, "a", 1), Change(C1, ConversationChangeKind.Meta, 2), Attention(C1, "completed", 3, fresh: true),
            Change(C1, ConversationChangeKind.Deleted, 4), Item(C1, "b", 5), Change(C1, ConversationChangeKind.Queue, 6),
        ]);

        var work = round.Conversations.Single();
        work.Deleted.Should().BeTrue();
        work.Items.Should().BeEmpty();
        work.Attentions.Should().BeEmpty();
        (work.HeaderSeq, work.ComposerSeq, work.Resync).Should().Be(((long?)null, (long?)null, false));
        round.ListChanged.Should().BeTrue();
    }

    [Fact]
    public void purged清掉它之前的增量_之後的照記_resync帶seq()
    {
        var round = ConversationGatewayRules.Fold(
        [
            Item(C1, "a", 1), Change(C1, ConversationChangeKind.Queue, 2), Change(C1, ConversationChangeKind.Meta, 3), Change(C1, ConversationChangeKind.Purged, 3),
            Change(C1, ConversationChangeKind.Meta, 7), Attention(C1, "completed", 8, fresh: true),
        ]);

        var work = round.Conversations.Single();
        (work.Resync, work.ResyncSeq).Should().Be((true, (long?)3L));
        work.Items.Should().BeEmpty();
        work.ComposerSeq.Should().BeNull();
        work.HeaderSeq.Should().Be(7, "resync 之後的變更給快照已經比 resync 新的連線");
        work.Attentions.Should().ContainSingle("notify 與內容無關");
        round.ListChanged.Should().BeTrue();

        // 同一輪兩次降級：取後面的 seq
        ConversationGatewayRules.Fold([Change(C1, ConversationChangeKind.Purged, 3), Change(C1, ConversationChangeKind.Purged, 9)]).Conversations.Single().ResyncSeq.Should().Be(9);
    }

    [Fact]
    public void rebuilt一律resync_之後的purged也不改()
    {
        var rebuilt = ConversationGatewayRules.Fold([Item(C1, "a", 1), Change(C1, ConversationChangeKind.Rebuilt, 5), Change(C1, ConversationChangeKind.Purged, 9)])
            .Conversations.Single();
        (rebuilt.Resync, rebuilt.ResyncSeq).Should().Be((true, (long?)null));
        rebuilt.Items.Should().BeEmpty();

        var purgedFirst = ConversationGatewayRules.Fold([Change(C1, ConversationChangeKind.Purged, 3), Change(C1, ConversationChangeKind.Rebuilt, 5)]).Conversations.Single();
        (purgedFirst.Resync, purgedFirst.ResyncSeq).Should().Be((true, (long?)null));
    }

    // ───── 看得到、notify ─────

    [Fact]
    public void 看得到_要visible而且在visible清單或openConversationIds裡()
    {
        var visible = new BrowserClientState(true, false, [C2, "d01J9Q0000000000000000000AA"], 0);
        ConversationGatewayRules.CanSee(null, [C1], C1).Should().BeFalse("還沒收到 ClientState");
        ConversationGatewayRules.CanSee(visible with { Visible = false }, [C1], C1).Should().BeFalse("分頁不可見");
        ConversationGatewayRules.CanSee(visible, [C1], C1).Should().BeTrue("在 visible 指令的清單裡");
        ConversationGatewayRules.CanSee(visible, [], C2).Should().BeTrue("在 openConversationIds 裡");
        ConversationGatewayRules.CanSee(visible, [], C1).Should().BeFalse();
        ConversationGatewayRules.CanSee(visible, ["d01J9Q0000000000000000000AA"], "d01J9Q0000000000000000000AA").Should().BeFalse("草稿不是對話 id");
        ConversationGatewayRules.CanSee(visible, [], C2.ToUpperInvariant()).Should().BeFalse();
    }

    [Theory]
    [InlineData("plan", "approval")]
    [InlineData("approval", "approval")]
    [InlineData("question", "question")]
    [InlineData("completed", "completed")]
    [InlineData("failed", "failed")]
    [InlineData("Plan", null)]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void notify的kind(string? attention, string? expected) => ConversationGatewayRules.NotifyKind(attention).Should().Be(expected);

    [Theory]
    [InlineData("「甲」已完成：說明", "completed", "甲", "「甲」已完成：說明")]
    [InlineData("", "completed", "甲", null)]
    [InlineData(null, "completed", "甲", "「甲」已完成")]
    [InlineData(null, "failed", "甲", "「甲」執行失敗")]
    [InlineData(null, "question", "甲", "「甲」有問題想問你")]
    [InlineData(null, "approval", "甲", "「甲」要執行操作，等你核准")]
    [InlineData(null, "plan", "甲", "「甲」的計畫等你確認")]
    [InlineData(null, "unknown", "甲", null)]
    [InlineData(null, "completed", "  ", "對話已完成")]
    public void speak的句子_帶了照念_空字串不念_沒帶組只有標題的(string? speech, string? kind, string? title, string? expected)
        => ConversationGatewayRules.SpeechText(speech, kind, title).Should().Be(expected);

    [Fact]
    public void speak的訊息格式()
        => ConversationGatewayRules.SpeakMessage(C1, 42, "「甲」已完成").Json.Should().Be(
            $$"""{"type":"speak","conversationId":"{{C1}}","id":42,"text":"「甲」已完成"}""");

    // ───── 清單 ─────

    [Fact]
    public void 時區_ClientState優先_沒有時用伺服器時區當時的偏移()
    {
        var taipei = TimeZoneInfo.CreateCustomTimeZone("tst", TimeSpan.FromHours(8), "tst", "tst");
        ConversationGatewayRules.TimezoneOf(new BrowserClientState(true, true, [], 300), taipei, 0).Should().Be(300);
        ConversationGatewayRules.TimezoneOf(null, taipei, 0).Should().Be(-480);
        ConversationGatewayRules.TimezoneOf(null, TimeZoneInfo.Utc, 0).Should().Be(0);
    }

    [Theory]
    [InlineData(0L, 0, 0L)]
    [InlineData(86_399_999L, 0, 0L)]
    [InlineData(86_400_000L, 0, 1L)]
    [InlineData(-1L, 0, -1L)]
    [InlineData(57_600_000L, -480, 1L)] // UTC 16:00＝臺灣隔天 00:00
    [InlineData(57_599_999L, -480, 0L)]
    [InlineData(0L, 60, -1L)] // UTC 00:00＝UTC-1 的前一天 23:00
    public void 當地日期的序號以當地午夜為界(long utcMs, int tz, long day) => ConversationGatewayRules.LocalDay(utcMs, tz).Should().Be(day);

    [Fact]
    public void 清單_雜湊變了才送_時區或午夜變了只更新標記()
    {
        var mark = new ConversationListMark("h1", -480, 1000);
        ConversationGatewayRules.ListAction(null, "h1", -480, 1000).Should().Be(ConversationListAction.Send, "還沒送過");
        ConversationGatewayRules.ListAction(mark, "h2", -480, 1000).Should().Be(ConversationListAction.Send);
        ConversationGatewayRules.ListAction(mark, "h2", 0, 2000).Should().Be(ConversationListAction.Send);
        ConversationGatewayRules.ListAction(mark, "h1", 0, 1000).Should().Be(ConversationListAction.Remark, "時區換了但內容一樣");
        ConversationGatewayRules.ListAction(mark, "h1", -480, 2000).Should().Be(ConversationListAction.Remark, "過了午夜但分組沒變");
        ConversationGatewayRules.ListAction(mark, "h1", -480, 1000).Should().Be(ConversationListAction.None);
    }

    [Fact]
    public void 清單雜湊_同內容相同_不同內容不同()
    {
        ConversationGatewayRules.ListHash("[]").Should().Be(ConversationGatewayRules.ListHash("[]")).And.HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        ConversationGatewayRules.ListHash("[]").Should().NotBe(ConversationGatewayRules.ListHash("[{}]"));
    }

    // ───── open（K4、D20） ─────

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1_048_576L, false)]
    [InlineData(1_048_577L, true)]
    [InlineData(2_097_152L, true)]
    public void open在送出佇列超過上限一半時延到下一輪(long bytes, bool defer) =>
        ConversationGatewayRules.ShouldDeferOpen(bytes, 2_097_152).Should().Be(defer);

    [Theory]
    [InlineData(80, 0, 0, 0, false)]
    [InlineData(81, 0, 0, 0, true)] // 超過一頁：快照一定不比它大
    [InlineData(50, 0, 200, 0, false)] // 200＋50＝250：剛好一半
    [InlineData(51, 0, 200, 0, true)]
    [InlineData(1, 1_048_575, 0, 0, false)]
    [InlineData(1, 1_000, 0, 1_047_576, false)] // 合計剛好一半
    [InlineData(1, 1_001, 0, 1_047_576, true)]
    [InlineData(1, 0, 0, 1_048_577, true)] // 還沒渲染（0 位元組）時佇列已超過一半
    public void 增量改成重開_超過一頁或排進去會超過佇列上限的一半(int increments, long incoming, int count, long bytes, bool reopen) =>
        ConversationGatewayRules.ShouldReopen(increments, incoming, count, bytes, 500, 2_097_152).Should().Be(reopen);

    [Theory]
    [InlineData(64, 1_000_000, 0, 0, true)]
    [InlineData(64, 1_048_577, 0, 0, false)] // 超過剩餘空間的一半
    [InlineData(64, 500_000, 0, 1_097_152, true)] // 剩 1,000,000：一半是 500,000
    [InlineData(64, 500_001, 0, 1_097_152, false)]
    [InlineData(250, 1, 0, 0, true)]
    [InlineData(251, 1, 0, 0, false)]
    [InlineData(10, 1, 481, 0, false)] // 剩 19 則：一半是 9
    public void open之後的疊加_不超過送出佇列剩餘空間的一半才推(int slots, long slotBytes, int count, long bytes, bool push) =>
        ConversationGatewayRules.ShouldPushSlotsAfterOpen(slots, slotBytes, count, bytes, 500, 2_097_152).Should().Be(push);

    [Fact]
    public void 分頁_從最新往前放_筆數與位元組上限_至少一筆()
    {
        var items = Enumerable.Range(1, 5).Select(i => new string('x', 10 * i)).ToList();

        var (all, more) = ConversationGatewayRules.PageOf(items, false, 80, 1000);
        all.Should().Equal(items);
        more.Should().BeFalse();

        var (byCount, moreByCount) = ConversationGatewayRules.PageOf(items, false, 2, 1000);
        byCount.Should().Equal(items[3], items[4]);
        moreByCount.Should().BeTrue();

        var (byBytes, moreByBytes) = ConversationGatewayRules.PageOf(items, false, 80, 95);
        byBytes.Should().Equal(new[] { items[3], items[4] }, "最新的兩筆 40＋50 位元組");
        moreByBytes.Should().BeTrue();

        var (atLeastOne, _) = ConversationGatewayRules.PageOf(items, false, 80, 1);
        atLeastOne.Should().Equal(items[4]);

        ConversationGatewayRules.PageOf(items, true, 80, 1000).HasMore.Should().BeTrue("讀的那一頁之前還有");
        var (empty, emptyMore) = ConversationGatewayRules.PageOf([], true, 80, 1000);
        empty.Should().BeEmpty();
        emptyMore.Should().BeTrue();
    }

    // ───── 訊息 ─────

    [Fact]
    public void conversation_paneId與draft是null_項目與標頭原文嵌入_是對話屏障()
    {
        var message = ConversationGatewayRules.ConversationMessage(C1, """{"id":"h"}""", ["""{"id":"i1"}""", """{"id":"i2"}"""], true, """{"busy":false}""");
        message.Type.Should().Be("conversation");
        message.ConversationId.Should().Be(C1);
        message.Key.Should().BeNull("快照不合併");
        var json = JObject.Parse(message.Json);
        json.Properties().Select(p => p.Name).Should().Equal("type", "conversationId", "paneId", "header", "items", "hasMore", "composer", "draft");
        json["paneId"]!.Type.Should().Be(JTokenType.Null);
        json["draft"]!.Type.Should().Be(JTokenType.Null);
        json["items"]!.Select(i => i["id"]!.Value<string>()).Should().Equal("i1", "i2");
        json["header"]!["id"]!.Value<string>().Should().Be("h");
        json["hasMore"]!.Value<bool>().Should().BeTrue();
        message.Json.Should().Contain("\"items\":[{\"id\":\"i1\"},{\"id\":\"i2\"}]", "已渲染的 JSON 原樣嵌入");
    }

    [Fact]
    public void 增量帶合併鍵與對話_關閉resync與舊項目不帶鍵()
    {
        var item = ConversationGatewayRules.ItemMessage(C1, "i9", """{"id":"i9"}""");
        var removed = ConversationGatewayRules.RemoveItemMessage(C1, "i9");
        var header = ConversationGatewayRules.HeaderMessage(C1, """{"id":"h"}""");
        var composer = ConversationGatewayRules.ComposerMessage(C1, "{}");
        (item.Key, removed.Key).Should().Be((ClientOutboxRules.ItemKey(C1, "i9"), ClientOutboxRules.ItemKey(C1, "i9")));
        header.Key.Should().Be(ClientOutboxRules.HeaderKey(C1));
        composer.Key.Should().Be(ClientOutboxRules.ComposerKey(C1));
        new[] { item, removed, header, composer }.Should().OnlyContain(m => m.ConversationId == C1 && ClientOutboxRules.IsConversationIncrement(m.Type));
        JObject.Parse(item.Json)["item"]!["id"]!.Value<string>().Should().Be("i9");
        JObject.Parse(removed.Json).ShouldBeJson(($$"""{"type":"removeItem","conversationId":"{{C1}}","id":"i9"}"""));
        JObject.Parse(header.Json)["header"]!["id"]!.Value<string>().Should().Be("h");
        JObject.Parse(composer.Json)["type"]!.Value<string>().Should().Be("composer");

        var older = ConversationGatewayRules.OlderItemsMessage(C1, "i5", ["""{"id":"i1"}"""], false);
        var closed = ConversationGatewayRules.ClosedMessage(C1);
        var resync = ConversationGatewayRules.ResyncMessage(C1);
        new[] { older, closed, resync }.Should().OnlyContain(m => m.Key == null && m.ConversationId == C1 && !ClientOutboxRules.IsConversationIncrement(m.Type));
        // beforeId 原樣送回（網頁只採用自己還在等的那一頁，合併前審查 LIVE-2）
        JObject.Parse(older.Json).ShouldBeJson(($$"""{"type":"olderItems","conversationId":"{{C1}}","beforeId":"i5","items":[{"id":"i1"}],"hasMore":false}"""));
        JObject.Parse(closed.Json).ShouldBeJson(($$"""{"type":"conversationClosed","conversationId":"{{C1}}"}"""));
        JObject.Parse(resync.Json).ShouldBeJson(($$"""{"type":"resync","conversationId":"{{C1}}"}"""));
    }

    [Fact]
    public void 清單notify與clipboard的形狀_通知類在init屏障保留()
    {
        var list = ConversationGatewayRules.ConversationsMessage("""[{"id":"x"}]""");
        list.Type.Should().Be("conversations");
        ClientOutboxRules.MergeKey(list).Should().Be("conversations", "整份取代、以 type 合併");
        JObject.Parse(list.Json).ShouldBeJson(("""{"type":"conversations","items":[{"id":"x"}]}"""));

        var notify = ConversationGatewayRules.NotifyMessage(C1, "approval", "<b>標題</b>", null);
        JObject.Parse(notify.Json).ShouldBeJson((
            $$"""{"type":"notify","conversationId":"{{C1}}","kind":"approval","title":"<b>標題</b>","deviceName":null}"""));
        var clipboard = ConversationGatewayRules.ClipboardMessage(C1, "i9", "原文\n\"引號\"");
        JObject.Parse(clipboard.Json)["text"]!.Value<string>().Should().Be("原文\n\"引號\"");
        new[] { notify, clipboard }.Should().OnlyContain(m => ClientOutboxRules.KeptAcrossInit(m.Type) && m.Key == null);
    }

    private static ConversationChange Item(string conversationId, string itemId, long seq) =>
        new(conversationId, ConversationChangeKind.Item, seq, Owner, ItemId: itemId, Ord: seq, Ver: 1, Body: "{}");

    private static ConversationChange Removed(string conversationId, string itemId, long seq) =>
        new(conversationId, ConversationChangeKind.ItemRemoved, seq, Owner, ItemId: itemId);

    private static ConversationChange Change(string conversationId, ConversationChangeKind kind, long seq) => new(conversationId, kind, seq, Owner);

    private static ConversationChange Attention(string conversationId, string kind, long seq, bool fresh) =>
        new(conversationId, ConversationChangeKind.Attention, seq, Owner, AttentionKind: kind, Fresh: fresh);
}
