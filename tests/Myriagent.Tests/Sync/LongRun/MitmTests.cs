using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// SEC-10 的中間人：裝置經 TLS 終止代理（另一張自簽憑證）連線，開發信任接受了代理的憑證，但 Hello 帶的是代理的 SPKI——
/// 伺服器稽核 spki_mismatch 並中斷，裝置沒有走到 Prove（伺服器先 Abort 才丟例外，不依賴一定收到 not_proven），停在退避
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class MitmTests(ITestOutputHelper output)
{
    private const string Reason = "對 MyriagentHub 子程序的長情境：TLS 終止代理（中間人）";

    [EnvFact(LongRun.Gate, Reason)]
    public async Task TLS代理換憑證()
    {
        await using var hub = await MyriagentHubProcess.StartAsync();
        var db = new HubDb(hub.DataDir);
        using var dir = new DeviceDir();
        PairedDevice paired;
        using (var owner = await OwnerClient.LoginAsync(hub))
            paired = await DevicePairing.PairAsync(hub, owner, dir.Paths);
        await using var proxy = TlsInterceptProxy.Start(hub.Port);
        var hubSpki = await hub.CertificateSpkiAsync();
        proxy.Spki.Should().NotBe(hubSpki, "代理用的是另一張憑證");

        await using var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1), serverOverride: proxy.BaseAddress);
        engine.Start();
        await WaitForAsync(() => db.Audit("device.hello").Any(a => IsMismatch(a, paired.DeviceId)), "伺服器稽核 spki_mismatch");
        await engine.WaitUntilAsync(v => v.State == SyncEngineState.Backoff && v.ConnectionGeneration >= 2, LongRun.Wait, "中斷後退避、又試了一次");

        var hello = db.Audit("device.hello").Where(a => a.ActorId == paired.DeviceId || a.Target == paired.DeviceId).ToList();
        hello.Should().Contain(a => IsMismatch(a, paired.DeviceId));
        hello.Should().NotContain(a => a.Outcome == "ok", "沒有任何一次 Prove 成功");
        hello.Should().NotContain(a => a.Detail != null && a.Detail.Contains("bad_device_signature", StringComparison.Ordinal), "沒有呼叫 Prove（伺服器在 Hello 就中斷）");
        engine.Host.Statuses.Select(s => s.Status.State).Should().NotContain([SyncEngineState.Draining, SyncEngineState.Live], "交握沒有完成過");
        (await engine.StateAsync()).Should().Be(SyncEngineState.Backoff);
        db.DeviceEvents(paired.DeviceId).Should().BeEmpty();
        proxy.Connections.Should().BeGreaterThan(0, "裝置確實經過代理（陽性對照）");
        output.WriteLine($"代理轉送 {proxy.Connections} 條連線；稽核 spki_mismatch {hello.Count(a => IsMismatch(a, paired.DeviceId))} 筆；狀態 {string.Join(" → ", engine.Host.Statuses.Select(s => s.Status.State).Distinct())}");
    }

    private static bool IsMismatch(HubAudit audit, string deviceId) =>
        (audit.ActorId == deviceId || audit.Target == deviceId) && audit.Outcome == "denied" && audit.Detail is { } detail && detail.Contains("spki_mismatch", StringComparison.Ordinal);

    private static async Task WaitForAsync(Func<bool> probe, string because)
    {
        var deadline = DateTime.UtcNow + LongRun.Wait;
        while (!probe())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"{LongRun.Wait.TotalSeconds:0} 秒內沒有等到「{because}」");
            await Task.Delay(100);
        }
    }
}
