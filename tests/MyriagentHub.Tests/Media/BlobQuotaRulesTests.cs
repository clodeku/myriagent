using MyriagentHub.Media;

namespace MyriagentHub.Tests.Media;

/// <summary>blob 的配額等級與收不收（設計 3.4:633：200 GB、80% 告警、95% 拒收；C: 可用空間下限 40 GB）</summary>
public class BlobQuotaRulesTests
{
    private const long GB = 1L << 30;

    [Fact]
    public void Level_照門檻()
    {
        BlobQuotaRules.Level(160 * GB, 200 * GB, 80, 95, 500 * GB, 40 * GB).Should().Be(BlobQuotaLevel.Warn, "正好 80%");
        BlobQuotaRules.Level(190 * GB, 200 * GB, 80, 95, 500 * GB, 40 * GB).Should().Be(BlobQuotaLevel.Refuse, "正好 95%");
        BlobQuotaRules.Level(160 * GB - GB / 10, 200 * GB, 80, 95, 500 * GB, 40 * GB).Should().Be(BlobQuotaLevel.Ok, "159.9 GB");
        BlobQuotaRules.Level(160 * GB - 1, 200 * GB, 80, 95, 500 * GB, 40 * GB).Should().Be(BlobQuotaLevel.Ok, "差 1 位元組");
        BlobQuotaRules.Level(190 * GB - 1, 200 * GB, 80, 95, 500 * GB, 40 * GB).Should().Be(BlobQuotaLevel.Warn);
        BlobQuotaRules.Level(0, 200 * GB, 80, 95, 500 * GB, 40 * GB).Should().Be(BlobQuotaLevel.Ok);
    }

    [Fact]
    public void Level_磁碟可用空間不足或量不到是Refuse()
    {
        BlobQuotaRules.Level(10 * GB, 200 * GB, 80, 95, 39 * GB, 40 * GB).Should().Be(BlobQuotaLevel.Refuse);
        BlobQuotaRules.Level(10 * GB, 200 * GB, 80, 95, 40 * GB, 40 * GB).Should().Be(BlobQuotaLevel.Ok, "正好在下限");
        BlobQuotaRules.Level(10 * GB, 200 * GB, 80, 95, -1, 40 * GB).Should().Be(BlobQuotaLevel.Refuse, "量不到");
    }

    [Fact]
    public void Admits_磁碟下限()
    {
        BlobQuotaRules.Admits(10 * GB, 1, 200 * GB, 95, 39 * GB, 40 * GB).Should().BeFalse();
        BlobQuotaRules.Admits(10 * GB, GB, 200 * GB, 95, 41 * GB, 40 * GB).Should().BeTrue("存了之後正好在下限");
        BlobQuotaRules.Admits(10 * GB, GB + 1, 200 * GB, 95, 41 * GB, 40 * GB).Should().BeFalse("存了之後低於下限");
        BlobQuotaRules.Admits(10 * GB, 0, 200 * GB, 95, -1, 40 * GB).Should().BeFalse("量不到可用空間");
    }

    [Fact]
    public void Admits_存了之後到拒收門檻就不收()
    {
        BlobQuotaRules.Admits(190 * GB - 2, 1, 200 * GB, 95, 500 * GB, 40 * GB).Should().BeTrue();
        BlobQuotaRules.Admits(190 * GB - 1, 1, 200 * GB, 95, 500 * GB, 40 * GB).Should().BeFalse("存了之後正好 95%");
        BlobQuotaRules.Admits(190 * GB, 0, 200 * GB, 95, 500 * GB, 40 * GB).Should().BeFalse("已經是 Refuse 時連 0 位元組也不收");
    }

    [Fact]
    public void Check_給原因_磁碟優先()
    {
        BlobQuotaRules.Check(0, 1, 200 * GB, 95, 500 * GB, 40 * GB).Should().BeNull();
        BlobQuotaRules.Check(190 * GB, 1, 200 * GB, 95, 500 * GB, 40 * GB).Should().Be(BlobQuotaRefusal.Quota);
        BlobQuotaRules.Check(190 * GB, 1, 200 * GB, 95, 39 * GB, 40 * GB).Should().Be(BlobQuotaRefusal.DiskLow);
    }

    [Fact]
    public void 大數字不溢位()
    {
        BlobQuotaRules.Level(long.MaxValue / 2, long.MaxValue, 80, 95, long.MaxValue, 40 * GB).Should().Be(BlobQuotaLevel.Ok);
        BlobQuotaRules.Admits(long.MaxValue - 1, long.MaxValue, long.MaxValue, 95, long.MaxValue, 0).Should().BeFalse();
    }

    [Fact]
    public void 負的大小丟例外()
    {
        var act = () => BlobQuotaRules.Admits(0, -1, 200 * GB, 95, 500 * GB, 40 * GB);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
