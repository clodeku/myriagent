using Myriagent.Media;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Media;

/// <summary>媒體轉送的單段 8 MB 與沒帶 Range 時的分段（WP4.7 計畫 D11）</summary>
public class MediaRelayRulesTests
{
    [Fact]
    public void 上限常數照設計()
    {
        MediaRelayRules.MaxSegmentBytes.Should().Be(8L * 1024 * 1024);
        MediaRelayRules.MaxUnrangedBytes.Should().Be(64L * 1024 * 1024);
        MediaRelayRules.MaxUnrangedFileBytes.Should().Be(512L * 1024 * 1024, "檔案下載（/f/）的大簡報不帶 Range 也要能整份下載");
        MediaRelayRules.MaxConcurrentPerDevice.Should().Be(20);
    }

    [Fact]
    public void ClampSegment_開放式與太長的縮成8MB()
    {
        MediaRelayRules.ClampSegment(0, null).Should().Be(new MediaRange(0, 8388607));
        MediaRelayRules.ClampSegment(0, 8388608).Should().Be(new MediaRange(0, 8388607));
        MediaRelayRules.ClampSegment(100, null).Should().Be(new MediaRange(100, 100 + 8388607));
        MediaRelayRules.ClampSegment(long.MaxValue - 10, null).Should().Be(new MediaRange(long.MaxValue - 10, long.MaxValue), "不溢位");
    }

    [Fact]
    public void ClampSegment_短的照原樣()
    {
        MediaRelayRules.ClampSegment(100, 200).Should().Be(new MediaRange(100, 200));
        MediaRelayRules.ClampSegment(0, 8388607).Should().Be(new MediaRange(0, 8388607));
        MediaRelayRules.ClampSegment(5, 5).Should().Be(new MediaRange(5, 5));
    }

    [Theory]
    [InlineData(5L, 4L)]
    [InlineData(0L, -1L)]
    [InlineData(-1L, null)]
    public void ClampSegment_不合格丟例外(long start, long? end)
    {
        var act = () => MediaRelayRules.ClampSegment(start, end);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Segments_20MB正好三段()
    {
        var segments = MediaRelayRules.Segments(0, 20971519).ToList();

        segments.Should().Equal(
            new MediaRange(0, 8388607),
            new MediaRange(8388608, 16777215),
            new MediaRange(16777216, 20971519));
    }

    [Fact]
    public void Segments_邊界()
    {
        MediaRelayRules.Segments(0, 0).Should().Equal(new MediaRange(0, 0));
        MediaRelayRules.Segments(0, 8388607).Should().Equal(new MediaRange(0, 8388607));
        MediaRelayRules.Segments(0, 8388608).Should().Equal(new MediaRange(0, 8388607), new MediaRange(8388608, 8388608));
        MediaRelayRules.Segments(10, 20).Should().Equal(new MediaRange(10, 20));
        MediaRelayRules.Segments(long.MaxValue - 10, long.MaxValue).Should().Equal(new MediaRange(long.MaxValue - 10, long.MaxValue));
    }

    [Fact]
    public void Segments_結尾在起點之前丟例外()
    {
        var act = () => MediaRelayRules.Segments(5, 4);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
