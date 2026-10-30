using Myriagent.Media;

namespace Myriagent.Tests.Protocol.Media;

/// <summary>HTTP Range 的三態解析（WP4.7 計畫 D40；桌面 RangeRules 委派這裡）</summary>
public class HttpRangeRulesTests
{
    [Theory]
    [InlineData("bytes=0-", 0L, 9L)]
    [InlineData("bytes=0-99", 0L, 9L)]
    [InlineData("bytes=2-5", 2L, 5L)]
    [InlineData("bytes=9-9", 9L, 9L)]
    [InlineData("  BYTES= 3 - 4 ", 3L, 4L)]
    public void Parse_長度10的單一區間(string header, long start, long end)
    {
        var (kind, range, suffix) = HttpRangeRules.Parse(header, 10);

        kind.Should().Be(RangeKind.Satisfiable);
        range.Should().Be(new ByteRange(start, end));
        suffix.Should().Be(0);
    }

    [Fact]
    public void Parse_後綴範圍()
    {
        HttpRangeRules.Parse("bytes=-3", 10).Should().Be((RangeKind.Suffix, new ByteRange(7, 9), 3L));
        HttpRangeRules.Parse("bytes=-20", 10).Should().Be((RangeKind.Suffix, new ByteRange(0, 9), 20L), "比總長長就從 0 開始");
    }

    [Theory]
    [InlineData("bytes=10-")]
    [InlineData("bytes=10-20")]
    [InlineData("bytes=99-")]
    public void Parse_起點在結尾之後是Unsatisfiable(string header)
        => HttpRangeRules.Parse(header, 10).Kind.Should().Be(RangeKind.Unsatisfiable);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bytes=5-2")]
    [InlineData("items=0-1")]
    [InlineData("bytes=0-1,3-4")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=-")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=+1-2")]
    [InlineData("bytes=1")]
    [InlineData("bytes=99999999999999999999-")]
    public void Parse_不合格或不支援是Full(string? header)
        => HttpRangeRules.Parse(header, 10).Should().Be((RangeKind.Full, default(ByteRange), 0L));

    [Fact]
    public void Parse_長度0一律Full()
    {
        HttpRangeRules.Parse("bytes=0-", 0).Kind.Should().Be(RangeKind.Full);
        HttpRangeRules.Parse("bytes=-3", 0).Kind.Should().Be(RangeKind.Full);
    }

    [Fact]
    public void Parse_長度未知時結尾待定()
    {
        var (kind, range, _) = HttpRangeRules.Parse("bytes=0-", null);
        kind.Should().Be(RangeKind.Satisfiable);
        range.Should().Be(new ByteRange(0, ByteRange.OpenEnd));
        range.IsOpenEnded.Should().BeTrue();

        HttpRangeRules.Parse("bytes=0-99", null).Should().Be((RangeKind.Satisfiable, new ByteRange(0, 99), 0L));
        HttpRangeRules.Parse("bytes=10-", null).Kind.Should().Be(RangeKind.Satisfiable, "不知道長度就判斷不了起點是否在結尾之後");
    }

    [Fact]
    public void Parse_長度未知的後綴範圍只給長度()
    {
        var (kind, range, suffix) = HttpRangeRules.Parse("bytes=-3", null);

        kind.Should().Be(RangeKind.Suffix);
        suffix.Should().Be(3);
        range.Should().Be(new ByteRange(ByteRange.OpenEnd, ByteRange.OpenEnd));
    }

    [Fact]
    public void Parse_負的長度丟例外()
    {
        var act = () => HttpRangeRules.Parse("bytes=0-", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ContentRange與Unsatisfied()
    {
        HttpRangeRules.ContentRange(new ByteRange(0, 9), 10).Should().Be("bytes 0-9/10");
        HttpRangeRules.ContentRange(new ByteRange(7, 9), 10).Should().Be("bytes 7-9/10");
        HttpRangeRules.Unsatisfied(10).Should().Be("bytes */10");
    }

    [Theory]
    [InlineData(0L, 10L)]
    [InlineData(5L, 4L)]
    [InlineData(-1L, 3L)]
    [InlineData(0L, ByteRange.OpenEnd)]
    public void ContentRange_範圍不在長度內丟例外(long start, long end)
    {
        var act = () => HttpRangeRules.ContentRange(new ByteRange(start, end), 10);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("bytes 0-9/10", 0L, 9L, 10L)]
    [InlineData("bytes 7-9/10", 7L, 9L, 10L)]
    [InlineData("BYTES  0-0/1 ", 0L, 0L, 1L)]
    [InlineData("bytes 16777216-20971519/20971520", 16777216L, 20971519L, 20971520L)]
    public void TryParseContentRange_合格(string header, long start, long end, long length)
    {
        HttpRangeRules.TryParseContentRange(header, out var range, out var total).Should().BeTrue();

        range.Should().Be(new ByteRange(start, end));
        total.Should().Be(length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bytes */10")]
    [InlineData("bytes 0-9/*")]
    [InlineData("bytes 0-10/10")]
    [InlineData("bytes 5-4/10")]
    [InlineData("bytes -1-4/10")]
    [InlineData("bytes 0-9")]
    [InlineData("items 0-9/10")]
    [InlineData("bytes=0-9/10")]
    [InlineData("bytes 0-+9/10")]
    [InlineData("bytes 0-9/99999999999999999999")]
    public void TryParseContentRange_不合格(string? header)
        => HttpRangeRules.TryParseContentRange(header, out _, out _).Should().BeFalse();

    [Fact]
    public void TryParseContentRange_是ContentRange的反向()
    {
        var text = HttpRangeRules.ContentRange(new ByteRange(3, 8), 20);

        HttpRangeRules.TryParseContentRange(text, out var range, out var total).Should().BeTrue();
        range.Should().Be(new ByteRange(3, 8));
        total.Should().Be(20);
    }

    [Fact]
    public void ByteRange_長度()
    {
        new ByteRange(2, 5).Length.Should().Be(4);
        var act = () => new ByteRange(2, ByteRange.OpenEnd).Length;
        act.Should().Throw<InvalidOperationException>();
    }
}
