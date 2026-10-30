using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;

namespace Myriagent.Tests.Sync;

/// <summary>
/// 建立連線的上限（SyncTimingRules.ConnectTimeout）：伺服器部署換版那幾秒建立的連線 TCP 連得上、之後一直沒有回應（2026-10-22 實測卡 330 秒）。
/// 這裡以本機只接受連線、從不讀寫的 TCP 伺服器重現
/// </summary>
public sealed class SignalRSyncTransportTests
{
    /// <summary>上限沒作用時測試不會永遠卡住：到這個時間就以取消結束（斷言會失敗）</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task 伺服器接受連線卻一直不回應_到了上限丟連線逾時()
    {
        using var server = new SilentServer();
        using var guard = new CancellationTokenSource(Guard);
        var transport = new SignalRSyncTransport(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        var thrown = await Record.ExceptionAsync(() => transport.ConnectAsync(Request(server.Uri), new RecordingSessionEvents(), guard.Token));

        thrown.Should().BeOfType<SyncTransportException>().Which.Failure.Code.Should().Be(SyncFailureRules.ConnectTimeoutCode);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "上限是 0.5 秒，不是等到系統斷線");
        server.Accepted.Should().BeGreaterThan(0, "TCP 連上了，卡在之後的 TLS");
    }

    [Fact]
    public async Task 呼叫端取消照原樣丟出_不算連線逾時()
    {
        using var server = new SilentServer();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var transport = new SignalRSyncTransport(TimeSpan.FromSeconds(20));

        var thrown = await Record.ExceptionAsync(() => transport.ConnectAsync(Request(server.Uri), new RecordingSessionEvents(), cancel.Token));

        thrown.Should().BeAssignableTo<OperationCanceledException>("引擎停止時的取消不是連線失敗");
    }

    [Fact]
    public async Task 連不上的埠_照原本的分類不算逾時()
    {
        var port = FreePort();
        using var guard = new CancellationTokenSource(Guard);
        var transport = new SignalRSyncTransport(TimeSpan.FromSeconds(20));

        var thrown = await Record.ExceptionAsync(() => transport.ConnectAsync(Request(new Uri($"https://127.0.0.1:{port}")), new RecordingSessionEvents(), guard.Token));

        var failure = thrown.Should().BeOfType<SyncTransportException>().Which.Failure;
        failure.Code.Should().BeNull();
        failure.Detail.Should().Be("伺服器連不上");
    }

    private static SyncConnectRequest Request(Uri server) => new(server, server, "chd_test", DevTrustEnv: true);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>只接受 TCP 連線、從不讀寫也不關閉（用戶端的 TLS ClientHello 一直等不到回應）</summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> sockets = [];
        private readonly CancellationTokenSource stop = new();

        public SilentServer()
        {
            listener.Start();
            Uri = new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            _ = AcceptLoopAsync();
        }

        public Uri Uri { get; }

        public int Accepted
        {
            get
            {
                lock (sockets)
                    return sockets.Count;
            }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var socket = await listener.AcceptSocketAsync(stop.Token);
                    lock (sockets)
                        sockets.Add(socket);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // 測試結束
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            lock (sockets)
            {
                foreach (var socket in sockets)
                    socket.Dispose();
            }
            stop.Dispose();
        }
    }
}
