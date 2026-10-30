using System.Text;
using MyriagentHub.Media;
using MyriagentHub.Tests.Sync;

namespace MyriagentHub.Tests.Media;

/// <summary>項目 blob 的第二道 DLP（WP4.7 計畫 D15）：嚴格 UTF-8＋HasLeakText，禁止的鍵交給讀取端</summary>
public sealed class BlobInspectRulesTests
{
    [Theory]
    [InlineData("一般的回覆文字")]
    [InlineData("")]
    [InlineData("[{\"path\":\"a\"}]")]
    [InlineData("[{\"name\":\"附件.png\",\"kind\":\"Image\"}]")]
    public void 乾淨的文字通過(string text) => BlobInspectRules.CheckItemBody(Encoding.UTF8.GetBytes(text)).Should().BeNull();

    [Fact]
    public void 含path鍵的JSON陣列文字照樣收_禁止的鍵交給讀取端()
        => BlobInspectRules.CheckItemBody(Encoding.UTF8.GetBytes("[{\"path\":\"C:/Users/U/a.png\"}]")).Should().BeNull();

    [Theory]
    [InlineData(new byte[] { 0xC3, 0x28 })]
    [InlineData(new byte[] { 0x61, 0xE5, 0xA0 })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    public void 不是嚴格UTF8回invalid(byte[] data) => BlobInspectRules.CheckItemBody(data).Should().Be(BlobInspectRules.Invalid);

    [Fact]
    public void 權杖回dlp()
    {
        BlobInspectRules.CheckItemBody(Encoding.UTF8.GetBytes("工具輸出 " + FakeSecrets.GitHubStyle() + " 結尾")).Should().Be(BlobInspectRules.Dlp);
        BlobInspectRules.CheckItemBody(Encoding.UTF8.GetBytes("[{\"text\":\"" + FakeSecrets.GitHubStyle() + "\"}]")).Should().Be(BlobInspectRules.Dlp, "陣列欄位的 JSON");
    }

    [Fact]
    public void JSON跳脫寫法的權杖也是dlp()
    {
        var escaped = "[{\"text\":\"" + ConversationSamples.EscapeFirst(FakeSecrets.GitHubStyle()) + "\"}]";
        BlobInspectRules.CheckItemBody(Encoding.UTF8.GetBytes(escaped)).Should().Be(BlobInspectRules.Dlp, "解碼後才看得到的權杖");
    }

    [Fact]
    public void 縮圖只看魔數_PNG與JPEG()
    {
        BlobInspectRules.ThumbnailType(MediaBytes.Png(32)).Should().Be("image/png");
        BlobInspectRules.ThumbnailType(MediaBytes.Jpeg(32)).Should().Be("image/jpeg");
        BlobInspectRules.ThumbnailType([0x89, 0x50, 0x4E, 0x47]).Should().BeNull("PNG 的魔數只有一半");
        BlobInspectRules.ThumbnailType([0xFF, 0xD8]).Should().BeNull();
        BlobInspectRules.ThumbnailType([]).Should().BeNull();
        BlobInspectRules.ThumbnailType(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")).Should().BeNull("SVG 不是縮圖");
        BlobInspectRules.ThumbnailType([0x47, 0x49, 0x46, 0x38, 0x39, 0x61]).Should().BeNull("GIF 不收");
    }

    [Fact]
    public void 同一份內容和渲染器的判斷一致()
    {
        foreach (var text in new[] { "乾淨", FakeSecrets.GitHubStyle(), "{\"a\":\"" + FakeSecrets.GitHubStyle(3) + "\"}" })
        {
            var expected = MyriagentHub.Sync.ConversationDlpRules.HasLeakText(text) ? BlobInspectRules.Dlp : null;
            BlobInspectRules.CheckItemBody(Encoding.UTF8.GetBytes(text)).Should().Be(expected, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
