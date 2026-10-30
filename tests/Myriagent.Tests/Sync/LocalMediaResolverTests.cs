using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;

namespace Myriagent.Tests.Sync;

/// <summary>
/// 項目沒載入時唯讀讀 items.json 的索引（WP4.7 計畫 D26；合併前審查 R1）：同一份檔同時進來的轉送只讀一次（單一飛行）、
/// 超過 8 個對話時只丟最早建的那一個、只取索引要的欄位（其他欄位不影響）
/// </summary>
public sealed class LocalMediaResolverTests : IDisposable
{
    private readonly MediaTestFiles files = new();

    public void Dispose() => files.Dispose();

    private static LocalMediaResolver Resolver() => new((_, _) => Task.FromResult<MediaScopeSnapshot?>(null), MediaTestFiles.Guard());

    private static LocalMediaRefRow Row(string itemId = "item-1", string attachmentId = "att-1")
        => new(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, itemId, attachmentId, null, 1791600000000);

    /// <summary>一個沒載入的對話：items.json 有一則帶附件的使用者訊息與一則很長的工具輸出；回快照與附件的路徑</summary>
    private (MediaScopeSnapshot Snapshot, string Photo) Conversation(string name)
    {
        var directory = files.Dir.CreateDirectory(Path.Combine("data", "conversations", name));
        var photo = Path.Combine(directory, "attachments", "photo.png");
        JsonFile.Write(Path.Combine(directory, "items.json"), new List<AgentChatItem>
        {
            new() { Id = "item-0", Kind = AgentItemKind.Tool, Text = "跑測試", Output = new string('o', 12_000), MediaPaths = [@"C:\repo\out\chart.png"] },
            new()
            {
                Id = "item-1", Kind = AgentItemKind.User, Text = "看這張圖",
                Attachments = [new AgentAttachment { Id = "att-1", FileName = "圖.png", StoredPath = photo, Kind = AgentAttachmentKind.Image }],
            },
        });
        return (files.Snapshot(itemsLoaded: false) with { ConversationId = name, ConversationDirectory = directory }, photo);
    }

    [Fact]
    public async Task 同時解析同一個沒載入的對話_items_json只讀一次()
    {
        var resolver = Resolver();
        var (snapshot, photo) = Conversation("c1");
        // 拉長讀檔的時間：沒有單一飛行時，同時進來的每一個都會自己讀一次
        resolver.IndexReadingForTest = () => Thread.Sleep(200);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => resolver.Resolve(snapshot, Row()))));

        results.Should().OnlyContain(r => r != null && r.Path == photo && r.FileName == "圖.png" && r.Sequence == null);
        resolver.IndexReadsForTest.Should().Be(1, "同一份檔只讀、只解析一次，其他的等同一個結果");
    }

    [Fact]
    public void 超過8個對話時只丟最早建的那一個_其他的不重讀()
    {
        var resolver = Resolver();
        var conversations = Enumerable.Range(1, 9).Select(i => Conversation($"c{i}")).ToList();

        foreach (var (snapshot, _) in conversations.Take(8))
            resolver.Resolve(snapshot, Row()).Should().NotBeNull();
        resolver.IndexReadsForTest.Should().Be(8);

        resolver.Resolve(conversations[8].Snapshot, Row()).Should().NotBeNull();
        resolver.IndexReadsForTest.Should().Be(9);

        resolver.Resolve(conversations[1].Snapshot, Row()).Should().NotBeNull();
        resolver.IndexReadsForTest.Should().Be(9, "第 2 個對話的索引還在（原本超過上限就整個清掉，換對話瀏覽時全部重讀）");
        resolver.Resolve(conversations[0].Snapshot, Row()).Should().NotBeNull();
        resolver.IndexReadsForTest.Should().Be(10, "最早建的那一個被丟掉了");
    }

    [Fact]
    public void 只取索引的欄位_代理媒體有序號_檔案改了重讀()
    {
        var resolver = Resolver();
        var (snapshot, _) = Conversation("c1");

        var media = resolver.Resolve(snapshot, new LocalMediaRefRow(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, "item-0", null, @"C:\repo\out\chart.png", 1));
        media.Should().Be(new ResolvedMedia(@"C:\repo\out\chart.png", "chart.png", 1));
        resolver.Resolve(snapshot, Row(attachmentId: "att-9")).Should().BeNull("items.json 裡沒有這個附件");
        resolver.Resolve(snapshot, Row(itemId: "item-9")).Should().BeNull("沒有這個項目");
        resolver.IndexReadsForTest.Should().Be(1);

        var itemsJson = Path.Combine(snapshot.ConversationDirectory, "items.json");
        JsonFile.Write(itemsJson, new List<AgentChatItem>
        {
            new() { Id = "item-1", Kind = AgentItemKind.User, Attachments = [new AgentAttachment { Id = "att-2", FileName = "新.png", StoredPath = @"C:\x\新.png", Kind = AgentAttachmentKind.Image }] },
        });
        File.SetLastWriteTimeUtc(itemsJson, DateTime.UtcNow.AddMinutes(1));

        resolver.Resolve(snapshot, Row(attachmentId: "att-2"))!.FileName.Should().Be("新.png");
        resolver.IndexReadsForTest.Should().Be(2);
    }
}
