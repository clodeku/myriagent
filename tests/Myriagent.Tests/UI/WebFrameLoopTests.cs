using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.UI;

/// <summary>
/// 隔一段時間才換畫面的 JS 動畫（2026-10-24）：token 數字與標題的解碼每 45 ms 才換一次字，以前各自 requestAnimationFrame、每個影格都要求重畫，
/// 只有數字在跳時主執行緒照樣每秒 60 幀（每一幀都要更新所有動畫元素的樣式、圖層化、繪製）；改經 fxclock.js 的 wakeAt，只在要換字的那一幀要影格
/// </summary>
public class WebFrameLoopTests
{
    private static string WebFile(string name) => File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web", name));

    [Theory]
    [InlineData("tokenfx.js")]
    [InlineData("textfx.js")]
    public void 解碼動畫經fxclock要影格_不自己每幀requestAnimationFrame(string file)
    {
        var source = WebFile(file);
        source.Should().Contain("from './fxclock.js'", $"{file} 的解碼只在要換字的那一幀要影格");
        source.Should().Contain("wakeAt(", $"{file} 以 wakeAt 排下一次換字");
        source.Should().NotContain("requestAnimationFrame", $"{file} 不自己每個影格都要求重畫（只有每 45 ms 才換字）");
    }

    [Fact]
    public void fxclock_js在時間到了才要一個影格()
    {
        var source = WebFile("fxclock.js");
        source.Should().Contain("setTimeout(", "等到該換的時間才要影格");
        source.Should().Contain("requestAnimationFrame(frame)", "換字落在影格上（和以前一樣是到點之後的第一個影格）");
        source.Should().NotContain("import ", "節拍不依賴其他模組");
    }
}
