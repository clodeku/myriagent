namespace Myriagent.Sync;

internal sealed partial class SyncEngine
{
    /// <summary>
    /// 一條連線（D10 的連線世代）：傳輸的事件接收物件＋這條連線自己的旗標。每次重連都 new 一個，傳輸在任意執行緒呼叫的
    /// 回呼只做 O(1) 的入列（排進引擎執行緒），到了引擎執行緒才檢查「還是不是目前的連線」——舊連線晚到的 OnClosed、BatchAck 一律丟掉。
    /// Prove 完成（<see cref="Proven"/>）之前收到的 UsageAssign、Notify、TrustChanged、ServerNotice 只記下來、不呼叫任何 hub 方法（D11）；
    /// ServerNotice 交握完成才採用（伺服器身分驗過之前收到的不可信：正牌伺服器只對已 Prove 的連線送）。
    /// 第 4 階段的訊息（WatchChanged、SnapshotRequest、MediaRequest、CommandDelivery）同樣暫存到交握完成（<see cref="EarlyMessages"/>，D49），
    /// 處理時沒協商出對應功能的一律略過（SyncEngine.Contracts.cs）。
    /// 旗標只在引擎執行緒讀寫。
    /// </summary>
    private sealed class SyncConnection : ISyncSessionEvents
    {
        private readonly SyncEngine engine;
        private readonly CancellationTokenSource cts;

        public SyncConnection(SyncEngine engine, int generation, CancellationToken lifetime)
        {
            this.engine = engine;
            Generation = generation;
            cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Token = cts.Token;
        }

        public int Generation { get; }

        /// <summary>這條連線的呼叫用的取消權杖（斷線、停止時取消）</summary>
        public CancellationToken Token { get; }

        public ISyncSession? Session { get; set; }

        /// <summary>這條連線 Hello 送出的 nonceD（Prove 的簽章內容）</summary>
        public string? NonceD { get; set; }

        /// <summary>
        /// 信封 body 的 <c>conn</c>（sync-protocol 14.4）：這條連線 Hello 回應的 nonceS，伺服器身分（hubSig）驗過之後才填；之前是 null。
        /// WP4.4 驗信封時以它組 <c>CommandEnvelopeTarget</c>（裝置略過 negotiate，拿不到 SignalR 的連線 id）
        /// </summary>
        public string? EnvelopeConn { get; set; }

        /// <summary>Hello／Prove 進行中（連線被關、沒有代碼時分類成 not_proven）</summary>
        public bool InHandshake { get; set; }

        /// <summary>交握完成（Prove 與名冊檢查都過了），可以送批次、拉取、心跳</summary>
        public bool Proven { get; set; }

        /// <summary>收過 ServerNotice{shutdown}：之後的斷線不當成錯誤顯示</summary>
        public bool ShutdownNotice { get; set; }

        /// <summary>
        /// Prove 回 updateOnly（app 自我更新 U3；sync-protocol 1.4）：伺服器只放行心跳與換存取權杖。這條連線不送批次、不拉取、不拉名冊、
        /// 不上傳用量，功能只留 update1
        /// </summary>
        public bool UpdateOnly { get; set; }

        public bool Pulling { get; set; }

        public bool PullRequested { get; set; }

        /// <summary>這條連線拉到過 more:false（追上伺服器）</summary>
        public bool PulledToEnd { get; set; }

        /// <summary>PublishBatch 的呼叫進行中（同一條連線的批次一個接一個送，伺服器依到達順序處理）</summary>
        public bool Sending { get; set; }

        public bool TrustRefreshing { get; set; }

        public bool TrustRefreshAgain { get; set; }

        /// <summary>Prove 完成前收到的用量指派（Prove 之後照 term 合併）</summary>
        public List<UsageAssign> EarlyAssigns { get; } = [];

        /// <summary>Prove 完成前收到的 TrustChanged 的名冊版本</summary>
        public long? EarlyTrustRev { get; set; }

        /// <summary>交握完成前收到的伺服器通知（最多 <see cref="MaxEarlyNotices"/> 則；交握完成才依序採用，連線中斷就隨連線丟掉）</summary>
        public List<ServerNotice> EarlyNotices { get; } = [];

        /// <summary>交握完成前收到的第 4 階段訊息（D49；交握完成才依序處理，連線中斷就隨連線丟掉）</summary>
        public SyncEarlyMessages EarlyMessages { get; } = new();

        /// <summary>這條連線 Hello 宣告的功能（null＝沒宣告，第 3 階段的行為）</summary>
        public IReadOnlyList<string>? DeclaredFeatures { get; set; }

        /// <summary>這條連線協商出的功能（伺服器身分驗過之後才填；之前是空的）</summary>
        public IReadOnlyList<string> Features { get; set; } = [];

        /// <summary>這條連線已經記過「略過沒協商的訊息」的方法（每種只記一次 log）</summary>
        public HashSet<string> IgnoredMethods { get; } = new(StringComparer.Ordinal);

        public void Cancel()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException ex)
            {
                // 取消時傳輸登記的回呼丟出例外：記下來就好，連線本來就要丟掉
                Core.Log.Warn($"取消同步連線時回呼丟出例外：{ex.InnerException?.Message}");
            }

            cts.Dispose();
        }

        void ISyncSessionEvents.OnBatchAck(BatchAck ack) => engine.Deliver(this, () => engine.OnBatchAck(this, ack));

        void ISyncSessionEvents.OnNotify(Notify notify) => engine.Deliver(this, () => engine.OnNotify(this, notify));

        void ISyncSessionEvents.OnUsageAssign(UsageAssign assign) => engine.Deliver(this, () => engine.OnUsageAssign(this, assign));

        void ISyncSessionEvents.OnTrustChanged(TrustChanged changed) => engine.Deliver(this, () => engine.OnTrustChanged(this, changed));

        void ISyncSessionEvents.OnServerNotice(ServerNotice notice) => engine.Deliver(this, () => engine.OnServerNotice(this, notice));

        void ISyncSessionEvents.OnWatchChanged(WatchChanged changed) => engine.Deliver(this, () => engine.OnPhase4Message(this, changed));

        void ISyncSessionEvents.OnSnapshotRequest(SnapshotRequest request) => engine.Deliver(this, () => engine.OnPhase4Message(this, request));

        void ISyncSessionEvents.OnMediaRequest(MediaRequest request) => engine.Deliver(this, () => engine.OnPhase4Message(this, request));

        void ISyncSessionEvents.OnCommandDelivery(CommandDelivery delivery) => engine.Deliver(this, () => engine.OnPhase4Message(this, delivery));

        void ISyncSessionEvents.OnPeerReply(PeerReply reply) => engine.Deliver(this, () => engine.OnPhase4Message(this, reply));

        void ISyncSessionEvents.OnStatsUpdate(StatsUpdate update) => engine.Deliver(this, () => engine.OnPhase4Message(this, update));

        void ISyncSessionEvents.OnTokensUpdate(TokensUpdate update) => engine.Deliver(this, () => engine.OnPhase4Message(this, update));

        void ISyncSessionEvents.OnSchedulesUpdate(SchedulesUpdate update) => engine.Deliver(this, () => engine.OnPhase4Message(this, update));

        void ISyncSessionEvents.OnComposerDraftUpdate(ComposerDraftUpdate update) => engine.Deliver(this, () => engine.OnPhase4Message(this, update));

        void ISyncSessionEvents.OnCollabUpdate(CollaboratorsState state) => engine.Deliver(this, () => engine.OnPhase4Message(this, state));

        void ISyncSessionEvents.OnLiveResync(LiveResync resync) => engine.Deliver(this, () => engine.OnPhase4Message(this, resync));

        // 第 5 階段的伺服器訊息走同一條分派（交握完成前暫存、沒協商功能的略過；DeviceFeatureRules.RequiredFor）

        void ISyncSessionEvents.OnHandoffRequest(HandoffRequest request) => engine.Deliver(this, () => engine.OnPhase4Message(this, request));

        void ISyncSessionEvents.OnHandoffUpdate(HandoffUpdate update) => engine.Deliver(this, () => engine.OnPhase4Message(this, update));

        void ISyncSessionEvents.OnLogRepair(LogRepair repair) => engine.Deliver(this, () => engine.OnPhase4Message(this, repair));

        void ISyncSessionEvents.OnExecReply(ExecChunk chunk) => engine.Deliver(this, () => engine.OnPhase4Message(this, chunk));

        void ISyncSessionEvents.OnExecAbort(ExecAbort abort) => engine.Deliver(this, () => engine.OnPhase4Message(this, abort));

        void ISyncSessionEvents.OnDeskDown(DeskPacket packet) => engine.Deliver(this, () => engine.OnPhase4Message(this, packet));

        void ISyncSessionEvents.OnClosed(Exception? error) => engine.Deliver(this, () => engine.OnClosed(this, error));
    }
}
