using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Sync;
using MyriagentHub.Tls;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>測試用的已配對裝置：P-256 裝置金鑰與 chd_ 權杖（直接寫進 hub.db 與裝置目錄，不走配對）</summary>
internal sealed class TestDevice : IDisposable
{
    private TestDevice(string id, string token, ECDsa key)
    {
        Id = id;
        Token = token;
        Key = key;
    }

    public string Id { get; }

    public string Token { get; set; }

    public ECDsa Key { get; }

    /// <summary>走過真的配對流程的裝置（金鑰由測試產生、權杖由 /pair/token 換到）</summary>
    public static TestDevice Paired(string id, string token, ECDsa key) => new(id, token, key);

    public static async Task<TestDevice> AddAsync(HubTestHost host, string name = "測試電腦", long? tokenIssuedAt = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = DeviceTokenRules.NewToken();
        var record = new DeviceRecord
        {
            Id = SyncIds.NewDeviceId(),
            Name = name,
            MachineName = "TEST-PC",
            TokenHash = DeviceTokenRules.Hash(token),
            TokenIssuedAt = tokenIssuedAt ?? host.Clock.NowMs,
            DevicePubkey = key.ExportSubjectPublicKeyInfo(),
            KeyStorage = "software",
            AppVersion = "0.1.0",
            CreatedAt = host.Clock.NowMs,
        };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            db.Devices.Add(record);
            await db.SaveChangesAsync();
        }
        host.Services.GetRequiredService<DeviceDirectory>().Put(DeviceInfo.From(record));
        return new TestDevice(record.Id, token, key);
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>裝置 hub 的 SignalR 用戶端（TestServer 或真的 Kestrel）：收到的伺服器訊息依序放進 <see cref="Received"/></summary>
internal sealed class DeviceHubClient : IAsyncDisposable
{
    private readonly TaskCompletionSource<Exception?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DeviceHubClient(HubConnection connection)
    {
        Connection = connection;
        foreach (var (method, type) in new (string, Type)[]
        {
            (DeviceHubMethods.BatchAck, typeof(BatchAck)),
            (DeviceHubMethods.Notify, typeof(Notify)),
            (DeviceHubMethods.UsageAssign, typeof(UsageAssign)),
            (DeviceHubMethods.TrustChanged, typeof(TrustChanged)),
            (DeviceHubMethods.ServerNotice, typeof(ServerNotice)),
            (DeviceHubMethods.WatchChanged, typeof(WatchChanged)),
            (DeviceHubMethods.SnapshotRequest, typeof(SnapshotRequest)),
            (DeviceHubMethods.CommandDelivery, typeof(CommandDelivery)),
            (DeviceHubMethods.MediaRequest, typeof(MediaRequest)),
            (DeviceHubMethods.PeerReply, typeof(PeerReply)),
            (DeviceHubMethods.StatsUpdate, typeof(StatsUpdate)),
            (DeviceHubMethods.TokensUpdate, typeof(TokensUpdate)),
            (DeviceHubMethods.SchedulesUpdate, typeof(SchedulesUpdate)),
            (DeviceHubMethods.ExecReply, typeof(ExecChunk)),
            (DeviceHubMethods.ExecAbort, typeof(ExecAbort)),
            (DeviceHubMethods.CollabUpdate, typeof(CollaboratorsState)),
        })
        {
            connection.On(method, [type], args =>
            {
                Received.Writer.TryWrite(args[0]!);
                return Task.CompletedTask;
            });
        }
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    public Channel<object> Received { get; } = Channel.CreateUnbounded<object>();

    /// <summary>連線被關掉（伺服器中斷）時完成</summary>
    public Task<Exception?> Closed => closed.Task;

    /// <summary>經 TestServer 連線（沒有 TLS：伺服器在 Testing 環境以目前的憑證當作這條連線的憑證）</summary>
    public static async Task<DeviceHubClient> ConnectAsync(HubTestHost host, string token)
    {
        var server = host.App.GetTestServer();
        var connection = Builder(new Uri("https://localhost" + SyncProtocol.DeviceHubPath), o =>
        {
            o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            o.WebSocketFactory = async (context, cancellationToken) =>
            {
                var ws = server.CreateWebSocketClient();
                ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
                return await ws.ConnectAsync(context.Uri, cancellationToken);
            };
        });
        var client = new DeviceHubClient(connection);
        await connection.StartAsync();
        return client;
    }

    /// <summary>經真的 Kestrel（wss、自簽憑證）：回傳交握看到的伺服器葉憑證</summary>
    public static Task<(DeviceHubClient Client, X509Certificate2 Seen)> ConnectKestrelAsync(HubTestHost host, string token) =>
        ConnectKestrelAsync(host.KestrelAddress!, token);

    /// <summary>經真的 Kestrel（<paramref name="baseAddress"/>＝https://127.0.0.1:埠，例如子程序的 <see cref="HubProcess"/>）</summary>
    public static async Task<(DeviceHubClient Client, X509Certificate2 Seen)> ConnectKestrelAsync(Uri baseAddress, string token, CancellationToken cancellationToken = default)
    {
        X509Certificate2? seen = null;
        var uri = new Uri(baseAddress, SyncProtocol.DeviceHubPath);
        var connection = Builder(uri, o =>
        {
            o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                seen ??= certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                return true;
            };
        });
        var client = new DeviceHubClient(connection);
        try
        {
            await connection.StartAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        return (client, seen ?? throw new InvalidOperationException("沒有看到伺服器憑證"));
    }

    /// <summary>TestServer 連線時裝置「看到」的憑證：伺服器目前的憑證</summary>
    public static string ObservedSpki(HubTestHost host) => HandshakeRules.SpkiSha256(host.Services.GetRequiredService<CertificateHolder>().Current!.Certificate);

    /// <param name="features">裝置宣告的功能（null＝不宣告，第 3 階段的行為）</param>
    public static HelloRequest HelloRequest(TestDevice device, string observedSpki, string nonceD, OutboxPosition? outbox = null, string appVersion = "0.1.0", string bootId = "boot-1",
        IReadOnlyList<string>? features = null) =>
        new(SyncProtocol.Version, device.Id, bootId, appVersion, "TEST-PC", "User", nonceD, observedSpki, null, outbox ?? new OutboxPosition(1, null, null),
            new Dictionary<string, long> { ["catalog"] = 0, ["usage"] = 0 }, new DevicePolicy("Control", false), features);

    /// <summary>
    /// 完整的 Hello／Prove（裝置以自己的金鑰簽）。<paramref name="features"/> 是 Hello 宣告的功能：app 太舊（低於伺服器的 minAppVersion）
    /// 又宣告了 <c>update1</c> 時，伺服器給只更新的連線（sync-protocol 1.4，模擬 U3 起的桌面）
    /// </summary>
    public async Task<(HelloResponse Hello, ProveResponse Prove)> HandshakeAsync(TestDevice device, string observedSpki, OutboxPosition? outbox = null, string bootId = "boot-1",
        string appVersion = "0.1.0", IReadOnlyList<string>? features = null)
    {
        var nonceD = HandshakeRules.NewNonce();
        var hello = await Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, HelloRequest(device, observedSpki, nonceD, outbox, appVersion, bootId, features));
        var sig = P256KeyRules.Sign(device.Key, HandshakeRules.DeviceSignedData(device.Id, hello.NonceS, nonceD, observedSpki));
        var prove = await Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig));
        return (hello, prove);
    }

    /// <summary>回報遠端指令的結果（cmd1；伺服器寫進 sync.db 之後才回）</summary>
    public Task SendResultAsync(CommandResult result) => Connection.InvokeAsync(DeviceHubMethods.CommandResult, result);

    /// <summary>回報遠端指令的輸出（cmd1）</summary>
    public Task SendOutputAsync(CommandOutput output) => Connection.InvokeAsync(DeviceHubMethods.CommandOutput, output);

    /// <summary>桌面遠端操作（sync-protocol 14.7）：對其他電腦下指令</summary>
    public Task<PeerCmdResult> PeerCmdAsync(PeerCmd command) => Connection.InvokeAsync<PeerCmdResult>(DeviceHubMethods.PeerCmd, command);

    /// <summary>遠端執行指令（sync-protocol 14.11）：被操作的一方送輸出</summary>
    public Task<ExecOutputResult> ExecOutputAsync(ExecChunk chunk) => Connection.InvokeAsync<ExecOutputResult>(DeviceHubMethods.ExecOutput, chunk);

    /// <summary>遠端執行指令：發起的一方要求停止</summary>
    public Task<ExecCancelResult> ExecCancelAsync(ExecCancel cancel) => Connection.InvokeAsync<ExecCancelResult>(DeviceHubMethods.ExecCancel, cancel);

    /// <summary>等第一則符合條件的伺服器訊息（其他的略過）</summary>
    public async Task<T> WaitForAsync<T>(Func<T, bool>? predicate = null, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var message = await Received.Reader.ReadAsync(cts.Token);
            if (message is T typed && (predicate is null || predicate(typed)))
                return typed;
        }
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    private static HubConnection Builder(Uri uri, Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions> configure) =>
        new HubConnectionBuilder()
            .WithUrl(uri, o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                configure(o);
            })
            .AddNewtonsoftJsonProtocol(o => o.PayloadSerializerSettings = SyncJson.Settings)
            .Build();
}
