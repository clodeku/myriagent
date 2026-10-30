using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 輸入框下方那一行的 token 數（標頭的 lastTurn、turnLive；TurnTokenRules）：一輪進行中跟著即時的增量跳、結束換成 CLI 回報的最終數字、
/// 下一輪從 0 累加、停止時用即時記到的（不跳回更早一輪）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubTurnTokensTests
{
    private static AgentUsage U(long input, long output, long cached = 0) => new(input, output, cached, null);

    [Fact]
    public void 規則_執行中有即時的累計才用它_其他用上一輪()
    {
        TurnTokenRules.Shown(true, U(10, 2), U(500, 50)).Should().Be((U(10, 2), true));
        TurnTokenRules.Shown(true, null, U(500, 50)).Should().Be((U(500, 50), false), "這一輪還沒收到任何增量：照舊顯示上一輪");
        TurnTokenRules.Shown(true, U(0, 0), U(500, 50)).Should().Be((U(500, 50), false));
        TurnTokenRules.Shown(false, U(10, 2), U(500, 50)).Should().Be((U(500, 50), false), "沒在執行：一律上一輪");
        TurnTokenRules.Shown(false, null, null).Should().Be(((AgentUsage?)null, false));
    }

    [Fact]
    public void 規則_一輪結束_有回報用回報的_沒有用即時記到的_都沒有不改()
    {
        TurnTokenRules.Final(U(100, 20), U(90, 10)).Should().Be(U(100, 20));
        TurnTokenRules.Final(null, U(90, 10)).Should().Be(U(90, 10));
        TurnTokenRules.Final(null, U(0, 0)).Should().BeNull();
        TurnTokenRules.Final(null, null).Should().BeNull();
    }

    [Fact]
    public void 規則_格式是網頁認得的_千分位不受系統地區設定影響()
    {
        TurnTokenRules.Text(U(1_234_567, 89)).Should().Be("輸入 1,234,567・輸出 89 tokens");
        TurnTokenRules.Text(null).Should().BeNull();
    }

    [Fact]
    public async Task 一輪進行中即時跳_結束換成最終數字_下一輪從0累加_停止後不跳回更早一輪()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        static string? Turn(JObject m) => m["header"]!.Value<string>("lastTurn");
        static bool Live(JObject m) => m["header"]!.Value<bool>("turnLive");

        var mark = h.Main.Mark();
        h.Raise(session, new AgentTokenUsageEvent(U(1_000, 20)));
        var live1 = await h.Main.WaitForAsync("header", m => Turn(m) == "輸入 1,000・輸出 20 tokens", since: mark, conversationId: id);
        Live(live1).Should().BeTrue();
        h.Raise(session, new AgentTokenUsageEvent(U(500, 30)));
        await h.Main.WaitForAsync("header", m => Turn(m) == "輸入 1,500・輸出 50 tokens" && Live(m), since: mark, conversationId: id);

        // 結束：換成 CLI 回報的這一輪用量
        h.CompleteTurn(session, usage: U(1_600, 55));
        var done = await h.Main.WaitForAsync("header", m => !m["header"]!.Value<bool>("running"), since: mark, conversationId: id);
        Turn(done).Should().Be("輸入 1,600・輸出 55 tokens");
        Live(done).Should().BeFalse();

        // 下一輪：還沒收到增量時照舊是上一輪，收到之後從 0 累加
        mark = h.Main.Mark();
        h.Send(h.Main, id, "第二則");
        var started = await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), since: mark, conversationId: id);
        Turn(started).Should().Be("輸入 1,600・輸出 55 tokens");
        Live(started).Should().BeFalse();
        h.Raise(session, new AgentTokenUsageEvent(U(200, 5)));
        await h.Main.WaitForAsync("header", m => Turn(m) == "輸入 200・輸出 5 tokens" && Live(m), since: mark, conversationId: id);

        // 停止（CLI 沒回報用量）：上一輪是這一輪即時記到的，不跳回 1,600
        h.Command(h.Main, "stop", new { conversationId = id });
        h.CompleteTurn(session, AgentTurnOutcome.Interrupted);
        var stopped = await h.Main.WaitForAsync("header", m => !m["header"]!.Value<bool>("running"), since: mark, conversationId: id);
        Turn(stopped).Should().Be("輸入 200・輸出 5 tokens");
        Live(stopped).Should().BeFalse();

        // 再下一輪：不會把停止前的 200 一起算進來
        mark = h.Main.Mark();
        h.Send(h.Main, id, "第三則");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), since: mark, conversationId: id);
        h.Raise(session, new AgentTokenUsageEvent(U(10, 1)));
        await h.Main.WaitForAsync("header", m => Turn(m) == "輸入 10・輸出 1 tokens" && Live(m), since: mark, conversationId: id);
    }
}
