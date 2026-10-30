using System.Security.Cryptography;
using Myriagent.Agents;
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
/// WP4.4 計畫步 12 的端到端：瀏覽器 hub 的用戶端 → 同一棵樹的 MyriagentHub 子程序（WP4.5 的指令路由：投影、選連線、簽信封）→
/// 整條桌面（tools/fake-device 的 DeviceRuntime：中樞、協調者與引擎，劇本化的會話）：引擎驗信封、commands_seen 先記 executing、經 port 進中樞閘門 →
/// 結果回伺服器 → 瀏覽器的 <c>commandResult</c>。伺服器的 <c>Hub:PublicOrigin</c> 是配對網址本身（https://127.0.0.1:埠），信封的 aud 才等於電腦由配對網址推得的（D5）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RemoteCommandScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly FakeAccountSpec Account = LongRunDevice.Account("remote", AgentProvider.Claude, "remote@example.test", "org-remote");

    [EnvFact(LongRunGate.Gate, "遠端指令的端到端：瀏覽器 → 同一棵樹的 MyriagentHub → 桌面的引擎與中樞 → 結果回瀏覽器")]
    public async Task 瀏覽器送send_政策Off時rejected_policy什麼都沒做_改成Control之後done而且對話收到訊息()
    {
        var keysBefore = UserKeyStore.FilesCreatedByTestHub();
        var hub = await MyriagentHubProcess.StartAsync(publicOriginIsBaseAddress: true);
        try
        {
            await SendScenarioAsync(hub);
        }
        finally
        {
            await hub.DisposeAsync();
        }

        hub.ForcedKill.Should().BeFalse("MyriagentHub 要能正常停止（強制結束會留下孤兒金鑰檔）");
        UserKeyStore.FilesCreatedByTestHub().Except(keysBefore).Should().BeEmpty("正常停止的 MyriagentHub 不留下金鑰檔（D5）");
    }

    private async Task SendScenarioAsync(MyriagentHubProcess hub)
    {
        using var owner = await OwnerClient.LoginAsync(hub);
        await using var device = await LongRunDevice.PairAndStartAsync(hub, owner, "remote-cmd", [Account], HubEngine.FastBackoff(0.2, 0.5, 1));
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Cmd1), Wait, "連上、協商出 cmd1");

        // 本機開一個對話跑一輪（固定回覆），發布到伺服器
        var chat = await device.Runtime.ChatAsync(new ChatSpec("遠端指令", Account.Alias, "本機的第一則", [], null, Wait));
        chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var wireId = ConversationIds.ToWire(chat.ConversationId) ?? throw new InvalidOperationException("對話 id 不是線上 id 的形狀");
        (await device.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue("對話事件送完");

        await using var browser = await BrowserHubClient.ConnectAsync(hub.BaseAddress, owner.Cookies, hub.PublicOrigin);
        var init = await browser.ReadyAsync(Wait);
        if (!Lists(init, wireId))
            await browser.WaitForAsync(m => m.Value<string>("type") == "conversations" && Lists(m, wireId), Wait, "對話清單有這個對話、擁有者在線");

        // 1. 預設政策 Off：伺服器不預檢（P45 D37），電腦的閘門 5e 回 policy，對話不變
        var itemsBefore = await device.Runtime.OnUi(() => device.Runtime.Hub.GetItems(chat.ConversationId).Count);
        var rejected = await browser.RemoteAsync(Send(wireId, "政策 Off 時送的"), Wait);
        output.WriteLine("政策 Off：" + rejected.ToString(Newtonsoft.Json.Formatting.None));
        rejected.Value<string>("state").Should().Be(RemoteResultStates.Rejected);
        rejected.Value<string>("code").Should().Be(RemoteResultCodes.Policy);
        (await device.Runtime.OnUi(() => device.Runtime.Hub.GetItems(chat.ConversationId).Count)).Should().Be(itemsBefore, "被拒的指令什麼都沒做");

        // 2. 本機改成 Control：引擎以新的政策重新 Hello，之後同一個對話收得到遠端訊息
        var generation = await device.Inspect(v => v.ConnectionGeneration);
        await device.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control" });
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Cmd1), Wait,
            "以新的政策重新連上");
        var marker = "遠端送的 " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var done = await browser.RemoteAsync(Send(wireId, marker), Wait);
        output.WriteLine("政策 Control：" + done.ToString(Newtonsoft.Json.Formatting.None));
        done.Value<string>("state").Should().Be(RemoteResultStates.Done);
        done.Value<string?>("code").Should().BeNull();
        (await device.Runtime.WaitUntilAsync(() => device.Runtime.Hub.GetItems(chat.ConversationId)
            .Any(i => i.Kind == AgentItemKind.User && i.Text == marker), Wait)).Should().BeTrue("遠端的訊息成為這個對話的使用者項目");

        // 電腦的遠端操作紀錄：兩個指令各一列（新到舊），政策照當時的
        var engine = await device.EngineAsync();
        var audit = await ((ISyncEngineCommands)engine).LoadRemoteAuditAsync(null, 10, CancellationToken.None);
        foreach (var entry in audit)
            output.WriteLine($"remote_audit：{entry.Cmd} {entry.State} {entry.Code} step={entry.Step} policy={entry.Policy} tier={entry.Tier} issuer={entry.IssuerLabel}");
        audit.Should().HaveCount(2);
        audit[0].Should().Match<RemoteAuditEntry>(e => e.Cmd == "send" && e.State == RemoteResultStates.Done && e.Policy == "Control" && e.ConversationId == wireId);
        audit[1].Should().Match<RemoteAuditEntry>(e => e.Cmd == "send" && e.State == RemoteResultStates.Rejected && e.Code == RemoteResultCodes.Policy
                                                         && e.Step == RemoteGateSteps.Policy && e.Policy == "Off");
        await device.StopAsync();
    }

    private static JObject Send(string conversationId, string text) => new() { ["cmd"] = "send", ["conversationId"] = conversationId, ["text"] = text };

    /// <summary>init 或 conversations 的清單有這個對話，而且擁有者在線</summary>
    private static bool Lists(JObject message, string conversationId) =>
        message["conversations"] is JArray list
            ? list.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true)
            : message["items"] is JArray items && items.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true);
}
