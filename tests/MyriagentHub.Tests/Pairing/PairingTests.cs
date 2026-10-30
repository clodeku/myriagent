using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Pairing;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Trust;
using Microsoft.EntityFrameworkCore;
using static MyriagentHub.Tests.Sync.EventLogTests;

namespace MyriagentHub.Tests.Pairing;

/// <summary>
/// 裝置配對（sync-protocol 2）與裝置名冊（7.4）：開始 → 核准頁 → 擁有者核准（近期驗證過密碼）→ 裝置換到權杖與名冊（自己的那一筆是自己的公鑰）→
/// 連上裝置 hub；輪詢的各種狀態；核准要近期驗證；撤銷
/// </summary>
public sealed class PairingTests
{
    [Fact]
    public async Task 配對全程_核准頁_核准_換權杖_名冊裡自己的那一筆_權杖連得上裝置hub()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        using var browser = await LoginAsync(owner);
        using var desktop = host.CreateClient(cookies: false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = P256KeyRules.ExportSpki(key);

        var start = await StartAsync(desktop, Start(spki));
        start.UserCode.Should().MatchRegex("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$");
        start.VerificationUriComplete.Should().Be("https://localhost/#pair=" + start.UserCode);
        start.ExpiresIn.Should().Be(600);
        start.Interval.Should().Be(2);
        (await PollAsync(desktop, start.DeviceCode)).Error.Should().Be(PairingErrors.AuthorizationPending);

        // 核准頁：與桌面畫面對照的裝置指紋；配對碼大小寫與連字號不拘
        var pending = await browser.GetJsonAsync("/api/v1/pair/pending?code=" + start.UserCode.Replace("-", "").ToLowerInvariant());
        pending.GetProperty("deviceName").GetString().Should().Be("辦公室電腦");
        pending.GetProperty("fingerprint").GetString().Should().Be(P256KeyRules.Fingerprint(spki));
        pending.GetProperty("ip").GetString().Should().Be(TestIpStartupFilter.DefaultIp);

        await browser.PostJsonAsync("/api/v1/pair/approve", new { userCode = start.UserCode });

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        var token = await TokenAsync(desktop, start.DeviceCode);
        var deviceId = token.DeviceId;
        SyncIds.IsDeviceId(deviceId).Should().BeTrue();
        token.DeviceToken.Should().StartWith(SyncProtocol.DeviceTokenPrefix);
        token.HubIdentitySpki.Should().Be(host.Services.GetRequiredService<HubIdentity>().Spki);

        // 裝置檢查：名冊格式正確、裡面自己那一筆是自己的公鑰
        DeviceRegistryRules.IsValid(token.Trust.Registry).Should().BeTrue();
        var own = token.Trust.Registry!.Single(r => r.DeviceId == deviceId);
        own.Name.Should().Be("辦公室電腦");
        DeviceRegistryRules.IsOwnRecord(own, deviceId, spki).Should().BeTrue();
        token.Trust.RegistryRev.Should().Be(1);

        // deviceCode 只能換一次
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        (await PollAsync(desktop, start.DeviceCode)).Error.Should().Be(PairingErrors.InvalidGrant);
        await WaitForAuditAsync(host, AuditActions.PairCompleted);

        using var device = TestDevice.Paired(deviceId, token.DeviceToken, ECDsa.Create(key.ExportParameters(true)));
        await using var hub = await DeviceHubClient.ConnectAsync(host, token.DeviceToken);
        (await hub.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host))).Prove.NextDeviceSeqExpected.Should().Be(1);
        ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Last(p => p.DeviceId == deviceId).Fingerprint.Should().Be(P256KeyRules.Fingerprint(spki));
    }

    [Fact]
    public async Task 輪詢_太快回slow_down加5秒_拒絕回access_denied_到期回expired_token()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        using var browser = await LoginAsync(owner);
        using var desktop = host.CreateClient(cookies: false);

        var first = await StartAsync(desktop, Start(NewSpki()));
        (await PollAsync(desktop, first.DeviceCode)).Error.Should().Be(PairingErrors.AuthorizationPending);
        (await PollAsync(desktop, first.DeviceCode)).Should().Be(new PairError(PairingErrors.SlowDown, 7));
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        (await PollAsync(desktop, first.DeviceCode)).Error.Should().Be(PairingErrors.SlowDown, "間隔已經是 7 秒");
        host.Clock.Advance(TimeSpan.FromSeconds(13));
        (await PollAsync(desktop, first.DeviceCode)).Error.Should().Be(PairingErrors.AuthorizationPending);

        await browser.PostJsonAsync("/api/v1/pair/deny", new { userCode = first.UserCode });
        host.Clock.Advance(TimeSpan.FromSeconds(13));
        (await PollAsync(desktop, first.DeviceCode)).Error.Should().Be(PairingErrors.AccessDenied);
        using (var gone = await browser.GetAsync("/api/v1/pair/pending?code=" + first.UserCode))
            gone.StatusCode.Should().Be(HttpStatusCode.NotFound, "拒絕之後核准頁也找不到");
        await WaitForAuditAsync(host, AuditActions.PairDenied);

        var second = await StartAsync(desktop, Start(NewSpki()));
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        (await PollAsync(desktop, second.DeviceCode)).Error.Should().Be(PairingErrors.ExpiredToken);
        (await OwnerFlows.StepUpAsync(browser)).Dispose();
        using (var late = await browser.PostAsync("/api/v1/pair/approve", new { userCode = second.UserCode }))
            late.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await PollAsync(desktop, PairingRules.NewDeviceCode())).Error.Should().Be(PairingErrors.InvalidGrant);
        (await PollAsync(desktop, "x")).Error.Should().Be(PairingErrors.InvalidRequest);
    }

    [Fact]
    public async Task 開始配對的檢查_格式_版本_待核准最多5筆_配對過的金鑰不能再配()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        using var desktop = host.CreateClient(cookies: false);
        var ok = Start(NewSpki());

        foreach (var (bad, code) in new (PairStartRequest, string)[]
        {
            (ok with { DeviceName = " ​ " }, PairingErrors.InvalidRequest),
            (ok with { DevicePubKey = "AAAA" }, PairingErrors.InvalidRequest),
            (ok with { KeyStorage = "cloud" }, PairingErrors.InvalidRequest),
            (ok with { MachineName = new string('x', 200) }, PairingErrors.InvalidRequest),
            (ok with { ProtocolVersion = 2 }, SyncErrorCodes.Protocol),
            (ok with { AppVersion = "0.0.1" }, SyncErrorCodes.Protocol),
        })
        {
            using var response = await desktop.PostAsync("/api/v1/pair/start", bad, csrf: false, origin: null);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            SyncJson.Deserialize<PairError>(await response.Content.ReadAsStringAsync())!.Error.Should().Be(code);
        }

        for (var i = 0; i < PairingRules.MaxPending; i++)
            await StartAsync(desktop, Start(NewSpki()));
        using (var sixth = await desktop.PostAsync("/api/v1/pair/start", Start(NewSpki()), csrf: false, origin: null))
            SyncJson.Deserialize<PairError>(await sixth.Content.ReadAsStringAsync())!.Error.Should().Be(PairingErrors.TooManyPending);

        // 到期的不算待核准；已配對裝置的金鑰不能再配
        host.Clock.Advance(TimeSpan.FromMinutes(11));
        using var existing = await TestDevice.AddAsync(host);
        using (var reuse = await desktop.PostAsync("/api/v1/pair/start", Start(P256KeyRules.ExportSpki(existing.Key)), csrf: false, origin: null))
            SyncJson.Deserialize<PairError>(await reuse.Content.ReadAsStringAsync())!.Error.Should().Be(PairingErrors.InvalidRequest);
        await StartAsync(desktop, Start(NewSpki()));
    }

    [Fact]
    public async Task 核准要近期驗證_登入超過5分鐘要先再輸入密碼_密碼錯不放行_核准過的不能再核准()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        using var browser = await LoginAsync(owner);
        using var desktop = host.CreateClient(cookies: false);
        var start = await StartAsync(desktop, Start(NewSpki()));

        host.Clock.Advance(TimeSpan.FromMinutes(6));
        await ExpectErrorAsync(browser, "/api/v1/pair/approve", new { userCode = start.UserCode }, HttpStatusCode.Forbidden, "step_up_required");
        using (var wrong = await OwnerFlows.StepUpAsync(browser, "not-the-password"))
            wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await ExpectErrorAsync(browser, "/api/v1/pair/approve", new { userCode = start.UserCode }, HttpStatusCode.Forbidden, "step_up_required");
        host.Clock.Advance(TimeSpan.FromSeconds(3));
        (await PollAsync(desktop, start.DeviceCode)).Error.Should().Be(PairingErrors.AuthorizationPending, "還沒核准");

        using (var ok = await OwnerFlows.StepUpAsync(browser))
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        await browser.PostJsonAsync("/api/v1/pair/approve", new { userCode = start.UserCode });
        await ExpectErrorAsync(browser, "/api/v1/pair/approve", new { userCode = start.UserCode }, HttpStatusCode.NotFound, "not_found");
        await WaitForAuditAsync(host, AuditActions.PairApproved);
    }

    [Fact]
    public async Task 電腦名稱與使用者名稱的格式字元_去掉而不是拒絕()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        using var browser = await LoginAsync(owner);
        using var desktop = host.CreateClient(cookies: false);
        // 雙向覆寫（讓畫面上的字倒過來）、組合 emoji 的零寬連接字元
        var machine = "OFFICE" + (char)0x202E + "CP";
        var user = "User" + char.ConvertFromUtf32(0x1F468) + (char)0x200D + char.ConvertFromUtf32(0x1F4BB);
        var start = await StartAsync(desktop, Start(NewSpki()) with { MachineName = machine, WindowsUser = user });

        var pending = await browser.GetJsonAsync("/api/v1/pair/pending?code=" + start.UserCode);
        pending.GetProperty("machineName").GetString().Should().Be("OFFICECP");
        pending.GetProperty("windowsUser").GetString().Should().Be("User" + char.ConvertFromUtf32(0x1F468) + char.ConvertFromUtf32(0x1F4BB));
    }

    [Fact]
    public async Task 同一把金鑰的兩筆配對請求_只有先核准的那筆成功()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        using var browser = await LoginAsync(owner);
        using var desktop = host.CreateClient(cookies: false);
        var spki = NewSpki();
        var first = await StartAsync(desktop, Start(spki));
        var second = await StartAsync(desktop, Start(spki, "同一把金鑰"));

        await browser.PostJsonAsync("/api/v1/pair/approve", new { userCode = first.UserCode });
        await ExpectErrorAsync(browser, "/api/v1/pair/approve", new { userCode = second.UserCode }, HttpStatusCode.BadRequest, PairingErrors.InvalidRequest);
    }

    [Fact]
    public async Task 配對過的裝置含撤銷的達到名冊上限_不再核准_名冊仍合格()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyriagentHub.Data.HubDbContext>();
            for (var i = 0; i < DeviceRegistryRules.MaxDevices; i++)
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                db.Devices.Add(new MyriagentHub.Data.DeviceRecord
                {
                    Id = SyncIds.NewDeviceId(),
                    Name = "PC " + i,
                    TokenHash = RandomNumberGenerator.GetBytes(32),
                    DevicePubkey = key.ExportSubjectPublicKeyInfo(),
                    KeyStorage = "software",
                    CreatedAt = host.Clock.NowMs,
                    // 一半是撤銷過的：上限算的是名冊的列數（含撤銷）
                    RevokedAt = i % 2 == 0 ? host.Clock.NowMs : null,
                    RevokedReason = i % 2 == 0 ? DeviceRegistryRules.RevokedRetired : null,
                });
            }
            await db.SaveChangesAsync();
        }
        using var browser = await LoginAsync(owner);
        using var desktop = host.CreateClient(cookies: false);
        var start = await StartAsync(desktop, Start(NewSpki()));

        await ExpectErrorAsync(browser, "/api/v1/pair/approve", new { userCode = start.UserCode }, HttpStatusCode.Conflict, PairingErrors.TooManyDevices);
        var bundle = await host.Services.GetRequiredService<TrustService>().GetBundleAsync(-1);
        bundle.Registry.Should().HaveCount(DeviceRegistryRules.MaxDevices);
        DeviceRegistryRules.IsValid(bundle.Registry).Should().BeTrue("沒有超過上限，裝置照樣採用");
        (await PollAsync(desktop, start.DeviceCode)).Error.Should().Be(PairingErrors.AuthorizationPending, "沒有核准成功");
    }

    [Fact]
    public async Task 已移除的裝置不算進名冊上限_核准得了_名冊不列它()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        string removed;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyriagentHub.Data.HubDbContext>();
            var ids = new List<string>();
            for (var i = 0; i < DeviceRegistryRules.MaxDevices; i++)
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var id = SyncIds.NewDeviceId();
                ids.Add(id);
                db.Devices.Add(new MyriagentHub.Data.DeviceRecord
                {
                    Id = id,
                    Name = "PC " + i,
                    TokenHash = RandomNumberGenerator.GetBytes(32),
                    DevicePubkey = key.ExportSubjectPublicKeyInfo(),
                    KeyStorage = "software",
                    CreatedAt = host.Clock.NowMs,
                    // 第一台撤銷之後移除了（設計 14.22）：列還在，但不在名冊、不算上限
                    RevokedAt = i == 0 ? host.Clock.NowMs : null,
                    RevokedReason = i == 0 ? DeviceRegistryRules.RevokedRetired : null,
                    RemovedAt = i == 0 ? host.Clock.NowMs : null,
                });
            }
            removed = ids[0];
            await db.SaveChangesAsync();
        }
        using var browser = await LoginAsync(owner);
        using var desktop = host.CreateClient(cookies: false);
        var start = await StartAsync(desktop, Start(NewSpki()));

        await browser.PostJsonAsync("/api/v1/pair/approve", new { userCode = start.UserCode });
        var bundle = await host.Services.GetRequiredService<TrustService>().GetBundleAsync(-1);
        bundle.Registry.Should().HaveCount(DeviceRegistryRules.MaxDevices, "255 台＋新核准的一台；移除的不列");
        bundle.Registry.Should().NotContain(e => e.DeviceId == removed);
        DeviceRegistryRules.IsValid(bundle.Registry).Should().BeTrue();
        (await TokenAsync(desktop, start.DeviceCode)).DeviceId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task 撤銷_連線中斷_權杖失效_名冊版本前進_解除帳戶連結並發布device_revoked()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await AppendAsync(host, device.Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))));
        var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, prove) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), outbox: new OutboxPosition(2, null, 1));
        var revsBefore = await host.Services.GetRequiredService<TrustService>().GetRevsAsync();

        await host.Services.GetRequiredService<PairingService>().RevokeAsync(device.Id, DeviceRegistryRules.RevokedCompromised, null, null);

        await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        await FluentActions.Awaiting(() => DeviceHubClient.ConnectAsync(host, device.Token)).Should().ThrowAsync<Exception>("撤銷的裝置權杖立即失效");
        using var http = host.CreateClient(cookies: false);
        http.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", prove.AccessToken);
        (await http.GetAsync("/api/v1/device/blobs/x")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "存取權杖一併作廢");

        (await host.Services.GetRequiredService<TrustService>().GetRevsAsync()).Registry.Should().Be(revsBefore.Registry + 1);
        ServerPayloads<DeviceRevokedPayload>(host, SyncEventKinds.DeviceRevoked).Should().ContainSingle(p => p.DeviceId == device.Id && p.Reason == "compromised");
        LatestAccount(host).Links.Should().BeEmpty();
        host.Services.GetRequiredService<ConnectionRegistry>().IsOnline(device.Id).Should().BeFalse();
        await FluentActions.Awaiting(() => host.Services.GetRequiredService<PairingService>().RevokeAsync(device.Id, DeviceRegistryRules.RevokedRetired, null, null))
            .Should().ThrowAsync<PairingException>().Where(e => e.Code == "already_revoked");
        await WaitForAuditAsync(host, AuditActions.DeviceRevoked);
    }

    /// <summary>
    /// 撤銷在 hub.db 提交之後被取消（擁有者按下撤銷就關分頁、斷線：端點傳的是 RequestAborted）：sync.db 那一半照樣做完、稽核與撤銷同一個交易。
    /// 取消的時間點以「中斷那台的連線時取消」重現：AbortDevice 在 hub.db 提交之後、sync.db 寫入之前同步呼叫
    /// </summary>
    [Fact]
    public async Task 撤銷在hub_db提交之後被取消_sync_db那一半與稽核照樣做完()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await AppendAsync(host, device.Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))));
        LinksOf(host, device.Id).Should().Be(1);
        using var cts = new CancellationTokenSource();
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        registry.Add(new DeviceConnection("revoke-probe", device.Id, cts.Cancel, new UpstreamContext(device.Id, null, null)), host.Clock.NowMs);
        var pairing = host.Services.GetRequiredService<PairingService>();
        try
        {
            await pairing.RevokeAsync(device.Id, DeviceRegistryRules.RevokedCompromised, null, null, cts.Token);

            cts.IsCancellationRequested.Should().BeTrue("對照：權杖在 hub.db 提交之後真的被取消了");
            LinksOf(host, device.Id).Should().Be(0, "解除帳戶連結");
            ServerPayloads<DeviceRevokedPayload>(host, SyncEventKinds.DeviceRevoked).Should().ContainSingle(p => p.DeviceId == device.Id && p.Reason == "compromised");
            LatestAccount(host).Links.Should().BeEmpty("受影響帳戶的 account.upsert");
            (await AuditCountAsync(host, AuditActions.DeviceRevoked)).Should().Be(1, "稽核與撤銷同一個 hub.db 交易，不必等");

            await FluentActions.Awaiting(() => pairing.RevokeAsync(device.Id, DeviceRegistryRules.RevokedRetired, null, null))
                .Should().ThrowAsync<PairingException>().Where(e => e.Code == "already_revoked");
            ServerPayloads<DeviceRevokedPayload>(host, SyncEventKinds.DeviceRevoked).Should().ContainSingle("重送不再發 device.revoked");
            (await AuditCountAsync(host, AuditActions.DeviceRevoked)).Should().Be(1, "重送不再寫稽核");
        }
        finally
        {
            registry.Remove("revoke-probe");
        }
    }

    /// <summary>之前的撤銷只做了 hub.db 那一半（舊版在提交之後被取消、sync.db 寫入失敗留下的狀態）：重送時補做 sync.db 那一半，照樣回 409</summary>
    [Fact]
    public async Task 撤銷只做了hub_db那一半時_重送補做sync_db那一半再回409()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await AppendAsync(host, device.Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))));
        await MarkRevokedInHubDbAsync(host, device.Id, DeviceRegistryRules.RevokedCompromised);
        LinksOf(host, device.Id).Should().Be(1, "對照：sync.db 那一半還沒做");
        var pairing = host.Services.GetRequiredService<PairingService>();

        await FluentActions.Awaiting(() => pairing.RevokeAsync(device.Id, DeviceRegistryRules.RevokedRetired, null, null))
            .Should().ThrowAsync<PairingException>().Where(e => e.Code == "already_revoked");
        LinksOf(host, device.Id).Should().Be(0);
        ServerPayloads<DeviceRevokedPayload>(host, SyncEventKinds.DeviceRevoked).Should().ContainSingle(p => p.DeviceId == device.Id && p.Reason == "compromised",
            "原因照 hub.db 記的（不是重送帶的）");
        LatestAccount(host).Links.Should().BeEmpty();

        await FluentActions.Awaiting(() => pairing.RevokeAsync(device.Id, DeviceRegistryRules.RevokedRetired, null, null))
            .Should().ThrowAsync<PairingException>().Where(e => e.Code == "already_revoked");
        ServerPayloads<DeviceRevokedPayload>(host, SyncEventKinds.DeviceRevoked).Should().ContainSingle("補過了就不再發");
    }

    /// <summary>撤銷只做了 hub.db 那一半（寫入者已停止、程序在兩步之間結束）：下次啟動時補做，再下一次啟動不重發</summary>
    [Fact]
    public async Task 撤銷只做了hub_db那一半時_重新啟動補做sync_db那一半()
    {
        var dir = TempDir.Create();
        try
        {
            string deviceId;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                using var device = await TestDevice.AddAsync(host);
                deviceId = device.Id;
                await AppendAsync(host, deviceId, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))));
                await MarkRevokedInHubDbAsync(host, deviceId, DeviceRegistryRules.RevokedRetired);
                ServerPayloads<DeviceRevokedPayload>(host, SyncEventKinds.DeviceRevoked).Should().BeEmpty("對照：sync.db 那一半還沒做");
            }
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                LinksOf(host, deviceId).Should().Be(0);
                ServerPayloads<DeviceRevokedPayload>(host, SyncEventKinds.DeviceRevoked).Should().ContainSingle(p => p.DeviceId == deviceId && p.Reason == "retired");
                LatestAccount(host).Links.Should().BeEmpty();
            }
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
                ServerPayloads<DeviceRevokedPayload>(host, SyncEventKinds.DeviceRevoked).Should().ContainSingle("再啟動不重發");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    // ───── 工具 ─────

    private static long LinksOf(HubTestHost host, string deviceId) => Count(host, $"SELECT COUNT(*) FROM account_links WHERE device_id = '{deviceId}'");

    /// <summary>只在 hub.db 標撤銷並更新裝置目錄（撤銷在 hub.db 提交之後中斷時留下的狀態）</summary>
    private static async Task MarkRevokedInHubDbAsync(HubTestHost host, string deviceId, string reason)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyriagentHub.Data.HubDbContext>();
        var row = await db.Devices.FirstAsync(d => d.Id == deviceId);
        row.RevokedAt = host.Clock.NowMs;
        row.RevokedReason = reason;
        await db.SaveChangesAsync();
        host.Services.GetRequiredService<DeviceDirectory>().Put(DeviceInfo.From(row));
    }

    private static async Task<int> AuditCountAsync(HubTestHost host, string action)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyriagentHub.Data.HubDbContext>();
        return await db.Audit.CountAsync(a => a.Action == action);
    }

    private static PairStartRequest Start(string spki, string name = "辦公室電腦") => new(name, "OFFICE-PC", "User", spki, PairingRules.KeyStorageSoftware, "0.1.0", SyncProtocol.Version);

    private static string NewSpki()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return P256KeyRules.ExportSpki(key);
    }

    private static async Task<HubClient> LoginAsync(OwnerHost owner)
    {
        var browser = owner.Host.CreateClient();
        (await OwnerFlows.LoginAsync(browser)).Dispose();
        return browser;
    }

    private static async Task<PairStartResponse> StartAsync(HubClient desktop, PairStartRequest request)
    {
        using var response = await desktop.PostAsync("/api/v1/pair/start", request, csrf: false, origin: null);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return SyncJson.Deserialize<PairStartResponse>(text)!;
    }

    private static async Task<PairError> PollAsync(HubClient desktop, string deviceCode)
    {
        using var response = await desktop.PostAsync("/api/v1/pair/token", new PairTokenRequest(deviceCode), csrf: false, origin: null);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        return SyncJson.Deserialize<PairError>(text)!;
    }

    private static async Task<PairTokenResponse> TokenAsync(HubClient desktop, string deviceCode)
    {
        using var response = await desktop.PostAsync("/api/v1/pair/token", new PairTokenRequest(deviceCode), csrf: false, origin: null);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return SyncJson.Deserialize<PairTokenResponse>(text)!;
    }

    private static async Task ExpectErrorAsync(HubClient client, string path, object body, HttpStatusCode status, string code)
    {
        using var response = await client.PostAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        JsonDocument.Parse(text).RootElement.GetProperty("error").GetString().Should().Be(code);
    }
}
