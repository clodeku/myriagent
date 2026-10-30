using Myriagent.Media;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Ops;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 每日媒體清理（WP4.7 計畫步 5；D19）：保留窗看最舊的備份、上傳 24 小時過期、縮圖快取上限、對帳孤兒檔與有列沒檔、tmp 的暫存檔、
/// 過期的抽屜與 fileFetch 參照。時間以主機的手動時鐘（2026-10-11 09:00 UTC 起，本機時區 UTC）為準，檔案的修改時間照它設
/// </summary>
public sealed class MediaMaintenanceTests
{
    private static readonly long HourMs = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    [Fact]
    public async Task 維護_item_body在門檻前才刪_晚1秒與不到1小時的留著()
    {
        await using var host = await MediaHost.StartAsync();
        // 最舊的備份是 10/05（本機＝UTC）：門檻是 10/04 00:00
        Directory.CreateDirectory(Path.Combine(host.Host.Paths.DailyBackupsDir, "20261005"));
        Directory.CreateDirectory(Path.Combine(host.Host.Paths.DailyBackupsDir, "20261010"));
        var before = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var early = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(101), BlobKinds.ItemBody);
        var late = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(102), BlobKinds.ItemBody);
        var young = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(103), BlobKinds.ItemBody);
        var referenced = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(104), BlobKinds.ItemBody);
        var now = host.Host.Clock.NowMs;
        await SetBlobAsync(host, early, createdAt: now - (30 * 24 * HourMs), lastRefAt: before - 1000);
        await SetBlobAsync(host, late, createdAt: now - (30 * 24 * HourMs), lastRefAt: before + 1000);
        await SetBlobAsync(host, young, createdAt: now - HourMs + 1000, lastRefAt: before - 1000);
        await SetBlobAsync(host, referenced, createdAt: now - (30 * 24 * HourMs), lastRefAt: before - 1000);
        await AddBlobRefAsync(host, referenced, BlobRefKinds.Item, "0123456789abcdef0123456789abcdef:item1");
        var store = host.Services.GetRequiredService<BlobStore>();
        var used = store.UsedBytes;

        var result = await host.Services.GetRequiredService<MediaMaintenance>().RunAsync(CancellationToken.None);

        result.DeleteBefore.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(before));
        result.BlobsSkipped.Should().BeFalse();
        result.BlobsDeleted.Should().Be(1);
        result.BytesFreed.Should().Be(101);
        store.SizeOf(early).Should().BeNull();
        File.Exists(store.PathOf(early)).Should().BeFalse();
        foreach (var kept in new[] { late, young, referenced })
            store.SizeOf(kept).Should().NotBeNull();
        store.UsedBytes.Should().Be(used - 101);
        store.Count.Should().Be(3);
    }

    [Fact]
    public async Task 維護_沒有任何備份時項目與縮圖一律不刪()
    {
        await using var host = await MediaHost.StartAsync();
        var item = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(200), BlobKinds.ItemBody);
        var thumb = await MediaSeed.StoreBlobAsync(host, MediaBytes.Png(300), BlobKinds.Thumb);
        var now = host.Host.Clock.NowMs;
        await SetBlobAsync(host, item, now - (400 * 24 * HourMs), now - (300 * 24 * HourMs));
        await SetBlobAsync(host, thumb, now - (400 * 24 * HourMs), now - (300 * 24 * HourMs));

        var result = await host.Services.GetRequiredService<MediaMaintenance>().RunAsync(CancellationToken.None);

        result.DeleteBefore.Should().BeNull();
        result.BlobsDeleted.Should().Be(0);
        host.Services.GetRequiredService<BlobStore>().Count.Should().Be(2);
    }

    [Fact]
    public async Task 維護_predeploy資料夾以建立時間算進最舊的備份()
    {
        await using var host = await MediaHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(host.Host.Paths.DailyBackupsDir, "20261010"));
        var predeploy = Directory.CreateDirectory(Path.Combine(host.Host.Paths.BackupsDir, "predeploy-1.0.0-abcdef0"));
        predeploy.CreationTimeUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        host.Services.GetRequiredService<MediaMaintenance>().DeleteBefore().Should().Be(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task 維護_upload引用24小時過期_從沒被引用的上傳隨即刪掉()
    {
        await using var host = await MediaHost.StartAsync();
        var expired = await MediaSeed.StoreBlobAsync(host, MediaBytes.Random(500, seed: 1), BlobKinds.Upload);
        var fresh = await MediaSeed.StoreBlobAsync(host, MediaBytes.Random(600, seed: 2), BlobKinds.Upload);
        host.Host.Clock.Advance(TimeSpan.FromHours(30));
        var now = host.Host.Clock.NowMs;
        await AddBlobRefAsync(host, expired, BlobRefKinds.Upload, UploadRules.RefId("old-upload", now - UploadRules.LifetimeMs - 1000));
        await AddBlobRefAsync(host, fresh, BlobRefKinds.Upload, UploadRules.RefId("new-upload", now - UploadRules.LifetimeMs + 1000));

        // 每日維護（備份關著）也會跑媒體清理
        await host.Services.GetRequiredService<MaintenanceService>().RunDailyAsync(CancellationToken.None);

        MediaSeed.BlobRefs(host, BlobRefKinds.Upload).Should().Equal((fresh, UploadRules.RefId("new-upload", now - UploadRules.LifetimeMs + 1000)));
        var store = host.Services.GetRequiredService<BlobStore>();
        store.SizeOf(expired).Should().BeNull("引用過期、從沒被項目或縮圖引用過、存入超過 1 小時：不等保留窗");
        store.SizeOf(fresh).Should().Be(600);
    }

    [Fact]
    public async Task 維護_縮圖快取超過上限刪修改時間最舊的()
    {
        await using var host = await MediaHost.StartAsync(new Dictionary<string, string?> { ["Hub:Media:ThumbCacheBytes"] = "3072" });
        var dir = host.Host.Paths.ThumbCacheDir;
        var clock = host.Host.Clock.GetUtcNow().UtcDateTime;
        var names = Enumerable.Range(0, 3).Select(_ => MediaKeyRules.Format(Myriagent.Sync.SyncIds.NewDeviceId(), MediaSeed.NewRef())).ToList();
        for (var i = 0; i < names.Count; i++)
        {
            var path = Path.Combine(dir, names[i]);
            await File.WriteAllBytesAsync(path, MediaBytes.Png(1500, seed: i));
            File.SetLastWriteTimeUtc(path, clock.AddMinutes(-10 + i));
        }

        var result = await host.Services.GetRequiredService<MediaMaintenance>().RunAsync(CancellationToken.None);

        result.ThumbCacheTrimmed.Should().Be(1);
        Directory.EnumerateFiles(dir).Select(Path.GetFileName).Should().BeEquivalentTo(names[1], names[2]);
        host.Services.GetRequiredService<ThumbCache>().Bytes.Should().Be(3000);
    }

    [Fact]
    public async Task 維護_對帳_沒有列的舊檔刪掉_新的留著_有列沒檔的列刪掉()
    {
        await using var host = await MediaHost.StartAsync();
        var store = host.Services.GetRequiredService<BlobStore>();
        var clock = host.Host.Clock.GetUtcNow().UtcDateTime;
        var oldOrphan = WriteOrphan(store, MediaBytes.Random(300, seed: 11), clock.AddHours(-2));
        var newOrphan = WriteOrphan(store, MediaBytes.Random(300, seed: 12), clock.AddMinutes(-30));
        var stray = Path.Combine(host.Host.Paths.BlobsDir, "ab", "not-a-blob.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        await File.WriteAllTextAsync(stray, "人放的東西");
        File.SetLastWriteTimeUtc(stray, clock.AddDays(-5));
        var lost = await MediaSeed.StoreBlobAsync(host, MediaBytes.Random(400, seed: 13), BlobKinds.ItemBody);
        await AddBlobRefAsync(host, lost, BlobRefKinds.Item, "0123456789abcdef0123456789abcdef:item1");
        File.Delete(store.PathOf(lost));
        var kept = await MediaSeed.StoreBlobAsync(host, MediaBytes.Random(500, seed: 14), BlobKinds.ItemBody);

        var result = await host.Services.GetRequiredService<MediaMaintenance>().RunAsync(CancellationToken.None);

        result.OrphanFiles.Should().Be(1);
        result.RowsWithoutFile.Should().Be(1);
        File.Exists(store.PathOf(oldOrphan)).Should().BeFalse();
        File.Exists(store.PathOf(newOrphan)).Should().BeTrue("不到 1 小時：可能正在存入");
        File.Exists(stray).Should().BeTrue("不是 blob 名稱的檔不動");
        store.SizeOf(lost).Should().BeNull("有列沒檔：刪列，裝置下次 HEAD 就會重傳");
        store.SizeOf(kept).Should().Be(500);
        store.Count.Should().Be(1);
        store.UsedBytes.Should().Be(500);
    }

    [Fact]
    public async Task 維護_tmp只刪自己前綴超過1小時的檔()
    {
        await using var host = await MediaHost.StartAsync();
        var tmp = host.Host.Paths.TmpDir;
        var clock = host.Host.Clock.GetUtcNow().UtcDateTime;
        var files = new Dictionary<string, DateTime>
        {
            [BlobStore.TmpPrefix + "old"] = clock.AddHours(-3),
            [ThumbCache.TmpPrefix + "old"] = clock.AddHours(-3),
            [BlobStore.TmpPrefix + "new"] = clock.AddMinutes(-10),
            ["restore-staging"] = clock.AddDays(-3),
        };
        foreach (var (name, written) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, name), "x");
            File.SetLastWriteTimeUtc(Path.Combine(tmp, name), written);
        }

        var result = await host.Services.GetRequiredService<MediaMaintenance>().RunAsync(CancellationToken.None);

        result.TmpDeleted.Should().Be(2);
        Directory.EnumerateFiles(tmp).Select(Path.GetFileName).Should().BeEquivalentTo(BlobStore.TmpPrefix + "new", "restore-staging");
    }

    [Fact]
    public async Task 維護_抽屜30天與fileFetch15分鐘到期的參照刪掉_項目的不動()
    {
        await using var host = await MediaHost.StartAsync();
        var device = Myriagent.Sync.SyncIds.NewDeviceId();
        const string conversation = "0123456789abcdef0123456789abcdef";
        var now = host.Host.Clock.NowMs;
        var listOld = MediaSeed.NewRef();
        var listNew = MediaSeed.NewRef();
        var fileOld = MediaSeed.NewRef();
        var fileNew = MediaSeed.NewRef();
        var item = MediaSeed.NewRef();
        await MediaSeed.RefAsync(host, device, conversation, listOld, MediaRefPurposes.List, createdAt: now - (long)MediaLookupRules.ListLifetime.TotalMilliseconds);
        await MediaSeed.RefAsync(host, device, conversation, listNew, MediaRefPurposes.List, createdAt: now - (long)MediaLookupRules.ListLifetime.TotalMilliseconds + 1000);
        await MediaSeed.RefAsync(host, device, conversation, fileOld, MediaRefPurposes.File, createdAt: now - (16 * 60 * 1000));
        await MediaSeed.RefAsync(host, device, conversation, fileNew, MediaRefPurposes.File, createdAt: now - (14 * 60 * 1000));
        await MediaSeed.RefAsync(host, device, conversation, item, "item:item1", createdAt: now - (400L * 24 * HourMs));
        var cache = host.Services.GetRequiredService<ThumbCache>();
        (await cache.WriteAsync(MediaKeyRules.Format(device, listOld), "1712345678", MediaBytes.Png(200), CancellationToken.None)).Should().BeTrue();

        var result = await host.Services.GetRequiredService<MediaMaintenance>().RunAsync(CancellationToken.None);

        result.MediaRefsExpired.Should().Be(2);
        MediaSeed.Rows(host).Select(r => r.Ref).Should().BeEquivalentTo(listNew, fileNew, item);
        cache.TryRead(MediaKeyRules.Format(device, listOld), "1712345678").Should().BeNull("到期的抽屜縮圖快取一起刪");
    }

    [Fact]
    public async Task 維護_冪等_第二次什麼都不做()
    {
        await using var host = await MediaHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(host.Host.Paths.DailyBackupsDir, "20261001"));
        var blob = await MediaSeed.StoreBlobAsync(host, MediaBytes.Text(50), BlobKinds.ItemBody);
        await SetBlobAsync(host, blob, 0, 0);
        var maintenance = host.Services.GetRequiredService<MediaMaintenance>();

        (await maintenance.RunAsync(CancellationToken.None)).BlobsDeleted.Should().Be(1);
        var second = await maintenance.RunAsync(CancellationToken.None);

        second.Should().BeEquivalentTo(new MediaMaintenanceResult(0, 0, 0, second.DeleteBefore, false, 0, 0, 0, 0, 0));
    }

    private static string WriteOrphan(BlobStore store, byte[] content, DateTime written)
    {
        var sha = MediaBytes.Sha(content);
        var path = store.PathOf(sha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, written);
        return sha;
    }

    private static Task SetBlobAsync(MediaHost host, string sha, long createdAt, long lastRefAt) =>
        host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute(
            "UPDATE blobs SET created_at = $c, last_ref_at = $l WHERE sha256 = $s", ("$c", createdAt), ("$l", lastRefAt), ("$s", sha)));

    private static Task AddBlobRefAsync(MediaHost host, string sha, string kind, string refId) =>
        host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute(
            "INSERT INTO blob_refs(sha256, ref_kind, ref_id) VALUES($s, $k, $r)", ("$s", sha), ("$k", kind), ("$r", refId)));
}
