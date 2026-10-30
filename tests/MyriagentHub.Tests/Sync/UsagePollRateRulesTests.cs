using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Sync;

/// <summary>usagePollsPerHour 的滑動一小時計數（S12 的伺服器指標）</summary>
public sealed class UsagePollRateRulesTests
{
    private const long T0 = 1_791_600_000_000;

    private const long Hour = UsagePollRateRules.WindowMs;

    [Fact]
    public void 剛好一小時的過期_差一毫秒的還在()
    {
        UsagePollRateRules.InWindow(T0, T0).Should().BeTrue();
        UsagePollRateRules.InWindow(T0, T0 + Hour - 1).Should().BeTrue();
        UsagePollRateRules.InWindow(T0, T0 + Hour).Should().BeFalse();
        UsagePollRateRules.InWindow(T0 + 5000, T0).Should().BeTrue("時鐘倒退時比 now 晚的照算");

        UsagePollRateRules.Prune([T0, T0 + 10, T0 + Hour - 1, T0 + Hour], T0 + Hour)
            .Should().Equal(T0 + 10, T0 + Hour - 1, T0 + Hour);
        UsagePollRateRules.Prune([], T0).Should().BeEmpty();
    }

    [Fact]
    public void 記一次_先去掉過期的再加上現在()
    {
        IReadOnlyList<long> samples = [];
        samples = UsagePollRateRules.Add(samples, T0);
        samples = UsagePollRateRules.Add(samples, T0 + 20 * 60_000);
        samples.Should().Equal(T0, T0 + 20 * 60_000);

        samples = UsagePollRateRules.Add(samples, T0 + Hour + 1);
        samples.Should().Equal(T0 + 20 * 60_000, T0 + Hour + 1);
    }

    [Fact]
    public void 每個帳戶有上限_超過丟最舊的_次數以上限計()
    {
        IReadOnlyList<long> samples = [];
        for (var i = 0; i < UsagePollRateRules.MaxSamplesPerAccount + 50; i++)
            samples = UsagePollRateRules.Add(samples, T0 + i);

        samples.Should().HaveCount(UsagePollRateRules.MaxSamplesPerAccount);
        samples[0].Should().Be(T0 + 50);
        samples[^1].Should().Be(T0 + UsagePollRateRules.MaxSamplesPerAccount + 49);
    }

    [Fact]
    public void 彙整_多帳戶依id排序_0次的不列_取最大值()
    {
        var samples = new Dictionary<string, IReadOnlyList<long>>(StringComparer.Ordinal)
        {
            ["la_B"] = [T0, T0 + 1000, T0 + 2000],
            ["la_A"] = [T0 + 3000],
            ["la_C"] = [T0 - Hour],
            ["la_D"] = [],
        };

        var summary = UsagePollRateRules.Summarize(samples, T0 + 5000);

        summary.Max.Should().Be(3);
        summary.Accounts.Should().Equal(new UsagePollCount("la_A", 1), new UsagePollCount("la_B", 3));

        // 一小時之後：la_B 的前兩筆過期
        var later = UsagePollRateRules.Summarize(samples, T0 + Hour + 1500);
        later.Max.Should().Be(1);
        later.Accounts.Should().Equal(new UsagePollCount("la_A", 1), new UsagePollCount("la_B", 1));
    }

    [Fact]
    public void 沒有任何上傳_最大值0_清單是空的()
    {
        var summary = UsagePollRateRules.Summarize(new Dictionary<string, IReadOnlyList<long>>(), T0);

        summary.Max.Should().Be(0);
        summary.Accounts.Should().BeEmpty();
    }

    [Fact]
    public void 計數只在記憶體_新的實例是空的_查詢時丟掉過期的()
    {
        var stats = new UsagePollStats();
        stats.Record("la_A", T0);
        stats.Record("la_A", T0 + 1000);
        stats.Record("la_B", T0 + 2000);

        var summary = stats.Snapshot(T0 + 3000);
        summary.Max.Should().Be(2);
        summary.Accounts.Should().Equal(new UsagePollCount("la_A", 2), new UsagePollCount("la_B", 1));

        stats.Snapshot(T0 + Hour + 1500).Accounts.Should().Equal(new UsagePollCount("la_B", 1));
        stats.Snapshot(T0 + 2 * Hour).Accounts.Should().BeEmpty();

        // 服務重新啟動＝新的實例：歸零
        stats.Record("la_A", T0 + 2 * Hour);
        var restarted = new UsagePollStats().Snapshot(T0 + 2 * Hour);
        restarted.Max.Should().Be(0);
        restarted.Accounts.Should().BeEmpty();
    }
}
