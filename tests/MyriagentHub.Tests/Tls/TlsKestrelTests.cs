using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MyriagentHub.Tls;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Tls;

/// <summary>
/// 真的 Kestrel（127.0.0.1 隨機埠＋自簽憑證）：TLS 交握、同一個埠回應 TLS-ALPN-01、熱換憑證不重啟。
/// 完整的 ACME 簽發（Pebble＋challtestsrv）由 tools/acme-e2e 負責。
/// </summary>
public sealed class TlsKestrelTests
{
    [Fact]
    public async Task 自簽憑證服務https_healthz與安全標頭()
    {
        await using var host = await HubTestHost.StartAsync(kestrel: true);
        var holder = host.Services.GetRequiredService<CertificateHolder>();
        holder.Current!.SelfSigned.Should().BeTrue("還沒有正式憑證時先用自簽");
        using var client = PinnedClient(holder.Current.Certificate.Thumbprint, out var seen);
        using var response = await client.GetAsync(new Uri(host.KestrelAddress!, "/healthz"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seen.Should().ContainSingle().Which.Should().Be(holder.Current.Certificate.Thumbprint);
        response.Headers.GetValues("Strict-Transport-Security").Single().Should().Be("max-age=31536000");
        response.Headers.Contains("Server").Should().BeFalse("AddServerHeader=false");
    }

    [Fact]
    public async Task 同一個埠回應TLS_ALPN_01_驗證連線拿到驗證憑證()
    {
        await using var host = await HubTestHost.StartAsync(kestrel: true);
        var store = host.Services.GetRequiredService<TlsAlpnChallengeStore>();
        const string keyAuthorization = "token-abc.thumbprint-xyz";
        using var challenge = TlsAlpnChallengeStore.CreateChallengeCertificate("localhost", keyAuthorization);
        store.Add("localhost", challenge);
        try
        {
            var (protocol, certificate) = await HandshakeAsync(host.KestrelAddress!, "localhost", [new SslApplicationProtocol(TlsAlpnChallengeStore.AcmeTlsProtocol)]);
            protocol.ToString().Should().Be(TlsAlpnChallengeStore.AcmeTlsProtocol);
            var extension = certificate.Extensions.Single(e => e.Oid!.Value == "1.3.6.1.5.5.7.1.31");
            extension.Critical.Should().BeTrue();
            extension.RawData.Should().Equal(new byte[] { 0x04, 0x20 }.Concat(SHA256.HashData(Encoding.ASCII.GetBytes(keyAuthorization))));
            store.ChallengeHandshakes.Should().Be(1);

            // 一般連線同時照常拿到正式（這裡是自簽）憑證
            var (normalProtocol, normalCertificate) = await HandshakeAsync(host.KestrelAddress!, "localhost", [SslApplicationProtocol.Http11]);
            normalProtocol.Should().Be(SslApplicationProtocol.Http11);
            normalCertificate.Thumbprint.Should().Be(host.Services.GetRequiredService<CertificateHolder>().Current!.Certificate.Thumbprint);
        }
        finally
        {
            store.Remove("localhost");
        }

        // 沒有進行中的驗證時，只提 acme-tls/1 的連線依 RFC 7301 被拒
        var noChallenge = () => HandshakeAsync(host.KestrelAddress!, "localhost", [new SslApplicationProtocol(TlsAlpnChallengeStore.AcmeTlsProtocol)]);
        await noChallenge.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task 熱換憑證_新連線拿到新憑證_不必重啟()
    {
        await using var host = await HubTestHost.StartAsync(kestrel: true);
        var holder = host.Services.GetRequiredService<CertificateHolder>();
        var before = holder.Current!.Certificate.Thumbprint;
        var pfx = TlsCertificates.CreateSelfSignedPfx("localhost", TimeSpan.FromDays(2), DateTimeOffset.UtcNow);
        var next = X509CertificateLoader.LoadPkcs12(pfx, null);
        holder.Swap(next, null, selfSigned: true);
        holder.SwapCount.Should().Be(2);

        var (_, certificate) = await HandshakeAsync(host.KestrelAddress!, "localhost", [SslApplicationProtocol.Http11]);
        certificate.Thumbprint.Should().Be(next.Thumbprint).And.NotBe(before);
    }

    [Fact]
    public async Task 自簽憑證只匯入一次_重啟沿用同一張_PFX以DPAPI加密存放()
    {
        var dir = TempDir.Create();
        try
        {
            string first;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
                first = host.Services.GetRequiredService<CertificateHolder>().Current!.Certificate.Thumbprint;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                host.Services.GetRequiredService<CertificateHolder>().Current!.Certificate.Thumbprint.Should().Be(first, "不會每次啟動都產生、匯入新的金鑰");
                var files = Directory.GetFiles(Path.Combine(host.Paths.SecretsDir, "tls"));
                files.Should().ContainSingle().Which.Should().EndWith(".pfx.dpapi", "PFX 以 DPAPI 加密存放");
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    private static HttpClient PinnedClient(string thumbprint, out List<string> seen)
    {
        var observed = new List<string>();
        seen = observed;
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            var cert = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData());
            observed.Add(cert.Thumbprint);
            return cert.Thumbprint == thumbprint;
        };
        return new HttpClient(handler);
    }

    private static async Task<(SslApplicationProtocol Protocol, X509Certificate2 Certificate)> HandshakeAsync(Uri address, string sni, List<SslApplicationProtocol> alpn)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = sni,
            ApplicationProtocols = alpn,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, cts.Token);
        var remote = ssl.RemoteCertificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate!.GetRawCertData());
        return (ssl.NegotiatedApplicationProtocol, remote);
    }
}
