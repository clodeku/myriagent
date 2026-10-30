using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞的渲染、檔案操作與存檔（審查修正的回歸測試）：渲染失敗不擋住整個對話、檔案抽屜與開檔不偷偷重建資料夾也不卡 UI 執行緒、
/// 刪除唯讀檔、設定與對話資料檔讀不到或損毀時的提示與還原、等待回應的判定、節流計時、維護存檔只存有變動的對話
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRenderStoreTests
{
    /// <summary>Markdig 的巢狀深度上限是 128 層，超過就丟 ArgumentException</summary>
    private static readonly string TooDeeplyNested = new string('>', 130) + " 引用";

    // ───── 渲染失敗不擋住整個對話 ─────

    [Fact]
    public void 巢狀過深的Markdown_開啟對話與往上載入仍送出_該筆改用純文字顯示()
    {
        AgentConversation? meta = null;
        AgentChatItem? bad = null;
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "有壞內容的對話" };
            store.SaveConversation(meta);
            bad = new AgentChatItem { Kind = AgentItemKind.Assistant, Text = TooDeeplyNested };
            var items = new List<AgentChatItem> { bad, new() { Kind = AgentItemKind.Assistant, Text = "**正常**的一段" } };
            items.AddRange(Enumerable.Range(1, AgentHub.PageSize).Select(i => new AgentChatItem { Kind = AgentItemKind.User, Text = $"第 {i} 則" }));
            items.Insert(AgentHub.PageSize, new AgentChatItem { Kind = AgentItemKind.Assistant, Text = TooDeeplyNested });
            store.SaveItems(meta.Id, items);
        });
        FluentActions.Invoking(() => AgentMarkdownRenderer.ToHtml(TooDeeplyNested)).Should().Throw<ArgumentException>("前提：Markdig 會對這段內容丟例外");

        h.Command(h.Main, "open", new { conversationId = meta!.Id });

        var conversation = h.Main.Last("conversation", meta.Id);
        conversation.Should().NotBeNull("一筆壞內容不能讓整個對話打不開");
        h.Main.Messages("toast").Should().BeEmpty();
        var shown = conversation!["items"]!.Single(i => i.Value<string>("kind") == "Assistant");
        shown.Value<string>("html").Should().Contain("&gt;&gt;&gt;").And.Contain("引用", "內容改用純文字照樣看得到");

        h.Command(h.Main, "loadOlder", new { conversationId = meta.Id, beforeId = conversation["items"]![0]!.Value<string>("id") });

        var older = h.Main.Last("olderItems", meta.Id);
        older.Should().NotBeNull("往上載入到壞內容所在的那一頁也要送出");
        older!["items"]!.Select(i => i.Value<string>("id")).Should().Contain(bad!.Id);
        older["items"]!.Single(i => i.Value<string>("id") == bad.Id).Value<string>("html").Should().Contain("&gt;");
        older["items"]!.Single(i => i.Value<string>("html")!.Contains("正常")).Value<string>("html").Should().Contain("<strong>正常</strong>", "其他項目照常轉 Markdown");
    }

    [Fact]
    public async Task 巢狀過深的Markdown_串流中照樣送出項目更新()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "引用一段");
        var mark = h.Main.Mark();

        h.Raise(s, new AgentTextDeltaEvent("m:0", TooDeeplyNested));
        h.Raise(s, new AgentTextCompletedEvent("m:0", TooDeeplyNested));

        var item = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("kind") == "Assistant" && m["item"]!.Value<string>("status") == "Done", mark, conversationId: id);
        item["item"]!.Value<string>("html").Should().Contain("引用");
    }

    // ───── 媒體白名單 ─────

    [Fact]
    public async Task 媒體白名單_回覆中引用的非媒體檔不登記_移除附件後撤銷()
    {
        using var h = new HubHarness();
        var pdf = h.Temp.WriteFile(@"docs\報告.pdf");
        var png = h.Temp.WriteFile(@"out\圖.png");
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理");

        h.Raise(s, new AgentTextCompletedEvent("m:0", $"![報告]({pdf.Replace('\\', '/')}) ![圖]({png.Replace('\\', '/')})"));

        await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("html")!.Contains("<img"), conversationId: id);
        h.OnUi(() => h.Hub.IsMediaAllowed(png)).Should().BeTrue();
        h.OnUi(() => h.Hub.IsMediaAllowed(pdf)).Should().BeFalse();
        h.OnUi(() => h.Hub.IsThumbAllowed(pdf)).Should().BeFalse("回覆中引用的非媒體檔不登記，/thumb 不替任意檔案產生外殼縮圖");

        var file = h.Temp.WriteFile(@"in\截圖.png", "png");
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { file });
        var composer = await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: id);
        var attachment = composer["composer"]!["attachments"]![0]!;
        var stored = attachment.Value<string>("path")!;
        h.OnUi(() => h.Hub.IsThumbAllowed(stored)).Should().BeTrue();

        h.Command(h.Main, "removeAttachment", new { conversationId = id, id = attachment.Value<string>("id") });

        h.OnUi(() => h.Hub.IsThumbAllowed(stored)).Should().BeFalse("移除的附件不再經 /thumb、/media 提供");
        h.OnUi(() => h.Hub.IsMediaAllowed(stored)).Should().BeFalse();
    }

    // ───── 維護計時器的存檔 ─────

    [Fact]
    public void 維護計時器_只存項目有變動的執行中對話()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑很久的工作");
        var itemsPath = Path.Combine(h.Store.GetConversationDirectory(id), "items.json");
        File.Exists(itemsPath).Should().BeTrue("送出時已存檔");
        File.Delete(itemsPath);

        // 維護的紀錄在背景寫（ItemsFileWriter）：每次維護之後等背景寫完再看檔案
        void Maintain()
        {
            h.Ui.Invoke(() => h.Hub.RunMaintenance());
            h.OnUi(() => h.Hub.ItemsWriterForTests)?.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        }

        Maintain();
        File.Exists(itemsPath).Should().BeFalse("沒有變動（例如在等核准）就不整份重寫：長對話每次要上百毫秒");

        h.Raise(s, new AgentTextDeltaEvent("m:0", "進度回報"));
        Maintain();
        new ConversationStore(h.Store.RootDirectory).LoadItems(id).Last().Text.Should().Be("進度回報", "有變動才存");

        File.Delete(itemsPath);
        Maintain();
        File.Exists(itemsPath).Should().BeFalse("存過之後沒有新的變動");
    }

    // ───── 等待你回應的判定 ─────

    [Fact]
    public async Task 非同步提問_這一輪結束後清單帳戶卡與系統匣都算等待_略過後清掉()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "先問一題");
        h.Raise(s, new AgentQuestionEvent("async:m1", new[] { new AgentQuestion { Id = "m1:0", Text = "要哪種格式？" } }, AnswerAsMessage: true));
        var mark = h.Main.Mark();

        h.CompleteTurn(s);

        bool ListWaiting(JObject m) => m["items"]!.Single(i => i.Value<string>("id") == id).Value<bool>("waiting");
        int AccountWaiting(JObject m) => m["accounts"]!.Single(a => a.Value<string>("id") == h.Codex.Id)["sessions"]!.Value<int>("waiting");
        await h.Main.WaitForAsync("conversations", ListWaiting, mark);
        await h.Main.WaitForAsync("accounts", m => AccountWaiting(m) == 1, mark);
        h.OnUi(() => h.Hub.WaitingCount).Should().Be(1);
        h.Host.Tray.All.Last().Waiting.Should().Be(1, "網頁分頁顯示「!」，側欄、儀表板與系統匣也要算它在等你回應");

        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        mark = h.Main.Mark();
        h.Command(h.Main, "skipQuestion", new { conversationId = id, itemId = question.Id });

        await h.Main.WaitForAsync("conversations", m => !ListWaiting(m), mark);
        await h.Main.WaitForAsync("accounts", m => AccountWaiting(m) == 0, mark);
        h.OnUi(() => h.Hub.WaitingCount).Should().Be(0);
    }

    // ───── 帳戶卡片節流 ─────

    [Theory]
    [InlineData(null, 5_000L, 0)]
    [InlineData(1_000L, 1_200L, 300)]
    [InlineData(1_000L, 1_500L, 0)]
    [InlineData(1_000L, 9_000_000_000L, 0)]
    [InlineData(1_000L, 900L, 500)]
    public void 帳戶卡片節流_等待時間一定在0到500毫秒之間(long? last, long now, int expectedMs)
        => AgentHub.AccountsFlushWait(last, now).Should().Be(TimeSpan.FromMilliseconds(expectedMs),
            "原本用 DateTime.Now 計時，系統時間往回調一小時就會排一個一小時的等待，帳戶卡整段時間不更新");

    // ───── 啟動時讀不到或損毀的檔案：介面就緒後提示 ─────

    [Fact]
    public void 設定檔讀不到與對話資料檔損毀_主視窗就緒時各提示一次_彈出視窗不提示()
    {
        string? recoveredId = null;
        using var h = new HubHarness(
            store =>
            {
                var meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "原本的標題" };
                store.SaveConversation(meta);
                store.SaveItems(meta.Id, new[] { new AgentChatItem { Kind = AgentItemKind.User, Text = "查一下報表" } });
                File.WriteAllText(Path.Combine(store.GetConversationDirectory(meta.Id), "meta.json"), "{壞掉");
                recoveredId = meta.Id;
            },
            loadSettings: () =>
            {
                File.WriteAllText(AppPaths.SettingsFile, """{ "Theme": "light" }""");
                using var locked = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return AppSettings.Load();
            });
        var popout = h.AddPopout("pop", recoveredId!);

        h.Command(popout, "ready");
        h.Command(h.Main, "ready");
        h.Command(h.Main, "ready");

        popout.Messages("toast").Should().BeEmpty();
        var toasts = h.Main.Messages("toast").Select(t => t.Value<string>("text")).ToList();
        toasts.Should().HaveCount(2, "每個提示只在第一次就緒時顯示");
        toasts.Should().Contain(t => t!.Contains("設定檔暫時讀不到"));
        toasts.Should().Contain(t => t!.Contains("已從對話紀錄還原"));
        h.Main.Last("init")!["conversations"]!.Single().Value<string>("title").Should().Be("查一下報表", "損毀的對話還在清單上");
        h.Main.Last("init")!["settings"]!.Value<string>("theme").Should().Be("dark");
    }

    // ───── 檔案抽屜與開資料夾：不偷偷重建、失敗要回應 ─────

    /// <summary>這台電腦沒有的磁碟代號（不碰檔案系統，只看邏輯磁碟清單）</summary>
    private static string MissingDriveFolder()
    {
        var present = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Reverse().First(l => !present.Contains(l));
        return $@"{letter}:\不存在的專案";
    }

    [Fact]
    public async Task 檔案抽屜_自訂資料夾被刪掉時回找不到_不偷偷重建_送出照樣被擋()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("搬走的專案");
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);
        Directory.Delete(folder);

        h.Command(h.Main, "listFiles", new { requestId = "r1", conversationId = id, path = (string?)null });

        var files = await h.Main.WaitForAsync("files", m => m.Value<string>("requestId") == "r1");
        files.Value<string>("error").Should().Contain("找不到資料夾");
        files["entries"]!.Should().BeEmpty();
        Directory.Exists(folder).Should().BeFalse("使用者選的資料夾被刪掉時不偷偷重建");

        var mark = h.Main.Mark();
        h.Send(h.Main, id, "繼續");
        await h.Main.WaitForAsync("sendRejected", since: mark, conversationId: id);
        h.Sessions.Should().BeEmpty("資料夾不存在，代理不能在重建出來的空資料夾裡工作");
    }

    [Fact]
    public async Task 檔案抽屜_工作資料夾的磁碟不在時回錯誤_不會沒有回應()
    {
        var missing = MissingDriveFolder();
        AgentConversation? meta = null;
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "隨身碟上的專案", WorkingDirectory = missing };
            store.SaveConversation(meta);
        });
        h.Command(h.Main, "open", new { conversationId = meta!.Id });

        h.Command(h.Main, "listFiles", new { requestId = "r1", conversationId = meta.Id, path = (string?)null });

        var files = await h.Main.WaitForAsync("files", m => m.Value<string>("requestId") == "r1");
        files.Value<string>("error").Should().NotBeNullOrWhiteSpace("磁碟不在也要回 files，抽屜才不會一直轉圈");
        files.Value<string>("root").Should().Be(missing);
    }

    [Fact]
    public async Task 檔案抽屜_預設工作資料夾不存在時照常建立()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var workspace = h.Store.GetDefaultWorkingDirectory(id);
        Directory.Delete(workspace, recursive: true);

        h.Command(h.Main, "listFiles", new { requestId = "r1", conversationId = id, path = (string?)null });

        var files = await h.Main.WaitForAsync("files", m => m.Value<string>("requestId") == "r1");
        files.Value<string>("error").Should().BeNull();
        Directory.Exists(workspace).Should().BeTrue("對話自己的預設 workspace 可以建立");
    }

    [Fact]
    public async Task 在檔案總管開啟_自訂資料夾被刪掉時提示_不重建_附件與預設資料夾照常建立()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("刪掉的專案");
        var plain = h.NewConversation(h.Main, h.Claude);
        var custom = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, custom, folder);
        Directory.Delete(folder);
        var mark = h.Main.Mark();

        h.Command(h.Main, "openFolder", new { conversationId = custom, which = "working" });
        h.Command(h.Main, "openFolder", new { conversationId = plain, which = "working" });
        h.Command(h.Main, "openFolder", new { conversationId = plain, which = "attachments" });

        await h.WaitUntilAsync(() => h.Host.Revealed.Count == 2, "預設資料夾與附件資料夾建立後在檔案總管開啟");
        (await h.Main.WaitForAsync("toast", m => m.Value<string>("text")!.Contains("找不到資料夾"), mark)).Value<string>("level").Should().Be("error");
        Directory.Exists(folder).Should().BeFalse("使用者選的資料夾被刪掉時不偷偷重建");
        h.Host.Revealed.All.Should().BeEquivalentTo(new[] { h.Store.GetDefaultWorkingDirectory(plain), h.Store.GetAttachmentDirectory(plain) });
    }

    [Fact]
    public async Task 開檔與在檔案總管選取_找不到時提示()
    {
        using var h = new HubHarness();
        var missing = h.Temp.Combine("沒有", "這個檔案.pdf");

        h.Command(h.Main, "openPath", new { path = missing });
        h.Command(h.Main, "revealPath", new { path = missing });

        await h.Main.WaitForAsync("toast", m => m.Value<string>("text") == $"找不到檔案：{missing}");
        await h.Main.WaitForAsync("toast", m => m.Value<string>("text") == $"找不到：{missing}");
        h.Host.Opened.Count.Should().Be(0);
        h.Host.Revealed.Count.Should().Be(0);
    }
}
