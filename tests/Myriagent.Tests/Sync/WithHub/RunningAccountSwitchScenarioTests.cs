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
/// 執行中換帳戶的端到端（瀏覽器版，使用者 2026-10-23）：桌面 A 有兩個 Claude 帳戶，對話停在等核准（這一輪還在進行）；
/// 瀏覽器 hub 的用戶端經同一棵樹的 MyriagentHub 子程序送 setAccount（邏輯帳戶 id，伺服器換成 A 的本機帳戶）→ A 的中樞先停下這一輪
/// （劇本化的會話以「被中斷」結束）→ 這一輪結束時換到另一個帳戶、隱藏送出接續訊息，新帳戶的這一輪照常做完。
/// 之前先送一次帶 when turnEnd 的（2026-10-24）：伺服器收這個鍵、A 不停下這一輪也還沒換，之後改選立即換才停下。
/// 伺服器的 <c>Hub:PublicOrigin</c> 是配對網址本身（同 RemoteCommandScenarioTests）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RunningAccountSwitchScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly FakeAccountSpec Main = LongRunDevice.Account("run-acct-1", AgentProvider.Claude, "run-acct-1@example.test", "org-run-acct-1");

    private static readonly FakeAccountSpec Second = LongRunDevice.Account("run-acct-2", AgentProvider.Claude, "run-acct-2@example.test", "org-run-acct-2");

    [EnvFact(LongRunGate.Gate, "執行中換帳戶的端到端：瀏覽器 → 同一棵樹的 MyriagentHub → 桌面 A 停下這一輪、換到另一個 Claude 帳戶接著做")]
    public async Task 這一輪還在進行_瀏覽器換到同一台的另一個Claude帳戶_A停下這一輪後換過去接著做()
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
        await using var a = await LongRunDevice.PairAndStartAsync(hub, owner, "run-acct-a", [Main, Second], HubEngine.FastBackoff(0.2, 0.5, 1));
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Cmd1), Wait, "A 連上、協商出 cmd1");
        var generation = await a.Inspect(v => v.ConnectionGeneration);
        await a.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control" });
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Cmd1), Wait,
            "A 以新的政策重新連上");
        var first = await a.Runtime.FindAccountAsync(Main.Alias) ?? throw new InvalidOperationException("A 的主帳戶不見了");
        var second = await a.Runtime.FindAccountAsync(Second.Alias) ?? throw new InvalidOperationException("A 的第二個帳戶不見了");

        // A 的對話這一輪停在等核准（劇本沒有替使用者回應）
        var turn = new ScriptedTurn([new ScriptedApproval("執行指令", "bash", "npm test", null, null, 0), new ScriptedText("核准之後的回覆", 1, 0)]);
        var chat = await a.Runtime.ChatAsync(new ChatSpec("執行中換帳戶", Main.Alias, "跑測試", [], turn, Wait));
        chat.Waiting.Should().BeTrue("這一輪停在等核准、還沒結束");
        var wireId = ConversationIds.ToWire(chat.ConversationId) ?? throw new InvalidOperationException("對話 id 不是線上 id 的形狀");
        (await a.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue("A 的對話事件送完");

        await using var browser = await BrowserHubClient.ConnectAsync(hub.BaseAddress, owner.Cookies, hub.PublicOrigin);
        var init = await browser.ReadyAsync(Wait);
        if (!Lists(init, wireId))
            await browser.WaitForAsync(m => m.Value<string>("type") == "conversations" && Lists(m, wireId), Wait, "對話清單有這個對話、擁有者在線");
        // 瀏覽器的帳戶是邏輯帳戶：照信箱找（帳戶連結進目錄之後才有）
        var secondLogical = LogicalIdByEmail(init, Second.Email)
                            ?? LogicalIdByEmail(await browser.WaitForAsync(m => m.Value<string>("type") == "accounts" && LogicalIdByEmail(m, Second.Email) is not null, Wait,
                                "第二個 Claude 帳戶出現在瀏覽器的帳戶清單"), Second.Email)!;
        var interrupts0 = Interrupts(a);

        // 1. 這一輪結束後再換（when turnEnd）：伺服器收這個鍵、done；A 不停下這一輪，也還沒換
        var later = await browser.RemoteAsync(SetAccount(wireId, secondLogical, "turnEnd"), Wait);
        output.WriteLine("這一輪結束後換到第二個 Claude 帳戶：" + later.ToString(Newtonsoft.Json.Formatting.None));
        later.Value<string>("state").Should().Be(RemoteResultStates.Done);
        (Interrupts(a) - interrupts0).Should().Be(0, "等這一輪結束才換：不停下");
        (await a.Runtime.OnUi(() => a.Runtime.Hub.IsRunning(chat.ConversationId))).Should().BeTrue("這一輪還在等核准");
        (await a.Runtime.OnUi(() => ((IConversationPublishSource)a.Runtime.Hub).SnapshotMeta([chat.ConversationId]).Single().Meta.AccountId))
            .Should().Be(first.Id, "這一輪結束之前還沒換");

        // 2. 改選立即換到同一台的另一個 Claude 帳戶：done；A 先停下這一輪，結束時換過去、接續的一輪照常做完
        var switched = await browser.RemoteAsync(SetAccount(wireId, secondLogical), Wait);
        output.WriteLine("換到第二個 Claude 帳戶：" + switched.ToString(Newtonsoft.Json.Formatting.None));
        switched.Value<string>("state").Should().Be(RemoteResultStates.Done);
        (await a.Runtime.WaitUntilAsync(() => ((IConversationPublishSource)a.Runtime.Hub).SnapshotMeta([chat.ConversationId]).Single().Meta.AccountId == second.Id, Wait))
            .Should().BeTrue("這一輪停下之後，A 的對話換到第二個帳戶");
        (Interrupts(a) - interrupts0).Should().Be(1, "為了換帳戶停下這一輪：會話收到一次停止");
        (await a.Runtime.WaitUntilAsync(() => a.Runtime.Hub.GetItems(chat.ConversationId)
                .Any(i => i.Kind == AgentItemKind.Notice && i.NoticeKind == AgentNoticeKind.AccountSwitch && i.Text.StartsWith("已從「", StringComparison.Ordinal)), Wait))
            .Should().BeTrue("對話加一則換帳戶的提示");
        var items = await a.Runtime.OnUi(() => a.Runtime.Hub.GetItems(chat.ConversationId).ToList());
        items.Should().NotContain(i => i.Kind == AgentItemKind.Notice && i.Text.StartsWith("已停止這一輪", StringComparison.Ordinal), "為了換帳戶停下的不算「已停止」");
        items.Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "接續訊息不顯示成使用者訊息");
        (await a.Runtime.WaitUntilAsync(() => !a.Runtime.Hub.IsRunning(chat.ConversationId), Wait)).Should().BeTrue("新帳戶接續的一輪做完");
        (await a.Runtime.OnUi(() => a.Runtime.Settings.LastAccountId)).Should().NotBe(second.Id, "遠端不改「上次使用的帳戶」（D23）");

        await a.StopAsync();
    }

    private static long Interrupts(LongRunDevice device) => device.Runtime.SessionCalls.ToJson().Value<long>("interrupts");

    private static JObject SetAccount(string conversationId, string accountId, string? when = null)
    {
        var cmd = new JObject { ["cmd"] = "setAccount", ["conversationId"] = conversationId, ["accountId"] = accountId };
        if (when != null)
            cmd["when"] = when;
        return cmd;
    }

    /// <summary>init 或 accounts 訊息裡這個信箱的邏輯帳戶 id（沒有回 null）</summary>
    private static string? LogicalIdByEmail(JObject message, string email) =>
        (message["accounts"] as JArray)?.OfType<JObject>().FirstOrDefault(x => string.Equals(x.Value<string>("email"), email, StringComparison.OrdinalIgnoreCase))?.Value<string>("id");

    /// <summary>init 或 conversations 的清單有這個對話，而且擁有者在線</summary>
    private static bool Lists(JObject message, string conversationId) =>
        message["conversations"] is JArray list
            ? list.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true)
            : message["items"] is JArray items && items.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true);
}
