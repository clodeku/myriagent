using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 對話事件的合併鍵（WP4.3 計畫 D5）：<c>meta:{id}:{gen}</c>、<c>item:{id}:{itemId}</c>、<c>queue:{id}</c>；都要屬於那個對話
/// （WP4.1 的 <see cref="OutboxAppendRules.KeyBelongsTo"/>，不同對話的事件不會互相取代）
/// </summary>
public sealed class PublishKeyRulesTests
{
    private static readonly string Id = "0123456789abcdef0123456789abcdef";

    private static readonly string Other = new('f', 32);

    private static readonly string Boot = Guid.NewGuid().ToString("N");

    [Fact]
    public void 四種鍵_KeyBelongsTo都為真()
    {
        var keys = new[]
        {
            PublishKeyRules.Meta(Id, PublishKeyRules.Generation(Boot, 0)),
            PublishKeyRules.Meta(Id, PublishKeyRules.Generation(Boot, 17)),
            PublishKeyRules.Item(Id, "a1B2-c_3"),
            PublishKeyRules.Queue(Id),
        };

        keys.Should().AllSatisfy(key =>
        {
            OutboxAppendRules.KeyBelongsTo(key, Id).Should().BeTrue(key);
            OutboxAppendRules.KeyBelongsTo(key, Other).Should().BeFalse(key);
            OutboxAppendRules.Problem(new OutboxAppend(SyncStreams.Conversation(Id), ConversationEventKinds.Item, "{}", key, Id, 1)).Should().BeNull(key);
        });
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 鍵的格式逐字釘住()
    {
        PublishKeyRules.Generation("b00t", 3).Should().Be("b00t.3");
        PublishKeyRules.Meta(Id, "b00t.3").Should().Be("meta:" + Id + ":b00t.3");
        PublishKeyRules.Item(Id, "x9").Should().Be("item:" + Id + ":x9");
        PublishKeyRules.Queue(Id).Should().Be("queue:" + Id);
    }

    [Fact]
    public void meta鍵_世代不同就不同()
    {
        var first = PublishKeyRules.Meta(Id, PublishKeyRules.Generation(Boot, 1));

        PublishKeyRules.Meta(Id, PublishKeyRules.Generation(Boot, 2)).Should().NotBe(first, "一輪開始或結束的標記之後換新世代");
        PublishKeyRules.Meta(Id, PublishKeyRules.Generation(Guid.NewGuid().ToString("N"), 1)).Should().NotBe(first, "下一次啟動的同一個序號");
        PublishKeyRules.Meta(Id, PublishKeyRules.Generation(Boot, 1)).Should().Be(first);
        PublishKeyRules.Meta(Other, PublishKeyRules.Generation(Boot, 1)).Should().NotBe(first);
    }

    [Fact]
    public void 項目鍵_解回對話與項目_其他鍵解不出()
    {
        foreach (var itemId in new[] { "a", "A", "0f1e2d3c4b5a69788796a5b4c3d2e1f0", "x-y_z", new string('Z', 64) })
        {
            PublishKeyRules.TryParseItem(PublishKeyRules.Item(Id, itemId), out var wireId, out var parsed).Should().BeTrue(itemId);
            wireId.Should().Be(Id);
            parsed.Should().Be(itemId, "項目 id 照原樣、區分大小寫");
        }

        var rejected = new[]
        {
            null,
            "",
            PublishKeyRules.Meta(Id, "b.1"),
            PublishKeyRules.Queue(Id),
            "item:" + Id,
            "item:" + Id + ":",
            "item:" + Id.ToUpperInvariant() + ":x",
            "item:" + Id[..31] + ":x",
            "item:" + Id + "0:x",
            "item:" + Id + ":x:y",
            "item:" + Id + ":" + new string('a', 65),
            "Item:" + Id + ":x",
            " item:" + Id + ":x",
        };
        foreach (var key in rejected)
        {
            PublishKeyRules.TryParseItem(key, out var wireId, out var itemId).Should().BeFalse(key ?? "null");
            wireId.Should().BeEmpty();
            itemId.Should().BeEmpty();
        }
    }

    [Fact]
    public void 不合格的輸入丟例外()
    {
        FluentActions.Invoking(() => PublishKeyRules.Meta(Id.ToUpperInvariant(), "b.1")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PublishKeyRules.Meta(Id, "")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PublishKeyRules.Item("abc", "x")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PublishKeyRules.Item(Id, "x:y")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PublishKeyRules.Queue(Id + "0")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PublishKeyRules.Generation("a:b", 1)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PublishKeyRules.Generation(Boot, -1)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
