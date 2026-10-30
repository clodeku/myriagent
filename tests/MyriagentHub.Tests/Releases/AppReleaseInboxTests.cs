using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Releases;
using MyriagentHub.Tests.Media;
using MyriagentHub.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Tests.Releases;

/// <summary>
/// 收件匣（U2 T4）：送件腳本放進 admin-inbox\app-release\ 的三個檔與 request.json，服務複製、以內建金鑰驗證、記錄、回執。
/// 測試主機的收件匣每秒輪詢一次（HubTestHost 的 Hub:Releases:InboxPollSeconds=1），內建金鑰換成夾具的暫時金鑰（TestBuiltInReleaseKeys），
/// 磁碟可用空間固定（FakeDiskSpace：不看跑測試那台的磁碟）
/// </summary>
public sealed class AppReleaseInboxTests
{
    private const string Release301 = "0.2.301-bc23de4";
    private const string Release302 = "0.2.302-cd34ef5";
    private const string Release303 = "0.2.303-de45f06";

    /// <summary>
    /// 測試主機的磁碟可用空間（500 GB）：不看跑測試那台的磁碟。Hub:Releases:MinFreeDiskMb 預設 2 GiB，GitHub 的 Windows runner 或快滿的開發機
    /// 會讓每個走到第 4 步的收件都回 disk_full（同 MediaRegistrationTests 固定 blob 的可用空間）
    /// </summary>
    private const long PlentyOfDisk = 500L << 30;

    [Fact]
    public async Task 收件成功_寫資料表_回執ok_刪收件匣_稽核_發出Ingested()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        var ingested = new ConcurrentQueue<AppReleaseInfo>();
        store.Ingested += ingested.Enqueue;

        fixture.Write(host.Paths.AdminInboxAppReleaseDir, "pub-1", host.Clock.NowMs);
        var receipt = await WaitProcessedAsync(host, "pub-1");

        receipt.Should().Be(new AppReleaseReceipt("pub-1", true, fixture.Release, AppReleaseIngestCodes.Ingested, host.Clock.NowMs));
        var raw = await File.ReadAllBytesAsync(host.Paths.AppReleaseAckFile);
        raw.Should().OnlyContain(b => b < 0x80, "送件腳本以 Windows PowerShell 5.1 讀回執（D19）");
        using (var doc = JsonDocument.Parse(raw))
            doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("id", "ok", "release", "message", "processedAt");

        var row = await store.FindAsync(fixture.Release, CancellationToken.None);
        row.Should().BeEquivalentTo(RecordOf(fixture.Default, host.Clock.NowMs));
        row!.MinRuntime.Should().Be("Microsoft.WindowsDesktop.App 10.0.0");
        row.Kid.Should().Be(fixture.Keys[0].Kid);
        row.ZipBytes.Should().Be(fixture.Default.Zip.Length);
        row.WithdrawnAt.Should().BeNull();

        File.ReadAllBytes(store.ZipPathOf(fixture.Release)).Should().Equal(fixture.Default.Zip);
        File.ReadAllBytes(store.ManifestPathOf(fixture.Release)).Should().Equal(fixture.Default.Manifest);
        File.ReadAllText(store.SignaturePathOf(fixture.Release)).Should().Be(fixture.Default.Signature);
        Directory.EnumerateFileSystemEntries(host.Paths.AdminInboxAppReleaseDir).Should().BeEmpty("收件成功後三個檔與 request.json 都刪掉");
        PartialFolders(host).Should().BeEmpty();

        var audit = (await AuditsAsync(host)).Should().ContainSingle(a => a.Action == AuditActions.AppReleaseIngested).Subject;
        audit.ActorType.Should().Be(AuditActors.System);
        audit.Target.Should().Be(fixture.Release);
        audit.Outcome.Should().Be(AuditOutcomes.Ok);
        using (var detail = JsonDocument.Parse(audit.Detail!))
        {
            var d = detail.RootElement;
            d.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("release", "commit", "kid", "zipBytes", "manifestSha256");
            d.GetProperty("release").GetString().Should().Be(fixture.Release);
            d.GetProperty("commit").GetString().Should().Be(row.Commit);
            d.GetProperty("kid").GetString().Should().Be(fixture.Keys[0].Kid);
            d.GetProperty("zipBytes").GetInt64().Should().Be(fixture.Default.Zip.Length);
            d.GetProperty("manifestSha256").GetString().Should().Be(fixture.Default.ManifestSha256);
        }

        ingested.Should().ContainSingle().Which.Should().Be(new AppReleaseInfo(fixture.Release, "0.2.300"));
        store.Latest.Should().Be(new AppReleaseInfo(fixture.Release, "0.2.300"));
    }

    [Fact]
    public async Task request的release與檔名或manifest不符_拒收()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        var inbox = host.Paths.AdminInboxAppReleaseDir;

        // 三個檔是 0.2.300 的，request 寫的是 0.2.301：照 request 的 release 找檔，找不到
        ReleasePackageFixture.WriteFiles(inbox, fixture.Default);
        ReleasePackageFixture.WriteRequest(inbox, "pub-a", Release301, host.Clock.NowMs);
        var missing = await WaitProcessedAsync(host, "pub-a");
        missing.Should().Be(new AppReleaseReceipt("pub-a", false, Release301, AppReleaseIngestCodes.MissingFile, host.Clock.NowMs));

        // 檔名與 request 都是 0.2.302，manifest 寫的卻是 0.2.300
        ReleasePackageFixture.WriteFiles(inbox, fixture.Default, asRelease: Release302);
        ReleasePackageFixture.WriteRequest(inbox, "pub-b", Release302, host.Clock.NowMs);
        var mismatch = await WaitProcessedAsync(host, "pub-b");
        mismatch.Should().Be(new AppReleaseReceipt("pub-b", false, Release302, AppReleaseIngestCodes.ReleaseMismatch, host.Clock.NowMs));

        var expected = new[] { fixture.Release, Release302 }.SelectMany(r =>
        {
            var names = ReleasePackageRules.FileNames(r);
            return new[] { names.Zip, names.Manifest, names.Signature };
        });
        Directory.GetFiles(inbox).Select(f => Path.GetFileName(f)).Should().BeEquivalentTo(expected, "拒收時三個檔留著，只刪 request.json");
        PartialFolders(host).Should().BeEmpty();
        (await store.FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
        (await store.FindAsync(Release302, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task 超過上限的檔不讀_拒收too_large()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture, new Dictionary<string, string?> { ["Hub:Releases:MaxZipMb"] = "1" });
        var inbox = host.Paths.AdminInboxAppReleaseDir;
        var names = ReleasePackageRules.FileNames(fixture.Release);

        await SubmitOversizedAsync("big-manifest", names.Manifest, ReleaseManifestRules.MaxManifestBytes + 1);
        await SubmitOversizedAsync("big-sig", names.Signature, ReleaseManifestRules.MaxSignatureBytes + 1);
        await SubmitOversizedAsync("big-zip", names.Zip, 1024 * 1024 + 1);

        (await host.Services.GetRequiredService<AppReleaseStore>().FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
        PartialFolders(host).Should().BeEmpty();
        new FileInfo(Path.Combine(inbox, names.Zip)).Length.Should().Be(1024 * 1024 + 1, "拒收時三個檔留著不動");

        async Task SubmitOversizedAsync(string id, string fileName, int size)
        {
            ReleasePackageFixture.WriteFiles(inbox, fixture.Default);
            var path = Path.Combine(inbox, fileName);
            await File.WriteAllBytesAsync(path, new byte[size]);
            // 測試獨占開著過大的檔：服務只要開它就會共用違規（下次再試、等不到回執），拿到 too_large 就表示只看了大小
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                ReleasePackageFixture.WriteRequest(inbox, id, fixture.Release, host.Clock.NowMs);
                var receipt = await WaitProcessedAsync(host, id);
                receipt.Should().Be(new AppReleaseReceipt(id, false, fixture.Release, AppReleaseIngestCodes.TooLarge, host.Clock.NowMs), fileName);
            }
        }
    }

    [Fact]
    public async Task 簽章錯_拒收signature_不驗zip()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);

        // 簽的是另一份內容，zip 是亂碼：先驗簽章（5.1），代碼是 signature 而不是 zip_hash
        var forged = fixture.Default with
        {
            Signature = fixture.Sign(Encoding.UTF8.GetBytes("另一份 manifest")),
            Zip = RandomNumberGenerator.GetBytes(64 * 1024),
        };
        var receipt = await SubmitAsync(host, forged, "pub-1");
        receipt.Should().Be(new AppReleaseReceipt("pub-1", false, fixture.Release, ReleasePackageSteps.Signature, host.Clock.NowMs));

        var malformed = await SubmitAsync(host, fixture.Default with { Signature = "{\"kid\":\"nope\"}" }, "pub-2");
        malformed.Message.Should().Be(ReleasePackageSteps.Signature, "簽章檔格式不對");

        (await host.Services.GetRequiredService<AppReleaseStore>().FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
        PartialFolders(host).Should().BeEmpty();
    }

    [Fact]
    public async Task 不認得的金鑰拒收signature()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);

        var receipt = await SubmitAsync(host, fixture.Build(key: fixture.CreateOtherKey()), "pub-1");

        receipt.Should().Be(new AppReleaseReceipt("pub-1", false, fixture.Release, ReleasePackageSteps.Signature, host.Clock.NowMs));
        (await host.Services.GetRequiredService<AppReleaseStore>().FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task manifest格式錯_政策不符_協定不支援()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);

        var schema2 = Encoding.UTF8.GetBytes("{\"schema\":2}");
        var badManifest = await SubmitAsync(host, fixture.Default with { Manifest = schema2, Signature = fixture.Sign(schema2) }, "pub-1");
        badManifest.Should().Be(new AppReleaseReceipt("pub-1", false, fixture.Release, ReleasePackageSteps.Manifest, host.Clock.NowMs));

        var wrongEntry = await SubmitAsync(host, fixture.Build(Release301, entry: "Myriagent.dll"), "pub-2");
        wrongEntry.Should().Be(new AppReleaseReceipt("pub-2", false, Release301, ReleasePackageSteps.Policy, host.Clock.NowMs));

        var reserved = fixture.Build(Release302, extraFiles: new Dictionary<string, string> { ["release.manifest.json"] = "{}" });
        (await SubmitAsync(host, reserved, "pub-3")).Message.Should().Be(ReleasePackageSteps.Policy, "清單不得含桌面保留的檔名（D11）");

        var future = await SubmitAsync(host, fixture.Build(Release303, protocolVersion: SyncProtocol.Version + 1), "pub-4");
        future.Should().Be(new AppReleaseReceipt("pub-4", false, Release303, ReleasePackageSteps.Protocol, host.Clock.NowMs));

        (await host.Services.GetRequiredService<AppReleaseStore>().ListAsync(CancellationToken.None)).Should().BeEmpty();
        PartialFolders(host).Should().BeEmpty();
    }

    [Fact]
    public async Task zip雜湊不符_項目不符()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);

        var flipped = await SubmitAsync(host, fixture.Default with { Zip = ReleasePackageFixture.FlipByte(fixture.Default.Zip) }, "pub-1");
        flipped.Should().Be(new AppReleaseReceipt("pub-1", false, fixture.Release, ReleasePackageSteps.ZipHash, host.Clock.NowMs));

        var extra = await SubmitAsync(host, fixture.Build(Release301, zipOnlyFile: ("extra.dll", "不在清單裡")), "pub-2");
        extra.Should().Be(new AppReleaseReceipt("pub-2", false, Release301, ReleasePackageSteps.ZipEntries, host.Clock.NowMs));

        (await host.Services.GetRequiredService<AppReleaseStore>().ListAsync(CancellationToken.None)).Should().BeEmpty();
        PartialFolders(host).Should().BeEmpty();
    }

    [Fact]
    public async Task 不比可用版本新_拒收not_newer()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        (await SubmitAsync(host, fixture.Build(Release301), "pub-301")).Message.Should().Be(AppReleaseIngestCodes.Ingested);

        var older = await SubmitAsync(host, fixture.Default, "pub-300");
        older.Should().Be(new AppReleaseReceipt("pub-300", false, fixture.Release, AppReleaseIngestCodes.NotNewer, host.Clock.NowMs));
        var sameVersion = await SubmitAsync(host, fixture.Build("0.2.301-ffffff0"), "pub-301b");
        sameVersion.Message.Should().Be(AppReleaseIngestCodes.NotNewer, "版本號相同、build 不同也不收：版本只能往前");

        (await store.FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
        (await store.FindAsync("0.2.301-ffffff0", CancellationToken.None)).Should().BeNull();
        store.Latest.Should().Be(new AppReleaseInfo(Release301, "0.2.301"));
    }

    [Fact]
    public async Task 同一版再送一次_相同manifest回ok_不重複發出Ingested()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        var ingested = new ConcurrentQueue<AppReleaseInfo>();
        store.Ingested += ingested.Enqueue;
        (await SubmitAsync(host, fixture.Default, "pub-1")).Message.Should().Be(AppReleaseIngestCodes.Ingested);
        var firstAt = host.Clock.NowMs;

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = await SubmitAsync(host, fixture.Default, "pub-2");

        again.Should().Be(new AppReleaseReceipt("pub-2", true, fixture.Release, AppReleaseIngestCodes.AlreadyIngested, host.Clock.NowMs));
        ingested.Should().ContainSingle("冪等的重送不呼叫 AddAsync，不重複通知裝置");
        (await AuditsAsync(host)).Count(a => a.Action == AuditActions.AppReleaseIngested).Should().Be(1, "冪等的重送不稽核");
        (await store.FindAsync(fixture.Release, CancellationToken.None))!.IngestedAt.Should().Be(firstAt);
        Directory.EnumerateFileSystemEntries(host.Paths.AdminInboxAppReleaseDir).Should().BeEmpty("冪等成功也清掉收件匣");
        PartialFolders(host).Should().BeEmpty();
    }

    [Fact]
    public async Task 同名不同manifest_拒收release_exists()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        (await SubmitAsync(host, fixture.Default, "pub-1")).Message.Should().Be(AppReleaseIngestCodes.Ingested);

        var changed = fixture.Build(createdAt: ReleasePackageFixture.DefaultCreatedAt.AddMinutes(1));
        changed.ManifestSha256.Should().NotBe(fixture.Default.ManifestSha256);
        var receipt = await SubmitAsync(host, changed, "pub-2");

        receipt.Should().Be(new AppReleaseReceipt("pub-2", false, fixture.Release, AppReleaseIngestCodes.ReleaseExists, host.Clock.NowMs));
        var row = await store.FindAsync(fixture.Release, CancellationToken.None);
        row!.ManifestSha256.Should().Be(fixture.Default.ManifestSha256);
        row.State.Should().Be(AppReleaseStates.Available);
        File.ReadAllBytes(store.ManifestPathOf(fixture.Release)).Should().Equal(fixture.Default.Manifest, "已收的版本資料夾不被覆寫");
        Directory.GetFiles(host.Paths.AdminInboxAppReleaseDir).Should().HaveCount(3, "拒收時三個檔留著");
        PartialFolders(host).Should().BeEmpty();
    }

    [Fact]
    public async Task 撤回過的同名版本不能再收()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        (await SubmitAsync(host, fixture.Default, "pub-1")).Message.Should().Be(AppReleaseIngestCodes.Ingested);
        (await store.WithdrawAsync(fixture.Release, CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.Withdrawn);

        var again = await SubmitAsync(host, fixture.Default, "pub-2");

        again.Should().Be(new AppReleaseReceipt("pub-2", false, fixture.Release, AppReleaseIngestCodes.ReleaseExists, host.Clock.NowMs),
            "同名的版本永遠不再收（D14），撤回的也一樣");
        (await store.FindAsync(fixture.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Withdrawn);
        (await SubmitAsync(host, fixture.Build(Release301), "pub-3")).Message.Should().Be(AppReleaseIngestCodes.Ingested);
        store.Latest.Should().Be(new AppReleaseInfo(Release301, "0.2.301"));
    }

    [Fact]
    public async Task request格式錯_拒收且刪除request()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var inbox = host.Paths.AdminInboxAppReleaseDir;
        ReleasePackageFixture.WriteFiles(inbox, fixture.Default);

        ReleasePackageFixture.WriteRawRequest(inbox, "{\"id\":\"bad-release\",\"release\":\"0.2.300-AB12CD3\",\"requestedAt\":1}");
        var badRelease = await WaitProcessedAsync(host, "bad-release");
        badRelease.Should().Be(new AppReleaseReceipt("bad-release", false, null, AppReleaseIngestCodes.InvalidRequest, host.Clock.NowMs),
            "release 不合法：回執照樣帶解析得出來的 id，release 不寫");

        File.Delete(host.Paths.AppReleaseAckFile);
        ReleasePackageFixture.WriteRawRequest(inbox,
            "{\"id\":\"big\",\"release\":\"" + fixture.Release + "\",\"pad\":\"" + new string('x', AdminInboxRules.MaxFileBytes) + "\"}");
        var tooBig = await WaitProcessedAsync(host, "");
        tooBig.Should().Be(new AppReleaseReceipt("", false, null, AppReleaseIngestCodes.InvalidRequest, host.Clock.NowMs), "超過 64 KiB 不讀內容");

        File.Delete(host.Paths.AppReleaseAckFile);
        ReleasePackageFixture.WriteRawRequest(inbox, "這不是 JSON");
        var notJson = await WaitProcessedAsync(host, "");
        notJson.Should().Be(new AppReleaseReceipt("", false, null, AppReleaseIngestCodes.InvalidRequest, host.Clock.NowMs));

        File.Exists(RequestPath(host)).Should().BeFalse();
        Directory.GetFiles(inbox).Should().HaveCount(3, "三個檔留著");
        PartialFolders(host).Should().BeEmpty();
    }

    [Fact]
    public async Task request是PowerShell寫的_有BOM與CRLF也收()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var inbox = host.Paths.AdminInboxAppReleaseDir;
        ReleasePackageFixture.WriteFiles(inbox, fixture.Default);

        // Windows PowerShell 5.1：Set-Content -Encoding UTF8 帶 BOM，ConvertTo-Json 用 CRLF、四個空白縮排、冒號後兩個空白
        var json = "{\r\n    \"id\":  \"pub-ps\",\r\n    \"release\":  \"" + fixture.Release + "\",\r\n    \"requestedAt\":  1760000000000\r\n}";
        var temp = RequestPath(host) + ".tmp";
        await File.WriteAllBytesAsync(temp, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray());
        File.Move(temp, RequestPath(host));

        var receipt = await WaitProcessedAsync(host, "pub-ps");
        receipt.Should().Be(new AppReleaseReceipt("pub-ps", true, fixture.Release, AppReleaseIngestCodes.Ingested, host.Clock.NowMs));
    }

    [Fact]
    public async Task 檔案被鎖住時下次輪詢再試()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        var inbox = host.Paths.AdminInboxAppReleaseDir;
        ReleasePackageFixture.WriteFiles(inbox, fixture.Default);

        using (new FileStream(Path.Combine(inbox, ReleasePackageRules.FileNames(fixture.Release).Zip), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // 送件端（或防毒）還開著 zip：複製不了就下次輪詢再試，不回執、不刪 request.json（輪詢 1 秒，3 秒內至少試過兩次）
            ReleasePackageFixture.WriteRequest(inbox, "pub-1", fixture.Release, host.Clock.NowMs);
            await Task.Delay(TimeSpan.FromSeconds(3));
            File.Exists(host.Paths.AppReleaseAckFile).Should().BeFalse();
            File.Exists(RequestPath(host)).Should().BeTrue();
            (await store.FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
        }

        var receipt = await WaitProcessedAsync(host, "pub-1");
        receipt.Message.Should().Be(AppReleaseIngestCodes.Ingested);
        PartialFolders(host).Should().BeEmpty("重試前刪掉 partial，成功時改名成版本資料夾");
    }

    [Fact]
    public async Task 處理中刪不掉request()
    {
        using var fixture = ReleasePackageFixture.Create();
        var keys = new GatedBuiltInReleaseKeys(fixture.Keys);
        await using var host = await HubTestHost.StartAsync(services: s =>
        {
            s.AddSingleton<IBuiltInReleaseKeys>(keys);
            s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(PlentyOfDisk));
        });
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        var writeLock = host.Services.GetRequiredService<HubWriteLock>();
        var request = RequestPath(host);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? holder = null;
        try
        {
            fixture.Write(host.Paths.AdminInboxAppReleaseDir, "pub-1", host.Clock.NowMs);
            await keys.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            AssertRequestHeld(request);

            // 收件停在第 6 步（取內建金鑰）時拿住 hub.db 的寫入鎖：這時迴圈不在保存，放行後收件會卡在第 10 步的 AddAsync
            holder = writeLock.RunAsync(async () =>
            {
                locked.SetResult();
                await unlock.Task;
            }, CancellationToken.None);
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(30));
            keys.Proceed.Set();
            await WaitUntilAsync(() => Directory.Exists(store.FolderOf(fixture.Release)), "收件走到第 10 步：partial 已改名成版本資料夾");

            AssertRequestHeld(request);
            File.Exists(host.Paths.AppReleaseAckFile).Should().BeFalse("還沒寫進資料表，不回執");
            (await store.FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
        }
        finally
        {
            keys.Proceed.Set();
            unlock.TrySetResult();
            if (holder is not null)
                await holder;
        }

        var receipt = await WaitProcessedAsync(host, "pub-1");
        receipt.Message.Should().Be(AppReleaseIngestCodes.Ingested);
        File.Exists(request).Should().BeFalse();
    }

    [Fact]
    public async Task 改名成版本資料夾時副本被開著_下次輪詢再試()
    {
        // INGEST-2：防毒在掃剛複製的副本時，第 10 步的 Directory.Move 丟 IOException 0x80070005（不是共用違規），原本落到未預期的錯誤回 internal_error、
        // 刪掉 request.json；改成同第 5 步：不回執、不刪 request.json，下次輪詢再試
        using var fixture = ReleasePackageFixture.Create();
        var keys = new GatedBuiltInReleaseKeys(fixture.Keys);
        await using var host = await HubTestHost.StartAsync(services: s =>
        {
            s.AddSingleton<IBuiltInReleaseKeys>(keys);
            s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(PlentyOfDisk));
        });
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        var ingested = new ConcurrentQueue<AppReleaseInfo>();
        store.Ingested += ingested.Enqueue;
        try
        {
            fixture.Write(host.Paths.AdminInboxAppReleaseDir, "pub-1", host.Clock.NowMs);
            await keys.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // 收件停在第 6 步：副本都已落盤。像防毒一樣開著副本 zip（允許別人讀：第 9 步照樣讀得到），再放行
            var copy = Path.Combine(store.PartialFolderOf("pub-1"), ReleasePackageRules.FileNames(fixture.Release).Zip);
            using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                keys.Proceed.Set();
                await Task.Delay(TimeSpan.FromSeconds(3));   // 輪詢 1 秒：至少試過兩次

                File.Exists(host.Paths.AppReleaseAckFile).Should().BeFalse("改名被擋下不是拒收，不回執");
                File.Exists(RequestPath(host)).Should().BeTrue("request.json 留著，下次輪詢再試");
                Directory.Exists(store.FolderOf(fixture.Release)).Should().BeFalse();
                (await store.FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
            }
        }
        finally
        {
            keys.Proceed.Set();
        }

        var receipt = await WaitProcessedAsync(host, "pub-1");
        receipt.Message.Should().Be(AppReleaseIngestCodes.Ingested);
        File.ReadAllBytes(store.ZipPathOf(fixture.Release)).Should().Equal(fixture.Default.Zip);
        PartialFolders(host).Should().BeEmpty();
        ingested.Should().ContainSingle();
    }

    [Fact]
    public async Task 改名之後寫不進資料表_回internal_error_留下的資料夾在重送時清掉()
    {
        // INGEST-3：改名之後的失敗路徑。資料表寫不進去（這裡用 SQLite 觸發器讓 INSERT 失敗）：回執 internal_error、刪 request.json、三個檔留著；
        // 版本資料夾已經改名過去、沒有資料列（裝置與版本頁只看資料表），由下一次同名的收件先刪掉、或下次啟動的對帳刪掉
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        var ingested = new ConcurrentQueue<AppReleaseInfo>();
        store.Ingested += ingested.Enqueue;
        var inbox = host.Paths.AdminInboxAppReleaseDir;
        await ExecuteSqlAsync(host, "CREATE TRIGGER test_fail_app_releases BEFORE INSERT ON app_releases BEGIN SELECT RAISE(ABORT, 'test: insert blocked'); END;");

        var failed = await SubmitAsync(host, fixture.Default, "pub-1");

        failed.Should().Be(new AppReleaseReceipt("pub-1", false, fixture.Release, AppReleaseIngestCodes.Internal, host.Clock.NowMs));
        Directory.GetFiles(inbox).Should().HaveCount(3, "拒收時三個檔留著，可以直接重送");
        (await store.FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
        store.Latest.Should().BeNull();
        ingested.Should().BeEmpty();
        (await AuditsAsync(host)).Should().NotContain(a => a.Action == AuditActions.AppReleaseIngested);
        Directory.Exists(store.FolderOf(fixture.Release)).Should().BeTrue("前提：改名之後才失敗，留下沒有資料列的版本資料夾");
        PartialFolders(host).Should().BeEmpty();

        // 修好之後重送同一份：第 10 步先刪掉留下的資料夾再改名
        await ExecuteSqlAsync(host, "DROP TRIGGER test_fail_app_releases;");
        ReleasePackageFixture.WriteRequest(inbox, "pub-2", fixture.Release, host.Clock.NowMs);
        var receipt = await WaitProcessedAsync(host, "pub-2");

        receipt.Message.Should().Be(AppReleaseIngestCodes.Ingested);
        (await store.FindAsync(fixture.Release, CancellationToken.None)).Should().BeEquivalentTo(RecordOf(fixture.Default, host.Clock.NowMs));
        File.ReadAllBytes(store.ZipPathOf(fixture.Release)).Should().Equal(fixture.Default.Zip);
        ingested.Should().ContainSingle();
        store.Latest.Should().Be(new AppReleaseInfo(fixture.Release, "0.2.300"));
    }

    [Fact]
    public async Task 磁碟空間不足_拒收disk_full()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture,
            new Dictionary<string, string?> { ["Hub:Releases:MinFreeDiskMb"] = int.MaxValue.ToString(CultureInfo.InvariantCulture) });

        var receipt = await SubmitAsync(host, fixture.Default, "pub-1");

        receipt.Should().Be(new AppReleaseReceipt("pub-1", false, fixture.Release, AppReleaseIngestCodes.DiskFull, host.Clock.NowMs));
        Directory.GetFiles(host.Paths.AdminInboxAppReleaseDir).Should().HaveCount(3);
        PartialFolders(host).Should().BeEmpty("空間不夠就不複製");
        (await host.Services.GetRequiredService<AppReleaseStore>().FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task 查不到磁碟可用空間_拒收io_error()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture, freeBytes: -1);

        var receipt = await SubmitAsync(host, fixture.Default, "pub-1");

        receipt.Should().Be(new AppReleaseReceipt("pub-1", false, fixture.Release, AppReleaseIngestCodes.IoError, host.Clock.NowMs),
            "IDiskSpaceProbe 量不到（-1）不算空間夠，也不算 disk_full");
        Directory.GetFiles(host.Paths.AdminInboxAppReleaseDir).Should().HaveCount(3);
        PartialFolders(host).Should().BeEmpty("查不到空間就不複製");
        (await host.Services.GetRequiredService<AppReleaseStore>().FindAsync(fixture.Release, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task 保留最近3個可用版本()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);
        var store = host.Services.GetRequiredService<AppReleaseStore>();

        (await SubmitAsync(host, fixture.Default, "pub-0")).Message.Should().Be(AppReleaseIngestCodes.Ingested);
        foreach (var release in new[] { Release301, Release302, Release303 })
            (await SubmitAsync(host, fixture.Build(release), "pub-" + release)).Message.Should().Be(AppReleaseIngestCodes.Ingested);

        await WaitUntilAsync(() => !Directory.Exists(store.FolderOf(fixture.Release)), "第 4 版收進來之後，最舊的版本資料夾被刪");
        (await store.FindAsync(fixture.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Pruned);
        foreach (var release in new[] { Release301, Release302, Release303 })
        {
            (await store.FindAsync(release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Available);
            File.Exists(store.ZipPathOf(release)).Should().BeTrue();
        }
        store.Latest.Should().Be(new AppReleaseInfo(Release303, "0.2.303"));
    }

    [Fact]
    public async Task 收件中途當機後重送同一個request_冪等ok()
    {
        using var fixture = ReleasePackageFixture.Create();
        var dir = TempDir.Create();
        try
        {
            long ingestedAt;
            await using (var first = await StartHostAsync(fixture, dataDir: dir))
            {
                // 模擬第 10 步寫完資料表、回執之前當掉：版本資料夾與資料列都在，收件匣的檔還沒刪
                var firstStore = first.Services.GetRequiredService<AppReleaseStore>();
                ReleasePackageFixture.WriteFiles(firstStore.FolderOf(fixture.Release), fixture.Default);
                ingestedAt = first.Clock.NowMs;
                await firstStore.AddAsync(RecordOf(fixture.Default, ingestedAt), CancellationToken.None);
            }
            ReleasePackageFixture.Write(new HubPaths(dir).AdminInboxAppReleaseDir, "crash-1", ingestedAt, fixture.Default);

            await using var host = await StartHostAsync(fixture, dataDir: dir);
            var receipt = await WaitProcessedAsync(host, "crash-1");

            receipt.Should().Be(new AppReleaseReceipt("crash-1", true, fixture.Release, AppReleaseIngestCodes.AlreadyIngested, host.Clock.NowMs));
            var store = host.Services.GetRequiredService<AppReleaseStore>();
            (await store.FindAsync(fixture.Release, CancellationToken.None)).Should().BeEquivalentTo(RecordOf(fixture.Default, ingestedAt));
            File.ReadAllBytes(store.ZipPathOf(fixture.Release)).Should().Equal(fixture.Default.Zip);
            Directory.EnumerateFileSystemEntries(host.Paths.AdminInboxAppReleaseDir).Should().BeEmpty();
            (await AuditsAsync(host)).Should().NotContain(a => a.Action == AuditActions.AppReleaseIngested);
            store.Latest.Should().Be(new AppReleaseInfo(fixture.Release, "0.2.300"));
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 拒收不寫稽核()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartHostAsync(fixture);

        var rejected = await SubmitAsync(host, fixture.Build(key: fixture.CreateOtherKey()), "pub-x");

        rejected.Message.Should().Be(ReleasePackageSteps.Signature);
        var audits = await AuditsAsync(host);
        audits.Should().NotContain(a => a.Action.StartsWith("app.", StringComparison.Ordinal), "拒收只寫服務記錄（D19）");
        audits.Should().NotContain(a => a.Target == fixture.Release || a.Target == "pub-x");
    }

    [Fact]
    public async Task 預設內建金鑰來源是正式金鑰()
    {
        using var fixture = ReleasePackageFixture.Create();
        // 只換磁碟可用空間（不看測試機的磁碟）；內建金鑰照正式的登記
        await using var host = await HubTestHost.StartAsync(services: s => s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(PlentyOfDisk)));

        var builtIn = host.Services.GetRequiredService<IBuiltInReleaseKeys>();
        builtIn.Should().BeOfType<ProductionBuiltInReleaseKeys>();
        builtIn.Keys.Should().BeSameAs(ReleaseKeys.Production);

        var receipt = await SubmitAsync(host, fixture.Default, "pub-1");
        receipt.Should().Be(new AppReleaseReceipt("pub-1", false, fixture.Release, ReleasePackageSteps.Signature, host.Clock.NowMs),
            "測試金鑰簽的套件，沒換內建金鑰的主機不收");
    }

    [Fact]
    public void 伺服器的信任來源只有內建金鑰()
    {
        var root = RepoFiles.Combine("src", "MyriagentHub");
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Path: Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), Text: File.ReadAllText(f)))
            .Where(f => !f.Path.StartsWith("bin/", StringComparison.Ordinal) && !f.Path.StartsWith("obj/", StringComparison.Ordinal))
            .ToList();

        sources.Select(f => f.Path).Should().Contain("Releases/IBuiltInReleaseKeys.cs", "掃描範圍要涵蓋內建金鑰的接縫");
        sources.Where(f => f.Text.Contains("ReleaseKeys.Trusted(", StringComparison.Ordinal) || f.Text.Contains("TestKeySpki(", StringComparison.Ordinal))
            .Select(f => f.Path).Should().BeEmpty("伺服器不認測試金鑰（D1）");
        sources.Where(f => f.Text.Contains("ReleaseKeys.Production", StringComparison.Ordinal))
            .Select(f => f.Path).Should().Equal(["Releases/IBuiltInReleaseKeys.cs"], "收件的信任來源只有內建的正式金鑰，沒有設定檔或指令能加（D1）");
    }

    /// <param name="freeBytes">磁碟可用空間（FakeDiskSpace；-1＝量不到）</param>
    private static Task<HubTestHost> StartHostAsync(ReleasePackageFixture fixture, IDictionary<string, string?>? settings = null, string? dataDir = null,
        long freeBytes = PlentyOfDisk) =>
        HubTestHost.StartAsync(dataDir: dataDir, settings: settings, services: s =>
        {
            s.AddSingleton<IBuiltInReleaseKeys>(new TestBuiltInReleaseKeys(fixture.Keys));
            s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(freeBytes));
        });

    private static async Task<AppReleaseReceipt> SubmitAsync(HubTestHost host, ReleasePackageFixture.Package package, string id)
    {
        ReleasePackageFixture.Write(host.Paths.AdminInboxAppReleaseDir, id, host.Clock.NowMs, package);
        return await WaitProcessedAsync(host, id);
    }

    /// <summary>等到這個 id 的回執，再等 request.json 被刪（服務先寫回執、關閉 request.json 之後才刪它）</summary>
    private static async Task<AppReleaseReceipt> WaitProcessedAsync(HubTestHost host, string id)
    {
        var receipt = await WaitReceiptAsync(host, id);
        await WaitUntilAsync(() => !File.Exists(RequestPath(host)), $"{id} 的 request.json 被刪");
        return receipt;
    }

    private static async Task<AppReleaseReceipt> WaitReceiptAsync(HubTestHost host, string id)
    {
        for (var i = 0; i < 600; i++)
        {
            try
            {
                if (File.Exists(host.Paths.AppReleaseAckFile)
                    && JsonSerializer.Deserialize<AppReleaseReceipt>(await File.ReadAllTextAsync(host.Paths.AppReleaseAckFile), StatusJson.Options) is { } receipt
                    && receipt.Id == id)
                    return receipt;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 服務正在換檔：下一輪再讀
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"等不到 {id} 的回執");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 600 && !condition(); i++)
            await Task.Delay(50);
        condition().Should().BeTrue(because);
    }

    private static string RequestPath(HubTestHost host) =>
        Path.Combine(host.Paths.AdminInboxAppReleaseDir, AppReleaseIngestRules.RequestFileName);

    private static string[] PartialFolders(HubTestHost host) =>
        Directory.GetDirectories(host.Paths.AppReleasesDir, AppReleaseStore.PartialPrefix + "*");

    private static Task<IReadOnlyList<AuditRecord>> AuditsAsync(HubTestHost host) =>
        host.Services.GetRequiredService<AuditLog>().ListAsync(null, 200);

    /// <summary>直接對 hub.db 下一句 SQL（測試用的觸發器）：經服務自己的 DbContext，不另外開連線字串</summary>
    private static async Task ExecuteSqlAsync(HubTestHost host, string sql)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<HubDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private static AppReleaseRecord RecordOf(ReleasePackageFixture.Package package, long ingestedAt)
    {
        if (!ReleaseManifestRules.TryParse(package.Manifest, out var manifest, out var error))
            throw new InvalidOperationException(error);
        return new AppReleaseRecord
        {
            Release = package.Release,
            Version = manifest.Identity.Version.ToString(),
            Build = manifest.Identity.Build,
            Commit = manifest.Commit,
            ManifestSha256 = package.ManifestSha256,
            ZipSha256 = manifest.ZipSha256,
            ZipBytes = package.Zip.Length,
            ProtocolVersion = manifest.ProtocolVersion,
            MinRuntime = AppReleaseRules.MinRuntimeText(manifest.MinRuntime),
            Kid = package.Kid,
            State = AppReleaseStates.Available,
            IngestedAt = ingestedAt,
        };
    }

    /// <summary>服務開著 request.json（FileShare.Read）：送件腳本刪不掉、也改不了名，就知道正在處理</summary>
    private static void AssertRequestHeld(string request)
    {
        FluentActions.Invoking(() => File.Delete(request)).Should().Throw<IOException>("處理期間 request.json 開著");
        FluentActions.Invoking(() => File.Move(request, request + ".retracted")).Should().Throw<IOException>();
        File.Exists(request).Should().BeTrue();
    }

    /// <summary>收件第 6 步取內建金鑰時先停住，等測試放行（Proceed 最多等 30 秒）</summary>
    private sealed class GatedBuiltInReleaseKeys(IReadOnlyList<ReleaseKey> keys) : IBuiltInReleaseKeys
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Proceed { get; } = new(false);

        public IReadOnlyList<ReleaseKey> Keys
        {
            get
            {
                Entered.TrySetResult();
                Proceed.Wait(TimeSpan.FromSeconds(30));
                return keys;
            }
        }
    }
}
