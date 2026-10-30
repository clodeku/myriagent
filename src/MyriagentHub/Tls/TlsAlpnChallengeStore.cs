using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Connections;

namespace MyriagentHub.Tls;

/// <summary>
/// 在「正式服務用的同一個 443」上回應 ACME TLS-ALPN-01（RFC 8737）：
/// 1. TlsClientHelloBytesCallback 讀 ClientHello，用戶端只提 "acme-tls/1" 時在連線上做記號；
/// 2. OnAuthenticate 對有記號、且有進行中驗證的連線只宣告 acme-tls/1、改用驗證用憑證；
///    其他連線用正式憑證（含中繼憑證的 SslStreamCertificateContext）。
/// 所以不必停服務、不必開 80。沒有進行中的驗證時，只提 acme-tls/1 的連線依 RFC 7301 收到 no_application_protocol。
/// </summary>
internal sealed class TlsAlpnChallengeStore(CertificateHolder holder, ILogger<TlsAlpnChallengeStore> log)
{
    public const string AcmeTlsProtocol = "acme-tls/1";
    private const string ItemKey = "acme-tls-alpn/sni";
    private static readonly SslApplicationProtocol AcmeAlpn = new(AcmeTlsProtocol);
    private readonly ConcurrentDictionary<string, SslStreamCertificateContext> challenges = new(StringComparer.OrdinalIgnoreCase);
    private int challengeHandshakes;
    private int parsedHellos;

    public int ChallengeHandshakes => challengeHandshakes;

    public int ParsedHellos => parsedHellos;

    public void Add(string domain, X509Certificate2 challengeCertificate) =>
        challenges[domain] = SslStreamCertificateContext.Create(challengeCertificate, null, offline: true);

    public void Remove(string domain) => challenges.TryRemove(domain, out _);

    /// <summary>Kestrel 在交握前呼叫（HttpsConnectionAdapterOptions.TlsClientHelloBytesCallback）</summary>
    public void InspectClientHello(ConnectionContext connection, ReadOnlySequence<byte> bytes)
    {
        if (!ClientHelloParser.TryParse(bytes, out var hello) || hello is null)
            return;
        Interlocked.Increment(ref parsedHellos);
        if (hello.Alpn.Count == 1 && hello.Alpn[0] == AcmeTlsProtocol)
            connection.Items[ItemKey] = hello.ServerName ?? string.Empty;
    }

    /// <summary>Kestrel 在其他設定都套用後呼叫（HttpsConnectionAdapterOptions.OnAuthenticate）</summary>
    public void OnAuthenticate(ConnectionContext connection, SslServerAuthenticationOptions ssl)
    {
        if (connection.Items.TryGetValue(ItemKey, out var value) && value is string sni && challenges.TryGetValue(sni, out var challenge))
        {
            Interlocked.Increment(ref challengeHandshakes);
            log.LogInformation("TLS-ALPN-01 驗證連線：SNI={Sni}，來源 {Remote}", sni, connection.RemoteEndPoint);
            ssl.ApplicationProtocols = [AcmeAlpn];
            ssl.ServerCertificateSelectionCallback = null;
            ssl.ServerCertificate = null;
            ssl.ServerCertificateContext = challenge;
            return;
        }

        // 一般連線：用含中繼憑證的 context（ServerCertificateSelector 只能回葉憑證）
        ssl.ServerCertificateSelectionCallback = null;
        ssl.ServerCertificate = null;
        ssl.ServerCertificateContext = holder.CurrentContext;
    }

    /// <summary>RFC 8737 驗證用憑證：SAN＝網域、critical 的 id-pe-acmeIdentifier（1.3.6.1.5.5.7.1.31）＝DER OCTET STRING(SHA-256(keyAuthorization))</summary>
    public static X509Certificate2 CreateChallengeCertificate(string domain, string keyAuthorization)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        request.CertificateExtensions.Add(san.Build());
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(keyAuthorization));
        var der = new byte[2 + digest.Length];
        der[0] = 0x04; // OCTET STRING
        der[1] = (byte)digest.Length;
        digest.CopyTo(der, 2);
        request.CertificateExtensions.Add(new X509Extension(new Oid("1.3.6.1.5.5.7.1.31"), der, critical: true));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
        // SChannel 要寫入磁碟的私鑰：匯出 PFX 再以預設旗標匯入（Dispose 時刪除）
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }
}
