using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.LongRun;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;
using LongRunGate = Myriagent.Tests.Sync.LongRun.LongRun;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// WP4.3 計畫步 13：真的 MyriagentHub 子程序＋整條桌面發布鏈（tools/fake-device 的 DeviceRuntime：中樞、發布者、引擎；劇本化的會話）。
/// <list type="bullet">
/// <item>沒有 conv1 的伺服器（<c>MYRIAGENT_SYNC_OLD_HUB</c> 指的 1724718 版）：上一次進 Live 時有 conv1、離線時寫的對話事件
/// 在送出前換成 <c>{}</c>，伺服器以 invalid 擋下，<c>rejected_events</c> 與整個資料庫都沒有對話的文字；進 Live 之後不寫新的對話事件，目錄同步照常。
/// 同一棵樹的 MyriagentHub 從 WP4.2 合併起一律宣告 conv1，重新為基底之前那個「基底伺服器」的版本在步 16 刪掉，沒設變數時的涵蓋在 FakeSyncServer（步 10）。</item>
/// <item>conv1 的回補（<c>MYRIAGENT_SYNC_IT=1</c>；同一棵樹的 MyriagentHub）：伺服器上每個對話的項目 id 與 Ord 集合等於本機；
/// 4 台同時回補量伺服器的寫入負載。</item>
/// </list>
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class ConversationScenarioTests(MyriagentHubFixture fixture, ITestOutputHelper output) : IClassFixture<MyriagentHubFixture>
{
    /// <summary>以 <c>git archive 1724718</c> 建出的 MyriagentHub.exe 的完整路徑（兩包共用的約定；建法見 tools/fake-device/README.md）</summary>
    public const string OldHubVariable = "MYRIAGENT_SYNC_OLD_HUB";

    private const string OldHubName = "old";

    private static readonly DateTime BaseTime = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private static readonly FakeAccountSpec Account = LongRunDevice.Account("seed", AgentProvider.Claude, "seed@example.test", "org-seed");

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    // ───────────────────────────── 沒有 conv1 ─────────────────────────────

    // 步 16（重新為基底之後）刪掉了以同一棵樹的 MyriagentHub 跑的「基底伺服器_沒有conv1_…」：WP4.2 合併之後同一棵樹一律宣告 conv1。
    // 沒設 MYRIAGENT_SYNC_OLD_HUB 時的涵蓋在 FakeSyncServer（ConversationPublishEngineTests 的 舊伺服器_沒協商conv1_…）

    [EnvFileFact(OldHubVariable, "以 git archive 1724718 建出的 MyriagentHub.exe（建法見 tools/fake-device/README.md）")]
    public async Task 舊伺服器_沒有conv1_對話不外洩_目錄同步照常()
    {
        var keysBefore = UserKeyStore.FilesNaming(MyriagentHubExe.KeyNeedle(MyriagentHubExe.AlternateDirectoryName(OldHubName)));
        var hub = await MyriagentHubProcess.StartAsync(alternate: (OldHubName, Environment.GetEnvironmentVariable(OldHubVariable)!));
        try
        {
            output.WriteLine($"舊伺服器：{Environment.GetEnvironmentVariable(OldHubVariable)}（複製成 {hub.ExePath}）");
            using var owner = await OwnerClient.LoginAsync(hub);
            await NoConv1Async(hub, owner);
        }
        finally
        {
            await hub.DisposeAsync();
        }

        hub.ForcedKill.Should().BeFalse("舊版 MyriagentHub 也要能正常停止（強制結束會留下孤兒金鑰檔）");
        hub.KeyFilesOfThisExe().Except(keysBefore).Should().BeEmpty("正常停止的舊版 MyriagentHub 不留下金鑰檔（D5）");
    }

    /// <summary>
    /// 配對 → 灌 12 個舊對話 → kv conv_armed＝1（上一次進 Live 的連線有 conv1：伺服器之後回滾）→ 停伺服器、啟動桌面 → 離線時在舊對話與新對話各跑一輪
    /// （訊息、回覆、工具輸出都帶唯一的標記；舊對話的整份歷史等 created 確認才走，只寫 created）→ 伺服器回來：待送的對話列換成 {}、被 invalid 擋下、記 needs_redo，conv_armed 改成 0 →
    /// 進 Live 之後再跑一輪不寫新的對話事件，CLI 狀態照常上傳 → 停掉兩邊，唯讀開伺服器的 sync.db 檢查
    /// </summary>
    private async Task NoConv1Async(MyriagentHubProcess hub, OwnerClient owner)
    {
        const string name = "conv-old-hub";
        var (dir, paired) = await LongRunDevice.PairAsync(hub, owner, name);
        SeedResult seeded;
        try
        {
            seeded = ConversationSeeder.Write(dir.Root, new SeedSpec(12, 3, 10, 1724718, BaseTime, Account.Alias));
            using var store = SyncStore.Open(paired.Paths.DatabaseFile, paired.DeviceId);
            store.SetKv(SyncStoreSchema.ConvArmed, "1");
            await hub.StopAsync();
        }
        catch
        {
            dir.Dispose();
            throw;
        }

        var marker = "conv-leak-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var turn = ScriptedTurn.Parse(new JArray(
            new JObject { ["text"] = $"回覆 {marker}", ["segments"] = 4 },
            new JObject { ["tool"] = "執行指令", ["kind"] = "bash", ["title"] = "dotnet test", ["output"] = $"輸出 {marker}", ["segments"] = 2 }));
        int conversationRows;
        await using (var device = await LongRunDevice.StartPairedAsync(dir, paired, name, [Account], HubEngine.FastBackoff(0.2, 0.5, 1)))
        {
            var old = await device.Runtime.ChatAsync(new ChatSpec(seeded.Ids[0], null, $"舊對話裡的新訊息 {marker}", [], turn, Wait));
            var fresh = await device.Runtime.ChatAsync(new ChatSpec("離線時開的新對話", Account.Alias, $"新對話 {marker}", [], turn, Wait));
            old.Outcome.Should().Be(AgentTurnOutcome.Completed);
            fresh.Outcome.Should().Be(AgentTurnOutcome.Completed);
            fresh.Created.Should().BeTrue();
            // 舊對話的項目還沒有 Ord（配對後重設）、要整份走，created 確認之前不走（合併前審查 P4）：它只有 created 待送
            await device.WaitUntilAsync(v => v.Outbox.Any(e => e.ConversationId == old.ConversationId && e.State == OutboxState.Pending)
                                             && HasPendingMarker(v, fresh.ConversationId, marker), Wait,
                "離線、上一次有 conv1：兩個對話的事件照寫進 outbox（新對話含標記）");

            await hub.LaunchAsync();
            await device.WaitUntilAsync(v => v.State == SyncEngineState.Live, Wait, "伺服器回來後連上");
            var features = await device.Inspect(v => v.Features);
            features.Should().BeEmpty("沒有 conv1 的伺服器不宣告任何第 4 階段的功能");
            var (drained, _, _) = await device.Runtime.WaitDrainedAsync(Wait);
            drained.Should().BeTrue("待送的列全部送出（對話列被擋下、其他的確認）");
            (await device.Inspect(v => v.Store!.GetKv(SyncStoreSchema.ConvArmed))).Should().Be("0", "這一次進 Live 的連線沒有 conv1");

            var rows = await device.Inspect(v => v.Outbox.Where(e => e.ConversationId is not null).ToList());
            rows.Should().NotBeEmpty().And.OnlyContain(e => e.State == OutboxState.Rejected, "換成 {} 送出、伺服器以 invalid 擋下");
            rows.Select(e => e.ConversationId).Distinct().Should().BeEquivalentTo([old.ConversationId, fresh.ConversationId]);
            (await device.Inspect(v => v.Store!.LoadNeedsRedo())).Should().Contain([old.ConversationId, fresh.ConversationId], "被擋下的記成要重整，之後的 conv1 連線補送");
            conversationRows = rows.Count;

            // 進 Live 之後：沒有 conv1 不寫新的對話事件（發布者最多合併 1 秒）；目錄同步照常
            var afterLive = await device.Runtime.ChatAsync(new ChatSpec(old.ConversationId, null, $"Live 之後 {marker}", [], turn, Wait));
            afterLive.Outcome.Should().Be(AgentTurnOutcome.Completed);
            await device.Runtime.SetCliAsync(AgentProvider.Claude, "9.9.9-scenario");
            await Task.Delay(TimeSpan.FromSeconds(2));
            (drained, _, _) = await device.Runtime.WaitDrainedAsync(Wait);
            drained.Should().BeTrue();
            (await device.Inspect(v => v.Outbox.Count(e => e.ConversationId is not null))).Should().Be(conversationRows, "沒有 conv1 不寫新的對話事件");
            await device.StopAsync();
        }

        await hub.StopAsync();
        var all = ServerRows(hub.SyncDbPath);
        var mine = all.Where(r => r.Author == paired.DeviceId).ToList();
        mine.Where(r => r.Table == "events" && r.ConversationId is not null).Should().BeEmpty("伺服器沒有收下任何對話事件");
        var rejected = mine.Where(r => r.Table == "rejected_events" && r.ConversationId is not null).ToList();
        rejected.Should().HaveCount(conversationRows, "本機每一筆對話列都在伺服器擋下一次")
            .And.OnlyContain(r => r.Payload == SyncStore.EmptyConversationPayload && r.Enc == PayloadRules.EncJson && r.Reason == SyncErrorCodes.Invalid,
                "rejected_events 只有 {}（invalid）");
        mine.Where(r => r.Table == "events").Select(r => r.Kind).Should().Contain([SyncEventKinds.LinkUpsert, SyncEventKinds.CliState], "目錄同步照常");
        mine.Should().Contain(r => r.Table == "events" && r.Kind == SyncEventKinds.CliState && Decode(r).Contains("9.9.9-scenario"), "進 Live 之後的 CLI 狀態也上傳了");
        all.Where(r => Decode(r).Contains(marker, StringComparison.Ordinal)).Should().BeEmpty("任何事件（解開之後）都沒有對話的文字");
        var needle = Encoding.UTF8.GetBytes(marker);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(hub.SyncDbPath)!))
            File.ReadAllBytes(file).AsSpan().IndexOf(needle).Should().BeLessThan(0, $"{Path.GetFileName(file)} 的原始位元組沒有對話的文字");
        output.WriteLine($"1724718 版的 MyriagentHub：{seeded.Conversations} 個舊對話（SHA-256 {seeded.Sha256}），" +
                         $"對話列 {conversationRows} 筆全部換成 {{}} 被擋下；伺服器收下這台的事件 {mine.Count(r => r.Table == "events")} 筆");
        await hub.LaunchAsync();
    }

    private static bool HasPendingMarker(SyncEngine.EngineView v, string conversationId, string marker) =>
        v.Outbox.Any(e => e.ConversationId == conversationId && e.State == OutboxState.Pending && Decode(e.Enc, e.Payload).Contains(marker, StringComparison.Ordinal));

    // ───────────────────────────── conv1 的回補（重新為基底之後） ─────────────────────────────

    [EnvFact(LongRunGate.Gate, "conv1：30 個對話的回補（需要 WP4.2 進同一棵樹）")]
    public async Task conv1_30個對話回補後伺服器的對話與項目id與Ord集合等於本機()
    {
        var run = await BackfillAsync(devices: 1, conversations: 30, items: 20, seedBase: Conv1SeedBase);
        output.WriteLine(run.Describe());
    }

    [EnvFact(LongRunGate.Gate, "S19：300 個對話的回補（需要 WP4.2 進同一棵樹）")]
    public async Task S19_300個對話回補後伺服器的對話與項目id與Ord集合等於本機()
    {
        var run = await BackfillAsync(devices: 1, conversations: 300, items: 40, seedBase: S19SeedBase);
        output.WriteLine(run.Describe());
    }

    /// <summary>
    /// 4 台各 300 個對話同時回補：記 BatchAck 的 p95（桌面量的，送出到收到同一批的確認）、回補總時間、伺服器 sync.db（含 -wal）的成長量，結果記進附錄 B。
    /// 每台的種子不同（形狀相同）：同一份 id 會在伺服器上撞擁有者（not_owner），量到的就不是寫入負載
    /// </summary>
    [EnvFact(LongRunGate.Gate, "4 台假裝置各 300 個對話同時回補，量本機伺服器的寫入負載（需要 WP4.2 進同一棵樹）")]
    public async Task 四台假裝置同時回補_量本機伺服器的寫入負載()
    {
        var run = await BackfillAsync(devices: 4, conversations: 300, items: 40, seedBase: FourDeviceSeedBase);
        output.WriteLine(run.Describe());
        run.AckMs.Should().NotBeEmpty();
    }

    private sealed record BackfillRun(int Devices, int Conversations, int Items, int SeedBase, TimeSpan Elapsed, IReadOnlyList<double> AckMs, long DbGrowthBytes,
        IReadOnlyList<string> Sha256)
    {
        public double P95 => AckMs.Count == 0 ? 0 : AckMs.Order().ElementAt(Math.Min(AckMs.Count - 1, (int)Math.Ceiling(AckMs.Count * 0.95) - 1));

        public string Describe() =>
            $"{Devices} 台 × {Conversations} 個對話 × {Items} 則（種子 {SeedBase}～{SeedBase + Devices - 1}、baseTimeUtc 2026-10-20T00:00:00Z；SHA-256 {string.Join("、", Sha256)}）：" +
            $"回補總時間 {Elapsed.TotalSeconds:0.0} 秒、BatchAck {AckMs.Count} 批 p95 {P95:0} ms（最大 {(AckMs.Count == 0 ? 0 : AckMs.Max()):0} ms）、" +
            $"伺服器 sync.db（含 -wal）成長 {DbGrowthBytes / 1048576.0:0.0} MB";
    }

    // 各情境的種子（每台裝置是種子＋第幾台）。這個類別的情境共用同一個 MyriagentHub（類別的 fixture），所以範圍不可重疊：
    // 同一個種子產生的第一個對話 id 相同（id 在項目之前產生），後跑的情境的新裝置會在伺服器上撞到前一個情境的擁有者（not_owner，
    // 那個對話封鎖、伺服器上沒有它的內容）；計畫附錄 B 步 16
    private const int Conv1SeedBase = 20261020;

    private const int S19SeedBase = 20261030;

    private const int FourDeviceSeedBase = 20261040;

    /// <summary>
    /// 配對、灌舊對話（每台的種子不同）、以量時間的傳輸層啟動、確認協商出 conv1（沒有就失敗並提示）、等每台的回補做完，
    /// 再逐一比對伺服器上每個對話的項目 id 與 Ord 集合等於本機（以 sync.db 的發布紀錄準備的 Ord）
    /// </summary>
    private async Task<BackfillRun> BackfillAsync(int devices, int conversations, int items, int seedBase)
    {
        await EnsureHubAsync();
        var hub = fixture.Hub;
        var prepared = new List<(DeviceDir Dir, PairedDevice Paired, string Name, SeedResult Seeded)>();
        var started = new List<LongRunDevice>();
        var timings = new List<TimingTransport>();
        try
        {
            for (var i = 0; i < devices; i++)
            {
                var name = $"backfill-{i + 1}";
                var (dir, paired) = await LongRunDevice.PairAsync(hub, fixture.Owner, name);
                try
                {
                    prepared.Add((dir, paired, name, ConversationSeeder.Write(dir.Root, new SeedSpec(conversations, 3, items, seedBase + i, BaseTime, Account.Alias))));
                }
                catch
                {
                    dir.Dispose();
                    throw;
                }
            }

            var dbBefore = DbBytes(hub);
            var watch = Stopwatch.StartNew();
            foreach (var (dir, paired, name, _) in prepared.ToList())
            {
                var timing = new TimingTransport(new SignalRSyncTransport());
                timings.Add(timing);
                prepared.RemoveAt(0);
                started.Add(await LongRunDevice.StartPairedAsync(dir, paired, name, [Account], engineFactory: args => new SyncEngine(new SyncEngineArgs(
                    args.Paths, args.Device, paired.SignerFactory, SyncEngineDefaults.OpenStore, timing, args.Host, args.Applier, TimeProvider.System,
                    args.Options ?? SyncEngineOptions.Default, args.Policy, args.ServerOverride, args.DevTrustEnv, args.AppVersion, SyncEngineDefaults.BootId,
                    args.MachineName, args.WindowsUser, new ConversationChangeBox()))));
            }

            foreach (var device in started)
            {
                await device.WaitUntilAsync(v => v.State == SyncEngineState.Live, Wait, "連上");
                if (!(await device.Inspect(v => v.Features)).Contains(DeviceFeatureRules.Conv1))
                    throw new XunitException("需要 WP4.2 進同一棵樹：同一棵樹的 MyriagentHub 沒有宣告 conv1（重新為基底之後、計畫步 16 再跑）");
            }

            foreach (var device in started)
            {
                var (done, seconds) = await device.Runtime.WaitBackfillDoneAsync(TimeSpan.FromMinutes(30));
                done.Should().BeTrue($"{device.Name} 的回補在時限內做完（{seconds} 秒）");
            }

            watch.Stop();
            var growth = DbBytes(hub) - dbBefore;
            var hashes = new List<string>();
            for (var i = 0; i < started.Count; i++)
            {
                var device = started[i];
                var seeded = ConversationSeeder.Describe(ConversationSeeder.Generate(new SeedSpec(conversations, 3, items, seedBase + i, BaseTime, Account.Alias)));
                hashes.Add(seeded.Sha256);
                var server = ServerItems(hub.SyncDbPath, device.DeviceId);
                var mismatched = new List<string>();
                foreach (var id in seeded.Ids)
                {
                    var local = await LocalItemsAsync(device, id);
                    if (!server.TryGetValue(id, out var remote) || !SameSet(local, remote.Items) || !remote.Created)
                        mismatched.Add(id);
                }

                mismatched.Should().BeEmpty($"{device.Name}：每個對話伺服器的項目 id 與 Ord 集合等於本機、conv.created 有收下");
                (await device.Inspect(v => v.Store!.LoadBackfilled())).Should().HaveCount(conversations, "全部記回補完成");
            }

            return new BackfillRun(devices, conversations, items, seedBase, watch.Elapsed, timings.SelectMany(t => t.AckMs).ToList(), growth, hashes);
        }
        finally
        {
            foreach (var device in started)
                await device.DisposeAsync();
            foreach (var (dir, _, _, _) in prepared)
                dir.Dispose();
        }
    }

    /// <summary>本機的項目 id → Ord：載入對話、以 sync.db 的發布紀錄（hint）準備 Ord（同引擎要快照時）</summary>
    private static async Task<Dictionary<string, long>> LocalItemsAsync(LongRunDevice device, string conversationId)
    {
        var hint = await device.Inspect(v => v.Store!.OrdHintOf(conversationId));
        return await device.Runtime.OnUi(() =>
        {
            device.Runtime.Hub.GetItems(conversationId);
            device.Runtime.Hub.PrepareOrdsForPublish(conversationId, hint).Should().BeTrue("Ord 準備得了");
            return device.Runtime.Hub.GetItems(conversationId).ToDictionary(i => i.Id, i => i.Ord!.Value, StringComparer.Ordinal);
        });
    }

    private static bool SameSet(IReadOnlyDictionary<string, long> a, IReadOnlyDictionary<string, long> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var ord) && ord == p.Value);

    // ───────────────────────────── 伺服器的資料庫（停止後或執行中唯讀） ─────────────────────────────

    /// <summary>events 或 rejected_events 的一列（rejected 的 reason；events 是 null）</summary>
    private sealed record ServerRow(string Table, string Author, string Kind, string? ConversationId, string? Enc, string? Payload, string? Reason);

    /// <summary>兩張表的每一列（唯讀；連線不用池）</summary>
    private static List<ServerRow> ServerRows(string syncDb)
    {
        using var connection = HubDb.OpenReadOnly(syncDb);
        var rows = new List<ServerRow>();
        foreach (var (table, reason) in new[] { ("events", "NULL"), ("rejected_events", "reason") })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT author, kind, conversation_id, enc, payload, {reason} FROM {table}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ServerRow(table, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        return rows;
    }

    /// <summary>
    /// 伺服器上這台裝置的每個對話：依 seq 套用接受的 conv.item、conv.itemRemoved，conv.deleted 清空（同 FakeSyncServer.Items）；有沒有收下 conv.created
    /// </summary>
    private static Dictionary<string, (Dictionary<string, long> Items, bool Created)> ServerItems(string syncDb, string deviceId)
    {
        using var connection = HubDb.OpenReadOnly(syncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT kind, conversation_id, enc, payload FROM events WHERE author = $a AND conversation_id IS NOT NULL ORDER BY seq";
        cmd.Parameters.AddWithValue("$a", deviceId);
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, (Dictionary<string, long> Items, bool Created)>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var id = reader.GetString(1);
            if (!result.TryGetValue(id, out var state))
                result[id] = state = (new Dictionary<string, long>(StringComparer.Ordinal), false);
            var json = Decode(reader.GetString(2), reader.GetString(3));
            switch (reader.GetString(0))
            {
                case ConversationEventKinds.Created:
                    result[id] = (state.Items, true);
                    break;
                case ConversationEventKinds.Item:
                {
                    var o = JObject.Parse(json);
                    state.Items[o["id"]!.Value<string>()!] = o["ord"]!.Value<long>();
                    break;
                }
                case ConversationEventKinds.ItemRemoved:
                    state.Items.Remove(JObject.Parse(json)["id"]!.Value<string>()!);
                    break;
                case ConversationEventKinds.Deleted:
                    state.Items.Clear();
                    break;
            }
        }

        return result;
    }

    /// <summary>伺服器 sync.db 與 -wal 的大小（執行中）</summary>
    private static long DbBytes(MyriagentHubProcess hub) =>
        new[] { hub.SyncDbPath, hub.SyncDbPath + "-wal" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    private static string Decode(ServerRow row) => row.Payload is null ? string.Empty : Decode(row.Enc, row.Payload);

    private static string Decode(string? enc, string payload) =>
        PayloadRules.TryDecode(enc, payload, 16 << 20, out var json) ? json : throw new InvalidOperationException($"解不開的 payload（enc {enc}）");

    /// <summary>前一個測試失敗時伺服器可能停在停止狀態：先確認在執行</summary>
    private async Task EnsureHubAsync()
    {
        if (!fixture.Hub.IsRunning)
            await fixture.Hub.LaunchAsync();
    }
}
