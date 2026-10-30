using Myriagent.Media;
using MyriagentHub.Auth;
using MyriagentHub.Media;

namespace MyriagentHub.Tests.Media;

/// <summary>媒體端點的狀態碼對應與本文上限（WP4.7 計畫步 3）</summary>
public sealed class MediaApiRulesTests
{
    [Theory]
    [InlineData((int)BlobPutOutcome.Mismatch, null, 400, "mismatch")]
    [InlineData((int)BlobPutOutcome.TooLarge, null, 413, "too_large")]
    [InlineData((int)BlobPutOutcome.QuotaExceeded, null, 507, "insufficient_storage")]
    [InlineData((int)BlobPutOutcome.DiskLow, null, 507, "insufficient_storage")]
    [InlineData((int)BlobPutOutcome.Rejected, "dlp", 422, "dlp")]
    [InlineData((int)BlobPutOutcome.Rejected, "invalid", 422, "invalid")]
    [InlineData((int)BlobPutOutcome.Rejected, null, 422, "invalid")]
    public void PutFailure_對應(int outcome, string? rejection, int status, string code)
        => MediaApiRules.PutFailure((BlobPutOutcome)outcome, rejection).Should().Be((status, code));

    [Theory]
    [InlineData((int)BlobPutOutcome.Stored)]
    [InlineData((int)BlobPutOutcome.Exists)]
    public void PutFailure_成功回null(int outcome) => MediaApiRules.PutFailure((BlobPutOutcome)outcome, null).Should().BeNull();

    [Fact]
    public void PutFailure_每個結果都有對應()
    {
        foreach (var outcome in Enum.GetValues<BlobPutOutcome>())
            FluentActions.Invoking(() => MediaApiRules.PutFailure(outcome, null)).Should().NotThrow(outcome.ToString());
        FluentActions.Invoking(() => MediaApiRules.PutFailure((BlobPutOutcome)99, null)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData((int)UploadPutKind.Conflict, 409, "conflict")]
    [InlineData((int)UploadPutKind.TooLarge, 413, "too_large")]
    [InlineData((int)UploadPutKind.PendingFull, 507, "pending_full")]
    public void BeginFailure_對應(int kind, int status, string code) => MediaApiRules.BeginFailure((UploadPutKind)kind).Should().Be((status, code));

    [Fact]
    public void BeginFailure_要讀本文的回null()
    {
        MediaApiRules.BeginFailure(UploadPutKind.New).Should().BeNull();
        MediaApiRules.BeginFailure(UploadPutKind.Existing).Should().BeNull();
        foreach (var kind in Enum.GetValues<UploadPutKind>())
            FluentActions.Invoking(() => MediaApiRules.BeginFailure(kind)).Should().NotThrow(kind.ToString());
    }

    [Fact]
    public void BadRequestCode_413是太大其他是不合格()
    {
        MediaApiRules.BadRequestCode(413).Should().Be("too_large");
        MediaApiRules.BadRequestCode(400).Should().Be("invalid");
        MediaApiRules.BadRequestCode(408).Should().Be("invalid");
    }

    [Fact]
    public void RequestBodyLimits_宣告超過才算()
    {
        RequestBodyLimits.Exceeds(null, 10).Should().BeFalse("沒宣告的交給讀取時計數");
        RequestBodyLimits.Exceeds(10, 10).Should().BeFalse();
        RequestBodyLimits.Exceeds(11, 10).Should().BeTrue();
    }

    [Fact]
    public void 裝置端點的前綴就是存取權杖認得的前綴()
        => (MediaRegistration.DevicePrefix + "/").Should().Be(AccessTokenStore.PathPrefix);

    [Fact]
    public void 錯誤代碼和檢查的代碼一致()
    {
        MediaApiErrors.Dlp.Should().Be(MediaErrorCodes.Dlp, "和轉送的錯誤代碼同一個字");
        MediaApiErrors.TooLarge.Should().Be(MediaErrorCodes.TooLarge);
        MediaApiErrors.NotFound.Should().Be(MediaErrorCodes.NotFound);
    }
}
