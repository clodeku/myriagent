using System.Text.RegularExpressions;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.UI;

/// <summary>
/// 網頁的影音元素（2026-10-24）：有控制列的 &lt;video&gt;／&lt;audio&gt; 只能經 framedvideo.js 建在 iframe 的文件裡。
/// Chrome 內建控制列的載入轉圈會對所在的文件註冊 animationiteration 監聽，文件的這個旗標到文件結束都不清，
/// 從此每個 CSS 動畫每轉一圈都叫醒主執行緒、整頁的持續動畫跟著重算（tools/ui-probe 的 perfNoAnimationEvents 量執行結果）
/// </summary>
public class WebMediaElementTests
{
    private static readonly string WebDir = Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web");

    /// <summary>在這份文件建立影音元素的寫法：h('video'…)、createElement('audio')、new Audio(、HTML 字串裡的標籤</summary>
    private static readonly Regex MediaElement = new(
        @"h\(\s*['""`](video|audio)['""`]|document\.createElement\(\s*['""`](video|audio)['""`]|new\s+Audio\s*\(|<(video|audio)[\s>]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void 只有framedvideo_js建立影音元素()
    {
        var files = Directory.GetFiles(WebDir, "*.js").Where(f => Path.GetFileName(f) != "framedvideo.js").ToList();
        files.Should().HaveCountGreaterThan(10, "要讀得到 Web/ 的模組");
        foreach (var file in files)
            MediaElement.Matches(File.ReadAllText(file)).Select(m => m.Value).Should().BeEmpty(
                $"{Path.GetFileName(file)} 的影片要經 framedvideo.js 放進 iframe（控制列的載入轉圈會讓主文件的動畫每轉一圈都叫醒主執行緒）");
        MediaElement.IsMatch(File.ReadAllText(Path.Combine(WebDir, "index.html"))).Should().BeFalse("index.html 不放影音元素");
    }

    [Fact]
    public void framedvideo_js在iframe的文件建立影片()
    {
        var source = File.ReadAllText(Path.Combine(WebDir, "framedvideo.js"));
        source.Should().Contain("h('iframe'", "影片放在 iframe 裡");
        source.Should().Contain("el.contentDocument", "影片建在 iframe 的文件");
        source.Should().Contain("doc.createElement('video')", "影片要用 iframe 文件的 createElement 建立，旗標才會設在 iframe 的文件");
        MediaElement.IsMatch(source).Should().BeFalse("framedvideo.js 也不在主文件建立影音元素");
    }
}
