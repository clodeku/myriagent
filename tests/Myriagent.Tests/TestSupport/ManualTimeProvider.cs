namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 可以撥快的時鐘（照 MyriagentHub.Tests 的 ManualTimeProvider 寫，另外支援 <see cref="CreateTimer"/>）：計時器只在 <see cref="Advance"/>／<see cref="AdvanceAsync"/> 時觸發，
/// 依到期時間的先後、同時到期的依建立順序。本機時區固定 UTC，結果不隨執行環境改變。
/// <c>Task.Delay(delay, provider, ct)</c> 與 <c>PeriodicTimer</c> 都走 CreateTimer，所以配對輪詢與同步引擎的計時都撥得動。
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = new();
    private DateTimeOffset now;
    private long sequence;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public ManualTimeProvider(DateTimeOffset start) => now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public long NowMs => GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>目前排著的計時器數（已排定到期時間的）</summary>
    public int ActiveTimers
    {
        get
        {
            lock (gate)
                return timers.Count;
        }
    }

    /// <summary>到目前為止觸發過的計時器回呼次數</summary>
    public int FiredCount { get; private set; }

    /// <summary>每個排著的計時器距離到期還有多久（由近到遠）</summary>
    public IReadOnlyList<TimeSpan> DueIn()
    {
        lock (gate)
            return timers.Select(t => t.Due - now).Order().ToList();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>撥快並同步觸發到期的計時器（回呼裡新排的計時器若也到期，同樣觸發）</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (gate)
            target = now + by;
        while (FireNext(target))
        {
        }
    }

    /// <summary>撥快；每觸發一個計時器就等 <paramref name="settle"/>（讓收到回呼的執行緒做完它的工作、排好下一個計時器）</summary>
    public async Task AdvanceAsync(TimeSpan by, Func<Task> settle)
    {
        DateTimeOffset target;
        lock (gate)
            target = now + by;
        await settle();
        while (FireNext(target))
            await settle();
    }

    /// <summary>等到至少有 <paramref name="count"/> 個計時器排著（非同步流程走到 Task.Delay 了）</summary>
    public async Task WaitForTimersAsync(int count = 1, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (ActiveTimers < count)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"{timeoutMs} ms 內沒有排到 {count} 個計時器（目前 {ActiveTimers} 個）");
            await Task.Delay(5);
        }
    }

    private bool FireNext(DateTimeOffset target)
    {
        ManualTimer? next;
        lock (gate)
        {
            next = null;
            foreach (var timer in timers)
            {
                if (timer.Due <= target && (next is null || timer.Due < next.Due || (timer.Due == next.Due && timer.Sequence < next.Sequence)))
                    next = timer;
            }

            if (next is null)
            {
                if (target > now)
                    now = target;
                return false;
            }

            if (next.Due > now)
                now = next.Due;
            if (next.Period > TimeSpan.Zero)
            {
                next.Due += next.Period;
                next.Sequence = ++sequence;
            }
            else
            {
                timers.Remove(next);
            }

            FiredCount++;
        }

        next.Callback(next.State);
        return true;
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
                return;
            timer.Due = now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            timer.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            timer.Sequence = ++sequence;
            timers.Add(timer);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate)
            timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public DateTimeOffset Due { get; set; }

        public TimeSpan Period { get; set; }

        public long Sequence { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime, period);
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
