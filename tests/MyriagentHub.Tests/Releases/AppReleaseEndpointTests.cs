using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Releases;
using MyriagentHub.Tests.Media;
using MyriagentHub.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Releases;

/// <summary>
/// 裝置的 app 下載端點（U2 設計 T5；規格 §7.3、D5、D17、D21）：latest 與套件掛既有的 DeviceAccess 政策（只收 cha_；chd_ 403、
/// 擁有者 cookie 與匿名 401）；套件支援 Range 續傳與 If-Range，撤回、清除回 410，排空中回 503 加 Retry-After，下載途中撤回或清除照樣傳完。
/// 權杖不分範圍、不限次數與同時下載數、不寫下載稽核（D21）。版本一律經真的收件匣（T4）收進來。
/// 錯誤代碼以字串比對（線上格式），不引用 AppReleaseErrorCodes：實作之前就編得過，紅燈是執行期的失敗
/// </summary>
public sealed class AppReleaseEndpointTests
{
    /// <summary>TestServer 的回應管線只容得下一小段：第一個請求只讀 4 KiB，伺服器就停在送出途中、zip 一直開著</summary>
    private const int LargePayload = 4 * 1024 * 1024;

    /// <summary>測試主機的磁碟可用空間（500 GB，同 AppReleaseInboxTests）：收件不看跑測試那台的磁碟，Hub:Releases:MinFreeDiskMb 維持預設</summary>
    private const long PlentyOfDisk = 500L << 30;

    [Fact]
    public async Task 沒有可用版本回204()
    {
        await using var host = await StartAsync([]);
        using var client = host.CreateClient(cookies: false);

        using var response = await GetAsync(client, AppReleaseRoutes.Latest, IssueToken(host));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task latest回manifest原文的base64與sig物件()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        using var client = host.CreateClient(cookies: false);

        using var response = await GetAsync(client, AppReleaseRoutes.Latest, IssueToken(host));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var text = await response.Content.ReadAsStringAsync();
        var json = JObject.Parse(text);
        json.Properties().Select(p => p.Name).Should().Equal(new[] { "release", "version", "build", "manifest", "sig", "zipBytes" },
            "形狀照 T1 的 LatestAppRelease（之後只加選用欄位）");
        ((string?)json["release"]).Should().Be(package.Release);
        ((string?)json["version"]).Should().Be("0.2.300");
        ((string?)json["build"]).Should().Be("000012c");
        json["manifest"]!.Type.Should().Be(JTokenType.String, "manifest 是原文的 base64，不是解析過的物件");
        json["sig"]!.Type.Should().Be(JTokenType.Object);
        ((long?)json["zipBytes"]).Should().Be(package.Zip.Length);

        var body = SyncJson.Deserialize<LatestAppRelease>(text)!;
        var manifest = Convert.FromBase64String(body.Manifest);
        manifest.Should().Equal(package.Manifest);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        manifest.Should().Equal(await File.ReadAllBytesAsync(store.ManifestPathOf(package.Release)), "跟 app-releases 裡的 manifest 逐位元組相同");
        body.Sig.Should().Be(package.Sig);
        ReleaseManifestRules.TryParseSignature(ReleaseManifestRules.SerializeSignature(body.Sig), out var roundTrip).Should().BeTrue();
        roundTrip.Should().Be(package.Sig, "U3 以 SerializeSignature 組回簽章檔");
        ReleaseManifestRules.VerifySignature(manifest, body.Sig, [package.Key], []).Should().BeNull("裝置以內建的發佈金鑰自己驗（D1）");
    }

    [Fact]
    public async Task 只收cha_權杖_chd_回403_擁有者cookie與匿名回401()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        using var device = await TestDevice.AddAsync(host);
        await using var hub = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, prove) = await hub.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        using var client = host.CreateClient(cookies: false);
        using var owner = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(owner);
        using (var me = await owner.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.OK, "正向對照：擁有者的 cookie 本身有效");

        using (var latest = await GetAsync(client, AppReleaseRoutes.Latest, prove.AccessToken))
            latest.StatusCode.Should().Be(HttpStatusCode.OK, "Prove 發的 cha_");
        using (var download = await GetAsync(client, AppReleaseRoutes.Package(package.Release), prove.AccessToken))
        {
            download.StatusCode.Should().Be(HttpStatusCode.OK, "Prove 發的 cha_");
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(package.Zip);
        }

        foreach (var path in new[] { AppReleaseRoutes.Latest, AppReleaseRoutes.Package(package.Release) })
        {
            using (var response = await GetAsync(client, path, device.Token))
                await ExpectNoBodyAsync(response, HttpStatusCode.Forbidden, $"chd_ 認證得過，但 DeviceAccess 只收存取權杖：{path}");
            using (var response = await GetAsync(client, path, token: null))
                await ExpectNoBodyAsync(response, HttpStatusCode.Unauthorized, $"匿名：{path}");
            using (var response = await owner.GetAsync(path))
                await ExpectNoBodyAsync(response, HttpStatusCode.Unauthorized, $"擁有者 cookie 不是裝置權杖的方案：{path}");
        }
    }

    [Fact]
    public async Task 套件回zip_ETag與AcceptRanges()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        using var client = host.CreateClient(cookies: false);

        using var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), IssueToken(host));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.Should().Be($"\"{package.ZipSha256}\"");
        response.Headers.ETag.IsWeak.Should().BeFalse("強 ETag：If-Range 用強比對");
        response.Headers.AcceptRanges.Should().Contain("bytes");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentLength.Should().Be(package.Zip.Length);
        response.Content.Headers.ContentDisposition!.ToString().Should().Contain(ReleasePackageRules.FileNames(package.Release).Zip);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(package.Zip);
        (await AppAuditActionsAsync(host)).Should().Equal(new[] { AuditActions.AppReleaseIngested }, "下載不寫稽核（D21）：app.* 只有收件那一筆");
    }

    [Fact]
    public async Task Range續傳回206()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        using var client = host.CreateClient(cookies: false);

        using var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), IssueToken(host),
            r => r.Headers.Range = new RangeHeaderValue(100, null));
        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        var range = response.Content.Headers.ContentRange!;
        range.From.Should().Be(100);
        range.To.Should().Be(package.Zip.Length - 1);
        range.Length.Should().Be(package.Zip.Length);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(package.Zip[100..]);
    }

    [Fact]
    public async Task 超出範圍回416()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        using var client = host.CreateClient(cookies: false);

        using var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), IssueToken(host),
            r => r.Headers.Range = new RangeHeaderValue(package.Zip.Length, null));
        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        var range = response.Content.Headers.ContentRange!;
        range.HasRange.Should().BeFalse("Content-Range: bytes */<長度>");
        range.Length.Should().Be(package.Zip.Length);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task IfRange不符回整檔200()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        using var client = host.CreateClient(cookies: false);
        var token = IssueToken(host);

        using (var matched = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token, r =>
        {
            r.Headers.Range = new RangeHeaderValue(100, null);
            r.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{package.ZipSha256}\""));
        }))
        {
            matched.StatusCode.Should().Be(HttpStatusCode.PartialContent, "If-Range 相符：照 Range 續傳（U3 的寫法）");
            (await matched.Content.ReadAsByteArrayAsync()).Should().Equal(package.Zip[100..]);
        }

        using var mismatched = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token, r =>
        {
            r.Headers.Range = new RangeHeaderValue(100, null);
            r.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{new string('0', 64)}\""));
        });
        mismatched.StatusCode.Should().Be(HttpStatusCode.OK, "If-Range 不符：不理 Range、回整檔");
        mismatched.Content.Headers.ContentRange.Should().BeNull();
        (await mismatched.Content.ReadAsByteArrayAsync()).Should().Equal(package.Zip);
    }

    [Fact]
    public async Task 撤回回410_清除回410()
    {
        using var key = NewKey();
        var older = TestPackage.Create(300, key);
        var newer = TestPackage.Create(301, key);
        await using var host = await StartAsync([older.Key], new Dictionary<string, string?> { ["Hub:Releases:KeepAvailable"] = "1" });
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        await IngestAsync(host, older);
        await IngestAsync(host, newer);
        await WaitForFolderDeletedAsync(store, older.Release);
        (await store.FindAsync(older.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Pruned, "只保留 1 個可用版本：舊的那一版被清除（D5）");
        using var client = host.CreateClient(cookies: false);
        var token = IssueToken(host);

        using (var pruned = await GetAsync(client, AppReleaseRoutes.Package(older.Release), token))
        {
            pruned.StatusCode.Should().Be(HttpStatusCode.Gone, "清除的版本");
            (await ErrorAsync(pruned)).Should().Be("gone");
        }
        (await LatestAsync(client, token))!.Release.Should().Be(newer.Release);

        (await store.WithdrawAsync(newer.Release, CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.Withdrawn);
        using (var withdrawn = await GetAsync(client, AppReleaseRoutes.Package(newer.Release), token))
        {
            withdrawn.StatusCode.Should().Be(HttpStatusCode.Gone, "撤回的版本");
            (await ErrorAsync(withdrawn)).Should().Be("gone");
        }
        (await LatestAsync(client, token)).Should().BeNull("撤回之後沒有可用版本");
    }

    [Fact]
    public async Task 可用版本的套件檔不在回410()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        using var client = host.CreateClient(cookies: false);
        var token = IssueToken(host);

        File.Delete(store.ZipPathOf(package.Release));
        using (var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Gone, "zip 不在（FileNotFoundException）");
            (await ErrorAsync(response)).Should().Be("gone");
        }
        Directory.Delete(store.FolderOf(package.Release), recursive: true);
        using (var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Gone, "整個版本資料夾不在（DirectoryNotFoundException）");
            (await ErrorAsync(response)).Should().Be("gone");
        }
        (await store.FindAsync(package.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Available,
            "資料列還是 available：410 來自缺檔，不是狀態");
    }

    [Fact]
    public async Task 可用版本的套件檔暫時開不了回503與RetryAfter_不是500()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        using var client = host.CreateClient(cookies: false);
        var token = IssueToken(host);

        // 防毒或備份獨占開著 zip：開檔丟共用違規的 IOException（不是 FileNotFoundException），版本仍是可用的
        using (new FileStream(store.ZipPathOf(package.Release), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "可用版本的 zip 暫時開不了：裝置照 Retry-After 稍後再試，不是 500");
            response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(AdminInboxRules.DefaultRetryAfterSec));
            (await ErrorAsync(response)).Should().Be("unavailable");
        }
        using (var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, "放開之後照常下載");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(package.Zip);
        }
        (await store.FindAsync(package.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Available);
    }

    [Fact]
    public async Task 不存在或格式不對回404()
    {
        await using var host = await StartAsync([]);
        using var client = host.CreateClient(cookies: false);
        var token = IssueToken(host);

        foreach (var release in new[] { "0.2.999-abcdef0", "not-a-release", "0.2.01-abcdef0", "0.2.5-ABCDEF0", "0.2.5-abcdef0x" })
        {
            using var response = await GetAsync(client, RawPackagePath(release), token);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, release);
            (await ErrorAsync(response)).Should().Be("not_found", release);
        }
    }

    [Fact]
    public async Task 排空中回503與RetryAfter()
    {
        using var key = NewKey();
        var package = TestPackage.Create(300, key);
        await using var host = await StartAsync([package.Key]);
        await IngestAsync(host, package);
        using var client = host.CreateClient(cookies: false);
        var token = IssueToken(host);
        var drain = host.Services.GetRequiredService<DrainState>();

        drain.Drain(DrainSource.Admin, host.Clock.NowMs, 45, DrainRules.DefaultDeployDrainMaxSeconds, null);
        using (var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(45), "取排空的建議秒數");
            (await ErrorAsync(response)).Should().Be("draining");
        }
        using (var missing = await GetAsync(client, AppReleaseRoutes.Package("0.2.999-abcdef0"), token))
            missing.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "順序是名稱格式、排空、資料列：合法但不存在的版本也先回 503");
        using (var malformed = await GetAsync(client, RawPackagePath("not-a-release"), token))
            malformed.StatusCode.Should().Be(HttpStatusCode.NotFound, "名稱格式在排空之前檢查");
        (await LatestAsync(client, token))!.Release.Should().Be(package.Release, "排空只擋套件下載，latest 照常回應（D17）");

        drain.Drain(DrainSource.Admin, host.Clock.NowMs, 0, DrainRules.DefaultDeployDrainMaxSeconds, null);
        using (var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(AdminInboxRules.DefaultRetryAfterSec), "建議秒數不是正數時用預設值");
        }

        drain.Undrain();
        using (var response = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, "解除排空之後照常下載");
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(package.Zip);
        }
    }

    [Fact]
    public async Task 下載途中撤回或清除_照樣傳完_之後回410()
    {
        using var key = NewKey();
        var v300 = TestPackage.Create(300, key, LargePayload);
        var v301 = TestPackage.Create(301, key, LargePayload);
        var v302 = TestPackage.Create(302, key);
        var v303 = TestPackage.Create(303, key);
        await using var host = await StartAsync([v300.Key], new Dictionary<string, string?> { ["Hub:Releases:KeepAvailable"] = "2" });
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        await IngestAsync(host, v300);
        await IngestAsync(host, v301);
        using var client = host.CreateClient(cookies: false);
        var token = IssueToken(host);

        // 撤回：301 讀了 4 KiB 時撤回並跑保存規則
        await using (var download = await StartDownloadAsync(client, v301.Release, token))
        {
            (await store.WithdrawAsync(v301.Release, CancellationToken.None)).Should().Be(AppReleaseWithdrawOutcome.Withdrawn);
            await store.PruneAsync(CancellationToken.None);
            File.Exists(store.ZipPathOf(v301.Release)).Should().BeTrue("下載中的 zip 開著、沒有刪除權：資料夾刪不掉，留到下一輪");
            (await store.FindAsync(v301.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Withdrawn);
            (await LatestAsync(client, token))!.Release.Should().Be(v300.Release, "撤回之後 latest 立刻改回前一個可用版本");
            await ExpectGoneAsync(client, v301, token);
            ReleaseManifestRules.Sha256Hex(await download.ReadToEndAsync()).Should().Be(v301.ZipSha256, "進行中的下載照樣傳完");
        }
        await WaitForFolderDeletedAsync(store, v301.Release);

        // 清除：300 讀了 4 KiB 時再收兩版，可用版本超過保留數（2），300 被清除
        await using (var download = await StartDownloadAsync(client, v300.Release, token))
        {
            await IngestAsync(host, v302);
            await IngestAsync(host, v303);
            await store.PruneAsync(CancellationToken.None);
            File.Exists(store.ZipPathOf(v300.Release)).Should().BeTrue("下載中的 zip 刪不掉");
            (await store.FindAsync(v300.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Pruned, "資料列先改標 pruned（D5）");
            (await LatestAsync(client, token))!.Release.Should().Be(v303.Release);
            await ExpectGoneAsync(client, v300, token);
            ReleaseManifestRules.Sha256Hex(await download.ReadToEndAsync()).Should().Be(v300.ZipSha256, "進行中的下載照樣傳完");
        }
        await WaitForFolderDeletedAsync(store, v300.Release);
        (await store.FindAsync(v300.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Pruned);
    }

    /// <summary>
    /// 測試主機：以 TestBuiltInReleaseKeys 換掉內建的發佈金鑰（ReleaseKeys.Production 在 O3 之前是空的）；
    /// 磁碟可用空間固定（FakeDiskSpace：不看跑測試那台的磁碟，磁碟剩多少不影響收件）
    /// </summary>
    private static Task<HubTestHost> StartAsync(ReleaseKey[] builtIn, IDictionary<string, string?>? settings = null) =>
        HubTestHost.StartAsync(settings: settings, services: s =>
        {
            s.AddSingleton<IBuiltInReleaseKeys>(new TestBuiltInReleaseKeys(builtIn));
            s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(PlentyOfDisk));
        });

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static ReleaseKey ReleaseKeyOf(ECDsa key)
    {
        var spki = P256KeyRules.ExportSpki(key);
        return new ReleaseKey(ReleaseManifestRules.ComputeKid(spki), spki);
    }

    /// <summary>直接向存取權杖表要一個 cha_（新的裝置 id；cha_ 只在記憶體、綁 deviceId，認證時不查 devices 表）</summary>
    private static string IssueToken(HubTestHost host) =>
        host.Services.GetRequiredService<AccessTokenStore>().Issue(SyncIds.NewDeviceId()).AccessToken;

    /// <summary>不經驗證組出的套件路徑（AppReleaseRoutes.Package 對不合法的 release 丟例外）：測伺服器對格式不對的名稱回 404</summary>
    private static string RawPackagePath(string release) =>
        AppReleaseRoutes.PackagePattern.Replace("{release}", release, StringComparison.Ordinal);

    private static async Task<HttpResponseMessage> GetAsync(HubClient client, string path, string? token,
        Action<HttpRequestMessage>? configure = null, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        configure?.Invoke(request);
        return await client.Http.SendAsync(request, completion);
    }

    /// <summary>latest：204 回 null，200 回內容（其他狀態碼讓測試失敗）</summary>
    private static async Task<LatestAppRelease?> LatestAsync(HubClient client, string token)
    {
        using var response = await GetAsync(client, AppReleaseRoutes.Latest, token);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return SyncJson.Deserialize<LatestAppRelease>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetString();
    }

    /// <summary>401、403 由裝置權杖的驗證處理器回，沒有本文（sync-protocol 第 15 節）</summary>
    private static async Task ExpectNoBodyAsync(HttpResponseMessage response, HttpStatusCode status, string because)
    {
        response.StatusCode.Should().Be(status, because);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty(because);
    }

    /// <summary>撤回或清除之後：新的下載與續傳（U3 的寫法：Range 加 If-Range）都回 410 gone</summary>
    private static async Task ExpectGoneAsync(HubClient client, TestPackage package, string token)
    {
        using (var fresh = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token))
        {
            fresh.StatusCode.Should().Be(HttpStatusCode.Gone, "新的下載");
            (await ErrorAsync(fresh)).Should().Be("gone");
        }
        using var resume = await GetAsync(client, AppReleaseRoutes.Package(package.Release), token, r =>
        {
            r.Headers.Range = new RangeHeaderValue(4096, null);
            r.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{package.ZipSha256}\""));
        });
        resume.StatusCode.Should().Be(HttpStatusCode.Gone, "續傳");
    }

    /// <summary>開始下載、只讀前 4 KiB 就停（伺服器停在送出途中，zip 一直開著）</summary>
    private static async Task<PartialDownload> StartDownloadAsync(HubClient client, string release, string token)
    {
        var response = await GetAsync(client, AppReleaseRoutes.Package(release), token, completion: HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStreamAsync();
        var head = new byte[4096];
        await body.ReadExactlyAsync(head);
        return new PartialDownload(response, body, head);
    }

    /// <summary>
    /// 經真的收件匣（T4）收一版：先放三個檔，最後原子寫 request.json，等 id 對上的回執而且 ok。
    /// 收件服務處理期間一直開著 request.json、處理完才刪：寫之前與收完之後都等它不在，才不會跟上一個要求撞在一起
    /// </summary>
    private static async Task IngestAsync(HubTestHost host, TestPackage package)
    {
        var inbox = host.Paths.AdminInboxAppReleaseDir;
        var request = Path.Combine(inbox, AppReleaseIngestRules.RequestFileName);
        await WaitUntilAsync(() => !File.Exists(request), "上一個收件要求處理完", 20_000);
        var names = ReleasePackageRules.FileNames(package.Release);
        await File.WriteAllBytesAsync(Path.Combine(inbox, names.Zip), package.Zip);
        await File.WriteAllBytesAsync(Path.Combine(inbox, names.Manifest), package.Manifest);
        await File.WriteAllTextAsync(Path.Combine(inbox, names.Signature), package.Signature);
        var id = "t5-" + Guid.NewGuid().ToString("N")[..12];
        var temp = request + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { id, release = package.Release, requestedAt = host.Clock.NowMs }));
        File.Move(temp, request);

        JsonElement? receipt = null;
        await WaitUntilAsync(() => (receipt = ReadReceipt(host.Paths.AppReleaseAckFile)) is { } r
            && r.TryGetProperty("id", out var rid) && rid.GetString() == id, "收件回執 " + id, 20_000);
        var ack = receipt ?? throw new InvalidOperationException("沒有回執");
        ack.GetProperty("ok").GetBoolean().Should().BeTrue("收件應該成功：" + (ack.TryGetProperty("message", out var message) ? message.GetString() : null));
        await WaitUntilAsync(() => !File.Exists(request), "收件處理完刪掉 request.json", 20_000);
    }

    private static JsonElement? ReadReceipt(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>反覆跑保存規則，直到這一版的資料夾刪掉（下載結束時框架才關檔；收件服務每輪也會跑，兩邊同時跑不影響結果）</summary>
    private static async Task WaitForFolderDeletedAsync(AppReleaseStore store, string release)
    {
        var watch = Stopwatch.StartNew();
        while (Directory.Exists(store.FolderOf(release)))
        {
            if (watch.ElapsedMilliseconds > 10_000)
                throw new TimeoutException($"{release} 的資料夾一直刪不掉");
            await store.PruneAsync(CancellationToken.None);
            await Task.Delay(100);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("等不到：" + because);
            await Task.Delay(50);
        }
    }

    private static async Task<List<string>> AppAuditActionsAsync(HubTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.Audit.AsNoTracking().Where(a => a.Action.StartsWith("app.")).OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync();
    }

    /// <summary>讀到一半的下載：<see cref="ReadToEndAsync"/> 回整個內容（已讀的前 4 KiB 加上其餘的）</summary>
    private sealed class PartialDownload(HttpResponseMessage response, Stream body, byte[] head) : IAsyncDisposable
    {
        public async Task<byte[]> ReadToEndAsync()
        {
            using var all = new MemoryStream();
            all.Write(head);
            await body.CopyToAsync(all);
            return all.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await body.DisposeAsync();
            response.Dispose();
        }
    }

    /// <summary>測試用的發佈：假的 exe 加一個填滿亂數的 dll（以 patch 當種子，內容固定）、zip、manifest 原文、簽章檔內容</summary>
    private sealed record TestPackage(string Release, ReleaseKey Key, byte[] Zip, byte[] Manifest, string Signature)
    {
        public string ZipSha256 => ReleaseManifestRules.Sha256Hex(Zip);

        public ReleaseSignature Sig => ReleaseManifestRules.TryParseSignature(Signature, out var sig) ? sig : throw new InvalidOperationException("簽章檔格式不對");

        public static TestPackage Create(int patch, ECDsa key, int payloadBytes = 4096)
        {
            var build = patch.ToString("x7", CultureInfo.InvariantCulture);
            var identity = new ReleaseIdentity(new ReleaseVersion(0, 2, patch), build);
            var payload = new byte[payloadBytes];
            new Random(patch).NextBytes(payload);
            (string Path, byte[] Content)[] entries =
            [
                ("Myriagent.dll", payload),
                (ReleasePackageRules.EntryExecutable, Encoding.UTF8.GetBytes("fake exe " + identity.Release)),
            ];
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in entries)
                {
                    using var entry = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
                    entry.Write(content);
                }
            }
            var zip = zipStream.ToArray();
            var files = entries.Select(e => new ReleaseFile(e.Path, e.Content.Length, ReleaseManifestRules.Sha256Hex(e.Content))).ToList();
            var manifest = ReleaseManifestRules.Build(identity, build + new string('a', 33), new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero),
                ReleasePackageRules.EntryExecutable, new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0)),
                SyncProtocol.Version, ReleaseManifestRules.Sha256Hex(zip), files);
            var signature = ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifest, key));
            return new TestPackage(identity.Release, ReleaseKeyOf(key), zip, manifest, signature);
        }
    }
}
