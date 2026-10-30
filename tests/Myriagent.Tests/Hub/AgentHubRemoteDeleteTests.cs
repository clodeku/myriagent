using System.Security.Cryptography;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端 delete（WP4.4 計畫步 7、11，D24、D25、SEC-27）：對話從清單移除、資料夾移到回收區（原本每個檔案內容不變，只多了伺服器刪除的旁檔：
/// 之後還原或手動搬回都不重新發布），本機刪除照舊直接刪；刪除的掛鉤在移除之前、程序先停再搬；搬不動的記 pending，
/// 下次建構中樞時在載入之前完成，仍搬不動的不出現在清單；建構中樞時在背景清掉超過 30 天的；草稿清理不碰回收區
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteDeleteTests
{
    private static Dictionary<string, string> Hashes(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    /// <summary>伺服器刪除的旁檔以外的檔案的 SHA-256</summary>
    private static Dictionary<string, string> HashesWithoutSidecar(string dir)
        => Hashes(dir).Where(kv => kv.Key != ConversationStore.ServerDeletedFileName).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>閒置的 Claude 對話（這台允許遠端操作），工作資料夾裡另外放一個二進位檔</summary>
    private static (string Id, FakeSession Session, string Dir) IdleConversation(HubHarness h, string text = "要從手機刪的對話")
    {
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, text);
        h.CompleteTurn(s);
        var dir = h.Store.GetConversationDirectory(id);
        var bytes = new byte[8192];
        new Random(11).NextBytes(bytes);
        Directory.CreateDirectory(Path.Combine(dir, "workspace"));
        File.WriteAllBytes(Path.Combine(dir, "workspace", "data.bin"), bytes);
        return (id, s, dir);
    }

    private static Task<RemoteOutcome> DeleteAsync(HubHarness h, string id, string? cid = null)
        => RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("delete", id), tier: 2, cid: cid));

    private static ConversationTrash Trash(HubHarness h) => h.OnUi(() => h.Hub.Trash);

    private static string PendingPath(HubHarness h) => Path.Combine(Trash(h).RootDirectory, ConversationTrash.PendingFileName);

    private static void WriteInfo(HubHarness h, string id, long trashedAt)
    {
        Directory.CreateDirectory(Trash(h).RootDirectory);
        File.WriteAllText(Path.Combine(Trash(h).RootDirectory, id + ConversationTrash.InfoSuffix), $$"""{"trashedAt":{{trashedAt}}}""");
    }

    [Fact]
    public async Task 遠端delete_移到回收區_每個檔案的SHA256相同_清單移除_回done()
    {
        using var h = new HubHarness();
        var (id, s, dir) = IdleConversation(h);
        var title = h.Conversation(id)!.Title;
        var before = Hashes(dir);
        before.Should().ContainKeys("meta.json", "items.json", Path.Combine("workspace", "data.bin"));
        var mark = h.Main.Mark();
        var cid = RemoteEntrySamples.NewCid();
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var outcome = await DeleteAsync(h, id, cid);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Conversation(id).Should().BeNull();
        s.Disposed.Should().BeTrue();
        h.Host.Deleted.All.Should().Equal(id);
        h.Main.Messages("conversationClosed", mark).Should().ContainSingle(m => m.Value<string>("conversationId") == id);
        var trash = Trash(h);
        trash.RootDirectory.Should().Be(Path.Combine(h.Temp.Root, "conversations-trash"), "資料根目錄的 conversations-trash（D24）");
        await h.WaitUntilAsync(() => trash.Contains(id) && !Directory.Exists(dir), "資料夾在背景移到回收區");
        before.Should().NotContainKey(ConversationStore.ServerDeletedFileName, "前提：刪除前沒有旁檔");
        HashesWithoutSidecar(Path.Combine(trash.RootDirectory, id)).Should().BeEquivalentTo(before, "遠端刪除不刪本機資料（SEC-27）");
        var sidecar = JObject.Parse(File.ReadAllText(Path.Combine(trash.RootDirectory, id, ConversationStore.ServerDeletedFileName)));
        sidecar.Value<long>("at").Should().BeInRange(startedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "回收區的資料夾帶著伺服器刪除的旁檔（D25）");
        var entry = trash.List().Single();
        entry.ConversationId.Should().Be(id);
        entry.Info.CommandId.Should().Be(cid);
        entry.Info.IssuerLabel.Should().Be(RemoteEntrySamples.IssuerLabel);
        entry.Info.Title.Should().Be(title);
        entry.Info.TrashedAt.Should().BeInRange(startedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await h.WaitUntilAsync(() => !File.Exists(PendingPath(h)), "搬好就從 pending 拿掉");
        await h.Main.WaitForAsync("conversations", m => m["items"]!.All(i => i.Value<string>("id") != id), mark);

        await h.RestartAsync();
        h.Conversation(id).Should().BeNull("重新啟動之後也不回到清單");
        Trash(h).Contains(id).Should().BeTrue();
    }

    [Fact]
    public async Task 本機delete_照舊直接刪除_不進回收區()
    {
        using var h = new HubHarness();
        var (id, _, dir) = IdleConversation(h);

        h.Command(h.Main, "delete", new { conversationId = id });

        h.Conversation(id).Should().BeNull();
        await h.WaitUntilAsync(() => !Directory.Exists(dir), "對話資料夾在背景刪除");
        await Task.Delay(100);
        Directory.Exists(Trash(h).RootDirectory).Should().BeFalse("本機刪除不經過回收區");
    }

    [Fact]
    public async Task 遠端delete_刪除的掛鉤在移除之前呼叫_程序已停_資料夾還在原處()
    {
        using var h = new HubHarness();
        var (id, s, dir) = IdleConversation(h);
        var seen = new Recorder<(bool InHub, bool Disposed, bool FolderInPlace, bool InTrash)>();
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(new DeletingSink(_ =>
            seen.Add((h.Hub.GetConversation(id) != null, s.Disposed, Directory.Exists(dir), h.Hub.Trash.Contains(id))))));

        var outcome = await DeleteAsync(h, id);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        seen.All.Should().Equal((true, true, true, false));
        await h.WaitUntilAsync(() => Trash(h).Contains(id), "之後才搬進回收區");
    }

    [Fact]
    public async Task 執行中的對話被遠端刪除_程序先停_再搬移()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "還在跑的對話");
        h.IsRunning(id).Should().BeTrue();
        var dir = h.Store.GetConversationDirectory(id);
        var order = new Recorder<string>();
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(new DeletingSink(_ =>
            order.Add($"deleting:disposed={s.Disposed}:folder={Directory.Exists(dir)}"))));

        var outcome = await DeleteAsync(h, id);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        order.All.Should().Equal("deleting:disposed=True:folder=True");
        await h.WaitUntilAsync(() => Trash(h).Contains(id) && !Directory.Exists(dir), "程序停了之後搬進回收區");
        h.Ui.Invoke(() => h.Hub.HasRunningSessions(AgentProvider.Claude)).Should().BeFalse();
    }

    [Fact]
    public async Task 搬移失敗_記pending_下次建構中樞前完成_仍失敗的不出現在清單()
    {
        using var h = new HubHarness();
        var (id, _, dir) = IdleConversation(h);
        var before = Hashes(dir);
        h.Ui.Invoke(() =>
        {
            h.Hub.TrashMoveRetryInterval = TimeSpan.FromMilliseconds(50);
            h.Hub.TrashMoveRetryLimit = TimeSpan.FromMilliseconds(300);
        });
        var mark = h.Main.Mark();
        var locked = new FileStream(Path.Combine(dir, "workspace", "data.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var outcome = await DeleteAsync(h, id);

            outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
            File.ReadAllText(PendingPath(h)).Should().Contain(id, "回 done 之前已記進 pending");
            h.Conversation(id).Should().BeNull();
            var toast = await h.Main.WaitForAsync("toast", m => m.Value<string>("text")!.Contains("暫時移不到回收區"), mark, timeoutMs: 5000);
            toast.Value<string>("text").Should().Contain("下次啟動時");
            Directory.Exists(dir).Should().BeTrue();
            File.Exists(Path.Combine(dir, "meta.json")).Should().BeTrue("meta.json 不先刪（D24）");

            await h.RestartAsync();
            h.Store.LoadConversations().Select(c => c.Id).Should().Contain(id, "資料夾還在對話根目錄");
            h.Conversation(id).Should().BeNull("仍搬不動的不出現在清單");
            h.Main.Messages("conversations").SelectMany(m => m["items"]!).Should().NotContain(i => i.Value<string>("id") == id);
            File.ReadAllText(PendingPath(h)).Should().Contain(id);
        }
        finally
        {
            locked.Dispose();
        }

        await h.RestartAsync();
        Trash(h).Contains(id).Should().BeTrue("放開之後下次建構中樞時完成");
        Directory.Exists(dir).Should().BeFalse();
        HashesWithoutSidecar(Path.Combine(Trash(h).RootDirectory, id)).Should().BeEquivalentTo(before);
        File.Exists(Path.Combine(Trash(h).RootDirectory, id, ConversationStore.ServerDeletedFileName)).Should().BeTrue("補搬的也帶著旁檔");
        h.Conversation(id).Should().BeNull();
        File.Exists(PendingPath(h)).Should().BeFalse();
    }

    [Fact]
    public async Task 建構中樞時在背景清掉超過30天的_沒過期的留著()
    {
        using var h = new HubHarness();
        var (expired, fresh) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (id, at) in new[] { (expired, now - ConversationTrashRules.RetentionMs - 60_000), (fresh, now - 29L * 86_400_000) })
        {
            Directory.CreateDirectory(Path.Combine(Trash(h).RootDirectory, id));
            File.WriteAllText(Path.Combine(Trash(h).RootDirectory, id, "items.json"), "[]");
            WriteInfo(h, id, at);
        }

        await h.RestartAsync();

        await h.WaitUntilAsync(() => !Trash(h).Contains(expired), "超過 30 天的在背景清掉");
        Trash(h).Contains(fresh).Should().BeTrue();
        File.Exists(Path.Combine(Trash(h).RootDirectory, expired + ConversationTrash.InfoSuffix)).Should().BeFalse();
    }

    [Fact]
    public async Task 草稿清理不碰回收區()
    {
        using var h = new HubHarness();
        var (id, _, _) = IdleConversation(h);
        (await DeleteAsync(h, id)).State.Should().Be(RemoteResultStates.Done);
        await h.WaitUntilAsync(() => Trash(h).Contains(id), "搬進回收區");
        // 回收區裡一個長得像草稿殘留的資料夾（只有 attachments\ 與空的 workspace\）
        var draftLike = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(Trash(h).RootDirectory, draftLike, "attachments"));
        Directory.CreateDirectory(Path.Combine(Trash(h).RootDirectory, draftLike, "workspace"));
        WriteInfo(h, draftLike, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // 對照：對話根目錄裡同樣的形狀會被找出來
        var leftover = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(h.Store.RootDirectory, leftover, "attachments"));

        h.Store.FindDraftLeftovers().Should().Equal(leftover);

        await h.RestartAsync();
        Trash(h).Contains(id).Should().BeTrue();
        Trash(h).Contains(draftLike).Should().BeTrue();
    }

    [Fact]
    public async Task pending裡id不合格的_不搬_照常載入()
    {
        var legacy = "legacy-1";
        using var h = new HubHarness(seedStore: store => store.SaveConversation(new AgentConversation
        {
            Id = legacy, Title = "舊版留下的對話", AccountId = "acc-claude", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now,
        }));
        h.Conversation(legacy).Should().NotBeNull("前提：不是十六進位的資料夾名稱也照樣載入");
        Directory.CreateDirectory(Trash(h).RootDirectory);
        File.WriteAllText(PendingPath(h), $$"""{"moves":[{"conversationId":"{{legacy}}","trashedAt":1},{"conversationId":"..\\x","trashedAt":1}]}""");
        WriteInfo(h, "notes", 0);
        Directory.CreateDirectory(Path.Combine(Trash(h).RootDirectory, "notes"));

        await h.RestartAsync();
        await Task.Delay(200);

        h.Conversation(legacy).Should().NotBeNull("不合格的 id 不搬、不略過");
        Directory.Exists(h.Store.GetConversationDirectory(legacy)).Should().BeTrue();
        File.Exists(PendingPath(h)).Should().BeFalse("不合格的 id 從 pending 拿掉");
        Directory.Exists(Path.Combine(Trash(h).RootDirectory, "notes")).Should().BeTrue("回收區裡不是對話名稱的資料夾不清");
    }

    [Fact]
    public async Task 手動把資料夾從回收區搬回conversations_重新啟動後載入_localOnly_發布者不重新發布()
    {
        using var h = new HubHarness();
        var (id, _, dir) = IdleConversation(h);
        h.OnUi(() => h.Hub.GetEffectiveSyncPolicy(id)).Should().Be(ConversationSyncPolicies.Full, "對照：刪除之前是 full");
        (await DeleteAsync(h, id)).State.Should().Be(RemoteResultStates.Done);
        await h.WaitUntilAsync(() => Trash(h).Contains(id) && !Directory.Exists(dir) && !File.Exists(PendingPath(h)), "搬進回收區");

        // 使用者在檔案總管把資料夾拖回 conversations\（不經過還原）
        Directory.Move(Path.Combine(Trash(h).RootDirectory, id), dir);
        File.Delete(Path.Combine(Trash(h).RootDirectory, id + ConversationTrash.InfoSuffix));
        await h.RestartAsync();

        h.Conversation(id).Should().NotBeNull("資料夾在對話根目錄就照常載入");
        h.OnUi(() => h.Hub.GetEffectiveSyncPolicy(id)).Should().Be(ConversationSyncPolicies.LocalOnly, "旁檔跟著資料夾回來");
        var snapshot = h.OnUi(() => ((IConversationPublishSource)h.Hub).SnapshotMeta([id])).Single();
        snapshot.ServerDeleted.Should().BeTrue();
        PublishPlanRules.Plan(snapshot.EffectivePolicy, new PublishKnowledge(false, null, false, snapshot.ServerDeleted),
                [new PublishOp(PublishOpKind.Created), new PublishOp(PublishOpKind.Meta), new PublishOp(PublishOpKind.Item, "i1")])
            .Should().BeEmpty("伺服器端已刪除的對話不重新發布（D25）");
    }

    [Fact]
    public async Task 遠端delete_不存在的對話_conflict()
    {
        using var h = new HubHarness();
        var (id, _, _) = IdleConversation(h);
        h.Command(h.Main, "delete", new { conversationId = id });

        var outcome = await DeleteAsync(h, id);

        outcome.State.Should().Be(RemoteResultStates.Rejected);
        Directory.Exists(Trash(h).RootDirectory).Should().BeFalse();
    }

    /// <summary>只記錄 OnConversationDeleting 當下的狀態</summary>
    private sealed class DeletingSink(Action<string> onDeleting) : IConversationChangeSink
    {
        public void OnConversationDeleting(ConversationHookInfo conversation) => onDeleting(conversation.Meta.Id);

        public void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item)
        {
        }

        public void OnItemRemoved(ConversationHookInfo conversation, AgentChatItem item)
        {
        }

        public void OnConversationSaved(ConversationHookInfo conversation, bool itemsSaved)
        {
        }

        public void OnHeaderChanged(ConversationHookInfo conversation)
        {
        }

        public void OnRunningCountsChanged()
        {
        }

        public void OnQueueChanged(ConversationHookInfo conversation)
        {
        }

        public void OnComposerChanged(ConversationHookInfo conversation)
        {
        }

        public void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli)
        {
        }

        public void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage)
        {
        }

        public void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech)
        {
        }
    }
}
