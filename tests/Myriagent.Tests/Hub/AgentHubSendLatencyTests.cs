using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 送出（與執行中插入）的使用者訊息先上畫面再存檔（AgentHub.FlushNow）：在處理送出的同一個 UI 工作裡就送給網頁，
/// 不等 50 ms 的合併計時器（計時器經執行緒集區才回到 UI 執行緒，集區忙時會拖好幾秒），也不等整份 items.json 重寫並落盤（2026-10-23）。
/// FakeHost 的合併計時器要排回 UI 執行緒，所以在同一個 UI 工作裡看得到的，一定是送出當下就送的
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSendLatencyTests
{
    private static bool IsUserItem(JObject m, string text)
        => m.Value<string>("type") == "item" && m["item"]?.Value<string>("kind") == "User" && m["item"]?.Value<string>("html") == text;

    [Fact]
    public void 送出_使用者訊息與執行中的標頭在同一個UI工作裡就送給網頁_而且早於存檔()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Claude);
        bool? savedWhenPosted = null;
        h.Main.Posting = m =>
        {
            if (IsUserItem(m, "馬上出現") && savedWhenPosted == null)
                savedWhenPosted = h.Store.LoadItems(id).Any(i => i.Text == "馬上出現");
        };
        var mark = h.Main.Mark();
        bool itemPosted = false, runningPosted = false;

        h.Ui.Invoke(() =>
        {
            h.Hub.HandleCommand(h.Main, HubHarness.MakeCommand("send", new { conversationId = id, text = "馬上出現" }));
            itemPosted = h.Main.Messages("item", mark, id).Any(m => IsUserItem(m, "馬上出現"));
            runningPosted = h.Main.Messages("header", mark, id).Any(m => m["header"]!.Value<bool>("running"));
        });

        itemPosted.Should().BeTrue("使用者的訊息在處理送出的同一個 UI 工作裡就送出，不等合併計時器");
        runningPosted.Should().BeTrue("執行中的標頭（停止鈕、狀態）一起送");
        savedWhenPosted.Should().BeFalse("先上畫面再存檔：送到網頁的那一刻紀錄檔還沒寫進這則");
        h.Store.LoadItems(id).Select(i => i.Text).Should().Contain("馬上出現", "之後照樣存檔");
    }

    [Fact]
    public void 執行中插入_插進去的使用者訊息在同一個UI工作裡就送給網頁_而且早於存檔()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        s.CanSteer = true;
        bool? savedWhenPosted = null;
        h.Main.Posting = m =>
        {
            if (IsUserItem(m, "插一句") && savedWhenPosted == null)
                savedWhenPosted = h.Store.LoadItems(id).Any(i => i.Text == "插一句");
        };
        var mark = h.Main.Mark();
        var itemPosted = false;

        h.Ui.Invoke(() =>
        {
            h.Hub.HandleCommand(h.Main, HubHarness.MakeCommand("send", new { conversationId = id, text = "插一句" }));
            itemPosted = h.Main.Messages("item", mark, id).Any(m => IsUserItem(m, "插一句"));
        });

        s.Steered.All.Select(i => i.Text).Should().Equal("插一句");
        itemPosted.Should().BeTrue("插進去就在同一個 UI 工作裡送出使用者訊息");
        savedWhenPosted.Should().BeFalse("先上畫面再存檔");
        h.Store.LoadItems(id).Where(i => i.Kind == AgentItemKind.User).Select(i => i.Text).Should().Equal("整理報表", "插一句");
    }
}
