using System.Collections.Concurrent;
using System.Text;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>一則 LiveRelay 的處置（<see cref="LiveRelayIntake.Receive"/>；裝置收不到，只給測試與計數）</summary>
internal enum LiveIntakeOutcome
{
    /// <summary>放進疊加層的佇列（之後閘道照疊加層的規則決定收不收）</summary>
    Offered,

    /// <summary>遮蔽後放進佇列（D44；計入 remasked.live）</summary>
    OfferedRemasked,

    Dropped,
}

/// <summary>
/// 裝置 hub 的 <c>LiveRelay</c> 與 <c>LiveDelta</c> 收件（WP4.2 計畫步 11；R2、R13，D27、D44、F37；增量見 docs/plans/2026-10-23-live-delta-and-replica-live.md 3.4）：
/// 在裝置 hub 的呼叫執行緒上、不等寫入者，便宜的檢查先做，任何一步不通過就丟掉、計入 <c>liveDropped</c>（<see cref="LiveDropReason"/>）、不回錯誤給裝置。順序：
/// <list type="number">
/// <item>連線協商出 live1（增量要 live2，<see cref="DeviceConnection.Allows"/>）。</item>
/// <item>外框的便宜檢查：對話 id 是線上格式、epoch／ord／ver ≥1、item 不是 null 而且不超過上限（沒有 live2 的連線 64 KB、有的 256 KB，<see cref="LiveRules.MaxItemBytes"/>）。</item>
/// <item>每台裝置的權杖桶（每秒 <c>MaxLivePerDevicePerSecond</c> 則、突發同值）。</item>
/// <item>增量：以這條連線記著的基底（<see cref="LiveDeltaBases"/>）重組成整則（<see cref="LiveDeltaRules.Apply"/>，與直接送整則逐位元組相同）。
/// 沒有基底或對不上就丟、清基底、送 <c>LiveResync{base}</c>；重組成功就換基底——之後的步驟丟掉這一則也不影響（桌面認定的基底是「我最後送的那一版」）。</item>
/// <item>目錄：擁有者是這台、世代相符、沒有刪除、政策是 full。</item>
/// <item>每個對話的權杖桶（每秒 <see cref="SyncLimits.MaxLiveHz"/> 則，live2 的連線 <see cref="SyncLimits.MaxLiveDeltaHz"/>；突發 <c>LiveBurstPerConversation</c>）：鍵在目錄確認之後才建，亂送的 id 撐不大它。</item>
/// <item>裝置沒有被撤銷。</item>
/// <item>完整的 <see cref="LiveRules.CheckRelay"/>（Validate、只有一種解讀、裡面的 ord／ver 等於外層的）＋沒有標準 JSON 沒有的節點（建構式等）：
/// 超速的不做這一步（F37：同一條連線的 Pull 與心跳不被拖慢）。live2 的連線不合格時清基底、要整則。</item>
/// <item>第二道 DLP（答 5、D44）：item 的走遍檢查命中就以 <see cref="ConversationRemaskRules.Remask"/>（Item 種類）遮蔽，遮蔽版再跑 Fits、CheckRelay 與走遍檢查，
/// 仍命中或不合格就丟（dlp）；遮蔽後轉送的計入 <c>remasked.live</c>。ord、ver 不變。DLP 一律對重組後的整則做（權杖切在兩則增量之間也比對得到）；
/// 命中時 live2 的連線不留基底（伺服器不在記憶體保留它判定外洩的原文）、送 <c>LiveResync{remask}</c>，桌面對這一筆改送整則。</item>
/// <item><see cref="LiveOverlay.Offer"/>。live2 的連線收下的整則也成為這一筆的基底。</item>
/// </list>
/// 權杖桶以注入的 <see cref="TimeProvider"/> 的單調時鐘計（D43）。
/// </summary>
internal sealed class LiveRelayIntake
{
    /// <summary>同一筆（連線, 對話, ord）至少隔這麼久才再送一則 <c>LiveResync</c></summary>
    public const int ResyncIntervalMs = 1000;

    private readonly ConversationDirectoryCache directory;
    private readonly DeviceDirectory devices;
    private readonly LiveOverlay overlay;
    private readonly ConversationCounters counters;
    private readonly IOptions<HubOptions> options;
    private readonly TimeProvider time;
    private readonly LiveDeltaBases bases;
    private readonly IDeviceMessageSender sender;
    private readonly ILogger<LiveRelayIntake>? log;
    private readonly ConcurrentDictionary<string, BucketBox> deviceBuckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BucketBox> conversationBuckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<LiveDeltaBases.Key, long> lastResyncMs = new();
    private int checkRelayCalls;
    private long deltaApplied;
    private long resyncSent;

    public LiveRelayIntake(ConversationDirectoryCache directory, DeviceDirectory devices, LiveOverlay overlay, ConversationCounters counters, IOptions<HubOptions> options,
        TimeProvider time, LiveDeltaBases bases, IDeviceMessageSender sender, ConnectionRegistry registry, ILogger<LiveRelayIntake>? log = null)
    {
        this.directory = directory;
        this.devices = devices;
        this.overlay = overlay;
        this.counters = counters;
        this.options = options;
        this.time = time;
        this.bases = bases;
        this.sender = sender;
        this.log = log;
        registry.ConnectionRemoved += connection =>
        {
            bases.RemoveConnection(connection.ConnectionId);
            foreach (var key in lastResyncMs.Keys.Where(k => k.ConnectionId == connection.ConnectionId).ToList())
                lastResyncMs.TryRemove(key, out _);
        };
    }

    /// <summary>測試注入點：取代 <see cref="ConversationRemaskRules.Remask"/>，讓「遮蔽後仍命中」的分支測得到</summary>
    internal Func<string, JObject, string>? RemaskForTest { get; set; }

    /// <summary>做過幾次完整的 <see cref="LiveRules.CheckRelay"/>（測試用：超速的不該做）</summary>
    internal int CheckRelayCalls => Volatile.Read(ref checkRelayCalls);

    /// <summary>每個對話的權杖桶有幾個（測試用：亂送的對話 id 不建桶）</summary>
    internal int ConversationBucketCount => conversationBuckets.Count;

    /// <summary>重組成功的增量則數</summary>
    internal long DeltaApplied => Interlocked.Read(ref deltaApplied);

    /// <summary>送出的 LiveResync 則數</summary>
    internal long ResyncSent => Interlocked.Read(ref resyncSent);

    /// <summary>收一則整則（<paramref name="connection"/> 是這條裝置連線的登記；不在登記裡是 null）。不丟例外</summary>
    public LiveIntakeOutcome Receive(DeviceConnection? connection, LiveRelay? relay)
    {
        if (connection is null || !connection.Allows(DeviceHubMethods.LiveRelay))
            return Drop(LiveDropReason.NotOwner);
        var live2 = connection.Allows(DeviceHubMethods.LiveDelta);
        if (relay is null || !ConversationIds.IsWireId(relay.ConversationId) || !ConversationIds.IsEpoch(relay.Epoch) || relay.Ord < 1 || relay.Ver < 1
            || relay.Item is null || !LiveRules.Fits(relay.Item, LiveRules.MaxItemBytes(live2)))
            return Drop(LiveDropReason.Stale);
        if (!TakeDevice(connection))
            return Drop(LiveDropReason.Rate);
        return Process(connection, relay, live2, fromDelta: false);
    }

    /// <summary>收一則增量（<c>live2</c>）：重組成整則之後照 <see cref="Receive"/> 的其餘步驟。不丟例外</summary>
    public LiveIntakeOutcome ReceiveDelta(DeviceConnection? connection, LiveDelta? delta)
    {
        if (connection is null || !connection.Allows(DeviceHubMethods.LiveDelta) || !connection.Allows(DeviceHubMethods.LiveRelay))
            return Drop(LiveDropReason.NotOwner);
        if (delta is null || !ConversationIds.IsWireId(delta.ConversationId) || !ConversationIds.IsEpoch(delta.Epoch) || delta.Ord < 1 || delta.Ver < 1
            || delta.BaseVer < 1 || delta.Head is null || delta.Ops is null || delta.Ops.Count > LiveDeltaRules.Fields.Count
            || (long)delta.Head.Length + delta.Ops.Sum(o => (long)(o?.S?.Length ?? 0)) > LiveRules.MaxDeltaItemBytes)
            return Drop(LiveDropReason.Stale);
        if (!TakeDevice(connection))
            return Drop(LiveDropReason.Rate);

        var key = new LiveDeltaBases.Key(connection.ConnectionId, delta.ConversationId, delta.Ord);
        if (!bases.TryGet(key, out var baseVer, out var basis))
        {
            Resync(key, LiveResyncReasons.Base);
            return Drop(LiveDropReason.Stale);
        }
        if (LiveDeltaRules.Apply(basis, baseVer, delta, out var fields, out var why) is not { } item || !LiveRules.Fits(item, LiveRules.MaxDeltaItemBytes))
        {
            log?.LogDebug("對話 {Conversation} 的增量重組不了（{Why}）：要整則", delta.ConversationId, why ?? "超過上限");
            bases.Remove(key);
            Resync(key, LiveResyncReasons.Base);
            return Drop(LiveDropReason.Stale);
        }
        Interlocked.Increment(ref deltaApplied);
        SetBase(key, delta.Ver, fields);
        return Process(connection, new LiveRelay(delta.ConversationId, delta.Epoch, delta.Ord, delta.Ver, item), live2: true, fromDelta: true);
    }

    /// <summary>外框與每台的權杖桶之後的步驟（整則與重組後的增量共用）</summary>
    private LiveIntakeOutcome Process(DeviceConnection connection, LiveRelay relay, bool live2, bool fromDelta)
    {
        var settings = options.Value.Conversations;
        var nowMs = NowMs();
        var deviceId = connection.DeviceId;
        var key = new LiveDeltaBases.Key(connection.ConnectionId, relay.ConversationId, relay.Ord);
        if (!directory.TryGet(relay.ConversationId, out var conversation) || conversation.OwnerDeviceId != deviceId || conversation.Epoch != relay.Epoch)
            return Drop(LiveDropReason.NotOwner);
        if (conversation.Deleted || conversation.SyncPolicy != ConversationSyncPolicies.Full)
            return Drop(LiveDropReason.Policy);

        var rate = live2 && SyncLimits.Default.MaxLiveDeltaHz is > 0 and var deltaHz ? deltaHz : SyncLimits.Default.MaxLiveHz;
        if (!conversationBuckets.GetOrAdd(relay.ConversationId, _ => new BucketBox()).TryTake(nowMs, rate, settings.LiveBurstPerConversation))
            return Drop(LiveDropReason.Rate);
        if (devices.Get(deviceId) is not { Revoked: false })
            return Drop(LiveDropReason.NotOwner);

        if (Check(relay, live2) is not { } item)
        {
            if (live2)
            {
                bases.Remove(key);
                Resync(key, LiveResyncReasons.Base);
            }
            return Drop(LiveDropReason.Stale);
        }
        var json = relay.Item;
        var remasked = false;
        if (ConversationDlpRules.HasLeak(item))
        {
            // 命中第二道 DLP：live2 的連線不留基底（不在記憶體保留判定外洩的原文），這一筆之後只收整則（每則照樣重遮）
            if (live2)
            {
                bases.Remove(key);
                Resync(key, LiveResyncReasons.Remask);
            }
            if (Remask(relay, item, live2) is not { } masked)
                return Drop(LiveDropReason.Dlp);
            (json, item) = masked;
            remasked = true;
        }
        else if (live2 && !fromDelta)
        {
            // 整則也成為這一筆的基底（第一則、桌面判斷送整則比較划算、收到 LiveResync 之後）
            SetBase(key, relay.Ver, LiveDeltaRules.FieldsOf(item));
        }
        if (item["id"] is not JValue { Type: JTokenType.String, Value: string itemId })
            return Drop(LiveDropReason.Stale);

        overlay.Offer(new LiveEntry(relay.ConversationId, relay.Ord, itemId, relay.Epoch, relay.Ver, connection.Order, json, Encoding.UTF8.GetByteCount(json), deviceId));
        if (!remasked)
            return LiveIntakeOutcome.Offered;
        counters.RemaskedLiveRelay();
        return LiveIntakeOutcome.OfferedRemasked;
    }

    private bool TakeDevice(DeviceConnection connection)
    {
        var perDevice = Math.Max(0, options.Value.Conversations.MaxLivePerDevicePerSecond);
        return deviceBuckets.GetOrAdd(connection.DeviceId, _ => new BucketBox()).TryTake(NowMs(), perDevice, perDevice);
    }

    private void SetBase(LiveDeltaBases.Key key, long ver, LiveLongFields fields)
    {
        foreach (var evicted in bases.Set(key, ver, fields))
            Resync(evicted, LiveResyncReasons.Evicted);
    }

    /// <summary>請桌面下一則送整則（同一筆至少隔 <see cref="ResyncIntervalMs"/>；送不出去只記錄，下一則增量對不上時會再要一次）</summary>
    private void Resync(LiveDeltaBases.Key key, string reason)
    {
        var now = NowMs();
        var due = true;
        lastResyncMs.AddOrUpdate(key, now, (_, last) =>
        {
            if (now - last < ResyncIntervalMs)
            {
                due = false;
                return last;
            }
            return now;
        });
        if (!due)
            return;
        Interlocked.Increment(ref resyncSent);
        _ = SendResyncAsync(key, reason);
    }

    private async Task SendResyncAsync(LiveDeltaBases.Key key, string reason)
    {
        try
        {
            await sender.SendAsync(key.ConnectionId, DeviceHubMethods.LiveResync, new LiveResync(key.ConversationId, key.Ord, reason), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "送 LiveResync 失敗");
        }
    }

    /// <summary>
    /// 完整的檢查；合格回解析後的 item。有標準 JSON 沒有的節點（<c>new 名稱(…)</c> 建構式等，<see cref="ConversationDlpRules.HasNonStandardToken"/>）也不合格：
    /// 走遍檢查看不到建構式的名稱，不能當成乾淨的轉送
    /// </summary>
    private JObject? Check(LiveRelay relay, bool live2)
    {
        Interlocked.Increment(ref checkRelayCalls);
        if (LiveRules.CheckRelay(relay, out _, LiveRules.MaxItemBytes(live2)) is not null)
            return null;
        return ConversationEventRules.TryParseStrict(relay.Item, out var token) && token is JObject item && !ConversationDlpRules.HasNonStandardToken(item)
            ? item
            : null;
    }

    /// <summary>遮蔽並重新檢查（D44）：遮蔽版仍不合格、太大或仍命中回 null。遮蔽丟出毒事件的例外也當成失敗（只丟這一則）</summary>
    private (string Json, JObject Item)? Remask(LiveRelay relay, JObject item, bool live2)
    {
        string masked;
        try
        {
            masked = (RemaskForTest ?? ConversationRemaskRules.Remask)(ConversationEventKinds.Item, item);
        }
        catch (Exception ex) when (ConversationFaultRules.IsPoison(ex))
        {
            return null;
        }
        if (!LiveRules.Fits(masked, LiveRules.MaxItemBytes(live2)) || Check(relay with { Item = masked }, live2) is not { } reparsed || ConversationDlpRules.HasLeak(reparsed))
            return null;
        return (masked, reparsed);
    }

    private LiveIntakeOutcome Drop(string reason)
    {
        counters.LiveDropped(reason);
        return LiveIntakeOutcome.Dropped;
    }

    private long NowMs() => time.GetTimestamp() * 1000 / time.TimestampFrequency;

    /// <summary>一個權杖桶（各自加鎖：同一台裝置的呼叫依序、不同裝置平行）</summary>
    private sealed class BucketBox
    {
        private readonly object gate = new();
        private LiveTokenBucket bucket;

        public bool TryTake(long nowMs, double ratePerSecond, int burst)
        {
            lock (gate)
                return LiveOverlayRules.TakeToken(ref bucket, nowMs, ratePerSecond, burst);
        }
    }
}
