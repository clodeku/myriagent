using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.Store;

/// <summary><c>media_refs_local</c> 的讀寫（WP4.7 計畫步 9，D3、D4）：新增、依參照查、依項目或對話讀與刪；重開之後還在</summary>
public sealed class SyncStoreMediaTests : IDisposable
{
    private const long PairedAt = 1791500000000;

    private static readonly ServerInstance Server = new("srv-instance-1", 0);

    private static readonly string ConversationId = new('c', 32);

    private static readonly string OtherConversation = new('d', 32);

    private readonly TempDirectory temp = new("syncstore-media");
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");

    private string DbPath => temp.Combine("sync", "sync.db");

    public void Dispose() => temp.Dispose();

    private SyncStore Create() => SyncStore.Create(DbPath, self.DeviceId, PairedAt, Server, new TrustBundle(3, [self.Entry()]));

    private SyncStore Reopen() => SyncStore.Open(DbPath, self.DeviceId);

    private static LocalMediaRefRow Row(string conversation, string item, string? attachment, string? path, long at)
        => new(CrockfordBase32.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), conversation, item, attachment, path, at);

    [Fact]
    public void 依項目與對話讀_依建立時間排序_重開還在()
    {
        var a2 = Row(ConversationId, "item-1", null, @"C:\out\b.png", 20);
        var a1 = Row(ConversationId, "item-1", "att-1", null, 10);
        var b = Row(ConversationId, "item-2", "att-9", null, 5);
        var other = Row(OtherConversation, "item-1", "att-1", null, 1);
        using (var store = Create())
        {
            store.AddMediaRefs([a2, a1, b, other]);
            store.LoadMediaRefs(ConversationId, "item-1").Should().Equal(a1, a2);
            store.LoadMediaRefs(ConversationId).Should().Equal(b, a1, a2);
            store.LoadMediaRefs(ConversationId, "item-3").Should().BeEmpty();
        }

        using var reopened = Reopen();
        reopened.FindMediaRef(a1.Ref).Should().Be(a1);
        reopened.LoadMediaRefs(OtherConversation).Should().Equal(other);
    }

    [Fact]
    public void 依項目刪只刪那個項目_依對話刪整個對話_別的對話不動()
    {
        using var store = Create();
        var a = Row(ConversationId, "item-1", "att-1", null, 1);
        var b = Row(ConversationId, "item-2", null, @"C:\out\b.png", 2);
        var other = Row(OtherConversation, "item-1", "att-1", null, 3);
        store.AddMediaRefs([a, b, other]);

        store.RemoveMediaRefs(ConversationId, "item-1").Should().Be(1);
        store.FindMediaRef(a.Ref).Should().BeNull();
        store.FindMediaRef(b.Ref).Should().Be(b);

        store.RemoveMediaRefs(ConversationId).Should().Be(1);
        store.LoadMediaRefs(ConversationId).Should().BeEmpty();
        store.FindMediaRef(other.Ref).Should().Be(other);
        store.RemoveMediaRefs(ConversationId).Should().Be(0);
    }

    [Fact]
    public void 同一個參照再加不動_形狀不合格一筆都不寫()
    {
        using var store = Create();
        var a = Row(ConversationId, "item-1", "att-1", null, 1);
        store.AddMediaRefs([a]);
        store.AddMediaRefs([a with { ItemId = "item-9" }]);
        store.FindMediaRef(a.Ref).Should().Be(a, "ON CONFLICT DO NOTHING");

        var good = Row(ConversationId, "item-2", "att-2", null, 2);
        var both = Row(ConversationId, "item-3", "att-3", @"C:\x.png", 3);
        FluentActions.Invoking(() => store.AddMediaRefs([good, both])).Should().Throw<ArgumentException>();
        store.FindMediaRef(good.Ref).Should().BeNull("一個交易：不合格的一批一筆都不寫");
    }

    [Fact]
    public void 抽屜的列只讀這個對話_不早於界線_依建立時間排序()
    {
        using var store = Create();
        var old = Row(ConversationId, MediaServerRules.ListItemId, null, @"C:\p\old.png", 99);
        var edge = Row(ConversationId, MediaServerRules.ListItemId, null, @"C:\p\edge.png", 100);
        var later = Row(ConversationId, MediaServerRules.ListItemId, null, @"C:\p\b.png", 300);
        var mid = Row(ConversationId, MediaServerRules.ListItemId, null, @"C:\p\a.png", 200);
        var fetched = Row(ConversationId, MediaServerRules.FileItemId, null, @"C:\p\a.png", 200);
        var item = Row(ConversationId, "item-1", null, @"C:\p\a.png", 200);
        var other = Row(OtherConversation, MediaServerRules.ListItemId, null, @"C:\p\a.png", 200);
        store.AddMediaRefs([old, edge, later, mid, fetched, item, other]);

        store.LoadListRefs(ConversationId, 100).Should().Equal(edge, mid, later);
        store.LoadListRefs(OtherConversation, 0).Should().Equal(other);
    }

    [Fact]
    public void 每日修剪只刪過期的抽屜與fileFetch列_項目的列不動()
    {
        using var store = Create();
        var listOld = Row(ConversationId, MediaServerRules.ListItemId, null, @"C:\p\a.png", 999);
        var listKeep = Row(ConversationId, MediaServerRules.ListItemId, null, @"C:\p\b.png", 1000);
        var fileOld = Row(ConversationId, MediaServerRules.FileItemId, null, @"C:\p\a.txt", 4999);
        var fileKeep = Row(ConversationId, MediaServerRules.FileItemId, null, @"C:\p\b.txt", 5000);
        var item = Row(ConversationId, "item-1", "att-1", null, 1);
        store.AddMediaRefs([listOld, listKeep, fileOld, fileKeep, item]);

        store.PruneMediaRefs(listBefore: 1000, fileBefore: 5000).Should().Be(2);

        store.LoadMediaRefs(ConversationId).Should().Equal(item, listKeep, fileKeep);
        store.PruneMediaRefs(1000, 5000).Should().Be(0);
    }
}
