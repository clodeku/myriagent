using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 回溯（rewindPreview、rewind）：使用者訊息的 CliUuid 在逐字稿找分支點，對話回溯移除那則與之後的項目、記下 ResumeAt（第一則開新的 session）、
/// 文字放回輸入框；檔案以 rewind_files 還原，失敗時對話不動；執行中不做
/// </summary>
public class AgentHubRewindTests
{
    private static string Entry(string type, string uuid, string? parent)
        => "{\"type\":\"" + type + "\",\"uuid\":\"" + uuid + "\",\"parentUuid\":" + (parent == null ? "null" : "\"" + parent + "\"") + "}";

    /// <summary>兩輪的對話與它的逐字稿（使用者訊息的 uuid 是中樞給的）</summary>
    private static (string Id, FakeSession Session, AgentChatItem First, AgentChatItem Second) TwoTurns(HubHarness h, TempDirectory temp)
    {
        var (id, session) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.CompleteTurn(session);
        h.Send(h.Main, id, "第二則：改 a.txt");
        h.CompleteTurn(session);
        var users = h.Items(id).Where(i => i.Kind == AgentItemKind.User).ToList();
        var path = temp.WriteFile("t.jsonl", string.Join("\n",
            Entry("user", users[0].CliUuid!, null), Entry("assistant", "a1", users[0].CliUuid), Entry("user", users[1].CliUuid!, "a1"), Entry("assistant", "a2", users[1].CliUuid)) + "\n");
        session.Transcript = path;
        return (id, session, users[0], users[1]);
    }

    [Fact]
    public async Task 預覽_對話與檔案都回得去()
    {
        using var temp = new TempDirectory("rewind");
        using var h = new HubHarness();
        var (id, session, _, second) = TwoTurns(h, temp);

        h.Command(h.Main, "rewindPreview", new { conversationId = id, itemId = second.Id, requestId = "rw1" });
        var reply = await h.Main.WaitForAsync("rewindPreview", m => m.Value<string>("requestId") == "rw1");
        reply.Value<string>("error").Should().BeNull();
        reply.Value<bool>("conversation").Should().BeTrue();
        reply.Value<int>("fileCount").Should().Be(1);
        reply.Value<int>("insertions").Should().Be(1);
        reply.Value<int>("laterMessages").Should().Be(1);
        reply.Value<string>("filesError").Should().BeNull();
        session.RewindCalls.All.Should().Equal((second.CliUuid!, true));
    }

    [Fact]
    public async Task 回溯對話與檔案_移除之後的項目_下次從分支點啟動_分支開始後清掉()
    {
        using var temp = new TempDirectory("rewind");
        using var h = new HubHarness();
        var (id, session, first, second) = TwoTurns(h, temp);
        var mark = h.Main.Mark();

        h.Command(h.Main, "rewind", new { conversationId = id, itemId = second.Id, files = true, conversation = true });
        var back = await h.Main.WaitForAsync("rewound", null, mark, conversationId: id);
        back.Value<string>("text").Should().Be("第二則：改 a.txt");
        session.RewindCalls.All.Should().Equal((second.CliUuid!, false));
        var items = h.Items(id);
        items.Should().Contain(i => i.Id == first.Id);
        items.Should().NotContain(i => i.Id == second.Id);
        items.Last().Kind.Should().Be(AgentItemKind.Notice);
        items.Last().Text.Should().Contain("已回溯到「第二則：改 a.txt」之前").And.Contain("檔案也還原了");
        h.Conversation(id)!.ResumeAt.Should().Be("a1");
        session.Closes.Should().Be(1, "開著的程序還記得之後的內容：關掉");

        h.Send(h.Main, id, "改走另一條路");
        session.Sent.All[^1].Settings.ResumeAt.Should().Be("a1");
        h.Raise(session, new AgentSessionStartedEvent("forked-session", null, null));
        h.Conversation(id)!.ResumeAt.Should().BeNull("分支開始了：之後照常續接它");
        h.Conversation(id)!.SessionId.Should().Be("forked-session");
    }

    [Fact]
    public async Task 回溯第一則開新的session_只還原檔案時對話不動()
    {
        using var temp = new TempDirectory("rewind");
        using var h = new HubHarness();
        var (id, session, first, second) = TwoTurns(h, temp);
        var before = h.Conversation(id)!.SessionId;

        h.Command(h.Main, "rewind", new { conversationId = id, itemId = second.Id, files = true, conversation = false });
        await h.WaitUntilAsync(() => h.Items(id).Last().Text.Contains("已把檔案還原到"), "只還原檔案");
        h.Items(id).Should().Contain(i => i.Id == second.Id);
        h.Conversation(id)!.ResumeAt.Should().BeNull();

        h.Command(h.Main, "rewind", new { conversationId = id, itemId = first.Id, files = false, conversation = true });
        await h.WaitUntilAsync(() => h.Items(id).All(i => i.Kind != AgentItemKind.User), "回溯第一則");
        var meta = h.Conversation(id)!;
        meta.SessionId.Should().NotBe(before);
        meta.SessionStarted.Should().BeFalse();
        meta.ResumeAt.Should().BeNull();
    }

    [Fact]
    public async Task 檔案還原失敗時對話不動_執行中不回溯()
    {
        using var temp = new TempDirectory("rewind");
        using var h = new HubHarness();
        var (id, session, _, second) = TwoTurns(h, temp);
        session.RewindResult = new AgentRewindResult(false, [], 0, 0, "這則訊息沒有檔案還原點");

        var mark = h.Main.Mark();
        h.Command(h.Main, "rewind", new { conversationId = id, itemId = second.Id, files = true, conversation = true });
        var toast = await h.Main.WaitForAsync("toast", m => m.Value<string>("text")!.Contains("檔案沒有還原"), mark);
        toast.Value<string>("text").Should().Contain("對話也沒有回溯");
        h.Items(id).Should().Contain(i => i.Id == second.Id);
        h.Conversation(id)!.ResumeAt.Should().BeNull();

        h.Send(h.Main, id, "第三則");
        mark = h.Main.Mark();
        h.Command(h.Main, "rewindPreview", new { conversationId = id, itemId = second.Id, requestId = "rw2" });
        var reply = await h.Main.WaitForAsync("rewindPreview", m => m.Value<string>("requestId") == "rw2", mark);
        reply.Value<string>("error").Should().Contain("先按停止");
    }
}
