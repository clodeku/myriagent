using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.LongRun;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Xunit.Abstractions;
using LongRunGate = Myriagent.Tests.Sync.LongRun.LongRun;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// Grok、Antigravity 也同步的端到端（設計 docs/plans/2026-10-22-grok-antigravity-sync.md 第 3 節、P5）：兩台整條桌面（tools/fake-device 的 DeviceRuntime）
/// 配對到同一棵樹的 MyriagentHub 子程序（宣告 prov1）。A 有一個 Grok 帳戶並在它開一個對話 → 伺服器收下 link.upsert（身分鍵 grok:{信箱}）、對話事件與四種的
/// cli.state，一筆都沒有被擋 → B 的帳戶目錄有這個 Grok 邏輯帳戶、B 有這個對話的副本
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class GrokSyncScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly FakeAccountSpec ClaudeA = LongRunDevice.Account("grok-scn-claude", AgentProvider.Claude, "scn-a@example.test", "org-scn-a");

    private static readonly FakeAccountSpec GrokA = LongRunDevice.Account("grok-scn", AgentProvider.Grok, "Grok-Scn@Example.test");

    private static readonly FakeAccountSpec ClaudeB = LongRunDevice.Account("grok-scn-b", AgentProvider.Claude, "scn-b@example.test", "org-scn-b");

    [EnvFact(LongRunGate.Gate, "Grok 的帳戶與對話經同一棵樹的 MyriagentHub 從 A 到 B（prov1）")]
    public async Task A的Grok帳戶與對話_經伺服器到B_目錄與副本都看得到()
    {
        var keysBefore = UserKeyStore.FilesCreatedByTestHub();
        var hub = await MyriagentHubProcess.StartAsync();
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
        await using var a = await LongRunDevice.PairAndStartAsync(hub, owner, "grok-scn-a", [ClaudeA, GrokA], HubEngine.FastBackoff(0.2, 0.5, 1));
        await using var b = await LongRunDevice.PairAndStartAsync(hub, owner, "grok-scn-b", [ClaudeB], HubEngine.FastBackoff(0.2, 0.5, 1));
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Prov1), Wait, "A 連上、協商出 prov1");
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Prov1), Wait, "B 連上、協商出 prov1");

        var chat = await a.Runtime.ChatAsync(new ChatSpec("Grok 的對話", GrokA.Alias, "Grok 對話的第一則", [], null, Wait));
        chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var wireId = ConversationIds.ToWire(chat.ConversationId) ?? throw new InvalidOperationException("對話 id 不是線上 id 的形狀");
        (await a.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue("A 的帳戶與對話事件送完");

        var rejected = await a.Inspect(v => v.Outbox.Where(e => e.State == OutboxState.Rejected).Select(e => $"{e.Kind}:{e.ConversationId}").ToList());
        rejected.Should().BeEmpty("prov1 的伺服器收 Grok 的目錄、對話事件與 cli.state，一筆都不擋");
        (await a.Inspect(v => v.Store!.GetKv(SyncStoreSchema.Prov1Armed))).Should().Be("1");
        (await a.Inspect(v => v.Store!.GetKv(SyncStoreSchema.Prov1Replicas))).Should().Be("1", "第一次協商出 prov1：補拉過一次對話中繼資料");

        // B：目錄有 A 的 Grok 邏輯帳戶（身分鍵是小寫的信箱），也有這個對話的副本
        await b.WaitUntilAsync(v => v.Catalog?.Accounts.Values.Any(e => e.Provider == "Grok" && e.IdentityKey == "grok:grok-scn@example.test") ?? false,
            Wait, "B 的帳戶目錄收到 A 的 Grok 帳戶");
        (await b.Runtime.WaitUntilAsync(() => b.Runtime.Hub.IsReplicaConversation(wireId), Wait))
            .Should().BeTrue("B 有 A 的 Grok 對話的副本（桌面的 ConversationEventRules 收四種）");
        output.WriteLine($"A 的 Grok 對話 {wireId} 已到 B 的副本；B 的目錄有 grok:grok-scn@example.test");
    }
}
