using System.Net;
using System.Text;
using System.Text.Json;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 瀏覽器上傳（WP4.7 計畫 E4；D13、D21）：真的 Kestrel、擁有者 cookie＋本站 Origin＋CSRF、≤512 MB、同 id 同內容 200、不同內容 409、
/// 待處理合計 507，以及給 WP4.5 的上傳檢查 TryVerify
/// </summary>
public sealed class UploadEndpointsTests
{
    private const string Path = "/api/v1/uploads/";
    private const int Mb = 1024 * 1024;

    [Fact]
    public async Task E4_沒有CSRF標頭或跨站回403()
    {
        await using var host = await MediaHost.StartAsync();
        using var owner = await host.OwnerAsync();
        var data = MediaBytes.Random(100);

        using (var noCsrf = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "u1", MediaHost.Bytes(data), csrf: false)))
        {
            noCsrf.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await MediaHost.ErrorAsync(noCsrf)).Should().Be("forbidden_origin");
        }

        var crossSite = MediaHost.Owner(HttpMethod.Put, Path + "u1", MediaHost.Bytes(data));
        crossSite.Headers.Remove("Origin");
        crossSite.Headers.Add("Origin", "https://evil.example");
        using (var response = await owner.SendAsync(crossSite))
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        host.Services.GetRequiredService<UploadRegistry>().PendingBytes.Should().Be(0);
        BlobCount(host).Should().Be(0);
    }

    [Fact]
    public async Task E4_2MB成功_其他API端點照舊1MB上限()
    {
        await using var host = await MediaHost.StartAsync();
        using var owner = await host.OwnerAsync();
        var data = MediaBytes.Random(2 * Mb);
        var sha = MediaBytes.Sha(data);

        using (var response = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "01HZXUPLOAD", MediaHost.Bytes(data))))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("uploadId").GetString().Should().Be("01HZXUPLOAD");
            body.GetProperty("sha256").GetString().Should().Be(sha);
            body.GetProperty("size").GetInt64().Should().Be(data.Length);
        }

        var store = host.Services.GetRequiredService<BlobStore>();
        store.Exists(sha).Should().BeTrue();
        Refs(host).Should().ContainSingle().Which.Should().Be((sha, BlobRefKinds.Upload, UploadRules.RefId("01HZXUPLOAD", host.Host.Clock.NowMs)));
        host.Services.GetRequiredService<UploadRegistry>().PendingBytes.Should().Be(data.Length);

        // 同一個測試裡另一個 /api JSON 端點送 2 MB：全域的 1 MB 上限照舊（只有上傳端點放寬）
        // （Expect: 100-continue：Kestrel 在讀本文之前就以宣告的長度擋下，用戶端不必送出 2 MB）
        using var other = await owner.SendAsync(MediaHost.Unread(MediaHost.Owner(HttpMethod.Post, "/api/v1/auth/stepup"), 2 * Mb, "application/json"));
        other.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task E4_同id同內容回200_不同內容回409()
    {
        await using var host = await MediaHost.StartAsync();
        using var owner = await host.OwnerAsync();
        var data = MediaBytes.Random(50_000, seed: 1);
        var sha = MediaBytes.Sha(data);
        using (var first = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "same", MediaHost.Bytes(data))))
            first.StatusCode.Should().Be(HttpStatusCode.Created);

        using (var again = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "same", MediaHost.Bytes(data))))
        {
            again.StatusCode.Should().Be(HttpStatusCode.OK, "重傳冪等");
            var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("sha256").GetString().Should().Be(sha);
            body.GetProperty("size").GetInt64().Should().Be(data.Length);
        }

        var other = MediaBytes.Random(50_000, seed: 2);
        using (var different = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "same", MediaHost.Bytes(other))))
        {
            different.StatusCode.Should().Be(HttpStatusCode.Conflict, "同一個 id、同樣大小、不同內容");
            (await MediaHost.ErrorAsync(different)).Should().Be("conflict");
        }

        using (var otherLength = await owner.SendAsync(MediaHost.Unread(MediaHost.Owner(HttpMethod.Put, Path + "same"), data.Length + 1)))
            otherLength.StatusCode.Should().Be(HttpStatusCode.Conflict, "宣告的大小不同：不讀本文就回");

        BlobCount(host).Should().Be(1, "比對重傳不另存一份");
        Refs(host).Should().HaveCount(1);
        host.Services.GetRequiredService<UploadRegistry>().PendingBytes.Should().Be(data.Length);
    }

    [Fact]
    public async Task E4_宣告513MB回413不讀本文()
    {
        await using var host = await MediaHost.StartAsync();
        using var owner = await host.OwnerAsync();

        using var response = await owner.SendAsync(MediaHost.Unread(MediaHost.Owner(HttpMethod.Put, Path + "huge"), 513L * Mb));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await MediaHost.ErrorAsync(response)).Should().Be("too_large");
        host.Services.GetRequiredService<UploadRegistry>().PendingBytes.Should().Be(0);
    }

    [Fact]
    public async Task E4_待處理合計超過上限回507()
    {
        await using var host = await MediaHost.StartAsync(new Dictionary<string, string?>
        {
            ["Hub:Media:MaxUploadBytes"] = (2 * Mb).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Hub:Media:MaxPendingUploadBytes"] = (3 * Mb).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        using var owner = await host.OwnerAsync();
        using (var first = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "a", MediaHost.Bytes(MediaBytes.Random(2 * Mb, seed: 3)))))
            first.StatusCode.Should().Be(HttpStatusCode.Created);

        using (var full = await owner.SendAsync(MediaHost.Unread(MediaHost.Owner(HttpMethod.Put, Path + "b"), 2 * Mb)))
        {
            full.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage, "2 MB＋2 MB 超過 3 MB：讀本文之前就回");
            (await MediaHost.ErrorAsync(full)).Should().Be("pending_full");
        }

        using (var fits = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "c", MediaHost.Bytes(MediaBytes.Random(Mb, seed: 4)))))
            fits.StatusCode.Should().Be(HttpStatusCode.Created, "2 MB＋1 MB 剛好 3 MB");
        using (var tooLarge = await owner.SendAsync(MediaHost.Unread(MediaHost.Owner(HttpMethod.Put, Path + "d"), 2 * Mb + 1)))
            tooLarge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, "單檔上限照設定");
    }

    [Fact]
    public async Task E4_blob配額或磁碟下限回507_登記收回()
    {
        await using var host = await MediaHost.StartAsync(freeBytes: 39L << 30);
        using var owner = await host.OwnerAsync();

        using var response = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "x", MediaHost.Bytes(MediaBytes.Random(1000))));

        response.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);
        (await MediaHost.ErrorAsync(response)).Should().Be("insufficient_storage");
        host.Services.GetRequiredService<UploadRegistry>().PendingBytes.Should().Be(0, "失敗的上傳不留預留");
        host.Disk.Free = 500L << 30;
        using var retry = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "x", MediaHost.Bytes(MediaBytes.Random(1000))));
        retry.StatusCode.Should().Be(HttpStatusCode.Created, "同一個 id 可以重來");
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("a%40b")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123x")]
    public async Task E4_上傳id不合格回400(string id)
    {
        await using var host = await MediaHost.StartAsync();
        using var owner = await host.OwnerAsync();

        using var response = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + id, MediaHost.Bytes(MediaBytes.Random(10))));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await MediaHost.ErrorAsync(response)).Should().Be("invalid");
    }

    [Fact]
    public async Task E4_只收擁有者cookie()
    {
        await using var host = await MediaHost.StartAsync();
        var (device, cha) = await host.DeviceAsync();
        using var _ = device;
        using var client = host.Client();
        var data = MediaBytes.Random(10);

        using (var anonymous = await client.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + "u", MediaHost.Bytes(data))))
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var bearer in new[] { cha, device.Token })
        {
            var request = MediaHost.Owner(HttpMethod.Put, Path + "u", MediaHost.Bytes(data));
            request.Headers.Authorization = new("Bearer", bearer);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "裝置的權杖不能上傳");
        }

        BlobCount(host).Should().Be(0);
    }

    [Fact]
    public async Task E4_同一個id正在上傳時另一個回409()
    {
        await using var host = await MediaHost.StartAsync();
        using var owner = await host.OwnerAsync();
        var registry = host.Services.GetRequiredService<UploadRegistry>();
        var data = MediaBytes.Random(100_000, seed: 5);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new StreamContent(new ScriptedStream(data, gate.Task));
        slow.Headers.ContentLength = data.Length;
        var firstRequest = MediaHost.Owner(HttpMethod.Put, Path + "busy", slow);
        firstRequest.Headers.ExpectContinue = true; // 用戶端先送出標頭（不等本文的第一個位元組）

        var first = owner.SendAsync(firstRequest);
        await BrowserFixtures.WaitUntilAsync(() => registry.PendingBytes == data.Length, 10_000, "伺服器開始收第一個上傳");

        using (var second = await owner.SendAsync(MediaHost.Unread(MediaHost.Owner(HttpMethod.Put, Path + "busy"), data.Length)))
            second.StatusCode.Should().Be(HttpStatusCode.Conflict, "同一個 id 同時只能有一個上傳");

        gate.SetResult();
        using var done = await first;
        done.StatusCode.Should().Be(HttpStatusCode.Created, "先開始的照常完成，不被第二個收回");
        registry.TryVerify([new UploadClaim("busy", data.Length, MediaBytes.Sha(data))]).Should().BeTrue();
    }

    [Fact]
    public async Task TryVerify_雜湊大小過期()
    {
        await using var host = await MediaHost.StartAsync();
        using var owner = await host.OwnerAsync();
        var registry = host.Services.GetRequiredService<UploadRegistry>();
        var a = MediaBytes.Random(1000, seed: 6);
        var b = MediaBytes.Random(2000, seed: 7);
        foreach (var (id, data) in new[] { ("a", a), ("b", b) })
        {
            using var response = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, Path + id, MediaHost.Bytes(data)));
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var claimA = new UploadClaim("a", a.Length, MediaBytes.Sha(a));
        var claimB = new UploadClaim("b", b.Length, MediaBytes.Sha(b));
        registry.TryVerify([claimA, claimB]).Should().BeTrue();
        registry.TryVerify([]).Should().BeTrue("沒有要驗的上傳");
        registry.TryVerify([claimA with { Sha256 = MediaBytes.Sha(b) }]).Should().BeFalse("雜湊不同");
        registry.TryVerify([claimA with { Size = a.Length + 1 }]).Should().BeFalse("大小不同");
        registry.TryVerify([claimA, new UploadClaim("nope", 1, MediaBytes.Sha(a))]).Should().BeFalse("有一筆不存在就不通過");
        ((IUploadRegistry)host.Services.GetRequiredService<IUploadRegistry>()).Should().BeSameAs(registry, "接縫解析到同一個實例");

        host.Host.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1));
        registry.TryVerify([claimA]).Should().BeTrue("還差 1 毫秒");
        host.Host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        registry.TryVerify([claimA]).Should().BeFalse("滿 24 小時過期");
        registry.PendingBytes.Should().Be(0, "過期的不算待處理");
    }

    [Fact]
    public async Task 重新啟動後從blob_refs載回()
    {
        var dir = TempDir.Create();
        try
        {
            var data = MediaBytes.Random(3000, seed: 8);
            await using (var first = await HubTestHost.StartAsync(dataDir: dir, services: s => s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(500L << 30))))
            {
                var store = first.Services.GetRequiredService<BlobStore>();
                var put = await store.PutStreamingAsync(null, data.Length, data.Length, BlobKinds.Upload, null, new MemoryStream(data), null, CancellationToken.None);
                put.Outcome.Should().Be(BlobPutOutcome.Stored);
                var registry = first.Services.GetRequiredService<UploadRegistry>();
                registry.Begin("keep", data.Length).Kind.Should().Be(UploadPutKind.New);
                await registry.CompleteAsync("keep", put.Sha256!, data.Length, CancellationToken.None);
                registry.Begin("gone", 10).Kind.Should().Be(UploadPutKind.New, "正在上傳的不寫資料庫");
            }

            await using var second = await HubTestHost.StartAsync(dataDir: dir, services: s => s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(500L << 30)));
            var reloaded = second.Services.GetRequiredService<UploadRegistry>();
            reloaded.TryVerify([new UploadClaim("keep", data.Length, MediaBytes.Sha(data))]).Should().BeTrue();
            reloaded.PendingBytes.Should().Be(data.Length, "正在上傳的沒有留下來");
            reloaded.Begin("keep", data.Length).Kind.Should().Be(UploadPutKind.Existing);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    private static long BlobCount(MediaHost host)
    {
        using var connection = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM blobs";
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<(string Sha, string Kind, string Id)> Refs(MediaHost host)
    {
        using var connection = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sha256, ref_kind, ref_id FROM blob_refs ORDER BY ref_id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }
}
