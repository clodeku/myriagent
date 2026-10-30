using Myriagent.Agents;
using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>/media 的回應準備（在背景執行緒開檔，UI 執行緒只建立回應物件）與縮圖網址的檔案版本</summary>
public class MediaEndpointTests
{
    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void PrepareMedia_沒有Range回整個檔案()
    {
        using var temp = new TestSupport.TempDirectory("media");
        var path = temp.Combine("影片.mp4");
        File.WriteAllBytes(path, Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());

        var prepared = AgentWebView.PrepareMedia(path, "video/mp4", null);

        prepared.Should().NotBeNull();
        using var content = prepared!.Content!;
        prepared.StatusCode.Should().Be(200);
        prepared.Headers.Should().Contain("Content-Type: video/mp4").And.Contain("Content-Length: 1000").And.Contain("Accept-Ranges: bytes").And.Contain("Cache-Control: no-cache");
        ReadAll(content).Should().HaveCount(1000);
    }

    [Fact]
    public void PrepareMedia_有Range回206部分內容()
    {
        using var temp = new TestSupport.TempDirectory("media");
        var path = temp.Combine("影片.mp4");
        File.WriteAllBytes(path, Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());

        var prepared = AgentWebView.PrepareMedia(path, "video/mp4", "bytes=100-199");

        prepared.Should().NotBeNull();
        using var content = prepared!.Content!;
        prepared.StatusCode.Should().Be(206);
        prepared.ReasonPhrase.Should().Be("Partial Content");
        prepared.Headers.Should().Contain("Content-Length: 100").And.Contain("Content-Range: bytes 100-199/1000");
        ReadAll(content).Should().Equal(Enumerable.Range(100, 100).Select(i => (byte)i));
    }

    [Fact]
    public void PrepareMedia_找不到或是資料夾回Null_檔案不會被鎖住()
    {
        using var temp = new TestSupport.TempDirectory("media");
        AgentWebView.PrepareMedia(temp.Combine("沒有這個.png"), "image/png", null).Should().BeNull();
        AgentWebView.PrepareMedia(temp.Combine(@"沒有這個資料夾\a.png"), "image/png", null).Should().BeNull();
        AgentWebView.PrepareMedia(temp.CreateDirectory("假裝是圖.png"), "image/png", null).Should().BeNull();

        var path = temp.WriteFile("圖.png", "png");
        var prepared = AgentWebView.PrepareMedia(path, "image/png", "bytes=0-1");
        File.Delete(path);   // 以 FileShare.Delete 開啟：網頁還在讀時，代理照樣能刪除或覆寫
        File.Exists(path).Should().BeFalse();
        prepared!.Content!.Dispose();
    }

    [Fact]
    public void ThumbUrl_帶檔案版本_路徑與大小照樣取得_沒有版本時網址不變()
    {
        AgentMarkdownRenderer.ThumbUrl(@"C:\a.png", 240).Should().Be("https://myriagent.local/thumb?path=C%3A%5Ca.png&size=240");

        var versioned = new Uri(AgentMarkdownRenderer.ThumbUrl(@"C:\資料\a+b.png", 240, "638640000000000000-48213"));
        versioned.Query.Should().EndWith("&v=638640000000000000-48213");
        AgentWebView.GetQueryValue(versioned, "path").Should().Be(@"C:\資料\a+b.png");
        AgentWebView.GetQueryValue(versioned, "size").Should().Be("240");
    }
}
