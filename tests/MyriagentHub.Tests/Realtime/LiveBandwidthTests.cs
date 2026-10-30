using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 即時更新的流量（甲5；設計 docs/plans/2026-10-23-live-delta-and-replica-live.md 第 6 節；只在 <c>MYRIAGENT_HUB_PERF=1</c> 時跑，約兩分鐘）：
/// 真的 Kestrel，裝置與瀏覽器各經一個 <see cref="DelayProxy"/>（每個方向 20 ms），代理數兩邊實際走在線上的位元組（含 TLS、SignalR 與 WebSocket 的框）。
/// 一則串流中的回覆每 250 ms 長約 1 KB（一段落），比較三組：
/// <list type="bullet">
/// <item>整則：裝置只宣告 live1（每則送整筆 LiveRelay）、瀏覽器沒宣告 patch1（每則收整筆 item）——上線前的做法；50 段（整則上限 64 KB 以內）</item>
/// <item>增量：裝置宣告 live2（第一則整筆、之後 LiveDelta）、瀏覽器宣告 patch1（itemPatch）；同樣 50 段</item>
/// <item>增量、長回覆：180 段（約 180 KB，整則模式在 64 KB 就停住）</item>
/// </list>
/// 每組都核對瀏覽器重組出的最後一版含最後一段。寫一行 JSON（記進多電腦同步設計 14.23）；斷言增量模式兩段各 ≤ 最後大小 × 2 ＋ 則數 × 512 B
/// </summary>
[Collection(PerfCollection.Name)]
public sealed class LiveBandwidthTests(ITestOutputHelper output)
{
    private static readonly TimeSpan OneWay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private const string ItemId = "live1";
    private static readonly string[] PatchFields = ["html", "detailHtml", "detail", "output"];

    [EnvFact("MYRIAGENT_HUB_PERF", "即時更新的流量量測，約兩分鐘")]
    public async Task 增量與整則_裝置到伺服器與伺服器到瀏覽器的位元組()
    {
        var full = await RunAsync(delta: false, segments: 50);
        var delta = await RunAsync(delta: true, segments: 50);
        var longer = await RunAsync(delta: true, segments: 180);

        output.WriteLine(JsonConvert.SerializeObject(new Dictionary<string, object?>
        {
            ["test"] = "live-bandwidth",
            ["oneWayDelayMs"] = OneWay.TotalMilliseconds,
            ["intervalMs"] = Interval.TotalMilliseconds,
            ["full50"] = full,
            ["delta50"] = delta,
            ["delta180"] = longer,
            ["machine"] = Environment.MachineName,
            ["build"] = BuildInfo.Current.Build,
        }, Formatting.None));

        foreach (var run in new[] { full, delta, longer })
            run.Delivered.Should().BeTrue($"瀏覽器重組出的最後一版含最後一段（{run.Mode}）");
        foreach (var run in new[] { delta, longer })
        {
            var budget = run.FinalTextBytes * 2 + run.Sent * 512L;
            run.DeviceUpBytes.Should().BeLessThanOrEqualTo(budget, $"裝置→伺服器 ≤ 最後大小 × 2 ＋ 則數 × 512 B（{run.Mode}）");
            run.BrowserDownBytes.Should().BeLessThanOrEqualTo(budget, $"伺服器→瀏覽器 ≤ 最後大小 × 2 ＋ 則數 × 512 B（{run.Mode}）");
            run.Resyncs.Should().Be(0, "沒有對不上的增量");
        }
        delta.DeviceUpBytes.Should().BeLessThan(full.DeviceUpBytes / 5, "同一段串流，增量比整則省很多");
        delta.BrowserDownBytes.Should().BeLessThan(full.BrowserDownBytes / 5);
    }

    private async Task<RunResult> RunAsync(bool delta, int segments)
    {
        await using var host = await HubTestHost.StartAsync(kestrel: true);
        await using var deviceProxy = DelayProxy.Start(host.KestrelAddress!, OneWay);
        await using var browserProxy = DelayProxy.Start(host.KestrelAddress!, OneWay);
        using var device = await TestDevice.AddAsync(host, "量測");
        var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(deviceProxy.BaseAddress, device.Token);
        await using var deviceConnection = client;
        string[] features = delta
            ? [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2]
            : [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
        await client.HandshakeAsync(device, HandshakeRules.SpkiSha256(seen), features: features);
        var id = ConversationHarness.ConversationId(1);
        await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch,
            EventLogTests.Batch([ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta("流量 " + id), conversationId: id)]));

        var cookies = new CookieContainer();
        var handler = new CookieHandler(cookies)
        {
            InnerHandler = new SocketsHttpHandler { UseCookies = false, SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } },
        };
        using var http = new HubClient(new HttpClient(handler) { BaseAddress = browserProxy.BaseAddress }, handler);
        await OwnerFlows.LoginOwnerAsync(http);
        await using var browser = await BrowserHubClient.ConnectKestrelAsync(browserProxy.BaseAddress, cookies);
        await browser.ReadyAsync();
        var state = new JObject
        {
            ["visible"] = true,
            ["focused"] = true,
            ["openConversationIds"] = new JArray(id),
            ["tzOffsetMinutes"] = -480,
        };
        if (delta)
            state["features"] = new JArray("patch1");
        await browser.Connection.InvokeAsync(BrowserHubMethods.ClientState, state);
        (await browser.CmdAsync(new { cmd = "open", conversationId = id }))["accepted"]!.Value<bool>().Should().BeTrue();
        await browser.WaitForAsync("conversation", m => m["conversationId"]!.Value<string>() == id);
        await client.WaitForAsync<WatchChanged>(w => w.ConversationIds.Contains(id), 10_000);
        await Task.Delay(500); // 交握與開啟的尾巴送完再開始數

        var lastMarker = Marker(segments);
        JObject? shown = null;
        var items = 0;
        var patches = 0;
        var patchFailed = 0;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopReceiving = new CancellationTokenSource();
        var receiver = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in browser.Messages.Reader.ReadAllAsync(stopReceiving.Token))
                {
                    switch (message["type"]?.Value<string>())
                    {
                        case "item" when message["item"] is JObject item && item["id"]?.Value<string>() == ItemId:
                            items++;
                            shown = item;
                            break;
                        case "itemPatch" when message["id"]?.Value<string>() == ItemId:
                            patches++;
                            var next = ApplyPatch(shown, message);
                            if (next is null)
                                patchFailed++;
                            else
                                shown = next;
                            break;
                        default:
                            continue;
                    }
                    if (shown?["html"]?.Value<string>()?.Contains(lastMarker, StringComparison.Ordinal) == true)
                        delivered.TrySetResult();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var upBefore = deviceProxy.UpBytes;
        var downBefore = browserProxy.DownBytes;
        var text = new StringBuilder();
        LiveLongFields? basis = null;
        long basisVer = 0;
        var start = Stopwatch.GetTimestamp();
        for (var k = 1; k <= segments; k++)
        {
            var wait = Interval * (k - 1) - Stopwatch.GetElapsedTime(start);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
            text.Append(Segment(k));
            var wire = ConversationSamples.Item(text.ToString(), "Running", ItemId, 1, k);
            if (delta && basis is { } b)
                await client.Connection.SendAsync(DeviceHubMethods.LiveDelta, LiveDeltaRules.Encode(id, 1, 1, k, basisVer, b, wire));
            else
                await client.Connection.SendAsync(DeviceHubMethods.LiveRelay, new LiveRelay(id, 1, 1, k, wire.ToString(Formatting.None)));
            basis = LiveDeltaRules.FieldsOf(wire);
            basisVer = k;
        }
        var arrived = await Task.WhenAny(delivered.Task, Task.Delay(TimeSpan.FromSeconds(10))) == delivered.Task;
        await Task.Delay(TimeSpan.FromSeconds(1));
        var deviceUp = deviceProxy.UpBytes - upBefore;
        var browserDown = browserProxy.DownBytes - downBefore;
        await stopReceiving.CancelAsync();
        await receiver;

        var intake = host.Services.GetRequiredService<LiveRelayIntake>();
        var lastHtml = shown?["html"]?.Value<string>() ?? string.Empty;
        return new RunResult(
            delta ? "delta" : "full", segments, segments, Encoding.UTF8.GetByteCount(text.ToString()), deviceUp, browserDown,
            items, patches, patchFailed, intake.DeltaApplied, intake.ResyncSent, arrived,
            Encoding.UTF8.GetByteCount(lastHtml), lastHtml.Length > 120 ? lastHtml[^120..] : lastHtml);
    }

    /// <summary>第 k 段：一個段落，約 1 KB（UTF-8），開頭是可以找的記號</summary>
    private static string Segment(int k) =>
        Marker(k) + "：" + string.Concat(Enumerable.Repeat("串流中的回覆文字，", 33)) + "（" + k.ToString(CultureInfo.InvariantCulture) + "）\n\n";

    private static string Marker(int k) => "第" + k.ToString(CultureInfo.InvariantCulture) + "段結尾記號";

    /// <summary>網頁 liverules.applyItemPatch 的做法：set 的欄位＋長欄位（有 op 的照 at、s 拼，長度要等於 n；沒有的沿用基底）</summary>
    private static JObject? ApplyPatch(JObject? shown, JObject patch)
    {
        if (shown is null || patch["set"] is not JObject set || patch["ops"] is not JArray ops)
            return null;
        var next = (JObject)set.DeepClone();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in ops.OfType<JObject>())
        {
            var field = op["f"]!.Value<string>()!;
            var old = shown[field]?.Value<string>() ?? string.Empty;
            var at = op["at"]!.Value<int>();
            if (at > old.Length)
                return null;
            var value = old[..at] + op["s"]!.Value<string>();
            if (value.Length != op["n"]!.Value<int>())
                return null;
            next[field] = value;
            touched.Add(field);
        }
        foreach (var field in PatchFields)
        {
            if (!touched.Contains(field) && shown[field] is JValue { Type: JTokenType.String } value)
                next[field] = value.Value<string>();
        }
        return next;
    }

    private sealed record RunResult(
        string Mode, int Segments, int Sent, long FinalTextBytes, long DeviceUpBytes, long BrowserDownBytes,
        int BrowserItems, int BrowserPatches, int BrowserPatchFailed, long DeltaApplied, long Resyncs, bool Delivered,
        long LastHtmlBytes, string LastHtmlTail);
}
