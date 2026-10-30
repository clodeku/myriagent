using System.Net;
using System.Text;
using System.Text.Json;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 裝置的 blob 端點（WP4.7 計畫 E5、E6；D13、D15）：真的 Kestrel（本文上限只有它才有），存取權杖 cha_，項目 blob 的第二道 DLP
/// </summary>
public sealed class BlobEndpointsTests
{
    private const string Path = "/api/v1/device/blobs/";

    [Fact]
    public async Task E5_新存201_已存在200不讀本文_HEAD()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        var data = Encoding.UTF8.GetBytes("定稿的長回覆");
        var sha = MediaBytes.Sha(data);

        using (var response = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + sha, cha, MediaHost.Bytes(data))))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("sha256").GetString().Should().Be(sha);
            body.GetProperty("length").GetInt64().Should().Be(data.Length);
            body.GetProperty("existed").GetBoolean().Should().BeFalse();
        }

        Rows(host).Should().ContainSingle().Which.Should().Be((sha, (long)data.Length, BlobKinds.ItemBody));

        using (var again = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + sha, cha, MediaHost.Bytes(data))))
        {
            again.StatusCode.Should().Be(HttpStatusCode.OK, "已存在（不讀本文、不重寫：BlobStoreTests 以會丟例外的串流證明）");
            JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("existed").GetBoolean().Should().BeTrue();
        }

        using (var head = await client.SendAsync(MediaHost.Device(HttpMethod.Head, Path + sha, cha)))
        {
            head.StatusCode.Should().Be(HttpStatusCode.OK);
            head.Content.Headers.ContentLength.Should().Be(data.Length);
        }

        using (var missing = await client.SendAsync(MediaHost.Device(HttpMethod.Head, Path + MediaBytes.Sha([1, 2, 3]), cha)))
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Rows(host).Should().HaveCount(1);
    }

    [Fact]
    public async Task E5_有列沒檔時HEAD回404_PUT把檔補回來()
    {
        // 合併前審查 DI-2：還原到沒有引用它的備份之後（或檔被隔離、手動刪掉）列在檔不在；HEAD 只看列時回 200，裝置不送 PUT，項目引用沒有內容的 blob
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        var data = Encoding.UTF8.GetBytes("還原之後檔不見了的項目 blob");
        var sha = MediaBytes.Sha(data);
        using (var put = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + sha, cha, MediaHost.Bytes(data))))
            put.StatusCode.Should().Be(HttpStatusCode.Created);
        var store = host.Services.GetRequiredService<BlobStore>();
        File.Delete(store.PathOf(sha));

        using (var head = await client.SendAsync(MediaHost.Device(HttpMethod.Head, Path + sha, cha)))
            head.StatusCode.Should().Be(HttpStatusCode.NotFound, "列在、檔不在：要裝置重傳");
        using (var again = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + sha, cha, MediaHost.Bytes(data))))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Created, "有列沒檔：收下內容、把檔補回來");
            JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("existed").GetBoolean().Should().BeFalse();
        }

        File.ReadAllBytes(store.PathOf(sha)).Should().Equal(data);
        using (var head = await client.SendAsync(MediaHost.Device(HttpMethod.Head, Path + sha, cha)))
        {
            head.StatusCode.Should().Be(HttpStatusCode.OK);
            head.Content.Headers.ContentLength.Should().Be(data.Length);
        }

        using (var get = await client.SendAsync(Ranged(sha, cha, null)))
        {
            get.StatusCode.Should().Be(HttpStatusCode.OK);
            (await get.Content.ReadAsByteArrayAsync()).Should().Equal(data);
        }

        Rows(host).Should().ContainSingle().Which.Should().Be((sha, (long)data.Length, BlobKinds.ItemBody));
        store.ReservedBytes.Should().Be(0);
    }

    [Fact]
    public async Task E5_DLP命中回422_什麼都不留()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        var data = Encoding.UTF8.GetBytes("工具輸出：" + FakeSecrets.GitHubStyle() + "\n第二行");

        using var response = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + MediaBytes.Sha(data), cha, MediaHost.Bytes(data)));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await MediaHost.ErrorAsync(response)).Should().Be("dlp");
        AssertNothingStored(host);
    }

    [Fact]
    public async Task E5_含path鍵的JSON陣列文字照樣收()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        var data = Encoding.UTF8.GetBytes("[{\"path\":\"a\"}]");

        using var response = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + MediaBytes.Sha(data), cha, MediaHost.Bytes(data)));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "禁止的鍵交給讀取端（TryReadItem 會重跑形狀檢查）");
    }

    [Fact]
    public async Task E5_不是UTF8回422invalid_雜湊不符回400()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        byte[] notUtf8 = [0xC3, 0x28];

        using (var response = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + MediaBytes.Sha(notUtf8), cha, MediaHost.Bytes(notUtf8))))
        {
            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await MediaHost.ErrorAsync(response)).Should().Be("invalid");
        }

        var data = Encoding.UTF8.GetBytes("真的內容");
        using (var mismatch = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + MediaBytes.Sha(Encoding.UTF8.GetBytes("別的內容")), cha, MediaHost.Bytes(data))))
        {
            mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await MediaHost.ErrorAsync(mismatch)).Should().Be("mismatch");
        }

        AssertNothingStored(host);
    }

    [Fact]
    public async Task E5_Kestrel上限_1MB加1成功_宣告4MB加1回413不讀本文_沒宣告長度411()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();

        var big = MediaBytes.Text(1024 * 1024 + 1);
        using (var response = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + MediaBytes.Sha(big), cha, MediaHost.Bytes(big))))
            response.StatusCode.Should().Be(HttpStatusCode.Created, "這個端點自己放寬到 4 MB");

        const long declared = 4L * 1024 * 1024 + 1;
        using (var tooLarge = await client.SendAsync(MediaHost.Unread(MediaHost.Device(HttpMethod.Put, Path + MediaBytes.Sha([9]), cha), declared)))
        {
            tooLarge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, "宣告超過 4 MB：讀本文之前就回（內容一讀就丟例外）");
            (await MediaHost.ErrorAsync(tooLarge)).Should().Be("too_large");
        }

        var chunked = new StreamContent(new ScriptedStream(Encoding.UTF8.GetBytes("沒有宣告長度")));
        using (var noLength = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + MediaBytes.Sha([8]), cha, chunked)))
        {
            noLength.StatusCode.Should().Be(HttpStatusCode.LengthRequired);
            (await MediaHost.ErrorAsync(noLength)).Should().Be("length_required");
        }

        Rows(host).Should().HaveCount(1);
    }

    [Fact]
    public async Task E5_只收存取權杖()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, _) = await host.DeviceAsync();
        using var d = device;
        using var anonymous = host.Client();
        var data = Encoding.UTF8.GetBytes("內容");
        var sha = MediaBytes.Sha(data);

        using (var chd = await anonymous.SendAsync(MediaHost.Device(HttpMethod.Put, Path + sha, device.Token, MediaHost.Bytes(data))))
            chd.StatusCode.Should().Be(HttpStatusCode.Forbidden, "長效的 chd_ 認證得過、但 DeviceAccess 只收 cha_");
        using (var none = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Put, Path + sha) { Content = MediaHost.Bytes(data) }))
            none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var owner = await host.OwnerAsync();
        using (var cookie = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + sha, MediaHost.Bytes(data))))
            cookie.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "擁有者的 cookie 不行");
        using (var get = await owner.GetAsync(Path + sha))
            get.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        AssertNothingStored(host);
    }

    [Fact]
    public async Task E5_磁碟可用空間低於下限回507()
    {
        await using var host = await MediaHost.StartAsync(freeBytes: 39L << 30);
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        var data = Encoding.UTF8.GetBytes("內容");

        using var response = await client.SendAsync(MediaHost.Device(HttpMethod.Put, Path + MediaBytes.Sha(data), cha, MediaHost.Bytes(data)));

        response.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);
        (await MediaHost.ErrorAsync(response)).Should().Be("insufficient_storage");
        AssertNothingStored(host);
    }

    [Fact]
    public async Task E6_Range()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        var data = Enumerable.Range(0, 10).Select(i => (byte)(0x30 + i)).ToArray();
        var sha = MediaBytes.Sha(data);
        (await host.Services.GetRequiredService<IBlobStore>().PutAsync(sha, data.Length, BlobKinds.Upload, null, new MemoryStream(data), CancellationToken.None)).Should().BeTrue();

        using (var partial = await client.SendAsync(Ranged(sha, cha, "bytes=2-5")))
        {
            partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            partial.Content.Headers.ContentRange!.ToString().Should().Be("bytes 2-5/10");
            (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(data[2..6]);
            partial.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            partial.Headers.CacheControl!.NoStore.Should().BeTrue();
        }

        using (var suffix = await client.SendAsync(Ranged(sha, cha, "bytes=-3")))
        {
            suffix.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            suffix.Content.Headers.ContentRange!.ToString().Should().Be("bytes 7-9/10");
            (await suffix.Content.ReadAsByteArrayAsync()).Should().Equal(data[7..]);
        }

        using (var unsatisfiable = await client.SendAsync(Ranged(sha, cha, "bytes=10-")))
        {
            unsatisfiable.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
            unsatisfiable.Content.Headers.ContentRange!.ToString().Should().Be("bytes */10");
        }

        using (var full = await client.SendAsync(Ranged(sha, cha, null)))
        {
            full.StatusCode.Should().Be(HttpStatusCode.OK);
            full.Headers.AcceptRanges.Should().Contain("bytes");
            (await full.Content.ReadAsByteArrayAsync()).Should().Equal(data);
        }

        using (var multi = await client.SendAsync(Ranged(sha, cha, "bytes=0-1,3-4")))
            multi.StatusCode.Should().Be(HttpStatusCode.OK, "多區間不支援：照 RFC 忽略 Range");

        using (var missing = await client.SendAsync(Ranged(MediaBytes.Sha([1]), cha, null)))
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var upper = await client.SendAsync(Ranged(sha.ToUpperInvariant(), cha, null)))
            upper.StatusCode.Should().Be(HttpStatusCode.NotFound, "blob id 只收小寫");
    }

    [Fact]
    public async Task CSRF豁免的端點只有配對與只收存取權杖的裝置端點()
    {
        await using var host = await HubTestHost.StartAsync();
        var exempt = host.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<MyriagentHub.Auth.CsrfExemptAttribute>() is not null)
            .ToList();

        exempt.Should().NotBeEmpty();
        foreach (var endpoint in exempt)
        {
            var name = endpoint.RoutePattern.RawText;
            var anonymousPairing = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null
                && name!.StartsWith("/api/v1/pair/", StringComparison.Ordinal);
            var policies = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(a => a.Policy).ToList();
            (anonymousPairing || (policies.Count > 0 && policies.All(p => p == MyriagentHub.Auth.HubPolicies.DeviceAccess)))
                .Should().BeTrue($"{name} 不用 CSRF 標頭，就不能收 cookie（只能是配對或 DeviceAccess）");
        }

        exempt.Select(e => e.RoutePattern.RawText).Should().Contain("/api/v1/device" + BlobEndpoints.Route);
        exempt.Select(e => e.RoutePattern.RawText).Should().NotContain("/api/v1/uploads/{uploadId}", "瀏覽器上傳照常要 CSRF 標頭");
    }

    private static HttpRequestMessage Ranged(string sha, string cha, string? range)
    {
        var request = MediaHost.Device(HttpMethod.Get, Path + sha, cha);
        if (range is not null)
            request.Headers.TryAddWithoutValidation("Range", range);
        return request;
    }

    private static List<(string Sha, long Size, string Kind)> Rows(MediaHost host)
    {
        using var connection = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sha256, size, kind FROM blobs ORDER BY sha256";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, long, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        return rows;
    }

    private static void AssertNothingStored(MediaHost host)
    {
        Rows(host).Should().BeEmpty();
        Directory.EnumerateFiles(host.Host.Paths.BlobsDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateFiles(host.Host.Paths.TmpDir, BlobStore.TmpPrefix + "*").Should().BeEmpty();
        host.Services.GetRequiredService<BlobStore>().ReservedBytes.Should().Be(0);
    }
}
