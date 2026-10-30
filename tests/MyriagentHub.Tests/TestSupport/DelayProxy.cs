using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 加延遲的 TCP 轉送（WP4.2 計畫步 14 的延遲量測，S7）：聽 127.0.0.1 的隨機埠，每條進來的連線各自連到 <c>target</c>，
/// 兩個方向各自把讀到的每一段資料延後 <c>delay</c> 再寫出去（照到達順序；不改內容，TLS 照樣端到端）。模擬手機到 Hub 主機的網路往返
/// </summary>
internal sealed class DelayProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint target;
    private readonly TimeSpan delay;
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> pumps = [];
    private readonly Task accepting;
    private long upBytes;
    private long downBytes;

    private DelayProxy(IPEndPoint target, TimeSpan delay)
    {
        this.target = target;
        this.delay = delay;
        listener.Start();
        accepting = AcceptAsync();
    }

    /// <summary>https://127.0.0.1:轉送的埠（用戶端連這裡）</summary>
    public Uri BaseAddress => new($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

    /// <param name="server">要轉送到的位址（https://127.0.0.1:埠）</param>
    /// <param name="delay">每個方向的延遲</param>
    public static DelayProxy Start(Uri server, TimeSpan delay) => new(new IPEndPoint(IPAddress.Loopback, server.Port), delay);

    /// <summary>用戶端送往伺服器的位元組（含 TLS，所有連線合計；流量量測用）</summary>
    public long UpBytes => Interlocked.Read(ref upBytes);

    /// <summary>伺服器送往用戶端的位元組（含 TLS，所有連線合計）</summary>
    public long DownBytes => Interlocked.Read(ref downBytes);

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        try
        {
            await accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        Task[] running;
        lock (pumps)
            running = [.. pumps];
        await Task.WhenAll(running.Select(p => p.ContinueWith(_ => { }, TaskScheduler.Default)));
        stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stop.Token);
            var pump = Task.Run(() => RelayAsync(client));
            lock (pumps)
                pumps.Add(pump);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            client.NoDelay = true;
            upstream.NoDelay = true;
            try
            {
                await upstream.ConnectAsync(target, stop.Token);
                using var both = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var a = DirectionAsync(client.GetStream(), upstream.GetStream(), up: true, both.Token);
                var b = DirectionAsync(upstream.GetStream(), client.GetStream(), up: false, both.Token);
                await Task.WhenAny(a, b);
                // 一邊關了就整條關掉
                await both.CancelAsync();
                await Task.WhenAll(a.ContinueWith(_ => { }, TaskScheduler.Default), b.ContinueWith(_ => { }, TaskScheduler.Default));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>一個方向：讀到一段就記下「該送出的時間」排進佇列，另一個迴圈到時間才寫（不阻塞讀取，延遲不會累加）</summary>
    private async Task DirectionAsync(NetworkStream from, NetworkStream to, bool up, CancellationToken cancellationToken)
    {
        var queue = Channel.CreateUnbounded<(long Due, byte[] Data)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var writer = Task.Run(async () =>
        {
            await foreach (var (due, data) in queue.Reader.ReadAllAsync(cancellationToken))
            {
                var remaining = due - Stopwatch.GetTimestamp();
                if (remaining > 0)
                    await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), cancellationToken);
                await to.WriteAsync(data, cancellationToken);
            }
        }, cancellationToken);
        var buffer = new byte[64 * 1024];
        var delayTicks = (long)(delay.TotalSeconds * Stopwatch.Frequency);
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer, cancellationToken);
                if (n == 0)
                    break;
                if (up)
                    Interlocked.Add(ref upBytes, n);
                else
                    Interlocked.Add(ref downBytes, n);
                queue.Writer.TryWrite((Stopwatch.GetTimestamp() + delayTicks, buffer.AsSpan(0, n).ToArray()));
            }
        }
        finally
        {
            queue.Writer.TryComplete();
        }
        await writer;
        try
        {
            to.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }
}
