using System.Text;
using Myriagent.Security;
using Myriagent.Update;
using MyriagentHub.Admin;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Releases;
using MyriagentHub.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Tests.Releases;

/// <summary>
/// 收進來的桌面版（U2 T3）：路徑、app_releases 資料表、啟動對帳、保存（先改資料列再刪資料夾、刪不掉留到下次）、撤回與最新版快取、事件、設定驗證。
/// 版本的檔案由測試直接放（manifest 是任意位元組：store 只比 SHA-256，不解析）。
/// 斷言不依賴「這段期間沒有別人跑保存」：收件匣（T4 起）每一輪都會呼叫 PruneAsync，要確定留著的資料夾一律以開著的檔擋住（FileShare.Read，同下載）
/// </summary>
public sealed class AppReleaseStoreTests
{
    private const string TestKid = "0123456789abcdef";

    private static readonly string TestSig = Base64UrlRules.Encode(new byte[64]);

    private static readonly Dictionary<string, string?> KeepOne = new() { ["Hub:Releases:KeepAvailable"] = "1" };

    /// <summary>在 app-releases\&lt;release&gt;\ 放好三個檔（<paramref name="zip"/> 是 false 時不放 zip），回傳對應的 available 資料列</summary>
    private static AppReleaseRecord WriteRelease(HubPaths paths, string release, string kid = TestKid, bool zip = true)
    {
        ReleaseIdentity.TryParseRelease(release, out var parsed).Should().BeTrue(release);
        var identity = parsed!;
        var names = ReleasePackageRules.FileNames(release);
        var folder = Path.Combine(paths.AppReleasesDir, release);
        Directory.CreateDirectory(folder);
        var manifest = Encoding.UTF8.GetBytes("{\"release\":\"" + release + "\"}\n");
        var zipBytes = Encoding.UTF8.GetBytes("zip " + release);
        File.WriteAllBytes(Path.Combine(folder, names.Manifest), manifest);
        File.WriteAllText(Path.Combine(folder, names.Signature), ReleaseManifestRules.SerializeSignature(new ReleaseSignature(kid, TestSig)));
        if (zip)
            File.WriteAllBytes(Path.Combine(folder, names.Zip), zipBytes);
        return new AppReleaseRecord
        {
            Release = release,
            Version = identity.Version.ToString(),
            Build = identity.Build,
            Commit = identity.Build + new string('0', 33),
            ManifestSha256 = ReleaseManifestRules.Sha256Hex(manifest),
            ZipSha256 = ReleaseManifestRules.Sha256Hex(zipBytes),
            ZipBytes = zipBytes.Length,
            ProtocolVersion = 1,
            MinRuntime = AppReleaseRules.MinRuntimeText(new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0))),
            Kid = kid,
            State = AppReleaseStates.Available,
            IngestedAt = 1_792_000_000_000,
        };
    }

    /// <summary>直接寫資料表（不經 store：模擬上一次執行留下的狀態，下次啟動才對帳）</summary>
    private static async Task InsertAsync(HubTestHost host, params AppReleaseRecord[] rows)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        db.AppReleases.AddRange(rows);
        await db.SaveChangesAsync();
    }

    /// <summary>以下載的方式開著 zip（套件端點用 FileShare.Read，不給刪除權）：開著的期間誰都刪不掉那個版本的資料夾</summary>
    private static FileStream HoldZip(AppReleaseStore store, string release) =>
        new(store.ZipPathOf(release), FileMode.Open, FileAccess.Read, FileShare.Read);

    private static async Task<string?> StateAsync(AppReleaseStore store, string release) => (await store.FindAsync(release, CancellationToken.None))?.State;

    [Fact]
    public async Task 路徑配置_收件匣_版本資料夾_回執()
    {
        var fixedPaths = new HubPaths(@"C:\ProgramData\MyriagentHub");
        fixedPaths.AdminInboxAppReleaseDir.Should().Be(@"C:\ProgramData\MyriagentHub\admin-inbox\app-release");
        fixedPaths.AppReleasesDir.Should().Be(@"C:\ProgramData\MyriagentHub\app-releases");
        fixedPaths.AppReleaseAckFile.Should().Be(@"C:\ProgramData\MyriagentHub\status\app-release.json");

        await using var host = await HubTestHost.StartAsync();
        Directory.Exists(host.Paths.AdminInboxAppReleaseDir).Should().BeTrue("服務啟動時自己建收件匣（EnsureCreated），不碰 ACL");
        Directory.Exists(host.Paths.AppReleasesDir).Should().BeTrue();
        BackupService.DatabaseFiles(host.Paths).Should().BeEquivalentTo(new[] { host.Paths.HubDb, host.Paths.SyncDb },
            "每日備份只有兩個資料庫，不含 app-releases（可由 GitHub 重建，R29）");

        var store = host.Services.GetRequiredService<AppReleaseStore>();
        const string release = "0.2.1523-ab12cd3";
        var folder = Path.Combine(host.Paths.AppReleasesDir, release);
        store.FolderOf(release).Should().Be(folder);
        store.ZipPathOf(release).Should().Be(Path.Combine(folder, "myriagent-0.2.1523-ab12cd3.zip"));
        store.ManifestPathOf(release).Should().Be(Path.Combine(folder, "myriagent-0.2.1523-ab12cd3.manifest.json"));
        store.SignaturePathOf(release).Should().Be(Path.Combine(folder, "myriagent-0.2.1523-ab12cd3.manifest.sig"));
        store.PartialFolderOf("rel-0.2.1523-ab12cd3-1").Should().Be(Path.Combine(host.Paths.AppReleasesDir, ".partial-rel-0.2.1523-ab12cd3-1"));
        foreach (var bad in new[] { "../0.2.1-ab12cd3", "0.2.1-AB12CD3", "0.2.01-ab12cd3", "0.2.1-dev", "" })
            FluentActions.Invoking(() => store.FolderOf(bad)).Should().Throw<ArgumentException>(bad);
        FluentActions.Invoking(() => store.ZipPathOf("..")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => store.PartialFolderOf("a..b")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => store.PartialFolderOf(@"a\b")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task 資料表app_releases的欄位照規格()
    {
        await using var host = await HubTestHost.StartAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var columns = await db.Database
            .SqlQueryRaw<string>("SELECT name || ':' || type || ':' || \"notnull\" || ':' || pk AS Value FROM pragma_table_info('app_releases') ORDER BY cid")
            .ToListAsync();
        columns.Should().Equal(
            "release:TEXT:1:1", "version:TEXT:1:0", "build:TEXT:1:0", "commit:TEXT:1:0", "manifest_sha256:TEXT:1:0", "zip_sha256:TEXT:1:0",
            "zip_bytes:INTEGER:1:0", "protocol_version:INTEGER:1:0", "min_runtime:TEXT:1:0", "kid:TEXT:1:0", "state:TEXT:1:0",
            "ingested_at:INTEGER:1:0", "withdrawn_at:INTEGER:0:0");
        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(m => m.EndsWith("_AppReleases", StringComparison.Ordinal), "只加表的遷移：服務回滾到舊版仍讀得動");
    }

    [Fact]
    public async Task 啟動對帳_刪partial與孤兒資料夾_缺檔的可用版本標pruned()
    {
        var dir = TempDir.Create();
        try
        {
            var paths = new HubPaths(dir);
            await using (var first = await HubTestHost.StartAsync(dataDir: dir))
            {
                var good = WriteRelease(paths, "0.2.6-bbbbbb6");
                var noZip = WriteRelease(paths, "0.2.7-ccccccc", zip: false);
                var noFolder = WriteRelease(paths, "0.2.8-ddddddd");
                Directory.Delete(Path.Combine(paths.AppReleasesDir, noFolder.Release), recursive: true);
                var withdrawn = WriteRelease(paths, "0.2.4-aaaaaa4");
                withdrawn.State = AppReleaseStates.Withdrawn;
                withdrawn.WithdrawnAt = 1_792_000_100_000;
                await InsertAsync(first, good, noZip, noFolder, withdrawn);
            }
            WriteRelease(paths, "0.2.5-aaaaaa5");
            Directory.CreateDirectory(Path.Combine(paths.AppReleasesDir, ".partial-abc-123"));
            File.WriteAllText(Path.Combine(paths.AppReleasesDir, ".partial-abc-123", "x.zip"), "partial");
            Directory.CreateDirectory(Path.Combine(paths.AppReleasesDir, ".partial-a..b"));
            Directory.CreateDirectory(Path.Combine(paths.AppReleasesDir, "keep-me"));
            File.WriteAllText(Path.Combine(paths.AppReleasesDir, "notes.txt"), "not ours");

            await using var host = await HubTestHost.StartAsync(dataDir: dir);
            var store = host.Services.GetRequiredService<AppReleaseStore>();

            Directory.Exists(Path.Combine(paths.AppReleasesDir, ".partial-abc-123")).Should().BeFalse("收件留下的副本");
            Directory.Exists(Path.Combine(paths.AppReleasesDir, ".partial-a..b")).Should().BeTrue("id 不合法的不碰");
            Directory.Exists(Path.Combine(paths.AppReleasesDir, "keep-me")).Should().BeTrue("名稱不是 release 的不碰");
            File.Exists(Path.Combine(paths.AppReleasesDir, "notes.txt")).Should().BeTrue();
            Directory.Exists(Path.Combine(paths.AppReleasesDir, "0.2.5-aaaaaa5")).Should().BeFalse("沒有資料列的版本資料夾（改名之後、寫資料表之前當掉）");

            (await StateAsync(store, "0.2.7-ccccccc")).Should().Be(AppReleaseStates.Pruned, "缺 zip");
            (await StateAsync(store, "0.2.8-ddddddd")).Should().Be(AppReleaseStates.Pruned, "整個資料夾不在");
            Directory.Exists(Path.Combine(paths.AppReleasesDir, "0.2.7-ccccccc")).Should().BeFalse("改標 pruned 之後保存規則刪掉剩下的檔");
            (await StateAsync(store, "0.2.4-aaaaaa4")).Should().Be(AppReleaseStates.Withdrawn);
            Directory.Exists(Path.Combine(paths.AppReleasesDir, "0.2.4-aaaaaa4")).Should().BeFalse("撤回的版本，檔案在沒有下載時刪掉");
            (await StateAsync(store, "0.2.6-bbbbbb6")).Should().Be(AppReleaseStates.Available);
            File.Exists(store.ZipPathOf("0.2.6-bbbbbb6")).Should().BeTrue();

            store.Latest.Should().Be(new AppReleaseInfo("0.2.6-bbbbbb6", "0.2.6"), "比它新的 0.2.7、0.2.8 缺檔，不提供");
            store.LatestPayload!.Release.Should().Be("0.2.6-bbbbbb6");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 刪除失敗留到下次()
    {
        await using var host = await HubTestHost.StartAsync(settings: KeepOne);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        await store.AddAsync(WriteRelease(host.Paths, "0.2.10-aaaaaa0"), CancellationToken.None);

        // 先開著 zip（下載中）再讓它超出保留數量：之後不論哪一次保存（包括別人觸發的）都刪不掉
        using (HoldZip(store, "0.2.10-aaaaaa0"))
        {
            await store.AddAsync(WriteRelease(host.Paths, "0.2.11-aaaaaa1"), CancellationToken.None);
            await store.Invoking(s => s.PruneAsync(CancellationToken.None)).Should().NotThrowAsync("刪不掉只記資訊，不往外丟");
            (await StateAsync(store, "0.2.10-aaaaaa0")).Should().Be(AppReleaseStates.Pruned, "先改資料列（latest 與套件端點立刻不再提供），再刪資料夾");
            store.Latest!.Release.Should().Be("0.2.11-aaaaaa1");
            File.Exists(store.ZipPathOf("0.2.10-aaaaaa0")).Should().BeTrue("下載中的 zip 刪不掉");
            File.Exists(store.ManifestPathOf("0.2.10-aaaaaa0")).Should().BeTrue("先刪 zip：刪不掉就整個資料夾留著，下次再刪");
            File.Exists(store.SignaturePathOf("0.2.10-aaaaaa0")).Should().BeTrue();

            using (HoldZip(store, "0.2.11-aaaaaa1"))
            {
                (await store.WithdrawAsync("0.2.11-aaaaaa1", CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.Withdrawn);
                store.Latest.Should().BeNull("唯一的可用版本撤回了");
                await store.PruneAsync(CancellationToken.None);
                (await StateAsync(store, "0.2.11-aaaaaa1")).Should().Be(AppReleaseStates.Withdrawn);
                File.Exists(store.ZipPathOf("0.2.11-aaaaaa1")).Should().BeTrue("撤回的版本也一樣：下載中刪不掉，進行中的下載照樣傳完");
                File.Exists(store.ManifestPathOf("0.2.11-aaaaaa1")).Should().BeTrue();
            }
        }

        await store.PruneAsync(CancellationToken.None);
        Directory.Exists(store.FolderOf("0.2.10-aaaaaa0")).Should().BeFalse("關掉之後，下一次保存就刪掉");
        Directory.Exists(store.FolderOf("0.2.11-aaaaaa1")).Should().BeFalse();
        (await StateAsync(store, "0.2.10-aaaaaa0")).Should().Be(AppReleaseStates.Pruned);
        (await StateAsync(store, "0.2.11-aaaaaa1")).Should().Be(AppReleaseStates.Withdrawn, "刪檔不改撤回的狀態");

        (await store.WithdrawAsync("0.2.10-aaaaaa0", CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.Withdrawn, "清除過的版本也能撤回");
        (await StateAsync(store, "0.2.10-aaaaaa0")).Should().Be(AppReleaseStates.Withdrawn);
    }

    [Fact]
    public async Task 撤回後Latest改成前一個可用版本並觸發LatestChanged()
    {
        await using var host = await HubTestHost.StartAsync();
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        foreach (var release in new[] { "0.2.9-aaaaaa9", "0.2.10-aaaaaa0", "0.2.11-aaaaaa1" })
            await store.AddAsync(WriteRelease(host.Paths, release), CancellationToken.None);
        store.Latest!.Release.Should().Be("0.2.11-aaaaaa1");
        var changed = 0;
        store.LatestChanged += () => changed++;

        using (HoldZip(store, "0.2.11-aaaaaa1"))
        {
            (await store.WithdrawAsync("0.2.9-aaaaaa9", CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.Withdrawn);
            changed.Should().Be(0, "撤回的不是最新版");

            (await store.WithdrawAsync("0.2.11-aaaaaa1", CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.Withdrawn);
            store.Latest.Should().Be(new AppReleaseInfo("0.2.10-aaaaaa0", "0.2.10"));
            store.LatestPayload!.Release.Should().Be("0.2.10-aaaaaa0");
            changed.Should().Be(1);
            var row = await store.FindAsync("0.2.11-aaaaaa1", CancellationToken.None);
            row!.State.Should().Be(AppReleaseStates.Withdrawn);
            row.WithdrawnAt.Should().Be(host.Clock.NowMs);

            await store.PruneAsync(CancellationToken.None);
            Directory.Exists(store.FolderOf("0.2.11-aaaaaa1")).Should().BeTrue("撤回只改資料列；下載中的檔案開著，資料夾刪不掉，進行中的下載照樣傳完");
            Directory.Exists(store.FolderOf("0.2.9-aaaaaa9")).Should().BeFalse("沒有下載的撤回版本在保存時刪掉");
        }

        (await store.WithdrawAsync("0.2.11-aaaaaa1", CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.AlreadyWithdrawn);
        (await store.WithdrawAsync("0.2.99-fffffff", CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.NotFound);
        (await store.WithdrawAsync("not-a-release", CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.NotFound);
        changed.Should().Be(1, "沒有改變最新版的呼叫不觸發");
        await store.Invoking(s => s.AddAsync(WriteRelease(host.Paths, "0.2.11-aaaaaa1"), CancellationToken.None))
            .Should().ThrowAsync<DbUpdateException>("撤回過的同名版本永遠不能再收（主鍵，U2 D14）");
        (await StateAsync(store, "0.2.11-aaaaaa1")).Should().Be(AppReleaseStates.Withdrawn);
        changed.Should().Be(1);

        await store.PruneAsync(CancellationToken.None);
        Directory.Exists(store.FolderOf("0.2.11-aaaaaa1")).Should().BeFalse("關掉之後保存就刪掉");
        (await StateAsync(store, "0.2.11-aaaaaa1")).Should().Be(AppReleaseStates.Withdrawn, "刪檔不改撤回的狀態");

        (await store.WithdrawAsync("0.2.10-aaaaaa0", CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.Withdrawn);
        store.Latest.Should().BeNull("沒有可用版本了");
        store.LatestPayload.Should().BeNull();
        changed.Should().Be(2);
    }

    [Fact]
    public async Task 新增版本_LatestPayload帶manifest原文與簽章_觸發Ingested與LatestChanged()
    {
        await using var host = await HubTestHost.StartAsync();
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        store.Latest.Should().BeNull();
        store.LatestPayload.Should().BeNull();
        var ingested = new List<AppReleaseInfo>();
        var changed = 0;
        store.Ingested += _ => throw new InvalidOperationException("處理器的例外不影響寫入，也不擋後面的處理器");
        store.Ingested += ingested.Add;
        store.LatestChanged += () => changed++;

        var record = WriteRelease(host.Paths, "0.2.30-abcdef0");
        await store.AddAsync(record, CancellationToken.None);

        ingested.Should().Equal(new AppReleaseInfo("0.2.30-abcdef0", "0.2.30"));
        changed.Should().Be(1);
        store.Latest.Should().Be(new AppReleaseInfo("0.2.30-abcdef0", "0.2.30"));
        var payload = store.LatestPayload!;
        payload.Release.Should().Be("0.2.30-abcdef0");
        payload.Version.Should().Be("0.2.30");
        payload.Build.Should().Be("abcdef0");
        Convert.FromBase64String(payload.Manifest).Should().Equal(File.ReadAllBytes(store.ManifestPathOf("0.2.30-abcdef0")), "manifest 原文的標準 base64");
        payload.Sig.Should().Be(new ReleaseSignature(TestKid, TestSig));
        payload.ZipBytes.Should().Be(record.ZipBytes);
        (await store.FindAsync("0.2.30-abcdef0", CancellationToken.None)).Should().BeEquivalentTo(record);
        (await store.ListAsync(CancellationToken.None)).Select(r => r.Release).Should().Equal("0.2.30-abcdef0");

        var badVersion = WriteRelease(host.Paths, "0.2.31-abcdef1");
        badVersion.Version = "0.2.32";
        await store.Invoking(s => s.AddAsync(badVersion, CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
        var notAvailable = WriteRelease(host.Paths, "0.2.33-abcdef3");
        notAvailable.State = AppReleaseStates.Withdrawn;
        await store.Invoking(s => s.AddAsync(notAvailable, CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
        var badRelease = WriteRelease(host.Paths, "0.2.34-abcdef4");
        badRelease.Release = "0.2.34-ABCDEF4";
        await store.Invoking(s => s.AddAsync(badRelease, CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
        (await store.ListAsync(CancellationToken.None)).Should().HaveCount(1, "不合格的資料列不寫入");
        ingested.Should().HaveCount(1);
        changed.Should().Be(1);
    }

    [Fact]
    public async Task manifest雜湊_簽章kid_zip大小不符的版本不提供_資料表不改()
    {
        var dir = TempDir.Create();
        try
        {
            var paths = new HubPaths(dir);
            var keepFive = new Dictionary<string, string?> { ["Hub:Releases:KeepAvailable"] = "5" };
            await using (var first = await HubTestHost.StartAsync(dataDir: dir, settings: keepFive))
            {
                var good = WriteRelease(paths, "0.2.50-aaaaaa0");
                var badHash = WriteRelease(paths, "0.2.51-aaaaaa1");
                badHash.ManifestSha256 = new string('0', 64);
                var badKid = WriteRelease(paths, "0.2.52-aaaaaa2");
                badKid.Kid = "fedcba9876543210";
                var badSize = WriteRelease(paths, "0.2.53-aaaaaa3");
                badSize.ZipBytes += 1;
                await InsertAsync(first, good, badHash, badKid, badSize);
            }

            await using var host = await HubTestHost.StartAsync(dataDir: dir, settings: keepFive);
            var store = host.Services.GetRequiredService<AppReleaseStore>();
            store.Latest!.Release.Should().Be("0.2.50-aaaaaa0", "比它新的三版都跟資料表不符，跳過");
            var rows = await store.ListAsync(CancellationToken.None);
            rows.Should().HaveCount(4);
            rows.Should().OnlyContain(r => r.State == AppReleaseStates.Available, "只是不提供，資料表不改");
            foreach (var row in rows)
                Directory.Exists(store.FolderOf(row.Release)).Should().BeTrue(row.Release);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 設定不合格_服務啟動失敗_preflight也擋下()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            var keepZero = () => HubTestHost.StartAsync(dataDir: dir, settings: new Dictionary<string, string?> { ["Hub:Releases:KeepAvailable"] = "0" });
            (await keepZero.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Hub:Releases:KeepAvailable*：0");
            var pollTooSlow = () => HubTestHost.StartAsync(dataDir: dir, settings: new Dictionary<string, string?> { ["Hub:Releases:InboxPollSeconds"] = "3601" });
            (await pollTooSlow.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Hub:Releases:InboxPollSeconds*：3601");

            var paths = new HubPaths(dir);
            Directory.CreateDirectory(paths.ConfigDir);
            var config = Path.Combine(paths.ConfigDir, "appsettings.Production.json");
            await File.WriteAllTextAsync(config, "{\"Hub\":{\"ServerDomain\":\"hub.example.com\"}}");
            var ok = (await Preflight.RunAsync(paths, work, CancellationToken.None)).Checks.Single(c => c.Name == "config.releases");
            ok.Ok.Should().BeTrue();
            ok.Detail.Should().Be("keepAvailable=3 maxZipMb=256 minFreeDiskMb=2048 inboxPollSeconds=30");

            await File.WriteAllTextAsync(config, "{\"Hub\":{\"ServerDomain\":\"hub.example.com\",\"Releases\":{\"MaxZipMb\":0}}}");
            var result = await Preflight.RunAsync(paths, work, CancellationToken.None);
            result.Ok.Should().BeFalse("部署前就擋下：新版讀到不合法的設定");
            var check = result.Checks.Single(c => c.Name == "config.releases");
            check.Ok.Should().BeFalse();
            check.Detail.Should().Contain("Hub:Releases:MaxZipMb").And.EndWith("：0");
        }
        finally
        {
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }
}
