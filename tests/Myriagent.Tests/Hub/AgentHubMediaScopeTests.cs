using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞給媒體轉送的對話快照（WP4.7 計畫步 8，D26）：本機擁有、完整同步的對話才看得到（本機剛改成 metadata、伺服器還不知道的空窗也擋），
/// 草稿、複本、其他電腦擁有的一律看不到；項目已載入時以附件 id 或媒體路徑解析出檔案，沒載入時交給 MediaServer 讀 items.json
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubMediaScopeTests
{
    private const string Me = "d_01J9Q0000000000000000000ME";

    private static AgentConversation Seeded(AgentProvider provider = AgentProvider.Claude, string? owner = null) => new()
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
        Title = "有附件的對話",
        SessionId = Guid.NewGuid().ToString(),
        SessionStarted = true,
        OwnerDeviceId = owner,
    };

    /// <summary>一個附件與一個代理產出的媒體（路徑照存檔的樣子放在對話的 attachments\ 與工作資料夾）</summary>
    private static List<AgentChatItem> Items(ConversationStore store, string id) =>
    [
        new()
        {
            Id = "item-1",
            Kind = AgentItemKind.User,
            Text = "看這張圖",
            Attachments = [new AgentAttachment { Id = "att-1", FileName = "螢幕截圖.png", StoredPath = Path.Combine(store.GetAttachmentDirectory(id), "a1.png"), Kind = AgentAttachmentKind.Image }],
        },
        new()
        {
            Id = "item-2",
            Kind = AgentItemKind.Assistant,
            Text = "畫好了",
            MediaPaths = [Path.Combine(store.GetDefaultWorkingDirectory(id), "out", "chart.png")],
        },
    ];

    private static MediaScopeQuery Query(string id, string itemId = "item-1", string? attachmentId = "att-1", string? mediaPath = null, string device = Me)
        => new(ConversationIds.ToWire(id)!, itemId, attachmentId, mediaPath, device);

    private static MediaScopeSnapshot? Scope(HubHarness h, MediaScopeQuery query) => h.OnUi(() => ((IMediaScopeSource)h.Hub).GetMediaScope(query));

    private static (HubHarness H, string Id) Create(AgentConversation? conversation = null)
    {
        var c = conversation ?? Seeded();
        var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(c);
            store.SaveItems(c.Id, Items(store, c.Id));
        });
        return (h, c.Id);
    }

    [Fact]
    public void 完整同步_項目沒載入_看得到_路徑與禁止清單的資料夾都在()
    {
        var (h, id) = Create();
        using var _ = h;

        var scope = Scope(h, Query(id))!;

        scope.Visible.Should().BeTrue();
        scope.ConversationId.Should().Be(id);
        scope.ItemsLoaded.Should().BeFalse("中樞不在 UI 執行緒讀檔：交給 MediaServer 讀 items.json");
        scope.Resolved.Should().BeNull();
        scope.ConversationDirectory.Should().Be(h.Store.GetConversationDirectory(id));
        scope.AttachmentDirectory.Should().Be(h.Store.GetAttachmentDirectory(id));
        scope.DefaultWorkingDirectory.Should().Be(h.Store.GetDefaultWorkingDirectory(id));
        scope.WorkingDirectory.Should().Be(h.Store.GetDefaultWorkingDirectory(id), "沒選資料夾的對話用預設的工作資料夾");
        scope.Folders.AppDataRoot.Should().Be(AppPaths.Root);
        scope.Folders.UserProfile.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        scope.Folders.LocalAppData.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        scope.Folders.AccountConfigDirectories.Should().BeEquivalentTo([h.Claude2.ConfigDirectory, h.Codex.ConfigDirectory], "系統預設帳戶沒有自己的資料夾");
        Scope(h, Query(id))!.ItemsLoaded.Should().BeFalse("問快照不載入項目");
    }

    [Fact]
    public void 開過的對話_附件以id解析_代理媒體要在MediaPaths()
    {
        var (h, id) = Create();
        using var _ = h;
        h.Command(h.Main, "open", new { conversationId = id });
        var chart = Path.Combine(h.Store.GetDefaultWorkingDirectory(id), "out", "chart.png");

        var attachment = Scope(h, Query(id))!;
        var media = Scope(h, Query(id, "item-2", null, chart.ToUpperInvariant()))!;
        var gone = Scope(h, Query(id, "item-1", "att-9"))!;
        var otherItem = Scope(h, Query(id, "item-2", null, Path.Combine(h.Store.GetDefaultWorkingDirectory(id), "out", "other.png")))!;

        attachment.ItemsLoaded.Should().BeTrue();
        attachment.Resolved.Should().Be((Path.Combine(h.Store.GetAttachmentDirectory(id), "a1.png"), "螢幕截圖.png"));
        media.Resolved.Should().NotBeNull();
        media.Resolved!.Value.FileName.Should().Be("CHART.PNG");
        media.MediaSequence.Should().Be(1, "Codex 產生圖片的複本檔名用（步 9，Q2-A）");
        attachment.MediaSequence.Should().BeNull("附件不需要序號");
        gone.Visible.Should().BeTrue();
        gone.Resolved.Should().BeNull("附件已經不在");
        otherItem.Resolved.Should().BeNull("不在項目的 MediaPaths");
    }

    [Fact]
    public void 抽屜與檔案的參照_不解析項目()
    {
        var (h, id) = Create();
        using var _ = h;
        h.Command(h.Main, "open", new { conversationId = id });

        var list = Scope(h, Query(id, MediaServerRules.ListItemId, null, @"C:\repo\shot.png"))!;
        var file = Scope(h, Query(id, MediaServerRules.FileItemId, null, @"C:\repo\notes.txt"))!;

        list.Visible.Should().BeTrue();
        list.ItemsLoaded.Should().BeFalse("路徑由參照的列提供");
        list.Resolved.Should().BeNull();
        file.Visible.Should().BeTrue();
        file.Resolved.Should().BeNull();
    }

    [Fact]
    public void 本機改成metadata_伺服器還不知道_也看不到()
    {
        var (h, id) = Create();
        using var _ = h;
        Scope(h, Query(id))!.Visible.Should().BeTrue("陽性對照");

        h.OnUi(() => h.Hub.SetConversationSyncPolicy(id, ConversationSyncPolicies.Metadata)).Should().BeTrue();

        var scope = Scope(h, Query(id))!;
        scope.Visible.Should().BeFalse("合併就生效：不等伺服器處理政策事件");
        scope.Should().BeEquivalentTo(MediaScopeSnapshot.Hidden(id));
    }

    [Fact]
    public void localOnly的對話看不到_Antigravity的full對話看得到()
    {
        var (h, id) = Create();
        using var _ = h;
        h.OnUi(() => h.Hub.SetConversationSyncPolicy(id, ConversationSyncPolicies.LocalOnly)).Should().BeTrue();
        Scope(h, Query(id))!.Visible.Should().BeFalse();

        var (h2, agy) = Create(Seeded(AgentProvider.Antigravity));
        using var __ = h2;
        Scope(h2, Query(agy))!.Visible.Should().BeTrue("2026-10-22 起 Antigravity 的對話也同步（伺服器只會為它知道的對話來要媒體）");
        h2.OnUi(() => h2.Hub.SetConversationSyncPolicy(agy, ConversationSyncPolicies.LocalOnly)).Should().BeTrue();
        Scope(h2, Query(agy))!.Visible.Should().BeFalse();
    }

    [Fact]
    public void 複本_草稿_其他電腦擁有的對話看不到()
    {
        var (h, id) = Create(Seeded(owner: Me));
        using var _ = h;
        Scope(h, Query(id))!.Visible.Should().BeTrue("擁有者是這台");
        Scope(h, Query(id, device: "d_01J9Q0000000000000000OTHER"))!.Visible.Should().BeFalse("擁有者是別台");

        h.OnUi(() =>
        {
            h.Hub.MarkReplicaForTest(id);
            return 0;
        });
        Scope(h, Query(id))!.Visible.Should().BeFalse("複本");

        var draft = h.NewConversation(h.Main, h.Claude);
        Scope(h, Query(draft, attachmentId: null)).Should().BeEquivalentTo(MediaScopeSnapshot.Hidden(draft), "草稿還沒發布");
    }

    [Fact]
    public void 對話不存在回null()
    {
        var (h, _) = Create();
        using var __ = h;

        Scope(h, new MediaScopeQuery(new string('e', 32), "item-1", "att-1", null, Me)).Should().BeNull();
    }
}
