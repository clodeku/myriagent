using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>Claude 的持續目標（/goal）：標頭的 goal、程序開始與結束時清掉、goalClear 照一般的訊息送出 /goal clear</summary>
public class AgentHubGoalTests
{
    private static string? Goal(JObject header) => (header["header"]?["goal"] as JObject)?.Value<string>("condition");

    private static Task<JObject> HeaderWithGoal(HubHarness h, string id, string? goal, int mark)
        => h.Main.WaitForAsync("header", m => Goal(m) == goal, mark, conversationId: id);

    [Fact]
    public async Task 一輪結束回報的目標顯示在標頭_清除時送出goal_clear()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "/goal 九個檔案");
        var mark = h.Main.Mark();
        h.Raise(session, new AgentGoalEvent("九個檔案"));
        h.CompleteTurn(session, AgentTurnOutcome.Interrupted);
        await HeaderWithGoal(h, id, "九個檔案", mark);

        h.Command(h.Main, "goalClear", new { conversationId = id });
        await h.WaitUntilAsync(() => session.Sent.Count == 2, "送出 /goal clear");
        session.Sent.All[^1].Input.Text.Should().Be("/goal clear");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("/goal clear", "照一般的訊息送出：看得到");

        mark = h.Main.Mark();
        h.Raise(session, new AgentGoalEvent(null));
        h.CompleteTurn(session);
        await HeaderWithGoal(h, id, null, mark);
    }

    [Fact]
    public async Task 執行中不清除_程序開始或結束時目標跟著沒了()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "/goal 一直做");
        var mark = h.Main.Mark();
        h.Raise(session, new AgentGoalEvent("一直做"));
        await HeaderWithGoal(h, id, "一直做", mark);
        var sent = session.Sent.Count;
        mark = h.Main.Mark();
        h.Command(h.Main, "goalClear", new { conversationId = id });
        session.Sent.Count.Should().Be(sent, "執行中不送");
        h.Main.Messages("toast", mark).Should().ContainSingle();

        h.CompleteTurn(session);
        mark = h.Main.Mark();
        h.Raise(session, new AgentProcessExitedEvent(0, null, Expected: true, "p1"));
        await HeaderWithGoal(h, id, null, mark);

        mark = h.Main.Mark();
        h.Raise(session, new AgentGoalEvent("又一個"));
        await HeaderWithGoal(h, id, "又一個", mark);
        mark = h.Main.Mark();
        h.Raise(session, new AgentProcessStartedEvent("p2", null));
        await HeaderWithGoal(h, id, null, mark);
    }
}
