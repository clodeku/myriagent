using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Engine;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// 重整與回補（WP4.3 計畫步 12；D10、D12、D22～D24、D36、D38，擁有者答 2～答 4）。大部分以引擎＋<see cref="FakeConversationUi"/>（代替發布者與中樞）＋
/// 暫存的對話資料夾（沒載入的對話真的寫 items.json、meta.json）＋照 WP4.2 定案模擬的假伺服器測；D10 的「之後載入的 Ord 與已送的相同」與 S19 桌面端
/// 以真的中樞串起來（<see cref="ReconcileChainRig"/>）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ConversationReconcileTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Local);

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box);
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            Root = H.Dir.Combine("conversations");
            Directory.CreateDirectory(Root);
            Box.ConversationsRoot = Root;
            H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
            _ = H.Server.Conversations;
            H.OpenStore = (path, id) =>
            {
                var store = SyncStore.Open(path, id);
                store.FreeDiskBytesForTest = FreeDisk;
                store.RetainedStatsForTest = Retained;
                return store;
            };
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        /// <summary>對話資料夾的根目錄（沒載入的對話的檔案）</summary>
        public string Root { get; }

        public FakeConversations Model => H.Server.Conversations;

        /// <summary>之後開啟的 sync.db 回報的剩餘空間（預設不限）</summary>
        public long FreeDisk { get; set; } = long.MaxValue;

        /// <summary>之後開啟的 sync.db 回報的保留中的對話列（null＝照實算）</summary>
        public (int Rows, long Bytes)? Retained { get; set; }

        public Task StartAsync(Action<SyncEngine>? beforeStart = null) => H.StartAsync(beforeStart);

        public Task StopAsync() => H.Engine.StopAsync(TimeSpan.FromSeconds(5));

        public Task IdleAsync() => H.IdleAsync();

        public Task SendAsync(double seconds = 1) => H.AdvanceAsync(seconds);

        /// <summary>一秒一秒撥快，直到重整與回補做完、outbox 的列都完成</summary>
        public async Task SettleAsync(int maxSeconds = 300)
        {
            for (var i = 0; i < maxSeconds; i++)
            {
                await H.AdvanceAsync(1);
                if (Box.Count == 0 && await H.Inspect(v => v.ReconcileQueue.Count == 0 && v.ActiveReconcile is null && !v.Refreshing && v.Outbox.All(e => e.IsDone)))
                    return;
            }

            throw new XunitException($"{maxSeconds} 秒（測試時鐘）內重整與回補沒有做完");
        }

        /// <summary>重新連上（伺服器的設定先改好），送完待送的、進 Live</summary>
        public async Task ReconnectAsync()
        {
            H.Server.ConnectFailure = null;
            if (H.Server.Current is { Closed: false } session)
                session.Drop();
            await IdleAsync();
            H.Engine.RetryNow();
            await IdleAsync();
            await SendAsync(2);
            (await H.StateAsync()).Should().Be(SyncEngineState.Live);
        }

        /// <summary>伺服器換了實例（重建，什麼都沒有）：之後的第一次連線就是新的實例</summary>
        public void NewServerInstance(string id = "instance-2")
        {
            Model.Clear();
            H.Server.Instance = new ServerInstance(id, 0);
        }

        /// <summary>一次放出一個扣住的確認，直到條件成立</summary>
        public async Task ReleaseUntilAsync(Func<Task<bool>> done, string because)
        {
            for (var i = 0; i < 60; i++)
            {
                if (await done())
                    return;
                H.Server.Current!.ReleaseOneAck();
                await SendAsync();
            }

            throw new XunitException("放出確認之後等不到：" + because);
        }

        public FakeConversation New(string? policy = null, int minute = 0, bool loaded = true)
        {
            var c = Ui.Add(new FakeConversation(new AgentConversation
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "測試對話",
                AccountId = EngineHarness.LocalAccount,
                Provider = AgentProvider.Claude,
                SyncPolicy = policy,
                CreatedAt = T0,
                UpdatedAt = T0.AddMinutes(minute),
            }));
            c.Loaded = loaded;
            return c;
        }

        /// <summary>中樞記憶體裡的項目（已載入、Ord 已配）；不放進盒子（重整時才看到）</summary>
        public List<AgentChatItem> Fill(FakeConversation c, int count, string prefix = "記憶體")
        {
            var added = new List<AgentChatItem>(count);
            for (var i = 0; i < count; i++)
            {
                var item = NewItem($"{prefix} {i}。");
                item.Ord = c.NextOrd++;
                c.Items.Add(item);
                added.Add(item);
            }

            return added;
        }

        /// <summary>沒載入的對話：WP4.3 之前的格式（項目沒有 Ord）寫進對話資料夾</summary>
        public List<AgentChatItem> Disk(FakeConversation c, int count, string prefix = "檔案")
        {
            var items = Enumerable.Range(0, count).Select(i => NewItem($"{prefix} {i}。")).ToList();
            WriteFiles(c, items);
            return items;
        }

        public void WriteFiles(FakeConversation c, IReadOnlyList<AgentChatItem> items)
        {
            JsonFile.Write(Path.Combine(DirOf(c), "meta.json"), c.Meta);
            JsonFile.Write(Path.Combine(DirOf(c), "items.json"), items);
        }

        public string DirOf(FakeConversation c) => Path.Combine(Root, c.Id);

        /// <summary>檔案的項目照中樞載入的規則（meta 的 NextOrd、旁檔、發布紀錄）會配到的 Ord</summary>
        public Dictionary<string, long> Planned(FakeConversation c, IReadOnlyList<AgentChatItem> items, OrdHint? hint = null)
        {
            var plan = OrdAssignRules.PlanLoad(items.Select(i => (i.Id, i.Ord)).ToList(), c.Meta.NextOrd, ConversationStore.ReadOrdReservationFile(DirOf(c)), hint);
            return items.Select((item, index) => (item.Id, Ord: item.Ord is >= 1 ? item.Ord.Value : plan.Ords[index]))
                .ToDictionary(p => p.Id, p => p.Ord, StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, long> ServerItems(FakeConversation c) => Model.Items(c.Id);

        /// <summary>目前送出過的批次數（之後 <see cref="Sent"/> 的 since）</summary>
        public int Mark() => H.Server.Batches.Count;

        public IReadOnlyList<SyncEvent> Sent(FakeConversation? c = null, int since = 0)
            => H.Server.Batches.Skip(since).SelectMany(b => b.Events).Where(e => e.ConversationId is not null && (c is null || e.ConversationId == c.Id)).ToList();

        public int CallMark() => Ui.Calls.Count;

        public IReadOnlyList<string> Calls(int since = 0) => Ui.Calls.Skip(since).ToList();

        public static string ItemsCall(FakeConversation c) => $"RequestItems:{c.Id}:0";

        public async Task<IReadOnlyList<OutboxEntry>> RowsAsync(FakeConversation c)
            => (await H.Inspect(v => v.Outbox)).Where(e => e.ConversationId == c.Id).ToList();

        public async Task<int> ItemRowsAsync(FakeConversation c) => (await RowsAsync(c)).Count(r => r.Kind == ConversationEventKinds.Item);

        public async Task<PublishMetaRow> RowAsync(FakeConversation c) => (await H.Inspect(v => v.Store!.LoadPublishMeta()))[c.Id];

        public Task<IReadOnlyList<PublishedItemRow>> PublishedAsync(FakeConversation c) => H.Inspect(v => v.Store!.LoadPublishedItems(c.Id));

        public Task<IReadOnlySet<string>> NeedsRedoAsync() => H.Inspect(v => v.Store!.LoadNeedsRedo());

        public Task<IReadOnlyDictionary<string, long>> BackfilledAsync() => H.Inspect(v => v.Store!.LoadBackfilled());

        public Task<int> ProjectedAsync() => H.Inspect(v => v.ProjectedItems);

        public void Snapshot(FakeConversation c, string reason) => H.Server.Current!.Snapshot(c.Id, reason);

        /// <summary>改目前的 sync.db 回報的保留中的列與剩餘空間（之後重開的也一樣）</summary>
        public async Task SetDiskAsync((int Rows, long Bytes)? retained, long free)
        {
            Retained = retained;
            FreeDisk = free;
            await H.Inspect(v =>
            {
                v.Store!.RetainedStatsForTest = retained;
                v.Store.FreeDiskBytesForTest = free;
                return true;
            });
        }

        /// <summary>引擎停著的時候直接改 sync.db（模擬中間跑過的舊版、或當機前的狀態）</summary>
        public void Sql(string sql, params (string Name, object Value)[] args)
        {
            using var connection = new SqliteConnection(SyncStore.ConnectionString(H.Paths.DatabaseFile, SyncStoreOpenMode.Open));
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static AgentChatItem NewItem(string text, AgentItemKind kind = AgentItemKind.Assistant)
        => new() { Kind = kind, Status = AgentItemStatus.Done, Text = text, CreatedAt = T0, UpdatedAt = T0 };

    private static Dictionary<string, long> OrdsOf(IEnumerable<AgentChatItem> items) => items.ToDictionary(i => i.Id, i => i.Ord!.Value, StringComparer.Ordinal);

    private static string Json(SyncEvent e) => PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開");

    private static string IdOf(SyncEvent e) => JObject.Parse(Json(e))["id"]!.Value<string>()!;

    private static IEnumerable<SyncEvent> OfKind(IEnumerable<SyncEvent> events, string kind) => events.Where(e => e.Kind == kind);

    /// <summary>讀檔的閘門：讀檔開始時通知，等測試放行</summary>
    private sealed class FileGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(string wire)
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new XunitException("10 秒內等不到：" + because);
            await Task.Delay(20);
        }
    }

    // ───────────────────────────── 回補的順序與範圍（答 2、D24） ─────────────────────────────

    [Fact]
    public async Task 第一次上線_所有對話先created_再由新到舊送項目()
    {
        await using var rig = new Rig();
        var old = rig.New(minute: 0);
        var oldItems = rig.Fill(old, 3);
        var mid = rig.New(minute: 10, loaded: false);
        var midItems = rig.Disk(mid, 4);
        var newest = rig.New(minute: 20);
        var newItems = rig.Fill(newest, 2);
        var metadata = rig.New(ConversationSyncPolicies.Metadata, minute: 30);
        rig.Fill(metadata, 2);
        var local = rig.New(ConversationSyncPolicies.LocalOnly, minute: 40);
        rig.Fill(local, 1);

        await rig.StartAsync();
        await rig.SettleAsync();

        var sent = rig.Sent();
        var firstItem = sent.ToList().FindIndex(e => e.Kind == ConversationEventKinds.Item);
        firstItem.Should().BeGreaterThan(0);
        sent.Take(firstItem).Where(e => e.Kind == ConversationEventKinds.Created).Select(e => e.ConversationId)
            .Should().BeEquivalentTo([old.Id, mid.Id, newest.Id, metadata.Id], "先為所有該發布的對話寫 conv.created（metadata 的也是），再送內容");
        OfKind(sent, ConversationEventKinds.Created).Should().HaveCount(4);
        OfKind(sent, ConversationEventKinds.Item).Select(e => e.ConversationId).Distinct()
            .Should().Equal([newest.Id, mid.Id, old.Id], "內容依最後更新由新到舊（metadata 的不送內容）");
        rig.Sent(local).Should().BeEmpty("localOnly 的什麼都不送");

        rig.ServerItems(old).Should().BeEquivalentTo(OrdsOf(oldItems));
        rig.ServerItems(newest).Should().BeEquivalentTo(OrdsOf(newItems));
        rig.ServerItems(mid).Should().BeEquivalentTo(rig.Planned(mid, midItems), "沒載入的對話照讀檔的結果送");
        rig.ServerItems(metadata).Should().BeEmpty();
        (await rig.BackfilledAsync()).Keys.Should().BeEquivalentTo([old.Id, mid.Id, newest.Id], "全部確認之後記回補完成");
        rig.Ui.Progress.Last().Progress.Should().BeNull("都完成了");
    }

    [Fact]
    public async Task 回補跳過已封鎖的對話()
    {
        await using var rig = new Rig();
        var ok = rig.New(minute: 0);
        var okItems = rig.Fill(ok, 2);
        var other = rig.New(minute: 10);
        rig.Fill(other, 2);
        // 重新配對前的舊身分擁有的對話：created 收到 not_owner、封鎖
        rig.Model.Seed(other.Id, "device-old-identity");

        await rig.StartAsync();
        await rig.SettleAsync();

        (await rig.RowAsync(other)).Blocked.Should().Be(ConversationRejectCodes.NotOwner, "前提：封鎖了");
        OfKind(rig.Sent(other), ConversationEventKinds.Item).Should().BeEmpty("封鎖的對話不回補");
        rig.Calls().Should().NotContain(Rig.ItemsCall(other), "連快照都不要");
        (await rig.BackfilledAsync()).Keys.Should().Equal([ok.Id], "封鎖的對話不記回補完成");
        rig.ServerItems(ok).Should().BeEquivalentTo(OrdsOf(okItems));
        await rig.SendAsync(3);
        rig.Ui.Notices.Should().ContainSingle(n => n.Kind == PublishNoticeKind.NotOwner, "彙總提示（2 秒內合併）");
    }

    [Fact]
    public async Task 回補的內容先清秘密回答再遮蔽()
    {
        var random = new Random(20261020);
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        string Body(int length) => new(Enumerable.Range(0, length).Select(_ => chars[random.Next(chars.Length)]).ToArray());
        var token = "sk-" + "ant-" + "api03-" + Body(40);
        var password = "f" + Body(22) + "7";
        var url = "git clone https://deploy:" + password + "@git.example.test/org/repo.git";
        CredentialLeakRules.HasLeak(token).Should().BeTrue();
        CredentialLeakRules.HasLeak(url).Should().BeTrue("網址的 userinfo 是 F10 的樣式");
        var secret = "答案-" + Body(16);

        await using var rig = new Rig();
        var c = rig.New(loaded: false);
        var question = new AgentQuestion { Id = "q1", Text = "密碼是什麼", IsSecret = true };
        var card = new AgentChatItem
        {
            Kind = AgentItemKind.Question, Status = AgentItemStatus.Done, AnswerAsMessage = true, Decision = "answered",
            Questions = [question], Answers = new Dictionary<string, List<string>> { ["q1"] = [ConversationWire.HiddenAnswer] }, CreatedAt = T0, UpdatedAt = T0,
        };
        // WP4.3 之前的中樞把秘密原文存成「我的回答：」的文字（存在 items.json 裡）
        var answer = NewItem(AnswerMessageRules.Prefix + "密碼是什麼：" + secret, AgentItemKind.User);
        var leak = NewItem("權杖 " + token + "\n" + url);
        var items = new List<AgentChatItem> { NewItem("請幫我設定部署", AgentItemKind.User), card, answer, leak };
        rig.WriteFiles(c, items);
        var file = File.ReadAllBytes(Path.Combine(rig.DirOf(c), "items.json"));

        await rig.StartAsync();
        await rig.SettleAsync();

        var accepted = rig.Model.Accepted.Where(e => e.ConversationId == c.Id).ToList();
        OfKind(accepted, ConversationEventKinds.Item).Select(IdOf).Should().BeEquivalentTo(items.Select(i => i.Id));
        foreach (var e in accepted)
        {
            var json = Json(e);
            json.Should().NotContain(secret).And.NotContain(token).And.NotContain(password);
            CredentialLeakRules.HasLeak(json).Should().BeFalse();
        }

        var sentAnswer = OfKind(accepted, ConversationEventKinds.Item).Single(e => IdOf(e) == answer.Id);
        JObject.Parse(Json(sentAnswer))["text"]!.Value<string>().Should().Contain(ConversationWire.HiddenAnswer, "對著整份清單清除秘密回答");
        File.ReadAllBytes(Path.Combine(rig.DirOf(c), "items.json")).Should().Equal(file, "唯讀：檔案不動");
    }

    // ───────────────────────────── 排程：一般事件優先、不限速、只有磁碟保護（答 3） ─────────────────────────────

    [Fact]
    public async Task 一般事件待送時不排回補()
    {
        await using var rig = new Rig();
        var b = rig.New();
        var items = rig.Fill(b, 3);
        rig.H.Server.HoldAcks = true;
        await rig.StartAsync();
        await rig.SendAsync();
        (await rig.RowsAsync(b)).Should().ContainSingle(r => r.Kind == ConversationEventKinds.Created, "前提：created 送出了、確認被扣住");

        // created 確認之前加一筆一般事件（cli.state），之後只放出 created 的確認
        rig.H.Append();
        await rig.SendAsync();
        await rig.ReleaseUntilAsync(async () => (await rig.RowAsync(b)).CreatedAcked, "created 確認");
        await rig.IdleAsync();

        var outbox = await rig.H.Inspect(v => v.Outbox);
        outbox.Should().Contain(e => e.Kind == SyncEventKinds.CliState && !e.IsDone, "前提：一般事件還沒完成");
        (await rig.ItemRowsAsync(b)).Should().Be(0, "一般事件待送：不排回補");
        (await rig.H.Inspect(v => v.ReconcileQueue)).Should().ContainSingle(q => q.WireId == b.Id && q.Reason == ReconcileReason.Backfill);
        (await rig.H.Inspect(v => v.ActiveReconcile)).Should().BeNull();

        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        await rig.SettleAsync();
        rig.ServerItems(b).Should().BeEquivalentTo(OrdsOf(items), "一般事件完成之後照常回補");
    }

    [Fact]
    public async Task 本機對話執行中_回補照常()
    {
        await using var rig = new Rig();
        var running = rig.New(minute: 30);
        running.Running = true;
        rig.Fill(running, 1);
        var b = rig.New(minute: 0, loaded: false);
        var items = rig.Disk(b, 5);

        await rig.StartAsync();
        await rig.SettleAsync();

        rig.ServerItems(b).Should().BeEquivalentTo(rig.Planned(b, items), "本機有對話執行中也照常回補（不暫停、不限速）");
        (await rig.H.Inspect(v => v.BackfillPause)).Should().BeNull();
    }

    [Fact]
    public async Task 保留上限不是節流_超過256MB照常回補()
    {
        await using var rig = new Rig { Retained = (200_000, 300L * 1024 * 1024) };
        var b = rig.New(loaded: false);
        var items = rig.Disk(b, 5);

        await rig.StartAsync();
        await rig.SettleAsync();

        rig.ServerItems(b).Should().BeEquivalentTo(rig.Planned(b, items), "保留 300 MB 不是暫停的條件（只有 4 GiB 與磁碟剩 2 GiB）");
        (await rig.H.Inspect(v => v.BackfillPause)).Should().BeNull("從沒暫停過");
        rig.Ui.Progress.Should().OnlyContain(p => p.Pause == null);
    }

    [Fact]
    public async Task 保留payload超過4GiB或磁碟剩不到2GiB_暫停_Pause記暫停時間()
    {
        await using var rig = new Rig { Retained = (10, ReconcileQueueRules.RetainedMaxBytes + 1) };
        var b = rig.New();
        var items = rig.Fill(b, 3);

        await rig.StartAsync();
        await rig.SendAsync(2);
        (await rig.RowAsync(b)).CreatedAcked.Should().BeTrue("前提：created 照送（重整不受磁碟保護影響）");
        var first = await rig.H.Inspect(v => v.BackfillPause);
        first.Should().NotBeNull();
        first!.Reason.Should().Be(BackfillHold.RetainedBytes);
        first.SinceMs.Should().NotBeNull();
        (await rig.H.Inspect(v => v.HasHoldTimer)).Should().BeTrue("30 秒後再看一次");
        (await rig.ItemRowsAsync(b)).Should().Be(0, "暫停：不寫內容");
        rig.Ui.Progress.Last().Pause.Should().Be(first, "暫停時間交給 UI");
        rig.Ui.Progress.Last().Progress.Should().NotBeNull();

        // 換成磁碟剩不到 2 GiB：原因改了、起點不變
        await rig.SetDiskAsync((10, 0), ReconcileQueueRules.MinFreeDiskBytes - 1);
        await rig.SendAsync(30);
        var second = await rig.H.Inspect(v => v.BackfillPause);
        second!.Reason.Should().Be(BackfillHold.LowDisk);
        second.SinceMs.Should().Be(first.SinceMs, "原因改變不重設起點");
        (await rig.ItemRowsAsync(b)).Should().Be(0);

        // 空間回來：記下這一次的長度，接著回補
        await rig.SetDiskAsync((10, 0), long.MaxValue);
        await rig.SendAsync(30);
        await rig.SettleAsync();
        var done = await rig.H.Inspect(v => v.BackfillPause);
        done!.SinceMs.Should().BeNull();
        done.TotalMs.Should().BeGreaterThanOrEqualTo(30_000, "暫停的總長保留");
        rig.Ui.Progress.Should().Contain(p => p.Pause != null && p.Pause.SinceMs == null && p.Pause.TotalMs == done.TotalMs);
        rig.ServerItems(b).Should().BeEquivalentTo(OrdsOf(items));
        (await rig.H.Inspect(v => v.HasHoldTimer)).Should().BeFalse("閒置時沒有計時器");
    }

    // ───────────────────────────── 沒載入的對話（D10） ─────────────────────────────

    [Fact]
    public async Task 沒載入的對話_唯讀讀檔_不寫任何檔_之後載入的Ord與已送的相同()
    {
        var seeds = new[]
        {
            ReconcileChainRig.Seed(T0, 7, "第一個"),
            ReconcileChainRig.Seed(T0.AddMinutes(5), 3, "第二個"),
        };
        await using var rig = new ReconcileChainRig(seeds);
        var before = seeds.ToDictionary(s => s.Meta.Id, s => ReconcileChainRig.FolderDigest(rig.DirOf(s.Meta.Id)));

        await rig.StartAsync();
        await rig.DrainAsync();

        foreach (var (meta, items) in seeds)
        {
            ReconcileChainRig.FolderDigest(rig.DirOf(meta.Id)).Should().Be(before[meta.Id], "沒載入的對話唯讀：資料夾裡什麼都沒寫（含 sync-ord.json）");
            rig.Model.Items(meta.Id).Should().BeEquivalentTo(ReconcileChainRig.Planned(items));
        }

        // 之後載入、伺服器要快照（網頁打開）：中樞以發布紀錄（hint）準備 Ord，和已送的相同，摘要相同不重送
        var mark = rig.E.Server.Batches.Count;
        foreach (var (meta, _) in seeds)
        {
            rig.Hub.Items(meta.Id).Should().NotBeEmpty();
            rig.E.Server.Current!.Snapshot(meta.Id, SnapshotReasons.Open);
        }

        await rig.DrainAsync();
        foreach (var (meta, _) in seeds)
            rig.Hub.Items(meta.Id).ToDictionary(i => i.Id, i => i.Ord!.Value).Should().BeEquivalentTo(rig.Model.Items(meta.Id), "載入之後的 Ord 等於已送的");
        rig.E.Server.Batches.Skip(mark).SelectMany(b => b.Events).Where(e => e.Kind == ConversationEventKinds.Item).Should().BeEmpty("摘要相同不重送");
    }

    [Fact]
    public async Task 已載入的用中樞記憶體_檔案結果被丟掉()
    {
        await using var rig = new Rig();
        var c = rig.New(loaded: false);
        var fileItems = rig.Disk(c, 3);
        var planned = rig.Planned(c, fileItems);
        var gate = new FileGate();

        var flow = Task.Run(async () =>
        {
            await rig.StartAsync(e => e.FileReadGateForTest = gate.WaitAsync);
            await rig.SendAsync(2);
        });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 讀檔途中中樞載入了這個對話（同樣的 id 與 Ord；記憶體裡的第一則已經改了）
        foreach (var item in fileItems)
        {
            var copy = ChatItemCopyRules.Copy(item);
            copy.Ord = planned[item.Id];
            c.Items.Add(copy);
        }

        c.Items[0].Text = "記憶體的版本";
        c.NextOrd = planned.Values.Max() + 1;
        c.Loaded = true;
        gate.Release.SetResult();
        await flow;
        await rig.SettleAsync();

        var texts = OfKind(rig.Model.Accepted.Where(e => e.ConversationId == c.Id), ConversationEventKinds.Item).Select(e => JObject.Parse(Json(e))["text"]!.Value<string>()).ToList();
        texts.Should().Contain("記憶體的版本", "讀完檔再問一次中樞：已載入就用記憶體");
        texts.Should().NotContain(fileItems[0].Text, "檔案的結果被丟掉");
        rig.ServerItems(c).Should().BeEquivalentTo(OrdsOf(c.Items));
        rig.Calls().Count(call => call == Rig.ItemsCall(c)).Should().Be(2, "開始時一次（沒載入）、讀完檔再確認一次（已載入）");
    }

    // ───────────────────────────── 摘要比對（D11、D12） ─────────────────────────────

    [Fact]
    public async Task 摘要相同不重送()
    {
        await using var rig = new Rig();
        var c = rig.New();
        rig.Fill(c, 4);
        await rig.StartAsync();
        await rig.SettleAsync();
        var mark = rig.Mark();
        var calls = rig.CallMark();

        await rig.H.RestartAsync();
        await rig.SettleAsync();

        rig.Calls(calls).Should().Contain(Rig.ItemsCall(c), "前提：重開之後整份比對過");
        rig.Sent(c, mark).Should().BeEmpty("摘要相同：created、meta、項目都不重送");
        (await rig.ProjectedAsync()).Should().Be(0, "來源摘要相同的連投影都不做");
    }

    [Fact]
    public async Task items_json的SHA相同_不投影()
    {
        await using var rig = new Rig();
        var c = rig.New(loaded: false);
        rig.Disk(c, 4);
        await rig.StartAsync();
        await rig.SettleAsync();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(rig.DirOf(c), "items.json"))));
        (await rig.RowAsync(c)).ItemsFileSha256.Should().Be(sha, "回補完成時記下 items.json 的 SHA-256");
        var mark = rig.Mark();
        var calls = rig.CallMark();

        await rig.H.RestartAsync();
        await rig.SettleAsync();

        rig.Calls(calls).Count(call => call == Rig.ItemsCall(c)).Should().Be(1, "沒載入、SHA 相同：只問一次中樞，不再確認");
        (await rig.ProjectedAsync()).Should().Be(0, "SHA 相同不投影");
        rig.Sent(c, mark).Should().BeEmpty();
    }

    [Fact]
    public async Task 本機刪掉的項目_送itemRemoved()
    {
        await using var rig = new Rig();
        var mem = rig.New(minute: 1);
        var memItems = rig.Fill(mem, 3);
        var disk = rig.New(minute: 0, loaded: false);
        var diskItems = rig.Disk(disk, 3);
        await rig.StartAsync();
        await rig.SettleAsync();
        var diskOrds = rig.ServerItems(disk).ToDictionary(p => p.Key, p => p.Value);

        // app 沒開時刪掉中間那一則（記憶體與檔案各一個）
        await rig.StopAsync();
        mem.Items.Remove(memItems[1]);
        rig.WriteFiles(disk, [diskItems[0], diskItems[2]]);
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        OfKind(rig.Sent(mem, mark), ConversationEventKinds.ItemRemoved).Select(IdOf).Should().Equal([memItems[1].Id]);
        OfKind(rig.Sent(disk, mark), ConversationEventKinds.ItemRemoved).Select(IdOf).Should().Equal([diskItems[1].Id]);
        rig.ServerItems(mem).Should().BeEquivalentTo(OrdsOf([memItems[0], memItems[2]]));
        rig.ServerItems(disk).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [diskItems[0].Id] = diskOrds[diskItems[0].Id],
            [diskItems[2].Id] = diskOrds[diskItems[2].Id],
        }, "沒有 Ord 的檔案以發布紀錄沿用原本的 Ord");
        OfKind(rig.Sent(disk, mark), ConversationEventKinds.Item).Should().BeEmpty("留下的項目摘要相同不重送");
    }

    [Fact]
    public async Task 項目檔不在或損毀過_不推斷刪除_掛鉤的刪除照送()
    {
        await using var rig = new Rig();
        var missing = rig.New(minute: 3, loaded: false);
        var missingItems = rig.Disk(missing, 2);
        var backup = rig.New(minute: 2, loaded: false);
        var backupItems = rig.Disk(backup, 2);
        var mem = rig.New(minute: 1);
        var memItems = rig.Fill(mem, 3);
        await rig.StartAsync();
        await rig.SettleAsync();
        var missingOrds = rig.ServerItems(missing).ToDictionary(p => p.Key, p => p.Value);
        var backupOrds = rig.ServerItems(backup).ToDictionary(p => p.Key, p => p.Value);

        // app 沒開時：一個的 items.json 不見了；一個損毀過（改名成 .corrupt-*、之後存的新檔只有錯誤項目）；
        // 載入的那個在中樞載入時損毀（清單只剩一筆沒有 Ord 的錯誤項目）
        await rig.StopAsync();
        File.Delete(Path.Combine(rig.DirOf(missing), "items.json"));
        var backupError = NewItem("對話紀錄損毀，已備份", AgentItemKind.Error);
        rig.WriteFiles(backup, [backupError]);
        File.WriteAllText(Path.Combine(rig.DirOf(backup), "items.json.corrupt-20261016-090000"), "[{");
        mem.Items.Clear();
        mem.Items.Add(NewItem("對話紀錄損毀，已備份", AgentItemKind.Error));
        mem.RemovalsTrusted = false;
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        foreach (var c in new[] { missing, backup, mem })
            OfKind(rig.Sent(c, mark), ConversationEventKinds.ItemRemoved).Should().BeEmpty($"{c.Meta.Title}：清單可能少了東西，不推斷刪除（合併前審查 F2）");
        rig.ServerItems(missing).Should().BeEquivalentTo(missingOrds);
        rig.ServerItems(backup).Should().Contain(backupOrds).And.ContainKey(backupError.Id, "其他項目照常發布");
        rig.ServerItems(mem).Keys.Should().Contain(memItems.Select(i => i.Id)).And.Contain(mem.Items[0].Id, "其他項目照常發布");

        // 使用者真的刪的項目：掛鉤直接送
        rig.Ui.Put(new ItemRemovedChange(mem.Id, mem.Items[0].Id));
        mem.Items.Clear();
        await rig.SettleAsync();
        OfKind(rig.Sent(mem, mark), ConversationEventKinds.ItemRemoved).Should().ContainSingle();
    }

    [Fact]
    public async Task 項目檔損毀過_讀檔時只推斷損毀之後的項目的刪除()
    {
        await using var rig = new Rig();
        var c = rig.New(loaded: false);
        rig.Disk(c, 2);
        await rig.StartAsync();
        await rig.SettleAsync();
        var beforeOrds = rig.ServerItems(c).ToDictionary(p => p.Key, p => p.Value);
        beforeOrds.Should().HaveCount(2, "前提");

        // app 沒開時損毀：清單只剩錯誤項目，之後接著加了兩則（Ord 接在後面）
        await rig.StopAsync();
        var error = NewItem("對話紀錄損毀，已備份", AgentItemKind.Error);
        var kept = NewItem("損毀之後的一則");
        var later = NewItem("之後被移除的一則");
        rig.WriteFiles(c, [error, kept, later]);
        File.WriteAllText(Path.Combine(rig.DirOf(c), "items.json.corrupt-20261016-090000"), "[{");
        await rig.StartAsync();
        await rig.SettleAsync();
        rig.ServerItems(c).Keys.Should().Contain([error.Id, kept.Id, later.Id], "前提：損毀之後的項目發布了");

        // 之後那一則被移除（沒有 conv1 的期間、掛鉤的移除沒送）：重整讀檔時補送，損毀之前的不推斷（第二輪合併前審查 F2-untrusted）
        await rig.StopAsync();
        rig.WriteFiles(c, [error, kept]);
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        OfKind(rig.Sent(c, mark), ConversationEventKinds.ItemRemoved).Select(IdOf).Should().Equal(later.Id);
        rig.ServerItems(c).Should().Contain(beforeOrds, "損毀之前的（可能是損毀時遺失的）不推斷刪除");
        rig.ServerItems(c).Should().NotContainKey(later.Id);
    }

    [Fact]
    public async Task 分頁途中移除項目_沒有誤送itemRemoved()
    {
        await using var rig = new Rig();
        var c = rig.New();
        var items = rig.Fill(c, 700);
        rig.H.Server.HoldAcks = true;
        await rig.StartAsync();
        await rig.ReleaseUntilAsync(async () => await rig.ItemRowsAsync(c) > 0, "第一頁寫進 outbox");
        (await rig.ItemRowsAsync(c)).Should().Be(PublishTimingRules.ItemsPage, "前提：第一頁寫了、第二頁等下一輪");

        // 第一頁與第二頁範圍內各移除一則（中樞照常放 ItemRemovedChange）
        foreach (var removed in new[] { items[2], items[599] })
        {
            c.Items.Remove(removed);
            rig.Ui.Put(new ItemRemovedChange(c.Id, removed.Id));
        }

        await rig.IdleAsync();
        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        await rig.SettleAsync();

        OfKind(rig.Sent(c), ConversationEventKinds.ItemRemoved).Select(IdOf).Should().BeEquivalentTo([items[2].Id, items[599].Id], "只有真的移除的");
        rig.ServerItems(c).Should().BeEquivalentTo(OrdsOf(c.Items), "依 Ord 分頁：移除項目不讓第二頁跳過或重複");
    }

    // ───────────────────────────── 對話層級的刪除（D12） ─────────────────────────────

    [Fact]
    public async Task 本機已刪的對話_送deleted()
    {
        await using var rig = new Rig();
        var gone = rig.New(minute: 1);
        rig.Fill(gone, 2);
        var keep = rig.New(minute: 0);
        rig.Fill(keep, 1);
        await rig.StartAsync();
        await rig.SettleAsync();

        // app 沒開時刪掉了（資料夾不在）
        await rig.StopAsync();
        rig.Ui.Remove(gone.Id);
        await rig.StartAsync();
        await rig.SettleAsync();

        rig.Sent(gone).Last().Kind.Should().Be(ConversationEventKinds.Deleted);
        rig.Model[gone.Id]!.Deleted.Should().BeTrue();
        (await rig.RowAsync(gone)).DeletedAt.Should().NotBeNull("記墓碑");
        rig.Model[keep.Id]!.Deleted.Should().BeFalse();
        OfKind(rig.Sent(keep), ConversationEventKinds.Deleted).Should().BeEmpty();
    }

    [Fact]
    public async Task Id不符_損毀還原失敗_被占用_三種都不送deleted()
    {
        await using var rig = new Rig();
        var mismatch = rig.New(minute: 3);
        rig.Fill(mismatch, 1);
        var corrupt = rig.New(minute: 2);
        rig.Fill(corrupt, 1);
        var locked = rig.New(minute: 1);
        rig.Fill(locked, 1);
        var gone = rig.New(minute: 0);
        rig.Fill(gone, 1);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 中樞載入時略過的三種（清單裡沒有、資料夾還在），另一個是真的刪掉的對照組
        await rig.StopAsync();
        foreach (var c in new[] { mismatch, corrupt, locked, gone })
            rig.Ui.Remove(c.Id);
        var other = new AgentConversation { Id = Guid.NewGuid().ToString("N"), Title = "別的對話", AccountId = EngineHarness.LocalAccount, Provider = AgentProvider.Claude };
        JsonFile.Write(Path.Combine(rig.DirOf(mismatch), "meta.json"), other);
        Directory.CreateDirectory(rig.DirOf(corrupt));
        File.WriteAllText(Path.Combine(rig.DirOf(corrupt), "meta.json.corrupt-20261016-090000"), "{");
        JsonFile.Write(Path.Combine(rig.DirOf(locked), "meta.json"), locked.Meta);
        var mark = rig.Mark();
        using (new FileStream(Path.Combine(rig.DirOf(locked), "meta.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await rig.StartAsync();
            await rig.SettleAsync();
        }

        foreach (var c in new[] { mismatch, corrupt, locked })
        {
            OfKind(rig.Sent(c, mark), ConversationEventKinds.Deleted).Should().BeEmpty("資料夾還在（Id 不符、損毀備份、被占用）：不送刪除");
            (await rig.RowAsync(c)).DeletedAt.Should().BeNull();
            rig.Model[c.Id]!.Deleted.Should().BeFalse();
        }

        OfKind(rig.Sent(gone, mark), ConversationEventKinds.Deleted).Should().ContainSingle("對照組：資料夾不在才確定刪除");
    }

    [Fact]
    public async Task 重整進行中刪除_之後的結果被丟掉()
    {
        await using var rig = new Rig();
        var c = rig.New(loaded: false);
        rig.Disk(c, 3);
        var gate = new FileGate();
        var flow = Task.Run(async () =>
        {
            await rig.StartAsync(e => e.FileReadGateForTest = gate.WaitAsync);
            await rig.SendAsync(2);
        });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 讀檔途中刪除：送 conv.deleted、記墓碑，進行中的工作作廢
        rig.Ui.Remove(c.Id);
        rig.Ui.Put(new DeletingChange(c.Id));
        await WaitUntilAsync(async () => await rig.H.Inspect(v => v.ActiveReconcile) is null, "刪除之後工作作廢");
        gate.Release.SetResult();
        await flow;
        await rig.SettleAsync();

        rig.Sent(c).Select(e => e.Kind).Should().Equal([ConversationEventKinds.Created, ConversationEventKinds.Deleted], "讀檔的結果在刪除之後才回來：丟掉");
        rig.Model[c.Id]!.Deleted.Should().BeTrue();
        (await rig.RowAsync(c)).DeletedAt.Should().NotBeNull();
    }

    // ───────────────────────────── 伺服器換實例（D22、C9） ─────────────────────────────

    [Fact]
    public async Task 伺服器換實例_清掉狀態整份重送_created先送()
    {
        await using var rig = new Rig();
        var a = rig.New(minute: 2);
        var aItems = rig.Fill(a, 3);
        var b = rig.New(minute: 1);
        var bItems = rig.Fill(b, 2);
        var d = rig.New(minute: 0, loaded: false);
        rig.Disk(d, 3);
        await rig.StartAsync();
        await rig.SettleAsync();
        var dOrds = rig.ServerItems(d).ToDictionary(p => p.Key, p => p.Value);

        rig.NewServerInstance();
        await rig.ReconnectAsync();
        await rig.SettleAsync();

        var accepted = rig.Model.Accepted.Where(e => e.ConversationId is not null).ToList();
        var lastCreated = accepted.FindLastIndex(e => e.Kind == ConversationEventKinds.Created);
        var firstItem = accepted.FindIndex(e => e.Kind == ConversationEventKinds.Item);
        lastCreated.Should().BeLessThan(firstItem, "新的實例：先為所有對話送 created，再送內容");
        OfKind(accepted, ConversationEventKinds.Created).Select(e => e.ConversationId).Should().BeEquivalentTo([a.Id, b.Id, d.Id]);
        rig.ServerItems(a).Should().BeEquivalentTo(OrdsOf(aItems), "摘要清掉了：整份重送");
        rig.ServerItems(b).Should().BeEquivalentTo(OrdsOf(bItems));
        rig.ServerItems(d).Should().BeEquivalentTo(dOrds, "沒載入的也重送，Ord 不變");
    }

    [Fact]
    public async Task 回補途中伺服器換實例()
    {
        await using var rig = new Rig();
        var c = rig.New(loaded: false);
        var items = rig.Disk(c, 1_200);
        rig.H.Server.HoldAcks = true;
        await rig.StartAsync();
        await rig.ReleaseUntilAsync(async () => await rig.ItemRowsAsync(c) > 0, "第一段寫進 outbox");
        (await rig.ItemRowsAsync(c)).Should().BeLessThan(items.Count, "前提：還在回補途中");

        // 換實例：扣住的確認丟掉（連線斷了），新的實例什麼都沒有
        rig.H.Server.Current!.DiscardAcks();
        rig.H.Server.HoldAcks = false;
        rig.NewServerInstance();
        await rig.ReconnectAsync();
        await rig.SettleAsync();

        rig.Model.Accepted.First(e => e.ConversationId == c.Id).Kind.Should().Be(ConversationEventKinds.Created, "新的實例先收到 created");
        rig.ServerItems(c).Should().BeEquivalentTo(rig.Planned(c, items), "途中的工作丟掉、整份重送");
        (await rig.BackfilledAsync()).Keys.Should().Contain(c.Id);
    }

    [Fact]
    public async Task 換實例後_墓碑保留_補送deleted()
    {
        await using var rig = new Rig();
        var t = rig.New();
        rig.Fill(t, 1);
        await rig.StartAsync();
        await rig.SettleAsync();
        rig.Ui.Remove(t.Id);
        rig.Ui.Put(new DeletingChange(t.Id));
        await rig.SettleAsync();
        (await rig.RowAsync(t)).DeletedAt.Should().NotBeNull("前提：墓碑");
        var mark = rig.Mark();

        rig.NewServerInstance();
        await rig.ReconnectAsync();
        await rig.SettleAsync();

        OfKind(rig.Sent(t, mark), ConversationEventKinds.Deleted).Should().NotBeEmpty("新的實例：墓碑補送 conv.deleted");
        rig.Sent(t, mark).Should().OnlyContain(e => e.Kind == ConversationEventKinds.Deleted, "墓碑只送刪除");
        (await rig.RowAsync(t)).DeletedAt.Should().NotBeNull("墓碑保留");
        (await rig.NeedsRedoAsync()).Should().NotContain(t.Id, "確認之後清掉");
    }

    [Fact]
    public async Task 換實例後_重新發布之前刪除或改localOnly_照樣送deleted記墓碑()
    {
        await using var rig = new Rig();
        var gone = rig.New(minute: 2);
        rig.Fill(gone, 1);
        var local = rig.New(minute: 1);
        rig.Fill(local, 1);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 伺服器從備份還原（換了實例、還留著舊的對話）；重整還沒處理到這兩個對話（不在這次的清單裡：回補還沒輪到的模擬）
        rig.H.Server.Instance = new ServerInstance("instance-restored", 0);
        rig.Ui.Remove(gone.Id);
        rig.Ui.Remove(local.Id);
        await rig.ReconnectAsync();
        await rig.SettleAsync();
        (await rig.RowAsync(gone)).CreatedWritten.Should().BeFalse("前提：換實例之後還沒重新發布");
        var mark = rig.Mark();

        rig.Ui.Put(new DeletingChange(gone.Id));
        local.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Ui.Put(new MetaChanged(local.Id, local.Snapshot()));
        await rig.SettleAsync();

        foreach (var c in new[] { gone, local })
        {
            OfKind(rig.Sent(c, mark), ConversationEventKinds.Deleted).Should().NotBeEmpty("還原的伺服器上可能還有它（合併前審查 F3）");
            OfKind(rig.Sent(c, mark), ConversationEventKinds.Created).Should().BeEmpty("不先建立再刪除");
            rig.Model[c.Id]!.Deleted.Should().BeTrue();
            (await rig.RowAsync(c)).DeletedAt.Should().NotBeNull("記墓碑");
        }

        (await rig.RowAsync(local)).Blocked.Should().Be(SyncEngine.BlockedLocalOnly);
    }

    [Fact]
    public async Task 換實例時本機已刪的對話_還原的伺服器上也刪掉()
    {
        await using var rig = new Rig();
        var gone = rig.New(minute: 1);
        rig.Fill(gone, 1);
        var keep = rig.New(minute: 0);
        rig.Fill(keep, 1);
        await rig.StartAsync();
        await rig.SettleAsync();

        // app 沒開時刪掉了（資料夾不在），同時伺服器從備份還原
        await rig.StopAsync();
        rig.Ui.Remove(gone.Id);
        rig.H.Server.Instance = new ServerInstance("instance-restored", 0);
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        OfKind(rig.Sent(gone, mark), ConversationEventKinds.Deleted).Should().ContainSingle("換實例重設了 created_written，曾經建立過的照樣確認資料夾、送刪除（合併前審查 F3）");
        rig.Model[gone.Id]!.Deleted.Should().BeTrue();
        rig.Model[keep.Id]!.Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task 伺服器回滾期間刪除與新建的對話_回到conv1後補送()
    {
        await using var rig = new Rig();
        var gone = rig.New(minute: 1);
        rig.Fill(gone, 2);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 伺服器回滾到 1724718（不宣告 conv1）：這段期間刪除一個、新建一個（帶兩則）
        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();
        rig.Ui.Remove(gone.Id);
        rig.Ui.Put(new DeletingChange(gone.Id));
        var fresh = rig.New(minute: 2);
        rig.Ui.Put(new MetaChanged(fresh.Id, fresh.Snapshot()));
        foreach (var item in rig.Fill(fresh, 2))
            rig.Ui.Put(new ItemChanged(fresh.Id, ChatItemCopyRules.Copy(item)));
        await rig.IdleAsync();
        await rig.SendAsync(3);

        // 回到 WP4.2，接著又多一則
        rig.H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        var third = rig.Fill(fresh, 1).Single();
        rig.Ui.Put(new ItemChanged(fresh.Id, ChatItemCopyRules.Copy(third)));
        await rig.SettleAsync();

        rig.Model[gone.Id]!.Deleted.Should().BeTrue("回滾期間刪掉的：回到 conv1 後補送 conv.deleted（合併前審查 compat F4）");
        (await rig.RowAsync(gone)).DeletedAt.Should().NotBeNull();
        rig.ServerItems(fresh).Should().BeEquivalentTo(OrdsOf(fresh.Items), "回滾期間新建的：重整整份補送");
        (await rig.NeedsRedoAsync()).Should().NotContain([gone.Id, fresh.Id]);
    }

    /// <summary>
    /// 兩則已上伺服器的對話：離線時刪掉第二則（conv.itemRemoved 寫進 outbox、published_items 記 removed），接著連上回滾的伺服器（沒有 conv1）：
    /// 那一列換成 {}、被 invalid 擋下（保留在 outbox）。回傳那個對話與兩則項目
    /// </summary>
    private static async Task<(FakeConversation C, List<AgentChatItem> Items)> RemovalRejectedOnOldServerAsync(Rig rig)
    {
        var c = rig.New(minute: 1);
        var items = rig.Fill(c, 2);
        await rig.StartAsync();
        await rig.SettleAsync();
        rig.ServerItems(c).Keys.Should().BeEquivalentTo(items.Select(i => i.Id), "前提：兩則都在伺服器上");

        rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
        rig.H.Server.Current!.Drop();
        await rig.IdleAsync();
        c.Items.Remove(items[1]);
        rig.Ui.Put(new ItemRemovedChange(c.Id, items[1].Id));
        await rig.IdleAsync();
        (await rig.PublishedAsync(c)).Single(p => p.ItemId == items[1].Id).Removed.Should().BeTrue("前提：刪除寫進 outbox、記成已送");

        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();
        await rig.SendAsync(3);
        (await rig.H.Inspect(v => v.Outbox)).Should().Contain(e => e.ConversationId == c.Id && e.Kind == ConversationEventKinds.ItemRemoved && e.State == OutboxState.Rejected,
            "前提：換成 {} 的刪除被擋下");
        return (c, items);
    }

    [Fact]
    public async Task 回滾期間被擋下的itemRemoved_回到conv1後重送()
    {
        await using var rig = new Rig();
        var (c, items) = await RemovalRejectedOnOldServerAsync(rig);

        rig.H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        await rig.SettleAsync();

        rig.ServerItems(c).Keys.Should().BeEquivalentTo([items[0].Id], "被擋下的刪除連「已送」一起清掉，重整時重送（合併前審查 F4）");
        (await rig.PublishedAsync(c)).Single(p => p.ItemId == items[1].Id).Removed.Should().BeTrue("重送之後再記成已送");
    }

    [Fact]
    public async Task 保留中被擋下的itemRemoved_開啟時清掉已送_重整時重送()
    {
        await using var rig = new Rig();
        var (c, items) = await RemovalRejectedOnOldServerAsync(rig);

        // 「已送刪除」被寫回（例如中間跑過的版本）：開啟時掃保留中的被拒列（D36），連這個一起清
        await rig.StopAsync();
        rig.Sql("UPDATE published_items SET removed = 1 WHERE conversation_id = $c AND item_id = $i", ("$c", c.Id), ("$i", items[1].Id));
        rig.H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
        rig.Model.OldServer = false;
        rig.H.Server.ConnectFailure = null;
        await rig.StartAsync();
        await rig.SettleAsync();

        rig.ServerItems(c).Keys.Should().BeEquivalentTo([items[0].Id], "開啟時的掃描把被擋下的刪除也排回重整（合併前審查 F4）");
    }

    [Fact]
    public async Task deleted回invalid或deleted_都算完成()
    {
        await using var rig = new Rig();
        var unknown = rig.New(minute: 1);
        rig.Fill(unknown, 1);
        var gone = rig.New(minute: 0);
        rig.Fill(gone, 1);
        await rig.StartAsync();
        await rig.SettleAsync();
        foreach (var c in new[] { unknown, gone })
        {
            rig.Ui.Remove(c.Id);
            rig.Ui.Put(new DeletingChange(c.Id));
        }

        await rig.SettleAsync();

        // 新的實例：一個不認得（invalid），一個從備份還原時已是刪除（deleted）
        rig.NewServerInstance();
        rig.Model.Seed(gone.Id, rig.H.DeviceId, deleted: true);
        var mark = rig.Mark();
        await rig.ReconnectAsync();
        await rig.SettleAsync();
        rig.Model.Rejected.Select(r => (r.ConversationId, r.Code)).Should().BeEquivalentTo(new[]
        {
            (unknown.Id, SyncErrorCodes.Invalid),
            (gone.Id, ConversationRejectCodes.Deleted),
        }, "前提：兩種回應各一");
        (await rig.NeedsRedoAsync()).Should().NotContain(unknown.Id, "invalid 算完成").And.NotContain(gone.Id, "deleted 也算完成");
        foreach (var c in new[] { unknown, gone })
            (await rig.RowAsync(c)).Blocked.Should().BeNull("不是封鎖");

        // 再連一次不再補送
        var again = rig.Mark();
        await rig.ReconnectAsync();
        await rig.SettleAsync();
        rig.Sent(unknown, again).Should().BeEmpty();
        rig.Sent(gone, again).Should().BeEmpty();
        OfKind(rig.Sent(null, mark), ConversationEventKinds.Deleted).Should().HaveCount(2);
    }

    [Fact]
    public async Task 有旁檔沒有墓碑_補送deleted()
    {
        await using var rig = new Rig();
        var c = rig.New();
        rig.Fill(c, 2);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 中樞寫好伺服器刪除的旁檔之後當機：引擎還沒送 conv.deleted
        await rig.StopAsync();
        c.ServerDeleted = true;
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        rig.Sent(c, mark).Select(e => e.Kind).Should().Equal([ConversationEventKinds.Deleted], "照 Transition＝Tombstone 只補送刪除");
        var row = await rig.RowAsync(c);
        row.DeletedAt.Should().NotBeNull();
        row.Blocked.Should().Be(SyncEngine.BlockedLocalOnly);
        rig.Model[c.Id]!.Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task 換實例後有旁檔的對話_只送deleted不送內容()
    {
        await using var rig = new Rig();
        var c = rig.New();
        rig.Fill(c, 2);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 旁檔寫好了（app 沒開時），伺服器也換了實例：發布狀態整份重設，沒有墓碑
        await rig.StopAsync();
        c.ServerDeleted = true;
        rig.NewServerInstance();
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        rig.Sent(c, mark).Select(e => e.Kind).Should().Equal([ConversationEventKinds.Deleted], "有旁檔：不送 created、不送內容，只補送刪除");
        var row = await rig.RowAsync(c);
        row.DeletedAt.Should().NotBeNull();
        row.Blocked.Should().Be(SyncEngine.BlockedLocalOnly);
        rig.Calls().Should().NotContain($"MarkServerDeleted:{c.Id}", "旁檔已經在了");
    }

    [Fact]
    public async Task 讀檔時發現旁檔_只補送deleted()
    {
        await using var rig = new Rig();
        var c = rig.New(loaded: false);
        rig.Disk(c, 3);
        var gate = new FileGate();
        var flow = Task.Run(async () =>
        {
            await rig.StartAsync(e => e.FileReadGateForTest = gate.WaitAsync);
            await rig.SendAsync(2);
        });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        new ConversationStore(rig.Root).TryWriteServerDeleted(c.Id, 1).Should().BeTrue("前提：旁檔寫好");
        gate.Release.SetResult();
        await flow;
        await rig.SettleAsync();

        rig.Sent(c).Select(e => e.Kind).Should().Equal([ConversationEventKinds.Created, ConversationEventKinds.Deleted], "讀到旁檔：只補送刪除、不送內容");
        (await rig.RowAsync(c)).Blocked.Should().Be(SyncEngine.BlockedLocalOnly);
    }

    // ───────────────────────────── 插隊與 restore 後備（D23） ─────────────────────────────

    [Fact]
    public async Task SnapshotRequest_open移到最前面_不清摘要_10分鐘一次()
    {
        await using var rig = new Rig();
        var t = rig.New(minute: 0);
        rig.Fill(t, 3);
        var b1 = rig.New(minute: 20);
        rig.Fill(b1, 2);
        var b2 = rig.New(minute: 10);
        rig.Fill(b2, 2);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 重開時磁碟保護暫停：三個已知的對話都在佇列裡（t 最舊、排最後）
        await rig.StopAsync();
        rig.Retained = (10, ReconcileQueueRules.RetainedMaxBytes + 1);
        var mark = rig.Mark();
        var calls = rig.CallMark();
        await rig.StartAsync();
        await rig.SendAsync();
        (await rig.H.Inspect(v => v.ReconcileQueue)).Select(q => q.WireId).Should().Equal([b1.Id, b2.Id, t.Id], "前提：由新到舊");

        rig.Snapshot(t, SnapshotReasons.Open);
        await rig.IdleAsync();
        var queue = await rig.H.Inspect(v => v.ReconcileQueue);
        queue[0].Should().Match<ReconcileCandidate>(q => q.WireId == t.Id && q.Reason == ReconcileReason.Requested, "有人在網頁打開：排到最前面");

        await rig.SetDiskAsync((10, 0), long.MaxValue);
        await rig.SettleAsync();
        rig.Calls(calls).Where(call => call.StartsWith("RequestItems:", StringComparison.Ordinal)).First().Should().Be(Rig.ItemsCall(t), "插隊的先走");
        OfKind(rig.Sent(t, mark), ConversationEventKinds.Item).Should().BeEmpty("不清摘要：相同的不重送");
        (await rig.ProjectedAsync()).Should().Be(0);

        // 10 分鐘內同一個對話再要：不處理；過了 10 分鐘才再處理
        var before = rig.Calls().Count(call => call == Rig.ItemsCall(t));
        rig.Snapshot(t, SnapshotReasons.Open);
        await rig.SettleAsync();
        rig.Calls().Count(call => call == Rig.ItemsCall(t)).Should().Be(before, "10 分鐘一次");
        await rig.H.AdvanceAsync(TimeSpan.FromMilliseconds(SyncEngine.OpenRequestIntervalMs));
        rig.Snapshot(t, SnapshotReasons.Open);
        await rig.SettleAsync();
        rig.Calls().Count(call => call == Rig.ItemsCall(t)).Should().Be(before + 1);
    }

    [Fact]
    public async Task SnapshotRequest要localOnly的對話_不送()
    {
        await using var rig = new Rig();
        var full = rig.New(minute: 0);
        rig.Fill(full, 1);
        var local = rig.New(ConversationSyncPolicies.LocalOnly, minute: 1);
        rig.Fill(local, 2);
        var metadata = rig.New(ConversationSyncPolicies.Metadata, minute: 2);
        rig.Fill(metadata, 2);
        await rig.StartAsync();
        await rig.SettleAsync();
        var mark = rig.Mark();
        var calls = rig.CallMark();

        foreach (var c in new[] { local, metadata })
        {
            rig.Snapshot(c, SnapshotReasons.Open);
            rig.Snapshot(c, SnapshotReasons.Restore);
        }

        await rig.SettleAsync();
        rig.Calls(calls).Should().NotContain(call => call.StartsWith("RequestItems:", StringComparison.Ordinal), "本機的政策不允許：不送");
        rig.Sent(local).Should().BeEmpty("localOnly 的什麼都不送");
        rig.Sent(metadata, mark).Should().BeEmpty("metadata 的不送內容、也不補 created");
    }

    [Fact]
    public async Task restore後備_還沒重發的插到最前面()
    {
        await using var rig = new Rig();
        var a = rig.New(minute: 20);
        var aItems = rig.Fill(a, 2);
        var b = rig.New(minute: 10);
        var bItems = rig.Fill(b, 2);
        var x = rig.New(minute: 0);
        var xItems = rig.Fill(x, 2);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 伺服器從備份還原（換實例）：連線時（Prove 之前）就送來 x 的 restore
        rig.NewServerInstance();
        rig.H.Server.BeforeProveReturns = session => session.Snapshot(x.Id, SnapshotReasons.Restore);
        var calls = rig.CallMark();
        await rig.ReconnectAsync();
        rig.H.Server.BeforeProveReturns = null;
        await rig.SettleAsync();

        rig.Calls(calls).Where(call => call.StartsWith("RequestItems:", StringComparison.Ordinal)).Should().Equal(
            [Rig.ItemsCall(x), Rig.ItemsCall(a), Rig.ItemsCall(b)], "還沒重發的 restore 排在一般回補前面（x 最舊）");
        rig.ServerItems(x).Should().BeEquivalentTo(OrdsOf(xItems));
        rig.ServerItems(a).Should().BeEquivalentTo(OrdsOf(aItems));
        rig.ServerItems(b).Should().BeEquivalentTo(OrdsOf(bItems));
    }

    [Fact]
    public async Task restore後備_已重發的只補created()
    {
        await using var rig = new Rig();
        var x = rig.New();
        rig.Fill(x, 2);
        await rig.StartAsync();
        await rig.SettleAsync();
        var mark = rig.Mark();
        var calls = rig.CallMark();

        rig.Snapshot(x, SnapshotReasons.Restore);
        await rig.SettleAsync();

        rig.Sent(x, mark).Select(e => e.Kind).Should().Equal([ConversationEventKinds.Created], "已重發的只補一筆新的 conv.created（解除伺服器的 recovering）");
        rig.Calls(calls).Should().NotContain(Rig.ItemsCall(x), "不整份重送");
    }

    [Fact]
    public async Task 同一世代同一對話只處理一次()
    {
        await using var rig = new Rig();
        var x = rig.New();
        rig.Fill(x, 2);
        await rig.StartAsync();
        await rig.SettleAsync();
        var mark = rig.Mark();

        rig.Snapshot(x, SnapshotReasons.Restore);
        await rig.SettleAsync();
        rig.Snapshot(x, SnapshotReasons.Restore);
        await rig.SettleAsync();
        OfKind(rig.Sent(x, mark), ConversationEventKinds.Created).Should().HaveCount(1, "同一個（對話, 世代）只處理一次");

        // 下一個世代（伺服器又換了實例）：重發之後再要一次，照樣處理
        rig.NewServerInstance("instance-3");
        await rig.ReconnectAsync();
        await rig.SettleAsync();
        var next = rig.Mark();
        rig.Snapshot(x, SnapshotReasons.Restore);
        await rig.SettleAsync();
        OfKind(rig.Sent(x, next), ConversationEventKinds.Created).Should().HaveCount(1);
    }

    // ───────────────────────────── DLP 換版、舊版偵測（D36、D38） ─────────────────────────────

    [Fact]
    public async Task DLP版本改變_重新投影_只送結果改變的()
    {
        await using var rig = new Rig();
        var c = rig.New();
        var items = rig.Fill(c, 3);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 新版的 DLP 對第一則的遮蔽結果不同（以改掉記錄的線上摘要模擬）
        await rig.StopAsync();
        rig.Sql("UPDATE published_items SET wire_sha256 = 'changed' WHERE conversation_id = $c AND item_id = $i", ("$c", c.Id), ("$i", items[0].Id));
        rig.H.DlpVersion = CredentialLeakRules.Version + 1;
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        (await rig.ProjectedAsync()).Should().Be(3, "來源摘要清掉了：每一則都重新投影");
        OfKind(rig.Sent(c, mark), ConversationEventKinds.Item).Select(IdOf).Should().Equal([items[0].Id], "只送線上摘要改變的");
        (await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.PublisherDlpVersion))).Should().Be((CredentialLeakRules.Version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task 中間跑過舊版_全部記needs_redo()
    {
        await using var rig = new Rig();
        var mem = rig.New(minute: 1);
        rig.Fill(mem, 2);
        var disk = rig.New(minute: 0, loaded: false);
        rig.Disk(disk, 2);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 舊版寫過 outbox（next_device_seq 前進），但不知道要更新發布者記的值
        await rig.StopAsync();
        rig.Sql("UPDATE kv SET value = '1' WHERE key = $k", ("$k", SyncStoreSchema.PublisherNextDeviceSeq));
        rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
        await rig.StartAsync();

        (await rig.NeedsRedoAsync()).Should().BeEquivalentTo([mem.Id, disk.Id], "開啟時就記 needs_redo");
        (await rig.RowAsync(disk)).ItemsFileSha256.Should().BeNull("摘要清掉（含 items.json 的）");
        (await rig.PublishedAsync(mem)).Should().OnlyContain(p => p.SrcSha256 == null && p.WireSha256 == null && p.Ord >= 1, "摘要清掉、Ord 保留");

        var mark = rig.Mark();
        await rig.ReconnectAsync();
        await rig.SettleAsync();
        OfKind(rig.Sent(mem, mark), ConversationEventKinds.Item).Should().HaveCount(2, "整份重送");
        OfKind(rig.Sent(disk, mark), ConversationEventKinds.Item).Should().HaveCount(2);
        (await rig.NeedsRedoAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 中間跑過舊版_處理過換實例_整份重送()
    {
        await using var rig = new Rig();
        var c = rig.New();
        rig.Fill(c, 2);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 中間跑過的舊版處理了換實例（發布狀態記的伺服器和目前的不同）
        await rig.StopAsync();
        rig.Sql("UPDATE kv SET value = 'instance-0/0' WHERE key = $k", ("$k", SyncStoreSchema.PublishServer));
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        rig.Sent(c, mark).Select(e => e.Kind).Should().StartWith(ConversationEventKinds.Created, "照換實例重設：先送 created");
        OfKind(rig.Sent(c, mark), ConversationEventKinds.Item).Should().HaveCount(2, "再整份重送");
        (await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.PublishServer))).Should().Be(SyncStore.PublishServerKeyOf(rig.H.Server.Instance));
    }

    [Fact]
    public async Task 保留中的被拒列_重新排入()
    {
        await using var rig = new Rig();
        var c = rig.New();
        var items = rig.Fill(c, 2);
        await rig.StartAsync();
        await rig.SettleAsync();

        // 第二版被伺服器以 dlp 擋下：列保留、摘要清掉
        rig.Model.Reject = e => e.ConversationId == c.Id && e.Kind == ConversationEventKinds.Item ? ConversationRejectCodes.Dlp : null;
        items[0].Text = "第二版。";
        rig.Ui.Put(new ItemChanged(c.Id, ChatItemCopyRules.Copy(items[0])));
        await rig.IdleAsync();
        var recorded = (await rig.PublishedAsync(c)).Single(p => p.ItemId == items[0].Id);
        recorded.SrcSha256.Should().NotBeNull("前提：寫進 outbox 時記了第二版的摘要");
        await rig.SettleAsync();
        (await rig.H.Inspect(v => v.Outbox)).Should().Contain(e => e.ConversationId == c.Id && e.State == OutboxState.Rejected, "前提：被拒的列保留");

        // 摘要被寫回（例如中間跑過的版本）：開啟時掃保留中的被拒列，重新排入
        await rig.StopAsync();
        rig.Sql("UPDATE published_items SET src_sha256 = $s, wire_sha256 = $w WHERE conversation_id = $c AND item_id = $i",
            ("$s", recorded.SrcSha256!), ("$w", recorded.WireSha256!), ("$c", c.Id), ("$i", items[0].Id));
        rig.Model.Reject = null;
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        var resent = OfKind(rig.Model.Accepted.Where(e => e.ConversationId == c.Id), ConversationEventKinds.Item).Where(e => IdOf(e) == items[0].Id).ToList();
        resent.Select(e => JObject.Parse(Json(e))["text"]!.Value<string>()).Should().Contain("第二版。", "被拒的那一則重新送出");
        OfKind(rig.Sent(c, mark), ConversationEventKinds.Item).Select(IdOf).Should().Equal([items[0].Id], "只送被拒的");
    }

    // ───────────────────────────── 回補與即時、進度 ─────────────────────────────

    [Fact]
    public async Task 回補期間_另一個對話的即時更新照常()
    {
        await using var rig = new Rig();
        var b = rig.New(minute: 0);
        var bItems = rig.Fill(b, 700);
        rig.H.Server.HoldAcks = true;
        await rig.StartAsync();
        await rig.ReleaseUntilAsync(async () => await rig.ItemRowsAsync(b) > 0, "第一頁寫進 outbox");

        // 回補還沒做完：另一個對話的新內容照常寫進 outbox（不經佇列）
        var live = rig.New(minute: 30);
        rig.Ui.Put(new MetaChanged(live.Id, live.Snapshot()));
        var added = rig.Fill(live, 1, "即時");
        rig.Ui.Put(new ItemChanged(live.Id, ChatItemCopyRules.Copy(added[0])));
        await rig.IdleAsync();

        (await rig.RowsAsync(live)).Select(r => r.Kind).Should().Equal([ConversationEventKinds.Created, ConversationEventKinds.Item]);
        (await rig.ItemRowsAsync(b)).Should().Be(PublishTimingRules.ItemsPage, "回補的下一頁還在等");

        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        await rig.SettleAsync();
        rig.ServerItems(live).Should().BeEquivalentTo(OrdsOf(added));
        rig.ServerItems(b).Should().BeEquivalentTo(OrdsOf(bItems));
    }

    [Fact]
    public async Task 進度遞減_完成是null()
    {
        await using var rig = new Rig();
        var conversations = new[] { (minute: 0, count: 3), (minute: 1, count: 30), (minute: 2, count: 300) }
            .Select(p =>
            {
                var c = rig.New(minute: p.minute, loaded: false);
                rig.Disk(c, p.count);
                return c;
            }).ToList();
        var total = conversations.Sum(c => new FileInfo(Path.Combine(rig.DirOf(c), "items.json")).Length);

        await rig.StartAsync();
        await rig.SettleAsync();

        var reports = rig.Ui.Progress.ToList();
        reports.Last().Progress.Should().BeNull("都完成了");
        var counts = reports.Where(r => r.Progress != null).Select(r => r.Progress!.ConversationsLeft).ToList();
        counts.Should().NotBeEmpty();
        counts.Max().Should().Be(3);
        counts.Should().BeInDescendingOrder("剩下的對話數只會減少");
        reports.Where(r => r.Progress != null).Max(r => r.Progress!.BytesLeft).Should().Be(total, "位元組以 items.json 的大小估計");
        reports.Should().OnlyContain(r => r.Pause == null);
    }

    [Fact]
    public async Task 舊版剝除後_新Ord沿用hint_伺服器多的項目被移除()
    {
        await using var rig = new Rig();
        var c = rig.New(loaded: false);
        // WP4.3 的中樞存過的檔：項目有 Ord、meta 有 NextOrd
        var items = Enumerable.Range(0, 3).Select(i => NewItem($"第 {i} 則。")).ToList();
        for (var i = 0; i < items.Count; i++)
            items[i].Ord = i + 1;
        c.Meta.NextOrd = 4;
        rig.WriteFiles(c, items);
        await rig.StartAsync();
        await rig.SettleAsync();
        rig.ServerItems(c).Should().BeEquivalentTo(OrdsOf(items), "前提");

        // 舊版剝掉 Ord 與 NextOrd、刪了第二則、加了一則
        await rig.StopAsync();
        var added = NewItem("舊版加的。");
        var stripped = new[] { items[0], items[2], added }.Select(i =>
        {
            var copy = ChatItemCopyRules.Copy(i);
            copy.Ord = null;
            return copy;
        }).ToList();
        c.Meta.NextOrd = null;
        rig.WriteFiles(c, stripped);
        var mark = rig.Mark();
        await rig.StartAsync();
        await rig.SettleAsync();

        var server = rig.ServerItems(c);
        server.Keys.Should().BeEquivalentTo([items[0].Id, items[2].Id, added.Id]);
        server[items[0].Id].Should().Be(1, "沿用發布紀錄的 Ord");
        server[items[2].Id].Should().Be(3);
        server[added.Id].Should().BeGreaterThan(3, "新的接在發布過的最大值之後");
        OfKind(rig.Sent(c, mark), ConversationEventKinds.ItemRemoved).Select(IdOf).Should().Equal([items[1].Id], "伺服器多的項目被移除");
        OfKind(rig.Sent(c, mark), ConversationEventKinds.Item).Select(IdOf).Should().Equal([added.Id], "沿用的摘要相同不重送");

        // 之後中樞載入：同樣的規劃（發布紀錄當 hint）得到相同的 Ord
        var hint = await rig.H.Inspect(v => v.Store!.OrdHintOf(c.Id));
        rig.Planned(c, stripped, hint).Should().BeEquivalentTo(server);
    }

    // ───────────────────────────── S19 桌面端（縮小版） ─────────────────────────────

    [Fact]
    public async Task S19桌面端_30個對話其中20個超過90天_送完後id與Ord集合等於本機()
    {
        var seeds = new List<(AgentConversation Meta, List<AgentChatItem> Items)>();
        for (var i = 0; i < 30; i++)
        {
            var updated = i < 20 ? T0.AddDays(-91 - i * 7) : T0.AddDays(-(i - 20));
            var count = i == 5 ? 1_100 : 1 + i % 9 * 3;
            seeds.Add(ReconcileChainRig.Seed(updated, count, $"對話 {i}"));
        }

        await using var rig = new ReconcileChainRig(seeds);
        await rig.StartAsync();
        await rig.DrainAsync();

        foreach (var (meta, items) in seeds)
        {
            var local = await rig.LoadAndPrepareAsync(meta.Id);
            local.Keys.Should().BeEquivalentTo(items.Select(i => i.Id));
            rig.Model.Items(meta.Id).Should().BeEquivalentTo(local, $"對話 {meta.Title}：伺服器的 id 與 Ord 集合等於本機（超過 90 天的也送，答 1 不限天數）");
        }

        (await rig.E.Inspect(v => v.Store!.LoadBackfilled())).Keys.Should().BeEquivalentTo(seeds.Select(s => s.Meta.Id));
    }
}
