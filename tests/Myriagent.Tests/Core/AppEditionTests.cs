using Myriagent.Core;
using Myriagent.Tests.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Core;

/// <summary>組建的版本（單機版的安裝檔以 -p:AppEdition=standalone 建置）：中繼資料的解析、一般的建置不是單機版、init 把它交給網頁</summary>
public sealed class AppEditionTests
{
    [Theory]
    [InlineData("standalone", "standalone")]
    [InlineData(" Standalone ", "standalone")]
    [InlineData("STANDALONE", "standalone")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("pro", null)]
    public void 中繼資料_只認得單機版_其他一律當成一般的建置(string? value, string? expected)
        => AppEdition.Parse(value).Should().Be(expected);

    /// <summary>一般的建置（測試、開發、自己的發佈）沒有帶 -p:AppEdition：不能因為誤設而悄悄變成不同步的單機版</summary>
    [Fact]
    public void 一般的建置_不是單機版()
    {
        AppEdition.Name.Should().BeNull();
        AppEdition.IsStandalone.Should().BeFalse();
    }

    [Fact]
    public void init_帶著組建的版本給網頁_一般的建置是null()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready");
        var first = h.Main.Last("init")!;
        first.ContainsKey("edition").Should().BeTrue();
        first.Value<string>("edition").Should().BeNull();

        h.Ui.Invoke(() => h.Hub.Edition = AppEdition.Standalone);
        var reload = h.Main.Mark();
        h.Command(h.Main, "ready");
        h.Main.Messages("init", reload).Should().ContainSingle().Subject.Value<string>("edition").Should().Be("standalone");
    }
}
