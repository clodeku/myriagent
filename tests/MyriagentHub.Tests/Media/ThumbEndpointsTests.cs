using System.Net;
using System.Text.Json;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 擁有者電腦的縮圖端點（WP4.7 計畫步 5；E8 上傳並綁到 <c>media_refs.thumb_blob</c>、E10 缺縮圖的清單）
/// </summary>
public sealed class ThumbEndpointsTests
{
    private sealed class Scene : IAsyncDisposable
    {
        private Scene(MediaHost host, TestDevice device, string token, string conversation)
        {
            Host = host;
            Device = device;
            Token = token;
            Conversation = conversation;
        }

        public MediaHost Host { get; }

        public TestDevice Device { get; }

        public string Token { get; }

        public string Conversation { get; }

        public static async Task<Scene> StartAsync(IDictionary<string, string?>? settings = null)
        {
            var host = await MediaHost.StartAsync(settings);
            var (device, token) = await host.DeviceAsync();
            return new Scene(host, device, token, await MediaSeed.ConversationAsync(host, device.Id));
        }

        /// <summary>這個對話的一筆項目帶一個附件參照（等索引建好列）</summary>
        public async Task<string> ItemRefAsync(string itemId = "item1")
        {
            var mediaRef = MediaSeed.NewRef();
            await MediaSeed.ItemAsync(Host, Device.Id, Conversation, itemId, [mediaRef]);
            await MediaSeed.IndexedAsync(Host);
            return mediaRef;
        }

        public Task<HttpResponseMessage> PutAsync(string mediaRef, byte[] body, string? conversation = "", string? token = null) =>
            MediaSeed.PutThumbAsync(Host, token ?? Token, mediaRef, conversation == string.Empty ? Conversation : conversation, MediaHost.Bytes(body));

        public async Task<JsonElement> MissingAsync(string query = "", HttpStatusCode expected = HttpStatusCode.OK, string? token = null)
        {
            using var client = Host.Client();
            using var request = MediaHost.Device(HttpMethod.Get, "/api/v1/device/thumbs/missing" + query, token ?? Token);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(expected);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            Device.Dispose();
            await Host.DisposeAsync();
        }
    }

    [Fact]
    public async Task E8_成功後綁到參照_E2不經轉送回200()
    {
        await using var scene = await Scene.StartAsync();
        var mediaRef = await scene.ItemRefAsync();
        var png = MediaBytes.Png(900);

        using var put = await scene.PutAsync(mediaRef, png);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("sha256").GetString().Should().Be(MediaBytes.Sha(png));
        body.GetProperty("length").GetInt64().Should().Be(png.Length);
        var key = MediaKeyRules.Format(scene.Device.Id, mediaRef);
        MediaSeed.Rows(scene.Host, scene.Conversation).Single().Thumb.Should().Be(MediaBytes.Sha(png));
        MediaSeed.BlobRefs(scene.Host, BlobRefKinds.Thumb).Should().Equal((MediaBytes.Sha(png), key));
        MediaSeed.BlobColumn(scene.Host, MediaBytes.Sha(png), "size").Should().Be(png.Length);

        // 來源電腦根本沒連上裝置 hub：縮圖照樣看得到（取自 thumb_blob，不轉送）
        using var owner = await scene.Host.OwnerAsync();
        using var thumb = await owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320"));
        thumb.StatusCode.Should().Be(HttpStatusCode.OK);
        thumb.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await thumb.Content.ReadAsByteArrayAsync()).Should().Equal(png);
    }

    [Fact]
    public async Task E8_換縮圖時舊縮圖的引用移除_轉送的快取刪掉()
    {
        await using var scene = await Scene.StartAsync();
        var mediaRef = await scene.ItemRefAsync();
        var key = MediaKeyRules.Format(scene.Device.Id, mediaRef);
        var first = MediaBytes.Png(700, seed: 1);
        var second = MediaBytes.Jpeg(800, seed: 2);
        using (var put = await scene.PutAsync(mediaRef, first))
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        var cache = scene.Host.Services.GetRequiredService<ThumbCache>();
        (await cache.WriteAsync(key, null, MediaBytes.Png(300, seed: 3), CancellationToken.None)).Should().BeTrue();

        scene.Host.Host.Clock.Advance(TimeSpan.FromMinutes(7));
        using (var put = await scene.PutAsync(mediaRef, second))
            put.StatusCode.Should().Be(HttpStatusCode.OK, "JPEG 也收");

        MediaSeed.Rows(scene.Host, scene.Conversation).Single().Thumb.Should().Be(MediaBytes.Sha(second));
        MediaSeed.BlobRefs(scene.Host, BlobRefKinds.Thumb).Should().Equal((MediaBytes.Sha(second), key));
        MediaSeed.BlobColumn(scene.Host, MediaBytes.Sha(first), "last_ref_at").Should().Be(scene.Host.Host.Clock.NowMs);
        cache.TryRead(key, null).Should().BeNull("綁了 blob 之後轉送得到的快取用不到了");
    }

    [Fact]
    public async Task E8_別台裝置404()
    {
        await using var scene = await Scene.StartAsync();
        var mediaRef = await scene.ItemRefAsync();
        var (other, otherToken) = await scene.Host.DeviceAsync();
        using var _o = other;

        using var put = await scene.PutAsync(mediaRef, MediaBytes.Png(500), token: otherToken);

        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await MediaHost.ErrorAsync(put)).Should().Be(MediaApiErrors.NotFound);
        MediaSeed.Rows(scene.Host, scene.Conversation).Single().Thumb.Should().BeNull();
        MediaSeed.BlobRefs(scene.Host, BlobRefKinds.Thumb).Should().BeEmpty();
    }

    [Fact]
    public async Task E8_政策metadata回409()
    {
        await using var scene = await Scene.StartAsync();
        var mediaRef = await scene.ItemRefAsync();
        await MediaSeed.SetPolicyAsync(scene.Host, scene.Device.Id, scene.Conversation, ConversationSyncPolicies.Metadata);

        using var put = await scene.PutAsync(mediaRef, MediaBytes.Png(500));

        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await MediaHost.ErrorAsync(put)).Should().Be(MediaApiErrors.Policy);
        scene.Host.Services.GetRequiredService<BlobStore>().Count.Should().Be(0, "被拒的什麼都不留");
    }

    [Fact]
    public async Task E8_PNG與JPEG以外的魔數回415_什麼都不留()
    {
        await using var scene = await Scene.StartAsync();
        var mediaRef = await scene.ItemRefAsync();

        using var put = await scene.PutAsync(mediaRef, MediaBytes.Random(600));

        put.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await MediaHost.ErrorAsync(put)).Should().Be(MediaApiErrors.UnsupportedType);
        scene.Host.Services.GetRequiredService<BlobStore>().Count.Should().Be(0);
        MediaSeed.Rows(scene.Host, scene.Conversation).Single().Thumb.Should().BeNull();
    }

    [Fact]
    public async Task E8_參照不在_不是這個對話_不是項目參照都回404()
    {
        await using var scene = await Scene.StartAsync();
        var mediaRef = await scene.ItemRefAsync();
        var other = await MediaSeed.ConversationAsync(scene.Host, scene.Device.Id);
        var listRef = MediaSeed.NewRef();
        await MediaSeed.RefAsync(scene.Host, scene.Device.Id, scene.Conversation, listRef, MediaRefPurposes.List);

        using (var unknown = await scene.PutAsync(MediaSeed.NewRef(), MediaBytes.Png(500)))
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var wrongConversation = await scene.PutAsync(mediaRef, MediaBytes.Png(500), conversation: other))
            wrongConversation.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var drawer = await scene.PutAsync(listRef, MediaBytes.Png(500)))
            drawer.StatusCode.Should().Be(HttpStatusCode.NotFound, "抽屜的縮圖一律按需轉送（D30）");
        using (var badRef = await scene.PutAsync("not-a-ref", MediaBytes.Png(500)))
            badRef.StatusCode.Should().Be(HttpStatusCode.NotFound);
        scene.Host.Services.GetRequiredService<BlobStore>().Count.Should().Be(0);
    }

    [Fact]
    public async Task E8_對話標頭沒帶或不合格400_沒有長度411_超過上限413()
    {
        await using var scene = await Scene.StartAsync(new Dictionary<string, string?> { ["Hub:Media:MaxThumbBytes"] = "4096" });
        var mediaRef = await scene.ItemRefAsync();

        using (var none = await scene.PutAsync(mediaRef, MediaBytes.Png(500), conversation: null))
            none.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using (var bad = await scene.PutAsync(mediaRef, MediaBytes.Png(500), conversation: scene.Conversation.ToUpperInvariant()))
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using (var chunked = await MediaSeed.PutThumbAsync(scene.Host, scene.Token, mediaRef, scene.Conversation, new StreamContent(new ScriptedStream(MediaBytes.Png(500)))))
            chunked.StatusCode.Should().Be(HttpStatusCode.LengthRequired);
        using (var large = await scene.PutAsync(mediaRef, MediaBytes.Png(4097)))
        {
            large.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            (await MediaHost.ErrorAsync(large)).Should().Be(MediaApiErrors.TooLarge);
        }

        using (var exact = await scene.PutAsync(mediaRef, MediaBytes.Png(4096)))
            exact.StatusCode.Should().Be(HttpStatusCode.OK, "正好上限可以");
    }

    [Fact]
    public async Task E8_E10只收存取權杖()
    {
        await using var scene = await Scene.StartAsync();
        var mediaRef = await scene.ItemRefAsync();
        using var owner = await scene.Host.OwnerAsync();
        using var put = MediaHost.Owner(HttpMethod.Put, "/api/v1/device/thumbs/" + mediaRef, MediaHost.Bytes(MediaBytes.Png(500)));
        put.Headers.TryAddWithoutValidation(MediaRelayHeaders.Conversation, scene.Conversation).Should().BeTrue();
        using (var response = await owner.SendAsync(put))
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using (var response = await owner.GetAsync("/api/v1/device/thumbs/missing"))
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task E10_只回這台的item列_由新到舊_before分頁()
    {
        await using var scene = await Scene.StartAsync();
        var r1 = await scene.ItemRefAsync("i1");
        scene.Host.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var r2 = await scene.ItemRefAsync("i2");
        scene.Host.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var r3 = await scene.ItemRefAsync("i3");
        scene.Host.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        var withThumb = await scene.ItemRefAsync("i4");
        using (var put = await scene.PutAsync(withThumb, MediaBytes.Png(500)))
            put.StatusCode.Should().Be(HttpStatusCode.OK);
        await MediaSeed.RefAsync(scene.Host, scene.Device.Id, scene.Conversation, MediaSeed.NewRef(), MediaRefPurposes.List);
        await MediaSeed.RefAsync(scene.Host, scene.Device.Id, scene.Conversation, MediaSeed.NewRef(), MediaRefPurposes.File);
        var (other, otherToken) = await scene.Host.DeviceAsync();
        using var _o = other;
        var otherConversation = await MediaSeed.ConversationAsync(scene.Host, other.Id);
        var otherRef = MediaSeed.NewRef();
        await MediaSeed.ItemAsync(scene.Host, other.Id, otherConversation, "x1", [otherRef]);
        await MediaSeed.IndexedAsync(scene.Host);

        var all = await scene.MissingAsync();
        Refs(all).Should().Equal(r3, r2, r1);
        all.GetProperty("refs")[0].GetProperty("conversationId").GetString().Should().Be(scene.Conversation);
        all.GetProperty("refs")[0].GetProperty("itemId").GetString().Should().Be("i3");
        all.GetProperty("next").ValueKind.Should().Be(JsonValueKind.Null);

        var page1 = await scene.MissingAsync("?limit=2");
        Refs(page1).Should().Equal(r3, r2);
        var next = page1.GetProperty("next").GetString()!;
        var page2 = await scene.MissingAsync("?limit=2&before=" + Uri.EscapeDataString(next));
        Refs(page2).Should().Equal(r1);
        page2.GetProperty("next").ValueKind.Should().Be(JsonValueKind.Null);

        Refs(await scene.MissingAsync(token: otherToken)).Should().Equal(otherRef);
    }

    [Fact]
    public async Task E10_降級或刪除的對話不列_before不合格400()
    {
        await using var scene = await Scene.StartAsync();
        var kept = await scene.ItemRefAsync("i1");
        var downgraded = await MediaSeed.ConversationAsync(scene.Host, scene.Device.Id);
        await MediaSeed.ItemAsync(scene.Host, scene.Device.Id, downgraded, "i1", [MediaSeed.NewRef()]);
        await MediaSeed.IndexedAsync(scene.Host);
        await MediaSeed.SetPolicyAsync(scene.Host, scene.Device.Id, downgraded, ConversationSyncPolicies.Paused);
        await MediaSeed.IndexedAsync(scene.Host);

        Refs(await scene.MissingAsync()).Should().Equal(kept);
        (await scene.MissingAsync("?before=abc", HttpStatusCode.BadRequest)).GetProperty("error").GetString().Should().Be(MediaApiErrors.Invalid);
        await scene.MissingAsync("?before=1_" + kept + "&before=2_" + kept, HttpStatusCode.BadRequest);
    }

    private static List<string> Refs(JsonElement page) => page.GetProperty("refs").EnumerateArray().Select(r => r.GetProperty("ref").GetString()!).ToList();
}
