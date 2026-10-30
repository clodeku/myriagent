using System.Diagnostics;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;
using static MyriagentHub.Tests.Sync.EventLogTests;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// S8（多電腦同步計畫的驗收表）：伺服器不掉資料。MyriagentHub 以子程序（<see cref="HubProcess"/>）執行，三台裝置持續產生事件放進 outbox、
/// 邊產生邊上傳；串流中強制結束伺服器（Kill，不跑正常停止）並立刻重啟，共兩次。裝置斷線後重連、從 Prove 的 nextDeviceSeqExpected 續傳。
/// 驗收：每次強制結束後 30 秒內三台都重新完成交握；伺服器的期待值不小於裝置已收到確認的位置（確認過的沒有遺失）；
/// 結束後每台的 deviceSeq 剛好 1..N 各一筆、內容雜湊與送出的相同（0 遺失、0 重複）、沒有被擋的事件。
/// 瀏覽器那一半要等第 4 階段的網頁即時通道。
/// </summary>
public sealed class SoakTests(ITestOutputHelper output)
{
    private const int DeviceCount = 3;
    private const int EventsPerDevice = 500;
    private const int GenerateDelayMs = 10;
    private const int Kills = 2;
    private static readonly TimeSpan RecoveryLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S8_串流中強制結束伺服器並立刻重啟_確認過的不遺失_重送不重複()
    {
        var dir = TempDir.Create("myriagenthub-soak");
        var devices = new List<SoakDevice>();
        try
        {
            await using (var seed = await HubTestHost.StartAsync(dataDir: dir))
            {
                // 子程序用真的時鐘：權杖的簽發時間也用真的，Prove 不會因為「超過 30 天」而輪換
                for (var i = 0; i < DeviceCount; i++)
                    devices.Add(new SoakDevice(await TestDevice.AddAsync(seed, $"電腦{i + 1}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), EventsPerDevice, resendOverlap: i == 0));
            }

            await RunWithKillsAsync(dir, devices, output);

            // 最後一次也是強制結束：由新的主機開同一個資料根，逐筆比對
            await using var check = await HubTestHost.StartAsync(dataDir: dir);
            AssertStored(check, devices);
            Count(check, "SELECT COUNT(*) FROM rejected_events").Should().Be(0);
        }
        finally
        {
            foreach (var d in devices)
                d.Device.Dispose();
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// 以子程序跑 MyriagentHub、裝置邊產生邊送；串流中強制結束並立刻重啟 <see cref="Kills"/> 次，最後送完、強制結束。
    /// 斷言每次 30 秒內全部恢復交握、沒有違規（被擋、對帳模式、確認過的不見）、第一台刻意重送過已寫入的事件（對話的 soak 也用，WP4.2 計畫步 14）
    /// </summary>
    internal static async Task RunWithKillsAsync(string dir, IReadOnlyList<SoakDevice> devices, ITestOutputHelper output)
    {
        HubProcess? server = null;
        try
        {
            var port = HubProcess.FreePort();
            server = await HubProcess.StartAsync(dir, port);
            var baseAddress = server.BaseAddress;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var generators = devices.Select(d => d.GenerateAsync(GenerateDelayMs, stop.Token)).ToList();
            var senders = devices.Select(d => d.RunAsync(baseAddress, stop.Token)).ToList();

            for (var kill = 1; kill <= Kills; kill++)
            {
                // 每台都確認過一部分、而且還在送的時候結束（第 k 次在大約 k／3 的位置）
                var threshold = devices.Min(d => d.Total) * kill / (Kills + 1);
                await WaitUntilAsync(() => devices.All(d => d.Acked >= threshold), TimeSpan.FromSeconds(60), $"第 {kill} 次結束前的進度");
                var handshakes = devices.Select(d => d.Handshakes).ToArray();
                var acked = devices.Select(d => d.Acked).ToArray();
                var generated = devices.Select(d => d.Generated).ToArray();

                var clock = Stopwatch.StartNew();
                server.Kill();
                server.Dispose();
                var killed = clock.Elapsed;
                server = await HubProcess.StartAsync(dir, port);
                var ready = clock.Elapsed;
                await WaitUntilAsync(() => devices.Select((d, i) => d.Handshakes > handshakes[i]).All(x => x), RecoveryLimit - clock.Elapsed, $"第 {kill} 次重啟後裝置恢復");
                var recovered = clock.Elapsed;

                output.WriteLine($"第 {kill} 次：結束時已確認 [{string.Join(", ", acked)}]／產生 [{string.Join(", ", generated)}]；" +
                    $"Kill {killed.TotalMilliseconds:0} ms、就緒 {ready.TotalMilliseconds:0} ms、{devices.Count} 台恢復 {recovered.TotalMilliseconds:0} ms");
                recovered.Should().BeLessThan(RecoveryLimit);
            }

            await Task.WhenAll(generators);
            var finished = Task.WhenAll(senders);
            if (await Task.WhenAny(finished, Task.Delay(RecoveryLimit)) != finished)
                throw new TimeoutException("裝置沒有在時限內送完：" + string.Join(", ", devices.Select(d => $"{d.Acked}/{d.Generated}")));
            await finished;
            server.Kill();
            server.Dispose();
            server = null;
        }
        finally
        {
            server?.Dispose();
        }

        foreach (var d in devices)
        {
            output.WriteLine($"{d.Device.Id}：交握 {d.Handshakes} 次、批次 {d.Batches}、已寫入但確認沒送到 {d.StoredButUnacked} 筆、刻意重送已寫入的 {d.Resent} 筆");
            d.Violations.Should().BeEmpty();
        }
        var latencies = devices.SelectMany(d => d.AckLatenciesMs).Order().ToList();
        output.WriteLine($"BatchAck 延遲：{latencies.Count} 批，p50 {latencies[latencies.Count / 2]:0.0} ms、p99 {latencies[latencies.Count * 99 / 100]:0.0} ms、最大 {latencies[^1]:0.0} ms");
        devices.Sum(d => d.Handshakes).Should().BeGreaterThanOrEqualTo(devices.Count * (Kills + 1));
        devices[0].Resent.Should().BeGreaterThan(0, "重連後重送已寫入的事件，伺服器要當成重複（回原本的 seq）而不是再寫一次");
    }

    /// <summary>每台的 deviceSeq 剛好 1..N 各一筆、內容雜湊就是裝置送出的那一筆（0 遺失、0 重複）、伺服器期待 N＋1</summary>
    internal static void AssertStored(HubTestHost check, IReadOnlyList<SoakDevice> devices)
    {
        using var read = check.Services.GetRequiredService<SyncWriter>().OpenRead();
        foreach (var d in devices)
        {
            var stored = StoredEvents(read, d.Device.Id);
            stored.Select(s => s.DeviceSeq).Should().Equal(Enumerable.Range(1, d.Total).Select(i => (long)i), "每台的 deviceSeq 剛好 1..N 各一筆");
            stored.Select(s => s.Sha).Should().Equal(Enumerable.Range(1, d.Total).Select(i => d.Sha(i)), "內容就是裝置送出的那一筆");
            Count(check, $"SELECT next_device_seq FROM device_streams WHERE device_id = '{d.Device.Id}'").Should().Be(d.Total + 1);
        }
    }

    private static List<(long DeviceSeq, string Sha)> StoredEvents(SqliteConnection read, string deviceId)
    {
        using var command = read.CreateCommand();
        command.CommandText = "SELECT device_seq, lower(hex(payload_sha256)) FROM events WHERE author = $d ORDER BY device_seq";
        command.Parameters.AddWithValue("$d", deviceId);
        using var reader = command.ExecuteReader();
        var rows = new List<(long, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        return rows;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout)
                throw new TimeoutException("等不到：" + what);
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// 模擬桌面的 outbox：產生端每隔一段時間放一筆（預設 <c>cli.state</c>，<c>checkedAt</c>＝deviceSeq，每筆內容不同；
    /// <paramref name="generate"/> 給的話照它產生，例如對話事件），傳送端最多 4 批在途、每批最多 16 筆；斷線就重連，從 Prove 的期待值續傳（不是從自己收到的確認位置）。
    /// <paramref name="resendOverlap"/>＝重連後刻意從期待值往前一批開始送（伺服器已寫入的那幾筆再送一次），驗證重送不重複。
    /// <paramref name="features"/>＝Hello 宣告的功能（null＝第 3 階段的行為）
    /// </summary>
    internal sealed class SoakDevice(TestDevice device, int total, bool resendOverlap, Func<int, SyncEvent>? generate = null, IReadOnlyList<string>? features = null)
    {
        private const int BatchSize = 16;
        private const int MaxInFlight = 4;

        private readonly object gate = new();
        private readonly List<SyncEvent> outbox = [];
        private readonly List<string> hashes = [];
        private long acked;

        public TestDevice Device { get; } = device;

        /// <summary>要產生的總筆數（deviceSeq 1..Total）</summary>
        public int Total { get; } = total;

        public long Acked => Interlocked.Read(ref acked);

        public long Generated
        {
            get
            {
                lock (gate)
                    return outbox.Count;
            }
        }

        public int Handshakes;

        public int Batches;

        /// <summary>重連時伺服器的期待值超過自己收到的確認：那幾筆已提交、只是確認在結束時沒送到（裝置不重送，所以也不會重複）</summary>
        public long StoredButUnacked;

        /// <summary>刻意重送的已寫入事件數（<c>resendOverlap</c>）</summary>
        public long Resent;

        public List<string> Violations { get; } = [];

        /// <summary>每批從送出到收到 BatchAck 的時間</summary>
        public List<double> AckLatenciesMs { get; } = [];

        public string Sha(int deviceSeq)
        {
            lock (gate)
                return hashes[deviceSeq - 1];
        }

        public async Task GenerateAsync(int delayMs, CancellationToken cancellationToken)
        {
            for (var seq = 1; seq <= Total; seq++)
            {
                var e = generate?.Invoke(seq) ?? CliState(seq);
                if (e.DeviceSeq != seq)
                    throw new InvalidOperationException($"產生的事件 deviceSeq 是 {e.DeviceSeq}，應該是 {seq}");
                lock (gate)
                {
                    outbox.Add(e);
                    hashes.Add(e.PayloadSha256.ToLowerInvariant());
                }
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        private static SyncEvent CliState(int seq)
        {
            var cli = new CliStatePayload(new Dictionary<string, DeviceCliView>
            {
                ["Claude"] = new(true, "2.1.0", "native", "2.2.0", false, 0, 0, null, seq),
            });
            return Ev(seq, SyncEventKinds.CliState, cli);
        }

        public async Task RunAsync(Uri server, CancellationToken cancellationToken)
        {
            while (Acked < Total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeviceHubClient client;
                System.Security.Cryptography.X509Certificates.X509Certificate2 seen;
                try
                {
                    (client, seen) = await DeviceHubClient.ConnectKestrelAsync(server, Device.Token, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }
                await using (client)
                {
                    try
                    {
                        var (_, prove) = await client.HandshakeAsync(Device, HandshakeRules.SpkiSha256(seen), Position(), features: features);
                        Interlocked.Increment(ref Handshakes);
                        if (prove.TokenRotation is { } rotated)
                            Device.Token = rotated;
                        if (prove.Resync)
                            Violate("不該進入對帳模式");
                        var acknowledged = Acked;
                        if (prove.NextDeviceSeqExpected <= acknowledged)
                            Violate($"確認過的不見了：已確認到 {acknowledged}，伺服器期待 {prove.NextDeviceSeqExpected}");
                        else if (prove.NextDeviceSeqExpected > acknowledged + 1)
                            Interlocked.Add(ref StoredButUnacked, prove.NextDeviceSeqExpected - 1 - acknowledged);
                        if (prove.NextDeviceSeqExpected > Generated + 1)
                            Violate($"伺服器期待 {prove.NextDeviceSeqExpected}，超過裝置產生的 {Generated}");
                        Raise(prove.NextDeviceSeqExpected - 1);
                        var from = prove.NextDeviceSeqExpected;
                        if (resendOverlap && from > 1)
                        {
                            var overlap = Math.Min(BatchSize, from - 1);
                            Interlocked.Add(ref Resent, overlap);
                            from -= overlap;
                        }
                        await PumpAsync(client, from, cancellationToken);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // 伺服器被結束（呼叫失敗或連線關閉）：重連
                    }
                }
            }
        }

        private async Task PumpAsync(DeviceHubClient client, long from, CancellationToken cancellationToken)
        {
            var sentAt = new Dictionary<string, long>();
            var next = from;
            var inFlight = 0;
            while (Acked < Total)
            {
                while (inFlight < MaxInFlight && next <= Generated)
                {
                    List<SyncEvent> events;
                    lock (gate)
                        events = outbox.Skip((int)(next - 1)).Take(BatchSize).ToList();
                    var batch = new Batch(SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), events);
                    sentAt[batch.BatchId] = Stopwatch.GetTimestamp();
                    await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, batch, cancellationToken);
                    Interlocked.Increment(ref Batches);
                    next += events.Count;
                    inFlight++;
                }
                if (client.Closed.IsCompleted)
                    throw new IOException("連線已關閉");
                if (await NextAckAsync(client, cancellationToken) is not { } ack)
                    continue;
                inFlight--;
                if (sentAt.Remove(ack.BatchId, out var started))
                {
                    lock (AckLatenciesMs)
                        AckLatenciesMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                if (ack.Error is not null)
                    Violate($"批次錯誤：{ack.Error}");
                foreach (var result in ack.Results.Where(r => r.Seq is null || r.Rejected is not null))
                    Violate($"deviceSeq {result.DeviceSeq} 沒有被接受：{result.Rejected}");
                Raise(ack.AcceptedThrough);
            }
        }

        /// <summary>等下一個 BatchAck（最多 50 ms，好讓新產生的事件也送出去）</summary>
        private static async Task<BatchAck?> NextAckAsync(DeviceHubClient client, CancellationToken cancellationToken)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(50);
            try
            {
                while (await client.Received.Reader.WaitToReadAsync(wait.Token))
                {
                    while (client.Received.Reader.TryRead(out var message))
                    {
                        if (message is BatchAck ack)
                            return ack;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            return null;
        }

        private OutboxPosition Position()
        {
            var generated = Generated;
            var acknowledged = Acked;
            return acknowledged < generated
                ? new OutboxPosition(generated + 1, acknowledged + 1, 1)
                : new OutboxPosition(generated + 1, null, generated > 0 ? 1 : null);
        }

        private void Raise(long through)
        {
            long current;
            while ((current = Interlocked.Read(ref acked)) < through)
                Interlocked.CompareExchange(ref acked, through, current);
        }

        private void Violate(string message)
        {
            lock (Violations)
                Violations.Add(message);
        }
    }
}
