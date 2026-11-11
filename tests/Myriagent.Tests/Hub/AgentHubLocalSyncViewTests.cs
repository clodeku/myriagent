using Myriagent.Accounts;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞的同步檢視（WP4.6 計畫步 9；D22、D24、D25）：副本在清單與標頭帶擁有者電腦與那邊的狀態、不被當成「帳戶已移除」；
/// 掛著接收者的本機對話在標頭帶 conversationSync，政策或伺服器刪除改了就重送標頭；沒有接收者的本機對話逐鍵不變。
/// 真的中樞（HubHarness）＋帶盒子的假引擎＋真的發布者（同 AgentHubReplicaTests）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubLocalSyncViewTests
{
    private const string Owner = "d_01J9Q0000000000000000000OW";

    private const string IdA = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    /// <summary>RenderConversationList 原本的鍵（WP4.6 之前，加上自動重新命名的 titling、等背景工作回報的 background；本機對話一律逐鍵相同）</summary>
    private static readonly string[] SummaryKeys =
        ["id", "accountId", "provider", "title", "group", "updatedAt", "workingDirectory", "customFolder", "running", "waiting", "background", "programCount", "unread", "titling"];

    /// <summary>
    /// RenderHeader 原本的鍵（WP4.6 之前，加上執行中插入訊息的 steer、對話上方常駐的 lastPrompt、上下文用量提示的 context、背景工作的 background、代理另開的程式 programs、
    /// 執行中選了另一個帳戶的 pendingAccount、一輪進行中即時 token 數的 turnLive、@ 其他帳戶委派的 accountDelegation）
    /// </summary>
    private static readonly string[] HeaderKeys =
    [
        "id", "accountId", "provider", "title", "model", "effort", "defaultEffort", "permission", "permissionModes", "models", "commands", "workingDirectory",
        "customFolder", "recentFolders", "isDraft", "running", "steer", "status", "titling", "startedAt", "background", "programs", "lastTurn", "turnLive", "context", "accountMissing",
        "accountDelegation", "pendingAccount", "restart", "lastPrompt",
    ];

    /// <summary>這台的 Claude 對話另外帶主代理與輸出風格（AgentHub.ClaudeCustomizations.cs；副本不帶）</summary>
    private static readonly string[] LocalClaudeHeaderKeys = [.. HeaderKeys, "agent", "outputStyle", "outputStyles", "idleSince"];

    private sealed class Rig : IDisposable
    {
        public Rig(bool attach = true, Func<AppSettings>? loadSettings = null)
        {
            H = new HubHarness(loadSettings: loadSettings);
            Engine = new RecordingSyncEngine { Conversations = Box };
            Publisher = H.OnUi(() => ConversationPublisher.TryCreate(H.Hub, Engine, Time))!;
            if (attach)
                Attach();
        }

        public HubHarness H { get; }

        public ManualTimeProvider Time { get; } = new();

        public ConversationChangeBox Box { get; } = new();

        public RecordingSyncEngine Engine { get; }

        public ConversationPublisher Publisher { get; }

        public FakeView Main => H.Main;

        public void Attach() => H.Ui.Invoke(() => H.Hub.AttachConversationSink(Publisher));

        public void Show() => H.Ui.Invoke(() => H.Hub.ShowReplicas = true);

        public void Deliver(ReplicaDelta delta) => H.Ui.Invoke(() => Publisher.ApplyReplicaDelta(delta));

        /// <summary>開著這個對話，回 open 的回覆的標頭</summary>
        public JObject OpenHeader(string id)
        {
            var mark = Main.Mark();
            H.Command(Main, "open", new { conversationId = id });
            return (JObject)Main.Messages("conversation", mark, id).Single()["header"]!;
        }

        /// <summary>
        /// 等之前排定的合併送出（ScheduleFlush 延 50 ms；開始、完成一輪與 open 都會排）送完：之後收到的標頭才是測試的改動送的。
        /// 不等的話排著的那一次會順便帶上新的值，拿掉「改了就重送」也照樣通過
        /// </summary>
        public async Task SettleAsync()
        {
            await Task.Delay(200);
            await H.Ui.DrainAsync();
        }

        public void Dispose() => H.Dispose();
    }

    /// <summary>照引擎的路徑組副本的 meta（同 AgentHubReplicaTests.Meta；<paramref name="updatedAt"/> 是擁有者電腦的時鐘）</summary>
    private static ReplicaMeta Meta(string id, bool running = false, bool waiting = false, string policy = ConversationSyncPolicies.Full, long updatedAt = 1791500060000,
        string? folder = @"C:\owner\repo", string? model = "gpt-5", IReadOnlyList<ConversationModelOption>? models = null)
        => ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 3,
            ConversationWire.Serialize(new ConversationMetaPayload("Codex", "acc-owner", "擁有者的對話", "FullAccess", model, "high", folder is not null, folder, policy,
                1791500000000, updatedAt, running, waiting, models)), false, 7))!;

    /// <summary>conversations 訊息（items）或 init（conversations）裡這個對話的那一筆</summary>
    private static JObject Row(JObject message, string id)
        => (JObject)((JArray)(message["items"] ?? message["conversations"])!).Single(i => i.Value<string>("id") == id);

    private static IEnumerable<string> Keys(JObject o) => o.Properties().Select(p => p.Name);

    [Fact]
    public async Task 副本的清單項目帶deviceId與擁有者的running()
    {
        using var rig = new Rig();
        rig.Show();
        var mark = rig.Main.Mark();

        rig.Deliver(new ReplicaDelta([Meta(IdA, running: true, waiting: true)], [], [], Full: true));

        var list = await rig.Main.WaitForAsync("conversations", m => ((JArray)m["items"]!).Any(i => i.Value<string>("id") == IdA), mark);
        var row = Row(list, IdA);
        row.Value<string>("deviceId").Should().Be(Owner);
        row.Value<bool>("running").Should().BeTrue("擁有者那邊執行中（只供顯示；這台的 RunningCount 不算它）");
        row.Value<bool>("waiting").Should().BeTrue();
        Keys(row).Should().Equal(SummaryKeys.Append("deviceId"), "名稱與在線不填（網頁從 remote.devices 查）");
        rig.H.OnUi(() => rig.H.Hub.RunningCount).Should().Be(0);
    }

    [Fact]
    public void 副本標頭_accountMissing為false_不顯示本機最近資料夾()
    {
        var recent = Path.GetTempPath();
        using var rig = new Rig(loadSettings: () => new AppSettings { RecentFolders = [recent] });
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA, running: true)], [], [], Full: true));

        var header = rig.OpenHeader(IdA);

        header.Value<bool>("accountMissing").Should().BeFalse("副本的 AccountId 是空字串，不是帳戶被移除（審查 4）");
        ((JArray)header["recentFolders"]!).Should().BeEmpty("不是這台的資料夾");
        ((JArray)header["commands"]!).Should().BeEmpty();
        header.Value<string>("deviceId").Should().Be(Owner);
        header.Value<string>("workingDirectory").Should().Be(@"C:\owner\repo", "擁有者電腦上的自訂資料夾，只供顯示（同瀏覽器版）");
        header.Value<bool>("customFolder").Should().BeTrue();
        rig.H.Conversation(IdA)!.WorkingDirectory.Should().BeNull("不當成這台的資料夾（WP4.3 的 ShownWorkingDirectory）");
        header.ContainsKey("conversationSync").Should().BeFalse("副本的政策只能在擁有者那台改");

        // 對照：本機對話照舊看得到最近的資料夾
        var local = rig.H.NewConversation(rig.Main, rig.H.Claude);
        var draftHeader = (JObject)rig.Main.Last("conversation", local)!["header"]!;
        ((JArray)draftHeader["recentFolders"]!).Select(f => f.Value<string>()).Should().Contain(recent);
    }

    [Fact]
    public async Task 擁有者換了工作資料夾_開著副本的視窗收到新的資料夾()
    {
        // 桌面遠端操作：從這台對副本送 setFolder／resetFolder，那台換好之後發布 meta；這台開著的標頭與清單要跟著變，
        // 否則看起來像沒換成功，「恢復預設工作區」也一直是停用的（customFolder 一律 false）
        using var rig = new Rig();
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA, folder: null)], [], [], Full: true));
        var opened = rig.OpenHeader(IdA);
        opened.Value<bool>("customFolder").Should().BeFalse("擁有者那邊是對話自己的 workspace");
        opened["workingDirectory"]!.Type.Should().Be(JTokenType.Null);
        await rig.SettleAsync();

        var mark = rig.Main.Mark();
        rig.Deliver(new ReplicaDelta([Meta(IdA, folder: @"D:\work\proj")], [], []));
        var changed = await rig.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("customFolder"), mark, conversationId: IdA);
        changed["header"]!.Value<string>("workingDirectory").Should().Be(@"D:\work\proj");
        var list = await rig.Main.WaitForAsync("conversations", m => ((JArray)m["items"]!).Any(i => i.Value<string>("id") == IdA), mark);
        Row(list, IdA).Value<string>("workingDirectory").Should().Be(@"D:\work\proj", "側欄的提示也是那台的資料夾");

        await rig.SettleAsync();
        var reset = rig.Main.Mark();
        rig.Deliver(new ReplicaDelta([Meta(IdA, folder: null)], [], []));
        var back = await rig.Main.WaitForAsync("header", m => !m["header"]!.Value<bool>("customFolder"), reset, conversationId: IdA);
        back["header"]!["workingDirectory"]!.Type.Should().Be(JTokenType.Null, "恢復預設工作區");
    }

    [Fact]
    public async Task 副本的模型選單是擁有者發布的清單_那台換了模型_開著的視窗跟著變()
    {
        // 從這台改那台對話的模型：選單要列那台那個帳戶實際可選的（Codex 的內建選單只有「預設」，選不了別的）；
        // 那台換好之後發布 meta，這台開著的標頭跟著變
        ConversationModelOption[] published = [new("gpt-5", "GPT-5", "旗艦", ["low", "high"]), new("gpt-5-mini", "GPT-5 mini", null, ["low"])];
        using var rig = new Rig();
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA, models: published)], [], [], Full: true));

        var opened = rig.OpenHeader(IdA);
        var models = (JArray)opened["models"]!;
        models.Select(m => m.Value<string>("value")).Should().Equal("default", "gpt-5", "gpt-5-mini");
        models[1].Value<string>("label").Should().Be("GPT-5");
        models[1].Value<string>("description").Should().Be("旗艦");
        models[1]["efforts"]!.Values<string>().Should().Equal("low", "high");
        models[0]["efforts"]!.Values<string>().Should().Equal(["low", "high"], "補的「預設」用第一項的強度（同本機的 EnsureDefaultModel）");
        opened.Value<string>("model").Should().Be("gpt-5");
        await rig.SettleAsync();

        var mark = rig.Main.Mark();
        rig.Deliver(new ReplicaDelta([Meta(IdA, model: "gpt-5-mini", models: published)], [], []));
        var changed = await rig.Main.WaitForAsync("header", m => m["header"]!.Value<string>("model") == "gpt-5-mini", mark, conversationId: IdA);
        changed["header"]!["model"]!.Value<string>().Should().Be("gpt-5-mini");
        ((JObject)changed["header"]!).ContainsKey("models").Should().BeFalse("清單沒變：標頭更新省略，網頁沿用上一份（HeaderListRules）");

        // 舊版的擁有者（沒送 models）：照舊用內建的預設選單
        await rig.SettleAsync();
        var old = rig.Main.Mark();
        rig.Deliver(new ReplicaDelta([Meta(IdA, models: null)], [], []));
        var fallback = await rig.Main.WaitForAsync("header", m => m["header"]!.Value<string>("model") == "gpt-5", old, conversationId: IdA);
        ((JArray)fallback["header"]!["models"]!).Select(m => m.Value<string>("value"))
            .Should().Equal(AgentHub.DefaultModels(Myriagent.Agents.AgentProvider.Codex).Select(m => m.Value));
    }

    [Fact]
    public void 副本標頭帶replica_running是false()
    {
        using var rig = new Rig();
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA, running: true, waiting: true, policy: ConversationSyncPolicies.Metadata)], [], [], Full: true));

        var header = rig.OpenHeader(IdA);

        header.Value<bool>("running").Should().BeFalse("這台沒有副本的程序");
        JToken.DeepEquals(header["replica"], JObject.Parse("""{"running":true,"waiting":true,"itemsAvailable":false,"accountId":null}"""))
            .Should().BeTrue(header["replica"]!.ToString());
        Keys(header).Should().Equal(HeaderKeys.Append("deviceId").Append("replica"));
    }

    /// <summary>只換副本帳戶的同步橋（目錄的連結：擁有者電腦＋那台的本機帳戶 → 邏輯帳戶）</summary>
    private sealed class CatalogBridge : ISyncBridge
    {
        public Dictionary<(string Device, string Local), string> Links { get; } = [];

        string? ISyncBridge.ReplicaLogicalAccount(string? ownerDeviceId, string? ownerAccountId)
            => ownerDeviceId is not null && ownerAccountId is not null && Links.TryGetValue((ownerDeviceId, ownerAccountId), out var logical) ? logical : null;

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

    [Fact]
    public async Task 副本標頭的replica_accountId是擁有者那台的帳戶連結到的邏輯帳戶_目錄換了就重送()
    {
        // 2026-10-25：從這台換其他電腦的對話的帳戶，晶片與選單要知道那台現在用的是哪個邏輯帳戶（中繼資料只帶那台的本機帳戶 id）
        using var rig = new Rig();
        var bridge = new CatalogBridge();
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachSync(bridge));
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));

        var opened = rig.OpenHeader(IdA);
        opened["replica"]!["accountId"]!.Type.Should().Be(JTokenType.Null, "目錄還沒到：不明");
        opened.Value<string>("accountId").Should().BeEmpty("標頭自己的 accountId 照舊是空字串（網頁拿它判斷這台的帳戶）");
        await rig.SettleAsync();

        // 目錄到了（協調者套用一頁目錄之後呼叫 RefreshSyncDecorations）：開著的副本重送標頭
        var mark = rig.Main.Mark();
        rig.H.Ui.Invoke(() =>
        {
            bridge.Links[(Owner, "acc-owner")] = "la_0123456789ABCDEFGHJKMNPQRS";
            rig.H.Hub.RefreshSyncDecorations();
        });
        var changed = await rig.Main.WaitForAsync("header", m => m["header"]!["replica"]?["accountId"]?.Type == JTokenType.String, mark, conversationId: IdA);
        changed["header"]!["replica"]!.Value<string>("accountId").Should().Be("la_0123456789ABCDEFGHJKMNPQRS");
    }

    [Fact]
    public async Task 本機對話的清單與標頭逐鍵和裝飾之前相同()
    {
        // 沒有接收者（未配對）：本機對話的清單與標頭只有原本的鍵
        using var rig = new Rig(attach: false);
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.H.Command(rig.Main, "ready");

        var init = rig.Main.Last("init")!;
        Keys(Row(init, id)).Should().Equal(SummaryKeys);
        Keys(rig.OpenHeader(id)).Should().Equal(LocalClaudeHeaderKeys);

        // 掛著接收者：清單照舊逐鍵相同，標頭只多 conversationSync
        rig.Attach();
        var mark = rig.Main.Mark();
        rig.H.Command(rig.Main, "rename", new { conversationId = id, title = "改名" });
        var list = await rig.Main.WaitForAsync("conversations", m => ((JArray)m["items"]!).Any(i => i.Value<string>("title") == "改名"), mark);
        Keys(Row(list, id)).Should().Equal(SummaryKeys);
        Keys(rig.OpenHeader(id)).Should().Equal(LocalClaudeHeaderKeys.Append("conversationSync"));
    }

    [Fact]
    public void 有接收者時標頭帶conversationSync_沒有時沒有這個鍵()
    {
        using var rig = new Rig(attach: false);
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Codex);
        rig.H.CompleteTurn(s);

        rig.OpenHeader(id).ContainsKey("conversationSync").Should().BeFalse("沒有接收者（未配對）");

        rig.Attach();
        var header = rig.OpenHeader(id);
        JToken.DeepEquals(header["conversationSync"], JObject.Parse("""{"policy":"full","choices":["full","metadata","localOnly"],"serverDeleted":false}"""))
            .Should().BeTrue(header["conversationSync"]!.ToString());

        // 草稿沒有政策可選
        var draft = rig.H.NewConversation(rig.Main, rig.H.Claude);
        ((JObject)rig.Main.Last("conversation", draft)!["header"]!).ContainsKey("conversationSync").Should().BeFalse("草稿不設同步政策");
    }

    [Fact]
    public async Task 接收者換了_開著的對話重送標頭()
    {
        using var rig = new Rig(attach: false);
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.OpenHeader(id);
        await rig.SettleAsync();
        var mark = rig.Main.Mark();

        // 協調者的 AttachPublisher 在 AttachConversationSink 之前呼叫 RefreshSyncHeaders：合併送出時接收者已經換好
        rig.H.Ui.Invoke(() =>
        {
            rig.H.Hub.RefreshSyncHeaders();
            rig.H.Hub.AttachConversationSink(rig.Publisher);
        });

        var pushed = await rig.Main.WaitForAsync("header", m => m["header"]!["conversationSync"] is JObject, mark, conversationId: id);
        pushed["header"]!["conversationSync"]!.Value<string>("policy").Should().Be("full");

        var detached = rig.Main.Mark();
        rig.H.Ui.Invoke(() =>
        {
            rig.H.Hub.RefreshSyncHeaders();
            rig.H.Hub.AttachConversationSink(null);
        });
        var removed = await rig.Main.WaitForAsync("header", since: detached, conversationId: id);
        ((JObject)removed["header"]!).ContainsKey("conversationSync").Should().BeFalse("解除配對之後沒有政策選單");
    }

    [Fact]
    public async Task 伺服器刪除的choices只有localOnly()
    {
        using var rig = new Rig();
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.OpenHeader(id);
        await rig.SettleAsync();
        var mark = rig.Main.Mark();

        rig.H.OnUi(() => rig.H.Hub.MarkServerDeleted(id, 1791600000000)).Should().BeTrue();

        var header = await rig.Main.WaitForAsync("header", m => m["header"]!["conversationSync"]?.Value<bool>("serverDeleted") == true, mark, conversationId: id);
        JToken.DeepEquals(header["header"]!["conversationSync"], JObject.Parse("""{"policy":"localOnly","choices":["localOnly"],"serverDeleted":true}"""))
            .Should().BeTrue(header["header"]!["conversationSync"]!.ToString());
    }

    [Fact]
    public async Task SetConversationSyncPolicy之後重送header()
    {
        using var rig = new Rig();
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.OpenHeader(id);
        await rig.SettleAsync();
        var mark = rig.Main.Mark();

        rig.H.OnUi(() => rig.H.Hub.SetConversationSyncPolicy(id, ConversationSyncPolicies.Metadata)).Should().BeTrue();

        var header = await rig.Main.WaitForAsync("header", m => m["header"]!["conversationSync"]?.Value<string>("policy") == "metadata", mark, conversationId: id);
        header["header"]!["conversationSync"]!["choices"]!.Values<string>().Should().Equal("full", "metadata", "localOnly");

        // 改成 localOnly（等於從伺服器刪除；旁檔由發布者在 conv.deleted 之前寫）：標頭照樣跟著改
        var local = rig.Main.Mark();
        rig.H.OnUi(() => rig.H.Hub.SetConversationSyncPolicy(id, ConversationSyncPolicies.LocalOnly)).Should().BeTrue();
        await rig.Main.WaitForAsync("header", m => m["header"]!["conversationSync"]?.Value<string>("policy") == "localOnly", local, conversationId: id);
    }

    [Fact]
    public async Task DLP暫停之後重送header_policy是paused()
    {
        // 權杖 DLP 命中（引擎經發布者的 UI 埠，回 UI 執行緒改 paused）：政策選單跟著顯示 paused，選 full／metadata／localOnly 就是解除
        using var rig = new Rig();
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.OpenHeader(id);
        await rig.SettleAsync();
        var mark = rig.Main.Mark();

        await Task.Run(() => ((IConversationUiPort)rig.Publisher).PauseForDlp(id));

        var header = await rig.Main.WaitForAsync("header", m => m["header"]!["conversationSync"]?.Value<string>("policy") == ConversationSyncPolicies.Paused, mark, conversationId: id);
        header["header"]!["conversationSync"]!["choices"]!.Values<string>().Should().Equal("full", "metadata", "localOnly");
    }

    [Fact]
    public async Task 副本兩千筆_本機對話不被擠出清單()
    {
        // 回歸測試（D24；WP4.3 已把本機與副本的上限分開）：副本的時鐘比較新、數量是上限的兩倍，本機的對話照樣在清單
        using var rig = new Rig();
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.Show();
        var replicas = Enumerable.Range(0, 2 * AgentHub.ReplicaListMax)
            .Select(i => Meta(i.ToString("x32"), updatedAt: 1893456000000 + i))
            .ToList();
        var mark = rig.Main.Mark();

        rig.Deliver(new ReplicaDelta(replicas, [], [], Full: true));

        var list = await rig.Main.WaitForAsync("conversations", m => ((JArray)m["items"]!).Count > 1, mark, timeoutMs: 10000);
        var items = (JArray)list["items"]!;
        items.Select(i => i.Value<string>("id")).Should().Contain(id, "本機的對話不被副本擠出清單");
        items.Count(i => i["deviceId"] is not null).Should().Be(AgentHub.ReplicaListMax, "副本另有自己的上限");
        items.Count.Should().Be(AgentHub.ReplicaListMax + 1);
    }
}
