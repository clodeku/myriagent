using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// fake-device 的 session（WP4.9 計畫步 8）：真的 MyriagentHub 子程序＋DeviceRuntime（和 session 指令同一份配對、狀態與解除配對的程式），
/// 記憶體金鑰、引擎經 TransportMeter：正式站守門讓本機的 MyriagentHub 過、協商出 conv1 與 live1、BatchAck 有量到、帶時間標記的串流在伺服器的
/// 項目文字裡解析得到、CNG 的金鑰存放區沒有這台的金鑰檔（配對中與解除配對之後都沒有）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class FakeDeviceSessionTests(MyriagentHubFixture fixture, ITestOutputHelper output) : IClassFixture<MyriagentHubFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static readonly FakeAccountSpec Account = new("fd-session", AgentProvider.Claude, "Claude fd-session", null, "fd-session@example.test", "org-fd-session", "max");

    [Fact]
    public async Task 記憶體金鑰的一輪_協商功能_BatchAck_時間標記_金鑰存放區沒有這台的檔()
    {
        var hub = fixture.Hub;
        if (!hub.IsRunning)
            await hub.LaunchAsync();

        // 守門：本機 Testing 的 MyriagentHub（主控台、自簽 localhost）可以連
        var facts = await LocalHubTarget.VerifyAsync(hub.BaseAddress, TimeSpan.FromSeconds(15));
        facts.Environment.Should().Be("console");
        LocalHubTargetRules.IsProductionTarget(facts).Should().BeFalse();

        using var dir = new TempDirectory("fd-session");
        await using var runtime = await DeviceRuntime.StartAsync(new DeviceRuntimeOptions
        {
            DataRoot = dir.Root,
            Persist = false,
            DevTrust = true,
            MachineName = "TEST-PC",
            WindowsUser = "tester",
            Accounts = [Account],
            EphemeralKey = true,
            MeasureTransport = true,
        });
        runtime.UsesEphemeralKey.Should().BeTrue();

        var code = await runtime.StartPairingAsync(hub.BaseAddress, "fd-session");
        await fixture.Owner.ApproveAsync(code.UserCode);
        await runtime.WaitPairedLiveAsync(Wait);
        var (_, device) = DeviceFile.Load(runtime.Paths.DeviceFile);
        device.Should().NotBeNull();
        device!.KeyStorage.Should().Be(PairingRules.KeyStorageSoftware);
        device.KeyName.Should().StartWith(DeviceKey.NamePrefix);
        UserKeyStore.FilesNaming(device.KeyName).Should().BeEmpty("記憶體金鑰：CNG 的金鑰存放區沒有這個名稱的檔");

        // 帶時間標記的串流（一個項目、4 段）
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var turn = ScriptedTurn.Parse(new JArray(new JObject { ["text"] = "時間標記的串流測試", ["segments"] = 4, ["delayMs"] = 30, ["stamp"] = true }));
        var chat = await runtime.ChatAsync(new ChatSpec("stamped", Account.Alias, "開始", [], turn, Wait));
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var (drained, _, rejected) = await runtime.WaitDrainedAsync(Wait);
        drained.Should().BeTrue();
        rejected.Should().Be(0);

        var status = await runtime.DescribeAsync();
        output.WriteLine(status.ToString(Newtonsoft.Json.Formatting.None));
        status["features"]!.Values<string>().Should().Contain(DeviceFeatureRules.Conv1).And.Contain(DeviceFeatureRules.Live1);
        status.Value<int>("refusedCli").Should().Be(0);
        status.Value<bool>("ephemeralKey").Should().BeTrue();
        status["batchAck"]!.Value<int>("count").Should().BeGreaterThan(0);
        status["batchAck"]!.Value<int>("engines").Should().Be(1);
        status["batchAck"]!.Value<double>("p50Ms").Should().BeGreaterThan(0);
        status["liveRelay"]!.Value<long>("sent").Should().Be(0, "沒有瀏覽器在看，不送即時更新（陽性對照在 full-flow 的 FF2：有人看時同一個計數器大於 0）");
        status["replicas"]!.Value<int?>("known").Should().NotBeNull();

        // 伺服器上這個對話的項目：完成的那一份文字裡有 4 個標記，都在送出的前後之間
        var deviceId = status.Value<string>("deviceId")!;
        // outbox 的 deviceSeq 範圍（WP4.9 步 18 的 FF9）：清空之後全部完成，max 等於伺服器記下的這台最大的 deviceSeq
        var outboxSeq = status["outboxSeq"]!;
        outboxSeq.Value<long?>("max").Should().BeGreaterThan(0);
        outboxSeq.Value<long?>("doneMax").Should().Be(outboxSeq.Value<long?>("max"));
        outboxSeq.Value<long?>("firstUndone").Should().BeNull();
        ServerMaxDeviceSeq(hub.SyncDbPath, deviceId).Should().Be(outboxSeq.Value<long?>("max"));
        var stamps = ServerItemStrings(hub.SyncDbPath, deviceId, chat.ConversationId).SelectMany(StampRules.Parse).Distinct().Order().ToList();
        stamps.Should().HaveCount(4).And.OnlyContain(ms => ms >= before && ms <= after);

        // 控制通道的 ords（WP4.9 步 15）：配對之後 sync.db 的 published_items 有每個項目的 Ord，等於中樞記憶體裡的
        await using (var control = new FakeControl(runtime))
        {
            var ords = await control.ExecuteAsync(FakeControlRules.Parse("""{"op":"ords","conversation":"stamped"}""").Command!);
            var rows = ((JArray)ords["items"]!).Cast<JObject>().ToList();
            rows.Should().HaveCount(chat.Items).And.OnlyContain(r => r["ord"]!.Type == JTokenType.Integer && r.Value<long>("ord") == r.Value<long?>("publishedOrd"));
            ords.Value<int>("withoutPublishedOrd").Should().Be(0);
            ((JArray)ords["published"]!).Select(p => p.Value<string>("id")).Should().BeEquivalentTo(rows.Select(r => r.Value<string>("id")));
        }

        await runtime.UnpairAsync(TimeSpan.FromSeconds(20));
        File.Exists(runtime.Paths.DeviceFile).Should().BeFalse();
        UserKeyStore.FilesNaming(device.KeyName).Should().BeEmpty("解除配對之後也沒有");
    }

    /// <summary>伺服器上某台裝置某個對話的 conv.item（唯讀）：payload 解開之後的每一個字串值（JSON 可能把非 ASCII 跳脫，不直接比原文）</summary>
    /// <summary>伺服器記下的這台最大的 deviceSeq（events 與 rejected_events 合起來）</summary>
    private static long? ServerMaxDeviceSeq(string syncDb, string deviceId)
    {
        using var connection = HubDb.OpenReadOnly(syncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(s) FROM (SELECT device_seq AS s FROM events WHERE author = $a UNION ALL SELECT device_seq FROM rejected_events WHERE author = $a)";
        cmd.Parameters.AddWithValue("$a", deviceId);
        return cmd.ExecuteScalar() is long max ? max : null;
    }

    private static List<string> ServerItemStrings(string syncDb, string deviceId, string conversationId)
    {
        using var connection = HubDb.OpenReadOnly(syncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT enc, payload FROM events WHERE author = $a AND conversation_id = $c AND kind = $k ORDER BY seq";
        cmd.Parameters.AddWithValue("$a", deviceId);
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$k", ConversationEventKinds.Item);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            PayloadRules.TryDecode(reader.GetString(0), reader.GetString(1), 16 << 20, out var json).Should().BeTrue();
            result.AddRange(JToken.Parse(json).SelectTokens("$..*").OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v!));
        }

        return result;
    }
}
