using System.Text;

namespace Myriagent.Sync;

/// <summary>連線的參數（裝置權杖只放在 Authorization 標頭；ToString 不印權杖，D16）</summary>
/// <param name="Server">要連的伺服器（MYRIAGENT_SYNC_SERVER 覆寫時是它）</param>
/// <param name="PairedServer">配對的伺服器（開發信任要兩者都是迴路位址）</param>
internal sealed record SyncConnectRequest(Uri Server, Uri PairedServer, string DeviceToken, bool DevTrustEnv)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Server = ").Append(Server).Append(", PairedServer = ").Append(PairedServer).Append(", DevTrustEnv = ").Append(DevTrustEnv);
        return true;
    }
}

/// <summary>
/// 伺服器→裝置的訊息與連線關閉（每條連線各一個，帶連線世代，D10）。傳輸在任意執行緒呼叫，實作只做 O(1) 的入列（sync-protocol 4）
/// </summary>
internal interface ISyncSessionEvents
{
    void OnBatchAck(BatchAck ack);

    void OnNotify(Notify notify);

    void OnUsageAssign(UsageAssign assign);

    void OnTrustChanged(TrustChanged changed);

    void OnServerNotice(ServerNotice notice);

    // 第 4 階段（sync-protocol 14.3、14.6；WP4.1 步 10 一次登記好）：伺服器只在協商出對應的功能（DeviceFeatureRules.RequiredFor）時送，
    // 引擎收到沒協商的一律略過；交握完成前到的照 D49 暫存

    /// <summary><c>live1</c>：有人在看的對話（整份取代）</summary>
    void OnWatchChanged(WatchChanged changed);

    /// <summary><c>conv1</c>：要這個對話的整份內容</summary>
    void OnSnapshotRequest(SnapshotRequest request);

    /// <summary><c>media1</c>：向發這個媒體參照的裝置要位元組</summary>
    void OnMediaRequest(MediaRequest request);

    /// <summary><c>cmd1</c>：遠端指令的信封（裝置驗過信封、本機政策與中樞閘門之後才執行）</summary>
    void OnCommandDelivery(CommandDelivery delivery);

    /// <summary><c>peer1</c>：這台對其他電腦下的指令的結果與輸出（桌面遠端操作，14.7）</summary>
    void OnPeerReply(PeerReply reply);

    /// <summary><c>stat1</c>：其他電腦的即時狀態（整份取代，14.8）</summary>
    void OnStatsUpdate(StatsUpdate update);

    /// <summary><c>tok2</c>：其他電腦的 token 計數（整份取代，14.10）</summary>
    void OnTokensUpdate(TokensUpdate update);

    /// <summary><c>sched1</c>：其他電腦最後回報的排程（整份取代，14.12）；測試替身不必實作</summary>
    void OnSchedulesUpdate(SchedulesUpdate update)
    {
    }

    /// <summary><c>draft1</c>：其他電腦與瀏覽器版的輸入框草稿（14.17）；測試替身不必實作</summary>
    void OnComposerDraftUpdate(ComposerDraftUpdate update)
    {
    }


    /// <summary><c>collab1</c>：協作人清單與分享（整份取代，14.14）；測試替身不必實作</summary>
    void OnCollabUpdate(CollaboratorsState state)
    {
    }

    /// <summary><c>live2</c>：伺服器沒有這一筆的基底（或判定外洩），下一則送整則（14.3）</summary>
    void OnLiveResync(LiveResync resync);

    // 第 5 階段（sync-protocol 第 16 節；WP5.1 一次登記好）：同上，伺服器只在協商出 hand1／log1 時送

    /// <summary><c>hand1</c>：請這台交出一個對話</summary>
    void OnHandoffRequest(HandoffRequest request);

    /// <summary><c>hand1</c>：交接的狀態</summary>
    void OnHandoffUpdate(HandoffUpdate update);

    /// <summary><c>log1</c>：伺服器存的日誌損毀，整檔重傳</summary>
    void OnLogRepair(LogRepair repair);

    /// <summary><c>exec1</c>：這台下的遠端執行指令的一段輸出（14.11）</summary>
    void OnExecReply(ExecChunk chunk);

    /// <summary><c>exec1</c>：停止這台正在執行的遠端執行指令（14.11）</summary>
    void OnExecAbort(ExecAbort abort);

    /// <summary><c>desk1</c>：遠端桌面的封包（14.16）；測試替身不必實作</summary>
    void OnDeskDown(DeskPacket packet)
    {
    }

    /// <summary>連線關了（伺服器中斷、網路斷、自己關的）；error 是 SignalR 給的原因</summary>
    void OnClosed(Exception? error);
}

/// <summary>連上裝置 hub（正式是 SignalR，測試是假伺服器）。失敗丟 <see cref="SyncTransportException"/></summary>
internal interface ISyncTransport
{
    Task<ISyncSession> ConnectAsync(SyncConnectRequest request, ISyncSessionEvents events, CancellationToken ct);
}

/// <summary>一條已連上的連線（hub 方法失敗一律丟 <see cref="SyncTransportException"/>，帶 HubException 的代碼）</summary>
internal interface ISyncSession : IAsyncDisposable
{
    /// <summary>這條連線 TLS 交握看到的伺服器葉憑證 SPKI 雜湊（HandshakeRules.SpkiSha256）</summary>
    string ObservedSpkiSha256 { get; }

    Task<HelloResponse> HelloAsync(HelloRequest r, CancellationToken ct);

    Task<ProveResponse> ProveAsync(ProveRequest r, CancellationToken ct);

    /// <summary>Invoke：伺服器只入列就返回，結果以 BatchAck 另外送回</summary>
    Task PublishBatchAsync(Batch b, CancellationToken ct);

    Task<PullResponse> PullAsync(PullRequest r, CancellationToken ct);

    /// <summary>Send（不等伺服器處理）</summary>
    Task HeartbeatAsync(Heartbeat h, CancellationToken ct);

    Task UsagePublishAsync(UsagePublish u, CancellationToken ct);

    Task<TrustBundle> GetTrustAsync(GetTrustRequest r, CancellationToken ct);

    /// <summary>Invoke：換一個新的 <c>cha_</c>（媒體 HTTP 端點用，15 分鐘；只在交握完成的連線上呼叫）</summary>
    Task<AccessTokenResponse> RefreshAccessTokenAsync(CancellationToken ct);

    // 第 4 階段（sync-protocol 14.3、14.6）：只在協商出對應的功能時呼叫（DeviceFeatureRules.RequiredFor）

    /// <summary>Send（<c>live1</c>，不等伺服器處理）：串流中的項目</summary>
    Task LiveRelayAsync(LiveRelay r, CancellationToken ct);

    /// <summary>Send（<c>live2</c>，不等伺服器處理）：串流中的項目的增量（伺服器以上一則重組成整則）</summary>
    Task LiveDeltaAsync(LiveDelta d, CancellationToken ct);

    /// <summary>Invoke（<c>cmd1</c>）：遠端指令的最終結果</summary>
    Task CommandResultAsync(CommandResult r, CancellationToken ct);

    /// <summary>Invoke（<c>cmd1</c>）：指令執行後中樞回給發起檢視的網頁訊息</summary>
    Task CommandOutputAsync(CommandOutput o, CancellationToken ct);

    /// <summary>Invoke（<c>peer1</c>）：這台對其他電腦下的指令（桌面遠端操作，14.7）</summary>
    Task<PeerCmdResult> PeerCmdAsync(PeerCmd c, CancellationToken ct);

    /// <summary>Invoke（<c>stat1</c>）：這台的即時狀態（14.8）；回現在有沒有別人在看</summary>
    Task<StatsPublishResult> StatsPublishAsync(StatsPublish p, CancellationToken ct);

    /// <summary>Invoke（<c>tok1</c>）：這台各帳戶的 token 計數（14.9）</summary>
    Task TokensPublishAsync(TokensPublish p, CancellationToken ct);

    /// <summary>Invoke（<c>sched1</c>）：這台排程頁的摘要（14.12）；測試替身不必實作（什麼都不做）</summary>
    Task SchedulesPublishAsync(SchedulesPublish p, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Invoke（<c>draft1</c>）：這台某個對話的輸入框草稿（14.17）；測試替身不必實作（什麼都不做）</summary>
    Task ComposerDraftPublishAsync(ComposerDraftPublish p, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Invoke（<c>exec1</c>）：這台執行的遠端執行指令的一段輸出（14.11）；回伺服器還要不要它</summary>
    Task<ExecOutputResult> ExecOutputAsync(ExecChunk c, CancellationToken ct);

    /// <summary>Invoke（<c>exec1</c>）：停止這台下的遠端執行指令（14.11）</summary>
    Task<ExecCancelResult> ExecCancelAsync(ExecCancel c, CancellationToken ct);

    /// <summary>Invoke（<c>desk1</c>）：遠端桌面的封包（14.16）；回伺服器還要不要轉送這個工作階段。測試替身沒實作時回 keep false</summary>
    Task<DeskUpResult> DeskUpAsync(DeskPacket p, CancellationToken ct) => Task.FromResult(new DeskUpResult(false));

    /// <summary>Invoke（<c>collab1</c>）：替擁有者送協作人的管理指令（14.14）；測試替身沒實作時回 unavailable</summary>
    Task<CollabCmdResult> CollabCmdAsync(CollabCmdRequest r, CancellationToken ct)
        => Task.FromResult(new CollabCmdResult(false, CollaboratorCodes.Unavailable));

    // 第 5 階段（sync-protocol 第 16 節，hand1）：只在協商出 hand1 時呼叫（WP5.6、WP5.7 用；WP5.1 沒有呼叫者）

    /// <summary>Invoke（<c>hand1</c>）：換電腦的預覽</summary>
    Task<HandoffPreviewResult> HandoffPreviewAsync(HandoffPreviewRequest r, CancellationToken ct);

    /// <summary>Invoke（<c>hand1</c>）：發起交接或強制接手</summary>
    Task<HandoffStartResult> HandoffStartAsync(HandoffStartRequest r, CancellationToken ct);

    /// <summary>Invoke（<c>hand1</c>）：取消交接（提交前）</summary>
    Task<HandoffCancelResult> HandoffCancelAsync(HandoffCancelRequest r, CancellationToken ct);

    /// <summary>Invoke（<c>hand1</c>）：交出端或接手端的進度</summary>
    Task<HandoffAck> HandoffProgressAsync(HandoffProgress p, CancellationToken ct);

    /// <summary>Invoke（<c>hand1</c>）：交出端排空、上傳完</summary>
    Task<HandoffAck> HandoffReadyAsync(HandoffReady r, CancellationToken ct);
}

/// <summary>連線或 hub 方法失敗（已依 <see cref="SyncFailureRules.Classify"/> 分類；訊息不含權杖）</summary>
internal sealed class SyncTransportException(SyncFailure failure, Exception? inner = null) : Exception(failure.Detail, inner)
{
    public SyncFailure Failure { get; } = failure;
}
