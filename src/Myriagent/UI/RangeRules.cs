using Myriagent.Media;

namespace Myriagent.UI;

/// <summary>HTTP Range（影片拖曳進度用）的解析與有限長度的串流</summary>
internal static class RangeRules
{
    /// <summary>
    /// 解析「bytes=start-end」「bytes=start-」「bytes=-suffix」；只支援單一區間。不合法或超出範圍回 null（呼叫端回整個檔案或 416）。
    /// 解析本身委派伺服器共用的 <see cref="HttpRangeRules"/>（WP4.7 計畫 D40）：Full 與 Unsatisfiable 都回 null
    /// </summary>
    public static (long Start, long End)? Parse(string? header, long length)
    {
        if (length <= 0)
            return null;
        var (kind, range, _) = HttpRangeRules.Parse(header, length);
        return kind switch
        {
            RangeKind.Satisfiable or RangeKind.Suffix => (range.Start, range.End),
            RangeKind.Full or RangeKind.Unsatisfiable => null,
            _ => throw new InvalidOperationException($"不認得的 RangeKind：{kind}"),
        };
    }
}

/// <summary>只讀取底層串流其中一段的串流（Range 回應用）；Dispose 時一併關閉底層串流</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream inner;
    private readonly long start;
    private readonly long length;
    private long position;

    public SubStream(Stream inner, long start, long length)
    {
        this.inner = inner;
        this.start = start;
        this.length = length;
        inner.Seek(start, SeekOrigin.Begin);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = length - position;
        if (remaining <= 0)
            return 0;
        var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => length + offset,
        };
        position = Math.Clamp(target, 0, length);
        inner.Seek(start + position, SeekOrigin.Begin);
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
