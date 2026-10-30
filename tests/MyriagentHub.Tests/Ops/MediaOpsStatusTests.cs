using System.Net;
using System.Text.Json;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Tests.Media;

namespace MyriagentHub.Tests.Ops;

/// <summary>ops/status 與 /readyz 的 <c>blobs</c> 區塊、告警 <c>blob_quota</c>／<c>blob_refuse</c>（WP4.7 計畫 D42）</summary>
public sealed class MediaOpsStatusTests
{
    private static readonly AlertInputs Quiet = new("none", false, true, false, true, 100L * 1024 * 1024 * 1024, 100, 400L * 1024 * 1024, false, false, 0, 20);

    [Fact]
    public void 告警_告警門檻是blob_quota_拒收門檻只報blob_refuse()
    {
        HealthRules.Alerts(Quiet).Should().BeEmpty("預設是 Ok：舊的呼叫端不用改");
        HealthRules.Alerts(Quiet with { BlobLevel = BlobQuotaLevel.Ok }).Should().BeEmpty();
        HealthRules.Alerts(Quiet with { BlobLevel = BlobQuotaLevel.Warn }).Should().Equal("blob_quota");
        HealthRules.Alerts(Quiet with { BlobLevel = BlobQuotaLevel.Refuse }).Should().Equal("blob_refuse");
    }

    [Fact]
    public void 區塊的欄位名稱與等級的值()
    {
        var json = JsonSerializer.SerializeToElement(MediaOpsStatus.Block(3, 900, 1000, BlobQuotaLevel.Warn, 12, 34, 5, 6));
        json.EnumerateObject().Select(p => p.Name).Should()
            .Equal("count", "bytes", "quotaBytes", "level", "thumbCacheBytes", "uploadsPending", "relaysActive", "relayTimeouts");
        json.GetProperty("level").GetString().Should().Be("warn");
        MediaOpsStatus.LevelName(BlobQuotaLevel.Ok).Should().Be("ok");
        MediaOpsStatus.LevelName(BlobQuotaLevel.Refuse).Should().Be("refuse");
        FluentActions.Invoking(() => MediaOpsStatus.LevelName((BlobQuotaLevel)99)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ops_status與readyz都有blobs區塊_用量80百分比告警blob_quota_磁碟不足告警blob_refuse()
    {
        await using var host = await MediaHost.StartAsync(new Dictionary<string, string?>
        {
            ["Hub:Media:QuotaBytes"] = "1000",
            ["Hub:Media:AlertPercent"] = "80",
            ["Hub:Media:RefusePercent"] = "95",
        });
        await MediaSeed.StoreBlobAsync(host, MediaBytes.Random(800), BlobKinds.ItemBody);
        using var owner = await host.OwnerAsync();

        var status = await JsonAsync(owner, "/api/v1/ops/status");
        var blobs = status.GetProperty("blobs");
        blobs.GetProperty("count").GetInt64().Should().Be(1);
        blobs.GetProperty("bytes").GetInt64().Should().Be(800);
        blobs.GetProperty("quotaBytes").GetInt64().Should().Be(1000);
        blobs.GetProperty("level").GetString().Should().Be("warn");
        blobs.GetProperty("thumbCacheBytes").GetInt64().Should().Be(0);
        blobs.GetProperty("uploadsPending").GetInt64().Should().Be(0);
        blobs.GetProperty("relaysActive").GetInt32().Should().Be(0);
        blobs.GetProperty("relayTimeouts").GetInt64().Should().Be(0);
        Alerts(status).Should().Contain("blob_quota").And.NotContain("blob_refuse");

        // /readyz：本機迴路不必登入（部署腳本與本機監看）
        using (var local = host.Client())
        {
            var ready = await JsonAsync(local, "/readyz");
            ready.GetProperty("blobs").GetProperty("level").GetString().Should().Be("warn");
        }

        host.Disk.Free = 10L << 30;
        var low = await JsonAsync(owner, "/api/v1/ops/status");
        low.GetProperty("blobs").GetProperty("level").GetString().Should().Be("refuse");
        Alerts(low).Should().Contain("blob_refuse").And.NotContain("blob_quota");
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static List<string> Alerts(JsonElement status) => status.GetProperty("alerts").EnumerateArray().Select(a => a.GetString()!).ToList();
}
