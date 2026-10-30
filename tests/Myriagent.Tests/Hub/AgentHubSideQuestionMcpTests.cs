using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 旁支提問（/btw，AgentHub.SideQuestion.cs）與 MCP 伺服器的 elicitation 問卷卡：中樞接住 /btw 不送給代理、不排佇列；
/// 問卷卡帶來源（儀表板的 MCP 在 AgentHubAccountMcpTests）
/// </summary>
public class AgentHubSideQuestionMcpTests
{
    private static AgentChatItem? SideNotice(HubHarness h, string id)
        => h.Items(id).LastOrDefault(i => i.Kind == AgentItemKind.Notice && i.NoticeKind == AgentNoticeKind.SideQuestion);

    [Fact]
    public async Task 執行中送出btw_不插進這一輪也不排隊_問答畫成一則旁支提問()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "做事");
        var answer = new TaskCompletionSource<AgentSideAnswer>();
        session.SideAnswer = answer;
        var sentBefore = session.Sent.All.Count;

        h.Send(h.Main, id, "  /btw 你現在在做什麼？\n一句話  ");
        session.SideQuestions.All.Should().Equal("你現在在做什麼？\n一句話");
        h.Queue(id).Should().BeEmpty("不排進佇列");
        session.Sent.All.Count.Should().Be(sentBefore, "不送給代理");
        var pending = SideNotice(h, id)!;
        pending.Status.Should().Be(AgentItemStatus.Running);
        pending.Text.Should().Be("> 你現在在做什麼？\n> 一句話\n\n" + SideQuestionRules.PendingLine);
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(1, "不是使用者訊息");
        h.IsRunning(id).Should().BeTrue("這一輪照常進行");

        answer.SetResult(new AgentSideAnswer("正在跑測試。", null));
        await h.Ui.DrainAsync();
        var done = SideNotice(h, id)!;
        done.Status.Should().Be(AgentItemStatus.Done);
        done.Text.Should().Be("> 你現在在做什麼？\n> 一句話\n\n正在跑測試。");
    }

    [Fact]
    public async Task btw的答案失敗_標成失敗並寫出原因()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(session);
        session.SideAnswer = new TaskCompletionSource<AgentSideAnswer>();
        session.SideAnswer.SetResult(new AgentSideAnswer(null, "Claude 沒有回應（逾時或程序已結束）"));

        h.Send(h.Main, id, "/btw 剛才改了哪些檔案？");
        await h.Ui.DrainAsync();
        var item = SideNotice(h, id)!;
        item.Status.Should().Be(AgentItemStatus.Failed);
        item.Text.Should().EndWith("沒有回答：Claude 沒有回應（逾時或程序已結束）");
        h.IsRunning(id).Should().BeFalse("旁支提問不開始一輪");
    }

    [Theory]
    [InlineData("/btw", "用法：/btw 你的問題（不打斷這一輪，問答不進對話的上下文）")]
    [InlineData("/btw   ", "用法：/btw 你的問題（不打斷這一輪，問答不進對話的上下文）")]
    public async Task 空的btw_以sendRejected拒絕(string text, string reason)
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(session);
        var mark = h.Main.Mark();
        h.Send(h.Main, id, text);
        var rejected = await h.Main.WaitForAsync("sendRejected", since: mark, conversationId: id);
        rejected.Value<string>("reason").Should().Be(reason);
        session.SideQuestions.All.Should().BeEmpty();
    }

    [Fact]
    public async Task 不是Claude的對話_btw以sendRejected拒絕_指令清單也不列()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex);
        h.CompleteTurn(session);
        var mark = h.Main.Mark();
        h.Send(h.Main, id, "/btw 問一下");
        var rejected = await h.Main.WaitForAsync("sendRejected", since: mark, conversationId: id);
        rejected.Value<string>("reason").Should().Be("只有 Claude 的對話支援 /btw 旁支提問");
        session.SideQuestions.All.Should().BeEmpty();
        session.Sent.All.Should().HaveCount(1, "也不當成一般訊息送出");

        // header 訊息在清單沒變時省略 commands（HeaderListRules），看整份快照
        Commands(h.Main.Last("conversation", id)!).Should().NotContain("btw").And.Contain("clear");
        var (claudeId, _) = h.StartConversation(h.Main, h.Claude);
        Commands(h.Main.Last("conversation", claudeId)!).Should().Contain("btw").And.Contain("clear");
    }

    private static IEnumerable<string> Commands(JObject message)
        => ((JArray)message["header"]!["commands"]!).Select(c => c.Value<string>("name")!);

    [Fact]
    public void 不是btw的斜線指令_照一般送出()
    {
        AgentHubRules.SideQuestionOf("/btwx 問題").Should().BeNull();
        AgentHubRules.SideQuestionOf("btw 問題").Should().BeNull();
        AgentHubRules.SideQuestionOf("/btw\t問題 ").Should().Be("問題");
        AgentHubRules.SideQuestionOf(" /btw ").Should().Be(string.Empty);
    }

    [Fact]
    public async Task MCP伺服器的問卷_卡片帶來源_代理問的不帶()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        var questions = new List<AgentQuestion> { new() { Id = "name", Header = "Name", Text = "your name" } };
        h.Raise(session, new AgentQuestionEvent("req-1", questions, AnswerAsMessage: false, Detail: "MCP 伺服器「good」要你填寫：", Source: "MCP：good"));
        h.Raise(session, new AgentQuestionEvent("req-2", questions, AnswerAsMessage: false));

        var cards = h.Items(id).Where(i => i.Kind == AgentItemKind.Question).ToList();
        cards.Select(i => i.ToolName).Should().Equal("MCP：good", null);
        await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("toolName") == "MCP：good", conversationId: id);
        var plain = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("kind") == "Question" && m["item"]!["toolName"] == null, conversationId: id);
        plain["item"]!["questions"]!.Should().HaveCount(1, "代理問的省略 toolName（輸出和以前一樣）");
    }
}
