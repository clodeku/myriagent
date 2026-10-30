using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;

namespace Myriagent.Tests.Usage;

/// <summary>各帳戶每天的 token 計數：依本機日期累加、近 N 天合計、只保留 30 天、存檔與重新載入、讀不到的原檔不覆寫</summary>
public sealed class TokenTallyStoreTests : IDisposable
{
    private readonly TempDirectory temp = new("tokens");

    public void Dispose() => temp.Dispose();

    private static AgentUsage U(long input, long output, long cached) => new(input, output, cached, null);

    [Fact]
    public void 依日期累加_近N天合計含今天()
    {
        var store = new TokenTallyStore(temp.Combine("usage-tokens.json"));
        var today = new DateTime(2026, 10, 22, 9, 0, 0);
        store.Add("a1", today, U(46_873, 6, 16_240));
        store.Add("a1", today.AddHours(3), U(0, 267, 0));
        store.Add("a1", today.AddDays(-1), U(1_000, 10, 500));
        store.Add("a1", today.AddDays(-8), U(9_000, 90, 0));
        store.Add("a2", today, U(7, 7, 7));
        store.Add("a2", today, TokenTallyRules.Zero);

        store.Sum("a1", today, 1).Should().Be(U(46_873, 273, 16_240));
        store.Sum("a1", today, 7).Should().Be(U(47_873, 283, 16_740));
        store.Sum("a1", today, 30).Should().Be(U(56_873, 373, 16_740));
        store.Sum("a2", today, 1).Should().Be(U(7, 7, 7));
        store.Sum("沒有的帳戶", today, 30).Should().Be(TokenTallyRules.Zero);
        store.Sum("a1", today.AddDays(1), 1).Should().Be(TokenTallyRules.Zero, "隔天今天從 0 開始");
    }

    [Fact]
    public async Task 複本列出每一格_其他執行緒同時記也不會壞()
    {
        var store = new TokenTallyStore(temp.Combine("usage-tokens.json"));
        var today = new DateTime(2026, 10, 22, 9, 0, 0);
        store.Add("a1", today, U(10, 1, 4));
        store.Add("a1", today.AddDays(-1), U(5, 2, 0));
        store.Add("a2", today, U(7, 7, 7));

        store.Snapshot().OrderBy(t => t.Day).ThenBy(t => t.LocalAccountId).Should().Equal(
            new LocalTokenTally("a1", "2026-10-21", 5, 0, 2),
            new LocalTokenTally("a1", "2026-10-22", 10, 4, 1),
            new LocalTokenTally("a2", "2026-10-22", 7, 7, 7));

        // 同步引擎在自己的執行緒取複本（tok1），中樞同時在 UI 執行緒記
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
                store.Add("a" + (i % 50), today.AddDays(-(i % 20)), U(1, 1, 0));
        });
        while (!writer.IsCompleted)
            store.Snapshot().Should().NotBeNull();
        await writer;
        store.Snapshot().Sum(t => t.Output).Should().Be(1 + 2 + 7 + 2_000);
    }

    [Fact]
    public void 存檔後重新載入_超過30天的在記新的一天時刪掉()
    {
        var path = temp.Combine("usage-tokens.json");
        var day1 = new DateTime(2026, 9, 1, 12, 0, 0);
        var store = new TokenTallyStore(path);
        store.Add("a1", day1, U(100, 1, 0));
        store.Flush();
        File.Exists(path).Should().BeTrue();

        var reloaded = new TokenTallyStore(path);
        reloaded.Sum("a1", day1, 1).Should().Be(U(100, 1, 0));
        var later = day1.AddDays(40);
        reloaded.Add("a1", later, U(5, 5, 0));
        reloaded.Flush();

        var again = new TokenTallyStore(path);
        again.Sum("a1", later, 30).Should().Be(U(5, 5, 0));
        File.ReadAllText(path).Should().NotContain("2026-09-01", "超過 30 天的日期已刪掉");
    }

    [Fact]
    public void 沒有變動不寫檔()
    {
        var path = temp.Combine("usage-tokens.json");
        var store = new TokenTallyStore(path);
        store.Sum("a1", DateTime.Now, 1);
        store.Flush();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void 讀不到的原檔_這次執行只在記憶體累加_不覆寫()
    {
        var path = temp.Combine("usage-tokens.json");
        File.WriteAllText(path, """{"Days":{"2026-10-21":{"a1":{"Input":1,"Cached":0,"Output":1}}}}""");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var store = new TokenTallyStore(path);
            store.Add("a1", new DateTime(2026, 10, 22), U(10, 10, 0));
            store.Sum("a1", new DateTime(2026, 10, 22), 1).Should().Be(U(10, 10, 0));
            store.Flush();
        }
        File.ReadAllText(path).Should().Contain("2026-10-21").And.NotContain("2026-10-22");
    }
}
