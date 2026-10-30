using System.Diagnostics;
using Myriagent.Core;
using Myriagent.Sync;

namespace Myriagent.SystemStatus;

/// <summary>一次取樣：<paramref name="Seq"/> 每次遞增，<paramref name="TakenAt"/> 是 Stopwatch 的時間戳</summary>
internal sealed record SystemSample(long Seq, long TakenAt, DeviceStats Stats);

/// <summary>這台電腦的狀態來源（中樞與同步引擎用；測試換成假的）</summary>
internal interface ISystemMonitor
{
    /// <summary>每秒取樣中（有視窗開著電腦狀態面板）。任何執行緒</summary>
    bool Active { get; set; }

    /// <summary>每次取樣之後（計時器的執行緒；處理常式要自己排回 UI 執行緒）</summary>
    event Action<SystemSample>? Sampled;

    /// <summary>最近一次取樣；還沒取過是 null。任何執行緒</summary>
    SystemSample? Latest { get; }

    /// <summary>不比 <paramref name="maxAge"/> 舊的一份：最近一次夠新就用它，否則現在取一次（同步引擎送出前呼叫；任何執行緒）</summary>
    SystemSample Current(TimeSpan maxAge);
}

/// <summary>
/// 這台電腦的狀態（stat1）：有視窗開著面板時每秒取樣一次（<see cref="Active"/>，打開時立刻取第一次），同步引擎送出時再以 <see cref="Current"/> 取一份夠新的。
/// 兩把鎖：<c>gate</c> 只管開關（UI 執行緒設定時不等取樣），<c>sampling</c> 序列化取樣（取樣器記著上一次的累計值）。
/// 不開面板也沒有同步時完全不取樣（閒置時不多一個喚醒）
/// </summary>
internal sealed class SystemMonitor : ISystemMonitor, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly object gate = new();
    private readonly object sampling = new();
    private readonly Func<DeviceStats> sample;
    private readonly System.Threading.Timer timer;
    private SystemSample? latest;
    private long seq;
    private bool active;
    private bool disposed;

    public SystemMonitor()
        : this(NativeStats.CreateSampler().Sample)
    {
    }

    internal SystemMonitor(Func<DeviceStats> sample)
    {
        this.sample = sample;
        timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<SystemSample>? Sampled;

    public SystemSample? Latest => Volatile.Read(ref latest);

    public bool Active
    {
        get
        {
            lock (gate)
                return active;
        }
        set
        {
            lock (gate)
            {
                if (disposed || active == value)
                    return;
                active = value;
                // 打開時立刻取第一次（面板不必空等一秒），之後每秒一次
                timer.Change(value ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, value ? Interval : Timeout.InfiniteTimeSpan);
            }
        }
    }

    public SystemSample Current(TimeSpan maxAge)
    {
        lock (sampling)
        {
            if (latest is { } recent && Stopwatch.GetElapsedTime(recent.TakenAt) <= maxAge)
                return recent;
            return TakeLocked();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            active = false;
        }
        timer.Dispose();
    }

    private void Tick()
    {
        lock (gate)
        {
            if (!active || disposed)
                return;
        }
        SystemSample taken;
        try
        {
            lock (sampling)
                taken = TakeLocked();
        }
        catch (Exception ex)
        {
            Log.Error("讀這台電腦的狀態失敗", ex);
            return;
        }
        try
        {
            Sampled?.Invoke(taken);
        }
        catch (Exception ex)
        {
            Log.Error("處理這台電腦的狀態失敗", ex);
        }
    }

    /// <summary>取一次樣（呼叫端持有 <c>sampling</c>）</summary>
    private SystemSample TakeLocked()
    {
        var taken = new SystemSample(++seq, Stopwatch.GetTimestamp(), sample());
        Volatile.Write(ref latest, taken);
        return taken;
    }
}
