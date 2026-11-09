using System.Diagnostics;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 混合版本與回滾（WP4.2 計畫步 14、3.1；答 1、D35、D36、D46、提案 C10）：
/// <list type="bullet">
/// <item>第 3 階段的桌面（Hello 原文沒有 features）連到一律宣告 conv1、live1（WP4.5 起另有 cmd1）的伺服器：照常 Prove、送批次、Pull，協商出的是空的。</item>
/// <item>WP4.1 的桌面（不宣告功能）：伺服器不送 WatchChanged、SnapshotRequest，它的 LiveRelay 被丟掉；同時在線、宣告了功能的另一台照常收到（對照）。</item>
/// <item>回滾到 <c>1724718</c>（<c>MYRIAGENT_SYNC_OLD_HUB</c>＝以 <c>git archive 1724718</c> 建出的 MyriagentHub.exe 完整路徑，沒設就略過；建法見 src/MyriagentHub/README.md）：
/// 舊組建開 WP4.2 寫過的 v3 sync.db 的複本、照常服務第 3 階段的流量、對話事件一律 invalid；再升級回來不重建、物化表與 events 一致、舊組建留下的被拒列 30 天後刪掉。</item>
/// </list>
/// </summary>
public sealed class MixedVersionTests(ITestOutputHelper output)
{
    /// <summary>舊組建（<c>1724718</c>）的 MyriagentHub.exe 完整路徑（和 WP4.3 共用的約定，計畫 6.3）</summary>
    public const string OldHubVariable = "MYRIAGENT_SYNC_OLD_HUB";

    private const string OldHubReason = "回滾到 1724718 的測試：值是舊組建 MyriagentHub.exe 的完整路徑，建法見 src/MyriagentHub/README.md";

    private static readonly string Conv = ConversationSamples.Conv;

    // ───── 第 3 階段的桌面 ─────

    [Fact]
    public async Task 第3階段的Hello原文連宣告了功能的伺服器_照常Prove與Pull()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var spki = DeviceHubClient.ObservedSpki(host);
        var nonceD = HandshakeRules.NewNonce();
        // 第 3 階段桌面送的 Hello 原文（SyncJson：camelCase、null 不寫）：沒有 features 這個鍵
        var raw = """
            {"protocolVersion":1,"deviceId":"@id","bootId":"boot-1","appVersion":"0.1.0","machineName":"TEST-PC","windowsUser":"User","nonceD":"@nonce","observedSpkiSha256":"@spki","outbox":{"nextDeviceSeq":1},"cursors":{"catalog":0,"usage":0},"policy":{"remoteControlLevel":"Control","allowRemoteFullAccess":false}}
            """.Replace("@id", device.Id).Replace("@nonce", nonceD).Replace("@spki", spki);
        var request = JObject.Parse(raw);
        request.ContainsKey("features").Should().BeFalse("前提：原文就是第 3 階段的形狀");

        var hello = await client.Connection.InvokeAsync<JObject>(DeviceHubMethods.Hello, request);

        hello.Properties().Select(p => p.Name).Should().Equal(
            new[] { "serverTime", "nonceS", "server", "hubSig", "hubIdentitySpki", "trustRevs", "limits", "minAppVersion", "features" },
            "只多 features（舊桌面忽略未知欄位；不在 hubSig 的範圍）");
        // WP4.5 步 6 起伺服器另宣告 cmd1，WP4.7 步 4 起再宣告 media1，U2 起 AddHubReleases（HubApplication 在 AddHubPairing 之後）宣告 update1（依登記的順序）
        hello["features"]!.ToObject<string[]>().Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Prov1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Update1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1, DeviceFeatureRules.Media1, DeviceFeatureRules.Stat1, DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2, DeviceFeatureRules.Sched1, DeviceFeatureRules.Collab1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2);
        var sig = P256KeyRules.Sign(device.Key, HandshakeRules.DeviceSignedData(device.Id, hello["nonceS"]!.Value<string>()!, nonceD, spki));
        var prove = await client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig));
        prove.AccessToken.Should().NotBeNullOrEmpty();
        prove.NextDeviceSeqExpected.Should().Be(1);
        var connection = host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(device.Id).Single();
        (connection.IsProven, connection.Features.Count).Should().Be((true, 0), "協商出的是空的：伺服器不送第 4 階段的訊息");

        await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, EventLogTests.Batch(CliState(1), CliState(2)));
        var ack = await client.WaitForAsync<BatchAck>();
        (ack.Error, ack.AcceptedThrough).Should().Be((null, 2L));
        ack.Results.Should().OnlyContain(r => r.Seq != null && r.Rejected == null);

        var pulled = await client.Connection.InvokeAsync<PullResponse>(DeviceHubMethods.Pull,
            new PullRequest([new PullCursor(0, Cls: SyncClasses.Catalog), new PullCursor(0, Cls: SyncClasses.Usage)], 100, 1_000_000));
        pulled.Events.Where(e => e.Author == device.Id).Select(e => e.Kind).Should().Equal(SyncEventKinds.CliState, SyncEventKinds.CliState);
        pulled.Events.Should().Contain(e => e.Kind == SyncEventKinds.DeviceUpsert, "第 3 階段的伺服器事件照常");
        await Task.Delay(200);
        Received(client).Should().NotContain(m => m is WatchChanged || m is SnapshotRequest);
    }

    [Fact]
    public async Task 沒宣告功能的裝置_收不到WatchChanged與SnapshotRequest_LiveRelay被丟掉()
    {
        // WP4.1 的桌面（不宣告任何功能）照樣可以送對話事件（D35：收件不看功能），但伺服器不送它第 4 階段的訊息；
        // 對照組是同時在線、宣告 conv1＋live1 的另一台：同一個瀏覽器看著兩台的對話，只有它收到
        await using var host = await HubTestHost.StartAsync(clock: HourClock());
        using var legacy = await TestDevice.AddAsync(host, "WP4.1");
        using var current = await TestDevice.AddAsync(host, "WP4.2");
        await using var old = await DeviceHubClient.ConnectAsync(host, legacy.Token);
        await using var live = await DeviceHubClient.ConnectAsync(host, current.Token);
        var (oldHello, _) = await old.HandshakeAsync(legacy, DeviceHubClient.ObservedSpki(host));
        oldHello.Features.Should().Equal(new[] { DeviceFeatureRules.Conv1, DeviceFeatureRules.Prov1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Update1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1, DeviceFeatureRules.Media1, DeviceFeatureRules.Stat1, DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2, DeviceFeatureRules.Sched1, DeviceFeatureRules.Collab1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2 }, "伺服器照樣宣告（WP4.5 起含 cmd1、WP4.7 起含 media1、U2 起含 update1、桌面遠端操作起含 peer1、電腦狀態起含 stat1、token 計數起含 tok1、即時增量起含 live2、其他電腦的 token 起含 tok2、遠端執行指令起含 exec1、排程頁起含 sched1、多人協作起含 collab1）；協商出的是交集（空的）");
        await live.HandshakeAsync(current, DeviceHubClient.ObservedSpki(host), features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1]);
        (await live.WaitForAsync<WatchChanged>()).ConversationIds.Should().BeEmpty();
        var theirs = ConversationHarness.ConversationId(9);
        (await ConversationEventLogTests.Append(host, legacy.Id, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta("舊的")))).Ack.Results
            .Should().OnlyContain(r => r.Rejected == null, "收件不看連線協商出的功能（D35）");
        (await ConversationEventLogTests.Append(host, current.Id, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta("新的"), conversationId: theirs))).Ack.Results
            .Should().OnlyContain(r => r.Rejected == null);

        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        await owner.Hub.Connection.InvokeAsync(BrowserHubMethods.ClientState, new JObject
        {
            ["visible"] = true,
            ["focused"] = true,
            ["openConversationIds"] = new JArray(Conv, theirs),
            ["tzOffsetMinutes"] = -480,
        });
        (await live.WaitForAsync<WatchChanged>(w => w.ConversationIds.Count > 0)).ConversationIds.Should().Equal(new[] { theirs }, "只列這台擁有的、看得到的");
        foreach (var id in new[] { Conv, theirs })
        {
            (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = id }))["accepted"]!.Value<bool>().Should().BeTrue();
            await owner.Hub.WaitForAsync("conversation", m => m["conversationId"]!.Value<string>() == id);
        }
        (await live.WaitForAsync<SnapshotRequest>()).Should().Be(new SnapshotRequest(theirs, SnapshotReasons.Open), "open 一個沒有項目的對話：對照組收到快照請求");

        var counters = host.Services.GetRequiredService<ConversationCounters>();
        var dropped = counters.Snapshot().LiveDropped![LiveDropReason.NotOwner];
        await old.Connection.InvokeAsync(DeviceHubMethods.LiveRelay, Relay(Conv, "舊桌面的即時更新"));
        await live.Connection.InvokeAsync(DeviceHubMethods.LiveRelay, Relay(theirs, "新桌面的即時更新"));
        var pushed = await owner.Hub.WaitForAsync("item");
        pushed["conversationId"]!.Value<string>().Should().Be(theirs, "對照組的即時更新照常推到瀏覽器");
        counters.Snapshot().LiveDropped![LiveDropReason.NotOwner].Should().Be(dropped + 1, "沒協商出 live1 的 LiveRelay 丟掉、只計數");
        old.Connection.State.Should().Be(HubConnectionState.Connected, "不回錯誤、不中斷");
        await owner.Hub.AssertNoMessageAsync("item", 300);

        Received(old).Should().NotContain(m => m is WatchChanged || m is SnapshotRequest, "沒宣告功能的連線一則都不送");
    }

    // ───── 回滾到 1724718（MYRIAGENT_SYNC_OLD_HUB） ─────

    [EnvPathFact(OldHubVariable, OldHubReason)]
    public async Task 舊組建_開v3的syncdb_healthz200_Hello與Pull照常_對話事件invalid()
    {
        var dir = TempDir.Create("myriagenthub-oldhub");
        var copy = TempDir.Create("myriagenthub-oldhub-copy");
        try
        {
            using var seeded = await SeedAsync(dir);
            CopyDirectory(dir, copy);

            using (var old = await StartOldAsync(copy))
            {
                var warning = await WaitForLogAsync(copy, "比程式新");
                output.WriteLine("舊組建的記錄：" + warning);
                warning.Should().Contain("\"l\":\"Warning\"", "舊組建開比它新的檔只記一筆警告");
                var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(old.BaseAddress, seeded.Device.Token);
                await using (client)
                {
                    var (hello, prove) = await client.HandshakeAsync(seeded.Device, HandshakeRules.SpkiSha256(seen), new OutboxPosition(seeded.Next, null, 1),
                        features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1]);
                    hello.Features.Should().BeNull("1724718 沒有宣告任何功能");
                    (prove.NextDeviceSeqExpected, prove.Resync).Should().Be((seeded.Next, false), "WP4.2 寫過的事件都在");

                    var ack = await PublishAsync(client,
                        CliState(seeded.Next),
                        ConversationEventLogTests.Ev(seeded.Next + 1, K.Item, ConversationSamples.Item("回滾後送的", id: "item2", ord: 2)),
                        ConversationEventLogTests.Ev(seeded.Next + 2, K.Created, ConversationSamples.Meta("新對話"), conversationId: ConversationSamples.OtherConv));
                    ack.Error.Should().BeNull();
                    ack.Results[0].Should().Match<BatchEventResult>(r => r.Seq != null && r.Rejected == null, "第 3 階段的事件照常");
                    ack.Results.Skip(1).Should().OnlyContain(r => r.Rejected == SyncErrorCodes.Invalid && r.Seq == null, "舊組建對所有對話事件回 invalid");

                    var pulled = await client.Connection.InvokeAsync<PullResponse>(DeviceHubMethods.Pull,
                        new PullRequest([new PullCursor(0, Cls: SyncClasses.Catalog)], 100, 1_000_000));
                    pulled.Events.Where(e => e.Author == seeded.Device.Id).Select(e => e.Kind).Should().Equal(new[] { SyncEventKinds.CliState, SyncEventKinds.CliState },
                        "WP4.2 寫的與舊組建寫的第 3 階段事件都拉得到");
                }
                await HealthzAsync(old.BaseAddress);
                (await old.StopAsync()).Should().BeTrue("舊組建照樣以 admin stop 正常結束");
            }

            using var read = SqliteSetup.Open(new HubPaths(copy).SyncDb, readOnly: true);
            SqliteSetup.Scalar(read, "SELECT value FROM server_meta WHERE key = 'schema_version'")
                .Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "舊組建不動結構、不降 schema_version（F48）");
            Convert.ToInt64(SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM conversation_unread")).Should().Be(1, "v3 的表與資料原樣留著");
            Convert.ToInt64(SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM rejected_events WHERE reason = 'invalid' AND conversation_id IS NOT NULL")).Should().Be(2);
        }
        finally
        {
            TempDir.TryDelete(dir);
            TempDir.TryDelete(copy);
        }
    }

    [EnvPathFact(OldHubVariable, OldHubReason)]
    public async Task 舊組建收到空物件的對話事件_invalid_只存空物件()
    {
        // 提案 C10：沒有 conv1 的連線上，WP4.3 的桌面把待送的對話列原地換成 {}（保留 deviceSeq、kind、stream、conversationId、epoch）
        var dir = TempDir.Create("myriagenthub-oldhub");
        try
        {
            using var seeded = await SeedAsync(dir);
            using (var old = await StartOldAsync(dir))
            {
                var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(old.BaseAddress, seeded.Device.Token);
                await using (client)
                {
                    await client.HandshakeAsync(seeded.Device, HandshakeRules.SpkiSha256(seen), new OutboxPosition(seeded.Next, null, 1),
                        features: [DeviceFeatureRules.Conv1]);
                    var ack = await PublishAsync(client, Empties(seeded.Next));
                    ack.Error.Should().BeNull();
                    ack.Results.Should().HaveCount(3).And.OnlyContain(r => r.Rejected == SyncErrorCodes.Invalid && r.Seq == null);
                }
                (await old.StopAsync()).Should().BeTrue();
            }

            using var read = SqliteSetup.Open(new HubPaths(dir).SyncDb, readOnly: true);
            using var cmd = read.CreateCommand();
            cmd.CommandText = "SELECT device_seq, kind, payload, enc, conversation_id, epoch, stream FROM rejected_events ORDER BY device_seq";
            using var reader = cmd.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add($"{reader.GetInt64(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}|{reader.GetString(4)}|{reader.GetInt64(5)}|{reader.GetString(6)}");
            var stream = SyncStreams.Conversation(Conv);
            rows.Should().Equal(
                $"{seeded.Next}|{K.Item}|{{}}|json|{Conv}|1|{stream}",
                $"{seeded.Next + 1}|{K.Meta}|{{}}|json|{Conv}|1|{stream}",
                $"{seeded.Next + 2}|{K.Deleted}|{{}}|json|{Conv}|1|{stream}");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [EnvPathFact(OldHubVariable, OldHubReason)]
    public async Task 舊組建寫過之後再升級_不重建_物化與events一致()
    {
        var dir = TempDir.Create("myriagenthub-oldhub");
        try
        {
            using var seeded = await SeedAsync(dir);
            using (var old = await StartOldAsync(dir))
            {
                var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(old.BaseAddress, seeded.Device.Token);
                await using (client)
                {
                    await client.HandshakeAsync(seeded.Device, HandshakeRules.SpkiSha256(seen), new OutboxPosition(seeded.Next, null, 1),
                        features: [DeviceFeatureRules.Conv1]);
                    // 提案 C10 的 {}、回滾那一刻已送出的原文（K1）、第 3 階段的事件與一筆不合格的第 3 階段事件
                    var bogus = new SyncEvent(seeded.Next + 4, SyncStreams.Catalog, "bogus.kind", PayloadRules.EncJson, PayloadRules.Sha256Hex("{}"), "{}");
                    var ack = await PublishAsync(client, [.. Empties(seeded.Next),
                        ConversationEventLogTests.Ev(seeded.Next + 3, K.Item, ConversationSamples.Item("回滾那一刻已送出的", id: "item2", ord: 2)),
                        bogus,
                        CliState(seeded.Next + 5)]);
                    ack.Results.Select(r => r.Rejected).Should().Equal(SyncErrorCodes.Invalid, SyncErrorCodes.Invalid, SyncErrorCodes.Invalid, SyncErrorCodes.Invalid,
                        SyncErrorCodes.Invalid, null);
                }
                (await old.StopAsync()).Should().BeTrue();
            }

            var calls = 0;
            var hooks = new ConversationRebuildHooks { AfterChunk = (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; } };
            await using var host = await HubTestHost.StartAsync(dataDir: dir, clock: HourClock(), services: s => s.AddSingleton(hooks));
            var rebuild = host.Services.GetRequiredService<ConversationRebuild>();
            await rebuild.FirstRun.WaitAsync(TimeSpan.FromSeconds(30));

            calls.Should().Be(0, "版本鍵相等：不重建（D14）");
            ConversationEventLogTests.Rows(host, $"SELECT value FROM server_meta WHERE key = '{ConversationMaterializer.VersionKey}'").Single()[0]
                .Should().Be(ConversationMaterializer.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var directory = host.Services.GetRequiredService<ConversationDirectoryCache>();
            directory.RebuildCounts().Should().Be((0, 0));
            var afterUpgrade = ConversationEventLogTests.FoldDump(host);
            afterUpgrade.Sha.Should().Be(seeded.Dump.Sha, "舊組建不碰物化表");
            directory.TryGet(Conv, out var entry).Should().BeTrue("目錄快取照常載入");
            entry!.ItemCount.Should().Be(1);
            host.Services.GetRequiredService<ConversationUnread>().Contains(Conv).Should().BeTrue("未讀照常載入");

            // 物化表等於依 seq 摺疊：從 events 重建一次，結果相同（舊組建留下的只在 rejected_events）
            await rebuild.RebuildAsync("回滾後的檢查", CancellationToken.None);
            ConversationEventLogTests.FoldDump(host).Sha.Should().Be(seeded.Dump.Sha);

            // D40：舊組建留下的對話被拒列 30 天後刪掉，第 3 階段的被拒列不動
            EventLogTests.Count(host, "SELECT COUNT(*) FROM rejected_events WHERE conversation_id IS NOT NULL").Should().Be(4);
            var maintenance = host.Services.GetRequiredService<ConversationMaintenance>();
            (await maintenance.PurgeRejectedAsync(CancellationToken.None)).Should().BeEmpty("還在保留期內");
            host.Clock.Advance(TimeSpan.FromDays(31));
            (await maintenance.PurgeRejectedAsync(CancellationToken.None)).Should().Equal(4);
            ConversationEventLogTests.Rows(host, "SELECT kind FROM rejected_events").Select(r => (string)r[0]!).Should().Equal("bogus.kind");

            // 升級回來之後照常收對話事件（桌面在下一次 conv1 連線重整補送）
            var resent = await ConversationEventLogTests.Append(host, seeded.Device.Id,
                ConversationEventLogTests.Ev(seeded.Next + 6, K.Item, ConversationSamples.Item("升級回來之後補送的", id: "item2", ord: 2)));
            resent.Ack.Results.Single().Should().Match<BatchEventResult>(r => r.Seq != null && r.Rejected == null);
            output.WriteLine("回滾前後的物化指紋：" + afterUpgrade.Describe());
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    // ───── 工具 ─────

    /// <summary>WP4.2 的組建寫好的資料根：一台裝置（權杖簽發時間用真的時鐘）與它的下一個 deviceSeq、寫完時三張物化表的指紋</summary>
    private sealed record Seeded(TestDevice Device, long Next, ConversationEventLogTests.FoldSnapshot Dump) : IDisposable
    {
        public void Dispose() => Device.Dispose();
    }

    /// <summary>
    /// 以 WP4.2 的組建（測試主機）寫入第 3 階段的 cli.state、一個對話（created、item、完成的 attention→沒有瀏覽器看見而標未讀）並停止：
    /// sync.db 是目前的版本、有 conversation_unread（v3 的表）的列
    /// </summary>
    private static async Task<Seeded> SeedAsync(string dir)
    {
        await using var host = await HubTestHost.StartAsync(dataDir: dir, clock: HourClock());
        var device = await TestDevice.AddAsync(host, "回滾測試", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var outcome = await EventLogTests.AppendAsync(host, device.Id, EventLogTests.Batch(
            CliState(1),
            ConversationEventLogTests.Ev(2, K.Created, ConversationSamples.Meta()),
            ConversationEventLogTests.Ev(3, K.Item, ConversationSamples.Item()),
            ConversationEventLogTests.Ev(4, K.Attention, new JObject { ["kind"] = "completed" })));
        outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null && r.Rejected == null);
        var writer = host.Services.GetRequiredService<SyncWriter>();
        var clock = Stopwatch.StartNew();
        while (EventLogTests.Count(host, "SELECT COUNT(*) FROM conversation_unread") == 0)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("閘道沒有標未讀");
            await Task.Delay(20);
            await writer.WriteAsync(_ => { });
        }
        ConversationEventLogTests.Text(host, "SELECT value FROM server_meta WHERE key = 'schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new Seeded(device, 5, ConversationEventLogTests.FoldDump(host));
    }

    private static async Task<HubProcess> StartOldAsync(string dataDir)
    {
        var exe = Environment.GetEnvironmentVariable(OldHubVariable)!;
        File.Exists(exe).Should().BeTrue($"{OldHubVariable} 要指向舊組建的 MyriagentHub.exe：{exe}");
        // 記錄檔打開：要看舊組建記下的「比程式新」警告
        return await HubProcess.StartAsync(dataDir, HubProcess.FreePort(), exe: exe,
            settings: new Dictionary<string, string?> { ["Hub:FileLog:Enabled"] = "true" });
    }

    /// <summary>等到資料根的記錄檔（diag\*.log，JSON 行）出現含 <paramref name="text"/> 的一行，回傳那一行（記錄在背景執行緒寫檔）</summary>
    private static async Task<string> WaitForLogAsync(string dataDir, string text)
    {
        var diag = new HubPaths(dataDir).DiagDir;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            foreach (var file in Directory.Exists(diag) ? Directory.GetFiles(diag, "*.log") : [])
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Contains(text, StringComparison.Ordinal))
                        return line;
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("記錄檔裡沒有：" + text);
    }

    private static async Task HealthzAsync(Uri baseAddress)
    {
        using var http = new HttpClient(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } });
        using var response = await http.GetAsync(new Uri(baseAddress, "/healthz"));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    private static async Task<BatchAck> PublishAsync(DeviceHubClient client, params SyncEvent[] events)
    {
        var batch = EventLogTests.Batch(events);
        await client.Connection.InvokeAsync(DeviceHubMethods.PublishBatch, batch);
        return await client.WaitForAsync<BatchAck>(a => a.BatchId == batch.BatchId, 10_000);
    }

    /// <summary>提案 C10 換成 {} 的三筆（conv.item、conv.meta、conv.deleted；deviceSeq 從 <paramref name="first"/> 起）</summary>
    private static SyncEvent[] Empties(long first) =>
    [
        Empty(first, K.Item),
        Empty(first + 1, K.Meta),
        Empty(first + 2, K.Deleted),
    ];

    private static SyncEvent Empty(long deviceSeq, string kind) =>
        new(deviceSeq, SyncStreams.Conversation(Conv), kind, PayloadRules.EncJson, PayloadRules.Sha256Hex("{}"), "{}", Conv, 1);

    private static SyncEvent CliState(long deviceSeq) =>
        EventLogTests.Ev(deviceSeq, SyncEventKinds.CliState, new CliStatePayload(new Dictionary<string, DeviceCliView>
        {
            ["Claude"] = new(true, "2.1.0", "native", "2.2.0", false, 0, 0, null, deviceSeq),
        }));

    private static LiveRelay Relay(string conversationId, string text)
    {
        var item = ConversationSamples.Item(text, "Running", "live1", 5, 1);
        return new LiveRelay(conversationId, 1, 5, 1, item.ToString(Newtonsoft.Json.Formatting.None));
    }

    private static List<object> Received(DeviceHubClient client)
    {
        var list = new List<object>();
        while (client.Received.Reader.TryRead(out var message))
            list.Add(message);
        return list;
    }

    /// <summary>起點是現在截到整點的假時鐘（D43：和以真實時間判斷到期的元件不衝突；子程序的舊組建用真的時鐘）</summary>
    private static ManualTimeProvider HourClock()
    {
        var now = DateTimeOffset.UtcNow;
        return new ManualTimeProvider(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
