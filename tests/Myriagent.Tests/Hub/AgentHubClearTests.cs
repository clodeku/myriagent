using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// /clear（AgentHubRules.IsClearCommand）：Claude 已開始的 session 交給 CLI、這一輪正常結束才加清除提示；其他代理由中樞換新的 session、不送給代理；
/// 執行中不插入也不排隊；草稿不做事。項目不刪，之後的前文節錄只收清除提示之後的
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubClearTests
{
    private static bool IsClearNotice(AgentChatItem item) => item.Kind == AgentItemKind.Notice && item.NoticeKind == AgentNoticeKind.Clear;

    [Fact]
    public void Codex閒置時清除_不送給代理_關掉程序換新session_加清除提示_之後的訊息不附前文節錄()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "整理報表");
        h.Raise(s, new AgentTextCompletedEvent("b1", "報表整理好了"));
        h.CompleteTurn(s);
        var itemsBefore = h.Items(id).Count;

        h.Send(h.Main, id, " /clear ");

        s.Sent.Count.Should().Be(1, "Codex 沒有 /clear：送出去只會變成給模型的一句話");
        s.Disposed.Should().BeTrue("舊的 thread 不再用");
        var meta = h.Conversation(id)!;
        meta.SessionStarted.Should().BeFalse();
        meta.SessionId.Should().BeNull("Codex 的 thread id 由下一次 thread/start 決定");
        var items = h.Items(id);
        items.Count.Should().Be(itemsBefore + 1, "項目不刪，只加一則提示");
        items[^1].Should().Match<AgentChatItem>(i => IsClearNotice(i) && i.Text == AgentHubRules.ClearNoticeText);
        items.Should().NotContain(i => i.Kind == AgentItemKind.User && i.Text.Contains("/clear"), "指令本身不顯示成使用者訊息");
        h.Store.LoadItems(id).Should().Contain(i => IsClearNotice(i), "存檔");
        h.IsRunning(id).Should().BeFalse();

        h.Send(h.Main, id, "下一則");

        h.Sessions.Count.Should().Be(2, "下一則開新的會話");
        var (input, settings) = h.Sessions[^1].Sent.All.Single();
        input.Text.Should().Be("下一則", "清除前的內容不能用前文節錄帶回去");
        settings.SessionStarted.Should().BeFalse();
        settings.HistoryRecap.Should().BeNull();
    }

    [Fact]
    public void Claude還沒開始的session_由中樞清除_不送給CLI_換新的session_id()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表", started: false);
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "程序起不來");
        var oldSessionId = h.Conversation(id)!.SessionId;

        h.Send(h.Main, id, "/clear");

        s.Sent.Count.Should().Be(1, "CLI 那邊沒有要清的，送出去還會被接上前文節錄");
        var meta = h.Conversation(id)!;
        meta.SessionStarted.Should().BeFalse();
        Guid.TryParse(meta.SessionId, out _).Should().BeTrue("Claude 的 session id 由 app 預先產生");
        meta.SessionId.Should().NotBe(oldSessionId);
        h.Items(id)[^1].Should().Match<AgentChatItem>(i => IsClearNotice(i));
    }

    [Fact]
    public void Claude已開始的session_清除照常送給CLI_不帶附件_正常結束才加清除提示_不通知完成()
    {
        using var h = new HubHarness();
        h.Host.IsForeground = false;
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);
        var notified = h.Host.Notifications.Count;

        h.Send(h.Main, id, "/clear");

        s.Sent.Count.Should().Be(2, "CLI 自己換新的 session，程序與 Remote Control 照舊開著");
        s.Sent.All[1].Input.Text.Should().Be("/clear");
        s.Sent.All[1].Input.Attachments.Should().BeEmpty();
        s.Disposed.Should().BeFalse();
        h.Items(id).Should().NotContain(i => IsClearNotice(i), "還不知道 CLI 有沒有清掉");

        h.Raise(s, new AgentSessionStartedEvent("cleared-session", null, null));
        h.CompleteTurn(s);

        h.Items(id)[^1].Should().Match<AgentChatItem>(i => IsClearNotice(i));
        h.Conversation(id)!.SessionId.Should().Be("cleared-session", "CLI 回報的新 session，之後續接它");
        h.Host.Notifications.Count.Should().Be(notified, "清除不是代理的回覆");

        // 一般的一輪照常通知、不加清除提示
        h.Send(h.Main, id, "下一則");
        h.CompleteTurn(s);
        h.Items(id).Count(i => IsClearNotice(i)).Should().Be(1);
        h.Host.Notifications.Count.Should().Be(notified + 1);
    }

    [Fact]
    public void Claude清除那一輪失敗或被停止_不加清除提示()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);

        h.Send(h.Main, id, "/clear");
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "連線中斷");
        h.Send(h.Main, id, "/clear");
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        h.Items(id).Should().NotContain(i => IsClearNotice(i));
    }

    [Fact]
    public void 執行中送出clear_不插入不排隊_字還回輸入框()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        s.CanSteer = true;
        var mark = h.Main.Mark();

        h.Send(h.Main, id, "/clear");

        s.SteerAttempts.Should().Be(0);
        h.Queue(id).Should().BeEmpty();
        h.Main.Messages("sendRejected", mark, id).Should().ContainSingle().Which.Value<string>("text").Should().Be("/clear");
        h.Main.Messages("toast", mark).Should().Contain(m => m.Value<string>("text")!.Contains("執行中不能清除"));
        h.IsRunning(id).Should().BeTrue();
        h.Items(id).Should().NotContain(i => IsClearNotice(i));
    }

    [Fact]
    public void 草稿送出clear_不建立對話也不開程序()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Codex);
        var mark = h.Main.Mark();

        h.Send(h.Main, id, "/clear");

        h.Sessions.Should().BeEmpty();
        h.Store.LoadConversations().Should().NotContain(c => c.Id == id, "草稿沒有變成對話");
        h.Main.Messages("toast", mark).Should().Contain(m => m.Value<string>("text") == "這個對話還沒有內容，不需要清除");
    }

    [Fact]
    public void 輸入區的指令清單_沒有clear的代理也列出clear()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Codex);

        var header = h.Main.Last("conversation", id)!["header"]!;

        header["commands"]!.Select(c => c.Value<string>("name")).Should().ContainSingle(n => n == "clear");
    }
}
