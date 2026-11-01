using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 回覆的媒體「留一份」（2026-10-25 使用者回報：回覆引用的探針截圖之後被別的程式刪掉，對話裡只剩破圖）：
/// 回覆、工具、代理送來的媒體完成時在背景複製到對話資料夾的 attachments\.kept\，之後畫面、另存新檔、遠端取檔都用副本
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubMediaKeepTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3];

    private static string WritePng(HubHarness h, string relative, byte[]? content = null)
    {
        var path = h.Temp.WriteFile(relative);
        File.WriteAllBytes(path, content ?? Png);
        return path;
    }

    /// <summary>等背景複製做完、回呼回到 UI 執行緒處理完</summary>
    private static void WaitKept(HubHarness h)
    {
        h.OnUi(() => h.Hub.MediaKeeperForTests)!.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        h.OnUi(() => 0);
    }

    private static AgentChatItem Item(HubHarness h, string id, Func<AgentChatItem, bool> match) => h.Items(id).Single(match);

    [Fact]
    public async Task 回覆提到的截圖_留一份_原檔刪掉後畫面照樣顯示副本()
    {
        using var h = new HubHarness();
        h.OnUi(() => { h.Hub.AttachMediaKeeper(); return 0; });
        var shot = WritePng(h, @"repo\tools\ui-probe\out\fx-matrix-turn-dark.png");
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentTextCompletedEvent("m:1", $"改好了。\n\n{shot}\n\n**說明**"));
        WaitKept(h);

        var item = Item(h, id, i => i.Kind == AgentItemKind.Assistant);
        item.MediaPaths.Should().Equal(shot);
        item.KeptMedia.Should().ContainSingle();
        var kept = MediaKeepRules.KeptPath(item.KeptMedia, h.Store.GetConversationDirectory(id), shot)!;
        kept.Should().StartWith(Path.Combine(h.Store.GetAttachmentDirectory(id), ".kept") + Path.DirectorySeparatorChar);
        Path.GetFileName(kept).Should().Be("fx-matrix-turn-dark.png", "檔名照舊");
        File.ReadAllBytes(kept).Should().Equal(Png);

        // 開著的畫面收到換成副本的那一筆（線上的內容沒變，只有這台的網址換了）
        var shown = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("id") == item.Id
            && m["item"]!["media"] is JArray { Count: 1 } media && media[0]!.Value<string>("path") == kept, conversationId: id);
        shown["item"]!["media"]![0]!.Value<string>("name").Should().Be("fx-matrix-turn-dark.png");
        h.OnUi(() => h.Hub.IsMediaAllowed(kept)).Should().BeTrue("副本登記進 /media 白名單");

        // 探針下一次執行把整個 out 刪掉：重新渲染照樣是副本
        File.Delete(shot);
        var media = h.OnUi(() => h.Hub.RenderItemForTests(id, item.Id))["media"]!;
        media[0]!.Value<string>("path").Should().Be(kept);
        media[0]!.Value<string>("url").Should().Be(AgentMarkdownRenderer.MediaUrl(kept));
    }

    [Fact]
    public void 原檔之後被覆寫成別的內容_這則回覆照樣是當時那一張_下一則回覆留新的()
    {
        using var h = new HubHarness();
        h.OnUi(() => { h.Hub.AttachMediaKeeper(); return 0; });
        var shot = WritePng(h, @"out\shot.png", [1, 1, 1]);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentTextCompletedEvent("m:1", $"第一張：{shot}"));
        WaitKept(h);

        File.WriteAllBytes(shot, [2, 2, 2]);
        h.Raise(s, new AgentTextCompletedEvent("m:2", $"第二張：{shot}"));
        WaitKept(h);

        var replies = h.Items(id).Where(i => i.Kind == AgentItemKind.Assistant).ToList();
        replies.Should().HaveCount(2);
        var dir = h.Store.GetConversationDirectory(id);
        File.ReadAllBytes(MediaKeepRules.KeptPath(replies[0].KeptMedia, dir, shot)!).Should().Equal([1, 1, 1]);
        File.ReadAllBytes(MediaKeepRules.KeptPath(replies[1].KeptMedia, dir, shot)!).Should().Equal([2, 2, 2]);
    }

    [Fact]
    public async Task Markdown引用的本機圖片_也留一份_img網址換成副本()
    {
        using var h = new HubHarness();
        h.OnUi(() => { h.Hub.AttachMediaKeeper(); return 0; });
        var chart = WritePng(h, @"out\chart.png");
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentTextCompletedEvent("m:1", $"![圖表](<{chart.Replace('\\', '/')}>)"));
        WaitKept(h);

        var item = Item(h, id, i => i.Kind == AgentItemKind.Assistant);
        item.MediaPaths.Should().BeEmpty("Markdown 的圖片直接畫在回覆裡，不另外列");
        var kept = MediaKeepRules.KeptPath(item.KeptMedia, h.Store.GetConversationDirectory(id), chart)!;
        File.Exists(kept).Should().BeTrue();

        File.Delete(chart);
        var shown = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("id") == item.Id
            && m["item"]!.Value<string>("html")!.Contains(Uri.EscapeDataString(kept)), conversationId: id);
        shown["item"]!.Value<string>("html").Should().NotContain(Uri.EscapeDataString(chart));
        h.OnUi(() => h.Hub.IsMediaAllowed(kept)).Should().BeTrue();
    }

    [Fact]
    public void 工具與代理送來的媒體_也留一份_影片照留_不能顯示的與對話自己的附件不留()
    {
        using var h = new HubHarness();
        h.OnUi(() => { h.Hub.AttachMediaKeeper(); return 0; });
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var video = WritePng(h, @"out\demo.mp4");
        var toolShot = WritePng(h, @"out\tool.png");
        var own = Path.Combine(h.Store.GetAttachmentDirectory(id), "mine.png");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllBytes(own, Png);

        h.Raise(s, new AgentMediaEvent(video));
        h.Raise(s, new AgentToolStartedEvent("t1", "Bash", "execute", "截圖", null));
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "ok", [toolShot, own]));
        WaitKept(h);

        var dir = h.Store.GetConversationDirectory(id);
        var mediaItem = Item(h, id, i => i.Kind == AgentItemKind.Assistant && i.MediaPaths.Contains(video));
        File.Exists(MediaKeepRules.KeptPath(mediaItem.KeptMedia, dir, video)).Should().BeTrue();
        var tool = Item(h, id, i => i.Kind == AgentItemKind.Tool);
        tool.KeptMedia!.Keys.Should().Equal(toolShot);
        File.Exists(MediaKeepRules.KeptPath(tool.KeptMedia, dir, toolShot)).Should().BeTrue();
    }

    [Fact]
    public void 沒有接上_不留()
    {
        using var h = new HubHarness();
        var shot = WritePng(h, @"out\shot.png");
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentTextCompletedEvent("m:1", shot));

        h.OnUi(() => h.Hub.MediaKeeperForTests).Should().BeNull();
        Item(h, id, i => i.Kind == AgentItemKind.Assistant).KeptMedia.Should().BeNull();
        Directory.Exists(Path.Combine(h.Store.GetAttachmentDirectory(id), MediaKeepRules.FolderName)).Should().BeFalse();
    }

    [Fact]
    public void 一輪結束後才留好_記錄照樣存進items_json_重開也是副本()
    {
        using var h = new HubHarness();
        h.OnUi(() => { h.Hub.AttachMediaKeeper(); return 0; });
        var shot = WritePng(h, @"out\shot.png");
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentTextCompletedEvent("m:1", shot));
        h.CompleteTurn(s);
        WaitKept(h);
        h.OnUi(() => h.Hub.ItemsWriterForTests)?.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var saved = JsonFile.Read<List<AgentChatItem>>(h.Store.GetItemsPath(id))!;
        var reply = saved.Single(i => i.Kind == AgentItemKind.Assistant);
        reply.KeptMedia.Should().ContainKey(shot);
        File.Exists(MediaKeepRules.KeptPath(reply.KeptMedia, h.Store.GetConversationDirectory(id), shot)).Should().BeTrue();
    }

    [Fact]
    public void 副本被刪掉_畫面改回原檔()
    {
        using var h = new HubHarness();
        h.OnUi(() => { h.Hub.AttachMediaKeeper(); return 0; });
        var shot = WritePng(h, @"out\shot.png");
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentTextCompletedEvent("m:1", shot));
        WaitKept(h);
        var item = Item(h, id, i => i.Kind == AgentItemKind.Assistant);
        File.Delete(MediaKeepRules.KeptPath(item.KeptMedia, h.Store.GetConversationDirectory(id), shot)!);

        var rendered = h.OnUi(() => h.Hub.RenderItemForTests(id, item.Id));

        rendered["media"]![0]!.Value<string>("path").Should().Be(shot);
    }

    [Fact]
    public void 刪除對話之後才輪到的複製_不重建資料夾()
    {
        using var temp = new TempDirectory("media-keep");
        var source = temp.WriteFile(@"out\a.png");
        var gone = temp.Combine("conversations", "deleted");
        var calls = 0;
        using var keeper = new MediaKeeper((_, _, _) => Interlocked.Increment(ref calls), _ => { });

        keeper.Enqueue(new MediaKeeper.Job("deleted", "item-1", gone, [source], null, null, []));

        keeper.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        Directory.Exists(gone).Should().BeFalse("對話已經刪掉：不重建資料夾");
        calls.Should().Be(0);
    }

    [Fact]
    public void 超過大小上限的不留()
    {
        using var temp = new TempDirectory("media-keep");
        var big = temp.Combine("out", "huge.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        using (var f = new FileStream(big, FileMode.Create))
            f.SetLength(MediaKeepRules.MaxBytes + 1);
        var small = temp.WriteFile(@"out\small.png");
        var conversation = temp.CreateDirectory(@"conversations\c1");
        IReadOnlyDictionary<string, string>? result = null;
        using var keeper = new MediaKeeper((_, _, kept) => result = kept, _ => { });

        keeper.Enqueue(new MediaKeeper.Job("c1", "item-1", conversation, [big, small], null, null, []));

        keeper.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        result!.Keys.Should().Equal(small);
    }

    /// <summary>修正之前延伸出來的對話（2026-11-01 實機）：載入紀錄時補留其他對話資料夾裡的媒體，每次執行只看一次</summary>
    [Fact]
    public void 修正之前延伸出來的對話_載入紀錄時補留其他對話資料夾裡的媒體_工作資料夾的不補()
    {
        var sourceId = Guid.NewGuid().ToString("N");
        var forkId = Guid.NewGuid().ToString("N");
        string card = "", inWorkspace = "";
        using var h = new HubHarness(seedStore: store =>
        {
            var sourceDir = store.GetConversationDirectory(sourceId);
            var workspace = Path.Combine(sourceDir, "workspace");
            card = Path.Combine(sourceDir, "caption-demo", "contact.png");
            inWorkspace = Path.Combine(workspace, "shot.png");
            Directory.CreateDirectory(Path.GetDirectoryName(card)!);
            Directory.CreateDirectory(workspace);
            File.WriteAllBytes(card, Png);
            File.WriteAllBytes(inWorkspace, Png);
            store.SaveConversation(new AgentConversation { Id = sourceId, AccountId = "acc-codex", Provider = AgentProvider.Codex, Title = "原對話", SessionId = "s0" });
            store.SaveConversation(new AgentConversation
            {
                Id = forkId, AccountId = "acc-codex", Provider = AgentProvider.Codex, Title = "原對話（延伸）", SessionId = "s1",
                WorkingDirectory = workspace, ForkedFrom = sourceId, ForkedFromTitle = "原對話",
            });
            store.SaveItems(forkId,
            [
                new AgentChatItem { Id = "tool-1", Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, Text = "做字卡", MediaPaths = [card, inWorkspace] },
                new AgentChatItem { Id = "user-1", Kind = AgentItemKind.User, Status = AgentItemStatus.Done, Text = "好" },
            ]);
        });
        h.OnUi(() => { h.Hub.AttachMediaKeeper(); return 0; });

        h.Items(forkId).Should().HaveCount(2);
        WaitKept(h);

        var tool = Item(h, forkId, i => i.Id == "tool-1");
        tool.KeptMedia!.Keys.Should().Equal([card], "工作資料夾（原對話的工作區）遠端本來就放行");
        File.ReadAllBytes(MediaKeepRules.KeptPath(tool.KeptMedia, h.Store.GetConversationDirectory(forkId), card)!).Should().Equal(Png);
        h.OnUi(() => h.Hub.MediaKeeperForTests!.Done).Should().Be(1, "只排了要補的那一筆");

        h.Items(forkId);
        h.OnUi(() => h.Hub.MediaKeeperForTests!.Done).Should().Be(1, "同一次執行不再看");
    }

    [Fact]
    public void 遠端取檔_項目已載入與沒載入_都給副本_副本不在給原檔()
    {
        using var h = new HubHarness();
        h.OnUi(() => { h.Hub.AttachMediaKeeper(); return 0; });
        var shot = WritePng(h, @"out\shot.png");
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentTextCompletedEvent("m:1", shot));
        h.CompleteTurn(s);
        WaitKept(h);
        h.OnUi(() => h.Hub.ItemsWriterForTests)?.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        var item = Item(h, id, i => i.Kind == AgentItemKind.Assistant);
        var dir = h.Store.GetConversationDirectory(id);
        var kept = MediaKeepRules.KeptPath(item.KeptMedia, dir, shot)!;
        var row = new LocalMediaRefRow("r1", ConversationIds.ToWire(id)!, item.Id, null, shot, 1);

        var loaded = h.OnUi(() => ((IMediaScopeSource)h.Hub).GetMediaScope(new MediaScopeQuery(ConversationIds.ToWire(id)!, item.Id, null, shot, "d_01J9Q0000000000000000000ME")));
        loaded!.Visible.Should().BeTrue();
        loaded.ItemsLoaded.Should().BeTrue();
        loaded.KeptPath.Should().Be(kept);
        var resolver = new LocalMediaResolver((_, _) => Task.FromResult<MediaScopeSnapshot?>(loaded), MediaTestFiles.Guard());
        resolver.Resolve(loaded, row).Should().Be(new ResolvedMedia(kept, "shot.png", 1));

        var notLoaded = loaded with { ItemsLoaded = false, Resolved = null, KeptPath = null, ConversationDirectory = dir };
        resolver.Resolve(notLoaded, row).Should().Be(new ResolvedMedia(kept, "shot.png", 1));

        File.Delete(kept);
        resolver.Resolve(notLoaded, row)!.Path.Should().Be(shot, "副本不在就照原檔");
    }
}
