using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Myriagent.Core;
using Myriagent.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Myriagent.Sync;

/// <summary>
/// 裝置 hub 的 SignalR 傳輸（sync-protocol 3、4；計畫 D11）：
/// <list type="bullet">
/// <item>每次連線 new 一個 HubConnection，不用自動重連與 stateful reconnect（重連狀態機在引擎）；KeepAlive／ServerTimeout 用預設值（15／30 秒，配合伺服器）。</item>
/// <item>只用 WebSockets、略過 negotiate；自訂 WebSocketFactory：權杖只放 Authorization 標頭（不設 AccessTokenProvider，不進查詢字串）、
/// 不送 Origin、HTTP/1.1、收集升級失敗的 HTTP 狀態（401／403），TLS 交握時記下葉憑證算 SPKI 雜湊（Hello 的 observedSpkiSha256）。</item>
/// <item>憑證：正式環境照 WebPKI 驗；驗不過的只有開發信任（<see cref="DevTrustRules.AcceptCertificate"/>）時接受。</item>
/// <item>伺服器→裝置的處理常式在 StartAsync 之前登記，只把訊息交給這條連線的 <see cref="ISyncSessionEvents"/>。</item>
/// <item>hub 方法的錯誤以 <see cref="SyncFailureRules"/> 分類成 <see cref="SyncTransportException"/>。</item>
/// <item>建立連線（StartAsync）最多等 <see cref="SyncTimingRules.ConnectTimeout"/>，超過算連線逾時（測試可以換成較短的）。</item>
/// </list>
/// 只能經 <see cref="SyncEngineLoader"/> 的載入邊界建立（S6：未配對時不載入 SignalR）。
/// </summary>
internal sealed class SignalRSyncTransport(TimeSpan? connectTimeout = null) : ISyncTransport
{
    private readonly TimeSpan connectTimeout = connectTimeout ?? SyncTimingRules.ConnectTimeout;

    public async Task<ISyncSession> ConnectAsync(SyncConnectRequest request, ISyncSessionEvents events, CancellationToken ct)
    {
        var factory = new SocketFactory(request);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(request.Server, SyncProtocol.DeviceHubPath), HttpTransportType.WebSockets, factory.Configure)
            .AddNewtonsoftJsonProtocol(o => o.PayloadSerializerSettings = SyncJson.Settings)
            .Build();
        var session = new Session(connection, factory, events);
        // StartAsync 本身沒有上限（WebSocket 的 TCP、TLS 與升級都只看取消）：伺服器換版那幾秒建立的連線可能一直等不到回應
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(connectTimeout);
        try
        {
            await connection.StartAsync(limit.Token);
        }
        catch (Exception ex)
        {
            await DisposeQuietlyAsync(connection);
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            // 升級失敗的 HTTP 狀態（401／403）與 TLS 驗證失敗都在 factory 上；訊息只放例外型別（權杖不會出現在例外裡）
            var timedOut = limit.IsCancellationRequested && !ct.IsCancellationRequested;
            throw new SyncTransportException(SyncFailureRules.ClassifyConnect(factory.HttpStatus, factory.TlsRejected, timedOut, connectTimeout), ex);
        }

        if (factory.ObservedSpki is null)
        {
            // 沒有擷取到伺服器憑證就不送 Hello（不能證明連到的是哪一張憑證）
            await DisposeQuietlyAsync(connection);
            throw new SyncTransportException(new SyncFailure(SyncFailureKind.Transient, "tls", "沒有擷取到伺服器的憑證"));
        }

        return session;
    }

    private static async Task DisposeQuietlyAsync(HubConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"關閉 SignalR 連線時發生錯誤（忽略）：{ex.GetType().Name}");
        }
    }

    /// <summary>一條連線的 WebSocket 工廠：擷取到的憑證 SPKI 與升級的 HTTP 狀態存在它身上</summary>
    private sealed class SocketFactory(SyncConnectRequest request)
    {
        public string? ObservedSpki { get; private set; }

        public int? HttpStatus { get; private set; }

        public bool TlsRejected { get; private set; }

        public void Configure(HttpConnectionOptions options)
        {
            options.SkipNegotiation = true;
            options.WebSocketFactory = CreateAsync;
        }

        private async ValueTask<WebSocket> CreateAsync(WebSocketConnectionContext context, CancellationToken ct)
        {
            var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.HttpVersion = HttpVersion.Version11;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            socket.Options.SetRequestHeader("Authorization", "Bearer " + request.DeviceToken);
            socket.Options.RemoteCertificateValidationCallback = Validate;
            try
            {
                await socket.ConnectAsync(context.Uri, ct);
                return socket;
            }
            catch
            {
                if (socket.HttpStatusCode != 0)
                    HttpStatus = (int)socket.HttpStatusCode;
                socket.Dispose();
                throw;
            }
        }

        private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null || !DevTrustRules.AcceptCertificate(request.Server, request.PairedServer, errors, request.DevTrustEnv))
            {
                TlsRejected = true;
                return false;
            }

            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            ObservedSpki = HandshakeRules.SpkiSha256(leaf);
            return true;
        }
    }

    /// <summary>一條已連上的連線</summary>
    private sealed class Session : ISyncSession
    {
        private readonly HubConnection connection;
        private readonly SocketFactory factory;
        private int disposed;

        public Session(HubConnection connection, SocketFactory factory, ISyncSessionEvents events)
        {
            this.connection = connection;
            this.factory = factory;
            // StartAsync 之前登記：Prove 回應之前就可能收到（D11），處理常式只做入列
            connection.On<BatchAck>(DeviceHubMethods.BatchAck, events.OnBatchAck);
            connection.On<Notify>(DeviceHubMethods.Notify, events.OnNotify);
            connection.On<UsageAssign>(DeviceHubMethods.UsageAssign, events.OnUsageAssign);
            connection.On<TrustChanged>(DeviceHubMethods.TrustChanged, events.OnTrustChanged);
            connection.On<ServerNotice>(DeviceHubMethods.ServerNotice, events.OnServerNotice);
            // 第 4 階段（WP4.1 步 10）：沒協商功能的伺服器不送；送了也由引擎略過（DeviceFeatureRules.RequiredFor）
            connection.On<WatchChanged>(DeviceHubMethods.WatchChanged, events.OnWatchChanged);
            connection.On<SnapshotRequest>(DeviceHubMethods.SnapshotRequest, events.OnSnapshotRequest);
            connection.On<MediaRequest>(DeviceHubMethods.MediaRequest, events.OnMediaRequest);
            connection.On<CommandDelivery>(DeviceHubMethods.CommandDelivery, events.OnCommandDelivery);
            connection.On<PeerReply>(DeviceHubMethods.PeerReply, events.OnPeerReply);
            connection.On<StatsUpdate>(DeviceHubMethods.StatsUpdate, events.OnStatsUpdate);
            connection.On<TokensUpdate>(DeviceHubMethods.TokensUpdate, events.OnTokensUpdate);
            connection.On<SchedulesUpdate>(DeviceHubMethods.SchedulesUpdate, events.OnSchedulesUpdate);
            connection.On<CollaboratorsState>(DeviceHubMethods.CollabUpdate, events.OnCollabUpdate);
            connection.On<LiveResync>(DeviceHubMethods.LiveResync, events.OnLiveResync);
            // 第 5 階段（WP5.1）：沒協商 hand1／log1 的伺服器不送；送了也由引擎略過
            connection.On<HandoffRequest>(DeviceHubMethods.HandoffRequest, events.OnHandoffRequest);
            connection.On<HandoffUpdate>(DeviceHubMethods.HandoffUpdate, events.OnHandoffUpdate);
            connection.On<LogRepair>(DeviceHubMethods.LogRepair, events.OnLogRepair);
            // 遠端執行指令（14.11）：沒協商 exec1 的伺服器不送；送了也由引擎略過
            connection.On<ExecChunk>(DeviceHubMethods.ExecReply, events.OnExecReply);
            connection.On<ExecAbort>(DeviceHubMethods.ExecAbort, events.OnExecAbort);
            // 遠端桌面（14.16）：沒協商 desk1 的伺服器不送；送了也由引擎略過
            connection.On<DeskPacket>(DeviceHubMethods.DeskDown, events.OnDeskDown);
            connection.Closed += error =>
            {
                events.OnClosed(error);
                return Task.CompletedTask;
            };
        }

        public string ObservedSpkiSha256 => factory.ObservedSpki!;

        public Task<HelloResponse> HelloAsync(HelloRequest r, CancellationToken ct) => InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, r, handshake: true, ct);

        public Task<ProveResponse> ProveAsync(ProveRequest r, CancellationToken ct) => InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, r, handshake: true, ct);

        public async Task PublishBatchAsync(Batch b, CancellationToken ct)
        {
            // 送出前自檢：超過單則訊息上限伺服器會關掉整條連線（NextBatch 已經保證，這裡多一道）
            EnsureFits(b, "批次", b.BatchId);
            await RunAsync(() => connection.InvokeAsync(DeviceHubMethods.PublishBatch, b, ct), handshake: false, ct);
        }

        public Task LiveRelayAsync(LiveRelay r, CancellationToken ct)
        {
            EnsureFits(r, "即時更新", r.ConversationId);
            return RunAsync(() => connection.SendAsync(DeviceHubMethods.LiveRelay, r, ct), handshake: false, ct);
        }

        public Task LiveDeltaAsync(LiveDelta d, CancellationToken ct)
        {
            EnsureFits(d, "即時更新的增量", d.ConversationId);
            return RunAsync(() => connection.SendAsync(DeviceHubMethods.LiveDelta, d, ct), handshake: false, ct);
        }

        public Task CommandResultAsync(CommandResult r, CancellationToken ct)
        {
            EnsureFits(r, "指令結果", r.CommandId);
            return RunAsync(() => connection.InvokeAsync(DeviceHubMethods.CommandResult, r, ct), handshake: false, ct);
        }

        public Task CommandOutputAsync(CommandOutput o, CancellationToken ct)
        {
            EnsureFits(o, "指令輸出", o.CommandId);
            return RunAsync(() => connection.InvokeAsync(DeviceHubMethods.CommandOutput, o, ct), handshake: false, ct);
        }

        public async Task<PeerCmdResult> PeerCmdAsync(PeerCmd c, CancellationToken ct)
        {
            EnsureFits(c, "桌面遠端操作的指令", c.CommandId);
            PeerCmdResult result = null!;
            await RunAsync(async () => result = await connection.InvokeAsync<PeerCmdResult>(DeviceHubMethods.PeerCmd, c, ct), handshake: false, ct);
            return result;
        }

        public async Task<StatsPublishResult> StatsPublishAsync(StatsPublish p, CancellationToken ct)
        {
            StatsPublishResult result = null!;
            await RunAsync(async () => result = await connection.InvokeAsync<StatsPublishResult>(DeviceHubMethods.StatsPublish, p, ct), handshake: false, ct);
            return result;
        }

        public Task SchedulesPublishAsync(SchedulesPublish p, CancellationToken ct)
        {
            EnsureFits(p, "排程摘要", null);
            return RunAsync(() => connection.InvokeAsync(DeviceHubMethods.SchedulesPublish, p, ct), handshake: false, ct);
        }

        public Task TokensPublishAsync(TokensPublish p, CancellationToken ct)
        {
            EnsureFits(p, "token 計數", null);
            return RunAsync(() => connection.InvokeAsync(DeviceHubMethods.TokensPublish, p, ct), handshake: false, ct);
        }

        public async Task<ExecOutputResult> ExecOutputAsync(ExecChunk c, CancellationToken ct)
        {
            EnsureFits(c, "遠端執行指令的輸出", c.CommandId);
            ExecOutputResult result = null!;
            await RunAsync(async () => result = await connection.InvokeAsync<ExecOutputResult>(DeviceHubMethods.ExecOutput, c, ct), handshake: false, ct);
            return result;
        }

        public async Task<CollabCmdResult> CollabCmdAsync(CollabCmdRequest r, CancellationToken ct)
        {
            EnsureFits(r, "協作人的管理指令", null);
            CollabCmdResult result = null!;
            await RunAsync(async () => result = await connection.InvokeAsync<CollabCmdResult>(DeviceHubMethods.CollabCmd, r, ct), handshake: false, ct);
            return result;
        }

        public async Task<DeskUpResult> DeskUpAsync(DeskPacket p, CancellationToken ct)
        {
            // 封包的大小由 DeskRules.MaxPacketBytes 限制（影格在送的一方拆好），不再序列化一次量大小
            DeskUpResult result = null!;
            await RunAsync(async () => result = await connection.InvokeAsync<DeskUpResult>(DeviceHubMethods.DeskUp, p, ct), handshake: false, ct);
            return result;
        }

        public async Task<ExecCancelResult> ExecCancelAsync(ExecCancel c, CancellationToken ct)
        {
            ExecCancelResult result = null!;
            await RunAsync(async () => result = await connection.InvokeAsync<ExecCancelResult>(DeviceHubMethods.ExecCancel, c, ct), handshake: false, ct);
            return result;
        }

        public Task<HandoffPreviewResult> HandoffPreviewAsync(HandoffPreviewRequest r, CancellationToken ct)
            => InvokeResultAsync<HandoffPreviewResult>(DeviceHubMethods.HandoffPreview, r, ct);

        public Task<HandoffStartResult> HandoffStartAsync(HandoffStartRequest r, CancellationToken ct)
            => InvokeResultAsync<HandoffStartResult>(DeviceHubMethods.HandoffStart, r, ct);

        public Task<HandoffCancelResult> HandoffCancelAsync(HandoffCancelRequest r, CancellationToken ct)
            => InvokeResultAsync<HandoffCancelResult>(DeviceHubMethods.HandoffCancel, r, ct);

        public Task<HandoffAck> HandoffProgressAsync(HandoffProgress p, CancellationToken ct)
            => InvokeResultAsync<HandoffAck>(DeviceHubMethods.HandoffProgress, p, ct);

        public Task<HandoffAck> HandoffReadyAsync(HandoffReady r, CancellationToken ct)
        {
            // logs 最多 2,000 筆：送出前自檢（超過單則訊息上限伺服器會關掉整條連線）
            EnsureFits(r, "交接的 HandoffReady", r.HandoffId);
            return InvokeResultAsync<HandoffAck>(DeviceHubMethods.HandoffReady, r, ct);
        }

        /// <summary>第 5 階段有回應的 hub 方法（交握完成之後）</summary>
        private async Task<T> InvokeResultAsync<T>(string method, object argument, CancellationToken ct)
        {
            T result = default!;
            await RunAsync(async () => result = await connection.InvokeAsync<T>(method, argument, ct), handshake: false, ct);
            return result;
        }

        /// <summary>
        /// 序列化後超過單則訊息上限減呼叫框的預留（<see cref="SyncProtocol.FitsInvocation"/>）就不送（整個框超過上限時伺服器會關掉整條連線）：
        /// 丟 <c>too_large</c>（ClientBug）
        /// </summary>
        private static void EnsureFits(object message, string what, string? id)
        {
            var size = Encoding.UTF8.GetByteCount(SyncJson.Serialize(message));
            if (SyncProtocol.FitsInvocation(size))
                return;
            Log.Error($"{what} {id} 序列化後 {size} 位元組，超過單則訊息上限（程式錯誤）：不送");
            throw new SyncTransportException(new SyncFailure(SyncFailureKind.ClientBug, SyncErrorCodes.TooLarge, what + "超過單則訊息上限"));
        }

        public Task<PullResponse> PullAsync(PullRequest r, CancellationToken ct) => InvokeAsync<PullResponse>(DeviceHubMethods.Pull, r, handshake: false, ct);

        public Task HeartbeatAsync(Heartbeat h, CancellationToken ct) => RunAsync(() => connection.SendAsync(DeviceHubMethods.Heartbeat, h, ct), handshake: false, ct);

        public Task UsagePublishAsync(UsagePublish u, CancellationToken ct) => RunAsync(() => connection.InvokeAsync(DeviceHubMethods.UsagePublish, u, ct), handshake: false, ct);

        public Task<TrustBundle> GetTrustAsync(GetTrustRequest r, CancellationToken ct) => InvokeAsync<TrustBundle>(DeviceHubMethods.GetTrust, r, handshake: false, ct);

        public async Task<AccessTokenResponse> RefreshAccessTokenAsync(CancellationToken ct)
        {
            AccessTokenResponse result = null!;
            await RunAsync(async () => result = await connection.InvokeAsync<AccessTokenResponse>(DeviceHubMethods.RefreshAccessToken, ct), handshake: false, ct);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            await DisposeQuietlyAsync(connection);
        }

        private async Task<T> InvokeAsync<T>(string method, object argument, bool handshake, CancellationToken ct)
        {
            T result = default!;
            await RunAsync(async () => result = await connection.InvokeAsync<T>(method, argument, ct), handshake, ct);
            return result;
        }

        /// <summary>hub 方法的錯誤一律轉成 <see cref="SyncTransportException"/>（取消照原樣丟出）</summary>
        private static async Task RunAsync(Func<Task> call, bool handshake, CancellationToken ct)
        {
            try
            {
                await call();
            }
            catch (HubException ex)
            {
                throw new SyncTransportException(SyncFailureRules.Classify(null, SyncFailureRules.HubErrorCode(ex.Message), false, handshake), ex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or WebSocketException or AuthenticationException)
            {
                // 連線在呼叫途中被關（SignalR 以 TaskCanceledException 結束未完成的呼叫）或已經不在：交握中算 not_proven，其他是暫時性的
                throw new SyncTransportException(SyncFailureRules.Classify(null, null, false, handshake), ex);
            }
        }
    }
}
