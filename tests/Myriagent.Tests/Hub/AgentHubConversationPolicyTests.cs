using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞的同步政策指令與伺服器刪除的旁檔（WP4.3 計畫步 6，D25、D35；擁有者 2026-10-19 的答 4：已從伺服器刪除的對話不能再改回同步）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubConversationPolicyTests
{
    private const long At = 1791600000000;

    private static AgentConversation Seeded(AgentProvider provider = AgentProvider.Claude, string? policy = null) => new()
    {
        AccountId = provider switch
        {
            AgentProvider.Claude => "acc-claude",
            AgentProvider.Codex => "acc-codex",
            AgentProvider.Antigravity => "local-antigravity",
            AgentProvider.Grok => "local-grok",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        },
        Provider = provider,
        Title = "上次的對話",
        SessionId = Guid.NewGuid().ToString(),
        SessionStarted = true,
        SyncPolicy = policy,
    };

    private static bool Set(HubHarness h, string id, string? policy) => h.OnUi(() => h.Hub.SetConversationSyncPolicy(id, policy));

    private static string? Effective(HubHarness h, string id) => h.OnUi(() => h.Hub.GetEffectiveSyncPolicy(id));

    private static string Dir(HubHarness h, string id) => h.Store.GetConversationDirectory(id);

    private static string? MetaPolicy(HubHarness h, string id)
        => JObject.Parse(File.ReadAllText(Path.Combine(Dir(h, id), "meta.json")))["SyncPolicy"]?.Value<string>();

    private static string? SidecarPolicy(HubHarness h, string id)
    {
        var path = Path.Combine(Dir(h, id), ConversationStore.SyncPolicyFileName);
        return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)).Value<string>("policy") : null;
    }

    private static string? Reloaded(HubHarness h, string id) => new ConversationStore(h.Store.RootDirectory).LoadConversations().Single(c => c.Id == id).SyncPolicy;

    private static List<string> Toasts(HubHarness h, int since) => h.Main.Messages("toast", since).Select(m => m.Value<string>("text")!).ToList();

    [Fact]
    public void paused改成full_旁檔與meta都是full_重新載入仍是full()
    {
        var conversation = Seeded(policy: ConversationSyncPolicies.Paused);
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        SidecarPolicy(h, id).Should().Be("paused", "陽性對照：DLP 暫停的對話有旁檔");

        Set(h, id, "full").Should().BeTrue("選 full 就是解除暫停");

        MetaPolicy(h, id).Should().Be("full");
        SidecarPolicy(h, id).Should().BeNull("改回 full 刪掉旁檔（沒有旁檔＝full）");
        Reloaded(h, id).Should().Be("full");
        Effective(h, id).Should().Be("full");
    }

    [Fact]
    public void full改成localOnly_旁檔先寫()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        var meta = Path.Combine(Dir(h, id), "meta.json");
        var before = File.ReadAllBytes(meta);

        // meta.json 被占用、寫不進去：同步政策的旁檔已經先寫好，重新載入照樣是 localOnly（中途失敗只會比較嚴格）
        using (new FileStream(meta, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Set(h, id, "localOnly");

        File.ReadAllBytes(meta).Should().Equal(before, "meta.json 沒寫進去");
        SidecarPolicy(h, id).Should().Be("localOnly", "旁檔先寫");
        Reloaded(h, id).Should().Be("localOnly");
    }

    [Theory]
    [InlineData("Antigravity")]
    [InlineData("Grok")]
    public void Antigravity與Grok的對話和其他種類一樣收三種政策(string providerName)
    {
        // 2026-10-22 起四種都同步（沒有 prov1 時由同步引擎延後，不是 localOnly）
        var conversation = Seeded(Enum.Parse<AgentProvider>(providerName));
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        Effective(h, id).Should().Be("full", "沒設過政策＝full");

        Set(h, id, "metadata").Should().BeTrue();
        MetaPolicy(h, id).Should().Be("metadata");
        Set(h, id, "localOnly").Should().BeTrue();
        Effective(h, id).Should().Be("localOnly");
        Set(h, id, "full").Should().BeTrue();
        Effective(h, id).Should().Be("full");
    }

    [Fact]
    public void 設paused被拒()
    {
        var conversation = Seeded(policy: ConversationSyncPolicies.Metadata);
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        var mark = h.Main.Mark();

        Set(h, id, "paused").Should().BeFalse("paused 只由權杖 DLP 設定");
        Set(h, id, "Full").Should().BeFalse("大小寫不同是不認得的值");
        Set(h, id, null).Should().BeFalse();

        MetaPolicy(h, id).Should().Be("metadata");
        Toasts(h, mark).Should().BeEmpty("其他原因只記錄");
    }

    [Fact]
    public void 草稿與複本被拒_不寫檔()
    {
        var replica = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(replica));
        var draft = h.NewConversation(h.Main, h.Claude);

        Set(h, draft, "localOnly").Should().BeFalse();
        Directory.Exists(Dir(h, draft)).Should().BeFalse("草稿不建資料夾");

        h.OnUi(() =>
        {
            h.Hub.MarkReplicaForTest(replica.Id);
            return 0;
        });
        var before = Directory.EnumerateFiles(Dir(h, replica.Id)).ToDictionary(p => p, File.ReadAllBytes);
        Set(h, replica.Id, "localOnly").Should().BeFalse();
        h.OnUi(() => h.Hub.MarkServerDeleted(replica.Id, At)).Should().BeFalse("複本不寫伺服器刪除的旁檔");
        Directory.EnumerateFiles(Dir(h, replica.Id)).ToDictionary(p => p, File.ReadAllBytes).Should().BeEquivalentTo(before, "複本的資料夾一個檔都不動");
    }

    [Fact]
    public void 找不到的對話_不建資料夾()
    {
        using var h = new HubHarness();
        var id = Guid.NewGuid().ToString("N");

        Set(h, id, "full").Should().BeFalse();
        h.OnUi(() => h.Hub.MarkServerDeleted(id, At)).Should().BeFalse();
        Effective(h, id).Should().BeNull();
        Directory.Exists(Dir(h, id)).Should().BeFalse();
    }

    [Fact]
    public void 有伺服器刪除旁檔_設full或metadata被拒_跳一則toast()
    {
        var conversation = Seeded(policy: ConversationSyncPolicies.LocalOnly);
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(conversation);
            store.TryWriteServerDeleted(conversation.Id, At).Should().BeTrue();
        });
        var id = conversation.Id;
        var mark = h.Main.Mark();

        Set(h, id, "full").Should().BeFalse();
        Toasts(h, mark).Should().Equal(ConversationPolicyCommandRules.ServerDeletedReason);

        mark = h.Main.Mark();
        Set(h, id, "metadata").Should().BeFalse();
        Toasts(h, mark).Should().Equal(ConversationPolicyCommandRules.ServerDeletedReason);

        MetaPolicy(h, id).Should().Be("localOnly", "不能改回同步");
        Effective(h, id).Should().Be("localOnly");
    }

    [Fact]
    public void 有伺服器刪除旁檔_設localOnly照收()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(conversation);
            store.TryWriteServerDeleted(conversation.Id, At).Should().BeTrue();
        });
        var id = conversation.Id;
        var mark = h.Main.Mark();
        Effective(h, id).Should().Be("localOnly", "有旁檔一律 localOnly（meta.json 還是 null＝full）");

        Set(h, id, "localOnly").Should().BeTrue();

        MetaPolicy(h, id).Should().Be("localOnly");
        Toasts(h, mark).Should().BeEmpty();
    }

    [Fact]
    public async Task 有伺服器刪除旁檔_換回Claude帳戶_有效政策仍是localOnly()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        Effective(h, id).Should().Be("full", "陽性對照：沒有旁檔時照存檔（null＝full）");

        h.OnUi(() => h.Hub.MarkServerDeleted(id, At)).Should().BeTrue();
        File.Exists(Path.Combine(Dir(h, id), ConversationStore.ServerDeletedFileName)).Should().BeTrue();
        h.Store.IsServerDeleted(id).Should().BeTrue();
        Effective(h, id).Should().Be("localOnly");

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Codex.Id });
        h.Conversation(id)!.Provider.Should().Be(AgentProvider.Codex);
        Effective(h, id).Should().Be("localOnly");
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude.Id });
        h.Conversation(id)!.Provider.Should().Be(AgentProvider.Claude);
        Effective(h, id).Should().Be("localOnly", "換回 Claude 帳戶也不恢復同步");

        await h.RestartAsync();
        Effective(h, id).Should().Be("localOnly", "重開之後從旁檔讀回");
        h.OnUi(() => h.Hub.MarkServerDeleted(id, At + 1)).Should().BeTrue("已經有旁檔：照樣成功");
        JObject.Parse(File.ReadAllText(Path.Combine(Dir(h, id), ConversationStore.ServerDeletedFileName))).Value<long>("at").Should().Be(At, "不重寫，保留第一次的時間");
    }

    [Fact]
    public async Task MarkServerDeleted_meta不存在_回false不建資料夾()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        Effective(h, id).Should().Be("full");

        // 中樞還記得這個對話，但資料夾已經被拿掉（例如另一個程式刪的）
        Directory.Delete(Dir(h, id), recursive: true);
        h.OnUi(() => h.Hub.MarkServerDeleted(id, At)).Should().BeFalse();
        Directory.Exists(Dir(h, id)).Should().BeFalse("不建資料夾");

        var draft = h.NewConversation(h.Main, h.Claude);
        h.OnUi(() => h.Hub.MarkServerDeleted(draft, At)).Should().BeFalse("草稿");
        Directory.Exists(Dir(h, draft)).Should().BeFalse();

        var (started, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Command(h.Main, "delete", new { conversationId = started });
        await h.WaitUntilAsync(() => !Directory.Exists(Dir(h, started)), "對話資料夾刪掉");
        h.OnUi(() => h.Hub.MarkServerDeleted(started, At)).Should().BeFalse("已刪除的對話");
        Directory.Exists(Dir(h, started)).Should().BeFalse();
    }
}
