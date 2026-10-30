using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// 本機的 TLS 終止代理（中間人）：以另一張自簽憑證接受用戶端的 TLS，再另開一條 TLS 連到真的伺服器（不驗它的憑證），兩邊照原樣轉送位元組。
/// 用戶端開著開發信任（迴路位址）時會接受這張憑證，但它看到的 SPKI 不是伺服器自己的
/// </summary>
internal sealed class TlsInterceptProxy : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly int targetPort;
    private readonly CancellationTokenSource stop = new();
    private readonly Task accepting;
    private int connections;

    private TlsInterceptProxy(X509Certificate2 certificate, int targetPort)
    {
        this.certificate = certificate;
        this.targetPort = targetPort;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptLoopAsync();
    }

    public int Port { get; }

    public Uri BaseAddress => new($"https://127.0.0.1:{Port}/");

    /// <summary>完成兩邊 TLS、開始轉送的連線數（陽性對照）</summary>
    public int Connections => Volatile.Read(ref connections);

    /// <summary>代理自己的憑證的 SPKI 雜湊（HandshakeRules.SpkiSha256）</summary>
    public string Spki => Myriagent.Security.HandshakeRules.SpkiSha256(certificate);

    public static TlsInterceptProxy Start(int targetPort)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // SChannel 要落地的私鑰（暫時性金鑰不能當 TLS 伺服器憑證）：匯出再匯入；不加 PersistKeySet，Dispose 時刪掉金鑰檔
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        return new TlsInterceptProxy(certificate, targetPort);
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var upstream = new TcpClient())
            {
                await using var down = new SslStream(client.GetStream(), false);
                await down.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    ClientCertificateRequired = false,
                }, stop.Token);
                await upstream.ConnectAsync(IPAddress.Loopback, targetPort, stop.Token);
                await using var up = new SslStream(upstream.GetStream(), false, (_, _, _, _) => true);
                await up.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                }, stop.Token);
                Interlocked.Increment(ref connections);
                var toServer = down.CopyToAsync(up, stop.Token);
                var toClient = up.CopyToAsync(down, stop.Token);
                await Task.WhenAny(toServer, toClient);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or AuthenticationException)
        {
            // 任一邊關閉或中斷：這條轉送結束
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await accepting;
        stop.Dispose();
        certificate.Dispose();
    }
}
