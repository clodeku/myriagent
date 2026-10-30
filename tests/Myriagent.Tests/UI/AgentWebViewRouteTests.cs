using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>
/// AgentWebView 的複本媒體路由（WP4.7 計畫步 12，D38）：<c>/m/{鍵}</c>、<c>/t/{鍵}?s=</c> 的比對、只把登記過的鍵交給來源、來源的回覆變成 WebView2 的回應
/// （no-store、nosniff、206 的 Content-Range）。本機的 <c>/media</c>、<c>/thumb</c>、<c>/open</c> 不經這裡（既有的測試在 WebViewRulesTests、MediaEndpoint 類）
/// </summary>
public sealed class AgentWebViewRouteTests
{
    private static readonly string Owner = SyncIds.NewDeviceId();

    private static string Key() => MediaKeyRules.Format(Owner, MediaTestFiles.NewId());

    private static Uri Url(string pathAndQuery) => new("https://myriagent.local" + pathAndQuery);

    private static Dictionary<string, string> HeadersOf(string headers)
        => headers.Split("\r\n").Select(line => line.Split(": ", 2)).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

    private static async Task<byte[]> BodyAsync(AgentWebView.PreparedResponse response)
    {
        using var buffer = new MemoryStream();
        await response.Content!.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // ── 比對 ──

    [Fact]
    public void 比對_m與t()
    {
        var key = Key();
        ReplicaMediaRouteRules.Match(Url("/m/" + key)).Should().Be(new ReplicaRoute(ReplicaRouteKind.Valid, key, MediaPurposes.Media));
        ReplicaMediaRouteRules.Match(Url("/t/" + key + "?s=160")).Should().Be(new ReplicaRoute(ReplicaRouteKind.Valid, key, MediaPurposes.Thumb, 160));
        ReplicaMediaRouteRules.Match(Url("/t/" + key + "?s=320")).Should().Be(new ReplicaRoute(ReplicaRouteKind.Valid, key, MediaPurposes.Thumb, 320));
    }

    [Theory]
    [InlineData("/media?path=C%3A%5Ca.png")]
    [InlineData("/thumb?path=C%3A%5Ca.png&size=160")]
    [InlineData("/open")]
    [InlineData("/index.html")]
    [InlineData("/app.js")]
    [InlineData("/")]
    [InlineData("/M/{key}")]
    [InlineData("/mx/{key}")]
    [InlineData("/m")]
    public void 不是複本的路由_照原本的(string path)
        => ReplicaMediaRouteRules.Match(Url(path.Replace("{key}", Key(), StringComparison.Ordinal))).Kind.Should().Be(ReplicaRouteKind.None);

    [Theory]
    [InlineData("/m/")]
    [InlineData("/m/abc")]
    [InlineData("/m/{key}/x")]
    [InlineData("/m/{key}x")]
    [InlineData("/m/{lower}")]
    [InlineData("/m/..%2F..%2Fapp.js")]
    [InlineData("/t/{key}")]
    [InlineData("/t/{key}?s=240")]
    [InlineData("/t/{key}?s=")]
    [InlineData("/t/{key}?s=0160")]
    [InlineData("/t/")]
    public void 鍵或大小不合格_Invalid(string path)
    {
        var key = Key();
        var url = Url(path.Replace("{key}", key, StringComparison.Ordinal).Replace("{lower}", key.ToLowerInvariant(), StringComparison.Ordinal));
        ReplicaMediaRouteRules.Match(url).Kind.Should().Be(ReplicaRouteKind.Invalid);
    }

    // ── 路由到來源 ──

    [Fact]
    public async Task 複本的m帶Range()
    {
        var key = Key();
        var body = new byte[] { 1, 2, 3, 4 };
        var source = new FakeReplicaMediaSource { Reply = _ => new ReplicaMediaReply(206, "video/mp4", body, "bytes 100-103/5000") };
        var asked = new List<(string, string)>();

        var work = AgentWebView.RouteReplica(ReplicaMediaRouteRules.Match(Url("/m/" + key)), "bytes=100-103", (k, p) =>
        {
            asked.Add((k, p));
            return source;
        });
        var response = await work!();

        asked.Should().Equal((key, MediaPurposes.Media));
        source.Calls.Should().Equal(new ReplicaMediaCall(key, MediaPurposes.Media, null, "bytes=100-103"));
        response!.StatusCode.Should().Be(206);
        response.ReasonPhrase.Should().Be("Partial Content");
        var headers = HeadersOf(response.Headers);
        headers.Should().Equal(new Dictionary<string, string>
        {
            ["Content-Type"] = "video/mp4",
            ["Content-Length"] = "4",
            ["Content-Range"] = "bytes 100-103/5000",
            ["Accept-Ranges"] = "bytes",
            ["Cache-Control"] = "no-store",
            ["X-Content-Type-Options"] = "nosniff",
        });
        (await BodyAsync(response)).Should().Equal(body);
    }

    [Fact]
    public async Task 縮圖不帶Range_回200沒有Content_Range與Accept_Ranges()
    {
        var key = Key();
        var source = new FakeReplicaMediaSource { Reply = _ => new ReplicaMediaReply(200, "image/png", MediaTestFiles.Png, null) };

        var response = await AgentWebView.RouteReplica(ReplicaMediaRouteRules.Match(Url("/t/" + key + "?s=160")), "bytes=0-1", (_, _) => source)!();

        source.Calls.Should().Equal(new ReplicaMediaCall(key, MediaPurposes.Thumb, 160, null));
        response!.StatusCode.Should().Be(200);
        HeadersOf(response.Headers).Should().Equal(new Dictionary<string, string>
        {
            ["Content-Type"] = "image/png",
            ["Content-Length"] = MediaTestFiles.Png.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cache-Control"] = "no-store",
            ["X-Content-Type-Options"] = "nosniff",
        });
    }

    [Theory]
    [InlineData("/m/abc")]
    [InlineData("/t/{key}?s=999")]
    [InlineData("/media?path=C%3A%5Ca.png")]
    public void 鍵不合格回404且來源沒被呼叫(string path)
    {
        var source = new FakeReplicaMediaSource();
        var asked = 0;

        var work = AgentWebView.RouteReplica(ReplicaMediaRouteRules.Match(Url(path.Replace("{key}", Key(), StringComparison.Ordinal))), "bytes=0-1", (_, _) =>
        {
            asked++;
            return source;
        });

        work.Should().BeNull("404");
        asked.Should().Be(0, "中樞也不問");
        source.Calls.Should().BeEmpty();
    }

    [Fact]
    public void 中樞沒有來源或沒登記_回404()
        => AgentWebView.RouteReplica(ReplicaMediaRouteRules.Match(Url("/m/" + Key())), null, (_, _) => null).Should().BeNull();

    [Fact]
    public async Task 來源回null_回404()
    {
        var source = new FakeReplicaMediaSource();
        var response = await AgentWebView.RouteReplica(ReplicaMediaRouteRules.Match(Url("/m/" + Key())), null, (_, _) => source)!();
        response.Should().BeNull();
        source.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task 範圍不能滿足_416帶Content_Range()
    {
        var source = new FakeReplicaMediaSource { Reply = _ => new ReplicaMediaReply(416, "video/mp4", [], "bytes */5000") };
        var response = await AgentWebView.RouteReplica(ReplicaMediaRouteRules.Match(Url("/m/" + Key())), "bytes=9000-", (_, _) => source)!();

        response!.StatusCode.Should().Be(416);
        response.ReasonPhrase.Should().Be("Range Not Satisfiable");
        HeadersOf(response.Headers)["Content-Range"].Should().Be("bytes */5000");
        HeadersOf(response.Headers)["Content-Length"].Should().Be("0");
    }

    // ── 標頭 ──

    [Theory]
    [InlineData(500, "image/png", null)]
    [InlineData(302, "image/png", null)]
    [InlineData(206, "video/mp4", null)]
    [InlineData(206, "video/mp4", "")]
    [InlineData(416, "video/mp4", null)]
    [InlineData(200, "image/png\r\nSet-Cookie: x=1", null)]
    [InlineData(206, "video/mp4", "bytes 0-1/2\r\nX-Evil: 1")]
    [InlineData(200, "image/png\nX: 1", null)]
    public void 狀態碼或標頭不合格_不建回應(int status, string contentType, string? contentRange)
        => ReplicaMediaRouteRules.Headers(new ReplicaMediaReply(status, contentType, [1], contentRange), isMedia: true).Should().BeNull();

    [Fact]
    public async Task 標頭不合格時PrepareReplicaAsync回null()
    {
        var source = new FakeReplicaMediaSource { Reply = _ => new ReplicaMediaReply(200, "image/png\r\nX: 1", [1], null) };
        (await AgentWebView.PrepareReplicaAsync(source, Key(), MediaPurposes.Media, null, null, CancellationToken.None)).Should().BeNull();
    }
}
