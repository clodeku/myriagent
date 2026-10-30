using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class RuntimeRulesTests
{
    private static ReleaseVersion V(string text)
    {
        ReleaseVersion.TryParse(text, out var v).Should().BeTrue();
        return v;
    }

    [Theory]
    [InlineData(new[] { "8.0.11", "10.0.2" }, "10.0.0", true)]
    [InlineData(new[] { "10.0.0" }, "10.0.0", true)]
    [InlineData(new[] { "9.0.5" }, "10.0.0", false)]
    [InlineData(new[] { "10.0.0-rc.2.25502.107" }, "10.0.0", false)]
    [InlineData(new[] { "10.1.0-preview.1" }, "10.0.0", true)]
    [InlineData(new string[0], "10.0.0", false)]
    public void 已安裝的版本有一個夠新就好_預覽版只算比它小的正式版(string[] installed, string required, bool expected)
        => RuntimeRules.Satisfies(installed, V(required)).Should().Be(expected);

    [Fact]
    public void 執行中的程序載入的框架資料夾被移除_回那一版_都在或沒有路徑回null()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\dotnet\shared\Microsoft.NETCore.App\10.0.14" };
        RuntimeRules.RemovedRuntime([@"C:\dotnet\shared\Microsoft.NETCore.App\10.0.14\"], present.Contains).Should().BeNull("結尾的分隔字元不影響");
        RuntimeRules.RemovedRuntime([@"C:\dotnet\shared\Microsoft.NETCore.App\10.0.14", @"C:\dotnet\shared\Microsoft.WindowsDesktop.App\10.0.13"], present.Contains)
            .Should().Be("10.0.13");
        RuntimeRules.RemovedRuntime([@"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13\"], present.Contains).Should().Be("10.0.13");
        RuntimeRules.RemovedRuntime([null, "", "  "], _ => false).Should().BeNull("單一檔案發佈沒有組件路徑：不算");
        RuntimeRules.RemovedDetail("10.0.13").Should().Be("這台的 .NET 已更新，app 啟動時用的 10.0.13 已被移除，要重新啟動 app 才能安裝（閒置時會自動重新啟動）");
    }
}
