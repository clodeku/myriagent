using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Tests.Sync.Wiring;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// S19 桌面端的長情境（WP4.3 計畫步 12；<c>MYRIAGENT_SYNC_IT=1</c>，同一個程序裡跑、不連外）：300 個舊格式的對話（項目沒載入、沒有 Ord，
/// 三分之二超過 90 天，每 50 個有一個上千則）以真的中樞＋發布者＋引擎＋假伺服器回補，送完後每個對話伺服器的 id 與 Ord 集合等於本機
/// （中樞之後載入、以發布紀錄準備的 Ord），回補前後程序的私有記憶體增量不超過 30 MB（沒載入的對話一次只讀一個、讀完就丟）。
/// 量法：
/// <list type="bullet">
/// <item>先以另外 300 個同樣產生的對話完整跑一次暖機：JIT、SQLite，以及權杖 DLP 樣式（NonBacktracking）的狀態快取——第一次大量遮蔽時
/// 長到約 38 MB、同樣的內容第二次不再長——是程序裡一次性的成本，不算在回補的增量裡（實測記在計畫附錄 B）。</item>
/// <item>量「之後」之前，假伺服器為了測試檢查保留的每一筆事件（約 24 MB）先丟掉（<see cref="Fakes.FakeSyncServer.DropHistoryForMeasurement"/>）；
/// 伺服器端每個對話的 id 與 Ord 集合先算成摘要留著比對。</item>
/// </list>
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ConversationBackfillLongRunTests(ITestOutputHelper output)
{
    private const long BudgetBytes = 30L * 1024 * 1024;

    [EnvFact(LongRun.Gate, "300 個對話的回補，先暖機一次（約 1 分鐘）")]
    public async Task S19桌面端_300個對話_送完後id與Ord集合等於本機_私有記憶體增量不超過30MB()
    {
        var random = new Random(20261020);
        await WarmUpAsync(Generate(300, random));

        var seeds = Generate(300, random);
        await using var rig = new ReconcileChainRig(seeds);
        var totalItems = seeds.Sum(s => s.Items.Count);
        var before = PrivateBytes();
        var managedBefore = GC.GetTotalMemory(true);

        var watch = Stopwatch.StartNew();
        await rig.StartAsync();
        await rig.DrainAsync(3_600);
        watch.Stop();
        var serverDigests = seeds.ToDictionary(s => s.Meta.Id, s => Digest(rig.Model.Items(s.Meta.Id)), StringComparer.Ordinal);
        rig.E.Server.DropHistoryForMeasurement();
        var after = PrivateBytes();
        var growth = after - before;
        var managedGrowth = GC.GetTotalMemory(true) - managedBefore;
        output.WriteLine($"300 個對話、{totalItems} 則：回補 {watch.Elapsed.TotalSeconds:0.0} 秒（實際時間），私有記憶體 {before / 1048576.0:0.00} → {after / 1048576.0:0.00} MB" +
                         $"（增量 {growth / 1048576.0:0.00} MB；受管理堆積增量 {managedGrowth / 1048576.0:0.00} MB）");

        var mismatched = 0;
        foreach (var (meta, items) in seeds)
        {
            var local = await rig.LoadAndPrepareAsync(meta.Id);
            if (!local.Keys.ToHashSet().SetEquals(items.Select(i => i.Id)) || Digest(local) != serverDigests[meta.Id])
                mismatched++;
        }

        output.WriteLine($"id 與 Ord 集合不符的對話：{mismatched} 個");
        mismatched.Should().Be(0, "每個對話伺服器的 id 與 Ord 集合等於本機");
        (await rig.E.Inspect(v => v.Store!.LoadBackfilled())).Should().HaveCount(seeds.Count, "全部記回補完成");
        growth.Should().BeLessThanOrEqualTo(BudgetBytes, "回補前後的私有記憶體增量不超過 30 MB");
    }

    /// <summary>暖機：同樣產生的另一批對話完整回補一次（一次性的成本留在程序裡），記下這一次的增量給附錄 B</summary>
    private async Task WarmUpAsync(List<(AgentConversation Meta, List<AgentChatItem> Items)> seeds)
    {
        var before = PrivateBytes();
        await using (var warm = new ReconcileChainRig(seeds))
        {
            await warm.StartAsync();
            await warm.DrainAsync(3_600);
            warm.E.Server.DropHistoryForMeasurement();
            output.WriteLine($"暖機（300 個對話、{seeds.Sum(s => s.Items.Count)} 則）：私有記憶體增量 {(PrivateBytes() - before) / 1048576.0:0.00} MB（含 JIT、SQLite 與 DLP 樣式的快取）");
        }
    }

    /// <summary>舊格式的對話：三分之二超過 90 天，每 50 個有一個上千則</summary>
    private static List<(AgentConversation Meta, List<AgentChatItem> Items)> Generate(int count, Random random)
    {
        var seeds = new List<(AgentConversation Meta, List<AgentChatItem> Items)>(count);
        for (var i = 0; i < count; i++)
        {
            var updated = i < count * 2 / 3 ? ReconcileChainRig.T0.AddDays(-91 - i) : ReconcileChainRig.T0.AddHours(-i);
            var items = i % 50 == 7 ? 1_000 + random.Next(500) : 5 + random.Next(60);
            seeds.Add(ReconcileChainRig.Seed(updated, items, $"對話 {i}"));
        }

        return seeds;
    }

    /// <summary>id → Ord 集合的摘要（依 id 排序）</summary>
    private static string Digest(IReadOnlyDictionary<string, long> ords)
    {
        var text = new StringBuilder();
        foreach (var (id, ord) in ords.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(id).Append(':').Append(ord).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>完整回收（含大型物件堆積的壓縮；Aggressive 把空出來的記憶體交還作業系統，私有記憶體才不含 GC 保留的空間）之後的私有記憶體</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long PrivateBytes()
    {
        for (var i = 0; i < 3; i++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }
}
