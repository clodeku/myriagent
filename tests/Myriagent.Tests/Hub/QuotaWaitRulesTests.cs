using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>額度全用完時等重置後自動接著做（QuotaWaitRules）：要不要等、等到什麼時候、提示與接續訊息</summary>
public sealed class QuotaWaitRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 重置時間在12小時內_等到重置後多一分鐘()
    {
        QuotaWaitRules.Plan(Now.AddHours(3), Now, attempts: 0).Should().Be(Now.AddHours(3) + QuotaWaitRules.Grace);
        QuotaWaitRules.Plan(Now.AddHours(12), Now, attempts: 0).Should().Be(Now.AddHours(12) + QuotaWaitRules.Grace, "剛好 12 小時也等");
    }

    [Fact]
    public void 重置時間已過_一分鐘後接著做()
        => QuotaWaitRules.Plan(Now.AddMinutes(-5), Now, attempts: 0).Should().Be(Now + QuotaWaitRules.Grace);

    [Fact]
    public void 不等_時間不明_太久_重來太多次()
    {
        QuotaWaitRules.Plan(null, Now, 0).Should().BeNull();
        QuotaWaitRules.Plan(Now.AddHours(12).AddMinutes(1), Now, 0).Should().BeNull("每週額度這種要等好幾天的不等");
        QuotaWaitRules.Plan(Now.AddHours(1), Now, QuotaWaitRules.MaxAttempts).Should().BeNull();
        QuotaWaitRules.Plan(Now.AddHours(1), Now, QuotaWaitRules.MaxAttempts - 1).Should().NotBeNull();
    }

    [Fact]
    public void 提示寫出帳戶與重置時間()
    {
        var until = new DateTimeOffset(2026, 10, 26, 14, 5, 0, TimeSpan.FromHours(8));
        var when = QuotaWaitRules.When(until, Now);
        QuotaWaitRules.ScheduledNotice("工作用 Max", until, Now).Should().StartWith($"額度用完了，也沒有能接手的帳戶：等「工作用 Max」在 {when} 重置後自動接著做");
        QuotaWaitRules.ScheduledNotice(null, until, Now).Should().Contain("等這個帳戶在");
        QuotaWaitRules.ResumedNotice("工作用 Max").Should().Be("「工作用 Max」的額度已重置，自動接著做");
    }

    [Fact]
    public void 重置時間_同一天只寫時分_其他天加月日()
    {
        var local = Now.ToLocalTime();
        QuotaWaitRules.When(local.AddMinutes(30), local).Should().MatchRegex(@"^\d{2}:\d{2}$");
        QuotaWaitRules.When(local.Date.AddDays(1).AddHours(3), local).Should().MatchRegex(@"^\d{1,2}/\d{1,2} \d{2}:\d{2}$");
    }

    [Fact]
    public void 接續訊息_列出做過的步驟_沒有步驟也照送()
    {
        var text = QuotaWaitRules.BuildContinuation(["讀 a.cs", " ", "改 b.cs"]);
        text.Should().Contain("額度已重置").And.Contain("〈讀 a.cs〉、〈改 b.cs〉").And.Contain("已完成的步驟不要重做");
        QuotaWaitRules.BuildContinuation([]).Should().NotContain("已經做過的步驟").And.Contain("請繼續原本的工作");
    }
}
