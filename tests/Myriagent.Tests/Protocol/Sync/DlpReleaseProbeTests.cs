using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Xunit.Abstractions;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// WP4.9 步 3 組 e（S6 診斷；只在 <c>MYRIAGENT_PERF=1</c> 時跑）：量「釋放已編譯的 DLP 樣式能收回多少」。步 3 時 Protocol 還沒有釋放的介面，
/// 以反射另建一份樣式量上限；步 4 之後改用正式的 <see cref="CredentialLeakRules.ReleaseCompiledPatterns"/>（同一個語料、同一種回收）。
/// <list type="number">
/// <item>語料：measure_sync 的已配對資料根那一份（FD 的 <see cref="ConversationSeeder"/>：300 個對話、每個 40 則、種子 1、基準時間 2026-10-20），
/// 啟動重整與回補會遮蔽的字串——meta 的標題、模型、強度、工作資料夾，項目的 id、文字、工具名稱與種類、標題、細節、輸出、檔案路徑。</item>
/// <item>先釋放（程序裡別的測試可能已經建過），再照啟動重整的順序（先 meta、再項目）遮蔽兩輪，每一段之後量受管理堆積（<c>GC.GetTotalMemory(true)</c>）、
/// GC 已提交（<c>GC.GetGCMemoryInfo()</c>）與私有記憶體。</item>
/// <item>釋放，照同樣的回收量一次、再積極回收（<c>GCCollectionMode.Aggressive</c>，D5）量一次；最後再遮蔽一輪，量重建的耗時與成本。</item>
/// </list>
/// 收回的量就是可回收的部分；第一次的成本減掉它，是釋放也放不掉的共用部分（計畫第 8 節風險 2）。
/// 斷言只有「釋放之後舊的實例真的被回收」（弱參考）；數字印出來、記進 WP4.9 計畫的附錄 B
/// </summary>
[Collection(DlpPatternCollection.Name)]
public sealed class DlpReleaseProbeTests(ITestOutputHelper output)
{
    private const string Gate = "MYRIAGENT_PERF";

    private const int Conversations = 300;

    private const int ItemsPerConversation = 40;

    private static readonly DateTime BaseTimeUtc = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private static readonly FieldInfo CompiledField =
        typeof(CredentialLeakRules).GetField("compiled", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("找不到 CredentialLeakRules.compiled");

    /// <summary>一次取樣：受管理堆積、GC 已提交、程序的私有記憶體（位元組）</summary>
    private readonly record struct Sample(long Managed, long Committed, long Private);

    [EnvFact(Gate, "DLP 樣式的可回收量（WP4.9 步 3 組 e），約 1 分鐘")]
    public void 遮蔽同一份語料之後釋放_舊的實例被回收_印出可回收量()
    {
        var (metaTexts, itemTexts) = Corpus();
        output.WriteLine($"語料：{Conversations} 個對話的 meta 字串 {metaTexts.Count:N0} 個、{Conversations * ItemsPerConversation:N0} 則項目的字串 {itemTexts.Count:N0} 個；" +
                         $"GC：{(GCSettings.IsServerGC ? "Server" : "Workstation")}、延遲模式 {GCSettings.LatencyMode}、{DescribeGcConfig()}");

        // 暖身：JIT 與程序裡共用的靜態部分（Regex 的剖析器等）先建好，再釋放，量的才是一份樣式自己的成本
        CredentialLeakRules.Mask(metaTexts[0]);
        CredentialLeakRules.ReleaseCompiledPatterns();
        var start = Settle();

        var watch = Stopwatch.StartNew();
        MaskAll(metaTexts);
        var metaMs = watch.ElapsedMilliseconds;
        var afterMeta = Settle();
        watch.Restart();
        MaskAll(itemTexts);
        var itemsMs = watch.ElapsedMilliseconds;
        var afterItems = Settle();
        MaskAll(metaTexts);
        MaskAll(itemTexts);
        var second = Settle();

        var weak = WeakCompiled();
        CredentialLeakRules.ReleaseCompiledPatterns();
        var released = Settle();
        var releasedAggressive = Settle(aggressive: true);
        var collected = !weak.IsAlive;

        watch.Restart();
        MaskAll(metaTexts);
        var rebuildMetaMs = watch.ElapsedMilliseconds;
        var rebuilt = Settle();

        Row("開始（語料已產生、樣式已釋放）", start, start);
        Row($"meta 之後（含建置；{metaMs:N0} ms）", afterMeta, start);
        Row($"項目之後（{itemsMs:N0} ms）", afterItems, start);
        Row("第二輪之後", second, start);
        Row("釋放、同樣的回收之後", released, start);
        Row("再積極回收之後（Aggressive）", releasedAggressive, start);
        Row($"重建、再遮蔽 meta 之後（{rebuildMetaMs:N0} ms）", rebuilt, start);

        var cost = second.Managed - start.Managed;
        var reclaimed = second.Managed - released.Managed;
        output.WriteLine($"受管理堆積：第二輪之後的成本 {Mb(cost)}、釋放收回 {Mb(reclaimed)}、放不掉的部分 {Mb(cost - reclaimed)}");
        output.WriteLine($"GC 已提交：第二輪之後 {Mb(second.Committed)} → 釋放之後 {Mb(released.Committed)} → 積極回收之後 {Mb(releasedAggressive.Committed)}；" +
                         $"私有記憶體 {Mb(second.Private)} → {Mb(released.Private)} → {Mb(releasedAggressive.Private)}");

        collected.Should().BeTrue("釋放之後沒有任何根留住舊的實例（NonBacktracking 的狀態快取在它底下）");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MaskAll(List<string> texts)
    {
        foreach (var text in texts)
            CredentialLeakRules.Mask(text);
    }

    /// <summary>現在的已編譯實例的弱參考（不內嵌：回傳之後這裡的區域變數不再留住它）</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WeakCompiled()
        => new(CompiledField.GetValue(null) ?? throw new InvalidOperationException("還沒編譯"));

    /// <summary>measure_sync 的已配對資料根那一份對話裡，啟動重整（meta）與回補（項目）會遮蔽的字串</summary>
    private static (List<string> Meta, List<string> Items) Corpus()
    {
        var seeds = ConversationSeeder.Generate(new SeedSpec(Conversations, OlderThanDays: 3, ItemsPerConversation, Seed: 1, BaseTimeUtc, Account: "measure-seed"));
        var meta = new List<string>();
        var items = new List<string>();
        foreach (var (conversation, conversationItems) in seeds)
        {
            Add(meta, conversation.Title, conversation.Model, conversation.Effort, conversation.WorkingDirectory);
            foreach (var item in conversationItems)
                Add(items, item.Id, item.Text, item.ToolName, item.ToolKind, item.Title, item.Detail, item.Output, item.FilePath);
        }

        return (meta, items);
    }

    private static void Add(List<string> texts, params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value))
                texts.Add(value);
        }
    }

    /// <summary>
    /// 完整回收之後取樣：平常是兩次強制、壓縮的完整回收（同 <c>OutboxSlimTests</c> 的 Settle），<paramref name="aggressive"/> 時改成積極回收
    /// （含大型物件堆積的壓縮，把空出來的記憶體交還作業系統；D5 在釋放之後做的那一次）
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sample Settle(bool aggressive = false)
    {
        for (var i = 0; i < 2; i++)
        {
            if (aggressive)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            else
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            GC.WaitForPendingFinalizers();
        }

        var managed = GC.GetTotalMemory(true);
        var committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
        using var process = Process.GetCurrentProcess();
        return new Sample(managed, committed, process.PrivateMemorySize64);
    }

    private void Row(string label, Sample sample, Sample reference)
        => output.WriteLine($"{label}：受管理 {Mb(sample.Managed)}（{Signed(sample.Managed - reference.Managed)}）、GC 已提交 {Mb(sample.Committed)}（{Signed(sample.Committed - reference.Committed)}）、" +
                            $"私有 {Mb(sample.Private)}（{Signed(sample.Private - reference.Private)}）");

    /// <summary>
    /// 這個程序的 GC 設定裡和 S6 診斷有關的幾項（<see cref="GC.GetConfigurationVariables"/>：GC 實際採用的值；
    /// <c>GCGen0MaxBudget</c> 是 gen0 預算的上限，沒設定時由 L3 快取的大小算出）
    /// </summary>
    private static string DescribeGcConfig()
    {
        var wanted = new[] { "ServerGC", "ConcurrentGC", "GCGen0MaxBudget", "GCConserveMem", "GCHeapHardLimit" };
        var config = GC.GetConfigurationVariables();
        return string.Join("、", wanted.Where(config.ContainsKey).Select(k => string.Create(CultureInfo.InvariantCulture, $"{k}={config[k]}")));
    }

    private static string Mb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.00} MB");

    private static string Signed(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{(bytes >= 0 ? "+" : "")}{bytes / 1048576.0:0.00} MB");
}
