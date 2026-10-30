using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 閒置時 CLI 自己開的一輪（Claude 背景工作完成的通知，AgentIdleTurnStartedEvent）：對話改成執行中——標頭轉圈、狀態文字、
/// 執行數、可以停止、送出的插入或排進佇列，這一輪照常以 TurnCompleted 結束。以前中樞不知道這一輪開始，
/// 回覆一直出來、對話卻沒有執行中的動畫，也沒有停止鈕
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubIdleTurnTests
{
    [Fact]
    public async Task 背景工作完成後CLI自己開的一輪_對話標成執行中_狀態文字_結束後回到閒置並通知()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));

        h.IsRunning(id).Should().BeTrue();
        h.OnUi(() => h.Hub.RunningCount).Should().Be(1);
        var header = await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), mark, conversationId: id);
        header["header"]!.Value<string>("status").Should().Be(AgentHub.BackgroundTaskTurnStatus, "第一個輸出之前說明為什麼代理自己動起來");
        header["header"]!.Value<long?>("startedAt").Should().NotBeNull("顯示經過的秒數");

        mark = h.Main.Mark();
        h.Raise(s, new AgentTextDeltaEvent("b1", "測試都過了"));
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("status") == "回覆中…", mark, conversationId: id);

        h.CompleteTurn(s);
        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant).Status.Should().Be(AgentItemStatus.Done);
    }

    [Fact]
    public async Task 一般的CLI自己開的一輪_狀態文字是思考中()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: false));

        h.IsRunning(id).Should().BeTrue();
        (await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), mark, conversationId: id))["header"]!
            .Value<string>("status").Should().Be("思考中…");
    }

    [Fact]
    public async Task Codex持續目標的下一輪_對話標成執行中_狀態文字說明是持續目標_可以停止()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "移植全部功能");
        h.Raise(s, new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, null) { ContinuesOnItsOwn = true });
        var mark = h.Main.Mark();

        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: false, ForThreadGoal: true));

        h.IsRunning(id).Should().BeTrue("以前 codex 自己開的下一輪中樞看不到：回覆一直出來，對話卻沒有執行中的動畫與停止鈕");
        (await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), mark, conversationId: id))["header"]!
            .Value<string>("status").Should().Be(AgentHub.ThreadGoalTurnStatus);
        h.Command(h.Main, "stop", new { conversationId = id });
        s.Interrupts.Should().Be(1);
    }

    [Fact]
    public void Codex持續目標還沒完成_每輪結束不念完成也不通知_標記完成的那一輪才通知()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "移植全部功能");

        h.Raise(s, new AgentTextCompletedEvent("b1", "商品欄位已接通，接著做採購。"));
        h.Raise(s, new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, null) { ContinuesOnItsOwn = true });
        h.Host.Spoken.Count.Should().Be(0, "codex 接著自己開下一輪，還沒真的完成");
        h.Host.Notifications.Count.Should().Be(0);
        h.IsRunning(id).Should().BeFalse("這一輪照常結束（存檔、同步記一輪結束），下一輪由 AgentIdleTurnStartedEvent 開始");

        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: false, ForThreadGoal: true));
        h.Raise(s, new AgentTextCompletedEvent("b2", "全部功能都已移植並驗證。"));
        h.CompleteTurn(s);
        h.Host.Spoken.All.Should().Equal($"「{h.Conversation(id)!.Title}」已完成：全部功能都已移植並驗證");
        h.Host.Notifications.Count.Should().Be(1);
    }

    [Fact]
    public void 那一輪進行中送出_插不進去就排進佇列_結束後接著送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.CompleteTurn(s);
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));

        h.Send(h.Main, id, "看完結果再改一下");

        s.Sent.Count.Should().Be(1, "CLI 自己那一輪還在跑：不直接送");
        h.Queue(id).Should().Equal("看完結果再改一下");
        h.CompleteTurn(s);
        s.Sent.Count.Should().Be(2, "那一輪結束後接著送");
        s.Sent.All[1].Input.Text.Should().Be("看完結果再改一下");
    }

    [Fact]
    public void 那一輪進行中送出_插得進去就插進那一輪()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.CompleteTurn(s);
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        s.CanSteer = true;

        h.Send(h.Main, id, "順便看一下覆蓋率");

        s.Steered.All.Select(i => i.Text).Should().Equal("順便看一下覆蓋率");
        h.Queue(id).Should().BeEmpty();
        h.CompleteTurn(s);
        s.Sent.Count.Should().Be(1, "已經插進那一輪，不能再當成下一則送出");
    }

    [Fact]
    public void 那一輪可以從app停止()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.CompleteTurn(s);
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));

        h.Command(h.Main, "stop", new { conversationId = id });

        s.Interrupts.Should().Be(1, "以前中樞以為閒置，停止鈕不出現、按了也不送");
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Last().Text.Should().Be("已停止這一輪");
    }

    [Fact]
    public async Task 送出比事件先到_已在執行中_標頭不動()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();
        h.Send(h.Main, id, "第二則");
        h.Raise(s, new AgentTextDeltaEvent("b1", "好"));
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("status") == "回覆中…", mark, conversationId: id);
        mark = h.Main.Mark();

        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));

        await Task.Delay(150);
        h.Main.Messages("header", mark, id).Should().BeEmpty("會話已讓這則接手 CLI 自己那一輪：同一輪，狀態文字與開始時間都不改");
        h.OnUi(() => h.Hub.RunningCount).Should().Be(1);
    }
}
