using System.Net;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.Pairing;

/// <summary>
/// 配對流程（ManualTimeProvider 撥時間、ScriptedHttpHandler 當伺服器）。呼叫端在測試的 UI 執行緒上（正式環境是協調者在 WinForms 的 UI 執行緒），
/// 檢查 commit 一律在背景執行緒。需要確認「金鑰被刪掉」的情境用真的軟體 KSP 金鑰（finally 再刪一次）。
/// </summary>
public sealed class PairingSessionTests : IDisposable
{
    private readonly TempDirectory temp = new("pairing-session");
    private readonly ManualTimeProvider time = new();
    private readonly ScriptedHttpHandler http;
    private readonly UiThread ui = new();
    private readonly List<string> createdKeys = new();

    public PairingSessionTests() => http = new ScriptedHttpHandler(time);

    private SyncPaths Paths => new(temp.Combine("sync"));

    public void Dispose()
    {
        foreach (var name in createdKeys)
            DeviceKey.Delete(name, PairingRules.KeyStorageSoftware);
        ui.Dispose();
        temp.Dispose();
    }

    private PairingRequest Request(string name = "OFFICE-PC") =>
        new(new Uri(PairingTestData.Server), new Uri(PairingTestData.Server), name, "OFFICE-PC", "User", DevTrustEnv: false, TryTpm: false, Paths, "0.1.0");

    private static string Pending() => SyncJson.Serialize(new PairError(PairingErrors.AuthorizationPending));

    /// <summary>在 UI 執行緒上開始配對（await 的接續回到 UI 執行緒，同正式環境）</summary>
    private Task<PairingCommitResult> Run(
        IPairingProgress progress,
        Func<SyncPaths, PairTokenResponse, DeviceFileData, PairingCommitResult> commit,
        Func<string, bool, IDeviceSigner> keyFactory,
        CancellationToken ct = default,
        string name = "OFFICE-PC") =>
        ui.Invoke(() => PairingSession.RunAsync(Request(name), progress, time, commit, ct, keyFactory, http));

    /// <summary>真的 CNG 軟體金鑰（記下名稱，Dispose 時保險再刪一次）</summary>
    private Func<string, bool, IDeviceSigner> RealKeys(Action<string, string>? created = null) => (name, _) =>
    {
        lock (createdKeys)
            createdKeys.Add(name);
        var key = DeviceKey.Create(name, tryTpm: false);
        created?.Invoke(name, key.PublicSpki);
        return key;
    };

    [Fact]
    public async Task 核准_commit在非UI執行緒_收到正確的回應與裝置資料_刪pending檔()
    {
        var signer = new InMemoryDeviceSigner();
        var deviceId = SyncIds.NewDeviceId();
        var approval = PairingTestData.Approval(deviceId, signer.PublicSpki);
        var start = PairingTestData.Start(interval: 2);
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(start));
        http.Respond(HttpStatusCode.BadRequest, Pending());
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(approval));
        var progress = new RecordingProgress(ui);
        int? commitThread = null;
        PairTokenResponse? committedResponse = null;

        var run = Run(progress, (paths, response, device) =>
        {
            commitThread = Environment.CurrentManagedThreadId;
            committedResponse = response;
            File.Exists(paths.PendingKeyFile).Should().BeTrue("commit 完成之前 pending 檔還在");
            return new PairingCommitResult(device);
        }, (_, _) => signer);

        await time.WaitForTimersAsync();
        progress.Started.Should().ContainSingle();
        var (shown, fingerprint, expiresAt) = progress.Started[0];
        shown.UserCode.Should().Be(start.UserCode);
        fingerprint.Should().Be(P256KeyRules.Fingerprint(signer.PublicSpki));
        expiresAt.Should().Be(time.NowMs + 600_000);
        progress.OnUiThread.Should().BeTrue("進度回報在呼叫端（UI）的同步內容上");
        time.Advance(TimeSpan.FromSeconds(2));
        await http.WaitForRequestsAsync(2);
        await time.WaitForTimersAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        commitThread.Should().NotBeNull().And.NotBe(ui.Invoke(() => Environment.CurrentManagedThreadId), "commit 一律在背景執行緒");
        committedResponse!.DeviceId.Should().Be(deviceId);
        var device = result.Device;
        device.DeviceId.Should().Be(deviceId);
        device.DeviceName.Should().Be("OFFICE-PC");
        device.ServerUrl.Should().Be(PairingTestData.Server);
        device.KeyName.Should().StartWith(DeviceKey.NamePrefix);
        device.KeyStorage.Should().Be(PairingRules.KeyStorageSoftware);
        device.DevicePubKey.Should().Be(signer.PublicSpki);
        device.HubIdentitySpki.Should().Be(approval.HubIdentitySpki);
        device.PairedAt.Should().Be(time.NowMs);
        DeviceSecrets.Unprotect(device.ProtectedDeviceToken).Should().Be(approval.DeviceToken);
        File.Exists(Paths.PendingKeyFile).Should().BeFalse();
        progress.Failures.Should().BeEmpty();
        signer.IsDisposed.Should().BeTrue("配對結束後簽章者就關了（引擎之後自己開金鑰）");
        var requests = http.Requests;
        requests.Select(r => r.Uri.AbsolutePath).Should().Equal("/api/v1/pair/start", "/api/v1/pair/token", "/api/v1/pair/token");
        SyncJson.Deserialize<PairStartRequest>(requests[0].Body)!.DevicePubKey.Should().Be(signer.PublicSpki);
    }

    [Fact]
    public async Task 下一次輪詢在收到回應之後才等interval()
    {
        var signer = new InMemoryDeviceSigner();
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start(interval: 2)));
        var slow = http.Gate(HttpStatusCode.BadRequest, Pending());
        http.Respond(HttpStatusCode.BadRequest, SyncJson.Serialize(new PairError(PairingErrors.AccessDenied)));
        var progress = new RecordingProgress(ui);

        var run = Run(progress, (_, _, d) => new PairingCommitResult(d), (_, _) => signer);

        await time.WaitForTimersAsync();
        time.Advance(TimeSpan.FromSeconds(1.9));
        http.Requests.Should().HaveCount(1, "還沒滿 interval");
        time.Advance(TimeSpan.FromSeconds(0.1));
        await http.WaitForRequestsAsync(2);

        // 第一次輪詢的回應還沒回來：怎麼撥時間都不會送下一次
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        http.Requests.Should().HaveCount(2);
        time.ActiveTimers.Should().Be(0, "回應回來之前沒有排下一次");

        slow.SetResult();
        await time.WaitForTimersAsync();
        time.DueIn().Should().Equal(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(1.9));
        http.Requests.Should().HaveCount(2);
        time.Advance(TimeSpan.FromSeconds(0.1));

        (await run.Awaiting(r => r.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingErrors.AccessDenied);
        var requests = http.Requests;
        (requests[2].At - requests[1].At).Should().Be(TimeSpan.FromSeconds(32), "第二次輪詢距第一次的請求是 30 秒等回應＋2 秒間隔");
        progress.Failures.Should().ContainSingle().Which.Code.Should().Be(PairingErrors.AccessDenied);
    }

    [Fact]
    public async Task 取消_刪金鑰與pending檔()
    {
        string? keyName = null;
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start()));
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress(ui);

        var run = Run(progress, (_, _, d) => new PairingCommitResult(d), RealKeys((name, _) => keyName = name), cts.Token);
        await time.WaitForTimersAsync();
        DeviceKey.Exists(keyName!, PairingRules.KeyStorageSoftware).Should().BeTrue();
        File.Exists(Paths.PendingKeyFile).Should().BeTrue();
        cts.Cancel();

        await run.Awaiting(r => r.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<OperationCanceledException>();
        DeviceKey.Exists(keyName!, PairingRules.KeyStorageSoftware).Should().BeFalse();
        File.Exists(Paths.PendingKeyFile).Should().BeFalse();
        progress.Failures.Should().BeEmpty("取消不算失敗");
    }

    [Fact]
    public async Task 核准回應的名冊不含自己_失敗並刪金鑰()
    {
        string? keyName = null;
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start()));
        var deviceId = SyncIds.NewDeviceId();
        var stranger = new DeviceRegistryEntry(SyncIds.NewDeviceId(), "HOME-PC", PairingTestData.NewSpki(), 1);
        http.RespondWith(HttpStatusCode.OK, () => SyncJson.Serialize(PairingTestData.Approval(deviceId, PairingTestData.NewSpki(), registry: [stranger])));
        var progress = new RecordingProgress(ui);
        var committed = false;

        var run = Run(progress, (_, _, d) =>
        {
            committed = true;
            return new PairingCommitResult(d);
        }, RealKeys((name, _) => keyName = name));
        await time.WaitForTimersAsync();
        time.Advance(TimeSpan.FromSeconds(2));

        (await run.Awaiting(r => r.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingCodes.InvalidApproval);
        committed.Should().BeFalse();
        DeviceKey.Exists(keyName!, PairingRules.KeyStorageSoftware).Should().BeFalse();
        File.Exists(Paths.PendingKeyFile).Should().BeFalse();
        File.Exists(Paths.DeviceFile).Should().BeFalse();
        progress.Failures.Should().ContainSingle().Which.Code.Should().Be(PairingCodes.InvalidApproval);
    }

    [Fact]
    public async Task 建金鑰前已寫pending檔()
    {
        var pendingSeen = false;
        string? recordedStorage = null;
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start()));
        http.Respond(HttpStatusCode.BadRequest, SyncJson.Serialize(new PairError(PairingErrors.ExpiredToken)));
        var progress = new RecordingProgress(ui);

        var run = Run(progress, (_, _, d) => new PairingCommitResult(d), (name, tryTpm) =>
        {
            var (exists, record) = PendingKeyRecord.Read(Paths.PendingKeyFile);
            pendingSeen = exists && record?.KeyName == name;
            recordedStorage = record?.KeyStorage;
            tryTpm.Should().BeFalse();
            return new InMemoryDeviceSigner();
        });
        await time.WaitForTimersAsync();
        time.Advance(TimeSpan.FromSeconds(2));

        (await run.Awaiting(r => r.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingErrors.ExpiredToken);
        pendingSeen.Should().BeTrue("建金鑰之前 pending-key.json 已經寫好，記著同一個名稱");
        recordedStorage.Should().Be(PairingRules.KeyStorageSoftware);
        File.Exists(Paths.PendingKeyFile).Should().BeFalse("失敗後刪掉");
    }

    [Fact]
    public async Task commit丟例外_刪金鑰與pending_回報要到網頁撤銷()
    {
        string? keyName = null;
        string? publicKey = null;
        var deviceId = SyncIds.NewDeviceId();
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start()));
        http.RespondWith(HttpStatusCode.OK, () => SyncJson.Serialize(PairingTestData.Approval(deviceId, publicKey!)));
        var progress = new RecordingProgress(ui);

        var run = Run(progress, (_, _, _) => throw new IOException("磁碟已滿"), RealKeys((name, spki) =>
        {
            keyName = name;
            publicKey = spki;
        }));
        await time.WaitForTimersAsync();
        time.Advance(TimeSpan.FromSeconds(2));

        (await run.Awaiting(r => r.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingCodes.CommitFailed);
        DeviceKey.Exists(keyName!, PairingRules.KeyStorageSoftware).Should().BeFalse();
        File.Exists(Paths.PendingKeyFile).Should().BeFalse();
        var failure = progress.Failures.Should().ContainSingle().Subject;
        failure.Code.Should().Be(PairingCodes.CommitFailed);
        failure.Text.Should().Contain("網頁撤銷");
    }

    [Fact]
    public async Task device_json損毀時commit失敗_仍刪這次的金鑰與pending檔()
    {
        // 損毀的 device.json：PairingRecovery.Cleanup 分不出 pending 那把是不是它的、什麼都不刪；但 commit 沒完成，這把不可能是它的
        Directory.CreateDirectory(Paths.Directory);
        File.WriteAllText(Paths.DeviceFile, "{ 這不是 JSON");
        string? keyName = null;
        string? publicKey = null;
        var deviceId = SyncIds.NewDeviceId();
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start()));
        http.RespondWith(HttpStatusCode.OK, () => SyncJson.Serialize(PairingTestData.Approval(deviceId, publicKey!)));
        var progress = new RecordingProgress(ui);

        var run = Run(progress, (_, _, _) => throw new IOException("同步資料庫已存在"), RealKeys((name, spki) =>
        {
            keyName = name;
            publicKey = spki;
        }));
        await time.WaitForTimersAsync();
        time.Advance(TimeSpan.FromSeconds(2));

        (await run.Awaiting(r => r.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingCodes.CommitFailed);
        DeviceKey.Exists(keyName!, PairingRules.KeyStorageSoftware).Should().BeFalse("每次失敗的配對都留一把孤兒金鑰的話，照提示重試就一直累積");
        File.Exists(Paths.PendingKeyFile).Should().BeFalse();
        File.ReadAllText(Paths.DeviceFile).Should().Be("{ 這不是 JSON", "損毀的 device.json 不動");
    }

    [Fact]
    public async Task 失敗時pending檔記著別把_留著給下次清理()
    {
        // 之前當掉留下的 pending（記著別把）＋損毀的 device.json：這次失敗只刪自己那把，別把的紀錄留著
        Directory.CreateDirectory(Paths.Directory);
        File.WriteAllText(Paths.DeviceFile, "[]");
        var progress = new RecordingProgress(ui);
        string? keyName = null;
        var stale = new PendingKeyRecord(DeviceKey.NewKeyName(PairingTestData.TestKeyPrefix), PairingRules.KeyStorageSoftware, 1);
        http.RespondWith(HttpStatusCode.OK, () =>
        {
            // 建好金鑰之後、配對失敗之前，pending 檔被換成別把的紀錄（模擬另一次當掉留下的）
            PendingKeyRecord.Write(Paths.PendingKeyFile, stale);
            return SyncJson.Serialize(PairingTestData.Start());
        });
        http.Respond(HttpStatusCode.BadRequest, SyncJson.Serialize(new PairError(PairingErrors.AccessDenied)));

        var run = Run(progress, (_, _, d) => new PairingCommitResult(d), RealKeys((name, _) => keyName = name));
        await time.WaitForTimersAsync();
        time.Advance(TimeSpan.FromSeconds(2));

        (await run.Awaiting(r => r.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingErrors.AccessDenied);
        DeviceKey.Exists(keyName!, PairingRules.KeyStorageSoftware).Should().BeFalse();
        PendingKeyRecord.Read(Paths.PendingKeyFile).Record.Should().Be(stale);
    }

    [Fact]
    public async Task 裝置名稱不合格_立刻失敗_不建金鑰()
    {
        var progress = new RecordingProgress(ui);
        var created = false;

        var run = Run(progress, (_, _, d) => new PairingCommitResult(d), (_, _) =>
        {
            created = true;
            return new InMemoryDeviceSigner();
        }, name: " " + (char)7 + " ");

        (await run.Awaiting(r => r.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingCodes.InvalidDeviceName);
        created.Should().BeFalse();
        http.Requests.Should().BeEmpty();
        File.Exists(Paths.PendingKeyFile).Should().BeFalse();
    }

    /// <summary>記下進度回報與是否在 UI 執行緒上</summary>
    private sealed class RecordingProgress(UiThread ui) : IPairingProgress
    {
        private readonly object gate = new();

        public List<(PairStartResponse Response, string Fingerprint, long ExpiresAt)> Started { get; } = new();

        public List<(string Code, string Text)> Failures { get; } = new();

        public bool OnUiThread { get; private set; } = true;

        void IPairingProgress.Started(PairStartResponse response, string deviceFingerprint, long expiresAt)
        {
            lock (gate)
            {
                OnUiThread &= ui.IsCurrent;
                Started.Add((response, deviceFingerprint, expiresAt));
            }
        }

        void IPairingProgress.Failed(string code, string text)
        {
            lock (gate)
            {
                OnUiThread &= ui.IsCurrent;
                Failures.Add((code, text));
            }
        }
    }
}
