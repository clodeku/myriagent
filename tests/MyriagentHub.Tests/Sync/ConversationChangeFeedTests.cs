using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Tests.Sync;

/// <summary>對話的變更通知（WP4.2 計畫 D13）：有界佇列，滿了丟掉並設「整份重讀」，消費者一次取走</summary>
public sealed class ConversationChangeFeedTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void 依提交順序取走_取完是空的()
    {
        var feed = Feed(new ConversationSettings());
        var posted = 0;
        feed.Posted += () => posted++;

        feed.Post(Change(1));
        feed.Post(Change(2, ConversationChangeKind.Item));
        feed.Pending.Should().Be(2);

        var drained = new List<ConversationChange>();
        feed.TryDrain(drained, out var fullRefresh).Should().BeTrue();
        fullRefresh.Should().BeFalse();
        drained.Select(c => c.Seq).Should().Equal(1, 2);
        posted.Should().Be(2, "每一筆都喚醒消費者");
        feed.Pending.Should().Be(0);
        feed.TryDrain(drained, out fullRefresh).Should().BeFalse("沒有新的");
        fullRefresh.Should().BeFalse();
    }

    [Fact]
    public void 超過則數上限_丟掉並設整份重讀_取走後清掉旗標()
    {
        var feed = Feed(new ConversationSettings { ChangeFeedMaxEntries = 2 });
        var posted = 0;
        feed.Posted += () => posted++;

        for (var seq = 1; seq <= 3; seq++)
            feed.Post(Change(seq));

        posted.Should().Be(3, "丟掉的那一筆也要喚醒：消費者要看到旗標");
        var drained = new List<ConversationChange>();
        feed.TryDrain(drained, out var fullRefresh).Should().BeTrue();
        fullRefresh.Should().BeTrue();
        drained.Select(c => c.Seq).Should().Equal(1, 2);
        feed.TryDrain(drained, out fullRefresh).Should().BeFalse();
        fullRefresh.Should().BeFalse("旗標取走一次就清掉");

        feed.Post(Change(4));
        feed.TryDrain(drained, out fullRefresh).Should().BeTrue();
        fullRefresh.Should().BeFalse("取走之後又有空間：照常收");
        drained.Select(c => c.Seq).Should().Equal(1, 2, 4);
    }

    [Fact]
    public void 超過位元組上限_丟掉並設整份重讀()
    {
        var feed = Feed(new ConversationSettings { ChangeFeedMaxBytes = ConversationChangeFeed.EntryOverheadBytes * 3 });

        feed.Post(Change(1));
        feed.Post(Change(2, ConversationChangeKind.Item, body: new string('a', 200)));
        feed.Post(Change(3));

        var drained = new List<ConversationChange>();
        feed.TryDrain(drained, out var fullRefresh).Should().BeTrue();
        fullRefresh.Should().BeTrue("body 估計 400 位元組，加上開銷超過上限");
        drained.Select(c => c.Seq).Should().Equal(1, 3);
    }

    private static ConversationChangeFeed Feed(ConversationSettings settings) =>
        new(Microsoft.Extensions.Options.Options.Create(new HubOptions { Conversations = settings }));

    private static ConversationChange Change(long seq, ConversationChangeKind kind = ConversationChangeKind.Meta, string? body = null) =>
        new(Conv, kind, seq, "d_A", Body: body);
}
