using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// WP4.9 步 6 第 2 點（只在 <c>MYRIAGENT_PERF=1</c> 時跑；合併前審查 F2：原本是 scratchpad 的丟棄式主控台程式，附錄 B 引用的數字改由這裡重跑）：
/// 桌面閒置時釋放 DLP 樣式之後，第一次遮蔽要重建樣式；在 UI 執行緒上遮蔽的只有遠端 <c>answer</c> 的 <see cref="RemoteCardRules"/>
/// （<c>Check</c>、<c>TryMapAnswers</c>：每個題目 id 一次 <see cref="CredentialLeakRules.Mask(string?)"/>）。超過 50 ms 就要在遠端指令到達引擎時先預熱（計畫步 6 第 2 點）。
/// <list type="number">
/// <item>STA 執行緒裝 <see cref="WindowsFormsSynchronizationContext"/>（同 app 的 UI 執行緒），先照啟動重整遮蔽 measure_sync 那一份 300 個對話的 meta 暖身。</item>
/// <item>每一回都照引擎的修剪：<see cref="CredentialLeakRules.ReleaseCompiledPatterns"/>＋一次積極回收（阻塞、壓縮），停 50 ms 之後才開碼表，
/// 碼表只包住那一次同步的呼叫（不含任何等待或訊息迴圈，量不到計時器刻度）。</item>
/// <item>UI 執行緒上：一個題目、四個題目的 <c>TryMapAnswers</c>、3,000 字的 <c>Mask</c>（各 <see cref="Rounds"/> 回）；照實際的順序（引擎執行緒先遮蔽指令名稱
/// <c>answer</c>、UI 執行緒才對答案）時 UI 那一段；已編譯時四個題目的對照。斷言 UI 執行緒上三種第一次遮蔽的中位數都不到 50 ms。</item>
/// <item>背景（執行緒集區）的檔案 DLP（<see cref="MediaServerRules.HasLeak"/>，fileFetch 在 <c>Task.Run</c> 裡呼叫）：4 KB、100 KB、1 MB、5 MB 釋放後第一次與已編譯時的耗時，只印出來。</item>
/// </list>
/// 執行：<c>MYRIAGENT_PERF=1 dotnet test tests/Myriagent.Tests/Myriagent.Tests.csproj -c Release --filter "FullyQualifiedName~RemoteCardFirstMaskProbe"</c>，數字記進 WP4.9 計畫的附錄 B
/// </summary>
[Collection(DlpPatternCollection.Name)]
public sealed class RemoteCardFirstMaskProbeTests(ITestOutputHelper output)
{
    private const string Gate = "MYRIAGENT_PERF";

    private const int Rounds = 40;

    private const int BackgroundRounds = 10;

    /// <summary>UI 執行緒上釋放之後第一次遮蔽的中位數上限（計畫步 6 第 2 點）</summary>
    private static readonly TimeSpan UiBudget = TimeSpan.FromMilliseconds(50);

    private static readonly DateTime BaseTimeUtc = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    [EnvFact(Gate, "釋放 DLP 樣式之後 UI 執行緒上第一次遮蔽的耗時（WP4.9 步 6 第 2 點），約 1 分鐘")]
    public void 釋放之後UI執行緒上第一次遮蔽_中位數不到50ms_背景的檔案DLP印出來()
    {
        var one = Card(1);
        var four = Card(4);
        var oneAnswers = Answers(one);
        var fourAnswers = Answers(four);
        var text = LongText(3000);
        var warmup = SeededMeta();

        var ui = OnUiThread(() =>
        {
            foreach (var meta in warmup)
                CredentialLeakRules.Mask(meta);
            RemoteCardRules.TryMapAnswers(four, fourAnswers, out _).Should().BeTrue("前提：答案對得回題目");

            var r = new UiTimings(new List<double>(), new List<double>(), new List<double>(), new List<double>(), new List<double>());
            for (var i = 0; i < Rounds; i++)
            {
                r.One.Add(AfterRelease(() => RemoteCardRules.TryMapAnswers(one, oneAnswers, out _)));
                r.Four.Add(AfterRelease(() => RemoteCardRules.TryMapAnswers(four, fourAnswers, out _)));
                r.Text.Add(AfterRelease(() => CredentialLeakRules.Mask(text)));
                // 實際的順序：每個遠端指令先經引擎執行緒的 AuditFacts 遮蔽指令名稱（重建發生在那裡），UI 執行緒才對答案
                r.EngineFirst.Add(AfterRelease(() => RemoteCardRules.TryMapAnswers(four, fourAnswers, out _),
                    beforeTimer: () => Task.Run(() => CredentialLeakRules.Mask("answer")).GetAwaiter().GetResult()));
                r.Compiled.Add(Time(() => RemoteCardRules.TryMapAnswers(four, fourAnswers, out _)));
            }

            return r;
        });

        output.WriteLine($"GC：{(System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation")}；暖身 {warmup.Count:N0} 個 meta 字串；每一回釋放＋積極回收、停 50 ms 再量（{Rounds} 回）");
        Report("UI：釋放後第一次，一個題目的 TryMapAnswers", ui.One);
        Report("UI：釋放後第一次，四個題目", ui.Four);
        Report($"UI：釋放後第一次，{text.Length:N0} 字的 Mask", ui.Text);
        Report("UI：引擎執行緒先遮蔽 answer 之後，四個題目", ui.EngineFirst);
        Report("UI：已編譯時，四個題目", ui.Compiled);

        foreach (var size in new[] { 4 * 1024, 100 * 1024, 1024 * 1024, 5 * 1024 * 1024 })
        {
            var content = FileContent(size);
            MediaServerRules.HasLeak(content).Should().BeFalse("前提：內容沒有權杖（整份掃完，最壞的情況）");
            var first = new List<double>();
            var compiled = new List<double>();
            for (var i = 0; i < BackgroundRounds; i++)
            {
                first.Add(Task.Run(() => AfterRelease(() => MediaServerRules.HasLeak(content))).GetAwaiter().GetResult());
                compiled.Add(Task.Run(() => Time(() => MediaServerRules.HasLeak(content))).GetAwaiter().GetResult());
            }

            Report($"背景：檔案 DLP {size / 1024:N0} KB 釋放後第一次", first);
            Report($"背景：檔案 DLP {size / 1024:N0} KB 已編譯", compiled);
        }

        Median(ui.One).Should().BeLessThan(UiBudget.TotalMilliseconds, "UI 執行緒上釋放後第一次遮蔽一個題目（超過就要在遠端指令到達引擎時預熱）");
        Median(ui.Four).Should().BeLessThan(UiBudget.TotalMilliseconds);
        Median(ui.Text).Should().BeLessThan(UiBudget.TotalMilliseconds);
    }

    private sealed record UiTimings(List<double> One, List<double> Four, List<double> Text, List<double> EngineFirst, List<double> Compiled);

    /// <summary>照引擎的修剪釋放、積極回收、停 50 ms，再量一次呼叫（毫秒）；<paramref name="beforeTimer"/> 在開碼表之前做</summary>
    private static double AfterRelease(Action work, Action? beforeTimer = null)
    {
        CredentialLeakRules.ReleaseCompiledPatterns();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Thread.Sleep(50);
        beforeTimer?.Invoke();
        return Time(work);
    }

    private static double AfterRelease(Func<bool> work, Action? beforeTimer = null) => AfterRelease(() => { _ = work(); }, beforeTimer);

    private static double AfterRelease(Func<string?> work, Action? beforeTimer = null) => AfterRelease(() => { _ = work(); }, beforeTimer);

    private static double Time(Action work)
    {
        var watch = Stopwatch.StartNew();
        work();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Time(Func<bool> work) => Time(() => { _ = work(); });

    private static double Median(List<double> values)
    {
        var ordered = values.Order().ToList();
        return ordered.Count % 2 == 1 ? ordered[ordered.Count / 2] : (ordered[ordered.Count / 2 - 1] + ordered[ordered.Count / 2]) / 2;
    }

    private void Report(string label, List<double> values)
        => output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label}：中位數 {Median(values):0.00} ms、最大 {values.Max():0.00} ms、最小 {values.Min():0.00} ms（{values.Count} 回）"));

    /// <summary>在新開的 STA 執行緒（裝 WindowsFormsSynchronizationContext，同 app 的 UI 執行緒）同步執行</summary>
    private static T OnUiThread<T>(Func<T> work)
    {
        T? result = default;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            using var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }) { IsBackground = true, Name = "UI（第一次遮蔽的量測）" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result!;
    }

    /// <summary>等待回答的題目卡（Claude 的題目 id 就是題目文字；沒有權杖）</summary>
    private static AgentChatItem Card(int questions) => new()
    {
        Id = "q-card-" + questions.ToString(CultureInfo.InvariantCulture),
        Kind = AgentItemKind.Question,
        Status = AgentItemStatus.Waiting,
        RequestKey = "request-1",
        Questions = Enumerable.Range(1, questions).Select(i => new AgentQuestion
        {
            Id = $"第 {i} 題：部署之後要先看哪一個數字？",
            Text = $"第 {i} 題：部署之後要先看哪一個數字？",
            Options = [new AgentQuestionOption { Label = "healthz 的 build" }, new AgentQuestionOption { Label = "ops/status 的記憶體" }],
        }).ToList(),
    };

    /// <summary>遠端送來的答案：鍵是遮蔽後的題目 id（這裡沒有權杖，等於原文）</summary>
    private static JObject Answers(AgentChatItem card)
        => new(card.Questions.Select(q => new JProperty(q.Id, new JArray("healthz 的 build"))));

    private static string LongText(int length)
    {
        var builder = new StringBuilder();
        var i = 0;
        while (builder.Length < length)
            builder.Append(CultureInfo.InvariantCulture, $"第 {i++} 行：部署之後在 Hub 主機上看 /healthz 的 build 與 ops/status 的 privateMb，沒有權杖。");
        return builder.ToString(0, length);
    }

    /// <summary>檔案內容（ASCII、沒有權杖、剛好 <paramref name="size"/> 位元組）</summary>
    private static byte[] FileContent(int size)
    {
        var builder = new StringBuilder(size + 128);
        var i = 0;
        while (builder.Length < size)
            builder.Append(CultureInfo.InvariantCulture, $"line {i++}: the quick brown fox jumps over the lazy dog 0123456789 build=7c5d790 ok\n");
        return Encoding.ASCII.GetBytes(builder.ToString(0, size));
    }

    /// <summary>measure_sync 的已配對資料根那一份對話（FD 的 ConversationSeeder：300 個、種子 1）的 meta 字串（啟動重整遮蔽的那一批）</summary>
    private static List<string> SeededMeta()
    {
        var seeds = ConversationSeeder.Generate(new SeedSpec(300, OlderThanDays: 3, ItemsPerConversation: 40, Seed: 1, BaseTimeUtc, Account: "measure-seed"));
        var meta = new List<string>();
        foreach (var (conversation, _) in seeds)
        {
            foreach (var value in new[] { conversation.Title, conversation.Model, conversation.Effort, conversation.WorkingDirectory })
            {
                if (!string.IsNullOrEmpty(value))
                    meta.Add(value);
            }
        }

        return meta;
    }
}
