using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>HTTP Range（影片拖曳進度）解析與只讀一段的串流</summary>
public class RangeRulesTests
{
    [Theory]
    [InlineData("bytes=0-99", 1000L, 0L, 99L)]
    [InlineData("bytes=100-", 1000L, 100L, 999L)]
    [InlineData("bytes=-500", 1000L, 500L, 999L)]
    [InlineData("bytes=-5000", 1000L, 0L, 999L)]
    [InlineData("bytes=900-5000", 1000L, 900L, 999L)]
    [InlineData("bytes=999-999", 1000L, 999L, 999L)]
    [InlineData("  BYTES= 10 - 20 ", 1000L, 10L, 20L)]
    public void Parse_合法的單一區間(string header, long length, long start, long end)
        => RangeRules.Parse(header, length).Should().Be((start, end));

    [Theory]
    [InlineData("bytes=1000-", 1000L)]
    [InlineData("bytes=2000-3000", 1000L)]
    [InlineData("bytes=0-99,200-299", 1000L)]
    [InlineData("bytes=50-10", 1000L)]
    [InlineData("bytes=-0", 1000L)]
    [InlineData("bytes=-", 1000L)]
    [InlineData("bytes=abc-def", 1000L)]
    [InlineData("bytes=+5-10", 1000L)]
    [InlineData("bytes=10", 1000L)]
    [InlineData("items=0-10", 1000L)]
    [InlineData("bytes=0-10", 0L)]
    [InlineData("", 1000L)]
    [InlineData(null, 1000L)]
    public void Parse_不合法超出範圍或多區間回Null(string? header, long length)
        => RangeRules.Parse(header, length).Should().BeNull();

    private static MemoryStream Bytes(int count) => new(Enumerable.Range(0, count).Select(i => (byte)i).ToArray());

    [Fact]
    public void SubStream_只讀指定的範圍()
    {
        using var sub = new SubStream(Bytes(100), 10, 20);

        sub.Length.Should().Be(20);
        sub.CanRead.Should().BeTrue();
        sub.CanSeek.Should().BeTrue();
        sub.CanWrite.Should().BeFalse();
        var buffer = new byte[64];
        var read = sub.Read(buffer, 0, buffer.Length);
        read.Should().Be(20);
        buffer.Take(read).Should().Equal(Enumerable.Range(10, 20).Select(i => (byte)i));
        sub.Read(buffer, 0, buffer.Length).Should().Be(0, "讀完範圍就結束");
        sub.Position.Should().Be(20);
    }

    [Fact]
    public void SubStream_分次讀取不會超出範圍()
    {
        using var sub = new SubStream(Bytes(100), 95, 5);
        var buffer = new byte[3];
        sub.Read(buffer, 0, 3).Should().Be(3);
        buffer.Should().Equal(95, 96, 97);
        sub.Read(buffer, 0, 3).Should().Be(2);
        buffer.Take(2).Should().Equal((byte)98, (byte)99);
    }

    [Fact]
    public void SubStream_Seek以範圍內的位置計算並夾在範圍內()
    {
        using var sub = new SubStream(Bytes(100), 10, 20);
        var one = new byte[1];

        sub.Seek(5, SeekOrigin.Begin).Should().Be(5);
        sub.Read(one, 0, 1).Should().Be(1);
        one[0].Should().Be(15);

        sub.Seek(2, SeekOrigin.Current).Should().Be(8);
        sub.Read(one, 0, 1).Should().Be(1);
        one[0].Should().Be(18);

        sub.Seek(-1, SeekOrigin.End).Should().Be(19);
        sub.Read(one, 0, 1).Should().Be(1);
        one[0].Should().Be(29);

        sub.Seek(-50, SeekOrigin.Begin).Should().Be(0, "不能移到範圍之前");
        sub.Seek(500, SeekOrigin.Begin).Should().Be(20, "不能移到範圍之後");
        sub.Position = 3;
        sub.Read(one, 0, 1).Should().Be(1);
        one[0].Should().Be(13);
    }

    [Fact]
    public void SubStream_不能寫入_Dispose時關閉底層串流()
    {
        var inner = Bytes(10);
        var sub = new SubStream(inner, 0, 10);
        FluentActions.Invoking(() => sub.Write(new byte[1], 0, 1)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => sub.SetLength(1)).Should().Throw<NotSupportedException>();

        sub.Dispose();

        FluentActions.Invoking(() => inner.ReadByte()).Should().Throw<ObjectDisposedException>("檔案串流要跟著關，否則影片檔一直被鎖住");
    }

    [Fact]
    public void SubStream_搭配Parse的結果讀出Range內容()
    {
        var range = RangeRules.Parse("bytes=-4", 100)!.Value;
        using var sub = new SubStream(Bytes(100), range.Start, range.End - range.Start + 1);
        using var copy = new MemoryStream();
        sub.CopyTo(copy);
        copy.ToArray().Should().Equal(96, 97, 98, 99);
    }
}
