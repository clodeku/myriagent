using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Security;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Rules;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 拉其他電腦的對話存成複本（WP4.3 計畫步 14，D26）：只在協商出 conv1 時拉 cls=meta 與打開的複本串流（每次 ≤12 個、輪流，游標總數 ≤16），
/// 這台寫的、本機已有的對話一律略過（SEC-27），不合格的略過、游標照樣前進，副本的丟棄規則（P10），對話游標存 conversation_cursors、
/// v1 的 cursors 只有第 3 階段的類別，UI 套用失敗時游標不前進，伺服器換實例清掉重拉，解除配對時隨 sync.db 刪除。
/// 其他電腦的事件由假伺服器直接放進拉取的紀錄（<see cref="FakeSyncServer.PublishConversation"/>）
/// </summary>
public sealed class ReplicaPullTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Local);

    private static readonly string[] BothFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];

    /// <summary>引擎＋盒子＋假 UI（本機對話的根目錄是暫存資料夾）＋假伺服器；<see cref="Other"/> 是另一台電腦</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public Rig(bool conv1 = true)
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box);
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            Root = H.Dir.CreateDirectory("conversations");
            Box.ConversationsRoot = Root;
            H.Server.Features = conv1 ? BothFeatures : null;
            _ = H.Server.Conversations;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        public string Root { get; }

        public string Other { get; } = SyncIds.NewDeviceId();

        public Task StartAsync() => H.StartAsync();

        public Task IdleAsync() => H.IdleAsync();

        /// <summary>另一台電腦（預設 <see cref="Other"/>）寫的對話事件</summary>
        public long Publish(string kind, string id, string json, string? author = null, long epoch = 1) => H.Server.PublishConversation(kind, id, json, author ?? Other, epoch);

        /// <summary>伺服器通知有新事件，等引擎拉完</summary>
        public async Task NotifyAsync(params string[] classes)
        {
            H.Server.Current!.Notify(classes);
            await IdleAsync();
        }

        /// <summary>中樞打開這些複本（ReplicaRequested），等引擎拉完</summary>
        public async Task OpenAsync(params string[] ids)
        {
            foreach (var id in ids)
                Ui.Put(new ReplicaRequested(id));
            await IdleAsync();
        }

        /// <summary>中樞不再顯示這些複本（ReplicaClosed；null＝全部），等引擎處理完</summary>
        public async Task CloseAsync(params string?[] ids)
        {
            foreach (var id in ids)
                Ui.Put(new ReplicaClosed(id));
            await IdleAsync();
        }

        public Task<IReadOnlyDictionary<string, ReplicaMetaRow>> MetasAsync() => H.Inspect(v => v.Store!.LoadReplicaMeta());

        public Task<IReadOnlyList<ReplicaItemRow>> ItemsAsync(string id) => H.Inspect(v => v.Store!.LoadReplicaItems(id));

        public Task<IReadOnlyDictionary<string, long>> ConversationCursorsAsync() => H.Inspect(v => v.Store!.GetConversationCursors());

        /// <summary>這一刻之後的拉取要的串流（每次一份）</summary>
        public List<List<string>> StreamsSince(int mark)
            => H.Server.Pulls.Skip(mark).Select(p => p.Cursors.Where(c => c.Stream is not null).Select(c => c.Stream!).ToList()).ToList();

        /// <summary>重新連上（伺服器的設定先改好），送完待送的、進 Live</summary>
        public async Task ReconnectAsync()
        {
            if (H.Server.Current is { Closed: false } session)
                session.Drop();
            await IdleAsync();
            H.Engine.RetryNow();
            await IdleAsync();
            await H.AdvanceAsync(2);
            (await H.StateAsync()).Should().Be(SyncEngineState.Live);
        }

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string MetaJson(string title, string policy = ConversationSyncPolicies.Full, bool running = false)
        => ConversationWire.Serialize(new ConversationMetaPayload("Claude", "acc-9", title, "Ask", null, null, false, null, policy, 1791500000000, 1791500060000, running, false));

    private static string ItemJson(string itemId, long ord, string text, long ver = 1)
    {
        var item = new AgentChatItem { Id = itemId, Ord = ord, Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = text, CreatedAt = T0, UpdatedAt = T0 };
        ConversationWire.TryItem(item, ver, out var wire).Should().BeTrue();
        return ConversationWire.Serialize(wire!);
    }

    private static string RemovedJson(string itemId) => ConversationWire.Serialize(new ConversationItemRemovedPayload(itemId));

    /// <summary>本機的對話資料夾（meta.json、items.json）</summary>
    private static void WriteLocalFolder(string root, string id)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        File.WriteAllText(Path.Combine(dir, "meta.json"), $$"""{"Id":"{{id}}","Title":"本機的對話"}""");
        File.WriteAllText(Path.Combine(dir, "items.json"), """[{"Id":"i0","Text":"本機的內容"}]""");
    }

    /// <summary>資料夾裡每個檔的相對路徑與內容的 SHA-256</summary>
    private static string FolderSha(string root)
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append(Path.GetRelativePath(root, file)).Append('=').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).Append('\n');
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            sb.Append(Path.GetRelativePath(root, dir)).Append("/\n");
        return sb.ToString();
    }

    /// <summary>依序套用交給中樞的項目變化，得到中樞看到的最後內容（項目 id → 文字）</summary>
    private static Dictionary<string, string> UiItems(FakeConversationUi ui, string id)
    {
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in ui.ReplicaDeltas.SelectMany(d => d.Items).Where(i => i.ConversationId == id))
        {
            if (change.Reset)
                items.Clear();
            foreach (var removed in change.Removed)
                items.Remove(removed);
            foreach (var item in change.Upserts)
                items[item.Id] = item.Text;
        }

        return items;
    }

    private static bool Mentions(ReplicaDelta delta, string id)
        => delta.Metas.Any(m => m.ConversationId == id) || delta.Deleted.Contains(id) || delta.Items.Any(i => i.ConversationId == id);

    // ───────────────────────────── 協商與游標 ─────────────────────────────

    [Fact]
    public async Task 沒協商conv1_不拉meta_Hello沒有meta鍵()
    {
        await using var rig = new Rig(conv1: false);
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson("別台的對話"));
        await rig.StartAsync();
        await rig.NotifyAsync(SyncClasses.Meta, SyncClasses.Item);
        await rig.ReconnectAsync();

        rig.H.Server.Hellos.Should().HaveCount(2).And.OnlyContain(h => h.Cursors.Keys.Order().SequenceEqual(new[] { SyncClasses.Catalog, SyncClasses.Usage }));
        rig.H.Server.Pulls.Should().NotBeEmpty().And.OnlyContain(p => p.Cursors.All(c => c.Cls == SyncClasses.Catalog || c.Cls == SyncClasses.Usage));
        (await rig.MetasAsync()).Should().BeEmpty();
        (await rig.ConversationCursorsAsync()).Should().BeEmpty();
        rig.Ui.ReplicaDeltas.Should().BeEmpty();
    }

    [Fact]
    public async Task 協商後_Hello帶meta_拉取接在第3階段兩個之後()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        rig.H.Server.Hellos.Single().Cursors.Keys.Should().BeEquivalentTo([SyncClasses.Catalog, SyncClasses.Usage], "第一次連線之前沒有 conv1 的紀錄（conv_armed 預設 false）");
        rig.H.Server.Pulls.First().Cursors.Should().Equal(new PullCursor(0, SyncClasses.Catalog), new PullCursor(0, SyncClasses.Usage), new PullCursor(0, SyncClasses.Meta));

        var seq = rig.Publish(ConversationEventKinds.Created, NewId(), MetaJson("x"));
        await rig.NotifyAsync(SyncClasses.Meta);
        await rig.ReconnectAsync();

        rig.H.Server.Hellos.Last().Cursors.Should().BeEquivalentTo(new Dictionary<string, long> { [SyncClasses.Catalog] = 0, [SyncClasses.Usage] = 0, [SyncClasses.Meta] = seq },
            "上一次進 Live 的連線有 conv1：Hello 帶 meta");
        rig.H.Server.Pulls.Last().Cursors.Should().Equal(new PullCursor(0, SyncClasses.Catalog), new PullCursor(0, SyncClasses.Usage), new PullCursor(seq, SyncClasses.Meta));
    }

    [Fact]
    public async Task v1的cursors只有catalog與usage()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson("x"));
        var prefs = rig.H.Server.Publish(SyncEventKinds.PrefsSet, new PrefsSetPayload("theme", "dark", 1));
        await rig.NotifyAsync(SyncClasses.Meta, SyncClasses.Catalog);
        await rig.OpenAsync(id);
        var item = rig.Publish(ConversationEventKinds.Item, id, ItemJson("i1", 1, "內容"));
        await rig.NotifyAsync(SyncClasses.Item);

        var raw = rig.H.RawCursors();
        raw.Keys.Should().BeSubsetOf([SyncClasses.Catalog, SyncClasses.Usage], "v1 的 cursors 只放第 3 階段的類別（契約步 11）");
        raw[SyncClasses.Catalog].Should().Be(prefs);
        (await rig.H.Inspect(v => v.Cursors)).Keys.Should().BeSubsetOf([SyncClasses.Catalog, SyncClasses.Usage]);
        var conversationCursors = await rig.ConversationCursorsAsync();
        conversationCursors.Keys.Should().BeEquivalentTo([SyncClasses.Meta, SyncStreams.Conversation(id)]);
        conversationCursors[SyncStreams.Conversation(id)].Should().Be(item);
    }

    // ───────────────────────────── 套用 ─────────────────────────────

    [Fact]
    public async Task 別台的created與meta寫進replica_meta()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson("第一版"));
        var last = rig.Publish(ConversationEventKinds.Meta, id, MetaJson("第二版", running: true));
        await rig.NotifyAsync(SyncClasses.Meta);

        var row = (await rig.MetasAsync()).Should().ContainKey(id).WhoseValue;
        row.OwnerDeviceId.Should().Be(rig.Other);
        row.Seq.Should().Be(last);
        row.Epoch.Should().Be(1);
        row.Deleted.Should().BeFalse();
        row.MetaJson.Should().Be(MetaJson("第二版", running: true));
        (await rig.ConversationCursorsAsync())[SyncClasses.Meta].Should().Be(last);

        var meta = rig.Ui.ReplicaDeltas.SelectMany(d => d.Metas).Should().ContainSingle("同一頁的 created 與 meta 併成最後一版").Subject;
        meta.ConversationId.Should().Be(id);
        meta.OwnerDeviceId.Should().Be(rig.Other);
        meta.Running.Should().BeTrue();
        meta.ItemsAvailable.Should().BeTrue();
        meta.Meta.Title.Should().Be("第二版");
        meta.Meta.AccountId.Should().BeEmpty("擁有者電腦的本機帳戶在這台沒有意義");
        meta.Meta.WorkingDirectory.Should().BeNull();
        meta.Meta.OwnerDeviceId.Should().Be(rig.Other);
        (await rig.ItemsAsync(id)).Should().BeEmpty("沒打開的複本不拉串流");
    }

    [Fact]
    public async Task 自己的對話被別人寫的事件_略過_本機資料不動()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var mine = rig.Ui.Add(new FakeConversation(new AgentConversation
        {
            Id = NewId(), Title = "我的對話", AccountId = EngineHarness.LocalAccount, Provider = AgentProvider.Claude, CreatedAt = T0, UpdatedAt = T0,
        }));
        rig.Ui.Put(new MetaChanged(mine.Id, mine.Snapshot()));
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(1);
        (await rig.H.Inspect(v => v.Store!.LoadPublishMeta())).Should().ContainKey(mine.Id, "這台發布過它");
        WriteLocalFolder(rig.Root, mine.Id);
        var before = FolderSha(rig.Root);

        rig.Publish(ConversationEventKinds.Meta, mine.Id, MetaJson("被別人改的標題"));
        rig.Publish(ConversationEventKinds.Item, mine.Id, ItemJson("i1", 1, "別人寫的項目"));
        rig.Publish(ConversationEventKinds.ItemRemoved, mine.Id, RemovedJson("i0"));
        var deleted = rig.Publish(ConversationEventKinds.Deleted, mine.Id, "{}");
        await rig.OpenAsync(mine.Id);
        await rig.NotifyAsync(SyncClasses.Meta, SyncClasses.Item);

        (await rig.MetasAsync()).Should().BeEmpty("SEC-27：自己的對話不當成複本");
        (await rig.ItemsAsync(mine.Id)).Should().BeEmpty();
        FolderSha(rig.Root).Should().Be(before, "本機的資料不動");
        rig.Ui.ReplicaDeltas.Should().NotContain(d => Mentions(d, mine.Id));
        rig.H.Server.Pulls.Should().NotContain(p => p.Cursors.Any(c => c.Stream == SyncStreams.Conversation(mine.Id)), "本機的對話不拉它的串流");
        (await rig.ConversationCursorsAsync())[SyncClasses.Meta].Should().Be(deleted, "略過的照樣前進");
    }

    [Fact]
    public async Task 自己寫的事件略過()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var orphan = NewId();
        // 這台寫的、本機已經沒有的對話（例如資料夾被手動刪掉）：也不當成複本
        rig.Publish(ConversationEventKinds.Created, orphan, MetaJson("這台寫的"), author: rig.H.DeviceId);
        var other = NewId();
        rig.Publish(ConversationEventKinds.Created, other, MetaJson("別台的"));
        await rig.NotifyAsync(SyncClasses.Meta);

        (await rig.MetasAsync()).Keys.Should().Equal(other);
        rig.Ui.ReplicaDeltas.Should().NotContain(d => Mentions(d, orphan));
    }

    [Fact]
    public async Task 不合格的略過_游標前進()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Publish(ConversationEventKinds.Meta, NewId(), "{}");
        rig.H.Server.PublishRaw(ConversationEventKinds.Meta, "gzip", "不是 base64", SyncClasses.Meta, rig.Other);
        rig.Publish(ConversationEventKinds.Created, NewId(), MetaJson("epoch 不合格"), epoch: 0);
        rig.Publish(ConversationEventKinds.Created, NewId(), "不是 JSON");
        rig.Publish(ConversationEventKinds.Created, NewId(), MetaJson("x").Replace("\"Claude\"", "\"Gemini\"", StringComparison.Ordinal));
        var good = NewId();
        var last = rig.Publish(ConversationEventKinds.Created, good, MetaJson("合格的"));
        await rig.NotifyAsync(SyncClasses.Meta);

        (await rig.MetasAsync()).Keys.Should().Equal([good], "不合格的略過，後面合格的照常套用（不是整頁停）");
        (await rig.ConversationCursorsAsync())[SyncClasses.Meta].Should().Be(last, "游標越過不合格的");
        (await rig.H.StateAsync()).Should().Be(SyncEngineState.Live);
        rig.H.Host.LastStatus!.Detail.Should().BeNull("不合格的對話事件只記錄、不是不認得的種類");
    }

    [Fact]
    public async Task deleted_丟掉整個副本()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson("要被刪的"));
        await rig.NotifyAsync(SyncClasses.Meta);
        await rig.OpenAsync(id);
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("i1", 1, "一"));
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("i2", 2, "二"));
        await rig.NotifyAsync(SyncClasses.Item);
        (await rig.ItemsAsync(id)).Should().HaveCount(2);

        rig.Publish(ConversationEventKinds.Deleted, id, "{}");
        await rig.NotifyAsync(SyncClasses.Meta);

        (await rig.MetasAsync()).Should().NotContainKey(id);
        (await rig.ItemsAsync(id)).Should().BeEmpty();
        rig.Ui.ReplicaDeltas.Last().Deleted.Should().Equal(id);
        var mark = rig.H.Server.Pulls.Count;
        rig.Publish(ConversationEventKinds.Created, NewId(), MetaJson("另一個"));
        await rig.NotifyAsync(SyncClasses.Meta, SyncClasses.Item);
        rig.StreamsSince(mark).SelectMany(s => s).Should().NotContain(SyncStreams.Conversation(id), "刪掉的複本不再拉串流");
    }

    [Fact]
    public async Task meta改成metadata_丟掉replica_items_留meta()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson("會降級的"));
        await rig.NotifyAsync(SyncClasses.Meta);
        await rig.OpenAsync(id);
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("i1", 1, "一"));
        await rig.NotifyAsync(SyncClasses.Item);
        (await rig.ItemsAsync(id)).Should().ContainSingle();

        rig.Publish(ConversationEventKinds.Meta, id, MetaJson("會降級的", ConversationSyncPolicies.Metadata));
        await rig.NotifyAsync(SyncClasses.Meta);

        (await rig.ItemsAsync(id)).Should().BeEmpty("WP4.2 的 P10：政策不是 full 就丟掉項目");
        var row = (await rig.MetasAsync())[id];
        ReplicaRules.PolicyOf(row.MetaJson).Should().Be(ConversationSyncPolicies.Metadata, "meta 留著（標題與狀態）");
        UiItems(rig.Ui, id).Should().BeEmpty("中樞的項目也清掉");
        rig.Ui.ReplicaDeltas.SelectMany(d => d.Metas).Last().ItemsAvailable.Should().BeFalse();

        // 改回 full：擁有者整份重送，又拉得到
        rig.Publish(ConversationEventKinds.Meta, id, MetaJson("會降級的"));
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("i1", 1, "一（重送）", ver: 2));
        await rig.NotifyAsync(SyncClasses.Meta, SyncClasses.Item);
        (await rig.ItemsAsync(id)).Should().ContainSingle().Which.ItemId.Should().Be("i1");
        UiItems(rig.Ui, id).Should().Equal(new Dictionary<string, string> { ["i1"] = "一（重送）" });
    }

    [Fact]
    public async Task 本機已有的id_不建立副本()
    {
        await using var rig = new Rig();
        // 重新配對前的舊身分寫的對話：這台還有它的資料夾，發布狀態裡沒有它
        var old = NewId();
        WriteLocalFolder(rig.Root, old);
        var before = FolderSha(rig.Root);
        rig.Publish(ConversationEventKinds.Created, old, MetaJson("舊身分的對話"), author: SyncIds.NewDeviceId());
        rig.Publish(ConversationEventKinds.Item, old, ItemJson("i1", 1, "舊身分的項目"));
        var other = NewId();
        rig.Publish(ConversationEventKinds.Created, other, MetaJson("別台的"));
        await rig.StartAsync();
        await rig.OpenAsync(old);

        (await rig.MetasAsync()).Keys.Should().Equal([other], "本機已有的 id 不當成副本");
        (await rig.ItemsAsync(old)).Should().BeEmpty();
        FolderSha(rig.Root).Should().Be(before);
        rig.Ui.ReplicaDeltas.Should().NotContain(d => Mentions(d, old));
        rig.H.Server.Pulls.Should().NotContain(p => p.Cursors.Any(c => c.Stream == SyncStreams.Conversation(old)));
    }

    [Fact]
    public async Task 從頭拉_舊的與新的item事件依seq套用_收斂到擁有者的內容()
    {
        await using var rig = new Rig();
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson("從頭拉"));
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("itemA", 1, "A 第一版"));
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("itemB", 2, "B 第一版"));
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("itemA", 1, "A 第二版", ver: 2));
        rig.Publish(ConversationEventKinds.Meta, id, MetaJson("從頭拉", ConversationSyncPolicies.Metadata));
        rig.Publish(ConversationEventKinds.Meta, id, MetaJson("從頭拉"));
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("itemA", 1, "A 第三版", ver: 3));
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("itemB", 2, "B 第二版", ver: 2));
        rig.Publish(ConversationEventKinds.ItemRemoved, id, RemovedJson("itemB"));
        var lastSeq = rig.Publish(ConversationEventKinds.Item, id, ItemJson("itemC", 3, "C"));
        await rig.StartAsync();
        await rig.OpenAsync(id);

        var items = await rig.ItemsAsync(id);
        items.Select(i => i.ItemId).Should().Equal("itemA", "itemC");
        items[0].Payload.Should().Be(ItemJson("itemA", 1, "A 第三版", ver: 3), "依 seq 套用：新的取代舊的");
        UiItems(rig.Ui, id).Should().Equal(new Dictionary<string, string> { ["itemA"] = "A 第三版", ["itemC"] = "C" }, "中樞看到的也收斂到擁有者的內容");
        (await rig.ConversationCursorsAsync())[SyncStreams.Conversation(id)].Should().Be(lastSeq);

        // 重新打開：存著的項目整份交給中樞（串流游標已經在最後，不重拉）
        var mark = rig.Ui.ReplicaDeltas.Count;
        await rig.OpenAsync(id);
        var reopened = rig.Ui.ReplicaDeltas.Skip(mark).SelectMany(d => d.Items).Should().ContainSingle().Subject;
        reopened.Reset.Should().BeTrue();
        reopened.Upserts.Select(i => i.Text).Should().Equal("A 第三版", "C");
    }

    // ───────────────────────────── 串流的輪流與上限 ─────────────────────────────

    [Fact]
    public async Task 開著的複本拉串流_最多12個_輪流()
    {
        await using var rig = new Rig();
        var ids = Enumerable.Range(0, 20).Select(_ => NewId()).ToList();
        foreach (var id in ids)
        {
            rig.Publish(ConversationEventKinds.Created, id, MetaJson("複本 " + id));
            rig.Publish(ConversationEventKinds.Item, id, ItemJson("item1", 1, "第一則"));
        }

        await rig.StartAsync();
        var mark = rig.H.Server.Pulls.Count;
        await rig.OpenAsync([.. ids]);

        var opened = rig.StreamsSince(mark);
        opened.Should().OnlyContain(s => s.Count <= ConversationReplicas.MaxStreamsPerPull);
        opened.SelectMany(s => s).Should().BeEquivalentTo(ids.Select(SyncStreams.Conversation), "每個打開的串流拉一次就到底");
        foreach (var id in ids)
            (await rig.ItemsAsync(id)).Should().ContainSingle();

        // 有新的 item 類事件：20 個分兩頁輪流（12＋8），不重複
        foreach (var id in ids)
            rig.Publish(ConversationEventKinds.Item, id, ItemJson("item2", 2, "第二則"));
        mark = rig.H.Server.Pulls.Count;
        await rig.NotifyAsync(SyncClasses.Item);

        var round = rig.StreamsSince(mark).Where(s => s.Count > 0).ToList();
        round.Select(s => s.Count).Should().Equal(12, 8);
        round.SelectMany(s => s).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(ids.Select(SyncStreams.Conversation));
        foreach (var id in ids)
            (await rig.ItemsAsync(id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task 游標總數不超過16()
    {
        await using var rig = new Rig();
        var ids = Enumerable.Range(0, 60).Select(_ => NewId()).ToList();
        foreach (var id in ids)
            rig.Publish(ConversationEventKinds.Created, id, MetaJson("複本 " + id));
        await rig.StartAsync();
        await rig.OpenAsync([.. ids]);
        foreach (var id in ids)
            rig.Publish(ConversationEventKinds.Item, id, ItemJson("item1", 1, "內容"));
        var mark = rig.H.Server.Pulls.Count;
        await rig.NotifyAsync(SyncClasses.Item);

        rig.H.Server.Pulls.Should().OnlyContain(p => p.Cursors.Count <= ConversationReplicas.MaxPullCursors, "伺服器一次最多收 16 個游標");
        var streams = rig.StreamsSince(mark).SelectMany(s => s).ToList();
        streams.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(ids.Select(SyncStreams.Conversation),
            "顯示中的超過 50 個時照樣拉、只記警告：不無聲拿掉顯示中的（合併前審查 R3）");
        foreach (var id in ids)
            (await rig.ItemsAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task 關掉的複本不佔名額_一直顯示的不被擠掉()
    {
        await using var rig = new Rig();
        var shown = NewId();
        var others = Enumerable.Range(0, ConversationReplicas.MaxOpen).Select(_ => NewId()).ToList();
        rig.Publish(ConversationEventKinds.Created, shown, MetaJson("一直開著的"));
        foreach (var id in others)
            rig.Publish(ConversationEventKinds.Created, id, MetaJson("看一下就關的 " + id));
        await rig.StartAsync();
        await rig.OpenAsync(shown);
        foreach (var id in others)
        {
            await rig.OpenAsync(id);
            await rig.CloseAsync(id);
        }

        rig.Publish(ConversationEventKinds.Item, shown, ItemJson("i1", 1, "擁有者的新項目"));
        var mark = rig.H.Server.Pulls.Count;
        await rig.NotifyAsync(SyncClasses.Item);

        rig.StreamsSince(mark).SelectMany(s => s).Should().Equal([SyncStreams.Conversation(shown)], "關掉的不再拉；一直開著的照樣拉（合併前審查 R3）");
        (await rig.ItemsAsync(shown)).Should().ContainSingle();
        UiItems(rig.Ui, shown).Should().Equal(new Dictionary<string, string> { ["i1"] = "擁有者的新項目" });
    }

    [Fact]
    public async Task 全部關掉之後_item的通知不拉_再打開從DB整份加游標之後接著拉()
    {
        await using var rig = new Rig();
        var a = NewId();
        var b = NewId();
        rig.Publish(ConversationEventKinds.Created, a, MetaJson("A"));
        rig.Publish(ConversationEventKinds.Created, b, MetaJson("B"));
        rig.Publish(ConversationEventKinds.Item, a, ItemJson("i1", 1, "一"));
        await rig.StartAsync();
        await rig.OpenAsync(a, b);
        (await rig.ItemsAsync(a)).Should().ContainSingle();

        await rig.CloseAsync(a);
        await rig.CloseAsync((string?)null);
        rig.Publish(ConversationEventKinds.Item, a, ItemJson("i2", 2, "二"));
        var mark = rig.H.Server.Pulls.Count;
        await rig.NotifyAsync(SyncClasses.Item);

        rig.H.Server.Pulls.Count.Should().Be(mark, "沒有顯示中的複本：item 的通知不拉（不重拉關掉的串流）");
        (await rig.H.Inspect(v => v.Replicas.Open.Count)).Should().Be(0);

        var deltas = rig.Ui.ReplicaDeltas.Count;
        await rig.OpenAsync(a);
        rig.Ui.ReplicaDeltas.Skip(deltas).SelectMany(d => d.Items).First(i => i.ConversationId == a).Should()
            .Match<ReplicaItems>(i => i.Reset && i.Upserts.Count == 1 && i.Upserts[0].Id == "i1", "再打開：存著的整份先給中樞");
        (await rig.ItemsAsync(a)).Select(i => i.ItemId).Should().Equal("i1", "i2");
        UiItems(rig.Ui, a).Should().Equal(new Dictionary<string, string> { ["i1"] = "一", ["i2"] = "二" }, "從游標接著拉到關著期間的項目");
    }

    // ───────────────────────────── 一頁在 UI 套用、還沒提交的空檔 ─────────────────────────────

    [Fact]
    public async Task 一頁已交給中樞_提交之前又打開同一個複本_整份快照不蓋掉這一頁的項目()
    {
        await using var rig = new Rig();
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson("開著的複本"));
        await rig.StartAsync();
        await rig.OpenAsync(id);
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("i1", 1, "一"));
        await rig.NotifyAsync(SyncClasses.Item);

        // 中樞套用第二則所在的那一頁時，使用者又打開它一次（例如彈出視窗）：要求排在這一頁的提交之前（合併前審查 R2）
        var reopened = 0;
        rig.Ui.ReplicaFailure = delta =>
        {
            if (delta.Items.Any(i => i.ConversationId == id && !i.Reset && i.Upserts.Any(u => u.Id == "i2")) && Interlocked.Exchange(ref reopened, 1) == 0)
                rig.Ui.Put(new ReplicaRequested(id));
            return null;
        };
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("i2", 2, "二"));
        await rig.NotifyAsync(SyncClasses.Item);

        reopened.Should().Be(1, "前提：要求在這一頁的 UI 套用期間送出");
        (await rig.ItemsAsync(id)).Select(i => i.ItemId).Should().Equal("i1", "i2");
        rig.Ui.ReplicaDeltas.Last(d => d.Items.Any(i => i.ConversationId == id)).Items.Single().Reset.Should().BeTrue("前提：打開的要求照樣回了整份");
        UiItems(rig.Ui, id).Should().Equal(new Dictionary<string, string> { ["i1"] = "一", ["i2"] = "二" }, "整份在這一頁寫進 sync.db 之後才讀");
    }

    [Fact]
    public async Task 一頁已交給中樞_提交之前要整份清單_這一頁新的複本不被移除()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var fresh = NewId();
        var asked = 0;
        rig.Ui.ReplicaFailure = delta =>
        {
            if (!delta.Full && delta.Metas.Any(m => m.ConversationId == fresh) && Interlocked.Exchange(ref asked, 1) == 0)
                rig.Ui.Put(new ReplicaRequested(null));
            return null;
        };
        rig.Publish(ConversationEventKinds.Created, fresh, MetaJson("這一頁新的"));
        await rig.NotifyAsync(SyncClasses.Meta);

        asked.Should().Be(1, "前提：要求在這一頁的 UI 套用期間送出");
        rig.Ui.ReplicaDeltas.Last(d => d.Full).Metas.Should().ContainSingle(m => m.ConversationId == fresh, "整份清單在這一頁寫進 sync.db 之後才讀（合併前審查 R2）");
    }

    [Fact]
    public async Task 一頁還沒提交時打開又關掉_重播照順序_關掉的不再拉()
    {
        await using var rig = new Rig();
        var shown = NewId();
        var other = NewId();
        rig.Publish(ConversationEventKinds.Created, shown, MetaJson("開著的"));
        rig.Publish(ConversationEventKinds.Created, other, MetaJson("打開又關掉的"));
        await rig.StartAsync();
        await rig.OpenAsync(shown);

        var once = 0;
        rig.Ui.ReplicaFailure = delta =>
        {
            if (delta.Items.Any(i => i.ConversationId == shown && !i.Reset) && Interlocked.Exchange(ref once, 1) == 0)
            {
                rig.Ui.Put(new ReplicaRequested(other));
                rig.Ui.Put(new ReplicaClosed(other));
            }

            return null;
        };
        rig.Publish(ConversationEventKinds.Item, shown, ItemJson("i1", 1, "一"));
        await rig.NotifyAsync(SyncClasses.Item);
        once.Should().Be(1, "前提：要求在這一頁的 UI 套用期間送出");

        (await rig.H.Inspect(v => v.Replicas.Open.ToList())).Should().Equal([shown], "延後的打開與關掉照到達的順序重播");
    }

    /// <summary>輪詢引擎的狀態（UI 執行緒被卡住時不能用 IdleAsync）</summary>
    private static async Task UntilAsync(Rig rig, Func<SyncEngine.EngineView, bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await rig.H.Inspect(condition))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等不到：" + because);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 舊連線的頁還卡在UI時換了連線_舊的拉取迴圈結束不放棄新連線的頁()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var a = NewId();
        var b = NewId();
        using var entered = new SemaphoreSlim(0);
        using var gate = new ManualResetEventSlim(false);
        var held = 0;
        var asked = 0;
        rig.Ui.ReplicaFailure = delta =>
        {
            if (delta.Full)
                return null;
            // 第一條連線的那一頁：UI 動作開始跑之後卡住（大量目錄或複本套用；取消已叫不醒它）
            if (delta.Metas.Any(m => m.ConversationId == a) && delta.Metas.All(m => m.ConversationId != b) && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.Release();
                gate.Wait(TimeSpan.FromSeconds(30));
                return null;
            }

            // 新連線的那一頁在 UI 套用時要整份清單：要延到這一頁寫進 sync.db 之後（合併前審查 R2）
            if (delta.Metas.Any(m => m.ConversationId == b) && Interlocked.Exchange(ref asked, 1) == 0)
                rig.Ui.Put(new ReplicaRequested(null));
            return null;
        };
        rig.Publish(ConversationEventKinds.Created, a, MetaJson("第一條連線拉到的"));
        rig.H.Server.Current!.Notify(SyncClasses.Meta);
        (await entered.WaitAsync(TimeSpan.FromSeconds(20))).Should().BeTrue("前提：第一條連線那一頁的 UI 動作卡住了");

        // 這時換了連線（伺服器推裝置政策、暫停再恢復）：新連線交握完、拉到含 b 的一頁，排在卡住的 UI 動作後面
        rig.Publish(ConversationEventKinds.Created, b, MetaJson("新連線拉到的"));
        var pulls = rig.H.Server.Pulls.Count;
        rig.H.Server.Current!.Drop();
        rig.H.Engine.RetryNow();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (rig.H.Server.Pulls.Count <= pulls)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("新連線沒有開始拉");
            await Task.Delay(10);
        }

        await UntilAsync(rig, v => v.ReplicaPagePending, "新連線的那一頁排進 UI");

        // 放行：舊連線的拉取迴圈結束（finally）在新連線的那一頁寫進去之前
        gate.Set();
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(2);

        asked.Should().Be(1, "前提：要求在新連線那一頁的 UI 套用期間送出");
        (await rig.MetasAsync()).Keys.Should().BeEquivalentTo([a, b], "新連線的那一頁照常提交");
        rig.Ui.ReplicaDeltas.Last(d => d.Full).Metas.Select(m => m.ConversationId).Should().BeEquivalentTo([a, b],
            "整份清單在新連線那一頁寫進 sync.db 之後才讀：舊連線的迴圈結束沒有拿掉延後的保護（第二輪合併前審查 R2）");
        (await rig.H.Inspect(v => v.DeferredReplicaRequests)).Should().Be(0);
    }

    // ───────────────────────────── 失敗、換實例、解除配對 ─────────────────────────────

    [Fact]
    public async Task UI套用失敗_游標不前進()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var id = NewId();
        var seq = rig.Publish(ConversationEventKinds.Created, id, MetaJson("x"));
        rig.Ui.ReplicaFailure = _ => new InvalidOperationException("模擬：中樞套用失敗");
        await rig.NotifyAsync(SyncClasses.Meta);

        (await rig.ConversationCursorsAsync()).Should().NotContainKey(SyncClasses.Meta, "套用失敗：這一頁的游標不前進、下次重拉");
        (await rig.MetasAsync()).Should().BeEmpty("提交之後才寫複本");
        rig.H.Host.LastStatus!.Detail.Should().Contain("套用伺服器的資料失敗");

        rig.Ui.ReplicaFailure = null;
        await rig.NotifyAsync(SyncClasses.Catalog);

        (await rig.ConversationCursorsAsync())[SyncClasses.Meta].Should().Be(seq);
        (await rig.MetasAsync()).Should().ContainKey(id);
        rig.Ui.ReplicaDeltas.SelectMany(d => d.Metas).Should().ContainSingle(m => m.ConversationId == id);
    }

    // ───────────────────────────── 已移除的電腦（設計 14.22） ─────────────────────────────

    private const long RevokedAt = 1791570000000;

    /// <summary>名冊裡多兩台電腦（HOME 擁有 gone、THIRD 擁有 stays），開著 gone 並拉到一個項目</summary>
    private static async Task<(TestDevice Home, string Gone, string Stays)> TwoOwnersAsync(Rig rig)
    {
        var home = TestDevice.Create("HOME-PC");
        var third = TestDevice.Create("THIRD-PC");
        rig.H.Server.Registry.AddRange([home.Entry(), third.Entry()]);
        rig.H.Server.RegistryRev = 2;
        await rig.StartAsync();
        var (gone, stays) = (NewId(), NewId());
        rig.Publish(ConversationEventKinds.Created, gone, MetaJson("HOME-PC 的對話"), author: home.DeviceId);
        rig.Publish(ConversationEventKinds.Created, stays, MetaJson("THIRD-PC 的對話"), author: third.DeviceId);
        await rig.NotifyAsync(SyncClasses.Meta);
        await rig.OpenAsync(gone);
        rig.Publish(ConversationEventKinds.Item, gone, ItemJson("i1", 1, "一"), author: home.DeviceId);
        await rig.NotifyAsync(SyncClasses.Item);
        (await rig.ItemsAsync(gone)).Should().ContainSingle();
        return (home, gone, stays);
    }

    private static async Task TrustChangedAsync(Rig rig)
    {
        rig.H.Server.RegistryRev++;
        rig.H.Server.Current!.TrustChanged(rig.H.Server.RegistryRev);
        await rig.IdleAsync();
    }

    [Fact]
    public async Task 已移除的電腦擁有的副本丟掉_只撤銷的與別台的留著()
    {
        await using var rig = new Rig();
        var (home, gone, stays) = await TwoOwnersAsync(rig);

        // 撤銷：目錄有 device.revoked、名冊照樣列它（帶撤銷）→ 副本留著
        rig.H.Server.Publish(SyncEventKinds.DeviceRevoked, new DeviceRevokedPayload(home.DeviceId, RevokedAt, DeviceRegistryRules.RevokedRetired));
        rig.H.Server.Registry[rig.H.Server.Registry.FindIndex(e => e.DeviceId == home.DeviceId)] = home.Entry(RevokedAt, DeviceRegistryRules.RevokedRetired);
        await TrustChangedAsync(rig);
        await rig.NotifyAsync(SyncClasses.Catalog);
        (await rig.MetasAsync()).Keys.Should().BeEquivalentTo([gone, stays]);
        var deltas = rig.Ui.ReplicaDeltas.Count;

        // 移除：名冊不再列它
        rig.H.Server.Registry.RemoveAll(e => e.DeviceId == home.DeviceId);
        await TrustChangedAsync(rig);

        (await rig.MetasAsync()).Keys.Should().Equal(stays);
        (await rig.ItemsAsync(gone)).Should().BeEmpty();
        rig.Ui.ReplicaDeltas.Skip(deltas).Should().ContainSingle().Which.Deleted.Should().Equal(gone);
        var mark = rig.H.Server.Pulls.Count;
        await rig.NotifyAsync(SyncClasses.Meta, SyncClasses.Item);
        rig.StreamsSince(mark).SelectMany(x => x).Should().NotContain(SyncStreams.Conversation(gone), "丟掉的複本不再拉串流");
        rig.Ui.ReplicaDeltas.Skip(deltas + 1).Should().NotContain(d => Mentions(d, gone), "只丟一次");
    }

    [Fact]
    public async Task 名冊先不列它_之後才拉到device_revoked_那一頁寫完就丟()
    {
        // 也是「舊版已存下新名冊、升級之後」的路徑：不靠名冊更新，每一頁拉取寫完之後都檢查
        await using var rig = new Rig();
        var (home, gone, stays) = await TwoOwnersAsync(rig);
        rig.H.Server.Registry.RemoveAll(e => e.DeviceId == home.DeviceId);
        await TrustChangedAsync(rig);
        (await rig.MetasAsync()).Should().ContainKey(gone, "還沒撤銷：不在名冊不算移除");

        rig.H.Server.Publish(SyncEventKinds.DeviceRevoked, new DeviceRevokedPayload(home.DeviceId, RevokedAt, DeviceRegistryRules.RevokedRetired));
        await rig.NotifyAsync(SyncClasses.Catalog);

        (await rig.MetasAsync()).Keys.Should().Equal(stays);
        rig.Ui.ReplicaDeltas.Last().Deleted.Should().Equal(gone);
    }

    [Fact]
    public async Task 伺服器換實例_複本清掉重拉()
    {
        await using var rig = new Rig();
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson("換實例前"));
        await rig.StartAsync();
        await rig.OpenAsync(id);
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("i1", 1, "內容"));
        await rig.NotifyAsync(SyncClasses.Item);
        (await rig.ItemsAsync(id)).Should().ContainSingle();
        var pulls = rig.H.Server.Pulls.Count;
        var deltas = rig.Ui.ReplicaDeltas.Count;

        rig.H.Server.Instance = new ServerInstance("instance-2", 0);
        await rig.ReconnectAsync();

        rig.Ui.ReplicaDeltas.Skip(deltas).Should().Contain(d => d.Full && d.Metas.Count == 0, "中樞的複本先清掉");
        rig.H.Server.Pulls.Skip(pulls).First().Cursors.Should().Contain(new PullCursor(0, SyncClasses.Meta), "游標清掉、從頭拉");
        (await rig.MetasAsync()).Should().ContainKey(id, "從頭重拉");
        (await rig.ItemsAsync(id)).Should().BeEmpty("項目也清掉了：要重新打開才拉");
        rig.StreamsSince(pulls).SelectMany(s => s).Should().BeEmpty();

        await rig.OpenAsync(id);
        (await rig.ItemsAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task 解除配對_複本隨sync_db刪除()
    {
        await using var rig = new Rig();
        const string Marker = "只在複本裡的標題-7f3a";
        var id = NewId();
        rig.Publish(ConversationEventKinds.Created, id, MetaJson(Marker));
        await rig.StartAsync();
        await rig.OpenAsync(id);
        rig.Publish(ConversationEventKinds.Item, id, ItemJson("i1", 1, Marker + "的項目"));
        await rig.NotifyAsync(SyncClasses.Item);
        (await rig.MetasAsync()).Should().ContainKey(id);
        (await rig.ItemsAsync(id)).Should().ContainSingle();
        await rig.H.Engine.StopAsync(TimeSpan.FromSeconds(5));

        var marker = Encoding.UTF8.GetBytes(Marker);
        bool HasMarker(string file) => File.ReadAllBytes(file).AsSpan().IndexOf(marker) >= 0;
        var files = Directory.GetFiles(rig.H.Dir.Root, "*", SearchOption.AllDirectories);
        var database = rig.H.Paths.DatabaseFile;
        var dbFiles = new[] { database, database + "-wal", database + "-shm" };
        files.Where(f => !dbFiles.Contains(f, StringComparer.OrdinalIgnoreCase)).Where(HasMarker).Should().BeEmpty("複本只存在 sync.db（不寫對話資料夾、不另開檔案）");
        files.Where(f => dbFiles.Contains(f, StringComparer.OrdinalIgnoreCase)).Where(HasMarker).Should().NotBeEmpty("對照：複本確實在 sync.db 裡");
        Directory.GetFileSystemEntries(rig.Root).Should().BeEmpty("對話資料夾的根目錄沒有多出任何東西");

        // 解除配對刪掉 sync.db（含 -wal／-shm，SyncCoordinator.DeletePairing）：複本跟著不見
        foreach (var file in dbFiles)
            File.Delete(file);
        Directory.GetFiles(rig.H.Dir.Root, "*", SearchOption.AllDirectories).Where(HasMarker).Should().BeEmpty();
    }
}
