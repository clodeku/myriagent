using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using EventLog = MyriagentHub.Sync.EventLog;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 對話事件的寫入量測（WP4.2 計畫步 14；D3、D47、K2、K16；只在 <c>MYRIAGENT_HUB_PERF=1</c> 時跑）。三個情境都在測試主機裡、以
/// <see cref="Probe"/> 走和 <see cref="EventLog.AppendBatchAsync"/> 相同的兩步（呼叫端的預先檢查、寫入者裡的 <see cref="EventLog.AppendBatch(SyncWriteContext, UpstreamContext, Batch, IReadOnlyList{ConversationPrecheck}?)"/>），
/// 只多了量時間：預先檢查（不在寫入者上）、寫入工作（寫入執行緒上，不含提交與 fsync）、從呼叫到完成（hub 在這時送 BatchAck）。
/// 量測值寫成一行 JSON（記進計畫附錄 B）：
/// <list type="number">
/// <item>5 個對話各每秒 20 次 <c>conv.item</c>，30 秒：寫入工作 p99 ≤10 ms（開發機）。</item>
/// <item>4 台同時送，每批 2 筆接近 128 KB、每個字串都只差一個字元就命中 DLP，30 秒：預先檢查 p99 ≤50 ms、寫入工作 p99 ≤10 ms；
/// 另一次真的命中、要再遮蔽：記錄預先檢查加再遮蔽的 p99，寫入工作照樣 ≤10 ms。</item>
/// <item>4 台同時回補（答 3：不限速；每批 ≤256 KB、≤500 筆，每台最多 4 批在途、依確認驅動），60 秒；同時有一個副本照類別游標拉：
/// 記錄寫入工作的 p99、BatchAck 的 p95、Pull 的 p99，以及每 GB 歷史讓 sync.db 長多少（步 15 的 SqliteBench B6 照這個比例產生合成資料）。</item>
/// </list>
/// </summary>
[Collection(PerfCollection.Name)]
public sealed class ConversationWriteBench(ITestOutputHelper output)
{
    private const string Perf = "MYRIAGENT_HUB_PERF";

    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    // ───── 量測用的路徑與 AppendBatchAsync 相同（不受 MYRIAGENT_HUB_PERF 限制） ─────

    [Fact]
    public async Task 量測走的兩步與AppendBatchAsync的結果相同()
    {
        await using var direct = await HubTestHost.StartAsync();
        await using var probed = await HubTestHost.StartAsync();
        var device = SyncIds.NewDeviceId();
        await AddDeviceAsync(direct, device);
        await AddDeviceAsync(probed, device);
        var batch = EventLogTests.Batch(
            ConversationEventLogTests.Ev(1, K.Created, Meta()),
            ConversationEventLogTests.Ev(2, K.Item, Item("外洩 " + GitHub())),
            ConversationEventLogTests.Ev(3, K.Item, Item("沒有對話", id: "x"), conversationId: OtherConv),
            ConversationEventLogTests.Ev(4, K.Queue, Queue("排隊")));

        var expected = await direct.Services.GetRequiredService<EventLog>().AppendBatchAsync(new UpstreamContext(device, null, null), batch);
        var probe = new Probe(probed);
        var actual = await probe.AppendAsync(device, batch);

        actual.Should().BeEquivalentTo(expected);
        ConversationEventLogTests.FoldDump(probed).Sha.Should().Be(ConversationEventLogTests.FoldDump(direct).Sha);
        (probe.Precheck.Count, probe.Job.Count, probe.Ack.Count).Should().Be((1, 1, 1));
    }

    // ───── 情境一 ─────

    [EnvFact(Perf, "寫入量測，約 35 秒")]
    public async Task 情境一_5個對話各每秒20次item_寫入工作p99不超過10ms()
    {
        await using var host = await HubTestHost.StartAsync(clock: HourClock());
        var probe = new Probe(host);
        var devices = await AddDevicesAsync(host, 5);
        foreach (var d in devices)
            await probe.AppendAsync(d.Id, EventLogTests.Batch(d.Next(K.Created, Meta("情境一"))));
        probe.Reset();

        var rng = new Random(101);
        var texts = Enumerable.Range(0, 64).Select(i => MixedText(rng, 500 + rng.Next(3500))).ToArray();
        const int perSecond = 20;
        const int seconds = 30;
        var start = Stopwatch.GetTimestamp();
        await Task.WhenAll(devices.Select((d, n) => Task.Run(async () =>
        {
            for (var k = 0; k < perSecond * seconds; k++)
            {
                await UntilAsync(start, TimeSpan.FromMilliseconds(1000.0 / perSecond * k + n * 7));
                var slot = k % 50;
                var item = Item(texts[(k + n) % texts.Length], k % 3 == 0 ? "Done" : "Running", "i" + slot.ToString(CultureInfo.InvariantCulture), slot + 1, k + 1);
                (await probe.AppendAsync(d.Id, EventLogTests.Batch(d.Next(K.Item, item)))).Ack.Results.Should().OnlyContain(r => r.Seq != null);
            }
        })));

        Report(new() { ["scenario"] = 1, ["seconds"] = seconds, ["conversations"] = 5, ["perSecond"] = perSecond, ["events"] = probe.Job.Count, ["job"] = probe.Job.Summary(),
            ["precheck"] = probe.Precheck.Summary(), ["ack"] = probe.Ack.Summary() });
        probe.Job.Count.Should().Be(5 * perSecond * seconds);
        probe.Job.Percentile(0.99).Should().BeLessThanOrEqualTo(10);
    }

    // ───── 情境二 ─────

    [EnvFact(Perf, "寫入量測，約 35 秒")]
    public async Task 情境二_4台同時送接近128KB而且接近命中DLP_預先檢查p99不超過50ms_寫入工作p99不超過10ms()
    {
        var (probe, counters, batches, bytes) = await RunLargeAsync(hit: false);

        Report(new() { ["scenario"] = 2, ["hit"] = false, ["seconds"] = 30, ["devices"] = 4, ["batches"] = batches, ["decodedBytesPerEvent"] = bytes,
            ["precheck"] = probe.Precheck.Summary(), ["job"] = probe.Job.Summary(), ["ack"] = probe.Ack.Summary() });
        counters.RemaskedEvents.Should().Be(0, "每個字串都只差一個字元：一筆都不命中");
        probe.Precheck.Percentile(0.99).Should().BeLessThanOrEqualTo(50);
        probe.Job.Percentile(0.99).Should().BeLessThanOrEqualTo(10);
    }

    [EnvFact(Perf, "寫入量測，約 35 秒")]
    public async Task 情境二_真的命中要再遮蔽_記錄預先檢查加再遮蔽的p99_寫入工作p99不超過10ms()
    {
        var (probe, counters, batches, bytes) = await RunLargeAsync(hit: true);

        Report(new() { ["scenario"] = 2, ["hit"] = true, ["seconds"] = 30, ["devices"] = 4, ["batches"] = batches, ["decodedBytesPerEvent"] = bytes,
            ["precheckWithRemask"] = probe.Precheck.Summary(), ["job"] = probe.Job.Summary(), ["ack"] = probe.Ack.Summary() });
        counters.RemaskedEvents.Should().Be(batches * 2, "每一筆都命中、遮蔽後接受");
        probe.Job.Percentile(0.99).Should().BeLessThanOrEqualTo(10, "再遮蔽在預先檢查裡做（D3），寫入者只存遮蔽版");
    }

    /// <summary>情境二：4 台各自依序送（每批 2 筆接近 128 KB 的 Tool 項目），30 秒</summary>
    private static async Task<(Probe Probe, ConversationCountsSnapshot Counters, int Batches, int DecodedBytesPerEvent)> RunLargeAsync(bool hit)
    {
        await using var host = await HubTestHost.StartAsync(clock: HourClock());
        var probe = new Probe(host);
        var devices = await AddDevicesAsync(host, 4);
        foreach (var d in devices)
            await probe.AppendAsync(d.Id, EventLogTests.Batch(d.Next(K.Created, Meta("情境二"))));
        probe.Reset();
        var baseline = host.Services.GetRequiredService<ConversationCounters>().Snapshot().RemaskedEvents;

        var batches = 0;
        var decoded = 0;
        var stop = Stopwatch.StartNew();
        await Task.WhenAll(devices.Select((d, n) => Task.Run(async () =>
        {
            var rng = new Random(200 + n);
            var ver = 0;
            while (stop.Elapsed < TimeSpan.FromSeconds(30))
            {
                var events = new SyncEvent[2];
                for (var i = 0; i < events.Length; i++)
                {
                    ver++;
                    var item = LargeTool(rng, hit, "t" + (ver % 40).ToString(CultureInfo.InvariantCulture), ver % 40 + 1, ver);
                    events[i] = d.Next(K.Item, item);
                    Interlocked.Exchange(ref decoded, Encoding.UTF8.GetByteCount(ConversationWire.Serialize(item)));
                }
                events.Sum(e => PayloadRules.WireSize(e.Payload)).Should().BeLessThanOrEqualTo(SyncLimits.Default.MaxBatchBytes, "一批最多 256 KB");
                (await probe.AppendAsync(d.Id, EventLogTests.Batch(events))).Ack.Results.Should().OnlyContain(r => r.Seq != null && r.Rejected == null);
                Interlocked.Increment(ref batches);
            }
        })));
        var counters = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
        return (probe, counters with { RemaskedEvents = counters.RemaskedEvents - baseline }, batches, decoded);
    }

    // ───── 情境三 ─────

    [EnvFact(Perf, "回補量測，約 70 秒")]
    public async Task 情境三_4台同時回補不限速_記錄寫入工作p99_BatchAck的p95_Pull的p99與每GB的增量()
    {
        await using var host = await HubTestHost.StartAsync(clock: HourClock());
        var probe = new Probe(host);
        var writer = host.Services.GetRequiredService<SyncWriter>();
        var log = host.Services.GetRequiredService<EventLog>();
        var devices = await AddDevicesAsync(host, 4);
        await writer.CheckpointAsync();
        var sizeBefore = DatabaseBytes(host);

        var duration = TimeSpan.FromSeconds(60);
        var clock = Stopwatch.StartNew();
        long historyBytes = 0;
        long wireBytes = 0;
        long events = 0;
        var pulls = new LatencySamples();
        long pulled = 0;
        var finishing = 0;
        var replica = Task.Run(async () =>
        {
            // 副本：依類別游標拉 meta 與 item（同 WP4.3 的唯讀副本），追上了就等一下（真的副本由 Notify 喚醒）；
            // 回補結束之後拉到追上為止
            var cursors = new Dictionary<string, long> { [SyncClasses.Meta] = 0, [SyncClasses.Item] = 0 };
            while (true)
            {
                var last = Volatile.Read(ref finishing) == 1;
                var started = Stopwatch.GetTimestamp();
                var page = log.Pull(new PullRequest(cursors.Select(c => new PullCursor(c.Value, Cls: c.Key)).ToList(), SyncLimits.Default.MaxPullEvents,
                    SyncLimits.Default.MaxPullBytes));
                pulls.AddSince(started);
                foreach (var e in page.Events)
                    cursors[e.Cls] = Math.Max(cursors[e.Cls], e.Seq);
                Interlocked.Add(ref pulled, page.Events.Count);
                if (page.More)
                    continue;
                if (last)
                    return;
                await Task.Delay(100);
            }
        });

        await Task.WhenAll(devices.Select((d, n) => Task.Run(async () =>
        {
            var history = new Backfill(new Random(300 + n), d);
            var inFlight = Channel.CreateBounded<(Batch Batch, long Enqueued)>(new BoundedChannelOptions(SyncLimits.Default.MaxInFlightBatches) { SingleReader = true });
            // 幫浦：同一條連線的批次依序寫（同 DeviceHubRuntime 的批次佇列）；確認的延遲從排進佇列算起
            var pump = Task.Run(async () =>
            {
                await foreach (var (batch, enqueued) in inFlight.Reader.ReadAllAsync())
                {
                    var outcome = await probe.AppendAsync(d.Id, batch, enqueued);
                    outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null && r.Rejected == null);
                }
            });
            while (clock.Elapsed < duration && history.Next(out var batch, out var decodedBytes, out var batchWire))
            {
                await inFlight.Writer.WriteAsync((batch, Stopwatch.GetTimestamp()));
                Interlocked.Add(ref historyBytes, decodedBytes);
                Interlocked.Add(ref wireBytes, batchWire);
                Interlocked.Add(ref events, batch.Events.Count);
            }
            inFlight.Writer.Complete();
            await pump;
        })));
        var elapsed = clock.Elapsed;
        Volatile.Write(ref finishing, 1);
        await replica;

        await writer.CheckpointAsync();
        var sizeAfter = DatabaseBytes(host);
        var eventPayload = EventLogTests.Count(host, "SELECT IFNULL(SUM(LENGTH(CAST(payload AS BLOB))), 0) FROM events WHERE cls IN ('meta', 'item')");
        var itemBodies = EventLogTests.Count(host, "SELECT IFNULL(SUM(LENGTH(CAST(body AS BLOB))), 0) FROM items");
        var growth = sizeAfter - sizeBefore;
        Report(new()
        {
            ["scenario"] = 3, ["seconds"] = Math.Round(elapsed.TotalSeconds, 1), ["devices"] = 4, ["events"] = events, ["batches"] = probe.Job.Count,
            ["historyMB"] = Math.Round(historyBytes / 1048576.0, 1), ["wireMB"] = Math.Round(wireBytes / 1048576.0, 1),
            ["historyMBps"] = Math.Round(historyBytes / 1048576.0 / elapsed.TotalSeconds, 2),
            ["job"] = probe.Job.Summary(), ["precheck"] = probe.Precheck.Summary(), ["batchAck"] = probe.Ack.Summary(), ["pull"] = pulls.Summary(), ["pulled"] = pulled,
            ["syncDbGrowthMB"] = Math.Round(growth / 1048576.0, 1),
            ["syncDbGBPerHistoryGB"] = Math.Round((double)growth / historyBytes, 3),
            ["eventPayloadMB"] = Math.Round(eventPayload / 1048576.0, 1), ["itemBodyMB"] = Math.Round(itemBodies / 1048576.0, 1),
        });
        events.Should().BeGreaterThan(0);
        pulled.Should().Be(EventLogTests.Count(host, "SELECT COUNT(*) FROM events WHERE cls IN ('meta', 'item')"), "副本最後拉到全部（都是 full、沒刪除的對話）");
    }

    // ───── 工具 ─────

    /// <summary>
    /// 和 <see cref="EventLog.AppendBatchAsync"/> 相同的兩步，加上量時間（測試 <see cref="量測走的兩步與AppendBatchAsync的結果相同"/> 把關）
    /// </summary>
    private sealed class Probe(HubTestHost host)
    {
        private readonly EventLog log = host.Services.GetRequiredService<EventLog>();
        private readonly SyncWriter writer = host.Services.GetRequiredService<SyncWriter>();
        private readonly ConversationMaterializer materializer = host.Services.GetRequiredService<ConversationMaterializer>();

        public LatencySamples Precheck { get; private set; } = new();

        public LatencySamples Job { get; private set; } = new();

        /// <summary>從呼叫（或排進在途佇列）到寫入完成（hub 在這時送 BatchAck）</summary>
        public LatencySamples Ack { get; private set; } = new();

        public void Reset()
        {
            Precheck = new();
            Job = new();
            Ack = new();
        }

        /// <param name="enqueued">確認延遲的起點（排進在途佇列的時刻）；null＝從呼叫算起</param>
        public async Task<BatchOutcome> AppendAsync(string deviceId, Batch batch, long? enqueued = null)
        {
            var upstream = new UpstreamContext(deviceId, null, null);
            var started = Stopwatch.GetTimestamp();
            var prechecks = materializer.Precheck(batch, log.Limits);
            Precheck.AddSince(started);
            var job = Job;
            var outcome = await writer.WriteAsync(context =>
            {
                var at = Stopwatch.GetTimestamp();
                var result = log.AppendBatch(context, upstream, batch, prechecks);
                job.AddSince(at);
                return result;
            });
            Ack.AddSince(enqueued ?? started);
            return outcome;
        }
    }

    /// <summary>一台裝置與它下一個 deviceSeq（一台一個對話）</summary>
    private sealed class BenchDevice(string id, string conversationId)
    {
        private long next = 1;

        public string Id { get; } = id;

        public string ConversationId { get; } = conversationId;

        public SyncEvent Next(string kind, JToken payload, string? conversationId = null) =>
            ConversationEventLogTests.Ev(next++, kind, payload, conversationId ?? ConversationId);

        /// <summary>收回上一個 deviceSeq（產生了、但放不進這一批）</summary>
        public void Rewind() => next--;
    }

    /// <summary>同 <see cref="EventLogTests.AddDeviceAsync"/>，但 id 由呼叫端給（兩台主機放同一台裝置）</summary>
    private static async Task AddDeviceAsync(HubTestHost host, string id)
    {
        var record = new DeviceRecord
        {
            Id = id,
            Name = "量測",
            TokenHash = Guid.NewGuid().ToByteArray(),
            DevicePubkey = [1, 2, 3, 4],
            KeyStorage = "software",
            CreatedAt = 1,
        };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            db.Devices.Add(record);
            await db.SaveChangesAsync();
        }
        host.Services.GetRequiredService<DeviceDirectory>().Put(DeviceInfo.From(record));
    }

    private static async Task<List<BenchDevice>> AddDevicesAsync(HubTestHost host, int count)
    {
        var devices = new List<BenchDevice>();
        for (var i = 0; i < count; i++)
            devices.Add(new BenchDevice(await EventLogTests.AddDeviceAsync(host, "量測" + i.ToString(CultureInfo.InvariantCulture)), ConversationHarness.ConversationId(i + 1)));
        return devices;
    }

    /// <summary>
    /// 一台的回補（WP4.3 的順序：先所有對話的 conv.created，再由新到舊送內容）：每個對話 150 則上下、大小不一（文字、工具輸出、使用者訊息、通知），
    /// 夾著佇列與一輪的開始與結束；批次照桌面的 <see cref="OutboxRules"/>：≤500 筆、線上大小 ≤256 KB
    /// </summary>
    private sealed class Backfill(Random rng, BenchDevice device)
    {
        // 60 秒內送不完（開發機每台約 10 個對話／秒）：量的是整整 60 秒的回補
        private const int ConversationCount = 1000;
        private readonly Queue<(string Kind, JToken Payload, string Conversation)> pending = new();
        private int createdSent;
        private int conversation = ConversationCount;

        public bool Next(out Batch batch, out long decodedBytes, out long wireBytes)
        {
            var events = new List<SyncEvent>();
            decodedBytes = 0;
            wireBytes = 0;
            var limit = SyncLimits.Default.MaxBatchBytes - 8 * 1024;
            while (events.Count < SyncLimits.Default.MaxBatchEvents)
            {
                if (!TryPeek(out var next))
                    break;
                var e = device.Next(next.Kind, next.Payload, next.Conversation);
                var size = PayloadRules.WireSize(e.Payload);
                if (events.Count > 0 && wireBytes + size > limit)
                {
                    device.Rewind();
                    break;
                }
                pending.Dequeue();
                events.Add(e);
                wireBytes += size;
                decodedBytes += Encoding.UTF8.GetByteCount(ConversationWire.Serialize(next.Payload));
            }
            batch = EventLogTests.Batch([.. events]);
            return events.Count > 0;
        }

        private bool TryPeek(out (string Kind, JToken Payload, string Conversation) next)
        {
            if (pending.Count == 0)
                Refill();
            return pending.TryPeek(out next);
        }

        private void Refill()
        {
            if (createdSent < ConversationCount)
            {
                for (var i = 0; i < 100 && createdSent < ConversationCount; i++, createdSent++)
                    pending.Enqueue((K.Created, Meta("回補 " + createdSent.ToString(CultureInfo.InvariantCulture)), Id(createdSent)));
                return;
            }
            if (conversation == 0)
                return;
            conversation--;
            var id = Id(conversation);
            var count = 100 + rng.Next(100);
            for (var ord = 1; ord <= count; ord++)
            {
                var roll = rng.Next(100);
                JObject item;
                if (roll < 45)
                {
                    item = Item(MixedText(rng, 300 + rng.Next(2700)), "Done", "i" + ord.ToString(CultureInfo.InvariantCulture), ord, 1);
                }
                else if (roll < 80)
                {
                    item = Item(null, "Done", "i" + ord.ToString(CultureInfo.InvariantCulture), ord, 1);
                    item["kind"] = "Tool";
                    item["title"] = "Bash";
                    item["detail"] = MixedText(rng, 100 + rng.Next(400));
                    item["output"] = MixedText(rng, 500 + rng.Next(11500));
                }
                else if (roll < 95)
                {
                    item = Item(MixedText(rng, 50 + rng.Next(450)), "Done", "i" + ord.ToString(CultureInfo.InvariantCulture), ord, 1);
                    item["kind"] = "User";
                }
                else
                {
                    item = Item("通知 " + ord.ToString(CultureInfo.InvariantCulture), "Done", "i" + ord.ToString(CultureInfo.InvariantCulture), ord, 1);
                    item["kind"] = "Notice";
                }
                pending.Enqueue((K.Item, item, id));
            }
            pending.Enqueue((K.Queue, Queue("下一步"), id));
            pending.Enqueue((K.Turn, ConversationEventLogTests.Turn(ConversationTurnPhases.Completed), id));
        }

        /// <summary>這台的第 n 個對話（前 8 碼是這台的編號，各台不重複）</summary>
        private string Id(int n) => device.ConversationId[^8..] + n.ToString("x24", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 接近 128 KB 的 Tool 項目：text、detail、output 各約 30 KB，diff 30 行各約 1 KB。<paramref name="hit"/>＝假：每個字串都是
    /// 「只差一個字元就命中」的片段（GitHub、xAI、Google 存取權杖少一個字、OpenAI 舊式少一個字、AWS 存取金鑰少一個字、JWT 第二段少一個字）；
    /// 真：每個字串裡另有完整的權杖（要再遮蔽）。權杖的前綴一律在執行時組字串（契約 D44 的慣例）
    /// </summary>
    private static JObject LargeTool(Random rng, bool hit, string id, long ord, long ver)
    {
        var item = Item(NearMiss(rng, 30 * 1024, hit), "Done", id, ord, ver);
        item["kind"] = "Tool";
        item["title"] = "接近命中的輸出";
        item["detail"] = NearMiss(rng, 30 * 1024, hit);
        item["output"] = NearMiss(rng, 30 * 1024, hit);
        item["diff"] = new JArray(Enumerable.Range(0, 30).Select(_ => new JObject { ["t"] = "add", ["s"] = NearMiss(rng, 1000, hit) }));
        return item;
    }

    private static string NearMiss(Random rng, int length, bool hit)
    {
        var text = new StringBuilder(length + 64);
        if (hit)
            text.Append(GitHub((char)('a' + rng.Next(26)))).Append(' ');
        while (text.Length < length)
        {
            switch (rng.Next(6))
            {
                case 0:
                    text.Append("gh").Append("p_").Append(Random(rng, 19, Alnum));
                    break;
                case 1:
                    text.Append("xa").Append("i-").Append(Random(rng, 19, Alnum));
                    break;
                case 2:
                    text.Append("ya").Append("29.").Append(Random(rng, 19, Alnum));
                    break;
                case 3:
                    text.Append("s").Append("k-").Append(Random(rng, 39, Alnum));
                    break;
                case 4:
                    text.Append("AK").Append("IA").Append(Random(rng, 15, Alnum[..26] + "0123456789"));
                    break;
                default:
                    text.Append("ey").Append('J').Append(Random(rng, 12, Alnum)).Append(".ey").Append('J').Append(Random(rng, 9, Alnum));
                    break;
            }
            text.Append(' ');
        }
        return text.ToString(0, Math.Min(text.Length, length));
    }

    private static string Random(Random rng, int length, string alphabet)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(chars);
    }

    /// <summary>中英文夾雜、有換行的文字（壓縮率接近真的對話）</summary>
    private static string MixedText(Random rng, int length)
    {
        string[] words = ["檔案", "函式", "測試", "伺服器", "設定", "錯誤", "已完成", "修改", "the", "result", "build", "error", "value", "return", "async", "public", "目錄", "資料庫"];
        var text = new StringBuilder(length + 16);
        while (text.Length < length)
        {
            text.Append(words[rng.Next(words.Length)]);
            text.Append(rng.Next(12) == 0 ? '\n' : ' ');
            if (rng.Next(20) == 0)
                text.Append(rng.Next(100000).ToString(CultureInfo.InvariantCulture)).Append(' ');
        }
        return text.ToString(0, length);
    }

    private static async Task UntilAsync(long start, TimeSpan due)
    {
        var wait = due - Stopwatch.GetElapsedTime(start);
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);
    }

    private static long DatabaseBytes(HubTestHost host)
    {
        var db = new FileInfo(host.Paths.SyncDb);
        var wal = new FileInfo(host.Paths.SyncDb + "-wal");
        return (db.Exists ? db.Length : 0) + (wal.Exists ? wal.Length : 0);
    }

    private void Report(Dictionary<string, object?> values)
    {
        values["machine"] = Environment.MachineName;
        values["cpus"] = Environment.ProcessorCount;
        values["build"] = BuildInfo.Current.Build;
        output.WriteLine(JsonConvert.SerializeObject(values, Formatting.None));
    }

    /// <summary>起點是現在截到整點的假時鐘（D43）</summary>
    private static ManualTimeProvider HourClock()
    {
        var now = DateTimeOffset.UtcNow;
        return new ManualTimeProvider(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero));
    }
}
