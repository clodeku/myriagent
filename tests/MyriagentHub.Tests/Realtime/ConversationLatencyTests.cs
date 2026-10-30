using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// S7 延遲（WP4.2 計畫步 14；只在 <c>MYRIAGENT_HUB_PERF=1</c> 時跑，約一分鐘）：真的 Kestrel 前面接 <see cref="DelayProxy"/>（每個方向 20 ms），
/// 裝置與瀏覽器都經過它。5 個對話各 4 Hz 送 LiveRelay（同一則串流中的項目），每秒一次另以 PublishBatch 送一則持久的 <c>conv.item</c>；
/// 項目文字帶送出時的單調時鐘。瀏覽器開著這 5 個對話，收到 <c>item</c> 時算延遲，分開統計 LiveRelay→item 與 PublishBatch→item 的 p50、p95、p99，
/// 寫成一行 JSON（記進計畫附錄 B）。斷言兩者都是 p95 ≤500 ms、p99 ≤1 秒
/// </summary>
[Collection(PerfCollection.Name)]
public sealed partial class ConversationLatencyTests(ITestOutputHelper output)
{
    private const int Conversations = 5;
    private const int Hz = 4;
    private const int Seconds = 50;
    private static readonly TimeSpan OneWay = TimeSpan.FromMilliseconds(20);

    [EnvFact("MYRIAGENT_HUB_PERF", "S7 延遲量測，約一分鐘")]
    public async Task S7_LiveRelay與PublishBatch到瀏覽器item_p95不超過500ms_p99不超過1秒()
    {
        await using var host = await HubTestHost.StartAsync(kestrel: true, clock: HourClock());
        await using var proxy = DelayProxy.Start(host.KestrelAddress!, OneWay);
        using var device = await TestDevice.AddAsync(host, "量測");
        var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(proxy.BaseAddress, device.Token);
        await using var deviceConnection = client;
        await client.HandshakeAsync(device, HandshakeRules.SpkiSha256(seen), features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1]);
        var ids = Enumerable.Range(1, Conversations).Select(ConversationHarness.ConversationId).ToArray();
        var send = new SemaphoreSlim(1, 1);
        long nextSeq = 1;
        await PublishAsync(client, send, () => ids.Select(id => ConversationEventLogTests.Ev(nextSeq++, K.Created, ConversationSamples.Meta("量測 " + id), conversationId: id)).ToArray());

        // 瀏覽器：經轉送登入、連瀏覽器 hub、看著並開著這 5 個對話
        var cookies = new CookieContainer();
        var handler = new CookieHandler(cookies)
        {
            InnerHandler = new SocketsHttpHandler { UseCookies = false, SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } },
        };
        using var http = new HubClient(new HttpClient(handler) { BaseAddress = proxy.BaseAddress }, handler);
        await OwnerFlows.LoginOwnerAsync(http);
        await using var browser = await BrowserHubClient.ConnectKestrelAsync(proxy.BaseAddress, cookies);
        await browser.ReadyAsync();
        await browser.Connection.InvokeAsync(BrowserHubMethods.ClientState, new JObject
        {
            ["visible"] = true,
            ["focused"] = true,
            ["openConversationIds"] = new JArray(ids),
            ["tzOffsetMinutes"] = -480,
        });
        foreach (var id in ids)
        {
            (await browser.CmdAsync(new { cmd = "open", conversationId = id }))["accepted"]!.Value<bool>().Should().BeTrue();
            await browser.WaitForAsync("conversation", m => m["conversationId"]!.Value<string>() == id);
        }
        (await client.WaitForAsync<WatchChanged>(w => w.ConversationIds.Count == Conversations, 10_000)).ConversationIds.Should().BeEquivalentTo(ids);

        var live = new LatencySamples();
        var durable = new LatencySamples();
        using var stopReceiving = new CancellationTokenSource();
        var receiver = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in browser.Messages.Reader.ReadAllAsync(stopReceiving.Token))
                {
                    if (message["type"]?.Value<string>() != "item" || message["item"] is not JObject item)
                        continue;
                    var sentAt = SentAt().Match(item["html"]?.Value<string>() ?? string.Empty);
                    if (!sentAt.Success)
                        continue;
                    var ms = Stopwatch.GetElapsedTime(long.Parse(sentAt.Groups[1].Value, CultureInfo.InvariantCulture)).TotalMilliseconds;
                    (item["id"]!.Value<string>()!.StartsWith("live", StringComparison.Ordinal) ? live : durable).Add(ms);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // 每個對話照固定的時刻表送（第 k 次在開始後 k×250 ms），每 4 次另送一則持久的
        var ticks = Seconds * Hz;
        var sentLive = 0;
        var sentDurable = 0;
        var start = Stopwatch.GetTimestamp();
        var senders = ids.Select((id, n) => Task.Run(async () =>
        {
            for (var k = 0; k < ticks; k++)
            {
                var due = TimeSpan.FromMilliseconds(1000.0 / Hz * k + n * 13);
                var wait = due - Stopwatch.GetElapsedTime(start);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);
                var item = ConversationSamples.Item(Stamp(), "Running", "live1", 1, k + 1);
                await send.WaitAsync();
                try
                {
                    await client.Connection.SendAsync(DeviceHubMethods.LiveRelay, new LiveRelay(id, 1, 1, k + 1, item.ToString(Formatting.None)));
                }
                finally
                {
                    send.Release();
                }
                Interlocked.Increment(ref sentLive);
                if (k % Hz == 2)
                {
                    var ord = 1000 + k;
                    await PublishAsync(client, send, () =>
                        [ConversationEventLogTests.Ev(nextSeq++, K.Item, ConversationSamples.Item(Stamp(), "Done", "d" + k.ToString(CultureInfo.InvariantCulture), ord, 1), conversationId: id)]);
                    Interlocked.Increment(ref sentDurable);
                }
            }
        })).ToList();
        await Task.WhenAll(senders);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await stopReceiving.CancelAsync();
        await receiver;

        var counters = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
        var report = new Dictionary<string, object?>
        {
            ["test"] = "S7",
            ["oneWayDelayMs"] = OneWay.TotalMilliseconds,
            ["conversations"] = Conversations,
            ["hz"] = Hz,
            ["seconds"] = Seconds,
            ["live"] = Merge(live.Summary(), ("sent", sentLive)),
            ["durable"] = Merge(durable.Summary(), ("sent", sentDurable)),
            ["liveDropped"] = counters.LiveDropped,
            ["machine"] = Environment.MachineName,
            ["cpus"] = Environment.ProcessorCount,
            ["build"] = BuildInfo.Current.Build,
        };
        output.WriteLine(JsonConvert.SerializeObject(report, Formatting.None));

        (sentLive + sentDurable).Should().BeGreaterThanOrEqualTo(1000, "至少 1,000 則");
        live.Count.Should().BeGreaterThanOrEqualTo(sentLive * 9 / 10, "絕大部分的即時更新都推到瀏覽器（同一輪合併的只算最後一則）");
        durable.Count.Should().Be(sentDurable, "每一則持久的項目都推到瀏覽器");
        live.Percentile(0.5).Should().BeGreaterThanOrEqualTo(2 * OneWay.TotalMilliseconds, "裝置到伺服器、伺服器到瀏覽器各經過轉送一次（轉送真的有加延遲）");
        live.Percentile(0.95).Should().BeLessThanOrEqualTo(500);
        live.Percentile(0.99).Should().BeLessThanOrEqualTo(1000);
        durable.Percentile(0.95).Should().BeLessThanOrEqualTo(500);
        durable.Percentile(0.99).Should().BeLessThanOrEqualTo(1000);
    }

    /// <summary>項目文字：送出時的單調時鐘（Stopwatch 的 timestamp；同一個程序裡收到時相減）</summary>
    private static string Stamp() => "t=" + Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("t=([0-9]+)")]
    private static partial Regex SentAt();

    /// <summary>送一批（同一台裝置的批次依序送：deviceSeq 在鎖裡配發）；BatchAck 不等</summary>
    private static async Task PublishAsync(DeviceHubClient client, SemaphoreSlim send, Func<SyncEvent[]> events)
    {
        await send.WaitAsync();
        try
        {
            await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, EventLogTests.Batch(events()));
        }
        finally
        {
            send.Release();
        }
    }

    private static Dictionary<string, object> Merge(Dictionary<string, object> summary, (string Key, object Value) extra)
    {
        summary[extra.Key] = extra.Value;
        return summary;
    }

    /// <summary>起點是現在截到整點的假時鐘（D43）</summary>
    private static ManualTimeProvider HourClock()
    {
        var now = DateTimeOffset.UtcNow;
        return new ManualTimeProvider(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero));
    }
}
