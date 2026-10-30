using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Engine;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端觀看時的提醒判斷（WP4.3 計畫步 11，D19）：真的中樞（HubHarness）＋真的發布者＋同步引擎＋假伺服器整條串起來。
/// 瀏覽器看得到（伺服器說有人看、引擎以本機政策過濾、created 已確認、沒有封鎖）而且中樞再看一次本機政策是 full 的對話，本機不通知、不閃工作列；
/// 其他情況（政策不是 full——連引擎的集合還沒更新的那一刻也一樣、created 還沒確認、被封鎖、斷線、解除配對）照常提醒。
/// 另外量「變更到送出」：被觀看的對話不超過合併延遲加 4 Hz 的間隔（S7）。發布者與引擎共用一個可撥的時鐘
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSeenRemotelyTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Hub = new HubHarness();
            E = new EngineHarness(conversations: Box);
            E.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
            _ = E.Server.Conversations;
        }

        public HubHarness Hub { get; }

        public EngineHarness E { get; }

        public ConversationChangeBox Box { get; } = new();

        public ConversationPublisher Publisher { get; private set; } = null!;

        public FakeConversations Model => E.Server.Conversations;

        /// <summary>引擎建好、啟動之前掛上發布者（盒子的 UI 端），再把它掛到中樞上</summary>
        public async Task StartAsync()
        {
            await E.StartAsync(engine => Publisher = Hub.OnUi(() => ConversationPublisher.TryCreate(Hub.Hub, engine, E.Time))!);
            Hub.Ui.Invoke(() => Hub.Hub.AttachConversationSink(Publisher));
            await SettleAsync();
        }

        /// <summary>UI 執行緒與引擎輪流做完手上的工作（發布者的 flush 經 BeginInvoke、引擎推觀看集合也經 BeginInvoke）</summary>
        public async Task SettleAsync()
        {
            for (var i = 0; i < 3; i++)
            {
                await Hub.Ui.DrainAsync();
                await E.IdleAsync();
            }
        }

        public async Task AdvanceMsAsync(int ms)
        {
            await E.Time.AdvanceAsync(TimeSpan.FromMilliseconds(ms), SettleAsync);
            await SettleAsync();
        }

        /// <summary>開一個對話、送出第一則（這一輪還在跑），讓發布者與引擎送完 created 與項目</summary>
        public async Task<(string Id, string Wire, FakeSession Session)> StartedAsync(bool acked = true)
        {
            var (id, session) = Hub.StartConversation(Hub.Main, Hub.Claude);
            await AdvanceMsAsync(2_000);
            var wire = ConversationIds.ToWire(id)!;
            var row = (await E.Inspect(v => v.Store!.LoadPublishMeta()))[wire];
            row.CreatedWritten.Should().BeTrue("前提：created 寫了");
            row.CreatedAcked.Should().Be(acked, "前提：created 的確認");
            return (id, wire, session);
        }

        public async Task WatchAsync(params string[] wires)
        {
            E.Server.Current!.Watch(wires);
            await SettleAsync();
        }

        /// <summary>中樞記的「其他地方看得到」的對話（引擎經發布者推來的）</summary>
        public IReadOnlySet<string> RemoteWatched => Hub.OnUi(() => Hub.Hub.RemoteWatched);

        public IReadOnlyList<LiveRelay> Relays(string wire) => E.Server.LiveRelays.Where(r => r.ConversationId == wire).ToList();

        public IReadOnlyList<SyncEvent> Sent(string wire) => E.Server.Batches.SelectMany(b => b.Events).Where(e => e.ConversationId == wire).ToList();

        public async ValueTask DisposeAsync()
        {
            await E.DisposeAsync();
            Hub.Dispose();
        }
    }

    // ───────────────────────────── 看得到：不提醒 ─────────────────────────────

    [Fact]
    public async Task 被看得到的full對話_IsSeenRemotely為真_本機不提醒()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (id, wire, session) = await rig.StartedAsync();
        await rig.WatchAsync(wire);
        rig.RemoteWatched.Should().BeEquivalentTo([id], "引擎推來的觀看集合（本機 id）");

        rig.Hub.Host.IsForeground = false;
        var attention = rig.Hub.Host.AttentionRequests;
        rig.Hub.CompleteTurn(session);

        rig.Hub.Host.Notifications.Count.Should().Be(0, "瀏覽器看得到這個對話：本機不通知");
        rig.Hub.Host.AttentionRequests.Should().Be(attention, "也不閃工作列");

        // 提醒照樣上線（瀏覽器那邊看得到「已完成」）
        await rig.AdvanceMsAsync(1_000);
        rig.Sent(wire).Should().Contain(e => e.Kind == ConversationEventKinds.Attention);
    }

    // ───────────────────────────── 看不到：照常提醒 ─────────────────────────────

    [Fact]
    public async Task 被看但政策不是full或created未確認或封鎖_照常提醒_政策metadata()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (id, wire, session) = await rig.StartedAsync();
        await rig.WatchAsync(wire);
        rig.RemoteWatched.Should().Contain(id);

        // 改成只同步標題：引擎的集合還沒更新（發布者的合併計時器還沒到）的那一刻，中樞自己看本機政策
        rig.Hub.OnUi(() => rig.Hub.Hub.SetConversationSyncPolicy(id, ConversationSyncPolicies.Metadata)).Should().BeTrue();
        rig.RemoteWatched.Should().Contain(id, "前提：引擎還沒推新的觀看集合");
        rig.Hub.Host.IsForeground = false;
        rig.Hub.CompleteTurn(session);

        rig.Hub.Host.Notifications.Count.Should().Be(1, "政策不是 full：瀏覽器看不到內容，本機照常通知");
        rig.Hub.Host.AttentionRequests.Should().BeGreaterThan(0);

        await rig.AdvanceMsAsync(1_000);
        rig.RemoteWatched.Should().NotContain(id, "引擎收到新的政策之後推的集合也不含它");
        rig.Relays(wire).Should().BeEmpty();
    }

    [Fact]
    public async Task 被看但政策不是full或created未確認或封鎖_照常提醒_created未確認()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.E.Server.HoldAcks = true;
        var (id, wire, session) = await rig.StartedAsync(acked: false);
        await rig.WatchAsync(wire);
        rig.RemoteWatched.Should().NotContain(id, "created 還沒確認：伺服器不一定有它");

        rig.Hub.Host.IsForeground = false;
        rig.Hub.CompleteTurn(session);

        rig.Hub.Host.Notifications.Count.Should().Be(1, "本機照常通知");
        rig.Hub.Host.AttentionRequests.Should().BeGreaterThan(0);

        // 確認之後才算看得到
        rig.E.Server.HoldAcks = false;
        rig.E.Server.Current!.ReleaseAcks();
        await rig.SettleAsync();
        rig.RemoteWatched.Should().Contain(id);
    }

    [Fact]
    public async Task 被看但政策不是full或created未確認或封鎖_照常提醒_封鎖()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (id, wire, session) = await rig.StartedAsync();
        await rig.WatchAsync(wire);
        rig.RemoteWatched.Should().Contain(id);

        rig.Model.Reject = e => e.ConversationId == wire ? ConversationRejectCodes.NotOwner : null;
        rig.Hub.Raise(session, new AgentTextDeltaEvent("b1", "被擋下的一段"));
        await rig.AdvanceMsAsync(2_000);
        (await rig.E.Inspect(v => v.Store!.LoadPublishMeta()))[wire].Blocked.Should().Be(ConversationRejectCodes.NotOwner, "前提：被封鎖");
        rig.RemoteWatched.Should().NotContain(id, "封鎖之後引擎推的集合不含它");

        rig.Hub.Host.IsForeground = false;
        rig.Hub.CompleteTurn(session);

        rig.Hub.Host.Notifications.Count.Should().Be(1, "本機照常通知");
        rig.Hub.Host.AttentionRequests.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task 斷線後_照常提醒()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (id, wire, session) = await rig.StartedAsync();
        await rig.WatchAsync(wire);
        rig.RemoteWatched.Should().Contain(id);

        rig.E.Server.ConnectFailure = _ => EngineHarness.Transient();
        rig.E.Server.Current!.Drop();
        await rig.SettleAsync();
        rig.RemoteWatched.Should().BeEmpty("連線丟掉：觀看集合清空");

        rig.Hub.Host.IsForeground = false;
        rig.Hub.CompleteTurn(session);

        rig.Hub.Host.Notifications.Count.Should().Be(1, "斷線之後瀏覽器看不到即時的狀態，本機照常通知");
        rig.Hub.Host.AttentionRequests.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task 解除配對後_照常提醒()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (id, wire, session) = await rig.StartedAsync();
        await rig.WatchAsync(wire);
        rig.RemoteWatched.Should().Contain(id);

        // 解除配對＝協調者拿掉接收者（引擎之後才停）
        rig.Hub.Ui.Invoke(() => rig.Hub.Hub.AttachConversationSink(null));
        rig.RemoteWatched.Should().BeEmpty("接收者拿掉：之前推來的觀看集合不再算數");

        rig.Hub.Host.IsForeground = false;
        rig.Hub.CompleteTurn(session);

        rig.Hub.Host.Notifications.Count.Should().Be(1, "本機照常通知");
    }

    // ───────────────────────────── 延遲（S7） ─────────────────────────────

    [Fact]
    public async Task 從變更到送出_不超過flush延遲加Allows間隔()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var (id, wire, session) = await rig.StartedAsync();
        await rig.WatchAsync(wire);
        rig.RemoteWatched.Should().Contain(id);
        rig.Hub.Items(id).Should().OnlyContain(i => i.Ord >= 1, "前提：Ord 已準備好（之後新增的項目立刻配 Ord）");
        var budget = PublishTimingRules.WatchedFlushMs + PublishTimingRules.LiveIntervalMs(SyncLimits.Default.MaxLiveHz);
        var elapsed = 0;

        // 第一段：被觀看的合併延遲之後立刻送
        rig.Hub.Raise(session, new AgentTextDeltaEvent("b1", "第一段"));
        await rig.AdvanceMsAsync(PublishTimingRules.WatchedFlushMs - 1);
        elapsed += PublishTimingRules.WatchedFlushMs - 1;
        rig.Relays(wire).Should().BeEmpty("合併延遲還沒到");
        await rig.AdvanceMsAsync(1);
        elapsed += 1;
        rig.Relays(wire).Should().ContainSingle("被觀看的對話：50 ms 合併之後送出");
        var firstAt = elapsed;

        // 第二段：10 ms 後的變化被 4 Hz 擋下，等到上一則之後 250 ms
        await rig.AdvanceMsAsync(10);
        elapsed += 10;
        var changedAt = elapsed;
        rig.Hub.Raise(session, new AgentTextDeltaEvent("b1", "，第二段"));
        var expected = firstAt + PublishTimingRules.LiveIntervalMs(SyncLimits.Default.MaxLiveHz);
        await rig.AdvanceMsAsync(expected - elapsed - 1);
        elapsed = expected - 1;
        rig.Relays(wire).Should().HaveCount(1, "4 Hz 的間隔還沒到");
        await rig.AdvanceMsAsync(1);
        elapsed += 1;
        rig.Relays(wire).Should().HaveCount(2, "上一則之後 250 ms 送出最新的");
        (elapsed - changedAt).Should().BeLessThanOrEqualTo(budget, "變更到送出不超過合併延遲加 Allows 間隔（S7）");
        rig.Relays(wire)[1].Item.Should().Contain("第一段，第二段");
    }
}
