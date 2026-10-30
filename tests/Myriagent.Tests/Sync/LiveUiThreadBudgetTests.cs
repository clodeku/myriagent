using System.Diagnostics;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync;

/// <summary>
/// UI 執行緒的預算（WP4.9 計畫步 10；設計 11.4「5 個對話同時串流時同步記髒與淺快照的單次耗時 &lt;1 ms」）：真的中樞（HubHarness）＋對話發布者＋帶盒子的假引擎＋
/// 可撥的時鐘。5 個對話都被觀看（50 ms 合併），每個每 250 ms 一個文字片段（4 Hz，設計:3187），各 200 次更新（先暖身 10 次不算）：
/// <list type="bullet">
/// <item>記髒：中樞的掛鉤呼叫發布者（包一層記時間的接收者），每一次一筆。</item>
/// <item>淺快照：合併到期時發布者在 UI 執行緒 flush（複製項目放進盒子；包一層記時間的快照來源，量經 BeginInvoke 執行的那一次），每一次一筆。</item>
/// </list>
/// 時間型的斷言只在 <c>MYRIAGENT_PERF=1</c> 時做（p99 &lt;1 ms；機器忙時偶發失敗，P46:1469），預設只印數字；次數與盒子裡的內容一律斷言
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class LiveUiThreadBudgetTests(ITestOutputHelper output)
{
    private const string PerfGate = "MYRIAGENT_PERF";

    private const int Conversations = 5;

    private const int Updates = 200;

    private const int WarmUp = 10;

    /// <summary>4 Hz</summary>
    private const int TickMs = 250;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1);

    /// <summary>包住發布者：記下每一次掛鉤呼叫的耗時（記髒）</summary>
    private sealed class TimingSink(IConversationChangeSink inner) : IConversationChangeSink
    {
        public bool Recording { get; set; }

        public List<double> Marks { get; } = [];

        public void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item) => Time(() => inner.OnItemChanged(conversation, item));

        public void OnItemRemoved(ConversationHookInfo conversation, AgentChatItem item) => Time(() => inner.OnItemRemoved(conversation, item));

        public void OnConversationSaved(ConversationHookInfo conversation, bool itemsSaved) => Time(() => inner.OnConversationSaved(conversation, itemsSaved));

        public void OnConversationDeleting(ConversationHookInfo conversation) => Time(() => inner.OnConversationDeleting(conversation));

        public void OnHeaderChanged(ConversationHookInfo conversation) => Time(() => inner.OnHeaderChanged(conversation));

        public void OnRunningCountsChanged() => Time(inner.OnRunningCountsChanged);

        public void OnQueueChanged(ConversationHookInfo conversation) => Time(() => inner.OnQueueChanged(conversation));

        public void OnComposerChanged(ConversationHookInfo conversation) => Time(() => inner.OnComposerChanged(conversation));

        public void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli) => Time(() => inner.OnTurnStarted(conversation, startedByCli));

        public void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage) => Time(() => inner.OnTurnCompleted(conversation, outcome, usage));

        public void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech) => Time(() => inner.OnAttention(conversation, kind, itemId, speech));

        private void Time(Action call)
        {
            if (!Recording)
            {
                call();
                return;
            }

            var start = Stopwatch.GetTimestamp();
            call();
            Marks.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    /// <summary>包住中樞的快照來源：記下經 BeginInvoke 在 UI 執行緒執行的那一次（發布者的 flush＝淺快照）的耗時</summary>
    private sealed class TimingSource(IConversationPublishSource inner) : IConversationPublishSource
    {
        public bool Recording { get; set; }

        public List<double> Flushes { get; } = [];

        public string ConversationsRoot => inner.ConversationsRoot;

        public void BeginInvoke(Action action) => inner.BeginInvoke(() =>
        {
            if (!Recording)
            {
                action();
                return;
            }

            var start = Stopwatch.GetTimestamp();
            action();
            Flushes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        });

        public IReadOnlyList<string> ConversationIds() => inner.ConversationIds();

        public IReadOnlyList<PublishMetaSnapshot> SnapshotMeta(IReadOnlyList<string> ids) => inner.SnapshotMeta(ids);

        public PublishItemsPage? SnapshotItems(string conversationId, long afterOrd, int take, OrdHint? hint) => inner.SnapshotItems(conversationId, afterOrd, take, hint);

        public PublishQueueSnapshot? SnapshotQueue(string conversationId) => inner.SnapshotQueue(conversationId);

        public void SetWatched(IReadOnlySet<string> conversationIds) => inner.SetWatched(conversationIds);

        public void PauseForDlp(string conversationId) => inner.PauseForDlp(conversationId);

        public bool MarkServerDeleted(string conversationId, long atMs) => inner.MarkServerDeleted(conversationId, atMs);

        public void ShowPublishNotice(PublishNoticeKind kind, int count) => inner.ShowPublishNotice(kind, count);

        public void ApplyReplicaDelta(ReplicaDelta delta) => inner.ApplyReplicaDelta(delta);
    }

    [Fact]
    public async Task 五個對話各4Hz串流200次_記髒與淺快照的單次耗時()
    {
        using var h = new HubHarness();
        var time = new ManualTimeProvider();
        var box = new ConversationChangeBox();
        var engine = new RecordingSyncEngine { Conversations = box };
        var source = new TimingSource(h.Hub);
        var publisher = h.OnUi(() => ConversationPublisher.TryCreate(source, engine, time))!;
        var sink = new TimingSink(publisher);
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(sink));
        void Advance(int ms)
        {
            h.Ui.Invoke(() => time.Advance(TimeSpan.FromMilliseconds(ms)));
            // 經 BeginInvoke 排進 UI 執行緒的 flush 也做完
            h.Ui.Invoke(() => { });
        }

        AgentAccount[] accounts = [h.Claude, h.Claude2, h.Codex, h.Claude, h.Claude2];
        var started = accounts.Select(a => h.StartConversation(h.Main, a)).ToList();
        started.Select(s => s.Id).Should().OnlyHaveUniqueItems().And.HaveCount(Conversations);
        Advance(5_000);
        box.TakeAll();
        ((IConversationUiPort)publisher).SetWatched(started.Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
        await h.Ui.DrainAsync();

        var itemChanges = 0;
        for (var tick = 0; tick < WarmUp + Updates; tick++)
        {
            var recording = tick >= WarmUp;
            sink.Recording = recording;
            source.Recording = recording;
            foreach (var (_, session) in started)
                h.Raise(session, new AgentTextDeltaEvent("b1", "負載串流"));
            Advance(PublishTimingRules.WatchedFlushMs);
            source.Recording = false;
            sink.Recording = false;
            Advance(TickMs - PublishTimingRules.WatchedFlushMs);
            // 引擎那一側把盒子取空（不然盒子到上限會溢出）
            var taken = box.TakeAll().OfType<ItemChanged>().Count();
            if (recording)
                itemChanges += taken;
        }

        var marks = sink.Marks;
        var flushes = source.Flushes;
        output.WriteLine($"{Conversations} 個對話 × {Updates} 次更新（4 Hz、被觀看、50 ms 合併；暖身 {WarmUp} 次不算）：");
        output.WriteLine($"記髒（掛鉤）{Describe(marks)}");
        output.WriteLine($"淺快照（flush）{Describe(flushes)}；盒子收到 {itemChanges} 筆項目變化");

        marks.Should().HaveCountGreaterThanOrEqualTo(Conversations * Updates, "每個片段至少一次掛鉤呼叫");
        flushes.Should().HaveCountGreaterThanOrEqualTo(Updates, "每 250 ms 至少一次 flush（被觀看：50 ms 到期）");
        itemChanges.Should().BeGreaterThanOrEqualTo(Conversations * Updates, "每一次 flush 把 5 個對話的項目都放進盒子");
        h.Ui.Errors.Should().BeEmpty();
        if (Environment.GetEnvironmentVariable(PerfGate) == "1")
        {
            Percentile(marks, 99).Should().BeLessThan(Budget.TotalMilliseconds, "記髒的單次耗時 p99 <1 ms（設計 11.4）");
            Percentile(flushes, 99).Should().BeLessThan(Budget.TotalMilliseconds, "淺快照的單次耗時 p99 <1 ms（設計 11.4）");
        }
    }

    private static string Describe(List<double> values) =>
        $"{values.Count} 次：p50 {Percentile(values, 50) * 1000:0.0} µs、p95 {Percentile(values, 95) * 1000:0.0} µs、p99 {Percentile(values, 99) * 1000:0.0} µs、最大 {values.Max() * 1000:0.0} µs";

    /// <summary>nearest-rank 百分位數（毫秒）</summary>
    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count), 1, sorted.Count) - 1];
    }
}
