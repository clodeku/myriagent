using System.Net;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Realtime;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 縮圖與離線退回（WP4.7 計畫步 4；D10、D29、D30）：E2 的順序是 <c>thumb_blob</c>（ETag、304）→ 快取 → 轉送；
/// <c>/m/</c> 的圖片在來源離線或拒絕時改回縮圖，影片照回錯誤
/// </summary>
public sealed class MediaThumbTests
{
    private sealed class Scene : IAsyncDisposable
    {
        private Scene(MediaHost host, FakeMediaDevice device, HttpClient owner, string conversation)
        {
            Host = host;
            Device = device;
            Owner = owner;
            Conversation = conversation;
        }

        public MediaHost Host { get; }

        public FakeMediaDevice Device { get; }

        public HttpClient Owner { get; }

        public string Conversation { get; }

        public static async Task<Scene> StartAsync(bool serve = true)
        {
            var host = await MediaHost.StartAsync();
            var device = await FakeMediaDevice.ConnectAsync(host);
            if (serve)
                device.Serve();
            var owner = await host.OwnerAsync();
            return new Scene(host, device, owner, await MediaSeed.ConversationAsync(host, device.Id));
        }

        /// <summary>伺服器上存一份縮圖 blob（同步驟 5 的 E8 會寫的）</summary>
        public async Task<string> StoreThumbAsync(byte[] png)
        {
            var sha = MediaBytes.Sha(png);
            using var content = new MemoryStream(png);
            await Host.Services.GetRequiredService<BlobStore>().PutAsync(sha, png.Length, BlobKinds.Thumb, "image/png", content, CancellationToken.None);
            return sha;
        }

        /// <summary>假電腦手上的原檔與縮圖（同一個參照：原檔回 media、縮圖回 thumb）</summary>
        public async Task<string> AddAsync(FakeMediaFile original, byte[]? thumb = null, string? thumbBlob = null, string purpose = "item:item1")
        {
            var mediaRef = MediaSeed.NewRef();
            Device.Files[mediaRef] = original;
            if (thumb is not null)
                Thumbs[mediaRef] = thumb;
            return await MediaSeed.RefAsync(Host, Device.Id, Conversation, mediaRef, purpose, thumbBlob);
        }

        public Dictionary<string, byte[]> Thumbs { get; } = new(StringComparer.Ordinal);

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Device.DisposeAsync();
            await Host.DisposeAsync();
        }

        /// <summary>自動回覆：thumb 回 <see cref="Thumbs"/>（200 整份），其他照檔案</summary>
        public void ServeWithThumbs(Func<MediaRequest, FakeReply?>? overrideReply = null) => Device.Serve(async request =>
        {
            var reply = overrideReply?.Invoke(request)
                ?? (request.Purpose == MediaPurposes.Thumb
                    ? FakeReply.For(Thumbs.TryGetValue(request.Ref, out var thumb) ? new FakeMediaFile(thumb, "image/png") : null, request)
                    : FakeReply.For(Device.Files.GetValueOrDefault(request.Ref), request));
            using var response = await Device.PutAsync(request.RelayId, reply);
            Device.Replies.Enqueue(response.StatusCode);
        });

        public async Task GoOfflineAsync()
        {
            await Device.Client.Connection.StopAsync();
            var registry = Host.Services.GetRequiredService<ConnectionRegistry>();
            await MediaBrowser.EventuallyAsync(() => !registry.IsOnline(Device.Id), "伺服器看到裝置離線");
        }
    }

    [Fact]
    public async Task E2_有thumb_blob時裝置離線也回200_ETag是SHA256_IfNoneMatch回304()
    {
        await using var scene = await Scene.StartAsync();
        var png = MediaBytes.Png(4000);
        var sha = await scene.StoreThumbAsync(png);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumbBlob: sha);
        await scene.GoOfflineAsync();

        using (var response = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=160")))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            MediaBrowser.Header(response, "Content-Type").Should().Be("image/png");
            MediaBrowser.Header(response, "ETag").Should().Be("\"" + sha + "\"");
            MediaBrowser.Header(response, "Cache-Control").Should().Be("private, max-age=300");
            MediaBrowser.Header(response, "Content-Security-Policy").Should().Be(MediaHeaderRules.MediaCsp);
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(png);
        }

        var conditional = MediaBrowser.Get("/t/" + key + "?s=320");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"" + sha + "\"");
        using var notModified = await scene.Owner.SendAsync(conditional);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        MediaBrowser.Header(notModified, "ETag").Should().Be("\"" + sha + "\"");
        scene.Device.Seen.Should().BeEmpty("有 thumb_blob 就不向電腦要");
    }

    [Fact]
    public async Task E2_轉送一次後從快取回200()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var png = MediaBytes.Png(3000);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumb: png);
        scene.ServeWithThumbs();

        using (var first = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320")))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            (await first.Content.ReadAsByteArrayAsync()).Should().Equal(png);
        }

        var request = scene.Device.Seen.Should().ContainSingle().Subject;
        request.Purpose.Should().Be(MediaPurposes.Thumb);
        request.Range.Should().BeNull("縮圖整份要");
        File.Exists(Path.Combine(scene.Host.Host.Paths.ThumbCacheDir, key)).Should().BeTrue();

        await scene.GoOfflineAsync();
        using var second = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320"));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsByteArrayAsync()).Should().Equal(png);
        scene.Device.Seen.Should().HaveCount(1, "第二次從快取");
    }

    [Fact]
    public async Task E2_轉送與快取的縮圖帶內容的ETag_IfNoneMatch回304()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var png = MediaBytes.Png(3000);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumb: png);
        scene.ServeWithThumbs();
        var etag = "\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)) + "\"";

        using (var first = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320")))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            MediaBrowser.Header(first, "ETag").Should().Be(etag, "轉送來的縮圖也帶內容的 ETag");
        }

        var conditional = MediaBrowser.Get("/t/" + key + "?s=320");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var notModified = await scene.Owner.SendAsync(conditional);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified, "快取裡的縮圖也認 If-None-Match");
        MediaBrowser.Header(notModified, "ETag").Should().Be(etag);
        (await notModified.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        scene.Device.Seen.Should().HaveCount(1, "第二次從快取，不再問電腦");
    }

    [Fact]
    public async Task E2_不是PNG或JPEG的回應不寫進快取_回502()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var notImage = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumb: notImage);
        scene.ServeWithThumbs();

        using (var response = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320")))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await MediaHost.ErrorAsync(response)).Should().Be("bad_gateway");
        }

        Directory.EnumerateFiles(scene.Host.Host.Paths.ThumbCacheDir).Should().BeEmpty();
        using (await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320")))
        {
        }

        scene.Device.Seen.Should().HaveCount(2, "沒寫進快取，下次照樣轉送");
    }

    [Fact]
    public async Task E2_裝置提早結束的縮圖不寫進快取()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var png = MediaBytes.Png(400 * 1024);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumb: png);
        scene.Device.Serve(async request =>
        {
            var content = new StreamContent(new FailingStream(png.AsSpan(0, 256 * 1024).ToArray()));
            content.Headers.ContentLength = png.Length;
            try
            {
                // Expect: 100-continue：伺服器開始讀本文（已接下回覆）才送本文，中止不會在伺服器讀到標頭之前把連線重設掉
                var put = scene.Device.BuildPut(request.RelayId, new FakeReply(200, png.Length, ContentType: "image/png", Content: content));
                put.Headers.ExpectContinue = true;
                using var _ = await scene.Device.SendAsync(put);
            }
            catch (HttpRequestException)
            {
            }
        });

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        Directory.EnumerateFiles(scene.Host.Host.Paths.ThumbCacheDir).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?s=100")]
    [InlineData("?s=320&s=160")]
    [InlineData("?s=320&v=abc")]
    [InlineData("?s=320&v=1&v=2")]
    public async Task E2_s只收160與320_v只收數字(string query)
    {
        await using var scene = await Scene.StartAsync();
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumb: MediaBytes.Png(100));

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + query));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MediaHost.ErrorAsync(response)).Should().Be("invalid");
        scene.Device.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task E2_抽屜的縮圖依版本分開快取()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumb: MediaBytes.Png(500), purpose: "list");
        scene.ServeWithThumbs();

        using (await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=160&v=1712345678")))
        {
        }

        using (var again = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=160&v=1712345678")))
            again.StatusCode.Should().Be(HttpStatusCode.OK);
        scene.Device.Seen.Should().HaveCount(1);
        using (var changed = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=160&v=1712345679")))
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
        scene.Device.Seen.Should().HaveCount(2, "檔案改過（版本不同）就重新轉送");
        File.Exists(Path.Combine(scene.Host.Host.Paths.ThumbCacheDir, key + "@1712345678")).Should().BeTrue();
    }

    [Fact]
    public async Task E1_圖片在離線時退回縮圖_影片照回503()
    {
        await using var scene = await Scene.StartAsync();
        var png = MediaBytes.Png(2000);
        var sha = await scene.StoreThumbAsync(png);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100_000), "image/jpeg"), thumbBlob: sha);
        await scene.GoOfflineAsync();

        using (var image = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, fetchDest: "image")))
        {
            image.StatusCode.Should().Be(HttpStatusCode.OK);
            MediaBrowser.Header(image, "Content-Type").Should().Be("image/png");
            MediaBrowser.Header(image, "Cache-Control").Should().Be("no-store");
            MediaBrowser.Header(image, MediaRelayHeaders.Fallback).Should().Be("thumb");
            MediaBrowser.Header(image, "ETag").Should().BeEmpty();
            MediaBrowser.Header(image, "Content-Security-Policy").Should().Be(MediaHeaderRules.MediaCsp);
            (await image.Content.ReadAsByteArrayAsync()).Should().Equal(png);
        }

        using var video = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-", fetchDest: "video"));
        video.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await MediaHost.ErrorAsync(video)).Should().Be("offline");
    }

    [Fact]
    public async Task E1_沒有縮圖時離線照回503()
    {
        await using var scene = await Scene.StartAsync();
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/jpeg"));
        await scene.GoOfflineAsync();

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, fetchDest: "image"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task E1_電腦以policy拒絕時圖片退回快取的縮圖()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var png = MediaBytes.Png(800);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/jpeg"), thumb: png);
        // 縮圖照給，原檔以遠端政策 Off 拒絕（Q1＝A）
        scene.ServeWithThumbs(request => request.Purpose == MediaPurposes.Media
            ? new FakeReply(403, ContentType: "image/jpeg", Error: MediaErrorCodes.Policy)
            : null);
        using (await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320")))
        {
        }

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        MediaBrowser.Header(response, MediaRelayHeaders.Fallback).Should().Be("thumb");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(png);
    }

    /// <summary>2026-10-27：原檔被刪掉（來源電腦回 404）的舊圖片在瀏覽器版是破圖、一直回報「媒體載入失敗」</summary>
    [Fact]
    public async Task E1_原檔已經不在_有縮圖退回縮圖_沒有縮圖時瀏覽器的圖片回佔位圖_其他照回404()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var png = MediaBytes.Png(800);
        var sha = await scene.StoreThumbAsync(png);
        var withThumb = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumbBlob: sha);
        var noThumb = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"));
        scene.ServeWithThumbs(_ => new FakeReply(404, Error: MediaErrorCodes.NotFound));

        using (var fallback = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + withThumb, fetchDest: "image")))
        {
            fallback.StatusCode.Should().Be(HttpStatusCode.OK);
            MediaBrowser.Header(fallback, MediaRelayHeaders.Fallback).Should().Be("thumb");
            (await fallback.Content.ReadAsByteArrayAsync()).Should().Equal(png);
        }

        foreach (var path in new[] { "/m/" + noThumb, "/t/" + noThumb + "?s=320" })
        {
            using var placeholder = await scene.Owner.SendAsync(MediaBrowser.Get(path, fetchDest: "image"));
            placeholder.StatusCode.Should().Be(HttpStatusCode.OK, path);
            MediaBrowser.Header(placeholder, "Content-Type").Should().Be("image/svg+xml");
            MediaBrowser.Header(placeholder, "Cache-Control").Should().Be(MediaServeRules.MissingCacheControl);
            MediaBrowser.Header(placeholder, MediaRelayHeaders.Fallback).Should().Be("missing");
            MediaBrowser.Header(placeholder, "Content-Security-Policy").Should().Be(MediaHeaderRules.MediaCsp, "SVG 照媒體的 CSP（sandbox）");
            (await placeholder.Content.ReadAsByteArrayAsync()).Should().Equal(MediaServeRules.MissingImageSvg());
        }

        using var direct = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + noThumb));
        direct.StatusCode.Should().Be(HttpStatusCode.NotFound, "不是瀏覽器的 <img>（直接開、程式取）照回 404");
    }

    [Fact]
    public async Task E1_電腦以policy拒絕影片時回403policy()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var sha = await scene.StoreThumbAsync(MediaBytes.Png(800));
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "video/mp4"), thumbBlob: sha);
        scene.ServeWithThumbs(_ => new FakeReply(403, ContentType: "video/mp4", Error: MediaErrorCodes.Policy));

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await MediaHost.ErrorAsync(response)).Should().Be("policy");
    }

    [Fact]
    public async Task E9_複本的縮圖代理_no_store()
    {
        await using var scene = await Scene.StartAsync();
        var png = MediaBytes.Png(1200);
        var sha = await scene.StoreThumbAsync(png);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), thumbBlob: sha);
        await using var replica = await FakeMediaDevice.ConnectAsync(scene.Host);
        using var client = scene.Host.Client();

        using var response = await client.SendAsync(MediaHost.Device(HttpMethod.Get, "/api/v1/device/media/" + key + "?purpose=thumb&s=320", replica.AccessToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        MediaBrowser.Header(response, "Cache-Control").Should().Be("no-store");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(png);
    }
}
