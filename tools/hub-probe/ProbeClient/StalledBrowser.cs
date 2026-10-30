using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MyriagentHub.ProbeClient;

/// <summary>
/// 卡住的瀏覽器（WP4.9 計畫 D7）：原生 WebSocket 連 <c>/hubs/v1/browser</c>，**完成 SignalR JSON 握手**（<c>{"protocol":"json","version":1}</c> 加記錄分隔字元
/// 0x1E；伺服器只推給握手完成、看得到的連線，不握手的連線不會變成慢用戶端），送 ready、讀到清單裡的 load 對話之後送 ClientState、open 與 visible，
/// 然後**停止讀取**。期間照樣每 5 秒送 ping（不然伺服器 30 秒沒收到東西就以逾時關閉，量不到慢用戶端）。接收緩衝區刻意設小，伺服器的送出很快被擋住，
/// 預期被判成慢用戶端（送出佇列 60 秒內第 3 次溢出的 <c>slow_client</c>，或單則 15 秒送不出去的 <c>send_timeout</c>，web-protocol 第六節第 7 點）而被關掉
/// </summary>
internal sealed class StalledBrowser : IAsyncDisposable
{
    private const char RecordSeparator = '\u001e';

    private readonly ClientWebSocket socket;
    private readonly SocketsHttpHandler handler;
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly StringBuilder pending = new();
    private readonly HashSet<string> conversations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource pinging = new();
    private int invocation;
    private Task? pinger;

    private StalledBrowser(ClientWebSocket socket, SocketsHttpHandler handler)
    {
        this.socket = socket;
        this.handler = handler;
    }

    /// <summary>停止讀取的時間（UTC 毫秒；null＝還沒停）</summary>
    public long? StalledAtMs { get; private set; }

    /// <summary>ping 送不出去的時間（伺服器關掉連線之後）</summary>
    public long? PingFailedAtMs { get; private set; }

    public string? PingError { get; private set; }

    public int Conversations => conversations.Count;

    public static async Task<StalledBrowser> ConnectAsync(string baseUrl, string origin, string cookie, CancellationToken ct)
    {
        var uri = new UriBuilder(new Uri(new Uri(baseUrl), BrowserLoadClient.HubPath)) { Scheme = "wss" }.Uri;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            // 接收緩衝區刻意設小（明確設定也關掉 Windows 的自動調整）：停止讀取之後伺服器很快就送不出去
            ConnectCallback = async (context, token) =>
            {
                var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveBufferSize = 4096 };
                try
                {
                    await s.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", origin);
        ws.Options.SetRequestHeader("Cookie", cookie);
        ws.Options.SetBuffer(4096, 4096);
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            await ws.ConnectAsync(uri, invoker, ct);
        }
        catch
        {
            ws.Dispose();
            handler.Dispose();
            throw;
        }

        var browser = new StalledBrowser(ws, handler);
        await browser.SendRecordAsync("{\"protocol\":\"json\",\"version\":1}", ct);
        var handshake = await browser.ReadRecordAsync(ct) ?? throw new InvalidOperationException("SignalR 握手沒有回應");
        if (handshake.Contains("\"error\"", StringComparison.Ordinal))
            throw new InvalidOperationException("SignalR 握手被拒：" + handshake);
        await browser.InvokeAsync("Cmd", "{\"cmd\":{\"cmd\":\"ready\",\"mode\":\"main\"}}", ct);
        return browser;
    }

    /// <summary>
    /// 讀到清單裡 <paramref name="expected"/> 個 load 對話（或到 <paramref name="deadline"/> 時至少一個）就送 ClientState、open 與 visible，
    /// 然後停止讀取（之後只送 ping）。回傳停下來時認得的對話數
    /// </summary>
    public async Task<int> StallWhenReadyAsync(int expected, DateTimeOffset deadline, CancellationToken ct)
    {
        while (conversations.Count < Math.Max(1, expected))
        {
            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero && conversations.Count > 0)
                break;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            string? record;
            try
            {
                record = await ReadRecordAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (conversations.Count > 0)
                    break;
                throw new TimeoutException("卡住的瀏覽器在時限內沒有看到任何 load 的對話");
            }

            if (record is null)
                throw new InvalidOperationException("卡住的瀏覽器的連線在停止讀取之前就被關掉");
            Collect(record);
        }

        var ids = string.Join(",", conversations.Select(id => JsonSerializer.Serialize(id)));
        await InvokeAsync("ClientState", $"{{\"visible\":true,\"focused\":true,\"openConversationIds\":[{ids}],\"tzOffsetMinutes\":-480}}", ct);
        foreach (var id in conversations)
            await InvokeAsync("Cmd", $"{{\"cmd\":{{\"cmd\":\"open\",\"conversationId\":{JsonSerializer.Serialize(id)}}}}}", ct);
        await InvokeAsync("Cmd", $"{{\"cmd\":{{\"cmd\":\"visible\",\"conversationIds\":[{ids}]}}}}", ct);
        StalledAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        pinger = Task.Run(() => PingAsync(pinging.Token));
        return conversations.Count;
    }

    /// <summary>
    /// 停止讀取之後，伺服器有沒有把連線關掉：ping 已經送不出去就算；否則恢復讀取（先讀完緩衝裡的），讀到關閉或例外就算，
    /// <paramref name="timeout"/> 內一直有資料也沒關閉就是沒有被關
    /// </summary>
    public async Task<(bool Closed, string Detail, long BytesAfterResume)> CheckClosedAsync(TimeSpan timeout)
    {
        await pinging.CancelAsync();
        if (pinger is not null)
            await pinger;
        if (PingFailedAtMs is not null)
            return (true, "ping 送不出去：" + PingError, 0);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, deadline.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return (true, $"伺服器送了關閉訊框（{socket.CloseStatus} {socket.CloseStatusDescription}）", bytes);
                bytes += result.Count;
            }
        }
        catch (OperationCanceledException)
        {
            return (false, $"{timeout.TotalSeconds:0} 秒內連線還開著（讀到 {bytes} 位元組）", bytes);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or SocketException or ObjectDisposedException)
        {
            return (true, ex.GetType().Name + "：" + ex.Message, bytes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await pinging.CancelAsync();
        if (pinger is not null)
        {
            try
            {
                await pinger;
            }
            catch (OperationCanceledException)
            {
            }
        }

        socket.Abort();
        socket.Dispose();
        handler.Dispose();
        pinging.Dispose();
        sendGate.Dispose();
    }

    private void Collect(string record)
    {
        using var doc = JsonDocument.Parse(record);
        var root = doc.RootElement;
        // 1＝Invocation（伺服器的 Msg）、7＝Close
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number)
            return;
        if (type.GetInt32() == 7)
            throw new InvalidOperationException("卡住的瀏覽器在停止讀取之前收到 Close：" + record);
        if (type.GetInt32() != 1 || !root.TryGetProperty("target", out var target) || target.GetString() != "Msg"
            || !root.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() == 0)
            return;
        var message = args[0];
        var kind = message.TryGetProperty("type", out var k) ? k.GetString() : null;
        var list = kind switch
        {
            "init" when message.TryGetProperty("conversations", out var c) => c,
            "conversations" when message.TryGetProperty("items", out var i) => i,
            _ => default,
        };
        if (list.ValueKind != JsonValueKind.Array)
            return;
        foreach (var summary in list.EnumerateArray())
        {
            if (summary.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && summary.TryGetProperty("title", out var title) && FleetRules.RoleOfTitle(title.GetString()) is not null)
                conversations.Add(id.GetString()!);
        }
    }

    private async Task PingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await SendRecordAsync("{\"type\":6}", timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                PingFailedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                PingError = ex.GetType().Name + "：" + ex.Message;
                return;
            }
        }
    }

    private Task InvokeAsync(string target, string argumentJson, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref invocation).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return SendRecordAsync($"{{\"type\":1,\"invocationId\":\"{id}\",\"target\":\"{target}\",\"arguments\":[{argumentJson}]}}", ct);
    }

    private async Task SendRecordAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + RecordSeparator);
        await sendGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            sendGate.Release();
        }
    }

    /// <summary>讀到下一筆以 0x1E 結尾的記錄（不含分隔字元）；連線關閉回 null</summary>
    private async Task<string?> ReadRecordAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        while (true)
        {
            var text = pending.ToString();
            var end = text.IndexOf(RecordSeparator, StringComparison.Ordinal);
            if (end >= 0)
            {
                pending.Remove(0, end + 1);
                return text[..end];
            }

            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            var count = decoder.GetChars(buffer, 0, result.Count, chars, 0, flush: false);
            pending.Append(chars, 0, count);
        }
    }
}
