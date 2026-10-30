using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>中樞的靜態小工具：時間轉 Unix 毫秒、媒體白名單用的路徑正規化</summary>
public class AgentHubStaticTests
{
    [Fact]
    public void ToUnixMs_本機時間轉毫秒_轉換不了的值回Null不丟例外()
    {
        var local = new DateTime(2026, 10, 10, 8, 30, 0, DateTimeKind.Local);
        AgentHub.ToUnixMs(local).Should().Be(new DateTimeOffset(local).ToUnixTimeMilliseconds());
        AgentHub.ToUnixMs(null).Should().BeNull();
        AgentHub.ToUnixMs(DateTime.MinValue).Should().BeNull("壞檔裡的 0001-01-01");
        AgentHub.ToUnixMs(DateTime.MaxValue).Should().BeNull();
        AgentHub.ToUnixMs(new DateTime(1970, 6, 1)).Should().BeNull("1971 年以前視為無效");
    }

    [Theory]
    [InlineData(@"C:\out\chart.png", @"C:\out\chart.png")]
    [InlineData("C:/out/chart.png", @"C:\out\chart.png")]
    [InlineData(@"C:\out\sub\..\chart.png", @"C:\out\chart.png")]
    [InlineData(@"\\evil\share\chart.png", null)]
    [InlineData("//evil/share/chart.png", null)]
    [InlineData("relative/chart.png", null)]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void NormalizePath_只認本機磁碟路徑並正規化(string? path, string? expected)
        => AgentHub.NormalizePath(path).Should().Be(expected);
}
