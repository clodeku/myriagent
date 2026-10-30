using System.Globalization;
using System.Runtime;
using Myriagent.Sync;
using MyriagentHub.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Ops;

/// <summary>
/// 伺服器的活動（閒置修剪用，<see cref="IdleTrimRules"/>）：最後一次活動的時間、活動的次數與處理中的請求與 hub 呼叫數。任何執行緒都可以呼叫；
/// 每次只寫幾個 long，不排計時器。啟動也算一次活動（啟動之後靜置滿才第一次回收）
/// </summary>
internal sealed class HubActivity(TimeProvider time)
{
    private long lastAt = time.GetTimestamp();
    private long version;
    private int inFlight;

    /// <summary>活動的次數（每次開始、結束與 <see cref="Note"/> 都加 1）：和上一次回收時記下的相同＝之後沒有新的活動</summary>
    public long Version => Interlocked.Read(ref version);

    /// <summary>處理中的請求與 hub 呼叫數</summary>
    public int InFlight => Volatile.Read(ref inFlight);

    /// <summary>距最後一次活動多久</summary>
    public TimeSpan IdleFor => time.GetElapsedTime(Volatile.Read(ref lastAt));

    /// <summary>一個請求或 hub 呼叫開始（之後一定要 <see cref="End"/>）</summary>
    public void Begin()
    {
        Interlocked.Increment(ref inFlight);
        Note();
    }

    /// <summary>一個請求或 hub 呼叫結束</summary>
    public void End()
    {
        Interlocked.Decrement(ref inFlight);
        Note();
    }

    /// <summary>記一次活動</summary>
    public void Note()
    {
        Volatile.Write(ref lastAt, time.GetTimestamp());
        Interlocked.Increment(ref version);
    }
}

/// <summary>HTTP 請求記成活動（最外層；<see cref="IdleTrimRules.IsBackgroundRequest"/> 的不記）：處理中的請求在 <see cref="HubActivity.InFlight"/> 裡</summary>
internal sealed class HubActivityMiddleware(RequestDelegate next, HubActivity activity)
{
    public Task InvokeAsync(HttpContext context) =>
        IdleTrimRules.IsBackgroundRequest(context.Request.Path.Value) ? next(context) : CountedAsync(context);

    private async Task CountedAsync(HttpContext context)
    {
        activity.Begin();
        try
        {
            await next(context);
        }
        finally
        {
            activity.End();
        }
    }
}

/// <summary>
/// 裝置與瀏覽器 hub 的每一次呼叫記成活動（全域的過濾器，兩個 hub 都套用；<see cref="IdleTrimRules.CountsAsActivity"/> 的背景往來不記）
/// </summary>
internal sealed class HubActivityFilter(HubActivity activity) : IHubFilter
{
    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next) =>
        IdleTrimRules.CountsAsActivity(invocationContext.HubMethodName) ? CountedAsync(invocationContext, next) : next(invocationContext);

    private async ValueTask<object?> CountedAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        activity.Begin();
        try
        {
            return await next(invocationContext);
        }
        finally
        {
            activity.End();
        }
    }
}

/// <summary>量 GC 已提交的空間、釋放 DLP 樣式、做積極回收的地方（閒置修剪的測試換成假的；同桌面的 <c>IDlpHeapPort</c>）</summary>
internal interface IHubHeapPort
{
    /// <summary>目前 GC 已提交空間的估計（位元組，<see cref="IdleTrimRules.CommittedEstimate"/>）</summary>
    long CommittedBytes();

    /// <summary>已編譯的 DLP 樣式在不在</summary>
    bool DlpCompiled { get; }

    /// <summary>丟掉已編譯的 DLP 樣式（下一次遮蔽時重建）</summary>
    void ReleaseDlp();

    /// <summary>阻塞、壓縮的積極回收（含大型物件堆積的壓縮一次）</summary>
    void Compact();
}

/// <summary>這個程序真的 GC 與共用的 DLP 樣式</summary>
internal sealed class ProcessHubHeap : IHubHeapPort
{
    public long CommittedBytes() => IdleTrimRules.CommittedEstimate(GC.GetGCMemoryInfo().TotalCommittedBytes, GC.GetTotalMemory(false));

    public bool DlpCompiled => CredentialLeakRules.IsCompiled;

    public void ReleaseDlp() => CredentialLeakRules.ReleaseCompiledPatterns();

    public void Compact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}

/// <summary>
/// 閒置修剪（<see cref="IdleTrimRules"/>；<c>Hub:IdleTrim:Enabled</c>，預設開著，測試主機關掉）：每 <see cref="IdleTrimRules.CheckPeriod"/> 看一次
/// <see cref="HubActivity"/>，要修剪時先釋放已編譯的 DLP 樣式、再積極回收，記一行記錄、<see cref="Trims"/> 加 1（ops/status 的 <c>process.idleTrims</c>）。
/// 計時器用注入的 <see cref="TimeProvider"/>（測試撥快），每次檢查完才排下一次（不重疊）；停止時取消
/// </summary>
internal sealed class IdleTrimService(HubActivity activity, IHubHeapPort heap, TimeProvider time, IOptions<HubOptions> options, ILogger<IdleTrimService> log)
    : IHostedService, IDisposable
{
    private readonly Lock gate = new();
    private ITimer? timer;
    private bool stopped;
    private int trims;

    /// <summary>上一次回收的時間（<see cref="TimeProvider.GetTimestamp"/>；這次啟動之後還沒回收過是 null）</summary>
    private long? compactedAt;

    /// <summary>上一次回收時的 <see cref="HubActivity.Version"/>（相同＝之後沒有新的活動）</summary>
    private long compactedVersion;

    /// <summary>上一次回收之後的 GC 已提交空間</summary>
    private long committedAfter;

    /// <summary>閒置修剪（含同一段閒置裡的再回收）的次數</summary>
    public int Trims => Volatile.Read(ref trims);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.IdleTrim.Enabled)
        {
            lock (gate)
                timer = time.CreateTimer(_ => Check(), null, IdleTrimRules.CheckPeriod, Timeout.InfiniteTimeSpan);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (gate)
        {
            stopped = true;
            timer?.Dispose();
            timer = null;
        }
    }

    /// <summary>計時器到期：照 <see cref="IdleTrimRules.Decide"/> 做，之後再排下一次</summary>
    internal void Check()
    {
        lock (gate)
        {
            if (stopped)
                return;
            try
            {
                CheckOnce();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "閒置修剪失敗");
            }
            timer?.Change(IdleTrimRules.CheckPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckOnce()
    {
        var version = activity.Version;
        TimeSpan? since = compactedAt is { } at ? time.GetElapsedTime(at) : null;
        var growth = since is null ? 0 : heap.CommittedBytes() - committedAfter;
        var dlpCompiled = heap.DlpCompiled;
        var action = IdleTrimRules.Decide(activity.IdleFor, activity.InFlight, since is not null && version == compactedVersion, since, growth, dlpCompiled);
        if (action == IdleTrimAction.None)
            return;

        if (dlpCompiled)
            heap.ReleaseDlp();
        var before = heap.CommittedBytes();
        var started = time.GetTimestamp();
        heap.Compact();
        var took = time.GetElapsedTime(started);
        compactedAt = time.GetTimestamp();
        compactedVersion = version;
        committedAfter = heap.CommittedBytes();
        Interlocked.Increment(ref trims);
        log.LogInformation("閒置修剪：{Action}（{Dlp}；GC 已提交約 {BeforeMb} → {AfterMb} MB，{TookMs} ms）",
            action == IdleTrimAction.Trim ? "積極回收" : $"再回收（距上一次 {since!.Value.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} 分鐘、已提交多了 {Mb(growth)} MB）",
            dlpCompiled ? "釋放 DLP 樣式的快取" : "沒有 DLP 樣式要釋放", Mb(before), Mb(committedAfter), took.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture));
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
}
