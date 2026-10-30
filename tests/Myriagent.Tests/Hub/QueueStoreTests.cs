using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

[Collection(AppPathsCollection.Name)]
public sealed class QueueStoreTests
{
    [Fact]
    public void 存檔讀檔往返_空的就刪檔()
    {
        using var temp = new TempDirectory("queue");
        var snapshot = new QueueSnapshot(
            new List<QueuedMessageData> { new("q1", "下一則", new List<AgentAttachment> { new() { FileName = "a.png", StoredPath = temp.Combine("a.png"), Kind = AgentAttachmentKind.Image } }) },
            new List<AgentAttachment> { new() { FileName = "b.txt", StoredPath = temp.Combine("b.txt"), Kind = AgentAttachmentKind.Document } },
            new List<DeniedOperation> { new("執行指令", "git push", "先不要推") });

        QueueStore.Save(temp.Root, snapshot);
        var back = QueueStore.Load(temp.Root);

        back.Should().NotBeNull();
        back!.Queue.Single().Text.Should().Be("下一則");
        back.Queue.Single().Attachments.Single().FileName.Should().Be("a.png");
        back.PendingAttachments.Single().FileName.Should().Be("b.txt");
        back.PendingFeedback.Single().Should().Be(new DeniedOperation("執行指令", "git push", "先不要推"));
        back.Fingerprint().Should().Be(snapshot.Fingerprint());
        File.ReadAllText(Path.Combine(temp.Root, QueueStore.FileName)).Should().NotContain("IsEmpty", "算出來的屬性不存檔");

        QueueStore.Save(temp.Root, new QueueSnapshot(new(), new(), new()));
        File.Exists(Path.Combine(temp.Root, QueueStore.FileName)).Should().BeFalse();
        QueueStore.Load(temp.Root).Should().BeNull();
    }

    [Fact]
    public void 缺欄位的檔_清單補成空的_不丟例外()
    {
        using var temp = new TempDirectory("queue");
        var path = Path.Combine(temp.Root, QueueStore.FileName);

        File.WriteAllText(path, "{}");
        QueueStore.Load(temp.Root).Should().BeNull("沒有內容就等於沒有佇列");

        File.WriteAllText(path, """{ "Queue": [ null, { "Id": "q1", "Text": "下一則" } ], "PendingAttachments": null, "PendingFeedback": [ { "ToolName": "執行指令" } ] }""");
        var back = QueueStore.Load(temp.Root);

        back.Should().NotBeNull();
        var only = back!.Queue.Should().ContainSingle().Subject;
        only.Id.Should().Be("q1");
        only.Text.Should().Be("下一則");
        only.Attachments.Should().BeEmpty();
        back.PendingAttachments.Should().BeEmpty();
        back.PendingFeedback.Should().BeEmpty("沒有說明文字的拒絕沒有可送的內容");
    }

    [Fact]
    public void 指紋包含拒絕說明的完整內容()
    {
        static QueueSnapshot Denied(string? title, string feedback) => new(new(), new(), new() { new DeniedOperation("執行指令", title, feedback) });

        Denied("git push", "先不要推").Fingerprint().Should().NotBe(Denied("git pull", "先不要推").Fingerprint(), "標題不同");
        Denied("git push", "先不要推").Fingerprint().Should().NotBe(Denied("git push", "晚點再推").Fingerprint(), "說明長度相同、內容不同");
        Denied(null, "先不要推").Fingerprint().Should().NotBe(Denied(string.Empty, "先不要推").Fingerprint());
    }

    [Fact]
    public async Task 執行中排隊的訊息_重新啟動後還在佇列()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.Send(h.Main, id, "排隊的第二則");
        await h.WaitUntilAsync(() => h.Queue(id).Count == 1, "第二則排進佇列");
        File.Exists(QueueFile(h, id)).Should().BeTrue("排進佇列當下就寫檔（當機也不遺失）");

        await h.RestartAsync();

        h.Queue(id).Should().Equal("排隊的第二則");
    }

    [Fact]
    public async Task 佇列清空後_重新啟動不會復活()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.Send(h.Main, id, "排隊的第二則");
        await h.WaitUntilAsync(() => h.Queue(id).Count == 1, "第二則排進佇列");
        h.CompleteTurn(s);
        await h.WaitUntilAsync(() => h.Queue(id).Count == 0, "上一輪完成後自動送出第二則");
        File.Exists(QueueFile(h, id)).Should().BeFalse("佇列空了就刪檔");

        await h.RestartAsync();

        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public async Task 待送附件_重新啟動後還在輸入區_檔案已不存在的略過()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { h.Temp.WriteFile(@"in\留著.csv", "a"), h.Temp.WriteFile(@"in\會刪掉.csv", "b") });
        var composer = await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 2, mark, conversationId: id);
        File.Delete(composer["composer"]!["attachments"]!.Single(a => a.Value<string>("name") == "會刪掉.csv").Value<string>("path")!);

        await h.RestartAsync();
        h.Command(h.Main, "open", new { conversationId = id });

        h.Main.Last("conversation", id)!["composer"]!["attachments"]!.Select(a => a.Value<string>("name")).Should().Equal("留著.csv");
    }

    [Fact]
    public async Task 上次沒送出的拒絕說明_重新啟動後排在佇列最前面_等使用者按送出()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        h.Send(h.Main, id, "接著整理資料夾");
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "rm -rf tmp", "rm -rf tmp", "exec-1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny", message = "請改用資源回收筒" });
        QueueStore.Load(h.Store.GetConversationDirectory(id))!.PendingFeedback.Should().ContainSingle("拒絕當下就寫檔");

        // 這一輪沒結束就關掉 app（當掉也一樣）：那一輪已經不在了，說明照被打斷的一輪的慣例排到佇列最前面，跟著佇列暫停
        await h.RestartAsync();

        h.Queue(id).Should().Equal("（我剛才拒絕了那個操作）請改用資源回收筒", "接著整理資料夾");
        h.Sessions.Should().ContainSingle("佇列暫停，不自動送出");
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        var saved = QueueStore.Load(h.Store.GetConversationDirectory(id))!;
        saved.PendingFeedback.Should().BeEmpty("轉進佇列的說明在下次寫檔時改存成排隊訊息");
        saved.Queue.Select(q => q.Text).Should().Equal("（我剛才拒絕了那個操作）請改用資源回收筒", "接著整理資料夾");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 按了停止_這一輪結束前關掉app_重新啟動後不送拒絕說明_沒按停止的照舊還原(bool viaDispose)
    {
        using var h = new HubHarness();
        var (stoppedId, stopped) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        Deny(h, stoppedId, stopped, "請改用資源回收筒");
        var (keptId, kept) = h.StartConversation(h.Main, h.Codex, "整理設定");
        Deny(h, keptId, kept, "先不要動設定檔");
        h.Command(h.Main, "stop", new { conversationId = stoppedId });
        h.IsRunning(stoppedId).Should().BeTrue("CLI 還沒回報這一輪結束");
        QueueStore.Load(h.Store.GetConversationDirectory(stoppedId))!.PendingFeedback.Should().ContainSingle("拒絕當下寫的檔還在");

        // 這一輪結束前關掉 app：ShutdownAsync，或沒經過它直接 Dispose（兩條都會寫 queue.json）
        if (viaDispose)
            h.Ui.Invoke(() => h.Hub.Dispose());
        await h.RestartAsync();

        h.Queue(stoppedId).Should().BeEmpty("使用者按了停止就不送拒絕說明");
        QueueStore.Load(h.Store.GetConversationDirectory(stoppedId)).Should().BeNull("結束時寫檔前清掉：queue.json 已刪");
        h.Queue(keptId).Should().Equal(new[] { "（我剛才拒絕了那個操作）先不要動設定檔" }, "沒按停止的照舊排到佇列最前面");
    }

    private static void Deny(HubHarness h, string conversationId, FakeSession session, string feedback)
    {
        h.Raise(session, new AgentApprovalEvent("0", "執行指令", "bash", "rm -rf tmp", "rm -rf tmp", "exec-1", CanAllowForSession: true));
        var approval = h.Items(conversationId).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId, itemId = approval.Id, choice = "deny", message = feedback });
    }

    [Fact]
    public void 帳戶移除時在本機收尾的一輪_清掉的拒絕說明立即寫檔()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "rm -rf tmp", "rm -rf tmp", "exec-1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny", message = "請改用資源回收筒" });
        File.Exists(QueueFile(h, id)).Should().BeTrue();

        h.Command(h.Main, "accountRemove", new { accountId = h.Codex.Id, deleteData = false });

        File.Exists(QueueFile(h, id)).Should().BeFalse("這一輪已在本機收尾，說明不會再送");
    }

    [Fact]
    public async Task 啟動時讀不到的queue_json_讀到之前不覆寫_之後讀到就接回佇列()
    {
        var conversation = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "上次的對話", SessionId = Guid.NewGuid().ToString(), SessionStarted = true };
        FileStream? held = null;
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(conversation);
            var dir = store.GetConversationDirectory(conversation.Id);
            QueueStore.Save(dir, new QueueSnapshot(new() { new("q-old", "上次排的", new()) }, new(), new()));
            // 其他程式（防毒、備份）在啟動時短暫獨占開著：中樞建構時讀不到
            held = new FileStream(Path.Combine(dir, QueueStore.FileName), FileMode.Open, FileAccess.Read, FileShare.None);
        });
        held!.Dispose();
        var id = conversation.Id;
        h.Queue(id).Should().BeEmpty("啟動時讀不到");

        // 檔案已經放開、但中樞還沒重讀：這時的佇列變動不能把上次的佇列蓋掉
        h.Send(h.Main, id, "這一輪");
        h.Send(h.Main, id, "讀不到期間排的");
        await h.WaitUntilAsync(() => h.Queue(id).Count == 1, "執行中送出排進佇列");
        QueueStore.Load(h.Store.GetConversationDirectory(id))!.Queue.Select(q => q.Text).Should().Equal(new[] { "上次排的" }, "讀到之前不可以覆寫原檔");

        h.Ui.Invoke(() => h.Hub.RunMaintenance());

        h.Queue(id).Should().Equal("上次排的", "讀不到期間排的");
        QueueStore.Load(h.Store.GetConversationDirectory(id))!.Queue.Select(q => q.Text).Should().Equal("上次排的", "讀不到期間排的");
    }

    [Fact]
    public async Task 對話刪除後晚到的輸入區更新_不會重建只有queue_json的資料夾()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { h.Temp.WriteFile(@"in\資料.csv", "a,b") });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: id);
        var dir = h.Store.GetConversationDirectory(id);
        mark = h.Main.Mark();

        // 送出前在背景檢查工作資料夾（不存在）的期間刪掉對話，等資料夾刪完才讓檢查結果回到 UI 執行緒：
        // 附件還回輸入區時（PostComposer）對話已不在，不能寫 queue.json 把資料夾重建出來
        h.Ui.Invoke(() =>
        {
            h.Hub.HandleCommand(h.Main, HubHarness.MakeCommand("setFolder", new { conversationId = id, path = h.Temp.Combine("不存在的資料夾") }));
            h.Hub.HandleCommand(h.Main, HubHarness.MakeCommand("send", new { conversationId = id, text = "請整理這份資料" }));
            h.Hub.HandleCommand(h.Main, HubHarness.MakeCommand("delete", new { conversationId = id }));
            SpinWait.SpinUntil(() => !Directory.Exists(dir), TimeSpan.FromSeconds(5)).Should().BeTrue("對話資料夾在背景刪掉");
        });
        await h.Main.WaitForAsync("sendRejected", since: mark, conversationId: id);
        await h.Ui.DrainAsync();

        Directory.Exists(dir).Should().BeFalse();
    }

    private static string QueueFile(HubHarness h, string conversationId) => Path.Combine(h.Store.GetConversationDirectory(conversationId), QueueStore.FileName);
}
