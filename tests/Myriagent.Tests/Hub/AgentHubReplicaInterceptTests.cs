using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 副本在本機唯讀（WP4.4 計畫步 10、D29；步 11 起由 WP4.3 的白名單擋下，這裡只加提示）：本機網頁帶副本的 conversationId 送來寫入的指令，
/// 只給那個檢視「唯讀」的提示、什麼都不做（不啟動 CLI、不開路徑）；白名單以外的自動指令擋下但不提示；讀取照常；
/// 擴充點在副本的白名單之後、只給本機的檢視
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubReplicaInterceptTests
{
    /// <summary>一個閒置、已有第一輪的對話，標成副本</summary>
    private static (string Id, FakeSession Session) Replica(HubHarness h)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentTextCompletedEvent("m:0", "代理的回覆"));
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(id));
        return (id, s);
    }

    /// <summary>每個寫入的指令都用得到的欄位（沒被攔下的話每一個都會有效果）</summary>
    private static object WriteArgs(string id, string folder, string file) => new
    {
        conversationId = id,
        text = "副本上送的",
        title = "改掉的標題",
        path = file,
        which = "working",
        itemId = "item-1",
        id = "queued-1",
        key = "model",
        value = "opus",
        accountId = "acc-claude2",
        choice = "allow",
        provider = "Claude",
        policy = "localOnly",
        dataUrl = "data:image/png;base64,iVBORw0KGgo=",
        name = "a.png",
        requestId = "r1",
        query = "a",
        folder,
    };

    private static List<JObject> ReadOnlyToasts(FakeView view, int since = 0)
        => view.Messages("toast", since).Where(m => m.Value<string>("text") == AgentHub.ReplicaReadOnlyMessage).ToList();

    [Fact]
    public async Task 副本_本機的寫入指令全部攔下_給那個檢視toast_不啟動CLI()
    {
        using var h = new HubHarness();
        var other = h.AddView("other");
        var (id, s) = Replica(h);
        var folder = h.Temp.Combine("elsewhere");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "x");
        var before = h.Conversation(id)!;
        var (title, account, workingDirectory) = (before.Title, before.AccountId, before.WorkingDirectory);
        var sessions = h.Sessions.Count;
        var sent = s.Sent.Count;
        var mark = h.Main.Mark();

        foreach (var cmd in ReplicaCommandRules.Writes.Order(StringComparer.Ordinal))
            h.Command(h.Main, cmd, WriteArgs(id, folder, file));
        await h.Ui.DrainAsync();
        await Task.Delay(200);
        await h.Ui.DrainAsync();

        var toasts = ReadOnlyToasts(h.Main, mark);
        toasts.Should().HaveCount(ReplicaCommandRules.Writes.Count, "每一個寫入的指令都只得到一則唯讀提示");
        toasts.Should().OnlyContain(t => t.Value<string>("level") == "error");
        ReadOnlyToasts(other).Should().BeEmpty("提示只給送出指令的那個檢視");
        h.Sessions.Count.Should().Be(sessions, "不啟動 CLI");
        s.Sent.Count.Should().Be(sent, "沒有送進代理");
        var after = h.Conversation(id);
        after.Should().NotBeNull("沒有被刪除");
        (after!.Title, after.AccountId, after.WorkingDirectory).Should().Be((title, account, workingDirectory), "什麼都沒改");
        h.Queue(id).Should().BeEmpty();
        h.Host.Opened.All.Should().BeEmpty("不開路徑");
        h.Host.Revealed.All.Should().BeEmpty();
    }

    [Fact]
    public async Task openFolder_openPath_revealPath攔下_不帶對話的照常()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        var folder = h.Temp.Combine("elsewhere");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "x");
        var control = h.Temp.Combine("control");
        Directory.CreateDirectory(control);
        var mark = h.Main.Mark();

        h.Command(h.Main, "openFolder", new { conversationId = id, which = "attachments" });
        h.Command(h.Main, "openPath", new { conversationId = id, path = file });
        h.Command(h.Main, "revealPath", new { conversationId = id, path = folder });
        h.Command(h.Main, "revealPath", new { path = control });

        await h.WaitUntilAsync(() => h.Host.Revealed.Count > 0, "沒帶對話的 revealPath 照常在檔案總管顯示");
        await h.Ui.DrainAsync();
        h.Host.Revealed.All.Should().Equal(control);
        h.Host.Opened.All.Should().BeEmpty();
        ReadOnlyToasts(h.Main, mark).Should().HaveCount(3);
    }

    [Fact]
    public void open_loadOlder_copyItem不攔()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        var reply = h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);
        var mark = h.Main.Mark();

        h.Command(h.Main, "open", new { conversationId = id });
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        h.Command(h.Main, "loadOlder", new { conversationId = id, beforeId = reply.Id });
        h.Command(h.Main, "copyItem", new { conversationId = id, itemId = reply.Id });
        h.Command(h.Main, "popout", new { conversationId = id });
        h.Command(h.Main, "close", new { conversationId = id });

        ReadOnlyToasts(h.Main, mark).Should().BeEmpty();
        h.Main.Messages("conversation", mark, id).Should().NotBeEmpty("open 照常送出對話");
        h.Host.Copied.All.Should().Equal(reply.Text);
        h.Host.Popouts.All.Should().Equal(id);
    }

    [Fact]
    public void 白名單以外_不是寫入的指令_擋下但不提示_也不到擴充點()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        var calls = new Recorder<string?>();
        h.Ui.Invoke(() => h.Hub.InterceptCommandExtensionForTest = (_, cmd, _) =>
        {
            calls.Add(cmd);
            return false;
        });
        var mark = h.Main.Mark();

        // 不認得的、之後才有的指令：WP4.3 的白名單一律擋下（fail-closed），不是使用者的寫入就不提示
        h.Command(h.Main, "futureConversationCommand", new { conversationId = id });

        ReadOnlyToasts(h.Main, mark).Should().BeEmpty();
        calls.Count.Should().Be(0, "副本被擋下的指令不到擴充點");
        h.Command(h.Main, "rename", new { conversationId = id, title = "改掉的標題" });
        ReadOnlyToasts(h.Main, mark).Should().ContainSingle("對照：使用者的寫入有提示");
        calls.Count.Should().Be(0);
    }

    [Fact]
    public async Task 遠端的回應檢視_副本照樣擋下_不提示()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        var title = h.Conversation(id)!.Title;
        var output = new Recorder<JObject>();
        var mark = h.Main.Mark();

        // 不經閘門（5c 會先拒絕副本），直接以回應檢視進 HandleCommand：白名單照樣擋下，回應檢視與本機視窗都沒有唯讀提示
        h.Ui.Invoke(() => h.Hub.HandleCommandAsRemoteForTest(
            new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, id, 1, 1, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel),
            RemoteEntrySamples.Cmd("rename", id, new { title = "遠端改的" }), output.Add));
        await h.Ui.DrainAsync();

        h.Conversation(id)!.Title.Should().Be(title);
        output.All.Should().NotContain(m => m.Value<string>("text") == AgentHub.ReplicaReadOnlyMessage);
        ReadOnlyToasts(h.Main, mark).Should().BeEmpty();
    }

    [Fact]
    public void 非副本_不攔()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Command(h.Main, "rename", new { conversationId = id, title = "本機改的" });
        h.Send(h.Main, id, "第二則");

        ReadOnlyToasts(h.Main, mark).Should().BeEmpty();
        h.Conversation(id)!.Title.Should().Be("本機改的");
        s.Sent.Count.Should().Be(2, "第二則照常送出");
    }

    [Fact]
    public void 副本旗標拿掉之後_照常執行()
    {
        using var h = new HubHarness();
        var (id, _) = Replica(h);
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(id, replica: false));

        h.Command(h.Main, "rename", new { conversationId = id, title = "本機改的" });

        h.Conversation(id)!.Title.Should().Be("本機改的");
    }

    [Fact]
    public async Task 擴充點在副本攔截之後呼叫_只給本機的檢視()
    {
        using var h = new HubHarness();
        var (replicaId, _) = Replica(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var calls = new Recorder<(string ViewId, string? Cmd)>();
        h.Ui.Invoke(() => h.Hub.InterceptCommandExtensionForTest = (view, cmd, command) =>
        {
            calls.Add((view.Id, cmd));
            return cmd == "rename" && command.Value<string>("title") == "擴充點接下";
        });

        h.Command(h.Main, "send", new { conversationId = replicaId, text = "副本上送的" });
        calls.Count.Should().Be(0, "副本的寫入已被攔下，不到擴充點");

        h.Command(h.Main, "open", new { conversationId = replicaId });
        calls.All.Should().Equal(("main", "open"));

        h.Command(h.Main, "rename", new { conversationId = id, title = "擴充點接下" });
        calls.All[^1].Should().Be(("main", "rename"));
        h.Conversation(id)!.Title.Should().NotBe("擴充點接下", "擴充點回 true，HandleCommand 的 switch 不再看");

        h.Command(h.Main, "rename", new { conversationId = id, title = "沒接下" });
        h.Conversation(id)!.Title.Should().Be("沒接下", "擴充點回 false 照常處理");

        // 遠端指令的退路（回應檢視）不經擴充點
        RemoteEntrySamples.AllowRemote(h);
        var count = calls.Count;
        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("rename", id, new { title = "擴充點接下" })));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        calls.Count.Should().Be(count, "遠端的擴充走 IRemoteCommandHandler");
        h.Conversation(id)!.Title.Should().Be("擴充點接下", "遠端改名照退路執行");
    }
}
