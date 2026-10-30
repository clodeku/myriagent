namespace Myriagent.Launch;

/// <param name="Lines">這次給的行（依序）</param>
/// <param name="Epoch">第幾次執行的輸出（每次啟動清空、加一）</param>
/// <param name="Seq">目前最後一行的序號（這個 epoch 裡從 1 起算；沒有輸出是 0）</param>
/// <param name="Reset">給的是緩衝區的全部（呼叫端要的序號不在這個 epoch 或已被丟掉）：網頁換掉整份，否則接在後面</param>
internal sealed record LaunchLogSlice(IReadOnlyList<string> Lines, long Epoch, long Seq, bool Reset);

/// <summary>
/// 一個目標的輸出（建置與執行的 stdout、stderr）：只留最後 capacity 行。讀取程序輸出的背景執行緒寫入、UI 執行緒讀，所以自己加鎖。
/// 每次啟動 <see cref="Restart"/> 清空、epoch 加一，網頁看到 epoch 變了就整份重拿
/// </summary>
internal sealed class LaunchLog(int capacity)
{
    private readonly object gate = new();
    private readonly Queue<string> lines = new();
    private long epoch;
    private long seq;

    public long Epoch
    {
        get
        {
            lock (gate)
                return epoch;
        }
    }

    public long Seq
    {
        get
        {
            lock (gate)
                return seq;
        }
    }

    /// <summary>目前緩衝區裡的所有行（建置失敗時找錯誤）</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (gate)
            return lines.ToList();
    }

    public void Append(string line)
    {
        lock (gate)
        {
            lines.Enqueue(line);
            seq++;
            while (lines.Count > capacity)
                lines.Dequeue();
        }
    }

    /// <summary>還是這個 epoch 才加（停掉的上一次執行晚到的輸出不混進新的一次）；加了回 true</summary>
    public bool AppendIf(long expectedEpoch, string line)
    {
        lock (gate)
        {
            if (epoch != expectedEpoch)
                return false;
            Append(line);
            return true;
        }
    }

    /// <summary>新的一次執行：清空、換 epoch</summary>
    public void Restart()
    {
        lock (gate)
        {
            lines.Clear();
            seq = 0;
            epoch++;
        }
    }

    /// <summary>
    /// 序號 after 之後的行。<paramref name="knownEpoch"/> 不是目前的、或 after 之後的第一行已被丟掉時給整份（Reset）；
    /// after 比目前的序號還大（不該發生）也給整份
    /// </summary>
    public LaunchLogSlice Read(long knownEpoch, long after)
    {
        lock (gate)
        {
            var first = seq - lines.Count + 1;   // 緩衝區第一行的序號
            if (knownEpoch != epoch || after < first - 1 || after > seq)
                return new LaunchLogSlice(lines.ToList(), epoch, seq, Reset: true);
            return new LaunchLogSlice(lines.Skip((int)(after - (first - 1))).ToList(), epoch, seq, Reset: false);
        }
    }
}
