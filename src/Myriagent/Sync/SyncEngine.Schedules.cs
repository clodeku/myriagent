using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 引擎 → 協調者的排程摘要（sched1；協調者的 EnginePort 實作；引擎以 <c>args.Host as IScheduleSyncPort</c> 取得，沒有就不宣告 sched1）。任何執行緒呼叫
/// </summary>
internal interface IScheduleSyncPort
{
    /// <summary>這台排程頁目前的摘要（ScheduleSyncRules.ForSync 的結果，不會再被改動）；排程頁沒掛或還沒讀過是 null</summary>
    JObject? CurrentSchedules();

    /// <summary>伺服器送來的其他電腦最後回報的摘要（整份取代；格式已檢查過）</summary>
    void DeliverOtherSchedules(IReadOnlyList<DeviceSchedulesView> devices);
}

/// <summary>協調者給引擎的通知（<c>engine as ISyncEngineSchedules</c>）：這台的摘要變了（任何執行緒）</summary>
internal interface ISyncEngineSchedules
{
    void SchedulesChanged();
}

// 各電腦的排程（sync-protocol 14.12，sched1）：協商出 sched1 的連線交握完成就送一次這台的摘要（SchedulesPublish，整份取代），
// 之後摘要變了（中樞讀完排程、內容不同時通知）就再送，兩次至少隔 2 秒；內容和這條連線上次送成功的一樣就不送。
// 收到的 SchedulesUpdate 檢查格式後整份交給協調者（排程頁的其他電腦）。同時只有一個呼叫在途；狀態只在引擎執行緒讀寫
internal sealed partial class SyncEngine : ISyncEngineSchedules
{
    private IScheduleSyncPort? SchedulesPort => args.Host as IScheduleSyncPort;
    private ITimer? schedulesTimer;
    private SyncConnection? schedulesConn;
    private bool schedulesSending;
    private bool schedulesChangedWhileSending;
    private string? schedulesSent;
    private long? schedulesSentAt;
    private bool schedulesInvalidLogged;

    public void SchedulesChanged() => thread.Post(RequestSchedules);

    private void RequestSchedules()
    {
        if (schedulesConn is not { } conn || !IsCurrent(conn))
            return;
        if (schedulesSending)
        {
            schedulesChangedWhileSending = true;
            return;
        }
        ScheduleSchedules(conn, SchedulesDue());
    }

    /// <summary>Hello 的功能：有 port 才宣告 sched1 與 timed1</summary>
    private void ContributeSchedulesFeatures(List<string> into)
    {
        if (SchedulesPort is not null)
        {
            into.Add(DeviceFeatureRules.Sched1);
            // 定時訊息的遠端設定（timedSave、timedAction，2026-11-13）：這版的中樞收得了
            into.Add(DeviceFeatureRules.Timed1);
        }
    }

    /// <summary><c>SchedulesUpdate</c>：格式不合格整則略過，否則整份交給協調者</summary>
    private void OnSchedulesUpdate(SyncConnection conn, SchedulesUpdate update)
    {
        if (SchedulesPort is not { } port || conn.UpdateOnly)
            return;
        if (ScheduleSyncRules.CheckUpdate(update) is { } problem)
        {
            Log.Warn($"伺服器送來格式不合格的 SchedulesUpdate：略過（{problem}）");
            return;
        }
        try
        {
            port.DeliverOtherSchedules(update.Devices);
        }
        catch (Exception ex)
        {
            Log.Error("交出其他電腦的排程失敗", ex);
        }
    }

    /// <summary>交握完成：這條連線協商出 sched1 就送一次</summary>
    private void StartSchedules(SyncConnection conn)
    {
        if (SchedulesPort is null || conn.UpdateOnly || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Sched1))
            return;
        schedulesConn = conn;
        schedulesSending = false;
        schedulesChangedWhileSending = false;
        schedulesSent = null;
        schedulesSentAt = null;
        schedulesInvalidLogged = false;
        ScheduleSchedules(conn, TimeSpan.Zero);
    }

    private void StopSchedules(SyncConnection conn)
    {
        if (!ReferenceEquals(schedulesConn, conn))
            return;
        CancelTimer(ref schedulesTimer);
        schedulesConn = null;
        schedulesSending = false;
        schedulesChangedWhileSending = false;
        schedulesSent = null;
    }

    private TimeSpan SchedulesDue()
    {
        if (schedulesSentAt is not { } at)
            return TimeSpan.Zero;
        var left = ScheduleSyncRules.PublishInterval - time.GetElapsedTime(at);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void ScheduleSchedules(SyncConnection conn, TimeSpan due)
    {
        CancelTimer(ref schedulesTimer);
        if (due <= TimeSpan.Zero)
            thread.Post(() => OnSchedulesTick(conn));
        else
            schedulesTimer = time.CreateTimer(_ => thread.Post(() => OnSchedulesTick(conn)), null, due, Timeout.InfiniteTimeSpan);
    }

    private void OnSchedulesTick(SyncConnection conn)
    {
        if (!IsCurrent(conn) || !conn.Proven || !ReferenceEquals(schedulesConn, conn) || schedulesSending || SchedulesPort is not { } port)
            return;
        CancelTimer(ref schedulesTimer);
        JObject? snapshot;
        try
        {
            snapshot = port.CurrentSchedules();
        }
        catch (Exception ex)
        {
            Log.Error("整理這台的排程摘要失敗", ex);
            return;
        }
        if (snapshot is null || ScheduleSyncRules.CheckSnapshot(snapshot) is not null)
            return;
        var json = snapshot.ToString(Formatting.None);
        if (json == schedulesSent)
            return;
        schedulesSending = true;
        schedulesChangedWhileSending = false;
        schedulesSentAt = time.GetTimestamp();
        Spawn(async () =>
        {
            var accepted = false;
            try
            {
                await conn.Session!.SchedulesPublishAsync(new SchedulesPublish(snapshot), conn.Token);
                accepted = true;
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                if (!schedulesInvalidLogged)
                    Log.Warn($"伺服器拒收這台的排程摘要（{ex.Failure.Code}）：只記錄、不重連（這條連線之後不再記錄）");
                schedulesInvalidLogged = true;
            }
            catch (SyncTransportException ex)
            {
                schedulesSending = false;
                Fail(conn, ex.Failure);
                return;
            }
            schedulesSending = false;
            if (!IsCurrent(conn) || !ReferenceEquals(schedulesConn, conn))
                return;
            schedulesSent = accepted ? json : null;
            if (schedulesChangedWhileSending)
                ScheduleSchedules(conn, SchedulesDue());
        }, conn);
    }
}
