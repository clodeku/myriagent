using MyriagentHub.Admin;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tests.Media;

/// <summary>AddHubMedia、Hub:Media 的驗證（啟動與 preflight）與資料根的資料夾（WP4.7 計畫步 2）</summary>
public class MediaRegistrationTests
{
    /// <summary>磁碟可用空間固定 500 GB：不看跑測試那台的磁碟（GitHub 的 Windows runner 低於 40 GB 的 blob 下限，寫入會 507）</summary>
    private static void PlentyOfDisk(IServiceCollection s) => s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(500L << 30));

    [Fact]
    public async Task AddHubMedia_IBlobStore解析出BlobStore()
    {
        await using var host = await HubTestHost.StartAsync(services: PlentyOfDisk);

        var blobs = host.Services.GetRequiredService<IBlobStore>();
        blobs.Should().BeOfType<BlobStore>().And.BeSameAs(host.Services.GetRequiredService<BlobStore>(), "同一個實例");
        host.Services.GetServices<IBlobStore>().Should().HaveCount(2, "AddHubSync 的空儲存仍在，後登記的為準");

        var data = MediaBytes.Random(100);
        var sha = MediaBytes.Sha(data);
        (await blobs.PutAsync(sha, data.Length, "upload", null, new MemoryStream(data), CancellationToken.None)).Should().BeTrue();
        blobs.Exists(sha).Should().BeTrue();
        File.Exists(Path.Combine(host.Paths.BlobsDir, sha[..2], sha)).Should().BeTrue();
    }

    [Fact]
    public async Task 重新啟動後用量從blobs表載入()
    {
        var dir = TempDir.Create();
        try
        {
            var data = MediaBytes.Random(321);
            await using (var first = await HubTestHost.StartAsync(dataDir: dir, services: PlentyOfDisk))
                (await first.Services.GetRequiredService<IBlobStore>().PutAsync(MediaBytes.Sha(data), data.Length, "upload", null, new MemoryStream(data), CancellationToken.None))
                    .Should().BeTrue();

            await using var second = await HubTestHost.StartAsync(dataDir: dir, services: PlentyOfDisk);
            second.Services.GetRequiredService<BlobStore>().UsedBytes.Should().Be(data.Length);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public void HubPaths_EnsureCreated建立blobs與快取資料夾()
    {
        var dir = TempDir.Create();
        try
        {
            var paths = new HubPaths(dir);
            paths.EnsureCreated();

            paths.BlobsDir.Should().Be(Path.Combine(paths.Root, "blobs"));
            paths.ThumbCacheDir.Should().Be(Path.Combine(paths.Root, "cache", "thumbs"));
            foreach (var folder in new[] { paths.BlobsDir, paths.ThumbCacheDir, paths.TmpDir })
                Directory.Exists(folder).Should().BeTrue(folder);
            Directory.GetFileSystemEntries(paths.TmpDir).Should().BeEmpty("tmp 底下不預先建資料夾（preflight 的測試斷言它是空的）");
            Path.GetPathRoot(paths.BlobsDir).Should().Be(Path.GetPathRoot(paths.TmpDir), "同一個磁碟區才能原子改名");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public void 預設值照設計()
    {
        var o = new MediaOptions();

        o.QuotaBytes.Should().Be(200L * 1024 * 1024 * 1024);
        o.AlertPercent.Should().Be(80);
        o.RefusePercent.Should().Be(95);
        o.MinFreeBytes.Should().Be(40L * 1024 * 1024 * 1024);
        o.MaxItemBlobBytes.Should().Be(4L * 1024 * 1024);
        o.MaxThumbBytes.Should().Be(512L * 1024);
        o.MaxUploadBytes.Should().Be(512L * 1024 * 1024);
        o.MaxPendingUploadBytes.Should().Be(4L * 1024 * 1024 * 1024);
        o.MaxConcurrentRelays.Should().Be(64);
        MediaOptionsRules.Validate(o).Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(MediaOptions.QuotaBytes))]
    [InlineData(nameof(MediaOptions.AlertPercent))]
    [InlineData(nameof(MediaOptions.RefusePercent))]
    [InlineData(nameof(MediaOptions.MinFreeBytes))]
    [InlineData(nameof(MediaOptions.MaxPendingUploadBytes))]
    [InlineData(nameof(MediaOptions.MaxItemBlobBytes))]
    [InlineData(nameof(MediaOptions.MaxThumbBytes))]
    [InlineData(nameof(MediaOptions.MaxUploadBytes))]
    [InlineData(nameof(MediaOptions.ThumbCacheBytes))]
    [InlineData(nameof(MediaOptions.FirstByteTimeoutSeconds))]
    [InlineData(nameof(MediaOptions.MaxConcurrentRelays))]
    public void Validate_上限是0不合格(string name)
    {
        var o = new MediaOptions();
        var property = typeof(MediaOptions).GetProperty(name)!;
        property.SetValue(o, property.PropertyType == typeof(long) ? 0L : (object)0);

        MediaOptionsRules.Validate(o).Should().Contain("Hub:Media:" + name);
    }

    [Fact]
    public void Validate_每個上限都檢查()
        => MediaOptionsRules.Limits(new MediaOptions()).Select(l => l.Name).Should().BeEquivalentTo(
            typeof(MediaOptions).GetProperties().Select(p => p.Name), "新加的上限也要驗");

    [Fact]
    public void Validate_其他規則()
    {
        MediaOptionsRules.Validate(null).Should().Contain("Hub:Media");
        MediaOptionsRules.Validate(new MediaOptions { MaxItemBlobBytes = (64L << 20) + 1 }).Should().Contain("MaxItemBlobBytes");
        MediaOptionsRules.Validate(new MediaOptions { MaxItemBlobBytes = 64L << 20 }).Should().BeNull("等於型別上限可以");
        MediaOptionsRules.Validate(new MediaOptions { MaxUploadBytes = 2L << 30, MaxPendingUploadBytes = 1L << 30 }).Should().Contain("MaxPendingUploadBytes");
        MediaOptionsRules.Validate(new MediaOptions { AlertPercent = 95, RefusePercent = 95 }).Should().Contain("RefusePercent");
        MediaOptionsRules.Validate(new MediaOptions { RefusePercent = 101 }).Should().Contain("RefusePercent");
        MediaOptionsRules.Validate(new MediaOptions { AlertPercent = 1, RefusePercent = 100 }).Should().BeNull();
    }

    [Fact]
    public async Task 啟動時不合格_AddHubMedia丟例外()
    {
        var start = () => HubTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Hub:Media:QuotaBytes"] = "0" });
        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Hub:Media:QuotaBytes*");

        await using var ok = await HubTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Hub:Media:MaxUploadBytes"] = "1048576" });
        ok.Services.GetRequiredService<IOptions<HubOptions>>().Value.Media.MaxUploadBytes.Should().Be(1048576, "設定綁得進 Hub:Media");
    }

    [Fact]
    public async Task Preflight列出config_media檢查()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            var paths = new HubPaths(dir);
            Directory.CreateDirectory(paths.ConfigDir);
            var config = Path.Combine(paths.ConfigDir, "appsettings.Production.json");

            await File.WriteAllTextAsync(config, "{\"Hub\":{\"ServerDomain\":\"hub.example.com\"}}");
            var good = await Preflight.RunAsync(paths, work, CancellationToken.None);
            var goodCheck = good.Checks.Single(c => c.Name == "config.media");
            goodCheck.Ok.Should().BeTrue();
            goodCheck.Detail.Should().BeNull();

            await File.WriteAllTextAsync(config, "{\"Hub\":{\"ServerDomain\":\"hub.example.com\",\"Media\":{\"MaxItemBlobBytes\":134217728}}}");
            var bad = await Preflight.RunAsync(paths, work, CancellationToken.None);
            bad.Ok.Should().BeFalse("部署前就擋下：項目 blob 上限超過型別上限");
            var badCheck = bad.Checks.Single(c => c.Name == "config.media");
            badCheck.Ok.Should().BeFalse();
            badCheck.Detail.Should().Contain("Hub:Media:MaxItemBlobBytes");
        }
        finally
        {
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }
}
