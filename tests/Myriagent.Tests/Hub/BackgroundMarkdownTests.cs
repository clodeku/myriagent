using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Rendering;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 很長的執行中回覆在背景執行緒轉 Markdown（BackgroundMarkdownRules、BackgroundMarkdown）：輸出和 UI 執行緒照常轉的逐位元組相同、
/// 白名單照樣登記，同一筆只轉最新的、作廢的不送、轉換失敗交回 UI 執行緒；中樞串流時送出的就是背景轉好的
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class BackgroundMarkdownTests
{
    [Fact]
    public void Eligible_只有執行中_本機_走Markdown_超過門檻的()
    {
        var n = BackgroundMarkdownRules.MinChars + 1;
        BackgroundMarkdownRules.Eligible(AgentItemKind.Assistant, running: true, replica: false, n).Should().BeTrue();
        BackgroundMarkdownRules.Eligible(AgentItemKind.Thinking, true, false, n).Should().BeTrue();
        BackgroundMarkdownRules.Eligible(AgentItemKind.Assistant, false, false, n).Should().BeFalse("結束的照常轉：最後的內容馬上送");
        BackgroundMarkdownRules.Eligible(AgentItemKind.Assistant, true, true, n).Should().BeFalse("複本用另一個渲染器");
        BackgroundMarkdownRules.Eligible(AgentItemKind.Assistant, true, false, BackgroundMarkdownRules.MinChars).Should().BeFalse("短的轉一次不到 0.5 ms");
        BackgroundMarkdownRules.Eligible(AgentItemKind.User, true, false, n).Should().BeFalse("使用者訊息是純文字");
        BackgroundMarkdownRules.Eligible(AgentItemKind.Tool, true, false, n).Should().BeFalse("工具卡的 html 是空的");
    }

    [Fact]
    public void Next_有轉好的就送_稍早的照送並留著_文字被換掉或沒有就排_在轉就等_失敗交回UI執行緒()
    {
        BackgroundMarkdownRules.Next("abc", failed: false, current: "abc", pending: false).Should().Be(BackgroundMarkdownRules.Step.Post);
        BackgroundMarkdownRules.Next("abc", false, "abcdef", false).Should().Be(BackgroundMarkdownRules.Step.PostAndKeepDirty);
        BackgroundMarkdownRules.Next("abc", false, "abcdef", true).Should().Be(BackgroundMarkdownRules.Step.PostAndKeepDirty, "已經在轉下一份也照送稍早的");
        BackgroundMarkdownRules.Next("abc", false, "xyz", false).Should().Be(BackgroundMarkdownRules.Step.Request, "CLI 重開後從頭重寫：舊的不送");
        BackgroundMarkdownRules.Next("abc", false, "xyz", true).Should().Be(BackgroundMarkdownRules.Step.Wait);
        BackgroundMarkdownRules.Next(null, false, "abc", false).Should().Be(BackgroundMarkdownRules.Step.Request);
        BackgroundMarkdownRules.Next(null, false, "abc", true).Should().Be(BackgroundMarkdownRules.Step.Wait);
        BackgroundMarkdownRules.Next("abc", true, "abc", false).Should().Be(BackgroundMarkdownRules.Step.RenderNow);
    }

    /// <summary>回覆裡有本機圖片、本機檔案連結、相對路徑、行內程式碼的路徑、外部圖片、表格、程式碼區塊，拉到超過門檻</summary>
    private static string LongMarkdown(string dir)
    {
        var block = string.Join("\n",
            "## 結果",
            $"截圖：![畫面]({Path.Combine(dir, "shot.png")})，記錄檔 [run.log]({Path.Combine(dir, "run.log")})，相對路徑 [設定](config/app.json)。",
            $"改了 `{Path.Combine(dir, "src", "Main.cs")}` 和 docs/readme.md，外部圖片 ![x](https://example.test/a.png)。",
            "| 欄位 | 說明 |",
            "|---|---|",
            "| a | 第一欄 |",
            "```cs",
            "var x = Render(\"<b>\");",
            "```",
            "- 清單 ~~刪除~~ **粗體** 3~5 天 O(n^2) C++20",
            string.Empty);
        var text = string.Empty;
        while (text.Length <= BackgroundMarkdownRules.MinChars)
            text += block;
        return text;
    }

    [Fact]
    public void 背景轉出的HTML和UI執行緒照常轉的逐位元組相同_白名單登記也一樣()
    {
        using var temp = new TempDirectory("bg-markdown");
        var text = LongMarkdown(temp.Root);
        var item = new AgentChatItem { Id = "a1", Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = text };

        var syncUrls = new LocalMediaUrls();
        var sync = new ItemRenderer(syncUrls).Render(item, temp.Root);

        var result = BackgroundMarkdown.Render(text, temp.Root);
        result.Markdown.Should().NotBeNull();
        result.Links.Should().NotBeEmpty("有本機檔案連結");
        result.Markdown!.Images.Should().NotBeEmpty("有本機圖片");
        var bgUrls = new LocalMediaUrls();
        foreach (var link in result.Links)
            bgUrls.AllowMarkdownLink(link);
        var background = new ItemRenderer(bgUrls).Render(item, temp.Root, result.Markdown);

        background.ToString(Newtonsoft.Json.Formatting.None).Should().Be(sync.ToString(Newtonsoft.Json.Formatting.None), "整筆項目逐位元組相同");
        foreach (var path in new[] { Path.Combine(temp.Root, "shot.png"), Path.Combine(temp.Root, "run.log"), Path.Combine(temp.Root, "config", "app.json"), Path.Combine(temp.Root, "src", "Main.cs") })
        {
            bgUrls.IsMediaAllowed(path).Should().Be(syncUrls.IsMediaAllowed(path), path);
            bgUrls.IsSaveAllowed(path).Should().Be(syncUrls.IsSaveAllowed(path), path);
        }
        syncUrls.IsMediaAllowed(Path.Combine(temp.Root, "shot.png")).Should().BeTrue("對照組：圖片有登記");
        syncUrls.IsSaveAllowed(Path.Combine(temp.Root, "run.log")).Should().BeTrue("對照組：連結有登記");
    }

    [Fact]
    public void 轉換失敗回報失敗_交回UI執行緒照常轉()
    {
        var result = BackgroundMarkdown.Render(new string('>', 200) + " 巢狀過深", null);
        result.Markdown.Should().BeNull();
        result.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task 在背景執行緒轉_同一筆只轉最新的_作廢的不送()
    {
        var ready = new List<string>();
        using var worker = new BackgroundMarkdown((conv, item) => { lock (ready) ready.Add(conv + "/" + item); });
        var caller = Environment.CurrentManagedThreadId;
        var big = new string('長', 200_000);
        // 第一份夠長（轉的時候後兩份進來）：同一筆排著的被換成最新的，最多轉兩份
        worker.Request("c1", "i1", big, null);
        worker.Request("c1", "i1", big + "二", null);
        worker.Request("c1", "i1", big + "三", null);
        await WaitAsync(() => !worker.IsPending("c1", "i1"));
        worker.WorkerThreadId.Should().NotBeNull().And.NotBe(caller);
        worker.Rendered.Should().BeLessThanOrEqualTo(2, "排著還沒開始轉的那份被換掉");
        var last = worker.Take("c1", "i1");
        last.Should().NotBeNull();
        last!.Text.Should().EndWith("三", "留下的是最新的");
        worker.Take("c1", "i1").Should().BeNull("取走就沒了");

        // 作廢：排著的、正在轉的都不送
        lock (ready) ready.Clear();
        worker.Request("c1", "i2", big, null);
        worker.Forget("c1", "i2");
        await WaitAsync(() => !worker.IsPending("c1", "i2"));
        await Task.Delay(100);
        worker.Take("c1", "i2").Should().BeNull("作廢的不留");
        lock (ready) ready.Should().NotContain("c1/i2");
    }

    [Fact]
    public async Task 中樞_長回覆串流時送背景轉好的_和照常轉的相同_不在UI執行緒轉_結束時照常送最後的內容()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        var line = "第一段說明 **粗體** `code` 與清單：\n- 項目一\n- 項目二\n\n";
        var text = string.Empty;
        while (text.Length <= BackgroundMarkdownRules.MinChars)
            text += line;
        var mark = h.Main.Mark();
        h.Raise(s, new AgentTextDeltaEvent("b1", text));
        var first = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("kind") == "Assistant", since: mark, timeoutMs: 3000, conversationId: id);
        first.Should().NotBeNull("背景轉好後送出");

        var worker = h.OnUi(() => h.Hub.BackgroundMarkdownForTests);
        worker.Should().NotBeNull("超過門檻的執行中回覆在背景轉");
        worker!.Rendered.Should().BeGreaterThan(0);
        worker.WorkerThreadId.Should().NotBe(h.OnUi(() => Environment.CurrentManagedThreadId), "不在 UI 執行緒轉");

        var item = h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);
        var expected = h.OnUi(() => h.Hub.RenderItem(item, string.Empty))["html"]!.Value<string>();
        first!["item"]!.Value<string>("html").Should().Be(expected, "和照常轉的逐位元組相同");

        // 繼續串流：之後送的照樣是背景轉的、內容跟上
        for (var i = 0; i < 10; i++)
        {
            h.Raise(s, new AgentTextDeltaEvent("b1", "追加" + i));
            await Task.Delay(60);
        }
        await WaitAsync(() => h.Main.Messages("item", mark, id).LastOrDefault()?["item"]?.Value<string>("html")?.Contains("追加9") == true);

        var done = h.Main.Mark();
        h.CompleteTurn(s);
        var final = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("kind") == "Assistant" && m["item"]!.Value<string>("status") != "Running",
            since: done, timeoutMs: 1000, conversationId: id);
        final.Should().NotBeNull("結束時照常轉、馬上送");
        var finalItem = h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);
        final!["item"]!.Value<string>("html").Should().Be(h.OnUi(() => h.Hub.RenderItem(finalItem, string.Empty))["html"]!.Value<string>());
        await Task.Delay(300);
        h.Main.Messages("item", done, id).Last()["item"]!.Value<string>("status").Should().NotBe("Running", "結束之後不會再送背景轉好的稍早內容");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("等不到條件成立");
            await Task.Delay(20);
        }
    }
}
