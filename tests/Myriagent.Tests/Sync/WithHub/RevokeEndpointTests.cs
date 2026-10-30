using System.Net;
using System.Net.Http.Headers;
using Myriagent.Core;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// SEC-13 端到端（撤銷裝置 ≤1 秒斷線、之後 401）：真的 MyriagentHub 子程序、正式的桌面引擎（SignalR 傳輸、正式的退避設定、CNG 軟體金鑰），
/// 配對到 Live 之後由擁有者照網頁的做法撤銷（密碼登入的 cookie＋Origin＋X-Myriagent-Csrf、<c>POST /api/v1/devices/{id}/revoke</c>，
/// 要近期驗證就再驗證）。伺服器在 hub.db 提交後、回應之前就作廢存取權杖並中斷那台的連線（PairingService.RevokeAsync），
/// 引擎把斷線當成暫時的失敗先退避，重連時 WebSocket 升級 401 才停止（Unauthorized）。
/// 門檻量的是「收到撤銷回應」到「引擎發布離開 Live 的狀態」（伺服器在回應前就斷線時是負數）。
/// 伺服器停機時改資料庫的離線撤銷在 LongRun 的 <see cref="LongRun.RevokeTests"/>；MyriagentHub 單一程序內的量測在 MyriagentHub.Tests 的 BrowserEndpointsTests
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RevokeEndpointTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>SEC-13：收到撤銷回應之後 1 秒內斷線</summary>
    private static readonly TimeSpan DisconnectLimit = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task 撤銷端點_引擎1秒內離開Live_之後401_Stopped()
    {
        var hubKeysBefore = UserKeyStore.FilesCreatedByTestHub();
        string? keyName = null;
        try
        {
            await using (var hub = await MyriagentHubProcess.StartAsync())
            {
                await RevokeLiveDeviceAsync(hub, name => keyName = name);
                await hub.StopAsync();
                hub.ForcedKill.Should().BeFalse("MyriagentHub 要能正常停止（強制結束會留下孤兒金鑰檔）");
            }

            UserKeyStore.FilesCreatedByTestHub().Except(hubKeysBefore).Should().BeEmpty("正常停止的 MyriagentHub 不留下 TLS 私鑰檔（D5）");
        }
        finally
        {
            if (keyName is not null)
                DeviceKey.Delete(keyName, PairingRules.KeyStorageSoftware);
        }

        keyName.Should().NotBeNull("配對建了裝置金鑰");
        DeviceKey.Exists(keyName!, PairingRules.KeyStorageSoftware).Should().BeFalse();
        UserKeyStore.FilesNaming(keyName!).Should().BeEmpty("裝置金鑰刪除後不留孤兒金鑰檔");
    }

    private async Task RevokeLiveDeviceAsync(MyriagentHubProcess hub, Action<string> keyCreated)
    {
        using var dir = new DeviceDir();
        using var owner = await OwnerClient.LoginAsync(hub);
        var paired = await DevicePairing.PairAsync(hub, owner, dir.Paths, (name, tryTpm) =>
        {
            keyCreated(name);
            return DeviceKey.Create(name, tryTpm, Log.Info);
        });
        paired.Device.KeyStorage.Should().Be(PairingRules.KeyStorageSoftware, "測試不試 TPM");
        UserKeyStore.FilesNaming(paired.Device.KeyName).Should().ContainSingle("CNG 軟體金鑰存成一個檔案（陽性對照：最後的孤兒檢查認得出它）");

        await using var engine = new HubEngine(paired);
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);
        var device = engine.SavedDevice();
        var token = DeviceSecrets.Unprotect(device.ProtectedDeviceToken) ?? throw new InvalidOperationException("解不開裝置權杖");
        SyncSecrets.Remember(token);
        var server = new Uri(device.ServerUrl);
        using var bearer = BearerClient(hub, token);
        (await NegotiateAsync(bearer)).Should().Be(HttpStatusCode.OK, "正向對照：撤銷前這個權杖可以升級");

        var mark = engine.Host.Statuses.Count;
        var generation = await engine.Inspect(v => v.ConnectionGeneration);
        var backoffs = engine.Current.BackoffHistory.Count;
        (await engine.StateAsync()).Should().Be(SyncEngineState.Live, "撤銷前還在 Live");

        var timing = await owner.RevokeDeviceAsync(paired.DeviceId, DeviceRegistryRules.RevokedCompromised);
        await engine.WaitUntilAsync(v => v.State == SyncEngineState.Stopped, Wait, "撤銷後停止");

        var after = engine.Host.Statuses.Skip(mark).ToList();
        var left = after.FirstOrDefault(s => s.Status.State != SyncEngineState.Live);
        var stopped = after.FirstOrDefault(s => s.Status.State == SyncEngineState.Stopped);
        left.Status.Should().NotBeNull("宿主收到離開 Live 的狀態");
        stopped.Status.Should().NotBeNull("宿主收到停止的狀態");
        var toLeft = left.At - timing.AnsweredAt;
        var toStopped = stopped.At - timing.AnsweredAt;
        var states = string.Join(" → ", after.Select(s => s.Status.State).Where((s, i) => i == 0 || s != after[i - 1].Status.State));
        output.WriteLine(
            $"SEC-13：撤銷請求→回應 {timing.RoundTrip.TotalMilliseconds:0} ms；回應→離開 Live {toLeft.TotalMilliseconds:0} ms"
            + $"（請求送出→離開 Live {(left.At - timing.SentAt).TotalMilliseconds:0} ms）；回應→Stopped {toStopped.TotalMilliseconds:0} ms；"
            + $"狀態 {states}；再驗證 {(timing.SteppedUp ? "有" : "沒有")}");

        left.At.Should().BeOnOrAfter(timing.SentAt, "撤銷請求送出之後才離開 Live（是撤銷造成的）");
        toLeft.Should().BeLessThanOrEqualTo(DisconnectLimit, "SEC-13：收到撤銷回應之後 1 秒內斷線");
        left.Status.State.Should().Be(SyncEngineState.Backoff, "伺服器中斷連線，引擎先當成暫時的失敗退避");
        (await engine.Inspect(v => v.StopReason)).Should().Be(SyncStopReason.Unauthorized, "重連時 WebSocket 升級 401");
        engine.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.Unauthorized, "宿主收到停止的原因（網頁顯示要重新配對）");
        var generationAfter = await engine.Inspect(v => v.ConnectionGeneration);
        (generationAfter, engine.Current.BackoffHistory.Count).Should().Be((generation + 1, backoffs + 1), "斷線後只退避、重連一次就停止，不再重試");

        // 之後同一個權杖：negotiate 與 WebSocket 升級（Hello 之前）都 401
        (await NegotiateAsync(bearer)).Should().Be(HttpStatusCode.Unauthorized, "同一個權杖的 negotiate 401");
        using (var timeout = new CancellationTokenSource(Wait))
        {
            var connect = () => new SignalRSyncTransport().ConnectAsync(new SyncConnectRequest(server, server, token, DevTrustEnv: true), new RecordingSessionEvents(), timeout.Token);
            (await connect.Should().ThrowAsync<SyncTransportException>()).Which.Failure.Kind.Should().Be(SyncFailureKind.Unauthorized, "同一個權杖的 WebSocket 升級 401");
        }

        new HubDb(hub.DataDir).Audit("device.revoked").Should().ContainSingle(a => a.Target == paired.DeviceId && a.ActorType == "owner" && a.Outcome == "ok"
            && (a.Detail ?? "").Contains(DeviceRegistryRules.RevokedCompromised, StringComparison.Ordinal), "撤銷記下擁有者的稽核與原因");
    }

    private static HttpClient BearerClient(MyriagentHubProcess hub, string token)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        {
            BaseAddress = hub.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>以裝置權杖 negotiate（不帶 Origin：裝置端點拒收瀏覽器的請求）；回傳 HTTP 狀態</summary>
    private static async Task<HttpStatusCode> NegotiateAsync(HttpClient bearer)
    {
        using var response = await bearer.PostAsync(SyncProtocol.DeviceHubPath + "/negotiate?negotiateVersion=1", null);
        return response.StatusCode;
    }
}
