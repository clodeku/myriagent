using System.Globalization;
using Myriagent.Sync;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;
using SoakDevice = MyriagentHub.Tests.Realtime.SoakTests.SoakDevice;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// S8 子集的對話版（WP4.2 計畫步 14）：照 <see cref="SoakTests"/> 的作法（MyriagentHub 以子程序執行、串流中強制結束並立刻重啟兩次、裝置斷線重連從 Prove 的期待值續傳、
/// 第一台刻意重送已寫入的事件），三台宣告 conv1 的裝置各自擁有兩個對話，outbox 裡是對話事件（created、item、itemRemoved、queue、turn、attention、meta，
/// 少數帶權杖要再遮蔽）夾著第 3 階段的 <c>cli.state</c>。
/// 驗收：持久事件 0 遺失、0 重複（每台的 deviceSeq 剛好 1..N 各一筆、雜湊是裝置送的）、沒有被擋的事件；
/// 三張物化表逐列等於依 seq 摺疊（獨立的摺疊模型，時間取每筆的 <c>events.created_at</c>）；再從 events 重建一次，結果不變
/// </summary>
public sealed class ConversationSoakTests(ITestOutputHelper output)
{
    private const int DeviceCount = 3;
    private const int EventsPerDevice = 400;
    private const int ConversationsPerDevice = 2;
    private const int Seed = 5150;

    [Fact]
    public async Task S8_對話事件_串流中強制結束並立刻重啟_0遺失0重複_物化等於依seq摺疊()
    {
        var dir = TempDir.Create("myriagenthub-convsoak");
        var devices = new List<SoakDevice>();
        var scripts = new Dictionary<string, Script>(StringComparer.Ordinal);
        try
        {
            await using (var seed = await HubTestHost.StartAsync(dataDir: dir))
            {
                for (var i = 0; i < DeviceCount; i++)
                {
                    // 子程序用真的時鐘：權杖的簽發時間也用真的（同 SoakTests）
                    var device = await TestDevice.AddAsync(seed, $"電腦{i + 1}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    var script = new Script(i, Seed + i);
                    scripts[device.Id] = script;
                    devices.Add(new SoakDevice(device, EventsPerDevice, resendOverlap: i == 0, script.Event, [DeviceFeatureRules.Conv1]));
                }
            }

            await SoakTests.RunWithKillsAsync(dir, devices, output);

            await using var check = await HubTestHost.StartAsync(dataDir: dir);
            SoakTests.AssertStored(check, devices);
            EventLogTests.Count(check, "SELECT COUNT(*) FROM rejected_events").Should().Be(0, "每一筆都合格：沒有被擋的");

            // 依 seq 摺疊：每筆照它的 (作者, deviceSeq) 找回產生的事件，時間用伺服器收到它的時間
            var model = new ConversationEventLogTests.FoldModel(0);
            var rows = ConversationEventLogTests.Rows(check, "SELECT seq, author, device_seq, created_at FROM events WHERE cls IN ('meta', 'item') ORDER BY seq");
            rows.Should().HaveCount(scripts.Values.Sum(s => s.ConversationEvents));
            foreach (var row in rows)
            {
                var author = (string)row[1]!;
                var generated = scripts[author].Generated[(int)(long)row[2]!];
                model.Decide(author, generated).Should().BeNull(generated.Label);
                model.Fold(author, generated, (long)row[0]!, at: (long)row[3]!);
            }
            ConversationEventLogTests.AssertMatchesModel(check, model);
            model.Items.Values.Should().Contain(i => i.Removed).And.Contain(i => !i.Removed);
            var remasked = scripts.Values.Sum(s => s.Remasked);
            remasked.Should().BeGreaterThan(0, "有幾筆帶權杖、存的是遮蔽版");

            // 重建（從 events 依 seq 重播）的結果與強制結束之間的增量物化相同
            var incremental = ConversationEventLogTests.FoldDump(check);
            await check.Services.GetRequiredService<ConversationRebuild>().RebuildAsync("soak", CancellationToken.None);
            var rebuilt = ConversationEventLogTests.FoldDump(check);
            rebuilt.Sha.Should().Be(incremental.Sha);
            output.WriteLine($"物化：{incremental.Describe()}；再遮蔽 {remasked} 筆；對話事件 {rows.Count} 筆");
        }
        finally
        {
            foreach (var d in devices)
                d.Device.Dispose();
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// 一台裝置的 outbox 劇本（種子固定）：deviceSeq 1、2 是兩個對話的 <c>conv.created</c>，之後是對話事件夾著 <c>cli.state</c>；
    /// 每一筆都是這台擁有、政策 full 的對話的合格事件（全部會被接受）。<see cref="Generated"/> 以 deviceSeq 為索引記下對話事件與伺服器會存的版本
    /// </summary>
    private sealed class Script
    {
        private static readonly string[] Statuses = ["Running", "Waiting", "Done", "Failed", "Declined", "Cancelled"];
        private static readonly string[] Kinds = ["Assistant", "User", "Tool", "Notice"];
        private static readonly string[] Attention = ["question", "approval", "plan", "completed", "failed"];

        private readonly object gate = new();
        private readonly Random rng;
        private readonly string[] conversations;
        private readonly Dictionary<(string Conversation, int Item), long> versions = [];

        public Script(int index, int seed)
        {
            rng = new Random(seed);
            conversations = Enumerable.Range(0, ConversationsPerDevice)
                .Select(k => (index * 16 + k + 1).ToString("x32", CultureInfo.InvariantCulture))
                .ToArray();
        }

        /// <summary>對話事件（deviceSeq → 產生的事件）</summary>
        public Dictionary<int, ConversationEventLogTests.FoldEvent> Generated { get; } = [];

        public int ConversationEvents
        {
            get
            {
                lock (gate)
                    return Generated.Count;
            }
        }

        public int Remasked
        {
            get
            {
                lock (gate)
                    return Generated.Values.Count(g => g.Remasked);
            }
        }

        public SyncEvent Event(int deviceSeq)
        {
            lock (gate)
            {
                if (Next(deviceSeq) is not { } generated)
                    return EventLogTests.Ev(deviceSeq, SyncEventKinds.CliState, new CliStatePayload(new Dictionary<string, DeviceCliView>
                    {
                        ["Claude"] = new(true, "2.1.0", "native", "2.2.0", false, 0, 0, null, deviceSeq),
                    }));
                Generated[deviceSeq] = generated;
                return generated.Event;
            }
        }

        /// <summary>這個 deviceSeq 的對話事件；null＝這一筆是第 3 階段的 cli.state</summary>
        private ConversationEventLogTests.FoldEvent? Next(int deviceSeq)
        {
            if (deviceSeq <= conversations.Length)
                return Make(K.Created, Meta("對話 " + deviceSeq.ToString(CultureInfo.InvariantCulture)), conversations[deviceSeq - 1], deviceSeq, K.Created);
            var conversation = conversations[rng.Next(conversations.Length)];
            var r = rng.Next(100);
            if (r < 10)
                return null;
            if (r < 60)
            {
                var item = rng.Next(12);
                var ver = versions[(conversation, item)] = versions.GetValueOrDefault((conversation, item)) + 1;
                var token = rng.Next(100) < 4;
                var text = "第 " + deviceSeq.ToString(CultureInfo.InvariantCulture) + " 筆" + (token ? " " + GitHub((char)('a' + rng.Next(26))) : string.Empty)
                    + new string('字', rng.Next(0, 400));
                var payload = Item(text, Statuses[rng.Next(Statuses.Length)], "item" + item.ToString(CultureInfo.InvariantCulture), item + 1, ver);
                payload["kind"] = Kinds[rng.Next(Kinds.Length)];
                return Make(K.Item, payload, conversation, deviceSeq, K.Item, token);
            }
            if (r < 68)
                return Make(K.ItemRemoved, ConversationEventLogTests.Removal("item" + rng.Next(14).ToString(CultureInfo.InvariantCulture)), conversation, deviceSeq, K.ItemRemoved);
            if (r < 76)
                return Make(K.Queue, Queue(Enumerable.Range(0, rng.Next(1, 4)).Select(n => "待辦 " + n.ToString(CultureInfo.InvariantCulture)).ToArray()), conversation, deviceSeq, K.Queue);
            if (r < 86)
            {
                var phase = rng.Next(2) == 0 ? ConversationTurnPhases.Started : ConversationTurnPhases.Completed;
                return Make(K.Turn, ConversationEventLogTests.Turn(phase), conversation, deviceSeq, K.Turn);
            }
            if (r < 92)
                return Make(K.Attention, new JObject { ["kind"] = Attention[rng.Next(Attention.Length)] }, conversation, deviceSeq, K.Attention);
            var meta = Meta("改名 " + deviceSeq.ToString(CultureInfo.InvariantCulture));
            meta["running"] = rng.Next(2) == 0;
            return Make(K.Meta, meta, conversation, deviceSeq, K.Meta);
        }

        private static ConversationEventLogTests.FoldEvent Make(string kind, JObject payload, string conversation, int deviceSeq, string label, bool token = false) =>
            ConversationEventLogTests.Make(kind, payload, conversation, deviceSeq, label, token: token);
    }
}
