using Myriagent.Core;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// 第 8 步：對 MyriagentHub 子程序（真的 Kestrel、自簽憑證、Testing 環境）的配對與交握。同一個類別共用一個 MyriagentHub，
/// 每個測試配對自己的裝置（記憶體金鑰；只有 <see cref="真的CNG軟體金鑰_配對與交握"/> 用 CNG）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class PairingHandshakeTests(MyriagentHubFixture fixture) : IClassFixture<MyriagentHubFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private MyriagentHubProcess Hub => fixture.Hub;

    private OwnerClient Owner => fixture.Owner;

    /// <summary>R3：SignalR .NET 用戶端的實測點——略過 negotiate、自訂 WebSocketFactory 帶 Authorization、在 TLS 交握擷取的葉憑證 SPKI 與伺服器的相同，Hello／Prove 過得了</summary>
    [Fact]
    public async Task SignalR_自訂WebSocketFactory_SPKI相符_Prove成功()
    {
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);
        var device = paired.Device;
        var token = DeviceSecrets.Unprotect(device.ProtectedDeviceToken)!;
        var server = new Uri(device.ServerUrl);
        var certificateSpki = await Hub.CertificateSpkiAsync();
        var events = new RecordingSessionEvents();
        using var timeout = new CancellationTokenSource(Wait);

        var session = await new SignalRSyncTransport().ConnectAsync(new SyncConnectRequest(server, server, token, DevTrustEnv: true), events, timeout.Token);
        try
        {
            session.ObservedSpkiSha256.Should().Be(certificateSpki, "擷取的是這條連線的葉憑證");

            var nonceD = HandshakeRules.NewNonce();
            var hello = await session.HelloAsync(
                new HelloRequest(
                    SyncProtocol.Version,
                    device.DeviceId,
                    Guid.NewGuid().ToString("N"),
                    DevicePairing.AppVersion,
                    "TEST-PC",
                    "tester",
                    nonceD,
                    session.ObservedSpkiSha256,
                    null,
                    OutboxRules.Position([], 1),
                    new Dictionary<string, long>(StringComparer.Ordinal) { [SyncClasses.Catalog] = 0, [SyncClasses.Usage] = 0 },
                    new DevicePolicy("none", false)),
                timeout.Token);
            HandshakeRules.CheckHubIdentity(device.HubIdentitySpki, device.DeviceId, nonceD, session.ObservedSpkiSha256, hello).Outcome
                .Should().Be(HubIdentityOutcome.Ok, "伺服器以配對時釘的身分金鑰簽了這次的 nonce 與 SPKI");

            var signature = paired.MemoryKey!.Sign(HandshakeRules.DeviceSignedData(device.DeviceId, hello.NonceS, nonceD, session.ObservedSpkiSha256));
            var prove = await session.ProveAsync(new ProveRequest(signature), timeout.Token);

            prove.NextDeviceSeqExpected.Should().Be(1);
            prove.Resync.Should().BeFalse();
            prove.AccessToken.Should().NotBeNullOrEmpty();
            prove.TokenRotation.Should().BeNull("剛配對的權杖不必輪換");
        }
        finally
        {
            await session.DisposeAsync();
        }

        (await events.Closed.WaitAsync(Wait)).Should().BeNull("自己關的連線沒有錯誤");
    }

    [Fact]
    public async Task 配對_核准後Hello_Prove_Live()
    {
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);

        File.Exists(dir.Paths.DeviceFile).Should().BeTrue("device.json 是提交點");
        File.Exists(dir.Paths.DatabaseFile).Should().BeTrue();
        File.Exists(dir.Paths.PendingKeyFile).Should().BeFalse("配對完成就刪 pending 檔");
        paired.Device.ServerUrl.Should().Be(Hub.BaseAddress.GetLeftPart(UriPartial.Authority));
        paired.Device.DevicePubKey.Should().Be(paired.MemoryKey!.PublicSpki);

        await using var engine = new HubEngine(paired);
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);

        var (seen, registryRev, catalogDevices) = await engine.Inspect(v => (v.SeenServer, v.RegistryRev, v.Catalog?.Devices.Keys.ToList()));
        seen.Should().NotBeNull("Hello 之後記下伺服器實例");
        registryRev.Should().BeGreaterThanOrEqualTo(1);
        catalogDevices.Should().Contain(paired.DeviceId, "拉取拿到自己的 device.upsert");
        fixture.Db.Audit("device.hello").Should().Contain(a => a.ActorId == paired.DeviceId && a.Outcome == "ok");
        await engine.WaitUntilAsync(_ => engine.Host.LastStatus is { State: SyncEngineState.Live }, Wait, "狀態快照送到宿主");
        engine.Host.LastStatus!.DeviceFingerprint.Should().Be(P256KeyRules.Fingerprint(paired.Device.DevicePubKey));
        UserKeyStore.FilesCreatedByTestHub().Should().NotBeEmpty("執行中的 MyriagentHub 的 TLS 私鑰檔（陽性對照：類別結束時的孤兒金鑰檢查認得出本程序的 MyriagentHub 建的檔）");
    }

    /// <summary>SEC-08 的裝置端：權杖只換得到一次</summary>
    [Fact]
    public async Task 同一個deviceCode再換一次_invalid_grant()
    {
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);
        using var client = new PairingClient(Hub.BaseAddress, Hub.BaseAddress, devTrustEnv: true);
        using var timeout = new CancellationTokenSource(Wait);

        var again = await client.PollAsync(paired.Start.DeviceCode, timeout.Token);

        again.Should().BeOfType<PairPollResult.Error>().Which.Body.Error.Should().Be(PairingErrors.InvalidGrant);
    }

    [Fact]
    public async Task 配對_拒絕_access_denied_刪pending檔與金鑰()
    {
        using var dir = new DeviceDir();
        var attempt = await DevicePairing.BeginAsync(Hub, dir.Paths);
        File.Exists(dir.Paths.PendingKeyFile).Should().BeTrue("建金鑰之前就寫 pending 檔（陽性對照）");

        await Owner.DenyAsync(attempt.Start.UserCode);
        var act = () => attempt.Run.WaitAsync(DevicePairing.Timeout);

        (await act.Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingErrors.AccessDenied);
        attempt.Progress.FailedCode.Should().Be(PairingErrors.AccessDenied);
        File.Exists(dir.Paths.PendingKeyFile).Should().BeFalse("放棄的配對刪 pending 檔");
        File.Exists(dir.Paths.DeviceFile).Should().BeFalse();
        File.Exists(dir.Paths.DatabaseFile).Should().BeFalse();
        attempt.IssuedKey!.IsDisposed.Should().BeTrue("金鑰物件已釋放（CNG 金鑰的刪除另有 PairingSessionTests）");
    }

    /// <summary>SEC-09：權杖正確、簽章用另一把私鑰：伺服器擋在 Prove（稽核 bad_device_signature），引擎進不了 Live</summary>
    [Fact]
    public async Task 只有權杖沒有私鑰_Prove失敗()
    {
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);

        await using var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1), _ => new ForgedSigner(paired.Device.DevicePubKey));
        engine.Start();
        await engine.WaitUntilAsync(
            _ => fixture.Db.Audit("device.hello").Any(a => a.ActorId == paired.DeviceId && a.Outcome == "denied" && (a.Detail ?? "").Contains("bad_device_signature", StringComparison.Ordinal)),
            Wait,
            "稽核記下 bad_device_signature");
        await engine.WaitUntilAsync(v => v.ConnectionGeneration >= 2, Wait, "被拒之後退避重連（不是停止）");

        engine.Host.Statuses.Select(s => s.Status.State).Should().NotContain(SyncEngineState.Live);
        (await engine.StateAsync()).Should().NotBe(SyncEngineState.Live);
        fixture.Db.DeviceEvents(paired.DeviceId).Should().BeEmpty("沒有證明持有私鑰就送不了任何事件");
    }

    [Fact]
    public async Task 格式正確的假權杖_401_Stopped不重試()
    {
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);
        DeviceFile.Save(dir.Paths.DeviceFile, paired.Device with { ProtectedDeviceToken = DeviceSecrets.Protect(PairingTestData.NewToken()) });

        await using var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1));
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Stopped, Wait);

        (await engine.Inspect(v => v.StopReason)).Should().Be(SyncStopReason.Unauthorized, "WebSocket 升級回 401（CollectHttpResponseDetails 讀到狀態碼）");
        var generation = await engine.Inspect(v => v.ConnectionGeneration);
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await engine.Inspect(v => (v.State, v.ConnectionGeneration))).Should().Be((SyncEngineState.Stopped, generation), "401 不重試（退避步階 0.2 秒，2 秒內本來會重連好幾次）");
        engine.Current.BackoffHistory.Should().BeEmpty();
    }

    /// <summary>只有這一個用真的 CNG 軟體金鑰（測完刪除；以檔案內容認自己的金鑰檔，別的測試同時建刪的不算）</summary>
    [Fact]
    public async Task 真的CNG軟體金鑰_配對與交握()
    {
        using var dir = new DeviceDir();
        string? keyName = null;
        try
        {
            var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths, (name, tryTpm) =>
            {
                keyName = name;
                return DeviceKey.Create(name, tryTpm, Log.Info);
            });
            paired.Device.KeyStorage.Should().Be(PairingRules.KeyStorageSoftware, "測試不試 TPM");
            paired.MemoryKey.Should().BeNull();

            await using (var engine = new HubEngine(paired))
            {
                engine.Start();
                await engine.WaitForStateAsync(SyncEngineState.Live, Wait);
            }

            UserKeyStore.FilesNaming(keyName!).Should().ContainSingle("CNG 軟體金鑰存成一個檔案（陽性對照：認得出自己的金鑰檔）");
        }
        finally
        {
            if (keyName is not null)
                DeviceKey.Delete(keyName, PairingRules.KeyStorageSoftware);
        }

        DeviceKey.Exists(keyName!, PairingRules.KeyStorageSoftware).Should().BeFalse();
        UserKeyStore.FilesNaming(keyName!).Should().BeEmpty("刪除後金鑰檔也不在了");
    }
}
