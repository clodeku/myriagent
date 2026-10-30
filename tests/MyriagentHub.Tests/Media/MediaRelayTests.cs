using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 媒體轉送（WP4.7 計畫步 4；D7、D9～D13、D27）：瀏覽器 GET <c>/m/</c>、<c>/f/</c> → 伺服器送 MediaRequest → 假電腦以 E7 回覆 → 位元組經管線轉給瀏覽器。
/// 真的 Kestrel、真的裝置 hub 連線（Hello 協商出 media1）、真的擁有者 cookie
/// </summary>
public sealed class MediaRelayTests
{
    private const int MB = 1024 * 1024;

    /// <summary>一個主機、一台在線的假電腦（預設自動照檔案回覆）、一個登入的擁有者、一個完整同步的對話</summary>
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

        public MediaRelay Relay => Host.Services.GetRequiredService<MediaRelay>();

        public static async Task<Scene> StartAsync(bool serve = true, IReadOnlyList<string>? features = null, Action<IServiceCollection>? services = null,
            IDictionary<string, string?>? settings = null)
        {
            var host = await MediaHost.StartAsync(settings: settings, services: services);
            var device = await FakeMediaDevice.ConnectAsync(host, features: features);
            if (serve)
                device.Serve();
            var owner = await host.OwnerAsync();
            var conversation = await MediaSeed.ConversationAsync(host, device.Id);
            return new Scene(host, device, owner, conversation);
        }

        /// <summary>假電腦手上多一個檔，伺服器多一列參照（項目的附件）；回傳網址的鍵</summary>
        public Task<string> AddAsync(FakeMediaFile file, out string mediaRef) => AddAsync(file, "item:item1", out mediaRef);

        /// <summary>同上，指定 <c>media_refs.purpose</c></summary>
        public Task<string> AddAsync(FakeMediaFile file, string purpose, out string mediaRef)
        {
            mediaRef = MediaSeed.NewRef();
            Device.Files[mediaRef] = file;
            return MediaSeed.RefAsync(Host, Device.Id, Conversation, mediaRef, purpose);
        }

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Device.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Range_開放式縮成8MB_媒體CSP與快取標頭()
    {
        await using var scene = await Scene.StartAsync();
        var data = MediaBytes.Random(20 * MB);
        var key = await scene.AddAsync(new FakeMediaFile(data, "video/mp4", "\"v1\""), out var mediaRef);

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        MediaBrowser.Header(response, "Content-Range").Should().Be("bytes 0-8388607/20971520");
        MediaBrowser.Header(response, "Content-Security-Policy").Should().Be(MediaHeaderRules.MediaCsp);
        MediaBrowser.Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        MediaBrowser.Header(response, "Cache-Control").Should().Be("private, max-age=300");
        MediaBrowser.Header(response, "Accept-Ranges").Should().Be("bytes");
        MediaBrowser.Header(response, "Content-Type").Should().Be("video/mp4");
        MediaBrowser.Header(response, "ETag").Should().Be("\"v1\"");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(data.AsSpan(0, 8 * MB).ToArray());
        var request = scene.Device.Seen.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(new MediaRequest(request.RelayId, mediaRef, MediaPurposes.Media, new MediaRange(0, 8388607)));
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Active == 0, "轉送結束就釋放名額");
    }

    [Fact]
    public async Task 沒帶Range_分段接成200()
    {
        await using var scene = await Scene.StartAsync();
        var data = MediaBytes.Random(20 * MB, seed: 3);
        var key = await scene.AddAsync(new FakeMediaFile(data, "video/mp4"), out _);

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        MediaBrowser.Header(response, "Accept-Ranges").Should().Be("bytes");
        response.Content.Headers.ContentLength.Should().Be(data.Length);
        MediaBytes.Sha(await response.Content.ReadAsByteArrayAsync()).Should().Be(MediaBytes.Sha(data));
        scene.Device.Seen.Select(r => r.Range).Should().Equal(new MediaRange(0, 8388607), new MediaRange(8388608, 16777215), new MediaRange(16777216, 20971519));
    }

    [Fact]
    public async Task 沒帶Range_第二段的ETag不同時中止給瀏覽器的連線()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var data = MediaBytes.Random(10 * MB, seed: 4);
        var file = new FakeMediaFile(data, "video/mp4", "\"v1\"");
        var key = await scene.AddAsync(file, out _);
        scene.Device.Serve(async request =>
        {
            var reply = FakeReply.For(file, request);
            using var _ = await scene.Device.PutAsync(request.RelayId, request.Range!.Start == 0 ? reply : reply with { ETag = "\"v2\"" });
        });

        var body = await MediaBrowser.ReadOrAbortedAsync(scene.Owner, MediaBrowser.Get("/m/" + key));

        body.Should().BeNull("檔案在兩段之間換了：不能讓瀏覽器以為收到完整的檔");
        scene.Device.Seen.Should().HaveCount(2);
    }

    [Fact]
    public async Task 沒帶Range_超過64MB回413_裝置的PUT收到410()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var key = await scene.AddAsync(new FakeMediaFile([]), out _);
        var total = MediaRelayRules.MaxUnrangedBytes + 1;
        scene.Device.Serve(async request =>
        {
            var end = request.Range!.End!.Value;
            using var response = await scene.Device.PutAsync(request.RelayId,
                new FakeReply(206, total, new ByteRange(0, end), new byte[end + 1], "video/mp4"));
            scene.Device.Replies.Enqueue(response.StatusCode);
        });

        using var browser = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key));

        browser.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await MediaHost.ErrorAsync(browser)).Should().Be("too_large");
        await MediaBrowser.EventuallyAsync(() => !scene.Device.Replies.IsEmpty || !scene.Device.Errors.IsEmpty, "裝置的 PUT 結束");
        // 裝置還沒送完 8 MB 時伺服器就回 410、收掉連線（慢的機器上常見，GitHub 的 runner 每次都是）：
        // 裝置那邊只看到傳送失敗、拿不到 410。兩種都表示伺服器不收剩下的本文；其他例外照樣失敗
        if (scene.Device.Errors.TryPeek(out var error))
        {
            error.Should().BeOfType<HttpRequestException>("伺服器回 410 之後收掉連線，裝置只會看到傳送失敗；實際是 {0}", error);
            scene.Device.Replies.Should().BeEmpty();
        }
        else
            scene.Device.Replies.Should().Equal(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task 沒帶Range_帶IfNoneMatch_ETag相同只向電腦要1個位元組回304_不同照常整份()
    {
        await using var scene = await Scene.StartAsync();
        var data = MediaBytes.Random(3 * MB, seed: 21);
        var key = await scene.AddAsync(new FakeMediaFile(data, "video/mp4", "\"v1\""), out _);

        var same = MediaBrowser.Get("/m/" + key);
        same.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        using (var response = await scene.Owner.SendAsync(same))
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotModified);
            MediaBrowser.Header(response, "ETag").Should().Be("\"v1\"");
            MediaBrowser.Header(response, "Cache-Control").Should().NotBeEmpty();
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }
        scene.Device.Seen.Select(r => r.Range).Should().Equal([new MediaRange(0, 0)], "只探 ETag，不把 3 MB 再從電腦傳一次");
        await MediaBrowser.EventuallyAsync(() => scene.Device.Replies.Count == 1, "探 ETag 的 PUT 結束");
        scene.Device.Replies.Should().OnlyContain(s => s == HttpStatusCode.NoContent, "探 ETag 的那 1 個位元組也讀完（裝置不會收到 410）");

        var stale = MediaBrowser.Get("/m/" + key);
        stale.Headers.TryAddWithoutValidation("If-None-Match", "\"v0\"");
        using (var response = await scene.Owner.SendAsync(stale))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, "瀏覽器手上的是舊版");
            MediaBrowser.Header(response, "ETag").Should().Be("\"v1\"");
            MediaBytes.Sha(await response.Content.ReadAsByteArrayAsync()).Should().Be(MediaBytes.Sha(data));
        }
    }

    [Fact]
    public async Task 後綴範圍先探總長()
    {
        await using var scene = await Scene.StartAsync();
        var data = Encoding.ASCII.GetBytes("0123456789");
        var key = await scene.AddAsync(new FakeMediaFile(data, "video/mp4"), out _);

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=-3"));

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        MediaBrowser.Header(response, "Content-Range").Should().Be("bytes 7-9/10");
        Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()).Should().Be("789");
        scene.Device.Seen.Select(r => r.Range).Should().Equal(new MediaRange(0, 0), new MediaRange(7, 9));
        await MediaBrowser.EventuallyAsync(() => scene.Device.Replies.Count == 2, "兩個 PUT 都結束");
        scene.Device.Replies.Should().OnlyContain(s => s == HttpStatusCode.NoContent, "探總長的那 1 個位元組也讀完（裝置不會收到 410）");
    }

    [Fact]
    public async Task 起點在檔尾之後回416()
    {
        await using var scene = await Scene.StartAsync();
        var key = await scene.AddAsync(new FakeMediaFile(Encoding.ASCII.GetBytes("0123456789"), "video/mp4"), out _);

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=10-"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        MediaBrowser.Header(response, "Content-Range").Should().Be("bytes */10");
    }

    [Fact]
    public async Task 首位元組逾時回504_之後到的PUT收到410()
    {
        var timers = new ManualTimerProvider();
        await using var scene = await Scene.StartAsync(serve: false,
            services: s => s.Replace(ServiceDescriptor.Singleton(sp => ActivatorUtilities.CreateInstance<MediaRelay>(sp, (TimeProvider)timers))));
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "video/mp4"), out _);

        var browser = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-", fetchDest: "video"));
        var request = await scene.Device.NextRequestAsync();
        await MediaBrowser.EventuallyAsync(() => timers.Pending > 0, "轉送開始等首位元組");
        timers.Advance(TimeSpan.FromSeconds(14));
        browser.IsCompleted.Should().BeFalse("還沒到 15 秒");
        timers.Advance(TimeSpan.FromSeconds(1));
        using var response = await browser;

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        (await MediaHost.ErrorAsync(response)).Should().Be("timeout");
        scene.Relay.Timeouts.Should().Be(1);
        scene.Relay.Active.Should().Be(0);
        using var late = await scene.Device.ReplyAsync(request);
        late.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task 裝置不在線回503offline()
    {
        await using var scene = await Scene.StartAsync();
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "video/mp4"), out _);
        await scene.Device.Client.Connection.StopAsync();
        var registry = scene.Host.Services.GetRequiredService<ConnectionRegistry>();
        await MediaBrowser.EventuallyAsync(() => !registry.IsOnline(scene.Device.Id), "伺服器看到裝置離線");

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-", fetchDest: "video"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await MediaHost.ErrorAsync(response)).Should().Be("offline");
        MediaBrowser.Header(response, "Retry-After").Should().BeEmpty("離線不是忙碌");
    }

    [Fact]
    public async Task 裝置沒宣告media1回503_收不到任何MediaRequest()
    {
        await using var scene = await Scene.StartAsync(features: [DeviceFeatureRules.Conv1]);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "video/mp4"), out _);

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-", fetchDest: "video"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await MediaHost.ErrorAsync(response)).Should().Be("offline");
        await Task.Delay(300);
        scene.Device.Seen.Should().BeEmpty("沒協商出 media1 的連線，伺服器一則 MediaRequest 都不送");
    }

    [Fact]
    public async Task 政策改成metadata之後下一個請求404_不必等索引器()
    {
        await using var scene = await Scene.StartAsync();
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "video/mp4"), out _);
        using (var before = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-")))
            before.StatusCode.Should().Be(HttpStatusCode.PartialContent);

        await MediaSeed.SetPolicyAsync(scene.Host, scene.Device.Id, scene.Conversation, ConversationSyncPolicies.Metadata);
        using var after = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));

        after.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await MediaHost.ErrorAsync(after)).Should().Be("not_found");
        scene.Device.Seen.Should().HaveCount(1, "降級之後不再向電腦要位元組");
    }

    /// <summary>以手動的計時器建 MediaRelay（排隊與首位元組的等待都照它）</summary>
    private static Task<Scene> StartWithTimersAsync(ManualTimerProvider timers, IDictionary<string, string?>? settings = null)
        => Scene.StartAsync(serve: false, settings: settings,
            services: s => s.Replace(ServiceDescriptor.Singleton(sp => ActivatorUtilities.CreateInstance<MediaRelay>(sp, (TimeProvider)timers))));

    /// <summary>
    /// 2026-10-28：一台同時 20 個轉送，第 21 個以前直接回 503 加 Retry-After，但瀏覽器的 img 不照 Retry-After 重試、成了破圖。
    /// 改成排隊（還沒送 MediaRequest），名額空出來就交給它
    /// </summary>
    [Fact]
    public async Task 第21個同時轉送先排隊_不送給電腦_名額空出來就交給它()
    {
        var timers = new ManualTimerProvider();
        await using var scene = await StartWithTimersAsync(timers);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(1000), "video/mp4"), out _);
        var release = new SemaphoreSlim(0);
        scene.Device.Serve(async request =>
        {
            await release.WaitAsync();
            using var _ = await scene.Device.ReplyAsync(request);
        });

        var held = Enumerable.Range(0, MediaRelayRules.MaxConcurrentPerDevice)
            .Select(_ => scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-")))
            .ToList();
        await MediaBrowser.EventuallyAsync(() => scene.Device.Seen.Count == 20 && scene.Relay.Active == 20, "20 個轉送都在等電腦回覆");

        var queued = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Queued == 1, "第 21 個排隊");
        queued.IsCompleted.Should().BeFalse("排隊中不回 503");
        scene.Device.Seen.Should().HaveCount(20, "排隊中不送給電腦");
        scene.Relay.Active.Should().Be(20);

        release.Release();
        await MediaBrowser.EventuallyAsync(() => scene.Device.Seen.Count == 21 && scene.Relay.Queued == 0, "名額空出來就交給排著的");
        release.Release(20);
        foreach (var task in held.Append(queued))
        {
            using var response = await task;
            response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        }

        await MediaBrowser.EventuallyAsync(() => scene.Relay.Active == 0, "名額都釋放");
    }

    [Fact]
    public async Task 排隊等滿10秒還是沒有名額_回503busy加RetryAfter_不送給電腦()
    {
        var timers = new ManualTimerProvider();
        await using var scene = await StartWithTimersAsync(timers);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(1000), "video/mp4"), out _);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Device.Serve(async request =>
        {
            await gate.Task;
            using var _ = await scene.Device.ReplyAsync(request);
        });

        var held = Enumerable.Range(0, MediaRelayRules.MaxConcurrentPerDevice)
            .Select(_ => scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-")))
            .ToList();
        await MediaBrowser.EventuallyAsync(() => scene.Device.Seen.Count == 20 && timers.Pending == 20, "20 個轉送都在等首位元組");
        var queued = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Queued == 1 && timers.Pending == 21, "第 21 個排隊、開始等名額");

        timers.Advance(MediaRelay.QueueWait - TimeSpan.FromSeconds(1));
        queued.IsCompleted.Should().BeFalse("還沒等滿 10 秒");
        timers.Advance(TimeSpan.FromSeconds(1));
        using (var busy = await queued)
        {
            busy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await MediaHost.ErrorAsync(busy)).Should().Be("busy");
            MediaBrowser.Header(busy, "Retry-After").Should().Be("2");
        }

        scene.Relay.Queued.Should().Be(0, "等滿了退出隊伍");
        scene.Device.Seen.Should().HaveCount(20, "第 21 個不送給電腦");
        gate.SetResult();
        foreach (var task in held)
        {
            using var response = await task;
            response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        }

        await MediaBrowser.EventuallyAsync(() => scene.Relay.Active == 0, "名額都釋放");
    }

    [Fact]
    public async Task 排隊中瀏覽器離開_退出隊伍_不佔名額_之後空出來的名額給下一個()
    {
        var timers = new ManualTimerProvider();
        await using var scene = await StartWithTimersAsync(timers);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(1000), "video/mp4"), out _);
        var release = new SemaphoreSlim(0);
        scene.Device.Serve(async request =>
        {
            await release.WaitAsync();
            using var _ = await scene.Device.ReplyAsync(request);
        });
        var held = Enumerable.Range(0, MediaRelayRules.MaxConcurrentPerDevice)
            .Select(_ => scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-")))
            .ToList();
        await MediaBrowser.EventuallyAsync(() => scene.Device.Seen.Count == 20, "20 個轉送都在等電腦回覆");

        using var cancel = new CancellationTokenSource();
        var left = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"), cancel.Token);
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Queued == 1, "第 21 個排隊");
        var next = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Queued == 2, "第 22 個排在後面");

        await cancel.CancelAsync();
        await FluentActions.Awaiting(() => left).Should().ThrowAsync<OperationCanceledException>();
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Queued == 1, "離開的退出隊伍");
        scene.Relay.Active.Should().Be(20);

        release.Release();
        await MediaBrowser.EventuallyAsync(() => scene.Device.Seen.Count == 21 && scene.Relay.Queued == 0, "空出來的名額給還在等的那一個");
        release.Release(20);
        foreach (var task in held.Append(next))
        {
            using var response = await task;
            response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        }

        await MediaBrowser.EventuallyAsync(() => scene.Relay.Active == 0, "名額都釋放、離開的沒有多佔一個");
    }

    /// <summary>
    /// 所有來源合計的上限（<c>Hub:Media:MaxConcurrentRelays</c>，這裡調成 3）：兩台各自都沒到每台的 20 個，合計滿了照樣排隊、不送 MediaRequest，
    /// 等滿 10 秒回 503 busy 加 Retry-After；名額釋放後再試成功
    /// </summary>
    [Fact]
    public async Task 所有來源合計的同時轉送滿了先排隊_等滿10秒回503_不送給電腦_釋放後再試成功()
    {
        var timers = new ManualTimerProvider();
        await using var scene = await StartWithTimersAsync(timers, new Dictionary<string, string?> { ["Hub:Media:MaxConcurrentRelays"] = "3" });
        var file = new FakeMediaFile(MediaBytes.Random(1000), "video/mp4");
        var keyA = await scene.AddAsync(file, out _);
        await using var other = await FakeMediaDevice.ConnectAsync(scene.Host);
        var conversationB = await MediaSeed.ConversationAsync(scene.Host, other.Id);
        var refB = MediaSeed.NewRef();
        other.Files[refB] = file;
        var keyB = await MediaSeed.RefAsync(scene.Host, other.Id, conversationB, refB);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var device in new[] { scene.Device, other })
        {
            device.Serve(async request =>
            {
                await gate.Task;
                using var _ = await device.ReplyAsync(request);
            });
        }

        var held = new[] { keyA, keyA, keyB }.Select(key => scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"))).ToList();
        await MediaBrowser.EventuallyAsync(() => scene.Device.Seen.Count == 2 && other.Seen.Count == 1 && scene.Relay.Active == 3 && timers.Pending == 3,
            "3 個轉送都在等電腦回覆");

        var busy = new[] { keyA, keyB }.Select(key => scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"))).ToList();
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Queued == 2 && timers.Pending == 5, "合計滿了：兩個都排隊");
        timers.Advance(MediaRelay.QueueWait);
        foreach (var task in busy)
        {
            using var response = await task;
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await MediaHost.ErrorAsync(response)).Should().Be("busy");
            MediaBrowser.Header(response, "Retry-After").Should().Be("2");
        }

        scene.Device.Seen.Should().HaveCount(2, "合計滿了就不送給電腦");
        other.Seen.Should().HaveCount(1);
        gate.SetResult();
        foreach (var task in held)
        {
            using var response = await task;
            response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        }

        await MediaBrowser.EventuallyAsync(() => scene.Relay.Active == 0, "名額都釋放");
        using var again = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + keyB, "bytes=0-"));
        again.StatusCode.Should().Be(HttpStatusCode.PartialContent);
    }

    /// <summary>
    /// WP4.9 步 12 的回歸：Kestrel 給 PUT 本文的每次讀取常只有約 4 KB。管線暫停（未讀 ≥1 MB）時佔住的記憶體要約等於門檻，
    /// 不是每次讀取都另配一段 64 KB（舊的 <c>GetMemory(64 KB)</c>：1 MB ÷ 4 KB × 64 KB＝16 MB，一台 20 個轉送 320 MB，兩三台就 Out of memory）。
    /// 走正式的 StartAsync／AcceptAsync（PumpAsync）；本文用每次只給 4 KB 的串流，讀取大小固定、結果每次相同
    /// </summary>
    [Fact]
    public async Task 裝置每次只送4KB_暫停的管線只佔約門檻的記憶體_讀完全部還回()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var pool = new CountingMemoryPool();
        scene.Relay.PipePoolForTest = pool;
        var data = MediaBytes.Random((int)MediaRelayRules.MaxSegmentBytes, seed: 21);
        var source = new TrickleStream(data, chunk: 4096);

        var start = scene.Relay.StartAsync(scene.Device.Id, MediaSeed.NewRef(), MediaPurposes.Media, new MediaRange(0, data.Length - 1),
            MediaRelayRules.MaxSegmentBytes, CancellationToken.None);
        var request = await scene.Device.NextRequestAsync();
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Put;
        http.Request.Headers[MediaRelayHeaders.Status] = "206";
        http.Request.Headers[MediaRelayHeaders.TotalLength] = data.Length.ToString(CultureInfo.InvariantCulture);
        http.Request.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes 0-{data.Length - 1}/{data.Length}");
        http.Request.ContentType = "video/mp4";
        http.Request.ContentLength = data.Length;
        http.Request.Body = source;
        var put = Task.Run(() => scene.Relay.AcceptAsync(scene.Device.Id, request.RelayId, http));
        using var outcome = await start;
        outcome.Failure.Should().Be(RelayFailure.None);

        await MediaBrowser.EventuallyAsync(() => source.Delivered >= MediaRelay.PauseWriterBytes, "管線收到 1 MB");
        await Task.Delay(300);
        source.Delivered.Should().Be(MediaRelay.PauseWriterBytes, "未讀滿 1 MB 就讓裝置等（背壓）");
        put.IsCompleted.Should().BeFalse();
        pool.Outstanding.Should().BeLessThanOrEqualTo(MediaRelay.PauseWriterBytes + 2 * MediaRelay.SegmentBytes,
            "暫停的管線只佔約門檻的記憶體（每次讀 4 KB 也不能每次另配一段 64 KB）");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16 * 1024];
        long received = 0;
        int read;
        while ((read = await outcome.Body!.ReadAsync(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            received += read;
        }

        received.Should().Be(data.Length);
        Convert.ToHexStringLower(hash.GetHashAndReset()).Should().Be(MediaBytes.Sha(data));
        var result = await put;
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        pool.Peak.Should().BeLessThanOrEqualTo(MediaRelay.PauseWriterBytes + 3 * MediaRelay.SegmentBytes, "邊讀邊送時也不超過門檻加幾段");
        outcome.Dispose();
        pool.Outstanding.Should().Be(0, "轉送結束，管線的區段全部還回");
    }

    /// <summary>
    /// S9 閒置記憶體的修正（設計 14.20）：沒有測試注入點時，轉送的管線向 <see cref="MediaRelay.Segments"/> 要區段（不是共用的 ArrayPool：
    /// 那裡每個處理器每種大小留 32 個，轉送大量並行之後閒置停在約 300 MB）。走瀏覽器 GET /m/ 的整條路
    /// </summary>
    [Fact]
    public async Task 轉送的管線向自己的區段池要記憶體_不用共用的ArrayPool()
    {
        await using var scene = await Scene.StartAsync();
        var data = MediaBytes.Random(2 * MB, seed: 23);
        var key = await scene.AddAsync(new FakeMediaFile(data, "video/mp4"), out _);
        var before = scene.Relay.Segments.Rented;

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        MediaBytes.Sha(await response.Content.ReadAsByteArrayAsync()).Should().Be(MediaBytes.Sha(data));
        (scene.Relay.Segments.Rented - before).Should().BeGreaterThanOrEqualTo(data.Length / MediaRelay.SegmentBytes, "2 MB 至少 32 段，全部來自轉送自己的池");
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Segments.Counts == (0, 0), "轉送結束：管線的區段全部還回、池裡什麼都不留");
    }

    /// <summary>
    /// S9 閒置記憶體的修正（設計 14.20）：真的 Kestrel 的 socket 傳輸層每條連線最多先收 64 KB（預設 1 MB）。轉送暫停時裝置的 PUT 連線
    /// 不再各佔 1 MB 的 Kestrel 區塊，轉送大量並行之後也不會有幾十 MB 的區塊留在 Kestrel 的池裡（heap dump：閒置修剪之後還有 55.6 MB）
    /// </summary>
    [Fact]
    public async Task 真的Kestrel_傳輸層每條連線最多先收64KB()
    {
        await using var host = await MediaHost.StartAsync();
        var sockets = host.Services.GetRequiredService<IOptions<SocketTransportOptions>>().Value;
        sockets.MaxReadBufferSize.Should().Be(64 * 1024);
    }

    /// <summary>
    /// 轉送的區段池：還有區段借在外面時重用還回來的（並行轉送期間不必一直新配，私有記憶體不衝高），
    /// 最後一段還回來就連留著的全部放掉、GC 收得走（轉送都結束之後池裡什麼都不留）
    /// </summary>
    [Fact]
    public void 轉送的區段池_還有借在外面的就重用_最後一段還回來全部放掉_GC收得走()
    {
        var pool = new RelaySegmentPool();
        var hold = pool.Rent();
        var weak = RentAndReturn(pool, out var length);
        length.Should().Be(MediaRelay.SegmentBytes);
        pool.Counts.Should().Be((1, 1), "還有一段借在外面：還回來的留著");

        RentReusesCached(pool, weak).Should().BeTrue("不足一段的要求也給一整段，重用留著的那個陣列");
        pool.Allocated.Should().Be(2, "第三次借是重用的");
        pool.Counts.Should().Be((1, 1));

        using (var large = pool.Rent(MediaRelay.SegmentBytes + 1))
            large.Memory.Length.Should().Be(MediaRelay.SegmentBytes + 1);
        pool.Allocated.Should().Be(3);
        pool.Counts.Should().Be((1, 1), "其他大小的照配、不留");

        hold.Dispose();
        hold.Dispose();
        pool.Counts.Should().Be((0, 0), "最後一段還回來：留著的全部放掉（重複還回只算一次）");
        pool.Rented.Should().Be(4);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        weak.IsAlive.Should().BeFalse("沒有任何池留著還回來的區段");
    }

    /// <summary>借一段、記下長度與弱參考再還回去（不內嵌：回傳之後這裡的區域變數不再留住陣列）</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RentAndReturn(RelaySegmentPool pool, out int length)
    {
        var owner = pool.Rent();
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(owner.Memory, out var segment).Should().BeTrue();
        length = segment.Array!.Length;
        var weak = new WeakReference(segment.Array);
        owner.Dispose();
        FluentActions.Invoking(() => owner.Memory).Should().Throw<ObjectDisposedException>("還回來之後不再指向陣列");
        return weak;
    }

    /// <summary>借一段不足 64 KB 的、看是不是 <paramref name="cached"/> 那個陣列，再還回去（不內嵌，理由同 <see cref="RentAndReturn"/>）</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool RentReusesCached(RelaySegmentPool pool, WeakReference cached)
    {
        using var owner = pool.Rent(100);
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(owner.Memory, out var segment).Should().BeTrue();
        return segment.Array!.Length == MediaRelay.SegmentBytes && ReferenceEquals(segment.Array, cached.Target);
    }

    /// <summary>
    /// 瀏覽器在裝置的本文送到一半時離開（例如影片拖動進度），轉送正在等裝置的下一段：裝置照樣收到 410，伺服器不能留 Kestrel[67]
    /// 「automatic draining of the request body failed because the body reader is in an invalid state」錯誤（以取消權杖中止 Kestrel 的本文讀取，
    /// 回應結束時排空本文就會失敗並關掉連線；WP4.9 步 12 的負載中止時每次好幾行）
    /// </summary>
    [Fact]
    public async Task 瀏覽器在裝置的本文中途離開_裝置收到410_Kestrel不記排空失敗()
    {
        var logs = new CapturingLoggerProvider();
        await using var scene = await Scene.StartAsync(serve: false, services: s => s.AddSingleton<ILoggerProvider>(logs));
        var data = MediaBytes.Random(MB, seed: 22);
        var file = new FakeMediaFile(data, "video/mp4");
        var key = await scene.AddAsync(file, out _);
        var rest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var browser = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        var request = await scene.Device.NextRequestAsync();
        var put = scene.Device.SendAsync(scene.Device.BuildPut(request.RelayId, FakeReply.For(file, request) with { Content = new GatedContent(data, 64 * 1024, rest.Task) }));
        using (var response = await browser)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
            await stream.ReadExactlyAsync(new byte[64 * 1024], cancel.Token);
            // 轉送已經把前 64 KB 交給瀏覽器、正在等裝置的下一段；瀏覽器這時離開
            var pending = stream.ReadAsync(new byte[1024], cancel.Token).AsTask();
            await cancel.CancelAsync();
            await FluentActions.Awaiting(() => pending).Should().ThrowAsync<Exception>();
        }

        await MediaBrowser.EventuallyAsync(() => scene.Relay.Active == 0, "瀏覽器離開就釋放名額");
        await Task.Delay(300);
        rest.SetResult();
        HttpResponseMessage? reply = null;
        try
        {
            reply = await put.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (HttpRequestException)
        {
        }

        using (reply)
        {
            logs.Entries.Where(e => e.Category.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal) && e.Level >= LogLevel.Warning)
                .Select(e => e.Text).Should().BeEmpty("瀏覽器離開是正常的情況，Kestrel 不該記錯誤");
            reply?.StatusCode.Should().Be(HttpStatusCode.Gone);
            reply.Should().NotBeNull("裝置要收到 410（不是連線被關掉）");
        }
    }

    [Fact]
    public async Task 瀏覽器中斷_名額立即釋放_裝置的PUT收到410_再PUT一次404()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "video/mp4"), out var mediaRef);
        using var cancel = new CancellationTokenSource();

        var browser = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"), cancel.Token);
        var request = await scene.Device.NextRequestAsync();
        scene.Relay.Active.Should().Be(1);
        await cancel.CancelAsync();
        await FluentActions.Awaiting(() => browser).Should().ThrowAsync<OperationCanceledException>();
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Active == 0, "瀏覽器離開就釋放名額");

        using (var late = await scene.Device.ReplyAsync(request))
            late.StatusCode.Should().Be(HttpStatusCode.Gone);
        using (var again = await scene.Device.ReplyAsync(request))
            again.StatusCode.Should().Be(HttpStatusCode.NotFound, "同一個 relayId 只收一次");
        mediaRef.Should().Be(request.Ref);
    }

    [Fact]
    public async Task 連線關閉只取消那條連線的轉送()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var data = Encoding.ASCII.GetBytes("0123456789");
        var key = await scene.AddAsync(new FakeMediaFile(data, "video/mp4"), out _);
        var older = scene.Device;

        var first = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-", fetchDest: "video"));
        var onOlder = await older.NextRequestAsync();
        await using var newer = await FakeMediaDevice.ConnectAsync(scene.Host, older.Device);
        newer.Files[onOlder.Ref] = older.Files[onOlder.Ref];
        var second = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));
        var onNewer = await newer.NextRequestAsync();
        onNewer.RelayId.Should().NotBe(onOlder.RelayId, "新的轉送送給最新的連線");

        await older.Client.Connection.StopAsync();
        using (var cancelled = await first)
        {
            cancelled.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await MediaHost.ErrorAsync(cancelled)).Should().Be("offline");
        }

        using (var put = await newer.ReplyAsync(onNewer))
            put.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var completed = await second;
        completed.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await completed.Content.ReadAsByteArrayAsync()).Should().Equal(data);
    }

    [Fact]
    public async Task SVG改成octet_stream加attachment()
    {
        await using var scene = await Scene.StartAsync();
        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var key = await scene.AddAsync(new FakeMediaFile(svg, "image/svg+xml", FileName: "x.svg"), out _);

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        MediaBrowser.Header(response, "Content-Type").Should().Be("application/octet-stream");
        MediaBrowser.Header(response, "Content-Disposition").Should().StartWith("attachment; filename=\"x.svg\"");
        MediaBrowser.Header(response, "Content-Security-Policy").Should().Be(MediaHeaderRules.MediaCsp);
        MediaBrowser.Header(response, "X-Content-Type-Options").Should().Be("nosniff");
    }

    [Fact]
    public async Task 別台裝置用這個relayId回404_同一台回第二次也404()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var data = Encoding.ASCII.GetBytes("0123456789");
        var key = await scene.AddAsync(new FakeMediaFile(data, "video/mp4"), out _);
        await using var stranger = await FakeMediaDevice.ConnectAsync(scene.Host);

        var browser = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));
        var request = await scene.Device.NextRequestAsync();
        using (var other = await stranger.PutAsync(request.RelayId, FakeReply.For(new FakeMediaFile(Encoding.ASCII.GetBytes("XXXXXXXXXX"), "video/mp4"), request)))
            other.StatusCode.Should().Be(HttpStatusCode.NotFound, "relayId 只收發給這台的");
        using (var reply = await scene.Device.ReplyAsync(request))
            reply.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var response = await browser)
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(data);
        using var second = await scene.Device.ReplyAsync(request);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 檔案_中文檔名下載_no_store()
    {
        await using var scene = await Scene.StartAsync();
        var data = MediaBytes.Random(1000);
        var key = await scene.AddAsync(new FakeMediaFile(data, "application/zip", FileName: "報告.txt", Kind: MediaContentKinds.Binary), "file", out var mediaRef);

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/f/" + key));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        MediaBrowser.Header(response, "Content-Disposition").Should().Contain("filename*=UTF-8''%E5%A0%B1%E5%91%8A.txt").And.StartWith("attachment");
        MediaBrowser.Header(response, "Content-Type").Should().Be("application/octet-stream");
        MediaBrowser.Header(response, "Cache-Control").Should().Be("no-store");
        MediaBrowser.Header(response, "Content-Security-Policy").Should().Be(MediaHeaderRules.MediaCsp);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(data);
        scene.Device.Seen.Should().ContainSingle().Which.Purpose.Should().Be(MediaPurposes.File);
        scene.Device.Seen.Single().Ref.Should().Be(mediaRef);
    }

    [Fact]
    public async Task 檔案_文字2MB以內inline預覽()
    {
        await using var scene = await Scene.StartAsync();
        var text = Encoding.UTF8.GetBytes("第一行\n第二行\n");
        var key = await scene.AddAsync(new FakeMediaFile(text, "text/plain", FileName: "notes.md", Kind: MediaContentKinds.Text), "file", out _);

        using var response = await scene.Owner.SendAsync(MediaBrowser.Get("/f/" + key));

        MediaBrowser.Header(response, "Content-Type").Should().Be("text/plain; charset=utf-8");
        MediaBrowser.Header(response, "Content-Disposition").Should().StartWith("inline; filename=\"notes.md\"");
        (await response.Content.ReadAsStringAsync()).Should().Be("第一行\n第二行\n");
    }

    [Fact]
    public async Task 用途與端點綁定_到期_鍵不合格_一律同一種404()
    {
        await using var scene = await Scene.StartAsync();
        var file = new FakeMediaFile(MediaBytes.Random(10), "image/png");
        var item = await scene.AddAsync(file, "item:item1", out _);
        var fetched = await scene.AddAsync(file, "file", out _);

        foreach (var path in new[] { "/f/" + item, "/m/" + fetched, "/t/" + fetched + "?s=320", "/m/abc", "/m/" + MediaKeyRules.Format(scene.Device.Id, MediaSeed.NewRef()) })
        {
            using var response = await scene.Owner.SendAsync(MediaBrowser.Get(path));
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            (await MediaHost.ErrorAsync(response)).Should().Be("not_found", path);
            MediaBrowser.Header(response, "Cache-Control").Should().Be("no-store");
        }

        using (var fresh = await scene.Owner.SendAsync(MediaBrowser.Get("/f/" + fetched)))
            fresh.StatusCode.Should().Be(HttpStatusCode.OK);
        scene.Host.Host.Clock.Advance(TimeSpan.FromMinutes(15));
        using (var expired = await scene.Owner.SendAsync(MediaBrowser.Get("/f/" + fetched)))
            expired.StatusCode.Should().Be(HttpStatusCode.NotFound, "fileFetch 的參照 15 分鐘到期");
        scene.Device.Seen.Should().HaveCount(1, "查不到參照時不向電腦要");
    }

    [Fact]
    public async Task E7_1MB加1位元組的段照收_宣告8MB加1回413()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var data = MediaBytes.Random(MB + 1, seed: 9);
        var key = await scene.AddAsync(new FakeMediaFile(data, "video/mp4"), out _);

        var small = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));
        using (var put = await scene.Device.ReplyAsync(await scene.Device.NextRequestAsync()))
            put.StatusCode.Should().Be(HttpStatusCode.NoContent, "超過 Kestrel 全域的 1 MB 照收（E7 放寬到 8 MB）");
        using (var response = await small)
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(data);

        var big = scene.Owner.SendAsync(MediaBrowser.Get("/m/" + key, "bytes=0-"));
        var request = await scene.Device.NextRequestAsync();
        var put2 = scene.Device.BuildPut(request.RelayId, new FakeReply(206, 20 * MB, new ByteRange(0, 8388607), ContentType: "video/mp4"));
        using (var tooLarge = await scene.Device.SendAsync(MediaHost.Unread(put2, MediaRelayRules.MaxSegmentBytes + 1)))
            tooLarge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var browser = await big;
        browser.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task 裝置的本文提早結束_中止給瀏覽器的連線()
    {
        await using var scene = await Scene.StartAsync(serve: false);
        var data = MediaBytes.Random(MB, seed: 10);
        var file = new FakeMediaFile(data, "video/mp4");
        var key = await scene.AddAsync(file, out _);
        scene.Device.Serve(async request =>
        {
            var reply = FakeReply.For(file, request);
            var content = new StreamContent(new FailingStream(data.AsSpan(0, 256 * 1024).ToArray()));
            content.Headers.ContentLength = reply.Body!.Length;
            try
            {
                // Expect: 100-continue：伺服器開始讀本文（已接下回覆）才送本文，中止不會在伺服器讀到標頭之前把連線重設掉（那樣是 504）
                var put = scene.Device.BuildPut(request.RelayId, reply with { Content = content });
                put.Headers.ExpectContinue = true;
                using var _ = await scene.Device.SendAsync(put);
            }
            catch (HttpRequestException)
            {
                // 假裝置自己的本文丟例外：用戶端中止 PUT
            }
        });

        var body = await MediaBrowser.ReadOrAbortedAsync(scene.Owner, MediaBrowser.Get("/m/" + key, "bytes=0-"));

        body.Should().BeNull("截斷的內容不能以正常結束交給瀏覽器");
        await MediaBrowser.EventuallyAsync(() => scene.Relay.Active == 0, "名額釋放");
    }

    [Fact]
    public async Task E9_複本代理只收存取權杖_no_store()
    {
        await using var scene = await Scene.StartAsync();
        var data = Encoding.ASCII.GetBytes("0123456789");
        var key = await scene.AddAsync(new FakeMediaFile(data, "image/png"), out _);
        await using var replica = await FakeMediaDevice.ConnectAsync(scene.Host);
        using var client = scene.Host.Client();

        var request = MediaHost.Device(HttpMethod.Get, "/api/v1/device/media/" + key + "?purpose=media", replica.AccessToken);
        request.Headers.TryAddWithoutValidation("Range", "bytes=2-5");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        MediaBrowser.Header(response, "Content-Range").Should().Be("bytes 2-5/10");
        MediaBrowser.Header(response, "Cache-Control").Should().Be("no-store");
        MediaBrowser.Header(response, "Content-Security-Policy").Should().Be(MediaHeaderRules.MediaCsp);
        Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()).Should().Be("2345");

        using (var bad = await client.SendAsync(MediaHost.Device(HttpMethod.Get, "/api/v1/device/media/" + key + "?purpose=bogus", replica.AccessToken)))
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, "E9 只有 media 與 thumb（桌面遠端操作另加 E11 的 file）");
        using (var notFile = await client.SendAsync(MediaHost.Device(HttpMethod.Get, "/api/v1/device/media/" + key + "?purpose=file", replica.AccessToken)))
            notFile.StatusCode.Should().Be(HttpStatusCode.NotFound, "E11 只收 fileFetch 的參照，項目的參照查不到");
        using (var cookie = await scene.Owner.SendAsync(MediaBrowser.Get("/api/v1/device/media/" + key + "?purpose=media")))
            cookie.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "擁有者 cookie 不能用裝置端點");
        using (var device = await client.SendAsync(MediaHost.Device(HttpMethod.Get, "/api/v1/device/media/" + key + "?purpose=media", replica.Device.Token)))
            device.StatusCode.Should().Be(HttpStatusCode.Forbidden, "長效的 chd_ 不是存取權杖");
    }

    [Fact]
    public async Task 媒體端點匿名401()
    {
        await using var scene = await Scene.StartAsync();
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(10)), out _);
        using var anonymous = scene.Host.Client();

        foreach (var path in new[] { "/m/" + key, "/t/" + key + "?s=320", "/f/" + key })
        {
            using var response = await anonymous.SendAsync(MediaBrowser.Get(path));
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        }

        scene.Device.Seen.Should().BeEmpty();
    }

    /// <summary>
    /// 2026-10-23：網頁版的破圖在伺服器上查不到原因（查不到參照、電腦拒絕都不記）。查不到記原因（Information），電腦的守門員擋下記 Warning，
    /// 同一件事 10 分鐘內只記一次
    /// </summary>
    [Fact]
    public async Task 查不到與電腦拒絕都寫進記錄_同一件事只記一次()
    {
        var logs = new CapturingLoggerProvider();
        await using var scene = await Scene.StartAsync(serve: false, services: s => s.AddSingleton<ILoggerProvider>(logs));
        var key = await scene.AddAsync(new FakeMediaFile(MediaBytes.Random(100), "image/png"), out _);
        scene.Device.Serve(async request =>
        {
            using var response = await scene.Device.PutAsync(request.RelayId, new FakeReply(403, Error: MediaErrorCodes.Forbidden));
        });

        for (var i = 0; i < 2; i++)
        {
            using var refused = await scene.Owner.SendAsync(MediaBrowser.Get("/t/" + key + "?s=320"));
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var missing = MediaKeyRules.Format(scene.Device.Id, MediaSeed.NewRef());
        for (var i = 0; i < 2; i++)
        {
            using var notFound = await scene.Owner.SendAsync(MediaBrowser.Get("/m/" + missing));
            notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var media = logs.Entries.Where(e => e.Category == typeof(MediaResponder).FullName).ToList();
        var failures = media.Where(e => e.Text.Contains("媒體轉送沒拿到", StringComparison.Ordinal)).ToList();
        failures.Should().ContainSingle("同一件事只記一次");
        failures[0].Level.Should().Be(LogLevel.Warning, "電腦的守門員擋下登記過的媒體");
        failures[0].Text.Should().Contain(key).And.Contain("forbidden").And.Contain(scene.Conversation).And.Contain("回 403");

        var misses = media.Where(e => e.Text.Contains("媒體請求查不到", StringComparison.Ordinal)).ToList();
        misses.Should().ContainSingle();
        misses[0].Level.Should().Be(LogLevel.Information);
        misses[0].Text.Should().Contain(missing).And.Contain(MediaLookupMisses.NoRow);
    }
}
