using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 被觀看對話的即時更新與執行中項目的持久時機（WP4.3 計畫步 11，D19、D21）：沒人看、沒有 live1 不送；伺服器列出的 id 一律以本機政策再過濾
/// （被入侵的伺服器列出 localOnly 的 id 也拿不到內容）；每個對話 ≤4 Hz、留最新的；離開 Running 立刻持久；超過 64 KB 不送；10 秒的檢查點；
/// 開始被看時立刻送；連線丟掉清空觀看集合；閒置時沒有計時器；推給 UI 的觀看集合只含 created 已確認、沒有封鎖的。
/// UI 端是 <see cref="FakeConversationUi"/>，伺服器是照 WP4.2 定案模擬的 <see cref="FakeConversations"/>；時間一律以假時鐘撥
/// </summary>
public sealed class ConversationLiveTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Local);

    private static readonly string[] BothFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];

    /// <summary>引擎＋盒子＋假 UI＋假伺服器；引擎每次 PostLive（OnLiveRelay 的登記點）都記在 <see cref="Posted"/></summary>
    private sealed class Rig : IAsyncDisposable
    {
        public Rig(IReadOnlyList<string>? features = null)
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box);
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            H.Server.Features = features ?? BothFeatures;
            _ = H.Server.Conversations;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        public FakeConversations Model => H.Server.Conversations;

        /// <summary>引擎交給 OnLiveRelay 的每一則（＝PostLive 的呼叫，不論之後送不送）</summary>
        public ConcurrentQueue<LiveRelay> Posted { get; } = new();

        public Task StartAsync() => H.StartAsync(engine => engine.ContractProbeForTest = (hook, argument) =>
        {
            if (hook == "OnLiveRelay" && argument is LiveRelay relay)
                Posted.Enqueue(relay);
        });

        public Task IdleAsync() => H.IdleAsync();

        public Task AdvanceMsAsync(int ms) => H.AdvanceAsync(TimeSpan.FromMilliseconds(ms));

        public FakeConversation New(AgentProvider provider = AgentProvider.Claude, string? policy = null)
            => Ui.Add(new FakeConversation(new AgentConversation
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "即時更新的對話",
                AccountId = EngineHarness.LocalAccount,
                Provider = provider,
                SyncPolicy = policy,
                CreatedAt = T0,
                UpdatedAt = T0,
            }));

        public void Meta(FakeConversation c) => Ui.Put(new MetaChanged(c.Id, c.Snapshot()));

        /// <summary>新的對話：送出 created、確認</summary>
        public async Task<FakeConversation> KnownAsync(string? policy = null)
        {
            var c = New(policy: policy);
            Meta(c);
            await IdleAsync();
            await H.AdvanceAsync(1);
            (await H.Inspect(v => v.Store!.LoadPublishMeta()))[c.Id].CreatedAcked.Should().BeTrue("前提：created 已確認");
            return c;
        }

        public AgentChatItem Item(FakeConversation c, string text, AgentItemStatus status = AgentItemStatus.Running)
        {
            var item = new AgentChatItem { Kind = AgentItemKind.Assistant, Status = status, Text = text, CreatedAt = T0, UpdatedAt = T0, Ord = c.NextOrd++ };
            c.Items.Add(item);
            Changed(c, item);
            return item;
        }

        public void Changed(FakeConversation c, AgentChatItem item) => Ui.Put(new ItemChanged(c.Id, ChatItemCopyRules.Copy(item)));

        public void Change(FakeConversation c, AgentChatItem item, string text)
        {
            item.Text = text;
            Changed(c, item);
        }

        public async Task WatchAsync(params string[] ids)
        {
            H.Server.Current!.Watch(ids);
            await IdleAsync();
        }

        public IReadOnlyList<LiveRelay> Relays(FakeConversation c) => H.Server.LiveRelays.Where(r => r.ConversationId == c.Id).ToList();

        public IReadOnlyList<LiveRelay> PostedFor(string id) => Posted.Where(r => r.ConversationId == id).ToList();

        public IReadOnlySet<string> LastWatched => Ui.Watched.LastOrDefault() ?? new HashSet<string>();

        /// <summary>這個項目寫過幾筆持久的 conv.item（送出過的＋還在 outbox 待送的）</summary>
        public async Task<int> DurableCountAsync(FakeConversation c, AgentChatItem item)
        {
            var seqs = new HashSet<long>(H.Server.Batches.SelectMany(b => b.Events)
                .Where(e => e.ConversationId == c.Id && e.Kind == ConversationEventKinds.Item && Obj(e)["id"]!.Value<string>() == item.Id)
                .Select(e => e.DeviceSeq));
            foreach (var row in await PendingAsync(c, item))
                seqs.Add(row.DeviceSeq);
            return seqs.Count;
        }

        /// <summary>這個項目在 outbox 還沒送出的 conv.item 列</summary>
        public async Task<IReadOnlyList<OutboxEntry>> PendingAsync(FakeConversation c, AgentChatItem item)
        {
            var key = PublishKeyRules.Item(c.Id, item.Id);
            return (await H.Inspect(v => v.Outbox)).Where(e => e.State == OutboxState.Pending && e.CoalesceKey == key).ToList();
        }

        /// <summary>這個項目最後一筆持久版本（待送的優先，否則送出過的最後一筆）</summary>
        public async Task<JObject> LastDurableAsync(FakeConversation c, AgentChatItem item)
        {
            if ((await PendingAsync(c, item)).LastOrDefault() is { } pending)
                return Obj(pending);
            return H.Server.Batches.SelectMany(b => b.Events)
                .Where(e => e.ConversationId == c.Id && e.Kind == ConversationEventKinds.Item && Obj(e)["id"]!.Value<string>() == item.Id)
                .OrderBy(e => e.DeviceSeq).Select(Obj).Last();
        }

        public async Task GoOfflineAsync()
        {
            H.Server.ConnectFailure = _ => EngineHarness.Transient();
            H.Server.Current!.Drop();
            await IdleAsync();
        }

        public async Task ReconnectAsync()
        {
            H.Server.ConnectFailure = null;
            H.Engine.RetryNow();
            await IdleAsync();
            await H.AdvanceAsync(2);
            (await H.StateAsync()).Should().Be(SyncEngineState.Live);
        }

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static JObject Obj(SyncEvent e) => JObject.Parse(PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開"));

    private static JObject Obj(OutboxEntry e) => JObject.Parse(PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開"));

    private static string Text(LiveRelay relay) => JObject.Parse(relay.Item)["text"]!.Value<string>()!;

    /// <summary>外來的一則合格的即時更新（模擬 UI 端或其他程式直接呼叫 PostLive）</summary>
    private static LiveRelay Foreign(string conversationId, long ord = 1, long ver = 1_000)
    {
        var item = new AgentChatItem { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = "外來的內容", CreatedAt = T0, UpdatedAt = T0, Ord = ord };
        ConversationWire.TryItem(item, ver, out var wire).Should().BeTrue();
        return new LiveRelay(conversationId, 1, ord, ver, ConversationWire.Serialize(wire!));
    }

    /// <summary>執行時組出像真憑證的字串（固定種子；repo 裡沒有這種字串）</summary>
    private static string RuntimeToken(int seed)
    {
        var random = new Random(seed);
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return "sk-" + "ant-" + "api03-" + new string(Enumerable.Range(0, 40).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    // ───────────────────────────── 送不送 ─────────────────────────────

    [Fact]
    public async Task 沒人看_不送即時()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();

        var item = rig.Item(c, "片段一");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(300);
        rig.Change(c, item, "片段一二");
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(1);

        rig.Posted.Should().BeEmpty("沒有人看的對話不投影即時更新");
        rig.H.Server.LiveRelays.Should().BeEmpty();
        (await rig.DurableCountAsync(c, item)).Should().Be(1, "第一次 Running 持久一次，之後等檢查點");
    }

    [Fact]
    public async Task 沒協商live1_不送()
    {
        await using var rig = new Rig([DeviceFeatureRules.Conv1]);
        await rig.StartAsync();
        (await rig.H.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Conv1);
        var c = await rig.KnownAsync();

        // 假伺服器不看協商：照樣送 WatchChanged
        await rig.WatchAsync(c.Id);
        var item = rig.Item(c, "片段");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(300);
        rig.Change(c, item, "片段二");
        await rig.IdleAsync();
        rig.H.Engine.PostLive(Foreign(c.Id));
        await rig.H.AdvanceAsync(1);

        (await rig.H.Inspect(v => v.ServerWatch)).Should().BeEmpty("沒有 live1 的連線收不到觀看清單");
        rig.PostedFor(c.Id).Should().OnlyContain(r => r.Ver == 1_000, "引擎自己不投影即時更新（只有外來的那一則）");
        rig.H.Server.LiveRelays.Should().BeEmpty("外來的一則也丟掉");
        rig.Ui.Watched.Should().BeEmpty();
        (await rig.DurableCountAsync(c, item)).Should().Be(1, "持久的照常");
    }

    [Fact]
    public async Task 伺服器watch一個localOnly的id_沒有任何PostLive()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var ok = await rig.KnownAsync();
        var localOnly = rig.New(policy: ConversationSyncPolicies.LocalOnly);
        rig.Meta(localOnly);
        var antigravity = rig.New(provider: AgentProvider.Antigravity);
        rig.Meta(antigravity);
        var draft = rig.New();
        draft.IsDraft = true;
        rig.Meta(draft);
        var replica = rig.New();
        replica.IsReplica = true;
        rig.Meta(replica);
        // 上傳過、確認過，之後改成只同步標題
        var metadata = await rig.KnownAsync();
        metadata.Meta.SyncPolicy = ConversationSyncPolicies.Metadata;
        rig.Meta(metadata);
        // 上傳過、確認過，之後被伺服器以 not_owner 封鎖
        var blocked = await rig.KnownAsync();
        rig.Model.Reject = e => e.ConversationId == blocked.Id ? ConversationRejectCodes.NotOwner : null;
        rig.Item(blocked, "封鎖前的最後一筆", AgentItemStatus.Done);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(1);
        rig.Model.Reject = null;
        (await rig.H.Inspect(v => v.Store!.LoadPublishMeta()))[blocked.Id].Blocked.Should().Be(ConversationRejectCodes.NotOwner);
        var unknown = Guid.NewGuid().ToString("N");
        var bad = new[] { localOnly.Id, antigravity.Id, draft.Id, replica.Id, metadata.Id, blocked.Id, unknown };

        await rig.WatchAsync([ok.Id, .. bad]);
        foreach (var c in new[] { ok, localOnly, antigravity, draft, replica, metadata, blocked })
            rig.Item(c, "執行中的內容");
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(1);

        rig.Posted.Select(r => r.ConversationId).Should().OnlyContain(id => id == ok.Id, "只有本機政策允許的對話投影即時更新");
        rig.Relays(ok).Should().ContainSingle();
        rig.LastWatched.Should().BeEquivalentTo([ok.Id], "推給 UI 的觀看集合以本機政策過濾（IsSeenRemotely 壓不下這些對話的提醒）");
        rig.Ui.Watched.Should().OnlyContain(set => !set.Overlaps(bad));

        // 外來的 PostLive（UI 端或其他程式直接呼叫）也一樣以本機政策擋下
        foreach (var id in bad)
            rig.H.Engine.PostLive(Foreign(id));
        rig.H.Engine.PostLive(Foreign(ok.Id, ord: 1, ver: 5_000));
        await rig.IdleAsync();
        rig.H.Server.LiveRelays.Select(r => r.ConversationId).Should().OnlyContain(id => id == ok.Id);
        rig.H.Server.LiveRelays.Should().Contain(r => r.Ver == 5_000, "合格的外來一則照送（對照組）");
    }

    [Fact]
    public async Task 有人看_第一則立即_之後最多4Hz_留最新的()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);

        var item = rig.Item(c, "一");
        await rig.IdleAsync();
        rig.Relays(c).Should().ContainSingle("第一則立即送").Which.Ord.Should().Be(item.Ord!.Value);
        Text(rig.Relays(c)[0]).Should().Be("一");

        await rig.AdvanceMsAsync(100);
        rig.Change(c, item, "一二");
        await rig.IdleAsync();
        rig.Relays(c).Should().HaveCount(1, "100 ms：還不到 4 Hz 的間隔");
        await rig.AdvanceMsAsync(100);
        rig.Change(c, item, "一二三");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(49);
        rig.Relays(c).Should().HaveCount(1, "249 ms：還不到");

        await rig.AdvanceMsAsync(1);
        rig.Relays(c).Should().HaveCount(2, "250 ms：送被擋下的最新版本");
        Text(rig.Relays(c)[1]).Should().Be("一二三", "留最新的");
        rig.Relays(c)[1].Ver.Should().BeGreaterThan(rig.Relays(c)[0].Ver);

        await rig.H.AdvanceAsync(2);
        rig.Relays(c).Should().HaveCount(2, "沒有新的變化就不再送");
    }

    [Fact]
    public async Task 離開Running_立即持久_不再送即時()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);
        var item = rig.Item(c, "跑");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(100);
        rig.Change(c, item, "跑到一半");
        await rig.IdleAsync();
        rig.Relays(c).Should().HaveCount(1);
        (await rig.H.Inspect(v => v.HasRunningTimer)).Should().BeTrue("被頻率擋下的等 250 ms");

        item.Status = AgentItemStatus.Done;
        rig.Change(c, item, "跑完了");
        await rig.IdleAsync();

        var last = await rig.LastDurableAsync(c, item);
        last["status"]!.Value<string>().Should().Be(nameof(AgentItemStatus.Done), "離開 Running 立刻持久");
        last["text"]!.Value<string>().Should().Be("跑完了");
        (await rig.H.Inspect(v => v.RunningItems)).Should().Be(0);
        (await rig.H.Inspect(v => v.HasRunningTimer)).Should().BeFalse();
        var durable = await rig.DurableCountAsync(c, item);

        await rig.H.AdvanceAsync(11);
        rig.Relays(c).Should().HaveCount(1, "被擋下的那一則不再送（已經不是 Running）");
        (await rig.DurableCountAsync(c, item)).Should().Be(durable, "之後沒有 Running 的檢查點蓋掉完成的版本");
        (await rig.LastDurableAsync(c, item))["status"]!.Value<string>().Should().Be(nameof(AgentItemStatus.Done));
    }

    [Fact]
    public async Task 超過64KB不送這則()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);

        var big = rig.Item(c, new string('長', 30_000));
        await rig.IdleAsync();
        rig.Relays(c).Should().BeEmpty("UTF-8 約 90 KB，超過 64 KB 的即時更新不送（不截斷，等持久事件）");
        (await rig.DurableCountAsync(c, big)).Should().Be(1, "持久的照常寫（截斷）");

        var small = rig.Item(c, "短的");
        await rig.IdleAsync();
        rig.Relays(c).Should().ContainSingle("只丟太大的那一則；沒送出的不算進頻率").Which.Ord.Should().Be(small.Ord!.Value);
    }

    [Fact]
    public async Task 一定放不下的即時更新_串流途中不重做遮蔽與序列化()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);
        var big = rig.Item(c, new string('長', 30_000));
        await rig.IdleAsync();
        var masked = await rig.H.Inspect(v => v.LiveMasked);

        // 被看的對話每 50 ms 合併一次：同一筆超過 64 KB 的項目一直在長
        for (var i = 0; i < 20; i++)
        {
            rig.Change(c, big, big.Text + "續");
            await rig.AdvanceMsAsync(50);
        }

        (await rig.H.Inspect(v => v.LiveMasked)).Should().Be(masked, "UTF-8 已經超過 64 KB：不做整筆遮蔽與序列化（合併前審查 T1）");
        (await rig.H.Inspect(v => v.LiveTooLarge)).Should().BeGreaterThan(0);
        rig.Relays(c).Should().BeEmpty();
    }

    [Fact]
    public async Task 持久檢查點_9999毫秒沒有_10000毫秒一筆()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        // 文字的尾端不用英數字：執行中的項目尾端連續的權杖字元先不送（CredentialLeakRules.SafeLength）
        var item = rig.Item(c, "第0段");
        await rig.IdleAsync();
        (await rig.DurableCountAsync(c, item)).Should().Be(1, "第一次 Running 立即持久");

        for (var i = 1; i <= 9; i++)
        {
            await rig.AdvanceMsAsync(1_000);
            rig.Change(c, item, $"第{i}段");
            await rig.IdleAsync();
        }

        await rig.AdvanceMsAsync(999);
        (await rig.DurableCountAsync(c, item)).Should().Be(1, "9,999 ms：檢查點還沒到");
        await rig.AdvanceMsAsync(1);
        (await rig.DurableCountAsync(c, item)).Should().Be(2, "10,000 ms：一筆檢查點");
        (await rig.LastDurableAsync(c, item))["text"]!.Value<string>().Should().Be("第9段", "檢查點寫最新的快照");

        // 檢查點從這一筆重新算：12 秒的變化等到 20 秒
        await rig.AdvanceMsAsync(2_000);
        rig.Change(c, item, "第12段");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(7_999);
        (await rig.DurableCountAsync(c, item)).Should().Be(2, "19,999 ms：下一個檢查點還沒到");
        await rig.AdvanceMsAsync(1);
        (await rig.DurableCountAsync(c, item)).Should().Be(3, "20,000 ms：下一筆");
        (await rig.LastDurableAsync(c, item))["text"]!.Value<string>().Should().Be("第12段");

        await rig.H.AdvanceAsync(30);
        (await rig.DurableCountAsync(c, item)).Should().Be(3, "沒有新的變化就不寫檢查點");
    }

    [Fact]
    public async Task 開始被看時立刻送執行中的項目()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        var other = await rig.KnownAsync();
        var item = rig.Item(c, "還沒人看");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(1_000);
        rig.Change(c, item, "還沒人看＋一段");
        rig.Item(other, "另一個對話");
        await rig.IdleAsync();
        rig.Posted.Should().BeEmpty();

        await rig.WatchAsync(c.Id);

        rig.Relays(c).Should().ContainSingle("開始被看時立刻送，不等下一個變化");
        Text(rig.Relays(c)[0]).Should().Be("還沒人看＋一段", "送的是最新的快照");
        rig.H.Server.LiveRelays.Should().OnlyContain(r => r.ConversationId == c.Id, "沒被看的對話不送");
        rig.LastWatched.Should().BeEquivalentTo([c.Id]);
    }

    [Fact]
    public async Task 即時更新通過CheckRelay_ord與ver等於外層()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);
        var token = RuntimeToken(7);
        var item = rig.Item(c, $"讀到設定檔：key={token}，接著");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(300);
        rig.Change(c, item, $"讀到設定檔：key={token}，接著{token[..12]}");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(300);

        var relays = rig.Relays(c);
        relays.Should().HaveCount(2);
        foreach (var relay in relays)
        {
            LiveRules.CheckRelay(relay, out var detail).Should().BeNull(detail);
            var obj = JObject.Parse(relay.Item);
            obj["ord"]!.Value<long>().Should().Be(relay.Ord).And.Be(item.Ord!.Value);
            obj["ver"]!.Value<long>().Should().Be(relay.Ver);
            relay.Epoch.Should().Be(1);
            relay.Item.Should().NotContain(token, "即時更新也先遮蔽權杖");
            relay.Item.Should().NotContain(token[..12], "執行中的項目去掉尾端還沒完的權杖");
        }

        relays[1].Ver.Should().BeGreaterThan(relays[0].Ver, "ver 每次投影遞增");
    }

    [Fact]
    public async Task 斷線_觀看集合清空_UI收到空集合()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);
        rig.LastWatched.Should().BeEquivalentTo([c.Id]);
        var item = rig.Item(c, "片段");
        await rig.IdleAsync();
        rig.Relays(c).Should().HaveCount(1);

        await rig.GoOfflineAsync();
        rig.LastWatched.Should().BeEmpty("連線丟掉：推空集合給 UI（本機照常提醒）");
        (await rig.H.Inspect(v => v.ServerWatch)).Should().BeEmpty();
        var pushes = rig.Ui.Watched.Count;

        await rig.ReconnectAsync();
        await rig.AdvanceMsAsync(300);
        rig.Change(c, item, "重連之後的片段");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(300);

        (await rig.H.Inspect(v => v.ServerWatch)).Should().BeEmpty("新的連線還沒收到觀看清單");
        rig.Relays(c).Should().HaveCount(1, "舊連線的觀看清單不帶到新的連線");
        rig.Ui.Watched.Count.Should().Be(pushes);
        rig.LastWatched.Should().BeEmpty();
    }

    [Fact]
    public async Task 閒置時沒有計時器()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.H.AdvanceAsync(2);
        var baseline = rig.H.Time.ActiveTimers;
        (await rig.H.Inspect(v => v.HasRunningTimer)).Should().BeFalse();

        var item = rig.Item(c, "一");
        await rig.IdleAsync();
        (await rig.H.Inspect(v => v.HasRunningTimer)).Should().BeFalse("第一次就持久了，沒有要等的");
        await rig.AdvanceMsAsync(1_000);
        rig.Change(c, item, "一二");
        await rig.IdleAsync();
        (await rig.H.Inspect(v => v.HasRunningTimer)).Should().BeTrue("有沒寫的變化：等檢查點");

        await rig.H.AdvanceAsync(10);
        (await rig.H.Inspect(v => v.HasRunningTimer)).Should().BeFalse("檢查點寫完就沒有要等的");
        item.Status = AgentItemStatus.Done;
        rig.Changed(c, item);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(2);

        (await rig.H.Inspect(v => v.RunningItems)).Should().Be(0);
        (await rig.H.Inspect(v => v.HasRunningTimer)).Should().BeFalse();
        rig.H.Time.ActiveTimers.Should().Be(baseline, "閒置時對話發布不留任何計時器（S6）");
    }

    [Fact]
    public async Task 觀看集合推給UI_只含created已確認而且沒有封鎖的()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var acked = await rig.KnownAsync();
        rig.H.Server.HoldAcks = true;
        var unacked = rig.New();
        rig.Meta(unacked);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(1);
        (await rig.H.Inspect(v => v.Store!.LoadPublishMeta()))[unacked.Id].Should().Match<PublishMetaRow>(r => r.CreatedWritten && !r.CreatedAcked);

        await rig.WatchAsync(acked.Id, unacked.Id);
        rig.LastWatched.Should().BeEquivalentTo([acked.Id], "created 還沒確認的不算（伺服器不一定有它）");

        rig.H.Server.HoldAcks = false;
        rig.H.Server.Current!.ReleaseAcks();
        await rig.IdleAsync();
        rig.LastWatched.Should().BeEquivalentTo([acked.Id, unacked.Id], "確認之後推新的集合");

        rig.Model.Reject = e => e.ConversationId == acked.Id ? ConversationRejectCodes.NotOwner : null;
        rig.Item(acked, "被擋的一筆", AgentItemStatus.Done);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(1);
        rig.LastWatched.Should().BeEquivalentTo([unacked.Id], "封鎖之後拿掉");
    }

    // ───────────────────────────── 增量（live2） ─────────────────────────────

    private static readonly string[] Live2Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2];

    /// <summary>伺服器的做法：以上一則（整則或重組後的）為基底重組增量，回傳重組後的整則</summary>
    private static JObject Rebuild(JObject basis, LiveDelta delta)
    {
        var json = LiveDeltaRules.Apply(LiveDeltaRules.FieldsOf(basis), basis["ver"]!.Value<long>(), delta, out _, out var why);
        json.Should().NotBeNull(why);
        return JObject.Parse(json!);
    }

    private static IReadOnlyList<LiveDelta> Deltas(Rig rig, FakeConversation c) => rig.H.Server.LiveDeltas.Where(d => d.ConversationId == c.Id).ToList();

    [Fact]
    public async Task 協商live2_第一則送整則_之後送增量_伺服器重組出每一版_8Hz()
    {
        await using var rig = new Rig(Live2Features);
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);

        var prefix = new string('前', 2_000);
        var item = rig.Item(c, prefix);
        await rig.IdleAsync();
        rig.Relays(c).Should().ContainSingle("沒有基底：整則");
        var current = JObject.Parse(rig.Relays(c)[0].Item);

        for (var i = 1; i <= 5; i++)
        {
            await rig.AdvanceMsAsync(125);
            rig.Change(c, item, prefix + string.Concat(Enumerable.Range(1, i).Select(n => "第" + n + "段")));
            await rig.IdleAsync();
            Deltas(rig, c).Should().HaveCount(i, "125 ms（8 Hz）就送下一則，而且是增量");
            var delta = Deltas(rig, c)[i - 1];
            delta.BaseVer.Should().Be(current["ver"]!.Value<long>());
            delta.Ops.Should().ContainSingle().Which.F.Should().Be(LiveDeltaRules.TextField);
            delta.Ops[0].At.Should().Be(current["text"]!.Value<string>()!.Length, "只送共同前綴之後的新內容");
            current = Rebuild(current, delta);
            current["text"]!.Value<string>().Should().Be(item.Text);
            current["ver"]!.Value<long>().Should().Be(delta.Ver);
        }

        rig.Relays(c).Should().HaveCount(1, "之後都是增量");
    }

    [Fact]
    public async Task 增量不比整則省一半_送整則()
    {
        await using var rig = new Rig(Live2Features);
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);

        var item = rig.Item(c, new string('甲', 2_000));
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(125);
        rig.Change(c, item, new string('乙', 2_000));
        await rig.IdleAsync();

        Deltas(rig, c).Should().BeEmpty("整段換掉：增量和整則一樣大");
        rig.Relays(c).Should().HaveCount(2);
        Text(rig.Relays(c)[1]).Should().Be(item.Text);
    }

    [Fact]
    public async Task 伺服器要整則_忘掉基底並照頻率補送一則整則_之後再回到增量()
    {
        await using var rig = new Rig(Live2Features);
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);
        var prefix = new string('前', 2_000);
        var item = rig.Item(c, prefix);
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(125);
        rig.Change(c, item, prefix + "一");
        await rig.IdleAsync();
        Deltas(rig, c).Should().HaveCount(1);

        rig.H.Server.Current!.Resync(c.Id, item.Ord!.Value, LiveResyncReasons.Base);
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(125);
        rig.Relays(c).Should().HaveCount(2, "內容沒變也補送一則整則（伺服器丟了上一則）");
        Text(rig.Relays(c)[1]).Should().Be(prefix + "一");

        await rig.AdvanceMsAsync(125);
        rig.Change(c, item, prefix + "一二");
        await rig.IdleAsync();
        Deltas(rig, c).Should().HaveCount(2, "以補送的整則為基底");
        Deltas(rig, c)[1].BaseVer.Should().Be(rig.Relays(c)[1].Ver);
    }

    [Fact]
    public async Task 伺服器判定外洩_這一筆到離開Running只送整則_下一筆照常增量()
    {
        await using var rig = new Rig(Live2Features);
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);
        var prefix = new string('前', 2_000);
        var item = rig.Item(c, prefix);
        await rig.IdleAsync();

        rig.H.Server.Current!.Resync(c.Id, item.Ord!.Value, LiveResyncReasons.Remask);
        await rig.IdleAsync();
        for (var i = 1; i <= 3; i++)
        {
            await rig.AdvanceMsAsync(125);
            rig.Change(c, item, prefix + new string('後', i));
            await rig.IdleAsync();
        }

        Deltas(rig, c).Should().BeEmpty("remask 的這一筆只送整則");
        rig.Relays(c).Should().HaveCount(4, "remask 本身不補送");

        item.Status = AgentItemStatus.Done;
        rig.Change(c, item, prefix + "完");
        await rig.IdleAsync();
        var next = rig.Item(c, prefix);
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(125);
        rig.Relays(c).Should().HaveCount(5, "完成的那一則只寫持久版本；下一筆的第一則是整則（被頻率擋下 125 ms）");
        rig.Change(c, next, prefix + "下一筆");
        await rig.IdleAsync();
        await rig.AdvanceMsAsync(125);
        Deltas(rig, c).Should().ContainSingle("別筆照常").Which.Ord.Should().Be(next.Ord!.Value);
    }

    [Fact]
    public async Task 斷線重連_基底清空_第一則送整則()
    {
        await using var rig = new Rig(Live2Features);
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.WatchAsync(c.Id);
        var prefix = new string('前', 2_000);
        var item = rig.Item(c, prefix);
        await rig.IdleAsync();

        await rig.GoOfflineAsync();
        await rig.ReconnectAsync();
        await rig.WatchAsync(c.Id);
        Deltas(rig, c).Should().BeEmpty("新連線的伺服器沒有基底：開始被看時補送的是整則");
        rig.Relays(c).Should().HaveCount(2);

        await rig.AdvanceMsAsync(125);
        rig.Change(c, item, prefix + "重連之後");
        await rig.IdleAsync();
        Deltas(rig, c).Should().ContainSingle().Which.BaseVer.Should().Be(rig.Relays(c)[1].Ver, "以新連線送的整則為基底");
    }

    [Fact]
    public async Task 沒協商live2_只送整則_64KB上限_協商了到256KB而且依大小降頻()
    {
        await using (var old = new Rig())
        {
            await old.StartAsync();
            var c = await old.KnownAsync();
            await old.WatchAsync(c.Id);
            var item = old.Item(c, new string('前', 2_000));
            await old.IdleAsync();
            await old.AdvanceMsAsync(250);
            old.Change(c, item, item.Text + "後");
            await old.IdleAsync();
            old.Relays(c).Should().HaveCount(2);
            old.H.Server.LiveDeltas.Should().BeEmpty("沒有 live2 的伺服器只收整則");
        }

        await using var rig = new Rig(Live2Features);
        await rig.StartAsync();
        var conv = await rig.KnownAsync();
        await rig.WatchAsync(conv.Id);
        var big = rig.Item(conv, new string('長', 40_000));
        await rig.IdleAsync();
        rig.Relays(conv).Should().ContainSingle("UTF-8 約 120 KB：live2 的上限是 256 KB");

        await rig.AdvanceMsAsync(125);
        rig.Change(conv, big, big.Text + "一");
        await rig.IdleAsync();
        Deltas(rig, conv).Should().BeEmpty("≤128 KB 的是 250 ms：125 ms 還不到");
        await rig.AdvanceMsAsync(125);
        Deltas(rig, conv).Should().ContainSingle("250 ms：送被擋下的最新版本（增量）");
    }
}
