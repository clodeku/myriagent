using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Tests.Media;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Ops;

/// <summary>
/// 備份的磁碟空間與合計上限（2026-10-25）：存了之後可用空間會低於 <c>Hub:Backup:MinFreeBytes</c> 就先刪最舊的備份，刪了也不夠就不備份、一份都不刪；
/// 合計超過 <c>MaxTotalBytes</c> 從最舊的刪，最新的一定留。假時鐘是 2026-10-11 09:00（UTC），手動備份的資料夾是 20261011-090000
/// </summary>
public sealed class BackupSpaceTests
{
    private const long Mb = 1L << 20;

    private const long OldFolderBytes = 16 * Mb;

    private static readonly string[] OldFolders = ["20261008", "20261009", "20261010"];

    [Fact]
    public async Task 空間不夠_先刪最舊的備份騰出空間_照常備份()
    {
        const long minFree = 64 * Mb;
        await using var host = await StartAsync(new FakeDiskSpace(minFree), minFree);
        SeedOldFolders(host);

        var result = await Backup(host).RunAsync("test", scheduled: false);

        result.Ok.Should().BeTrue(result.Error);
        result.Expired.Should().Equal("20261008");
        Folders(host).Should().BeEquivalentTo(["20261009", "20261010", "20261011-090000"],
            "這次約要資料庫大小的空間（遠小於 16 MB）：刪最舊的一份就夠，其他的留著");
    }

    [Fact]
    public async Task 刪了最舊的也不夠_不備份_一份都不刪_記下原因()
    {
        await using var host = await StartAsync(new FakeDiskSpace(1L << 30), minFree: 10L << 30);
        SeedOldFolders(host);

        var result = await Backup(host).RunAsync("test", scheduled: false);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("磁碟空間不夠").And.Contain("Hub:Backup:MinFreeBytes");
        Folders(host).Should().BeEquivalentTo(OldFolders, "這次不會有新的，舊的就是僅有的備份");
        Backup(host).LastState!.LastError.Should().Contain(nameof(BackupDiskLowException));
        Backup(host).LastState!.LastSuccessAt.Should().BeNull("backup_stale 告警照常會出現");
    }

    [Fact]
    public async Task 可用空間量不到_當成不夠()
    {
        await using var host = await StartAsync(new FakeDiskSpace(-1), minFree: 0);

        var result = await Backup(host).RunAsync("test", scheduled: false);

        result.Ok.Should().BeFalse();
        Folders(host).Should().BeEmpty();
    }

    [Fact]
    public async Task 合計超過上限_從最舊的刪_最新的留()
    {
        await using var host = await StartAsync(new FakeDiskSpace(500L << 30), minFree: 0,
            new Dictionary<string, string?> { ["Hub:Backup:MaxTotalBytes"] = (40 * Mb).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        SeedOldFolders(host);

        var result = await Backup(host).RunAsync("test", scheduled: false);

        result.Ok.Should().BeTrue(result.Error);
        result.Expired.Should().Equal("20261008");
        Folders(host).Should().BeEquivalentTo(["20261009", "20261010", "20261011-090000"], "16＋16＋新的一份不到 40 MB");
    }

    private static Task<HubTestHost> StartAsync(FakeDiskSpace disk, long minFree, Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings["Hub:Backup:MinFreeBytes"] = minFree.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return HubTestHost.StartAsync(settings: settings, services: s => s.AddSingleton<IDiskSpaceProbe>(disk));
    }

    private static BackupService Backup(HubTestHost host) => host.Services.GetRequiredService<BackupService>();

    /// <summary>三份假的舊備份，各 16 MB（只設長度，不真的寫內容）</summary>
    private static void SeedOldFolders(HubTestHost host)
    {
        foreach (var name in OldFolders)
        {
            var dir = Path.Combine(host.Paths.DailyBackupsDir, name);
            Directory.CreateDirectory(dir);
            using var file = new FileStream(Path.Combine(dir, "sync.db"), FileMode.CreateNew);
            file.SetLength(OldFolderBytes);
        }
    }

    private static List<string> Folders(HubTestHost host) =>
        Directory.Exists(host.Paths.DailyBackupsDir)
            ? Directory.GetDirectories(host.Paths.DailyBackupsDir).Select(Path.GetFileName).OfType<string>().ToList()
            : [];
}
