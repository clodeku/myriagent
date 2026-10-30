using Myriagent.Update;

namespace Myriagent.Tests.Protocol.Update;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.2.1523", 0, 2, 1523)]
    [InlineData(" 10.0.0 ", 10, 0, 0)]
    [InlineData("1.20.3", 1, 20, 3)]
    public void 三段數字_解析成功(string text, int major, int minor, int patch)
    {
        ReleaseVersion.TryParse(text, out var v).Should().BeTrue();
        v.Should().Be(new ReleaseVersion(major, minor, patch));
        v.ToString().Should().Be($"{major}.{minor}.{patch}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.2")]
    [InlineData("0.2.1.4")]
    [InlineData("0.2.x")]
    [InlineData("0.02.1")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("1234567890.0.0")]
    [InlineData("１.2.3")]
    public void 不是正好三段十進位數字_拒絕(string? text)
        => ReleaseVersion.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void 比較依主次修訂_數字大小而非字串()
    {
        ReleaseVersion.TryParse("0.2.999", out var a).Should().BeTrue();
        ReleaseVersion.TryParse("0.2.999", out var a2).Should().BeTrue();
        ReleaseVersion.TryParse("0.2.1000", out var b).Should().BeTrue();
        ReleaseVersion.TryParse("0.3.0", out var c).Should().BeTrue();
        (a < b).Should().BeTrue();
        (b < c).Should().BeTrue();
        (c > a).Should().BeTrue();
        (a <= a2).Should().BeTrue();
        (a >= a2).Should().BeTrue();
        a.CompareTo(a2).Should().Be(0);
    }

    [Theory]
    [InlineData("0.2.1523-ab12cd3", "0.2.1523", "ab12cd3")]
    [InlineData("10.0.1-0000000", "10.0.1", "0000000")]
    public void release名稱_解析版本與build(string text, string version, string build)
    {
        ReleaseIdentity.TryParseRelease(text, out var id).Should().BeTrue();
        id!.Version.ToString().Should().Be(version);
        id.Build.Should().Be(build);
        id.Release.Should().Be(text);
        id.IsDev.Should().BeFalse();
    }

    [Theory]
    [InlineData("0.2.1523")]
    [InlineData("0.2.1523-AB12CD3")]
    [InlineData("0.2.1523-ab12cd")]
    [InlineData("0.2.1523-ab12cd3x")]
    [InlineData("0.2.1523-dev")]
    [InlineData("0.2-ab12cd3")]
    [InlineData(" 0.2.1523-ab12cd3")]
    [InlineData("0.2.1523 -ab12cd3")]
    public void release名稱格式不對_拒絕(string text) => ReleaseIdentity.TryParseRelease(text, out _).Should().BeFalse();

    [Fact]
    public void InformationalVersion帶完整雜湊_build取前7碼小寫()
    {
        var id = ReleaseIdentity.FromInformationalVersion("0.2.1523+AB12CD34ef5678901234567890abcdef12345678", new Version(0, 2, 1523, 0));
        id.Version.ToString().Should().Be("0.2.1523");
        id.Build.Should().Be("ab12cd3");
        id.Release.Should().Be("0.2.1523-ab12cd3");
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.2.0+")]
    [InlineData("0.2.0+xyz")]
    [InlineData(null)]
    public void 沒有可用的雜湊_build是dev(string? info)
    {
        var id = ReleaseIdentity.FromInformationalVersion(info, new Version(0, 2, 0, 0));
        id.Build.Should().Be(ReleaseIdentity.DevBuild);
        id.IsDev.Should().BeTrue();
        id.Version.ToString().Should().Be("0.2.0");
    }

    [Fact]
    public void 版本解析不了_改用組件版本的前三段()
        => ReleaseIdentity.FromInformationalVersion("garbage+ab12cd34", new Version(0, 2, 7, 0)).Version.ToString().Should().Be("0.2.7");

    [Fact]
    public void 目前組件的版本_主次是0點2()
    {
        AppVersionInfo.Current.Version.Major.Should().Be(0);
        AppVersionInfo.Current.Version.Minor.Should().Be(2);
        AppVersionInfo.Display.Should().StartWith(AppVersionInfo.VersionText + "（");
    }
}
