using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 引擎 → 協調者的 token 計數（tok1；協調者的 EnginePort 實作；引擎以 <c>args.Host as ITokenTallyPort</c> 取得，沒有就不宣告 tok1）。任何執行緒呼叫
/// </summary>
internal interface ITokenTallyPort
{
    /// <summary>這台目前的計數（本機帳戶 × 本機日期；複本）</summary>
    IReadOnlyList<LocalTokenTally> CurrentTokens();
}

/// <summary>
/// 引擎 → 協調者的其他電腦 token 計數（tok2，sync-protocol 14.10；協調者的 EnginePort 實作；引擎以 <c>args.Host as ITokenTallyViewPort</c> 取得，
/// 沒有就不宣告 tok2）。任何執行緒呼叫
/// </summary>
internal interface ITokenTallyViewPort
{
    /// <summary>伺服器送來的其他電腦的格（整份取代；格式已檢查過）</summary>
    void DeliverOthers(IReadOnlyList<TokenTallyRow> rows);
}

/// <summary>協調者給引擎的通知（<c>engine as ISyncEngineTokens</c>）：這台的計數變了（任何執行緒）</summary>
internal interface ISyncEngineTokens
{
    void TokensChanged();
}

// 各帳戶的 token 計數（sync-protocol 14.9）：協商出 tok1 的連線交握完成就送一次整份 TokensPublish（依目錄裡這台的連結換成邏輯帳戶），
// 之後計數變了（中樞每 50 毫秒合併一次通知）或拉到目錄的變動就重算、送新的與變了的格，同一條連線兩次至少隔 5 秒；沒有通知時每 60 秒重算一次
// （目錄的連結變了、跨日時舊的一天滑出保留範圍，內容都會不同；沒變就不送）。同時只有一個呼叫在途；狀態只在引擎執行緒讀寫。
// 其他電腦的計數（14.10）：協商出 tok2 的連線收到的 TokensUpdate 檢查格式後整份交給協調者；連線丟掉時不清（計數只會變大，最後收到的仍是對的下限）
internal sealed partial class SyncEngine : ISyncEngineTokens
{
    private ITokenTallyPort? TokensPort => args.Host as ITokenTallyPort;

    private ITokenTallyViewPort? TokensViewPort => args.Host as ITokenTallyViewPort;

    private ITimer? tokensTimer;

    /// <summary>送 TokensPublish 的連線（交握完成、協商出 tok1；斷線時清掉）</summary>
    private SyncConnection? tokensConn;

    private bool tokensSending;

    /// <summary>送出途中又有變動：送完照最短間隔再排一次</summary>
    private bool tokensChangedWhileSending;

    /// <summary>這條連線上次送成功之後伺服器有的格（null＝還沒送過或被拒，下一次送整份）</summary>
    private Dictionary<(string LogicalAccountId, string Day), TokenTallyRow>? tokensSent;

    /// <summary>上一次開始送出的時間（單調時鐘；最短間隔從這裡算）</summary>
    private long? tokensSentAt;

    /// <summary>這條連線已經記過「伺服器拒收」（每條連線只記一次）</summary>
    private bool tokensInvalidLogged;

    public void TokensChanged() => thread.Post(RequestTokens);

    /// <summary>計數或目錄的連結可能變了（引擎執行緒）：照最短間隔重算一次；正在送就等送完</summary>
    private void RequestTokens()
    {
        if (tokensConn is not { } conn || !IsCurrent(conn))
            return;
        if (tokensSending)
        {
            tokensChangedWhileSending = true;
            return;
        }
        ScheduleTokens(conn, TokensDue());
    }

    /// <summary>Hello 的功能：有 port 才宣告 tok1；收得下其他電腦的計數才宣告 tok2</summary>
    private void ContributeTokensFeatures(List<string> into)
    {
        if (TokensPort is not null)
            into.Add(DeviceFeatureRules.Tok1);
        if (TokensViewPort is not null)
            into.Add(DeviceFeatureRules.Tok2);
    }

    /// <summary><c>tok2</c> 的 <c>TokensUpdate</c>：格式不合格整則略過，否則整份交給協調者</summary>
    private void OnTokensUpdate(SyncConnection conn, TokensUpdate update)
    {
        if (TokensViewPort is not { } port || conn.UpdateOnly)
            return;
        if (TokenTallySyncRules.CheckUpdate(update) is { } problem)
        {
            Log.Warn($"伺服器送來格式不合格的 TokensUpdate：略過（{problem}）");
            return;
        }
        try
        {
            port.DeliverOthers(update.Rows);
        }
        catch (Exception ex)
        {
            Log.Error("交出其他電腦的 token 計數失敗", ex);
        }
    }

    /// <summary>交握完成（CompleteHandshake 的最後）：這條連線協商出 tok1 就送一次整份</summary>
    private void StartTokens(SyncConnection conn)
    {
        if (TokensPort is null || conn.UpdateOnly || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Tok1))
            return;
        tokensConn = conn;
        tokensSending = false;
        tokensChangedWhileSending = false;
        tokensSent = null;
        tokensSentAt = null;
        tokensInvalidLogged = false;
        ScheduleTokens(conn, TimeSpan.Zero);
    }

    /// <summary>連線丟掉了：停掉計時器；下一條連線從整份開始</summary>
    private void StopTokens(SyncConnection conn)
    {
        if (!ReferenceEquals(tokensConn, conn))
            return;
        CancelTimer(ref tokensTimer);
        tokensConn = null;
        tokensSending = false;
        tokensChangedWhileSending = false;
        tokensSent = null;
    }

    /// <summary>距離上一次送出滿最短間隔還要多久（沒送過是 0）</summary>
    private TimeSpan TokensDue()
    {
        if (tokensSentAt is not { } at)
            return TimeSpan.Zero;
        var left = TokenTallySyncRules.MinInterval - time.GetElapsedTime(at);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>排下一次（立刻的直接排進引擎執行緒，不經計時器）；排著的那一次取消</summary>
    private void ScheduleTokens(SyncConnection conn, TimeSpan due)
    {
        CancelTimer(ref tokensTimer);
        if (due <= TimeSpan.Zero)
            thread.Post(() => OnTokensTick(conn));
        else
            tokensTimer = time.CreateTimer(_ => thread.Post(() => OnTokensTick(conn)), null, due, Timeout.InfiniteTimeSpan);
    }

    private void OnTokensTick(SyncConnection conn)
    {
        if (!IsCurrent(conn) || !conn.Proven || !ReferenceEquals(tokensConn, conn) || tokensSending || TokensPort is not { } port || catalog is null)
            return;
        CancelTimer(ref tokensTimer);
        List<TokenTallyRow> current;
        TokensPublish? publish;
        try
        {
            var links = catalog.LinkStates().ToDictionary(l => l.Key, l => l.Value.LogicalAccountId, StringComparer.Ordinal);
            current = TokenTallySyncRules.ToRows(port.CurrentTokens(), links, DateOnly.FromDateTime(time.GetLocalNow().DateTime));
            publish = TokenTallySyncRules.Plan(tokensSent, current);
        }
        catch (Exception ex)
        {
            Log.Error("整理這台的 token 計數失敗", ex);
            ScheduleTokens(conn, TokenTallySyncRules.RecheckInterval);
            return;
        }
        if (publish is null)
        {
            ScheduleTokens(conn, TokenTallySyncRules.RecheckInterval);
            return;
        }

        tokensSending = true;
        tokensChangedWhileSending = false;
        tokensSentAt = time.GetTimestamp();
        Spawn(async () =>
        {
            var accepted = false;
            try
            {
                await conn.Session!.TokensPublishAsync(publish, conn.Token);
                accepted = true;
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                // 伺服器判定格式錯誤（invalid）或太大：只記錄、不重連（只供顯示的資料），下一次送整份
                if (!tokensInvalidLogged)
                    Log.Warn($"伺服器拒收這台的 token 計數（{ex.Failure.Code}）：只記錄、不重連（這條連線之後不再記錄）");
                tokensInvalidLogged = true;
            }
            catch (SyncTransportException ex)
            {
                tokensSending = false;
                Fail(conn, ex.Failure);
                return;
            }

            tokensSending = false;
            if (!IsCurrent(conn) || !ReferenceEquals(tokensConn, conn))
                return;
            // 部分送出只在上次的格都還在、數字沒變小時（Plan）：伺服器有的格就是這次的全部
            tokensSent = accepted ? current.ToDictionary(r => (r.LogicalAccountId, r.Day)) : null;
            ScheduleTokens(conn, tokensChangedWhileSending ? TokensDue() : TokenTallySyncRules.RecheckInterval);
        }, conn);
    }
}
