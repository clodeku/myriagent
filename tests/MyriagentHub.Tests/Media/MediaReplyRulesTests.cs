using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Media;

namespace MyriagentHub.Tests.Media;

/// <summary>裝置回覆轉送的標頭檢查（WP4.7 計畫 D12）：狀態、範圍與長度要和伺服器要的完全對得上，不合格一律 null</summary>
public sealed class MediaReplyRulesTests
{
    private const long Max = MediaRelayRules.MaxSegmentBytes;

    private static MediaReplyHeaders Headers(string? status, string? total = null, string? range = null, long? length = null, string? type = null,
        string? etag = null, string? fileName = null, string? kind = null, string? error = null) =>
        new(status, total, range, length, type, etag, fileName, kind, error);

    [Fact]
    public void 範圍_206正好是要的()
    {
        var reply = MediaReplyRules.Parse(Headers("206", "20971520", "bytes 0-8388607/20971520", 8388608, "video/mp4", "\"v1\""), new MediaRange(0, 8388607), Max);

        reply.Should().NotBeNull();
        reply!.Status.Should().Be(206);
        reply.Range.Should().Be(new ByteRange(0, 8388607));
        reply.TotalLength.Should().Be(20971520);
        reply.BodyLength.Should().Be(8388608);
        reply.HasBody.Should().BeTrue();
        reply.ContentType.Should().Be("video/mp4");
        reply.ETag.Should().Be("\"v1\"");
    }

    [Fact]
    public void 範圍_最後一段在檔尾結束()
    {
        var reply = MediaReplyRules.Parse(Headers("206", "10", "bytes 7-9/10", 3), new MediaRange(7, 8388614), Max);

        reply!.Range.Should().Be(new ByteRange(7, 9));
        reply.BodyLength.Should().Be(3);
    }

    [Theory]
    [InlineData("bytes 1-9/10", 9L, "起點和要的不同")]
    [InlineData("bytes 0-8/10", 9L, "結尾比要的短（又不是檔尾）")]
    [InlineData("bytes 0-9/11", 10L, "Content-Range 的總長和 X-Total-Length 不同")]
    [InlineData("bytes 0-9/10", 9L, "Content-Length 和範圍長度不同")]
    [InlineData("bytes */10", 0L, "206 不能是 */len")]
    [InlineData(null, 10L, "206 沒有 Content-Range")]
    public void 範圍_206對不上的不合格(string? range, long length, string because)
        => MediaReplyRules.Parse(Headers("206", "10", range, length), new MediaRange(0, 9), Max).Should().BeNull(because);

    [Fact]
    public void 範圍_206超過這段的上限不合格()
    {
        MediaReplyRules.Parse(Headers("206", "100", "bytes 0-99/100", 100), new MediaRange(0, 99), maxBodyBytes: 99).Should().BeNull();
        MediaReplyRules.Parse(Headers("206", "100", "bytes 0-99/100", 100), new MediaRange(0, 99), maxBodyBytes: 100).Should().NotBeNull();
    }

    [Fact]
    public void 範圍_沒帶TotalLength不合格()
        => MediaReplyRules.Parse(Headers("206", null, "bytes 0-9/10", 10), new MediaRange(0, 9), Max).Should().BeNull();

    [Fact]
    public void 範圍_416要起點不在檔案裡而且沒有本文()
    {
        MediaReplyRules.Parse(Headers("416", "10"), new MediaRange(10, 20), Max)!.TotalLength.Should().Be(10);
        MediaReplyRules.Parse(Headers("416", "0", length: 0), new MediaRange(0, 0), Max)!.TotalLength.Should().Be(0, "空檔");
        MediaReplyRules.Parse(Headers("416", "10"), new MediaRange(3, 4), Max).Should().BeNull("起點在檔案裡卻回 416");
        MediaReplyRules.Parse(Headers("416", "10", length: 5), new MediaRange(10, 20), Max).Should().BeNull("416 帶本文");
        MediaReplyRules.Parse(Headers("416"), new MediaRange(10, 20), Max).Should().BeNull("416 沒有總長");
    }

    [Fact]
    public void 範圍_回200不合格_沒要範圍回206也不合格()
    {
        MediaReplyRules.Parse(Headers("200", "10", length: 10), new MediaRange(0, 9), Max).Should().BeNull("要了範圍就只能 206／416");
        MediaReplyRules.Parse(Headers("206", "10", "bytes 0-9/10", 10), requested: null, Max).Should().BeNull("縮圖只能 200");
        MediaReplyRules.Parse(Headers("416", "10"), requested: null, Max).Should().BeNull();
    }

    [Fact]
    public void 整份_200的長度要等於總長而且不超過上限()
    {
        var reply = MediaReplyRules.Parse(Headers("200", "300", length: 300, type: "image/png"), requested: null, maxBodyBytes: 512 * 1024);
        reply!.BodyLength.Should().Be(300);
        reply.Range.Should().BeNull();

        MediaReplyRules.Parse(Headers("200", "300", length: 299), null, 512 * 1024).Should().BeNull("Content-Length 不符");
        MediaReplyRules.Parse(Headers("200", "300"), null, 512 * 1024).Should().BeNull("沒有 Content-Length（chunked）");
        MediaReplyRules.Parse(Headers("200", "600000", length: 600000), null, 512 * 1024).Should().BeNull("超過縮圖上限");
        MediaReplyRules.Parse(Headers("200", null, length: 300), null, 512 * 1024).Should().BeNull("沒有總長");
        MediaReplyRules.Parse(Headers("200", "300", "bytes 0-299/300", 300), null, 512 * 1024).Should().BeNull("200 不帶 Content-Range");
    }

    [Theory]
    [InlineData("403", "policy", "policy")]
    [InlineData("403", "dlp", "dlp")]
    [InlineData("403", "forbidden", "forbidden")]
    [InlineData("403", "whatever", null)]
    [InlineData("404", "not_found", "not_found")]
    [InlineData("413", "too_large", "too_large")]
    [InlineData("503", null, null)]
    public void 拒絕_不帶本文_原因只收認得的(string status, string? error, string? expected)
    {
        var reply = MediaReplyRules.Parse(Headers(status, error: error), new MediaRange(0, 9), Max);

        reply!.Status.Should().Be(int.Parse(status, System.Globalization.CultureInfo.InvariantCulture));
        reply.Error.Should().Be(expected);
        reply.HasBody.Should().BeFalse();
        reply.BodyLength.Should().Be(0);
    }

    [Fact]
    public void 拒絕帶本文或範圍不合格()
    {
        MediaReplyRules.Parse(Headers("403", length: 10), new MediaRange(0, 9), Max).Should().BeNull();
        MediaReplyRules.Parse(Headers("404", range: "bytes 0-9/10"), new MediaRange(0, 9), Max).Should().BeNull();
        MediaReplyRules.Parse(Headers("403", length: 0, type: "image/png"), new MediaRange(0, 9), Max)!.ContentType.Should().Be("image/png", "拒絕可以帶型別（退回縮圖用）");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("201")]
    [InlineData("500")]
    [InlineData("302")]
    [InlineData("+206")]
    [InlineData("2O6")]
    [InlineData("99999999999")]
    public void 不認得的狀態不合格(string? status)
        => MediaReplyRules.Parse(Headers(status, "10", "bytes 0-9/10", 10), new MediaRange(0, 9), Max).Should().BeNull();

    [Fact]
    public void 狀態前後的空白照收() =>
        MediaReplyRules.Parse(Headers(" 206 ", "10", "bytes 0-9/10", 10), new MediaRange(0, 9), Max)!.Status.Should().Be(206);

    [Fact]
    public void 總長不是數字不合格()
    {
        MediaReplyRules.Parse(Headers("404", "abc"), new MediaRange(0, 9), Max).Should().BeNull();
        MediaReplyRules.Parse(Headers("206", "-10", "bytes 0-9/10", 10), new MediaRange(0, 9), Max).Should().BeNull();
    }

    [Theory]
    [InlineData("\"abc\"", true)]
    [InlineData("W/\"abc\"", true)]
    [InlineData("\"\"", true)]
    [InlineData("abc", false)]
    [InlineData("\"ab\"c\"", false)]
    [InlineData("\"a b\"", false)]
    [InlineData("w/\"abc\"", false)]
    [InlineData("\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ETag的格式(string? etag, bool valid) => MediaReplyRules.IsETag(etag).Should().Be(valid);

    [Fact]
    public void 不合格的ETag_型別_種類被丟掉但回覆照收()
    {
        var reply = MediaReplyRules.Parse(Headers("206", "10", "bytes 0-9/10", 10, type: new string('a', 256), etag: "no-quotes", kind: "html"),
            new MediaRange(0, 9), Max);

        reply.Should().NotBeNull();
        reply!.ETag.Should().BeNull();
        reply.ContentType.Should().BeNull();
        reply.ContentKind.Should().BeNull();
    }

    [Fact]
    public void 檔名解碼並清理()
    {
        var reply = MediaReplyRules.Parse(Headers("206", "10", "bytes 0-9/10", 10, fileName: "UTF-8''..%5C..%5C%E5%A0%B1%E5%91%8A.txt", kind: "text"),
            new MediaRange(0, 9), Max);

        reply!.FileName.Should().Be("報告.txt");
        reply.ContentKind.Should().Be(MediaContentKinds.Text);
        MediaReplyRules.Parse(Headers("206", "10", "bytes 0-9/10", 10, fileName: "報告.txt"), new MediaRange(0, 9), Max)!.FileName
            .Should().BeNull("沒有編碼的非 ASCII 檔名不收");
    }
}
