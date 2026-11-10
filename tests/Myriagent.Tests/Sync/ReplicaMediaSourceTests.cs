using System.Net;
using System.Net.Http.Headers;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync;

/// <summary>
/// 複本的媒體來源（WP4.7 計畫步 12，D38；E9 <c>GET /api/v1/device/media/{key}</c>）：原檔一次最多 8 MB（網頁的 Range 縮進去、伺服器多給的截掉）、
/// 縮圖只收 PNG／JPEG ≤512 KB、逾時 20 秒、沒有權杖不送；任何不合格一律回 null（網頁 404）。假的伺服器是 <see cref="FakeRelayHandler.Responder"/>
/// </summary>
public sealed class ReplicaMediaSourceTests : IDisposable
{
    private const int Mb = 1024 * 1024;

    private readonly FakeRelayHandler relay = new();
    private readonly AccessTokenHolder tokens = new();
    private readonly ManualTimeProvider time = new();
    private readonly ReplicaMediaSource source;
    private readonly string key = MediaKeyRules.Format(SyncIds.NewDeviceId(), MediaTestFiles.NewId());

    public ReplicaMediaSourceTests()
    {
        tokens.Set("cha_unit", time.GetTimestamp());
        var hub = new Uri("https://hub.example/");
        source = new ReplicaMediaSource(new DeviceHttpClient(hub, hub, false, tokens, _ => Task.FromResult(false), time, relay), time);
    }

    public void Dispose() => source.Dispose();

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 31 + 7);
        return bytes;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, string contentType, string? contentRange = null, long? contentLength = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (contentRange is not null)
            content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        if (contentLength is { } length)
            content.Headers.ContentLength = length;
        return new HttpResponseMessage(status) { Content = content };
    }

    private Task<ReplicaMediaReply?> MediaAsync(string? range) => source.FetchAsync(key, MediaPurposes.Media, null, range, CancellationToken.None);

    // ── 原檔 ──

    [Fact]
    public async Task 網頁的Range照轉_回206()
    {
        var body = Bytes(100);
        relay.Responder = _ => Response(HttpStatusCode.PartialContent, body, "video/mp4", "bytes 100-199/5000");

        var reply = await MediaAsync("bytes=100-199");

        var request = relay.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Path.Should().Be("/api/v1/device/media/" + key);
        request.Query.Should().Be("?purpose=media");
        request.Header("Range").Should().Be("bytes=100-199");
        request.Authorization.Should().Be("Bearer cha_unit");
        reply.Should().BeEquivalentTo(new ReplicaMediaReply(206, "video/mp4", body, "bytes 100-199/5000"));
    }

    [Fact]
    public async Task 網頁的Range超過8MB_縮成8MB()
    {
        relay.Responder = _ => Response(HttpStatusCode.PartialContent, Bytes(8 * Mb), "video/mp4", $"bytes {Mb}-{(9 * Mb) - 1}/{20 * Mb}");

        var reply = await MediaAsync($"bytes={Mb}-");

        relay.Requests.Single().Header("Range").Should().Be($"bytes={Mb}-{(9 * Mb) - 1}");
        reply!.Status.Should().Be(206);
        reply.ContentRange.Should().Be($"bytes {Mb}-{(9 * Mb) - 1}/{20 * Mb}");
    }

    [Fact]
    public async Task 來源回超過8MB時截斷並回206()
    {
        // 伺服器不理 Range、回整份 10 MB
        var whole = Bytes(10 * Mb);
        relay.Responder = _ => Response(HttpStatusCode.OK, whole, "video/mp4");

        var reply = await MediaAsync(null);

        reply!.Status.Should().Be(206);
        reply.Body.Should().HaveCount(8 * Mb).And.Equal(whole.Take(8 * Mb));
        reply.ContentRange.Should().Be($"bytes 0-{(8 * Mb) - 1}/{10 * Mb}");
    }

    [Fact]
    public async Task 伺服器回的206比8MB多_只留前8MB()
    {
        var part = Bytes(10 * Mb);
        relay.Responder = _ => Response(HttpStatusCode.PartialContent, part, "video/mp4", $"bytes 0-{(10 * Mb) - 1}/{10 * Mb}");

        var reply = await MediaAsync($"bytes=-{10 * Mb}");

        relay.Requests.Single().Header("Range").Should().Be($"bytes=-{10 * Mb}", "後綴範圍照原樣（伺服器縮）");
        reply!.Status.Should().Be(206);
        reply.Body.Should().HaveCount(8 * Mb);
        reply.ContentRange.Should().Be($"bytes 0-{(8 * Mb) - 1}/{10 * Mb}");
    }

    [Fact]
    public async Task 沒帶Range_回來的正好是整份_改回200()
    {
        var body = Bytes(1000);
        relay.Responder = _ => Response(HttpStatusCode.PartialContent, body, "image/png", "bytes 0-999/1000");

        var reply = await MediaAsync(null);

        relay.Requests.Single().Header("Range").Should().Be($"bytes=0-{(8 * Mb) - 1}", "沒帶 Range 時要前 8 MB");
        reply.Should().BeEquivalentTo(new ReplicaMediaReply(200, "image/png", body, null));
    }

    [Fact]
    public async Task 沒帶Range_大檔回前8MB的206()
    {
        relay.Responder = _ => Response(HttpStatusCode.PartialContent, Bytes(8 * Mb), "video/webm", $"bytes 0-{(8 * Mb) - 1}/{30 * Mb}");

        var reply = await MediaAsync(null);

        reply!.Status.Should().Be(206);
        reply.ContentType.Should().Be("video/webm");
        reply.ContentRange.Should().Be($"bytes 0-{(8 * Mb) - 1}/{30 * Mb}");
    }

    [Fact]
    public async Task 伺服器回200而且不超過8MB_照回200()
    {
        var body = Bytes(500);
        relay.Responder = _ => Response(HttpStatusCode.OK, body, "image/jpeg");

        (await MediaAsync(null)).Should().BeEquivalentTo(new ReplicaMediaReply(200, "image/jpeg", body, null));
    }

    [Fact]
    public async Task 範圍不能滿足_回416()
    {
        relay.Responder = _ => Response(HttpStatusCode.RequestedRangeNotSatisfiable, [], "video/mp4", "bytes */5000");

        var reply = await MediaAsync("bytes=9000-");

        reply.Should().BeEquivalentTo(new ReplicaMediaReply(416, "video/mp4", [], "bytes */5000"));
    }

    [Fact]
    public async Task 型別不在白名單_改成octet_stream()
    {
        relay.Responder = _ => Response(HttpStatusCode.OK, [1, 2], "text/html");

        (await MediaAsync(null))!.ContentType.Should().Be("application/octet-stream");
    }

    [Theory]
    [InlineData("len")]
    [InlineData("range")]
    [InlineData("range-len")]
    [InlineData("short206")]
    [InlineData("416")]
    [InlineData("404")]
    [InlineData("500")]
    [InlineData("302")]
    public async Task 不合格的回應回null(string kind)
    {
        relay.Responder = _ => kind switch
        {
            "len" => Response(HttpStatusCode.OK, Bytes(100), "image/png", contentLength: 50),
            "range" => Response(HttpStatusCode.PartialContent, Bytes(100), "image/png", "bytes 0-99/*"),
            "range-len" => Response(HttpStatusCode.PartialContent, Bytes(100), "image/png", "bytes 0-199/1000"),
            "short206" => Response(HttpStatusCode.PartialContent, Bytes(100), "image/png", "bytes 0-199/1000", contentLength: 200),
            "416" => Response(HttpStatusCode.RequestedRangeNotSatisfiable, [], "image/png", "bytes 0-1/2"),
            "404" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } },
        };

        (await MediaAsync("bytes=0-99")).Should().BeNull();
    }

    // ── 縮圖 ──

    [Fact]
    public async Task 縮圖_purpose與大小_不帶Range()
    {
        relay.Responder = _ => Response(HttpStatusCode.OK, MediaTestFiles.Png, "image/png");

        var reply = await source.FetchAsync(key, MediaPurposes.Thumb, 320, "bytes=0-1", CancellationToken.None);

        var request = relay.Requests.Single();
        request.Query.Should().Be("?purpose=thumb&s=320");
        request.Header("Range").Should().BeNull();
        reply.Should().BeEquivalentTo(new ReplicaMediaReply(200, "image/png", MediaTestFiles.Png, null));
    }

    [Theory]
    [InlineData("image/gif", 100)]
    [InlineData("video/mp4", 100)]
    [InlineData("text/html", 100)]
    [InlineData("image/png", (512 * 1024) + 1)]
    public async Task 縮圖_不是PNG或JPEG或太大_回null(string contentType, int length)
    {
        relay.Responder = _ => Response(HttpStatusCode.OK, Bytes(length), contentType);

        (await source.FetchAsync(key, MediaPurposes.Thumb, 160, null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task 縮圖_5分鐘內同一張從快取回_不同大小分開_過期再抓_原檔不快取()
    {
        relay.Responder = _ => Response(HttpStatusCode.OK, MediaTestFiles.Png, "image/png");

        (await source.FetchAsync(key, MediaPurposes.Thumb, 320, null, CancellationToken.None))!.Body.Should().Equal(MediaTestFiles.Png);
        (await source.FetchAsync(key, MediaPurposes.Thumb, 320, null, CancellationToken.None))!.Body.Should().Equal(MediaTestFiles.Png);
        relay.Requests.Should().HaveCount(1, "第二次從快取（以前每次重畫都經伺服器向擁有者的電腦重抓）");

        await source.FetchAsync(key, MediaPurposes.Thumb, 160, null, CancellationToken.None);
        relay.Requests.Should().HaveCount(2, "不同大小是不同的縮圖");

        time.Advance(ReplicaThumbCache.Ttl);
        await source.FetchAsync(key, MediaPurposes.Thumb, 320, null, CancellationToken.None);
        relay.Requests.Should().HaveCount(3, "過了 5 分鐘重抓（擁有者可能換了縮圖）");

        relay.Responder = _ => Response(HttpStatusCode.OK, Bytes(100), "image/png");
        await MediaAsync(null);
        await MediaAsync(null);
        relay.Requests.Should().HaveCount(5, "原檔不快取");
    }

    [Fact]
    public async Task 縮圖_不合格的不放進快取()
    {
        relay.Responder = _ => Response(HttpStatusCode.OK, Bytes(100), "image/gif");
        (await source.FetchAsync(key, MediaPurposes.Thumb, 320, null, CancellationToken.None)).Should().BeNull();
        relay.Responder = _ => Response(HttpStatusCode.OK, MediaTestFiles.Png, "image/png");
        (await source.FetchAsync(key, MediaPurposes.Thumb, 320, null, CancellationToken.None))!.Body.Should().Equal(MediaTestFiles.Png);
        relay.Requests.Should().HaveCount(2);
        source.Thumbs.Bytes.Should().Be(MediaTestFiles.Png.Length);
    }

    [Fact]
    public void 縮圖快取_超過上限丟最久沒用的_太大的不放()
    {
        var cache = new ReplicaThumbCache(time, maxBytes: 300);
        cache.Put("a", Bytes(100), "image/png");
        cache.Put("b", Bytes(100), "image/png");
        cache.Put("c", Bytes(100), "image/png");
        cache.TryGet("a", out _, out _).Should().BeTrue("用過一次，變成最近用過");
        cache.Put("d", Bytes(100), "image/png");
        cache.TryGet("b", out _, out _).Should().BeFalse("最久沒用的被丟掉");
        cache.TryGet("a", out _, out _).Should().BeTrue();
        cache.Bytes.Should().Be(300);
        cache.Put("big", Bytes(301), "image/png");
        cache.TryGet("big", out _, out _).Should().BeFalse("比上限還大的不放");
        cache.Put("a", Bytes(50), "image/jpeg");
        cache.TryGet("a", out var body, out var type).Should().BeTrue();
        (body.Length, type).Should().Be((50, "image/jpeg"), "同一個鍵換成新的");
        cache.Bytes.Should().Be(250);
    }

    [Fact]
    public async Task 縮圖_206也不收()
    {
        relay.Responder = _ => Response(HttpStatusCode.PartialContent, MediaTestFiles.Png, "image/png", $"bytes 0-{MediaTestFiles.Png.Length - 1}/{MediaTestFiles.Png.Length}");

        (await source.FetchAsync(key, MediaPurposes.Thumb, 160, null, CancellationToken.None)).Should().BeNull();
    }

    // ── 不送 ──

    [Theory]
    [InlineData("d_x-y", MediaPurposes.Media, null)]
    [InlineData("{key}", MediaPurposes.Thumb, 240)]
    [InlineData("{key}", MediaPurposes.Media, 160)]
    [InlineData("{key}", "file", null)]
    public async Task 鍵或用途不合格_不送(string candidate, string purpose, int? size)
    {
        (await source.FetchAsync(candidate.Replace("{key}", key, StringComparison.Ordinal), purpose, size, null, CancellationToken.None)).Should().BeNull();
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 沒有權杖_不送()
    {
        tokens.Clear();

        (await MediaAsync(null)).Should().BeNull();
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 逾時20秒_回null()
    {
        relay.AsyncResponder = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };

        var pending = MediaAsync(null);
        await WaitUntilAsync(() => relay.Requests.Count == 1);
        time.Advance(ReplicaMediaSource.FetchTimeout - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        pending.IsCompleted.Should().BeFalse("還沒到 20 秒");
        time.Advance(TimeSpan.FromSeconds(1));

        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();
    }

    [Fact]
    public async Task 釋放之後_進行中的取消_之後不送()
    {
        relay.AsyncResponder = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };
        var pending = MediaAsync(null);
        await WaitUntilAsync(() => relay.Requests.Count == 1);

        source.Dispose();

        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();
        (await MediaAsync(null)).Should().BeNull();
        relay.Requests.Should().HaveCount(1);
    }

    // ── 分段下載（電腦狀態面板的檔案傳輸）──

    /// <summary>假的來源檔：照 Range 回 206；<paramref name="cut"/> 回傳要在這次回應的第幾個位元組之後斷線（null＝不斷）</summary>
    private void ServeFile(byte[] file, Func<int, long, int?>? cut = null, Func<int, HttpStatusCode?>? status = null)
    {
        var count = 0;
        relay.Responder = request =>
        {
            var n = count++;
            if (status?.Invoke(n) is { } fail)
                return new HttpResponseMessage(fail) { Content = new ByteArrayContent([]) };
            var range = request.Header("Range")!["bytes=".Length..].Split('-');
            var from = long.Parse(range[0]);
            var to = Math.Min(long.Parse(range[1]), file.Length - 1);
            var part = file[(int)from..(int)(to + 1)];
            HttpContent content = cut?.Invoke(n, from) is { } after
                ? new StreamContent(new BreakingStream(part, after))
                : new ByteArrayContent(part) as HttpContent;
            content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {from}-{to}/{file.Length}");
            content.Headers.ContentLength = part.Length;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        };
    }

    /// <summary>下載到結束；期間一直推進假時鐘（重試的等待用它）</summary>
    private async Task<(PeerDownload? Result, byte[] Bytes)> DownloadPumpingAsync()
    {
        using var target = new MemoryStream();
        var pending = source.DownloadAsync(key, target, null, CancellationToken.None);
        var deadline = Environment.TickCount64 + 30_000;
        while (!pending.IsCompleted && Environment.TickCount64 < deadline)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        return (result, target.ToArray());
    }

    [Fact]
    public async Task 分段下載_中途連線被切斷_從斷掉的位置接著下載_內容一致()
    {
        // 2026-11-10：來源電腦在下載途中換版重開，正在轉送的那一段被切斷，以前整個檔放棄
        var file = Bytes(20 * Mb);
        ServeFile(file, cut: (n, _) => n == 1 ? 3 * Mb : null);

        var (result, bytes) = await DownloadPumpingAsync();

        result.Should().NotBeNull();
        result!.Total.Should().Be(file.Length);
        bytes.Should().Equal(file);
        relay.Requests.Select(r => r.Header("Range")).Should().Equal(
            $"bytes=0-{8 * Mb - 1}", $"bytes={8 * Mb}-{16 * Mb - 1}", $"bytes={11 * Mb}-{19 * Mb - 1}", $"bytes={19 * Mb}-{20 * Mb - 1}");
    }

    [Fact]
    public async Task 分段下載_來源離線一陣子_503之後恢復_照樣下載完()
    {
        var file = Bytes(10 * Mb);
        ServeFile(file, status: n => n is >= 1 and <= 6 ? HttpStatusCode.ServiceUnavailable : null);

        var (result, bytes) = await DownloadPumpingAsync();

        result.Should().NotBeNull();
        bytes.Should().Equal(file);
    }

    [Fact]
    public async Task 分段下載_一直503超過三分鐘_放棄()
    {
        ServeFile(Bytes(Mb), status: _ => HttpStatusCode.ServiceUnavailable);

        var (result, _) = await DownloadPumpingAsync();

        result.Should().BeNull();
        relay.Requests.Count.Should().BeGreaterThan(5, "期間照間隔重試");
    }

    [Fact]
    public async Task 分段下載_被拒_不重試()
    {
        ServeFile(Bytes(Mb), status: _ => HttpStatusCode.NotFound);

        var (result, _) = await DownloadPumpingAsync();

        result.Should().BeNull();
        relay.Requests.Should().HaveCount(1);
    }

    /// <summary>讀到 <c>after</c> 個位元組之後丟 IOException（連線被切斷）</summary>
    private sealed class BreakingStream(byte[] data, int after) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= after)
                throw new IOException("遠端主機已強制關閉一個現存的連線。");
            var n = Math.Min(count, after - position);
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("等不到條件成立");
            await Task.Delay(5);
        }
    }
}
