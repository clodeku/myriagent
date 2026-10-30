using Myriagent.Agents;
using Myriagent.Media;

namespace Myriagent.Tests.Protocol.Media;

/// <summary>媒體回應的 CSP、型別白名單與檔名編碼（WP4.7 計畫 D9、D12；SEC-23 的伺服器端保證）</summary>
public class MediaHeaderRulesTests
{
    [Fact]
    public void MediaCsp_照設計逐字()
        => MediaHeaderRules.MediaCsp.Should().Be("default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox");

    [Theory]
    [InlineData("image/svg+xml", null)]
    [InlineData("text/html", null)]
    [InlineData("text/plain", null)]
    [InlineData("application/octet-stream", null)]
    [InlineData("image/jpg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData(" image/jpeg ", "image/jpeg")]
    [InlineData("image/png; charset=binary", "image/png")]
    [InlineData("video/mp4", "video/mp4")]
    [InlineData("Video/QuickTime", "video/quicktime")]
    public void SafeMediaContentType_只收白名單(string? declared, string? expected)
        => MediaHeaderRules.SafeMediaContentType(declared).Should().Be(expected);

    [Fact]
    public void SafeMediaContentType_白名單正好是GetMediaContentType的值域()
    {
        // 附件規則認得的所有副檔名（含不可顯示的 SVG、HEIC、TIFF 與文件）
        var extensions = new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif", ".svg", ".heic", ".tif", ".tiff",
            ".mp4", ".m4v", ".webm", ".mov", ".avi", ".mkv", ".wmv", ".mp3", ".wav", ".pdf", ".txt", ".html", ".htm", ".js",
        };
        var types = extensions.Select(e => AgentAttachmentRules.GetMediaContentType("a" + e)).OfType<string>().Distinct().ToList();

        types.Should().NotBeEmpty();
        types.Should().OnlyContain(t => MediaHeaderRules.SafeMediaContentType(t) == t);
        extensions.Where(e => AgentAttachmentRules.GetMediaContentType("a" + e) is null)
            .Select(e => "x/" + e.TrimStart('.'))
            .Should().OnlyContain(t => MediaHeaderRules.SafeMediaContentType(t) == null);
        types.Should().HaveCount(10, "白名單的十個型別都要有對應的副檔名");
    }

    [Fact]
    public void ContentDisposition_去掉引號與換行並附UTF8檔名()
        => MediaHeaderRules.ContentDisposition("報告\".txt\r\n", true)
            .Should().Be("attachment; filename=\"_.txt\"; filename*=UTF-8''%E5%A0%B1%E5%91%8A_.txt");

    [Fact]
    public void ContentDisposition_其他情形()
    {
        MediaHeaderRules.ContentDisposition(null, false).Should().Be("inline");
        MediaHeaderRules.ContentDisposition("\r\n", true).Should().Be("attachment", "清完是空的就不帶檔名");
        MediaHeaderRules.ContentDisposition("報告.txt", false)
            .Should().Be("inline; filename=\"file.txt\"; filename*=UTF-8''%E5%A0%B1%E5%91%8A.txt", "主檔名全是非 ASCII 時後備改成 file");
        MediaHeaderRules.ContentDisposition("報告", true).Should().Be("attachment; filename=\"file\"; filename*=UTF-8''%E5%A0%B1%E5%91%8A");
        MediaHeaderRules.ContentDisposition("100%.png", true).Should().Be("attachment; filename=\"100_.png\"; filename*=UTF-8''100%25.png");
        MediaHeaderRules.ContentDisposition(@"C:\Users\U\a b.png", false).Should().Be("inline; filename=\"a b.png\"; filename*=UTF-8''a%20b.png");
    }

    [Fact]
    public void EncodeFileName與DecodeFileName來回()
    {
        MediaHeaderRules.EncodeFileName("報告.txt").Should().Be("UTF-8''%E5%A0%B1%E5%91%8A.txt");
        MediaHeaderRules.DecodeFileName(MediaHeaderRules.EncodeFileName("報告.txt")).Should().Be("報告.txt");
        MediaHeaderRules.DecodeFileName(MediaHeaderRules.EncodeFileName("a b (1)~!.png")).Should().Be("a b (1)~!.png");
        MediaHeaderRules.DecodeFileName("utf-8'zh-TW'%E5%A0%B1.txt").Should().Be("報.txt", "charset 不分大小寫、語言可以有");
    }

    [Fact]
    public void DecodeFileName_只留最後一段()
    {
        MediaHeaderRules.DecodeFileName("UTF-8''..%5C..%5Cevil.exe").Should().Be("evil.exe");
        MediaHeaderRules.DecodeFileName("UTF-8''..%2F..%2Fetc%2Fpasswd").Should().Be("passwd");
        MediaHeaderRules.DecodeFileName("UTF-8''C%3A%5Ca%5C").Should().BeNull("最後一段是空的");
        MediaHeaderRules.DecodeFileName("UTF-8''..").Should().BeNull();
    }

    [Fact]
    public void DecodeFileName_去控制字元與格式字元()
    {
        var rtlOverride = MediaHeaderRules.EncodeFileName("a" + (char)0x202E + "gpj.exe");
        MediaHeaderRules.DecodeFileName(rtlOverride).Should().Be("agpj.exe", "RTL 覆寫會讓檔名看起來像別的副檔名");
        MediaHeaderRules.DecodeFileName("UTF-8''a%00b%0D%0A.txt").Should().Be("ab.txt");
        MediaHeaderRules.DecodeFileName("UTF-8''a%22b%3Fc.txt%20.%20.").Should().Be("a_b_c.txt");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("報告.txt")]
    [InlineData("ISO-8859-1''a.txt")]
    [InlineData("UTF-8''")]
    [InlineData("UTF-8'a.txt")]
    [InlineData("UTF-8''%E5%A0")]
    [InlineData("UTF-8''%G1.txt")]
    [InlineData("UTF-8''%4")]
    [InlineData("UTF-8''a b.txt")]
    [InlineData("UTF-8''%C3%28.txt")]
    public void DecodeFileName_不合格回null(string? header)
        => MediaHeaderRules.DecodeFileName(header).Should().BeNull();

    [Fact]
    public void CleanFileName_太長時保留副檔名()
    {
        var clean = MediaHeaderRules.CleanFileName(new string('a', 300) + ".txt");

        clean.Should().HaveLength(255);
        clean.Should().EndWith(".txt");
    }

    [Fact]
    public void CleanFileName_截短不切開代理字對()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var name = new string('a', 250) + string.Concat(Enumerable.Repeat(emoji, 10)) + ".txt";

        var clean = MediaHeaderRules.CleanFileName(name)!;

        clean.Length.Should().BeLessThanOrEqualTo(255);
        clean.Should().EndWith(".txt");
        char.IsHighSurrogate(clean[^5]).Should().BeFalse("副檔名前面不能留下落單的高代理字元");
    }
}
