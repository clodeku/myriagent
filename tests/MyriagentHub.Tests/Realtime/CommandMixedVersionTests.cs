using System.Globalization;
using System.Security.Cryptography;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Trust;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static MyriagentHub.Tests.Sync.CommandRouterTests;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 遠端指令的混合版本與回滾（WP4.5 計畫步 10、0.2）：
/// <list type="bullet">
/// <item>WP4.1 的桌面（Hello 原文沒有 <c>features</c>）：協商不到 cmd1，伺服器一則 <c>CommandDelivery</c> 都不送——在線時的指令立即 unsupported；
/// 「所有電腦登出」時不在線的那台排入 pending，之後它以舊版上線、重掃也不送，停在 pending（7 天後 expired，步 8 已測）。</item>
/// <item>回滾到合併前的 main（<c>MYRIAGENT_SYNC_PREV_HUB</c>＝以 <c>git archive 5538705</c> 建出的 MyriagentHub.exe 完整路徑，沒設就略過；建法見 src/MyriagentHub/README.md）：
/// 舊組建開本包寫過的資料根、healthz 200、HelloResponse 的 features 沒有 cmd1、WP4.4 的桌面收不到指令、<c>commands</c> 表逐列不變；
/// 再升級回來時啟動掃描把非 durable 的 delivered 標成 unknown（絕不重送）、durable 的照送，稽核的雜湊鏈照常。</item>
/// </list>
/// </summary>
public sealed class CommandMixedVersionTests(ITestOutputHelper output)
{
    /// <summary>合併前的 main（本包 rebase 的基底 <c>5538705</c>）的 MyriagentHub.exe 完整路徑（建法同 <see cref="MixedVersionTests.OldHubVariable"/>，只換提交）</summary>
    public const string PrevHubVariable = "MYRIAGENT_SYNC_PREV_HUB";

    private const string PrevHubReason = "回滾到合併前的 main（5538705）的測試：值是那個組建的 MyriagentHub.exe 完整路徑，建法見 src/MyriagentHub/README.md";

    // ───── WP4.1 的桌面 ─────

    [Fact]
    public async Task WP4_1的桌面沒宣告功能_永遠收不到CommandDelivery_瀏覽器得到unsupported()
    {
        await using var s = await Setup.StartAsync(connect: false);
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await LinkAsync(s.Host, b.Id, "acct-b")).Should().Be(la);
        await using var a = await ConnectWp41Async(s.Host, s.Owner);
        s.Host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(s.Owner.Id).Single().Features.Should().BeEmpty("協商出的是空的");

        // 對話的擁有者在線、沒有 cmd1：立即 unsupported（send 另還原文字）
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "舊桌面" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("那台電腦的 Clode Myriagent 還不支援網頁版遠端操作，請先更新那台電腦");
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["text"]!.Value<string>().Should().Be("舊桌面");
        (await s.Browser.Hub.CmdAsync(new { cmd = "stop", conversationId = Conv }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);

        // 所有電腦登出：在線而沒有 cmd1 的 A 不排入，不在線的 B 排入 durable
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var logout = await s.Browser.Hub.WaitForAsync("commandResult");
        (logout["state"]!.Value<string>(), logout["message"]!.Value<string>()).Should().Be((RemoteResultStates.Done, CommandMessageRules.FanOutText(0, 1, 1)));
        var rows = ConversationEventLogTests.Rows(s.Host, "SELECT target_device_id, state, durable FROM commands");
        rows.Should().ContainSingle();
        ((string)rows[0][0]!, (string)rows[0][1]!, (long)rows[0][2]!).Should().Be((b.Id, CommandStates.Pending, 1L));

        // B 以 WP4.1 的桌面上線：Prove 之後、60 秒的重掃之後都不送，停在 pending
        await using var bClient = await ConnectWp41Async(s.Host, b);
        await Task.Delay(500);
        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.RescanIntervalMs));
        await s.Host.Services.GetRequiredService<CommandMaintenance>().SweepAsync();
        await AssertNoDeliveryAsync(bClient, 300);
        await AssertNoDeliveryAsync(a, 0);
        ConversationEventLogTests.Text(s.Host, "SELECT state FROM commands").Should().Be(CommandStates.Pending);
        var counters = s.Counters.Snapshot();
        (counters.Issued, counters.Delivered, counters.RejectedAtServer).Should().Be((1L, 0L, 2L), "B 的那一列寫進去了、沒送出；send、stop 立即拒絕");
        a.Connection.State.Should().Be(HubConnectionState.Connected, "不回錯誤、不中斷");
        bClient.Connection.State.Should().Be(HubConnectionState.Connected);
    }

    // ───── 回滾到合併前的 main（MYRIAGENT_SYNC_PREV_HUB） ─────

    [EnvPathFact(PrevHubVariable, PrevHubReason)]
    public async Task 舊組建開WP4_5寫過的sync_db_healthz200_Hello的features沒有cmd1_commands表原封不動()
    {
        var dir = TempDir.Create("myriagenthub-prevhub");
        Seeded? seeded = null;
        try
        {
            seeded = await SeedAsync(dir);
            var before = Dump(dir);
            before.Should().HaveCount(3);
            output.WriteLine("回滾前的 commands：" + string.Join(" / ", before));

            using (var prev = await StartPrevAsync(dir))
            {
                var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(prev.BaseAddress, seeded.A.Token);
                await using (client)
                {
                    // A 以 WP4.4 的桌面上線（宣告 cmd1），而且有一列 durable 的 delivered：舊組建不宣告 cmd1、不送
                    var (hello, prove) = await client.HandshakeAsync(seeded.A, HandshakeRules.SpkiSha256(seen), new OutboxPosition(seeded.ANext, null, null),
                        features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Cmd1]);
                    hello.Features.Should().Equal(new[] { DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1 }, "合併前的 main 只宣告 WP4.2 的功能");
                    (prove.NextDeviceSeqExpected, prove.Resync).Should().Be((seeded.ANext, false));
                    await AssertNoDeliveryAsync(client, 1000);
                    client.Connection.State.Should().Be(HubConnectionState.Connected);
                }
                await HealthzAsync(prev.BaseAddress);
                (await prev.StopAsync()).Should().BeTrue("舊組建照樣以 admin stop 正常結束");
            }

            Dump(dir).Should().Equal(before, "舊組建不讀、不寫 commands 表：每一列的每一欄都不變");
            using var read = SqliteSetup.Open(new HubPaths(dir).SyncDb, readOnly: true);
            SqliteSetup.Scalar(read, "SELECT value FROM server_meta WHERE key = 'schema_version'")
                .Should().Be(SyncSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture), "舊組建不動結構、不降 schema_version（F48）");
        }
        finally
        {
            seeded?.Dispose();
            TempDir.TryDelete(dir);
        }
    }

    [EnvPathFact(PrevHubVariable, PrevHubReason)]
    public async Task 再升級_非durable的delivered變unknown_durable的照送()
    {
        var dir = TempDir.Create("myriagenthub-prevhub");
        Seeded? seeded = null;
        try
        {
            seeded = await SeedAsync(dir);
            using (var prev = await StartPrevAsync(dir))
            {
                // 回滾期間 A 上線過（舊組建記下它的 Hello）
                var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(prev.BaseAddress, seeded.A.Token);
                await using (client)
                {
                    await client.HandshakeAsync(seeded.A, HandshakeRules.SpkiSha256(seen), new OutboxPosition(seeded.ANext, null, null),
                        features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);
                    await AssertNoDeliveryAsync(client, 500);
                }
                (await prev.StopAsync()).Should().BeTrue();
            }

            // 再升級：啟動掃描（D28）
            await using var host = await HubTestHost.StartAsync(dataDir: dir, clock: new ManualTimeProvider(DateTimeOffset.UtcNow));
            var stop = Row(host, seeded.StopCid);
            (stop.State, stop.DoneAt).Should().Be((CommandStates.Unknown, host.Clock.NowMs), "非 durable、未結束的列標成 unknown，絕不重送");
            JObject.Parse(stop.ResultJson!)["state"]!.Value<string>().Should().Be(CommandStates.Unknown);
            var aRow = Row(host, seeded.ALogoutCid);
            (aRow.State, aRow.DoneAt, aRow.HasUnsignedBody).Should().Be((CommandStates.Pending, (long?)null, true), "durable 的 delivered 改回 pending、下次 Prove 重簽");
            var bRow = Row(host, seeded.BLogoutCid);
            (bRow.State, bRow.HasUnsignedBody).Should().Be((CommandStates.Pending, true), "回滾前就是 pending 的照舊");
            host.Services.GetRequiredService<CommandCounters>().Snapshot().Unknown.Should().Be(1);

            // A、B 以 WP4.4 的桌面上線：各收到自己的那一列（同一個 cid、內容摘要不變），stop 不重送
            var (a, aHello) = await ConnectAsync(host, seeded.A);
            await using (a)
            {
                var delivery = await a.WaitForAsync<CommandDelivery>();
                delivery.CommandId.Should().Be(seeded.ALogoutCid);
                Verify(host, delivery, seeded.A.Id, aHello.NonceS, aRow.Sha);
                await AssertNoDeliveryAsync(a, 300);
                await a.SendResultAsync(new CommandResult(delivery.CommandId, RemoteResultStates.Done));
            }
            var (b, bHello) = await ConnectAsync(host, seeded.B);
            await using (b)
            {
                var delivery = await b.WaitForAsync<CommandDelivery>();
                delivery.CommandId.Should().Be(seeded.BLogoutCid);
                Verify(host, delivery, seeded.B.Id, bHello.NonceS, bRow.Sha);
                await b.SendResultAsync(new CommandResult(delivery.CommandId, RemoteResultStates.Done));
            }
            foreach (var cid in new[] { seeded.ALogoutCid, seeded.BLogoutCid })
            {
                var row = Row(host, cid);
                (row.State, row.HasUnsignedBody).Should().Be((CommandStates.Done, false));
            }
            Row(host, seeded.StopCid).State.Should().Be(CommandStates.Unknown);

            // 回滾前寫的 command.issued 與舊組建寫的稽核都在同一條雜湊鏈上
            var audits = await ConversationEventLogTests.AuditRowsAsync(host);
            audits.Count(r => r.Action == AuditActions.CommandIssued).Should().Be(3);
            (await host.Services.GetRequiredService<AuditLog>().VerifyAsync()).Ok.Should().BeTrue();
        }
        finally
        {
            seeded?.Dispose();
            TempDir.TryDelete(dir);
        }
    }

    // ───── 工具 ─────

    /// <summary>本包的組建寫好的資料根：兩台電腦（A 在線時寫的列、B 不在線）與三列指令</summary>
    private sealed record Seeded(TestDevice A, TestDevice B, long ANext, string StopCid, string ALogoutCid, string BLogoutCid) : IDisposable
    {
        public void Dispose()
        {
            A.Dispose();
            B.Dispose();
        }
    }

    /// <summary>
    /// 以本包的組建（測試主機，真實時間起點的手動時鐘：子程序用真的時鐘）寫入：A 在線時的 <c>stop</c>（非 durable、delivered）、
    /// 「所有電腦登出」的兩列（A 的 durable delivered、B 不在線的 durable pending），等三筆 <c>command.issued</c> 稽核寫完再停止
    /// </summary>
    private static async Task<Seeded> SeedAsync(string dir)
    {
        await using var s = await Setup.StartAsync(dataDir: dir, clock: new ManualTimeProvider(DateTimeOffset.UtcNow));
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await LinkAsync(s.Host, b.Id, "acct-b");

        (await s.Browser.Hub.CmdAsync(new { cmd = "stop", conversationId = Conv }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var stopCid = (await s.Device!.WaitForAsync<CommandDelivery>()).CommandId;
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var aLogoutCid = (await s.Device.WaitForAsync<CommandDelivery>()).CommandId;
        var bLogoutCid = (string)ConversationEventLogTests.Rows(s.Host, $"SELECT command_id FROM commands WHERE target_device_id = '{b.Id}'").Single()[0]!;

        await WaitUntilAsync(() => States(s.Host).SequenceEqual(new[]
        {
            (stopCid, CommandStates.Delivered, false),
            (aLogoutCid, CommandStates.Delivered, true),
            (bLogoutCid, CommandStates.Pending, true),
        }.OrderBy(r => r.Item1, StringComparer.Ordinal)), because: "A 的兩列 delivered、B 的 pending");
        for (var i = 0; (await ConversationEventLogTests.AuditRowsAsync(s.Host)).Count(r => r.Action == AuditActions.CommandIssued) < 3; i++)
        {
            if (i > 100)
                throw new TimeoutException("command.issued 稽核不夠");
            await Task.Delay(20);
        }

        var aNext = NextSeq(s.Host, s.Owner.Id);
        var a = TestDevice.Paired(s.Owner.Id, s.Owner.Token, ECDsa.Create(s.Owner.Key.ExportParameters(includePrivateParameters: true)));
        var bCopy = TestDevice.Paired(b.Id, b.Token, ECDsa.Create(b.Key.ExportParameters(includePrivateParameters: true)));
        return new Seeded(a, bCopy, aNext, stopCid, aLogoutCid, bLogoutCid);
    }

    private static List<(string Cid, string State, bool Durable)> States(HubTestHost host) =>
        ConversationEventLogTests.Rows(host, "SELECT command_id, state, durable FROM commands ORDER BY command_id")
            .Select(r => ((string)r[0]!, (string)r[1]!, (long)r[2]! != 0)).ToList();

    /// <summary><c>commands</c> 表的每一列、每一欄（依 cid；NULL 寫成 ∅），舊組建停止之後直接讀檔</summary>
    private static List<string> Dump(string dataDir)
    {
        using var read = SqliteSetup.Open(new HubPaths(dataDir).SyncDb, readOnly: true);
        using var cmd = read.CreateCommand();
        cmd.CommandText = "SELECT * FROM commands ORDER BY command_id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.GetName(i) + "=" + (reader.IsDBNull(i) ? "∅" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            rows.Add(string.Join("|", values));
        }
        return rows;
    }

    private sealed record RowView(string State, long? DoneAt, string? ResultJson, bool HasUnsignedBody, string Sha);

    private static RowView Row(HubTestHost host, string cid)
    {
        var r = ConversationEventLogTests.Rows(host, $"SELECT state, done_at, result_json, unsigned_body, content_sha256 FROM commands WHERE command_id = '{cid}'").Single();
        return new RowView((string)r[0]!, (long?)r[1], (string?)r[2], r[3] is not null, (string)r[4]!);
    }

    private static async Task<HubProcess> StartPrevAsync(string dataDir)
    {
        var exe = Environment.GetEnvironmentVariable(PrevHubVariable)!;
        File.Exists(exe).Should().BeTrue($"{PrevHubVariable} 要指向合併前的 main 建出的 MyriagentHub.exe：{exe}");
        return await HubProcess.StartAsync(dataDir, HubProcess.FreePort(), exe: exe);
    }

    /// <summary>WP4.1 的桌面：Hello 的原文寫死（SyncJson：camelCase、null 不寫），沒有 <c>features</c> 這個鍵；照常 Prove</summary>
    private static async Task<DeviceHubClient> ConnectWp41Async(HubTestHost host, TestDevice device)
    {
        var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var spki = DeviceHubClient.ObservedSpki(host);
        var nonceD = HandshakeRules.NewNonce();
        var raw = """
            {"protocolVersion":1,"deviceId":"@id","bootId":"boot-1","appVersion":"0.1.0","machineName":"TEST-PC","windowsUser":"User","nonceD":"@nonce","observedSpkiSha256":"@spki","outbox":{"nextDeviceSeq":@next},"cursors":{"catalog":0,"usage":0},"policy":{"remoteControlLevel":"Control","allowRemoteFullAccess":false}}
            """.Replace("@id", device.Id).Replace("@nonce", nonceD).Replace("@spki", spki)
            .Replace("@next", NextSeq(host, device.Id).ToString(CultureInfo.InvariantCulture));
        var request = JObject.Parse(raw);
        request.ContainsKey("features").Should().BeFalse("前提：原文就是 WP4.1 的形狀");
        var hello = await client.Connection.InvokeAsync<JObject>(DeviceHubMethods.Hello, request);
        hello["features"]!.ToObject<string[]>().Should().Contain(DeviceFeatureRules.Cmd1, "伺服器照樣宣告，協商出的是交集");
        var sig = P256KeyRules.Sign(device.Key, HandshakeRules.DeviceSignedData(device.Id, hello["nonceS"]!.Value<string>()!, nonceD, spki));
        (await client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig))).AccessToken.Should().NotBeNullOrEmpty();
        return client;
    }

    /// <summary>以 TestServer 連線並以 <c>[conv1, cmd1]</c> 交握（WP4.4 的桌面）</summary>
    private static async Task<(DeviceHubClient Client, HelloResponse Hello)> ConnectAsync(HubTestHost host, TestDevice device)
    {
        var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (hello, _) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), new OutboxPosition(NextSeq(host, device.Id), null, null),
            features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);
        return (client, hello);
    }

    /// <summary>以裝置的方式驗重簽的信封（durable、內容摘要等於列的 content_sha256）</summary>
    private static void Verify(HubTestHost host, CommandDelivery delivery, string deviceId, string conn, string sha)
    {
        var options = host.Services.GetRequiredService<IOptions<HubOptions>>().Value;
        var target = new CommandEnvelopeTarget(host.Services.GetRequiredService<HubIdentity>().Spki,
            HostingRules.NormalizeOrigin(HostingRules.PublicOrigin(options))!, deviceId, conn);
        var verdict = CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, target, new ServerTimeBounds(host.Clock.NowMs, host.Clock.NowMs));
        verdict.Ok.Should().BeTrue(verdict.Detail);
        (verdict.Body!.Durable, verdict.ContentSha256).Should().Be((true, sha));
    }

    private static async Task HealthzAsync(Uri baseAddress)
    {
        using var http = new HttpClient(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } });
        using var response = await http.GetAsync(new Uri(baseAddress, "/healthz"));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
