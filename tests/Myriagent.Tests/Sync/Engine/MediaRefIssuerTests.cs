using System.Net;
using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 發布時配發媒體參照、歷史回補與縮圖上傳（WP4.7 計畫步 9，D3～D5、D30、D45）：引擎＋盒子＋假 UI（<see cref="FakeConversationUi"/>）＋假伺服器，
/// 縮圖的 E8／E10 是 <see cref="FakeRelayHandler"/>、中樞的媒體快照是 <see cref="FakeMediaScope"/>（預設遠端政策 View）
/// </summary>
public sealed class MediaRefIssuerTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Local);

    private static readonly string[] WithMedia = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Media1];

    private static readonly string[] WithoutMedia = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(bool media = true)
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box) { Policy = new DevicePolicy("View", false) };
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            Box.MediaScope = Scope;
            H.Server.Features = media ? WithMedia : WithoutMedia;
            _ = H.Server.Conversations;
            Relay.Responder = Respond;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        public FakeMediaScope Scope { get; } = new();

        public FakeRelayHandler Relay { get; } = new();

        public MediaTestFiles Files { get; } = new();

        /// <summary>E10 回的參照（依序；之後才設也照用）</summary>
        public List<MissingThumb> Missing { get; } = [];

        public Task StartAsync() => H.StartAsync(e =>
        {
            e.MediaHttpHandlerForTest = Relay;
            e.MediaGuardForTest = MediaTestFiles.Guard();
            e.MediaThumbnailForTest = (_, _, _) => Task.FromResult<byte[]?>(MediaTestFiles.Png);
        });

        /// <summary>停掉目前的引擎、以同一份資料與同樣的測試接縫再啟動</summary>
        public async Task RestartAsync()
        {
            await H.Engine.StopAsync(TimeSpan.FromSeconds(5));
            await StartAsync();
        }

        public Task IdleAsync() => H.IdleAsync();

        public Task SendAsync(double seconds = 1) => H.AdvanceAsync(seconds);

        public FakeConversation New(string? policy = null, int minute = 0) => Ui.Add(new FakeConversation(new AgentConversation
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "媒體",
            AccountId = EngineHarness.LocalAccount,
            Provider = AgentProvider.Claude,
            SyncPolicy = policy,
            CreatedAt = T0,
            UpdatedAt = T0.AddMinutes(minute),
        }));

        public void Meta(FakeConversation c) => Ui.Put(new MetaChanged(c.Id, c.Snapshot()));

        public AgentChatItem Item(FakeConversation c, AgentChatItem item)
        {
            item.Ord ??= c.NextOrd++;
            c.Items.Add(item);
            Changed(c, item);
            return item;
        }

        public void Changed(FakeConversation c, AgentChatItem item) => Ui.Put(new ItemChanged(c.Id, ChatItemCopyRules.Copy(item)));

        /// <summary>附一張圖的使用者訊息（路徑在 <see cref="MediaTestFiles.AttachmentDirectory"/>，要真的檔時 <paramref name="write"/>）</summary>
        public AgentChatItem Photo(FakeConversation c, string name = "photo.png", bool write = false, AgentItemStatus status = AgentItemStatus.Done)
        {
            var path = Path.Combine(Files.AttachmentDirectory, name);
            if (write)
                Files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", name), MediaTestFiles.Png);
            return Item(c, new AgentChatItem
            {
                Kind = AgentItemKind.User, Status = status, Text = "看這張圖", CreatedAt = T0, UpdatedAt = T0,
                Attachments = [new AgentAttachment { Id = "att-" + Guid.NewGuid().ToString("N")[..8], FileName = name, StoredPath = path, Kind = AgentAttachmentKind.Image, SizeBytes = 11 }],
            });
        }

        public Task<IReadOnlyList<LocalMediaRefRow>> RefsAsync(FakeConversation c, string? itemId = null) => H.Inspect(v => v.Store!.LoadMediaRefs(c.Id, itemId));

        public async Task<IReadOnlyList<OutboxEntry>> PendingItemsAsync(FakeConversation c)
            => (await H.Inspect(v => v.Outbox)).Where(e => e.ConversationId == c.Id && e.Kind == ConversationEventKinds.Item && e.State == OutboxState.Pending).ToList();

        public IReadOnlyList<SyncEvent> SentItems() => H.Server.Batches.SelectMany(b => b.Events).Where(e => e.Kind == ConversationEventKinds.Item).ToList();

        public IReadOnlyList<RelayPut> ThumbPuts() => Relay.Requests.Where(r => r.Method == HttpMethod.Put && r.Path.StartsWith("/api/v1/device/thumbs/", StringComparison.Ordinal)).ToList();

        public async Task GoOfflineAsync()
        {
            H.Server.ConnectFailure = _ => EngineHarness.Transient();
            H.Server.Current!.Drop();
            await IdleAsync();
        }

        public async Task ReconnectAsync()
        {
            H.Server.ConnectFailure = null;
            if (H.Server.Current is { Closed: false } session)
                session.Drop();
            await IdleAsync();
            H.Engine.RetryNow();
            await IdleAsync();
            await SendAsync(2);
            (await H.StateAsync()).Should().Be(SyncEngineState.Live);
        }

        /// <summary>E8 一律 200；E10 回 <see cref="Missing"/>（一頁，沒有下一頁）</summary>
        private HttpResponseMessage? Respond(RelayPut request)
        {
            if (request.Method == HttpMethod.Put && request.Path.StartsWith("/api/v1/device/thumbs/", StringComparison.Ordinal))
                return Json("""{"sha256":"00","length":1}""");
            if (request.Method == HttpMethod.Get && request.Path == "/api/v1/device/thumbs/missing")
            {
                var refs = new JArray(Missing.Select(m => new JObject { ["ref"] = m.Ref, ["conversationId"] = m.ConversationId, ["itemId"] = m.ItemId }));
                return Json(new JObject { ["refs"] = refs, ["next"] = null }.ToString());
            }

            return null;
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public async ValueTask DisposeAsync()
        {
            await H.DisposeAsync();
            Files.Dispose();
        }
    }

    private static JObject Obj(OutboxEntry e) => JObject.Parse(PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開"));

    private static JObject Obj(SyncEvent e) => JObject.Parse(PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開"));

    private static string? AttachmentRef(JObject payload) => payload["attachments"]?[0]?["ref"]?.Value<string>();

    // ───────────────────────────── 配發（D3～D5） ─────────────────────────────

    [Fact]
    public async Task Image附件的快照有1筆參照()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Photo(c);
        await rig.IdleAsync();

        var row = (await rig.RefsAsync(c)).Should().ContainSingle().Subject;
        row.Ref.Should().HaveLength(26);
        ConversationEventRules.IsMediaRef(row.Ref).Should().BeTrue();
        row.ItemId.Should().Be(item.Id);
        row.AttachmentId.Should().Be(item.Attachments[0].Id);
        row.MediaPath.Should().BeNull();
        var payload = Obj((await rig.PendingItemsAsync(c)).Single());
        AttachmentRef(payload).Should().Be(row.Ref);
        payload.ToString().Should().NotContain("storedPath").And.NotContain(rig.Files.AttachmentDirectory.Replace(@"\", @"\\"), "本機路徑不上線");
        c.Items[0].MediaRefs.Should().BeNull("中樞的項目（items.json）不存參照");
    }

    [Fact]
    public async Task 代理產出的圖片()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Item(c, new AgentChatItem
        {
            Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, ToolName = "產生圖片", CreatedAt = T0, UpdatedAt = T0,
            MediaPaths = [@"C:\repo\out\chart.png", @"C:\repo\out\clip.mp4"],
        });
        await rig.IdleAsync();

        var rows = await rig.RefsAsync(c);
        rows.Select(r => r.MediaPath).Should().BeEquivalentTo([@"C:\repo\out\chart.png", @"C:\repo\out\clip.mp4"]);
        rows.Should().OnlyContain(r => r.AttachmentId == null && r.ItemId == item.Id);
        var media = (JArray)Obj((await rig.PendingItemsAsync(c)).Single())["media"]!;
        media.Select(m => m["ref"]!.Value<string>()).Should().BeEquivalentTo(rows.Select(r => r.Ref));
        media.Select(m => m["kind"]!.Value<string>()).Should().Equal("image", "video");
    }

    [Fact]
    public async Task 第二次投影參照相同_來源摘要不變()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Photo(c);
        await rig.SendAsync(2);
        var first = (await rig.RefsAsync(c)).Single();

        item.Text = "改過的文字";
        rig.Changed(c, item);
        await rig.IdleAsync();
        await rig.SendAsync(2);

        (await rig.RefsAsync(c)).Should().Equal(first);
        var sent = rig.SentItems().Where(e => e.ConversationId == c.Id).ToList();
        sent.Should().HaveCount(2);
        sent.Select(e => AttachmentRef(Obj(e))).Should().Equal(first.Ref, first.Ref);
        var published = (await rig.H.Inspect(v => v.Store!.LoadPublishedItems(c.Id))).Single();
        published.SrcSha256.Should().Be(PublishDigestRules.Source(c.Items[0]), "來源摘要對中樞的快照算（沒有參照），重整時才比得起來");
    }

    [Fact]
    public async Task Document附件沒有參照_UNC路徑不發參照()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, new AgentChatItem
        {
            Kind = AgentItemKind.User, Status = AgentItemStatus.Done, Text = "文件", CreatedAt = T0, UpdatedAt = T0,
            Attachments = [new AgentAttachment { Id = "att-doc", FileName = "a.pdf", StoredPath = @"C:\data\a.pdf", Kind = AgentAttachmentKind.Document }],
            MediaPaths = [@"\\evil\share\x.png", "//evil/share/y.png"],
        });
        await rig.IdleAsync();

        (await rig.RefsAsync(c)).Should().BeEmpty();
        var payload = Obj((await rig.PendingItemsAsync(c)).Single());
        AttachmentRef(payload).Should().BeNull();
        ((JArray)payload["media"]!).Should().OnlyContain(m => m["ref"] == null);
        rig.Scope.Queries.Should().BeEmpty("配參照只看字串，不問中樞也不碰檔案系統");
    }

    [Fact]
    public async Task 先寫media_refs_local後寫outbox_寫outbox失敗重啟之後沿用同一個參照()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync(2);
        await rig.H.Inspect(v => v.Store!.FaultInjection = point =>
        {
            if (point == "AppendMany:commit")
                throw new SqliteException("模擬：寫入失敗", 10);
        });

        rig.Photo(c);
        await rig.IdleAsync();

        (await rig.PendingItemsAsync(c)).Should().BeEmpty("outbox 沒寫成");
        var issued = (await rig.RefsAsync(c)).Should().ContainSingle("參照先寫進 media_refs_local").Subject;

        await rig.H.Inspect(v => v.Store!.FaultInjection = null);
        await rig.RestartAsync();
        await rig.SendAsync(2);
        await rig.SendAsync(2);

        (await rig.RefsAsync(c)).Should().Equal(issued);
        rig.SentItems().Where(e => e.ConversationId == c.Id).Select(e => AttachmentRef(Obj(e))).Should().ContainSingle().Which.Should().Be(issued.Ref);
    }

    [Fact]
    public async Task 已發布的舊項目也補到參照()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Photo(c);
        await rig.SendAsync(2);
        // 舊版發布的：published_items 有摘要相同的列、media_refs_local 沒有
        var old = (await rig.RefsAsync(c)).Single();
        await rig.H.Inspect(v => v.Store!.RemoveMediaRefs(c.Id));
        var sentBefore = rig.SentItems().Count;

        await rig.RestartAsync();
        await rig.SendAsync(2);
        await rig.SendAsync(2);

        var row = (await rig.RefsAsync(c)).Should().ContainSingle().Subject;
        row.Ref.Should().NotBe(old.Ref);
        row.AttachmentId.Should().Be(item.Attachments[0].Id);
        var resent = rig.SentItems().Skip(sentBefore).Where(e => e.ConversationId == c.Id).ToList();
        resent.Select(e => AttachmentRef(Obj(e))).Should().ContainSingle().Which.Should().Be(row.Ref, "摘要相同、缺參照：重整時重新投影（RefsMissing）");

        // 參照齊了：再重整一次不重送
        var count = rig.SentItems().Count;
        await rig.RestartAsync();
        await rig.SendAsync(2);
        rig.SentItems().Count.Should().Be(count);
    }

    [Fact]
    public async Task 回補由新到舊_第一次協商出media1時()
    {
        await using var rig = new Rig(media: false);
        await rig.StartAsync();
        var oldest = rig.New(minute: 1);
        var newest = rig.New(minute: 3);
        var middle = rig.New(minute: 2);
        foreach (var c in new[] { oldest, newest, middle })
        {
            rig.Meta(c);
            rig.Photo(c);
        }

        await rig.SendAsync(2);
        (await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.MediaBackfillDone))).Should().BeNull("還沒協商出 media1");
        // 舊版發布的對話：沒有參照
        foreach (var c in new[] { oldest, newest, middle })
            await rig.H.Inspect(v => v.Store!.RemoveMediaRefs(c.Id));
        var sentBefore = rig.SentItems().Count;

        rig.H.Server.Features = WithMedia;
        await rig.ReconnectAsync();
        for (var i = 0; i < 6; i++)
            await rig.SendAsync(2);

        var resent = rig.SentItems().Skip(sentBefore).ToList();
        resent.Select(e => e.ConversationId).Should().Equal([newest.Id, middle.Id, oldest.Id], "依最後活動時間由新到舊");
        foreach (var e in resent)
            AttachmentRef(Obj(e)).Should().NotBeNull();
        var (done, key) = await rig.H.Inspect(v => (v.Store!.GetKv(SyncStoreSchema.MediaBackfillDone), v.Store.PublishServerKey()));
        done.Should().Be(key);
        (await rig.H.Inspect(v => v.Store!.LoadNeedsRedo())).Should().BeEmpty("重整完清掉 needs_redo");
    }

    // ───────────────────────────── 刪除（契約:700） ─────────────────────────────

    [Fact]
    public async Task 項目移除刪列()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var removed = rig.Photo(c, "a.png");
        var kept = rig.Photo(c, "b.png");
        await rig.SendAsync(2);
        (await rig.RefsAsync(c)).Should().HaveCount(2);

        c.Items.Remove(removed);
        rig.Ui.Put(new ItemRemovedChange(c.Id, removed.Id));
        await rig.IdleAsync();

        (await rig.RefsAsync(c)).Select(r => r.ItemId).Should().Equal(kept.Id);
    }

    [Fact]
    public async Task localOnly刪這個對話的列()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var leaving = rig.New();
        var staying = rig.New();
        foreach (var c in new[] { leaving, staying })
        {
            rig.Meta(c);
            rig.Photo(c);
        }

        await rig.SendAsync(2);

        leaving.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Meta(leaving);
        await rig.IdleAsync();
        await rig.SendAsync(2);

        (await rig.RefsAsync(leaving)).Should().BeEmpty("送 conv.deleted 時依對話刪");
        (await rig.RefsAsync(staying)).Should().ContainSingle();
    }

    [Fact]
    public async Task metadata不刪列()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Photo(c);
        await rig.SendAsync(2);

        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync(2);

        (await rig.RefsAsync(c)).Should().ContainSingle("metadata 在請求時擋（D26），列留著，改回 full 時沿用");
    }

    // ───────────────────────────── 縮圖（D30） ─────────────────────────────

    [Fact]
    public async Task 縮圖_定稿的圖片項目PUT_E8_執行中的不傳()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Photo(c, write: true, status: AgentItemStatus.Running);
        rig.Scope.Answer = q => rig.Files.Snapshot(resolved: (item.Attachments[0].StoredPath, "photo.png"));
        await rig.IdleAsync();
        await rig.SendAsync(3);
        rig.ThumbPuts().Should().BeEmpty("執行中的不推");

        item.Status = AgentItemStatus.Done;
        rig.Changed(c, item);
        await rig.IdleAsync();
        await rig.SendAsync(3);

        var row = (await rig.RefsAsync(c)).Single();
        var put = rig.ThumbPuts().Should().ContainSingle().Subject;
        put.Path.Should().Be("/api/v1/device/thumbs/" + row.Ref);
        put.Header(MediaRelayHeaders.Conversation).Should().Be(c.Id);
        put.Header("Content-Type").Should().Be("image/png");
        put.Authorization.Should().Be("Bearer cha_test");
        put.Body.Should().Equal(MediaTestFiles.Png);

        // 這次啟動上傳過：同一筆再投影不重傳
        item.Text = "又改了";
        rig.Changed(c, item);
        await rig.IdleAsync();
        await rig.SendAsync(3);
        rig.ThumbPuts().Should().ContainSingle();
    }

    [Fact]
    public async Task 縮圖_outbox有一般事件時不排()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync(2);
        rig.H.Server.HoldAcks = true;
        var item = rig.Photo(c, write: true);
        rig.Scope.Answer = q => rig.Files.Snapshot(resolved: (item.Attachments[0].StoredPath, "photo.png"));
        await rig.IdleAsync();
        await rig.SendAsync(5);

        rig.ThumbPuts().Should().BeEmpty("conv.item 還沒確認：一般事件優先（F43）");
        (await rig.H.Inspect(v => v.QueuedThumbnails)).Should().Be(1);

        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        await rig.IdleAsync();
        await rig.SendAsync(3);

        rig.ThumbPuts().Should().ContainSingle();
    }

    [Fact]
    public async Task 縮圖_E10回3筆依新到舊()
    {
        await using var rig = new Rig();
        var rows = new List<LocalMediaRefRow>();
        using (var store = SyncStore.Open(rig.H.Paths.DatabaseFile, rig.H.DeviceId))
        {
            for (var i = 1; i <= 3; i++)
                rows.Add(new LocalMediaRefRow(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, $"item-{i}", $"att-{i}", null, 1791600000000 + i));
            store.AddMediaRefs(rows);
        }

        var paths = rows.ToDictionary(r => r.AttachmentId!, r => rig.Files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", r.AttachmentId + ".png"), MediaTestFiles.Png));
        rig.Scope.Answer = q => rig.Files.Snapshot(resolved: (paths[q.AttachmentId!], q.AttachmentId + ".png"));
        // 伺服器的順序（由新到舊）；另一筆的項目對不上（伺服器的資料比較舊）略過
        rig.Missing.AddRange([.. new[] { rows[2], rows[0], rows[1] }.Select(r => new MissingThumb(r.Ref, r.ConversationId, r.ItemId)),
            new MissingThumb(rows[0].Ref, MediaTestFiles.ConversationId, "item-9")]);

        await rig.StartAsync();
        await rig.SendAsync(3);

        rig.Relay.Requests.Should().Contain(r => r.Method == HttpMethod.Get && r.Path == "/api/v1/device/thumbs/missing" && r.Query == "?limit=200");
        rig.ThumbPuts().Select(p => p.Path).Should().Equal(new[] { rows[2], rows[0], rows[1] }.Select(r => "/api/v1/device/thumbs/" + r.Ref));
    }

    /// <summary>
    /// 2026-10-27 的記錄：E10 每次連線都列出原檔已被刪掉的參照，桌面每次都問中樞、過守門員、被拒（not_found）一次，一天 54 次、大多在重開之後。
    /// 被拒的記進 sync.db 的 kv（thumb_gone），重連與重開之後 E10 再列出來也不再試；原檔還在的照常上傳
    /// </summary>
    [Fact]
    public async Task 縮圖_原檔已經不在_記進kv_重連與重開之後E10再列出也不再試()
    {
        await using var rig = new Rig();
        var present = new LocalMediaRefRow(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, "item-1", "att-1", null, 1791600000001);
        var deleted = new LocalMediaRefRow(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, "item-2", "att-2", null, 1791600000002);
        using (var store = SyncStore.Open(rig.H.Paths.DatabaseFile, rig.H.DeviceId))
            store.AddMediaRefs([present, deleted]);
        var presentPath = rig.Files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "att-1.png"), MediaTestFiles.Png);
        var deletedPath = Path.Combine(Path.GetDirectoryName(presentPath)!, "att-2.png");
        var asked = 0;
        rig.Scope.Answer = q =>
        {
            if (q.AttachmentId == "att-2")
                Interlocked.Increment(ref asked);
            return rig.Files.Snapshot(resolved: q.AttachmentId == "att-1" ? (presentPath, "att-1.png") : (deletedPath, "att-2.png"));
        };
        rig.Missing.AddRange([new MissingThumb(deleted.Ref, deleted.ConversationId, deleted.ItemId), new MissingThumb(present.Ref, present.ConversationId, present.ItemId)]);

        await rig.StartAsync();
        await rig.SendAsync(3);

        rig.ThumbPuts().Select(p => p.Path).Should().Equal("/api/v1/device/thumbs/" + present.Ref);
        asked.Should().Be(1, "原檔不在的試了一次");
        var kv = await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.ThumbGone));
        ThumbnailPublishRules.ParseGone(kv).Keys.Should().Equal(deleted.Ref);

        // 伺服器照舊列著（它沒有縮圖）：重連之後不再試，原檔還在的照常（以伺服器的缺漏清單為準，DI-1）
        await rig.ReconnectAsync();
        await rig.SendAsync(3);
        asked.Should().Be(1);
        rig.ThumbPuts().Should().HaveCount(2).And.OnlyContain(p => p.Path == "/api/v1/device/thumbs/" + present.Ref);

        // app 換版重開：記在 sync.db，照樣不再試
        await rig.RestartAsync();
        await rig.SendAsync(3);
        asked.Should().Be(1);
        rig.ThumbPuts().Should().HaveCount(3);
    }

    /// <summary>一則定稿的圖片訊息上傳過一次縮圖；回對話與它的參照列</summary>
    private static async Task<(FakeConversation Conversation, LocalMediaRefRow Row)> UploadedOnceAsync(Rig rig)
    {
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Photo(c, write: true);
        rig.Scope.Answer = q => rig.Files.Snapshot(resolved: (item.Attachments[0].StoredPath, "photo.png"));
        await rig.IdleAsync();
        await rig.SendAsync(3);
        var row = (await rig.RefsAsync(c)).Single();
        rig.ThumbPuts().Should().ContainSingle();
        return (c, row);
    }

    [Fact]
    public async Task 縮圖_這次上傳過的E10再列出時再傳一次()
    {
        // 合併前審查 DI-1：伺服器還原（或降級再改回 full）之後 thumb_blob 沒了，E10 列出它；桌面不重開也要補回去
        await using var rig = new Rig();
        var (_, row) = await UploadedOnceAsync(rig);

        rig.Missing.Add(new MissingThumb(row.Ref, row.ConversationId, row.ItemId));
        await rig.ReconnectAsync();
        await rig.SendAsync(3);

        rig.ThumbPuts().Should().HaveCount(2).And.OnlyContain(p => p.Path == "/api/v1/device/thumbs/" + row.Ref, "以伺服器的缺漏清單為準");
    }

    [Fact]
    public async Task 縮圖_改成metadata再改回full_定稿的再傳一次()
    {
        // 合併前審查 DI-1：伺服器降級時刪了這個對話的縮圖；改回 full 重新投影時，這次啟動上傳過的也要再傳
        await using var rig = new Rig();
        var (c, row) = await UploadedOnceAsync(rig);

        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync(2);
        rig.ThumbPuts().Should().ContainSingle("metadata 的期間不傳");

        c.Meta.SyncPolicy = ConversationSyncPolicies.Full;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync(3);
        await rig.SendAsync(3);

        rig.ThumbPuts().Should().HaveCount(2).And.OnlyContain(p => p.Path == "/api/v1/device/thumbs/" + row.Ref);
    }

    [Fact]
    public async Task 縮圖_伺服器換實例_這次上傳過的重新發布時再傳一次()
    {
        // 合併前審查 DI-1：伺服器從備份還原（換實例），thumb_blob 可能沒了；E10 還沒列出之前，重新發布對話時就要補回去
        await using var rig = new Rig();
        var (_, row) = await UploadedOnceAsync(rig);

        rig.H.Server.Restore(nextDeviceSeqAtBackup: 1, highAtBackup: 0);
        rig.H.Server.Current!.Drop();
        await rig.IdleAsync();
        await rig.SendAsync(3);
        await rig.SendAsync(3);

        (await rig.H.StateAsync()).Should().Be(SyncEngineState.Live);
        rig.ThumbPuts().Should().HaveCount(2).And.OnlyContain(p => p.Path == "/api/v1/device/thumbs/" + row.Ref, "E10 沒有列出（Missing 是空的）");
    }

    [Fact]
    public async Task 縮圖_沒有media1不上傳也不拉()
    {
        await using var rig = new Rig(media: false);
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Photo(c, write: true);
        rig.Scope.Answer = q => rig.Files.Snapshot(resolved: (item.Attachments[0].StoredPath, "photo.png"));
        await rig.IdleAsync();
        await rig.SendAsync(5);

        rig.Relay.Requests.Should().BeEmpty("伺服器沒有 media1：E8、E10 都不呼叫");
        (await rig.RefsAsync(c)).Should().ContainSingle("參照照樣配（payload 裡的 ref 對沒有 media1 的伺服器無害）");
    }
}
