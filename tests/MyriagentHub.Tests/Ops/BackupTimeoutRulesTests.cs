using MyriagentHub.Ops;

namespace MyriagentHub.Tests.Ops;

/// <summary>備份連線的命令逾時依檔案大小放寬（WP4.2 計畫 D41、答 7）</summary>
public sealed class BackupTimeoutRulesTests
{
    private const long GiB = 1L << 30;

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(1L)]
    [InlineData(GiB - 1)]
    public void 小於1GB是60秒(long bytes) => BackupTimeoutRules.CommandTimeoutSeconds(bytes).Should().Be(60);

    [Fact]
    public void 剛好1GB是90秒() => BackupTimeoutRules.CommandTimeoutSeconds(GiB).Should().Be(90);

    [Fact]
    public void 檔案3GB是150秒()
    {
        BackupTimeoutRules.CommandTimeoutSeconds(3 * GiB).Should().Be(150);
        BackupTimeoutRules.CommandTimeoutSeconds(4 * GiB - 1).Should().Be(150, "只算完整的 GiB");
    }

    [Fact]
    public void 備份與還原的連線_預設60秒_可依大小指定()
    {
        var dir = TestSupport.TempDir.Create();
        try
        {
            var path = Path.Combine(dir, "t.db");
            using (var connection = BackupService.OpenUnpooled(path))
                connection.DefaultTimeout.Should().Be(60, "還原的呼叫不變");
            using (var connection = BackupService.OpenUnpooled(path, BackupTimeoutRules.CommandTimeoutSeconds(3 * GiB)))
                connection.DefaultTimeout.Should().Be(150);
            BackupService.DatabaseBytes(path).Should().Be(new FileInfo(path).Length);
            BackupService.DatabaseBytes(Path.Combine(dir, "沒有這個檔.db")).Should().Be(0);
        }
        finally
        {
            TestSupport.TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public void 上限1800秒()
    {
        BackupTimeoutRules.CommandTimeoutSeconds(58 * GiB).Should().Be(1800, "60＋58×30＝1800");
        BackupTimeoutRules.CommandTimeoutSeconds(59 * GiB).Should().Be(1800);
        BackupTimeoutRules.CommandTimeoutSeconds(long.MaxValue).Should().Be(1800);
    }
}
