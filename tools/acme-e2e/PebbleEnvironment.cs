using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyriagentTools.AcmeE2e;

/// <summary>這次執行用的連接埠（全部只聽 127.0.0.1／localhost；0＝自動挑空的）</summary>
internal sealed record PortPlan(int Hub, int Acme, int AcmeManagement, int Dns, int DnsManagement, int Http01)
{
    public static PortPlan Pick(int hub, int acme, int acmeManagement, int dns, int dnsManagement)
    {
        var used = new HashSet<int>();
        int Take(int requested)
        {
            if (requested > 0)
            {
                used.Add(requested);
                return requested;
            }
            for (; ; )
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                if (used.Add(port))
                    return port;
            }
        }
        return new PortPlan(Take(hub), Take(acme), Take(acmeManagement), Take(dns), Take(dnsManagement), Take(0));
    }
}

/// <summary>Pebble 自己的 HTTPS 監聽憑證（目錄與管理介面）：本工具產生的小型 CA 簽發；CA 的 PEM 交給 MyriagentHub 的 Hub:Acme:DirectoryTrustPem</summary>
internal sealed record ListenerCertificates(string CaPemPath, string CertPemPath, string KeyPemPath, X509Certificate2 Ca);

internal static class PebbleSetup
{
    /// <summary>
    /// 產生 Pebble 監聽用的 CA 與伺服器憑證（ECDSA P-256；SAN＝localhost、127.0.0.1、::1；效期 1 天）。
    /// 金鑰只在記憶體（不寫進使用者金鑰存放區），私鑰以 PKCS#8 PEM 寫給 Pebble 讀。
    /// </summary>
    public static ListenerCertificates CreateListenerCertificates(string dir)
    {
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=myriagent acme-e2e Pebble listener CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var ca = caRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(2));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        leafRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var leaf = leafRequest.Create(ca, now.AddMinutes(-5), now.AddDays(1), serial);

        var caPem = Path.Combine(dir, "listener-ca.pem");
        var certPem = Path.Combine(dir, "listener-cert.pem");
        var keyPem = Path.Combine(dir, "listener-key.pem");
        File.WriteAllText(caPem, ca.ExportCertificatePem() + "\n", Encoding.ASCII);
        File.WriteAllText(certPem, leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem() + "\n", Encoding.ASCII);
        File.WriteAllText(keyPem, leafKey.ExportPkcs8PrivateKeyPem() + "\n", Encoding.ASCII);
        return new ListenerCertificates(caPem, certPem, keyPem, X509CertificateLoader.LoadCertificate(ca.RawData));
    }

    /// <summary>
    /// Pebble 設定：只聽 127.0.0.1；tlsPort＝MyriagentHub 的 HTTPS 埠（TLS-ALPN-01 驗證連到這裡）；
    /// 憑證設定檔 classic（MyriagentHub 預設 Hub:Acme:Profile，目錄有公告才會帶 ARI 的 replaces）與 shortlived。
    /// </summary>
    public static string WriteConfig(string dir, PortPlan ports, ListenerCertificates listener)
    {
        var config = new JsonObject
        {
            ["pebble"] = new JsonObject
            {
                ["listenAddress"] = $"127.0.0.1:{ports.Acme}",
                ["managementListenAddress"] = $"127.0.0.1:{ports.AcmeManagement}",
                ["certificate"] = listener.CertPemPath,
                ["privateKey"] = listener.KeyPemPath,
                ["httpPort"] = ports.Http01,
                ["tlsPort"] = ports.Hub,
                ["ocspResponderURL"] = string.Empty,
                ["externalAccountBindingRequired"] = false,
                ["domainBlocklist"] = new JsonArray("blocked-domain.example"),
                ["retryAfter"] = new JsonObject { ["authz"] = 1, ["order"] = 1 },
                ["keyAlgorithm"] = "ecdsa",
                ["profiles"] = new JsonObject
                {
                    ["classic"] = new JsonObject { ["description"] = "90 天（同 Let's Encrypt classic）", ["validityPeriod"] = 7_776_000 },
                    ["shortlived"] = new JsonObject { ["description"] = "6 天", ["validityPeriod"] = 518_400 },
                },
            },
        };
        var path = Path.Combine(dir, "pebble-config.json");
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return path;
    }
}

/// <summary>Pebble 的 ACME 目錄與管理介面（HTTPS；只信任本工具產生的監聽 CA）</summary>
internal sealed class PebbleControl : IDisposable
{
    private readonly HttpClient http;
    private readonly PortPlan ports;

    public PebbleControl(PortPlan ports, X509Certificate2 listenerCa)
    {
        this.ports = ports;
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(listenerCa);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return chain.Build(leaf);
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // RFC 8555 6.1：所有請求都要帶 User-Agent（Pebble 沒帶就回 400 malformed）
        http.DefaultRequestHeaders.UserAgent.ParseAdd("myriagent-acme-e2e/1.0");
    }

    public string DirectoryUrl => $"https://127.0.0.1:{ports.Acme}/dir";

    public async Task<JsonElement> DirectoryAsync(CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<JsonElement>(DirectoryUrl, cancellationToken);

    /// <summary>Pebble 這次啟動產生的發證根（每次啟動都不同）</summary>
    public async Task<X509Certificate2> RootAsync(CancellationToken cancellationToken) =>
        X509Certificate2.CreateFromPem(await http.GetStringAsync($"https://127.0.0.1:{ports.AcmeManagement}/roots/0", cancellationToken));

    public async Task<X509Certificate2> IntermediateAsync(CancellationToken cancellationToken) =>
        X509Certificate2.CreateFromPem(await http.GetStringAsync($"https://127.0.0.1:{ports.AcmeManagement}/intermediates/0", cancellationToken));

    /// <summary>
    /// 覆寫某張憑證的 ARI 回應（Pebble 管理介面 POST /set-renewal-info/）：用來模擬 CA 要求提前續期（例如撤銷事件）。
    /// </summary>
    public async Task<int> SetRenewalInfoAsync(X509Certificate2 certificate, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var ari = JsonSerializer.Serialize(new
        {
            suggestedWindow = new
            {
                start = start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                end = end.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            },
        });
        using var response = await http.PostAsJsonAsync($"https://127.0.0.1:{ports.AcmeManagement}/set-renewal-info/",
            new { certificate = certificate.ExportCertificatePem(), ariResponse = ari }, cancellationToken);
        return (int)response.StatusCode;
    }

    public void Dispose() => http.Dispose();
}

/// <summary>pebble-challtestsrv 的管理介面（HTTP，只聽 127.0.0.1）：讓網域暫時 SERVFAIL（驗證失敗）再恢復</summary>
internal sealed class ChallTestSrvControl(int managementPort) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<int> SetServFailAsync(string host, CancellationToken cancellationToken) =>
        await PostAsync("/set-servfail", new { host }, cancellationToken);

    public async Task<int> ClearServFailAsync(string host, CancellationToken cancellationToken) =>
        await PostAsync("/clear-servfail", new { host }, cancellationToken);

    public void Dispose() => http.Dispose();

    private async Task<int> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"http://127.0.0.1:{managementPort}{path}", body, cancellationToken);
        return (int)response.StatusCode;
    }
}

internal static class NetWait
{
    /// <summary>等到 127.0.0.1:埠 接受 TCP 連線</summary>
    public static async Task<bool> TcpAsync(int port, TimeSpan timeout, Func<bool> processExited, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !processExited())
        {
            using var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
        return false;
    }
}
