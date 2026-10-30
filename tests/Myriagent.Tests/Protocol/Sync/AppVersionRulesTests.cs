using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>app 版本與伺服器 minAppVersion 的比較（sync-protocol 1.4）</summary>
public sealed class AppVersionRulesTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0", true)]
    [InlineData("0.3.0", "0.1.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.1", "0.1.0", true)]
    [InlineData("1.0.0.5", "1.0.0", true)]
    [InlineData("0.3.0-beta.1", "0.3.0", true)]
    [InlineData("0.3.0+abc123", "0.3.0", true)]
    [InlineData("0.0.9", "0.1.0", false)]
    [InlineData("0.2.9", "0.3.0", false)]
    public void 版本比較_少的段補0_預覽版與建置資訊忽略(string app, string min, bool expected) =>
        AppVersionRules.IsAtLeast(app, min).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("9999999999.0.0")]
    public void 格式不對一律不能連線(string? app) => AppVersionRules.IsAtLeast(app, "0.0.1").Should().BeFalse();
}
