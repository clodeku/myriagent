using System.Security.Cryptography;
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
/// 桌面遠端操作的端到端（sync-protocol 14.7）：兩台整條桌面（tools/fake-device 的 DeviceRuntime：中樞、協調者與引擎）配對到同一棵樹的 MyriagentHub 子程序。
/// 桌面 B 在自己的副本送出 → B 的中樞交給同步（PeerCmd）→ 伺服器簽 issuer.kind = device 的信封 → 桌面 A 驗章、本機政策、中樞閘門、執行 → 結果經 PeerReply 回 B 的視窗；
/// 接著 B 把草稿改開在 A、第一次送出（conversationCreate）→ A 建立對話 → B 的副本出現、草稿換成它。伺服器的 <c>Hub:PublicOrigin</c> 是配對網址本身（同 RemoteCommandScenarioTests）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class PeerControlScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly FakeAccountSpec AccountA = LongRunDevice.Account("peer-a", AgentProvider.Claude, "peer-a@example.test", "org-peer-a");

    private static readonly FakeAccountSpec AccountB = LongRunDevice.Account("peer-b", AgentProvider.Claude, "peer-b@example.test", "org-peer-b");

    [EnvFact(LongRunGate.Gate, "桌面遠端操作的端到端：桌面 B → 同一棵樹的 MyriagentHub → 桌面 A 的引擎與中樞 → 結果回 B 的視窗")]
    public async Task 桌面B對A的對話送出_在A開新對話_A的紀錄寫電腦B()
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
        await using var a = await LongRunDevice.PairAndStartAsync(hub, owner, "peer-a", [AccountA], HubEngine.FastBackoff(0.2, 0.5, 1));
        await using var b = await LongRunDevice.PairAndStartAsync(hub, owner, "peer-b", [AccountB], HubEngine.FastBackoff(0.2, 0.5, 1));
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Peer1), Wait, "A 連上、協商出 peer1");
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Peer1), Wait, "B 連上、協商出 peer1");

        // A 允許遠端操作（預設 Off）
        var generation = await a.Inspect(v => v.ConnectionGeneration);
        await a.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control" });
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Peer1), Wait,
            "A 以新的政策重新連上");

        // A 這個帳戶的 CLI 回報過的模型清單（假裝置的會話不回報：直接放進設定）。B 內建的預設選單沒有這兩個值，B 列得出來就是 A 發布的
        var accountA = await a.Runtime.FindAccountAsync(AccountA.Alias) ?? throw new InvalidOperationException("A 的帳戶不見了");
        await a.Runtime.OnUi(() => a.Runtime.Settings.Models[accountA.Id] =
            [new AgentModelOption("opus-a", "A 的 Opus", "A 這個帳戶的清單", ["low", "high"]), new AgentModelOption("sonnet-a", "A 的 Sonnet", null, ["low"])]);

        // A 開一個對話跑一輪，發布到伺服器；B 看得到它的副本、能對其他電腦下指令
        var chat = await a.Runtime.ChatAsync(new ChatSpec("桌面遠端操作", AccountA.Alias, "A 的第一則", [], null, Wait));
        chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var wireId = ConversationIds.ToWire(chat.ConversationId) ?? throw new InvalidOperationException("對話 id 不是線上 id 的形狀");
        (await a.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue("A 的對話事件送完");
        (await b.Runtime.WaitUntilAsync(() => b.Runtime.Hub.IsReplicaConversation(wireId) && b.Runtime.Hub.PeerLive, Wait))
            .Should().BeTrue("B 有 A 的對話的副本，而且能對其他電腦下指令");

        // 1. B 在副本送出：A 的對話收到這則、B 的視窗收到 done
        var sendId = NewCommandId();
        var marker = "B 送的 " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        await b.Runtime.CommandAsync("send", new JObject { ["conversationId"] = wireId, ["text"] = marker, ["commandId"] = sendId });
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.Hub.GetItems(chat.ConversationId).Any(i => i.Kind == AgentItemKind.User && i.Text == marker), Wait))
            .Should().BeTrue("B 送的訊息成為 A 這個對話的使用者項目");
        (await b.Runtime.WaitUntilAsync(() => Result(b, sendId) is { } r && r.Value<string>("state") == RemoteResultStates.Done, Wait))
            .Should().BeTrue("結果經 PeerReply 回到 B 送出指令的視窗");

        // A 的遠端操作紀錄：發起者是「電腦「peer-b」」
        var audit = await ((ISyncEngineCommands)await a.EngineAsync()).LoadRemoteAuditAsync(null, 10, CancellationToken.None);
        foreach (var entry in audit)
            output.WriteLine($"A 的 remote_audit：{entry.Cmd} {entry.State} {entry.Code} step={entry.Step} issuer={entry.IssuerLabel}");
        audit.Should().Contain(e => e.Cmd == "send" && e.State == RemoteResultStates.Done && e.IssuerLabel == "電腦「peer-b」");

        // 2. B 改 A 這個對話的模型：B 的副本選單列的是 A 這個帳戶的清單（meta 的 models），A 換好、發布之後 B 開著的標頭跟著變
        await b.Runtime.CommandAsync("open", new JObject { ["conversationId"] = wireId });
        (await b.Runtime.WaitUntilAsync(() => ModelValues(ReplicaHeader(b, wireId)) == "default,opus-a,sonnet-a", Wait))
            .Should().BeTrue($"B 的模型選單是 A 這個帳戶的清單：{await b.Runtime.OnUi(() => ModelValues(ReplicaHeader(b, wireId)))}");
        var modelId = NewCommandId();
        await b.Runtime.CommandAsync("setOption", new JObject { ["conversationId"] = wireId, ["key"] = "model", ["value"] = "sonnet-a", ["commandId"] = modelId });
        (await a.Runtime.WaitUntilAsync(() => ((IConversationPublishSource)a.Runtime.Hub).SnapshotMeta([chat.ConversationId]).Single().Meta.Model == "sonnet-a", Wait))
            .Should().BeTrue("A 的對話換了模型");
        (await b.Runtime.WaitUntilAsync(() => Result(b, modelId) is { } r && r.Value<string>("state") == RemoteResultStates.Done, Wait))
            .Should().BeTrue("結果回到 B 的視窗");
        (await b.Runtime.WaitUntilAsync(() => ReplicaHeader(b, wireId)?.Value<string>("model") == "sonnet-a", Wait))
            .Should().BeTrue("A 發布新的 meta 之後，B 開著的副本標頭顯示新的模型");
        (await a.Runtime.OnUi(() => a.Runtime.Settings.DefaultsFor(accountA.Id).Model)).Should().NotBe("sonnet-a", "遠端只改這個對話，不記成 A 這個帳戶的預設（D23）");

        // 2b. B 改 A 這個對話的名稱（2026-11-10 使用者回報「從電腦 A 改電腦 B 的對話名稱好像會失敗」）：A 的標題換了、結果回 B、B 的副本標頭跟著變
        var renameId = NewCommandId();
        const string renamed = "B 改的名稱";
        await b.Runtime.CommandAsync("rename", new JObject { ["conversationId"] = wireId, ["title"] = renamed, ["commandId"] = renameId });
        var renameResult = await b.Runtime.WaitUntilAsync(() => Result(b, renameId) is not null, Wait);
        renameResult.Should().BeTrue("rename 的結果回到 B 的視窗");
        (await b.Runtime.OnUi(() => Result(b, renameId)!.Value<string>("state"))).Should().Be(RemoteResultStates.Done);
        (await a.Runtime.WaitUntilAsync(() => ((IConversationPublishSource)a.Runtime.Hub).SnapshotMeta([chat.ConversationId]).Single().Meta.Title == renamed, Wait))
            .Should().BeTrue("A 的對話改了名稱");
        (await b.Runtime.WaitUntilAsync(() => ReplicaHeader(b, wireId)?.Value<string>("title") == renamed, Wait))
            .Should().BeTrue($"A 發布新的 meta 之後，B 開著的副本標頭顯示新的名稱：{await b.Runtime.OnUi(() => ReplicaHeader(b, wireId)?.Value<string>("title"))}");

        // 3. B 把草稿改開在 A（A 的邏輯帳戶）、第一次送出：A 建立對話，B 的草稿換成副本
        // 邏輯帳戶 id 在帳戶目錄（AccountView 的 logicalId，宿主照目錄裝飾）：A 的網頁重新 ready，從 init 的 accounts 取
        string? logical = null;
        var deadline = DateTime.UtcNow + Wait;
        while (logical is null && DateTime.UtcNow < deadline)
        {
            await a.Runtime.CommandAsync("ready");
            logical = await a.Runtime.OnUi(() => (a.Runtime.View.Recent.LastOrDefault(m => m.Value<string>("type") == "init")?["accounts"] as JArray)
                ?.OfType<JObject>().FirstOrDefault(x => x.Value<string>("id") == accountA.Id)?.Value<string>("logicalId"));
            if (logical is null)
                await Task.Delay(500);
        }
        logical.Should().NotBeNull("A 的帳戶連結進目錄");
        var accountB = await b.Runtime.FindAccountAsync(AccountB.Alias) ?? throw new InvalidOperationException("B 的帳戶不見了");
        await b.Runtime.CommandAsync("newConversation", new JObject { ["accountId"] = accountB.Id, ["paneId"] = "p1" });
        var draft = await b.Runtime.OnUi(() => b.Runtime.View.Recent.LastOrDefault(m => m.Value<string>("type") == "conversation")?.Value<string>("conversationId"))
                    ?? throw new InvalidOperationException("B 沒有開出草稿");
        await b.Runtime.CommandAsync("setOption", new JObject { ["conversationId"] = draft, ["key"] = "model", ["value"] = "default" });
        await b.Runtime.CommandAsync("draftSetDevice", new JObject { ["conversationId"] = draft, ["deviceId"] = a.DeviceId, ["accountId"] = logical!, ["provider"] = "Claude" });
        var createId = NewCommandId();
        var created = "在 A 開的新對話 " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var aBefore = await a.Runtime.ConversationCountAsync();
        await b.Runtime.CommandAsync("send", new JObject { ["conversationId"] = draft, ["text"] = created, ["commandId"] = createId });

        string? swappedTo = null;
        (await b.Runtime.WaitUntilAsync(() =>
        {
            swappedTo ??= b.Runtime.View.Recent.LastOrDefault(m => m.Value<string>("type") == "conversationClosed" && m.Value<string>("conversationId") == draft)
                ?.Value<string>("replacedBy");
            return swappedTo is not null;
        }, Wait)).Should().BeTrue("A 建好、副本出現在 B 時，B 的草稿換成副本");
        output.WriteLine($"B 的草稿 {draft} 換成 {swappedTo}；conversationCreate 的結果：{Result(b, createId)}");
        (await b.Runtime.OnUi(() => b.Runtime.Hub.IsReplicaConversation(swappedTo!))).Should().BeTrue();
        (await a.Runtime.ConversationCountAsync()).Should().Be(aBefore + 1, "A 多了一個對話");
        var newOnA = await a.Runtime.OnUi(() => ((IConversationPublishSource)a.Runtime.Hub).ConversationIds().FirstOrDefault(id => ConversationIds.ToWire(id) == swappedTo));
        newOnA.Should().NotBeNull("新對話在 A 上的 id 就是 B 換過去的副本 id");
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.Hub.GetItems(newOnA!).Any(i => i.Kind == AgentItemKind.User && i.Text == created), Wait))
            .Should().BeTrue("第一則訊息在 A 的新對話裡");

        await b.StopAsync();
        await a.StopAsync();
    }

    private static string NewCommandId() => SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>B 的視窗收到的這個指令的 commandResult（還沒有回 null；在 UI 執行緒呼叫）</summary>
    private static JObject? Result(LongRunDevice device, string commandId) =>
        device.Runtime.View.Recent.LastOrDefault(m => m.Value<string>("type") == "commandResult" && m.Value<string>("commandId") == commandId);

    /// <summary>視窗最後收到的這個對話的標頭（open 的 conversation 或之後的 header；在 UI 執行緒呼叫）</summary>
    private static JObject? ReplicaHeader(LongRunDevice device, string conversationId) =>
        device.Runtime.View.Recent.LastOrDefault(m => m.Value<string>("type") is "conversation" or "header" && m.Value<string>("conversationId") == conversationId
            && m["header"] is JObject)?["header"] as JObject;

    /// <summary>標頭模型選單的 value，逗號隔開（沒有標頭時 null）</summary>
    private static string? ModelValues(JObject? header) =>
        header?["models"] is JArray models ? string.Join(",", models.Select(m => m.Value<string>("value"))) : null;
}
