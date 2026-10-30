using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.UI;

namespace Myriagent.Tests.Hub;

/// <summary>中樞提供給桌面外殼的能力：Windows 詢問登出／關機時先存檔、檔案瀏覽器縮圖網址的檔案版本</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubShellTests
{
    [Fact]
    public void SaveRunningConversations_執行中的對話存檔_不停止會話也不結束這一輪()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "幫我整理報告");
        h.Raise(session, new AgentTextDeltaEvent("m:1", "整理到一半的回覆"));
        new ConversationStore(h.Store.RootDirectory).LoadItems(id).Should().ContainSingle("串流中的回覆還沒存檔（只有送出時存的使用者訊息）");

        h.Ui.Invoke(() => h.Hub.SaveRunningConversations());

        var saved = new ConversationStore(h.Store.RootDirectory).LoadItems(id);
        saved.Select(i => i.Kind).Should().Equal(AgentItemKind.User, AgentItemKind.Assistant);
        saved[1].Text.Should().Be("整理到一半的回覆");
        session.Disposed.Should().BeFalse("使用者之後可能取消關機：代理繼續執行");
        h.OnUi(() => h.Hub.IsRunning(id)).Should().BeTrue();
        h.Command(h.Main, "ready");
        h.Main.Last("init").Should().NotBeNull("中樞照常處理網頁指令");
    }

    [Fact]
    public async Task 檔案瀏覽_縮圖網址帶檔案版本_檔案被覆寫後網址改變()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        var chart = h.Temp.WriteFile(@"proj\chart.png", "舊圖");
        File.SetLastWriteTime(chart, new DateTime(2026, 10, 1, 9, 0, 0));
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);

        async Task<string> ThumbUrlAsync(string requestId)
        {
            h.Command(h.Main, "listFiles", new { requestId, conversationId = id, path = (string?)null });
            var files = await h.Main.WaitForAsync("files", m => m.Value<string>("requestId") == requestId);
            return files["entries"]!.Single(e => e.Value<string>("name") == "chart.png").Value<string>("thumbUrl")!;
        }

        var before = await ThumbUrlAsync("r1");
        (await ThumbUrlAsync("r2")).Should().Be(before, "檔案沒變時網址不變（瀏覽器可沿用快取）");

        File.WriteAllText(chart, "代理重新產生的圖");
        File.SetLastWriteTime(chart, new DateTime(2026, 10, 1, 9, 5, 0));
        var after = await ThumbUrlAsync("r3");

        after.Should().NotBe(before, "修正前網址只有路徑與大小，重新整理後 5 分鐘內都顯示快取的舊縮圖");
        AgentWebView.GetQueryValue(new Uri(after), "path").Should().Be(chart);
        AgentWebView.GetQueryValue(new Uri(after), "size").Should().Be(AgentWebView.GetQueryValue(new Uri(before), "size"));
        h.OnUi(() => h.Hub.IsThumbAllowed(chart)).Should().BeTrue();
    }
}
