using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 引擎 → 協調者的各電腦即時狀態（stat1；協調者的 EnginePort 實作；引擎以 <c>args.Host as IDeviceStatsPort</c> 取得，沒有就不宣告 stat1）。任何執行緒呼叫
/// </summary>
internal interface IDeviceStatsPort
{
    /// <summary>這台現在的狀態（不超過約 1.5 秒舊；沒有來源回 null）</summary>
    DeviceStats? CurrentStats();

    /// <summary>這台有視窗開著電腦狀態面板（要收其他電腦的狀態）</summary>
    bool Watching { get; }

    /// <summary>伺服器送來的其他電腦的狀態（整份取代；連線斷了送一份空的）</summary>
    void DeliverStats(StatsUpdate update);

    /// <summary>這台和伺服器最近一次量到的往返時間（毫秒；連線斷了是 null）</summary>
    void ReportRtt(int? rttMs);
}

/// <summary>協調者給引擎的通知（<c>engine as ISyncEngineStats</c>）：這台的面板開了或關了（任何執行緒）</summary>
internal interface ISyncEngineStats
{
    void StatsWatchChanged();
}

// 各電腦的即時狀態（sync-protocol 14.8）：協商出 stat1 的連線交握完成就送一次 StatsPublish（這台的 CPU、記憶體、磁碟、網路與上一次量到的往返時間），
// 之後依伺服器回的 watched 與這台的面板決定間隔（有人在看 2 秒、沒人 10 秒）；每次送出只排一個計時器、同時只有一個呼叫在途。
// 往返時間以這次 Invoke 從送出到回應的經過時間量（同一條連線的呼叫依序處理，排在批次或拉取後面時會偏高）。
// 收到的 StatsUpdate 檢查格式後整份交給協調者；連線丟掉時交一份空的、往返時間回報 null。狀態只在引擎執行緒讀寫
internal sealed partial class SyncEngine : ISyncEngineStats
{
    private IDeviceStatsPort? StatsPort => args.Host as IDeviceStatsPort;

    private ITimer? statsTimer;

    /// <summary>送 StatsPublish 的連線（交握完成、協商出 stat1；斷線時清掉）</summary>
    private SyncConnection? statsConn;

    private bool statsSending;

    /// <summary>上一次量到的往返時間（下一次送出帶上；斷線時清掉）</summary>
    private int? statsRtt;

    /// <summary>這條連線已經記過「狀態不合格」（每條連線只記一次）</summary>
    private bool statsInvalidLogged;

    public void StatsWatchChanged() => thread.Post(() =>
    {
        // 面板剛打開：立刻送一次（watch=true），伺服器下一份就會送過來；正在送的那一次結束後照常排下一次
        if (statsConn is { } conn && IsCurrent(conn) && !statsSending && StatsPort is { Watching: true })
            ScheduleStats(conn, TimeSpan.Zero);
    });

    /// <summary>Hello 的功能：有 port 才宣告 stat1</summary>
    private void ContributeStatsFeatures(List<string> into)
    {
        if (StatsPort is not null)
            into.Add(DeviceFeatureRules.Stat1);
    }

    /// <summary>交握完成（CompleteHandshake 的最後）：這條連線協商出 stat1 就開始送</summary>
    private void StartStats(SyncConnection conn)
    {
        if (StatsPort is null || conn.UpdateOnly || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Stat1))
            return;
        statsConn = conn;
        statsSending = false;
        statsInvalidLogged = false;
        ScheduleStats(conn, TimeSpan.Zero);
    }

    /// <summary>連線丟掉了：停掉計時器、清掉往返時間，交給協調者一份空的</summary>
    private void StopStats(SyncConnection conn)
    {
        if (!ReferenceEquals(statsConn, conn))
            return;
        CancelTimer(ref statsTimer);
        statsConn = null;
        statsSending = false;
        statsRtt = null;
        if (StatsPort is not { } port)
            return;
        try
        {
            port.ReportRtt(null);
            port.DeliverStats(new StatsUpdate([]));
        }
        catch (Exception ex)
        {
            Log.Error("清掉各電腦的狀態失敗", ex);
        }
    }

    /// <summary>排下一次送出（立刻的直接排進引擎執行緒，不經計時器）</summary>
    private void ScheduleStats(SyncConnection conn, TimeSpan due)
    {
        CancelTimer(ref statsTimer);
        if (due <= TimeSpan.Zero)
            thread.Post(() => OnStatsTick(conn));
        else
            statsTimer = time.CreateTimer(_ => thread.Post(() => OnStatsTick(conn)), null, due, Timeout.InfiniteTimeSpan);
    }

    private void OnStatsTick(SyncConnection conn)
    {
        if (!IsCurrent(conn) || !conn.Proven || !ReferenceEquals(statsConn, conn) || statsSending || StatsPort is not { } port)
            return;
        DeviceStats? stats;
        try
        {
            stats = port.CurrentStats();
        }
        catch (Exception ex)
        {
            Log.Error("讀這台電腦的狀態失敗", ex);
            stats = null;
        }
        var watching = port.Watching;
        if (stats is null)
        {
            ScheduleStats(conn, DeviceStatsRules.IdleInterval);
            return;
        }
        stats = stats with { RttMs = statsRtt };
        if (DeviceStatsRules.Check(stats) is { } problem)
        {
            if (!statsInvalidLogged)
                Log.Warn($"這台電腦的狀態不合格，不送：{problem}（這條連線之後不再記錄）");
            statsInvalidLogged = true;
            ScheduleStats(conn, DeviceStatsRules.IdleInterval);
            return;
        }

        statsSending = true;
        var request = new StatsPublish(stats, watching);
        Spawn(async () =>
        {
            var started = time.GetTimestamp();
            StatsPublishResult? result = null;
            try
            {
                result = await conn.Session!.StatsPublishAsync(request, conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                // 伺服器判定格式錯誤：這一則丟掉、不重連（可丟的即時資料），照沒人在看的間隔再試
                Log.Warn($"伺服器拒收這台電腦的狀態（{ex.Failure.Code}）");
            }
            catch (SyncTransportException ex)
            {
                statsSending = false;
                Fail(conn, ex.Failure);
                return;
            }

            statsSending = false;
            if (!IsCurrent(conn) || !ReferenceEquals(statsConn, conn))
                return;
            var others = false;
            if (result is not null)
            {
                statsRtt = DeviceStatsRules.RttMs(time.GetElapsedTime(started));
                others = result.Watched;
                try
                {
                    port.ReportRtt(statsRtt);
                }
                catch (Exception ex)
                {
                    Log.Error("回報和伺服器的往返時間失敗", ex);
                }
            }
            // 有人在看（其他電腦、瀏覽器，或這台自己的面板要看延遲）就 2 秒一次
            ScheduleStats(conn, DeviceStatsRules.PublishInterval(others || port.Watching));
        }, conn);
    }

    /// <summary><c>stat1</c> 的 <c>StatsUpdate</c>：格式不合格整則略過，否則整份交給協調者</summary>
    private void OnStatsUpdate(SyncConnection conn, StatsUpdate update)
    {
        if (StatsPort is not { } port)
            return;
        if (DeviceStatsRules.CheckUpdate(update) is { } problem)
        {
            Log.Warn($"伺服器送來格式不合格的 StatsUpdate：略過（{problem}）");
            return;
        }
        try
        {
            port.DeliverStats(update);
        }
        catch (Exception ex)
        {
            Log.Error("交出其他電腦的狀態失敗", ex);
        }
    }
}
