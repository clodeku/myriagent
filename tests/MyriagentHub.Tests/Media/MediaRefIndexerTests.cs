using System.Net;
using System.Text;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 媒體參照的索引（WP4.7 計畫步 5；D6、D8）：從對話事件維護 <c>media_refs</c> 與項目欄位 blob 的引用；
/// 刪除、降級、移除項目時立即刪；游標跨重新啟動、還原世代換了就從 items 重建
/// </summary>
public sealed class MediaRefIndexerTests
{
    [Fact]
    public async Task 附件帶ref的conv_item會建列()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var attachment = MediaSeed.NewRef();
        var media = MediaSeed.NewRef();

        await MediaSeed.ItemAsync(host, device.Id, conversation, "item7", [attachment], [media]);

        await MediaBrowser.EventuallyAsync(() => MediaSeed.Rows(host, conversation).Count == 2, "附件與代理產出媒體的參照各一列");
        MediaSeed.Rows(host, conversation).Should().BeEquivalentTo(new[]
        {
            (device.Id, attachment, conversation, "item:item7", (string?)null),
            (device.Id, media, conversation, "item:item7", (string?)null),
        });
    }

    [Fact]
    public async Task attachments是blob參照時讀blob取ref_並記項目欄位的引用()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var mediaRef = MediaSeed.NewRef();
        var content = Encoding.UTF8.GetBytes(MediaSeed.Attachments([mediaRef]).ToString(Newtonsoft.Json.Formatting.None));
        var sha = await MediaSeed.StoreBlobAsync(host, content, BlobKinds.ItemBody);

        await MediaSeed.ItemAsync(host, device.Id, conversation, "item1",
            edit: item => item["attachments"] = new JObject { ["blob"] = sha, ["length"] = content.LongLength });
        await MediaSeed.IndexedAsync(host);

        MediaSeed.Rows(host, conversation).Select(r => (r.Ref, r.Purpose)).Should().Equal((mediaRef, "item:item1"));
        MediaSeed.BlobRefs(host, BlobRefKinds.Item).Should().Equal((sha, conversation + ":item1"));
    }

    [Fact]
    public async Task blob讀不到時略過那欄不丟例外_之後的事件照常處理()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var media = MediaSeed.NewRef();
        var later = MediaSeed.NewRef();

        await MediaSeed.ItemAsync(host, device.Id, conversation, "item1", mediaRefs: [media],
            edit: item => item["attachments"] = new JObject { ["blob"] = new string('c', 64), ["length"] = 100 });
        await MediaSeed.ItemAsync(host, device.Id, conversation, "item2", [later]);
        await MediaSeed.IndexedAsync(host);

        MediaSeed.Rows(host, conversation).Select(r => (r.Ref, r.Purpose)).Should().BeEquivalentTo(new[] { (media, "item:item1"), (later, "item:item2") });
    }

    [Fact]
    public async Task 同一筆換新時舊參照與項目欄位blob的引用移除_last_ref_at設成現在()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var (r1, r2) = (MediaSeed.NewRef(), MediaSeed.NewRef());
        var shaA = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(300), BlobKinds.ItemBody);
        var shaB = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(400), BlobKinds.ItemBody);
        var stored = host.Host.Clock.NowMs;

        await MediaSeed.ItemAsync(host, device.Id, conversation, "item1", [r1, r2], edit: item => item["text"] = new JObject { ["blob"] = shaA, ["length"] = 300 });
        await MediaSeed.IndexedAsync(host);
        MediaSeed.BlobRefs(host, BlobRefKinds.Item).Should().Equal((shaA, conversation + ":item1"));

        host.Host.Clock.Advance(TimeSpan.FromMinutes(5));
        await MediaSeed.ItemAsync(host, device.Id, conversation, "item1", [r2], ver: 2, edit: item => item["text"] = new JObject { ["blob"] = shaB, ["length"] = 400 });
        await MediaSeed.IndexedAsync(host);

        MediaSeed.Rows(host, conversation).Select(r => r.Ref).Should().Equal(r2);
        MediaSeed.BlobRefs(host, BlobRefKinds.Item).Should().Equal((shaB, conversation + ":item1"));
        MediaSeed.BlobColumn(host, shaA, "last_ref_at").Should().Be(host.Host.Clock.NowMs, "引用移除時更新（D41）");
        MediaSeed.BlobColumn(host, shaB, "last_ref_at").Should().Be(stored, "還在用的不動");
    }

    [Fact]
    public async Task conv_itemRemoved刪那一筆的列_同對話其他項目的列還在()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var (r1, r2, r3) = (MediaSeed.NewRef(), MediaSeed.NewRef(), MediaSeed.NewRef());
        await MediaSeed.ItemAsync(host, device.Id, conversation, "item1", [r1], [r2]);
        await MediaSeed.ItemAsync(host, device.Id, conversation, "item2", [r3]);
        await MediaSeed.IndexedAsync(host);
        MediaSeed.Rows(host, conversation).Should().HaveCount(3);

        await MediaSeed.RemoveItemAsync(host, device.Id, conversation, "item1");
        await MediaSeed.IndexedAsync(host);

        MediaSeed.Rows(host, conversation).Select(r => (r.Ref, r.Purpose)).Should().Equal((r3, "item:item2"));
    }

    [Fact]
    public async Task 改成metadata刪整個對話的列_含抽屜與fileFetch的()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var other = await MediaSeed.ConversationAsync(host, device.Id);
        await MediaSeed.ItemAsync(host, device.Id, conversation, "item1", [MediaSeed.NewRef()]);
        await MediaSeed.ItemAsync(host, device.Id, other, "item1", [MediaSeed.NewRef()]);
        await MediaSeed.IndexedAsync(host);
        await MediaSeed.RefAsync(host, device.Id, conversation, MediaSeed.NewRef(), MediaRefPurposes.List);
        await MediaSeed.RefAsync(host, device.Id, conversation, MediaSeed.NewRef(), MediaRefPurposes.File);
        MediaSeed.Rows(host, conversation).Should().HaveCount(3);

        await MediaSeed.SetPolicyAsync(host, device.Id, conversation, ConversationSyncPolicies.Metadata);
        await MediaSeed.IndexedAsync(host);

        MediaSeed.Rows(host, conversation).Should().BeEmpty();
        MediaSeed.Rows(host, other).Should().HaveCount(1, "別的對話不受影響");
    }

    [Fact]
    public async Task conv_deleted刪列_縮圖的引用與縮圖快取()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, token) = await host.DeviceAsync();
        using var _d = device;
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        var mediaRef = MediaSeed.NewRef();
        var key = MediaKeyRules.Format(device.Id, mediaRef);
        await MediaSeed.ItemAsync(host, device.Id, conversation, "item1", [mediaRef]);
        await MediaSeed.IndexedAsync(host);
        var png = MediaBytes.Png(600);
        using (var put = await MediaSeed.PutThumbAsync(host, token, mediaRef, conversation, MediaHost.Bytes(png)))
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        var cache = host.Services.GetRequiredService<ThumbCache>();
        (await cache.WriteAsync(key, null, png, CancellationToken.None)).Should().BeTrue();
        (await cache.WriteAsync(key, "123", png, CancellationToken.None)).Should().BeTrue();
        MediaSeed.BlobRefs(host, BlobRefKinds.Thumb).Should().Equal((MediaBytes.Sha(png), key));

        host.Host.Clock.Advance(TimeSpan.FromMinutes(3));
        await MediaSeed.DeleteAsync(host, device.Id, conversation);
        await MediaSeed.IndexedAsync(host);

        MediaSeed.Rows(host, conversation).Should().BeEmpty();
        MediaSeed.BlobRefs(host, BlobRefKinds.Thumb).Should().BeEmpty();
        MediaSeed.BlobColumn(host, MediaBytes.Sha(png), "last_ref_at").Should().Be(host.Host.Clock.NowMs);
        Directory.EnumerateFiles(host.Host.Paths.ThumbCacheDir).Should().BeEmpty("鍵的快取與各版本都刪了");
        cache.TryRead(key, null).Should().BeNull();
    }

    [Fact]
    public async Task 查不到參照時媒體端點等索引追上_不先回404()
    {
        await using var host = await MediaHost.StartAsync();
        await using var device = await FakeMediaDevice.ConnectAsync(host);
        device.Serve();
        using var owner = await host.OwnerAsync();
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        await MediaSeed.IndexedAsync(host);
        var mediaRef = MediaSeed.NewRef();
        device.Files[mediaRef] = new FakeMediaFile(MediaBytes.Png(500));
        var indexer = host.Services.GetRequiredService<MediaRefIndexer>();
        var held = 0;
        indexer.DrainPageHookForTest = async ct =>
        {
            // 項目那一頁晚 800 ms 才寫：網頁在索引追上之前就來要
            if (Interlocked.Exchange(ref held, 1) == 0)
                await Task.Delay(800, ct);
        };

        await MediaSeed.ItemAsync(host, device.Id, conversation, "item1", [mediaRef]);
        MediaSeed.Rows(host, conversation).Should().BeEmpty("索引還沒寫");
        using var response = await owner.SendAsync(MediaBrowser.Get("/m/" + MediaKeyRules.Format(device.Id, mediaRef)));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "等索引追上（≤2 秒）再查一次");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(device.Files[mediaRef].Data);
    }

    [Fact]
    public async Task 重新啟動後游標接著走_不重做也不重建()
    {
        var dir = TempDir.Create();
        try
        {
            string deviceId;
            string conversation;
            await using (var host = await MediaHost.StartAsync(dataDir: dir))
            {
                // 第一輪（空的重建）做完才送事件：它們一定由增量處理
                await host.Services.GetRequiredService<MediaRefIndexer>().Ready.WaitAsync(TimeSpan.FromSeconds(10));
                var (device, _) = await host.DeviceAsync();
                using var _d = device;
                deviceId = device.Id;
                conversation = await MediaSeed.ConversationAsync(host, deviceId);
                await MediaSeed.ItemAsync(host, deviceId, conversation, "item1", [MediaSeed.NewRef()]);
                await MediaSeed.ItemAsync(host, deviceId, conversation, "item2", [MediaSeed.NewRef()]);
                await MediaSeed.IndexedAsync(host);
                var first = host.Services.GetRequiredService<MediaRefIndexer>();
                first.Rebuilds.Should().Be(1, "第一次部署沒有游標：從 items 重建（空的）");
                first.Processed.Should().Be(3);
            }

            await using (var host = await MediaHost.StartAsync(dataDir: dir))
            {
                var indexer = host.Services.GetRequiredService<MediaRefIndexer>();
                await indexer.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                await MediaSeed.ItemAsync(host, deviceId, conversation, "item3", [MediaSeed.NewRef()]);
                await MediaSeed.ItemAsync(host, deviceId, conversation, "item4", [MediaSeed.NewRef()]);
                await MediaSeed.IndexedAsync(host);

                indexer.Rebuilds.Should().Be(0, "游標與實例都對得上");
                indexer.Processed.Should().Be(2, "只處理重新啟動之後的兩筆");
                MediaSeed.Rows(host, conversation).Should().HaveCount(4);
                indexer.Cursor.Should().Be(MaxSeq(host));
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 換還原世代_從items分頁重建_重建期間healthz是200()
    {
        var dir = TempDir.Create();
        try
        {
            List<(string Device, string Ref, string Conversation, string Purpose, string? Thumb)> before;
            string conversation;
            await using (var host = await MediaHost.StartAsync(dataDir: dir))
            {
                var (device, _) = await host.DeviceAsync();
                using var _d = device;
                conversation = await MediaSeed.ConversationAsync(host, device.Id);
                var removed = await MediaSeed.ConversationAsync(host, device.Id);
                var items = Enumerable.Range(0, MediaRefIndexer.PageSize + 5)
                    .Select(i =>
                    {
                        var item = ConversationItem("item" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                        item["attachments"] = MediaSeed.Attachments([MediaSeed.NewRef()]);
                        return item;
                    })
                    .ToList();
                await MediaSeed.ItemsAsync(host, device.Id, conversation, items);
                await MediaSeed.ItemAsync(host, device.Id, removed, "gone", [MediaSeed.NewRef()]);
                await MediaSeed.DeleteAsync(host, device.Id, removed);
                await MediaSeed.IndexedAsync(host);
                before = MediaSeed.Rows(host);
                before.Should().HaveCount(MediaRefIndexer.PageSize + 5);
            }

            // 模擬還原：世代加 1、索引的表被換成舊的（這裡清空，另加一列已刪對話的殘留）
            using (var connection = SqliteSetup.Open(new MyriagentHub.Hosting.HubPaths(dir).SyncDb))
            {
                Exec(connection, "UPDATE server_meta SET value = '1' WHERE key = 'restore_generation'");
                Exec(connection, "DELETE FROM media_refs");
                Exec(connection, "INSERT INTO media_refs(device_id, ref, conversation_id, purpose, thumb_blob, created_at, last_used_at) "
                                 + "VALUES('" + before[0].Device + "', '" + MediaSeed.NewRef() + "', 'ffffffffffffffffffffffffffffffff', 'item:x', NULL, 1, 1)");
            }

            var pages = new List<int>();
            var inPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var host = await MediaHost.StartAsync(dataDir: dir, services: s =>
                         {
                             s.RemoveAll<MediaRefIndexer>();
                             s.AddSingleton(sp =>
                             {
                                 var indexer = ActivatorUtilities.CreateInstance<MediaRefIndexer>(sp);
                                 indexer.RebuildPageHookForTest = async (page, ct) =>
                                 {
                                     lock (pages)
                                         pages.Add(page);
                                     inPage.TrySetResult();
                                     await release.Task.WaitAsync(ct);
                                 };
                                 return indexer;
                             });
                         }))
            {
                var indexer = host.Services.GetRequiredService<MediaRefIndexer>();
                await inPage.Task.WaitAsync(TimeSpan.FromSeconds(10));
                indexer.IsRebuilding.Should().BeTrue();
                using (var client = host.Client())
                using (var health = await client.GetAsync("/healthz"))
                    health.StatusCode.Should().Be(HttpStatusCode.OK, "重建不擋部署的健康閘門");
                release.TrySetResult();
                await indexer.Ready.WaitAsync(TimeSpan.FromSeconds(20));

                indexer.Rebuilds.Should().Be(1);
                pages.Should().Equal(1, 2);
                MediaSeed.Rows(host).Should().Equal(before, "重建出來的列和還原前相同，已刪對話的殘留被對帳拿掉");
                indexer.Cursor.Should().Be(MaxSeq(host));
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    private static JObject ConversationItem(string id) => MyriagentHub.Tests.Sync.ConversationSamples.Item(id: id);

    private static long MaxSeq(MediaHost host)
    {
        using var connection = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(seq) FROM events";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
