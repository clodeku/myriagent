using Myriagent.Update;

namespace Myriagent.Tests.Protocol.Update;

/// <summary>版號前面的日期時間：建置時寫進組件中繼資料 MyriagentCommitTime 的提交時間（Directory.Build.targets）</summary>
public sealed class CommitTimeRulesTests
{
    [Fact]
    public void 收git的cI格式_帶時區或Z()
    {
        CommitTimeRules.Parse("2026-10-23T14:05:12+08:00").Should().Be(new DateTimeOffset(2026, 10, 23, 14, 5, 12, TimeSpan.FromHours(8)));
        CommitTimeRules.Parse("2026-10-23T06:05:12Z").Should().Be(new DateTimeOffset(2026, 10, 23, 6, 5, 12, TimeSpan.Zero));
        CommitTimeRules.Parse(" 2026-10-23T14:05:12+08:00\r\n").Should().NotBeNull("Exec 的輸出前後可能有空白與換行");
        CommitTimeRules.ToUnixMs(CommitTimeRules.Parse("2026-10-22T23:05:12-07:00"))
            .Should().Be(new DateTimeOffset(2026, 10, 23, 6, 5, 12, TimeSpan.Zero).ToUnixTimeMilliseconds(), "不同時區的同一刻是同一個毫秒數");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-23T14:05:12")]
    [InlineData("2026-10-23 14:05:12 +0800")]
    [InlineData("2026-13-23T14:05:12+08:00")]
    [InlineData("cI")]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git")]
    public void 其他寫法一律null_沒有時區的不當成這台的當地時間(string? text)
        => CommitTimeRules.Parse(text).Should().BeNull();

    [Fact]
    public void 給網頁的毫秒()
    {
        CommitTimeRules.ToUnixMs(null).Should().BeNull();
        CommitTimeRules.ToUnixMs(DateTimeOffset.FromUnixTimeMilliseconds(1792732512000)).Should().Be(1792732512000);
    }

    [Fact]
    public void 在repo裡建置的桌面組件帶提交時間()
    {
        // Directory.Build.targets 的 MyriagentStampCommitTime：建置時以 git 讀 HEAD 的提交時間（測試一律在 repo 裡建置）
        AppVersionInfo.CommitTime.Should().NotBeNull();
        AppVersionInfo.CommitTime!.Value.Year.Should().BeGreaterThanOrEqualTo(2026);
        AppVersionInfo.CommitAtMs.Should().Be(AppVersionInfo.CommitTime.Value.ToUnixTimeMilliseconds());
    }
}
