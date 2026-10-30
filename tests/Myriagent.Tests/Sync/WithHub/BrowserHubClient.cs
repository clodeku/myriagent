using System.Net;
using System.Threading.Channels;
using Myriagent.Sync;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// 擁有者瀏覽器的 SignalR 用戶端（MyriagentHub 子程序的瀏覽器 hub；同 MyriagentHub.Tests 的 BrowserHubClient.ConnectKestrelAsync，不參考 MyriagentHub.Tests）：
/// WebSocket 升級帶登入的 cookie 與 Origin（伺服器的 PublicOrigin），伺服器憑證不驗（自簽）。伺服器的 <c>Msg</c> 依序放進 <see cref="messages"/>
/// </summary>
internal sealed class BrowserHubClient : IAsyncDisposable
{
    /// <summary>瀏覽器 hub 的方法名（MyriagentHub 的 BrowserHubMethods）</summary>
    private const string CmdMethod = "Cmd";

    private const string MsgMethod = "Msg";

    private readonly Channel<JObject> messages = Channel.CreateUnbounded<JObject>();
    private readonly HubConnection connection;

    private BrowserHubClient(HubConnection connection)
    {
        this.connection = connection;
        connection.On(MsgMethod, [typeof(JObject)], args =>
        {
            if (args[0] is JObject message)
                messages.Writer.TryWrite(message);
            return Task.CompletedTask;
        });
    }

    public static async Task<BrowserHubClient> ConnectAsync(Uri baseAddress, CookieContainer cookies, string origin)
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
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return client;
    }

    /// <summary>送 Cmd <c>{commandId?, cmd}</c>，回傳結果物件（<c>{accepted, commandId?, error?}</c>）</summary>
    public Task<JObject> CmdAsync(JObject cmd, string? commandId = null)
    {
        var request = new JObject();
        if (commandId is not null)
            request["commandId"] = commandId;
        request["cmd"] = cmd;
        return connection.InvokeAsync<JObject>(CmdMethod, request);
    }

    /// <summary>送 ready 並等 init</summary>
    public async Task<JObject> ReadyAsync(TimeSpan timeout)
    {
        var result = await CmdAsync(new JObject { ["cmd"] = "ready", ["mode"] = "main" });
        result.Value<bool?>("accepted").Should().BeTrue("ready 被收下：" + result.ToString(Newtonsoft.Json.Formatting.None));
        return await WaitForAsync(m => m.Value<string>("type") == "init", timeout, "init");
    }

    /// <summary>等第一則符合條件的訊息（其他的略過）</summary>
    public async Task<JObject> WaitForAsync(Func<JObject, bool> predicate, TimeSpan timeout, string what)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var message = await messages.Reader.ReadAsync(cts.Token);
                if (predicate(message))
                    return message;
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{timeout.TotalSeconds:0} 秒內沒有收到 {what}");
        }
    }

    /// <summary>以新的 commandId 送一個轉給電腦的指令，等同一個 commandId 的 <c>commandResult</c></summary>
    public async Task<JObject> RemoteAsync(JObject cmd, TimeSpan timeout)
    {
        var commandId = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var accepted = await CmdAsync(cmd, commandId);
        accepted.Value<bool?>("accepted").Should().BeTrue("伺服器收下 " + cmd.Value<string>("cmd") + "：" + accepted.ToString(Newtonsoft.Json.Formatting.None));
        return await WaitForAsync(m => m.Value<string>("type") == "commandResult" && m.Value<string>("commandId") == commandId, timeout, $"{commandId} 的 commandResult");
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}
