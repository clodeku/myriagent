using MyriagentHub.Media;

namespace MyriagentHub.Tests.Media;

/// <summary>瀏覽器上傳的規則（WP4.7 計畫 D21）：ref_id、24 小時過期、待處理合計、PUT 前的判斷</summary>
public sealed class UploadRulesTests
{
    private const long Mb = 1024 * 1024;
    private const long Max = 512 * Mb;
    private const long Pending = 4096 * Mb;
    private const long Now = 1_800_000_000_000;

    [Fact]
    public void RefId_來回()
    {
        UploadRules.RefId("01HZX-abc_9", 1_712_345_678_901).Should().Be("01HZX-abc_9@1712345678901");
        UploadRules.TryParseRefId("01HZX-abc_9@1712345678901", out var id, out var at).Should().BeTrue();
        id.Should().Be("01HZX-abc_9");
        at.Should().Be(1_712_345_678_901);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("@123")]
    [InlineData("abc@")]
    [InlineData("abc@-1")]
    [InlineData("abc@+1")]
    [InlineData("abc@1.5")]
    [InlineData("abc@12@3")]
    [InlineData("a.b@123")]
    [InlineData("abc@99999999999999999999")]
    public void TryParseRefId_不合格(string? refId) => UploadRules.TryParseRefId(refId, out _, out _).Should().BeFalse();

    [Fact]
    public void RefId_不合格的id丟例外()
    {
        FluentActions.Invoking(() => UploadRules.RefId("a@b", 1)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => UploadRules.RefId("abc", -1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsExpired_滿24小時就過期()
    {
        UploadRules.IsExpired(Now, Now + UploadRules.LifetimeMs - 1).Should().BeFalse();
        UploadRules.IsExpired(Now, Now + UploadRules.LifetimeMs).Should().BeTrue();
        UploadRules.LifetimeMs.Should().Be(24L * 3600 * 1000);
    }

    [Fact]
    public void PendingBytes_正在上傳的預留加完成而沒過期的()
    {
        var entries = new[]
        {
            new UploadEntry(Completed: false, null, 100, Now - UploadRules.LifetimeMs * 2),
            new UploadEntry(Completed: true, "a", 20, Now - 1),
            new UploadEntry(Completed: true, "b", 3, Now - UploadRules.LifetimeMs),
        };
        UploadRules.PendingBytes(entries, Now).Should().Be(120, "正在上傳的不會過期、過期的完成不算");
    }

    [Fact]
    public void Decide_新的()
    {
        UploadRules.Decide(null, 10 * Mb, Max, 0, Pending, Now).Kind.Should().Be(UploadPutKind.New);
        UploadRules.Decide(null, Max, Max, 0, Pending, Now).Kind.Should().Be(UploadPutKind.New, "等於上限可以");
        UploadRules.Decide(null, null, Max, Pending - Max, Pending, Now).Kind.Should().Be(UploadPutKind.New, "沒宣告長度時以上限預留，剛好放得下");
    }

    [Fact]
    public void Decide_超過上限413()
        => UploadRules.Decide(null, Max + 1, Max, 0, Pending, Now).Kind.Should().Be(UploadPutKind.TooLarge);

    [Fact]
    public void Decide_待處理合計超過507()
    {
        UploadRules.Decide(null, 2 * Mb, Max, Pending - 2 * Mb, Pending, Now).Kind.Should().Be(UploadPutKind.New);
        UploadRules.Decide(null, 2 * Mb + 1, Max, Pending - 2 * Mb, Pending, Now).Kind.Should().Be(UploadPutKind.PendingFull);
        UploadRules.Decide(null, null, Max, Pending - Max + 1, Pending, Now).Kind.Should().Be(UploadPutKind.PendingFull, "沒宣告長度時以上限預留");
    }

    [Fact]
    public void Decide_同一個id正在上傳409()
        => UploadRules.Decide(new UploadEntry(Completed: false, null, 10, Now), 10, Max, 10, Pending, Now).Kind.Should().Be(UploadPutKind.Conflict);

    [Fact]
    public void Decide_已上傳過的id()
    {
        var done = new UploadEntry(Completed: true, "sha", 10, Now - 1000);
        UploadRules.Decide(done, 10, Max, 10, Pending, Now).Should().Be(new UploadPutDecision(UploadPutKind.Existing, "sha", 10));
        UploadRules.Decide(done, null, Max, 10, Pending, Now).Should().Be(new UploadPutDecision(UploadPutKind.Existing, "sha", 10), "沒宣告長度時讀完再比");
        UploadRules.Decide(done, 11, Max, 10, Pending, Now).Kind.Should().Be(UploadPutKind.Conflict, "宣告的大小不同：不必讀就知道內容不同");
        UploadRules.Decide(done, 10, Max, Pending, Pending, Now).Kind.Should().Be(UploadPutKind.Existing, "重傳不另外佔待處理的額度");
    }

    [Fact]
    public void Decide_過期的id當成新的()
    {
        var expired = new UploadEntry(Completed: true, "sha", 10, Now - UploadRules.LifetimeMs);
        UploadRules.Decide(expired, 11, Max, 0, Pending, Now).Kind.Should().Be(UploadPutKind.New);
    }
}
