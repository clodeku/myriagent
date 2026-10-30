using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

public sealed class PullReduceRulesTests
{
    private const int Limit = PullReduceRules.MinServerPayloadBytes;

    internal static PulledEvent Event(long seq, string kind, object payload, long createdAt = 1791580000000, string? cls = null)
    {
        var (enc, text) = PayloadRules.Encode(SyncJson.Serialize(payload));
        var placement = SyncEventKinds.Placement(kind);
        return new PulledEvent(seq, placement?.Stream ?? "catalog", cls ?? placement?.Cls ?? "catalog", "server", kind, enc, text, createdAt);
    }

    internal static AccountUpsertPayload Upsert(string id, string label, long version = 1) =>
        new(id, "Claude", "claude:org:me@example.com", label, "#D97757", 0, "me@example.com", "org", "Claude Max", version, []);

    private static UsageSnapshotPayload Usage(string logicalId, double percent, string source = "d_SOURCE") =>
        new(logicalId, new SharedUsage(1791580000000, "api", [new SharedUsageWindow("session", "5 小時", percent, null, false)], null, null, null, false), source, 3);

    [Fact]
    public void 同鍵多筆_取seq最大_即使createdAt較舊()
    {
        var page = new[]
        {
            Event(10, SyncEventKinds.AccountUpsert, Upsert("la_A", "新"), createdAt: 1000),
            Event(5, SyncEventKinds.AccountUpsert, Upsert("la_A", "舊"), createdAt: 9999),
            Event(7, SyncEventKinds.PrefsSet, new PrefsSetPayload("theme", "light", 2), createdAt: 9999),
            Event(8, SyncEventKinds.PrefsSet, new PrefsSetPayload("theme", "dark", 3), createdAt: 1),
        };

        var delta = PullReduceRules.Reduce(page, Limit);

        delta.Accounts["la_A"].Seq.Should().Be(10);
        delta.Accounts["la_A"].Upsert!.Label.Should().Be("新", "依伺服器 seq，不看 createdAt");
        ((string?)delta.Prefs["theme"].Value).Should().Be("dark");
        delta.Ignored.Should().Be(0);
    }

    [Fact]
    public void deleted後又upsert_以upsert為準()
    {
        var delta = PullReduceRules.Reduce(
            [Event(3, SyncEventKinds.AccountDeleted, new AccountDeletedPayload("la_A", 2)), Event(4, SyncEventKinds.AccountUpsert, Upsert("la_A", "復活", 3))], Limit);

        delta.Accounts["la_A"].Upsert!.Label.Should().Be("復活", "伺服器會讓刪掉的帳戶復活");
        delta.Accounts["la_A"].Deleted.Should().BeNull();
    }

    [Fact]
    public void upsert後deleted_以deleted為準()
    {
        var delta = PullReduceRules.Reduce(
            [Event(4, SyncEventKinds.AccountUpsert, Upsert("la_A", "x")), Event(6, SyncEventKinds.AccountDeleted, new AccountDeletedPayload("la_A", 2))], Limit);

        delta.Accounts["la_A"].Upsert.Should().BeNull();
        delta.Accounts["la_A"].Deleted!.Version.Should().Be(2);
    }

    [Fact]
    public void 裝置發的種類_計入Ignored()
    {
        var delta = PullReduceRules.Reduce(
        [
            Event(1, SyncEventKinds.LinkUpsert, new LinkRemovedPayload("acc")),
            Event(2, SyncEventKinds.CliState, new CliStatePayload(new Dictionary<string, DeviceCliView>())),
            Event(3, "brand.new.kind", new { x = 1 }),
        ], Limit);

        delta.Ignored.Should().Be(3);
        delta.IgnoredKinds.Should().Equal(["brand.new.kind"], "裝置寫的種類是正常的，只有不認得的列為異常");
        delta.Accounts.Should().BeEmpty();
    }

    [Fact]
    public void 解不開的payload_計入Ignored不丟例外()
    {
        var page = new[]
        {
            new PulledEvent(1, "catalog", "catalog", "server", SyncEventKinds.AccountUpsert, "gzip", "不是 base64", 1),
            new PulledEvent(2, "catalog", "catalog", "server", SyncEventKinds.DeviceUpsert, "json", "{ 不是 JSON", 1),
            new PulledEvent(3, "catalog", "catalog", "server", SyncEventKinds.PrefsSet, "brotli", "{}", 1),
            new PulledEvent(4, "catalog", "catalog", "server", SyncEventKinds.DeviceRevoked, "json", "\"字串\"", 1),
            new PulledEvent(5, "catalog", "catalog", "server", SyncEventKinds.AccountDeleted, "json", "{}", 1),
            Event(6, SyncEventKinds.DeviceRevoked, new DeviceRevokedPayload("d_X", 5, "retired")),
        };

        var delta = PullReduceRules.Reduce(page, Limit);

        delta.Ignored.Should().Be(5);
        delta.IgnoredKinds.Should().BeEquivalentTo([SyncEventKinds.AccountUpsert, SyncEventKinds.DeviceUpsert, SyncEventKinds.PrefsSet, SyncEventKinds.DeviceRevoked, SyncEventKinds.AccountDeleted]);
        delta.Revoked.Should().ContainKey("d_X");
    }

    [Fact]
    public void 解碼上限至少4MB()
    {
        PullReduceRules.ServerPayloadLimit(SyncLimits.Default).Should().Be(4 * 1024 * 1024);
        PullReduceRules.ServerPayloadLimit(SyncLimits.Default with { MaxPullBytes = 8 * 1024 * 1024 }).Should().Be(8 * 1024 * 1024);

        var links = Enumerable.Range(0, 5000)
            .Select(i => new AccountLinkView($"d_{i:D26}", $"acc-{i}", "isolated", "ready", "已登入", $"user{i}@example.com", "org", "Claude Max",
                null, null, false, true, new LinkSessions(0, 0, 0), 1, false))
            .ToList();
        var big = Upsert("la_A", "大") with { Links = links };
        var e = Event(1, SyncEventKinds.AccountUpsert, big);
        e.Enc.Should().Be("gzip");
        SyncJson.Serialize(big).Length.Should().BeGreaterThan(SyncLimits.Default.MaxPullBytes, "比 1 MB 的拉取上限還大");

        PullReduceRules.Reduce([e], PullReduceRules.ServerPayloadLimit(SyncLimits.Default)).Accounts.Should().ContainKey("la_A");
        PullReduceRules.Reduce([e], SyncLimits.Default.MaxPullBytes).Ignored.Should().Be(1, "上限太小就解不開");
    }

    [Fact]
    public void usage點全部保留_依seq排序()
    {
        var delta = PullReduceRules.Reduce(
        [
            Event(9, SyncEventKinds.UsageSnapshot, Usage("la_A", 30), createdAt: 300),
            Event(4, SyncEventKinds.UsageSnapshot, Usage("la_A", 10), createdAt: 100),
            Event(6, SyncEventKinds.UsageSnapshot, Usage("la_A", 20), createdAt: 200),
        ], Limit);

        delta.UsagePoints.Select(p => p.Seq).Should().Equal(4, 6, 9);
        delta.UsagePoints.Select(p => p.CreatedAt).Should().Equal(100, 200, 300);
        delta.UsagePoints[^1].Payload.Usage.Windows[0].Percent.Should().Be(30);
    }

    [Fact]
    public void 對話事件_另放一份_依頁內順序原樣_不算略過也不警告()
    {
        var id = new string('a', 32);
        PulledEvent Conv(long seq, string kind, string payload)
        {
            var (cls, stream) = ConversationEventKinds.Placement(kind, id)!.Value;
            var (enc, text) = PayloadRules.Encode(payload);
            return new PulledEvent(seq, stream, cls, "d_OWNER", kind, enc, text, 1791580000000, id, 1);
        }

        var created = Conv(3, ConversationEventKinds.Created, "{}");
        var item = Conv(4, ConversationEventKinds.Item, "不是 JSON 也照樣交出去");
        var removed = Conv(9, ConversationEventKinds.ItemRemoved, "{}");
        var page = new[]
        {
            created,
            Event(5, SyncEventKinds.AccountUpsert, Upsert("la_A", "x")),
            item,
            new PulledEvent(7, "catalog", "catalog", "server", "conv.unknown", "json", "{}", 1),
            removed,
        };

        var delta = PullReduceRules.Reduce(page, Limit);

        delta.Conversations.Should().Equal(created, item, removed);
        delta.Accounts.Should().ContainKey("la_A");
        delta.Ignored.Should().Be(1, "只有不認得的 conv.unknown");
        delta.IgnoredKinds.Should().Equal("conv.unknown");
        ConversationEventKinds.All.Should().AllSatisfy(kind => PullReduceRules.Reduce([Conv(1, kind, "{}")], Limit).Conversations.Should().ContainSingle(kind));
        PullReduceRules.Reduce([Event(5, SyncEventKinds.AccountUpsert, Upsert("la_A", "x"))], Limit).Conversations.Should().BeEmpty();
    }

    [Fact]
    public void 游標_每個cls只前進到該cls的最大seq()
    {
        var page = new[]
        {
            Event(5, SyncEventKinds.AccountUpsert, Upsert("la_A", "x")),
            Event(12, SyncEventKinds.UsageSnapshot, Usage("la_A", 1)),
            Event(8, SyncEventKinds.DeviceUpsert, new DeviceRevokedPayload("d_X", 1, "retired")),
        };

        var cursors = PullReduceRules.AdvanceCursors(new Dictionary<string, long> { ["catalog"] = 3, ["usage"] = 20 }, page);

        cursors["catalog"].Should().Be(8);
        cursors["usage"].Should().Be(20, "游標不倒退");
        PullReduceRules.AdvanceCursors(new Dictionary<string, long>(), page)["usage"].Should().Be(12);
    }

    private static PulledEvent Conv(long seq, string kind, string conversationId)
    {
        var (cls, stream) = ConversationEventKinds.Placement(kind, conversationId)!.Value;
        var (enc, text) = PayloadRules.Encode("{}");
        return new PulledEvent(seq, stream, cls, "d_OWNER", kind, enc, text, 1791580000000, conversationId, 1);
    }

    [Fact]
    public void AdvanceCursors不收對話類別()
    {
        var a = new string('a', 32);
        var page = new[]
        {
            Event(5, SyncEventKinds.AccountUpsert, Upsert("la_A", "x")),
            Conv(6, ConversationEventKinds.Created, a),
            Conv(7, ConversationEventKinds.Item, a),
            new PulledEvent(9, "catalog", "brand.new", "server", "brand.new.kind", "json", "{}", 1),
        };

        var cursors = PullReduceRules.AdvanceCursors(new Dictionary<string, long> { ["catalog"] = 3 }, page);

        cursors.Should().BeEquivalentTo(new Dictionary<string, long> { ["catalog"] = 5 }, "v1 的游標只放第 3 階段的類別（對話的在 conversation_cursors）");
        PullReduceRules.AdvanceEveryClass(new Dictionary<string, long> { ["catalog"] = 3 }, page)
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["catalog"] = 5, ["meta"] = 6, ["item"] = 7, ["brand.new"] = 9 }, "沒有盒子的引擎照第 3 階段原本的做法");
    }

    [Fact]
    public void AdvanceConversationCursors各類別只前進到頁裡的最大seq()
    {
        var a = new string('a', 32);
        var b = new string('b', 32);
        var c = new string('c', 32);
        var page = new[]
        {
            Event(4, SyncEventKinds.AccountUpsert, Upsert("la_A", "x")),
            Conv(5, ConversationEventKinds.Created, a),
            Conv(6, ConversationEventKinds.Item, a),
            Conv(7, ConversationEventKinds.Meta, b),
            Conv(8, ConversationEventKinds.Item, a),
            Conv(9, ConversationEventKinds.Item, c),
            Conv(3, ConversationEventKinds.Meta, c),
            new PulledEvent(10, SyncStreams.Conversation(a), SyncClasses.Item, "d_OWNER", "conv.future", "json", "{}", 1, a, 1),
        };
        var requested = new[]
        {
            new PullCursor(0, SyncClasses.Catalog),
            new PullCursor(0, SyncClasses.Usage),
            new PullCursor(2, SyncClasses.Meta),
            new PullCursor(0, Stream: SyncStreams.Conversation(a)),
            new PullCursor(0, Stream: SyncStreams.Conversation(b)),
        };
        var before = new Dictionary<string, long> { ["meta"] = 2, [SyncStreams.Conversation(b)] = 20, [SyncStreams.Conversation(c)] = 1 };

        var next = PullReduceRules.AdvanceConversationCursors(before, requested, page);

        next.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["meta"] = 7,
            [SyncStreams.Conversation(a)] = 10,
            [SyncStreams.Conversation(b)] = 20,
            [SyncStreams.Conversation(c)] = 1,
        }, "meta 看類別、串流看 stream；不認得的種類照樣前進；沒要的 c 不動；不倒退；第 3 階段的類別不在這裡");
        before.Should().HaveCount(3, "不改傳入的字典");
        PullReduceRules.AdvanceConversationCursors(before, [], page).Should().BeEquivalentTo(before, "這一頁沒要對話的游標");
    }
}
