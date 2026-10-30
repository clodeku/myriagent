using Myriagent.Agents;
using FakeDevice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// 假會話收到的呼叫次數（WP4.9 步 15）：FF3 以 status 的 session 證明每個遠端動作只到假會話一次，所以每種呼叫都要各自計數、
/// 卡片不在等待時（重送、第二個分頁）照樣算，沒給計數器也照常運作
/// </summary>
public sealed class SessionCallsTests
{
    private static readonly AgentSessionSettings Settings = new(null, false, null, "fake", _ => { }, null, null, null,
        AgentPermissionMode.Ask, @"C:\", @"C:\", "", false);

    [Fact]
    public async Task 每種呼叫各記一次_卡片不在等待也算()
    {
        var calls = new SessionCalls();
        var gate = new object();
        var session = new ScriptedSession(AgentProvider.Claude, action =>
        {
            lock (gate)
                action();
        }, () => null, calls);
        calls.ToJson().Should().BeEquivalentTo(Counts(0, 0, 0, 0, 0, 0, 0, 0, 0));
        calls.LastModelSwitch.Should().BeNull();

        await session.SendAsync(new AgentTurnInput("第一則", []), Settings);
        await session.SendAsync(new AgentTurnInput("第二則", []), Settings);
        await session.InterruptAsync();
        await session.AnswerQuestionAsync("fake-req-1-0", new Dictionary<string, IReadOnlyList<string>> { ["q"] = ["a"] });
        await session.DeclineQuestionAsync("fake-req-1-1");
        await session.RespondApprovalAsync("fake-req-1-2", AgentApprovalChoice.Allow, null);
        await session.RespondApprovalAsync("fake-req-1-2", AgentApprovalChoice.Allow, null);
        await session.RespondPlanAsync("fake-req-1-3", AgentPlanChoice.Manual, null);
        (await session.SteerAsync(new AgentTurnInput("插入", []))).Should().BeFalse("假會話一律不插入（中樞照舊排進佇列）");
        (await session.SetModelAsync("sonnet", "high")).Should().BeTrue("假會話一律回已套用（同 Claude、Grok 的會話）");
        calls.LastModelSwitch.Should().Be(("sonnet", "high", false), "沒有劇本的一輪已經結束：記下收到的值與當時沒在執行");

        calls.ToJson().Should().BeEquivalentTo(Counts(sends: 2, interrupts: 1, answers: 1, declines: 1, approvals: 2, plans: 1, steers: 1, modelSwitches: 1, modelSwitchesWhileRunning: 0),
            "沒有在等的請求鍵（重送）也算一次：計數的是會話收到的呼叫，不是卡片被處理的次數");
    }

    [Fact]
    public async Task 沒給計數器照常運作()
    {
        var session = new ScriptedSession(AgentProvider.Claude, action => action());
        var events = new List<AgentEvent>();
        session.EventReceived += events.Add;
        await session.SendAsync(new AgentTurnInput("開始", []), Settings);
        await session.InterruptAsync();
        events.OfType<AgentTurnCompletedEvent>().Should().ContainSingle();
    }

    private static JObject Counts(long sends, long interrupts, long answers, long declines, long approvals, long plans, long steers,
        long modelSwitches, long modelSwitchesWhileRunning) => new()
    {
        ["sends"] = sends,
        ["interrupts"] = interrupts,
        ["answers"] = answers,
        ["declines"] = declines,
        ["approvals"] = approvals,
        ["plans"] = plans,
        ["steers"] = steers,
        ["modelSwitches"] = modelSwitches,
        ["modelSwitchesWhileRunning"] = modelSwitchesWhileRunning,
    };
}
