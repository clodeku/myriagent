using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Update;

/// <summary>
/// app 自我更新 U3 的版本來源（docs/sync-protocol.md 第 15 節）：latest 的 204／200、套件的 Range／If-Range 續傳、416 是內容錯誤、
/// 503 帶 Retry-After、沒有連上伺服器是「等待連上」；退避與「伺服器說有新版」的規則
/// </summary>
public sealed class HubReleaseSourceTests : IDisposable
{
    private const string Release = "0.3.120-0123abc";

    private static readonly Uri Server = new("https://agent.example.test");

    private readonly TempDirectory temp = new("hub-release");

    public void Dispose() => temp.Dispose();

    /// <summary>記下每個請求、依序回應（本文是位元組；沒有預備的回應就丟例外）</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> script = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) => script.Enqueue(respond);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (script.Count == 0)
                throw new InvalidOperationException("沒有預備的回應：" + request.RequestUri);
            return Task.FromResult(script.Dequeue()(request));
        }
    }

    private static (DeviceHttpClient Client, FakeHandler Handler) Client()
    {
        var handler = new FakeHandler();
        var tokens = new AccessTokenHolder();
        tokens.Set("cha_" + new string('a', 43), 0);
        var time = TimeProvider.System;
        return (new DeviceHttpClient(Server, Server, devTrustEnv: false, tokens, _ => Task.FromResult(false), time, handler), handler);
    }

    private static byte[] Manifest(string zipSha256) =>
        ReleaseFixtures.ManifestBytes(Release, [ReleaseFixtures.File("Myriagent.exe", "exe")], zipSha256);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(HttpStatusCode status, byte[] body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        configure?.Invoke(response);
        return response;
    }

    private static string LatestJson(byte[] manifest, long zipBytes = 1234) => new JObject
    {
        ["release"] = Release,
        ["version"] = "0.3.120",
        ["build"] = "0123abc",
        ["manifest"] = Convert.ToBase64String(manifest),
        ["sig"] = new JObject { ["kid"] = "7a411982ac841d16", ["sig"] = "AAAA" },
        ["zipBytes"] = zipBytes,
    }.ToString();

    [Fact]
    public async Task latest_204是沒有可用的發佈_200組回manifest原文與簽章檔()
    {
        var (client, handler) = Client();
        using var _ = client;
        var source = new HubReleaseSource(() => client);
        var manifest = Manifest(new string('c', 64));
        handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.Respond(_ => Json(HttpStatusCode.OK, LatestJson(manifest)));

        (await source.GetLatestAsync(CancellationToken.None)).Should().BeNull();
        var offer = await source.GetLatestAsync(CancellationToken.None);

        offer!.Release.Should().Be(Release);
        offer.ManifestBytes.Should().Equal(manifest, "簽章的對象是原文位元組");
        ReleaseManifestRules.TryParseSignature(offer.SignatureJson, out var signature).Should().BeTrue();
        signature!.Kid.Should().Be("7a411982ac841d16");
        offer.ZipBytes.Should().Be(1234);
        handler.Requests.Should().OnlyContain(r => r.RequestUri!.AbsolutePath == AppReleaseRoutes.Latest);
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
    }

    [Fact]
    public async Task 套件_已有部分內容帶Range與IfRange_206接著寫_200從頭寫()
    {
        var (client, handler) = Client();
        using var _ = client;
        var source = new HubReleaseSource(() => client);
        var zipSha = new string('d', 64);
        var offer = new ReleaseOffer(Release, Manifest(zipSha), "{}");
        var destination = temp.Combine("a.zip.partial");
        File.WriteAllBytes(destination, [1, 2, 3]);
        handler.Respond(_ => Bytes(HttpStatusCode.PartialContent, [4, 5], r => r.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 4, 5)));

        await source.DownloadZipAsync(offer, "myriagent-" + Release + ".zip", destination, null, CancellationToken.None);

        File.ReadAllBytes(destination).Should().Equal(1, 2, 3, 4, 5);
        var request = handler.Requests.Single();
        request.RequestUri!.AbsolutePath.Should().Be(AppReleaseRoutes.Package(Release));
        request.Headers.Range!.ToString().Should().Be("bytes=3-");
        request.Headers.GetValues("If-Range").Single().Should().Be("\"" + zipSha + "\"");

        // 伺服器的套件換了（If-Range 不符）：回整份 200，從頭寫
        handler.Respond(_ => Bytes(HttpStatusCode.OK, [9, 9]));
        await source.DownloadZipAsync(offer, "x", destination, null, CancellationToken.None);
        File.ReadAllBytes(destination).Should().Equal(9, 9);
    }

    /// <summary>
    /// 2026-10-27：上次已下載完整（之後的解壓失敗），每次檢查都從結尾續傳、伺服器回 416，被當成內容錯誤刪掉重下載。
    /// 已經和套件一樣大就不再下載（交給驗證；雜湊不符照內容錯誤重下載），比套件大就刪掉從頭下載
    /// </summary>
    [Fact]
    public async Task 套件_已經下載完整就不再請求_比套件大就刪掉從頭下載()
    {
        var (client, handler) = Client();
        using var _ = client;
        var source = new HubReleaseSource(() => client);
        var offer = new ReleaseOffer(Release, Manifest(new string('d', 64)), "{}") { ZipBytes = 3 };
        var destination = temp.Combine("full.zip.partial");
        File.WriteAllBytes(destination, [1, 2, 3]);
        var reported = new List<long>();

        await source.DownloadZipAsync(offer, "x", destination, new Progress(reported), CancellationToken.None);

        handler.Requests.Should().BeEmpty("已經完整，不必再問伺服器");
        File.ReadAllBytes(destination).Should().Equal(1, 2, 3);
        reported.Should().Equal(3);

        File.WriteAllBytes(destination, [1, 2, 3, 4]);
        handler.Respond(_ => Bytes(HttpStatusCode.OK, [7, 8, 9]));
        await source.DownloadZipAsync(offer, "x", destination, null, CancellationToken.None);
        handler.Requests.Single().Headers.Range.Should().BeNull("比套件大：刪掉從頭下載，不帶 Range");
        File.ReadAllBytes(destination).Should().Equal(7, 8, 9);
    }

    /// <summary>同步回報的進度（Progress&lt;T&gt; 會排到執行緒集區）</summary>
    private sealed class Progress(List<long> into) : IProgress<long>
    {
        public void Report(long value) => into.Add(value);
    }

    [Theory]
    [InlineData(0, 100L, nameof(ReleaseExistingPlan.Download))]
    [InlineData(50, 100L, nameof(ReleaseExistingPlan.Download))]
    [InlineData(100, 100L, nameof(ReleaseExistingPlan.Complete))]
    [InlineData(101, 100L, nameof(ReleaseExistingPlan.Discard))]
    [InlineData(100, null, nameof(ReleaseExistingPlan.Download))]
    [InlineData(100, 0L, nameof(ReleaseExistingPlan.Download))]
    public void 規則_已有部分下載時(long existing, long? zipBytes, string expected) =>
        HubReleaseRules.PlanExisting(existing, zipBytes).ToString().Should().Be(expected);

    [Fact]
    public async Task 套件_416與起點對不上是內容錯誤_503帶RetryAfter_410是暫時錯誤()
    {
        var (client, handler) = Client();
        using var _ = client;
        var source = new HubReleaseSource(() => client);
        var offer = new ReleaseOffer(Release, Manifest(new string('e', 64)), "{}");
        var destination = temp.Combine("b.zip.partial");
        File.WriteAllBytes(destination, [1, 2, 3]);

        handler.Respond(_ => Bytes(HttpStatusCode.RequestedRangeNotSatisfiable, []));
        await FluentActions.Invoking(() => source.DownloadZipAsync(offer, "x", destination, null, CancellationToken.None)).Should().ThrowAsync<InvalidDataException>();

        handler.Respond(_ => Bytes(HttpStatusCode.PartialContent, [4], r => r.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 1, 5)));
        await FluentActions.Invoking(() => source.DownloadZipAsync(offer, "x", destination, null, CancellationToken.None)).Should().ThrowAsync<InvalidDataException>();

        handler.Respond(_ => Json(HttpStatusCode.ServiceUnavailable, """{"error":"draining"}""").Also(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30))));
        var unavailable = await FluentActions.Invoking(() => source.DownloadZipAsync(offer, "x", destination, null, CancellationToken.None))
            .Should().ThrowAsync<ReleaseSourceUnavailableException>();
        unavailable.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
        unavailable.Which.Offline.Should().BeFalse();

        handler.Respond(_ => Json(HttpStatusCode.Gone, """{"error":"gone"}"""));
        (await FluentActions.Invoking(() => source.DownloadZipAsync(offer, "x", destination, null, CancellationToken.None))
            .Should().ThrowAsync<ReleaseSourceUnavailableException>()).Which.Message.Should().Contain("撤回");
        File.ReadAllBytes(destination).Should().Equal(new byte[] { 1, 2, 3 }, "暫時的錯誤保留部分下載");
    }

    [Fact]
    public async Task 沒有連上伺服器_是等待連上不是失敗()
    {
        var source = new HubReleaseSource(() => null);

        var error = await FluentActions.Invoking(() => source.GetLatestAsync(CancellationToken.None)).Should().ThrowAsync<ReleaseSourceUnavailableException>();
        error.Which.Offline.Should().BeTrue();
    }

    [Fact]
    public void 規則_latest的回應不合格丟InvalidDataException()
    {
        var manifest = Manifest(new string('c', 64));
        HubReleaseRules.ParseLatest(Encoding.UTF8.GetBytes(LatestJson(manifest))).Release.Should().Be(Release);
        foreach (var bad in new[]
        {
            "not json",
            """{"release":"bad name","manifest":"AAAA","sig":{"kid":"x","sig":"y"}}""",
            """{"release":"0.3.120-0123abc","manifest":"%%%","sig":{"kid":"x","sig":"y"}}""",
            """{"release":"0.3.120-0123abc","manifest":"AAAA"}""",
        })
        {
            FluentActions.Invoking(() => HubReleaseRules.ParseLatest(Encoding.UTF8.GetBytes(bad))).Should().Throw<InvalidDataException>(bad);
        }
    }

    [Theory]
    [InlineData(200, 0, null, nameof(ReleaseWritePlan.Restart))]
    [InlineData(200, 10, null, nameof(ReleaseWritePlan.Restart))]
    [InlineData(206, 10, 10L, nameof(ReleaseWritePlan.Append))]
    [InlineData(206, 10, 0L, nameof(ReleaseWritePlan.ContentError))]
    [InlineData(206, 0, 0L, nameof(ReleaseWritePlan.ContentError))]
    [InlineData(416, 10, null, nameof(ReleaseWritePlan.ContentError))]
    [InlineData(503, 10, null, nameof(ReleaseWritePlan.Unavailable))]
    [InlineData(410, 0, null, nameof(ReleaseWritePlan.Unavailable))]
    public void 規則_怎麼寫套件(int status, long existing, long? start, string expected) =>
        HubReleaseRules.PlanWrite(status, existing, start).ToString().Should().Be(expected);

    [Fact]
    public void 規則_退避照RetryAfter或1_2_4分鐘加倍_最多60分鐘()
    {
        AppUpdateRetryRules.NextDelay(1, null).Should().Be(TimeSpan.FromMinutes(1));
        AppUpdateRetryRules.NextDelay(2, null).Should().Be(TimeSpan.FromMinutes(2));
        AppUpdateRetryRules.NextDelay(3, null).Should().Be(TimeSpan.FromMinutes(4));
        AppUpdateRetryRules.NextDelay(7, null).Should().Be(TimeSpan.FromMinutes(60));
        AppUpdateRetryRules.NextDelay(50, null).Should().Be(TimeSpan.FromMinutes(60));
        AppUpdateRetryRules.NextDelay(5, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
        AppUpdateRetryRules.NextDelay(1, TimeSpan.Zero).Should().Be(AppUpdateRetryRules.MinDelay);
        AppUpdateRetryRules.NextDelay(1, TimeSpan.FromHours(5)).Should().Be(AppUpdateRetryRules.MaxDelay);
    }

    [Fact]
    public void 規則_伺服器說有新版_比這台新而且10分鐘一次才提早檢查()
    {
        ReleaseIdentity.TryParseRelease("0.3.100-aaaaaaa", out var current).Should().BeTrue();
        AppUpdateRetryRules.ShouldCheckOnAnnounce(Release, current!, null, null, 0).Should().BeTrue();
        AppUpdateRetryRules.ShouldCheckOnAnnounce(Release, current!, null, 0, 9 * 60_000).Should().BeFalse("10 分鐘內不再提早");
        AppUpdateRetryRules.ShouldCheckOnAnnounce(Release, current!, null, 0, 10 * 60_000).Should().BeTrue();
        AppUpdateRetryRules.ShouldCheckOnAnnounce("0.3.100-bbbbbbb", current!, null, null, 0).Should().BeFalse("一樣新不檢查");
        AppUpdateRetryRules.ShouldCheckOnAnnounce("0.2.900-ccccccc", current!, null, null, 0).Should().BeFalse("比這台舊");
        AppUpdateRetryRules.ShouldCheckOnAnnounce(Release, current!, Release, null, 0).Should().BeFalse("已經備妥");
        AppUpdateRetryRules.ShouldCheckOnAnnounce("bad", current!, null, null, 0).Should().BeFalse();
        AppUpdateRetryRules.ShouldCheckOnAnnounce(Release, new ReleaseIdentity(current!.Version, ReleaseIdentity.DevBuild), null, null, 0).Should().BeFalse("開發版不自動更新");
    }
}

internal static class HttpResponseMessageTestExtensions
{
    public static HttpResponseMessage Also(this HttpResponseMessage response, Action<HttpResponseMessage> configure)
    {
        configure(response);
        return response;
    }
}
