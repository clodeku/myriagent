using System.Diagnostics;
using System.Text.RegularExpressions;
using Myriagent.Core;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// 第 9 步：對 MyriagentHub 子程序的情境（只用正常停止）：伺服器身分輪換與遺失（SEC-10）、權杖輪換、積壓補送（S5 的快速版）。
/// 同一個類別共用一個 MyriagentHub（每個測試配對自己的裝置，需要時停止、改資料、再以同一個資料根與埠啟動）；
/// 最後一個測試掃描前面這些測試寫下的記錄與 device.json
/// </summary>
[Collection(MyriagentHubCollection.Name)]
[TestCaseOrderer(PriorityOrderer.TypeName, PriorityOrderer.AssemblyName)]
public sealed partial class HubScenarioTests(MyriagentHubFixture fixture, ITestOutputHelper output) : IClassFixture<MyriagentHubFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>這個類別開始前各記錄檔的長度（權杖樣式的掃描只看之後寫的）</summary>
    private static readonly IReadOnlyDictionary<string, long> LogMark = LogScan.Mark();

    private MyriagentHubProcess Hub => fixture.Hub;

    private OwnerClient Owner => fixture.Owner;

    private HubDb Db => fixture.Db;

    /// <summary>SEC-10 的輪換：舊金鑰簽了輪換聲明，引擎下一次連線自動改釘，不必使用者確認</summary>
    [Fact]
    [TestPriority(1)]
    public async Task admin_rotate_hub_identity_自動改釘_Live()
    {
        await EnsureHubAsync();
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);
        await using var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1));
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);
        var oldSpki = engine.SavedDevice().HubIdentitySpki;

        var (code, text, error) = await Hub.RunAdminAsync("rotate-hub-identity", "--wait", "20");
        code.Should().Be(0, $"輪換要成功：{text}{error}");
        var newSpki = JObject.Parse(text).SelectToken("$..newSpki")?.Value<string>();
        newSpki.Should().NotBeNullOrEmpty().And.NotBe(oldSpki);

        // 伺服器輪換後不中斷現有連線（下次 Hello 才帶聲明）：正常重啟伺服器，引擎自己退避重連
        await Hub.RestartAsync();
        await engine.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration >= 2, Wait, "重連後回到 Live");

        engine.SavedDevice().HubIdentitySpki.Should().Be(newSpki, "Hello 帶了舊金鑰簽的輪換聲明，引擎改釘新的公鑰並存進 device.json");
        engine.SavedDevice().HubFingerprint.Should().Be(Hub.ReadTrustFingerprint());
        engine.Host.Statuses.Select(s => s.Status.State).Should().NotContain(SyncEngineState.NeedsConfirmation, "有可信的聲明就不要使用者確認");
    }

    /// <summary>SEC-10 的本機確認：伺服器身分金鑰遺失（重建）、沒有輪換聲明——等使用者以伺服器主控台的指紋確認</summary>
    [Fact]
    [TestPriority(2)]
    public async Task 伺服器身分遺失_NeedsConfirmation_用trust_json的指紋確認後Live()
    {
        await EnsureHubAsync();
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);
        await using var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1));
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);
        var oldFingerprint = Hub.ReadTrustFingerprint();

        await Hub.StopAsync();
        File.Exists(Hub.IdentityKeyFile).Should().BeTrue("刪的是伺服器身分金鑰檔（陽性對照）");
        File.Delete(Hub.IdentityKeyFile);
        await Hub.LaunchAsync();
        await engine.WaitForStateAsync(SyncEngineState.NeedsConfirmation, Wait);
        await engine.WaitUntilAsync(_ => engine.Host.LastStatus is { State: SyncEngineState.NeedsConfirmation, ConfirmFingerprint: not null }, Wait, "待確認的指紋送到宿主");

        var fingerprint = Hub.ReadTrustFingerprint();
        fingerprint.Should().NotBe(oldFingerprint, "金鑰檔不在時伺服器產生新的身分");
        engine.Host.LastStatus!.ConfirmFingerprint.Should().Be(fingerprint, "網頁顯示的待確認指紋就是伺服器主控台（admin status、trust.json）的指紋");
        var generation = await engine.Inspect(v => v.ConnectionGeneration);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        (await engine.Inspect(v => (v.State, v.ConnectionGeneration))).Should().Be((SyncEngineState.NeedsConfirmation, generation), "確認前不重連（退避步階 0.2～1 秒）");
        engine.SavedDevice().HubFingerprint.Should().Be(oldFingerprint, "確認前不改釘");

        engine.Current.ConfirmHubIdentity(fingerprint.ToLowerInvariant());
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);

        engine.SavedDevice().HubFingerprint.Should().Be(fingerprint, "確認後改釘（大小寫不拘）");
    }

    [Fact]
    [TestPriority(3)]
    public async Task 權杖輪換_換存新chd_重連用新的()
    {
        await EnsureHubAsync();
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);
        var oldToken = DeviceSecrets.Unprotect(paired.Device.ProtectedDeviceToken);
        SyncSecrets.Remember(oldToken);
        await using var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1));
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);

        await Hub.StopAsync();
        Db.SetTokenIssuedAt(paired.DeviceId, DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds());
        await Hub.LaunchAsync();
        await engine.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.DeviceToken != oldToken, Wait, "Prove 帶回新權杖、回到 Live");

        var newToken = DeviceSecrets.Unprotect(engine.SavedDevice().ProtectedDeviceToken);
        SyncSecrets.Remember(newToken);
        newToken.Should().StartWith(SyncProtocol.DeviceTokenPrefix).And.NotBe(oldToken, "新權杖存進 device.json（DPAPI）");
        (await engine.Inspect(v => v.DeviceToken)).Should().Be(newToken);
        Db.Audit("device.token_rotated").Where(a => a.Target == paired.DeviceId || a.ActorId == paired.DeviceId).Should().ContainSingle();

        // 下一次連線（app 重新啟動：新的引擎從 device.json 讀權杖）用新的：伺服器不會因為收到舊權杖再發一次
        await engine.RestartAsync();
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);
        (await engine.Inspect(v => v.DeviceToken)).Should().Be(newToken);
        Db.Audit("device.token_rotated").Where(a => a.Target == paired.DeviceId || a.ActorId == paired.DeviceId).Should().ContainSingle("用新權杖連線不再輪換（用舊的會再發一次）");
    }

    /// <summary>S5 的快速版：伺服器停機時積壓 1,000 筆，恢復後不呼叫 RetryNow，靠退避（0.2, 0.5, 1, 2 秒）自己連上並在 30 秒內補完</summary>
    [Fact]
    [TestPriority(4)]
    public async Task 積壓1000筆_注入退避_恢復後30秒內補完_0重複()
    {
        const int Count = 1000;
        await EnsureHubAsync();
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);
        await using var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1, 2));
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);

        await Hub.StopAsync();
        await engine.WaitForStateAsync(SyncEngineState.Backoff, Wait);
        Backlog.Append(engine.Current, 1, Count);
        await engine.WaitUntilAsync(v => v.Outbox.Count == Count, Wait, "1,000 筆都進了 outbox");
        Db.DeviceEvents(paired.DeviceId).Should().BeEmpty("停機期間一筆都送不出去");

        await Hub.LaunchAsync();
        var elapsed = await Backlog.WaitDrainedAsync(Db, engine, paired.DeviceId, Count, Wait);
        var sinceLaunch = Stopwatch.GetElapsedTime(Hub.LaunchedAt);
        output.WriteLine($"補完 {Count} 筆：從 /healthz 就緒起 {elapsed.TotalSeconds:0.0} 秒、從啟動子程序起 {sinceLaunch.TotalSeconds:0.0} 秒");

        sinceLaunch.Should().BeLessThan(TimeSpan.FromSeconds(30), "恢復後 30 秒內補完（從啟動子程序起算，比從 /healthz 就緒起算保守）");
        await Backlog.AssertExactlyOnceAsync(Db, engine, paired.DeviceId, Count);
    }

    /// <summary>
    /// D16：權杖（chd_ 裝置權杖、cha_ 存取權杖）不進記錄、device.json 只有 DPAPI 密文。最後才跑：掃前面這些測試（與同一個程序裡
    /// 其他用 MyriagentHub 的測試）寫下的記錄，以及它們的 device.json（刪暫存資料夾前記下的）
    /// </summary>
    [Fact]
    [TestPriority(100)]
    public async Task 記錄與device_json不含chd_cha明文()
    {
        await EnsureHubAsync();
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(Hub, Owner, dir.Paths);
        SyncSecrets.Remember(DeviceSecrets.Unprotect(paired.Device.ProtectedDeviceToken));
        await using (var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1)))
        {
            engine.Start();
            await engine.WaitForStateAsync(SyncEngineState.Live, Wait);
            // 斷線再連上：引擎記下「同步連線中斷」（陽性對照：同步的記錄確實寫進掃描的檔案）
            await Hub.RestartAsync();
            await engine.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration >= 2, Wait, "重連後回到 Live");
        }

        SyncSecrets.RememberDeviceFile(dir.Paths.DeviceFile);
        Log.Flush();
        var known = SyncSecrets.Known;
        known.Should().NotBeEmpty();
        var logFiles = LogScan.Files();
        logFiles.Should().NotBeEmpty();
        var recent = LogScan.Since(LogMark);
        recent.Should().Contain("同步連線中斷", "陽性對照");
        TokenPattern().Matches(recent).Select(m => m.Value[..8] + "…").Should().BeEmpty("這個類別開始後的記錄沒有任何像權杖的字串");
        foreach (var file in logFiles)
        {
            var all = LogScan.ReadShared(file, 0);
            known.Where(t => all.Contains(t, StringComparison.Ordinal)).Should().BeEmpty($"{Path.GetFileName(file)} 不含測試用過的權杖");
        }

        var deviceFiles = SyncSecrets.RetiredDeviceFiles;
        deviceFiles.Should().HaveCountGreaterThanOrEqualTo(1);
        foreach (var (path, text) in deviceFiles)
        {
            text.Should().NotContain(SyncProtocol.DeviceTokenPrefix, $"{path} 的權杖只存 DPAPI 密文").And.NotContain(SyncProtocol.AccessTokenPrefix);
            known.Where(t => text.Contains(t, StringComparison.Ordinal)).Should().BeEmpty();
        }

        output.WriteLine($"掃描：記錄檔 {logFiles.Length} 個、device.json {deviceFiles.Count} 份、已知權杖 {known.Count} 個");
    }

    [GeneratedRegex("ch[da]_[A-Za-z0-9_-]{16,}")]
    private static partial Regex TokenPattern();

    /// <summary>前一個測試失敗時伺服器可能停在停止狀態：先確認在執行</summary>
    private async Task EnsureHubAsync()
    {
        if (!Hub.IsRunning)
            await Hub.LaunchAsync();
        _ = LogMark;
    }
}
