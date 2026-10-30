using System.Security.Cryptography;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 從回收區還原（WP4.4 計畫步 11、D25；WP4.6 的還原鈕呼叫 AgentHub.RestoreFromTrash）：搬回對話根目錄、加回清單；資料夾帶著伺服器刪除的旁檔，
/// 所以同步政策一律是 localOnly、發布者不重新發布（伺服器端的刪除不可恢復）。清單裡已有同一個 id、是副本、資料夾已存在、回收區沒有它時回 false，
/// 什麼檔案都不動
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubTrashRestoreTests
{
    private static Dictionary<string, string> Hashes(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static Dictionary<string, string> HashesWithoutSidecar(string dir)
        => Hashes(dir).Where(kv => kv.Key != ConversationStore.ServerDeletedFileName).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static ConversationTrash Trash(HubHarness h) => h.OnUi(() => h.Hub.Trash);

    private static string PendingPath(HubHarness h) => Path.Combine(Trash(h).RootDirectory, ConversationTrash.PendingFileName);

    /// <summary>閒置的 Claude 對話被遠端刪除、資料夾已搬進回收區；回（id、刪除前每個檔案的 SHA-256、原本的資料夾）</summary>
    private static async Task<(string Id, Dictionary<string, string> Before, string Dir)> RemoteDeletedAsync(HubHarness h, string text = "要還原的對話")
    {
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, text);
        h.CompleteTurn(s);
        var dir = h.Store.GetConversationDirectory(id);
        h.OnUi(() => h.Hub.GetEffectiveSyncPolicy(id)).Should().Be(ConversationSyncPolicies.Full, "對照：刪除之前是 full");
        var before = Hashes(dir);

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("delete", id), tier: 2));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        await h.WaitUntilAsync(() => Trash(h).Contains(id) && !Directory.Exists(dir) && !File.Exists(PendingPath(h)), "資料夾在背景移到回收區");
        return (id, before, dir);
    }

    /// <summary>發布者看到的：有效政策 localOnly、有伺服器刪除的旁檔，發布計畫什麼都不送（連 conv.created 都不補）</summary>
    private static void PublisherSkips(HubHarness h, string id)
    {
        h.OnUi(() => h.Hub.GetEffectiveSyncPolicy(id)).Should().Be(ConversationSyncPolicies.LocalOnly);
        var snapshot = h.OnUi(() => ((IConversationPublishSource)h.Hub).SnapshotMeta([id])).Single();
        (snapshot.EffectivePolicy, snapshot.ServerDeleted).Should().Be((ConversationSyncPolicies.LocalOnly, true));
        PublishPlanRules.Plan(snapshot.EffectivePolicy, new PublishKnowledge(false, null, false, snapshot.ServerDeleted),
                [new PublishOp(PublishOpKind.Created), new PublishOp(PublishOpKind.Meta), new PublishOp(PublishOpKind.Item, "i1")])
            .Should().BeEmpty("還原的對話不重新發布");
    }

    [Fact]
    public async Task 還原_回到清單_內容不變只多旁檔_localOnly_發布者不發布_重新啟動後照樣()
    {
        using var h = new HubHarness();
        var (id, before, dir) = await RemoteDeletedAsync(h);
        var mark = h.Main.Mark();

        h.OnUi(() => h.Hub.RestoreFromTrash(id)).Should().BeTrue();

        h.Conversation(id).Should().NotBeNull();
        h.Conversation(id)!.Title.Should().Be("要還原的對話");
        HashesWithoutSidecar(dir).Should().BeEquivalentTo(before, "還原是改名，原本的內容不變");
        File.Exists(Path.Combine(dir, ConversationStore.ServerDeletedFileName)).Should().BeTrue("刪除時寫的旁檔跟著回來");
        Trash(h).Contains(id).Should().BeFalse();
        File.Exists(Path.Combine(Trash(h).RootDirectory, id + ConversationTrash.InfoSuffix)).Should().BeFalse();
        PublisherSkips(h, id);
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id), mark);

        await h.RestartAsync();

        h.Conversation(id).Should().NotBeNull("重新啟動之後還在清單");
        Trash(h).Contains(id).Should().BeFalse("不會又被搬回回收區");
        PublisherSkips(h, id);
    }

    [Fact]
    public async Task 還原的對話可以照常送出()
    {
        using var h = new HubHarness();
        var (id, _, _) = await RemoteDeletedAsync(h);
        h.OnUi(() => h.Hub.RestoreFromTrash(id)).Should().BeTrue();

        h.Send(h.Main, id, "還原之後再問一題");

        h.Sessions.Last().Sent.All.Last().Input.Text.Should().Be("還原之後再問一題");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("還原之後再問一題");
    }

    [Fact]
    public async Task 還原的對話_刪除前從遠端排進佇列的訊息_取出時照有效政策localOnly留下()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude, "還在跑的對話");
        h.IsRunning(id).Should().BeTrue();
        var dir = h.Store.GetConversationDirectory(id);
        var cid = RemoteEntrySamples.NewCid();
        (await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text = "從遠端排的" }), cid: cid)))
            .State.Should().Be(RemoteResultStates.Done);
        h.Queue(id).Should().Equal("從遠端排的");
        (await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("delete", id), tier: 2))).State.Should().Be(RemoteResultStates.Done);
        await h.WaitUntilAsync(() => Trash(h).Contains(id) && !Directory.Exists(dir) && !File.Exists(PendingPath(h)), "資料夾在背景移到回收區");

        h.OnUi(() => h.Hub.RestoreFromTrash(id)).Should().BeTrue();
        h.OnUi(() => h.Hub.GetEffectiveSyncPolicy(id)).Should().Be(ConversationSyncPolicies.LocalOnly, "前提：有伺服器刪除的旁檔（meta.json 仍是 full）");
        var restored = h.OnUi(() => h.Hub.QueueEntriesForTest(id)).Single();
        restored.Origin.Should().NotBeNull("前提：佇列連同來源還原");
        restored.Origin!.CommandId.Should().Be(cid);

        // 本機送一則、正常結束：遠端在刪除前排的那則不自動送進代理（gate/F7 看有效政策，合併前審查 AMO-03）
        h.Send(h.Main, id, "本機的");
        h.CompleteTurn(h.Sessions[^1]);

        h.Sessions[^1].Sent.All.Select(x => x.Input.Text).Should().NotContain(t => t.Contains("從遠端排的"));
        var held = h.OnUi(() => h.Hub.QueueEntriesForTest(id)).Single();
        (held.Id, held.Held).Should().Be(("r" + cid, true));
        h.Items(id).Count(i => i.Kind == AgentItemKind.Notice && i.Text!.StartsWith("從遠端排進佇列的訊息暫停了", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task 還原的對話_遠端指令在閘門5c照有效政策localOnly回policy()
    {
        using var h = new HubHarness();
        var (id, _, _) = await RemoteDeletedAsync(h);
        h.OnUi(() => h.Hub.RestoreFromTrash(id)).Should().BeTrue();
        h.Conversation(id)!.SyncPolicy.Should().NotBe(ConversationSyncPolicies.LocalOnly, "前提：meta.json 沒改，只有伺服器刪除的旁檔");

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("rename", id, new { title = "遠端改的" })));

        // 閘門、取出時的重查與發布端看同一個有效政策（合併前審查 AMO-03）
        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Conversation);
        h.Conversation(id)!.Title.Should().Be("要還原的對話");
    }

    [Fact]
    public void 還原_回收區沒有_false()
    {
        using var h = new HubHarness();

        h.OnUi(() => h.Hub.RestoreFromTrash(Guid.NewGuid().ToString("N"))).Should().BeFalse();
        h.OnUi(() => h.Hub.RestoreFromTrash("legacy-1")).Should().BeFalse("id 不合格");
        h.OnUi(() => h.Hub.RestoreFromTrash(@"..\conversations")).Should().BeFalse();
        Directory.Exists(Trash(h).RootDirectory).Should().BeFalse();
    }

    [Fact]
    public void 還原_清單裡已有同一個id_大小寫不分_false而且不動任何檔案()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (live, s) = h.StartConversation(h.Main, h.Claude, "還在清單的對話");
        h.CompleteTurn(s);
        // 回收區裡放一個同 id 的資料夾（例如手動複製進去的）
        var trashDir = Path.Combine(Trash(h).RootDirectory, live);
        Directory.CreateDirectory(trashDir);
        File.WriteAllText(Path.Combine(trashDir, "meta.json"), $$"""{"Id":"{{live}}","Title":"回收區的那份"}""");
        var trashed = Hashes(trashDir);
        var liveDir = h.Store.GetConversationDirectory(live);
        var current = Hashes(liveDir);

        h.OnUi(() => h.Hub.RestoreFromTrash(live)).Should().BeFalse();
        h.OnUi(() => h.Hub.RestoreFromTrash(live.ToUpperInvariant())).Should().BeFalse("大小寫不分");

        Hashes(trashDir).Should().BeEquivalentTo(trashed);
        Hashes(liveDir).Should().BeEquivalentTo(current);
        h.Conversation(live)!.Title.Should().Be("還在清單的對話");
    }

    [Fact]
    public void 還原_id是清單裡的草稿_false而且不動任何檔案()
    {
        using var h = new HubHarness();
        var draft = h.NewConversation(h.Main, h.Claude);
        var trashDir = Path.Combine(Trash(h).RootDirectory, draft);
        Directory.CreateDirectory(trashDir);
        File.WriteAllText(Path.Combine(trashDir, "meta.json"), $$"""{"Id":"{{draft}}","Title":"回收區的那份","AccountId":"acc-claude"}""");
        var trashed = Hashes(trashDir);

        h.OnUi(() => h.Hub.RestoreFromTrash(draft)).Should().BeFalse("清單裡已有這個 id（草稿還沒有資料夾也一樣）");

        Hashes(trashDir).Should().BeEquivalentTo(trashed);
        File.Exists(Path.Combine(h.Store.GetConversationDirectory(draft), "meta.json")).Should().BeFalse("草稿沒有被回收區的那份蓋掉");
    }

    [Fact]
    public async Task 還原_id是副本_false而且不動任何檔案()
    {
        using var h = new HubHarness();
        var (id, _, dir) = await RemoteDeletedAsync(h);
        var trashed = Hashes(Path.Combine(Trash(h).RootDirectory, id));
        h.Ui.Invoke(() => h.Store.MarkReplica(id));

        h.OnUi(() => h.Hub.RestoreFromTrash(id)).Should().BeFalse();

        Directory.Exists(dir).Should().BeFalse();
        Hashes(Path.Combine(Trash(h).RootDirectory, id)).Should().BeEquivalentTo(trashed);
        h.Conversation(id).Should().BeNull();
    }

    [Fact]
    public async Task 還原_對話根目錄已有同名資料夾_false而且不動任何檔案()
    {
        using var h = new HubHarness();
        var (id, _, dir) = await RemoteDeletedAsync(h);
        var trashed = Hashes(Path.Combine(Trash(h).RootDirectory, id));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "手動放的");

        h.OnUi(() => h.Hub.RestoreFromTrash(id)).Should().BeFalse();

        Hashes(dir).Keys.Should().Equal("notes.txt");
        Hashes(Path.Combine(Trash(h).RootDirectory, id)).Should().BeEquivalentTo(trashed);
        h.Conversation(id).Should().BeNull();
    }
}
