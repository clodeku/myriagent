using System.Buffers;

namespace MyriagentHub.Media;

/// <summary>
/// 媒體轉送管線的區段來源（S9 閒置記憶體的修正，設計 14.20）：還有區段借在外面時重用還回來的，最後一個借出的區段還回來就全部放掉、交給 GC。
/// 管線沒給 pool 時用的是 <c>ArrayPool&lt;byte&gt;.Shared</c>，它每個邏輯處理器、每種大小都留一疊陣列，只在 gen2 回收時慢慢修（壓力低時每次每疊一個），
/// 轉送大量並行之後那些區段一直留在池裡（WP4.9 步 20 的 heap dump：925 個 64 KB 的陣列 60.6 MB，閒置停在約 300 MB）。
/// 也不能每段都新配：暫停的管線裡的區段活過 gen0、死在 gen2，並行轉送期間私有記憶體比重用時高（本機 4 台、轉送尖峰 382.6 MB，逼近 400 MB 的告警線）。
/// 留著的量不超過同時借出的最大量。區段固定 <see cref="MediaRelay.SegmentBytes"/>（64 KB，小於大型物件堆積的門檻），其他大小的照配、不留。
/// <see cref="MaxBufferSize"/> 是 <see cref="int.MaxValue"/>：管線遇到比上限大的要求時會改向共用的 ArrayPool 要，這裡不讓它發生
/// </summary>
internal sealed class RelaySegmentPool : MemoryPool<byte>
{
    private readonly Lock gate = new();
    private readonly Stack<byte[]> free = new();
    private int outstanding;
    private long rented;
    private long allocated;

    /// <summary>借過幾次（測試確認轉送真的用這個池）</summary>
    public long Rented => Interlocked.Read(ref rented);

    /// <summary>新配過幾個陣列（其他的是重用）</summary>
    public long Allocated => Interlocked.Read(ref allocated);

    /// <summary>借在外面的區段數與留著等下一次借的區段數</summary>
    public (int Outstanding, int Cached) Counts
    {
        get
        {
            lock (gate)
                return (outstanding, free.Count);
        }
    }

    public override int MaxBufferSize => int.MaxValue;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        Interlocked.Increment(ref rented);
        lock (gate)
        {
            outstanding++;
            if (minBufferSize <= MediaRelay.SegmentBytes && free.TryPop(out var reused))
                return new Segment(this, reused);
        }

        Interlocked.Increment(ref allocated);
        return new Segment(this, new byte[Math.Max(minBufferSize, MediaRelay.SegmentBytes)]);
    }

    protected override void Dispose(bool disposing)
    {
        lock (gate)
            free.Clear();
    }

    /// <summary>還回一個區段：還有借在外面的就留著（只留標準大小的），這是最後一個就連留著的一起放掉</summary>
    private void Return(byte[] array)
    {
        lock (gate)
        {
            if (--outstanding == 0)
                free.Clear();
            else if (array.Length == MediaRelay.SegmentBytes)
                free.Push(array);
        }
    }

    /// <summary>一個區段：還回來（<see cref="Dispose"/>，只算第一次）之後不再指向陣列</summary>
    private sealed class Segment(RelaySegmentPool pool, byte[] array) : IMemoryOwner<byte>
    {
        private byte[]? data = array;

        public Memory<byte> Memory => data ?? throw new ObjectDisposedException(nameof(Segment));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref data, null) is { } returned)
                pool.Return(returned);
        }
    }
}
