using Myriagent.Agents;
using Myriagent.Usage;

namespace Myriagent.Tests.Usage;

/// <summary>側欄 token 計數的規則：增量、一輪結束的補記、Codex 的 total 差與續接的基準、日期鍵與保留天數</summary>
public class TokenTallyRulesTests
{
    private static AgentUsage U(long input, long output, long cached) => new(input, output, cached, null);

    [Fact]
    public void Increment_逐欄相減_負的當0_沒有上一份就是整份()
    {
        TokenTallyRules.Increment(null, U(100, 5, 40)).Should().Be(U(100, 5, 40));
        TokenTallyRules.Increment(U(100, 5, 40), U(100, 273, 40)).Should().Be(U(0, 268, 0), "message_delta 只多了輸出");
        TokenTallyRules.Increment(U(100, 5, 40), U(90, 5, 50)).Should().Be(U(0, 0, 10), "累計值變小不倒扣");
        TokenTallyRules.Increment(U(1, 2, 3), U(1, 2, 3)).Should().Match<AgentUsage>(u => TokenTallyRules.IsZero(u), "同一份再回報一次是 0");
    }

    [Fact]
    public void TopUp_只補這一輪的用量比已記的多出的部分()
    {
        TokenTallyRules.TopUp(null, U(10, 1, 0)).Should().Be(TokenTallyRules.Zero, "沒有這一輪的用量就不補");
        TokenTallyRules.TopUp(U(98_359, 880, 63_111), U(98_359, 880, 63_111)).Should().Match<AgentUsage>(u => TokenTallyRules.IsZero(u), "即時記到的已經是全部");
        TokenTallyRules.TopUp(U(1_200, 30, 0), null).Should().Be(U(1_200, 30, 0), "Grok 只在結束時有用量，整份補上");
        TokenTallyRules.TopUp(U(500, 30, 100), U(800, 10, 100)).Should().Be(U(0, 20, 0), "輸入已經記多了（含子代理）不倒扣，輸出補差額");
    }

    [Fact]
    public void Add與Total_輸入含快取_總數是輸入加輸出()
    {
        var sum = TokenTallyRules.Add(U(46_873, 6, 16_240), U(51_486, 4, 46_871));
        sum.Should().Be(U(98_359, 10, 63_111));
        TokenTallyRules.Total(sum).Should().Be(98_369);
        TokenTallyRules.Total(null).Should().Be(0);
        TokenTallyRules.Add(null, null).Should().Be(TokenTallyRules.Zero);
    }

    [Fact]
    public void CodexIncrement_有上一則取total的差_第一次看到只有這一輪的才記last()
    {
        // 實測（codex app-server、一輪兩次 API 呼叫）：total 是整個 thread 的累計、last 只是最後一次呼叫
        TokenTallyRules.CodexIncrement(null, U(14_200, 34, 0), U(14_200, 34, 0), countFirst: true).Should().Be(U(14_200, 34, 0));
        TokenTallyRules.CodexIncrement(U(14_200, 34, 0), U(28_457, 39, 14_080), U(14_257, 5, 14_080), countFirst: true).Should().Be(U(14_257, 5, 14_080));
        TokenTallyRules.CodexIncrement(U(28_457, 39, 14_080), U(28_457, 39, 14_080), U(14_257, 5, 14_080), countFirst: true)
            .Should().Match<AgentUsage>(u => TokenTallyRules.IsZero(u), "同一份再送一次不重複記");

        // thread/resume 一接上送的恢復累計（上一輪的 turnId）：只當基準
        TokenTallyRules.CodexIncrement(null, U(42_732, 44, 14_080), U(14_275, 5, 0), countFirst: false).Should().Be(TokenTallyRules.Zero);

        // 沒有 total 的舊格式：屬於這一輪才記 last
        TokenTallyRules.CodexIncrement(null, null, U(10, 7, 0), countFirst: true).Should().Be(U(10, 7, 0));
        TokenTallyRules.CodexIncrement(null, null, U(10, 7, 0), countFirst: false).Should().Be(TokenTallyRules.Zero);
        TokenTallyRules.CodexIncrement(null, null, null, countFirst: true).Should().Be(TokenTallyRules.Zero);
    }

    [Fact]
    public void 日期鍵與保留天數_今天往回30天_比今天晚的不刪()
    {
        var today = new DateTime(2026, 10, 22, 23, 59, 0);
        TokenTallyRules.DayKey(today).Should().Be("2026-10-22");
        TokenTallyRules.DayKeys(today, 3).Should().Equal("2026-10-22", "2026-10-21", "2026-10-20");
        TokenTallyRules.DayKeys(new DateTime(2026, 3, 1), 2).Should().Equal("2026-03-01", "2026-02-28");

        var keys = new[] { "2026-10-22", "2026-09-23", "2026-09-22", "2025-12-31", "2026-11-05" };
        TokenTallyRules.ExpiredDays(keys, today).Should().BeEquivalentTo("2026-09-22", "2025-12-31");
    }
}
