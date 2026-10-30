using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FakeDevice;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace MyriagentHub.ProbeClient;

/// <summary>一個時間標記的延遲：瀏覽器收到的時間（UTC 毫秒）與「收到 − 標記」（毫秒）</summary>
internal readonly record struct StampSample(long ReceivedAtMs, double LatencyMs);

/// <summary>擁有者以帳號密碼登入（同網頁的 auth.js）：回傳之後 WebSocket 升級要帶的 Cookie 標頭。密碼只從環境變數讀進來，不寫進任何輸出</summary>
internal static class OwnerLogin
{
    public static async Task<string> CookieAsync(string baseUrl, string origin, string userName, string password, CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var handler = new SocketsHttpHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "/api/v1/auth/login"))
        {
            Content = JsonContent.Create(new { userName, password }),
        };
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Myriagent-Csrf", "1");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"擁有者登入回 {(int)response.StatusCode}：{(text.Length <= 200 ? text : text[..200] + "…")}");
        }

        var header = cookies.GetCookieHeader(new Uri(baseUrl));
        return header.Length > 0 ? header : throw new InvalidOperationException("擁有者登入沒有拿到 cookie");
    }
}

/// <summary>
/// 一個擁有者瀏覽器的負載用戶端（WP4.9 計畫 D7、D8；.NET SignalR 用戶端，連 <c>/hubs/v1/browser</c>，帶登入的 cookie 與 Origin＝PublicOrigin）：
/// 送 ready、ClientState（visible、focused）；清單裡出現 load 的對話（標題 <c>load-stream-</c>／<c>load-persist-</c>）就 <c>open</c>，
/// 同時以 <c>visible</c> 指令與 ClientState 的 openConversationIds 讓它「看得到」（擁有者電腦只對看得到的對話送即時更新）。
/// 從 <c>item</c> 的文字解析時間標記：串流對話裡執行中的項目第一次帶著標記出現算一筆即時延遲；持久對話裡完成的項目第一次帶著標記出現算一筆持久延遲。
/// <c>conversation</c> 快照裡已有的標記只記下、不算（那是 open 之前的）。Msg 的處理常式不等任何呼叫（在 SignalR 的接收迴圈上），
/// open 等指令由另一個工作送。轉送階段（WP4.9 步 12）的媒體短對話（<c>load-relay-</c>）也開，記下項目裡第一個 <c>/m/</c> 網址（<see cref="RelayMediaUrls"/>）
/// </summary>
internal sealed class BrowserLoadClient : IAsyncDisposable
{
    public const string HubPath = "/hubs/v1/browser";

    /// <summary>臺灣的 getTimezoneOffset()</summary>
    private const int TzOffsetMinutes = -480;

    private readonly object gate = new();
    private readonly HubConnection connection;
    private readonly Channel<JsonElement> lists = Channel.CreateUnbounded<JsonElement>();
    private readonly Dictionary<string, FleetRole> roles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<long>> liveSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<long>> doneSeen = new(StringComparer.Ordinal);
    private readonly List<StampSample> live = [];
    private readonly List<StampSample> persistent = [];
    private readonly HashSet<string> opened = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> titles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> mediaUrls = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource init = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? loop;
    private Task? control;
    private bool stopping;
    private int inits;
    private int messages;
    private int items;
    private int snapshots;
    private int commandErrors;

    private BrowserLoadClient(int index, HubConnection connection)
    {
        Index = index;
        this.connection = connection;
        connection.On<JsonElement>("Msg", OnMessage);
        connection.Closed += error =>
        {
            lock (gate)
            {
                if (!stopping)
                {
                    ClosedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    CloseError = error?.Message ?? "closed";
                }
            }

            return Task.CompletedTask;
        };
    }

    public int Index { get; }

    /// <summary>不是我們關的、連線被關掉的時間（null＝沒有被關過）</summary>
    public long? ClosedAtMs { get; private set; }

    public string? CloseError { get; private set; }

    public static async Task<BrowserLoadClient> ConnectAsync(int index, string baseUrl, string origin, string cookie, CancellationToken ct)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(baseUrl), HubPath), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.Headers["Origin"] = origin;
                o.Headers["Cookie"] = cookie;
                o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            })
            .Build();
        var client = new BrowserLoadClient(index, hub);
        try
        {
            await hub.StartAsync(ct);
        }
        catch
        {
            await hub.DisposeAsync();
            throw;
        }

        return client;
    }

    /// <summary>送 ready 等 init，送 ClientState，接著在背景依清單開 load 的對話</summary>
    public async Task StartAsync(TimeSpan timeout, CancellationToken ct)
    {
        loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ready = await CmdAsync(new JsonObject { ["cmd"] = "ready", ["mode"] = "main" }, ct);
        if (!Accepted(ready))
            throw new InvalidOperationException($"瀏覽器 {Index} 的 ready 沒有被收下：{ready.GetRawText()}");
        await init.Task.WaitAsync(timeout, ct);
        control = Task.Run(() => ControlAsync(loop.Token));
    }

    /// <summary>即時與持久的延遲（只取收到時間在 [from, to] 的）</summary>
    public (List<StampSample> Live, List<StampSample> Persistent) Samples(long fromMs, long toMs)
    {
        lock (gate)
            return (live.Where(s => s.ReceivedAtMs >= fromMs && s.ReceivedAtMs <= toMs).ToList(), persistent.Where(s => s.ReceivedAtMs >= fromMs && s.ReceivedAtMs <= toMs).ToList());
    }

    /// <summary>
    /// 媒體短對話（<see cref="FleetRules.RelayTitlePrefix"/>）裡第一個 <c>/m/</c> 網址，依裝置名稱（WP4.9 步 12：轉送階段對哪個網址發請求）。
    /// 網址從 open 的快照或之後的 <c>item</c> 的 <c>media[].url</c> 取（HubItemRules.Finish 組的）
    /// </summary>
    public IReadOnlyDictionary<string, string> RelayMediaUrls()
    {
        lock (gate)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, url) in mediaUrls)
            {
                if (titles.TryGetValue(id, out var title) && FleetRules.DeviceOfRelayTitle(title) is { } device)
                    result.TryAdd(device, url);
            }

            return result;
        }
    }

    public object Describe()
    {
        lock (gate)
        {
            return new
            {
                index = Index,
                inits,
                resyncs = Math.Max(0, inits - 1),
                messages,
                items,
                snapshots,
                opened = opened.Count,
                liveSamples = live.Count,
                persistentSamples = persistent.Count,
                commandErrors,
                closedAtMs = ClosedAtMs,
                closeError = CloseError,
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
            stopping = true;
        loop?.Cancel();
        if (control is not null)
        {
            try
            {
                await control;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await connection.DisposeAsync();
        loop?.Dispose();
    }

    private void OnMessage(JsonElement message)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var typeProperty))
            return;
        var type = typeProperty.GetString();
        lock (gate)
        {
            messages++;
            switch (type)
            {
                case "init":
                    inits++;
                    // 伺服器入列 init 時忘掉這條連線開著的對話：清單裡的 load 對話要重新 open
                    opened.Clear();
                    if (message.TryGetProperty("conversations", out var list))
                        lists.Writer.TryWrite(list.Clone());
                    init.TrySetResult();
                    break;
                case "conversations":
                    if (message.TryGetProperty("items", out var summaries))
                        lists.Writer.TryWrite(summaries.Clone());
                    break;
                case "conversation":
                    snapshots++;
                    if (Id(message) is { } snapshotId && message.TryGetProperty("items", out var snapshotItems) && snapshotItems.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in snapshotItems.EnumerateArray())
                            See(snapshotId, item, now, sample: false);
                    }

                    break;
                case "item":
                    items++;
                    if (Id(message) is { } itemId && message.TryGetProperty("item", out var one))
                        See(itemId, one, now, sample: true);
                    break;
            }
        }
    }

    /// <summary>
    /// 一個項目裡的時間標記（在 gate 裡呼叫）。串流：執行中的項目第一次出現的標記算即時；持久：完成的項目第一次出現的標記算持久（D8）。
    /// <paramref name="sample"/> 為 false（open 的快照）只記下
    /// </summary>
    private void See(string conversationId, JsonElement item, long now, bool sample)
    {
        if (!roles.TryGetValue(conversationId, out var role) || item.ValueKind != JsonValueKind.Object)
            return;
        if (!mediaUrls.ContainsKey(conversationId) && MediaUrl(item) is { } url)
            mediaUrls[conversationId] = url;
        var status = item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var html = item.TryGetProperty("html", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        var stamps = StampRules.Parse(html);
        if (stamps.Count == 0)
            return;
        switch (role)
        {
            case FleetRole.Stream:
            {
                var seen = Set(liveSeen, conversationId);
                foreach (var stamp in stamps)
                {
                    if (seen.Add(stamp) && sample && status == "Running")
                        live.Add(new StampSample(now, now - stamp));
                }

                break;
            }
            case FleetRole.Persistent:
            {
                if (status != "Done")
                    return;
                var seen = Set(doneSeen, conversationId);
                foreach (var stamp in stamps)
                {
                    if (seen.Add(stamp) && sample)
                        persistent.Add(new StampSample(now, now - stamp));
                }

                break;
            }
            case FleetRole.Idle:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(role), role, "未知的角色");
        }
    }

    /// <summary>依清單開 load 的對話，接著更新 visible 與 ClientState（看得到）</summary>
    private async Task ControlAsync(CancellationToken ct)
    {
        await ClientStateAsync([], ct);
        await foreach (var list in lists.Reader.ReadAllAsync(ct))
        {
            var toOpen = new List<string>();
            lock (gate)
            {
                if (list.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var summary in list.EnumerateArray())
                {
                    if (summary.ValueKind != JsonValueKind.Object || Id(summary, "id") is not { } id)
                        continue;
                    var title = summary.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (FleetRules.RoleOfTitle(title) is not { } role)
                        continue;
                    roles[id] = role;
                    titles[id] = title!;
                    if (opened.Add(id))
                        toOpen.Add(id);
                }
            }

            if (toOpen.Count == 0)
                continue;
            foreach (var id in toOpen)
            {
                var result = await CmdAsync(new JsonObject { ["cmd"] = "open", ["conversationId"] = id }, ct);
                if (!Accepted(result))
                {
                    lock (gate)
                        commandErrors++;
                }
            }

            List<string> all;
            lock (gate)
                all = opened.ToList();
            await CmdAsync(new JsonObject { ["cmd"] = "visible", ["conversationIds"] = new JsonArray(all.Select(i => (JsonNode)i).ToArray()) }, ct);
            await ClientStateAsync(all, ct);
        }
    }

    private Task ClientStateAsync(IReadOnlyList<string> open, CancellationToken ct) =>
        connection.InvokeAsync("ClientState", new JsonObject
        {
            ["visible"] = true,
            ["focused"] = true,
            ["openConversationIds"] = new JsonArray(open.Select(i => (JsonNode)i).ToArray()),
            ["tzOffsetMinutes"] = TzOffsetMinutes,
        }, ct);

    private Task<JsonElement> CmdAsync(JsonObject cmd, CancellationToken ct) => connection.InvokeAsync<JsonElement>("Cmd", new JsonObject { ["cmd"] = cmd }, ct);

    private static bool Accepted(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("accepted", out var a) && a.ValueKind == JsonValueKind.True;

    /// <summary>項目的 <c>media</c> 裡第一個 <c>/m/</c> 網址；沒有回 null</summary>
    private static string? MediaUrl(JsonElement item)
    {
        if (!item.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in media.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                && url.GetString() is { } text && text.StartsWith("/m/", StringComparison.Ordinal))
                return text;
        }

        return null;
    }

    private static string? Id(JsonElement element, string name = "conversationId") =>
        element.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    private static HashSet<long> Set(Dictionary<string, HashSet<long>> map, string key)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = [];
        return set;
    }
}
