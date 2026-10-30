using Myriagent.Agents;
using Myriagent.Usage;

namespace Myriagent.Tests.Usage;

/// <summary>每週額度每 5% 的分段紀錄的純規則：哪些是每週的額度窗、邊界、跨過邊界結一筆、換週丟掉進行中的、彙總每 5% 的 token</summary>
public sealed class UsageSegmentRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Resets = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

    private static UsageWindow W(double percent, string key = "weekly", string label = "每週", DateTimeOffset? resets = null)
        => new(key, label, percent, resets ?? Resets);

    private static UsageSegmentRecord NewRecord(UsageSegmentOpen open, UsageWindow window, DateTimeOffset now) => new() { Id = "r", AccountId = open.AccountId };

    private static (UsageSegmentOpen Next, UsageSegmentRecord? Closed) Observe(UsageSegmentOpen? open, UsageWindow window, DateTimeOffset now)
        => UsageSegmentRules.Observe(open, "a1", window, now, NewRecord);

    private static AgentUsage U(long input, long output, long cached) => new(input, output, cached, null);

    [Theory]
    [InlineData("weekly", "每週", true)]
    [InlineData("weekly:Opus", "每週・Opus", true)]
    [InlineData("codex:10080", "每週", true)]
    [InlineData("codex_spark:10080", "每週・Spark", true)]
    [InlineData("agy-gemini-weekly", "每週・Gemini", true)]
    [InlineData("grok-credits", "每週", true)]
    [InlineData("grok-credits", "每月", false)]
    [InlineData("session", "5 小時", false)]
    [InlineData("codex:300", "5 小時", false)]
    [InlineData("agy-gemini-5h", "5 小時・Gemini", false)]
    public void 只看每週的額度窗(string key, string label, bool weekly)
        => UsageSegmentRules.IsWeekly(new UsageWindow(key, label, 10, null)).Should().Be(weekly);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4.9, 0)]
    [InlineData(5, 5)]
    [InlineData(14.99, 10)]
    [InlineData(15, 15)]
    [InlineData(99.9, 95)]
    [InlineData(100, 100)]
    [InlineData(130, 100)]
    [InlineData(-3, 0)]
    [InlineData(double.NaN, 0)]
    public void 邊界(double percent, double bucket) => UsageSegmentRules.BucketOf(percent).Should().Be(bucket);

    [Fact]
    public void 第一次看到_從這裡開始記_只有0才是完整的一段()
    {
        var (open, closed) = Observe(null, W(12.3), T0);
        closed.Should().BeNull();
        open.Should().Match<UsageSegmentOpen>(o => o.Bucket == 10 && o.StartPercent == 12.3 && !o.Complete && o.AccountId == "a1" && o.WindowKey == "weekly");
        Observe(null, W(0), T0).Next.Complete.Should().BeTrue("週一開始、還沒用過");
    }

    [Fact]
    public void 跨過邊界結一筆_記開始與結束看到的使用率與token_新的一段從這次開始且是完整的()
    {
        var (open, _) = Observe(null, W(12.3), T0);
        UsageSegmentRules.AddTokens(open, "claude-opus-5-5", U(1_000, 10, 800), 150);
        UsageSegmentRules.AddTokens(open, "claude-opus-5-5", U(2_000, 20, 1_900), 0);
        UsageSegmentRules.AddTokens(open, null, U(5, 1, 0), 0);
        (open, var closed) = Observe(open, W(14.9), T0.AddMinutes(10));
        closed.Should().BeNull("還沒到 15");
        open.LastPercent.Should().Be(14.9);

        (var next, closed) = Observe(open, W(15.2), T0.AddMinutes(20));
        closed.Should().NotBeNull();
        closed!.Should().Match<UsageSegmentRecord>(r => r.FromBucket == 10 && r.ToBucket == 15 && r.StartPercent == 12.3 && r.EndPercent == 15.2
            && r.StartedAt == T0 && r.EndedAt == T0.AddMinutes(20) && !r.Complete && r.WindowKey == "weekly" && r.ResetsAt == Resets);
        closed.Models["claude-opus-5-5"].Should().BeEquivalentTo(new SegmentTokens { Input = 3_000, Cached = 2_700, CacheWrite = 150, Output = 30 });
        closed.Models[UsageSegmentRules.UnknownModel].Output.Should().Be(1);
        next.Should().Match<UsageSegmentOpen>(o => o.Bucket == 15 && o.StartPercent == 15.2 && o.Complete && o.Models.Count == 0);

        // 一次跳過兩個邊界：一筆，終點是最後跨過的
        UsageSegmentRules.AddTokens(next, "m", U(10, 1, 0), 0);
        var jump = Observe(next, W(26), T0.AddHours(1)).Closed!;
        jump.Should().Match<UsageSegmentRecord>(r => r.FromBucket == 15 && r.ToBucket == 25 && r.Complete && r.EndPercent == 26);
    }

    [Fact]
    public void 換週_重置時間往後一週或使用率大跌_進行中的丟掉重新開始()
    {
        var (open, _) = Observe(null, W(40), T0);
        UsageSegmentRules.AddTokens(open, "m", U(10, 1, 0), 0);
        var (next, closed) = Observe(open, W(41, resets: Resets.AddDays(7)), T0.AddDays(5));
        closed.Should().BeNull("跨週的不結");
        next.Should().Match<UsageSegmentOpen>(o => o.Bucket == 40 && o.StartPercent == 41 && !o.Complete && o.Models.Count == 0 && o.ResetsAt == Resets.AddDays(7));

        (next, closed) = Observe(open, W(0, resets: null), T0.AddDays(5));
        closed.Should().BeNull();
        next.Should().Match<UsageSegmentOpen>(o => o.Bucket == 0 && o.Complete, "掉回 0：新的一週從頭開始");

        // 同一週的重置時間在不同來源之間差幾分鐘、使用率差一點點：不是換週
        var (same, _) = Observe(open, W(38.5, resets: Resets.AddMinutes(30)), T0.AddMinutes(1));
        same.Should().BeSameAs(open);
    }

    [Fact]
    public void 用滿100是最後一段_之後不再結()
    {
        var (open, _) = Observe(null, W(96), T0);
        var (full, closed) = Observe(open, new UsageWindow("weekly", "每週", 100, Resets, Locked: true), T0.AddHours(1));
        closed!.ToBucket.Should().Be(100);
        Observe(full, new UsageWindow("weekly", "每週", 100, Resets, Locked: true), T0.AddHours(2)).Closed.Should().BeNull();
    }

    [Fact]
    public void 主要模型_九成以上才算_否則混用()
    {
        var models = new Dictionary<string, SegmentTokens>
        {
            ["claude-opus-5-5"] = new() { Input = 950, Output = 5 },
            ["claude-haiku-5-5"] = new() { Input = 40, Output = 5 },
        };
        UsageSegmentRules.DominantModel(models).Should().Be("claude-opus-5-5");
        models["claude-haiku-5-5"].Input = 400;
        UsageSegmentRules.DominantModel(models).Should().Be(UsageSegmentRules.MixedModel);
        UsageSegmentRules.DominantModel(new Dictionary<string, SegmentTokens>()).Should().Be(UsageSegmentRules.UnknownModel);
    }

    [Fact]
    public void 彙總_只算完整的段落_依帳戶額度窗主要模型分組_每5百分比以實際前進的百分點換算()
    {
        static UsageSegmentRecord R(string account, double start, double end, long input, long output, bool complete = true, string model = "opus", string window = "weekly")
            => new()
            {
                AccountId = account, AccountLabel = account + " 名稱", Provider = "Claude", WindowKey = window, WindowLabel = "每週",
                StartPercent = start, EndPercent = end, Complete = complete, EndedAt = T0.AddMinutes(end),
                Models = new Dictionary<string, SegmentTokens> { [model] = new() { Input = input, Cached = input / 2, Output = output } },
            };
        var summary = UsageSegmentRules.Summarize(new[]
        {
            R("a1", 10, 15, 1_000_000, 10_000),
            R("a1", 15, 21, 1_200_000, 12_000),   // 一筆多走了 1%：以實際的 6% 換算
            R("a1", 3, 5, 999, 9, complete: false),   // 不完整的不算
            R("a1", 20, 20, 5, 5),                    // 沒有前進的不算
            R("a1", 25, 30, 500_000, 4_000, model: "sonnet"),
            R("a2", 0, 5, 300_000, 3_000),
        });
        summary.Should().HaveCount(3);
        var opus = summary.Single(g => g.AccountId == "a1" && g.Model == "opus");
        opus.Records.Should().Be(2);
        opus.Percent.Should().Be(11);
        opus.Tokens.Input.Should().Be(2_200_000);
        UsageSegmentRules.Per5(opus.Tokens.Input, opus.Percent).Should().Be(1_000_000);
        opus.MinPer5.Should().Be((1_200_000 + 12_000) * 5 / 6.0);
        opus.MaxPer5.Should().Be(1_010_000);
        opus.AccountLabel.Should().Be("a1 名稱");
        summary.Single(g => g.Model == "sonnet").Records.Should().Be(1);
        UsageSegmentRules.Per5(100, 0).Should().Be(0);
    }

    [Fact]
    public void 對話的模型_預設當成不知道()
    {
        UsageSegmentRules.ModelOfConversation(null).Should().BeNull();
        UsageSegmentRules.ModelOfConversation("default").Should().BeNull();
        UsageSegmentRules.ModelOfConversation("opus").Should().Be("opus");
    }
}
