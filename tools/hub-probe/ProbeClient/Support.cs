using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;

namespace MyriagentHub.ProbeClient;

internal sealed record TlsResult(bool Ok, string? Negotiated, string? Thumbprint, string? Subject, string? Issuer, IReadOnlyList<string> DnsNames, string? Error, bool AcmeExtension, double ElapsedMs);

internal static class ProbeTls
{
    /// <summary>一次 TLS 交握（不驗證憑證，只記錄）：協商出的 ALPN、葉憑證、是否帶 acmeIdentifier 擴充（TLS-ALPN-01 驗證憑證）</summary>
    public static async Task<TlsResult> HandshakeAsync(string host, int port, string sni, IReadOnlyList<string> alpn)
    {
        var sw = Stopwatch.StartNew();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                ApplicationProtocols = alpn.Select(a => new SslApplicationProtocol(a)).ToList(),
            });
            var cert = X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate!.GetRawCertData());
            var acme = cert.Extensions.Any(e => e.Oid?.Value == "1.3.6.1.5.5.7.1.31");
            var names = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
            return new TlsResult(true, ssl.NegotiatedApplicationProtocol.ToString(), cert.Thumbprint, cert.Subject, cert.Issuer, names, null, acme, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or System.ComponentModel.Win32Exception)
        {
            return new TlsResult(false, null, null, null, null, [], ex.GetType().Name + ": " + ex.Message + (ex.InnerException != null ? " / " + ex.InnerException.Message : ""), false, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
        }
    }
}

/// <summary>手寫的 HTTP/1.1 keep-alive 連線（TLS），同一條連線反覆送 GET；回應以 Content-Length 或 chunked 讀完</summary>
internal sealed class KeepAliveConnection : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly SslStream ssl;
    private readonly string hostHeader;
    private readonly byte[] buffer = new byte[16 * 1024];
    private int start;
    private int end;

    private KeepAliveConnection(TcpClient tcp, SslStream ssl, string hostHeader)
    {
        this.tcp = tcp;
        this.ssl = ssl;
        this.hostHeader = hostHeader;
    }

    public bool IsOpen { get; private set; } = true;

    public static async Task<KeepAliveConnection> OpenAsync(string host, int port, string sni)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port);
            var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = sni, ApplicationProtocols = [SslApplicationProtocol.Http11] });
            return new KeepAliveConnection(tcp, ssl, port == 443 ? sni : $"{sni}:{port}");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task<(int Status, byte[] Body)> GetAsync(string path)
    {
        try
        {
            await ssl.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {hostHeader}\r\nUser-Agent: myriagent-hub-probe/1.0\r\nConnection: keep-alive\r\n\r\n"));
            await ssl.FlushAsync();
            var statusLine = await ReadLineAsync() ?? throw new IOException("連線在回應前被關閉");
            var parts = statusLine.Split(' ', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var status))
                throw new IOException("看不懂的狀態列：" + statusLine);
            long? length = null;
            var chunked = false;
            for (; ; )
            {
                var line = await ReadLineAsync() ?? throw new IOException("連線在標頭中途被關閉");
                if (line.Length == 0)
                    break;
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    length = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                    chunked = true;
                else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) && value.Equals("close", StringComparison.OrdinalIgnoreCase))
                    IsOpen = false;
            }
            using var body = new MemoryStream();
            if (chunked)
            {
                for (; ; )
                {
                    var size = Convert.ToInt32((await ReadLineAsync() ?? throw new IOException("chunk 中途被關閉")).Split(';')[0].Trim(), 16);
                    if (size == 0)
                    {
                        while (!string.IsNullOrEmpty(await ReadLineAsync()))
                        {
                        }
                        break;
                    }
                    await ReadExactAsync(body, size);
                    await ReadLineAsync();
                }
            }
            else if (length is { } l)
            {
                await ReadExactAsync(body, (int)l);
            }
            return (status, body.ToArray());
        }
        catch (IOException)
        {
            IsOpen = false;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsOpen = false;
        await ssl.DisposeAsync();
        tcp.Dispose();
    }

    private async Task<bool> FillAsync()
    {
        if (start > 0)
        {
            Array.Copy(buffer, start, buffer, 0, end - start);
            end -= start;
            start = 0;
        }
        var n = await ssl.ReadAsync(buffer.AsMemory(end));
        if (n == 0)
            return false;
        end += n;
        return true;
    }

    private async Task<string?> ReadLineAsync()
    {
        for (; ; )
        {
            for (var i = start; i < end - 1; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n')
                {
                    var line = Encoding.ASCII.GetString(buffer, start, i - start);
                    start = i + 2;
                    return line;
                }
            }
            if (!await FillAsync())
                return null;
        }
    }

    private async Task ReadExactAsync(Stream target, int count)
    {
        while (count > 0)
        {
            if (start == end && !await FillAsync())
                throw new IOException("回應本文中途被關閉");
            var take = Math.Min(count, end - start);
            target.Write(buffer, start, take);
            start += take;
            count -= take;
        }
    }
}

/// <summary>無限重試（預設 WithAutomaticReconnect() 只試 0／2／10／30 秒四次就放棄）；但 401／403（權杖被撤銷）就停，不要永遠重試</summary>
internal sealed class ForeverRetry : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.RetryReason is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            return null;
        return TimeSpan.FromMilliseconds(Math.Min(5000, 250 * Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 5))));
    }
}

/// <summary>透明 TCP 轉送，可一次切斷所有連線（模擬網路瞬斷）；delayMs＞0 時每個方向延遲（模擬 WAN）</summary>
internal sealed class TcpRelay(IPAddress ip, int listenPort, int targetPort, int delayMs = 0)
{
    private readonly TcpListener listener = new(ip, listenPort);
    private readonly ConcurrentDictionary<int, (TcpClient A, TcpClient B)> pairs = new();
    private int nextId;

    public void Start()
    {
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient inbound;
                try
                {
                    inbound = await listener.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                var outbound = new TcpClient();
                await outbound.ConnectAsync(IPAddress.Loopback, targetPort);
                var id = Interlocked.Increment(ref nextId);
                pairs[id] = (inbound, outbound);
                _ = PumpAsync(id, inbound.GetStream(), outbound.GetStream());
                _ = PumpAsync(id, outbound.GetStream(), inbound.GetStream());
            }
        });
    }

    public int DropAll()
    {
        var n = 0;
        foreach (var id in pairs.Keys.ToList())
        {
            if (pairs.TryRemove(id, out var p))
            {
                p.A.Client.LingerState = new LingerOption(true, 0);
                p.A.Close();
                p.B.Close();
                n++;
            }
        }
        return n;
    }

    public void Stop() => listener.Stop();

    private async Task PumpAsync(int id, NetworkStream from, NetworkStream to)
    {
        try
        {
            if (delayMs <= 0)
            {
                await from.CopyToAsync(to);
            }
            else
            {
                // 模擬 WAN：每個方向每段資料延遲 delayMs（往返約 2×delayMs），保持順序
                var buf = new byte[65536];
                var queue = System.Threading.Channels.Channel.CreateUnbounded<(long Due, byte[] Data)>();
                var writer = Task.Run(async () =>
                {
                    await foreach (var (due, data) in queue.Reader.ReadAllAsync())
                    {
                        var wait = due - Environment.TickCount64;
                        if (wait > 0)
                            await Task.Delay((int)wait);
                        await to.WriteAsync(data);
                    }
                });
                int n;
                while ((n = await from.ReadAsync(buf)) > 0)
                    await queue.Writer.WriteAsync((Environment.TickCount64 + delayMs, buf[..n]));
                queue.Writer.Complete();
                await writer;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
        }
        if (pairs.TryRemove(id, out var p))
        {
            p.A.Close();
            p.B.Close();
        }
    }
}
