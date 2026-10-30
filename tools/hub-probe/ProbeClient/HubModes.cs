using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace MyriagentHub.ProbeClient;

/// <summary>
/// 需要第 3 階段裝置 hub 的模式（研究原型的作法原樣保留）：用原型的測試端點（/test/mint-device、/test/push、/test/broadcast、
/// /test/invoke、/test/slow、/test/stats、/test/swap-selfsigned）與 hub 方法（WhoAmI、Echo、Publish、PublishBatch、Resume、Command、
/// AskCallerBack），以及原型的配對端點（/api/pair/*）。第 3 階段的裝置 hub 與測試掛鉤定稿後，把這些名稱改成正式的。
/// 第 2 階段的 MyriagentHub 沒有這些端點：執行前先檢查 /test/stats，沒有就印 unsupported、結束碼 3。
/// </summary>
internal static class HubModes
{
    public static async Task<int> RunAsync(ProbeContext ctx, string mode)
    {
        if (!await SupportsAsync(ctx, mode))
            return Program.ExitUnsupported;
        var probe = new HubProbe(ctx);
        switch (mode)
        {
            case "conc-hub": await probe.ConcurrencyAsync(); break;
            case "size": await probe.MessageSizeAsync(); break;
            case "invoke": await probe.InvokeAsync(); break;
            case "replay": await probe.ReplayAsync(); break;
            case "soak": await probe.SoakAsync(); break;
            case "blip": await probe.BlipAsync(); break;
            case "backpressure": await probe.BackpressureAsync(); break;
            case "stall": await probe.StallAsync(); break;
            case "pubrate": await probe.PubRateAsync(); break;
            case "certswap": await probe.CertSwapAsync(); break;
            case "pair": await probe.PairAsync(); break;
        }
        return 0;
    }

    private static async Task<bool> SupportsAsync(ProbeContext ctx, string mode)
    {
        int status;
        try
        {
            using var r = await ctx.Http.GetAsync(ctx.BaseUrl + "/test/stats");
            status = (int)r.StatusCode;
        }
        catch (HttpRequestException ex)
        {
            ctx.Emit(new { @event = "unsupported", mode, error = ex.Message });
            return false;
        }
        if (status == 200)
            return true;
        ctx.Emit(new
        {
            @event = "unsupported",
            mode,
            testStatsStatus = status,
            reason = $"伺服器沒有原型的測試端點（/test/*）與 hub 方法（{ctx.HubPath} 是正式的裝置 hub）：原型的模式不支援；正式的 MyriagentHub 跑 tls、conc、load",
        });
        return false;
    }
}

/// <summary>原型 ProbeClient 的 hub 實驗（第 3 階段才能跑）</summary>
internal sealed class HubProbe(ProbeContext ctx)
{
    private readonly HttpClient http = ctx.Http;
    private readonly string baseUrl = ctx.BaseUrl;

    // conc-hub：N 條 hub 連線＋M 個同時進行的慢請求（IIS 用戶端版限制 3／10 個同時要求；Kestrel 不該有）
    public async Task ConcurrencyAsync()
    {
        var n = ctx.IntOpt("n", 50);
        var slow = ctx.IntOpt("slow", 30);
        var (_, token) = await MintDeviceAsync("conc");
        var sw = Stopwatch.StartNew();
        var conns = Enumerable.Range(0, n).Select(_ => NewHub(token)).ToList();
        await Task.WhenAll(conns.Select(c => c.StartAsync()));
        var startMs = sw.ElapsedMilliseconds;
        var who = await Task.WhenAll(conns.Select(c => c.InvokeAsync<JsonElement>("WhoAmI")));
        var transports = who.GroupBy(w => w.GetProperty("transport").GetString()).ToDictionary(g => g.Key ?? "?", g => g.Count());

        // 所有 WebSocket 都開著的同時，再丟 slow 個「每個 2 秒」的請求：全部約 2 秒內完成＝沒有排隊
        sw.Restart();
        await Task.WhenAll(Enumerable.Range(0, slow).Select(_ => http.GetFromJsonAsync<JsonElement>($"{baseUrl}/test/slow?ms=2000")));
        var slowMs = sw.ElapsedMilliseconds;
        var stats = await http.GetFromJsonAsync<JsonElement>($"{baseUrl}/test/stats");
        ctx.Emit(new
        {
            @event = "conc-hub",
            connections = n,
            startAllMs = startMs,
            transports,
            slowRequests = slow,
            slowAllCompletedMs = slowMs,
            serverConnections = stats.GetProperty("connections").GetInt32(),
            serverWorkingSetMB = stats.GetProperty("workingSetMB").GetDouble(),
            serverPrivateMB = stats.GetProperty("privateMB").GetDouble(),
        });
        await Task.WhenAll(conns.Select(c => c.DisposeAsync().AsTask()));
    }

    // size：用戶端→伺服器預設 32 KB 上限；伺服器→.NET 用戶端的大訊息
    public async Task MessageSizeAsync()
    {
        var (deviceId, token) = await MintDeviceAsync("size");
        foreach (var kb in new[] { 31, 33 })
        {
            var c = NewHub(token);
            Exception? closed = null;
            var closedTcs = new TaskCompletionSource();
            c.Closed += e =>
            {
                closed = e;
                closedTcs.TrySetResult();
                return Task.CompletedTask;
            };
            await c.StartAsync();
            string result;
            try
            {
                var len = await c.InvokeAsync<int>("Echo", new string('a', kb * 1024 - 20)).WaitAsync(TimeSpan.FromSeconds(10));
                result = "ok:" + len;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Task.WhenAny(closedTcs.Task, Task.Delay(3000));
                result = ex.GetType().Name + ": " + ex.Message + " | closed=" + closed?.Message;
            }
            ctx.Emit(new { @event = "size-up", kb, result });
            await c.DisposeAsync();
        }

        // 伺服器→用戶端：1 筆 sizeKB（預設 .NET 用戶端 ApplicationMaxBufferSize＝1MB）
        foreach (var (kb, appMax) in new[] { (512, 0L), (2048, 0L), (2048, 8L * 1024 * 1024) })
        {
            var got = new TaskCompletionSource<int>();
            var c = NewHub(token, o =>
            {
                if (appMax > 0)
                    o.ApplicationMaxBufferSize = appMax;
            });
            c.On<int, string>("Push", (_, payload) => got.TrySetResult(payload.Length));
            await c.StartAsync();
            var push = http.PostAsJsonAsync($"{baseUrl}/test/push", new { deviceId, count = 1, size = kb * 1024 });
            var winner = await Task.WhenAny(got.Task, Task.Delay(8000));
            ctx.Emit(new { @event = "size-down", kb, clientApplicationMaxBufferSize = appMax == 0 ? "預設" : appMax.ToString(System.Globalization.CultureInfo.InvariantCulture), received = winner == got.Task ? got.Task.Result : -1, state = c.State.ToString() });
            await c.DisposeAsync();
            try
            {
                await push.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or HttpRequestException)
            {
            }
        }
    }

    // invoke：伺服器主動呼叫指定裝置並等回覆（client results）
    public async Task InvokeAsync()
    {
        var (aId, aToken) = await MintDeviceAsync("invoke-a");
        var (_, bToken) = await MintDeviceAsync("invoke-b");
        var a = NewHub(aToken);
        a.On<string, string>("Command", cmd => "ack:" + cmd);
        await a.StartAsync();
        var b = NewHub(bToken);
        b.On<string, string>("Command", cmd => "b-ack:" + cmd);
        await b.StartAsync();

        // (a) hub 以外（IHubContext）叫裝置 A
        var r1 = await (await http.PostAsync($"{baseUrl}/test/invoke?deviceId={aId}", null)).Content.ReadAsStringAsync();
        // (b) 裝置 B 呼叫 hub.Command → hub 方法內 InvokeAsync 裝置 A（目標是別的連線）
        var sw = Stopwatch.StartNew();
        var r2 = await b.InvokeAsync<string>("Command", aId, "{\"cmd\":\"ping\"}");
        var r2ms = sw.ElapsedMilliseconds;
        // (c) hub 方法內 InvokeAsync「呼叫者自己」：MaximumParallelInvocationsPerClient=1 時預期逾時
        string r3;
        sw.Restart();
        try
        {
            r3 = await a.InvokeAsync<string>("AskCallerBack").WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is TimeoutException or HubException)
        {
            r3 = ex.GetType().Name + ": " + ex.Message;
        }
        ctx.Emit(new { @event = "invoke", fromHubContext = r1, viaHubMethod = r2, viaHubMethodMs = r2ms, askCallerBack = r3, askCallerBackMs = sw.ElapsedMilliseconds });
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    // replay：B 離線期間 A 發 100 則，B 重連後 Resume(lastSeq) 要剛好補回 100 則；A 重送不會重複
    public async Task ReplayAsync()
    {
        var (aId, aToken) = await MintDeviceAsync("replay-a");
        var (_, bToken) = await MintDeviceAsync("replay-b");
        var a = NewHub(aToken);
        await a.StartAsync();
        var received = new ConcurrentDictionary<long, string>();
        long lastSeq = 0;
        HubConnection NewSubscriber()
        {
            var b = NewHub(bToken);
            b.On<JsonElement>("Event", ev =>
            {
                var seq = ev.GetProperty("seq").GetInt64();
                if (ev.GetProperty("deviceId").GetString() != aId)
                    return; // 只看 A 的事件
                received[seq] = ev.GetProperty("clientEventId").GetString()!;
                lastSeq = Math.Max(lastSeq, seq);
            });
            return b;
        }

        var conv = "conv-" + Guid.NewGuid().ToString("N")[..6];
        var b1 = NewSubscriber();
        await b1.StartAsync();
        for (var i = 0; i < 10; i++)
            await a.InvokeAsync<long>("Publish", conv, $"e{i}", "item", $"{{\"i\":{i}}}");
        await Task.Delay(300);
        var liveBefore = received.Count;
        var seqAtDisconnect = lastSeq;
        await b1.DisposeAsync(); // B 離線

        var seqs = new List<long>();
        for (var i = 10; i < 110; i++)
            seqs.Add(await a.InvokeAsync<long>("Publish", conv, $"e{i}", "item", $"{{\"i\":{i}}}"));

        var b2 = NewSubscriber();
        await b2.StartAsync();
        var replayed = new List<JsonElement>();
        for (; ; )
        {
            var batch = await b2.InvokeAsync<List<JsonElement>>("Resume", seqAtDisconnect, 500);
            replayed.AddRange(batch.Where(e => e.GetProperty("deviceId").GetString() == aId));
            if (batch.Count < 500)
                break;
        }
        var replayedIds = replayed.Select(e => e.GetProperty("clientEventId").GetString()).ToList();
        var expectedIds = Enumerable.Range(10, 100).Select(i => $"e{i}").ToList();

        // A 重送 e50～e59（同 clientEventId）：回傳原序號，B 不應再收到即時事件
        var before = received.Count;
        var dupSeqs = new List<long>();
        for (var i = 50; i < 60; i++)
            dupSeqs.Add(await a.InvokeAsync<long>("Publish", conv, $"e{i}", "item", "{\"dup\":true}"));
        await Task.Delay(500);
        ctx.Emit(new
        {
            @event = "replay",
            liveBeforeDisconnect = liveBefore,
            replayedCount = replayed.Count,
            replayedInOrderAndComplete = replayedIds.SequenceEqual(expectedIds),
            duplicatePublishReturnedOriginalSeq = dupSeqs.SequenceEqual(seqs.Skip(40).Take(10)),
            liveEventsFromDuplicates = received.Count - before,
        });
        await a.DisposeAsync();
        await b2.DisposeAsync();
    }

    // soak：長時間收發（給「伺服器重啟」實驗）：A 每 100ms 發一則（outbox：失敗留著重送）；B 收即時＋重連後補送
    public async Task SoakAsync()
    {
        var seconds = ctx.IntOpt("seconds", 40);
        var stopFile = ctx.Opt("stopfile", "");
        var stateful = ctx.Opt("stateful", "true") == "true";
        var (aId, aToken) = await MintDeviceAsync("soak-a");
        var (_, bToken) = await MintDeviceAsync("soak-b");
        var timeline = new ConcurrentQueue<string>();
        var t0 = Stopwatch.StartNew();
        void Mark(string s) => timeline.Enqueue($"{t0.ElapsedMilliseconds}ms {s}");

        var a = NewHub(aToken, stateful: stateful, reconnect: true);
        a.Reconnecting += e =>
        {
            Mark("A reconnecting: " + e?.Message);
            return Task.CompletedTask;
        };
        a.Reconnected += id =>
        {
            Mark("A reconnected " + id);
            return Task.CompletedTask;
        };
        a.Closed += e =>
        {
            Mark("A closed: " + e?.Message);
            return Task.CompletedTask;
        };
        var b = NewHub(bToken, stateful: stateful, reconnect: true);
        var got = new ConcurrentDictionary<string, long>();
        long lastSeq = 0;
        var gate = new SemaphoreSlim(1, 1);
        async Task ResumeB()
        {
            await gate.WaitAsync();
            try
            {
                for (; ; )
                {
                    var batch = await b.InvokeAsync<List<JsonElement>>("Resume", Interlocked.Read(ref lastSeq), 500);
                    foreach (var ev in batch)
                        OnEvent(ev);
                    if (batch.Count < 500)
                        break;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        void OnEvent(JsonElement ev)
        {
            var seq = ev.GetProperty("seq").GetInt64();
            if (ev.GetProperty("deviceId").GetString() == aId)
                got.TryAdd(ev.GetProperty("clientEventId").GetString()!, seq);
            long cur;
            while ((cur = Interlocked.Read(ref lastSeq)) < seq && Interlocked.CompareExchange(ref lastSeq, seq, cur) != cur)
            {
            }
        }
        b.On<JsonElement>("Event", OnEvent);
        b.Reconnecting += e =>
        {
            Mark("B reconnecting: " + e?.Message);
            return Task.CompletedTask;
        };
        b.Reconnected += async id =>
        {
            Mark("B reconnected " + id);
            await ResumeB();
        };
        b.Closed += e =>
        {
            Mark("B closed: " + e?.Message);
            return Task.CompletedTask;
        };
        await a.StartAsync();
        await b.StartAsync();
        Mark("started");

        var outbox = new Queue<string>();
        var acked = new Dictionary<string, long>();
        var conv = "soak-" + Guid.NewGuid().ToString("N")[..6];
        var next = 0;
        var publishErrors = 0;
        while (t0.Elapsed.TotalSeconds < seconds && !(stopFile.Length > 0 && File.Exists(stopFile)))
        {
            outbox.Enqueue($"s{next++}");
            while (outbox.Count > 0 && a.State == HubConnectionState.Connected)
            {
                var id = outbox.Peek();
                try
                {
                    acked[id] = await a.InvokeAsync<long>("Publish", conv, id, "delta", $"{{\"t\":{t0.ElapsedMilliseconds}}}").WaitAsync(TimeSpan.FromSeconds(5));
                    outbox.Dequeue();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    publishErrors++;
                    Mark("publish failed " + id + ": " + ex.GetType().Name);
                    break;
                }
            }
            await Task.Delay(100);
        }
        // 收尾：把 outbox 送完、讓 B 再補送一次
        var drainUntil = Stopwatch.StartNew();
        while (outbox.Count > 0 && drainUntil.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (a.State == HubConnectionState.Connected)
            {
                var id = outbox.Peek();
                try
                {
                    acked[id] = await a.InvokeAsync<long>("Publish", conv, id, "delta", "{}");
                    outbox.Dequeue();
                    continue;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            }
            await Task.Delay(200);
        }
        await Task.Delay(500);
        if (b.State == HubConnectionState.Connected)
            await ResumeB();
        var missing = acked.Keys.Where(k => !got.ContainsKey(k)).ToList();
        ctx.Emit(new { @event = "soak", stateful, created = next, acked = acked.Count, outboxLeft = outbox.Count, publishErrors, receivedByB = got.Count, missingAtB = missing.Count, missingSample = missing.Take(5), timeline = timeline.ToArray() });
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    // blip：短暫斷線（TCP 被切）＋stateful reconnect：連線 ID 不變、斷線期間的訊息由緩衝補上
    public async Task BlipAsync()
    {
        var target = ctx.BaseUri;
        var relayPort = ctx.IntOpt("relay-port", 5444);
        var relay = new TcpRelay(IPAddress.Loopback, relayPort, target.Port);
        relay.Start();
        var relayBase = $"{target.Scheme}://127.0.0.1:{relayPort}"; // 轉送只聽 IPv4：用 "localhost" 會先試 ::1 白等 2 秒
        var (deviceId, token) = await MintDeviceAsync("blip");
        foreach (var stateful in new[] { true, false })
        {
            var events = new ConcurrentQueue<string>();
            var got = new ConcurrentQueue<int>();
            var c = NewHub(token, stateful: stateful, reconnect: true, url: relayBase + ctx.HubPath);
            c.On<int, string>("Push", (i, _) => got.Enqueue(i));
            c.Reconnecting += _ =>
            {
                events.Enqueue("reconnecting");
                return Task.CompletedTask;
            };
            c.Reconnected += id =>
            {
                events.Enqueue("reconnected:" + id);
                return Task.CompletedTask;
            };
            c.Closed += e =>
            {
                events.Enqueue("closed:" + e?.Message);
                return Task.CompletedTask;
            };
            await c.StartAsync();
            var idBefore = (await c.InvokeAsync<JsonElement>("WhoAmI")).GetProperty("connectionId").GetString();

            // 先推 0～19，切斷 TCP，伺服器在斷線期間推 20～39，再等用戶端自己接回
            await http.PostAsJsonAsync($"{baseUrl}/test/push", new { deviceId, count = 20, size = 200 });
            await Task.Delay(300);
            var sw = Stopwatch.StartNew();
            var dropped = relay.DropAll();
            var pushDuring = await http.PostAsJsonAsync($"{baseUrl}/test/push", new { deviceId, count = 20, size = 200, offset = 20 });
            var duringBody = await pushDuring.Content.ReadAsStringAsync();
            long connectedAgainMs = -1;
            while (sw.Elapsed < TimeSpan.FromSeconds(10) && got.Count < 40)
            {
                if (connectedAgainMs < 0 && c.State == HubConnectionState.Connected && (!events.IsEmpty || got.Count > 20))
                    connectedAgainMs = sw.ElapsedMilliseconds;
                await Task.Delay(20);
            }
            string? idAfter;
            try
            {
                idAfter = (await c.InvokeAsync<JsonElement>("WhoAmI")).GetProperty("connectionId").GetString();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                idAfter = "ERR " + ex.Message;
            }
            var list = got.ToArray();
            ctx.Emit(new
            {
                @event = "blip",
                stateful,
                droppedTcpConnections = dropped,
                connectedAgainMs,
                allDeliveredMs = got.Count >= 40 ? sw.ElapsedMilliseconds : -1,
                sameConnectionId = idBefore == idAfter,
                idBefore,
                idAfter,
                received = list.Length,
                missing = Enumerable.Range(0, 40).Except(list).Count(),
                inOrder = list.SequenceEqual(list.OrderBy(x => x)),
                pushDuringBlip = duringBody,
                clientEvents = events.ToArray(),
            });
            await c.DisposeAsync();
        }
        relay.Stop();
    }

    // backpressure：慢用戶端讓伺服器的 SendAsync 等待；群組廣播時慢的那一個會不會拖到快的
    public async Task BackpressureAsync()
    {
        var count = ctx.IntOpt("count", 3000);
        var size = ctx.IntOpt("size", 1024);
        var delay = ctx.IntOpt("delay", 10);
        var (slowId, slowToken) = await MintDeviceAsync("slow");
        var (_, fastToken) = await MintDeviceAsync("fast");
        var slowGot = 0;
        var fastGot = 0;
        var swAll = new Stopwatch();
        long fastLastMs = 0, slowLastMs = 0;
        var slow = NewHub(slowToken);
        slow.On<int, string>("Push", async (_, _) =>
        {
            await Task.Delay(delay);
            Interlocked.Increment(ref slowGot);
            slowLastMs = swAll.ElapsedMilliseconds;
        });
        var fast = NewHub(fastToken);
        fast.On<int, string>("Push", (_, _) =>
        {
            Interlocked.Increment(ref fastGot);
            fastLastMs = swAll.ElapsedMilliseconds;
        });
        await slow.StartAsync();
        await fast.StartAsync();

        // (a) 只推給慢的
        swAll.Start();
        var r1 = await (await http.PostAsJsonAsync($"{baseUrl}/test/push", new { deviceId = slowId, count, size })).Content.ReadFromJsonAsync<JsonElement>();
        var serverDoneMs = swAll.ElapsedMilliseconds;
        while (slowGot < count && swAll.Elapsed < TimeSpan.FromMinutes(2))
            await Task.Delay(50);
        ctx.Emit(new { @event = "backpressure-single", count, size, handlerDelayMs = delay, serverLoopMs = r1.GetProperty("totalMs").GetInt64(), serverMaxSingleSendMs = r1.GetProperty("maxSendMs").GetInt64(), serverDoneAtMs = serverDoneMs, clientDoneAtMs = slowLastMs, clientReceived = slowGot });

        // (b) 廣播給 devices 群組（快＋慢）
        slowGot = 0;
        fastGot = 0;
        swAll.Restart();
        var r2 = await (await http.PostAsJsonAsync($"{baseUrl}/test/broadcast", new { deviceId = "", count, size })).Content.ReadFromJsonAsync<JsonElement>();
        var bServerDone = swAll.ElapsedMilliseconds;
        while ((slowGot < count || fastGot < count) && swAll.Elapsed < TimeSpan.FromMinutes(2))
            await Task.Delay(50);
        ctx.Emit(new { @event = "backpressure-broadcast", count, size, handlerDelayMs = delay, serverLoopMs = r2.GetProperty("totalMs").GetInt64(), serverMaxSingleSendMs = r2.GetProperty("maxSendMs").GetInt64(), serverDoneAtMs = bServerDone, fastClientDoneAtMs = fastLastMs, slowClientDoneAtMs = slowLastMs, fastReceived = fastGot, slowReceived = slowGot });
        await slow.DisposeAsync();
        await fast.DisposeAsync();
    }

    // stall：完全卡住的用戶端（處理常式不返回）：伺服器送多少之後 SendAsync 才開始等？群組廣播會不會連快的一起卡？
    public async Task StallAsync()
    {
        var count = ctx.IntOpt("count", 200000);
        var size = ctx.IntOpt("size", 1024);
        var (stuckId, stuckToken) = await MintDeviceAsync("stuck");
        var (_, fastToken) = await MintDeviceAsync("fast2");
        var release = new TaskCompletionSource();
        var stuckGot = 0;
        var fastGot = 0;
        var stuck = NewHub(stuckToken);
        stuck.On<int, string>("Push", async (_, _) =>
        {
            if (Interlocked.Increment(ref stuckGot) == 1)
                await release.Task;
        });
        var fast = NewHub(fastToken);
        fast.On<int, string>("Push", (_, _) => Interlocked.Increment(ref fastGot));
        await stuck.StartAsync();
        await fast.StartAsync();

        var r1 = await (await http.PostAsJsonAsync($"{baseUrl}/test/push", new { deviceId = stuckId, count, size, sendTimeoutMs = 3000 })).Content.ReadAsStringAsync();
        var statsAfterSingle = await http.GetFromJsonAsync<JsonElement>($"{baseUrl}/test/stats");
        await Task.Delay(1000);
        GC.Collect();
        using var me = Process.GetCurrentProcess();
        ctx.Emit(new { @event = "stall-single", result = r1, stuckClientProcessed = stuckGot, serverPrivateMB = statsAfterSingle.GetProperty("privateMB").GetDouble(), clientPrivateMB = Math.Round(me.PrivateMemorySize64 / 1048576.0, 1), clientManagedMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1), stuckState = stuck.State.ToString() });

        fastGot = 0;
        var sw = Stopwatch.StartNew();
        var r2 = await (await http.PostAsJsonAsync($"{baseUrl}/test/broadcast", new { deviceId = "", count = 2000, size, sendTimeoutMs = 3000 })).Content.ReadAsStringAsync();
        await Task.Delay(500);
        ctx.Emit(new { @event = "stall-broadcast", result = r2, fastReceived = fastGot, elapsedMs = sw.ElapsedMilliseconds, stuckState = stuck.State.ToString(), fastState = fast.State.ToString() });
        release.TrySetResult();
        await Task.Delay(500);
        await stuck.DisposeAsync();
        await fast.DisposeAsync();
    }

    // pubrate：模擬 WAN 延遲（轉送每方向延遲 delay ms）下，逐則 InvokeAsync／SendAsync／批次 的吞吐
    public async Task PubRateAsync()
    {
        var delay = ctx.IntOpt("delay", 15);
        var relayPort = ctx.IntOpt("relay-port", 5445);
        var target = ctx.BaseUri;
        var relay = new TcpRelay(IPAddress.Loopback, relayPort, target.Port, delay);
        relay.Start();
        var (_, token) = await MintDeviceAsync("pubrate");
        var c = NewHub(token, url: $"{target.Scheme}://127.0.0.1:{relayPort}{ctx.HubPath}");
        await c.StartAsync();
        var conv = "rate-" + Guid.NewGuid().ToString("N")[..6];
        var sw = Stopwatch.StartNew();
        await c.InvokeAsync<JsonElement>("WhoAmI");
        var rttMs = sw.Elapsed.TotalMilliseconds;

        // (a) 逐則 await InvokeAsync
        const int nA = 100;
        sw.Restart();
        for (var i = 0; i < nA; i++)
            await c.InvokeAsync<long>("Publish", conv, $"a{i}", "delta", "{\"x\":1}");
        var aRate = nA / sw.Elapsed.TotalSeconds;

        // (b) SendAsync 不等回覆（同一連線依序處理），最後一則 InvokeAsync 當作排空
        const int nB = 2000;
        sw.Restart();
        for (var i = 0; i < nB; i++)
            await c.SendAsync("Publish", conv, $"b{i}", "delta", "{\"x\":1}");
        await c.InvokeAsync<long>("Publish", conv, "b-end", "delta", "{}");
        var bRate = (nB + 1) / sw.Elapsed.TotalSeconds;

        // (c) 批次：每次 20 則，逐批 await
        const int batches = 50, perBatch = 20;
        sw.Restart();
        for (var b = 0; b < batches; b++)
        {
            var items = Enumerable.Range(0, perBatch).Select(i => new { clientEventId = $"c{b}-{i}", kind = "delta", payload = "{\"x\":1}" }).ToList();
            await c.InvokeAsync<long[]>("PublishBatch", conv, items);
        }
        var cRate = batches * perBatch / sw.Elapsed.TotalSeconds;
        ctx.Emit(new { @event = "pubrate", relayDelayPerDirectionMs = delay, measuredRttMs = Math.Round(rttMs, 1), invokeEachPerSec = Math.Round(aRate), sendAsyncPerSec = Math.Round(bRate), batch20PerSec = Math.Round(cRate) });
        await c.DisposeAsync();
        relay.Stop();
    }

    // certswap：熱換憑證：WebSocket 連線開著時換憑證；新連線拿到新憑證、舊連線照常（第 2 階段的 HTTP keep-alive 版見 tools/acme-e2e）
    public async Task CertSwapAsync()
    {
        var u = ctx.BaseUri;
        var (_, token) = await MintDeviceAsync("swap");
        var c = NewHub(token);
        await c.StartAsync();
        var before = await ProbeTls.HandshakeAsync(ctx.ConnectHost, u.Port, ctx.Sni, ["http/1.1"]);
        var swap = await (await http.PostAsync($"{baseUrl}/test/swap-selfsigned?cn={ctx.Sni}", null)).Content.ReadFromJsonAsync<JsonElement>();
        var after = await ProbeTls.HandshakeAsync(ctx.ConnectHost, u.Port, ctx.Sni, ["http/1.1"]);
        var echo = await c.InvokeAsync<int>("Echo", "still-alive");
        var id = (await c.InvokeAsync<JsonElement>("WhoAmI")).GetProperty("connectionId").GetString();
        ctx.Emit(new { @event = "certswap", before = before.Thumbprint, swappedTo = swap.GetProperty("thumbprint").GetString(), after = after.Thumbprint, newConnectionsSeeNewCert = after.Thumbprint == swap.GetProperty("thumbprint").GetString(), oldWebSocketStillWorks = echo == "still-alive".Length, connectionState = c.State.ToString(), connectionId = id });
        await c.DisposeAsync();
    }

    // pair：裝置配對（類似 OAuth device flow）：開始 → 印出配對碼 → 等瀏覽器核准 → 拿權杖 → 連 hub → 發事件 → 回應指令 → 被撤銷
    public async Task PairAsync()
    {
        var name = ctx.Opt("name", "probe-pc");
        var publish = ctx.IntOpt("publish", 5);
        var start = await (await http.PostAsJsonAsync($"{baseUrl}/api/pair/start", new { deviceName = name })).Content.ReadFromJsonAsync<JsonElement>();
        ctx.Emit(new { @event = "userCode", userCode = start.GetProperty("userCode").GetString(), verificationUriComplete = start.GetProperty("verificationUriComplete").GetString() });
        var deviceCode = start.GetProperty("deviceCode").GetString();
        var interval = start.GetProperty("interval").GetInt32();
        string? token = null, deviceId = null;
        var polls = new Dictionary<string, int>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            await Task.Delay(TimeSpan.FromSeconds(interval));
            var res = await http.PostAsJsonAsync($"{baseUrl}/api/pair/token", new { deviceCode });
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (res.IsSuccessStatusCode)
            {
                token = body.GetProperty("deviceToken").GetString();
                deviceId = body.GetProperty("deviceId").GetString();
                break;
            }
            var err = body.GetProperty("error").GetString() ?? "?";
            polls[err] = polls.GetValueOrDefault(err) + 1;
            if (err is "expired_token" or "invalid_grant")
                break;
        }
        if (token is null)
        {
            ctx.Emit(new { @event = "pairFailed", polls });
            return;
        }
        var reuse = await http.PostAsJsonAsync($"{baseUrl}/api/pair/token", new { deviceCode }); // 同一個 deviceCode 不能再換第二次
        ctx.Emit(new { @event = "paired", deviceId, tokenPrefix = token[..8], polls, secondExchangeStatus = (int)reuse.StatusCode });

        var c = NewHub(token, reconnect: true);
        var closed = new TaskCompletionSource<string>();
        var commands = 0;
        c.On<string, string>("Command", cmd =>
        {
            Interlocked.Increment(ref commands);
            ctx.Emit(new { @event = "command", cmd });
            return "ack:" + cmd;
        });
        c.Reconnecting += e =>
        {
            ctx.Emit(new { @event = "reconnecting", error = e?.Message });
            return Task.CompletedTask;
        };
        c.Closed += e =>
        {
            closed.TrySetResult(e?.Message ?? "(null)");
            return Task.CompletedTask;
        };
        await c.StartAsync();
        var who = await c.InvokeAsync<JsonElement>("WhoAmI");
        for (var i = 0; i < publish; i++)
            await c.InvokeAsync<long>("Publish", "pair-conv", $"p{i}", "item", $"{{\"text\":\"第 {i} 則\"}}");
        ctx.Emit(new { @event = "connected", deviceId, transport = who.GetProperty("transport").GetString(), published = publish });

        // 等撤銷：連線會被伺服器 Abort；自動重連時 negotiate 會 401 → 最後 Closed
        var reason = await closed.Task.WaitAsync(TimeSpan.FromMinutes(3));
        using var revokedHttp = ProbeContext.NewHttp(token);
        var after = await revokedHttp.PostAsync($"{baseUrl}{ctx.HubPath}/negotiate?negotiateVersion=1", null);
        ctx.Emit(new { @event = "revoked", closedReason = reason, commandsHandled = commands, negotiateAfterRevoke = (int)after.StatusCode });
        await c.DisposeAsync();
    }

    private async Task<(string DeviceId, string Token)> MintDeviceAsync(string name)
    {
        var r = await (await http.PostAsync($"{baseUrl}/test/mint-device?name={name}", null)).Content.ReadFromJsonAsync<JsonElement>();
        return (r.GetProperty("deviceId").GetString()!, r.GetProperty("deviceToken").GetString()!);
    }

    private HubConnection NewHub(string token, Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>? configure = null, bool stateful = false, bool reconnect = false, string? url = null)
    {
        var b = new HubConnectionBuilder()
            .WithUrl(url ?? baseUrl + ctx.HubPath, o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.HttpMessageHandlerFactory = h =>
                {
                    if (h is HttpClientHandler hh)
                        hh.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    return h;
                };
                o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, _, _, _) => true; // 只給本機自簽憑證實驗
                configure?.Invoke(o);
            });
        if (stateful)
            b.WithStatefulReconnect();
        if (reconnect)
            b.WithAutomaticReconnect(new ForeverRetry());
        return b.Build();
    }
}
