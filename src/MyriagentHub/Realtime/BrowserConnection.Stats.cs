namespace MyriagentHub.Realtime;

/// <summary>
/// 一條瀏覽器連線的電腦狀態訂閱（stat1；web-protocol 的 <c>monitor</c> 與 <c>deviceStats</c>）：網頁開著電腦狀態面板而且看得到時送
/// <c>monitor {on:true}</c>，閘道只對這種連線送 <c>deviceStats</c>。訂閱不跨連線（重連後網頁收到 init 會再送一次）；init 入列時清掉看過的版本
/// （<see cref="EnqueueInit"/>：init 清掉了排著的 deviceStats，要再送一份）
/// </summary>
internal sealed partial class BrowserConnection
{
    private bool monitoring;
    private long statsSeen;

    /// <summary>網頁開著電腦狀態面板</summary>
    public bool Monitoring
    {
        get
        {
            lock (gate)
                return monitoring;
        }
    }

    /// <summary>這條連線看過的看板版本（還沒看過是 0）</summary>
    public long StatsSeen
    {
        get
        {
            lock (gate)
                return statsSeen;
        }
    }

    /// <summary>monitor 指令：值變了回 true；開啟時清掉看過的版本（立刻送目前的一份）</summary>
    public bool SetMonitoring(bool on)
    {
        lock (gate)
        {
            if (monitoring == on)
                return false;
            monitoring = on;
            if (on)
                statsSeen = 0;
            return true;
        }
    }

    public void MarkStatsSeen(long version)
    {
        lock (gate)
            statsSeen = version;
    }

    /// <summary>init 入列時（連線鎖裡）：之後要再送一份 deviceStats</summary>
    private void ResetStatsLocked() => statsSeen = 0;
}
