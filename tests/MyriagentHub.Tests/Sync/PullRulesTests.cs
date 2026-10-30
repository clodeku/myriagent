using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 拉取的合併與上限（合併前審查 P4SRV-MAT-01）：每個游標的位元組預算（<see cref="PullRules.CursorDone"/>）讓大事件不必整批讀進記憶體，
/// 回應的事件與 more 和每個游標讀滿 maxEvents＋1 筆時完全相同。以固定種子的隨機事件與重疊的游標（cls 與 stream 混用）比對兩種讀法
/// </summary>
public sealed class PullRulesTests
{
    private static readonly string[] Streams = ["c:a", "c:b", "c:c", SyncStreams.Catalog];

    [Fact]
    public void SelectPrefix_筆數與位元組上限_至少一筆()
    {
        PullRules.SelectPrefix([], 10, 100).Should().Be(0);
        PullRules.SelectPrefix([500], 10, 100).Should().Be(1, "單筆超過上限也回");
        PullRules.SelectPrefix([500, 1], 10, 100).Should().Be(1);
        PullRules.SelectPrefix([40, 60, 1], 10, 100).Should().Be(2, "合計剛好等於上限可以");
        PullRules.SelectPrefix([1, 1, 1], 2, 100).Should().Be(2, "筆數上限");
    }

    [Fact]
    public void CursorDone_兩列以上而且累計超過上限()
    {
        PullRules.CursorDone(1, 5_000, 100).Should().BeFalse("第一筆就越界時再讀一筆：more 才照實");
        PullRules.CursorDone(2, 101, 100).Should().BeTrue();
        PullRules.CursorDone(2, 100, 100).Should().BeFalse("剛好等於上限的兩筆可能都被選上");
        PullRules.CursorDone(5, 99, 100).Should().BeFalse();
    }

    [Fact]
    public void 位元組預算_回應與讀滿時完全相同_暫存有上限()
    {
        var rng = new Random(20261020);
        var budgeted = 0;
        for (var iteration = 0; iteration < 3_000; iteration++)
        {
            var log = RandomLog(rng);
            var cursors = RandomCursors(rng, log.Count);
            var maxEvents = rng.Next(1, 501);
            var maxBytes = rng.Next(4) switch
            {
                0 => rng.Next(1, 1_000),
                1 => rng.Next(1_000, 100_000),
                _ => rng.Next(100_000, 1_048_577),
            };

            var full = Run(log, cursors, maxEvents, maxBytes, budget: false);
            var withBudget = Run(log, cursors, maxEvents, maxBytes, budget: true);

            withBudget.Selected.Should().Equal(full.Selected, $"第 {iteration} 次：選到的事件相同");
            withBudget.More.Should().Be(full.More, $"第 {iteration} 次：more 相同");
            var largest = log.Count == 0 ? 0 : log.Max(e => e.Size);
            withBudget.MaxHeldPerCursor.Should().BeLessThanOrEqualTo(maxBytes + 2 * largest, $"第 {iteration} 次：每個游標暫存的只有上限加兩筆");
            if (withBudget.Read < full.Read)
                budgeted++;
        }
        budgeted.Should().BeGreaterThan(500, "樣本裡有很多次真的提早停讀");
    }

    private sealed record Event(long Seq, string Cls, string Stream, long Size);

    private sealed record Cursor(string? Cls, string? Stream, long After);

    /// <summary>同 EventLog.Pull：每個游標依 seq 取最多 maxEvents＋1 筆（<paramref name="budget"/> 時累計超過上限就停讀），合併後取前綴</summary>
    private static (List<long> Selected, bool More, long MaxHeldPerCursor, int Read) Run(List<Event> log, List<Cursor> cursors, int maxEvents, long maxBytes, bool budget)
    {
        var merged = new SortedDictionary<long, Event>();
        long maxHeld = 0;
        var read = 0;
        foreach (var cursor in cursors)
        {
            var rows = 0;
            long bytes = 0;
            foreach (var e in log.Where(e => e.Seq > cursor.After && (cursor.Cls is not null ? e.Cls == cursor.Cls : e.Stream == cursor.Stream)).Take(maxEvents + 1))
            {
                merged.TryAdd(e.Seq, e);
                read++;
                rows++;
                bytes += e.Size;
                if (budget && PullRules.CursorDone(rows, bytes, maxBytes))
                    break;
            }
            maxHeld = Math.Max(maxHeld, bytes);
        }
        var candidates = merged.Values.ToList();
        var selected = PullRules.SelectPrefix(candidates.Select(e => e.Size), maxEvents, maxBytes);
        return (candidates.Take(selected).Select(e => e.Seq).ToList(), merged.Count > selected, maxHeld, read);
    }

    private static List<Event> RandomLog(Random rng)
    {
        var count = rng.Next(0, 400);
        var log = new List<Event>(count);
        for (var seq = 1; seq <= count; seq++)
        {
            var stream = Streams[rng.Next(Streams.Length)];
            var cls = stream == SyncStreams.Catalog ? SyncClasses.Catalog : rng.Next(3) == 0 ? SyncClasses.Meta : SyncClasses.Item;
            var size = rng.Next(4) switch
            {
                0 => rng.Next(1, 500),
                1 => rng.Next(500, 20_000),
                2 => rng.Next(20_000, 200_000),
                _ => rng.Next(200_000, 1_200_000),
            };
            log.Add(new Event(seq, cls, stream, size + EventLog.PulledEventOverheadBytes));
        }
        return log;
    }

    private static List<Cursor> RandomCursors(Random rng, int count)
    {
        var cursors = new List<Cursor>();
        var n = rng.Next(1, EventLog.MaxPullCursors + 1);
        for (var i = 0; i < n; i++)
        {
            var after = rng.Next(3) == 0 ? 0 : rng.Next(0, count + 1);
            cursors.Add(rng.Next(2) == 0
                ? new Cursor(new[] { SyncClasses.Meta, SyncClasses.Item, SyncClasses.Catalog }[rng.Next(3)], null, after)
                : new Cursor(null, Streams[rng.Next(Streams.Length)], after));
        }
        return cursors;
    }
}
