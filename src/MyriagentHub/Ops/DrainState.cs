using MyriagentHub.Audit;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Ops;

/// <summary>
/// 部署前的排空狀態（管理收件匣或部署收件匣的 drain）：/readyz 回 503、第 3 階段起廣播 ServerNotice{shutdown, retryAfterSec}、
/// 停止接受新的遠端指令。只在記憶體裡：服務重啟後自動恢復。部署的 drain 有期限（<see cref="DrainRules"/>，到期由
/// <see cref="DrainExpiryService"/> 解除）。
/// 狀態是一個不可變的 <see cref="DrainSnapshot"/>，每次變更在鎖裡由純函式算出新快照整個換掉：讀的人（/readyz、ops status、
/// 第 3 階段的指令閘門）只讀 <see cref="Current"/> 一次，不會看到旗標、來源、期限各自更新到一半的組合。
/// </summary>
internal sealed class DrainState
{
    private readonly object gate = new();
    private DrainSnapshot current = DrainSnapshot.None;

    /// <summary>目前的排空狀態（同一個判斷要用到多個欄位時只讀一次）</summary>
    public DrainSnapshot Current => Volatile.Read(ref current);

    public bool IsDraining => Current.Draining;

    public long? Since => Current.Since;

    /// <summary>排空時給裝置的重連建議（秒）</summary>
    public int RetryAfterSec => Current.RetryAfterSec;

    /// <summary>
    /// 排空狀態變了（參數：變更之後是否排空中；期限重設、來源改變也算）。在狀態鎖裡依變更的先後呼叫，
    /// 所以收到的順序就是狀態的順序；處理器不可以阻塞（第 3 階段的廣播要丟進佇列再送）。
    /// </summary>
    public event Action<bool>? Changed;

    /// <summary>收到 drain（規則見 <see cref="DrainRules.Drain"/>）；回傳之後的狀態</summary>
    public DrainSnapshot Drain(DrainSource source, long now, int retryAfterSec, int deployMaxSeconds, string? requestId) =>
        Apply(s => DrainRules.Drain(s, source, now, retryAfterSec, deployMaxSeconds, requestId));

    /// <summary>收到 undrain：不論誰要求的排空都解除</summary>
    public DrainSnapshot Undrain() => Apply(_ => DrainRules.Undrain());

    /// <summary>部署的排空到期、而且服務不在停止中就解除；回傳被解除的那個排空，沒有解除回 null</summary>
    public DrainSnapshot? ExpireIfDue(long now, bool stopping)
    {
        lock (gate)
        {
            var before = current;
            if (!DrainRules.ShouldAutoUndrain(before, now, stopping))
                return null;
            Replace(before, DrainRules.Undrain());
            return before;
        }
    }

    private DrainSnapshot Apply(Func<DrainSnapshot, DrainSnapshot> change)
    {
        lock (gate)
        {
            var before = current;
            var after = change(before);
            Replace(before, after);
            return after;
        }
    }

    /// <summary>在鎖裡呼叫：狀態真的變了才換掉並通知</summary>
    private void Replace(DrainSnapshot before, DrainSnapshot after)
    {
        if (after == before)
            return;
        Volatile.Write(ref current, after);
        Changed?.Invoke(after.Draining);
    }
}

/// <summary>
/// 部署的排空到期自動解除（Hub:Deploy:DrainMaxSeconds，預設 300 秒）：安裝腳本的停止要求沒送到服務時（結束碼 4 stop-failed、
/// 服務仍是 Running）服務還在跑，不解除就一直排空到重啟。每秒（真實時間）以 TimeProvider 的時間比一次期限：到期而且不在停止中
/// 就解除、記警告與稽核（actor system、server.drain、target undrain）；DrainState.Changed 由解除本身觸發。擁有者的排空永不過期。
/// SCM 的停止要求一送到，WindowsServiceLifetime.OnStop 就先呼叫 StopApplication（ApplicationStopping），之後不解除：
/// 停止逾時（STOP_PENDING）的服務會自己結束，結束後要管理員啟動。
/// </summary>
internal sealed class DrainExpiryService(
    DrainState drain,
    AuditLog audit,
    TimeProvider time,
    IOptions<HubOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<DrainExpiryService> log) : BackgroundService
{
    /// <summary>檢查間隔（真實時間）：期限精確到 1 秒就夠，每次只比一個時間</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckAsync();
        }
        catch (OperationCanceledException)
        {
            // 服務停止
        }
    }

    /// <summary>檢查一次；解除了回 true（解除之後的稽核一定寫完，不隨停止取消）</summary>
    public async Task<bool> CheckAsync()
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (drain.ExpireIfDue(now, lifetime.ApplicationStopping.IsCancellationRequested) is not { } expired)
            return false;
        var maxSeconds = options.Value.Deploy.DrainMaxSeconds;
        var drainedSeconds = (now - (expired.Since ?? now)) / 1000;
        log.LogWarning("部署的排空逾時自動解除：安裝腳本可能沒有停掉服務（{RequestId}，已排空 {Seconds} 秒，上限 Hub:Deploy:DrainMaxSeconds={MaxSeconds}）",
            expired.RequestId, drainedSeconds, maxSeconds);
        await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.Drain, "undrain", AuditOutcomes.Ok,
            new { reason = "deploy_drain_expired", requestId = expired.RequestId, since = expired.Since, expiresAt = expired.ExpiresAt, maxSeconds }));
        return true;
    }
}
