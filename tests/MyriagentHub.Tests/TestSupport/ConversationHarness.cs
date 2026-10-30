using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Rendering;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>JSON 的結構比對（屬性順序不計；同 AccountViewRulesTests 的 JToken.DeepEquals）</summary>
internal static class JsonAssertions
{
    public static void ShouldBeJson(this JToken? actual, string expected)
    {
        actual.Should().NotBeNull();
        JToken.DeepEquals(actual, JToken.Parse(expected)).Should().BeTrue($"實際是 {actual!.ToString(Newtonsoft.Json.Formatting.None)}");
    }
}

/// <summary>
/// 伺服器對話的閘道測試環境（WP4.2 步 10）：真的主機（sync.db、EventLog、物化、目錄、讀取與渲染），但主機自己的閘道以 <c>views: null</c> 重新登記、
/// 不取變更通知（<c>withoutConversationGateway</c>）；另建一個由測試驅動的閘道（自己的連線登記與 <see cref="FakeTimestampProvider"/>，<see cref="RunAsync"/> 驅動一輪），取主機的同一個變更通知，
/// 並接上自己的 <see cref="BrowserGatewayWiring"/>（裝置上下線、ClientState）。指令經 <see cref="Commands"/> 直接呼叫（同 hub 的指令處理）。
/// 觀看追蹤與快照請求（步 12）也是測試自己的實例，送出記在 <see cref="DeviceSender"/>。
/// 兩個時鐘從同一個整點開始（D43：執行當下截到整點），<see cref="AdvanceClocks"/> 一起撥
/// </summary>
internal sealed class ConversationHarness : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, long> deviceSeqs = new(StringComparer.Ordinal);
    private readonly BrowserGatewayWiring wiring;

    private ConversationHarness(HubTestHost host, DateTimeOffset start, Action<HubOptions>? configure)
    {
        Host = host;
        var options = new HubOptions();
        configure?.Invoke(options);
        Options = Microsoft.Extensions.Options.Options.Create(options);
        Time = new FakeTimestampProvider(start);
        Connections = new BrowserConnections(Options, NullLogger<BrowserConnections>.Instance);
        var services = host.Services;
        Views = new ConversationViews(services.GetRequiredService<ConversationReader>(), services.GetRequiredService<HubItemRenderer>(),
            services.GetRequiredService<ConversationDirectoryCache>(), services.GetRequiredService<DeviceDirectory>(), services.GetRequiredService<IDevicePresence>(),
            Feed, services.GetRequiredService<ConversationCounters>(), Options, services.GetRequiredService<ConversationUnread>());
        Unread = services.GetRequiredService<ConversationUnread>();
        Overlay = services.GetRequiredService<LiveOverlay>();
        Counters = services.GetRequiredService<ConversationCounters>();
        // 收件以測試的時鐘計權杖桶（主機的 ManualTimeProvider 只撥牆上時鐘，單調時鐘是真的時間）
        Bases = new LiveDeltaBases(Time);
        Intake = new LiveRelayIntake(services.GetRequiredService<ConversationDirectoryCache>(), services.GetRequiredService<DeviceDirectory>(), Overlay, Counters,
            Options, Time, Bases, DeviceSender, Registry);
        // 伺服器草稿（步 13）：測試自己的連線登記與時鐘；閘道在新對話出現時換掉草稿（WP4.6 步 7）
        Drafts = new BrowserDrafts(Connections, Options, Time);
        Gateway = new BrowserGateway(Source, Connections, Options, Time, NullLogger<BrowserGateway>.Instance, Views, Overlay, Drafts);
        Directory = services.GetRequiredService<ConversationDirectoryCache>();
        // 觀看與快照請求（步 12）：測試自己的實例（記錄的送出端、測試的時鐘），不跑背景迴圈，由測試以 FlushAsync／TickAsync 驅動
        Watch = new ConversationWatch(Connections, Directory, Registry, Feed, DeviceSender, Options, Time, NullLogger<ConversationWatch>.Instance);
        Snapshots = new SnapshotRequester(Directory, Registry, DeviceSender, Options, Time, NullLogger<SnapshotRequester>.Instance);
        // 側欄的對話搜尋（2026-10-23）：測試自己的實例（結果經測試的閘道送）
        Search = new BrowserConversationSearch(Gateway, services.GetRequiredService<ConversationReader>(), services.GetRequiredService<IBlobStore>(),
            NullLogger<BrowserConversationSearch>.Instance);
        Commands = new BrowserConversationCommands(Gateway, services.GetRequiredService<IConversationDirectory>(), services.GetRequiredService<ConversationReader>(),
            services.GetRequiredService<IBlobStore>(), Snapshots, Drafts, Search);
        wiring = new BrowserGatewayWiring(services.GetRequiredService<SyncNotifier>(), Registry, Gateway, Connections,
            services.GetRequiredService<IHostApplicationLifetime>(), NullLogger<BrowserGatewayWiring>.Instance, Feed, Overlay);
    }

    public HubTestHost Host { get; }

    public IOptions<HubOptions> Options { get; }

    public FakeTimestampProvider Time { get; }

    public FakeSnapshotSource Source { get; } = new();

    public BrowserConnections Connections { get; }

    public ConversationViews Views { get; }

    public BrowserGateway Gateway { get; }

    public BrowserConversationCommands Commands { get; }

    /// <summary>側欄的對話搜尋（<see cref="Commands"/> 的 searchConversations 交給它）</summary>
    public BrowserConversationSearch Search { get; }

    /// <summary>主機的即時疊加層（主機的閘道沒有 views，不取它：測試的閘道是唯一的消費者）</summary>
    public LiveOverlay Overlay { get; }

    /// <summary>即時更新的收件（同裝置 hub 的 LiveRelay；主機的目錄與疊加層、測試的時鐘）</summary>
    public LiveRelayIntake Intake { get; }

    /// <summary>增量的基底（測試的時鐘）</summary>
    public LiveDeltaBases Bases { get; }

    public ConversationCounters Counters { get; }

    /// <summary>主機的目錄快取</summary>
    public ConversationDirectoryCache Directory { get; }

    /// <summary>主機的未讀集合（步 12A；主機的閘道沒有 views，只有測試的閘道讀寫它）</summary>
    public ConversationUnread Unread { get; }

    /// <summary>觀看與快照請求送給裝置的訊息</summary>
    public RecordingDeviceSender DeviceSender { get; } = new();

    /// <summary>觀看追蹤（測試的連線登記與送出端；<see cref="ConversationWatch.FlushAsync"/> 驅動）</summary>
    public ConversationWatch Watch { get; }

    /// <summary>快照請求（open 經 <see cref="Commands"/>；restore 以 <see cref="SnapshotRequester.TickAsync"/> 驅動）</summary>
    public SnapshotRequester Snapshots { get; }

    /// <summary>伺服器草稿（步 13；測試的連線登記與時鐘，newConversation 與草稿的 open／close 經 <see cref="Commands"/>）</summary>
    public BrowserDrafts Drafts { get; }

    /// <summary>被丟掉的即時更新（依原因）</summary>
    public long LiveDropped(string reason) => Counters.Snapshot().LiveDropped![reason];

    public ConversationChangeFeed Feed => Host.Services.GetRequiredService<ConversationChangeFeed>();

    public ConnectionRegistry Registry => Host.Services.GetRequiredService<ConnectionRegistry>();

    public TimeSpan PushInterval => TimeSpan.FromMilliseconds(Options.Value.Conversations.ConversationPushIntervalMs);

    /// <param name="configure">測試閘道的設定（頁面大小、送出佇列上限…）；主機的設定另以 <paramref name="settings"/> 給（變更通知的上限在主機）</param>
    public static async Task<ConversationHarness> StartAsync(Action<HubOptions>? configure = null, IDictionary<string, string?>? settings = null)
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        // 主機自己的閘道不取變更通知（不帶 views）：測試的閘道才是唯一的消費者
        var host = await HubTestHost.StartAsync(settings: settings, clock: new ManualTimeProvider(start), withoutConversationGateway: true);
        var harness = new ConversationHarness(host, start, configure);
        await harness.wiring.StartAsync(CancellationToken.None);
        harness.Watch.Attach();
        harness.Snapshots.Attach();
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        Watch.Detach();
        Snapshots.Detach();
        Watch.Dispose();
        Snapshots.Dispose();
        Drafts.Dispose();
        Search.Dispose();
        await wiring.StopAsync(CancellationToken.None);
        foreach (var connection in Connections.Snapshot())
            connection.Outbox.Complete();
        await Host.DisposeAsync();
    }

    // ───── 瀏覽器連線 ─────

    public SendRecorder Add(string connectionId, string sessionId = "s1")
    {
        var recorder = new SendRecorder();
        recorder.Connection = Connections.TryAdd(connectionId, sessionId, null, recorder.OnAbort, recorder.SendAsync)
            ?? throw new InvalidOperationException("連線被拒");
        return recorder;
    }

    /// <summary>新連線送 ready 並收下 init（回傳 init）</summary>
    public async Task<(SendRecorder Recorder, JObject Init)> ReadyAsync(string connectionId, string sessionId = "s1")
    {
        var recorder = Add(connectionId, sessionId);
        Gateway.RequestInit(recorder.Connection, "main");
        await RunAsync();
        var init = await recorder.NextAsync();
        init["type"]!.Value<string>().Should().Be("init");
        return (recorder, init);
    }

    public Task<TimeSpan> RunAsync() => Gateway.RunOnceAsync(CancellationToken.None);

    /// <summary>撥過一個推送間隔再跑一輪（對話的增量最多每 ConversationPushIntervalMs 處理一次）</summary>
    public Task<TimeSpan> RoundAsync()
    {
        Time.Advance(PushInterval);
        return RunAsync();
    }

    /// <summary>兩個時鐘一起撥（主機的牆上時鐘決定事件的收到時間，測試閘道的時鐘決定分組與節奏）</summary>
    public void AdvanceClocks(TimeSpan by)
    {
        Host.Clock.Advance(by);
        Time.Advance(by);
    }

    /// <summary>同 hub 的指令處理：回 null＝接受，否則是錯誤碼</summary>
    public Task<string?> CmdAsync(SendRecorder recorder, string cmd, object body)
    {
        var json = JObject.FromObject(body);
        json["cmd"] = cmd;
        return Commands.HandleAsync(recorder.Connection, cmd, json, CancellationToken.None);
    }

    /// <summary>open 並跑一輪，收下 conversation</summary>
    public async Task<JObject> OpenAsync(SendRecorder recorder, string conversationId)
    {
        (await CmdAsync(recorder, "open", new { conversationId })).Should().BeNull();
        await RoundAsync();
        var message = await recorder.NextAsync();
        message["type"]!.Value<string>().Should().Be("conversation");
        return message;
    }

    public void SetState(SendRecorder recorder, bool visible, string[] openConversationIds, int tzOffsetMinutes = 0, bool focused = true, IReadOnlyList<string>? features = null) =>
        recorder.Connection.SetState(new BrowserClientState(visible, focused, openConversationIds, tzOffsetMinutes) { Features = features ?? [] });

    // ───── 裝置與事件 ─────

    public Task<string> AddDeviceAsync(string name = "測試電腦") => EventLogTests.AddDeviceAsync(Host, name);

    /// <summary>
    /// 裝置上線（登記一條已證明的連線，<paramref name="features"/> 是協商出的功能；<paramref name="updateOnly"/>＝只更新的連線，sync-protocol 1.4）；回傳連線 id
    /// </summary>
    public string Online(string deviceId, IReadOnlyList<string>? features = null, bool updateOnly = false)
    {
        var connectionId = "dc-" + Guid.NewGuid().ToString("N")[..8];
        var connection = new DeviceConnection(connectionId, deviceId, () => { }, new UpstreamContext(deviceId, null, null), features, updateOnly: updateOnly);
        Registry.Add(connection, Host.Clock.NowMs);
        Registry.MarkProven(connection);
        return connectionId;
    }

    /// <summary>協商出 conv1、live1 的連線上線（即時更新的測試）</summary>
    public string OnlineLive(string deviceId) => Online(deviceId, [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1]);

    /// <summary>同裝置 hub 的 LiveRelay：這條連線送一則即時更新（<paramref name="item"/> 序列化成 JSON）</summary>
    public LiveIntakeOutcome Relay(string connectionId, string conversationId, long ord, long ver, JObject item, long epoch = 1) =>
        Intake.Receive(Registry.Get(connectionId), new LiveRelay(conversationId, epoch, ord, ver, item.ToString(Newtonsoft.Json.Formatting.None)));

    /// <summary>協商出 conv1、live1、live2（增量）的連線上線</summary>
    public string OnlineLive2(string deviceId) => Online(deviceId, [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2]);

    /// <summary>同裝置 hub 的 LiveDelta：以 <paramref name="basis"/>（這條連線上一則送出的長欄位）算出 <paramref name="item"/> 的增量送出</summary>
    public LiveIntakeOutcome Delta(string connectionId, string conversationId, long ord, long ver, long baseVer, LiveLongFields basis, JObject item, long epoch = 1) =>
        Intake.ReceiveDelta(Registry.Get(connectionId), LiveDeltaRules.Encode(conversationId, epoch, ord, ver, baseVer, basis, item));

    public void Offline(string connectionId) => Registry.Remove(connectionId);

    /// <summary>以這台裝置發布事件（deviceSeq 依序遞增）：(kind, payload, conversationId)</summary>
    public async Task<BatchOutcome> PublishAsync(string deviceId, params (string Kind, JToken Payload, string ConversationId)[] events)
    {
        var batch = events.Select(e => ConversationEventLogTests.Ev(deviceSeqs.AddOrUpdate(deviceId, 1, (_, n) => n + 1), e.Kind, e.Payload, e.ConversationId)).ToArray();
        var outcome = await ConversationEventLogTests.Append(Host, deviceId, batch);
        outcome.Ack.Results.Should().OnlyContain(r => r.Rejected == null, "測試的事件都應該被接受");
        return outcome;
    }

    public Task<BatchOutcome> CreateAsync(string deviceId, string conversationId, string title = "標題", long? updatedAt = null, string policy = ConversationSyncPolicies.Full)
    {
        var meta = ConversationSamples.Meta(title, policy);
        if (updatedAt is { } at)
            meta["updatedAt"] = at;
        return PublishAsync(deviceId, (K.Created, meta, conversationId));
    }

    public Task<BatchOutcome> ItemAsync(string deviceId, string conversationId, JObject item) => PublishAsync(deviceId, (K.Item, item, conversationId));

    /// <summary>一個對話＋<paramref name="count"/> 筆 Assistant 項目（item1…，ord 1…）</summary>
    public async Task SeedAsync(string deviceId, string conversationId, int count, string title = "標題", Func<int, string>? text = null)
    {
        await CreateAsync(deviceId, conversationId, title);
        const int perBatch = 20;
        for (var start = 1; start <= count; start += perBatch)
        {
            var events = Enumerable.Range(start, Math.Min(perBatch, count - start + 1))
                .Select(i => (K.Item, (JToken)ConversationSamples.Item(text?.Invoke(i) ?? "第 " + i + " 則", id: "item" + i, ord: i), conversationId))
                .ToArray();
            await PublishAsync(deviceId, events);
        }
    }

    /// <summary>32 字的十六進位對話 id（依序號產生）</summary>
    public static string ConversationId(int n) => n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
}
