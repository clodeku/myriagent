using System.Text;
using System.Text.Json;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Tests.Media;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Ops;

/// <summary>備份的 blob 清單（WP4.7 計畫 D18）：blob 不備份，manifest 記個數、合計與清單的 SHA-256；版本號仍是 1、舊的 manifest 讀得過</summary>
public sealed class BackupInventoryTests
{
    [Fact]
    public async Task 備份有blob清單_依sha排序_LF_manifest的雜湊等於檔案()
    {
        await using var host = await MediaHost.StartAsync();
        var a = await MediaSeed.StoreBlobAsync(host, MediaBytes.Random(300, seed: 21), BlobKinds.ItemBody);
        var b = await MediaSeed.StoreBlobAsync(host, MediaBytes.Png(400, seed: 22), BlobKinds.Thumb);
        var c = await MediaSeed.StoreBlobAsync(host, MediaBytes.Random(500, seed: 23), BlobKinds.Upload);

        var result = await host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false);

        result.Ok.Should().BeTrue(result.Error);
        var inventory = Path.Combine(result.Folder!, BackupService.BlobInventoryFile);
        var bytes = await File.ReadAllBytesAsync(inventory);
        bytes.Should().NotContain((byte)'\r');
        var expected = new[] { (a, 300, BlobKinds.ItemBody), (b, 400, BlobKinds.Thumb), (c, 500, BlobKinds.Upload) }
            .OrderBy(x => x.Item1, StringComparer.Ordinal)
            .Select(x => x.Item1 + " " + x.Item2 + " " + x.Item3 + "\n");
        Encoding.UTF8.GetString(bytes).Should().Be(string.Concat(expected));

        var manifest = BackupService.ReadManifest(result.Folder!)!;
        manifest.Format.Should().Be(1, "版本號不變");
        manifest.BlobCount.Should().Be(3);
        manifest.BlobBytes.Should().Be(1200);
        manifest.BlobInventorySha256.Should().Be(BackupService.Sha256File(inventory));
        manifest.Files.Select(f => f.Name).Should().BeEquivalentTo("hub.db", "sync.db");
        Directory.EnumerateFiles(host.Host.Paths.BlobsDir, "*", SearchOption.AllDirectories).Should().HaveCount(3, "blob 本身不複製");
        Directory.EnumerateFiles(result.Folder!, "*", SearchOption.AllDirectories).Should().HaveCount(4);
    }

    [Fact]
    public async Task 沒有blob時清單是空檔()
    {
        await using var owner = await OwnerHost.StartAsync();
        var result = await owner.Host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false);

        result.Ok.Should().BeTrue(result.Error);
        var inventory = Path.Combine(result.Folder!, BackupService.BlobInventoryFile);
        new FileInfo(inventory).Length.Should().Be(0);
        var manifest = BackupService.ReadManifest(result.Folder!)!;
        manifest.BlobCount.Should().Be(0);
        manifest.BlobBytes.Should().Be(0);
        manifest.BlobInventorySha256.Should().Be(BackupService.Sha256File(inventory));
    }

    [Fact]
    public void 舊格式的manifest讀得過_新欄位是null()
    {
        var dir = TempDir.Create();
        try
        {
            // 之前的版本寫的 manifest：沒有 blob 的三個欄位
            var old = new
            {
                format = 1,
                createdAt = 1_760_000_000_000L,
                version = "1.0.0",
                build = "abcdef0",
                reason = "daily",
                files = new[] { new { name = "hub.db", bytes = 10L, sha256 = new string('0', 64), integrity = "ok", tables = new Dictionary<string, long> { ["audit"] = 1 } } },
            };
            File.WriteAllText(Path.Combine(dir, BackupService.ManifestFile), JsonSerializer.Serialize(old, StatusJson.Options));

            var manifest = BackupService.ReadManifest(dir)!;

            manifest.Format.Should().Be(1);
            manifest.Build.Should().Be("abcdef0");
            manifest.Files.Single().Name.Should().Be("hub.db");
            manifest.Files.Single().Tables["audit"].Should().Be(1);
            manifest.BlobCount.Should().BeNull();
            manifest.BlobBytes.Should().BeNull();
            manifest.BlobInventorySha256.Should().BeNull();
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public void 新格式的manifest照樣有原本的欄位_多三個選用欄位()
    {
        var manifest = new BackupManifest(1, 1, "v", "b", "r", [], 2, 3, new string('a', 64));
        var json = JsonSerializer.SerializeToElement(manifest, StatusJson.Options);

        json.EnumerateObject().Select(p => p.Name).Should()
            .Equal("format", "createdAt", "version", "build", "reason", "files", "blobCount", "blobBytes", "blobInventorySha256");
    }
}
