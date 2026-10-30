using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Releases;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Releases;

/// <summary>
/// 版本頁的擁有者 API（U2 T9）：列出版本與各電腦的 app 狀態、撤回（只要密碼工作階段，D21）、擁有者以 cookie 下載。
/// 版本由測試直接放進 app-releases\&lt;release&gt;\ 再 <see cref="AppReleaseStore.AddAsync"/>（同 AppReleaseStoreTests：store 只比 manifest 的 SHA-256、
/// 簽章檔的 kid 與 zip 大小，manifest 可以是任意位元組）
/// </summary>
public sealed class AppReleaseOwnerEndpointTests
{
    private const string TestKid = "0123456789abcdef";

    private static readonly string TestSig = Base64UrlRules.Encode(new byte[64]);

    /// <summary>
    /// 在 app-releases\&lt;release&gt;\ 放好三個檔（zip 是 <paramref name="zipLength"/> 個固定內容的位元組）並經 <see cref="AppReleaseStore.AddAsync"/> 登記成 available。
    /// AdminInboxTests 也用它
    /// </summary>
    internal static async Task<AppReleaseRecord> SeedReleaseAsync(HubTestHost host, string release, long ingestedAt, int zipLength = 4096)
    {
        ReleaseIdentity.TryParseRelease(release, out var parsed).Should().BeTrue(release);
        var identity = parsed!;
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        Directory.CreateDirectory(store.FolderOf(release));
        var manifest = Encoding.UTF8.GetBytes("{\"release\":\"" + release + "\"}\n");
        var zip = Enumerable.Range(0, zipLength).Select(i => (byte)(i * 31 + release.Length)).ToArray();
        await File.WriteAllBytesAsync(store.ManifestPathOf(release), manifest);
        await File.WriteAllTextAsync(store.SignaturePathOf(release), ReleaseManifestRules.SerializeSignature(new ReleaseSignature(TestKid, TestSig)));
        await File.WriteAllBytesAsync(store.ZipPathOf(release), zip);
        var record = new AppReleaseRecord
        {
            Release = release,
            Version = identity.Version.ToString(),
            Build = identity.Build,
            Commit = identity.Build + new string('0', 33),
            ManifestSha256 = ReleaseManifestRules.Sha256Hex(manifest),
            ZipSha256 = ReleaseManifestRules.Sha256Hex(zip),
            ZipBytes = zip.Length,
            ProtocolVersion = SyncProtocol.Version,
            MinRuntime = AppReleaseRules.MinRuntimeText(new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0))),
            Kid = TestKid,
            State = AppReleaseStates.Available,
            IngestedAt = ingestedAt,
        };
        await store.AddAsync(record, CancellationToken.None);
        return record;
    }

    [Fact]
    public async Task 擁有者列出版本與各電腦狀態()
    {
        await using var host = await HubTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Hub:Sync:MinAppVersion"] = "0.2.0" });
        var older = await SeedReleaseAsync(host, "0.2.10-aaaaaa0", host.Clock.NowMs - 60_000);
        var newer = await SeedReleaseAsync(host, "0.2.11-bbbbbb1", host.Clock.NowMs);

        // 完整連線、只更新連線（app 太舊、宣告 update1）、從沒連過、已撤銷：各一台
        using var full = await TestDevice.AddAsync(host, "完整電腦");
        using var updateOnly = await TestDevice.AddAsync(host, "舊版電腦");
        using var never = await TestDevice.AddAsync(host, "沒連過的電腦");
        using var revoked = await TestDevice.AddAsync(host, "撤銷的電腦");
        var observed = DeviceHubClient.ObservedSpki(host);
        await using var fullClient = await DeviceHubClient.ConnectAsync(host, full.Token);
        var (_, fullProve) = await fullClient.HandshakeAsync(full, observed, appVersion: "0.2.5");
        fullProve.UpdateOnly.Should().BeNull();
        await using var oldClient = await DeviceHubClient.ConnectAsync(host, updateOnly.Token);
        var (_, oldProve) = await oldClient.HandshakeAsync(updateOnly, observed, appVersion: "0.1.5", features: [DeviceFeatureRules.Update1]);
        oldProve.UpdateOnly.Should().BeTrue();

        // device_app（T8 的 app.state 物化表）：完整的那台是合格的內容；只更新那台的 phase 不認得、沒連過那台不是 JSON，兩台的 app 都是 null
        var app = new AppStatePayload("0.2.5", "0.2.5-ccccccc", AppStatePhases.Staged, newer.Release, null, host.Clock.NowMs);
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(context =>
        {
            const string sql = "INSERT OR REPLACE INTO device_app(device_id, state_json, updated_at) VALUES($d, $s, $u)";
            context.Execute(sql, ("$d", full.Id), ("$s", SyncJson.Serialize(app)), ("$u", host.Clock.NowMs));
            context.Execute(sql, ("$d", updateOnly.Id), ("$s", "{\"version\":\"0.1.5\",\"phase\":\"exploded\",\"checkedAt\":1}"), ("$u", host.Clock.NowMs));
            context.Execute(sql, ("$d", never.Id), ("$s", "{not json"), ("$u", host.Clock.NowMs));
        });

        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        using (var revoke = await client.PostAsync($"/api/v1/devices/{revoked.Id}/revoke", new { reason = "retired" }))
            revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Services.GetRequiredService<DeviceDirectory>().Get(revoked.Id)!.Revoked.Should().BeTrue();

        var view = await client.GetJsonAsync("/api/v1/app/releases");

        view.EnumerateObject().Select(p => p.Name).Should().Equal("releases", "latest", "devices");
        view.GetProperty("latest").GetString().Should().Be(newer.Release);
        var releases = view.GetProperty("releases").EnumerateArray().ToList();
        releases.Select(r => r.GetProperty("release").GetString()).Should().Equal([newer.Release, older.Release], "收件時間由新到舊");
        var first = releases[0];
        first.EnumerateObject().Select(p => p.Name).Should().Equal("release", "version", "build", "commit", "zipBytes", "kid", "state", "ingestedAt", "withdrawnAt");
        first.GetProperty("version").GetString().Should().Be("0.2.11");
        first.GetProperty("build").GetString().Should().Be("bbbbbb1");
        first.GetProperty("commit").GetString().Should().Be(newer.Commit);
        first.GetProperty("zipBytes").GetInt64().Should().Be(newer.ZipBytes);
        first.GetProperty("kid").GetString().Should().Be(TestKid);
        first.GetProperty("state").GetString().Should().Be(AppReleaseStates.Available);
        first.GetProperty("ingestedAt").GetInt64().Should().Be(newer.IngestedAt);
        first.GetProperty("withdrawnAt").ValueKind.Should().Be(JsonValueKind.Null);

        var devices = view.GetProperty("devices").EnumerateArray().ToDictionary(d => d.GetProperty("deviceId").GetString()!);
        devices.Keys.Should().BeEquivalentTo([full.Id, updateOnly.Id, never.Id], "已撤銷的不列");
        devices[full.Id].EnumerateObject().Select(p => p.Name).Should().Equal("deviceId", "name", "appVersion", "lastHelloAt", "online", "updateOnly", "app");

        var f = devices[full.Id];
        f.GetProperty("name").GetString().Should().Be("完整電腦");
        f.GetProperty("appVersion").GetString().Should().Be("0.2.5", "Hello 回報的版本（device_streams.app_version）");
        f.GetProperty("lastHelloAt").GetInt64().Should().Be(host.Clock.NowMs);
        f.GetProperty("online").GetBoolean().Should().BeTrue();
        f.GetProperty("updateOnly").GetBoolean().Should().BeFalse();
        var a = f.GetProperty("app");
        a.GetProperty("version").GetString().Should().Be("0.2.5");
        a.GetProperty("release").GetString().Should().Be("0.2.5-ccccccc");
        a.GetProperty("phase").GetString().Should().Be(AppStatePhases.Staged);
        a.GetProperty("staged").GetString().Should().Be(newer.Release);
        a.GetProperty("lastError").ValueKind.Should().Be(JsonValueKind.Null);
        a.GetProperty("checkedAt").GetInt64().Should().Be(host.Clock.NowMs);

        var u = devices[updateOnly.Id];
        u.GetProperty("appVersion").GetString().Should().Be("0.1.5");
        u.GetProperty("online").GetBoolean().Should().BeTrue();
        u.GetProperty("updateOnly").GetBoolean().Should().BeTrue("在線而且全部連線都是只更新");
        u.GetProperty("app").ValueKind.Should().Be(JsonValueKind.Null, "device_app 的內容不合格（不認得的 phase）就不顯示");

        var n = devices[never.Id];
        n.GetProperty("name").GetString().Should().Be("沒連過的電腦");
        n.GetProperty("appVersion").GetString().Should().Be("0.1.0", "還沒連過的用配對時的版本");
        n.GetProperty("lastHelloAt").ValueKind.Should().Be(JsonValueKind.Null);
        n.GetProperty("online").GetBoolean().Should().BeFalse();
        n.GetProperty("updateOnly").GetBoolean().Should().BeFalse();
        n.GetProperty("app").ValueKind.Should().Be(JsonValueKind.Null, "device_app 解不開就不顯示");
    }

    [Fact]
    public async Task 撤回後latest改回前一版_套件410_稽核()
    {
        await using var host = await HubTestHost.StartAsync();
        var older = await SeedReleaseAsync(host, "0.2.10-aaaaaa0", host.Clock.NowMs - 60_000);
        var newer = await SeedReleaseAsync(host, "0.2.11-bbbbbb1", host.Clock.NowMs);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        store.Latest!.Release.Should().Be(newer.Release);
        var latestChanged = 0;
        store.LatestChanged += () => Interlocked.Increment(ref latestChanged);

        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        var sid = (await client.GetJsonAsync("/api/v1/auth/me")).GetProperty("session").GetProperty("id").GetString()!;
        using (var before = await client.GetAsync($"/api/v1/app/releases/{newer.Release}/package"))
            before.StatusCode.Should().Be(HttpStatusCode.OK);

        // 近期驗證（5 分鐘）早就過了：撤回只要密碼工作階段（D21）。正向對照：要近期驗證的稽核端點這時回 403
        host.Clock.Advance(TimeSpan.FromMinutes(30));
        using (var recent = await client.GetAsync("/api/v1/audit"))
            recent.StatusCode.Should().Be(HttpStatusCode.Forbidden, "近期驗證確實過期了");

        using var response = await client.PostAsync($"/api/v1/app/releases/{newer.Release}/withdraw");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.EnumerateObject().Select(p => p.Name).Should().Equal("release", "state", "alreadyWithdrawn");
        body.GetProperty("release").GetString().Should().Be(newer.Release);
        body.GetProperty("state").GetString().Should().Be(AppReleaseStates.Withdrawn);
        body.GetProperty("alreadyWithdrawn").GetBoolean().Should().BeFalse();

        latestChanged.Should().Be(1);
        store.Latest!.Release.Should().Be(older.Release);
        store.LatestPayload!.Release.Should().Be(older.Release, "裝置的 latest 端點也改回前一版");
        var view = await client.GetJsonAsync("/api/v1/app/releases");
        view.GetProperty("latest").GetString().Should().Be(older.Release);
        var withdrawn = view.GetProperty("releases").EnumerateArray().Single(r => r.GetProperty("release").GetString() == newer.Release);
        withdrawn.GetProperty("state").GetString().Should().Be(AppReleaseStates.Withdrawn);
        withdrawn.GetProperty("withdrawnAt").GetInt64().Should().Be(host.Clock.NowMs);

        using (var gone = await client.GetAsync($"/api/v1/app/releases/{newer.Release}/package"))
        {
            gone.StatusCode.Should().Be(HttpStatusCode.Gone);
            (await gone.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"gone\"}");
        }
        using (var stillThere = await client.GetAsync($"/api/v1/app/releases/{older.Release}/package"))
            stillThere.StatusCode.Should().Be(HttpStatusCode.OK, "前一版照樣下載得到");

        var entries = await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 200);
        var row = entries.Should().ContainSingle(e => e.Action == AuditActions.AppReleaseWithdrawn).Which;
        row.ActorType.Should().Be(AuditActors.Owner);
        row.ActorId.Should().Be(SessionRules.ShortId(sid));
        row.Ip.Should().Be(TestIpStartupFilter.DefaultIp);
        row.Target.Should().Be(newer.Release);
        row.Outcome.Should().Be(AuditOutcomes.Ok);
    }

    [Fact]
    public async Task 撤回不存在回404_重複撤回回alreadyWithdrawn()
    {
        await using var host = await HubTestHost.StartAsync();
        var record = await SeedReleaseAsync(host, "0.2.10-aaaaaa0", host.Clock.NowMs);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);

        using (var missing = await client.PostAsync("/api/v1/app/releases/0.2.99-fffffff/withdraw"))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await missing.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"not_found\"}");
        }
        foreach (var malformed in new[] { "not-a-release", "0.2.10-AAAAAA0", "0.2.010-aaaaaa0" })
        {
            using var bad = await client.PostAsync($"/api/v1/app/releases/{malformed}/withdraw");
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, malformed);
            (await bad.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"invalid_release\"}", malformed);
        }
        using (var noCsrf = await client.PostAsync($"/api/v1/app/releases/{record.Release}/withdraw", csrf: false))
            noCsrf.StatusCode.Should().Be(HttpStatusCode.Forbidden, "既有的 OriginGuard：非 GET 要 X-Myriagent-Csrf");
        store.Latest!.Release.Should().Be(record.Release, "被拒的請求都沒有撤回");

        var once = await client.PostJsonAsync($"/api/v1/app/releases/{record.Release}/withdraw");
        once.GetProperty("alreadyWithdrawn").GetBoolean().Should().BeFalse();
        var again = await client.PostJsonAsync($"/api/v1/app/releases/{record.Release}/withdraw");
        again.GetProperty("release").GetString().Should().Be(record.Release);
        again.GetProperty("state").GetString().Should().Be(AppReleaseStates.Withdrawn);
        again.GetProperty("alreadyWithdrawn").GetBoolean().Should().BeTrue();

        store.Latest.Should().BeNull("唯一的可用版本撤回了");
        (await client.GetJsonAsync("/api/v1/app/releases")).GetProperty("latest").ValueKind.Should().Be(JsonValueKind.Null);
        var entries = await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 200);
        entries.Count(e => e.Action == AuditActions.AppReleaseWithdrawn).Should().Be(1, "已經撤回的不再寫稽核");
    }

    [Fact]
    public async Task 擁有者下載回zip_匿名401()
    {
        await using var host = await HubTestHost.StartAsync();
        var record = await SeedReleaseAsync(host, "0.2.10-aaaaaa0", host.Clock.NowMs, zipLength: 64 * 1024);
        var store = host.Services.GetRequiredService<AppReleaseStore>();
        var path = $"/api/v1/app/releases/{record.Release}/package";

        using (var anonymous = host.CreateClient(cookies: false))
        {
            using (var response = await anonymous.GetAsync(path))
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            using (var list = await anonymous.GetAsync("/api/v1/app/releases"))
                list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // 裝置的存取權杖（cha_）只在 /api/v1/device/* 認證得過：擁有者的下載網址不認它
        using var device = await TestDevice.AddAsync(host);
        await using var hub = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, prove) = await hub.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        using (var withToken = host.CreateClient(cookies: false))
        {
            withToken.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", prove.AccessToken);
            using var response = await withToken.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        using (var ok = await client.GetAsync(path))
        {
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
            ok.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
            ok.Headers.ETag!.Tag.Should().Be($"\"{record.ZipSha256}\"");
            ok.Headers.AcceptRanges.Should().Contain("bytes");
            var disposition = ok.Content.Headers.ContentDisposition!;
            disposition.DispositionType.Should().Be("attachment");
            (disposition.FileNameStar ?? disposition.FileName?.Trim('"')).Should().Be(ReleasePackageRules.FileNames(record.Release).Zip);
            (await ok.Content.ReadAsByteArrayAsync()).Should().Equal(await File.ReadAllBytesAsync(store.ZipPathOf(record.Release)));
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.Range = new RangeHeaderValue(100, null);
            using var partial = await client.Http.SendAsync(request);
            partial.StatusCode.Should().Be(HttpStatusCode.PartialContent, "續傳同裝置的套件端點");
            partial.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 100-{record.ZipBytes - 1}/{record.ZipBytes}");
        }
        foreach (var missing in new[] { "0.2.99-fffffff", "not-a-release" })
        {
            using var response = await client.GetAsync($"/api/v1/app/releases/{missing}/package");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, missing);
        }

        var entries = await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 200);
        entries.Should().NotContain(e => e.Action.StartsWith("app.", StringComparison.Ordinal), "擁有者下載不寫稽核（D21）");
    }
}
