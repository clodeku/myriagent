using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Rendering;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞的唯讀複本（WP4.3 計畫步 15，D26、D27、F11）：預設不顯示；顯示時是沒有會話、不存檔、不發布的對話；儲存端另外拒寫；
/// 項目照常渲染但不登記本機媒體；本機已有的 id 不建複本。真的中樞（HubHarness）＋帶盒子的假引擎＋真的發布者，
/// 引擎交來的變化在 UI 執行緒經發布者的 <see cref="IConversationUiPort.ApplyReplicaDelta"/> 進中樞（同引擎的 uiActions）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubReplicaTests
{
    private const string Owner = "d_01J9Q0000000000000000000OW";

    private const string IdA = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    private const string IdB = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";

    private const string IdC = "2b3c4d5e6f7a48190a1b2c3d4e5f6a7b";

    private sealed class Rig : IDisposable
    {
        public Rig(Action<ConversationStore>? seedStore = null)
        {
            H = new HubHarness(seedStore: seedStore);
            Engine = new RecordingSyncEngine { Conversations = Box };
            Publisher = H.OnUi(() => ConversationPublisher.TryCreate(H.Hub, Engine, Time))!;
            H.Ui.Invoke(() => H.Hub.AttachConversationSink(Publisher));
        }

        public HubHarness H { get; }

        public ManualTimeProvider Time { get; } = new();

        public ConversationChangeBox Box { get; } = new();

        public RecordingSyncEngine Engine { get; }

        public ConversationPublisher Publisher { get; }

        public void Show() => H.Ui.Invoke(() => H.Hub.ShowReplicas = true);

        /// <summary>引擎交來的變化（引擎的 uiActions／RunOnUiAsync 在 UI 執行緒呼叫發布者）</summary>
        public void Deliver(ReplicaDelta delta) => H.Ui.Invoke(() => Publisher.ApplyReplicaDelta(delta));

        public void Deliver(params ReplicaMeta[] metas) => Deliver(new ReplicaDelta(metas, [], []));

        public void DeliverItems(string id, bool reset, IReadOnlyList<AgentChatItem> upserts, params string[] removed)
            => Deliver(new ReplicaDelta([], [], [new ReplicaItems(id, reset, upserts, removed)]));

        public bool IsReplica(string id) => H.OnUi(() => H.Hub.IsReplicaConversation(id));

        public List<string> ItemIds(string id) => H.Items(id).Select(i => i.Id).ToList();

        /// <summary>發布者的計時器撥快（待送的變化都進盒子），再取走盒子裡的</summary>
        public List<ConversationChange> Flush()
        {
            H.Ui.Invoke(() => Time.Advance(TimeSpan.FromSeconds(5)));
            return Box.TakeAll().ToList();
        }

        public void Dispose() => H.Dispose();
    }

    /// <summary>照引擎的路徑：擁有者發布的 meta 的 payload → replica_meta 的列 → 中樞要的樣子</summary>
    private static ReplicaMeta Meta(string id, string title = "擁有者的對話", string policy = ConversationSyncPolicies.Full, bool running = false, bool waiting = false)
        => ReplicaRules.ToMeta(new ReplicaMetaRow(id, Owner, 3,
            ConversationWire.Serialize(new ConversationMetaPayload("Codex", "acc-owner", title, "FullAccess", "gpt-5", "high", true, @"C:\owner\repo", policy,
                1791500000000, 1791500060000, running, waiting)), false, 7))!;

    /// <summary>照線上的路徑：擁有者的項目 → conv.item 的 payload → WireItemReader 轉回的項目</summary>
    private static AgentChatItem Item(string itemId, long ord, string text, AgentItemKind kind = AgentItemKind.Assistant, AgentItemStatus status = AgentItemStatus.Done,
        Action<AgentChatItem>? edit = null)
    {
        var source = new AgentChatItem { Id = itemId, Ord = ord, Kind = kind, Status = status, Text = text, CreatedAt = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Local) };
        edit?.Invoke(source);
        ConversationWire.TryItem(source, 1, out var wire).Should().BeTrue();
        return ReplicaRules.ToItem(ConversationWire.Serialize(wire!))!;
    }

    private static IEnumerable<string> ListedIds(JObject? conversations)
        => (conversations?["items"] as JArray ?? new JArray()).Select(i => i.Value<string>("id")!);

    /// <summary>暫存資料根底下所有檔案與資料夾（相對路徑）：複本的 id 一個都不能出現</summary>
    private static List<string> Entries(HubHarness h)
        => Directory.Exists(h.Store.RootDirectory)
            ? Directory.EnumerateFileSystemEntries(h.Store.RootDirectory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(h.Store.RootDirectory, p)).ToList()
            : [];

    [Fact]
    public void 預設_複本不進清單()
    {
        using var rig = new Rig();
        rig.H.OnUi(() => rig.H.Hub.ShowReplicas).Should().BeFalse("D27：顯示是之後的工作包");

        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));
        rig.Deliver(Meta(IdB));
        rig.DeliverItems(IdA, true, [Item("i1", 1, "內容")]);

        rig.IsReplica(IdA).Should().BeFalse();
        rig.H.Conversation(IdA).Should().BeNull();
        rig.H.Conversation(IdB).Should().BeNull();
        rig.H.Store.IsReplica(IdA).Should().BeFalse("沒有建立就不登記");
        rig.H.Command(rig.H.Main, "ready");
        var init = rig.H.Main.Last("init")!;
        ((JArray)init["conversations"]!).Select(i => i.Value<string>("id")).Should().NotContain([IdA, IdB]);
        rig.Box.TakeAll().OfType<ReplicaRequested>().Should().BeEmpty("沒打開顯示就不向引擎要複本");
    }

    [Fact]
    public async Task 複本清單等待_init與清單帶replicasPending_第一份整份清單到了才結束()
    {
        // 2026-10-29：同步元件在主視窗載入後才啟動，網頁還原版面時其他電腦的對話還不在清單裡，分頁被丟掉、少了分頁的版面又存回去
        using var rig = new Rig();
        rig.H.Command(rig.H.Main, "ready");
        rig.H.Main.Last("init")!.Value<bool>("replicasPending").Should().BeFalse("中樞預設沒有同步元件，不等");

        rig.H.Ui.Invoke(() => rig.H.Hub.ExpectReplicas());
        rig.H.Command(rig.H.Main, "ready");
        rig.H.Main.Last("init")!.Value<bool>("replicasPending").Should().BeTrue("協調者建立後、複本清單到之前");

        // 還沒打開顯示時引擎交來的整份清單不算（中樞不理它）
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));
        rig.H.OnUi(() => rig.H.Hub.ReplicasPending).Should().BeTrue();

        rig.Show();
        // 增量（不是整份）不算載完
        rig.Deliver(Meta(IdB));
        rig.H.OnUi(() => rig.H.Hub.ReplicasPending).Should().BeTrue("只有第一份整份清單才算載完");

        var mark = rig.H.Main.Mark();
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));
        rig.H.OnUi(() => rig.H.Hub.ReplicasPending).Should().BeFalse();
        var list = await rig.H.Main.WaitForAsync("conversations", m => m.Value<bool?>("replicasPending") == false, mark);
        ListedIds(list).Should().Contain(IdA, "結束等待的那一份清單裡就有複本，網頁照它接上分頁");
    }

    [Fact]
    public async Task 複本清單等待_不會有複本時結束_清單照樣重送()
    {
        using var rig = new Rig();
        rig.H.Ui.Invoke(() => rig.H.Hub.ExpectReplicas());
        var mark = rig.H.Main.Mark();
        // 協調者確定沒有複本（未配對、沒有發布者）
        rig.H.Ui.Invoke(() => rig.H.Hub.SettleReplicas());
        rig.H.OnUi(() => rig.H.Hub.ReplicasPending).Should().BeFalse();
        await rig.H.Main.WaitForAsync("conversations", m => m.Value<bool?>("replicasPending") == false, mark);

        // 關掉顯示（解除配對）也結束
        rig.H.Ui.Invoke(() => rig.H.Hub.ExpectReplicas());
        rig.Show();
        rig.H.Ui.Invoke(() => rig.H.Hub.ShowReplicas = false);
        rig.H.OnUi(() => rig.H.Hub.ReplicasPending).Should().BeFalse();
    }

    [Fact]
    public async Task 打開開關_複本出現_IsReplica為真_沒有會話()
    {
        using var rig = new Rig();
        rig.Show();
        rig.Box.TakeAll().OfType<ReplicaRequested>().Should().ContainSingle().Which.ConversationId.Should().BeNull("打開時要整份清單");

        var mark = rig.H.Main.Mark();
        rig.Deliver(new ReplicaDelta([Meta(IdA, "擁有者的對話", running: true, waiting: true), Meta(IdB, "第二個", ConversationSyncPolicies.Metadata)], [], [], Full: true));

        rig.IsReplica(IdA).Should().BeTrue();
        rig.IsReplica(IdB).Should().BeTrue();
        rig.H.Store.IsReplica(IdA).Should().BeTrue("儲存端登記了（第二道防線）");
        var meta = rig.H.Conversation(IdA)!;
        meta.Title.Should().Be("擁有者的對話");
        meta.AccountId.Should().BeEmpty();
        meta.WorkingDirectory.Should().BeNull();
        meta.OwnerDeviceId.Should().Be(Owner);
        rig.H.Sessions.Should().BeEmpty("複本不建會話");
        rig.H.IsRunning(IdA).Should().BeFalse("擁有者那邊執行中不是這台的執行中");
        rig.H.OnUi(() => rig.H.Hub.RunningCount).Should().Be(0);

        var list = await rig.H.Main.WaitForAsync("conversations", m => ListedIds(m).Contains(IdA), mark);
        var row = (JObject)((JArray)list["items"]!).Single(i => i.Value<string>("id") == IdA);
        // 桌面遠端操作：清單顯示擁有者電腦上的自訂資料夾（只供顯示，同瀏覽器版）；Meta.WorkingDirectory 照舊是 null，不算出這台的資料夾
        row.Value<string>("workingDirectory").Should().Be(@"C:\owner\repo");
        row.Value<bool>("customFolder").Should().BeTrue();
        // 〔WP4.6 D22〕清單的 running 是擁有者那邊的（只供顯示）；這台的執行中照舊不算它（上面的 IsRunning、RunningCount）
        row.Value<bool>("running").Should().BeTrue("WP4.6 起清單的 running 換成擁有者的 ReplicaRunning");
        row.Value<string>("deviceId").Should().Be(Owner);

        // 關掉顯示：全部移除
        rig.H.Ui.Invoke(() => rig.H.Hub.ShowReplicas = false);
        rig.H.Conversation(IdA).Should().BeNull();
        rig.H.Conversation(IdB).Should().BeNull();
    }

    [Fact]
    public async Task 複本不存檔_資料夾不出現()
    {
        using var rig = new Rig();
        var before = Entries(rig.H);
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));
        rig.H.Command(rig.H.Main, "open", new { conversationId = IdA });
        rig.DeliverItems(IdA, true, [Item("i1", 1, "問題", AgentItemKind.User), Item("i2", 2, "回答")]);

        // 只收看的指令：送出、改名、換帳戶、選資料夾、附件、刪除一律不處理
        var file = rig.H.Temp.Combine("attach.txt");
        File.WriteAllText(file, "附件");
        rig.H.Host.FilesToPick = [file];
        rig.H.Command(rig.H.Main, "setAccount", new { conversationId = IdA, accountId = rig.H.Codex.Id });
        rig.H.Send(rig.H.Main, IdA, "從這台送");
        rig.H.Command(rig.H.Main, "rename", new { conversationId = IdA, title = "改名" });
        rig.H.Command(rig.H.Main, "setFolder", new { conversationId = IdA, path = rig.H.Temp.Root });
        rig.H.Command(rig.H.Main, "attach", new { conversationId = IdA });
        rig.H.Command(rig.H.Main, "addFiles", new { conversationId = IdA }, [file]);
        rig.H.Command(rig.H.Main, "delete", new { conversationId = IdA });
        rig.H.Ui.Invoke(() => rig.H.Hub.RunMaintenance());
        await rig.H.Ui.DrainAsync();

        rig.H.Sessions.Should().BeEmpty("複本不建會話");
        rig.H.Conversation(IdA)!.Title.Should().Be("擁有者的對話");
        rig.H.Conversation(IdA)!.AccountId.Should().BeEmpty();
        rig.ItemIds(IdA).Should().Equal("i1", "i2");

        await rig.H.Ui.Invoke(() => rig.H.Hub.ShutdownAsync(null));
        Directory.Exists(rig.H.Store.GetConversationDirectory(IdA)).Should().BeFalse();
        Entries(rig.H).Should().Equal(before, "複本不在這台留下任何檔案");
    }

    [Fact]
    public async Task 側欄搜尋_複本搜記憶體裡的項目_政策不是full只比標題_不留檔案()
    {
        using var rig = new Rig();
        var before = Entries(rig.H);
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA), Meta(IdB, title: "只有標題的對話", policy: ConversationSyncPolicies.Metadata)], [], [], Full: true));
        rig.DeliverItems(IdA, true, [Item("i1", 1, "在別台電腦問的問題", AgentItemKind.User), Item("i2", 2, "別台的回答")]);

        async Task<Dictionary<string, string?>> Search(string query)
        {
            var mark = rig.H.Main.Mark();
            var requestId = Guid.NewGuid().ToString("N");
            rig.H.Command(rig.H.Main, "searchConversations", new { requestId, query });
            var reply = await rig.H.Main.WaitForAsync("conversationSearch", m => m.Value<string>("requestId") == requestId, mark, timeoutMs: 5000);
            return ((JArray)reply["hits"]!).ToDictionary(x => x.Value<string>("conversationId")!, x => x.Value<string>("snippet"));
        }

        (await Search("別台")).Should().Equal(new Dictionary<string, string?> { [IdA] = "在別台電腦問的問題" });
        (await Search("只有標題")).Should().Equal(new Dictionary<string, string?> { [IdB] = null });
        rig.H.OnUi(() => rig.H.Hub.SearchIndex.FileReads).Should().Be(0, "複本沒有紀錄檔");
        Entries(rig.H).Should().Equal(before);
    }

    [Fact]
    public void 儲存端直接呼叫也不寫()
    {
        using var temp = new TempDirectory("replica-store");
        var store = new ConversationStore(temp.Combine("conversations"));
        store.MarkReplica(IdA);
        var dir = store.GetConversationDirectory(IdA);
        var item = new AgentChatItem { Id = "i1", Ord = 1, Kind = AgentItemKind.User, Text = "x" };
        var queue = new QueueSnapshot([new QueuedMessageData("q1", "排隊", [])], [], []);

        store.SaveConversation(new AgentConversation { Id = IdA, Title = "複本" });
        store.SaveItems(IdA, [item]);
        store.SaveQueue(IdA, queue);
        store.SaveConversation(new AgentConversation { Id = IdA.ToUpperInvariant(), Title = "大小寫不同" });
        Directory.Exists(dir).Should().BeFalse("meta.json、items.json、queue.json 都不寫");

        // 已經有資料夾（同名的不是複本的）：Ord 保留旁檔、items.json、queue.json 都不寫，刪除也不刪
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "留著");
        store.TrySaveOrdReservation(IdA, 5).Should().BeFalse();
        store.SaveItems(IdA, [item]);
        store.SaveQueue(IdA, queue);
        store.SaveConversation(new AgentConversation { Id = IdA, Title = "複本" });
        store.DeleteConversation(IdA).Should().BeTrue("沒有要刪的");
        Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Should().BeEquivalentTo(["meta.json", "keep.txt"]);
        File.ReadAllText(Path.Combine(dir, "meta.json")).Should().Be("{}");

        // 沒登記的照常寫
        var local = new AgentConversation { Id = IdB, Title = "本機" };
        store.SaveConversation(local);
        store.SaveItems(IdB, [item]);
        store.SaveQueue(IdB, queue);
        store.TrySaveOrdReservation(IdB, 5).Should().BeTrue();
        Directory.EnumerateFiles(store.GetConversationDirectory(IdB)).Select(Path.GetFileName).Should().Contain(["meta.json", "items.json", "queue.json", "sync-ord.json"]);
        store.IsReplica(IdB).Should().BeFalse();
        store.IsReplica(IdA.ToUpperInvariant()).Should().BeTrue("資料夾名不分大小寫");
    }

    [Fact]
    public void 複本不發布()
    {
        using var rig = new Rig();
        rig.Show();
        rig.Box.TakeAll();

        rig.Deliver(new ReplicaDelta([Meta(IdA), Meta(IdB)], [], [], Full: true));
        rig.H.Command(rig.H.Main, "open", new { conversationId = IdA });
        rig.DeliverItems(IdA, true, [Item("i1", 1, "問題", AgentItemKind.User), Item("i3", 3, "回答")]);
        rig.DeliverItems(IdA, false, [Item("i2", 2, "插在中間"), Item("i3", 3, "回答（改）")], "i1");
        rig.Deliver(Meta(IdA, "改了標題", running: true));
        rig.Deliver(new ReplicaDelta([], [IdB], []));

        rig.Flush().Where(c => c is not (ReplicaRequested or ReplicaClosed)).Should().BeEmpty("盒子是空的：複本的變化不會被當成這台的對話發布");
        var source = (IConversationPublishSource)rig.H.Hub;
        rig.H.OnUi(source.ConversationIds).Should().NotContain(IdA);
        rig.H.OnUi(() => source.SnapshotItems(IdA, 0, 50, null)).Should().BeNull();
        rig.H.OnUi(() => source.SnapshotQueue(IdA)).Should().BeNull();
        rig.H.OnUi(() => source.SnapshotMeta([IdA])).Should().ContainSingle().Which.IsReplica.Should().BeTrue();
    }

    [Fact]
    public async Task 打開複本_要求拉串流_項目照常渲染()
    {
        using var rig = new Rig();
        rig.Show();
        rig.Box.TakeAll();
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));

        var view = rig.H.Main;
        var mark = view.Mark();
        rig.H.Command(view, "open", new { conversationId = IdA });
        var opened = view.Messages("conversation", mark, IdA).Should().ContainSingle().Subject;
        opened["header"]!.Value<string>("title").Should().Be("擁有者的對話");
        opened["header"]!.Value<string>("workingDirectory").Should().Be(@"C:\owner\repo", "擁有者電腦上的自訂資料夾（只供顯示）");
        ((JArray)opened["items"]!).Should().BeEmpty();
        opened["reload"].Should().BeNull("open 的回覆不帶 reload");
        rig.Box.TakeAll().OfType<ReplicaRequested>().Should().ContainSingle().Which.ConversationId.Should().Be(IdA, "打開時要引擎送存著的項目並開始拉它的串流");

        // 引擎送存著的項目（整份）：整個對話送一次（同本機對話只送最後一頁加 hasMore），不逐筆送（合併前審查 R1）
        mark = view.Mark();
        rig.DeliverItems(IdA, true, [Item("i1", 1, "問題", AgentItemKind.User), Item("i5", 5, "第五則"), Item("i3", 3, "**第三則**")]);
        view.Messages("item", mark, IdA).Should().BeEmpty();
        var whole = view.Messages("conversation", mark, IdA).Should().ContainSingle().Subject;
        whole.Value<bool?>("reload").Should().BeTrue("中樞主動重送：網頁已關掉這個分頁時丟掉、不重開（第二輪合併前審查 R1）");
        var items = ((JArray)whole["items"]!).Cast<JObject>().ToList();
        items.Select(i => i.Value<string>("id")).Should().Equal(["i1", "i3", "i5"], "依 Ord 排好");
        items[1].Value<string>("html").Should().Contain("<strong>第三則</strong>");
        items.Select(i => i.Value<long>("seq")).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();

        // 拉到的一頁：取代、移除接在後面的逐筆送
        mark = view.Mark();
        rig.DeliverItems(IdA, false, [Item("i5", 5, "第五則（改）")], "i1");
        view.Messages("removeItem", mark, IdA).Should().ContainSingle().Which.Value<string>("id").Should().Be("i1");
        view.Messages("item", mark, IdA).Should().ContainSingle().Which["item"]!.Value<string>("html").Should().Contain("第五則（改）");
        rig.ItemIds(IdA).Should().Equal("i3", "i5");

        // 插在中間（從頭拉時舊的項目晚到）：依 Ord 排，重新編號後整個重送
        mark = view.Mark();
        rig.DeliverItems(IdA, false, [Item("i4", 4, "第四則")]);
        rig.ItemIds(IdA).Should().Equal("i3", "i4", "i5");
        rig.H.Items(IdA).Select(i => i.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        var resent = view.Messages("conversation", mark, IdA).Should().ContainSingle().Subject;
        resent.Value<bool?>("reload").Should().BeTrue();
        ((JArray)resent["items"]!).Select(i => i.Value<string>("id")).Should().Equal("i3", "i4", "i5");

        // 往上載入照常（只收看的指令）
        mark = view.Mark();
        rig.H.Command(view, "loadOlder", new { conversationId = IdA, beforeId = "i4" });
        ((JArray)view.Messages("olderItems", mark, IdA).Single()["items"]!).Select(i => i.Value<string>("id")).Should().Equal("i3");

        // 擁有者改成 metadata：項目清掉、之後的項目不收
        mark = view.Mark();
        rig.Deliver(Meta(IdA, policy: ConversationSyncPolicies.Metadata));
        rig.ItemIds(IdA).Should().BeEmpty();
        rig.DeliverItems(IdA, false, [Item("i6", 6, "不該出現")]);
        rig.ItemIds(IdA).Should().BeEmpty();
        await view.WaitForAsync("header", m => m.Value<string>("conversationId") == IdA, mark);
    }

    [Fact]
    public void 大的複本_整份或一頁追趕_只送最後一頁加hasMore_不逐筆送()
    {
        using var rig = new Rig();
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));
        var view = rig.H.Main;
        rig.H.Command(view, "open", new { conversationId = IdA });

        // 第一次打開：sync.db 存著的 1000 筆整份交來
        var mark = view.Mark();
        rig.DeliverItems(IdA, true, Enumerable.Range(1, 1_000).Select(i => Item($"r{i}", i, $"第 {i} 則")).ToList());
        view.Messages("item", mark, IdA).Should().BeEmpty("不逐筆渲染、逐筆送（合併前審查 R1）");
        var opened = view.Messages("conversation", mark, IdA).Should().ContainSingle().Subject;
        ((JArray)opened["items"]!).Should().HaveCount(AgentHub.PageSize);
        ((JArray)opened["items"]!).Last().Value<string>("id").Should().Be("r1000");
        opened.Value<bool>("hasMore").Should().BeTrue("較早的靠 loadOlder");
        rig.H.Items(IdA).Should().HaveCount(1_000);

        // 串流追趕：一頁接在後面 500 筆
        mark = view.Mark();
        rig.DeliverItems(IdA, false, Enumerable.Range(1_001, 500).Select(i => Item($"r{i}", i, $"第 {i} 則")).ToList());
        view.Messages("item", mark, IdA).Should().BeEmpty();
        var caught = view.Messages("conversation", mark, IdA).Should().ContainSingle().Subject;
        caught.Value<bool?>("reload").Should().BeTrue();
        ((JArray)caught["items"]!).Should().HaveCount(AgentHub.PageSize);
        ((JArray)caught["items"]!).Last().Value<string>("id").Should().Be("r1500");
        rig.H.Items(IdA).Select(i => i.Ord).Should().BeInAscendingOrder();
        rig.H.Items(IdA).Select(i => i.Seq).Should().OnlyHaveUniqueItems();

        // 少量的照常逐筆送
        mark = view.Mark();
        rig.DeliverItems(IdA, false, [Item("r1501", 1_501, "新的一則")]);
        view.Messages("item", mark, IdA).Should().ContainSingle();
        view.Messages("conversation", mark, IdA).Should().BeEmpty();
    }

    [Fact]
    public void 對話清單的上限本機與複本分開算_複本不擠掉本機的對話()
    {
        var locals = Enumerable.Range(0, AgentHub.ConversationListMax).Select(i => new AgentConversation
        {
            AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = $"本機 {i}", UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local).AddMinutes(i),
        }).ToList();
        using var rig = new Rig(store =>
        {
            foreach (var c in locals)
                store.SaveConversation(c);
        });
        rig.Show();
        // 擁有者電腦的時鐘比較新：舊的作法把最舊的本機對話擠出清單
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));

        rig.H.Command(rig.H.Main, "ready");
        var listed = ((JArray)rig.H.Main.Last("init")!["conversations"]!).Select(i => i.Value<string>("id")!).ToList();
        listed.Should().Contain(locals.Select(c => c.Id), "本機的對話一個都不少（合併前審查 R5）");
        listed.Should().Contain(IdA);
        listed[0].Should().Be(IdA, "合併後照更新時間排");
    }

    [Fact]
    public void 渲染複本之後LocalMediaUrls沒有新增登記()
    {
        using var rig = new Rig();
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));
        var view = rig.H.Main;
        rig.H.Command(view, "open", new { conversationId = IdA });
        const string image = @"C:\owner\out\chart.png";
        const string linked = @"C:\owner\repo\a.cs";
        const string attached = @"C:\owner\att\screen.png";
        var mark = view.Mark();
        rig.DeliverItems(IdA, true,
        [
            Item("i1", 1, "附圖", AgentItemKind.User, edit: i => i.Attachments.Add(new AgentAttachment
            {
                Id = "x1", FileName = "screen.png", StoredPath = attached, Kind = AgentAttachmentKind.Image, SizeBytes = 2048, ModelImagePaths = { @"C:\owner\att\.model\screen.jpg" },
            })),
            Item("i2", 2, $"看圖 ![圖]({image}) 與 [檔案]({linked})", edit: i => i.MediaPaths.Add(@"C:\owner\out\made.png")),
        ]);
        rig.H.Command(view, "loadOlder", new { conversationId = IdA });

        var rendered = view.All().Skip(mark).ToList();
        rendered.Should().NotBeEmpty();
        var text = string.Join("\n", rendered.Select(m => m.ToString()));
        text.Should().NotContain(AgentMarkdownRenderer.Origin + "/media").And.NotContain(AgentMarkdownRenderer.Origin + "/thumb").And.NotContain(AgentMarkdownRenderer.Origin + "/open");
        // 整份的項目在 conversation 訊息裡（R1）
        var item = rendered.SelectMany(m => m.Value<string>("type") switch
            {
                "item" => new[] { (JObject)m["item"]! },
                "conversation" => ((JArray)m["items"]!).Cast<JObject>(),
                _ => Enumerable.Empty<JObject>(),
            })
            .First(i => i.Value<string>("id") == "i1");
        var attachment = (JObject)((JArray)item["attachments"]!).Single();
        attachment.Value<string>("thumbUrl").Should().BeNull();
        attachment.Value<string>("url").Should().BeNull();

        foreach (var path in new[] { image, linked, attached, @"C:\owner\out\made.png", @"C:\owner\att\.model\screen.jpg" })
        {
            rig.H.OnUi(() => rig.H.Hub.IsMediaAllowed(path)).Should().BeFalse(path);
            rig.H.OnUi(() => rig.H.Hub.IsThumbAllowed(path)).Should().BeFalse(path);
        }

        // 陽性對照：同一段 Markdown 以本機的渲染會登記（測試量得到登記）
        rig.H.OnUi(() => rig.H.Hub.RenderItem(new AgentChatItem { Id = "l1", Kind = AgentItemKind.Assistant, Text = $"![圖]({image})" }, rig.H.Temp.Root));
        rig.H.OnUi(() => rig.H.Hub.IsMediaAllowed(image)).Should().BeTrue();
    }

    [Fact]
    public void 複本被刪除_從清單移除()
    {
        using var rig = new Rig();
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA), Meta(IdB), Meta(IdC)], [], [], Full: true));
        var view = rig.H.Main;
        rig.H.Command(view, "open", new { conversationId = IdA });

        var mark = view.Mark();
        rig.Deliver(new ReplicaDelta([], [IdA], []));
        rig.H.Conversation(IdA).Should().BeNull();
        view.Messages("conversationClosed", mark, IdA).Should().ContainSingle("開著的分頁關掉");
        rig.H.Host.Deleted.All.Should().Contain(IdA, "彈出視窗也關掉");

        // 整份清單沒有的移除
        rig.Deliver(new ReplicaDelta([Meta(IdC)], [], [], Full: true));
        rig.H.Conversation(IdB).Should().BeNull();
        rig.IsReplica(IdC).Should().BeTrue();

        // 換了接收者（解除配對）：舊引擎的複本移除
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachConversationSink(null));
        rig.H.Conversation(IdC).Should().BeNull();
        rig.Flush().Where(c => c is not (ReplicaRequested or ReplicaClosed)).Should().BeEmpty("刪除複本不會送 conv.deleted");
    }

    [Fact]
    public void 複本沒有視窗開著時_通知引擎不再拉它的串流()
    {
        using var rig = new Rig();
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA), Meta(IdB)], [], [], Full: true));
        var main = rig.H.Main;
        var popout = rig.H.AddPopout("popout-a", IdA);
        rig.H.Command(main, "open", new { conversationId = IdA });
        rig.H.Command(popout, "open", new { conversationId = IdA });
        rig.H.Command(main, "open", new { conversationId = IdB });
        rig.Box.TakeAll();

        rig.H.Command(main, "close", new { conversationId = IdA });
        rig.Box.TakeAll().OfType<ReplicaClosed>().Should().BeEmpty("彈出視窗還開著它");
        rig.H.Ui.Invoke(() => rig.H.Hub.DetachView(popout.Id));
        rig.Box.TakeAll().OfType<ReplicaClosed>().Should().ContainSingle().Which.ConversationId.Should().Be(IdA, "沒有視窗開著了（合併前審查 R3）");

        // 網頁重新載入：之前開著的都不再算開著（網頁會重新 open 需要的分頁）
        rig.H.Command(main, "ready");
        rig.Box.TakeAll().OfType<ReplicaClosed>().Should().ContainSingle().Which.ConversationId.Should().Be(IdB);

        // 擁有者刪了開著的複本：分頁關掉，也不再拉
        rig.H.Command(main, "open", new { conversationId = IdA });
        rig.Box.TakeAll();
        rig.Deliver(new ReplicaDelta([], [IdA], []));
        rig.Box.TakeAll().OfType<ReplicaClosed>().Should().ContainSingle().Which.ConversationId.Should().Be(IdA);

        // 關掉顯示：一次通知全部
        rig.H.Command(main, "open", new { conversationId = IdB });
        rig.Box.TakeAll();
        rig.H.Ui.Invoke(() => rig.H.Hub.ShowReplicas = false);
        rig.Box.TakeAll().OfType<ReplicaClosed>().Should().ContainSingle().Which.ConversationId.Should().BeNull();
    }

    private sealed class WindowView(string id, string key, bool primary) : FakeView(id), IWindowView
    {
        public string WindowKey => key;

        public bool IsPrimaryWindow => primary;
    }

    [Fact]
    public void 複本的分頁可以拖到別的視窗_換一個視窗顯示_一直有視窗開著所以照樣拉串流()
    {
        // 2026-10-25 實機：其他電腦的對話拖到另一個視窗放開後沒搬過去——tabDrag、tabMoveIn、tabDragEnd 不在只看的指令裡，被當成寫入擋下
        using var rig = new Rig();
        rig.Show();
        rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));
        var a = new WindowView("wa", WindowRules.PrimaryKey, primary: true);
        rig.H.Settings.Windows.Add(new AppWindowState { Key = "key-b" });
        var b = new WindowView("wb", "key-b", primary: false);
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachView(a));
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachView(b));
        rig.H.Command(a, "open", new { conversationId = IdA });
        rig.Box.TakeAll();
        var markA = a.Mark();
        var markB = b.Mark();

        rig.H.Command(a, "tabDrag", new { conversationId = IdA, text = "" });
        b.Messages("tabDrag", markB).Should().ContainSingle("其他視窗要知道有分頁在拖").Which.Value<string>("conversationId").Should().Be(IdA);
        rig.H.Command(b, "tabMoveIn", new { conversationId = IdA, paneId = "p1" });
        rig.H.Command(a, "tabDragEnd", new { conversationId = IdA, dropped = true });

        b.Messages("conversation", markB, IdA).Should().ContainSingle().Which.Value<string>("paneId").Should().Be("p1");
        a.Messages("conversationMoved", markA).Should().ContainSingle().Which.Value<string>("conversationId").Should().Be(IdA);
        a.Messages("conversationClosed", markA).Should().BeEmpty("搬走不是關掉");
        rig.H.OnUi(() => rig.H.Hub.ViewIdsOf(IdA)).Should().Equal(b.Id);
        rig.Box.TakeAll().OfType<ReplicaClosed>().Should().BeEmpty("先開在目標再從來源拿掉：一直有視窗開著，不停止拉它的串流");
        b.Messages("tabDrag", markB).Select(m => m.Value<string>("conversationId")).Should().Equal([IdA, null], "開始拖時通知、拖完再通知一次");
        rig.IsReplica(IdA).Should().BeTrue();
        rig.H.Sessions.Should().BeEmpty("搬視窗不建會話");
    }

    [Fact]
    public void 本機已有的id_不建立副本_本機對話不變()
    {
        var local = new AgentConversation
        {
            Id = IdA.ToUpperInvariant(), AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "這台的對話", SessionId = Guid.NewGuid().ToString(), SessionStarted = true,
        };
        using var rig = new Rig(seedStore: store =>
        {
            store.SaveConversation(local);
            store.SaveItems(local.Id, [new AgentChatItem { Id = "l1", Kind = AgentItemKind.User, Text = "本機的內容" }]);
            // 這次沒載入的本機對話（只有資料夾，例如 meta.json 讀不到）
            Directory.CreateDirectory(Path.Combine(store.GetConversationDirectory(IdB), "attachments"));
        });
        rig.Show();

        rig.Deliver(new ReplicaDelta([Meta(IdA, "別台的標題"), Meta(IdB, "別台的標題"), Meta(IdC)], [], [], Full: true));
        rig.DeliverItems(IdA, true, [Item("r1", 1, "別台的內容")]);
        rig.DeliverItems(local.Id, true, [Item("r1", 1, "別台的內容")]);
        rig.Deliver(new ReplicaDelta([], [IdA, local.Id], []));
        rig.Deliver(new ReplicaDelta([Meta(IdC)], [], [], Full: true));

        var meta = rig.H.Conversation(local.Id)!;
        meta.Title.Should().Be("這台的對話");
        meta.AccountId.Should().Be("acc-claude");
        rig.IsReplica(local.Id).Should().BeFalse();
        rig.ItemIds(local.Id).Should().Equal("l1");
        rig.H.Conversation(IdA).Should().BeNull("線上 id 是小寫：不另建一個小寫的副本");
        rig.H.Conversation(IdB).Should().BeNull("對話資料夾存在就是本機的");
        rig.IsReplica(IdC).Should().BeTrue();
        rig.H.Store.IsReplica(local.Id).Should().BeFalse("本機的 id 不登記成複本（照常存檔）");
        rig.H.Store.IsReplica(IdB).Should().BeFalse();

        rig.H.Send(rig.H.Main, local.Id, "照常送出");
        rig.H.Sessions.Should().ContainSingle("本機的對話照常可以送出");
    }

    [Fact]
    public void 金檔不變()
    {
        // 錄製只由 ItemRendererGoldenTests 寫（這裡的呼叫端路徑在 Hub 資料夾）
        if (GoldenFile.Recording)
            return;
        foreach (var name in ItemRendererCorpus.Names)
        {
            var input = ItemRendererCorpus.Get(name);
            using var rig = new Rig();
            rig.Show();
            // 同一份語料先當成複本渲染過（不登記任何東西），本機的渲染與白名單仍與黃金檔逐位元組相同
            var replicaItems = input.Items.Select((item, index) =>
                {
                    var copy = ChatItemCopyRules.Copy(item);
                    copy.Ord = index + 1;
                    return ConversationWire.TryItem(copy, 1, out var wire) ? ReplicaRules.ToItem(ConversationWire.Serialize(wire)) : null;
                })
                .OfType<AgentChatItem>()
                .ToList();
            replicaItems.Should().NotBeEmpty(name);
            rig.Deliver(new ReplicaDelta([Meta(IdA)], [], [], Full: true));
            rig.H.Command(rig.H.Main, "open", new { conversationId = IdA });
            rig.DeliverItems(IdA, true, replicaItems);
            rig.H.Command(rig.H.Main, "loadOlder", new { conversationId = IdA });

            var golden = rig.H.OnUi(() => ItemRendererGoldenTests.InCulture(() => ItemRendererGoldenTests.BuildGolden(input,
                item => rig.H.Hub.RenderItem(item, input.WorkingDirectory), rig.H.Hub.IsMediaAllowed, rig.H.Hub.IsThumbAllowed)));
            GoldenFile.Verify(name, golden);
        }
    }
}
