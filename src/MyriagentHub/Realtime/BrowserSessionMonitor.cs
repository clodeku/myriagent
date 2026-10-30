using MyriagentHub.Auth;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器 hub 連線的工作階段監看（設計 8.2.2、14.6；SEC-04）：長連線不會再經過 cookie 驗證，所以
/// <list type="bullet">
/// <item>工作階段被撤銷（登出、撤銷、改密碼、重新登入換 key、reset-owner、還原）時，<see cref="SessionEvents.Revoked"/> 立即中斷它的所有連線；</item>
/// <item>每 <c>SessionSweepSeconds</c>（真實時間）巡檢一次：失效（閒置、絕對期限、撤銷但沒收到事件——例如其他程序直接改 hub.db）就中斷；
/// 上一輪以來 hub 上有呼叫的工作階段延長閒置期限（<see cref="SqliteTicketStore.TouchAsync"/>）。</item>
/// </list>
/// 每一輪都有最外層例外攔截（只放行取消）：巡檢的錯誤不能讓伺服器停機（Hub 主機可能同時跑著其他重要服務）。
/// </summary>
internal sealed class BrowserSessionMonitor(
    BrowserConnections connections,
    SessionService sessions,
    SqliteTicketStore tickets,
    SessionEvents events,
    TimeProvider time,
    IOptions<HubOptions> options,
    ILogger<BrowserSessionMonitor> log) : BackgroundService
{
    public const string RevokedReason = "session_revoked";

    public const string ExpiredReason = "session_expired";

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // 先訂閱再開始巡檢：啟動後第一條連線登記時事件一定接得到
        events.Revoked += OnRevoked;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        events.Revoked -= OnRevoked;
        await base.StopAsync(cancellationToken);
    }

    /// <summary>這個工作階段現在還有效嗎（hub.db；不存在、已撤銷、閒置或絕對期限到了都是無效）</summary>
    public async Task<bool> IsSessionValidAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var row = await sessions.FindAsync(sessionId, cancellationToken);
        return row is not null && SessionRules.IsValid(SqliteTicketStore.ToTimes(row), time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 一輪巡檢：先丟掉沒有連線而且補滿的權杖桶（<see cref="BrowserConnections.PruneIdleBuckets"/>）；每個有連線的工作階段查一次，無效就中斷它的連線；
    /// 有效而且上一輪以來有活動就延長閒置期限（延長時發現已失效也中斷）。回傳這一輪中斷的連線數。資料庫讀取失敗原樣往外丟（由 <see cref="ExecuteAsync"/> 的每一輪攔截）
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        connections.PruneIdleBuckets(time.GetUtcNow().ToUnixTimeMilliseconds());
        var aborted = 0;
        foreach (var sessionId in connections.ActiveSessions())
        {
            if (!await IsSessionValidAsync(sessionId, cancellationToken))
            {
                aborted += AbortSession(sessionId, ExpiredReason);
                continue;
            }
            // 每一條都要取走（不短路），下一輪才不會重複算
            var mine = connections.Snapshot().Where(c => c.SessionId == sessionId).ToList();
            var active = false;
            foreach (var connection in mine)
                active |= connection.TakeActivity();
            if (!active)
                continue;
            var ip = mine.OrderByDescending(c => c.LastActivityMs).Select(c => c.Ip).FirstOrDefault(i => i is not null);
            if (!await tickets.TouchAsync(sessionId, ip, cancellationToken))
                aborted += AbortSession(sessionId, ExpiredReason);
        }
        return aborted;
    }

    /// <summary>
    /// 撤銷事件（在撤銷者的執行緒上同步呼叫）：只中斷連線、不阻塞，整個包 try/catch——
    /// SqliteTicketStore.RemoveAsync 發事件時沒有保護，這裡丟出例外會讓登出失敗
    /// </summary>
    internal void OnRevoked(string sessionId)
    {
        try
        {
            connections.AbortSession(sessionId, RevokedReason);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "工作階段撤銷後中斷瀏覽器連線失敗：{Session}", SessionRules.ShortId(sessionId));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.Browser.SessionSweepSeconds));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, stoppingToken);
                await SweepRoundAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 服務停止
        }
    }

    /// <summary>一輪巡檢的最外層：服務停止的取消以外，例外一律記錄後吞掉（下一輪再試）</summary>
    internal async Task SweepRoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            log.LogError(ex, "瀏覽器連線的工作階段巡檢失敗");
        }
    }

    private int AbortSession(string sessionId, string reason)
    {
        try
        {
            return connections.AbortSession(sessionId, reason);
        }
        catch (AggregateException ex)
        {
            log.LogError(ex, "中斷瀏覽器連線失敗：{Session}", SessionRules.ShortId(sessionId));
            return 0;
        }
    }
}
