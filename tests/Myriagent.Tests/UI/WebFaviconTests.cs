using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.UI;

/// <summary>
/// 網頁的 favicon（Web/favicon.ico，桌面與瀏覽器版共用）與桌面的 app 圖示（app.ico）是同一張圖：兩者都由 tools/make_icon.py 產生，
/// favicon 的每個尺寸與 app.ico 的同尺寸逐位元組相同（改了圖案要重跑腳本，兩個檔案一起提交）
/// </summary>
public class WebFaviconTests
{
    private static string Source(params string[] parts) => Path.Combine([MyriagentHubExe.RepoRoot(), "src", "Myriagent", .. parts]);

    [Fact]
    public void favicon的每個尺寸都與app圖示的同尺寸逐位元組相同()
    {
        var app = Frames(File.ReadAllBytes(Source("app.ico")));
        var favicon = Frames(File.ReadAllBytes(Source("Web", "favicon.ico")));
        favicon.Keys.Should().Contain(new[] { 16, 32 }, "分頁圖示一般與高解析度螢幕要用的尺寸");
        foreach (var (size, image) in favicon)
        {
            app.Should().ContainKey(size, $"app.ico 要有 {size} px 才能比對");
            image.Should().Equal(app[size], $"favicon 的 {size} px 要與 app.ico 相同（重跑 tools/make_icon.py）");
        }
    }

    [Fact]
    public void index_html的圖示指向favicon()
    {
        File.ReadAllText(Source("Web", "index.html")).Should().Contain("<link rel=\"icon\" href=\"favicon.ico\">");
    }

    /// <summary>ICO 的各尺寸影像：ICONDIR（6 位元組）＋每個尺寸一筆 16 位元組的 ICONDIRENTRY（寬、高為 0 表示 256；第 8、12 位元組起是長度與位移）</summary>
    private static Dictionary<int, byte[]> Frames(byte[] ico)
    {
        BitConverter.ToUInt16(ico, 2).Should().Be(1, "type 1 是圖示");
        var count = BitConverter.ToUInt16(ico, 4);
        var frames = new Dictionary<int, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var width = ico[entry] == 0 ? 256 : ico[entry];
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            frames.Add(width, ico.AsSpan(offset, length).ToArray());
        }
        return frames;
    }
}
