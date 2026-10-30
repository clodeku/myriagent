using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 桌面的媒體網址與 /media、/thumb 白名單（LocalMediaUrls，共用渲染器的 IMediaUrlBuilder 桌面實作）：
/// 網址與抽出前相同、登記與正規化、Markdown 圖片只放行可顯示的媒體、UNC 一律不放行、撤銷、各兩萬筆的上限
/// </summary>
public class LocalMediaUrlsTests
{
    private static string Numbered(int i) => $@"C:\files\{i}.png";

    [Fact]
    public void 媒體網址_用正規化後的路徑_同時登記媒體白名單()
    {
        var urls = new LocalMediaUrls();

        urls.MediaUrl("C:/repo/a/../out/chart.png").Should().Be(AgentMarkdownRenderer.MediaUrl(@"C:\repo\out\chart.png"));

        urls.IsMediaAllowed(@"C:\repo\out\chart.png").Should().BeTrue();
        urls.IsMediaAllowed("c:/REPO/out/chart.png").Should().BeTrue("不分大小寫、正斜線視為同一個檔案");
        urls.IsThumbAllowed(@"C:\repo\out\chart.png").Should().BeTrue("可以顯示的媒體也可以產生縮圖");
        urls.IsMediaAllowed(@"C:\repo\out\other.png").Should().BeFalse();
    }

    [Fact]
    public void 縮圖網址_帶版本_只登記縮圖白名單()
    {
        var urls = new LocalMediaUrls();

        urls.ThumbUrl(@"C:\repo\a.png", 240, "638000-42").Should().Be(AgentMarkdownRenderer.ThumbUrl(@"C:\repo\a.png", 240, "638000-42"));
        urls.ThumbUrl("C:/repo/b.mp4", 320).Should().Be(AgentMarkdownRenderer.ThumbUrl(@"C:\repo\b.mp4", 320));

        urls.IsThumbAllowed(@"C:\repo\a.png").Should().BeTrue();
        urls.IsMediaAllowed(@"C:\repo\a.png").Should().BeFalse("只登記縮圖的檔案不能經 /media 讀整個檔");
    }

    [Fact]
    public void Markdown圖片與連結_產生網址不登記_轉換成功後只放行可顯示的媒體()
    {
        var urls = new LocalMediaUrls();

        urls.MarkdownImageUrl(@"C:\repo\out\chart.png").Should().Be(AgentMarkdownRenderer.MediaUrl(@"C:\repo\out\chart.png"));
        urls.MarkdownLinkUrl(@"C:\repo\r.xlsx").Should().Be(AgentMarkdownRenderer.OpenUrl(@"C:\repo\r.xlsx"));
        urls.IsMediaAllowed(@"C:\repo\out\chart.png").Should().BeFalse("產生網址不等於登記");

        urls.AllowMarkdownImage(@"C:\repo\out\chart.png");
        urls.AllowMarkdownImage("C:/repo/out/demo.mp4");
        urls.AllowMarkdownImage(@"C:\repo\docs\報告.pdf");
        urls.AllowMarkdownImage(@"\\evil\share\a.png");

        urls.IsMediaAllowed(@"C:\repo\out\chart.png").Should().BeTrue();
        urls.IsMediaAllowed(@"C:\repo\out\demo.mp4").Should().BeTrue();
        urls.IsMediaAllowed(@"C:\repo\docs\報告.pdf").Should().BeFalse("非媒體檔不登記，/thumb 才不會替任意檔案產生外殼縮圖");
        urls.IsThumbAllowed(@"C:\repo\docs\報告.pdf").Should().BeFalse();
        urls.IsMediaAllowed(@"\\evil\share\a.png").Should().BeFalse();
    }

    [Fact]
    public void Markdown檔案連結_登記另存白名單_不放行媒體端點()
    {
        var urls = new LocalMediaUrls();
        urls.MediaUrl(@"C:\repo\out\preview.jpg");

        urls.MarkdownLinkUrl("C:/repo/out/../out/簡報 37頁.pptx").Should().Be(AgentMarkdownRenderer.OpenUrl("C:/repo/out/../out/簡報 37頁.pptx"));
        urls.MarkdownLinkUrl(@"\\evil\share\a.pptx");

        urls.IsSaveAllowed(@"c:\REPO\out\簡報 37頁.pptx").Should().BeTrue("正規化後不分大小寫");
        urls.IsSaveAllowed(@"C:\repo\out\preview.jpg").Should().BeTrue("媒體端點登記過的檔也能另存");
        urls.IsSaveAllowed(@"C:\repo\out\other.pptx").Should().BeFalse();
        urls.IsSaveAllowed(@"\\evil\share\a.pptx").Should().BeFalse();
        urls.IsMediaAllowed(@"C:\repo\out\簡報 37頁.pptx").Should().BeFalse("檔案連結不放行 /media");
        urls.IsThumbAllowed(@"C:\repo\out\簡報 37頁.pptx").Should().BeFalse();
    }

    [Fact]
    public void Markdown圖片_看正規化後的副檔名()
    {
        var urls = new LocalMediaUrls();

        urls.AllowMarkdownImage(@"C:\repo\out\trailing.png.");

        urls.IsMediaAllowed(@"C:\repo\out\trailing.png").Should().BeTrue("Windows 會去掉結尾的點號，正規化後是 .png（與抽出前的中樞相同）");
    }

    [Fact]
    public void UNC與不是磁碟的路徑_網址照樣產生_但一律不放行()
    {
        var urls = new LocalMediaUrls();

        urls.MediaUrl(@"\\server\share\a.png").Should().Be(AgentMarkdownRenderer.MediaUrl(@"\\server\share\a.png"));
        urls.ThumbUrl(@"\\server\share\b.png", 160).Should().Be(AgentMarkdownRenderer.ThumbUrl(@"\\server\share\b.png", 160));

        urls.IsMediaAllowed(@"\\server\share\a.png").Should().BeFalse();
        urls.IsThumbAllowed(@"\\server\share\b.png").Should().BeFalse();
        LocalMediaUrls.NormalizePath(@"\\server\share\a.png").Should().BeNull();
        LocalMediaUrls.NormalizePath("relative/a.png").Should().BeNull();
        LocalMediaUrls.NormalizePath(" ").Should().BeNull();
    }

    [Fact]
    public void 撤銷_媒體與縮圖一起移除()
    {
        var urls = new LocalMediaUrls();
        urls.MediaUrl(@"C:\att\a.png");
        urls.ThumbUrl(@"C:\att\a.png", 320);

        urls.Revoke("C:/att/a.png");

        urls.IsMediaAllowed(@"C:\att\a.png").Should().BeFalse();
        urls.IsThumbAllowed(@"C:\att\a.png").Should().BeFalse();
    }

    [Fact]
    public void 上限各兩萬筆_最久沒用到的先移除_查詢也算用到()
    {
        LocalMediaUrls.MaxAllowedPaths.Should().Be(20_000);
        var urls = new LocalMediaUrls();
        for (int i = 0; i < LocalMediaUrls.MaxAllowedPaths; i++)
            urls.MediaUrl(Numbered(i));
        urls.IsMediaAllowed(Numbered(0)).Should().BeTrue("查詢也算用到：0 排到最後，不會先被擠掉");

        urls.MediaUrl(Numbered(LocalMediaUrls.MaxAllowedPaths));

        urls.IsMediaAllowed(Numbered(1)).Should().BeFalse("超過上限：最久沒用到的 1 被移除");
        urls.IsMediaAllowed(Numbered(0)).Should().BeTrue();
        urls.IsMediaAllowed(Numbered(LocalMediaUrls.MaxAllowedPaths)).Should().BeTrue();
    }

    [Fact]
    public void 縮圖白名單另外計算上限_塞滿縮圖不影響媒體()
    {
        var urls = new LocalMediaUrls();
        urls.MediaUrl(@"C:\keep\movie.mp4");

        for (int i = 0; i <= LocalMediaUrls.MaxAllowedPaths; i++)
            urls.ThumbUrl(Numbered(i), 240);

        urls.IsMediaAllowed(@"C:\keep\movie.mp4").Should().BeTrue();
        urls.IsThumbAllowed(Numbered(0)).Should().BeFalse("縮圖白名單滿了，最早的被移除");
        urls.IsThumbAllowed(Numbered(LocalMediaUrls.MaxAllowedPaths)).Should().BeTrue();
    }
}
