using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 代理在執行中送出非同步提問（Codex 的 request_user_input_async：CLI 立刻回「已收到」、模型照常接著做）時，中樞停下這一輪等使用者回答
/// （2026-10-28 使用者回報：代理問了問題卻不等回覆就繼續做下去）：停下不算「已停止」、不念完成，答案當作下一則訊息送出；
/// 略過就請代理依自己的判斷接著做；還沒回答時背景指令跑完不自動接續；使用者自己按停止照常是停止
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubAsyncQuestionPauseTests
{
    private static AgentQuestionEvent AsyncQuestion(string key = "async:m1")
        => new(key, new[] { new AgentQuestion { Id = "m1:0", Text = "要哪種格式？", Options = { new AgentQuestionOption { Label = "Excel" }, new AgentQuestionOption { Label = "CSV" } } } }, AnswerAsMessage: true);

    private static List<string> Notices(HubHarness h, string id)
        => h.Items(id).Where(i => i.Kind == AgentItemKind.Notice).Select(i => i.Text).ToList();

    [Fact]
    public void 執行中送出非同步提問_停下這一輪_不算已停止_不念完成_卡片等回答_回答後當下一則訊息送出()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "查一下為什麼卡住");

        h.Raise(s, AsyncQuestion());

        s.Interrupts.Should().Be(1, "代理問了問題就停下這一輪等回答");
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        h.IsRunning(id).Should().BeFalse();
        Notices(h, id).Should().NotContain(t => t.Contains("已停止"), "不是使用者按停止");
        h.Host.Notifications.All.Should().NotContain(n => n.Title.StartsWith("代理已完成"), "還沒做完，是在等回答");
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        question.Status.Should().Be(AgentItemStatus.Waiting);
        s.Sent.Count.Should().Be(1);

        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "CSV" } } });

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("我的回答：\n要哪種格式？：CSV");
        question.Status.Should().Be(AgentItemStatus.Done);
    }

    [Fact]
    public void 同一輪問了兩題_只停一次()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Codex);

        h.Raise(s, AsyncQuestion("async:m1"));
        h.Raise(s, AsyncQuestion("async:m2"));

        s.Interrupts.Should().Be(1);
    }

    [Fact]
    public void 停下之前這一輪就自己做完了_照一般的完成()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, AsyncQuestion());

        h.CompleteTurn(s);

        h.Host.Notifications.All.Should().Contain(n => n.Title.StartsWith("代理已完成"));
        h.Items(id).Single(i => i.Kind == AgentItemKind.Question).Status.Should().Be(AgentItemStatus.Waiting, "非同步提問在這一輪結束後仍可回答");
    }

    [Fact]
    public void 停下途中使用者按停止_照常是已停止()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, AsyncQuestion());

        h.Command(h.Main, "stop", new { conversationId = id });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        Notices(h, id).Should().Contain("已停止這一輪");
    }

    [Fact]
    public void 略過停下這一輪的提問_請代理依自己的判斷接著做()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, AsyncQuestion());
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);

        h.Command(h.Main, "skipQuestion", new { conversationId = id, itemId = question.Id });

        question.Status.Should().Be(AgentItemStatus.Declined);
        s.Sent.Count.Should().Be(2, "不然代理一直停著");
        s.Sent.All[1].Input.Text.Should().Be(AgentHubRules.SkippedAsyncQuestionMessage);
    }

    [Fact]
    public void 停下途中略過_停下後接著送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, AsyncQuestion());
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);

        h.Command(h.Main, "skipQuestion", new { conversationId = id, itemId = question.Id });
        s.Sent.Count.Should().Be(1, "這一輪還在停下");
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be(AgentHubRules.SkippedAsyncQuestionMessage);
    }

    [Fact]
    public void 等回答時背景指令跑完_不自動接續_回答的那一輪結束後才接續()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, AsyncQuestion());
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        h.Raise(s, new AgentBackgroundCommandFinishedEvent("t1", "npm run build", AgentItemStatus.Done, 0, TimeSpan.FromSeconds(30)));

        s.Sent.Count.Should().Be(1, "代理會不等答案就接著做");
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "Excel" } } });
        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().StartWith("我的回答：");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(3, "回答的那一輪結束後照常接續跑完的背景指令");
        s.Sent.All[2].Input.Text.Should().Contain("npm run build");
    }

    [Fact]
    public void Claude與規劃模式的確認卡_不停下()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentQuestionEvent("q1", new[] { new AgentQuestion { Id = "a", Text = "哪個？" } }, AnswerAsMessage: false));
        h.Raise(s, AsyncQuestion());

        s.Interrupts.Should().Be(0, "只有 Codex 的非同步提問不會擋住這一輪");
    }
}
