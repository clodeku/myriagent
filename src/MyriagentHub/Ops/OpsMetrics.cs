using System.Diagnostics;

namespace MyriagentHub.Ops;

/// <summary>一次程序取樣（/readyz、ops status 查詢當下取一次；記憶體保險絲每 Hub:MemoryFuse:SampleSeconds 秒也取一次）</summary>
internal sealed record ProcessSample(long At, long PrivateBytes, long WorkingSetBytes, int Threads, double CpuPercent, double CpuWindowSeconds);

/// <summary>
/// 營運指標：連線數、每分鐘 5xx、程序 CPU 與記憶體取樣。記憶體與執行緒數是取樣當下的值；CPU 是距上一次取樣
/// （保險絲的定期取樣或上一次查詢）這段時間的平均，時間窗至少 <see cref="MinCpuWindow"/>，太短就沿用上一次的值。
/// </summary>
internal sealed class OpsMetrics(TimeProvider time)
{
    /// <summary>CPU 平均的最短時間窗：兩次取樣隔太近（例如連續查詢）時算出來的百分比只是雜訊</summary>
    public static readonly TimeSpan MinCpuWindow = TimeSpan.FromMilliseconds(200);

    private readonly object gate = new();
    private readonly object sampleGate = new();
    private readonly Queue<long> serverErrors = new();
    private long activeConnections;
    private long totalConnections;
    private TimeSpan lastCpu;
    private long lastCpuSampleTicks;
    private ProcessSample last = new(0, 0, 0, 0, 0, 0);

    public long ActiveConnections => Interlocked.Read(ref activeConnections);

    public long TotalConnections => Interlocked.Read(ref totalConnections);

    /// <summary>最近一次取樣</summary>
    public ProcessSample Last => Volatile.Read(ref last);

    /// <summary>最近一次取樣的程序 CPU（佔全部邏輯處理器的百分比）</summary>
    public double CpuPercent => Last.CpuPercent;

    public long PrivateBytes => Last.PrivateBytes;

    public long WorkingSetBytes => Last.WorkingSetBytes;

    public void ConnectionOpened()
    {
        Interlocked.Increment(ref activeConnections);
        Interlocked.Increment(ref totalConnections);
    }

    public void ConnectionClosed() => Interlocked.Decrement(ref activeConnections);

    public void RecordServerError(long now)
    {
        lock (gate)
        {
            serverErrors.Enqueue(now);
            Trim(now);
        }
    }

    public int ServerErrorsLastMinute(long now)
    {
        lock (gate)
        {
            Trim(now);
            return serverErrors.Count;
        }
    }

    /// <summary>
    /// 取樣程序的私有記憶體、工作集、執行緒數與 CPU（只讀自己的程序，不啟動任何程序）。可同時從查詢與保險絲呼叫。
    /// </summary>
    public ProcessSample Sample()
    {
        lock (sampleGate)
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var ticks = Stopwatch.GetTimestamp();
            var previous = last;
            var cpuPercent = previous.CpuPercent;
            var window = previous.CpuWindowSeconds;
            if (lastCpuSampleTicks == 0)
            {
                lastCpu = cpu;
                lastCpuSampleTicks = ticks;
            }
            else
            {
                var wall = Stopwatch.GetElapsedTime(lastCpuSampleTicks, ticks);
                if (wall >= MinCpuWindow)
                {
                    cpuPercent = Math.Round((cpu - lastCpu).TotalMilliseconds / (wall.TotalMilliseconds * Environment.ProcessorCount) * 100, 2);
                    window = Math.Round(wall.TotalSeconds, 3);
                    lastCpu = cpu;
                    lastCpuSampleTicks = ticks;
                }
            }
            var sample = new ProcessSample(time.GetUtcNow().ToUnixTimeMilliseconds(), process.PrivateMemorySize64, process.WorkingSet64, process.Threads.Count, cpuPercent, window);
            Volatile.Write(ref last, sample);
            return sample;
        }
    }

    private void Trim(long now)
    {
        while (serverErrors.Count > 0 && now - serverErrors.Peek() >= 60_000)
            serverErrors.Dequeue();
    }
}

/// <summary>回應狀態碼 ≥500 的計數（每分鐘 5xx；未處理的例外也算）</summary>
internal sealed class ServerErrorCounterMiddleware(RequestDelegate next, OpsMetrics metrics, TimeProvider time, ILogger<ServerErrorCounterMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            metrics.RecordServerError(time.GetUtcNow().ToUnixTimeMilliseconds());
            log.LogError(ex, "未處理的例外：{Method} {Path}", context.Request.Method, context.Request.Path.Value);
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "internal_error" });
            }
            return;
        }
        if (context.Response.StatusCode >= 500)
            metrics.RecordServerError(time.GetUtcNow().ToUnixTimeMilliseconds());
    }
}
