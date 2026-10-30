using System.Security.Cryptography.X509Certificates;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Releases;
using MyriagentHub.Sync;
using MyriagentHub.Tls;
using MyriagentHub.Trust;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 這條連線 Hello 時記下的內容（Prove 要用）。<paramref name="Features"/> 是這條連線協商出的功能（裝置與伺服器宣告的交集，sync-protocol 14.1；
/// 沒宣告是空的），Prove 時交給 <see cref="DeviceConnection"/>。<paramref name="HubSpki"/> 是 hubSig 所用的伺服器身分公鑰（sync-protocol 14.4：
/// 身分金鑰輪換之後，這條連線的裝置還釘著它，不能再對這條連線簽信封）。<paramref name="UpdateOnly"/>＝版本閘門判成只更新（sync-protocol 1.4）：
/// Prove 照它決定要不要啟動批次佇列與對帳，<see cref="DeviceHubFilter"/> 照它只放行 RefreshAccessToken 與 Heartbeat。
/// <paramref name="LatestApp"/> 是 Hello 回應給的 latestApp：Prove 加入通知群組之後比對，之間才收進來的新版補送 appRelease（HUB-1）
/// </summary>
internal sealed record HelloState(string DeviceId, string NonceD, string NonceS, string ObservedSpki, OutboxPosition Outbox, string BootId, string AppVersion, DevicePolicy Policy,
    IReadOnlyList<string>? Features = null, string? HubSpki = null, bool UpdateOnly = false, AppReleaseInfo? LatestApp = null);

/// <summary>連線的交握狀態（HubCallerContext.Items，跟著連線走）</summary>
internal static class DeviceSession
{
    private const string HelloKey = "myriagent.hello";

    private const string ProvenKey = "myriagent.proven";

    public static HelloState? Hello(HubCallerContext context) => context.Items.TryGetValue(HelloKey, out var value) ? value as HelloState : null;

    public static void SetHello(HubCallerContext context, HelloState state) => context.Items[HelloKey] = state;

    public static bool IsProven(HubCallerContext context) => context.Items.ContainsKey(ProvenKey);

    public static void SetProven(HubCallerContext context) => context.Items[ProvenKey] = true;

    public static string? DeviceId(HubCallerContext context) => context.User?.FindFirst(HubClaims.DeviceId)?.Value;
}

/// <summary>
/// 裝置 hub 每一次呼叫的關卡（sync-protocol 1.4、3、4；WP4.1 D45）：方法要在允許清單 <see cref="DeviceHubMethods.DeviceToServer"/> 裡（不在的一律
/// <c>invalid</c>；partial 檔新增的公開方法要一起改清單），Prove 成功前只能呼叫 Hello、Prove；只更新的連線 Prove 之後只能呼叫
/// RefreshAccessToken 與 Heartbeat，其他一律 <c>update_required</c>（<see cref="DeviceHubRules.CheckInvocation"/>）
/// </summary>
internal sealed class DeviceHubFilter : IHubFilter
{
    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var context = invocationContext.Context;
        if (DeviceHubRules.CheckInvocation(invocationContext.HubMethodName, DeviceSession.IsProven(context), DeviceSession.Hello(context)?.UpdateOnly == true) is { } code)
            throw new HubException(code);
        return next(invocationContext);
    }
}

/// <summary>
/// 裝置 hub（<c>/hubs/v1/device</c>，sync-protocol 3、4）：連線以 chd_ 認證，連上後 Hello／Prove 雙向簽章（綁 TLS 憑證的 SPKI），
/// 之後才能發佈批次、拉取、心跳、分享用量、拉信任資料。方法只做驗證與入列；第 3 階段沒有任何讓裝置執行動作的伺服器訊息。
/// 排空中（部署或維護）的 Hello 回 <c>draining</c>：不寫稽核、不碰資料庫，連線不中斷。Hello 的回應帶最新的可用版本（<c>latestApp</c>，
/// 沒有就省略），要不要更新、怎麼驗證由裝置決定（U3）。
/// 宣告成 partial：第 4 階段在自己的檔案加對話相關的方法。
/// </summary>
internal sealed partial class DeviceHub(
    DeviceHubRuntime runtime,
    EventLog eventLog,
    UsageCoordinator usage,
    ConnectionRegistry connections,
    DeviceDirectory devices,
    SyncWriter writer,
    HubIdentity identity,
    TrustService trust,
    AccessTokenStore accessTokens,
    CertificateHolder certificates,
    DrainState drain,
    AppReleaseStore releases,
    AuditLog audit,
    IHostEnvironment environment,
    IOptions<HubOptions> options,
    TimeProvider time,
    ILogger<DeviceHub> log) : Hub
{
    public override Task OnConnectedAsync()
    {
        // 裝置 hub 只收長效裝置權杖（cha_ 在認證時就只認 /api/v1/device/*，這裡再擋一次）
        if (DeviceSession.DeviceId(Context) is null || Context.User?.FindFirst(HubClaims.TokenKind)?.Value != HubTokenKinds.Device)
        {
            Context.Abort();
            return Task.CompletedTask;
        }
        var context = Context;
        _ = Task.Run(async () =>
        {
            await Task.Delay(DeviceHubRules.ProveTimeout);
            if (!DeviceSession.IsProven(context))
                context.Abort();
        });
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception) => runtime.OnDisconnectedAsync(Context.ConnectionId);

    public async Task<HelloResponse> Hello(HelloRequest? request)
    {
        var deviceId = DeviceSession.DeviceId(Context)!;
        if (DeviceSession.Hello(Context) is not null || request is null)
            throw new HubException(SyncErrorCodes.Invalid);
        // 排空中（部署或維護）：在寫稽核、碰資料庫之前就拒絕，連線不中斷，也不記 HelloState——
        // 排空解除後同一條連線可以再 Hello（仍受連上後 30 秒內要完成 Prove 的限制；sync-protocol 第 4 節）
        if (drain.IsDraining)
            throw new HubException(SyncErrorCodes.Draining);
        var minAppVersion = options.Value.Sync.MinAppVersion;
        // 版本閘門（sync-protocol 1.4）：協定不在範圍、或 app 太舊又沒宣告 update1 → protocol；宣告了 → 只更新的連線
        var gate = DeviceHubRules.Gate(request.ProtocolVersion, request.AppVersion, request.Features, minAppVersion);
        if (gate == HelloGate.Protocol)
            throw new HubException(SyncErrorCodes.Protocol);
        var problem = DeviceHubRules.CheckHello(request);
        if (request.DeviceId != deviceId || problem is not null)
        {
            log.LogWarning("Hello 格式不正確：{DeviceId} {Problem}", deviceId, problem ?? "deviceId 與權杖不符");
            throw new HubException(SyncErrorCodes.Invalid);
        }
        if (devices.Get(deviceId) is not { Revoked: false })
        {
            await audit.WriteAsync(new AuditEntry(AuditActors.Device, deviceId, ClientIp(), AuditActions.DeviceHello, deviceId, AuditOutcomes.Denied, new { reason = "revoked" }));
            Context.Abort();
            throw new HubException(SyncErrorCodes.NotProven);
        }
        // 裝置看到的不是這條連線的憑證＝中間人（或 TLS 終結在別處）：中斷並稽核
        if (ExpectedSpki() is not { } expected || !string.Equals(expected, request.ObservedSpkiSha256, StringComparison.Ordinal))
        {
            await audit.WriteAsync(new AuditEntry(AuditActors.Device, deviceId, ClientIp(), AuditActions.DeviceHello, deviceId, AuditOutcomes.Denied, new { reason = "spki_mismatch" }));
            Context.Abort();
            throw new HubException(SyncErrorCodes.NotProven);
        }

        var nonceS = HandshakeRules.NewNonce();
        var serverTime = time.GetUtcNow().ToUnixTimeMilliseconds();
        var hubSig = identity.Sign(HandshakeRules.HubSignedData(deviceId, request.NonceD, nonceS, request.ObservedSpkiSha256, serverTime));
        var hubSpki = identity.Spki;
        // 功能宣告（sync-protocol 14.1）：這條連線啟用兩邊的交集；伺服器宣告 conv1、live1、cmd1、media1，U2 起再加 update1（AddHubReleases），
        // 裝置也宣告的才在交集裡。只更新的連線只留 update1：第 4 階段的功能一律不啟用
        var serverFeatures = runtime.ServerFeatures;
        var features = DeviceHubRules.ConnectionFeatures(gate, DeviceFeatureRules.Negotiate(request.Features, serverFeatures));
        // latestApp：最新的可用版本（AppReleaseStore 的記憶體快取；沒有就是 null，線上省略）。只讀一次：回應與 HelloState 是同一個值
        var latestApp = releases.Latest;
        DeviceSession.SetHello(Context, new HelloState(deviceId, request.NonceD, nonceS, request.ObservedSpkiSha256, request.Outbox, request.BootId, request.AppVersion, request.Policy,
            features, hubSpki, UpdateOnly: gate == HelloGate.UpdateOnly, LatestApp: latestApp));
        var revs = await trust.GetRevsAsync();
        return new HelloResponse(serverTime, nonceS, writer.Instance, hubSig, hubSpki, revs, DeviceHubRules.HelloLimits(eventLog.Limits, features), minAppVersion, identity.Rotation, serverFeatures,
            LatestApp: latestApp);
    }

    public async Task<ProveResponse> Prove(ProveRequest? request)
    {
        if (DeviceSession.Hello(Context) is not { } hello || DeviceSession.IsProven(Context))
            throw new HubException(SyncErrorCodes.NotProven);
        if (devices.Get(hello.DeviceId) is not { Revoked: false } device
            || !HandshakeRules.VerifyDevice(device.DevicePubKey, hello.DeviceId, hello.NonceS, hello.NonceD, hello.ObservedSpki, request?.DeviceSig))
        {
            await audit.WriteAsync(new AuditEntry(AuditActors.Device, hello.DeviceId, ClientIp(), AuditActions.DeviceHello, hello.DeviceId, AuditOutcomes.Denied, new { reason = "bad_device_signature" }));
            Context.Abort();
            throw new HubException(SyncErrorCodes.NotProven);
        }

        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        // 先登記（device.upsert 的 online 要是 true），上行狀態等讀到 device_streams 再定；這條連線的呼叫依序處理，Prove 回應前不會有批次進來
        // 信封的 conn 是這條連線 Hello 回應的 nonceS（sync-protocol 14.4：裝置略過 negotiate，拿不到 SignalR 的連線 id）。
        // 只更新的連線（sync-protocol 1.4）也登記：算在線、撤銷時一起中斷，但不是用量查詢的候選（ConnectionRegistry.IsSyncing）
        var connection = new DeviceConnection(Context.ConnectionId, hello.DeviceId, Context.Abort, new UpstreamContext(hello.DeviceId, null, hello.Outbox), hello.Features,
            envelopeConn: hello.NonceS, helloHubSpki: hello.HubSpki, updateOnly: hello.UpdateOnly);
        connections.Add(connection, now);
        try
        {
            var stream = await runtime.RecordHelloAsync(hello, now);
            // 只更新的連線不進對帳：它不送批次，對帳解除不了；還原後的 needs_resync 留給換版之後的完整連線
            long? resyncFrom = !hello.UpdateOnly && stream.NeedsResync ? OutboxRules.ResyncFrom(hello.Outbox) : null;
            var expected = resyncFrom is { } from ? Math.Min(stream.NextDeviceSeq, from) : stream.NextDeviceSeq;
            if (hello.UpdateOnly)
            {
                // 不啟動批次佇列（PublishBatch 被關卡擋下）；只收 ServerNotice，不收 Notify、TrustChanged
                await Groups.AddToGroupAsync(Context.ConnectionId, DeviceHubRuntime.UpdateGroup);
            }
            else
            {
                connection.Upstream = new UpstreamContext(hello.DeviceId, resyncFrom, hello.Outbox);
                runtime.Start(connection);
                await Groups.AddToGroupAsync(Context.ConnectionId, DeviceHubRuntime.Group);
            }

            var usedPrevious = Context.User?.FindFirst(HubClaims.PreviousToken) is not null;
            var rotation = await runtime.RotateTokenIfDueAsync(hello.DeviceId, usedPrevious, now);
            var access = accessTokens.Issue(hello.DeviceId);
            // 只更新的連線不是用量查詢的候選：不重算、不帶指派
            IReadOnlyList<UsageAssign> assignments = [];
            if (!hello.UpdateOnly)
            {
                await usage.ReassessAsync();
                assignments = usage.AssignmentsFor(hello.DeviceId);
            }
            // 撤銷可能落在上面的檢查與登記連線之間：撤銷是先標目錄、再作廢存取權杖與中斷連線，
            // 這裡在登記連線、發存取權杖之後再看一次目錄，兩邊一定有一方看得到對方
            if (devices.Get(hello.DeviceId) is not { Revoked: false })
            {
                accessTokens.RevokeDevice(hello.DeviceId);
                throw new HubException(SyncErrorCodes.NotProven);
            }
            DeviceSession.SetProven(Context);
            connections.MarkProven(connection);

            if (stream.PreviousBootId != hello.BootId || stream.PreviousAppVersion != hello.AppVersion)
            {
                await audit.WriteAsync(new AuditEntry(AuditActors.Device, hello.DeviceId, ClientIp(), AuditActions.DeviceHello, hello.DeviceId, AuditOutcomes.Ok,
                    new { bootId = hello.BootId, appVersion = hello.AppVersion, resync = resyncFrom is not null }));
            }
            if (resyncFrom is not null)
            {
                await audit.WriteAsync(new AuditEntry(AuditActors.Device, hello.DeviceId, null, AuditActions.SyncResync, hello.DeviceId, AuditOutcomes.Ok,
                    new { verdict = "start", resyncFrom, expected, helloNext = hello.Outbox.NextDeviceSeq }));
            }
            // HUB-1：Hello 之後、加入群組之前開始的排空，或收進來的新版，群組廣播送不到這條連線——加入群組之後再看一次，補送給它。
            // 比 Prove 的回應先到：裝置把交握完成前到的通知先暫存，交握完成才採用（同 Prove 之後馬上到的廣播）
            var drainNow = drain.Current;
            foreach (var notice in DeviceHubRules.CatchUpNotices(drainNow.Draining, drainNow.RetryAfterSec, hello.LatestApp, releases.Latest))
            {
                log.LogInformation("補送 Hello 之後的通知給 {DeviceId}：{Kind}", hello.DeviceId, notice.Kind);
                await Clients.Caller.SendAsync(DeviceHubMethods.ServerNotice, notice);
            }
            return new ProveResponse(access.AccessToken, access.ExpiresAt, expected, resyncFrom is not null, assignments, rotation,
                UpdateOnly: hello.UpdateOnly ? true : null);
        }
        catch
        {
            Context.Abort();
            throw;
        }
    }

    public void PublishBatch(Batch? batch)
    {
        if (connections.Get(Context.ConnectionId) is not { } connection)
            throw new HubException(SyncErrorCodes.NotProven);
        if (batch is null)
            throw new HubException(SyncErrorCodes.Invalid);
        if (!connection.Batches.Writer.TryWrite(batch))
        {
            // 在途批次超過上限（裝置最多 4 批）是用戶端的錯
            log.LogWarning("裝置在途批次超過上限：{DeviceId}", connection.DeviceId);
            throw new HubException(SyncErrorCodes.Invalid);
        }
    }

    public PullResponse Pull(PullRequest? request)
    {
        try
        {
            return eventLog.Pull(request);
        }
        catch (PullRequestException)
        {
            throw new HubException(SyncErrorCodes.Invalid);
        }
    }

    public Task Heartbeat(Heartbeat? heartbeat)
    {
        if (heartbeat is null || heartbeat.Running?.Count > DeviceHubRules.MaxHeartbeatConversations || heartbeat.Waiting?.Count > DeviceHubRules.MaxHeartbeatConversations)
            throw new HubException(SyncErrorCodes.Invalid);
        return runtime.HeartbeatAsync(DeviceSession.DeviceId(Context)!, heartbeat.UiAliveAt, time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    public async Task UsagePublish(UsagePublish? publish)
    {
        if (await usage.PublishAsync(DeviceSession.DeviceId(Context)!, publish) == UsagePublishResult.Invalid)
            throw new HubException(SyncErrorCodes.Invalid);
    }

    public Task<TrustBundle> GetTrust(GetTrustRequest? request)
    {
        if (request is null || request.RegistryAfter < 0)
            throw new HubException(SyncErrorCodes.Invalid);
        return trust.GetBundleAsync(request.RegistryAfter);
    }

    public AccessTokenResponse RefreshAccessToken()
    {
        var deviceId = DeviceSession.DeviceId(Context)!;
        var access = accessTokens.Issue(deviceId);
        // 同 Prove：撤銷先標目錄、再作廢權杖與中斷連線，落在兩步之間發的權杖在這裡收回
        if (devices.Get(deviceId) is not { Revoked: false })
        {
            accessTokens.RevokeDevice(deviceId);
            Context.Abort();
            throw new HubException(SyncErrorCodes.NotProven);
        }
        return access;
    }

    /// <summary>
    /// 這條連線送出的伺服器憑證的 SPKI 雜湊（Kestrel 的 TLS 串流）。沒有 TLS（測試用的 TestServer）時只在 Testing 環境以目前的憑證代替；
    /// 正式環境沒有 TLS 就一律不認
    /// </summary>
    private string? ExpectedSpki()
    {
        if (Context.GetHttpContext()?.Features.Get<ISslStreamFeature>()?.SslStream.LocalCertificate is { } local)
        {
            if (local is X509Certificate2 certificate)
                return HandshakeRules.SpkiSha256(certificate);
            using var copy = X509CertificateLoader.LoadCertificate(local.GetRawCertData());
            return HandshakeRules.SpkiSha256(copy);
        }
        return environment.IsEnvironment("Testing") && certificates.Current?.Certificate is { } current ? HandshakeRules.SpkiSha256(current) : null;
    }

    private string? ClientIp() => Context.GetHttpContext()?.ClientIp();
}
