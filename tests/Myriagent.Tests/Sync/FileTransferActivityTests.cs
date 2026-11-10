using Myriagent.Sync;

namespace Myriagent.Tests.Sync;

/// <summary>傳完一段之後 30 秒內還算在傳（分段下載的下一段請求還在路上）；從來沒有傳過不算</summary>
public sealed class FileTransferActivityTests
{
    [Fact]
    public void IsRecent_結束後30秒內還算在傳()
    {
        var window = FileTransferActivity.Linger;
        FileTransferActivity.IsRecent(long.MinValue, 1_000_000, window).Should().BeFalse("從來沒有傳過");
        FileTransferActivity.IsRecent(1_000_000, 1_000_000, window).Should().BeTrue();
        FileTransferActivity.IsRecent(1_000_000, 1_029_999, window).Should().BeTrue();
        FileTransferActivity.IsRecent(1_000_000, 1_030_000, window).Should().BeFalse();
        FileTransferActivity.IsRecent(1_000_000, 999_000, window).Should().BeFalse("時鐘倒退不算");
    }

    [Fact]
    public void Begin_傳的期間算在傳()
    {
        using (FileTransferActivity.Begin())
            FileTransferActivity.Active.Should().BeTrue();
        FileTransferActivity.Active.Should().BeTrue("剛結束，下一段還在路上");
    }
}
