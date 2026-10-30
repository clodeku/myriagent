using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 強制停止背景工作（taskStop）：標頭的 background.tasks 帶 stopId；只認目前回報的背景工作，帶 stopId 停一個、不帶是全部，停了在對話加提示；
/// 停不了只提示送指令的視窗；使用者停掉的 Codex 背景指令結束時不接續
/// </summary>
public class AgentHubTaskStopTests
{
    private static readonly AgentBackgroundTool Dev = new("執行指令", "bash", "npm run dev", null, TaskId: "b1", StopId: "b1");
    private static readonly AgentBackgroundTool Agent = new("子代理", "agent", "整理", null, StopId: "a1");

    private static async Task<(HubHarness H, string Id, FakeSession S)> WaitingAsync()
    {
        var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        session.HasLiveBackgroundWork = true;
        h.Raise(session, new AgentBackgroundWorkEvent(3, [Dev, Agent]));
        h.CompleteTurn(session);
        await h.Main.WaitForAsync("header", m => m["header"]?["background"] is JObject, conversationId: id);
        return (h, id, session);
    }

    private static List<AgentChatItem> StopNotices(HubHarness h, string id)
        => h.Items(id).Where(i => i.Kind == AgentItemKind.Notice && i.NoticeKind == AgentNoticeKind.BackgroundTask).ToList();

    [Fact]
    public async Task 帶stopId停一個_不帶是全部_停了在對話加提示_不是目前的背景工作不停()
    {
        var (h, id, session) = await WaitingAsync();
        using var _ = h;
        var tasks = h.Main.Messages("header").Last(m => m["header"]?["background"] is JObject)["header"]!["background"]!["tasks"]!.Cast<JObject>().ToList();
        tasks.Select(t => t.Value<string>("stopId")).Should().Equal("b1", "a1");

        var mark = h.Main.Mark();
        h.Command(h.Main, "taskStop", new { conversationId = id, stopId = "zzz" });
        (await h.Main.WaitForAsync("toast", null, mark)).Value<string>("text").Should().Contain("已經結束", "網頁不能指定目前沒有回報的 id");
        session.StopRequests.All.Should().BeEmpty();

        h.Command(h.Main, "taskStop", new { conversationId = id, stopId = "b1" });
        await h.Ui.DrainAsync();
        session.StopRequests.All.Should().Equal("b1");
        StopNotices(h, id).Single().Text.Should().Be("已強制停止背景工作：執行指令：npm run dev（代理不會自動接著處理）。");

        h.Command(h.Main, "taskStop", new { conversationId = id });
        await h.Ui.DrainAsync();
        session.StopRequests.All.Should().Equal("b1", "*");
        StopNotices(h, id).Last().Text.Should().Be("已強制停止 3 個背景工作：執行指令：npm run dev、子代理：整理 等（代理不會自動接著處理）。",
            "全部：數量照標頭的 count");
        session.Sent.Count.Should().Be(1, "停止不叫代理接著處理");
    }

    [Fact]
    public async Task 停不了_只提示送指令的視窗_不加提示_沒有背景工作時不問會話()
    {
        var (h, id, session) = await WaitingAsync();
        using var _ = h;
        var other = h.AddView("w2");
        session.StopResult = new AgentBackgroundStopResult(0, "代理還在處理這一輪：先按停止，再停止背景工作");

        var mark = h.Main.Mark();
        h.Command(h.Main, "taskStop", new { conversationId = id });
        (await h.Main.WaitForAsync("toast", null, mark)).Value<string>("text").Should().Be("沒有停止背景工作：代理還在處理這一輪：先按停止，再停止背景工作");
        other.Messages("toast").Should().BeEmpty();
        StopNotices(h, id).Should().BeEmpty();

        session.HasLiveBackgroundWork = false;
        h.Raise(session, new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
        mark = h.Main.Mark();
        h.Command(h.Main, "taskStop", new { conversationId = id });
        (await h.Main.WaitForAsync("toast", null, mark)).Value<string>("text").Should().Contain("已經結束");
        session.StopRequests.All.Should().Equal("*");
    }

    [Fact]
    public void Codex使用者停掉的背景指令結束_卡片照會話的結果_不接續()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "跑開發伺服器");
        h.Raise(s, new AgentToolStartedEvent("bg1", "執行指令", "bash", "npm run dev", "npm run dev"));
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(1, [new AgentBackgroundTool("執行指令", "bash", "npm run dev", "npm run dev", StopId: "bg1")], ["bg1"]));
        h.CompleteTurn(s);

        h.Raise(s, new AgentToolCompletedEvent("bg1", AgentItemStatus.Cancelled, CodexAppServerRules.StoppedByUserOutput, Array.Empty<string>()));
        s.HasLiveBackgroundWork = false;
        h.Raise(s, new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
        h.Raise(s, new AgentBackgroundCommandFinishedEvent("bg1", "npm run dev", AgentItemStatus.Cancelled, -1, TimeSpan.FromSeconds(3), StoppedByUser: true));

        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool && i.Title == "npm run dev");
        card.Status.Should().Be(AgentItemStatus.Cancelled);
        card.Output.Should().Be(CodexAppServerRules.StoppedByUserOutput);
        s.Sent.Count.Should().Be(1, "使用者停掉的：不隱藏送出接續訊息（自己跑完的才接續）");
        h.IsRunning(id).Should().BeFalse();
    }
}
