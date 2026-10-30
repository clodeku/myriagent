using System.Net;
using System.Text.Json;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Pairing;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Media;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Trust;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationEventLogTests;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Pairing;

/// <summary>
/// 移除已撤銷的電腦（設計 14.22）：端點要近期驗證、只收已撤銷的；名冊與電腦頁不再列它；它擁有的對話在伺服器上刪除（Pull 連 meta 類也不回、
/// 重建不讓它復活、媒體參照對帳刪掉）；hub.db 提交之後沒做完的由重送或下次啟動補做
/// </summary>
public sealed class DeviceRemovalTests
{
    // ───── 端點 ─────

    [Fact]
    public async Task 移除要近期驗證_只收已撤銷的_重送回409()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        using var owner = await LoginAsync(host);

        await ExpectAsync(await owner.PostAsync(Remove(device.Id), new { }), HttpStatusCode.Conflict, "not_revoked");
        using (var revoke = await owner.PostAsync(Revoke(device.Id), new { reason = "retired" }))
            revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        host.Clock.Advance(TimeSpan.FromMinutes(6));
        await ExpectAsync(await owner.PostAsync(Remove(device.Id), new { }), HttpStatusCode.Forbidden, "step_up_required");
        Directory(host).Get(device.Id)!.Removed.Should().BeFalse("近期驗證不夠新時什麼都不做");

        using (var stepUp = await OwnerFlows.StepUpAsync(owner))
            stepUp.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var ok = await owner.PostAsync(Remove(device.Id), new { }))
        {
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await Json(ok);
            body.GetProperty("ok").GetBoolean().Should().BeTrue();
            body.GetProperty("conversations").GetInt32().Should().Be(0);
        }
        var info = Directory(host).Get(device.Id)!;
        (info.Removed, info.Revoked).Should().Be((true, true), "查單台照樣查得到，已撤銷的判斷不變");
        Directory(host).Listed.Should().NotContain(d => d.Id == device.Id);
        Directory(host).All.Should().Contain(d => d.Id == device.Id);
        var audit = (await AuditAsync(host, AuditActions.DeviceRemoved)).Should().ContainSingle().Subject;
        (audit.Target, audit.ActorType, audit.Outcome).Should().Be((device.Id, AuditActors.Owner, AuditOutcomes.Ok));

        await ExpectAsync(await owner.PostAsync(Remove(device.Id), new { }), HttpStatusCode.Conflict, "already_removed");
        (await AuditAsync(host, AuditActions.DeviceRemoved)).Should().ContainSingle("重送不再寫稽核");
        await ExpectAsync(await owner.PostAsync(Remove(SyncIds.NewDeviceId()), new { }), HttpStatusCode.NotFound, "not_found");
        await ExpectAsync(await owner.PostAsync(Remove("not-a-device"), new { }), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task 沒有本文也走得到端點()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await Pairing(host).RevokeAsync(device.Id, "retired", null, null);
        using var owner = await LoginAsync(host);

        using var request = new HttpRequestMessage(HttpMethod.Post, Remove(device.Id));
        request.Headers.Add("X-Myriagent-Csrf", "1");
        request.Headers.Add("Origin", HubTestHost.Origin);
        using var response = await owner.Http.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "移除沒有選項：不帶本文也要比對得到端點");
        Directory(host).Get(device.Id)!.Removed.Should().BeTrue();
    }

    [Fact]
    public async Task 移除後名冊不列它_版本前進_瀏覽器的電腦頁也不列()
    {
        await using var host = await HubTestHost.StartAsync();
        var devices = await AddDevicesInOrderAsync(host, "甲", "乙");
        try
        {
            await using var owner = await LoginAndConnectAsync(host);
            (await owner.Hub.ReadyAsync())["remote"]!["devices"]!.Select(d => d["name"]!.Value<string>()).Should().Equal(["甲", "乙"]);
            using (var revoke = await owner.Http.PostAsync(Revoke(devices[0].Id), new { reason = "compromised" }))
                revoke.StatusCode.Should().Be(HttpStatusCode.OK);
            await owner.Hub.WaitForAsync("devices", m => m["devices"]!.Any(d => d["revoked"] is JObject));
            var trust = host.Services.GetRequiredService<TrustService>();
            var before = await trust.GetBundleAsync(-1);
            before.Registry!.Should().Contain(e => e.DeviceId == devices[0].Id && e.RevokedAt != null, "撤銷的照樣在名冊（撤銷由名冊發布）");
            var upserts = EventLogTests.ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Count;

            using (var remove = await owner.Http.PostAsync(Remove(devices[0].Id), new { }))
                remove.StatusCode.Should().Be(HttpStatusCode.OK);

            var after = await trust.GetBundleAsync(before.RegistryRev);
            after.RegistryRev.Should().Be(before.RegistryRev + 1);
            after.Registry!.Select(e => e.DeviceId).Should().Equal([devices[1].Id], "已移除的不在名冊：桌面據此不再列它（已撤銷而且不在名冊）");
            var pushed = await owner.Hub.WaitForAsync("devices", m => m["devices"]!.Count() == 1);
            pushed["devices"]!.Single()["name"]!.Value<string>().Should().Be("乙");
            EventLogTests.ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Should().HaveCount(upserts + 1,
                "重發它的 device.upsert：瀏覽器與各台在 catalog 類的事件之後重讀");
        }
        finally
        {
            foreach (var device in devices)
                device.Dispose();
        }
    }

    [Fact]
    public async Task 寫入者停止時移除回503_hub_db那一半已生效且有稽核()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await Pairing(host).RevokeAsync(device.Id, "retired", null, null);
        using var owner = await LoginAsync(host);
        host.Services.GetRequiredService<SyncWriter>().Stop();

        await ExpectAsync(await owner.PostAsync(Remove(device.Id), new { }), HttpStatusCode.ServiceUnavailable, "unavailable");
        Directory(host).Get(device.Id)!.Removed.Should().BeTrue("hub.db 那一半已提交（對話的刪除由重送或下次啟動補做）");
        (await AuditAsync(host, AuditActions.DeviceRemoved)).Should().ContainSingle();
    }

    // ───── 對話 ─────

    [Fact]
    public async Task 移除刪掉它擁有的對話_別台的不動_Pull連meta類也不回()
    {
        // 不掛閘道：變更通知由這個測試自己取（閘道是唯一的消費者）
        await using var host = await HubTestHost.StartAsync(withoutConversationGateway: true);
        var a = await EventLogTests.AddDeviceAsync(host, "要移除的");
        var b = await EventLogTests.AddDeviceAsync(host, "留著的");
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Queue, Queue("甲")));
        await Append(host, b, Ev(1, K.Created, Meta(), conversationId: OtherConv), Ev(2, K.Item, Item(), conversationId: OtherConv));
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c =>
            c.Execute("INSERT INTO conversation_unread(conversation_id, since_seq, since_at) VALUES($c, 1, 1)", ("$c", Conv)));
        var log = host.Services.GetRequiredService<EventLog>();
        log.Pull(Cls(SyncClasses.Meta)).Events.Should().Contain(e => e.ConversationId == Conv, "正向對照：移除前拉得到");
        var feed = host.Services.GetRequiredService<ConversationChangeFeed>();
        feed.TryDrain([], out _);

        await Pairing(host).RevokeAsync(a, "retired", null, null);
        (await Pairing(host).RemoveAsync(a, null, null)).Should().Be(1);

        Rows(host, $"SELECT deleted_by, deleted_at IS NOT NULL, item_count FROM conversations WHERE conversation_id = '{Conv}'").Single()
            .Should().Equal(SyncEventStore.ServerAuthor, 1L, 0L);
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM items WHERE conversation_id = '{Conv}'").Should().Be(0);
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM conversation_runtime WHERE conversation_id = '{Conv}'").Should().Be(0);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM conversation_unread").Should().Be(0);
        Rows(host, $"SELECT deleted_at, item_count FROM conversations WHERE conversation_id = '{OtherConv}'").Single().Should().Equal(null, 1L);

        foreach (var request in new[] { Cls(SyncClasses.Meta), Cls(SyncClasses.Item), Stream(Conv) })
            log.Pull(request).Events.Should().NotContain(e => e.ConversationId == Conv, "伺服器刪的不回任何事件（沒有 conv.deleted 可以通知副本）");
        log.Pull(Cls(SyncClasses.Meta)).Events.Should().Contain(e => e.ConversationId == OtherConv);
        log.Pull(Stream(OtherConv)).Events.Select(e => e.Kind).Should().Equal(K.Created, K.Item);

        var changes = new List<ConversationChange>();
        feed.TryDrain(changes, out _);
        changes.Should().ContainSingle(c => c.ConversationId == Conv && c.Kind == ConversationChangeKind.Deleted && c.Author == SyncEventStore.ServerAuthor);
        host.Services.GetRequiredService<ConversationDirectoryCache>().TryGet(Conv, out var entry).Should().BeTrue();
        entry!.Deleted.Should().BeTrue();

        // 被移除的電腦之後的事件本來就不收（已撤銷）；重送移除不會再刪或再發事件
        var events = EventLogTests.Count(host, "SELECT COUNT(*) FROM events");
        var thrown = await FluentActions.Awaiting(() => Pairing(host).RemoveAsync(a, null, null)).Should().ThrowAsync<PairingException>();
        thrown.Which.Code.Should().Be("already_removed");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM events").Should().Be(events, "沒有補做的就不發 device.upsert");
    }

    [Theory]
    [InlineData(SyncClasses.Meta, ConversationSyncPolicies.Full)]
    [InlineData(SyncClasses.Item, ConversationSyncPolicies.Full)]
    [InlineData(SyncClasses.Meta, ConversationSyncPolicies.Metadata)]
    public void Hidden_伺服器刪的對話不分類別都不回(string cls, string policy)
    {
        ConversationPullRules.Hidden(cls, policy, deleted: true, deletedByServer: true).Should().BeTrue();
        ConversationPullRules.Hidden(SyncClasses.Meta, policy, deleted: true).Should().BeFalse("擁有者刪的照舊回 meta 類（副本才知道它被刪）");
    }

    [Fact]
    public async Task 物化表重建不讓伺服器刪的對話復活()
    {
        await using var host = await HubTestHost.StartAsync(withoutConversationGateway: true);
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()));
        await Append(host, b, Ev(1, K.Created, Meta(), conversationId: OtherConv), Ev(2, K.Item, Item(), conversationId: OtherConv));
        await Pairing(host).RevokeAsync(a, "retired", null, null);
        await Pairing(host).RemoveAsync(a, null, null);
        var deletedAt = Rows(host, $"SELECT deleted_at FROM conversations WHERE conversation_id = '{Conv}'").Single()[0];

        await host.Services.GetRequiredService<ConversationRebuild>().RebuildAsync("測試", CancellationToken.None);

        Rows(host, $"SELECT deleted_by, deleted_at, item_count FROM conversations WHERE conversation_id = '{Conv}'").Single()
            .Should().Equal(SyncEventStore.ServerAuthor, deletedAt, 0L);
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM items WHERE conversation_id = '{Conv}'").Should().Be(0, "不從事件重播回來");
        // 別台的照常重建
        Rows(host, $"SELECT deleted_at, item_count FROM conversations WHERE conversation_id = '{OtherConv}'").Single().Should().Equal(null, 1L);
        host.Services.GetRequiredService<ConversationDirectoryCache>().IsRebuilding(Conv).Should().BeFalse("重建的標記清掉了");
    }

    [Fact]
    public async Task 重新啟動時補做沒做完的對話刪除()
    {
        var dir = TempDir.Create();
        try
        {
            string a;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                a = await EventLogTests.AddDeviceAsync(host);
                await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()));
                await Pairing(host).RevokeAsync(a, "retired", null, null);
                // hub.db 已標移除、sync.db 那一半沒做（程序在中間結束）
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
                (await db.Devices.SingleAsync(d => d.Id == a)).RemovedAt = host.Clock.NowMs;
                await db.SaveChangesAsync();
            }

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                Rows(host, $"SELECT deleted_by, item_count FROM conversations WHERE conversation_id = '{Conv}'").Single()
                    .Should().Equal(SyncEventStore.ServerAuthor, 0L);
                host.Services.GetRequiredService<EventLog>().Pull(Cls(SyncClasses.Meta)).Events.Should().NotContain(e => e.ConversationId == Conv);
                Directory(host).Listed.Should().NotContain(d => d.Id == a);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    // ───── 媒體 ─────

    [Fact]
    public async Task 移除後它的媒體參照與項目的blob引用由索引對帳刪掉()
    {
        await using var host = await MediaHost.StartAsync();
        var (removed, _) = await host.DeviceAsync();
        using var _r = removed;
        var (kept, _) = await host.DeviceAsync();
        using var _k = kept;
        var gone = await MediaSeed.ConversationAsync(host, removed.Id);
        var stays = await MediaSeed.ConversationAsync(host, kept.Id);
        var sha = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(300), BlobKinds.ItemBody);
        await MediaSeed.ItemAsync(host, removed.Id, gone, "item1", [MediaSeed.NewRef()], [MediaSeed.NewRef()],
            edit: item => item["text"] = new JObject { ["blob"] = sha, ["length"] = 300 });
        await MediaSeed.ItemAsync(host, kept.Id, stays, "item1", [MediaSeed.NewRef()]);
        await MediaSeed.IndexedAsync(host);
        MediaSeed.Rows(host, gone).Should().HaveCount(2);
        MediaSeed.BlobRefs(host, BlobRefKinds.Item).Should().Equal((sha, gone + ":item1"));
        var indexer = host.Services.GetRequiredService<MediaRefIndexer>();
        var reconciles = indexer.Reconciles;

        var pairing = host.Services.GetRequiredService<PairingService>();
        await pairing.RevokeAsync(removed.Id, "retired", null, null);
        (await pairing.RemoveAsync(removed.Id, null, null)).Should().Be(1);

        await MediaBrowser.EventuallyAsync(() => indexer.Reconciles > reconciles, "移除之後請索引對帳");
        MediaSeed.Rows(host, gone).Should().BeEmpty();
        MediaSeed.BlobRefs(host, BlobRefKinds.Item).Should().BeEmpty("blob 實體由每日維護依保留窗刪");
        MediaSeed.Rows(host, stays).Should().ContainSingle("別台的不動");
    }

    // ───── 工具 ─────

    private static string Revoke(string deviceId) => $"/api/v1/devices/{deviceId}/revoke";

    private static string Remove(string deviceId) => $"/api/v1/devices/{deviceId}/remove";

    private static PairingService Pairing(HubTestHost host) => host.Services.GetRequiredService<PairingService>();

    private static DeviceDirectory Directory(HubTestHost host) => host.Services.GetRequiredService<DeviceDirectory>();

    private static PullRequest Cls(string cls) => new([new PullCursor(0, Cls: cls)], 100, 1_000_000);

    private static PullRequest Stream(string conversationId) => new([new PullCursor(0, Stream: SyncStreams.Conversation(conversationId))], 100, 1_000_000);

    private static async Task<HubClient> LoginAsync(HubTestHost host)
    {
        var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(status, text);
            JsonDocument.Parse(text).RootElement.GetProperty("error").GetString().Should().Be(code);
        }
    }

    private static async Task<List<AuditRecord>> AuditAsync(HubTestHost host, string action)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.Audit.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Seq).ToListAsync();
    }
}
