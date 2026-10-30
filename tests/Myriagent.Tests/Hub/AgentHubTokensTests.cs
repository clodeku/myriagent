using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 側欄帳戶卡的 token 計數（AgentHub.Tokens.cs）：會話的增量依對話的帳戶即時累加、送 tokens 訊息；一輪結束時不再以這一輪的用量補差額（每一筆都由會話發）；
/// init 帶目前的計數；跨日時重送（今天歸零）；關閉時寫檔
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubTokensTests
{
    private static AgentUsage U(long input, long output, long cached) => new(input, output, cached, null);

    private static long Total(JObject message, string accountId, string span = "today")
    {
        var c = (JObject)message["tokens"]!["accounts"]![accountId]![span]!;
        return c.Value<long>("input") + c.Value<long>("output");
    }

    [Fact]
    public async Task 增量即時送出_一輪結束不以這一輪的用量補記()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready");
        var init = await h.Main.WaitForAsync("init");
        init["tokens"]!["accounts"]!["acc-claude"]!["today"]!.Value<long>("input").Should().Be(0, "init 就帶各帳戶的計數");
        var (id, session) = h.StartConversation(h.Main, h.Claude);

        var mark = h.Main.Mark();
        h.Raise(session, new AgentTokenUsageEvent(U(46_873, 6, 16_240)));
        var first = await h.Main.WaitForAsync("tokens", m => Total(m, "acc-claude") == 46_879, since: mark);
        ((JObject)first["tokens"]!["accounts"]!["acc-claude"]!["today"]!).Value<long>("cached").Should().Be(16_240);
        Total(first, "acc-codex").Should().Be(0, "別的帳戶不受影響");

        h.Raise(session, new AgentTokenUsageEvent(U(0, 267, 0)));
        h.Raise(session, new AgentTokenUsageEvent(U(51_486, 4, 46_871)));
        h.Raise(session, new AgentTokenUsageEvent(U(0, 603, 0)));
        // 這一輪的 usage 等於即時記到的合計
        h.CompleteTurn(session, usage: U(98_359, 880, 63_111));
        var settled = await h.Main.WaitForAsync("tokens", m => Total(m, "acc-claude") == 99_239, since: mark);
        Total(settled, "acc-claude", "week").Should().Be(99_239);
        Total(settled, "acc-claude", "month").Should().Be(99_239);

        // 沒有即時增量的一輪（Grok 的會話自己在結束時發增量）：中樞不以這一輪的用量補
        h.Send(h.Main, id, "第二則");
        h.CompleteTurn(session, usage: U(1_200, 30, 0));
        await Task.Delay(150);
        Total(h.Main.Last("tokens")!, "acc-claude").Should().Be(99_239, "這一輪的用量只拿來顯示「上一輪」");

        // 程序在這一輪中途重開過：即時記到的比結束時回報的多，不倒扣
        h.Send(h.Main, id, "第三則");
        h.Raise(session, new AgentTokenUsageEvent(U(5_000, 10, 0)));
        h.CompleteTurn(session, usage: U(2_000, 10, 0));
        await h.Main.WaitForAsync("tokens", m => Total(m, "acc-claude") == 104_249, since: mark);
        await Task.Delay(150);
        Total(h.Main.Last("tokens")!, "acc-claude").Should().Be(104_249);
    }

    [Fact]
    public async Task 中樞先收尾這一輪_之後CLI才回報整輪用量_不再記一次()
    {
        // 2026-10-25 的實例：Codex 一輪 9,326 萬輸入，中樞的這一輪計數被提早清掉，CLI 之後回報的整輪用量又被補記一次（那天多記 15%）
        using var h = new HubHarness();
        var (_, session) = h.StartConversation(h.Main, h.Codex);
        h.Raise(session, new AgentTokenUsageEvent(U(90_000, 300, 80_000)));
        await h.Main.WaitForAsync("tokens", m => Total(m, "acc-codex") == 90_300);
        // 這一輪先結束一次（中樞清掉這一輪的計數），CLI 晚到的結束又帶整輪的用量
        h.CompleteTurn(session, AgentTurnOutcome.Interrupted);
        h.CompleteTurn(session, AgentTurnOutcome.Interrupted, usage: U(90_000, 300, 80_000));
        await Task.Delay(200);
        Total(h.Main.Last("tokens")!, "acc-codex").Should().Be(90_300, "整輪用量只是顯示用，已即時記過");
    }

    [Fact]
    public async Task 跨日重送_今天歸零_近7天照算()
    {
        using var h = new HubHarness();
        var (_, session) = h.StartConversation(h.Main, h.Claude);
        h.Raise(session, new AgentTokenUsageEvent(U(1_000, 10, 0)));
        await h.Main.WaitForAsync("tokens", m => Total(m, "acc-claude") == 1_010);

        var tomorrow = DateTime.Now.Date.AddDays(1).AddMinutes(1);
        h.Ui.Invoke(() => h.Hub.TokenClock = () => tomorrow);
        var mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.RunMaintenance(tomorrow));
        var next = await h.Main.WaitForAsync("tokens", since: mark);
        next["tokens"]!.Value<string>("day").Should().Be(tomorrow.ToString("yyyy-MM-dd"));
        Total(next, "acc-claude").Should().Be(0, "新的一天從 0 開始");
        Total(next, "acc-claude", "week").Should().Be(1_010);
    }

    [Fact]
    public async Task 同步加上其他電腦的計數_重送tokens不通知同步_這台的計數變了才通知()
    {
        using var h = new HubHarness();
        var bridge = new TokensBridge();
        h.Ui.Invoke(() => h.Hub.AttachSync(bridge));
        h.Command(h.Main, "ready");
        var init = await h.Main.WaitForAsync("init");
        init["tokens"]!["accounts"]!["acc-claude"]!["today"]!.Value<long>("input").Should().Be(0, "還沒收到其他電腦的計數");

        bridge.Extra = 5_000;
        var mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.RefreshTokens());
        var refreshed = await h.Main.WaitForAsync("tokens", since: mark);
        Total(refreshed, "acc-claude").Should().Be(5_000, "組 tokens 時經同步橋加上其他電腦的");
        refreshed["tokens"]!["accounts"]!["acc-claude"]!["here"]!.Value<long>("input").Should().Be(0);
        bridge.Changed.Should().Be(0, "這台的計數沒變：不排上傳");

        var (_, session) = h.StartConversation(h.Main, h.Claude);
        h.Raise(session, new AgentTokenUsageEvent(U(1_000, 10, 0)));
        var counted = await h.Main.WaitForAsync("tokens", m => Total(m, "acc-claude") == 6_010);
        counted["tokens"]!["accounts"]!["acc-claude"]!["here"]!.Value<long>("input").Should().Be(1_000, "這台今天的");
        bridge.Changed.Should().BeGreaterThan(0, "這台的計數變了才通知同步");
    }

    /// <summary>只記 OnTokensChanged、DecorateTokens 時給 acc-claude 加上 <see cref="Extra"/>（here 是加之前的 today）的同步橋</summary>
    private sealed class TokensBridge : ISyncBridge
    {
        public long Extra { get; set; }

        public int Changed { get; private set; }

        void ISyncBridge.OnTokensChanged() => Changed++;

        void ISyncBridge.DecorateTokens(JObject tokens, DateTime now)
        {
            if (Extra == 0 || tokens["accounts"]?["acc-claude"] is not JObject entry)
                return;
            entry["here"] = entry["today"]!.DeepClone();
            entry["today"]!["input"] = entry["today"]!.Value<long>("input") + Extra;
        }

        public bool HandleCommand(IAgentView view, string type, JObject msg) => false;

        public void OnAccountUpdated(string accountId)
        {
        }

        public void OnAccountsReordered()
        {
        }

        public void OnRoamingSettingChanged(string key, JToken value)
        {
        }

        public JObject? InitRemote() => null;

        public bool IsStarting => false;

        public JObject? DevicesCli() => null;

        public void DecorateAccounts(JArray accounts)
        {
        }

        public CatalogAccountTemplate? ClaimCatalogAccount(string? logicalAccountId, out string failure)
        {
            failure = string.Empty;
            return null;
        }

        public string? ExpectedEmail(string localAccountId) => null;
    }

    [Fact]
    public async Task 關閉時寫檔()
    {
        string path;
        using (var h = new HubHarness())
        {
            var (_, session) = h.StartConversation(h.Main, h.Codex);
            h.Raise(session, new AgentTokenUsageEvent(U(14_200, 34, 0)));
            await h.Main.WaitForAsync("tokens", m => Total(m, "acc-codex") == 14_234);
            path = AppPaths.TokenTallyFile;
            h.Ui.Invoke(() => h.Hub.Dispose());
            File.ReadAllText(path).Should().Contain("acc-codex").And.Contain("14200");
        }
    }
}
