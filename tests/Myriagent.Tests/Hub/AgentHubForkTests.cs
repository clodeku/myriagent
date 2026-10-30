using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 延伸成新對話（forkConversation）：複製項目與附件、同帳戶同工作資料夾的新對話；Claude 從原對話逐字稿當下的副本 --fork-session 分支
/// （新的 session 開始後刪掉副本），其他代理第一則訊息附前文節錄；原對話照常、執行中也能延伸；草稿不處理
/// </summary>
public class AgentHubForkTests
{
    /// <summary>送 forkConversation、等帶著這個記號的 conversation 回來，回傳新對話的 id</summary>
    private static async Task<string> ForkAsync(HubHarness h, string id, string token = "fork-t1")
    {
        var mark = h.Main.Mark();
        h.Command(h.Main, "forkConversation", new { conversationId = id, paneId = token });
        var reply = await h.Main.WaitForAsync("conversation", m => m.Value<string>("paneId") == token, mark, timeoutMs: 5000);
        var forkId = reply.Value<string>("conversationId")!;
        forkId.Should().NotBe(id);
        (await h.Main.WaitForAsync("focusConversation", m => m.Value<string>("conversationId") == forkId, mark)).Should().NotBeNull();
        return forkId;
    }

    [Fact]
    public async Task Claude_從逐字稿副本分支_新的session開始後刪掉副本()
    {
        using var temp = new TempDirectory("fork");
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "先查登入的 bug");
        h.Raise(session, new AgentTextCompletedEvent("b1", "找到了：逾時太短"));
        h.CompleteTurn(session);
        // 原對話的逐字稿：最後一行寫到一半（副本截到最後一個換行）
        session.Transcript = temp.WriteFile("orig.jsonl", "{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n{\"type\":\"us");
        var original = h.Conversation(id)!;

        var forkId = await ForkAsync(h, id);

        var fork = h.Conversation(forkId)!;
        fork.ForkedFrom.Should().Be(id);
        fork.ForkedFromTitle.Should().Be(original.Title);
        fork.Title.Should().Be(original.Title + "（延伸）");
        // 對話清單：延伸出來的帶來源的 id 與當時的標題（標頭寫「延伸自」），原對話不帶
        var listed = (await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(c => c.Value<string>("id") == forkId)))["items"]!;
        var summary = (JObject)listed.Single(c => c.Value<string>("id") == forkId);
        summary.Value<string>("forkedFrom").Should().Be(id);
        summary.Value<string>("forkedFromTitle").Should().Be(original.Title);
        summary.ContainsKey("parentId").Should().BeFalse();
        ((JObject)listed.Single(c => c.Value<string>("id") == id)).ContainsKey("forkedFrom").Should().BeFalse();
        fork.AccountId.Should().Be(h.Claude.Id);
        fork.SessionStarted.Should().BeTrue("從副本分支：CLI 那邊已有前文");
        fork.SessionId.Should().NotBe(original.SessionId);
        fork.WorkingDirectory.Should().Be(h.Store.GetDefaultWorkingDirectory(id), "原對話用自己的工作區：延伸的對話指到同一個");
        fork.ForkSourceTranscript.Should().StartWith(h.Store.GetConversationDirectory(forkId));
        File.ReadAllText(fork.ForkSourceTranscript!).Should().Be("{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n");

        var items = h.Items(forkId);
        items.Select(i => i.Text).Should().ContainInOrder("先查登入的 bug", "找到了：逾時太短");
        items.Last().NoticeKind.Should().Be(AgentNoticeKind.Fork);
        items.Last().Text.Should().Contain("完整紀錄");
        items.Select(i => i.Id).Should().NotIntersectWith(h.Items(id).Select(i => i.Id));
        h.Store.LoadItems(forkId).Should().HaveCount(items.Count, "建立時就存檔");

        // 新對話的第一則：新的會話、帶副本、不附前文節錄（工作資料夾是指定的：送出前在背景確認它還在）
        var before = h.Sessions.Count;
        h.Send(h.Main, forkId, "改做 B");
        await h.WaitUntilAsync(() => h.Sessions.Count == before + 1 && h.Sessions[^1].Sent.Count == 1, "延伸的對話開自己的會話送出");
        var forkSession = h.Sessions[^1];
        forkSession.Should().NotBeSameAs(session);
        var sent = forkSession.Sent.All.Single();
        sent.Input.Text.Should().Be("改做 B");
        sent.Settings.ForkSource.Should().Be(fork.ForkSourceTranscript);
        sent.Settings.SessionStarted.Should().BeTrue();
        sent.Settings.SessionId.Should().Be(fork.SessionId);

        var snapshot = fork.ForkSourceTranscript!;
        h.Raise(forkSession, new AgentSessionStartedEvent(fork.SessionId!, null, null));
        h.Conversation(forkId)!.ForkSourceTranscript.Should().BeNull("分支好了：之後照常續接自己的 session");
        h.Store.LoadConversations().Single(c => c.Id == forkId).ForkSourceTranscript.Should().BeNull("清掉後存檔");
        await h.WaitUntilAsync(() => !File.Exists(snapshot), "副本刪掉");
        File.Exists(session.Transcript).Should().BeTrue("原對話的逐字稿不動");

        h.Items(id).Should().NotContain(i => i.Text == "改做 B", "兩個對話各自進行");
    }

    [Fact]
    public async Task Codex_第一則訊息附延伸的前文節錄()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex, "整理報表");
        h.Raise(session, new AgentTextCompletedEvent("b1", "已經整理好 A 表"));
        h.CompleteTurn(session);

        var forkId = await ForkAsync(h, id);

        var fork = h.Conversation(forkId)!;
        fork.SessionStarted.Should().BeFalse();
        fork.ForkSourceTranscript.Should().BeNull();
        fork.Provider.Should().Be(AgentProvider.Codex);
        h.Items(forkId).Last().Text.Should().Contain("前文節錄");

        var before = h.Sessions.Count;
        h.Send(h.Main, forkId, "接著做 B 表");
        await h.WaitUntilAsync(() => h.Sessions.Count == before + 1 && h.Sessions[^1].Sent.Count == 1, "延伸的對話開自己的會話送出");
        var sent = h.Sessions[^1].Sent.All.Single();
        sent.Input.Text.Should().StartWith(ConversationForkRules.RecapIntro)
            .And.Contain("【使用者】整理報表").And.Contain("【代理】已經整理好 A 表").And.EndWith("接著做 B 表");
        h.Items(forkId).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("接著做 B 表", "畫面只顯示原文");
    }

    [Fact]
    public async Task 附件資料夾複製過去_路徑改到新對話()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex, "看這張圖");
        h.CompleteTurn(session);
        var dir = h.Store.GetAttachmentDirectory(id);
        Directory.CreateDirectory(Path.Combine(dir, ".kept", "k1"));
        File.WriteAllText(Path.Combine(dir, "a.png"), "png");
        File.WriteAllText(Path.Combine(dir, ".kept", "k1", "s.png"), "kept");
        h.OnUi(() =>
        {
            var user = ((List<AgentChatItem>)h.Hub.GetItems(id)).First(i => i.Kind == AgentItemKind.User);
            user.Attachments.Add(new AgentAttachment { FileName = "a.png", StoredPath = Path.Combine(dir, "a.png"), Kind = AgentAttachmentKind.Image });
            return true;
        });

        var forkId = await ForkAsync(h, id);

        var to = h.Store.GetAttachmentDirectory(forkId);
        File.ReadAllText(Path.Combine(to, "a.png")).Should().Be("png");
        File.ReadAllText(Path.Combine(to, ".kept", "k1", "s.png")).Should().Be("kept");
        h.Items(forkId).First(i => i.Kind == AgentItemKind.User).Attachments.Single().StoredPath.Should().Be(Path.Combine(to, "a.png"));
        h.Items(id).First(i => i.Kind == AgentItemKind.User).Attachments.Single().StoredPath.Should().Be(Path.Combine(dir, "a.png"), "原對話不動");
    }

    [Fact]
    public async Task 原對話執行中也能延伸_原對話照常執行()
    {
        using var temp = new TempDirectory("fork");
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "跑測試");
        h.Raise(session, new AgentTextDeltaEvent("b1", "我先跑"));
        session.Transcript = temp.WriteFile("orig.jsonl", "{\"type\":\"user\"}\n");

        var forkId = await ForkAsync(h, id);

        h.IsRunning(id).Should().BeTrue();
        h.IsRunning(forkId).Should().BeFalse();
        var items = h.Items(forkId);
        items.Single(i => i.Kind == AgentItemKind.Assistant).Status.Should().Be(AgentItemStatus.Cancelled, "進行中的回覆在新對話算已中斷");
        items.Last().Text.Should().Contain("延伸時原對話還在執行");
        h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant).Status.Should().Be(AgentItemStatus.Running, "原對話的項目不動");
    }

    [Fact]
    public async Task 再延伸一次_標題編號遞增_找不到逐字稿的Claude改附前文節錄()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "做 A");
        h.CompleteTurn(session);
        session.Transcript = null;

        var first = await ForkAsync(h, id, "fork-a");
        var fork = h.Conversation(first)!;
        fork.SessionStarted.Should().BeFalse("找不到逐字稿：第一則訊息附前文節錄");
        fork.ForkSourceTranscript.Should().BeNull();

        var second = await ForkAsync(h, first, "fork-b");
        h.Conversation(second)!.Title.Should().EndWith("（延伸 2）");
        h.Conversation(second)!.ForkedFrom.Should().Be(first);
    }

    [Fact]
    public async Task 草稿不延伸()
    {
        using var h = new HubHarness();
        var draft = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();
        h.Command(h.Main, "forkConversation", new { conversationId = draft, paneId = "fork-x" });
        var toast = await h.Main.WaitForAsync("toast", null, mark);
        toast.Value<string>("text").Should().Contain("沒有內容可以延伸");
        h.Main.Messages("conversation", mark).Should().NotContain(m => m.Value<string>("paneId") == "fork-x");
    }
}
