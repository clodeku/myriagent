using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎的持久發布（WP4.3 計畫步 10）：DLP 版本閘門與 conv1 宣告、投影（秘密回答清除、截斷、遮蔽、等待中卡片的摘要）、合併鍵與 ver、
/// 摘要與 outbox 同一個交易、沒有 conv1 時不外洩（換成 {}）、拒絕碼的處置與彙總提示、補送 created、政策轉換與伺服器刪除、心跳。
/// UI 端是 <see cref="FakeConversationUi"/>（代替發布者＋中樞），伺服器是照 WP4.2 定案模擬的 <see cref="FakeConversations"/>
/// </summary>
public sealed class ConversationPublishEngineTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Local);

    private static readonly string[] BothFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];

    /// <summary>引擎＋盒子＋假 UI＋假伺服器（預設宣告 conv1、live1 的 WP4.2 伺服器）</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public Rig(bool box = true, int? dlpVersion = null)
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: box ? Box : null, dlpVersion: dlpVersion);
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            H.Server.Features = BothFeatures;
            _ = H.Server.Conversations;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        public FakeConversations Model => H.Server.Conversations;

        public Task StartAsync() => H.StartAsync();

        public Task IdleAsync() => H.IdleAsync();

        /// <summary>撥快讓送出計時器跑完（批次與確認）</summary>
        public Task SendAsync(double seconds = 1) => H.AdvanceAsync(seconds);

        public FakeConversation New(AgentProvider provider = AgentProvider.Claude, string? policy = null, string title = "測試對話", int minute = 0)
            => Ui.Add(new FakeConversation(new AgentConversation
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                AccountId = EngineHarness.LocalAccount,
                Provider = provider,
                SyncPolicy = policy,
                CreatedAt = T0,
                UpdatedAt = T0.AddMinutes(minute),
            }));

        public void Meta(FakeConversation c) => Ui.Put(new MetaChanged(c.Id, c.Snapshot()));

        public AgentChatItem Item(FakeConversation c, string text, AgentItemKind kind = AgentItemKind.Assistant, AgentItemStatus status = AgentItemStatus.Done, bool ord = true)
            => Item(c, new AgentChatItem { Kind = kind, Status = status, Text = text, CreatedAt = T0, UpdatedAt = T0 }, ord);

        public AgentChatItem Item(FakeConversation c, AgentChatItem item, bool ord = true)
        {
            if (ord && item.Ord is null)
                item.Ord = c.NextOrd++;
            c.Items.Add(item);
            Changed(c, item);
            return item;
        }

        public void Changed(FakeConversation c, AgentChatItem item) => Ui.Put(new ItemChanged(c.Id, ChatItemCopyRules.Copy(item)));

        public async Task<IReadOnlyList<OutboxEntry>> RowsAsync(FakeConversation? c = null)
            => (await H.Inspect(v => v.Outbox)).Where(e => e.ConversationId is not null && (c is null || e.ConversationId == c.Id)).ToList();

        public IReadOnlyList<SyncEvent> Sent(FakeConversation? c = null)
            => H.Server.Batches.SelectMany(b => b.Events).Where(e => e.ConversationId is not null && (c is null || e.ConversationId == c.Id)).ToList();

        public Task<IReadOnlyDictionary<string, PublishMetaRow>> PublishMetaAsync() => H.Inspect(v => v.Store!.LoadPublishMeta());

        public async Task<PublishMetaRow> RowAsync(FakeConversation c) => (await PublishMetaAsync())[c.Id];

        public Task<IReadOnlyList<PublishedItemRow>> PublishedAsync(FakeConversation c) => H.Inspect(v => v.Store!.LoadPublishedItems(c.Id));

        public Task<IReadOnlySet<string>> NeedsRedoAsync() => H.Inspect(v => v.Store!.LoadNeedsRedo());

        /// <summary>斷線並保持離線（之後 <see cref="ReconnectAsync"/>）</summary>
        public async Task GoOfflineAsync()
        {
            H.Server.ConnectFailure = _ => EngineHarness.Transient();
            H.Server.Current!.Drop();
            await IdleAsync();
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

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static string Json(OutboxEntry e) => PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開");

    private static string Json(SyncEvent e) => PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開");

    private static JObject Obj(OutboxEntry e) => JObject.Parse(Json(e));

    /// <summary>執行時組出像真憑證的字串（固定種子；repo 裡沒有這種字串）</summary>
    private static string RuntimeToken(int seed, int length = 40)
    {
        var random = new Random(seed);
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return "sk-" + "ant-" + "api03-" + new string(Enumerable.Range(0, length).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    private static string RuntimeBasic(int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[24];
        random.NextBytes(bytes);
        return "Authorization: " + "Basic " + Convert.ToBase64String(bytes);
    }

    // ───────────────────────────── 宣告（D3、D38） ─────────────────────────────

    [Fact]
    public async Task 沒有盒子_Hello不宣告()
    {
        await using var rig = new Rig(box: false);
        await rig.StartAsync();

        rig.H.Server.Hellos.Single().Features.Should().BeNull("沒有盒子的引擎照 WP4.1 不宣告任何功能");
        (await rig.H.Inspect(v => v.Features)).Should().BeEmpty();
        (await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.ConvArmed))).Should().BeNull("沒有盒子不碰對話的 kv");
    }

    [Fact]
    public async Task 有盒子_Hello宣告conv1_live1與prov1()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        // WP4.7 起有盒子的引擎也宣告 media1、即時增量起也宣告 live2（這個伺服器沒有，協商出的照舊）
        rig.H.Server.Hellos.Single().Features.Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Prov1, DeviceFeatureRules.Media1);
        (await rig.H.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1);
        (await rig.H.StateAsync()).Should().Be(SyncEngineState.Live);
        (await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.ConvArmed))).Should().Be("1", "進 Live 的連線有 conv1");
    }

    [Fact]
    public async Task v3表不在_不宣告()
    {
        await using var rig = new Rig();
        using (var connection = new SqliteConnection(SyncStore.ConnectionString(rig.H.Paths.DatabaseFile, SyncStoreOpenMode.Open)))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP INDEX ix_replica_items_ord; DROP TABLE published_items;";
            cmd.ExecuteNonQuery();
        }

        rig.H.OpenStore = (path, id) => SyncStore.Open(path, id, point =>
        {
            if (point == "UpgradeSchema:v3")
                throw new SqliteException("模擬：資料庫被鎖", 5);
        });
        await rig.StartAsync();

        (await rig.H.Inspect(v => v.Store!.ConversationTablesReady)).Should().BeFalse();
        rig.H.Server.Hellos.Single().Features.Should().BeNull();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "表不在時的內容");
        await rig.IdleAsync();
        (await rig.RowsAsync()).Should().BeEmpty();
        rig.Box.Count.Should().Be(0, "盒子照樣取空（否則之後再也不會喚醒引擎）");
    }

    [Fact]
    public async Task 開啟時升級失敗_上線時重試成功_記下發布者的狀態()
    {
        await using var rig = new Rig();
        using (var connection = new SqliteConnection(SyncStore.ConnectionString(rig.H.Paths.DatabaseFile, SyncStoreOpenMode.Open)))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP INDEX ix_replica_items_ord; DROP TABLE published_items;";
            cmd.ExecuteNonQuery();
        }

        // 只有開啟時那一次失敗（例如磁碟暫時滿了）：上線時 RetryUpgrade 成功
        var failures = 0;
        rig.H.OpenStore = (path, id) => SyncStore.Open(path, id, point =>
        {
            if (point == "UpgradeSchema:v3" && Interlocked.Increment(ref failures) == 1)
                throw new SqliteException("模擬：資料庫被鎖", 5);
        });
        await rig.StartAsync();

        (await rig.H.Inspect(v => v.Store!.ConversationTablesReady)).Should().BeTrue("前提：上線時重試成功");
        var (server, key, dlp) = await rig.H.Inspect(v => (v.Store!.GetKv(SyncStoreSchema.PublishServer), v.Store.PublishServerKey(),
            v.Store.GetKv(SyncStoreSchema.PublisherDlpVersion)));
        server.Should().Be(key, "下次開啟時才偵測得到中間跑過的舊版處理了換實例（合併前審查 F6）");
        dlp.Should().NotBeNull("下次開啟時才偵測得到權杖 DLP 換版");
    }

    [Fact]
    public async Task DLP版本低於必要版本_不宣告conv1()
    {
        await using var rig = new Rig(dlpVersion: ConversationPublisher.RequiredDlpVersion - 1);
        await rig.StartAsync();

        var hello = rig.H.Server.Hellos.Single();
        (hello.Features ?? []).Should().NotContain(DeviceFeatureRules.Conv1).And.NotContain(DeviceFeatureRules.Live1);
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "版本不夠時的內容");
        await rig.IdleAsync();
        await rig.SendAsync();
        (await rig.RowsAsync()).Should().BeEmpty("沒有 conv1 不寫對話事件");
        rig.Sent().Should().BeEmpty();
        (await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.ConvArmed))).Should().BeNull("沒連過有 conv1 的連線：維持預設的 false");
    }

    // ───────────────────────────── 事件、合併鍵與 ver（D5～D7） ─────────────────────────────

    [Fact]
    public async Task 新對話_先created_created沒有合併鍵()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Item(c, "你好");
        // 只有項目、沒有中繼資料的對話：先要 meta，到了才一起處理
        var d = rig.New();
        var onlyItem = rig.Item(d, "只有項目");
        await rig.IdleAsync();

        var rows = await rig.RowsAsync(c);
        rows.Select(r => r.Kind).Should().Equal(ConversationEventKinds.Created, ConversationEventKinds.Item);
        rows[0].CoalesceKey.Should().BeNull("created 不能被之後的 meta 換掉");
        rows[1].CoalesceKey.Should().Be(PublishKeyRules.Item(c.Id, item.Id));
        rows.Should().OnlyContain(r => r.Epoch == 1 && r.Stream == SyncStreams.Conversation(c.Id));
        rig.Ui.Calls.Should().Contain($"RequestMeta:{d.Id}");
        var dRows = await rig.RowsAsync(d);
        dRows.Select(r => r.Kind).Should().Equal(ConversationEventKinds.Created, ConversationEventKinds.Item);
        Obj(dRows[1])["id"]!.Value<string>().Should().Be(onlyItem.Id);

        await rig.SendAsync();
        rig.Model[c.Id].Should().NotBeNull();
        (await rig.RowAsync(c)).Should().Match<PublishMetaRow>(r => r.CreatedWritten && r.CreatedAcked && r.Policy == ConversationSyncPolicies.Full);
    }

    [Fact]
    public async Task 同一項目待送時原地取代_ver遞增()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Item(c, "第一版");
        await rig.IdleAsync();
        var first = (await rig.RowsAsync(c)).Single(r => r.Kind == ConversationEventKinds.Item);
        Obj(first)["ver"]!.Value<long>().Should().Be(1, "ver 每次啟動從 1 起");

        item.Text = "第二版";
        rig.Changed(c, item);
        await rig.IdleAsync();

        var second = (await rig.RowsAsync(c)).Single(r => r.Kind == ConversationEventKinds.Item);
        second.DeviceSeq.Should().Be(first.DeviceSeq, "待送的同一項目原地取代");
        Obj(second)["text"]!.Value<string>().Should().Be("第二版");
        Obj(second)["ver"]!.Value<long>().Should().Be(2);
    }

    [Fact]
    public async Task meta在一輪標記前後不合併_running的順序正確()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.IdleAsync();

        c.Running = true;
        rig.Ui.Put(new TurnStartedChange(c.Id, false));
        rig.Meta(c);
        rig.Item(c, "回覆");
        c.Running = false;
        rig.Ui.Put(new TurnCompletedChange(c.Id, AgentTurnOutcome.Completed, new AgentUsage(10, 20, 5, 0.01)));
        rig.Meta(c);
        await rig.IdleAsync();
        c.Meta.Title = "改過的標題";
        rig.Meta(c);
        await rig.IdleAsync();

        var rows = await rig.RowsAsync(c);
        rows.Select(r => r.Kind).Should().Equal(
            ConversationEventKinds.Created, ConversationEventKinds.Turn, ConversationEventKinds.Meta, ConversationEventKinds.Item,
            ConversationEventKinds.Turn, ConversationEventKinds.Meta);
        var metas = rows.Where(r => r.Kind == ConversationEventKinds.Meta).ToList();
        metas.Select(m => Obj(m)["running"]!.Value<bool>()).Should().Equal(true, false);
        metas[0].CoalesceKey.Should().NotBe(metas[1].CoalesceKey, "一輪的標記之後換新世代");
        Obj(metas[1])["title"]!.Value<string>().Should().Be("改過的標題", "同一個世代裡的 meta 原地取代");
        Obj(rows[1])["phase"]!.Value<string>().Should().Be(ConversationTurnPhases.Started);
        Obj(rows[4])["usage"]!["inputTokens"]!.Value<long>().Should().Be(10);
    }

    // ───────────────────────────── 政策（D3、D25） ─────────────────────────────

    [Fact]
    public async Task 不發布的對話_一筆都沒有()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var local = rig.New(policy: ConversationSyncPolicies.LocalOnly);
        var agy = rig.New(AgentProvider.Antigravity);
        var draft = rig.New();
        draft.IsDraft = true;
        var replica = rig.New();
        replica.IsReplica = true;
        foreach (var c in new[] { local, agy, draft, replica })
        {
            c.Running = true;
            rig.Meta(c);
            rig.Item(c, "不該上線的內容");
            rig.Ui.Put(new TurnStartedChange(c.Id, false));
        }

        await rig.IdleAsync();
        await rig.H.AdvanceAsync(16);

        (await rig.RowsAsync()).Should().BeEmpty();
        rig.Sent().Should().BeEmpty();
        var beat = rig.H.Server.Heartbeats.Last();
        beat.Running.Should().BeEmpty("心跳也不帶不發布的對話的 id");
        beat.Waiting.Should().BeEmpty();
    }

    [Fact]
    public async Task metadata與paused只發meta類()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        foreach (var policy in new[] { ConversationSyncPolicies.Metadata, ConversationSyncPolicies.Paused })
        {
            var c = rig.New(policy: policy);
            rig.Meta(c);
            var item = rig.Item(c, "內容不上線");
            rig.Ui.Put(new QueueChanged(c.Id, new PublishQueueSnapshot(c.Id, [(Guid.NewGuid().ToString("N"), "排隊的訊息")])));
            rig.Ui.Put(new TurnStartedChange(c.Id, false));
            rig.Ui.Put(new AttentionChange(c.Id, ConversationAttentionKinds.Question, item.Id));
            await rig.IdleAsync();

            var rows = await rig.RowsAsync(c);
            rows.Select(r => r.Kind).Should().Equal(ConversationEventKinds.Created, ConversationEventKinds.Turn, ConversationEventKinds.Attention);
            Obj(rows[0])["syncPolicy"]!.Value<string>().Should().Be(policy);
            rows.Should().OnlyContain(r => ConversationEventKinds.ClassOf(r.Kind) == SyncClasses.Meta);
        }
    }

    // ───────────────────────────── 投影（D16、D18） ─────────────────────────────

    [Fact]
    public async Task 權杖被遮蔽_秘密回答被清掉_禁止欄位不出現()
    {
        var token = RuntimeToken(7);
        var basic = RuntimeBasic(11);
        CredentialLeakRules.HasLeak(token).Should().BeTrue();
        CredentialLeakRules.HasLeak(basic).Should().BeTrue();
        var secret = "答案-" + Guid.NewGuid().ToString("N")[..16];
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        // 先讓 created 被確認：「我的回答：」要整份走，created 確認之前不走（合併前審查 P4）
        await rig.SendAsync();
        var question = new AgentQuestion { Id = "q1", Text = "密碼是什麼", IsSecret = true };
        var card = rig.Item(c, new AgentChatItem
        {
            Kind = AgentItemKind.Question, Status = AgentItemStatus.Done, AnswerAsMessage = true, Decision = "answered",
            Questions = [question], Answers = new Dictionary<string, List<string>> { ["q1"] = [ConversationWire.HiddenAnswer] },
        });
        // WP4.3 之前的中樞把秘密原文存成「我的回答：」的文字
        var answer = rig.Item(c, AnswerMessageRules.Prefix + "密碼是什麼：" + secret, AgentItemKind.User);
        var tool = rig.Item(c, new AgentChatItem
        {
            Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, ToolName = "執行指令", Output = "輸出 " + token, Detail = "curl -H \"" + basic + "\"",
            ProcessInstance = "proc-9", RequestKey = "rk-1", Seq = 5,
            Attachments = [new AgentAttachment { Id = "att1", FileName = "a.png", StoredPath = @"C:\data\a.png", Kind = AgentAttachmentKind.Image, SizeBytes = 3 }],
        });
        await rig.IdleAsync();

        rig.Ui.Calls.Should().Contain(call => call.StartsWith($"RequestItems:{c.Id}:", StringComparison.Ordinal), "「我的回答：」要對著整份清單清除");
        var rows = (await rig.RowsAsync(c)).Where(r => r.State == OutboxState.Pending).ToList();
        rows.Where(r => r.Kind == ConversationEventKinds.Item).Select(r => Obj(r)["id"]!.Value<string>()).Should().BeEquivalentTo([card.Id, answer.Id, tool.Id]);
        foreach (var row in rows)
        {
            var json = Json(row);
            json.Should().NotContain(token).And.NotContain(basic).And.NotContain(secret);
            CredentialLeakRules.HasLeak(json).Should().BeFalse();
            foreach (var forbidden in new[] { "processInstance", "requestKey", "\"seq\"", "storedPath", "proc-9", "rk-1", @"C:\\data" })
                json.Should().NotContain(forbidden);
        }

        var sentAnswer = rows.Single(r => r.Kind == ConversationEventKinds.Item && Obj(r)["id"]!.Value<string>() == answer.Id);
        Obj(sentAnswer)["text"]!.Value<string>().Should().Contain(ConversationWire.HiddenAnswer);
    }

    [Fact]
    public async Task 等待中卡片欄位被改_發布的requestDigest是重算的()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var card = new AgentChatItem
        {
            Kind = AgentItemKind.Approval, Status = AgentItemStatus.Waiting, ToolName = "執行指令", Title = "rm", Detail = "rm -rf build", RequestKey = "rk-7",
            CreatedAt = T0, UpdatedAt = T0,
        };
        card.RequestDigest = RequestDigestRules.Compute(card);
        var stale = card.RequestDigest;
        card.Detail = "rm -rf build out";
        rig.Item(c, card);
        await rig.IdleAsync();

        var row = (await rig.RowsAsync(c)).Single(r => r.Kind == ConversationEventKinds.Item);
        var digest = Obj(row)["requestDigest"]!.Value<string>();
        digest.Should().Be(RequestDigestRules.Compute(card)).And.NotBe(stale);
    }

    [Fact]
    public async Task 截斷的項目_Validate通過_truncated記1()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var big = rig.Item(c, new AgentChatItem
        {
            Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, ToolName = "讀取檔案", Output = new string('甲', 200_000), Detail = new string('x', 90_000),
        });
        var small = rig.Item(c, "小的");
        await rig.IdleAsync();

        var row = (await rig.RowsAsync(c)).Single(r => r.Kind == ConversationEventKinds.Item && Obj(r)["id"]!.Value<string>() == big.Id);
        ConversationEventRules.Validate(ConversationEventKinds.Item, Json(row)).Should().BeNull();
        var published = (await rig.PublishedAsync(c)).ToDictionary(p => p.ItemId);
        published[big.Id].Truncated.Should().BeTrue();
        published[small.Id].Truncated.Should().BeFalse();
        published[big.Id].Ord.Should().Be(big.Ord);
    }

    [Fact]
    public async Task 截斷之後才遮蔽_解碼範圍外的憑證不外洩()
    {
        // 很長的 base64（整個資料夾打包後印出來）：遮蔽只解碼開頭 64 KB；output 截斷只留結尾，所以要先截斷再遮蔽，
        // 留下的結尾（切點那一行整段換掉）才看得到
        var secret = RuntimeToken(13);
        var content = new string('x', 120_000) + "{\"refresh_token\":\"" + secret + "\"}";
        var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        CredentialLeakRules.Mask(blob).Should().Be(blob, "整段遮蔽時解碼不到結尾（這個測試的前提）");
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, new AgentChatItem { Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, ToolName = "執行指令", Output = blob });
        await rig.IdleAsync();

        var row = (await rig.RowsAsync(c)).Single(r => r.Kind == ConversationEventKinds.Item);
        var output = Obj(row)["output"]!.Value<string>()!;
        output.Should().NotContain(blob[^200..]);
        Json(row).Should().NotContain(secret);
        (await rig.PublishedAsync(c)).Single().Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task 不合格的一筆被丟掉_同一輪第3階段的事件照寫()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.IdleAsync();
        AgentChatItem? bad = null;
        AgentChatItem? good = null;
        // epoch 不合格（存檔壞了）：payload 合格、外框不合格，要在投影時就丟掉（AppendMany 遇到會整批不寫）
        var broken = rig.New();
        broken.Meta.LeaseEpoch = 0;
        // 在引擎執行緒上放：盒子的喚醒與第 3 階段的事件在同一輪收件匣
        await rig.H.Inspect(_ =>
        {
            bad = rig.Item(c, new AgentChatItem { Kind = (AgentItemKind)99, Text = "沒定義的種類" });
            good = rig.Item(c, "合格的");
            rig.Meta(broken);
            rig.Item(broken, "外框不合格的對話");
            rig.H.Append();
            return 0;
        });
        await rig.IdleAsync();

        var outbox = await rig.H.Inspect(v => v.Outbox);
        var ids = outbox.Where(e => e.Kind == ConversationEventKinds.Item).Select(e => Obj(e)["id"]!.Value<string>()).ToList();
        ids.Should().Equal(good!.Id);
        ids.Should().NotContain(bad!.Id);
        outbox.Should().NotContain(e => e.ConversationId == broken.Id);
        outbox.Should().Contain(e => e.Kind == SyncEventKinds.CliState, "同一輪的第 3 階段事件照寫");
    }

    [Fact]
    public async Task Ord是null_要求重取快照_不丟掉()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Item(c, "還沒配 Ord", ord: false);
        await rig.IdleAsync();

        // created 還沒被確認：先不整份走（伺服器可能回 not_owner，合併前審查 P4），記成要重新發布
        rig.Ui.Calls.Should().NotContain(call => call.StartsWith($"RequestItems:{c.Id}:", StringComparison.Ordinal));
        (await rig.RowsAsync(c)).Should().NotContain(r => r.Kind == ConversationEventKinds.Item);
        (await rig.NeedsRedoAsync()).Should().Contain(c.Id);

        await rig.SendAsync();
        await rig.SendAsync();

        rig.Ui.Calls.Should().Contain($"RequestItems:{c.Id}:0");
        // 確認之後 outbox 只留骨架：內容看伺服器收到的
        var sent = rig.Sent(c).Single(e => e.Kind == ConversationEventKinds.Item);
        var json = JObject.Parse(Json(sent));
        json["id"]!.Value<string>().Should().Be(item.Id);
        json["ord"]!.Value<long>().Should().Be(item.Ord!.Value).And.BeGreaterThan(0);
        rig.Model.Accepted.Should().Contain(e => e.DeviceSeq == sent.DeviceSeq);
        (await rig.NeedsRedoAsync()).Should().NotContain(c.Id);
    }

    [Fact]
    public async Task 舊身分的對話_created確認之前不整份走_被擋的沒有項目內容()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Model.Seed(c.Id, owner: "dev-old-identity");
        var text = "舊身分對話的歷史-" + Guid.NewGuid().ToString("N");
        // 重新配對後中樞把 OrdsReady 重設：歷史項目都還沒有 Ord，第一個變化一定要整份走
        for (var i = 0; i < 5; i++)
            c.Items.Add(new AgentChatItem { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = text + i, CreatedAt = T0, UpdatedAt = T0 });
        rig.Meta(c);
        rig.Item(c, "重新配對後的新一則", ord: false);
        await rig.IdleAsync();

        (await rig.RowsAsync(c)).Should().NotContain(r => r.Kind == ConversationEventKinds.Item, "created 還沒確認：不整份走");
        await rig.SendAsync();
        await rig.H.AdvanceAsync(3);

        (await rig.RowAsync(c)).Blocked.Should().Be(ConversationRejectCodes.NotOwner);
        rig.Model.Rejected.Should().NotBeEmpty().And.NotContain(r => r.Kind == ConversationEventKinds.Item);
        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(text));
        rig.Ui.Calls.Should().NotContain(call => call.StartsWith($"RequestItems:{c.Id}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 離線時created還沒確認_不整份走_連上確認之後才走()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.GoOfflineAsync();
        var c = rig.New();
        for (var i = 0; i < 3; i++)
            c.Items.Add(new AgentChatItem { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = "歷史" + i, CreatedAt = T0, UpdatedAt = T0 });
        rig.Meta(c);
        rig.Item(c, "離線時的新一則", ord: false);
        await rig.IdleAsync();

        (await rig.RowsAsync(c)).Select(r => r.Kind).Should().Equal([ConversationEventKinds.Created], "離線（armed）照寫 created，但它還沒確認：不整份走");

        await rig.ReconnectAsync();
        await rig.SendAsync(2);

        rig.Model.Items(c.Id).Keys.Should().BeEquivalentTo(c.Items.Select(i => i.Id));
        (await rig.NeedsRedoAsync()).Should().NotContain(c.Id);
    }

    // ───────────────────────────── 摘要（D11） ─────────────────────────────

    [Fact]
    public async Task 摘要與outbox同一個交易()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.H.Inspect(v => v.Store!.FaultInjection = point =>
        {
            if (point == "AppendMany:commit")
                throw new SqliteException("模擬：寫入失敗", 10);
        });

        var item = rig.Item(c, "寫不進去");
        await rig.IdleAsync();

        (await rig.RowsAsync(c)).Should().NotContain(r => r.Kind == ConversationEventKinds.Item);
        (await rig.PublishedAsync(c)).Should().BeEmpty("摘要與 outbox 同一個交易：outbox 沒寫成，摘要也沒有");
        (await rig.NeedsRedoAsync()).Should().Contain(c.Id, "沒寫成的對話記成要重新發布");

        await rig.H.Inspect(v => v.Store!.FaultInjection = null);
        rig.Changed(c, item);
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Should().ContainSingle(r => r.Kind == ConversationEventKinds.Item);
        (await rig.PublishedAsync(c)).Should().ContainSingle(p => p.ItemId == item.Id && p.SrcSha256 != null && p.WireSha256 != null);
    }

    [Fact]
    public async Task 被拒的清摘要_保留Ord()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Model.Reject = e => e.Kind == ConversationEventKinds.Item ? ConversationRejectCodes.Dlp : null;
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Item(c, "被擋的");
        await rig.IdleAsync();
        (await rig.PublishedAsync(c)).Single().SrcSha256.Should().NotBeNull();

        await rig.SendAsync();

        var published = (await rig.PublishedAsync(c)).Single();
        published.ItemId.Should().Be(item.Id);
        published.Ord.Should().Be(item.Ord);
        published.SrcSha256.Should().BeNull();
        published.WireSha256.Should().BeNull();
    }

    // ───────────────────────────── 沒有 conv1（D13、C10） ─────────────────────────────

    [Fact]
    public async Task 舊伺服器_沒協商conv1_不寫新事件_待送的換成空物件_rejected裡沒有項目文字()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "第一則");
        await rig.SendAsync();
        await rig.GoOfflineAsync();
        var text = "離線時寫的內容-" + Guid.NewGuid().ToString("N");
        rig.Item(c, text);
        rig.H.Append();
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Should().Contain(r => r.State == OutboxState.Pending && Json(r).Contains(text), "上一次進 Live 的連線有 conv1：離線照寫");

        // 伺服器回滾到 1724718：不宣告功能，所有對話種類一律 invalid
        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();

        (await rig.H.Inspect(v => v.Features)).Should().BeEmpty();
        rig.Model.Rejected.Should().NotBeEmpty().And.OnlyContain(r => r.Payload == SyncStore.EmptyConversationPayload);
        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(text));
        rig.H.Server.Accepted.Should().Contain(e => e.Kind == SyncEventKinds.CliState, "第 3 階段的事件照常確認");
        (await rig.NeedsRedoAsync()).Should().Contain(c.Id, "被擋下的記成要重整");
        (await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.ConvArmed))).Should().Be("0");

        var before = (await rig.RowsAsync()).Count;
        rig.Item(c, "舊伺服器上的新變化");
        await rig.IdleAsync();
        (await rig.RowsAsync()).Count.Should().Be(before, "沒有 conv1 不寫新的對話事件");
    }

    /// <summary>這一批在路上掉了（伺服器沒收到）：列是「送過」的，連線斷掉、之後重連</summary>
    private static void LoseNextBatchOf(Rig rig, FakeConversation c)
    {
        var lose = 1;
        rig.H.Server.BatchOverride = b =>
        {
            if (!b.Events.Any(e => e.ConversationId == c.Id) || Interlocked.Exchange(ref lose, 0) == 0)
                return null;
            rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
            throw EngineHarness.Transient();
        };
    }

    [Fact]
    public async Task 沒有conv1_送過沒確認的列內容凍結_只換待送的()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();
        LoseNextBatchOf(rig, c);
        rig.Item(c, "送過的內容");
        await rig.IdleAsync();
        await rig.SendAsync();
        var first = rig.Sent(c).Single(e => e.Kind == ConversationEventKinds.Item);
        rig.H.Server.Accepted.Should().NotContain(e => e.DeviceSeq == first.DeviceSeq);
        (await rig.H.StateAsync()).Should().NotBe(SyncEngineState.Live);

        var later = "之後才寫的內容-" + Guid.NewGuid().ToString("N");
        rig.Item(c, later);
        await rig.IdleAsync();
        rig.H.Server.BatchOverride = null;
        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();

        var again = rig.Sent(c).Where(e => e.DeviceSeq == first.DeviceSeq).ToList();
        again.Should().HaveCountGreaterThan(1, "重連後重送");
        again.Should().OnlyContain(e => e.PayloadSha256 == first.PayloadSha256 && e.Payload == first.Payload,
            "送過的列不換（同一個 deviceSeq 換了內容，伺服器會當成不同的事件）");
        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(later), "待送的換成空物件");
        rig.Model.Rejected.Should().Contain(r => r.Payload == SyncStore.EmptyConversationPayload);
    }

    [Fact]
    public async Task 換成空物件的列之後在conv1連線被擋_記重整補送_不算程式錯誤()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();
        await rig.GoOfflineAsync();
        rig.Item(c, "離線時寫的");
        await rig.IdleAsync();

        // 連上舊伺服器：送出前換成 {}，這一批卻在路上掉了
        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        LoseNextBatchOf(rig, c);
        rig.H.Server.ConnectFailure = null;
        rig.H.Engine.RetryNow();
        await rig.IdleAsync();
        await rig.SendAsync();
        Json(rig.Sent(c).Single(e => e.Kind == ConversationEventKinds.Item)).Should().Be(SyncStore.EmptyConversationPayload);
        (await rig.H.StateAsync()).Should().NotBe(SyncEngineState.Live);

        // 之後連上有 conv1 的伺服器：凍結的 {} 照原樣重送、被 invalid 擋下
        rig.H.Server.BatchOverride = null;
        rig.H.Server.Features = BothFeatures;
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Model.Rejected.Should().ContainSingle(r => r.Kind == ConversationEventKinds.Item && r.Code == SyncErrorCodes.Invalid && r.Payload == SyncStore.EmptyConversationPayload);
        var row = await rig.RowAsync(c);
        row.Blocked.Should().BeNull("換成 {} 的列被擋不是程式錯誤：記重整");
        var resent = rig.Sent(c).Last(e => e.Kind == ConversationEventKinds.Item);
        Json(resent).Should().Contain("離線時寫的", "進 Live 時的重整補送真正的內容");
        rig.Model.Accepted.Should().Contain(e => e.DeviceSeq == resent.DeviceSeq);
        (await rig.NeedsRedoAsync()).Should().NotContain(c.Id, "補送之後清掉");
        rig.Ui.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task 離線_conv_armed預設false_不寫()
    {
        await using var rig = new Rig();
        rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "從沒連上過");
        await rig.IdleAsync();

        (await rig.RowsAsync()).Should().BeEmpty();
        (await rig.H.Inspect(v => v.Store!.GetKv(SyncStoreSchema.ConvArmed))).Should().BeNull();
    }

    [Fact]
    public async Task 上次有conv1_離線照寫()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
        await rig.H.RestartAsync();
        (await rig.H.StateAsync()).Should().NotBe(SyncEngineState.Live);

        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "離線照寫");
        await rig.IdleAsync();

        (await rig.RowsAsync(c)).Select(r => r.Kind).Should().Equal(ConversationEventKinds.Created, ConversationEventKinds.Item);
    }

    // ───────────────────────────── 拒絕碼（D14、6.4） ─────────────────────────────

    [Fact]
    public async Task not_owner_封鎖_一百個對話只提示一次()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var conversations = Enumerable.Range(0, 100).Select(i => rig.New(minute: i)).ToList();
        foreach (var c in conversations)
        {
            rig.Model.Seed(c.Id, owner: "dev-old-identity");
            rig.Meta(c);
        }

        await rig.IdleAsync();
        await rig.SendAsync(1);
        await rig.H.AdvanceAsync(3);

        rig.Ui.Notices.Should().ContainSingle().Which.Should().Be((PublishNoticeKind.NotOwner, 100));
        var rows = await rig.PublishMetaAsync();
        conversations.Should().OnlyContain(c => rows[c.Id].Blocked == ConversationRejectCodes.NotOwner);

        var before = (await rig.RowsAsync()).Count;
        rig.Item(conversations[0], "封鎖之後");
        rig.Meta(conversations[1]);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(3);
        (await rig.RowsAsync()).Count.Should().Be(before);
        rig.Ui.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task deleted與fenced_封鎖_之後不寫()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var deleted = rig.New();
        var fenced = rig.New();
        rig.Model.Seed(deleted.Id, rig.H.DeviceId, deleted: true);
        rig.Model.Seed(fenced.Id, rig.H.DeviceId, epoch: 7);
        rig.Meta(deleted);
        rig.Meta(fenced);
        await rig.IdleAsync();
        await rig.SendAsync();

        (await rig.RowAsync(deleted)).Blocked.Should().Be(ConversationRejectCodes.Deleted);
        (await rig.RowAsync(fenced)).Blocked.Should().Be(ConversationRejectCodes.Fenced);
        var before = (await rig.RowsAsync()).Count;
        rig.Item(deleted, "之後");
        rig.Item(fenced, "之後");
        await rig.IdleAsync();
        (await rig.RowsAsync()).Count.Should().Be(before);
        await rig.H.AdvanceAsync(3);
        rig.Ui.Notices.Should().ContainSingle().Which.Should().Be((PublishNoticeKind.Blocked, 2));
    }

    [Fact]
    public async Task conv_deleted收到deleted或conv1的invalid_都算成功()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var a = rig.New();
        var b = rig.New();
        rig.Meta(a);
        rig.Meta(b);
        await rig.SendAsync();
        await rig.H.Inspect(v =>
        {
            v.Store!.SetNeedsRedo(a.Id, true);
            v.Store.SetNeedsRedo(b.Id, true);
            return 0;
        });
        rig.Model.Reject = e => e.Kind != ConversationEventKinds.Deleted ? null : e.ConversationId == a.Id ? ConversationRejectCodes.Deleted : SyncErrorCodes.Invalid;

        rig.Ui.Put(new DeletingChange(a.Id));
        rig.Ui.Put(new DeletingChange(b.Id));
        await rig.IdleAsync();
        await rig.SendAsync();

        rig.Model.Rejected.Select(r => r.Code).Should().BeEquivalentTo([ConversationRejectCodes.Deleted, SyncErrorCodes.Invalid]);
        (await rig.NeedsRedoAsync()).Should().NotContain([a.Id, b.Id]);
        var rows = await rig.PublishMetaAsync();
        rows[a.Id].DeletedAt.Should().NotBeNull();
        rows[b.Id].DeletedAt.Should().NotBeNull();
        rows[a.Id].Blocked.Should().BeNull();
        rows[b.Id].Blocked.Should().BeNull();
        await rig.H.AdvanceAsync(3);
        rig.Ui.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task item類dlp_經UI改成paused_提示一次()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Model.Reject = e => e.Kind == ConversationEventKinds.Item ? ConversationRejectCodes.Dlp : null;
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "第一筆");
        rig.Item(c, "第二筆");
        await rig.IdleAsync();
        await rig.SendAsync();
        await rig.H.AdvanceAsync(3);

        rig.Ui.Paused.Should().Equal(c.Id);
        rig.Ui.Notices.Should().ContainSingle().Which.Should().Be((PublishNoticeKind.DlpPaused, 1));
        (await rig.RowAsync(c)).Blocked.Should().BeNull("item 類的 dlp 改成 paused，不封鎖");

        // UI 存檔之後帶來 paused 的 meta：只送 meta（伺服器清掉內容）
        c.Meta.SyncPolicy = ConversationSyncPolicies.Paused;
        rig.Meta(c);
        await rig.IdleAsync();
        var last = (await rig.RowsAsync(c)).Last();
        last.Kind.Should().Be(ConversationEventKinds.Meta);
        Obj(last)["syncPolicy"]!.Value<string>().Should().Be(ConversationSyncPolicies.Paused);
    }

    [Fact]
    public async Task meta類dlp_封鎖()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Model.Reject = e => e.Kind == ConversationEventKinds.Created ? ConversationRejectCodes.Dlp : null;
        var c = rig.New();
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync();
        await rig.H.AdvanceAsync(3);

        (await rig.RowAsync(c)).Blocked.Should().Be(ConversationRejectCodes.Dlp);
        rig.Ui.Paused.Should().BeEmpty();
        rig.Ui.Notices.Should().ContainSingle().Which.Should().Be((PublishNoticeKind.Blocked, 1));
    }

    [Fact]
    public async Task conv1上created未確認的invalid_隔1_10_60分鐘補created_第4次封鎖併進彙總提示()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Model.Reject = e => e.ConversationId == c.Id ? SyncErrorCodes.Invalid : null;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync(1);
        int Created() => rig.Model.Rejected.Count(r => r.Kind == ConversationEventKinds.Created);
        Created().Should().Be(1);
        (await rig.RowAsync(c)).CreatedRetries.Should().Be(1);
        (await rig.NeedsRedoAsync()).Should().Contain(c.Id);

        await rig.H.AdvanceAsync(57);
        Created().Should().Be(1, "第 1 次補送在 1 分鐘之後");
        await rig.H.AdvanceAsync(4);
        Created().Should().Be(2);
        await rig.H.AdvanceAsync(590);
        Created().Should().Be(2, "第 2 次補送在 10 分鐘之後");
        await rig.H.AdvanceAsync(15);
        Created().Should().Be(3);
        await rig.H.AdvanceAsync(3590);
        Created().Should().Be(3, "第 3 次補送在 60 分鐘之後");
        await rig.H.AdvanceAsync(15);
        Created().Should().Be(4);
        await rig.H.AdvanceAsync(3);

        (await rig.RowAsync(c)).Blocked.Should().Be(SyncEngine.BlockedCreatedRetries, "第 4 次封鎖");
        rig.Ui.Notices.Should().ContainSingle().Which.Should().Be((PublishNoticeKind.NotOwner, 1), "併進彙總提示");
        await rig.H.AdvanceAsync(7200);
        Created().Should().Be(4, "封鎖之後不再補送");
    }

    [Fact]
    public async Task 同一對話retry_at之前的多筆invalid_只算一次()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Model.Reject = e => e.ConversationId == c.Id ? SyncErrorCodes.Invalid : null;
        rig.Meta(c);
        rig.Item(c, "一");
        rig.Item(c, "二");
        rig.Item(c, "三");
        await rig.IdleAsync();
        await rig.SendAsync(1);

        rig.Model.Rejected.Should().HaveCount(4);
        var row = await rig.RowAsync(c);
        row.CreatedRetries.Should().Be(1);
        row.RetryAt.Should().NotBeNull();
        (await rig.PublishedAsync(c)).Should().OnlyContain(p => p.SrcSha256 == null, "被拒那幾筆的摘要清掉");
    }

    [Fact]
    public async Task conv1連線上的invalid_算Bug_換伺服器實例後解除()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();
        (await rig.RowAsync(c)).CreatedAcked.Should().BeTrue();
        rig.Model.Reject = e => e.Kind == ConversationEventKinds.Item ? SyncErrorCodes.Invalid : null;
        rig.Item(c, "伺服器認得的對話還被擋");
        await rig.IdleAsync();
        await rig.SendAsync();
        await rig.H.AdvanceAsync(3);

        (await rig.RowAsync(c)).Blocked.Should().Be(SyncEngine.BlockedBugPrefix + rig.H.AppVersion);
        rig.Ui.Notices.Should().ContainSingle().Which.Should().Be((PublishNoticeKind.Blocked, 1));
        var before = (await rig.RowsAsync()).Count;
        rig.Item(c, "封鎖期間");
        await rig.IdleAsync();
        (await rig.RowsAsync()).Count.Should().Be(before);

        rig.Model.Reject = null;
        rig.H.Server.Instance = new ServerInstance("instance-2", 0);
        await rig.ReconnectAsync();

        (await rig.RowAsync(c)).Blocked.Should().BeNull("伺服器換實例：封鎖解除");
        rig.Item(c, "換實例之後");
        await rig.IdleAsync();
        var after = (await rig.RowsAsync(c)).Skip(before).Select(r => r.Kind).ToList();
        after.Should().StartWith(ConversationEventKinds.Created, "新的實例不認得它：先送 created").And.Contain(ConversationEventKinds.Item);
    }

    [Fact]
    public async Task 停止之後_發布的計時器都不留著()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var unknown = rig.New();
        rig.Model.Reject = e => e.ConversationId == unknown.Id ? SyncErrorCodes.Invalid : null;
        rig.Meta(unknown);
        var running = rig.New(minute: 1);
        rig.Meta(running);
        await rig.IdleAsync();
        await rig.SendAsync(1);
        running.Running = true;
        rig.Ui.Put(new TurnStartedChange(running.Id, false));
        rig.Meta(running);
        var streaming = rig.Item(running, "執行中", AgentItemKind.Assistant, AgentItemStatus.Running);
        await rig.IdleAsync();
        // 第一次就持久了；一秒內再變，沒寫的變化等檢查點（執行中項目的計時器）
        await rig.H.AdvanceAsync(TimeSpan.FromMilliseconds(1_000));
        streaming.Text += "又一段";
        rig.Changed(running, streaming);
        await rig.IdleAsync();
        (await rig.RowAsync(unknown)).RetryAt.Should().NotBeNull("前提：補送 created 的計時器排了（最長 1 小時）");
        (await rig.H.Inspect(v => v.HasRunningTimer)).Should().BeTrue("前提：執行中項目的計時器開著");

        await rig.H.Engine.StopAsync(TimeSpan.FromSeconds(5));

        rig.H.Time.ActiveTimers.Should().Be(0, "回呼抓著整個舊引擎：停止時一律取消（合併前審查 T4）");
    }

    [Fact]
    public async Task 程式錯誤的封鎖_app換版後解除()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();
        rig.Model.Reject = e => e.Kind == ConversationEventKinds.Item ? SyncErrorCodes.Invalid : null;
        rig.Item(c, "被擋");
        await rig.IdleAsync();
        await rig.SendAsync();
        rig.Model.Reject = null;

        await rig.H.RestartAsync();
        (await rig.RowAsync(c)).Blocked.Should().Be(SyncEngine.BlockedBugPrefix + "0.5.0", "同一版本照樣封鎖");

        rig.H.AppVersion = "0.6.0";
        await rig.H.RestartAsync();
        (await rig.RowAsync(c)).Blocked.Should().BeNull("換版之後在 conv1 連線上解除");
    }

    [Fact]
    public async Task 沒有中繼資料快照時刪除_conv_deleted帶刪除當下的epoch_不是寫死的1()
    {
        // 第 5 階段計畫 6：接手後的對話 epoch 不是 1；寫死 1 的刪除會被伺服器 fenced（D4），刪不掉
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        c.Meta.LeaseEpoch = 3;
        rig.Meta(c);
        await rig.SendAsync();
        (await rig.RowsAsync(c)).Should().OnlyContain(r => r.Epoch == 3);
        rig.Model[c.Id]!.Epoch.Should().Be(3);

        // 這次啟動還沒看過它的中繼資料（中樞已經沒有它）：走沒有快照的刪除
        rig.Ui.Remove(c.Id);
        await rig.H.RestartAsync();
        rig.Ui.Put(new DeletingChange(c.Id, 3));
        await rig.IdleAsync();
        await rig.SendAsync();

        var deleted = (await rig.RowsAsync(c)).Where(r => r.Kind == ConversationEventKinds.Deleted).ToList();
        deleted.Should().NotBeEmpty().And.OnlyContain(r => r.Epoch == 3);
        rig.Model[c.Id]!.Deleted.Should().BeTrue("epoch 對得上，伺服器照收");
        rig.Model.Rejected.Should().NotContain(r => r.Kind == ConversationEventKinds.Deleted);
    }

    [Fact]
    public async Task 沒有conv1時conv_deleted被invalid_墓碑記needs_redo_下次conv1補送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();
        await rig.GoOfflineAsync();
        rig.Ui.Put(new DeletingChange(c.Id));
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Last().Kind.Should().Be(ConversationEventKinds.Deleted);

        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();
        rig.Model.Rejected.Should().ContainSingle(r => r.Kind == ConversationEventKinds.Deleted);
        (await rig.NeedsRedoAsync()).Should().Contain(c.Id, "回滾期間的刪除不能當成完成");
        (await rig.RowAsync(c)).DeletedAt.Should().NotBeNull();
        rig.Model[c.Id]!.Deleted.Should().BeFalse();

        rig.H.Server.Features = BothFeatures;
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        await rig.SendAsync();

        rig.Model[c.Id]!.Deleted.Should().BeTrue("下一次 conv1 連線補送 conv.deleted");
        (await rig.NeedsRedoAsync()).Should().NotContain(c.Id);
    }

    [Fact]
    public async Task 伺服器遮蔽後接受_桌面不重送不提示_摘要不變()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        // 伺服器的第二道 DLP 比桌面多認一種樣式（模擬）：遮蔽後接受，結果和沒命中一樣
        rig.Model.RemaskAll = true;
        var c = rig.New();
        rig.Meta(c);
        var item = rig.Item(c, "伺服器會再遮蔽的內容 " + RuntimeToken(3));
        await rig.IdleAsync();
        var digest = (await rig.PublishedAsync(c)).Single();
        await rig.SendAsync();
        await rig.H.AdvanceAsync(30);

        rig.Model.Remasked.Should().BeGreaterThan(0);
        rig.Model.Rejected.Should().BeEmpty();
        rig.Sent(c).Count(e => e.Kind == ConversationEventKinds.Item).Should().Be(1, "不重送");
        rig.Ui.Notices.Should().BeEmpty();
        (await rig.PublishedAsync(c)).Single().Should().Be(digest);
        (await rig.RowAsync(c)).Blocked.Should().BeNull();
        item.Ord.Should().Be(digest.Ord);
    }

    // ───────────────────────────── 心跳（D20） ─────────────────────────────

    [Fact]
    public async Task 心跳只在conv1連線_各最多1000筆_只列已知未封鎖()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var running = Enumerable.Range(0, SyncEngine.MaxHeartbeatConversations + 1).Select(i => rig.New(minute: i)).ToList();
        running.ForEach(c => c.Running = true);
        var waiting = rig.New();
        waiting.Waiting = true;
        var blocked = rig.New(minute: 5000);
        blocked.Running = true;
        rig.Model.Seed(blocked.Id, "dev-old-identity");
        var localOnly = rig.New(policy: ConversationSyncPolicies.LocalOnly, minute: 5001);
        localOnly.Running = true;
        var metadata = rig.New(policy: ConversationSyncPolicies.Metadata, minute: 5002);
        metadata.Running = true;
        var leaving = rig.New(minute: 5003);
        foreach (var c in running.Append(waiting).Append(blocked).Append(localOnly).Append(metadata).Append(leaving))
            rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync(2);
        // 改成只在這台電腦、旁檔還沒寫好（伺服器上還沒刪、也還沒封鎖）的期間也不列
        rig.Ui.HoldServerDeleted = true;
        leaving.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        leaving.Running = true;
        rig.Meta(leaving);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(16);

        var beat = rig.H.Server.Heartbeats.Last();
        beat.Running.Should().HaveCount(SyncEngine.MaxHeartbeatConversations);
        beat.Running.Should().Contain(metadata.Id, "metadata 也發 meta 類");
        beat.Running.Should().NotContain([blocked.Id, localOnly.Id, leaving.Id, running[0].Id, running[1].Id], "封鎖的、不發布的不列；超過上限時留最近更新的");
        beat.Waiting.Should().Equal(waiting.Id);

        rig.H.Server.Features = null;
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(16);
        var old = rig.H.Server.Heartbeats.Last();
        old.Running.Should().BeEmpty("沒有 conv1 的連線不帶對話");
        old.Waiting.Should().BeEmpty();
    }

    // ───────────────────────────── 政策轉換（D25） ─────────────────────────────

    [Fact]
    public async Task 改成metadata_只送meta_清項目摘要_保留Ord()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var a = rig.Item(c, "甲");
        var b = rig.Item(c, "乙");
        await rig.SendAsync();
        var before = (await rig.RowsAsync(c)).Count;

        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        rig.Item(c, "改成 metadata 之後");
        await rig.IdleAsync();
        await rig.SendAsync();

        var added = (await rig.RowsAsync(c)).Skip(before).ToList();
        added.Select(r => r.Kind).Should().Equal(ConversationEventKinds.Meta);
        rig.Model[c.Id]!.Policy.Should().Be(ConversationSyncPolicies.Metadata);
        var published = await rig.PublishedAsync(c);
        published.Select(p => p.ItemId).Should().Equal(a.Id, b.Id);
        published.Should().OnlyContain(p => p.SrcSha256 == null && p.WireSha256 == null);
        published.Select(p => p.Ord).Should().Equal(a.Ord!.Value, b.Ord!.Value);
        (await rig.RowAsync(c)).Policy.Should().Be(ConversationSyncPolicies.Metadata);
    }

    [Fact]
    public async Task 改回full_先送meta再整份重整_沒了的項目送itemRemoved()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var a = rig.Item(c, "甲");
        var b = rig.Item(c, "乙");
        await rig.SendAsync();
        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.SendAsync();
        c.Items.Remove(b);
        var before = rig.Sent(c).Count;

        c.Meta.SyncPolicy = null;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync();

        // 確認之後 outbox 只留骨架：內容看伺服器收到的
        var added = rig.Sent(c).Skip(before).ToList();
        added.Select(r => r.Kind).Should().Equal(ConversationEventKinds.Meta, ConversationEventKinds.ItemRemoved, ConversationEventKinds.Item);
        JObject.Parse(Json(added[0]))["syncPolicy"]!.Value<string>().Should().Be(ConversationSyncPolicies.Full);
        JObject.Parse(Json(added[1]))["id"]!.Value<string>().Should().Be(b.Id);
        JObject.Parse(Json(added[2]))["id"]!.Value<string>().Should().Be(a.Id);
        rig.Model.Rejected.Should().BeEmpty();
        var published = (await rig.PublishedAsync(c)).ToDictionary(p => p.ItemId);
        published[a.Id].SrcSha256.Should().NotBeNull();
        published[b.Id].Removed.Should().BeTrue();
        published[b.Id].Ord.Should().Be(b.Ord);
    }

    [Fact]
    public async Task 改成localOnly_先寫旁檔再送deleted_然後封鎖()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "甲");
        await rig.SendAsync();
        rig.Ui.HoldServerDeleted = true;

        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Meta(c);
        rig.Item(c, "改了之後");
        await rig.IdleAsync();
        rig.Ui.Calls.Should().Contain($"MarkServerDeleted:{c.Id}");
        (await rig.RowsAsync(c)).Should().NotContain(r => r.Kind == ConversationEventKinds.Deleted, "旁檔寫好之前不送 conv.deleted");

        rig.Ui.ReleaseServerDeleted();
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Last().Kind.Should().Be(ConversationEventKinds.Deleted);
        await rig.SendAsync();

        rig.Model[c.Id]!.Deleted.Should().BeTrue();
        var row = await rig.RowAsync(c);
        row.DeletedAt.Should().NotBeNull();
        row.Blocked.Should().Be(SyncEngine.BlockedLocalOnly);
        var count = (await rig.RowsAsync(c)).Count;
        rig.Item(c, "之後");
        rig.Meta(c);
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Count.Should().Be(count);
    }

    [Fact]
    public async Task 換到Antigravity_伺服器沒有prov1_延後不送_不當成localOnly刪除()
    {
        // 這個伺服器只宣告 conv1、live1（同舊版伺服器）：Grok、Antigravity 的對話延後（設計 2026-10-22-grok-antigravity-sync 第 3 節）
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();
        var accepted = rig.Model.Accepted.Count;

        c.Meta.Provider = AgentProvider.Antigravity;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync();

        rig.Ui.Calls.Should().NotContain($"MarkServerDeleted:{c.Id}", "沒有 prov1 是延後，不是使用者選了 localOnly");
        rig.Model[c.Id]!.Deleted.Should().BeFalse();
        rig.Model.Accepted.Count.Should().Be(accepted, "Antigravity 的事件一筆都不送（舊伺服器會擋下、讓對話被封鎖）");
        rig.Model.Rejected.Should().BeEmpty();
        (await rig.RowAsync(c)).Blocked.Should().BeNull();
    }

    [Fact]
    public async Task Grok的對話_沒有prov1時一筆都不送_連上有prov1的伺服器後補送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var grok = rig.New(AgentProvider.Grok);
        rig.Meta(grok);
        rig.Item(grok, "沒有 prov1 時寫的");
        var claude = rig.New();
        rig.Meta(claude);
        await rig.IdleAsync();
        await rig.SendAsync();
        rig.Sent(grok).Should().BeEmpty("舊伺服器的 ConversationEventRules 只收 Claude、Codex");
        rig.Sent(claude).Should().NotBeEmpty("Claude 照常送");

        rig.H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Prov1];
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);
        await rig.IdleAsync();
        await rig.SendAsync();

        var wire = ConversationIds.ToWire(grok.Id);
        rig.Model.Accepted.Should().Contain(e => e.ConversationId == wire && e.Kind == ConversationEventKinds.Created, "進 Live 時重整，補送延後的對話");
        rig.Model.Rejected.Should().BeEmpty();
        (await rig.RowAsync(grok)).Blocked.Should().BeNull();
    }

    [Fact]
    public async Task 換到Antigravity_伺服器有prov1_照常送conv_meta()
    {
        await using var rig = new Rig();
        rig.H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Prov1];
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();

        c.Meta.Provider = AgentProvider.Antigravity;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync();

        rig.Ui.Calls.Should().NotContain($"MarkServerDeleted:{c.Id}");
        rig.Model[c.Id]!.Deleted.Should().BeFalse();
        var wire = ConversationIds.ToWire(c.Id);
        rig.Model.Accepted.Where(e => e.ConversationId == wire && e.Kind == ConversationEventKinds.Meta)
            .Select(e => PayloadRules.TryDecode(e.Enc, e.Payload, 1 << 20, out var json) ? json : "")
            .Should().Contain(json => json.Contains("\"provider\":\"Antigravity\"", StringComparison.Ordinal));
        rig.Model.Rejected.Should().BeEmpty();
    }

    [Fact]
    public async Task 旁檔寫不成功_不送deleted_記needs_redo()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();
        rig.Ui.ServerDeletedResult = _ => false;

        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync();

        (await rig.RowsAsync(c)).Should().NotContain(r => r.Kind == ConversationEventKinds.Deleted);
        rig.Model[c.Id]!.Deleted.Should().BeFalse();
        (await rig.NeedsRedoAsync()).Should().Contain(c.Id);
        var row = await rig.RowAsync(c);
        row.DeletedAt.Should().BeNull();
        row.Blocked.Should().BeNull();
    }

    [Fact]
    public async Task 離線時改成localOnly_下次conv1連線補送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.SendAsync();
        // 連過一次沒有 conv1 的伺服器（conv_armed 變成 false），之後離線
        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();
        await rig.GoOfflineAsync();

        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Meta(c);
        await rig.IdleAsync();
        rig.Ui.Calls.Should().NotContain($"MarkServerDeleted:{c.Id}");
        (await rig.NeedsRedoAsync()).Should().Contain(c.Id);

        rig.H.Server.Features = BothFeatures;
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        await rig.SendAsync();

        rig.Ui.Calls.Should().Contain($"MarkServerDeleted:{c.Id}");
        rig.Model[c.Id]!.Deleted.Should().BeTrue();
        (await rig.NeedsRedoAsync()).Should().NotContain(c.Id);
    }

    /// <summary>已發布、已確認的對話離線（armed）時開始一輪：turn、meta(running)、一則內容都待送（合併前審查 P1、F1）</summary>
    private static async Task<(FakeConversation C, string Text)> OfflineMidTurnAsync(Rig rig)
    {
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "甲");
        await rig.SendAsync();
        await rig.GoOfflineAsync();

        var text = "執行中寫下的內容-" + Guid.NewGuid().ToString("N");
        c.Running = true;
        rig.Ui.Put(new TurnStartedChange(c.Id, false));
        rig.Meta(c);
        rig.Item(c, text);
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Should().Contain(r => r.State == OutboxState.Pending && Json(r).Contains(text), "前提：內容待送");
        return (c, text);
    }

    [Fact]
    public async Task 提醒的語音句子_完整同步送完整版_metadata送標題版_不念送空字串_沒帶就不帶()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var line = new VoiceLine("「標題」已完成：月報已經匯出", "「標題」已完成");
        var full = rig.New();
        var metadata = rig.New(policy: ConversationSyncPolicies.Metadata);
        var paused = rig.New(policy: ConversationSyncPolicies.Paused);
        var silent = rig.New();
        var none = rig.New();
        foreach (var c in new[] { full, metadata, paused, silent, none })
            rig.Meta(c);
        rig.Ui.Put(new AttentionChange(full.Id, ConversationAttentionKinds.Completed, null, line));
        rig.Ui.Put(new AttentionChange(metadata.Id, ConversationAttentionKinds.Completed, null, line));
        rig.Ui.Put(new AttentionChange(paused.Id, ConversationAttentionKinds.Completed, null, line));
        rig.Ui.Put(new AttentionChange(silent.Id, ConversationAttentionKinds.Completed, null, VoiceLine.Silent));
        rig.Ui.Put(new AttentionChange(none.Id, ConversationAttentionKinds.Completed, null));
        await rig.IdleAsync();

        (await SpeechAsync(rig, full)).Should().Be(line.Full);
        (await SpeechAsync(rig, metadata)).Should().Be(line.Head, "內容不上線：只送標題與事件");
        (await SpeechAsync(rig, paused)).Should().Be(line.Head, "權杖 DLP 命中：內容不上線");
        (await SpeechAsync(rig, silent)).Should().BeEmpty("背景工作還在跑：不念");
        (await AttentionRowAsync(rig, none))["speech"].Should().BeNull("沒帶句子：線上格式和以前相同");
    }

    [Fact]
    public async Task 離線時改成metadata_待送提醒不帶說明_前一輪寫的拿掉句子_同一輪的照新政策()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.GoOfflineAsync();
        var line = new VoiceLine("「標題」已完成：月報已經匯出", "「標題」已完成");
        var earlier = rig.New();
        var sameRound = rig.New();
        rig.Meta(earlier);
        rig.Meta(sameRound);
        rig.Ui.Put(new AttentionChange(earlier.Id, ConversationAttentionKinds.Completed, null, line));
        await rig.IdleAsync();
        (await SpeechAsync(rig, earlier)).Should().Be(line.Full, "完整同步時寫的：帶說明");

        // 前一輪寫進 outbox 的（資料庫裡的待送列）
        earlier.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(earlier);
        // 同一輪裡寫的（還在這一批）
        rig.Ui.Put(new AttentionChange(sameRound.Id, ConversationAttentionKinds.Completed, null, line));
        sameRound.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(sameRound);
        await rig.IdleAsync();

        var earlierRow = await AttentionRowAsync(rig, earlier);
        earlierRow["speech"].Should().BeNull("前一輪寫的待送列：說明可能帶對話內容，拿掉之後伺服器改組只有標題的句子");
        earlierRow["kind"]!.Value<string>().Should().Be(ConversationAttentionKinds.Completed, "提醒本身照送");
        // 同一輪裡寫的：和政策變更落在同一批時，投影當下已經是新政策（只送標題與事件）；落在前後兩批時，前一批的待送列拿掉句子。都不能是完整版
        (await SpeechAsync(rig, sameRound)).Should().BeOneOf(new string?[] { null, line.Head });
    }

    private static async Task<JObject> AttentionRowAsync(Rig rig, FakeConversation c)
        => Obj((await rig.RowsAsync(c)).Single(r => r.Kind == ConversationEventKinds.Attention));

    private static async Task<string?> SpeechAsync(Rig rig, FakeConversation c)
        => (await AttentionRowAsync(rig, c))["speech"]?.Value<string>();

    [Fact]
    public async Task 離線一輪執行中改成metadata_待送的內容不外送_不封鎖()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);

        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.IdleAsync();

        var pending = (await rig.RowsAsync(c)).Where(r => r.State == OutboxState.Pending).ToList();
        pending.Should().NotContain(r => Json(r).Contains(text), "降級之後還沒送出的內容換成 {}");
        pending.Last().Kind.Should().Be(ConversationEventKinds.Meta, "降級的 meta 換新世代，排在所有待送列之後（不原地取代前面那筆）");
        Obj(pending.Last())["syncPolicy"]!.Value<string>().Should().Be(ConversationSyncPolicies.Metadata);

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(text));
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        (await rig.RowAsync(c)).Blocked.Should().BeNull("換成 {} 的列被擋是重整、不是程式錯誤");
        rig.Model[c.Id]!.Policy.Should().Be(ConversationSyncPolicies.Metadata);
        rig.Model.Items(c.Id).Should().BeEmpty("伺服器降級時清掉內容，之後沒有內容再送上去");
        rig.Ui.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task 離線一輪執行中改成localOnly_待送的內容不外送_伺服器上刪除()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);

        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Meta(c);
        await rig.IdleAsync();

        var pending = (await rig.RowsAsync(c)).Where(r => r.State == OutboxState.Pending).ToList();
        pending.Should().NotContain(r => Json(r).Contains(text));
        pending.Last().Kind.Should().Be(ConversationEventKinds.Deleted);

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(text));
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        rig.Model[c.Id]!.Deleted.Should().BeTrue();
        (await rig.RowAsync(c)).Blocked.Should().Be(SyncEngine.BlockedLocalOnly);
        rig.Ui.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task 離線時項目待送_降級跑一輪再改回full_伺服器上項目齊全()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var a = rig.Item(c, "甲");
        await rig.SendAsync();
        await rig.GoOfflineAsync();

        var b = rig.Item(c, "離線時寫的乙");
        await rig.IdleAsync();
        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.IdleAsync();
        c.Running = true;
        rig.Ui.Put(new TurnStartedChange(c.Id, false));
        rig.Meta(c);
        var d = rig.Item(c, "只同步標題時的回覆");
        c.Running = false;
        rig.Ui.Put(new TurnCompletedChange(c.Id, AgentTurnOutcome.Completed, new AgentUsage(1, 2, 0, null)));
        rig.Meta(c);
        await rig.IdleAsync();
        c.Meta.SyncPolicy = null;
        rig.Meta(c);
        await rig.IdleAsync();

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        // 改回 full 時重送的乙不合併回降級之前那一列（它排在清掉內容的 meta 前面，會被伺服器清掉）
        rig.Model.Items(c.Id).Keys.Should().BeEquivalentTo([a.Id, b.Id, d.Id]);
        rig.Model[c.Id]!.Policy.Should().Be(ConversationSyncPolicies.Full);
        (await rig.RowAsync(c)).Blocked.Should().BeNull();
        rig.Ui.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task 改回full_向UI要佇列快照_非空的重送_空的不送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        var e = rig.New(minute: 1);
        rig.Meta(c);
        rig.Meta(e);
        c.Queue.Add((Guid.NewGuid().ToString("N"), "排隊的訊息"));
        rig.Ui.Put(new QueueChanged(c.Id, new PublishQueueSnapshot(c.Id, c.Queue.ToList())));
        await rig.SendAsync();
        rig.Sent(c).Should().ContainSingle(ev => ev.Kind == ConversationEventKinds.Queue);

        foreach (var x in new[] { c, e })
        {
            x.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
            rig.Meta(x);
        }

        await rig.SendAsync();
        foreach (var x in new[] { c, e })
        {
            x.Meta.SyncPolicy = null;
            rig.Meta(x);
        }

        await rig.IdleAsync();
        await rig.SendAsync();

        rig.Ui.Calls.Should().Contain($"RequestQueue:{c.Id}").And.Contain($"RequestQueue:{e.Id}");
        var queues = rig.Sent(c).Where(ev => ev.Kind == ConversationEventKinds.Queue).DistinctBy(ev => ev.DeviceSeq).ToList();
        queues.Should().HaveCount(2, "伺服器降級時清掉了佇列：改回 full 重送一次");
        Json(queues[1]).Should().Contain("排隊的訊息");
        rig.Model.Accepted.Should().Contain(ev => ev.DeviceSeq == queues[1].DeviceSeq);
        rig.Sent(e).Should().NotContain(ev => ev.Kind == ConversationEventKinds.Queue, "空的佇列不送");
    }

    // ───────────────────────────── 刪除（D6、D12） ─────────────────────────────

    [Fact]
    public async Task 對話刪除_送deleted_之後的變更丟掉()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "甲");
        await rig.SendAsync();

        rig.Ui.Put(new DeletingChange(c.Id));
        rig.Item(c, "刪除之後才到的");
        rig.Meta(c);
        await rig.IdleAsync();

        var rows = await rig.RowsAsync(c);
        rows.Last().Kind.Should().Be(ConversationEventKinds.Deleted);
        rows.Count(r => r.Kind == ConversationEventKinds.Item).Should().Be(1);
        (await rig.RowAsync(c)).DeletedAt.Should().NotBeNull();
        await rig.SendAsync();
        rig.Model[c.Id]!.Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task created還在待送時刪除_照順序送created再deleted()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        await rig.IdleAsync();
        rig.Ui.Put(new DeletingChange(c.Id));
        await rig.IdleAsync();

        var rows = await rig.RowsAsync(c);
        rows.Select(r => r.Kind).Should().Equal(ConversationEventKinds.Created, ConversationEventKinds.Deleted);
        rows.Should().OnlyContain(r => r.State == OutboxState.Pending);
        await rig.SendAsync();
        rig.Sent(c).Select(e => e.Kind).Should().Equal(ConversationEventKinds.Created, ConversationEventKinds.Deleted);
        rig.Model[c.Id]!.Deleted.Should().BeTrue();
    }

    // ───────────────────────────── 每一筆都合格 ─────────────────────────────

    [Fact]
    public async Task 每筆payload通過CheckEvent與Validate()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New(title: "標題含權杖 " + RuntimeToken(5));
        c.Meta.WorkingDirectory = @"C:\work\proj";
        c.Meta.Model = "opus";
        c.Running = true;
        rig.Ui.Put(new TurnStartedChange(c.Id, false));
        rig.Meta(c);
        rig.Item(c, "使用者說的", AgentItemKind.User);
        rig.Item(c, "**回覆**", AgentItemKind.Assistant);
        rig.Item(c, "想一想", AgentItemKind.Thinking);
        rig.Item(c, new AgentChatItem
        {
            Kind = AgentItemKind.Tool, ToolName = "編輯", ToolKind = "edit", FilePath = @"C:\work\a.cs", Output = "ok",
            Diff = [new AgentDiffLine("add", "+ x"), new AgentDiffLine("del", "- y")],
        });
        var question = rig.Item(c, new AgentChatItem
        {
            Kind = AgentItemKind.Question, Status = AgentItemStatus.Waiting, RequestKey = "rk-q",
            Questions = [new AgentQuestion { Id = "q1", Text = "要哪一個？", Options = [new AgentQuestionOption { Label = "甲" }] }],
        });
        rig.Item(c, new AgentChatItem { Kind = AgentItemKind.Approval, Status = AgentItemStatus.Waiting, ToolName = "bash", Detail = "ls", RequestKey = "rk-a" });
        rig.Item(c, new AgentChatItem { Kind = AgentItemKind.PlanApproval, Status = AgentItemStatus.Waiting, Text = "計畫", RequestKey = "rk-p" });
        rig.Item(c, new AgentChatItem { Kind = AgentItemKind.Plan, PlanSteps = [new AgentPlanStep { Text = "步驟一", Status = "completed" }] });
        rig.Item(c, "提示", AgentItemKind.Notice);
        var error = rig.Item(c, "錯誤", AgentItemKind.Error, AgentItemStatus.Failed);
        rig.Item(c, "執行中", AgentItemKind.Assistant, AgentItemStatus.Running);
        rig.Ui.Put(new QueueChanged(c.Id, new PublishQueueSnapshot(c.Id, [(Guid.NewGuid().ToString("N"), "排隊 " + RuntimeToken(6))])));
        rig.Ui.Put(new AttentionChange(c.Id, ConversationAttentionKinds.Question, question.Id));
        rig.Ui.Put(new ItemRemovedChange(c.Id, error.Id));
        c.Running = false;
        rig.Ui.Put(new TurnCompletedChange(c.Id, AgentTurnOutcome.Failed, new AgentUsage(1, 2, 0, null)));
        rig.Meta(c);
        // 刪除要另成一輪：conv.created 還沒寫過的對話在同一輪就被刪除，照設計什麼都不寫（見下一個測試）。不先等，盒子被取走幾次看兩條執行緒的先後，
        // 機器忙時常常一次全取，outbox 是空的（間歇失敗）
        await rig.IdleAsync();
        rig.Ui.Put(new DeletingChange(c.Id));
        await rig.IdleAsync();

        var rows = await rig.RowsAsync(c);
        rows.Select(r => r.Kind).Distinct().Should().BeEquivalentTo(ConversationEventKinds.DeviceAuthored, "裝置寫的每一種（conv.lease 只由伺服器寫）");
        foreach (var row in rows)
        {
            ConversationEventRules.CheckEvent(row.Kind, row.Stream, row.ConversationId, row.Epoch).Should().BeNull(row.Kind);
            ConversationEventRules.Validate(row.Kind, Json(row)).Should().BeNull(row.Kind);
            CredentialLeakRules.HasLeak(Json(row)).Should().BeFalse(row.Kind);
        }

        await rig.SendAsync();
        rig.Model.Rejected.Should().BeEmpty();
    }

    [Fact]
    public async Task 同一輪建立又刪除的對話_什麼都不寫()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New(title: "標題含權杖 " + RuntimeToken(7));
        var item = new AgentChatItem { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = "還沒發布就刪掉", Ord = c.NextOrd++, CreatedAt = T0, UpdatedAt = T0 };
        c.Items.Add(item);

        // 直接放進盒子、只喚醒一次：一定在同一輪被取走（rig.Ui.Put 每次盒子從空變非空就喚醒，引擎可能在中途取走一部分）
        rig.Box.Put(new TurnStartedChange(c.Id, false));
        rig.Box.Put(new MetaChanged(c.Id, c.Snapshot()));
        rig.Box.Put(new ItemChanged(c.Id, ChatItemCopyRules.Copy(item)));
        rig.Box.Put(new DeletingChange(c.Id));
        rig.H.Engine.Post(ConversationWake.Instance);
        await rig.IdleAsync();

        (await rig.RowsAsync(c)).Should().BeEmpty("伺服器上本來就沒有它，不必先建立再刪除（PublishPlanRules.Plan，計畫附錄 B 步 2）");
        await rig.SendAsync();
        rig.Sent(c).Should().BeEmpty();
        rig.Model[c.Id].Should().BeNull();
    }

    // ───────────────────────────── 第二輪合併前審查 ─────────────────────────────

    private static bool IsEmptyPayload(SyncEvent e) => Json(e) == SyncStore.EmptyConversationPayload;

    [Fact]
    public async Task 改成localOnly_旁檔寫不成功_待送的內容照樣不外送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);
        rig.Ui.ServerDeletedResult = _ => false;

        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Meta(c);
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Where(r => r.State == OutboxState.Pending).Should().OnlyContain(r => Json(r) == SyncStore.EmptyConversationPayload,
            "改成 localOnly 當下就不論種類換成 {}，不等旁檔寫好（第二輪合併前審查 PRIV-1）");

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(text));
        rig.Model[c.Id]!.Deleted.Should().BeFalse("前提：旁檔寫不成功，不送 conv.deleted");
        (await rig.NeedsRedoAsync()).Should().Contain(c.Id);
    }

    [Fact]
    public async Task 改成localOnly_寫旁檔的期間送出計時器先到_待送的內容不外送()
    {
        await using var rig = new Rig();
        rig.H.Server.Limits = SyncLimits.Default with { MaxBatchEvents = 1, MaxInFlightBatches = 1 };
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "甲");
        await rig.SendAsync(2);
        rig.H.Server.HoldAcks = true;
        rig.Item(c, "在途的乙");
        var text = "待送的內容-" + Guid.NewGuid().ToString("N");
        rig.Item(c, text);
        await rig.IdleAsync();
        await rig.SendAsync();
        (await rig.RowsAsync(c)).Should().Contain(r => r.State == OutboxState.Pending && Json(r).Contains(text), "前提：一批在途、內容待送");

        rig.Ui.HoldServerDeleted = true;
        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Meta(c);
        await rig.IdleAsync();
        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        await rig.IdleAsync();
        await rig.SendAsync(3);
        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text), "UI 寫旁檔的期間送出的批次不含內容（第二輪合併前審查 PRIV-1）");

        rig.Ui.ReleaseServerDeleted();
        await rig.IdleAsync();
        await rig.SendAsync(3);
        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        rig.Model[c.Id]!.Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task 離線新建_還沒送出就改localOnly_標題與內容都不外送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.GoOfflineAsync();
        var title = "第一則訊息的開頭-" + Guid.NewGuid().ToString("N");
        var c = rig.New(title: title);
        rig.Meta(c);
        var text = "內容-" + Guid.NewGuid().ToString("N");
        rig.Item(c, text);
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Should().Contain(r => r.Kind == ConversationEventKinds.Created && r.State == OutboxState.Pending && Json(r).Contains(title), "前提");

        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Sent(c).Should().NotBeEmpty().And.OnlyContain(e => IsEmptyPayload(e), "localOnly 什麼都不發：待送的 created、item 都換成 {}，接著 conv.deleted（第二輪合併前審查 PRIV-2）");
        rig.Sent(c).Last().Kind.Should().Be(ConversationEventKinds.Deleted);
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(title) || Json(e).Contains(text));
        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(title) || r.Payload.Contains(text));
        (await rig.RowAsync(c)).Blocked.Should().Be(SyncEngine.BlockedLocalOnly, "conv.deleted 對未知的對話回 invalid 算成功");
        rig.Ui.Notices.Should().BeEmpty();
    }

    /// <summary>以一次一筆、確認扣住的方式連上（Draining 期間有待送的列；<paramref name="newInstance"/>＝伺服器換了實例）</summary>
    private static async Task DrainOneAtATimeAsync(Rig rig, bool newInstance)
    {
        if (newInstance)
            rig.H.Server.Instance = new ServerInstance("instance-2", 0);
        rig.H.Server.Limits = SyncLimits.Default with { MaxBatchEvents = 1, MaxInFlightBatches = 1 };
        rig.H.Server.HoldAcks = true;
        rig.H.Server.ConnectFailure = null;
        rig.H.Engine.RetryNow();
        await rig.IdleAsync();
        await rig.SendAsync(2);
    }

    [Fact]
    public async Task 換實例後Draining中改成metadata_換實例之前寫的待送內容不外送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);
        await DrainOneAtATimeAsync(rig, newInstance: true);
        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text), "前提：內容還沒送");
        (await rig.RowAsync(c)).CreatedWritten.Should().BeFalse("前提：換實例重設了發布狀態");

        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Should().NotContain(r => r.State == OutboxState.Pending && Json(r).Contains(text), "降級走 MetaOnly、待送的換成 {}（第二輪合併前審查 PRIV-3）");

        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        await rig.IdleAsync();
        await rig.SendAsync(10);
        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(text));
    }

    [Fact]
    public async Task 降級那一輪寫入失敗一次_待送的內容不外送_之後照新政策重送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);
        var failed = 0;
        await rig.H.Inspect(v => v.Store!.FaultInjection = point =>
        {
            if (point == "AppendMany:commit" && Interlocked.Increment(ref failed) == 1)
                throw new SqliteException("模擬：寫入失敗", 10);
        });

        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.IdleAsync();
        failed.Should().BeGreaterThan(0, "前提：降級那一輪的寫入失敗了");
        (await rig.RowsAsync(c)).Should().NotContain(r => r.State == OutboxState.Pending && Json(r).Contains(text), "寫入失敗之後馬上單獨補寫清除（第二輪合併前審查 PRIV-4）");
        await rig.H.Inspect(v => v.Store!.FaultInjection = null);

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        rig.Model[c.Id]!.Policy.Should().Be(ConversationSyncPolicies.Metadata, "重整照現在的政策重送降級的 meta");
        (await rig.RowAsync(c)).Policy.Should().Be(ConversationSyncPolicies.Metadata);
    }

    [Fact]
    public async Task 降級的清除一直寫不成_一批都不送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);
        await rig.H.Inspect(v => v.Store!.FaultInjection = point =>
        {
            if (point is "AppendMany:commit" or "WritePublish:commit")
                throw new SqliteException("模擬：寫入一直失敗", 10);
        });

        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.IdleAsync();
        var batches = rig.H.Server.Batches.Count;
        rig.H.Server.ConnectFailure = null;
        rig.H.Engine.RetryNow();
        await rig.IdleAsync();
        await rig.SendAsync(3);

        rig.H.Server.Hellos.Count.Should().BeGreaterThan(1, "前提：有重新連上");
        rig.H.Server.Batches.Count.Should().Be(batches, "降級的清除補寫不成：一批都不送（fail-closed，第二輪合併前審查 PRIV-4）");
        (await rig.H.StateAsync()).Should().NotBe(SyncEngineState.Live);

        await rig.H.Inspect(v => v.Store!.FaultInjection = null);
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);
        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        rig.Model[c.Id]!.Policy.Should().Be(ConversationSyncPolicies.Metadata);
    }

    [Fact]
    public async Task 重開時政策已改成metadata_沒收到MetaChanged_待送的內容不外送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);
        // 改政策的 MetaChanged 還在發布者的計時器裡就結束了：引擎不知道（第二輪合併前審查 F1）
        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        await rig.H.RestartAsync();
        (await rig.RowsAsync(c)).Should().Contain(r => r.State == OutboxState.Pending && Json(r).Contains(text), "前提：上一次執行的待送列還在");

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Ui.Calls.Should().Contain($"RequestMeta:{c.Id}", "送出之前先向中樞要它現在的政策");
        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        rig.Model[c.Id]!.Policy.Should().Be(ConversationSyncPolicies.Metadata);
        rig.Model.Items(c.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task 重開時已改成localOnly與新標題_沒收到MetaChanged_什麼都不外送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);
        var title = "新標題-" + Guid.NewGuid().ToString("N");
        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        c.Meta.Title = title;
        await rig.H.RestartAsync();
        var mark = rig.Sent(c).Count;

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        var sent = rig.Sent(c).Skip(mark).ToList();
        sent.Should().NotBeEmpty().And.OnlyContain(e => IsEmptyPayload(e), "上一次執行留下的待送列一律換成 {}，接著 conv.deleted");
        sent.Last().Kind.Should().Be(ConversationEventKinds.Deleted);
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text) || Json(e).Contains(title));
        rig.Model[c.Id]!.Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task 重開時中樞已沒有這個對話_待送的換成空物件_照常進Live()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);
        rig.Ui.Remove(c.Id);
        await rig.H.RestartAsync();
        var mark = rig.Sent(c).Count;

        // ReconnectAsync 斷言進 Live：閘門不會一直擋著
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        var sent = rig.Sent(c).Skip(mark).ToList();
        sent.Should().NotBeEmpty().And.OnlyContain(e => IsEmptyPayload(e), "政策不明：待送的一律換成 {}（fail-closed）");
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Rejected.Should().NotContain(r => r.Payload.Contains(text));
    }

    [Fact]
    public async Task created的確認沒收到_之後被接受的事件也算確認_不重送created()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.H.Server.HoldAcks = true;
        var c = rig.New();
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync();
        rig.Model[c.Id].Should().NotBeNull("前提：伺服器套用了 created");
        rig.H.Server.Current!.DiscardAcks();
        rig.H.Server.HoldAcks = false;
        await rig.GoOfflineAsync();
        c.Meta.Title = "離線時改的標題";
        rig.Meta(c);
        await rig.IdleAsync();

        // 重連：Rewind 把 created 標成已確認、沒有結果；之後被接受的 meta 等同 created 被確認（第二輪合併前審查 F2）
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        (await rig.RowAsync(c)).CreatedAcked.Should().BeTrue();
        rig.Sent(c).Count(e => e.Kind == ConversationEventKinds.Created).Should().Be(1, "被接受的 meta 已經確認了它：不必補送 created");
        rig.Model.Accepted.Should().Contain(e => e.Kind == ConversationEventKinds.Meta && Json(e).Contains("離線時改的標題"));
    }

    [Fact]
    public async Task created的確認沒收到_閒置的對話上線時補一次created_即時的路徑恢復()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.H.Server.HoldAcks = true;
        var c = rig.New();
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.SendAsync();
        rig.H.Server.Current!.DiscardAcks();
        rig.H.Server.HoldAcks = false;
        await rig.GoOfflineAsync();

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);
        (await rig.RowAsync(c)).CreatedAcked.Should().BeTrue("上線時補送 created，伺服器照 ReplaceMeta 接受（第二輪合併前審查 F2）");
        rig.Sent(c).Count(e => e.Kind == ConversationEventKinds.Created).Should().Be(2);

        // 沒有 Ord 的新項目要整份走：created 確認了才走
        var item = rig.Item(c, "沒有 Ord 的新一則", ord: false);
        await rig.IdleAsync();
        await rig.SendAsync(2);
        rig.Model.Items(c.Id).Keys.Should().Contain(item.Id);
        (await rig.NeedsRedoAsync()).Should().NotContain(c.Id);
    }

    [Fact]
    public async Task 沒有conv1的期間清空佇列_回到conv1之後伺服器上的佇列也清掉()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        c.Queue.Add((Guid.NewGuid().ToString("N"), "排隊的訊息"));
        rig.Ui.Put(new QueueChanged(c.Id, new PublishQueueSnapshot(c.Id, c.Queue.ToList())));
        await rig.SendAsync();
        rig.Sent(c).Should().ContainSingle(e => e.Kind == ConversationEventKinds.Queue, "前提");

        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();
        c.Queue.Clear();
        rig.Ui.Put(new QueueChanged(c.Id, new PublishQueueSnapshot(c.Id, [])));
        await rig.IdleAsync();
        rig.Sent(c).Count(e => e.Kind == ConversationEventKinds.Queue).Should().Be(1, "沒有 conv1：佇列的變化不送");

        rig.H.Server.Features = BothFeatures;
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        var last = rig.Sent(c).Last(e => e.Kind == ConversationEventKinds.Queue);
        JObject.Parse(Json(last))["entries"]!.Should().BeEmpty("重整（Redo）也向 UI 要佇列，空的也送、蓋掉伺服器上的舊佇列（第二輪合併前審查 F3）");
        rig.Model.Accepted.Should().Contain(e => e.DeviceSeq == last.DeviceSeq);
    }

    [Fact]
    public async Task 換實例之後重送_本機佇列是空的_送一次空的佇列()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "甲");
        c.Queue.Add((Guid.NewGuid().ToString("N"), "排隊的訊息"));
        rig.Ui.Put(new QueueChanged(c.Id, new PublishQueueSnapshot(c.Id, c.Queue.ToList())));
        await rig.SendAsync();
        // 之後本機的佇列空了；伺服器從備份還原（還留著那份佇列）
        c.Queue.Clear();
        var mark = rig.Sent(c).Count;
        rig.H.Server.Instance = new ServerInstance("instance-2", 0);
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        var queues = rig.Sent(c).Skip(mark).Where(e => e.Kind == ConversationEventKinds.Queue).ToList();
        queues.Should().NotBeEmpty("換實例之後伺服器上的佇列不明：空的也送一次（第二輪合併前審查 F3）");
        var last = queues[^1];
        JObject.Parse(Json(last))["entries"]!.Should().BeEmpty();
        rig.Model.Accepted.Should().Contain(e => e.DeviceSeq == last.DeviceSeq);
    }

    /// <summary>發布過兩則之後項目檔損毀（中樞的清單只剩錯誤項目、之後一直有 items.json.corrupt-*），之後又加了兩則（都已發布）</summary>
    private static async Task<(FakeConversation C, AgentChatItem[] Lost, AgentChatItem Error, AgentChatItem Kept, AgentChatItem Later)> CorruptedOnceAsync(Rig rig)
    {
        var c = rig.New();
        rig.Meta(c);
        var lost = new[] { rig.Item(c, "損毀前的甲"), rig.Item(c, "損毀前的乙") };
        await rig.SendAsync();
        c.Items.Clear();
        c.RemovalsTrusted = false;
        var error = rig.Item(c, "對話紀錄損毀，已備份", AgentItemKind.Error, AgentItemStatus.Failed);
        var kept = rig.Item(c, "損毀之後的一則");
        var later = rig.Item(c, "之後會被移除的暫時項目");
        await rig.SendAsync();
        rig.Model.Items(c.Id).Keys.Should().Contain([.. lost.Select(i => i.Id), error.Id, kept.Id, later.Id], "前提");
        return (c, lost, error, kept, later);
    }

    [Fact]
    public async Task 項目檔損毀過_沒有conv1的期間移除_回到conv1只推斷損毀之後的項目的刪除()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, lost, error, kept, later) = await CorruptedOnceAsync(rig);

        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();
        c.Items.Remove(later);
        rig.Ui.Put(new ItemRemovedChange(c.Id, later.Id));
        await rig.IdleAsync();

        rig.H.Server.Features = BothFeatures;
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        var items = rig.Model.Items(c.Id).Keys.ToList();
        items.Should().NotContain(later.Id, "Ord 比目前清單裡最小的大：損毀之後才有的，照推斷送 itemRemoved（第二輪合併前審查 F2-untrusted）");
        items.Should().Contain(lost.Select(i => i.Id), "損毀之前的（可能是損毀時遺失的）不推斷刪除");
        items.Should().Contain([error.Id, kept.Id]);
    }

    [Fact]
    public async Task 項目檔損毀過_itemRemoved換成空物件被擋下_重整重送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, lost, _, _, later) = await CorruptedOnceAsync(rig);
        await rig.GoOfflineAsync();
        c.Items.Remove(later);
        rig.Ui.Put(new ItemRemovedChange(c.Id, later.Id));
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Should().Contain(r => r.Kind == ConversationEventKinds.ItemRemoved && r.State == OutboxState.Pending, "前提：離線（armed）照寫");

        // 連上沒有 conv1 的伺服器：換成 {} 被擋下（F4：已送 itemRemoved 歸 0、記重整）
        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();
        rig.Model.Rejected.Should().Contain(r => r.Kind == ConversationEventKinds.ItemRemoved && r.Payload == SyncStore.EmptyConversationPayload);

        rig.H.Server.Features = BothFeatures;
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Model.Items(c.Id).Keys.Should().NotContain(later.Id).And.Contain(lost.Select(i => i.Id));
    }

    // ───────────────────────────── 第三輪合併前審查 ─────────────────────────────

    /// <summary>created 與「甲」同一批送出、伺服器套用了，確認卻沒收到（之後 created_acked 一直是 0）</summary>
    private static async Task<(FakeConversation C, AgentChatItem First)> CreatedAckLostAsync(Rig rig)
    {
        rig.H.Server.HoldAcks = true;
        var c = rig.New();
        rig.Meta(c);
        var first = rig.Item(c, "甲");
        await rig.IdleAsync();
        await rig.SendAsync();
        rig.Model.Items(c.Id).Keys.Should().Contain(first.Id, "前提：伺服器套用了 created 與甲");
        rig.H.Server.Current!.DiscardAcks();
        rig.H.Server.HoldAcks = false;
        return (c, first);
    }

    /// <summary>（回到 conv1、已經是 metadata 之後）伺服器清掉了內容、本機的摘要也清掉；改回 full 之後甲整份重送</summary>
    private static async Task AssertMetaOnlyThenRepublishAsync(Rig rig, FakeConversation c, AgentChatItem first)
    {
        rig.Model[c.Id]!.Policy.Should().Be(ConversationSyncPolicies.Metadata);
        rig.Model.Items(c.Id).Should().BeEmpty("伺服器降級時清掉了內容");
        (await rig.RowAsync(c)).Policy.Should().Be(ConversationSyncPolicies.Metadata);
        (await rig.PublishedAsync(c)).Should().NotBeEmpty().And.OnlyContain(r => r.SrcSha256 == null && r.WireSha256 == null,
            "補 created 之前照政策轉換走 MetaOnly：伺服器清掉了內容，本機的摘要也要清掉（第三輪合併前審查 LIVE-1）");

        c.Meta.SyncPolicy = ConversationSyncPolicies.Full;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(10);

        rig.Model[c.Id]!.Policy.Should().Be(ConversationSyncPolicies.Full);
        rig.Model.Items(c.Id).Keys.Should().Contain(first.Id, "改回 full 整份重送");
    }

    [Fact]
    public async Task created的確認沒收到_沒有conv1的期間改成metadata_回到conv1之後改回full_內容重送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, first) = await CreatedAckLostAsync(rig);

        // 伺服器回滾到沒有 conv1 的版本：Prove 把那一批標成已提交、沒有結果
        rig.H.Server.Features = null;
        rig.Model.OldServer = true;
        await rig.ReconnectAsync();
        (await rig.RowAsync(c)).CreatedAcked.Should().BeFalse("前提");
        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(c);
        await rig.IdleAsync();

        rig.H.Server.Features = BothFeatures;
        rig.Model.OldServer = false;
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        await AssertMetaOnlyThenRepublishAsync(rig, c, first);
    }

    [Fact]
    public async Task created的確認沒收到_重開時政策已改成metadata_之後改回full_內容重送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, first) = await CreatedAckLostAsync(rig);
        await rig.GoOfflineAsync();
        // 改政策的 MetaChanged 還在發布者的計時器裡就結束了（第二輪 F1 的情境）
        c.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        await rig.H.RestartAsync();

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        await AssertMetaOnlyThenRepublishAsync(rig, c, first);
    }

    [Fact]
    public async Task 重開時中樞暫時沒有這個對話_之後回來_待送的內容補送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (c, text) = await OfflineMidTurnAsync(rig);
        rig.Ui.Remove(c.Id);
        await rig.H.RestartAsync();
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text), "前提：政策不明，待送的換成 {}");

        // 中樞又讀得到它（例如上次 meta.json 被別的程式占用）
        rig.Ui.Add(c);
        await rig.H.RestartAsync();
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(10);

        rig.Model.Accepted.Should().Contain(e => Json(e).Contains(text), "換成 {} 的內容不能記成已送：摘要清掉，重整照摘要重送（第三輪合併前審查 PRIV-R3-2）");
    }

    [Fact]
    public async Task 重開時中樞暫時沒有這個對話_離線刪掉的項目_之後回來伺服器上也刪掉()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        var kept = rig.Item(c, "留著的甲");
        var removed = rig.Item(c, "會被刪掉的乙");
        await rig.SendAsync();
        await rig.GoOfflineAsync();
        c.Items.Remove(removed);
        rig.Ui.Put(new ItemRemovedChange(c.Id, removed.Id));
        await rig.IdleAsync();
        (await rig.RowsAsync(c)).Should().Contain(r => r.Kind == ConversationEventKinds.ItemRemoved && r.State == OutboxState.Pending, "前提：刪除待送");

        rig.Ui.Remove(c.Id);
        await rig.H.RestartAsync();
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);
        rig.Model.Items(c.Id).Keys.Should().Contain(removed.Id, "前提：政策不明，刪除換成 {}、伺服器上還在");

        rig.Ui.Add(c);
        await rig.H.RestartAsync();
        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(10);

        rig.Model.Items(c.Id).Keys.Should().BeEquivalentTo(new[] { kept.Id }, "換成 {} 的刪除不能記成已送：已送 itemRemoved 歸 0，重整照推斷補送（第三輪合併前審查 PRIV-R3-1）");
    }

    /// <summary>（啟動）已確認的 full 對話：一則被伺服器以 invalid 擋下（認得的對話＝程式錯誤、封鎖），後面那則內容還在待送</summary>
    private static async Task<(FakeConversation C, string Text)> BugBlockedWithPendingAsync(Rig rig)
    {
        rig.H.Server.Limits = SyncLimits.Default with { MaxBatchEvents = 1, MaxInFlightBatches = 1 };
        await rig.StartAsync();
        var c = rig.New();
        rig.Meta(c);
        rig.Item(c, "甲");
        await rig.SendAsync(2);
        (await rig.RowAsync(c)).CreatedAcked.Should().BeTrue("前提");

        var bad = "伺服器擋下的-" + Guid.NewGuid().ToString("N");
        rig.Model.Reject = e => e.Kind == ConversationEventKinds.Item && Json(e).Contains(bad) ? SyncErrorCodes.Invalid : null;
        rig.H.Server.HoldAcks = true;
        rig.Item(c, bad);
        var text = "封鎖之後待送的內容-" + Guid.NewGuid().ToString("N");
        rig.Item(c, text);
        await rig.IdleAsync();
        await rig.SendAsync();
        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        await rig.IdleAsync();

        (await rig.RowAsync(c)).Blocked.Should().Be(SyncEngine.BlockedBugPrefix + rig.H.AppVersion, "前提：認得的對話還被擋＝程式錯誤、封鎖");
        (await rig.RowsAsync(c)).Should().Contain(r => r.State == OutboxState.Pending && Json(r).Contains(text), "前提：封鎖之前寫的那則還在待送");
        return (c, text);
    }

    [Theory]
    [InlineData(ConversationSyncPolicies.LocalOnly)]
    [InlineData(ConversationSyncPolicies.Metadata)]
    public async Task 程式錯誤封鎖的對話降級_封鎖之前待送的內容不外送(string policy)
    {
        await using var rig = new Rig();
        var (c, text) = await BugBlockedWithPendingAsync(rig);

        c.Meta.SyncPolicy = policy;
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(5);

        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text), "封鎖中的對話降級：之前待送的照樣換成 {}（第三輪合併前審查 PRIV-R3-3）");
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
        (await rig.RowAsync(c)).Blocked.Should().Be(SyncEngine.BlockedBugPrefix + rig.H.AppVersion, "封鎖照舊（解除之後照轉換走）");
        (await rig.RowAsync(c)).Policy.Should().Be(ConversationSyncPolicies.Full, "發布狀態的政策不改：解除封鎖之後的轉換照舊走墓碑或 MetaOnly");
    }

    [Fact]
    public async Task 程式錯誤封鎖的對話_重開時已改成localOnly_沒收到MetaChanged_待送的內容不外送()
    {
        await using var rig = new Rig();
        var (c, text) = await BugBlockedWithPendingAsync(rig);
        await rig.GoOfflineAsync();
        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        await rig.H.RestartAsync();

        await rig.ReconnectAsync();
        await rig.H.AdvanceAsync(3);

        rig.Ui.Calls.Should().Contain($"RequestMeta:{c.Id}", "封鎖中的對話照樣先要中繼資料才送（第三輪合併前審查 PRIV-R3-3）");
        rig.Sent(c).Should().NotContain(e => Json(e).Contains(text));
        rig.Model.Accepted.Should().NotContain(e => Json(e).Contains(text));
    }
}
