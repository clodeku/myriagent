using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.LongRun;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using LongRunGate = Myriagent.Tests.Sync.LongRun.LongRun;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// 執行中換模型的端到端（瀏覽器版與桌面遠端操作）：桌面 A 的對話停在等核准（這一輪還在進行），
/// 瀏覽器 hub 的用戶端與桌面 B（副本）各經同一棵樹的 MyriagentHub 子程序送 setOption → A 的引擎與中樞閘門 → AgentHub.SetOption →
/// 會話的 SetModelAsync 在這一輪進行中就收到（劇本化的會話記下當時在執行）→ 結果回發起者、A 發布新的 meta 之後 B 的副本標頭跟著變。
/// 伺服器的 <c>Hub:PublicOrigin</c> 是配對網址本身（同 RemoteCommandScenarioTests）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RunningModelSwitchScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly FakeAccountSpec AccountA = LongRunDevice.Account("run-model-a", AgentProvider.Claude, "run-model-a@example.test", "org-run-model-a");

    private static readonly FakeAccountSpec AccountB = LongRunDevice.Account("run-model-b", AgentProvider.Claude, "run-model-b@example.test", "org-run-model-b");

    [EnvFact(LongRunGate.Gate, "執行中換模型的端到端：瀏覽器與桌面 B → 同一棵樹的 MyriagentHub → 桌面 A 這一輪還在進行的會話")]
    public async Task 這一輪還在進行_瀏覽器與桌面B換模型和推理強度_A的會話當下就收到()
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
        await using var a = await LongRunDevice.PairAndStartAsync(hub, owner, "run-model-a", [AccountA], HubEngine.FastBackoff(0.2, 0.5, 1));
        await using var b = await LongRunDevice.PairAndStartAsync(hub, owner, "run-model-b", [AccountB], HubEngine.FastBackoff(0.2, 0.5, 1));
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Peer1) && v.Features.Contains(DeviceFeatureRules.Cmd1), Wait,
            "A 連上、協商出 cmd1 與 peer1");
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Peer1), Wait, "B 連上、協商出 peer1");

        // A 允許遠端操作（預設 Off）
        var generation = await a.Inspect(v => v.ConnectionGeneration);
        await a.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control" });
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Peer1), Wait,
            "A 以新的政策重新連上");

        // A 這個帳戶的模型清單（假裝置的會話不回報：直接放進設定）：haiku-a 不支援推理強度
        var accountA = await a.Runtime.FindAccountAsync(AccountA.Alias) ?? throw new InvalidOperationException("A 的帳戶不見了");
        await a.Runtime.OnUi(() => a.Runtime.Settings.Models[accountA.Id] =
            [new AgentModelOption("opus-a", "A 的 Opus", null, ["low", "high"]), new AgentModelOption("sonnet-a", "A 的 Sonnet", null, ["low", "high"]),
             new AgentModelOption("haiku-a", "A 的 Haiku", null, [])]);

        // A 的對話這一輪停在等核准（劇本沒有替使用者回應）：之後的切換都在這一輪進行中
        var turn = new ScriptedTurn([new ScriptedApproval("執行指令", "bash", "npm test", null, null, 0), new ScriptedText("核准之後的回覆", 1, 0)]);
        var chat = await a.Runtime.ChatAsync(new ChatSpec("執行中換模型", AccountA.Alias, "跑測試", [], turn, Wait));
        chat.Waiting.Should().BeTrue("這一輪停在等核准、還沒結束");
        var wireId = ConversationIds.ToWire(chat.ConversationId) ?? throw new InvalidOperationException("對話 id 不是線上 id 的形狀");
        (await a.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue("A 的對話事件送完");

        // 1. 瀏覽器版：換模型、再換推理強度
        await using var browser = await BrowserHubClient.ConnectAsync(hub.BaseAddress, owner.Cookies, hub.PublicOrigin);
        var init = await browser.ReadyAsync(Wait);
        if (!Lists(init, wireId))
            await browser.WaitForAsync(m => m.Value<string>("type") == "conversations" && Lists(m, wireId), Wait, "對話清單有這個對話、擁有者在線");

        var switches0 = SwitchesWhileRunning(a);
        var byBrowser = await browser.RemoteAsync(SetOption(wireId, "model", "sonnet-a"), Wait);
        output.WriteLine("瀏覽器換模型：" + byBrowser.ToString(Newtonsoft.Json.Formatting.None));
        byBrowser.Value<string>("state").Should().Be(RemoteResultStates.Done);
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.SessionCalls.LastModelSwitch is { Model: "sonnet-a", Running: true }, Wait))
            .Should().BeTrue($"A 的會話在這一輪進行中就收到新的模型：{a.Runtime.SessionCalls.LastModelSwitch}");

        var byBrowserEffort = await browser.RemoteAsync(SetOption(wireId, "effort", "low"), Wait);
        byBrowserEffort.Value<string>("state").Should().Be(RemoteResultStates.Done);
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.SessionCalls.LastModelSwitch is { Model: "sonnet-a", Effort: "low", Running: true }, Wait))
            .Should().BeTrue($"推理強度也在這一輪進行中套用：{a.Runtime.SessionCalls.LastModelSwitch}");

        // 2. 桌面遠端操作：B 在副本換成不支援推理強度的模型（A 把強度清回預設）
        (await b.Runtime.WaitUntilAsync(() => b.Runtime.Hub.IsReplicaConversation(wireId) && b.Runtime.Hub.PeerLive, Wait))
            .Should().BeTrue("B 有 A 的對話的副本，而且能對其他電腦下指令");
        await b.Runtime.CommandAsync("open", new JObject { ["conversationId"] = wireId });
        (await b.Runtime.WaitUntilAsync(() => ReplicaHeader(b, wireId)?.Value<string>("model") == "sonnet-a", Wait))
            .Should().BeTrue("A 發布新的 meta 之後，B 的副本標頭是瀏覽器換的模型");
        var peerId = NewCommandId();
        await b.Runtime.CommandAsync("setOption", new JObject { ["conversationId"] = wireId, ["key"] = "model", ["value"] = "haiku-a", ["commandId"] = peerId });
        (await b.Runtime.WaitUntilAsync(() => Result(b, peerId) is { } r && r.Value<string>("state") == RemoteResultStates.Done, Wait))
            .Should().BeTrue("結果回到 B 的視窗");
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.SessionCalls.LastModelSwitch is { Model: "haiku-a", Effort: null, Running: true }, Wait))
            .Should().BeTrue($"A 的會話在這一輪進行中就收到 B 換的模型、強度清回預設：{a.Runtime.SessionCalls.LastModelSwitch}");
        (await b.Runtime.WaitUntilAsync(() => ReplicaHeader(b, wireId) is { } h && h.Value<string>("model") == "haiku-a" && h.Value<string>("effort") == "default", Wait))
            .Should().BeTrue("B 開著的副本標頭跟著變");
        (SwitchesWhileRunning(a) - switches0).Should().Be(3, "三次切換都在這一輪進行中送到會話，各一次");

        // 收尾：A 本機核准，這一輪照常結束
        var card = await a.Runtime.OnUi(() => a.Runtime.Hub.GetItems(chat.ConversationId).Last(i => i.Kind == AgentItemKind.Approval && i.Status == AgentItemStatus.Waiting));
        await a.Runtime.CommandAsync("approve", new JObject { ["conversationId"] = chat.ConversationId, ["itemId"] = card.Id, ["choice"] = "allow" });
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.Hub.GetItems(chat.ConversationId).Any(i => i.Kind == AgentItemKind.Assistant && i.Text == "核准之後的回覆"), Wait))
            .Should().BeTrue("核准之後這一輪照劇本接著做完");

        await b.StopAsync();
        await a.StopAsync();
    }

    private static long SwitchesWhileRunning(LongRunDevice device) => device.Runtime.SessionCalls.ToJson().Value<long>("modelSwitchesWhileRunning");

    private static JObject SetOption(string conversationId, string key, string value) =>
        new() { ["cmd"] = "setOption", ["conversationId"] = conversationId, ["key"] = key, ["value"] = value };

    private static string NewCommandId() => SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>B 的視窗收到的這個指令的 commandResult（還沒有回 null；在 UI 執行緒呼叫）</summary>
    private static JObject? Result(LongRunDevice device, string commandId) =>
        device.Runtime.View.Recent.LastOrDefault(m => m.Value<string>("type") == "commandResult" && m.Value<string>("commandId") == commandId);

    /// <summary>視窗最後收到的這個對話的標頭（open 的 conversation 或之後的 header；在 UI 執行緒呼叫）</summary>
    private static JObject? ReplicaHeader(LongRunDevice device, string conversationId) =>
        device.Runtime.View.Recent.LastOrDefault(m => m.Value<string>("type") is "conversation" or "header" && m.Value<string>("conversationId") == conversationId
            && m["header"] is JObject)?["header"] as JObject;

    /// <summary>init 或 conversations 的清單有這個對話，而且擁有者在線</summary>
    private static bool Lists(JObject message, string conversationId) =>
        message["conversations"] is JArray list
            ? list.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true)
            : message["items"] is JArray items && items.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true);
}
