using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MyriagentTools.AcmeE2e;

/// <summary>一次 TLS 交握看到的東西：協商出的 ALPN、葉憑證、伺服器送來的其他憑證（中繼）</summary>
internal sealed record HandshakeResult(bool Ok, string? Alpn, X509Certificate2? Leaf, IReadOnlyList<X509Certificate2> SentChain, string? Error, double ElapsedMs)
{
    public string? Thumbprint => Leaf?.Thumbprint;
}

/// <summary>TLS 探測：一律連 127.0.0.1:埠、SNI＝伺服器網域（不需要 DNS）；憑證另外用 Pebble 的根驗證</summary>
internal static class TlsProbe
{
    public static async Task<HandshakeResult> HandshakeAsync(int port, string sni, IReadOnlyList<string> alpn, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        X509Certificate2? leaf = null;
        var sent = new List<X509Certificate2>();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                ApplicationProtocols = alpn.Select(a => new SslApplicationProtocol(a)).ToList(),
                RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                {
                    if (certificate is not null)
                        leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    // SslStream 把伺服器送來的其他憑證放在 ExtraStore（用來確認伺服器有送中繼憑證）
                    if (chain is not null)
                    {
                        foreach (var extra in chain.ChainPolicy.ExtraStore)
                        {
                            if (leaf is null || !extra.RawDataMemory.Span.SequenceEqual(leaf.RawDataMemory.Span))
                                sent.Add(X509CertificateLoader.LoadCertificate(extra.RawData));
                        }
                    }
                    return true;
                },
            }, cancellationToken);
            return new HandshakeResult(true, ssl.NegotiatedApplicationProtocol.ToString(), leaf, sent, null, started.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 交握失敗也是結果（例如只提 acme-tls/1 而沒有進行中的驗證時，伺服器依 RFC 7301 回 no_application_protocol）
            return new HandshakeResult(false, null, leaf, sent, Describe(ex), started.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>以指定的根憑證（Pebble 的發證根）驗證伺服器憑證鏈；不上網抓 AIA、不查撤銷</summary>
    public static (bool Valid, string Detail) ValidateChain(X509Certificate2 leaf, IEnumerable<X509Certificate2> sentChain, X509Certificate2 root, string dnsName)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.AddRange(sentChain.ToArray());
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        var built = chain.Build(leaf);
        var names = leaf.MatchesHostname(dnsName);
        var status = string.Join("; ", chain.ChainStatus.Select(s => s.Status.ToString()));
        var path = string.Join(" → ", chain.ChainElements.Select(e => e.Certificate.Subject));
        return (built && names, $"built={built} hostname={names} path={path}{(status.Length > 0 ? " status=" + status : string.Empty)}");
    }

    public static string Describe(Exception ex) =>
        ex.GetType().Name + ": " + ex.Message + (ex.InnerException is { } inner ? " / " + inner.GetType().Name + ": " + inner.Message : string.Empty);
}

/// <summary>
/// 一條手寫的 HTTP/1.1 keep-alive 連線（同一個 TLS 工作階段反覆送 GET）：熱換憑證後再送一次，
/// 回應正常＝這條在熱換前建立的連線沒有被中斷，且它看到的仍是熱換前的憑證。
/// </summary>
internal sealed class KeepAliveHttp1 : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly SslStream ssl;
    private readonly string hostHeader;
    private readonly byte[] buffer = new byte[16 * 1024];
    private int bufferStart;
    private int bufferEnd;

    private KeepAliveHttp1(TcpClient tcp, SslStream ssl, string hostHeader, X509Certificate2 certificate)
    {
        this.tcp = tcp;
        this.ssl = ssl;
        this.hostHeader = hostHeader;
        Certificate = certificate;
    }

    public X509Certificate2 Certificate { get; }

    public int Requests { get; private set; }

    public static async Task<KeepAliveHttp1> OpenAsync(int port, string sni, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            X509Certificate2? seen = null;
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is not null)
                        seen = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    return true;
                },
            }, cancellationToken);
            return new KeepAliveHttp1(tcp, ssl, $"{sni}:{port}", seen ?? throw new IOException("沒有拿到伺服器憑證"));
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>送一個 GET、讀完整個回應（Content-Length 或 chunked）；連線被關掉就丟 IOException</summary>
    public async Task<(int Status, string Body)> GetAsync(string path, CancellationToken cancellationToken)
    {
        var request = $"GET {path} HTTP/1.1\r\nHost: {hostHeader}\r\nUser-Agent: myriagent-acme-e2e\r\nAccept: application/json\r\nConnection: keep-alive\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await ssl.FlushAsync(cancellationToken);

        var statusLine = await ReadLineAsync(cancellationToken) ?? throw new IOException("連線在回應前被關閉");
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var status))
            throw new IOException("看不懂的狀態列：" + statusLine);
        long? contentLength = null;
        var chunked = false;
        for (; ; )
        {
            var line = await ReadLineAsync(cancellationToken) ?? throw new IOException("連線在標頭中途被關閉");
            if (line.Length == 0)
                break;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                chunked = true;
        }

        using var body = new MemoryStream();
        if (chunked)
        {
            for (; ; )
            {
                var sizeLine = await ReadLineAsync(cancellationToken) ?? throw new IOException("chunk 中途被關閉");
                var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                if (size == 0)
                {
                    // 結尾的 trailer（通常沒有）讀到空行為止
                    while (!string.IsNullOrEmpty(await ReadLineAsync(cancellationToken)))
                    {
                    }
                    break;
                }
                await ReadExactAsync(body, size, cancellationToken);
                await ReadLineAsync(cancellationToken);
            }
        }
        else if (contentLength is { } length)
        {
            await ReadExactAsync(body, (int)length, cancellationToken);
        }
        Requests++;
        return (status, Encoding.UTF8.GetString(body.ToArray()));
    }

    public async ValueTask DisposeAsync()
    {
        await ssl.DisposeAsync();
        tcp.Dispose();
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (bufferStart > 0)
        {
            Array.Copy(buffer, bufferStart, buffer, 0, bufferEnd - bufferStart);
            bufferEnd -= bufferStart;
            bufferStart = 0;
        }
        var n = await ssl.ReadAsync(buffer.AsMemory(bufferEnd), cancellationToken);
        if (n == 0)
            return false;
        bufferEnd += n;
        return true;
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        for (; ; )
        {
            for (var i = bufferStart; i < bufferEnd - 1; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n')
                {
                    var line = Encoding.ASCII.GetString(buffer, bufferStart, i - bufferStart);
                    bufferStart = i + 2;
                    return line;
                }
            }
            if (!await FillAsync(cancellationToken))
                return null;
        }
    }

    private async Task ReadExactAsync(Stream target, int count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (bufferStart == bufferEnd && !await FillAsync(cancellationToken))
                throw new IOException("回應本文中途被關閉");
            var take = Math.Min(count, bufferEnd - bufferStart);
            target.Write(buffer, bufferStart, take);
            bufferStart += take;
            count -= take;
        }
    }
}

/// <summary>
/// HTTP/2 的連線（HttpClient＋SocketsHttpHandler）：記下建立過幾條 TCP 連線、做過幾次 TLS 交握。
/// 熱換後再送請求，連線數與交握數都不變＝原本那條 HTTP/2 連線繼續使用、沒有被中斷。
/// </summary>
internal sealed class Http2Session : IDisposable
{
    private readonly HttpClient client;
    private readonly string origin;
    private readonly List<string> handshakes = [];
    private int connects;

    public Http2Session(int port, string domain)
    {
        origin = $"https://{domain}:{port}";
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(30),
            EnableMultipleHttp2Connections = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref connects);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            lock (handshakes)
                handshakes.Add(certificate is null ? "(none)" : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()).Thumbprint);
            return true;
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public int Connects => Volatile.Read(ref connects);

    public IReadOnlyList<string> Handshakes
    {
        get
        {
            lock (handshakes)
                return handshakes.ToList();
        }
    }

    public async Task<(int Status, string Version)> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, origin + path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var response = await client.SendAsync(request, cancellationToken);
        await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return ((int)response.StatusCode, response.Version.ToString());
    }

    public void Dispose() => client.Dispose();
}
