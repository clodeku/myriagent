using Myriagent.Tests.TestSupport;
using Myriagent.Usage;

namespace Myriagent.Tests.Usage;

/// <summary>用量歷史（走勢線）：變化點才記、保留 14 天、畫面取 7 天每小時一點最多 168 點、存檔與重新載入</summary>
public sealed class UsageHistoryStoreTests : IDisposable
{
    private const long Minute = 60_000;
    private const long Hour = 3_600_000;

    private readonly TempDirectory temp = new("usage");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void AddPoint_變化不到0點5且30分鐘內不記_時間倒退不記()
    {
        var points = new Dictionary<string, List<double[]>>();
        long t0 = 1_791_000_000_000;

        UsageHistoryStore.AddPoint(points, "session", t0, 10).Should().BeTrue("第一點一定記");
        UsageHistoryStore.AddPoint(points, "session", t0 + 10 * Minute, 10.4).Should().BeFalse("變化 0.4、10 分鐘內");
        UsageHistoryStore.AddPoint(points, "session", t0 + 11 * Minute, 10.5).Should().BeTrue("變化剛好 0.5");
        UsageHistoryStore.AddPoint(points, "session", t0 + 11 * Minute, 50).Should().BeFalse("時間相同");
        UsageHistoryStore.AddPoint(points, "session", t0 + 5 * Minute, 50).Should().BeFalse("時間倒退");
        UsageHistoryStore.AddPoint(points, "session", t0 + 40 * Minute, 10.5).Should().BeFalse("沒變化、距上一點 29 分鐘");
        UsageHistoryStore.AddPoint(points, "session", t0 + 41 * Minute, 10.5).Should().BeTrue("沒變化但距上一點滿 30 分鐘");
        UsageHistoryStore.AddPoint(points, "weekly", t0, 30).Should().BeTrue("每個時間窗各自記");

        points["session"].Select(p => p[1]).Should().Equal(10, 10.5, 10.5);
        points["weekly"].Should().ContainSingle();
    }

    [Fact]
    public void Downsample_只取最近7天_每小時取最後一個值()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_791_000_000_000 / Hour * Hour + 30 * Minute);
        long n = now.ToUnixTimeMilliseconds();
        var hourStart = n / Hour * Hour;
        var points = new List<double[]>
        {
            new double[] { n - 8 * 24 * Hour, 1 },
            new double[] { n - 6 * 24 * Hour, 2 },
            new double[] { hourStart - Hour + 5 * Minute, 3 },
            new double[] { hourStart + 1 * Minute, 4 },
            new double[] { hourStart + 2 * Minute, 5 },
            new double[] { hourStart + 3 * Minute, 6 },
        };

        var view = UsageHistoryStore.Downsample(points, now);

        view.Select(p => p[1]).Should().Equal(2, 3, 6);
        view[^1][0].Should().Be(hourStart + 3 * Minute, "同一小時取最後一個點（保留原本的時間）");
    }

    [Fact]
    public void Downsample_最多168點_保留最新的()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_791_000_000_000 / Hour * Hour + 30 * Minute);
        long n = now.ToUnixTimeMilliseconds();
        var points = Enumerable.Range(0, 10 * 24).Select(i => new double[] { n - (10 * 24 - 1 - i) * Hour, i }).ToList();

        var view = UsageHistoryStore.Downsample(points, now);

        view.Should().HaveCount(UsageHistoryStore.MaxViewPoints);
        view[^1][1].Should().Be(10 * 24 - 1);
        view.Select(p => p[0]).Should().BeInAscendingOrder();
        UsageHistoryStore.Downsample(Array.Empty<double[]>(), now).Should().BeEmpty();
    }

    [Fact]
    public void Record_Flush後重新載入_快照與走勢點都還在()
    {
        var fetched = new DateTimeOffset(2026, 10, 10, 1, 0, 0, TimeSpan.Zero);
        var snapshot = new UsageSnapshot(fetched, "api", new[]
        {
            new UsageWindow("session", "5 小時", 12, fetched.AddHours(3)),
            new UsageWindow("weekly:Fable", "每週・Fable", 100, fetched.AddDays(4), Locked: true),
        }, "12,345.68", "已啟用額外用量", null, false);

        var store = new UsageHistoryStore(temp.Root);
        store.Record("acc-1", snapshot);
        File.Exists(Path.Combine(temp.Root, "acc-1.json")).Should().BeFalse("Flush 前不寫檔");
        store.Flush();
        File.Exists(Path.Combine(temp.Root, "acc-1.json")).Should().BeTrue();

        var reloaded = new UsageHistoryStore(temp.Root);
        var last = reloaded.GetLastSnapshot("acc-1")!;
        last.FetchedAt.Should().Be(fetched);
        last.Source.Should().Be("api");
        last.Credits.Should().Be("12,345.68");
        last.Note.Should().Be("已啟用額外用量");
        last.Stale.Should().BeFalse();
        last.Windows.Should().HaveCount(2);
        last.Windows[0].Should().Be(snapshot.Windows[0]);
        last.Windows[1].Locked.Should().BeTrue();
        last.Windows[1].ResetsAt.Should().Be(fetched.AddDays(4));

        var view = reloaded.GetView("acc-1", "session", fetched.AddMinutes(5));
        view.Should().ContainSingle();
        view[0][0].Should().Be(fetched.ToUnixTimeMilliseconds());
        view[0][1].Should().Be(12);
        reloaded.GetView("acc-1", "沒有這個時間窗", fetched).Should().BeEmpty();
        reloaded.GetLastSnapshot("沒有這個帳戶").Should().BeNull();
    }

    [Fact]
    public void Record_只有變化才加點_超過14天的點清掉()
    {
        // 00:50 記一點；01:05（15 分鐘後、只變 0.2）不記——它落在另一個小時，記了的話走勢線會多一點；02:30 變成 20 才記
        var t = new DateTimeOffset(2026, 10, 1, 0, 50, 0, TimeSpan.Zero);
        UsageSnapshot At(DateTimeOffset when, double percent) => new(when, "api", new[] { new UsageWindow("weekly", "每週", percent, null) }, null, null, null, false);

        var store = new UsageHistoryStore(temp.Root);
        store.Record("acc", At(t, 10));
        store.Record("acc", At(t.AddMinutes(15), 10.2));
        store.Record("acc", At(t.AddMinutes(100), 20));
        store.GetView("acc", "weekly", t.AddMinutes(100)).Select(p => p[1]).Should().Equal(10, 20);
        store.GetLastSnapshot("acc")!.Windows.Single().Percent.Should().Be(20, "最後快照一律更新");

        store.Record("acc", At(t.AddDays(15), 30));
        store.Flush();

        var reloaded = new UsageHistoryStore(temp.Root);
        reloaded.GetView("acc", "weekly", t.AddDays(15)).Select(p => p[1]).Should().Equal(new[] { 30d }, "14 天前的點在記錄時清掉");
    }

    [Fact]
    public void Remove_刪除帳戶的歷史檔()
    {
        var store = new UsageHistoryStore(temp.Root);
        store.Record("acc", new UsageSnapshot(DateTimeOffset.UtcNow, "api", new[] { new UsageWindow("session", "5 小時", 1, null) }, null, null, null, false));
        store.Flush();

        store.Remove("acc");

        File.Exists(Path.Combine(temp.Root, "acc.json")).Should().BeFalse();
        store.GetLastSnapshot("acc").Should().BeNull();
        store.Flush();
        File.Exists(Path.Combine(temp.Root, "acc.json")).Should().BeFalse("移除後不會再被寫回");
    }

    [Fact]
    public void 讀不到的歷史檔_這次不寫回_不用空的歷史蓋掉原本的走勢()
    {
        var fetched = new DateTimeOffset(2026, 10, 10, 1, 0, 0, TimeSpan.Zero);
        UsageSnapshot At(DateTimeOffset when, double percent) => new(when, "api", new[] { new UsageWindow("weekly", "每週", percent, null) }, null, null, null, false);
        var original = new UsageHistoryStore(temp.Root);
        original.Record("acc", At(fetched, 10));
        original.Record("acc", At(fetched.AddHours(2), 40));
        original.Flush();
        var path = Path.Combine(temp.Root, "acc.json");
        var before = File.ReadAllBytes(path);

        var store = new UsageHistoryStore(temp.Root);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            store.GetLastSnapshot("acc").Should().BeNull("被其他程式占用，這次讀不到");
        store.Record("acc", At(fetched.AddHours(3), 50));
        store.Flush();

        File.ReadAllBytes(path).Should().Equal(before, "讀不到時絕不能用只有新點的歷史覆寫原檔");
        new UsageHistoryStore(temp.Root).GetView("acc", "weekly", fetched.AddHours(3)).Select(p => p[1]).Should().Equal(10, 40);
    }

    [Fact]
    public void 壞掉的歷史檔當作沒有歷史()
    {
        File.WriteAllText(Path.Combine(temp.Root, "broken.json"), "{不是 JSON");
        var store = new UsageHistoryStore(temp.Root);
        store.GetLastSnapshot("broken").Should().BeNull();
        store.GetView("broken", "session", DateTimeOffset.UtcNow).Should().BeEmpty();
    }
}
