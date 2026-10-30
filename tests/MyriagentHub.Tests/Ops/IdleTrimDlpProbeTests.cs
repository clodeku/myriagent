using System.Diagnostics;
using System.Globalization;
using Myriagent.Sync;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace MyriagentHub.Tests.Ops;

/// <summary>
/// 閒置修剪釋放 DLP 樣式之後，第一次掃描要重建的成本（S9 的閒置記憶體，設計 14.20；只在 <c>MYRIAGENT_HUB_PERF=1</c> 時跑，幾秒）：
/// 伺服器在裝置發布的每個項目跑第二道 DLP（<see cref="ConversationDlpRules.HasLeak(JToken)"/>），閒置之後的第一個項目要先建好樣式。
/// 先暖身（JIT 與 Regex 的共用部分），再釋放、量第一次與第二次掃描各 7 輪，最後量樣式的快取佔多少受管理堆積；數字印出來記進設計 14.20，
/// 斷言只有「釋放之後重建了」
/// </summary>
[Collection(PerfCollection.Name)]
public sealed class IdleTrimDlpProbeTests(ITestOutputHelper output)
{
    private const int Rounds = 7;

    [EnvFact("MYRIAGENT_HUB_PERF", "閒置修剪之後 DLP 樣式第一次重建的成本，幾秒")]
    public void 釋放之後第一次掃描_印出重建的耗時()
    {
        var item = Item();
        ConversationDlpRules.HasLeak(item).Should().BeFalse("語料裡沒有權杖");

        var first = new List<double>();
        var second = new List<double>();
        for (var i = 0; i < Rounds; i++)
        {
            CredentialLeakRules.ReleaseCompiledPatterns();
            CredentialLeakRules.IsCompiled.Should().BeFalse();
            var watch = Stopwatch.StartNew();
            ConversationDlpRules.HasLeak(item);
            first.Add(watch.Elapsed.TotalMilliseconds);
            CredentialLeakRules.IsCompiled.Should().BeTrue("第一次掃描時重建");
            watch.Restart();
            ConversationDlpRules.HasLeak(item);
            second.Add(watch.Elapsed.TotalMilliseconds);
        }

        // 樣式的快取佔多少：釋放後與重建後各做一次完整回收再量受管理堆積
        CredentialLeakRules.ReleaseCompiledPatterns();
        var released = GC.GetTotalMemory(forceFullCollection: true);
        ConversationDlpRules.HasLeak(item);
        var compiled = GC.GetTotalMemory(forceFullCollection: true);
        CredentialLeakRules.IsCompiled.Should().BeTrue();

        output.WriteLine($"項目 {item.ToString(Newtonsoft.Json.Formatting.None).Length:N0} 字；釋放之後第一次掃描（含重建）{Describe(first)}；第二次 {Describe(second)}；"
            + string.Create(CultureInfo.InvariantCulture, $"樣式的快取（重建後減釋放後的受管理堆積）{(compiled - released) / 1048576.0:0.0} MB"));
    }

    /// <summary>一個工具輸出的項目（文字、指令、約 4 KB 的輸出、檔案路徑；沒有權杖）</summary>
    private static JObject Item()
    {
        var lines = Enumerable.Range(1, 80).Select(i => string.Create(CultureInfo.InvariantCulture, $"  src/MyriagentHub/Ops/File{i:000}.cs | {i * 7} ++++----"));
        return new JObject
        {
            ["id"] = "it-01JZ8Q2K4M5N6P7R8S9T0V1W2X",
            ["kind"] = "tool",
            ["status"] = "Completed",
            ["toolName"] = "Bash",
            ["title"] = "git diff --stat origin/main...HEAD",
            ["text"] = "看一下這個分支改了哪些檔案，再決定要不要拆成兩個提交。",
            ["detail"] = "{\"command\":\"git diff --stat origin/main...HEAD\",\"description\":\"列出變更的檔案\"}",
            ["output"] = string.Join("\n", lines),
            ["filePath"] = @"C:\repo\myriagent\src\MyriagentHub\Ops\IdleTrim.cs",
        };
    }

    private static string Describe(List<double> ms)
    {
        var sorted = ms.Order().ToList();
        return string.Create(CultureInfo.InvariantCulture, $"中位數 {sorted[sorted.Count / 2]:0.00} ms、最小 {sorted[0]:0.00}、最大 {sorted[^1]:0.00}（{ms.Count} 輪）");
    }
}
