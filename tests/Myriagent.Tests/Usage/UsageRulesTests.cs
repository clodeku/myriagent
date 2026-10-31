using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Usage;

/// <summary>用量解析（Claude 用量端點、rate_limit_event、Codex rateLimits 的實測回應）、合併、推算重置、嚴重度、顯示文字</summary>
public class UsageRulesTests
{
    private static readonly DateTimeOffset SessionReset = new DateTimeOffset(2026, 10, 9, 18, 10, 0, TimeSpan.Zero).AddTicks(3_207_960);
    private static readonly DateTimeOffset WeeklyReset = new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero).AddTicks(3_208_190);
    private static readonly DateTimeOffset FableReset = new(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);

    /// <summary>與 ClaudeUsageClient 相同的讀法：日期維持字串</summary>
    private static JObject ParseAsClient(string json)
        => JsonConvert.DeserializeObject<JObject>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;

    private static string Show(UsageWindow w) => $"{w.Key}|{w.Label}|{w.Percent}|{w.Locked}";

    // ───── Claude 用量端點 ─────

    [Fact]
    public void ParseClaudeUsage_實測回應以Limits清單產生5小時每週與每週Fable()
    {
        var (windows, note) = UsageRules.ParseClaudeUsage(ParseAsClient(RealSamples.ClaudeUsage));

        windows.Select(Show).Should().Equal(
            "session|5 小時|12|False",
            "weekly|每週|32|False",
            "weekly:Fable|每週・Fable|0|False");
        windows[0].ResetsAt.Should().Be(SessionReset);
        windows[1].ResetsAt.Should().Be(WeeklyReset);
        windows[2].ResetsAt.Should().Be(FableReset);
        note.Should().BeNull("額外用量沒有啟用");
    }

    [Fact]
    public void ParseClaudeUsage_日期被JSON讀成Date型別時結果相同()
    {
        var asString = UsageRules.ParseClaudeUsage(ParseAsClient(RealSamples.ClaudeUsage)).Windows;
        var asDate = UsageRules.ParseClaudeUsage(JObject.Parse(RealSamples.ClaudeUsage)).Windows;

        asDate.Select(w => w.ResetsAt).Should().Equal(asString.Select(w => w.ResetsAt));
    }

    [Fact]
    public void ParseClaudeUsage_沒有Limits時退回具名時間窗()
    {
        var json = ParseAsClient("""
            {"five_hour":{"utilization":45.56,"resets_at":"2026-10-09T18:10:00+00:00"},
             "seven_day":{"utilization":80,"resets_at":"2026-10-14T22:00:00+00:00"},
             "seven_day_opus":{"utilization":10,"resets_at":null},
             "seven_day_sonnet":null,
             "limits":[]}
            """);

        var (windows, _) = UsageRules.ParseClaudeUsage(json);

        windows.Select(Show).Should().Equal("session|5 小時|45.6|False", "weekly|每週|80|False", "weekly:Opus|每週・Opus|10|False");
        windows[0].ResetsAt.Should().Be(new DateTimeOffset(2026, 10, 9, 18, 10, 0, TimeSpan.Zero));
        windows[2].ResetsAt.Should().BeNull();
    }

    [Fact]
    public void ParseClaudeUsage_鎖定_未知種類_沒有百分比_重複與範圍外的值()
    {
        var json = ParseAsClient("""
            {"limits":[
              {"kind":"session","percent":120,"severity":"blocked","resets_at":"2026-10-09T18:10:00+00:00"},
              {"kind":"session","percent":5},
              {"kind":"weekly_all","percent":-3,"severity":"exceeded"},
              {"kind":"weekly_scoped","percent":33.333,"scope":{"model":{"display_name":"Opus"}}},
              {"kind":"weekly_scoped","percent":10,"scope":null},
              {"kind":"monthly","percent":7},
              {"kind":"weekly_all","percent":null}
            ]}
            """);

        var (windows, _) = UsageRules.ParseClaudeUsage(json);

        windows.Select(Show).Should().Equal(
            "session|5 小時|100|True",
            "weekly|每週|0|True",
            "weekly:Opus|每週・Opus|33.3|False",
            "monthly|monthly|7|False",
            "weekly_scoped|weekly_scoped|10|False");
    }

    [Fact]
    public void ParseClaudeUsage_額外用量啟用時附說明()
    {
        UsageRules.ParseClaudeUsage(ParseAsClient("""{"limits":[],"extra_usage":{"is_enabled":true,"used_credits":12.5,"monthly_limit":100}}""")).Note
            .Should().Be("已啟用額外用量（本月 12.5／100）");
        UsageRules.ParseClaudeUsage(ParseAsClient("""{"extra_usage":{"is_enabled":true,"used_credits":null,"monthly_limit":null}}""")).Note
            .Should().Be("已啟用額外用量");
        UsageRules.ParseClaudeUsage(null).Windows.Should().BeEmpty();
    }

    // ───── Claude 對話的 rate_limit_event ─────

    [Fact]
    public void ParseClaudeRateLimitEvent_使用率0到1乘100_重置時間是秒()
    {
        var windows = UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"allowed","resetsAt":1791436800,"unifiedWindows":{"five_hour":{"utilization":0.15,"resetsAt":1791436800},"seven_day":{"utilization":0.05,"resetsAt":1792015200}}}"""));

        windows.Select(Show).Should().Equal("session|5 小時|15|False", "weekly|每週|5|False");
        windows[0].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1791436800));
        windows[1].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792015200));
    }

    [Fact]
    public void ParseClaudeRateLimitEvent_只有使用率時沒有重置時間()
    {
        var windows = UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"allowed","unifiedWindows":{"five_hour":{"utilization":0.15},"seven_day":{"utilization":0.05}}}"""));
        windows.Should().HaveCount(2);
        windows.Should().OnlyContain(w => w.ResetsAt == null);
    }

    [Theory]
    [InlineData("five_hour", true, false)]
    [InlineData(null, true, false)]
    [InlineData("seven_day", false, true)]
    [InlineData("seven_day_opus", false, false)]
    [InlineData("seven_day_sonnet", false, false)]
    [InlineData("overage", false, false)]
    public void ParseClaudeRateLimitEvent_被拒時鎖定對應的時間窗(string? rateLimitType, bool sessionLocked, bool weeklyLocked)
    {
        var info = new JObject
        {
            ["status"] = "rejected",
            ["resetsAt"] = 1791436800,
            ["unifiedWindows"] = JObject.Parse("""{"five_hour":{"utilization":1.0,"resetsAt":1791436800},"seven_day":{"utilization":0.4,"resetsAt":1792015200}}"""),
        };
        if (rateLimitType != null)
            info["rateLimitType"] = rateLimitType;

        var windows = UsageRules.ParseClaudeRateLimitEvent(info);

        windows.Single(w => w.Key == UsageRules.SessionKey).Locked.Should().Be(sessionLocked);
        windows.Single(w => w.Key == UsageRules.WeeklyKey).Locked.Should().Be(weeklyLocked);
    }

    [Fact]
    public void ParseClaudeRateLimitEvent_被拒但沒帶類型與重置時間_是暫時性的429_不鎖()
    {
        // 2.1.310 實測（tools/probes/claude_api_error_probe.py rate-limit）：訂閱帳戶碰到沒有額度標頭的 429，CLI 送這則再報
        // 「Server is temporarily limiting requests (not your usage limit)」
        UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"rejected","isUsingOverage":false}""")).Should().BeEmpty();
        UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"rejected","unifiedWindows":{"five_hour":{"utilization":0.4,"resetsAt":1791436800}}}"""))
            .Should().ContainSingle().Which.Locked.Should().BeFalse("有使用率照常更新，只是不鎖");

        UsageRules.IsClaudeQuotaRejection(JObject.Parse("""{"status":"rejected","isUsingOverage":false}""")).Should().BeFalse();
        UsageRules.IsClaudeQuotaRejection(JObject.Parse("""{"status":"rejected","rateLimitType":""}""")).Should().BeFalse();
        UsageRules.IsClaudeQuotaRejection(JObject.Parse("""{"status":"allowed","rateLimitType":"five_hour","resetsAt":1791436800}""")).Should().BeFalse();
        UsageRules.IsClaudeQuotaRejection(null).Should().BeFalse();
        UsageRules.IsClaudeQuotaRejection(JObject.Parse("""{"status":"rejected","rateLimitType":"five_hour"}""")).Should().BeTrue();
        UsageRules.IsClaudeQuotaRejection(JObject.Parse("""{"status":"Rejected","resetsAt":1791436800}""")).Should().BeTrue();
    }

    [Fact]
    public void ParseClaudeRateLimitEvent_被拒但沒有時間窗資料時補一個鎖定的時間窗()
    {
        var session = UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"rejected","resetsAt":1791436800}""")).Single();
        session.Key.Should().Be("session");
        session.Percent.Should().Be(100);
        session.Locked.Should().BeTrue();
        session.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1791436800));

        var weekly = UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"rejected","rateLimitType":"seven_day"}""")).Single();
        weekly.Key.Should().Be("weekly");
        weekly.Label.Should().Be("每週");
        weekly.ResetsAt.Should().BeNull();

        UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"allowed"}""")).Should().BeEmpty();
        UsageRules.ParseClaudeRateLimitEvent(null).Should().BeEmpty();
    }

    [Fact]
    public void ParseClaudeRateLimitEvent_模型的每週上限只鎖那個模型_不鎖整個每週()
    {
        var windows = UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""
            {"status":"rejected","rateLimitType":"seven_day_opus","resetsAt":1792015200,
             "unifiedWindows":{"five_hour":{"utilization":0.2,"resetsAt":1791436800},"seven_day":{"utilization":0.4,"resetsAt":1792015200}}}
            """));

        windows.Select(Show).Should().Equal("session|5 小時|20|False", "weekly|每週|40|False", "weekly:Opus|每週・Opus|100|True");
        windows[2].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792015200));
        UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"rejected","rateLimitType":"seven_day_sonnet"}""")).Select(Show)
            .Should().Equal("weekly:Sonnet|每週・Sonnet|100|True");
    }

    [Fact]
    public void ParseClaudeRateLimitEvent_其他被拒類型不猜成5小時()
    {
        UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"rejected","rateLimitType":"overage","resetsAt":1791436800}"""))
            .Should().BeEmpty("不知道是哪個時間窗，不能在 5 小時留下 100% 的假尖峰");
        UsageRules.ParseClaudeRateLimitEvent(JObject.Parse("""{"status":"rejected","rateLimitType":"overage","unifiedWindows":{"five_hour":{"utilization":0.3}}}"""))
            .Select(Show).Should().Equal("session|5 小時|30|False");
    }

    // ───── Codex rateLimits ─────

    [Fact]
    public void ParseCodexRateLimits_實測回應_10080分鐘是每週_點數餘額格式化()
    {
        var (windows, credits) = UsageRules.ParseCodexRateLimits(JObject.Parse(RealSamples.CodexRateLimitsRead));

        windows.Select(Show).Should().Equal("codex:10080|每週|9|False");
        windows[0].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1791959804));
        credits.Should().Be("12,345.68");
    }

    [Fact]
    public void ParseCodexResetCredits_實測回應_次數與最早到期時間()
    {
        UsageRules.ParseCodexResetCredits(JObject.Parse(RealSamples.CodexRateLimitsRead))
            .Should().Be(((int?)1, (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(1794007837)));
    }

    [Fact]
    public void ParseCodexResetCredits_沒有可用_沒有欄位_格式不對_明細只看還能用的()
    {
        (int? Count, DateTimeOffset? NextExpiresAt) Parse(string json) => UsageRules.ParseCodexResetCredits(JObject.Parse(json));

        // codex-cli 0.166.0 實測（2026-10-30，兩個帳戶都沒有）
        Parse("""{"rateLimitResetCredits":{"availableCount":0,"credits":[]}}""").Should().Be(((int?)0, (DateTimeOffset?)null));
        Parse("""{"rateLimits":{"primary":{"usedPercent":1}}}""").Should().Be(((int?)null, (DateTimeOffset?)null), "舊版 CLI、對話中的 rateLimits/updated");
        Parse("""{"rateLimitResetCredits":null}""").Should().Be(((int?)null, (DateTimeOffset?)null));
        Parse("""{"rateLimitResetCredits":{"availableCount":"2"}}""").Should().Be(((int?)null, (DateTimeOffset?)null));
        UsageRules.ParseCodexResetCredits(null).Should().Be(((int?)null, (DateTimeOffset?)null));
        Parse("""{"rateLimitResetCredits":{"availableCount":2,"credits":null}}""").Should().Be(((int?)2, (DateTimeOffset?)null), "只知道次數");
        Parse("""{"rateLimitResetCredits":{"availableCount":-3}}""").Count.Should().Be(0);
        Parse("""{"rateLimitResetCredits":{"availableCount":99999999999999999999999}}""").Count.Should().Be(UsageRules.MaxResetCredits);
        Parse("""
            {"rateLimitResetCredits":{"availableCount":3,"credits":[
              {"id":"a","status":"redeemed","grantedAt":1,"expiresAt":1791000000,"resetType":"codexRateLimits"},
              {"id":"b","status":"available","grantedAt":1,"expiresAt":null,"resetType":"codexRateLimits"},
              {"id":"c","status":"available","grantedAt":1,"expiresAt":1793000000,"resetType":"codexRateLimits"},
              {"id":"d","status":"available","grantedAt":1,"expiresAt":1792000000,"resetType":"codexRateLimits"},
              null]}}
            """).Should().Be(((int?)3, (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(1792000000)), "已兌換的不算、不會到期的略過");
    }

    [Fact]
    public void SameForView_免費重置次數不同算不一樣()
    {
        var a = new UsageSnapshot(DateTimeOffset.UnixEpoch, "api", [], null, null, null, false, 1, null);
        UsageRules.SameForView(a, a with { FetchedAt = DateTimeOffset.UnixEpoch.AddMinutes(1) }).Should().BeTrue();
        UsageRules.SameForView(a, a with { ResetCredits = 0 }).Should().BeFalse();
        UsageRules.SameForView(a, a with { ResetCreditExpiresAt = DateTimeOffset.UnixEpoch.AddDays(3) }).Should().BeFalse();
    }

    [Fact]
    public void ParseCodexRateLimits_多個額度桶_名稱加上LimitName()
    {
        var json = JObject.Parse("""
            {"rateLimitsByLimitId":{
              "codex":{"limitId":"codex","limitName":null,"primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":1791500000},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1791959804},"credits":{"hasCredits":false,"unlimited":false,"balance":"0"}},
              "codex_sol":{"limitId":"codex_sol","limitName":"GPT-6.1-Sol","primary":{"usedPercent":100,"windowDurationMins":10080,"resetsAt":1791959804},"secondary":null,"rateLimitReachedType":"primary","credits":{"hasCredits":true,"unlimited":false,"balance":"1234.5"}}
            }}
            """);

        var (windows, credits) = UsageRules.ParseCodexRateLimits(json);

        windows.Select(Show).Should().Equal(
            "codex:300|5 小時|20|False",
            "codex:10080|每週|40|False",
            "codex_sol:10080|每週・GPT-6.1-Sol|100|True");
        credits.Should().Be("1,234.5", "第一個有點數的桶");
    }

    [Fact]
    public void ParseCodexRateLimits_對話中的稀疏更新直接是快照()
    {
        var updated = JObject.Parse("""{"limitId":"codex","primary":{"usedPercent":1,"windowDurationMins":10080,"resetsAt":1791959804},"secondary":null}""");

        var (windows, credits) = UsageRules.ParseCodexRateLimits(updated);

        windows.Select(Show).Should().Equal("codex:10080|每週|1|False");
        credits.Should().BeNull();
    }

    [Fact]
    public void ParseCodexRateLimits_只有RateLimits_沒有時間窗長度_不限量點數不顯示()
    {
        var (windows, credits) = UsageRules.ParseCodexRateLimits(JObject.Parse("""{"rateLimits":{"primary":{"usedPercent":55.56},"secondary":{"windowDurationMins":300},"credits":{"hasCredits":true,"unlimited":true,"balance":"999"}}}"""));

        windows.Select(Show).Should().Equal("codex:primary|額度|55.6|False");
        windows[0].ResetsAt.Should().BeNull();
        credits.Should().BeNull("不限量時不顯示餘額");

        UsageRules.ParseCodexRateLimits(null).Windows.Should().BeEmpty();
        UsageRules.ParseCodexRateLimits(new JObject()).Windows.Should().BeEmpty();
    }

    [Fact]
    public void ParseCodexRateLimits_工作區點數用完時未滿100也算已達上限_並說明原因()
    {
        // codex-cli 0.160.1 產生的 schema：RateLimitReachedType 有 rate_limit_reached 與四種 workspace_*
        var json = JObject.Parse("""
            {"ordinaryUsageAllowed":false,
             "rateLimits":{"limitId":"codex","limitName":null,"primary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":1791959804},"secondary":null,
                           "credits":null,"rateLimitReachedType":"workspace_member_credits_depleted"}}
            """);

        var (windows, _) = UsageRules.ParseCodexRateLimits(json);

        windows.Select(Show).Should().Equal("codex:10080|每週|30|True");
        UsageRules.Severity(windows[0].Percent, windows[0].Locked).Should().Be("max", "已被擋，不能顯示成正常");
        UsageRules.DescribeCodexBlock(json).Should().Be("你在工作區的點數額度已用完");
    }

    [Fact]
    public void ParseCodexRateLimits_OrdinaryUsageAllowed為false時全部已達上限_null不代表被擋()
    {
        var blocked = JObject.Parse("""{"ordinaryUsageAllowed":false,"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":5,"windowDurationMins":10080}}}""");
        UsageRules.ParseCodexRateLimits(blocked).Windows.Should().OnlyContain(w => w.Locked);
        UsageRules.DescribeCodexBlock(blocked).Should().Be("Codex 回報目前不能使用方案內含的額度");

        var unknown = JObject.Parse("""{"ordinaryUsageAllowed":null,"rateLimits":{"limitId":"codex","primary":{"usedPercent":10,"windowDurationMins":300}}}""");
        UsageRules.ParseCodexRateLimits(unknown).Windows.Should().OnlyContain(w => !w.Locked);
        UsageRules.DescribeCodexBlock(unknown).Should().BeNull();
        UsageRules.DescribeCodexBlock(JObject.Parse(RealSamples.CodexRateLimitsRead)).Should().BeNull("實測回應沒有被擋");
    }

    [Fact]
    public void ParseCodexRateLimits_用量達上限時只鎖用滿的時間窗_多桶時說明哪個額度()
    {
        var json = JObject.Parse("""
            {"rateLimitsByLimitId":{
              "codex":{"limitId":"codex","primary":{"usedPercent":100,"windowDurationMins":300},"secondary":{"usedPercent":40,"windowDurationMins":10080},"rateLimitReachedType":"rate_limit_reached"},
              "codex_sol":{"limitId":"codex_sol","limitName":"GPT-6.1-Sol","primary":{"usedPercent":20,"windowDurationMins":10080},"rateLimitReachedType":"workspace_owner_usage_limit_reached"}
            }}
            """);

        UsageRules.ParseCodexRateLimits(json).Windows.Select(Show).Should().Equal(
            "codex:300|5 小時|100|True",
            "codex:10080|每週|40|False",
            "codex_sol:10080|每週・GPT-6.1-Sol|20|True");
        UsageRules.DescribeCodexBlock(json).Should().Be("已達用量上限；GPT-6.1-Sol：工作區已達用量上限");
    }

    [Fact]
    public void HasCodexRateLimits_有額度桶才看得懂_桶裡沒有時間窗也算()
    {
        UsageRules.HasCodexRateLimits(JObject.Parse(RealSamples.CodexRateLimitsRead)).Should().BeTrue();
        var noWindows = JObject.Parse("""{"rateLimits":{"limitId":"codex","primary":null,"secondary":null}}""");
        UsageRules.HasCodexRateLimits(noWindows).Should().BeTrue("沒有用量上限的方案");
        UsageRules.ParseCodexRateLimits(noWindows).Windows.Should().BeEmpty();
        UsageRules.HasCodexRateLimits(JObject.Parse("""{"limits":[]}""")).Should().BeFalse("看不懂的格式");
        UsageRules.HasCodexRateLimits(new JObject()).Should().BeFalse();
        UsageRules.HasCodexRateLimits(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(10080L, "每週")]
    [InlineData(1440L, "每日")]
    [InlineData(300L, "5 小時")]
    [InlineData(60L, "1 小時")]
    [InlineData(120L, "2 小時")]
    [InlineData(90L, "90 分鐘")]
    [InlineData(45L, "45 分鐘")]
    [InlineData(null, "額度")]
    public void DescribeWindow_時間窗長度轉中文(long? minutes, string expected)
        => UsageRules.DescribeWindow(minutes).Should().Be(expected);

    // ───── 合併、推算重置、排序 ─────

    [Fact]
    public void SameForView_畫面上看得到的都一樣才算同一份_取得時間不算()
    {
        var reset = DateTimeOffset.FromUnixTimeSeconds(1791436800);
        UsageSnapshot Snap(double pct = 10, string source = "session", bool stale = false, string? credits = null, string? note = null, string? error = null, DateTimeOffset? at = null) =>
            new(at ?? reset, source, new[] { new UsageWindow("session", "5 小時", pct, reset), new UsageWindow("weekly", "每週", 30, reset) }, credits, note, error, stale);

        UsageRules.SameForView(Snap(), Snap(at: reset.AddSeconds(5))).Should().BeTrue("對話中每個請求都回報一次、數字沒變：只差取得時間（只在過時時顯示）");
        UsageRules.SameForView(null, Snap()).Should().BeFalse("第一次有資料");
        UsageRules.SameForView(Snap(), Snap(pct: 11)).Should().BeFalse("使用率變了");
        UsageRules.SameForView(Snap(stale: true, source: "cache"), Snap()).Should().BeFalse("從過時的快取變回即時：標記要拿掉");
        UsageRules.SameForView(Snap(), Snap(credits: "12")).Should().BeFalse();
        UsageRules.SameForView(Snap(), Snap(note: "額度已用完")).Should().BeFalse();
        UsageRules.SameForView(Snap(error: "逾時"), Snap()).Should().BeFalse();
        var fewer = Snap() with { Windows = new[] { new UsageWindow("session", "5 小時", 10, reset) } };
        UsageRules.SameForView(Snap(), fewer).Should().BeFalse("時間窗少了一個");
    }

    [Fact]
    public void Merge_同Key取代_沒帶重置時間沿用舊值_新Key加入_其他保留()
    {
        var a = DateTimeOffset.FromUnixTimeSeconds(1791436800);
        var b = DateTimeOffset.FromUnixTimeSeconds(1792015200);
        var c = DateTimeOffset.FromUnixTimeSeconds(1791450000);
        var existing = new[]
        {
            new UsageWindow("session", "5 小時", 10, a),
            new UsageWindow("weekly", "每週", 30, b),
            new UsageWindow("weekly:Fable", "每週・Fable", 3, b),
        };
        var updates = new[]
        {
            new UsageWindow("weekly", "每週", 5, null),
            new UsageWindow("session", "5 小時", 15, c, Locked: true),
            new UsageWindow("codex:300", "5 小時", 1, null),
        };

        var merged = UsageRules.Merge(existing, updates);

        merged.Select(Show).Should().Equal("session|5 小時|15|True", "weekly|每週|5|False", "weekly:Fable|每週・Fable|3|False", "codex:300|5 小時|1|False");
        merged[0].ResetsAt.Should().Be(c, "更新有帶重置時間就用新的");
        merged[1].ResetsAt.Should().Be(b, "rate_limit_event 有時只給使用率");
        merged[2].ResetsAt.Should().Be(b);
        existing[1].Percent.Should().Be(30, "不能改到原本的清單");
    }

    [Fact]
    public void Merge_Codex多額度桶_對話中的單桶更新不能把名稱裡的LimitName去掉()
    {
        var read = UsageRules.ParseCodexRateLimits(JObject.Parse("""
            {"rateLimitsByLimitId":{
              "codex":{"limitId":"codex","limitName":null,"primary":{"usedPercent":20,"windowDurationMins":10080,"resetsAt":1791959804}},
              "codex_sol":{"limitId":"codex_sol","limitName":"GPT-6.1-Sol","primary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":1791959804}}
            }}
            """)).Windows;
        var update = UsageRules.ParseCodexRateLimits(JObject.Parse("""{"limitId":"codex_sol","limitName":"GPT-6.1-Sol","primary":{"usedPercent":80,"windowDurationMins":10080,"resetsAt":1791959804},"secondary":null}""")).Windows;

        var merged = UsageRules.Merge(read, update);

        merged.Single(w => w.Key == "codex_sol:10080").Percent.Should().Be(80);
        merged.Single(w => w.Key == "codex_sol:10080").Label.Should().Be("每週・GPT-6.1-Sol");
        merged.Select(w => w.Label).Should().OnlyHaveUniqueItems("畫面上兩個圓環不能同名");
    }

    [Fact]
    public void Merge_沒有舊資料時就是更新本身()
    {
        var updates = new[] { new UsageWindow("weekly", "每週", 5, null), new UsageWindow("session", "5 小時", 15, null) };
        UsageRules.Merge(null, updates).Select(w => w.Key).Should().Equal("session", "weekly");
        UsageRules.Merge(updates, Array.Empty<UsageWindow>()).Should().HaveCount(2);
    }

    [Fact]
    public void ApplyResets_重置時間已過的歸零並解除鎖定()
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var windows = new[]
        {
            new UsageWindow("session", "5 小時", 100, now.AddMinutes(-1), Locked: true),
            new UsageWindow("weekly", "每週", 30, now.AddHours(1)),
            new UsageWindow("weekly:Fable", "每週・Fable", 50, null),
            new UsageWindow("codex:300", "5 小時", 70, now),
        };

        var result = UsageRules.ApplyResets(windows, now);

        result[0].Should().Be(new UsageWindow("session", "5 小時", 0, null, false));
        result[1].Should().Be(windows[1]);
        result[2].Should().Be(windows[2], "沒有重置時間的無法推算，維持原值");
        result[3].Percent.Should().Be(0, "剛好到重置時間也算已重置");
    }

    [Fact]
    public void ApplyDueResets_共享的用量到了重置時間_推算歸零並標成過時的快取()
    {
        var now = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.Zero);
        var shared = new UsageSnapshot(now.AddHours(-8), "shared", new[]
        {
            new UsageWindow("session", "5 小時", 0, null),
            new UsageWindow("weekly", "每週", 100, now.AddMinutes(-1), Locked: true),
        }, null, null, null, false);

        var result = UsageRules.ApplyDueResets(shared, now);

        result.Should().NotBeNull();
        result!.Windows[1].Should().Be(new UsageWindow("weekly", "每週", 0, null, false));
        result.Windows[0].Should().Be(shared.Windows[0]);
        result.Stale.Should().BeTrue("推算的值不能當成查到的（也就不會上傳）");
        result.Source.Should().Be("cache");
        result.FetchedAt.Should().Be(shared.FetchedAt);
        UsageRules.ApplyDueResets(result, now).Should().BeNull("已推算過、沒有到期的時間窗就不必再換");
        UsageRules.ApplyDueResets(shared, now.AddMinutes(-2)).Should().BeNull("還沒到重置時間");
        UsageRules.ApplyDueResets(null, now).Should().BeNull();
    }

    [Fact]
    public void Order_5小時每週優先_再來依模型_其他依時間窗長度()
    {
        var keys = new[] { "codex:10080", "weekly:Opus", "other", "session", "weekly", "weekly:Fable", "codex:300" };

        UsageRules.Order(keys.Select(k => new UsageWindow(k, k, 0, null))).Select(w => w.Key)
            .Should().Equal("session", "weekly", "weekly:Fable", "weekly:Opus", "other", "codex:300", "codex:10080");
    }

    // ───── 嚴重度與顯示 ─────

    [Theory]
    [InlineData(0d, false, "ok")]
    [InlineData(49.9d, false, "ok")]
    [InlineData(50d, false, "warn")]
    [InlineData(79.9d, false, "warn")]
    [InlineData(80d, false, "high")]
    [InlineData(94.9d, false, "high")]
    [InlineData(95d, false, "max")]
    [InlineData(100d, false, "max")]
    [InlineData(10d, true, "max")]
    public void Severity_四個級距邊界(double percent, bool locked, string expected)
        => UsageRules.Severity(percent, locked).Should().Be(expected);

    [Fact]
    public void DescribeReset_重置倒數()
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        UsageRules.DescribeReset(null, now).Should().BeEmpty();
        UsageRules.DescribeReset(now.AddMinutes(-5), now).Should().Be("已重置");
        UsageRules.DescribeReset(now, now).Should().Be("已重置");
        UsageRules.DescribeReset(now.AddSeconds(20), now).Should().Be("1 分後重置", "不到一分鐘也顯示 1 分");
        UsageRules.DescribeReset(now.AddMinutes(42).AddSeconds(10), now).Should().Be("43 分後重置", "無條件進位");
        UsageRules.DescribeReset(now.AddHours(1), now).Should().Be("1 小時 0 分後重置");
        UsageRules.DescribeReset(now.AddHours(3).AddMinutes(12), now).Should().Be("3 小時 12 分後重置");
        UsageRules.DescribeReset(now.AddDays(1).AddHours(2), now).Should().Be("1 天 2 小時後重置");
        UsageRules.DescribeReset(now.AddDays(6).AddHours(23).AddMinutes(59), now).Should().Be("6 天 23 小時後重置");
    }
}
