using System.Threading.Channels;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 瀏覽器 hub 的 SignalR 用戶端（TestServer）：以擁有者用戶端的 cookie 連線，伺服器的 Msg 依序放進 <see cref="Messages"/>。
/// 線上格式與瀏覽器的 JSON 協定相同（伺服器以 Newtonsoft 處理）
/// </summary>
internal sealed class BrowserHubClient : IAsyncDisposable
{
    private readonly TaskCompletionSource<Exception?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BrowserHubClient(HubConnection connection)
    {
        Connection = connection;
        connection.On(BrowserHubMethods.Msg, [typeof(JObject)], args =>
        {
            if (args[0] is JObject message)
                Messages.Writer.TryWrite(message);
            return Task.CompletedTask;
        });
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    public Channel<JObject> Messages { get; } = Channel.CreateUnbounded<JObject>();

    /// <summary>連線被關掉（伺服器中斷或關閉訊息）時完成；值是關閉的錯誤</summary>
    public Task<Exception?> Closed => closed.Task;

    /// <summary>伺服器登記的這條連線（連上時找到的；被拒的連線是 null）</summary>
    public BrowserConnection? ServerConnection { get; private set; }

    /// <summary>
    /// 經 TestServer 連線：WebSocket 升級帶 <paramref name="owner"/> 的 cookie、<paramref name="origin"/>（null＝不帶）、它的 X-Test-Ip；
    /// <paramref name="bearer"/> 不是 null 時另帶 Authorization 標頭（裝置權杖）。不協商（只有 WebSockets）。
    /// SignalR 先回交握、才跑 OnConnectedAsync（登記連線、登記之後查工作階段）：回傳前以一次呼叫當屏障，等到 OnConnectedAsync 跑完
    /// （登記了、或已經關閉這條連線；測試一次只連一條）。5 秒內沒跑完就丟 TimeoutException
    /// </summary>
    public static async Task<BrowserHubClient> ConnectAsync(HubTestHost host, HubClient? owner, string? origin = HubTestHost.Origin, string? bearer = null)
    {
        var server = host.App.GetTestServer();
        var registry = host.Services.GetRequiredService<BrowserConnections>();
        var before = registry.Snapshot().Select(c => c.ConnectionId).ToHashSet(StringComparer.Ordinal);
        var cookie = owner?.Cookies?.Container.GetCookieHeader(new Uri(HubTestHost.Origin));
        var ip = owner?.Http.DefaultRequestHeaders.TryGetValues(TestIpStartupFilter.Header, out var values) == true ? values.First() : TestIpStartupFilter.DefaultIp;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(HubTestHost.Origin + SyncProtocol.BrowserHubPath), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var ws = server.CreateWebSocketClient();
                    ws.ConfigureRequest = request =>
                    {
                        if (!string.IsNullOrEmpty(cookie))
                            request.Headers.Cookie = cookie;
                        if (origin is not null)
                            request.Headers.Origin = origin;
                        if (bearer is not null)
                            request.Headers.Authorization = "Bearer " + bearer;
                        request.Headers[TestIpStartupFilter.Header] = ip;
                    };
                    return await ws.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddNewtonsoftJsonProtocol(o => o.PayloadSerializerSettings = SyncJson.Settings)
            .Build();
        var client = new BrowserHubClient(connection);
        try
        {
            await connection.StartAsync();
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        // 屏障：SignalR 在 OnConnectedAsync（登記＋登記之後查工作階段）跑完之前不派送任何呼叫，所以這次呼叫回來時登記時的檢查一定做完了。
        // 只看「已登記」就回傳的話，登記時那次查詢可能還沒讀時鐘：測試接著撥快時鐘，它就判成閒置到期而中斷（偶發）。
        // 呼叫不存在的方法：協定繫結就失敗、回 HubException（不經 BrowserHubFilter：不記活動、不扣權杖，連線保持）
        try
        {
            await connection.InvokeAsync<object?>(AdmittedBarrier).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (HubException)
        {
            // 預期：OnConnectedAsync 已跑完、連線保持
        }
        catch (TimeoutException)
        {
            await connection.DisposeAsync();
            throw new TimeoutException("5 秒內伺服器沒有跑完瀏覽器 hub 的 OnConnectedAsync");
        }
        catch (Exception)
        {
            // 登記時被拒（too_many_connections、session_invalid、停機）：連線已被關掉，ServerConnection 是 null
        }
        client.ServerConnection = registry.Snapshot().FirstOrDefault(c => !before.Contains(c.ConnectionId));
        return client;
    }

    /// <summary>
    /// 經真的 Kestrel（<paramref name="baseAddress"/>＝https://127.0.0.1:埠，例如 <see cref="DelayProxy"/>；WP4.2 計畫步 14 的延遲量測）：
    /// WebSocket 升級帶 <paramref name="cookies"/> 裡這個位址的 cookie 與 <paramref name="origin"/>，伺服器憑證不驗（自簽）。屏障同 <see cref="ConnectAsync"/>；
    /// 不查伺服器的登記（<see cref="ServerConnection"/> 是 null）
    /// </summary>
    public static async Task<BrowserHubClient> ConnectKestrelAsync(Uri baseAddress, System.Net.CookieContainer cookies, string origin = HubTestHost.Origin)
    {
        var cookie = cookies.GetCookieHeader(baseAddress);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, SyncProtocol.BrowserHubPath), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.Headers["Origin"] = origin;
                if (!string.IsNullOrEmpty(cookie))
                    o.Headers["Cookie"] = cookie;
                o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            })
            .AddNewtonsoftJsonProtocol(o => o.PayloadSerializerSettings = SyncJson.Settings)
            .Build();
        var client = new BrowserHubClient(connection);
        try
        {
            await connection.StartAsync();
            await connection.InvokeAsync<object?>(AdmittedBarrier).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (HubException)
        {
            // 預期：OnConnectedAsync 已跑完、連線保持
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        return client;
    }

    /// <summary>不存在的 hub 方法（<see cref="ConnectAsync"/> 的屏障）</summary>
    private const string AdmittedBarrier = "__admitted";

    /// <summary>送 Cmd：<c>{commandId?, envelope?, cmd}</c>，回傳結果物件（null 欄位省略）</summary>
    public Task<JObject> CmdAsync(object cmd, string? commandId = null, object? envelope = null)
    {
        var request = new JObject();
        if (commandId is not null)
            request["commandId"] = commandId;
        if (envelope is not null)
            request["envelope"] = JToken.FromObject(envelope);
        request["cmd"] = cmd as JToken ?? JToken.FromObject(cmd);
        return Connection.InvokeAsync<JObject>(BrowserHubMethods.Cmd, request);
    }

    /// <summary>送 ready 並等 init</summary>
    public async Task<JObject> ReadyAsync(string mode = "main")
    {
        var result = await CmdAsync(new { cmd = "ready", mode });
        if (result["accepted"]?.Value<bool>() != true)
            throw new InvalidOperationException("ready 被拒：" + result.ToString(Newtonsoft.Json.Formatting.None));
        return await WaitForAsync("init");
    }

    /// <summary>等第一則符合條件的訊息（其他的略過）</summary>
    public async Task<JObject> WaitForAsync(string type, Func<JObject, bool>? predicate = null, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                var message = await Messages.Reader.ReadAsync(cts.Token);
                if ((string?)message["type"] == type && (predicate is null || predicate(message)))
                    return message;
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{timeoutMs} ms 內沒有收到 {type}");
        }
    }

    /// <summary>這段時間內沒有收到這一種訊息（其他種類的照樣取走）</summary>
    public async Task AssertNoMessageAsync(string type, int milliseconds)
    {
        using var cts = new CancellationTokenSource(milliseconds);
        try
        {
            while (true)
            {
                var message = await Messages.Reader.ReadAsync(cts.Token);
                if ((string?)message["type"] == type)
                    throw new InvalidOperationException($"不該收到 {type}：{message.ToString(Newtonsoft.Json.Formatting.None)}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>目前已收到、還沒取走的訊息（不等待）</summary>
    public List<JObject> Drain()
    {
        var list = new List<JObject>();
        while (Messages.Reader.TryRead(out var message))
            list.Add(message);
        return list;
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
