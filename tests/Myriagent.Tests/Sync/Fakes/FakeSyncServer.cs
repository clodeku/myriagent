using System.Collections.Concurrent;
using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>
/// 測試用的裝置 hub（實作 <see cref="ISyncTransport"/>）：照 MyriagentHub 的 DeviceHub／EventLog 的規則回應一台裝置——
/// Hello 以測試的伺服器身分金鑰簽（<see cref="HandshakeRules.HubSignedData"/>）、Prove 以 <see cref="HandshakeRules.VerifyDevice"/> 驗、
/// 批次照 <see cref="OutboxRules.CheckBatchShape"/>／<see cref="OutboxRules.ClassifyIncoming"/> 處理並保存這台的事件、拉取照 seq 合併各游標。
/// 每條連線有自己的世代；可以注入斷線、gap、換實例（還原）、各種通知、比 ProveResponse 先到的訊息、401、身分輪換與遺失、整批錯誤、
/// 不合格的拉取內容，也可以扣住 BatchAck 之後再送（舊連線的晚到）。所有狀態以一個鎖保護（引擎與測試在不同執行緒）。
/// partial：第 4 階段的工作包把自己的假伺服器行為放在自己的檔案（WP4.1 步 10）
/// </summary>
internal sealed partial class FakeSyncServer : ISyncTransport, IDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly Dictionary<long, (string Identity, BatchEventResult Result)> received = new();
    private readonly List<PulledEvent> log = [];
    private ECDsa hubKey;
    private long seq;
    private int connectAttempts;

    public FakeSyncServer(TimeProvider time, string deviceId, string devicePubKey)
    {
        this.time = time;
        DeviceId = deviceId;
        DevicePubKey = devicePubKey;
        hubKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        HubSpki = P256KeyRules.ExportSpki(hubKey);
        Registry = [new DeviceRegistryEntry(deviceId, "OFFICE-PC", devicePubKey, 1791500000000)];
    }

    public string DeviceId { get; }

    public string DevicePubKey { get; }

    /// <summary>目前的伺服器身分公鑰</summary>
    public string HubSpki { get; private set; }

    /// <summary>TLS 交握「看到」的伺服器憑證 SPKI 雜湊（傳輸回報給引擎的值）</summary>
    public string ObservedSpki { get; set; } = Base64UrlRules.Encode(RandomNumberGenerator.GetBytes(32));

    public ServerInstance Instance { get; set; } = new("instance-1", 0);

    public SyncLimits Limits { get; set; } = SyncLimits.Default;

    /// <summary>伺服器時鐘比測試時鐘快多少（時差警告）</summary>
    public long ClockOffsetMs { get; set; }

    public long RegistryRev { get; set; } = 1;

    public List<DeviceRegistryEntry> Registry { get; set; }

    /// <summary>HelloResponse 帶的輪換聲明（<see cref="RotateIdentity"/> 會填）</summary>
    public HubIdentityRotation? Rotation { get; set; }

    /// <summary>HelloResponse 宣告的功能（第 4 階段；null＝第 3 階段的伺服器）</summary>
    public IReadOnlyList<string>? Features { get; set; }

    /// <summary>HelloResponse 的 latestApp（app 自我更新 U3）；null＝不帶</summary>
    public AppReleaseInfo? LatestApp { get; set; }

    /// <summary>
    /// 只更新（app 自我更新 U3；sync-protocol 1.4）：Prove 回 updateOnly，之後這條連線只放行 Heartbeat 與 RefreshAccessToken，
    /// 其他呼叫照 MyriagentHub 的 DeviceHubFilter 回 update_required（引擎不該呼叫）
    /// </summary>
    public bool UpdateOnly { get; set; }

    /// <summary>第 n 次連線（從 1 起）的 Hello 要回的 hub 錯誤代碼（protocol、draining…）；null＝照常</summary>
    public Func<int, string?>? HelloError { get; set; }

    /// <summary>hubSig 用別的金鑰簽（冒牌伺服器）</summary>
    public bool ForgeHubSignature { get; set; }

    /// <summary>下一次 Prove 回的新裝置權杖（用一次就清掉）</summary>
    public string? NextTokenRotation { get; set; }

    /// <summary>下一次 Prove 回的用量指派（只列 on 的）</summary>
    public List<UsageAssign> ProveAssignments { get; } = [];

    /// <summary>伺服器這台的 next_device_seq</summary>
    public long NextDeviceSeq { get; set; } = 1;

    /// <summary>還原後的對帳模式（下一次 Prove 回 resync）</summary>
    public bool NeedsResync { get; set; }

    /// <summary>第 n 次連線（從 1 起）要丟的例外；null＝連得上</summary>
    public Func<int, Exception?>? ConnectFailure { get; set; }

    /// <summary>Prove 回應之前要做的事（送出比 ProveResponse 先到的 UsageAssign／Notify／TrustChanged）</summary>
    public Action<FakeSession>? BeforeProveReturns { get; set; }

    /// <summary>Hello 回應之前要做的事（伺服器身分驗證之前送來的訊息：中間人可以在回 Hello 之前送任何東西）</summary>
    public Action<FakeSession>? BeforeHelloReturns { get; set; }

    /// <summary>
    /// 照伺服器的物化（例如 Catalog.UpdateAccount 的版本檢查）決定一筆要不要擋：回拒絕原因（conflict 等）或 null（接受）。
    /// 在伺服器的鎖內、這筆分到 seq 之後呼叫，可以 Publish 伺服器事件（seq 排在這筆之後，同 EventLog.Apply）；被擋的不算進 <see cref="Accepted"/>
    /// </summary>
    public Func<SyncEvent, string?>? Materialize { get; set; }

    /// <summary>扣住 BatchAck，等測試 <see cref="FakeSession.ReleaseAcks"/> 才送</summary>
    public bool HoldAcks { get; set; }

    /// <summary>取代正常的批次處理（回非 null 就把它當 BatchAck 送回，不保存事件）</summary>
    public Func<Batch, BatchAck?>? BatchOverride { get; set; }

    /// <summary>取代正常的拉取回應</summary>
    public Func<PullRequest, PullResponse?>? PullOverride { get; set; }

    /// <summary>UsagePublish 要丟的例外</summary>
    public Func<UsagePublish, Exception?>? UsageFailure { get; set; }

    public ConcurrentQueue<FakeSession> Sessions { get; } = new();

    public ConcurrentQueue<SyncConnectRequest> ConnectRequests { get; } = new();

    public ConcurrentQueue<HelloRequest> Hellos { get; } = new();

    public ConcurrentQueue<Batch> Batches { get; } = new();

    public ConcurrentQueue<PullRequest> Pulls { get; } = new();

    public ConcurrentQueue<Heartbeat> Heartbeats { get; } = new();

    public ConcurrentQueue<UsagePublish> UsagePublishes { get; } = new();

    public ConcurrentQueue<GetTrustRequest> TrustRequests { get; } = new();

    public ConcurrentQueue<LiveRelay> LiveRelays { get; } = new();

    /// <summary>收到的增量（<c>live2</c>）</summary>
    public ConcurrentQueue<LiveDelta> LiveDeltas { get; } = new();

    public ConcurrentQueue<CommandResult> CommandResults { get; } = new();

    public ConcurrentQueue<CommandOutput> CommandOutputs { get; } = new();

    /// <summary>每個 hub 方法的呼叫（「世代:方法」，依呼叫順序）</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>只更新的連線上被擋下的呼叫（伺服器回 update_required 的；應該一直是空的）</summary>
    public ConcurrentQueue<string> UpdateOnlyViolations { get; } = new();

    public int ConnectAttempts => Volatile.Read(ref connectAttempts);

    /// <summary>目前最新的一條連線</summary>
    public FakeSession? Current => Sessions.LastOrDefault();

    /// <summary>伺服器提交過的這台的事件（依 deviceSeq；不含重複）</summary>
    public IReadOnlyList<SyncEvent> Accepted
    {
        get
        {
            lock (gate)
                return AcceptedEvents.OrderBy(e => e.DeviceSeq).ToList();
        }
    }

    /// <summary>同一個 deviceSeq 被套用了不只一次的次數（應該永遠是 0）</summary>
    public int DoubleApplied { get; private set; }

    private List<SyncEvent> AcceptedEvents { get; } = [];

    /// <summary>伺服器發布一筆事件（伺服器寫的種類；回傳 seq）</summary>
    public long Publish(string kind, object payload, long? createdAt = null)
    {
        var (enc, text) = PayloadRules.Encode(SyncJson.Serialize(payload));
        var placement = SyncEventKinds.Placement(kind) ?? (SyncClasses.Catalog, SyncStreams.Catalog);
        lock (gate)
        {
            var next = ++seq;
            log.Add(new PulledEvent(next, placement.Stream, placement.Cls, "server", kind, enc, text, createdAt ?? time.GetUtcNow().ToUnixTimeMilliseconds()));
            return next;
        }
    }

    /// <summary>放一筆原樣的事件進拉取的紀錄（不合格的 payload、裝置寫的種類、不認得的種類）</summary>
    public long PublishRaw(string kind, string enc, string payload, string cls = SyncClasses.Catalog, string author = "server")
    {
        lock (gate)
        {
            var next = ++seq;
            log.Add(new PulledEvent(next, cls, cls, author, kind, enc, payload, time.GetUtcNow().ToUnixTimeMilliseconds()));
            return next;
        }
    }

    /// <summary>放一筆擁有者裝置寫的對話事件進拉取的紀錄（第 4 階段，sync-protocol 14.2：串流 c:{id}、類別依種類）</summary>
    public long PublishConversation(string kind, string conversationId, string payloadJson, string author, long epoch = 1)
    {
        var (cls, stream) = ConversationEventKinds.Placement(kind, conversationId) ?? throw new ArgumentException("不是對話事件", nameof(kind));
        var (enc, text) = PayloadRules.Encode(payloadJson);
        lock (gate)
        {
            var next = ++seq;
            log.Add(new PulledEvent(next, stream, cls, author, kind, enc, text, time.GetUtcNow().ToUnixTimeMilliseconds(), conversationId, epoch));
            return next;
        }
    }

    public long High
    {
        get
        {
            lock (gate)
                return seq;
        }
    }

    /// <summary>伺服器身分金鑰輪換（admin rotate-hub-identity）：舊金鑰簽聲明</summary>
    public void RotateIdentity()
    {
        var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nextSpki = P256KeyRules.ExportSpki(next);
        var sig = P256KeyRules.Sign(hubKey, HandshakeRules.RotationSignedData(HubSpki, nextSpki));
        lock (gate)
        {
            Rotation = new HubIdentityRotation(HubSpki, nextSpki, sig);
            hubKey.Dispose();
            hubKey = next;
            HubSpki = nextSpki;
        }
    }

    /// <summary>伺服器身分遺失（重建）：換新金鑰、沒有輪換聲明</summary>
    public void LoseIdentity()
    {
        var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        lock (gate)
        {
            Rotation = null;
            hubKey.Dispose();
            hubKey = next;
            HubSpki = P256KeyRules.ExportSpki(next);
        }
    }

    /// <summary>
    /// 伺服器從備份還原（設計 10.2）：restoreGeneration＋1、這台的 next_device_seq 回到備份時的值、備份之後的事件不見了、進對帳模式。
    /// 拉取的紀錄也回到備份時（seq 不重複使用：還原後的新事件接在原本最大的 seq 之後）
    /// </summary>
    public void Restore(long nextDeviceSeqAtBackup, long highAtBackup)
    {
        lock (gate)
        {
            Instance = Instance with { RestoreGeneration = Instance.RestoreGeneration + 1 };
            NextDeviceSeq = nextDeviceSeqAtBackup;
            foreach (var key in received.Keys.Where(k => k >= nextDeviceSeqAtBackup).ToList())
                received.Remove(key);
            AcceptedEvents.RemoveAll(e => e.DeviceSeq >= nextDeviceSeqAtBackup);
            log.RemoveAll(e => e.Seq > highAtBackup);
            NeedsResync = true;
        }
    }

    /// <summary>
    /// 量記憶體用（WP4.3 計畫步 12 的長情境）：丟掉為了測試檢查而保留的每一筆事件（批次、接受的事件、去重的紀錄、對話模擬的紀錄；
    /// 真的伺服器不在桌面的程序裡）。之後不能再收這台的事件（去重的紀錄沒了）
    /// </summary>
    public void DropHistoryForMeasurement()
    {
        lock (gate)
        {
            while (Batches.TryDequeue(out _))
            {
            }

            AcceptedEvents.Clear();
            AcceptedEvents.TrimExcess();
            received.Clear();
            received.TrimExcess();
        }

        conversations?.Clear();
    }

    public Task<ISyncSession> ConnectAsync(SyncConnectRequest request, ISyncSessionEvents events, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var attempt = Interlocked.Increment(ref connectAttempts);
        ConnectRequests.Enqueue(request);
        if (ConnectFailure?.Invoke(attempt) is { } failure)
            return Task.FromException<ISyncSession>(failure);
        var session = new FakeSession(this, events, attempt);
        Sessions.Enqueue(session);
        return Task.FromResult<ISyncSession>(session);
    }

    /// <summary>以伺服器目前的身分金鑰簽一個指令信封（伺服器在 WP4.5 才簽；這裡給測試驗 conn 綁定用）</summary>
    public bool TrySignEnvelope(CommandEnvelopeBody body, out CommandEnvelope? envelope) =>
        CommandEnvelope.TrySign(body, data => P256KeyRules.Sign(hubKey, data), out envelope);

    public void Dispose() => hubKey.Dispose();

    private HelloResponse Hello(FakeSession session, HelloRequest r)
    {
        lock (gate)
        {
            Hellos.Enqueue(r);
            if (HelloError?.Invoke(session.Generation) is { } code)
                throw new SyncTransportException(SyncFailureRules.Classify(null, code, false));
            var nonceS = HandshakeRules.NewNonce();
            var serverTime = time.GetUtcNow().ToUnixTimeMilliseconds() + ClockOffsetMs;
            var data = HandshakeRules.HubSignedData(r.DeviceId, r.NonceD, nonceS, r.ObservedSpkiSha256, serverTime);
            string sig;
            if (ForgeHubSignature)
            {
                using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                sig = P256KeyRules.Sign(forger, data);
            }
            else
            {
                sig = P256KeyRules.Sign(hubKey, data);
            }

            session.HelloState = (r, nonceS);
            return new HelloResponse(serverTime, nonceS, Instance, sig, HubSpki, new TrustRevs(RegistryRev), Limits, "0.0.0", Rotation, Features, LatestApp);
        }
    }

    private ProveResponse Prove(FakeSession session, ProveRequest r)
    {
        lock (gate)
        {
            if (session.HelloState is not { } hello || session.Proven)
                throw NotProven();
            var (request, nonceS) = hello;
            if (!HandshakeRules.VerifyDevice(DevicePubKey, DeviceId, nonceS, request.NonceD, request.ObservedSpkiSha256, r.DeviceSig))
                throw NotProven();
            long? resyncFrom = NeedsResync ? OutboxRules.ResyncFrom(request.Outbox) : null;
            var expected = resyncFrom is { } from ? Math.Min(NextDeviceSeq, from) : NextDeviceSeq;
            session.ResyncFrom = resyncFrom;
            session.HelloOutbox = request.Outbox;
            var rotation = NextTokenRotation;
            NextTokenRotation = null;
            // 只更新的連線不進對帳、不當用量候選（MyriagentHub 的 DeviceHub.Prove）
            var updateOnly = UpdateOnly;
            var assignments = updateOnly ? [] : ProveAssignments.ToList();
            if (updateOnly)
            {
                resyncFrom = null;
                expected = NextDeviceSeq;
                session.ResyncFrom = null;
            }

            session.Proven = true;
            session.UpdateOnly = updateOnly;
            return new ProveResponse("cha_test", time.GetUtcNow().ToUnixTimeMilliseconds() + 900_000 + AccessTokenExpiryOffsetMs, expected, resyncFrom is not null, assignments, rotation,
                UpdateOnly: updateOnly ? true : null);
        }
    }

    /// <summary>照 MyriagentHub 的 EventLog.AppendBatch 處理一批</summary>
    private BatchAck Append(FakeSession session, Batch batch)
    {
        lock (gate)
        {
            Batches.Enqueue(batch);
            if (BatchOverride?.Invoke(batch) is { } overridden)
                return overridden;
            var next = NextDeviceSeq;
            if (OutboxRules.CheckBatchShape(batch, Limits) is { } shapeError)
                return new BatchAck(batch.BatchId ?? string.Empty, next - 1, [], new BatchError(shapeError));
            var start = next;
            var results = new List<BatchEventResult>();
            var applied = new List<(long DeviceSeq, string Identity, BatchEventResult Result, SyncEvent Event)>();
            foreach (var e in batch.Events)
            {
                var identity = OutboxRules.EventIdentity(e);
                string? existing = received.TryGetValue(e.DeviceSeq, out var found) ? found.Identity : null;
                var pending = applied.FirstOrDefault(a => a.DeviceSeq == e.DeviceSeq);
                if (existing is null && pending.Identity is not null)
                    existing = pending.Identity;
                switch (OutboxRules.ClassifyIncoming(next, e.DeviceSeq, existing, identity, session.ResyncFrom))
                {
                    case IncomingEventKind.Duplicate:
                        results.Add(received.TryGetValue(e.DeviceSeq, out var dup) ? dup.Result : pending.Result);
                        continue;
                    case IncomingEventKind.Regression:
                        return new BatchAck(batch.BatchId, start - 1, [], new BatchError(SyncErrorCodes.SeqRegression));
                    case IncomingEventKind.Gap:
                        return new BatchAck(batch.BatchId, start - 1, [], new BatchError(SyncErrorCodes.Gap, start));
                    case IncomingEventKind.GapAccepted:
                        next = e.DeviceSeq;
                        applied.Add(ApplyOne(e, identity));
                        results.Add(applied[^1].Result);
                        next++;
                        continue;
                    case IncomingEventKind.Next:
                        applied.Add(ApplyOne(e, identity));
                        results.Add(applied[^1].Result);
                        next++;
                        continue;
                    case IncomingEventKind.Missing:
                        applied.Add(ApplyOne(e, identity));
                        results.Add(applied[^1].Result);
                        continue;
                    default:
                        results.Add(new BatchEventResult(e.DeviceSeq));
                        continue;
                }
            }

            foreach (var (deviceSeq, identity, result, e) in applied)
            {
                if (received.ContainsKey(deviceSeq))
                    DoubleApplied++;
                received[deviceSeq] = (identity, result);
                if (result.Rejected is null)
                    AcceptedEvents.Add(e);
            }

            NextDeviceSeq = next;
            if (session.ResyncFrom is not null && session.HelloOutbox is { } hello && OutboxRules.IsResyncDone(next, hello))
            {
                NeedsResync = false;
                session.ResyncFrom = null;
            }

            return new BatchAck(batch.BatchId, batch.Events[^1].DeviceSeq, results);
        }
    }

    private (long, string, BatchEventResult, SyncEvent) ApplyOne(SyncEvent e, string identity)
    {
        var assigned = ++seq;
        // 第 4 階段的對話事件（FakeSyncServer.Conversations.cs）：結果可以帶 Owner／Epoch
        if (MaterializeConversation(e) is { } conversation)
            return (e.DeviceSeq, identity, conversation.Rejected is null ? new BatchEventResult(e.DeviceSeq, assigned) : conversation with { DeviceSeq = e.DeviceSeq }, e);
        if (Materialize?.Invoke(e) is { } reason)
            return (e.DeviceSeq, identity, new BatchEventResult(e.DeviceSeq, Rejected: reason), e);
        return (e.DeviceSeq, identity, new BatchEventResult(e.DeviceSeq, assigned), e);
    }

    /// <summary>照 MyriagentHub 的 EventLog.Pull：每個游標（依類別，或第 4 階段依串流）取最多 maxEvents＋1 筆、依 seq 合併後取前綴（筆數上限）</summary>
    private PullResponse Pull(PullRequest r)
    {
        lock (gate)
        {
            Pulls.Enqueue(r);
            if (PullOverride?.Invoke(r) is { } overridden)
                return overridden;
            var maxEvents = Math.Clamp(r.MaxEvents, 1, Limits.MaxPullEvents);
            var merged = new SortedDictionary<long, PulledEvent>();
            foreach (var cursor in r.Cursors)
            {
                foreach (var e in log.Where(e => (cursor.Stream is { } stream ? e.Stream == stream : e.Cls == cursor.Cls) && e.Seq > cursor.After)
                             .OrderBy(e => e.Seq).Take(maxEvents + 1))
                    merged.TryAdd(e.Seq, e);
            }

            var events = merged.Values.Take(maxEvents).ToList();
            return new PullResponse(events, merged.Count > events.Count, seq);
        }
    }

    private TrustBundle GetTrust(GetTrustRequest r)
    {
        lock (gate)
        {
            TrustRequests.Enqueue(r);
            return new TrustBundle(RegistryRev, r.RegistryAfter == RegistryRev ? null : Registry.ToList());
        }
    }

    private static SyncTransportException NotProven() =>
        new(SyncFailureRules.Classify(null, SyncErrorCodes.NotProven, false));

    /// <summary>一條連線（裝置端的 ISyncSession）</summary>
    internal sealed partial class FakeSession(FakeSyncServer server, ISyncSessionEvents events, int generation) : ISyncSession
    {
        private readonly ConcurrentQueue<BatchAck> heldAcks = new();

        public int Generation { get; } = generation;

        public ISyncSessionEvents Events { get; } = events;

        public bool Proven { get; set; }

        /// <summary>這條連線的 Prove 回了 updateOnly</summary>
        public bool UpdateOnly { get; set; }

        public bool Closed { get; private set; }

        public (HelloRequest Request, string NonceS)? HelloState { get; set; }

        public long? ResyncFrom { get; set; }

        public OutboxPosition? HelloOutbox { get; set; }

        public string ObservedSpkiSha256 { get; } = server.ObservedSpki;

        /// <summary>扣住的 BatchAck 數</summary>
        public int HeldAcks => heldAcks.Count;

        public Task<HelloResponse> HelloAsync(HelloRequest r, CancellationToken ct)
        {
            var response = Call(nameof(DeviceHubMethods.Hello), ct, () => server.Hello(this, r));
            if (server.BeforeHelloReturns is not { } early || !response.IsCompletedSuccessfully)
                return response;
            // 比 HelloResponse 先到的訊息：先送出，回應排在它們後面（同 BeforeProveReturns）
            early(this);
            var delayed = new TaskCompletionSource<HelloResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (SynchronizationContext.Current is { } context)
                context.Post(_ => delayed.TrySetResult(response.Result), null);
            else
                ThreadPool.QueueUserWorkItem(_ => delayed.TrySetResult(response.Result));
            return delayed.Task;
        }

        public Task<ProveResponse> ProveAsync(ProveRequest r, CancellationToken ct)
        {
            var response = Call(nameof(DeviceHubMethods.Prove), ct, () => server.Prove(this, r));
            if (server.BeforeProveReturns is not { } early || !response.IsCompletedSuccessfully)
                return response;
            // 比 ProveResponse 先到的伺服器訊息（DeviceHub 先登記連線、加群組，SetProven 在最後）：先送出訊息，
            // 回應排在它們後面（引擎執行緒依序處理：訊息先到、Prove 的接續後到）
            early(this);
            CallsAtProveReturn = -1;
            var delayed = new TaskCompletionSource<ProveResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Complete()
            {
                CallsAtProveReturn = server.Calls.Count;
                delayed.TrySetResult(response.Result);
            }

            if (SynchronizationContext.Current is { } context)
                context.Post(_ => Complete(), null);
            else
                ThreadPool.QueueUserWorkItem(_ => Complete());
            return delayed.Task;
        }

        /// <summary>延後回 ProveResponse 的那一刻，伺服器已經收到的 hub 呼叫數</summary>
        public int CallsAtProveReturn { get; private set; }

        public Task PublishBatchAsync(Batch b, CancellationToken ct) => Call(nameof(DeviceHubMethods.PublishBatch), ct, () =>
        {
            RequireProven();
            var ack = server.Append(this, b);
            if (server.HoldAcks)
                heldAcks.Enqueue(ack);
            else
                Events.OnBatchAck(ack);
            return true;
        });

        public Task<PullResponse> PullAsync(PullRequest r, CancellationToken ct) => Call(nameof(DeviceHubMethods.Pull), ct, () =>
        {
            RequireProven();
            return server.Pull(r);
        });

        public Task HeartbeatAsync(Heartbeat h, CancellationToken ct) => Call(nameof(DeviceHubMethods.Heartbeat), ct, () =>
        {
            RequireProven();
            server.Heartbeats.Enqueue(h);
            return true;
        });

        public Task UsagePublishAsync(UsagePublish u, CancellationToken ct) => Call(nameof(DeviceHubMethods.UsagePublish), ct, () =>
        {
            RequireProven();
            server.UsagePublishes.Enqueue(u);
            if (server.UsageFailure?.Invoke(u) is { } failure)
                throw failure;
            return true;
        });

        public Task<TrustBundle> GetTrustAsync(GetTrustRequest r, CancellationToken ct) => Call(nameof(DeviceHubMethods.GetTrust), ct, () =>
        {
            RequireProven();
            return server.GetTrust(r);
        });

        public Task LiveRelayAsync(LiveRelay r, CancellationToken ct) => Call(nameof(DeviceHubMethods.LiveRelay), ct, () =>
        {
            RequireProven();
            server.LiveRelays.Enqueue(r);
            return true;
        });

        public Task LiveDeltaAsync(LiveDelta d, CancellationToken ct) => Call(nameof(DeviceHubMethods.LiveDelta), ct, () =>
        {
            RequireProven();
            server.LiveDeltas.Enqueue(d);
            return true;
        });

        public Task CommandResultAsync(CommandResult r, CancellationToken ct) =>
            CommandSend(server, nameof(DeviceHubMethods.CommandResult), r, ct, () => server.CommandResults.Enqueue(r));

        public Task CommandOutputAsync(CommandOutput o, CancellationToken ct) =>
            CommandSend(server, nameof(DeviceHubMethods.CommandOutput), o, ct, () => server.CommandOutputs.Enqueue(o));

        /// <summary>送出扣住的 BatchAck（依序）</summary>
        public void ReleaseAcks()
        {
            while (heldAcks.TryDequeue(out var ack))
                Events.OnBatchAck(ack);
        }

        /// <summary>只送出最早扣住的一個 BatchAck</summary>
        public void ReleaseOneAck()
        {
            if (heldAcks.TryDequeue(out var ack))
                Events.OnBatchAck(ack);
        }

        /// <summary>丟掉扣住的 BatchAck（連線斷了、伺服器沒送出去）</summary>
        public void DiscardAcks() => heldAcks.Clear();

        /// <summary>伺服器端關閉連線（SignalR 的 Closed）</summary>
        public void Drop(Exception? error = null)
        {
            Closed = true;
            Events.OnClosed(error);
        }

        public void Notify(params string[] classes) => Events.OnNotify(new Notify(server.High, classes));

        public void Assign(string logicalAccountId, long term, bool on) => Events.OnUsageAssign(new UsageAssign(logicalAccountId, term, on));

        public void TrustChanged(long registry) => Events.OnTrustChanged(new TrustChanged(registry));

        public void Notice(string kind, int? retryAfterSec = null, string? text = null) => Events.OnServerNotice(new ServerNotice(kind, retryAfterSec, text));

        // 第 4 階段的伺服器 → 裝置（假伺服器不看協商：測試要能送出「不該送」的訊息）

        public void Watch(params string[] conversationIds) => Events.OnWatchChanged(new WatchChanged(conversationIds));

        public void Snapshot(string conversationId, string reason = SnapshotReasons.Open) => Events.OnSnapshotRequest(new SnapshotRequest(conversationId, reason));

        public void Media(MediaRequest request) => Events.OnMediaRequest(request);

        public void Command(CommandDelivery delivery) => Events.OnCommandDelivery(delivery);

        public void Resync(string conversationId, long ord, string reason = LiveResyncReasons.Base) => Events.OnLiveResync(new LiveResync(conversationId, ord, reason));

        public ValueTask DisposeAsync()
        {
            if (!Closed)
            {
                Closed = true;
                Events.OnClosed(null);
            }

            return ValueTask.CompletedTask;
        }

        private void RequireProven()
        {
            if (!Proven)
                throw NotProven();
        }

        private Task<T> Call<T>(string method, CancellationToken ct, Func<T> body)
        {
            server.Calls.Enqueue($"{Generation}:{method}");
            if (ct.IsCancellationRequested)
                return Task.FromCanceled<T>(ct);
            if (Closed)
                return Task.FromException<T>(new SyncTransportException(SyncFailureRules.Classify(null, null, false)));
            if (UpdateOnly && method is not (nameof(DeviceHubMethods.Heartbeat) or nameof(DeviceHubMethods.RefreshAccessToken)))
            {
                server.UpdateOnlyViolations.Enqueue(method);
                return Task.FromException<T>(new SyncTransportException(SyncFailureRules.Classify(null, SyncErrorCodes.UpdateRequired, false)));
            }
            try
            {
                return Task.FromResult(body());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }
    }
}
