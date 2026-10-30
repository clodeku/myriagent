using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Newtonsoft.Json;
using Xunit.Abstractions;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 動到 <see cref="CredentialLeakRules"/> 全程序共用的已編譯樣式（釋放、預熱、量記憶體）的測試循序執行：xUnit 先跑完所有可平行的集合才跑這種集合，
/// 別的測試不會在中途重建或抓住樣式（「釋放之後還沒重建」「舊的實例被回收」才斷言得了）
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DlpPatternCollection
{
    public const string Name = "DLP 已編譯樣式的釋放（循序執行）";
}

/// <summary>
/// 可釋放的已編譯 DLP 樣式（WP4.9 步 4、計畫 D4）：釋放前後遮蔽的輸出逐位元組相同；釋放之後舊的實例（含 NonBacktracking 的快取）可以被回收；
/// 指紋、名稱與選項由定義算、讀它們不會重建；遮蔽與釋放並行時輸出照樣正確；釋放之後第一次遮蔽的耗時（<c>MYRIAGENT_PERF=1</c> 時 ≤200 ms）
/// </summary>
[Collection(DlpPatternCollection.Name)]
public sealed class CredentialLeakRulesReleaseTests(ITestOutputHelper output)
{
    private const string PerfGate = "MYRIAGENT_PERF";

    /// <summary>釋放之後第一次遮蔽的耗時上限（只在 <c>MYRIAGENT_PERF=1</c> 時斷言；計畫步 4、審查意見 25）</summary>
    private static readonly TimeSpan FirstMaskAfterReleaseBudget = TimeSpan.FromMilliseconds(200);

    private static readonly FieldInfo CompiledField =
        typeof(CredentialLeakRules).GetField("compiled", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("找不到 CredentialLeakRules.compiled");

    [Fact]
    public void 釋放前後遮蔽輸出逐位元組相同()
    {
        var texts = CorpusTexts();
        var items = CorpusItems(texts);
        texts.Count.Should().BeGreaterThan(200, "DLP 正反例語料（兩份測試的正例與反例）全部都跑");

        CredentialLeakRules.EnsureCompiled();
        var before = Outputs(texts, items);

        CredentialLeakRules.ReleaseCompiledPatterns();
        CredentialLeakRules.IsCompiled.Should().BeFalse("剛釋放、還沒有人遮蔽");
        var afterFirstRelease = Outputs(texts, items);
        CredentialLeakRules.IsCompiled.Should().BeTrue("遮蔽時重建");

        CredentialLeakRules.ReleaseCompiledPatterns();
        var afterSecondRelease = Outputs(texts, items);

        afterFirstRelease.Should().Equal(before, "釋放之後重建的樣式，Mask、MaskLive、MaskCut、MaskTruncated、MaskJsonStrings、MaskItem、HasLeak 的輸出逐位元組相同");
        afterSecondRelease.Should().Equal(before);
        before.Should().Contain(o => o.Contains(CredentialLeakRules.Replacement, StringComparison.Ordinal), "語料裡有正例（真的遮到東西）");
    }

    [Fact]
    public void 釋放後舊的已編譯實例可以被回收()
    {
        CredentialLeakRules.EnsureCompiled();
        var weak = WeakCompiled();
        Collect();
        weak.IsAlive.Should().BeTrue("還沒釋放：靜態欄位留著它（對照組；釋放什麼都沒做時下面就會紅）");

        CredentialLeakRules.ReleaseCompiledPatterns();
        Collect();

        weak.IsAlive.Should().BeFalse("釋放之後沒有任何根留住舊的實例，NonBacktracking 的快取跟著一起被回收");
        CredentialLeakRules.IsCompiled.Should().BeFalse();
    }

    [Fact]
    public void 指紋名稱與選項不依賴已編譯實例_和已編譯的Regex一致()
    {
        CredentialLeakRules.EnsureCompiled();
        var regexes = CompiledRegexes();
        var fingerprint = CredentialLeakRules.PatternFingerprint;
        var names = CredentialLeakRules.PatternNames;
        var options = CredentialLeakRules.PatternOptions;

        // 由定義算的指紋，每一行的正規式原文與選項＝已編譯實例的 Regex.ToString() 與 Regex.Options（以前的算法）
        var lines = fingerprint.Split('\n').Take(regexes.Count).Select(l => l.Split('\t')).ToList();
        lines.Select(l => l[0]).Should().Equal(names);
        lines.Select(l => l[1]).Should().Equal(regexes.Select(r => r.ToString()), "正規式原文和已編譯的相同");
        lines.Select(l => l[2]).Should().Equal(regexes.Select(r => ((int)r.Options).ToString(CultureInfo.InvariantCulture)), "選項和已編譯的相同");
        options.Should().Equal(regexes.Select(r => r.Options));
        regexes.Should().OnlyContain(r => r.Options.HasFlag(RegexOptions.NonBacktracking), "R6：預熱時一次編譯全部，不支援的語法在這裡就會丟例外");

        CredentialLeakRules.ReleaseCompiledPatterns();
        CredentialLeakRules.PatternFingerprint.Should().Be(fingerprint);
        CredentialLeakRules.PatternNames.Should().Equal(names);
        CredentialLeakRules.PatternOptions.Should().Equal(options);
        CredentialLeakRules.TokenFieldNames.Should().NotBeEmpty();
        CredentialLeakRules.IsCompiled.Should().BeFalse("讀指紋、名稱、選項不會重建已編譯的樣式");
        CredentialLeakRules.Version.Should().Be(2);
    }

    [Fact]
    public void 空字串與null不建已編譯的樣式_有內容時才建()
    {
        CredentialLeakRules.ReleaseCompiledPatterns();
        CredentialLeakRules.Mask(null).Should().BeNull();
        CredentialLeakRules.Mask("").Should().BeEmpty();
        CredentialLeakRules.HasLeak("").Should().BeFalse();
        CredentialLeakRules.MaskLive("").Should().BeEmpty();
        CredentialLeakRules.MaskTruncated("沒有截斷").Should().Be("沒有截斷");
        CredentialLeakRules.IsCompiled.Should().BeFalse("沒有內容可遮的呼叫不建樣式");

        CredentialLeakRules.Mask("一般的文字");
        CredentialLeakRules.IsCompiled.Should().BeTrue();
    }

    [Fact]
    public void 熱路徑不寫已編譯樣式以外的共用狀態()
    {
        // 伺服器對每個字串都呼叫一次 Mask／HasLeak（ConversationDlpRules、CommandMaskRules）：可寫的靜態欄位只能是已編譯樣式那一個，
        // 量測用的時間戳記之類不進部署的熱路徑（WP4.9 合併前審查 DLP-1：以前的 lastUsedTimestamp 每次呼叫都 QPC＋寫共用的 long）
        var writable = typeof(CredentialLeakRules)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => !f.IsInitOnly && !f.IsLiteral)
            .Select(f => f.Name);
        writable.Should().Equal(["compiled"], "只有延遲建立、可釋放的已編譯樣式是可寫的");
    }

    [Fact]
    public void 並行遮蔽與釋放_輸出全部等於基準_沒有例外()
    {
        // 計畫寫每條 2,000 次：每一次釋放都讓每條執行緒下一次呼叫重建（約 20～40 ms），2,000 次在忙的機器上要約 16 秒；
        // 500 次仍有上百次釋放和遮蔽交錯（WP4.9 計畫附錄 B 步 4 的偏離）
        const int threads = 8;
        const int iterations = 500;
        var texts = CorpusTexts();
        CredentialLeakRules.EnsureCompiled();
        var expected = texts.Select(t => CredentialLeakRules.Mask(t)).ToArray();
        var expectedLeak = texts.Select(CredentialLeakRules.HasLeak).ToArray();

        using var done = new CancellationTokenSource();
        var releases = 0;
        var releaser = new Thread(() =>
        {
            while (!done.IsCancellationRequested)
            {
                CredentialLeakRules.ReleaseCompiledPatterns();
                Interlocked.Increment(ref releases);
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "釋放" };

        var mismatches = 0;
        var errors = new List<Exception>();
        var watch = Stopwatch.StartNew();
        releaser.Start();
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            try
            {
                for (var i = 0; i < iterations; i++)
                {
                    var k = (t * 7919 + i) % texts.Count;
                    if (!string.Equals(CredentialLeakRules.Mask(texts[k]), expected[k], StringComparison.Ordinal)
                        || CredentialLeakRules.HasLeak(texts[k]) != expectedLeak[k])
                        Interlocked.Increment(ref mismatches);
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                    errors.Add(ex);
            }
        }) { IsBackground = true, Name = $"遮蔽 {t}" }).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        done.Cancel();
        releaser.Join();
        watch.Stop();

        output.WriteLine($"{threads} 條執行緒各 {iterations:N0} 次 Mask＋HasLeak，期間釋放 {releases:N0} 次，{watch.ElapsedMilliseconds:N0} ms");
        errors.Should().BeEmpty();
        mismatches.Should().Be(0, "每一次呼叫都照它取到的那一份樣式做完，釋放不影響輸出");
        releases.Should().BeGreaterThan(10, "釋放真的和遮蔽交錯（不是做完才釋放）");
    }

    [Fact]
    public void 釋放後第一次遮蔽耗時()
    {
        var sample = string.Join("\n", CorpusTexts().Where(t => t.Length < 400).Take(40));
        CredentialLeakRules.EnsureCompiled();
        CredentialLeakRules.Mask(sample);

        CredentialLeakRules.ReleaseCompiledPatterns();
        var watch = Stopwatch.StartNew();
        CredentialLeakRules.Mask(sample);
        watch.Stop();
        var warm = Stopwatch.StartNew();
        CredentialLeakRules.Mask(sample);
        warm.Stop();

        output.WriteLine($"釋放後第一次遮蔽 {watch.Elapsed.TotalMilliseconds:0.0} ms（{sample.Length:N0} 字；之後第二次 {warm.Elapsed.TotalMilliseconds:0.0} ms）");
        if (Environment.GetEnvironmentVariable(PerfGate) == "1")
            watch.Elapsed.Should().BeLessThanOrEqualTo(FirstMaskAfterReleaseBudget, "釋放後第一次遮蔽要重建樣式（計畫步 4 的門檻）");
    }

    /// <summary>兩份 DLP 測試的正例與反例、加上截斷與差異的變形</summary>
    private static List<string> CorpusTexts()
    {
        var texts = new List<string>();
        texts.AddRange(CredentialLeakRulesTests.Positives().Select(p => p.Text));
        texts.AddRange(CredentialLeakRulesTests.Negatives());
        texts.AddRange(CredentialLeakRulesF10Tests.Positives().Select(p => p.Text));
        texts.AddRange(CredentialLeakRulesF10Tests.Negatives().Select(n => n.Text));
        var count = texts.Count;
        for (var i = 0; i < count; i++)
        {
            var t = texts[i];
            if (t.Length > 12)
            {
                texts.Add(AgentHubRules.OmittedPrefix(1234) + t[(t.Length / 3)..]);   // 開頭被截掉
                texts.Add(t[..(t.Length * 2 / 3)] + "…");                               // 結尾被截斷（MaskCut）
            }
        }

        return texts;
    }

    /// <summary>每段文字放進項目的不同欄位（完成的、執行中的、差異逐行）</summary>
    private static List<AgentChatItem> CorpusItems(List<string> texts)
        => texts.Select((t, i) => new AgentChatItem
        {
            Id = "i" + i.ToString(CultureInfo.InvariantCulture),
            Kind = i % 3 == 0 ? AgentItemKind.Tool : AgentItemKind.Assistant,
            Status = i % 2 == 0 ? AgentItemStatus.Running : AgentItemStatus.Done,
            Text = t,
            Output = i % 3 == 0 ? t : null,
            Title = t.Length > 80 ? t[..80] : t,
            Diff = i % 5 == 0 ? t.Split('\n').Select(line => new AgentDiffLine("add", line)).ToList() : null,
        }).ToList();

    /// <summary>每個公開的遮蔽函式對整份語料的輸出（項目序列化成 JSON 比對）</summary>
    private static List<string> Outputs(List<string> texts, List<AgentChatItem> items)
    {
        var outputs = new List<string>();
        foreach (var t in texts)
        {
            outputs.Add(CredentialLeakRules.Mask(t));
            outputs.Add(CredentialLeakRules.MaskLive(t));
            outputs.Add(CredentialLeakRules.MaskCut(t));
            outputs.Add(CredentialLeakRules.MaskTruncated(t));
            outputs.Add(CredentialLeakRules.MaskJsonStrings(t));
            outputs.Add(CredentialLeakRules.HasLeak(t) ? "leak" : "clean");
        }

        outputs.AddRange(items.Select(item => JsonConvert.SerializeObject(CredentialLeakRules.MaskItem(item))));
        return outputs;
    }

    /// <summary>現在的已編譯實例的弱參考（不內嵌：回傳之後這裡的區域變數不再留住它）</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WeakCompiled()
        => new(CompiledField.GetValue(null) ?? throw new InvalidOperationException("還沒編譯"));

    /// <summary>現在的已編譯實例裡、與定義同順序的 Regex</summary>
    private static IReadOnlyList<Regex> CompiledRegexes()
    {
        var compiled = CompiledField.GetValue(null) ?? throw new InvalidOperationException("還沒編譯");
        var property = compiled.GetType().GetProperty("Regexes") ?? throw new InvalidOperationException("找不到 Regexes");
        return (Regex[])property.GetValue(compiled)!;
    }

    private static void Collect()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
