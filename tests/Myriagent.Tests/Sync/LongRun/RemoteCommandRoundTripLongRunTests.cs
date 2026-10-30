using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// R33（WP4.9 計畫步 20；<c>MYRIAGENT_SYNC_IT=1</c>）：遠端指令一來一回的耗時。同 WP4.4 的 <see cref="RemoteCommandScenarioTests"/> 那一套——
/// 瀏覽器 hub 的用戶端 → 同一棵樹的 MyriagentHub 子程序（投影、選連線、簽信封）→ 假裝置的整條桌面（引擎驗信封、commands_seen 先記 executing、
/// 經 <see cref="IRemoteCommandPort"/> 進中樞閘門）→ 結果回伺服器 → 瀏覽器的 <c>commandResult</c>。
/// 指令是 <c>rename</c>（非帳戶、非 CLI 指令，不經 RemoteAccountRig；只改暫存資料根裡這個對話的標題），暖身 <see cref="WarmUp"/> 次不算，
/// 之後依序送 <see cref="Rounds"/> 次，每次從送出 Cmd 到收到同一個 commandId 的 commandResult 計時；記 p50、p95（另附 p99、最大、平均），**不設門檻**
/// （只供第 4 階段驗收的紀錄；本機迴路、沒有延遲代理）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RemoteCommandRoundTripLongRunTests(ITestOutputHelper output)
{
    private const int WarmUp = 5;

    private const int Rounds = 200;

    /// <summary>每一次往返的等待上限（不是門檻：等不到就是功能壞了）</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly FakeAccountSpec Account = LongRunDevice.Account("roundtrip", AgentProvider.Claude, "roundtrip@example.test", "org-roundtrip");

    [EnvFact(LongRun.Gate, "遠端 rename 往返 200 次的 p50、p95（自己的 MyriagentHub，約 1 分鐘）")]
    public async Task 遠端rename往返200次_記下p50與p95_每次都done_標題是最後一次的()
    {
        var keysBefore = UserKeyStore.FilesCreatedByTestHub();
        var hub = await MyriagentHubProcess.StartAsync(publicOriginIsBaseAddress: true);
        try
        {
            await RoundTripsAsync(hub);
        }
        finally
        {
            await hub.DisposeAsync();
        }

        hub.ForcedKill.Should().BeFalse("MyriagentHub 要能正常停止（強制結束會留下孤兒金鑰檔）");
        UserKeyStore.FilesCreatedByTestHub().Except(keysBefore).Should().BeEmpty("正常停止的 MyriagentHub 不留下金鑰檔");
    }

    private async Task RoundTripsAsync(MyriagentHubProcess hub)
    {
        using var owner = await OwnerClient.LoginAsync(hub);
        await using var device = await LongRunDevice.PairAndStartAsync(hub, owner, "roundtrip", [Account], HubEngine.FastBackoff(0.2, 0.5, 1));
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Cmd1), Wait, "連上、協商出 cmd1");

        // 本機開一個對話跑一輪（固定回覆），發布到伺服器
        var chat = await device.Runtime.ChatAsync(new ChatSpec("往返", Account.Alias, "本機的第一則", [], null, Wait));
        chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var wireId = ConversationIds.ToWire(chat.ConversationId) ?? throw new InvalidOperationException("對話 id 不是線上 id 的形狀");
        (await device.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue("對話事件送完");

        // 政策改成 Control（預設 Off 時電腦回 policy）：引擎以新的政策重新 Hello
        var generation = await device.Inspect(v => v.ConnectionGeneration);
        await device.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control" });
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Cmd1), Wait,
            "以新的政策重新連上");

        await using var browser = await BrowserHubClient.ConnectAsync(hub.BaseAddress, owner.Cookies, hub.PublicOrigin);
        var init = await browser.ReadyAsync(Wait);
        if (!Lists(init, wireId))
            await browser.WaitForAsync(m => m.Value<string>("type") == "conversations" && Lists(m, wireId), Wait, "對話清單有這個對話、擁有者在線");

        var samples = new List<double>(Rounds);
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastTitle = string.Empty;
        var total = Stopwatch.StartNew();
        for (var i = 0; i < WarmUp + Rounds; i++)
        {
            lastTitle = i < WarmUp ? $"暖身 {i + 1}" : $"往返 {i - WarmUp + 1:000}";
            var clock = Stopwatch.StartNew();
            var result = await browser.RemoteAsync(new JObject { ["cmd"] = "rename", ["conversationId"] = wireId, ["title"] = lastTitle }, Wait);
            var ms = clock.Elapsed.TotalMilliseconds;
            var state = result.Value<string>("state") ?? "（沒有 state）";
            if (i < WarmUp)
            {
                state.Should().Be(RemoteResultStates.Done, "暖身的 rename 也要 done：" + result.ToString(Newtonsoft.Json.Formatting.None));
                continue;
            }

            states[state] = states.GetValueOrDefault(state) + 1;
            samples.Add(ms);
        }

        total.Stop();
        output.WriteLine($"rename 往返 {samples.Count} 次（暖身 {WarmUp} 次不算；本機迴路、同一棵樹的 MyriagentHub、假裝置的整條桌面）：{Describe(samples)}；"
                         + $"合計 {total.Elapsed.TotalSeconds:0.0} 秒；結果 {string.Join("、", states.Select(kv => $"{kv.Key} {kv.Value}"))}");
        output.WriteLine("R33 " + new JObject
        {
            ["rounds"] = samples.Count,
            ["warmUp"] = WarmUp,
            ["p50Ms"] = Math.Round(Percentile(samples, 50), 1),
            ["p95Ms"] = Math.Round(Percentile(samples, 95), 1),
            ["p99Ms"] = Math.Round(Percentile(samples, 99), 1),
            ["maxMs"] = Math.Round(samples.Max(), 1),
            ["meanMs"] = Math.Round(samples.Average(), 1),
            ["states"] = JObject.FromObject(states),
        }.ToString(Newtonsoft.Json.Formatting.None));

        // 不設時間門檻；功能上：每一次都 done、本機的標題是最後一次送的
        states.Should().Equal(new Dictionary<string, int> { [RemoteResultStates.Done] = Rounds }, "每一次 rename 都 done");
        (await device.Runtime.OnUi(() => device.Runtime.Hub.GetConversation(chat.ConversationId)?.Title)).Should().Be(lastTitle, "本機對話的標題是最後一次 rename 的");
        await device.StopAsync();
    }

    private static string Describe(List<double> values) =>
        $"p50 {Percentile(values, 50):0.0} ms、p95 {Percentile(values, 95):0.0} ms、p99 {Percentile(values, 99):0.0} ms、最大 {values.Max():0.0} ms、平均 {values.Average():0.0} ms";

    /// <summary>nearest-rank 百分位數（同 LiveUiThreadBudgetTests）</summary>
    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count), 1, sorted.Count) - 1];
    }

    /// <summary>init 或 conversations 的清單有這個對話，而且擁有者在線（同 RemoteCommandScenarioTests）</summary>
    private static bool Lists(JObject message, string conversationId) =>
        message["conversations"] is JArray list
            ? list.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true)
            : message["items"] is JArray items && items.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true);
}
