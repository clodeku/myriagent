using Myriagent.Core;
using Myriagent.Remote;

namespace Myriagent.Sync;

/// <summary>引擎給協調者的遠端執行指令功能（<c>SyncEngine</c> 實作；協調者以 <c>engine as ISyncEngineExec</c> 取得）。只把工作排進引擎執行緒</summary>
internal interface ISyncEngineExec
{
    /// <summary>被操作的一方：送一段輸出、狀態或結束碼（任何執行緒）；依呼叫的順序送</summary>
    void SubmitExecOutput(ExecChunk chunk);

    /// <summary>發起的一方：要求停止這台下的指令（任何執行緒；<paramref name="commandId"/> 是 PeerCmd 的 commandId）</summary>
    void SubmitExecCancel(string commandId);
}

/// <summary>
/// 引擎 → 協調者的遠端執行指令（協調者的 EnginePort 實作；引擎以 <c>args.Host as IRemoteExecPort</c> 取得，沒有就不宣告 exec1）。任何執行緒呼叫
/// </summary>
internal interface IRemoteExecPort
{
    /// <summary>宿主接得住遠端執行指令（false 時不宣告 exec1）</summary>
    bool ExecSupported { get; }

    /// <summary>目前能不能搬遠端執行的串流（這條連線交握完成而且協商出 exec1）；值變了才呼叫</summary>
    void ReportExecLive(bool live);

    /// <summary>發起的一方：伺服器轉來的一段（<c>ExecReply</c>；commandId 是發起時的）</summary>
    void DeliverExecReply(ExecChunk chunk);

    /// <summary>
    /// 被操作的一方：停止這個指令（<paramref name="commandId"/> 是信封的 cid）——伺服器轉來的停止要求（<c>ExecAbort</c>），
    /// 或 <c>ExecOutput</c> 回 keep false（<paramref name="orphaned"/>：伺服器不再轉送，發起的電腦離線太久或伺服器重啟）
    /// </summary>
    void AbortExec(string commandId, bool orphaned);
}

// 遠端執行指令的引擎部分（docs/sync-protocol.md 14.11，exec1）：remoteExec 本身照一般的遠端指令（SyncEngine.Commands.cs）驗信封、去重、進中樞閘門；
// 這裡只搬串流。被操作的一方：輸出依序一則一則以 ExecOutput 送（等前一則的回應才送下一則，順序不亂；回 keep false 就請協調者停止那個指令），
// 斷線時沒送出的輸出丟掉（收的一方以 seq 看出缺漏），結束碼留到下一條協商出 exec1 的連線補送（最多 MaxPendingExits 則）。
// 發起的一方：ExecCancel 只在目前的連線送得出去時送（送不出去也沒關係：伺服器在發起的電腦離線太久時會請那台停止）；ExecReply、ExecAbort 檢查格式後交給協調者。
// 狀態只在引擎執行緒讀寫
internal sealed partial class SyncEngine : ISyncEngineExec
{
    /// <summary>斷線時留著補送的結束碼上限</summary>
    internal const int MaxPendingExits = 64;

    private IRemoteExecPort? ExecPort => args.Host is IRemoteExecPort { ExecSupported: true } port ? port : null;

    /// <summary>上一次回報的「能不能搬」（引擎執行緒）</summary>
    private bool execLiveReported;

    /// <summary>還沒送出的段落（依序；引擎執行緒）</summary>
    private readonly Queue<ExecChunk> execOutgoing = new();

    /// <summary>斷線時沒送出的結束碼（引擎執行緒）</summary>
    private readonly List<ExecChunk> execPendingExits = [];

    /// <summary>正在送的那條連線（null＝沒有在送）</summary>
    private SyncConnection? execSendingOn;

    public void SubmitExecOutput(ExecChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        thread.Post(() => OnSubmitExecOutput(chunk));
    }

    public void SubmitExecCancel(string commandId)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        thread.Post(() => OnSubmitExecCancel(commandId));
    }

    /// <summary>目前這條連線能搬遠端執行的串流（交握完成而且協商出 exec1）</summary>
    private SyncConnection? ExecConnection()
        => current is { Proven: true, Session: not null } conn && DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Exec1) ? conn : null;

    private void OnSubmitExecOutput(ExecChunk chunk)
    {
        if (ExecConnection() is null)
        {
            KeepIfExit(chunk);
            return;
        }

        execOutgoing.Enqueue(chunk);
        PumpExec();
    }

    private void KeepIfExit(ExecChunk chunk)
    {
        if (chunk.Kind != RemoteExecRules.KindExit)
            return;
        if (execPendingExits.Count >= MaxPendingExits)
            execPendingExits.RemoveAt(0);
        execPendingExits.Add(chunk);
    }

    /// <summary>沒有在送、有下一則、連線可以用時送下一則；回應到了（引擎執行緒）再送下一則</summary>
    private void PumpExec()
    {
        if (execSendingOn is not null || execOutgoing.Count == 0 || ExecConnection() is not { } conn)
            return;
        var chunk = execOutgoing.Dequeue();
        execSendingOn = conn;
        Spawn(async () =>
        {
            ExecOutputResult? result = null;
            try
            {
                result = await conn.Session!.ExecOutputAsync(chunk, conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                // 太大或伺服器判定格式錯誤：丟掉這一段，不重連
                Log.Warn($"伺服器拒收遠端執行指令 {chunk.CommandId} 的輸出（{ex.Failure.Code}）");
            }
            catch (SyncTransportException ex)
            {
                if (ReferenceEquals(execSendingOn, conn))
                    execSendingOn = null;
                KeepIfExit(chunk);
                Fail(conn, ex.Failure);
                // 下一條連線可能已經交握完成（補送在它的 OnExecProven 已經做過）：留著的結束碼現在就排上去
                if (ExecConnection() is { } next && !ReferenceEquals(next, conn))
                    OnExecProven(next);
                return;
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(execSendingOn, conn))
                    execSendingOn = null;
                KeepIfExit(chunk);
                throw;
            }

            if (ReferenceEquals(execSendingOn, conn))
                execSendingOn = null;
            if (result is { Keep: false } && chunk.Kind != RemoteExecRules.KindExit && ExecPort is { } port)
            {
                Log.Info($"伺服器不再轉送遠端執行指令 {chunk.CommandId}：停止它");
                try
                {
                    port.AbortExec(chunk.CommandId, orphaned: true);
                }
                catch (Exception ex)
                {
                    Log.Error("請協調者停止遠端執行指令失敗", ex);
                }
            }

            PumpExec();
        }, conn);
    }

    private void OnSubmitExecCancel(string commandId)
    {
        if (ExecConnection() is not { } conn)
            return;
        Spawn(async () =>
        {
            try
            {
                await conn.Session!.ExecCancelAsync(new ExecCancel(commandId), conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                Log.Warn($"伺服器拒收遠端執行指令 {commandId} 的停止要求（{ex.Failure.Code}）");
            }
            catch (SyncTransportException ex)
            {
                Fail(conn, ex.Failure);
            }
        }, conn);
    }

    /// <summary><c>exec1</c> 的 <c>ExecReply</c>（發起的一方）：格式合格才交給協調者</summary>
    private void OnExecReply(SyncConnection conn, ExecChunk chunk)
    {
        if (ExecPort is not { } port)
            return;
        if (!RemoteExecRules.IsValidChunk(chunk))
        {
            Log.Warn("伺服器送來格式不合格的 ExecReply：略過");
            return;
        }

        port.DeliverExecReply(chunk);
    }

    /// <summary><c>exec1</c> 的 <c>ExecAbort</c>（被操作的一方）</summary>
    private void OnExecAbort(SyncConnection conn, ExecAbort abort)
    {
        if (ExecPort is not { } port)
            return;
        if (abort is null || !SyncIds.IsUlid(abort.CommandId))
        {
            Log.Warn("伺服器送來格式不合格的 ExecAbort：略過");
            return;
        }

        port.AbortExec(abort.CommandId, orphaned: false);
    }

    /// <summary>值變了才回報「能不能搬」</summary>
    private void ReportExecLive(SyncConnection? conn)
    {
        if (ExecPort is not { } port)
            return;
        var live = conn is not null && ReferenceEquals(ExecConnection(), conn);
        if (live == execLiveReported)
            return;
        execLiveReported = live;
        try
        {
            port.ReportExecLive(live);
        }
        catch (Exception ex)
        {
            Log.Error("回報遠端執行指令能不能用失敗", ex);
        }
    }

    /// <summary>交握完成：補送上一條連線沒送出的結束碼</summary>
    private void OnExecProven(SyncConnection conn)
    {
        ReportExecLive(conn);
        if (!ReferenceEquals(ExecConnection(), conn) || execPendingExits.Count == 0)
            return;
        Log.Info($"補送上一條連線沒送出的遠端執行結束碼 {execPendingExits.Count} 則");
        foreach (var exit in execPendingExits)
            execOutgoing.Enqueue(exit);
        execPendingExits.Clear();
        PumpExec();
    }

    /// <summary>連線丟掉了：沒送出的輸出丟掉，結束碼留到下一條連線</summary>
    private void OnExecClosed(SyncConnection conn)
    {
        ReportExecLive(null);
        if (ReferenceEquals(execSendingOn, conn))
            execSendingOn = null;
        while (execOutgoing.TryDequeue(out var chunk))
            KeepIfExit(chunk);
    }
}
