using Myriagent.Media;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tests.Media;

/// <summary>轉送得到的縮圖快取（WP4.7 計畫 D30）：只寫完整且魔數正確的、磁碟下限、超過上限刪最舊的、刪除含各版本</summary>
public sealed class ThumbCacheTests : IDisposable
{
    private readonly string dir = TempDir.Create();
    private readonly HubPaths paths;
    private readonly FakeDiskSpace disk = new(500L << 30);

    public ThumbCacheTests()
    {
        paths = new HubPaths(dir);
        paths.EnsureCreated();
    }

    public void Dispose() => TempDir.TryDelete(dir);

    private static string NewKey() => MediaKeyRules.Format("d_" + MediaSeed.NewRef(), MediaSeed.NewRef());

    private ThumbCache Create(MediaOptions? media = null) =>
        new(paths, Options.Create(new HubOptions { Media = media ?? new MediaOptions { MinFreeBytes = 1000 } }), disk, NullLogger<ThumbCache>.Instance);

    [Fact]
    public async Task 寫入後讀得回來_版本分開()
    {
        var cache = Create();
        var key = NewKey();
        var png = MediaBytes.Png(500);
        var jpeg = MediaBytes.Jpeg(600);

        (await cache.WriteAsync(key, null, png, CancellationToken.None)).Should().BeTrue();
        (await cache.WriteAsync(key, "17", jpeg, CancellationToken.None)).Should().BeTrue();

        cache.TryRead(key, null).Should().Equal(png);
        cache.TryRead(key, "17").Should().Equal(jpeg);
        cache.TryRead(key, "18").Should().BeNull();
        cache.Bytes.Should().Be(1100);
        Directory.EnumerateFiles(paths.TmpDir).Should().BeEmpty("暫存檔改名進快取");
    }

    [Fact]
    public async Task 覆寫同一個檔_合計照新的算()
    {
        var cache = Create();
        var key = NewKey();
        await cache.WriteAsync(key, null, MediaBytes.Png(500), CancellationToken.None);

        (await cache.WriteAsync(key, null, MediaBytes.Png(300, seed: 9), CancellationToken.None)).Should().BeTrue();

        cache.Bytes.Should().Be(300);
        cache.TryRead(key, null).Should().Equal(MediaBytes.Png(300, seed: 9));
    }

    [Theory]
    [InlineData("svg")]
    [InlineData("empty")]
    [InlineData("big")]
    [InlineData("badKey")]
    [InlineData("badVersion")]
    public async Task 不合格的不寫(string what)
    {
        var cache = Create(new MediaOptions { MinFreeBytes = 1000, MaxThumbBytes = 1000 });
        var key = what == "badKey" ? "not-a-key" : NewKey();
        var version = what == "badVersion" ? "abc" : null;
        var data = what switch
        {
            "svg" => System.Text.Encoding.UTF8.GetBytes("<svg/>"),
            "empty" => [],
            "big" => MediaBytes.Png(1001),
            _ => MediaBytes.Png(100),
        };

        (await cache.WriteAsync(key, version, data, CancellationToken.None)).Should().BeFalse();

        Directory.EnumerateFiles(paths.ThumbCacheDir).Should().BeEmpty();
        Directory.EnumerateFiles(paths.TmpDir).Should().BeEmpty();
        cache.Bytes.Should().Be(0);
    }

    [Fact]
    public async Task 磁碟可用空間低於下限時不寫()
    {
        var cache = Create(new MediaOptions { MinFreeBytes = 10_000 });
        disk.Free = 10_000 + 99;

        (await cache.WriteAsync(NewKey(), null, MediaBytes.Png(100), CancellationToken.None)).Should().BeFalse("寫了就低於下限");
        disk.Free = 10_000 + 100;
        (await cache.WriteAsync(NewKey(), null, MediaBytes.Png(100), CancellationToken.None)).Should().BeTrue("寫完正好是下限");
        Directory.EnumerateFiles(paths.ThumbCacheDir).Should().ContainSingle();
    }

    [Fact]
    public void 魔數不對的快取檔當成沒有()
    {
        var cache = Create();
        var key = NewKey();
        File.WriteAllBytes(Path.Combine(paths.ThumbCacheDir, key), System.Text.Encoding.UTF8.GetBytes("<html>"));

        cache.TryRead(key, null).Should().BeNull();
        cache.TryRead("not-a-key", null).Should().BeNull();
    }

    [Fact]
    public async Task Delete刪掉這個鍵的所有版本_不碰前綴相同的別的鍵()
    {
        var cache = Create();
        var key = NewKey();
        var other = NewKey();
        await cache.WriteAsync(key, null, MediaBytes.Png(100), CancellationToken.None);
        await cache.WriteAsync(key, "1", MediaBytes.Png(100), CancellationToken.None);
        await cache.WriteAsync(key, "2", MediaBytes.Png(100), CancellationToken.None);
        await cache.WriteAsync(other, null, MediaBytes.Png(100), CancellationToken.None);
        // 同一台、參照多一個字：鍵是另一個的前綴，但不是它的版本
        File.WriteAllBytes(Path.Combine(paths.ThumbCacheDir, key + "X"), MediaBytes.Png(50));

        cache.Delete(key).Should().Be(3);

        cache.TryRead(other, null).Should().NotBeNull();
        File.Exists(Path.Combine(paths.ThumbCacheDir, key + "X")).Should().BeTrue();
        cache.Delete("not-a-key").Should().Be(0);
    }

    [Fact]
    public async Task 超過上限時從最舊的刪起()
    {
        var cache = Create(new MediaOptions { MinFreeBytes = 1000, ThumbCacheBytes = 250 });
        var keys = new[] { NewKey(), NewKey(), NewKey() };
        for (var i = 0; i < keys.Length; i++)
        {
            await cache.WriteAsync(keys[i], null, MediaBytes.Png(100, seed: i), CancellationToken.None);
            File.SetLastWriteTimeUtc(Path.Combine(paths.ThumbCacheDir, keys[i]), new DateTime(2020, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));
        }

        cache.TryRead(keys[0], null).Should().BeNull("第三個寫進來時合計 300 > 250，刪最舊的");
        cache.TryRead(keys[1], null).Should().NotBeNull();
        cache.TryRead(keys[2], null).Should().NotBeNull();
        cache.Bytes.Should().Be(200);

        cache.Trim(100).Should().Be(1);
        cache.TryRead(keys[1], null).Should().BeNull();
        cache.Bytes.Should().Be(100);
    }

    [Fact]
    public void 啟動時清掉上次留下的暫存檔_不碰別的暫存檔()
    {
        File.WriteAllBytes(Path.Combine(paths.TmpDir, ThumbCache.TmpPrefix + "abc"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(paths.TmpDir, "upload-abc"), [1, 2, 3]);

        Create().Initialize();

        Directory.EnumerateFiles(paths.TmpDir).Select(Path.GetFileName).Should().Equal("upload-abc");
    }

    [Fact]
    public void 大小一開始掃一次資料夾()
    {
        File.WriteAllBytes(Path.Combine(paths.ThumbCacheDir, NewKey()), MediaBytes.Png(123));

        Create().Bytes.Should().Be(123);
    }
}
