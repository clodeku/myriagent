using System.Diagnostics;
using System.Security.Cryptography;
using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.RemoteDesk;
using Myriagent.Tests.Sync.LongRun;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using LongRunGate = Myriagent.Tests.Sync.LongRun.LongRun;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// 遠端桌面的端到端（docs/sync-protocol.md 14.16）：兩台整條桌面（tools/fake-device 的 DeviceRuntime，接上假的畫面）配對到同一棵樹的 MyriagentHub 子程序。
/// A 的網頁送 deskOpen → 伺服器簽信封 → B 驗章、閘門、開始擷取 → 影格經 DeskUp／DeskDown 到 A 的視窗；A 的輸入與確認回到 B 的畫面；
/// A 結束時 B 停止。另量輸入的延遲與影格的往返（本機的伺服器，給效能參考）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RemoteDeskScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly FakeAccountSpec AccountA = LongRunDevice.Account("desk-a", AgentProvider.Claude, "desk-a@example.test", "org-desk-a");

    private static readonly FakeAccountSpec AccountB = LongRunDevice.Account("desk-b", AgentProvider.Claude, "desk-b@example.test", "org-desk-b");

    [EnvFact(LongRunGate.Gate, "遠端桌面的端到端：桌面 A 的網頁 → 同一棵樹的 MyriagentHub → 桌面 B 的畫面與輸入")]
    public async Task 桌面A看與操作桌面B的畫面()
    {
        var keysBefore = UserKeyStore.FilesCreatedByTestHub();
        var hub = await MyriagentHubProcess.StartAsync(publicOriginIsBaseAddress: true);
        try
        {
            await ScenarioAsync(hub);
        }
        finally
        {
            await hub.DisposeAsync();
        }

        hub.ForcedKill.Should().BeFalse("MyriagentHub 要能正常停止（強制結束會留下孤兒金鑰檔）");
        UserKeyStore.FilesCreatedByTestHub().Except(keysBefore).Should().BeEmpty("正常停止的 MyriagentHub 不留下金鑰檔");
    }

    private async Task ScenarioAsync(MyriagentHubProcess hub)
    {
        using var owner = await OwnerClient.LoginAsync(hub);
        var screenB = new FakeDeskScreen();
        await using var a = await LongRunDevice.PairAndStartAsync(hub, owner, "desk-a", [AccountA], HubEngine.FastBackoff(0.2, 0.5, 1), () => new FakeDeskScreen());
        await using var b = await LongRunDevice.PairAndStartAsync(hub, owner, "desk-b", [AccountB], HubEngine.FastBackoff(0.2, 0.5, 1), () => screenB);
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Desk1), Wait, "A 連上、協商出 desk1");
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Desk1), Wait, "B 連上、協商出 desk1");

        // B 允許遠端操作（預設 Off）
        var generation = await b.Inspect(v => v.ConnectionGeneration);
        await b.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control" });
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Desk1), Wait,
            "B 以新的政策重新連上");
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.Hub.PeerLive, Wait)).Should().BeTrue("A 能對其他電腦下指令");

        // 1. A 開遠端桌面：B 接下、第一個影格（整個畫面）到 A 的視窗
        var session = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var openId = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var started = Stopwatch.StartNew();
        await a.Runtime.CommandAsync("deskOpen", new JObject { ["deviceId"] = b.DeviceId, ["requestId"] = session, ["commandId"] = openId });
        (await a.Runtime.WaitUntilAsync(() => Frames(a, session).Any(), Wait)).Should().BeTrue("A 收到 B 的第一個影格");
        output.WriteLine($"開始到第一個影格：{started.ElapsedMilliseconds} ms");
        var first = await a.Runtime.OnUi(() => Frames(a, session).First());
        first["body"]!.Value<bool>("key").Should().BeTrue();
        first["body"]!["screens"]!.Should().HaveCount(2);
        (await b.Runtime.OnUi(() => b.Runtime.Hub.RemoteDeskActive)).Should().BeTrue();

        // 2. 確認、輸入：B 的畫面收到（量延遲）
        var latencies = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            var before = screenB.Injected.Count;
            var sw = Stopwatch.StartNew();
            var lastSeq = await a.Runtime.OnUi(() => Frames(a, session).Last().Value<long>("seq"));
            await a.Runtime.CommandAsync("desk", new JObject
            {
                ["sessionId"] = session, ["kind"] = DeskRules.Input, ["seq"] = i + 1,
                ["body"] = new JObject { ["ack"] = lastSeq, ["events"] = new JArray(new JObject { ["t"] = "kd", ["k"] = 65 + i }, new JObject { ["t"] = "ku", ["k"] = 65 + i }) },
            });
            while (screenB.Injected.Count == before && sw.ElapsedMilliseconds < 10_000)
                await Task.Delay(2);
            screenB.Injected.Count.Should().BeGreaterThan(before, "B 收到第 " + (i + 1) + " 包輸入");
            latencies.Add(sw.ElapsedMilliseconds);
        }

        output.WriteLine($"輸入到那台注入（經本機的伺服器）：中位數 {latencies.Order().ElementAt(latencies.Count / 2)} ms、最大 {latencies.Max()} ms");
        screenB.Injected.All.SelectMany(x => x.Events).Where(e => e.Type == "kd").Select(e => e.Key).Should().Equal(Enumerable.Range(65, 10));

        // 3. 畫面變了：確認之後新的影格只帶變動的區塊
        var framesBefore = await a.Runtime.OnUi(() => Frames(a, session).Count);
        screenB.Paint(new Remote.DeskRect(0, 0, 10, 10));
        (await a.Runtime.WaitUntilAsync(() => Frames(a, session).Skip(framesBefore).Any(f => f["body"]!["tiles"]!.Any()), Wait)).Should().BeTrue("變動的影格");
        var changed = await a.Runtime.OnUi(() => Frames(a, session).Skip(framesBefore).First(f => f["body"]!["tiles"]!.Any()));
        changed["body"]!["tiles"]!.Should().ContainSingle("只有左上那一格");

        // 4. A 結束：B 停止擷取
        await a.Runtime.CommandAsync("desk", new JObject { ["sessionId"] = session, ["kind"] = DeskRules.Close, ["seq"] = 99, ["body"] = new JObject() });
        (await b.Runtime.WaitUntilAsync(() => !b.Runtime.Hub.RemoteDeskActive, Wait)).Should().BeTrue("B 停止");
        screenB.Released.Should().Be(1);
    }

    [EnvFact(LongRunGate.Gate, "安全桌面的輸入的端到端：真的伺服器簽的 deskOpen 信封 → B 的同步留一份 → 鎖定畫面支援的幫手（測試程序裡的管道）驗章、解開、注入")]
    public async Task 安全桌面的輸入_伺服器簽的信封經B轉給幫手_A以服務的金鑰加密的輸入才注入()
    {
        var keysBefore = UserKeyStore.FilesCreatedByTestHub();
        var hub = await MyriagentHubProcess.StartAsync(publicOriginIsBaseAddress: true);
        try
        {
            await SecureScenarioAsync(hub);
        }
        finally
        {
            await hub.DisposeAsync();
        }

        hub.ForcedKill.Should().BeFalse("MyriagentHub 要能正常停止（強制結束會留下孤兒金鑰檔）");
        UserKeyStore.FilesCreatedByTestHub().Except(keysBefore).Should().BeEmpty("正常停止的 MyriagentHub 不留下金鑰檔");
    }

    private async Task SecureScenarioAsync(MyriagentHubProcess hub)
    {
        using var owner = await OwnerClient.LoginAsync(hub);

        // B 的鎖定畫面支援：服務與幫手的管道都在測試程序裡（一般權限、假的擷取與注入），幫手信任的伺服器是 B 配對之後的那一組（真的安裝是安裝器記下的）
        DeskServiceTrust? installedTrust = null;
        var injector = new DeskSecureInputTests.FakeInjector();
        var (helper, helperPipe) = await DeskSecureInputTests.StartHelperAsync(injector, () => installedTrust);
        using var disposeHelper = helper;
        var servicePipe = "desk-test-" + Guid.NewGuid().ToString("N")[..12];
        using var service = await DeskSecureInputTests.StartServiceAsync(servicePipe, helperPipe);
        // B 平常的擷取說鎖定中：畫面改由幫手給
        var plainB = new FakeDeskScreen { Locked = true };

        await using var a = await LongRunDevice.PairAndStartAsync(hub, owner, "desk-a", [AccountA], HubEngine.FastBackoff(0.2, 0.5, 1), () => new FakeDeskScreen());
        await using var b = await LongRunDevice.PairAndStartAsync(hub, owner, "desk-b", [AccountB], HubEngine.FastBackoff(0.2, 0.5, 1),
            () => new Myriagent.RemoteDesk.SecureDeskScreen(plainB, new Myriagent.RemoteDesk.SecureDeskCapture(() => true, servicePipe)));
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.DeskE2e1), Wait, "A 連上、協商出 deske2e1");
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.DeskE2e1), Wait, "B 連上、協商出 deske2e1");
        var generation = await b.Inspect(v => v.ConnectionGeneration);
        await b.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control" });
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Desk1), Wait,
            "B 以新的政策重新連上");
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.Hub.PeerLive, Wait)).Should().BeTrue("A 能對其他電腦下指令");

        // 「安裝」：B 現在信任的伺服器（釘選的身分金鑰、配對的來源、B 的裝置 id）
        (await b.Runtime.WaitUntilAsync(() => b.Runtime.Sync.DeskTrust is not null, Wait)).Should().BeTrue("B 配對之後有可以交給安裝器的信任資料");
        installedTrust = b.Runtime.Sync.DeskTrust;
        installedTrust!.DeviceId.Should().Be(b.DeviceId);

        // A（扮演看的一方的網頁）：這個工作階段的金鑰，公鑰放進 deskOpen
        using var viewer = DeskSealRules.CreateKey();
        var viewerPublic = DeskSealRules.ExportPublicKey(viewer);
        var session = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await a.Runtime.CommandAsync("deskOpen", new JObject
        {
            ["deviceId"] = b.DeviceId, ["requestId"] = session, ["commandId"] = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            [DeskSealRules.ViewerKeyField] = viewerPublic,
        });

        // B：鎖定中 → 幫手給畫面 → 幫手驗伺服器簽的信封 → status 帶 secure 與服務的公鑰，到 A
        JObject? SecureStatus() => a.Runtime.View.DeskMessages.LastOrDefault(m =>
            m.Value<string>("sessionId") == session && m.Value<string>("kind") == DeskRules.Status && DeskSealRules.ServicePublic(m["body"] as JObject) is not null);
        (await a.Runtime.WaitUntilAsync(() => SecureStatus() is not null, Wait)).Should().BeTrue("A 收到服務的公鑰（幫手驗過了真的伺服器簽的信封）");
        var status = (await a.Runtime.OnUi(SecureStatus))!;
        status["body"]!.Value<bool>("secure").Should().BeTrue();
        var servicePublic = DeskSealRules.ServicePublic(status["body"] as JObject)!;
        (await a.Runtime.WaitUntilAsync(() => Frames(a, session).Any(), Wait)).Should().BeTrue("鎖定時照樣有畫面（幫手擷取的）");

        // A 以服務的金鑰加密的輸入：經伺服器、B 的 app（解不開，原樣轉）到幫手，解開、注入
        var key = DeskSealRules.DeriveKey(viewer, servicePublic, session, viewerPublic, servicePublic, DeskSealRules.SecureInfo)!;
        async Task SendAsync(long seq, JObject body)
            => await a.Runtime.CommandAsync("desk", new JObject { ["sessionId"] = session, ["kind"] = DeskRules.Input, ["seq"] = seq, ["body"] = body });
        string Seal(long n, string label)
            => DeskSealRules.Seal(key, session, n, System.Text.Encoding.UTF8.GetBytes(new JObject
            {
                ["events"] = new JArray(new JObject { ["t"] = "kd", ["k"] = 64 + n }),
            }.ToString(Newtonsoft.Json.Formatting.None)), label);
        JObject ForService(long n, string label) => new() { ["e2e"] = new JObject { ["n"] = n, ["ct"] = Seal(n, label), ["k"] = DeskSealRules.ServiceKeyTag } };

        await SendAsync(1, ForService(1, DeskSealRules.SecureInfo));
        (await a.Runtime.WaitUntilAsync(() => injector.Injected.Count == 1, Wait)).Should().BeTrue("幫手注入了第 1 包");
        injector.Injected[0].Events.Single().Should().Be(new DeskInputEvent("kd", Key: 65));

        // 明文的、以 app 的標籤加密卻標成給服務的：都進不了安全桌面；之後正確的那一包照收
        await SendAsync(2, new JObject { ["events"] = new JArray(new JObject { ["t"] = "kd", ["k"] = 90 }) });
        await SendAsync(3, ForService(2, DeskSealRules.Info));
        await SendAsync(4, ForService(3, DeskSealRules.SecureInfo));
        (await a.Runtime.WaitUntilAsync(() => injector.Injected.Count == 2, Wait)).Should().BeTrue("之後正確的那一包照收");
        injector.Injected[1].Events.Single().Key.Should().Be(67);
        injector.Injected.Should().HaveCount(2);
        plainB.Injected.All.Should().BeEmpty("安全桌面時 B 的 app 自己不注入");

        await a.Runtime.CommandAsync("desk", new JObject { ["sessionId"] = session, ["kind"] = DeskRules.Close, ["seq"] = 99, ["body"] = new JObject() });
        (await b.Runtime.WaitUntilAsync(() => !b.Runtime.Hub.RemoteDeskActive, Wait)).Should().BeTrue("B 停止");
    }

    private static List<JObject> Frames(LongRunDevice device, string session)
        => device.Runtime.View.DeskMessages.Where(m => m.Value<string>("sessionId") == session && m.Value<string>("kind") == DeskRules.Frame).ToList();
}
