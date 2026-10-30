using Myriagent.Media;
using MyriagentHub.Media;
using Microsoft.Extensions.Logging;

namespace MyriagentHub.Tests.Media;

/// <summary>媒體回應給瀏覽器的型別、下載方式、狀態與縮圖退回（WP4.7 計畫 D9～D12、D29）</summary>
public sealed class MediaServeRulesTests
{
    private static MediaReply Reply(int status = 206, string? type = null, string? fileName = null, string? kind = null, long total = 10, string? error = null) =>
        new(status, total, status == 206 ? new Myriagent.Media.ByteRange(0, total - 1) : null, status == 206 ? total : 0, type, null, fileName, kind, error);

    [Theory]
    [InlineData((int)MediaEndpointKind.Media, "private, max-age=300")]
    [InlineData((int)MediaEndpointKind.Thumb, "private, max-age=300")]
    [InlineData((int)MediaEndpointKind.File, "no-store")]
    [InlineData((int)MediaEndpointKind.ReplicaMedia, "no-store")]
    [InlineData((int)MediaEndpointKind.ReplicaThumb, "no-store")]
    public void CacheControl(int endpoint, string expected) => MediaServeRules.CacheControl((MediaEndpointKind)endpoint).Should().Be(expected);

    [Fact]
    public void 原檔_白名單內照標準寫法_有檔名才帶inline()
    {
        MediaServeRules.Representation(MediaEndpointKind.Media, Reply(type: "IMAGE/PNG")).Should().Be(("image/png", (string?)null));
        MediaServeRules.Representation(MediaEndpointKind.Media, Reply(type: "video/mp4", fileName: "a.mp4"))
            .Should().Be(("video/mp4", (string?)"inline; filename=\"a.mp4\"; filename*=UTF-8''a.mp4"));
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    [InlineData(null)]
    public void 原檔_白名單外改成octet_stream加attachment(string? type)
    {
        var (contentType, disposition) = MediaServeRules.Representation(MediaEndpointKind.Media, Reply(type: type, fileName: "x.svg"));

        contentType.Should().Be("application/octet-stream");
        disposition.Should().StartWith("attachment");
    }

    [Fact]
    public void 複本代理和原檔同一套()
        => MediaServeRules.Representation(MediaEndpointKind.ReplicaMedia, Reply(type: "image/svg+xml"))
            .Should().Be(("application/octet-stream", (string?)"attachment"));

    [Fact]
    public void 檔案_文字2MB以內inline預覽()
    {
        var limit = MediaServeRules.MaxTextPreviewBytes;

        MediaServeRules.Representation(MediaEndpointKind.File, Reply(kind: "text", fileName: "a.txt", total: limit))
            .Should().Be(("text/plain; charset=utf-8", (string?)"inline; filename=\"a.txt\"; filename*=UTF-8''a.txt"));
        MediaServeRules.Representation(MediaEndpointKind.File, Reply(kind: "text", fileName: "a.txt", total: limit + 1))
            .Should().Be(("application/octet-stream", (string?)"attachment; filename=\"a.txt\"; filename*=UTF-8''a.txt"), "超過 2 MB 改成下載");
    }

    [Fact]
    public void 檔案_二進位與不明一律下載()
    {
        MediaServeRules.Representation(MediaEndpointKind.File, Reply(kind: "binary", type: "text/html", fileName: "a.html")).ContentType.Should().Be("application/octet-stream");
        MediaServeRules.Representation(MediaEndpointKind.File, Reply(kind: null, fileName: "a.txt")).Disposition.Should().StartWith("attachment");
        MediaServeRules.Representation(MediaEndpointKind.File, Reply(kind: "binary", type: "image/png")).Should().Be(("image/png", (string?)"attachment"));
    }

    [Fact]
    public void 縮圖端點不經Representation()
        => FluentActions.Invoking(() => MediaServeRules.Representation(MediaEndpointKind.Thumb, Reply())).Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData((int)RelayFailure.Offline, null, null, 503, "offline")]
    [InlineData((int)RelayFailure.Busy, null, null, 503, "busy")]
    [InlineData((int)RelayFailure.Timeout, null, null, 504, "timeout")]
    [InlineData((int)RelayFailure.BadReply, null, null, 502, "bad_gateway")]
    [InlineData((int)RelayFailure.None, 403, "policy", 403, "policy")]
    [InlineData((int)RelayFailure.None, 403, "dlp", 403, "dlp")]
    [InlineData((int)RelayFailure.None, 403, null, 403, "forbidden")]
    [InlineData((int)RelayFailure.None, 404, null, 404, "not_found")]
    [InlineData((int)RelayFailure.None, 413, null, 413, "too_large")]
    [InlineData((int)RelayFailure.None, 503, null, 503, "busy")]
    [InlineData((int)RelayFailure.None, 200, null, 502, "bad_gateway")]
    public void 失敗的狀態與代碼(int failure, int? status, string? error, int expectedStatus, string expectedCode)
    {
        var reply = status is { } s ? Reply(status: s, error: error) : null;

        MediaServeRules.Failure((RelayFailure)failure, reply).Should().Be((expectedStatus, expectedCode));
    }

    [Fact]
    public void 忙碌要帶RetryAfter()
    {
        MediaServeRules.IsBusy(RelayFailure.Busy, null).Should().BeTrue();
        MediaServeRules.IsBusy(RelayFailure.None, Reply(status: 503)).Should().BeTrue("電腦自己的同時上限");
        MediaServeRules.IsBusy(RelayFailure.Offline, null).Should().BeFalse();
        MediaServeRules.IsBusy(RelayFailure.None, Reply(status: 404)).Should().BeFalse();
    }

    [Fact]
    public void 退回縮圖_離線時看SecFetchDest()
    {
        MediaServeRules.FallbackToThumb(RelayFailure.Offline, null, "image").Should().BeTrue();
        MediaServeRules.FallbackToThumb(RelayFailure.Offline, null, null).Should().BeTrue("沒有標頭（直接開網址）當成圖片：伺服器手上只有縮圖");
        MediaServeRules.FallbackToThumb(RelayFailure.Offline, null, "video").Should().BeFalse("影片照回錯誤");
        MediaServeRules.FallbackToThumb(RelayFailure.Offline, null, " Audio ").Should().BeFalse();
    }

    [Fact]
    public void 退回縮圖_電腦拒絕時看它宣告的型別()
    {
        MediaServeRules.FallbackToThumb(RelayFailure.None, Reply(status: 403, type: "image/jpeg", error: "policy"), "video").Should().BeTrue("宣告是圖片");
        MediaServeRules.FallbackToThumb(RelayFailure.None, Reply(status: 403, type: "video/mp4", error: "policy"), "image").Should().BeFalse("宣告是影片");
        MediaServeRules.FallbackToThumb(RelayFailure.None, Reply(status: 403, type: "image/svg+xml"), "image").Should().BeTrue("白名單外的型別當成沒宣告");
    }

    [Theory]
    [InlineData((int)RelayFailure.Busy, null)]
    [InlineData((int)RelayFailure.Timeout, null)]
    [InlineData((int)RelayFailure.BadReply, null)]
    [InlineData((int)RelayFailure.None, 413)]
    [InlineData((int)RelayFailure.None, 503)]
    public void 退回縮圖_暫時的失敗與其他拒絕不退回(int failure, int? status)
        => MediaServeRules.FallbackToThumb((RelayFailure)failure, status is { } s ? Reply(status: s, type: "image/png") : null, "image").Should().BeFalse();

    [Fact]
    public void 退回縮圖_原檔已經不在也退回_影片照回錯誤()
    {
        MediaServeRules.FallbackToThumb(RelayFailure.None, Reply(status: 404, error: "not_found"), "image").Should().BeTrue("伺服器手上有縮圖就給縮圖（2026-10-27）");
        MediaServeRules.FallbackToThumb(RelayFailure.None, Reply(status: 404, error: "not_found"), "video").Should().BeFalse();
    }

    /// <summary>2026-10-27：原檔被刪掉的舊圖片在瀏覽器版是破圖、兩天 583 筆回報。只給瀏覽器 &lt;img&gt; 要的 /m/、/t/</summary>
    [Fact]
    public void 佔位圖_原檔不在而且是瀏覽器要的圖片才給()
    {
        var gone = Reply(status: 404, error: "not_found");
        MediaServeRules.MissingPlaceholder(MediaEndpointKind.Media, RelayFailure.None, gone, "image").Should().BeTrue();
        MediaServeRules.MissingPlaceholder(MediaEndpointKind.Thumb, RelayFailure.None, gone, " Image ").Should().BeTrue();
        MediaServeRules.MissingPlaceholder(MediaEndpointKind.Media, RelayFailure.None, gone, null).Should().BeFalse("直接開網址或程式取：照回 404");
        MediaServeRules.MissingPlaceholder(MediaEndpointKind.Media, RelayFailure.None, gone, "video").Should().BeFalse();
        MediaServeRules.MissingPlaceholder(MediaEndpointKind.ReplicaThumb, RelayFailure.None, gone, "image").Should().BeFalse("桌面複本的代理只收 PNG、JPEG");
        MediaServeRules.MissingPlaceholder(MediaEndpointKind.File, RelayFailure.None, gone, "image").Should().BeFalse();
        MediaServeRules.MissingPlaceholder(MediaEndpointKind.Media, RelayFailure.Offline, null, "image").Should().BeFalse("離線不是原檔不在");
        MediaServeRules.MissingPlaceholder(MediaEndpointKind.Media, RelayFailure.None, Reply(status: 403, error: "policy"), "image").Should().BeFalse();
        var svg = System.Text.Encoding.UTF8.GetString(MediaServeRules.MissingImageSvg());
        svg.Should().StartWith("<svg xmlns=\"http://www.w3.org/2000/svg\"").And.Contain("圖片已不存在").And.NotContain("<script");
        System.Xml.Linq.XDocument.Parse(svg).Root!.Name.LocalName.Should().Be("svg", "要是合格的 XML，瀏覽器才畫得出來");
    }

    [Theory]
    [InlineData("160", true)]
    [InlineData("320", true)]
    [InlineData("100", false)]
    [InlineData("0320", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 縮圖的s(string? size, bool valid) => MediaServeRules.IsThumbSize(size).Should().Be(valid);

    [Theory]
    [InlineData(null, true)]
    [InlineData("1712345678", true)]
    [InlineData("1234567890123456789", true)]
    [InlineData("12345678901234567890", false)]
    [InlineData("", false)]
    [InlineData("12a", false)]
    [InlineData("-1", false)]
    public void 縮圖的v(string? version, bool valid) => MediaServeRules.IsThumbVersion(version).Should().Be(valid);

    [Theory]
    [InlineData("\"abc\"", true)]
    [InlineData("W/\"abc\"", true)]
    [InlineData("\"x\", \"abc\"", true)]
    [InlineData("*", true)]
    [InlineData("\"abd\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IfNoneMatch(string? header, bool matches) => MediaServeRules.MatchesETag(header, "\"abc\"").Should().Be(matches);

    [Fact]
    public void FailureLogLevel_預期中的記Information_其他記Warning()
    {
        MediaServeRules.FailureLogLevel(RelayFailure.Offline, null).Should().Be(LogLevel.Information);
        MediaServeRules.FailureLogLevel(RelayFailure.Busy, null).Should().Be(LogLevel.Information);
        MediaServeRules.FailureLogLevel(RelayFailure.None, Reply(status: 404, error: "not_found")).Should().Be(LogLevel.Information, "檔案刪了");
        MediaServeRules.FailureLogLevel(RelayFailure.None, Reply(status: 503)).Should().Be(LogLevel.Information, "電腦忙碌");
        MediaServeRules.FailureLogLevel(RelayFailure.None, Reply(status: 403, error: "policy")).Should().Be(LogLevel.Information, "遠端政策 Off");

        MediaServeRules.FailureLogLevel(RelayFailure.None, Reply(status: 403, error: "forbidden")).Should().Be(LogLevel.Warning, "守門員擋下登記過的媒體");
        MediaServeRules.FailureLogLevel(RelayFailure.None, Reply(status: 403, error: "dlp")).Should().Be(LogLevel.Warning);
        MediaServeRules.FailureLogLevel(RelayFailure.None, Reply(status: 413, error: "too_large")).Should().Be(LogLevel.Warning);
        MediaServeRules.FailureLogLevel(RelayFailure.Timeout, null).Should().Be(LogLevel.Warning);
        MediaServeRules.FailureLogLevel(RelayFailure.BadReply, null).Should().Be(LogLevel.Warning);
    }
}
