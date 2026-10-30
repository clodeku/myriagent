using System.Collections.Concurrent;
using System.Diagnostics;
using Myriagent.Sync;

namespace FakeDevice;

/// <summary>
/// 量 BatchAck 的延遲（PublishBatch 送出到收到同一個 batchId 的 BatchAck，毫秒）與送出的即時更新數：包在正式的 SignalR 傳輸層外面，
/// 其他一律轉給它（WP4.9 步 8 從 ConversationScenarioTests 搬來，行為不變；fake-device 的引擎一律經 <see cref="TransportMeter"/> 包這一層）
/// </summary>
internal sealed class TimingTransport(ISyncTransport inner) : ISyncTransport
{
    private readonly ConcurrentDictionary<string, long> sent = new(StringComparer.Ordinal);
    private long liveRelays;

    public ConcurrentQueue<double> AckMs { get; } = new();

    /// <summary>交給連線送出的即時更新（LiveRelay）數</summary>
    public long LiveRelays => Interlocked.Read(ref liveRelays);

    public async Task<ISyncSession> ConnectAsync(SyncConnectRequest request, ISyncSessionEvents events, CancellationToken ct) =>
        new Session(await inner.ConnectAsync(request, new Events(events, this), ct), this);

    private sealed class Events(ISyncSessionEvents inner, TimingTransport owner) : ISyncSessionEvents
    {
        public void OnBatchAck(BatchAck ack)
        {
            if (owner.sent.TryRemove(ack.BatchId, out var at))
                owner.AckMs.Enqueue(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
            inner.OnBatchAck(ack);
        }

        public void OnNotify(Notify notify) => inner.OnNotify(notify);

        public void OnUsageAssign(UsageAssign assign) => inner.OnUsageAssign(assign);

        public void OnTrustChanged(TrustChanged changed) => inner.OnTrustChanged(changed);

        public void OnServerNotice(ServerNotice notice) => inner.OnServerNotice(notice);

        public void OnWatchChanged(WatchChanged changed) => inner.OnWatchChanged(changed);

        public void OnSnapshotRequest(SnapshotRequest request) => inner.OnSnapshotRequest(request);

        public void OnMediaRequest(MediaRequest request) => inner.OnMediaRequest(request);

        public void OnCommandDelivery(CommandDelivery delivery) => inner.OnCommandDelivery(delivery);

        public void OnPeerReply(PeerReply reply) => inner.OnPeerReply(reply);

        public void OnStatsUpdate(StatsUpdate update) => inner.OnStatsUpdate(update);

        public void OnTokensUpdate(TokensUpdate update) => inner.OnTokensUpdate(update);

        public void OnLiveResync(LiveResync resync) => inner.OnLiveResync(resync);

        public void OnHandoffRequest(HandoffRequest request) => inner.OnHandoffRequest(request);

        public void OnHandoffUpdate(HandoffUpdate update) => inner.OnHandoffUpdate(update);

        public void OnLogRepair(LogRepair repair) => inner.OnLogRepair(repair);

        public void OnExecReply(ExecChunk chunk) => inner.OnExecReply(chunk);

        public void OnExecAbort(ExecAbort abort) => inner.OnExecAbort(abort);

        public void OnClosed(Exception? error) => inner.OnClosed(error);
    }

    private sealed class Session(ISyncSession inner, TimingTransport owner) : ISyncSession
    {
        public string ObservedSpkiSha256 => inner.ObservedSpkiSha256;

        public Task<HelloResponse> HelloAsync(HelloRequest r, CancellationToken ct) => inner.HelloAsync(r, ct);

        public Task<ProveResponse> ProveAsync(ProveRequest r, CancellationToken ct) => inner.ProveAsync(r, ct);

        public Task PublishBatchAsync(Batch b, CancellationToken ct)
        {
            owner.sent[b.BatchId] = Stopwatch.GetTimestamp();
            return inner.PublishBatchAsync(b, ct);
        }

        public Task<PullResponse> PullAsync(PullRequest r, CancellationToken ct) => inner.PullAsync(r, ct);

        public Task HeartbeatAsync(Heartbeat h, CancellationToken ct) => inner.HeartbeatAsync(h, ct);

        public Task UsagePublishAsync(UsagePublish u, CancellationToken ct) => inner.UsagePublishAsync(u, ct);

        public Task<TrustBundle> GetTrustAsync(GetTrustRequest r, CancellationToken ct) => inner.GetTrustAsync(r, ct);

        public Task<AccessTokenResponse> RefreshAccessTokenAsync(CancellationToken ct) => inner.RefreshAccessTokenAsync(ct);

        public Task LiveRelayAsync(LiveRelay r, CancellationToken ct)
        {
            Interlocked.Increment(ref owner.liveRelays);
            return inner.LiveRelayAsync(r, ct);
        }

        public Task LiveDeltaAsync(LiveDelta d, CancellationToken ct)
        {
            // 增量（live2）也算一則即時更新
            Interlocked.Increment(ref owner.liveRelays);
            return inner.LiveDeltaAsync(d, ct);
        }

        public Task CommandResultAsync(CommandResult r, CancellationToken ct) => inner.CommandResultAsync(r, ct);

        public Task CommandOutputAsync(CommandOutput o, CancellationToken ct) => inner.CommandOutputAsync(o, ct);

        public Task<PeerCmdResult> PeerCmdAsync(PeerCmd c, CancellationToken ct) => inner.PeerCmdAsync(c, ct);

        public Task<StatsPublishResult> StatsPublishAsync(StatsPublish p, CancellationToken ct) => inner.StatsPublishAsync(p, ct);

        public Task TokensPublishAsync(TokensPublish p, CancellationToken ct) => inner.TokensPublishAsync(p, ct);

        public Task<ExecOutputResult> ExecOutputAsync(ExecChunk c, CancellationToken ct) => inner.ExecOutputAsync(c, ct);

        public Task<ExecCancelResult> ExecCancelAsync(ExecCancel c, CancellationToken ct) => inner.ExecCancelAsync(c, ct);

        public Task<HandoffPreviewResult> HandoffPreviewAsync(HandoffPreviewRequest r, CancellationToken ct) => inner.HandoffPreviewAsync(r, ct);

        public Task<HandoffStartResult> HandoffStartAsync(HandoffStartRequest r, CancellationToken ct) => inner.HandoffStartAsync(r, ct);

        public Task<HandoffCancelResult> HandoffCancelAsync(HandoffCancelRequest r, CancellationToken ct) => inner.HandoffCancelAsync(r, ct);

        public Task<HandoffAck> HandoffProgressAsync(HandoffProgress p, CancellationToken ct) => inner.HandoffProgressAsync(p, ct);

        public Task<HandoffAck> HandoffReadyAsync(HandoffReady r, CancellationToken ct) => inner.HandoffReadyAsync(r, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>BatchAck 延遲的摘要（毫秒；百分位數用 nearest-rank，同 tools/hub-e2e 的 stats-rules）</summary>
internal sealed record BatchAckSummary(int Count, double? P50, double? P95, double? Max)
{
    public static BatchAckSummary Of(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0)
            return new BatchAckSummary(0, null, null, null);
        var sorted = samples.Order().ToArray();
        double Rank(double p) => sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Length), 1, sorted.Length) - 1];
        return new BatchAckSummary(sorted.Length, Round(Rank(50)), Round(Rank(95)), Round(sorted[^1]));
    }

    private static double Round(double ms) => Math.Round(ms, 1);
}

/// <summary>
/// fake-device 的引擎工廠（WP4.9 步 8）：每個引擎一層 <see cref="TimingTransport"/> 包住正式的 SignalR 傳輸層，其他照正式的
/// <c>SyncEngineLoader.Create</c>；所有建過的引擎的 BatchAck 延遲與即時更新數合起來給 status
/// </summary>
internal sealed class TransportMeter
{
    private readonly ConcurrentQueue<TimingTransport> transports = new();

    /// <summary>建過的引擎數</summary>
    public int Engines => transports.Count;

    /// <summary>送出的即時更新數（所有引擎）</summary>
    public long LiveRelays => transports.Sum(t => t.LiveRelays);

    public BatchAckSummary BatchAck() => BatchAckSummary.Of(transports.SelectMany(t => t.AckMs).ToList());

    /// <param name="signerFactory">裝置金鑰；null＝協調者給的（再沒有就以 device.json 記的名稱開 CNG 金鑰）</param>
    public Func<SyncEngineCreateArgs, ISyncEngine> EngineFactory(Func<DeviceFileData, IDeviceSigner?>? signerFactory = null) => args =>
    {
        var timing = new TimingTransport(new SignalRSyncTransport());
        transports.Enqueue(timing);
        return new SyncEngine(new SyncEngineArgs(
            args.Paths,
            args.Device,
            signerFactory ?? args.SignerFactory ?? SyncEngineDefaults.SignerFactory,
            SyncEngineDefaults.OpenStore,
            timing,
            args.Host,
            args.Applier,
            TimeProvider.System,
            args.Options ?? SyncEngineOptions.Default,
            args.Policy,
            args.ServerOverride,
            args.DevTrustEnv,
            args.AppVersion,
            SyncEngineDefaults.BootId,
            args.MachineName,
            args.WindowsUser,
            new ConversationChangeBox()));
    };
}
