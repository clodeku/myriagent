using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 輸入框草稿的同步（web-protocol 的 composerDraft／composerDrafts、sync-protocol 14.17）：網頁打的字轉給這台的其他視窗、
/// 同步政策是 full 的才交給同步；伺服器送來的照 rev 新舊、這台還沒送成功的不蓋掉、整份的沒列出就清掉；init 之後補送整份
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubComposerDraftsTests
{
    private static AgentConversation Seeded(string? policy = null) => new()
    {
        AccountId = "acc-claude",
        Provider = AgentProvider.Claude,
        Title = "對話",
        SessionId = Guid.NewGuid().ToString(),
        SessionStarted = true,
        SyncPolicy = policy,
    };

    private static JObject Single(JObject message) => (JObject)((JArray)message["drafts"]!).Single();

    [Fact]
    public async Task 網頁打的字轉給其他視窗_full的對話送給同步_一樣的不重送()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var bridge = new DraftBridge();
        h.Ui.Invoke(() => h.Hub.AttachSync(bridge));
        var other = h.AddView("other");
        var mark = other.Mark();
        var mainMark = h.Main.Mark();

        h.Command(h.Main, "composerDraft", new { conversationId = conversation.Id, text = "幫我看一下" });

        var msg = await other.WaitForAsync("composerDrafts", since: mark);
        msg.Value<bool>("full").Should().BeFalse();
        var d = Single(msg);
        d.Value<string>("conversationId").Should().Be(conversation.Id, "網頁用這台的 id");
        d.Value<string>("text").Should().Be("幫我看一下");
        d.Value<long>("rev").Should().Be(0, "這台打的");
        d.Value<string>("from").Should().BeNull();
        h.Main.Messages("composerDrafts", mainMark).Should().BeEmpty("送來的視窗不送回去");
        bridge.Published.Should().Equal((ConversationIds.ToWire(conversation.Id)!, "幫我看一下"));

        h.Command(h.Main, "composerDraft", new { conversationId = conversation.Id, text = "幫我看一下" });
        h.Command(h.Main, "composerDraft", new { conversationId = conversation.Id, text = "  " });
        await h.Ui.DrainAsync();
        bridge.Published.Should().Equal((ConversationIds.ToWire(conversation.Id)!, "幫我看一下"), (ConversationIds.ToWire(conversation.Id)!, ""));
        other.Messages("composerDrafts", mark).Should().HaveCount(2, "一樣的不轉；清掉轉一則空字串");
        Single(other.Messages("composerDrafts", mark)[1]).Value<string>("text").Should().Be("");
    }

    [Fact]
    public async Task 同步政策不是full的對話_只轉給其他視窗_不送伺服器()
    {
        var conversation = Seeded(ConversationSyncPolicies.Metadata);
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var bridge = new DraftBridge();
        h.Ui.Invoke(() => h.Hub.AttachSync(bridge));
        var other = h.AddView("other");
        var mark = other.Mark();

        h.Command(h.Main, "composerDraft", new { conversationId = conversation.Id, text = "機密的字" });

        await other.WaitForAsync("composerDrafts", since: mark);
        bridge.Published.Should().BeEmpty("metadata 的對話內容不上線，打的字也不送");
    }

    [Fact]
    public async Task 疑似權杖的只轉給其他視窗_不送伺服器()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var bridge = new DraftBridge();
        h.Ui.Invoke(() => h.Hub.AttachSync(bridge));
        var other = h.AddView("other");
        var mark = other.Mark();

        h.Command(h.Main, "composerDraft", new { conversationId = conversation.Id, text = "金鑰是 " + "AK" + "IA" + "QWERTYUIOPASDFGH" });

        await other.WaitForAsync("composerDrafts", since: mark);
        bridge.Published.Should().BeEmpty("打進輸入框的金鑰不離開這台");
    }

    [Fact]
    public async Task 其他電腦的對話_那台同步了內容的照樣轉給其他視窗並送給同步_沒同步內容的不送()
    {
        // 2026-11-12：副本的指令白名單漏了 composerDraft，在這台對其他電腦的對話打的字一律被「唯讀」擋下、沒有同步出去
        using var h = new HubHarness();
        var bridge = new DraftBridge();
        var synced = new string('b', 32);
        var metaOnly = new string('c', 32);
        var owner = "d_" + new string('B', 26);
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachSync(bridge);
            h.Hub.ShowReplicas = true;
            h.Hub.ApplyReplicaDelta(new ReplicaDelta(
            [
                new ReplicaMeta(synced, owner, new AgentConversation { Id = synced, Provider = AgentProvider.Claude, Title = "別台的對話", AccountId = string.Empty },
                    Running: false, Waiting: false, ItemsAvailable: true),
                new ReplicaMeta(metaOnly, owner, new AgentConversation { Id = metaOnly, Provider = AgentProvider.Claude, Title = "只有清單", AccountId = string.Empty },
                    Running: false, Waiting: false, ItemsAvailable: false),
            ], [], []));
        });
        await h.Ui.DrainAsync();
        h.OnUi(() => h.Hub.IsReplicaConversation(synced)).Should().BeTrue("前提：是其他電腦的對話");
        var other = h.AddView("other");
        var mark = other.Mark();
        var mainMark = h.Main.Mark();

        h.Command(h.Main, "composerDraft", new { conversationId = synced, text = "在這台打給別台的字" });

        var d = Single(await other.WaitForAsync("composerDrafts", since: mark));
        d.Value<string>("conversationId").Should().Be(synced, "其他電腦的對話用線上 id");
        d.Value<string>("text").Should().Be("在這台打給別台的字");
        bridge.Published.Should().Equal((synced, "在這台打給別台的字"));
        h.Main.Messages("toast", mainMark).Should().BeEmpty("不是寫入，不提示唯讀");

        h.Command(h.Main, "composerDraft", new { conversationId = metaOnly, text = "內容沒上線的對話" });
        h.Command(h.Main, "composerDraft", new { conversationId = synced, text = "" });
        await h.Ui.DrainAsync();
        bridge.Published.Should().Equal([(synced, "在這台打給別台的字"), (synced, "")], "那台沒同步內容的不送；送出後清掉的照送");
        other.Messages("composerDrafts", mark).Should().HaveCount(3, "這台的其他視窗三則都收到");
    }

    [Fact]
    public async Task 不在這台的對話與太長的字_略過()
    {
        using var h = new HubHarness();
        var bridge = new DraftBridge();
        h.Ui.Invoke(() => h.Hub.AttachSync(bridge));
        var other = h.AddView("other");
        var mark = other.Mark();

        h.Command(h.Main, "composerDraft", new { conversationId = new string('a', 32), text = "x" });
        h.Command(h.Main, "composerDraft", new { conversationId = (string?)null, text = "x" });
        await h.Ui.DrainAsync();

        other.Messages("composerDrafts", mark).Should().BeEmpty();
        bridge.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task 伺服器送來的_轉給每個視窗_舊的rev不要_還沒送成功的不蓋掉()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var wire = ConversationIds.ToWire(conversation.Id)!;
        var replica = new string('b', 32);
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.ApplyComposerDrafts([new ComposerDraftView(wire, "手機打的", 10, 1, "網頁版"), new ComposerDraftView(replica, "別台的對話", 11, 1, "筆電")],
            full: false, pending: []));
        var msg = await h.Main.WaitForAsync("composerDrafts", since: mark);
        var drafts = ((JArray)msg["drafts"]!).Cast<JObject>().ToList();
        drafts.Select(d => d.Value<string>("conversationId")).Should().Equal(conversation.Id, replica);
        drafts[0].Value<string>("from").Should().Be("網頁版");
        drafts[0].Value<long>("rev").Should().Be(10);

        mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.ApplyComposerDrafts([new ComposerDraftView(wire, "比較舊的", 9, 1, "筆電")], full: false, pending: []));
        h.Ui.Invoke(() => h.Hub.ApplyComposerDrafts([new ComposerDraftView(wire, "伺服器的", 12, 1, "筆電")], full: false, pending: [wire]));
        await h.Ui.DrainAsync();
        h.Main.Messages("composerDrafts", mark).Should().BeEmpty("舊的 rev、這台還沒送成功的都不轉");
    }

    [Fact]
    public async Task 整份的_沒列出的伺服器草稿當成清掉_這台打的與還沒送成功的不動()
    {
        var a = Seeded();
        var b = Seeded();
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(a);
            store.SaveConversation(b);
        });
        var wireA = ConversationIds.ToWire(a.Id)!;
        var wireB = ConversationIds.ToWire(b.Id)!;
        var pendingId = new string('c', 32);
        h.Ui.Invoke(() => h.Hub.ApplyComposerDrafts([new ComposerDraftView(wireA, "伺服器的", 5, 1, "筆電"), new ComposerDraftView(pendingId, "p", 6, 1, "筆電")],
            full: false, pending: []));
        h.Command(h.Main, "composerDraft", new { conversationId = b.Id, text = "這台打的" });
        await h.Ui.DrainAsync();
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.ApplyComposerDrafts([], full: true, pending: [pendingId]));

        var msg = await h.Main.WaitForAsync("composerDrafts", since: mark);
        var d = Single(msg);
        d.Value<string>("conversationId").Should().Be(a.Id);
        d.Value<string>("text").Should().Be("", "伺服器沒有了：在別處送出或過期");

        // 新開的視窗：init 之後收整份（只剩這台打的與還沒送成功的）
        var late = h.AddView("late");
        h.Command(late, "ready", new { });
        var full = await late.WaitForAsync("composerDrafts");
        full.Value<bool>("full").Should().BeTrue();
        ((JArray)full["drafts"]!).Select(x => x.Value<string>("conversationId")).Should().BeEquivalentTo([b.Id, pendingId]);
        _ = wireB;
    }

    /// <summary>只記 OnComposerDraft 的同步橋</summary>
    private sealed class DraftBridge : ISyncBridge
    {
        public List<(string ConversationId, string Text)> Published { get; } = [];

        void ISyncBridge.OnComposerDraft(string conversationId, string text) => Published.Add((conversationId, text));

        public bool HandleCommand(IAgentView view, string type, JObject msg) => false;

        public void OnAccountUpdated(string accountId)
        {
        }

        public void OnAccountsReordered()
        {
        }

        public void OnRoamingSettingChanged(string key, JToken value)
        {
        }

        public JObject? InitRemote() => null;

        public bool IsStarting => false;

        public JObject? DevicesCli() => null;

        public void DecorateAccounts(JArray accounts)
        {
        }

        public CatalogAccountTemplate? ClaimCatalogAccount(string? logicalAccountId, out string failure)
        {
            failure = string.Empty;
            return null;
        }

        public string? ExpectedEmail(string localAccountId) => null;
    }
}
