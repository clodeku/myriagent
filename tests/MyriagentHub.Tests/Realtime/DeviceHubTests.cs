using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Pairing;
using MyriagentHub.Realtime;
using MyriagentHub.Releases;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Media;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Tls;
using MyriagentHub.Trust;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.EventLogTests;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 裝置 hub（sync-protocol 3、4）：Hello／Prove 雙向簽章綁 TLS 憑證、Prove 前只能交握、批次提交後才確認、通知、
/// 權杖（chd_ 只連 hub、cha_ 只給裝置端點、30 天輪換）、上下線、用量指派、信任與排空的通知、還原後的對帳模式；
/// U2：排空中的 Hello 回 draining、HelloResponse 的 latestApp、收件後的 appRelease 通知、舊版的只更新連線（updateOnly）
/// </summary>
public sealed class DeviceHubTests
{
    [Fact]
    public async Task 交握_伺服器簽章驗得過_Prove發存取權杖與期待值_上線發布device_upsert()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        var nonceD = HandshakeRules.NewNonce();

        var hello = await client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, nonceD));

        var identity = host.Services.GetRequiredService<HubIdentity>();
        hello.HubIdentitySpki.Should().Be(identity.Spki);
        HandshakeRules.VerifyHub(identity.Spki, device.Id, nonceD, hello.NonceS, observed, hello.ServerTime, hello.HubSig).Should().BeTrue();
        HandshakeRules.CheckHubIdentity(identity.Spki, device.Id, nonceD, observed, hello).Outcome.Should().Be(HubIdentityOutcome.Ok);
        hello.Server.Should().Be(host.Services.GetRequiredService<SyncWriter>().Instance);
        hello.ServerTime.Should().Be(host.Clock.NowMs);
        hello.Limits.Should().Be(SyncLimits.Default with { MaxLiveDeltaHz = null }, "沒宣告 live2 的裝置不帶 maxLiveDeltaHz（DeviceHubRules.HelloLimits）");
        hello.MinAppVersion.Should().Be("0.1.0");

        var sig = P256KeyRules.Sign(device.Key, HandshakeRules.DeviceSignedData(device.Id, hello.NonceS, nonceD, observed));
        var prove = await client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig));

        prove.AccessToken.Should().StartWith(SyncProtocol.AccessTokenPrefix);
        prove.AccessTokenExpiresAt.Should().Be(host.Clock.NowMs + 15 * 60_000);
        prove.NextDeviceSeqExpected.Should().Be(1);
        prove.Resync.Should().BeFalse();
        prove.UsageAssignments.Should().BeEmpty();
        prove.TokenRotation.Should().BeNull();
        host.Services.GetRequiredService<ConnectionRegistry>().IsOnline(device.Id).Should().BeTrue();
        var upsert = ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Last(p => p.DeviceId == device.Id);
        upsert.Online.Should().BeTrue();
        upsert.Policy.Should().Be(new DevicePolicy("Control", false));
        await WaitForAuditAsync(host, AuditActions.DeviceHello);

        // 存取權杖只給 /api/v1/device/*（第 3 階段沒有端點：認證過了、落到 404）
        using var http = host.CreateClient(cookies: false);
        http.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", prove.AccessToken);
        (await http.GetAsync("/api/v1/device/blobs/x")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await http.GetAsync("/readyz")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "存取權杖不能用在別的地方");
        var refreshed = await client.Connection.InvokeAsync<AccessTokenResponse>(DeviceHubMethods.RefreshAccessToken);
        refreshed.AccessToken.Should().NotBe(prove.AccessToken);
    }

    [Fact]
    public async Task Prove之前只能交握_Hello只能一次_格式或版本不對分別回invalid與protocol()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var observed = DeviceHubClient.ObservedSpki(host);

        // 裝置取最後一個「HubException: 」之後的字串當代碼（sync-protocol 11）
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<PullResponse>(DeviceHubMethods.Pull, new PullRequest([new PullCursor(0, Cls: SyncClasses.Catalog)], 10, 1000)))
            .Should().ThrowAsync<HubException>().WithMessage("An unexpected error occurred invoking 'Pull' on the server. HubException: not_proven");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest("x")))
            .Should().ThrowAsync<HubException>().WithMessage("*not_proven");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce()) with { ProtocolVersion = 2 }))
            .Should().ThrowAsync<HubException>().WithMessage("*protocol");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce(), appVersion: "0.0.9")))
            .Should().ThrowAsync<HubException>().WithMessage("*protocol");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, "短")))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce()) with { DeviceId = SyncIds.NewDeviceId() }))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid", "deviceId 必須是權杖的那台");

        await client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce()));
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce())))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid");
    }

    [Fact]
    public async Task 裝置簽章不符_中斷並稽核()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        var nonceD = HandshakeRules.NewNonce();
        var hello = await client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, nonceD));

        // 偷到 chd_ 但沒有裝置私鑰
        using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sig = P256KeyRules.Sign(thief, HandshakeRules.DeviceSignedData(device.Id, hello.NonceS, nonceD, observed));
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig)))
            .Should().ThrowAsync<Exception>();

        await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        host.Services.GetRequiredService<ConnectionRegistry>().IsOnline(device.Id).Should().BeFalse();
        await WaitForDeniedHelloAsync(host, "bad_device_signature");
    }

    [Fact]
    public async Task 裝置看到的憑證不是這條連線的_中間人_中斷並稽核()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var forged = Base64UrlRules.Encode(RandomNumberGenerator.GetBytes(32));

        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, forged, HandshakeRules.NewNonce())))
            .Should().ThrowAsync<Exception>();

        await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForDeniedHelloAsync(host, "spki_mismatch");
    }

    [Fact]
    public async Task 發佈批次_提交後送BatchAck_通知有新事件_拉取拿到結果()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        var batch = Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com")), Ev(2, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0)));

        await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, batch);

        var ack = await client.WaitForAsync<BatchAck>(a => a.BatchId == batch.BatchId);
        ack.Error.Should().BeNull();
        ack.AcceptedThrough.Should().Be(2);
        ack.Results.Should().OnlyContain(r => r.Seq != null);
        // 衍生的 account.upsert、prefs.set 在批次提交之後才另外寫，第一則通知可能還沒包含它們：等到有一則通知追上通知器的 High
        var notifier = host.Services.GetRequiredService<SyncNotifier>();
        var notify = await client.WaitForAsync<Notify>(n => n.Cls.Contains(SyncClasses.Catalog) && n.High >= ack.Results[1].Seq && n.High == notifier.High);
        notify.High.Should().Be(notifier.High);

        var pulled = await client.Connection.InvokeAsync<PullResponse>(DeviceHubMethods.Pull, new PullRequest([new PullCursor(0, Cls: SyncClasses.Catalog)], 500, 1_000_000));
        pulled.Events.Select(e => e.Kind).Should().Contain([SyncEventKinds.LinkUpsert, SyncEventKinds.AccountUpsert, SyncEventKinds.PrefsSet]);
        pulled.Events.Single(e => e.Kind == SyncEventKinds.LinkUpsert).Payload.Should().Be(batch.Events[0].Payload, "存的與發出去的是同一串");

        // 重送同一批（確認遺失）：回同樣的結果
        await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, batch);
        (await client.WaitForAsync<BatchAck>(a => a.BatchId == batch.BatchId)).Should().BeEquivalentTo(ack);

        await client.Connection.SendAsync(DeviceHubMethods.Heartbeat, new Heartbeat(host.Clock.NowMs, [], []));
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync(DeviceHubMethods.UsagePublish, new UsagePublish("la_x", null, null!)))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<PullResponse>(DeviceHubMethods.Pull, new PullRequest([new PullCursor(0)], 10, 10)))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid");
        var bundle = await client.Connection.InvokeAsync<TrustBundle>(DeviceHubMethods.GetTrust, new GetTrustRequest(0));
        bundle.Should().NotBeNull();
    }

    [Fact]
    public async Task 撤銷之後送來的批次_不寫入_不回BatchAck_中斷連線()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        // 撤銷的第一步（標目錄）已經做了、還沒中斷連線的那一刻
        var directory = host.Services.GetRequiredService<DeviceDirectory>();
        directory.Put(directory.Get(device.Id)! with { RevokedAt = host.Clock.NowMs, RevokedReason = "compromised" });

        var batch = Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com")));
        await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, batch);

        (await Task.WhenAny(client.Closed, Task.Delay(5000))).Should().Be(client.Closed, "伺服器中斷連線");
        var received = new List<object>();
        while (client.Received.Reader.TryRead(out var message))
            received.Add(message);
        received.OfType<BatchAck>().Should().BeEmpty();
        Count(host, $"SELECT COUNT(*) FROM events WHERE author = '{device.Id}'").Should().Be(0);
        Count(host, "SELECT COUNT(*) FROM account_links").Should().Be(0);
    }

    [Fact]
    public async Task 不開stateful_reconnect_協商不給重連_每條傳輸都要重新交握()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        using var http = host.CreateClient(cookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncProtocol.DeviceHubPath + "/negotiate?negotiateVersion=1&useStatefulReconnect=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Token);

        using var response = await http.Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var json = System.Text.Json.JsonDocument.Parse(text);
        (json.RootElement.TryGetProperty("useStatefulReconnect", out var stateful) && stateful.GetBoolean()).Should().BeFalse(text);
    }

    [Fact]
    public async Task 權杖_cha不能連hub_chd用了30天在Prove時輪換_舊的24小時內再連會再發一次()
    {
        await using var host = await HubTestHost.StartAsync();
        var thirtyOneDaysAgo = host.Clock.NowMs - 31L * 24 * 60 * 60 * 1000;
        using var device = await TestDevice.AddAsync(host, tokenIssuedAt: thirtyOneDaysAgo);
        var observed = DeviceHubClient.ObservedSpki(host);
        var original = device.Token;

        await using var first = await DeviceHubClient.ConnectAsync(host, original);
        var (_, prove) = await first.HandshakeAsync(device, observed);
        prove.TokenRotation.Should().StartWith(SyncProtocol.DeviceTokenPrefix);
        var rotated = prove.TokenRotation!;
        await WaitForAuditAsync(host, AuditActions.DeviceTokenRotated);

        await FluentActions.Awaiting(() => DeviceHubClient.ConnectAsync(host, prove.AccessToken)).Should().ThrowAsync<Exception>("cha_ 不能連裝置 hub");

        await using (var withNew = await DeviceHubClient.ConnectAsync(host, rotated))
            (await withNew.HandshakeAsync(device, observed, bootId: "boot-2")).Prove.TokenRotation.Should().BeNull("新權杖不必再換");

        // 裝置沒存好新的、還用舊的連：24 小時內可以，Prove 時再發一次（舊的期限不延長）
        host.Clock.Advance(TimeSpan.FromHours(23));
        await using (var withOld = await DeviceHubClient.ConnectAsync(host, original))
            (await withOld.HandshakeAsync(device, observed, bootId: "boot-3")).Prove.TokenRotation.Should().StartWith(SyncProtocol.DeviceTokenPrefix);

        host.Clock.Advance(TimeSpan.FromHours(2));
        await FluentActions.Awaiting(() => DeviceHubClient.ConnectAsync(host, original)).Should().ThrowAsync<Exception>("舊權杖過了 24 小時");
    }

    [Fact]
    public async Task 上線時指派用量查詢_斷線發布離線()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await AppendAsync(host, device.Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))));
        var la = LatestAccount(host).LogicalAccountId;
        var client = await DeviceHubClient.ConnectAsync(host, device.Token);

        var (_, prove) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), outbox: new OutboxPosition(2, null, 1));

        prove.NextDeviceSeqExpected.Should().Be(2);
        prove.UsageAssignments.Should().Equal(new UsageAssign(la, 1, true));

        await client.DisposeAsync();
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        for (var i = 0; i < 100 && registry.IsOnline(device.Id); i++)
            await Task.Delay(50);
        registry.IsOnline(device.Id).Should().BeFalse();
        for (var i = 0; i < 100 && ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Last(p => p.DeviceId == device.Id).Online; i++)
            await Task.Delay(50);
        ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Last(p => p.DeviceId == device.Id).Online.Should().BeFalse();
    }

    [Fact]
    public async Task 信任資料變了送TrustChanged_開始排空送ServerNotice()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));

        await host.Services.GetRequiredService<TrustService>().PublishAsync();
        await client.WaitForAsync<TrustChanged>();

        host.Services.GetRequiredService<DrainState>().Drain(DrainSource.Admin, host.Clock.NowMs, 45, 300, null);
        (await client.WaitForAsync<ServerNotice>()).Should().Be(new ServerNotice("shutdown", 45));
    }

    [Fact]
    public async Task 伺服器還原後_Prove回對帳模式_期待值是保留窗起點()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c =>
        {
            Catalog.EnsureStream(c, device.Id);
            c.Execute("UPDATE device_streams SET next_device_seq = 50, needs_resync = 1 WHERE device_id = $d", ("$d", device.Id));
        });
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);

        var (_, prove) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), outbox: new OutboxPosition(100, 60, 40));

        prove.Resync.Should().BeTrue();
        prove.NextDeviceSeqExpected.Should().Be(40, "min(伺服器的 50, 保留的最小 40)");
        host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(device.Id).Single().Upstream.ResyncFrom.Should().Be(40);
    }

    [Fact]
    public async Task 真的TLS_Hello綁這條連線送出的憑證_換憑證後舊連線照樣通過_新連線要新的()
    {
        await using var host = await HubTestHost.StartAsync(kestrel: true);
        using var device = await TestDevice.AddAsync(host);
        var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(host, device.Token);
        await using (client)
        {
            // 連上之後才換憑證：這條連線的憑證仍是舊的（不是比對「目前的憑證」）
            var holder = host.Services.GetRequiredService<CertificateHolder>();
            holder.Swap(X509CertificateLoader.LoadPkcs12(TlsCertificates.CreateSelfSignedPfx("localhost", TimeSpan.FromDays(2), DateTimeOffset.UtcNow), null), null, selfSigned: true);
            var (_, prove) = await client.HandshakeAsync(device, HandshakeRules.SpkiSha256(seen));
            prove.AccessToken.Should().StartWith(SyncProtocol.AccessTokenPrefix);
        }

        var (again, seenAgain) = await DeviceHubClient.ConnectKestrelAsync(host, device.Token);
        await using (again)
        {
            seenAgain.Thumbprint.Should().NotBe(seen.Thumbprint);
            await FluentActions.Awaiting(() => again.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, HandshakeRules.SpkiSha256(seen), HandshakeRules.NewNonce())))
                .Should().ThrowAsync<Exception>("舊憑證的 SPKI 不是這條連線的");
        }
    }

    [Fact]
    public async Task 排空中的Hello回draining_不寫稽核_解除後照常交握()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        var drain = host.Services.GetRequiredService<DrainState>();
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        drain.Drain(DrainSource.Admin, host.Clock.NowMs, 45, 300, null);

        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce())))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: draining");

        // 在寫稽核、碰資料庫之前就拒絕；連線不中斷（裝置自己照退避重連）
        (await AuditCountAsync(host, AuditActions.DeviceHello)).Should().Be(0);
        client.Closed.IsCompleted.Should().BeFalse();
        registry.IsOnline(device.Id).Should().BeFalse();

        // 解除之後同一條連線照常交握：被拒絕的 Hello 沒有記下 HelloState，不算「Hello 只能一次」
        drain.Undrain();
        var (_, prove) = await client.HandshakeAsync(device, observed);

        prove.AccessToken.Should().StartWith(SyncProtocol.AccessTokenPrefix);
        registry.IsOnline(device.Id).Should().BeTrue();
        (await AuditCountAsync(host, AuditActions.DeviceHello, AuditOutcomes.Ok)).Should().Be(1);
        (await AuditCountAsync(host, AuditActions.DeviceHello)).Should().Be(1, "排空中的那次 Hello 沒有留下任何稽核");
    }

    [Fact]
    public async Task HelloResponse帶latestApp_沒有可用版本時省略()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartReleaseHostAsync(fixture);
        using var device = await TestDevice.AddAsync(host);

        var (before, raw) = await HelloOnlyAsync(host, device);

        raw.ContainsKey("latestApp").Should().BeFalse("沒有可用版本時省略這個欄位，不是送 null");
        before.LatestApp.Should().BeNull();

        (await IngestAsync(host, fixture.Build("0.2.300-ab12cd3"), "t6-latest-1")).Message.Should().Be(AppReleaseIngestCodes.Ingested);
        var (after, rawAfter) = await HelloOnlyAsync(host, device);

        rawAfter.ContainsKey("latestApp").Should().BeTrue();
        rawAfter["latestApp"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"release":"0.2.300-ab12cd3","version":"0.2.300"}""");
        rawAfter.Properties().Last().Name.Should().Be("latestApp", "新欄位加在最後（features 之後）");
        after.LatestApp.Should().Be(new AppReleaseInfo("0.2.300-ab12cd3", "0.2.300"));
    }

    [Fact]
    public async Task 撤回最新版後latestApp改成前一個可用版本()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartReleaseHostAsync(fixture);
        using var device = await TestDevice.AddAsync(host);
        (await IngestAsync(host, fixture.Build("0.2.300-ab12cd3"), "t6-withdraw-1")).Message.Should().Be(AppReleaseIngestCodes.Ingested);
        (await IngestAsync(host, fixture.Build("0.2.301-bc23de4"), "t6-withdraw-2")).Message.Should().Be(AppReleaseIngestCodes.Ingested);
        (await HelloOnlyAsync(host, device)).Hello.LatestApp.Should().Be(new AppReleaseInfo("0.2.301-bc23de4", "0.2.301"));

        var outcome = await host.Services.GetRequiredService<AppReleaseStore>().WithdrawAsync("0.2.301-bc23de4", CancellationToken.None);

        outcome.Should().Be(AppReleaseWithdrawOutcome.Withdrawn);
        (await HelloOnlyAsync(host, device)).Hello.LatestApp.Should().Be(new AppReleaseInfo("0.2.300-ab12cd3", "0.2.300"));
    }

    [Fact]
    public async Task 收件完成對已連線的裝置送appRelease_冪等重送不送_還沒Prove的不送()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartReleaseHostAsync(fixture);
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        // 只做了 Hello、還沒 Prove 的連線不在 devices 群組：收不到
        using var other = await TestDevice.AddAsync(host, "還沒Prove的電腦");
        await using var pending = await DeviceHubClient.ConnectAsync(host, other.Token);
        await pending.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(other, DeviceHubClient.ObservedSpki(host), HandshakeRules.NewNonce()));
        var package = fixture.Build("0.2.300-ab12cd3");

        (await IngestAsync(host, package, "t6-notice-1")).Message.Should().Be(AppReleaseIngestCodes.Ingested);

        (await client.WaitForAsync<ServerNotice>(n => n.Kind == ServerNoticeKinds.AppRelease))
            .Should().Be(new ServerNotice(ServerNoticeKinds.AppRelease, Release: "0.2.300-ab12cd3"));
        (await DrainReceivedAsync(pending, TimeSpan.FromMilliseconds(300))).OfType<ServerNotice>().Should().BeEmpty();

        // 同一版再送一次（runner 重跑 publish job）：回執 already_ingested，不再通知
        (await IngestAsync(host, package, "t6-notice-2")).Message.Should().Be(AppReleaseIngestCodes.AlreadyIngested);
        (await DrainReceivedAsync(client, TimeSpan.FromSeconds(1))).OfType<ServerNotice>().Where(n => n.Kind == ServerNoticeKinds.AppRelease).Should().BeEmpty();
    }

    [Fact]
    public async Task 排空中收件照樣送appRelease()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartReleaseHostAsync(fixture);
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        host.Services.GetRequiredService<DrainState>().Drain(DrainSource.Admin, host.Clock.NowMs, 45, 300, null);
        (await client.WaitForAsync<ServerNotice>(n => n.Kind == ServerNoticeKinds.Shutdown)).RetryAfterSec.Should().Be(45);

        // D17：排空只擋新的 Hello 與套件下載；收件與通知照常（裝置照下載端點的 Retry-After 稍後再下載）
        (await IngestAsync(host, fixture.Build("0.2.300-ab12cd3"), "t6-drain-1")).Message.Should().Be(AppReleaseIngestCodes.Ingested);

        (await client.WaitForAsync<ServerNotice>(n => n.Kind == ServerNoticeKinds.AppRelease)).Release.Should().Be("0.2.300-ab12cd3");
    }

    [Fact]
    public async Task Hello之後才開始排空_Prove補送shutdown()
    {
        // HUB-1：Hello 時還沒排空（照常回應），Prove 之前開始排空——那時的廣播只送已 Prove 的連線，這條連線收不到；Prove 加入群組之後補送
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        var nonceD = HandshakeRules.NewNonce();
        var hello = await client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, nonceD));

        host.Services.GetRequiredService<DrainState>().Drain(DrainSource.Admin, host.Clock.NowMs, 45, 300, null);
        (await DrainReceivedAsync(client, TimeSpan.FromMilliseconds(300))).OfType<ServerNotice>().Should().BeEmpty("前提：排空的廣播送不到還沒 Prove 的連線");
        var sig = P256KeyRules.Sign(device.Key, HandshakeRules.DeviceSignedData(device.Id, hello.NonceS, nonceD, observed));
        var prove = await client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig));

        prove.AccessToken.Should().StartWith(SyncProtocol.AccessTokenPrefix, "已經 Hello 過的連線照常完成交握（排空只擋新的 Hello）");
        (await client.WaitForAsync<ServerNotice>(n => n.Kind == ServerNoticeKinds.Shutdown)).Should().Be(new ServerNotice(ServerNoticeKinds.Shutdown, 45));
        (await DrainReceivedAsync(client, TimeSpan.FromMilliseconds(300))).OfType<ServerNotice>().Should().BeEmpty("只補送一次");
    }

    [Fact]
    public async Task Hello之後才收進新版_Prove補送appRelease_沒有變就不送()
    {
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartReleaseHostAsync(fixture);
        using var device = await TestDevice.AddAsync(host);
        var observed = DeviceHubClient.ObservedSpki(host);

        // Hello 回應的 latestApp 是 null，Prove 之前收進 0.2.300：收件的通知只送已 Prove 的連線
        await using (var client = await DeviceHubClient.ConnectAsync(host, device.Token))
        {
            var nonceD = HandshakeRules.NewNonce();
            var hello = await client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, nonceD));
            hello.LatestApp.Should().BeNull();
            (await IngestAsync(host, fixture.Build("0.2.300-ab12cd3"), "hub1-1")).Message.Should().Be(AppReleaseIngestCodes.Ingested);
            (await DrainReceivedAsync(client, TimeSpan.FromMilliseconds(300))).OfType<ServerNotice>().Should().BeEmpty("前提：收件的通知送不到還沒 Prove 的連線");
            var sig = P256KeyRules.Sign(device.Key, HandshakeRules.DeviceSignedData(device.Id, hello.NonceS, nonceD, observed));
            await client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig));

            (await client.WaitForAsync<ServerNotice>(n => n.Kind == ServerNoticeKinds.AppRelease))
                .Should().Be(new ServerNotice(ServerNoticeKinds.AppRelease, Release: "0.2.300-ab12cd3"));
        }

        // Hello 已經帶了最新版：Prove 不再送
        await using var again = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (latest, _) = await again.HandshakeAsync(device, observed, bootId: "boot-2");
        latest.LatestApp.Should().Be(new AppReleaseInfo("0.2.300-ab12cd3", "0.2.300"));
        (await DrainReceivedAsync(again, TimeSpan.FromMilliseconds(500))).OfType<ServerNotice>().Should().BeEmpty();
    }

    // ───── 只更新的連線（sync-protocol 1.4；設計 D4、D10、D20、D21）─────

    /// <summary>低於 <see cref="RaisedMinAppVersion"/> 的 app 版本</summary>
    private const string UpdateOnlyAppVersion = "0.1.5";

    /// <summary>U3 起的桌面在 Hello 宣告的功能（D4）</summary>
    private static readonly IReadOnlyList<string> UpdateOnlyFeatures = [DeviceFeatureRules.Update1];

    /// <summary>正式程式宣告的功能，依 HubApplication 的登記順序（AddHubRealtime、AddHubReleases、AddHubBrowser 裡的 AddHubCommands、AddHubMedia、AddHubStats）</summary>
    private static readonly string[] ServerDeclared =
        [DeviceFeatureRules.Conv1, DeviceFeatureRules.Prov1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Update1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1, DeviceFeatureRules.Media1, DeviceFeatureRules.Stat1, DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2, DeviceFeatureRules.Sched1, DeviceFeatureRules.Draft1, DeviceFeatureRules.Collab1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2, DeviceFeatureRules.Desk1];

    [Fact]
    public async Task 宣告live2的裝置_Hello的上限帶即時增量的頻率_只更新的連線不帶()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        var observed = DeviceHubClient.ObservedSpki(host);
        string[] features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Update1];

        await using (var client = await DeviceHubClient.ConnectAsync(host, device.Token))
        {
            var hello = await client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello,
                DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce(), appVersion: "0.2.0", features: features));
            hello.Limits.Should().Be(SyncLimits.Default);
            hello.Limits.MaxLiveDeltaHz.Should().Be(8);
        }
        await using (var client = await DeviceHubClient.ConnectAsync(host, device.Token))
        {
            var hello = await client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello,
                DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce(), appVersion: UpdateOnlyAppVersion, features: features));
            hello.Limits.MaxLiveDeltaHz.Should().BeNull("只更新的連線不啟用 live2");
        }
    }

    [Fact]
    public async Task 宣告update1的舊版_照常交握_回updateOnly()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        // 裝置權杖用了 31 天：只更新的連線照常輪換（D20）
        using var device = await TestDevice.AddAsync(host, tokenIssuedAt: host.Clock.NowMs - 31L * 24 * 60 * 60 * 1000);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        var nonceD = HandshakeRules.NewNonce();

        var hello = await client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello,
            DeviceHubClient.HelloRequest(device, observed, nonceD, appVersion: UpdateOnlyAppVersion, features: UpdateOnlyFeatures));

        var identity = host.Services.GetRequiredService<HubIdentity>();
        HandshakeRules.VerifyHub(identity.Spki, device.Id, nonceD, hello.NonceS, observed, hello.ServerTime, hello.HubSig).Should().BeTrue("Hello 照常簽章");
        hello.MinAppVersion.Should().Be("0.2.0");
        hello.Features.Should().Equal(ServerDeclared, "只更新的連線也照常列出伺服器宣告的全部");
        var sig = P256KeyRules.Sign(device.Key, HandshakeRules.DeviceSignedData(device.Id, hello.NonceS, nonceD, observed));
        var prove = await client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig));

        prove.UpdateOnly.Should().BeTrue();
        prove.Resync.Should().BeFalse();
        prove.UsageAssignments.Should().BeEmpty();
        prove.NextDeviceSeqExpected.Should().Be(1);
        prove.TokenRotation.Should().StartWith(SyncProtocol.DeviceTokenPrefix, "裝置權杖照常輪換（D20）");
        prove.AccessToken.Should().StartWith(SyncProtocol.AccessTokenPrefix);
        host.Services.GetRequiredService<AccessTokenStore>().Validate(prove.AccessToken).Should().Be(device.Id, "一般的存取權杖（D21：不分範圍）");

        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        registry.IsOnline(device.Id).Should().BeTrue("只更新的連線照樣登記：撤銷時中斷得到");
        registry.IsUpdateOnly(device.Id).Should().BeTrue();
        registry.IsSyncing(device.Id).Should().BeFalse();
        var connection = registry.ConnectionsOf(device.Id).Single();
        connection.UpdateOnly.Should().BeTrue();
        connection.IsProven.Should().BeTrue();
        connection.Pump.IsCompleted.Should().BeTrue("不啟動批次幫浦");
        connection.Features.Should().Equal(new[] { DeviceFeatureRules.Update1 });
        Count(host, $"SELECT COUNT(*) FROM device_streams WHERE device_id = '{device.Id}' AND app_version = '{UpdateOnlyAppVersion}'")
            .Should().Be(1, "照常記錄 Hello");
        var upsert = ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Last(p => p.DeviceId == device.Id);
        upsert.Online.Should().BeTrue();
        upsert.AppVersion.Should().Be(UpdateOnlyAppVersion);
        upsert.Features.Should().Equal(new[] { DeviceFeatureRules.Update1 });

        // device.hello 的稽核照舊（D21：detail 不多欄位）
        await WaitForAuditAsync(host, AuditActions.DeviceHello);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var detail = await db.Audit.Where(a => a.Action == AuditActions.DeviceHello && a.Outcome == AuditOutcomes.Ok).Select(a => a.Detail).SingleAsync();
            using var json = System.Text.Json.JsonDocument.Parse(detail!);
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("bootId", "appVersion", "resync");
            json.RootElement.GetProperty("appVersion").GetString().Should().Be(UpdateOnlyAppVersion);
            json.RootElement.GetProperty("resync").GetBoolean().Should().BeFalse();
        }

        // 下載端點跟其他已配對的裝置一樣能用（還沒有版本時 latest 回 204）
        using var http = host.CreateClient(cookies: false);
        http.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", prove.AccessToken);
        (await http.GetAsync(AppReleaseRoutes.Latest)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task 只更新的連線只能呼叫RefreshAccessToken與Heartbeat()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        var (_, prove) = await client.HandshakeAsync(device, observed, appVersion: UpdateOnlyAppVersion, features: UpdateOnlyFeatures);
        prove.UpdateOnly.Should().BeTrue();

        // 裝置取最後一個「HubException: 」之後的字串當代碼（sync-protocol 11）
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<PullResponse>(DeviceHubMethods.Pull, new PullRequest([new PullCursor(0, Cls: SyncClasses.Catalog)], 10, 1000)))
            .Should().ThrowAsync<HubException>().WithMessage("An unexpected error occurred invoking 'Pull' on the server. HubException: update_required");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0)))))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: update_required");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync(DeviceHubMethods.UsagePublish, new UsagePublish("la_x", null, null!)))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: update_required", "關卡在方法之前：格式錯的也是 update_required，不是 invalid");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<TrustBundle>(DeviceHubMethods.GetTrust, new GetTrustRequest(0)))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: update_required");
        // 白名單：Prove 之後交握方法也擋（不是「Hello 只能一次」的 invalid）
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello,
                DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce(), appVersion: UpdateOnlyAppVersion, features: UpdateOnlyFeatures)))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: update_required");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest("x")))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: update_required");
        Count(host, $"SELECT COUNT(*) FROM events WHERE author = '{device.Id}'").Should().Be(0, "批次沒有入列");

        host.Clock.Advance(TimeSpan.FromSeconds(5));
        await client.Connection.InvokeAsync(DeviceHubMethods.Heartbeat, new Heartbeat(host.Clock.NowMs, [], []));
        host.Services.GetRequiredService<ConnectionRegistry>().LastHeartbeat(device.Id).Should().Be(host.Clock.NowMs, "心跳照常");

        var refreshed = await client.Connection.InvokeAsync<AccessTokenResponse>(DeviceHubMethods.RefreshAccessToken);
        refreshed.AccessToken.Should().NotBe(prove.AccessToken);
        host.Services.GetRequiredService<AccessTokenStore>().Validate(refreshed.AccessToken).Should().Be(device.Id, "一般的存取權杖（D21）");
        using var http = host.CreateClient(cookies: false);
        http.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        (await http.GetAsync(AppReleaseRoutes.Latest)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        client.Closed.IsCompleted.Should().BeFalse("被擋的呼叫不中斷連線");
    }

    [Fact]
    public async Task 只更新的連線不收Notify也不當用量候選()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var old = await TestDevice.AddAsync(host, "舊版電腦");
        using var current = await TestDevice.AddAsync(host, "新版電腦");
        // 兩台連結同一個帳戶；舊版那台有執行中的對話：它若算候選，會優先被指派（UsageRules.Choose）
        await AppendAsync(host, old.Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com") with { Sessions = new LinkSessions(1, 1, 0) })));
        await AppendAsync(host, current.Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-2", "claude:org1:a@example.com"))));
        var la = LatestAccount(host).LogicalAccountId;
        var observed = DeviceHubClient.ObservedSpki(host);
        await using var oldClient = await DeviceHubClient.ConnectAsync(host, old.Token);
        await using var currentClient = await DeviceHubClient.ConnectAsync(host, current.Token);

        var (_, oldProve) = await oldClient.HandshakeAsync(old, observed, outbox: new OutboxPosition(2, null, 1),
            appVersion: UpdateOnlyAppVersion, features: UpdateOnlyFeatures);
        var (_, currentProve) = await currentClient.HandshakeAsync(current, observed, outbox: new OutboxPosition(2, null, 1), appVersion: "0.2.0");

        oldProve.UpdateOnly.Should().BeTrue();
        oldProve.UsageAssignments.Should().BeEmpty();
        currentProve.UpdateOnly.Should().BeNull("只有只更新的連線才帶 updateOnly");
        currentProve.NextDeviceSeqExpected.Should().Be(2);
        currentProve.UsageAssignments.Should().Equal(new UsageAssign(la, 1, true));
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        registry.IsOnline(old.Id).Should().BeTrue();
        registry.IsSyncing(old.Id).Should().BeFalse();
        registry.IsUpdateOnly(old.Id).Should().BeTrue();
        registry.IsSyncing(current.Id).Should().BeTrue();
        registry.IsUpdateOnly(current.Id).Should().BeFalse();
        ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Last(p => p.DeviceId == old.Id).Online.Should().BeTrue("只更新的連線算在線");

        // 正向對照：完整連線的那台收得到 Notify 與 TrustChanged。
        // Notify 與 BatchAck 誰先到不保證（先等 BatchAck 會把先到的 Notify 略過），所以只等 high 超過送出前的 Notify
        var before = host.Services.GetRequiredService<SyncNotifier>().High;
        await currentClient.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, Batch(Ev(2, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0))));
        await currentClient.WaitForAsync<Notify>(n => n.High > before);
        await host.Services.GetRequiredService<TrustService>().PublishAsync();
        await currentClient.WaitForAsync<TrustChanged>();

        await Task.Delay(TimeSpan.FromSeconds(1));
        var received = new List<object>();
        while (oldClient.Received.Reader.TryRead(out var message))
            received.Add(message);
        received.OfType<Notify>().Should().BeEmpty("只更新的連線不在 devices 群組");
        received.OfType<TrustChanged>().Should().BeEmpty();
        received.OfType<UsageAssign>().Should().BeEmpty();

        // 之後的重算也不會把帳戶指派給只更新的那台（它有執行中的對話，算候選的話會搶走）
        var usage = host.Services.GetRequiredService<UsageCoordinator>();
        await usage.ReassessAsync();
        usage.AssignmentsFor(old.Id).Should().BeEmpty();
        usage.AssignmentsFor(current.Id).Should().Equal(new UsageAssign(la, 1, true));
    }

    [Fact]
    public async Task 只更新的連線照樣收到shutdown與appRelease()
    {
        // 收件的主機（StartReleaseHostAsync：夾具的暫時金鑰、固定的磁碟可用空間）另把 minAppVersion 調高
        using var fixture = ReleasePackageFixture.Create();
        await using var host = await StartReleaseHostAsync(fixture, RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, prove) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), appVersion: UpdateOnlyAppVersion, features: UpdateOnlyFeatures);
        prove.UpdateOnly.Should().BeTrue();

        // appRelease：收件完成（HubTestHost 的收件匣每秒輪詢）
        (await IngestAsync(host, fixture.Default, "t7-" + Guid.NewGuid().ToString("N"))).Message.Should().Be(AppReleaseIngestCodes.Ingested);
        var release = await client.WaitForAsync<ServerNotice>(n => n.Kind == ServerNoticeKinds.AppRelease);
        release.Release.Should().Be(fixture.Release);

        // 下載端點跟其他已配對的裝置一樣能用：latest 回這一版
        using var http = host.CreateClient(cookies: false);
        http.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", prove.AccessToken);
        using var response = await http.GetAsync(AppReleaseRoutes.Latest);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        SyncJson.Deserialize<LatestAppRelease>(text)!.Release.Should().Be(fixture.Release);

        // shutdown：開始排空
        host.Services.GetRequiredService<DrainState>().Drain(DrainSource.Admin, host.Clock.NowMs, 45, 300, null);
        (await client.WaitForAsync<ServerNotice>(n => n.Kind == ServerNoticeKinds.Shutdown)).Should().Be(new ServerNotice(ServerNoticeKinds.Shutdown, 45));
    }

    [Fact]
    public async Task 撤銷只更新的裝置會中斷連線()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, prove) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), appVersion: UpdateOnlyAppVersion, features: UpdateOnlyFeatures);
        prove.UpdateOnly.Should().BeTrue();
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        registry.IsUpdateOnly(device.Id).Should().BeTrue();

        await host.Services.GetRequiredService<PairingService>().RevokeAsync(device.Id, DeviceRegistryRules.RevokedCompromised, null, null);

        (await Task.WhenAny(client.Closed, Task.Delay(5000))).Should().Be(client.Closed, "撤銷照樣中斷只更新的連線");
        for (var i = 0; i < 100 && registry.IsOnline(device.Id); i++)
            await Task.Delay(50);
        registry.IsOnline(device.Id).Should().BeFalse();
        host.Services.GetRequiredService<AccessTokenStore>().Validate(prove.AccessToken).Should().BeNull("存取權杖一起作廢");
        using var http = host.CreateClient(cookies: false);
        http.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", prove.AccessToken);
        (await http.GetAsync(AppReleaseRoutes.Latest)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "下載端點也不能再用");
    }

    [Fact]
    public async Task 沒宣告update1的舊版照舊回protocol()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        var observed = DeviceHubClient.ObservedSpki(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        HelloRequest Old(IReadOnlyList<string>? features, string appVersion = UpdateOnlyAppVersion) =>
            DeviceHubClient.HelloRequest(device, observed, HandshakeRules.NewNonce(), appVersion: appVersion, features: features);

        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, Old(null)))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: protocol", "U3 之前的桌面不宣告：照舊回 protocol，桌面停止重試（D4）");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, Old([DeviceFeatureRules.Conv1])))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: protocol", "只宣告別的功能");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, Old(UpdateOnlyFeatures) with { ProtocolVersion = SyncProtocol.Version + 1 }))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: protocol", "協定比伺服器新（伺服器回滾）：宣告了 update1 也回 protocol");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, Old([DeviceFeatureRules.Update1, "Bad"])))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: invalid", "閘門判成只更新之後照樣檢查格式：features 不合格");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, Old(UpdateOnlyFeatures, new string('9', AppVersionRules.MaxLength + 1))))
            .Should().ThrowAsync<HubException>().WithMessage("*HubException: invalid", "appVersion 超過 64 字：解析不了當成太舊、走到格式檢查");
        host.Services.GetRequiredService<ConnectionRegistry>().IsOnline(device.Id).Should().BeFalse();
        Count(host, $"SELECT COUNT(*) FROM device_streams WHERE device_id = '{device.Id}'").Should().Be(0, "被拒的 Hello 不寫 device_streams");
    }

    [Fact]
    public async Task 同一台的完整與只更新連線並存_有完整的就算同步中()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        var observed = DeviceHubClient.ObservedSpki(host);
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();

        // 版本夠新時宣告 update1 也是完整連線
        await using var first = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, full) = await first.HandshakeAsync(device, observed, appVersion: "0.2.0", features: UpdateOnlyFeatures);
        full.UpdateOnly.Should().BeNull("只有只更新的連線才帶 updateOnly");
        registry.ConnectionsOf(device.Id).Single().Features.Should().Equal(new[] { DeviceFeatureRules.Update1 }, "完整連線照常協商");

        // 版本號解析不了：當成太舊，宣告了就是只更新（換版重啟的交疊：完整與只更新並存）
        var second = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, updateOnly) = await second.HandshakeAsync(device, observed, bootId: "boot-2", appVersion: "亂碼", features: UpdateOnlyFeatures);
        updateOnly.UpdateOnly.Should().BeTrue();
        registry.ConnectionsOf(device.Id).Select(c => c.UpdateOnly).Order().Should().Equal(false, true);
        registry.IsSyncing(device.Id).Should().BeTrue("另一條是完整連線");
        registry.IsUpdateOnly(device.Id).Should().BeFalse();
        Count(host, $"SELECT COUNT(*) FROM device_streams WHERE device_id = '{device.Id}' AND app_version = '亂碼'").Should().Be(1, "解析不了的版本號照樣記下");

        await second.DisposeAsync();
        for (var i = 0; i < 100 && registry.ConnectionsOf(device.Id).Count > 1; i++)
            await Task.Delay(50);
        registry.ConnectionsOf(device.Id).Single().UpdateOnly.Should().BeFalse();
        registry.IsSyncing(device.Id).Should().BeTrue();
    }

    [Fact]
    public async Task 換版交疊時UsageAssign只送完整連線_只更新的那條不收()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        await AppendAsync(host, device.Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))));
        var la = LatestAccount(host).LogicalAccountId;
        var observed = DeviceHubClient.ObservedSpki(host);

        // 舊版程序還連著（只更新），新版程序的完整連線上來：Prove 重算時把帳戶指派給這台，UsageAssign 推給這台的連線
        await using var old = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, oldProve) = await old.HandshakeAsync(device, observed, outbox: new OutboxPosition(2, null, 1),
            appVersion: UpdateOnlyAppVersion, features: UpdateOnlyFeatures);
        oldProve.UsageAssignments.Should().BeEmpty();
        await using var current = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (_, currentProve) = await current.HandshakeAsync(device, observed, outbox: new OutboxPosition(2, null, 1), bootId: "boot-2", appVersion: "0.2.0");

        currentProve.UsageAssignments.Should().Equal(new UsageAssign(la, 1, true));
        (await current.WaitForAsync<UsageAssign>()).Should().Be(new UsageAssign(la, 1, true), "正向對照：完整連線照收推送");
        await Task.Delay(TimeSpan.FromSeconds(1));
        var received = new List<object>();
        while (old.Received.Reader.TryRead(out var message))
            received.Add(message);
        received.OfType<UsageAssign>().Should().BeEmpty("只更新的連線不查用量：收到 on 會去查，UsagePublish 卻被關卡擋下（sync-protocol 1.4）");
    }

    [Fact]
    public async Task 只更新的連線不進對帳_對帳旗標留給換版後的完整連線()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c =>
        {
            Catalog.EnsureStream(c, device.Id);
            c.Execute("UPDATE device_streams SET next_device_seq = 50, needs_resync = 1 WHERE device_id = $d", ("$d", device.Id));
        });
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);

        var (_, prove) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), outbox: new OutboxPosition(100, 60, 40),
            appVersion: UpdateOnlyAppVersion, features: UpdateOnlyFeatures);

        prove.UpdateOnly.Should().BeTrue();
        prove.Resync.Should().BeFalse("只更新的連線不送批次，進了對帳也解除不了");
        prove.NextDeviceSeqExpected.Should().Be(50, "伺服器記的下一號，不是 min(50, 保留的最小 40)");
        host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(device.Id).Single().Upstream.ResyncFrom.Should().BeNull();
        Count(host, $"SELECT COUNT(*) FROM device_streams WHERE device_id = '{device.Id}' AND needs_resync = 1").Should().Be(1, "對帳旗標留給換版之後的完整連線");
        using var scope = host.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<HubDbContext>().Audit.CountAsync(a => a.Action == AuditActions.SyncResync))
            .Should().Be(0, "Prove 回應之前稽核已寫完：沒有開始對帳");
    }

    [Fact]
    public async Task 只更新的連線不啟用第4階段的功能()
    {
        await using var host = await HubTestHost.StartAsync(settings: RaisedMinAppVersion());
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);

        var (hello, prove) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), appVersion: UpdateOnlyAppVersion,
            features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Media1, DeviceFeatureRules.Update1]);

        prove.UpdateOnly.Should().BeTrue();
        hello.Features.Should().Equal(ServerDeclared, "伺服器宣告的照常列出");
        var connection = host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(device.Id).Single();
        connection.Features.Should().Equal(new[] { DeviceFeatureRules.Update1 }, "協商出的第 4 階段功能都不啟用");
        connection.Allows(DeviceHubMethods.CommandDelivery).Should().BeFalse("伺服器不對只更新的連線送第 14 節的訊息");
        connection.Allows(DeviceHubMethods.MediaRequest).Should().BeFalse();
        ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Last(p => p.DeviceId == device.Id).Features
            .Should().Equal(new[] { DeviceFeatureRules.Update1 });
    }

    [Fact]
    public void 連線登記_同一台有完整與只更新的連線時算同步中_全部只更新才算只更新()
    {
        var registry = new ConnectionRegistry();
        var deviceId = SyncIds.NewDeviceId();
        registry.IsUpdateOnly(deviceId).Should().BeFalse("不在線的不算只更新");
        registry.IsSyncing(deviceId).Should().BeFalse();

        registry.Add(new DeviceConnection("c1", deviceId, () => { }, new UpstreamContext(deviceId, null, null), updateOnly: true), 1);
        registry.IsOnline(deviceId).Should().BeTrue("只更新的連線也算在線");
        registry.LastHeartbeat(deviceId).Should().Be(1);
        registry.IsSyncing(deviceId).Should().BeFalse();
        registry.IsUpdateOnly(deviceId).Should().BeTrue();

        // 換版重啟的交疊：新版的完整連線上來了，舊版的只更新連線還沒斷
        registry.Add(new DeviceConnection("c2", deviceId, () => { }, new UpstreamContext(deviceId, null, null)), 2);
        registry.IsSyncing(deviceId).Should().BeTrue();
        registry.IsUpdateOnly(deviceId).Should().BeFalse();

        registry.Remove("c2");
        registry.IsSyncing(deviceId).Should().BeFalse("完整的那條斷了，剩下只更新的");
        registry.IsUpdateOnly(deviceId).Should().BeTrue();

        registry.Remove("c1");
        registry.IsOnline(deviceId).Should().BeFalse();
        registry.IsSyncing(deviceId).Should().BeFalse();
        registry.IsUpdateOnly(deviceId).Should().BeFalse();
    }

    /// <summary>minAppVersion 調高到 0.2.0：0.1.5 的桌面算太舊</summary>
    private static Dictionary<string, string?> RaisedMinAppVersion() => new() { ["Hub:Sync:MinAppVersion"] = "0.2.0" };

    private static async Task WaitForDeniedHelloAsync(HubTestHost host, string reason)
    {
        for (var i = 0; i < 100; i++)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            if (await db.Audit.AnyAsync(a => a.Action == AuditActions.DeviceHello && a.Outcome == AuditOutcomes.Denied && a.Detail!.Contains(reason)))
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("等不到稽核：" + reason);
    }

    // ───── U2：版本與通知（T6）─────

    /// <summary>收件的測試主機的磁碟可用空間（500 GB，同 AppReleaseInboxTests）：收件不看跑測試那台的磁碟，Hub:Releases:MinFreeDiskMb 維持預設</summary>
    private const long PlentyOfDisk = 500L << 30;

    /// <summary>
    /// 收件的測試主機：內建的發佈金鑰換成夾具的暫時金鑰（伺服器只認內建金鑰，D1）；
    /// 磁碟可用空間固定（FakeDiskSpace：不看跑測試那台的磁碟）。<paramref name="settings"/> 是另外的設定（只更新連線的測試調高 minAppVersion）
    /// </summary>
    private static Task<HubTestHost> StartReleaseHostAsync(ReleasePackageFixture fixture, IDictionary<string, string?>? settings = null) =>
        HubTestHost.StartAsync(settings: settings, services: s =>
        {
            s.AddSingleton<IBuiltInReleaseKeys>(new TestBuiltInReleaseKeys(fixture.Keys));
            s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(PlentyOfDisk));
        });

    /// <summary>
    /// 照 runner 的順序送進 app 發佈收件匣（T4 的 ReleasePackageFixture.Write：三個檔先，request.json 最後原子寫入），等到回執的 id 是這一次的。
    /// 伺服器先寫回執、再關閉並刪除收件匣的檔案（T4 第 10 步）：等它刪完才回傳，下一次放的檔才不會撞到這一次的清理
    /// </summary>
    private static async Task<AppReleaseReceipt> IngestAsync(HubTestHost host, ReleasePackageFixture.Package package, string id)
    {
        var inbox = host.Paths.AdminInboxAppReleaseDir;
        var names = ReleasePackageRules.FileNames(package.Release);
        string[] files = [Path.Combine(inbox, names.Zip), Path.Combine(inbox, names.Manifest), Path.Combine(inbox, names.Signature)];
        var request = Path.Combine(inbox, AppReleaseIngestRules.RequestFileName);
        ReleasePackageFixture.Write(inbox, id, host.Clock.NowMs, package);

        AppReleaseReceipt? receipt = null;
        for (var i = 0; i < 200 && receipt is null; i++)
        {
            try
            {
                var read = JsonSerializer.Deserialize<AppReleaseReceipt>(await File.ReadAllTextAsync(host.Paths.AppReleaseAckFile), StatusJson.Options);
                if (read?.Id == id)
                    receipt = read;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 還沒有回執，或伺服器正在換檔
            }
            if (receipt is null)
                await Task.Delay(100);
        }
        if (receipt is null)
            throw new TimeoutException("等不到 app 發佈的回執：" + id);

        string[] cleaned = receipt.Ok ? [.. files, request] : [request];
        for (var i = 0; i < 100 && cleaned.Any(File.Exists); i++)
            await Task.Delay(100);
        cleaned.Where(File.Exists).Should().BeEmpty("回執之後伺服器會刪掉收件匣的 request.json" + (receipt.Ok ? "與三個檔" : string.Empty));
        return receipt;
    }

    /// <summary>只做 Hello（每次新的連線，不 Prove）：回傳解析後的回應與線上的 JSON（看欄位有沒有出現）</summary>
    private static async Task<(HelloResponse Hello, JObject Raw)> HelloOnlyAsync(HubTestHost host, TestDevice device)
    {
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var raw = await client.Connection.InvokeAsync<JObject>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, DeviceHubClient.ObservedSpki(host), HandshakeRules.NewNonce()));
        return (SyncJson.Deserialize<HelloResponse>(raw.ToString(Newtonsoft.Json.Formatting.None))!, raw);
    }

    /// <summary>hub.db 的稽核筆數（Hello／Prove 的稽核在回應之前就寫完，不必等）</summary>
    private static async Task<int> AuditCountAsync(HubTestHost host, string action, string? outcome = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.Audit.CountAsync(a => a.Action == action && (outcome == null || a.Outcome == outcome));
    }

    /// <summary>等 <paramref name="quiet"/> 之後，把用戶端收到、還沒讀的訊息全部取出（用來確認某種通知「沒有」來）</summary>
    private static async Task<List<object>> DrainReceivedAsync(DeviceHubClient client, TimeSpan quiet)
    {
        await Task.Delay(quiet);
        var received = new List<object>();
        while (client.Received.Reader.TryRead(out var message))
            received.Add(message);
        return received;
    }
}
