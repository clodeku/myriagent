using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class StartupOptionsTests
{
    private static readonly string Token = new('a', 64);

    [Fact]
    public void 一般啟動與資料夾()
    {
        StartupOptions.Parse(Array.Empty<string>()).Should().Be(new StartupOptions());
        StartupOptions.Parse(new[] { "--data", @"C:\tmp\x" }).DataRoot.Should().Be(@"C:\tmp\x");
    }

    [Fact]
    public void 換版幫手的參數()
    {
        var o = StartupOptions.Parse(new[] { "--apply-update", "--to", "0.2.2-bbbbbb2", "--wait-pid", "1234", "--token", Token, "--no-relaunch" });
        o.ApplyUpdate.Should().BeTrue();
        o.To.Should().Be("0.2.2-bbbbbb2");
        o.WaitPid.Should().Be(1234);
        o.Token.Should().Be(Token);
        o.NoRelaunch.Should().BeTrue();
        o.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("--to", "not-a-release", "--wait-pid", "1", "--token", "x")]
    [InlineData("--to", "0.2.2-bbbbbb2", "--wait-pid", "abc", "--token", "x")]
    [InlineData("--to", "0.2.2-bbbbbb2", "--wait-pid", "1", "--token", "短")]
    [InlineData("--to", "0.2.2-bbbbbb2", "--token", "x")]
    public void 換版幫手的參數不完整_有錯誤(params string[] rest)
    {
        var args = new[] { "--apply-update" }.Concat(rest.Select(a => a == "x" ? Token : a)).ToArray();
        StartupOptions.Parse(args).Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 換版後啟動與退回後啟動()
    {
        StartupOptions.Parse(new[] { "--after-update", Token }).AfterUpdateToken.Should().Be(Token);
        StartupOptions.Parse(new[] { "--after-update-failed", Token }).AfterUpdateFailedToken.Should().Be(Token);
        StartupOptions.Parse(new[] { "--after-update", "壞的" }).AfterUpdateToken.Should().BeNull("token 格式不對就當一般啟動");
    }

    [Fact]
    public void 開機時自動啟動()
    {
        StartupOptions.Parse(new[] { "--autostart" }).AutoStart.Should().BeTrue();
        StartupOptions.Parse(Array.Empty<string>()).AutoStart.Should().BeFalse();
        StartupOptions.Parse(new[] { "--data", "--autostart" }).AutoStart.Should().BeTrue("旗標不看位置");
    }

    [Fact]
    public void 新token是64個小寫十六進位字()
    {
        var t = StartupOptions.NewToken();
        StartupOptions.IsValidToken(t).Should().BeTrue();
        StartupOptions.NewToken().Should().NotBe(t);
    }
}
