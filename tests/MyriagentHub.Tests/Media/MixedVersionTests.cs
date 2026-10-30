using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Media;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.Realtime;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static MyriagentHub.Tests.Sync.CommandRouterTests;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 媒體的混合版本與回滾（WP4.7 計畫步 13、0.1 第 4 點）：
/// <list type="bullet">
/// <item>新伺服器配舊桌面：WP4.5 合併後的桌面（<c>390145d</c>，宣告 conv1、live1、cmd1）與 WP4.1 的桌面（不宣告）協商不到 media1，
/// 伺服器一則 <c>MediaRequest</c> 都不送；<c>/m/</c> 的影片回 503，圖片有縮圖時以縮圖退回（<c>X-Myriagent-Fallback: thumb</c>）。
/// 參照是測試直接寫進 <c>media_refs</c> 的：模擬 WP4.7 的桌面發過參照、之後換回舊桌面（或離線）。真的舊桌面不發參照（線上的項目沒有 <c>ref</c>），
/// 網頁上只有檔名、沒有 <c>/m/</c>、<c>/t/</c> 網址（合併前審查 DI-6、INT-7）。</item>
/// <item>回滾（<c>MYRIAGENT_SYNC_PREV_HUB</c>：WP4.7 合併前的任何組建，例如 <c>390145d</c>、<c>55a758f</c>、<c>5538705</c>；<c>MYRIAGENT_SYNC_OLD_HUB</c>：<c>1724718</c>；
/// 沒設就略過，建法見 src/MyriagentHub/README.md）：舊組建開本包寫過的資料根（<c>media_refs</c>、<c>blobs</c>、<c>blob_refs</c> 有列，<c>blobs\</c> 與
/// <c>cache\thumbs\</c> 有檔）→ healthz 200、Hello 沒有 media1、帶 <c>{blob}</c> 欄位的項目照常渲染（不會 500），停止後三張表逐列逐欄不變、
/// 兩個資料夾的檔案不變；再以這個組建開同一個資料根，用量照表載入、縮圖照給，回滾期間寫的項目由索引器補上參照。</item>
/// </list>
/// 類別名稱前加 Media：和 <see cref="Realtime.MixedVersionTests"/>（WP4.2）分開
/// </summary>
public sealed class MediaMixedVersionTests(ITestOutputHelper output)
{
    private const string PrevReason = "回滾到 WP4.7 合併前的 main 的測試：值是那個組建（例如 390145d）的 MyriagentHub.exe 完整路徑，建法見 src/MyriagentHub/README.md";

    private const string OldReason = "回滾到 1724718 的測試：值是那個組建的 MyriagentHub.exe 完整路徑，建法見 src/MyriagentHub/README.md";

    // ───── 新伺服器配舊桌面 ─────

    [Theory]
    [InlineData("WP4.5 合併後的桌面", new[] { DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Cmd1 })]
    [InlineData("WP4.1 的桌面", new string[0])]
    public async Task 新伺服器配舊桌面_協商不到media1_0則MediaRequest_影片503_有縮圖的圖片以縮圖退回(string desktop, string[] features)
    {
        await using var host = await MediaHost.StartAsync();
        await using var device = await FakeMediaDevice.ConnectAsync(host, features: features);
        device.Serve();
        using var owner = await host.OwnerAsync();
        var conversation = await MediaSeed.ConversationAsync(host, device.Id);
        host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(device.Id).Single().Features
            .Should().NotContain(DeviceFeatureRules.Media1, $"{desktop}：伺服器照樣宣告 media1，協商出的是交集");

        var thumb = MediaBytes.Png(2048);
        var thumbSha = MediaBytes.Sha(thumb);
        await host.Services.GetRequiredService<BlobStore>().PutAsync(thumbSha, thumb.Length, BlobKinds.Thumb, "image/png", new MemoryStream(thumb), CancellationToken.None);
        var imageRef = MediaSeed.NewRef();
        device.Files[imageRef] = new FakeMediaFile(MediaBytes.Png(50_000, seed: 3), "image/png");
        var imageKey = await MediaSeed.RefAsync(host, device.Id, conversation, imageRef, thumbBlob: thumbSha);
        var videoRef = MediaSeed.NewRef();
        device.Files[videoRef] = new FakeMediaFile(MediaBytes.Random(50_000), "video/mp4");
        var videoKey = await MediaSeed.RefAsync(host, device.Id, conversation, videoRef);

        using (var image = await owner.SendAsync(MediaBrowser.Get("/m/" + imageKey, fetchDest: "image")))
        {
            image.StatusCode.Should().Be(HttpStatusCode.OK, "圖片有縮圖：退回縮圖");
            MediaBrowser.Header(image, MediaRelayHeaders.Fallback).Should().Be("thumb");
            (await image.Content.ReadAsByteArrayAsync()).Should().Equal(thumb);
        }

        using (var video = await owner.SendAsync(MediaBrowser.Get("/m/" + videoKey, "bytes=0-", fetchDest: "video")))
        {
            video.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await MediaHost.ErrorAsync(video)).Should().Be("offline", "沒有 media1 的連線等同離線");
        }

        using (var file = await owner.SendAsync(MediaBrowser.Get("/t/" + videoKey + "?s=160")))
            file.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "沒有縮圖 blob、快取也沒有：要轉送，但來源沒有 media1");

        await Task.Delay(300);
        device.Seen.Should().BeEmpty("沒協商出 media1 的連線，伺服器一則 MediaRequest 都不送");
        device.Client.Connection.State.Should().Be(HubConnectionState.Connected, "不回錯誤、不中斷");
    }

    // ───── 回滾 ─────

    [EnvPathFact(CommandMixedVersionTests.PrevHubVariable, PrevReason)]
    public Task 回滾到合併前的main_開WP4點7寫過的資料根_表與檔案原封不動_再升級回來照常() =>
        RoundTripAsync(CommandMixedVersionTests.PrevHubVariable, conversationsSupported: true);

    [EnvPathFact(Realtime.MixedVersionTests.OldHubVariable, OldReason)]
    public Task 回滾到1724718_開WP4點7寫過的資料根_表與檔案原封不動_再升級回來照常() =>
        RoundTripAsync(Realtime.MixedVersionTests.OldHubVariable, conversationsSupported: false);

    /// <param name="conversationsSupported">舊組建收對話事件、有瀏覽器的對話（WP4.2 起）；1724718 一律回 invalid</param>
    private async Task RoundTripAsync(string variable, bool conversationsSupported)
    {
        var exe = Environment.GetEnvironmentVariable(variable)!;
        File.Exists(exe).Should().BeTrue($"{variable} 要指向舊組建的 MyriagentHub.exe：{exe}");
        var dir = TempDir.Create("myriagenthub-media-rollback");
        Seeded? seeded = null;
        try
        {
            seeded = await SeedAsync(dir);
            var before = Snapshot(dir);
            output.WriteLine($"回滾前：media_refs {before.MediaRefs.Count} 列、blobs {before.Blobs.Count} 列、blob_refs {before.BlobRefs.Count} 列、檔案 {before.Files.Count} 個");
            before.MediaRefs.Should().HaveCountGreaterThanOrEqualTo(4);
            before.Files.Should().Contain(f => f.StartsWith("cache", StringComparison.Ordinal), "前提：縮圖快取有檔");
            before.Files.Should().Contain(f => f.StartsWith("blobs", StringComparison.Ordinal), "前提：blob 有檔");

            var lateRef = MediaSeed.NewRef();
            using (var old = await HubProcess.StartAsync(dir, HubProcess.FreePort(), exe: exe))
            {
                var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(old.BaseAddress, seeded.Device.Token);
                await using (client)
                {
                    var (hello, prove) = await client.HandshakeAsync(seeded.Device, HandshakeRules.SpkiSha256(seen), new OutboxPosition(seeded.Next, null, null),
                        features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Media1]);
                    output.WriteLine("舊組建的 features：" + (hello.Features is null ? "（沒有）" : string.Join(",", hello.Features)));
                    (hello.Features ?? []).Should().NotContain(DeviceFeatureRules.Media1, "合併前的組建不宣告 media1");
                    prove.NextDeviceSeqExpected.Should().Be(seeded.Next, "本包寫過的事件都在");

                    // 回滾期間這台送一筆帶附件參照的項目（舊組建的索引器不在：升級回來之後才補上 media_refs）
                    var late = ConversationSamples.Item("回滾期間送的", id: "item9", ord: 9);
                    late["attachments"] = MediaSeed.Attachments([lateRef]);
                    await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, EventLogTests.Batch(ConversationEventLogTests.Ev(seeded.Next, K.Item, late, seeded.Conversation)));
                    var ack = await client.WaitForAsync<BatchAck>();
                    if (conversationsSupported)
                        ack.Results.Single().Should().Match<BatchEventResult>(r => r.Seq != null && r.Rejected == null, "WP4.2 起照常收對話事件");
                    else
                        ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.Invalid, "1724718 對所有對話事件回 invalid");
                    await Task.Delay(300);
                    Received(client).Should().NotContain(m => m is MediaRequest);
                }

                if (conversationsSupported)
                    await AssertRendersAsync(old.BaseAddress, seeded.Conversation);
                await HealthzAsync(old.BaseAddress);
                (await old.StopAsync()).Should().BeTrue("舊組建照樣以 admin stop 正常結束");
            }

            var after = Snapshot(dir);
            after.MediaRefs.Should().Equal(before.MediaRefs, "舊組建不讀、不寫 media_refs：每一列的每一欄都不變");
            after.Blobs.Should().Equal(before.Blobs);
            after.BlobRefs.Should().Equal(before.BlobRefs);
            after.Files.Should().Equal(before.Files, "blobs\\ 與 cache\\thumbs\\ 的檔案（路徑、大小、SHA-256）都不變");
            using (var read = SqliteSetup.Open(new HubPaths(dir).SyncDb, readOnly: true))
                SqliteSetup.Scalar(read, "SELECT value FROM server_meta WHERE key = 'schema_version'")
                    .Should().Be(SyncSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture), "舊組建不動結構、不降 schema_version（F48）");

            // 再升級：用量從表載入、縮圖照給（thumb_blob 與快取）、回滾期間寫的項目由索引器補上參照
            await using var upgraded = await MediaHost.StartAsync(dataDir: dir, clock: new ManualTimeProvider(DateTimeOffset.UtcNow));
            var store = upgraded.Services.GetRequiredService<BlobStore>();
            (store.Count, store.UsedBytes).Should().Be((before.Blobs.Count, before.BlobBytes), "用量照 blobs 表");
            await MediaSeed.IndexedAsync(upgraded);
            var refs = MediaSeed.Rows(upgraded, seeded.Conversation).Select(r => r.Ref).ToList();
            if (conversationsSupported)
                refs.Should().Contain(lateRef, "回滾期間寫的項目：索引器從游標接著走，補上參照");
            else
                refs.Should().NotContain(lateRef, "1724718 沒有收那筆");
            refs.Should().Contain(seeded.ItemRef).And.Contain(seeded.BlobFieldRef);
            using var owner = await upgraded.OwnerAsync();
            using (var cached = await owner.SendAsync(MediaBrowser.Get("/t/" + seeded.CachedThumbKey + "?s=320")))
            {
                cached.StatusCode.Should().Be(HttpStatusCode.OK, "快取的縮圖（來源不在線）");
                (await cached.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.CachedThumb);
            }

            using (var stored = await owner.SendAsync(MediaBrowser.Get("/t/" + seeded.StoredThumbKey + "?s=160")))
            {
                stored.StatusCode.Should().Be(HttpStatusCode.OK, "thumb_blob 的縮圖");
                (await stored.Content.ReadAsByteArrayAsync()).Should().Equal(seeded.StoredThumb);
            }
        }
        finally
        {
            seeded?.Dispose();
            TempDir.TryDelete(dir);
        }
    }

    // ───── 工具 ─────

    /// <summary>本包的組建寫好的資料根</summary>
    private sealed record Seeded(TestDevice Device, string Conversation, long Next, string ItemRef, string BlobFieldRef, string StoredThumbKey, byte[] StoredThumb,
        string CachedThumbKey, byte[] CachedThumb) : IDisposable
    {
        public void Dispose() => Device.Dispose();
    }

    /// <summary>
    /// 以這個組建（Kestrel 測試主機，真實時間起點的手動時鐘：子程序用真的時鐘）寫入：一個對話、兩筆項目（附件帶參照；另一筆的 attachments 是 <c>{blob}</c>）、
    /// 一份縮圖 blob（<c>thumb_blob</c>）、轉送一次寫進快取的縮圖、一份 E4 上傳；等索引器追上再停止
    /// </summary>
    private static async Task<Seeded> SeedAsync(string dir)
    {
        await using var host = await MediaHost.StartAsync(dataDir: dir, clock: new ManualTimeProvider(DateTimeOffset.UtcNow));
        var device = await TestDevice.AddAsync(host.Host, "回滾", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            var cachedThumb = MediaBytes.Png(3000, seed: 21);
            var cachedRef = MediaSeed.NewRef();
            await using (var source = await FakeMediaDevice.ConnectAsync(host, device))
            {
                source.Serve(async request =>
                {
                    var reply = request.Purpose == MediaPurposes.Thumb && request.Ref == cachedRef
                        ? FakeReply.For(new FakeMediaFile(cachedThumb, "image/png"), request)
                        : FakeReply.For(null, request);
                    using var response = await source.PutAsync(request.RelayId, reply);
                    source.Replies.Enqueue(response.StatusCode);
                });

                var conversation = await MediaSeed.ConversationAsync(host, device.Id);
                var itemRef = MediaSeed.NewRef();
                await MediaSeed.ItemAsync(host, device.Id, conversation, "item1", [itemRef], edit: item => item["ord"] = 1);
                var blobFieldRef = MediaSeed.NewRef();
                var attachments = Encoding.UTF8.GetBytes(MediaSeed.Attachments([blobFieldRef]).ToString(Newtonsoft.Json.Formatting.None));
                var attachmentsSha = await MediaSeed.StoreBlobAsync(host, attachments, BlobKinds.ItemBody);
                await MediaSeed.ItemAsync(host, device.Id, conversation, "item2",
                    edit: item =>
                    {
                        item["ord"] = 2;
                        item["attachments"] = new JObject { ["blob"] = attachmentsSha, ["length"] = attachments.LongLength };
                    });
                await MediaSeed.IndexedAsync(host);

                var storedThumb = MediaBytes.Png(2500, seed: 22);
                var storedSha = MediaBytes.Sha(storedThumb);
                await host.Services.GetRequiredService<BlobStore>().PutAsync(storedSha, storedThumb.Length, BlobKinds.Thumb, "image/png", new MemoryStream(storedThumb),
                    CancellationToken.None);
                var storedKey = await MediaSeed.RefAsync(host, device.Id, conversation, MediaSeed.NewRef(), "item:item1", thumbBlob: storedSha);
                var cachedKey = await MediaSeed.RefAsync(host, device.Id, conversation, cachedRef, "item:item1");

                using var owner = await host.OwnerAsync();
                using (var first = await owner.SendAsync(MediaBrowser.Get("/t/" + cachedKey + "?s=320")))
                {
                    first.StatusCode.Should().Be(HttpStatusCode.OK, "轉送一次，寫進縮圖快取");
                    (await first.Content.ReadAsByteArrayAsync()).Should().Equal(cachedThumb);
                }

                File.Exists(Path.Combine(host.Host.Paths.ThumbCacheDir, cachedKey)).Should().BeTrue();
                var upload = MediaBytes.Random(70_000, seed: 23);
                using (var put = await owner.SendAsync(MediaHost.Owner(HttpMethod.Put, "/api/v1/uploads/rollback-upload", MediaHost.Bytes(upload))))
                    put.StatusCode.Should().Be(HttpStatusCode.Created);

                var next = NextSeq(host.Host, device.Id);
                var copy = TestDevice.Paired(device.Id, device.Token, ECDsa.Create(device.Key.ExportParameters(includePrivateParameters: true)));
                return new Seeded(copy, conversation, next, itemRef, blobFieldRef, storedKey, storedThumb, cachedKey, cachedThumb);
            }
        }
        finally
        {
            device.Dispose();
        }
    }

    private sealed record DataSnapshot(List<string> MediaRefs, List<string> Blobs, List<string> BlobRefs, List<string> Files, long BlobBytes);

    /// <summary>三張表的每一列每一欄（依主鍵排序）與 <c>blobs\</c>、<c>cache\thumbs\</c> 的檔案（相對路徑、大小、SHA-256）；停止之後直接讀檔</summary>
    private static DataSnapshot Snapshot(string dataDir)
    {
        var paths = new HubPaths(dataDir);
        using var read = SqliteSetup.Open(paths.SyncDb, readOnly: true);
        var files = new List<string>();
        foreach (var root in new[] { paths.BlobsDir, paths.ThumbCacheDir })
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var data = File.ReadAllBytes(file);
                files.Add($"{Path.GetRelativePath(dataDir, file)}|{data.Length}|{Convert.ToHexStringLower(SHA256.HashData(data))}");
            }
        }

        files.Sort(StringComparer.Ordinal);
        var bytes = Convert.ToInt64(SqliteSetup.Scalar(read, "SELECT COALESCE(SUM(size), 0) FROM blobs"), CultureInfo.InvariantCulture);
        return new DataSnapshot(Dump(read, "SELECT * FROM media_refs ORDER BY device_id, ref"), Dump(read, "SELECT * FROM blobs ORDER BY sha256"),
            Dump(read, "SELECT * FROM blob_refs ORDER BY sha256, ref_kind, ref_id"), files, bytes);
    }

    private static List<string> Dump(Microsoft.Data.Sqlite.SqliteConnection read, string sql)
    {
        using var cmd = read.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.GetName(i) + "=" + (reader.IsDBNull(i) ? "∅" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            rows.Add(string.Join("|", values));
        }

        return rows;
    }

    /// <summary>舊組建的瀏覽器：登入、open 這個對話，兩筆項目都渲染出來（<c>{blob}</c> 欄位的那一筆是 WP4.2 的佔位或照常的卡片，不會 500）</summary>
    private async Task AssertRendersAsync(Uri baseAddress, string conversation)
    {
        var cookies = new CookieContainer();
        using var http = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        {
            BaseAddress = baseAddress,
        };
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login"))
        {
            login.Headers.Add("Origin", HubTestHost.Origin);
            login.Headers.Add("X-Myriagent-Csrf", "1");
            login.Content = new StringContent("{\"userName\":\"" + OwnerAccount.UserName + "\",\"password\":\"" + OwnerAccount.DefaultPassword + "\"}", Encoding.UTF8,
                "application/json");
            using var response = await http.SendAsync(login);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "舊組建的擁有者登入");
        }

        await using var browser = await BrowserHubClient.ConnectKestrelAsync(baseAddress, cookies);
        await browser.ReadyAsync();
        (await browser.CmdAsync(new { cmd = "open", conversationId = conversation }))["accepted"]!.Value<bool>().Should().BeTrue();
        var opened = await browser.WaitForAsync("conversation", m => m["conversationId"]!.Value<string>() == conversation, timeoutMs: 10_000);
        var items = (JArray)opened["items"]!;
        output.WriteLine("舊組建渲染的項目：" + string.Join(" / ", items.Select(i => i["id"] + ":" + i["kind"])));
        items.Select(i => i["id"]!.Value<string>()).Should().Contain(["item1", "item2"], "帶 {blob} 欄位的項目照樣渲染（佔位），不會讓整個對話開不了");
    }

    private static async Task HealthzAsync(Uri baseAddress)
    {
        using var http = new HttpClient(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } });
        using var response = await http.GetAsync(new Uri(baseAddress, "/healthz"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static List<object> Received(DeviceHubClient client)
    {
        var list = new List<object>();
        while (client.Received.Reader.TryRead(out var message))
            list.Add(message);
        return list;
    }
}
