using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>寫進 outbox 之前的外框檢查（D55）：第 3 階段的事件不帶對話；帶對話的照 CheckEvent，合併鍵要屬於這個對話</summary>
public sealed class OutboxAppendRulesTests
{
    private static readonly string Id = new('a', 32);

    private static OutboxAppend Conv(string? key, string kind = ConversationEventKinds.Item) =>
        new(SyncStreams.Conversation(Id), kind, "{}", key, Id, 1);

    [Theory]
    [InlineData("meta:{id}", true)]
    [InlineData("item:{id}:0f1e2d", true)]
    [InlineData("queue:{id}:", true)]
    [InlineData("{id}", false)]
    [InlineData(":{id}", false)]
    [InlineData("meta:", false)]
    [InlineData("meta:{id}x", false)]
    [InlineData("meta:x{id}", false)]
    [InlineData("item:other:{id}", false)]
    [InlineData("meta:{ID}", false)]
    [InlineData("cli", false)]
    public void 合併鍵屬於對話_用途冒號對話id開頭(string pattern, bool expected)
    {
        var key = pattern.Replace("{id}", Id, StringComparison.Ordinal).Replace("{ID}", Id.ToUpperInvariant(), StringComparison.Ordinal);

        OutboxAppendRules.KeyBelongsTo(key, Id).Should().Be(expected, key);
        OutboxAppendRules.Problem(Conv(key)).Should().Be(expected ? null : "合併鍵與對話不符");
    }

    [Fact]
    public void 第3階段的事件_不帶對話照收_帶了epoch或是對話種類不收()
    {
        OutboxAppendRules.Problem(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, "{}", CatalogSyncRules.CliCoalesceKey)).Should().BeNull();
        OutboxAppendRules.Problem(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.LinkUpsert, "{}", CatalogSyncRules.LinkCoalesceKey("acc-1"))).Should().BeNull();
        OutboxAppendRules.Problem(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, "{}", null, Epoch: 1)).Should().NotBeNull();
        OutboxAppendRules.Problem(new OutboxAppend(SyncStreams.Conversation(Id), ConversationEventKinds.Meta, "{}", null)).Should().NotBeNull("對話事件一定帶 conversationId");
    }

    [Fact]
    public void 帶對話的_外框照CheckEvent_沒有合併鍵也可以()
    {
        OutboxAppendRules.Problem(Conv(null)).Should().BeNull();
        OutboxAppendRules.Problem(Conv(null, ConversationEventKinds.Meta) with { Epoch = 7 }).Should().BeNull();
        OutboxAppendRules.Problem(Conv(null) with { Epoch = null }).Should().NotBeNull();
        OutboxAppendRules.Problem(Conv(null) with { Epoch = 0 }).Should().NotBeNull();
        OutboxAppendRules.Problem(Conv(null) with { Stream = SyncStreams.Catalog }).Should().NotBeNull();
        OutboxAppendRules.Problem(Conv(null) with { ConversationId = Id.ToUpperInvariant(), Stream = SyncStreams.Conversation(Id.ToUpperInvariant()) }).Should().NotBeNull();
        OutboxAppendRules.Problem(Conv(null, SyncEventKinds.CliState)).Should().NotBeNull("帶對話的只收對話種類");
        FluentActions.Invoking(() => OutboxAppendRules.Problem(null!)).Should().Throw<ArgumentNullException>();
    }
}
