using Myriagent.Agents;
using Myriagent.Hub;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Myriagent.Tests.Hub;

/// <summary>帳戶卡的「子代理執行中」（AccountView.sessions.subagents；AgentHub.SubagentCount.cs、SubagentCountRules）</summary>
public sealed class AgentHubSubagentCountTests
{
    private static int Count(JObject m, HubHarness h) => m["accounts"]!.Single(a => a.Value<string>("id") == h.Claude.Id)["sessions"]!.Value<int>("subagents");

    private static AgentTaskLifecycleEvent TaskStarted(string taskId, string? toolUseId, bool background)
        => new(taskId, Ended: false, SubagentCountRules.ClaudeAgentTaskType, "找出所有呼叫點", toolUseId, background, null, null);

    private static AgentTaskLifecycleEvent TaskEnded(string taskId, string? toolUseId)
        => new(taskId, Ended: true, SubagentCountRules.ClaudeAgentTaskType, null, toolUseId, true, "completed", "完成");

    [Fact]
    public async Task 前景的子代理工具卡_一個算一個_其他工具不算_結束就減()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude, "平行查三件事");

        var mark = h.Main.Mark();
        h.Raise(s, new AgentToolStartedEvent("a1", "Agent", "agent", "查 A", null));
        h.Raise(s, new AgentToolStartedEvent("a2", "Agent", "agent", "查 B", null));
        h.Raise(s, new AgentToolStartedEvent("b1", "執行指令", "bash", "ls", null));
        await h.Main.WaitForAsync("accounts", m => Count(m, h) == 2, mark);

        // 同一個子代理的 local_agent 工作（tool_use_id 是那張卡）不重複算
        mark = h.Main.Mark();
        h.Raise(s, TaskStarted("t1", "a1", background: false));
        h.Raise(s, new AgentToolCompletedEvent("a2", AgentItemStatus.Done, "B 的結果", []));
        await h.Main.WaitForAsync("accounts", m => Count(m, h) == 1, mark);

        mark = h.Main.Mark();
        h.Raise(s, new AgentToolCompletedEvent("a1", AgentItemStatus.Done, "A 的結果", []));
        h.Raise(s, TaskEnded("t1", "a1"));
        await h.Main.WaitForAsync("accounts", m => Count(m, h) == 0, mark);
    }

    [Fact]
    public async Task 背景的子代理_工具卡結束了照算_這一輪結束了照算_工作結束才減()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude, "背景查資料");

        var mark = h.Main.Mark();
        h.Raise(s, new AgentToolStartedEvent("a1", "Agent", "agent", "背景查", null));
        h.Raise(s, TaskStarted("t1", "a1", background: true));
        // Claude 的背景子代理：工具卡馬上以「已在背景啟動」結束，子代理照樣在跑
        h.Raise(s, new AgentToolCompletedEvent("a1", AgentItemStatus.Done, "Async agent launched", []));
        h.CompleteTurn(s);
        await h.Main.WaitForAsync("accounts", m => Count(m, h) == 1, mark);

        mark = h.Main.Mark();
        h.Raise(s, TaskEnded("t1", "a1"));
        await h.Main.WaitForAsync("accounts", m => Count(m, h) == 0, mark);
    }

    [Fact]
    public async Task 程序結束_還沒結束的子代理工作跟著歸零()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude, "背景查資料");

        var mark = h.Main.Mark();
        h.Raise(s, TaskStarted("t1", null, background: true));
        h.Raise(s, TaskStarted("t2", "x2", background: true));
        h.CompleteTurn(s);
        await h.Main.WaitForAsync("accounts", m => Count(m, h) == 2, mark);

        mark = h.Main.Mark();
        h.Raise(s, new AgentProcessExitedEvent(1, null, Expected: false, "p1"));
        await h.Main.WaitForAsync("accounts", m => Count(m, h) == 0, mark);
    }

    [Fact]
    public async Task 重送init時_帳戶卡帶子代理數()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude, "平行查");
        h.Raise(s, new AgentToolStartedEvent("a1", "Agent", "agent", "查 A", null));

        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");
        var init = await h.Main.WaitForAsync("init", since: mark);
        Count(init, h).Should().Be(1);
    }
}
