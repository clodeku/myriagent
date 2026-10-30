using System.Diagnostics;
using System.Text.Json;

namespace MyriagentHub.ProbeClient;

/// <summary>對第 2 階段的 MyriagentHub（沒有裝置 hub、沒有測試端點）就能跑的模式</summary>
internal static class Phase2Modes
{
    /// <summary>
    /// tls：一般連線（h2、http/1.1）與只提 acme-tls/1 的連線各拿到什麼憑證、協商出什麼 ALPN。
    /// 沒有進行中的 TLS-ALPN-01 驗證時，只提 acme-tls/1 的連線依 RFC 7301 應被拒（no_application_protocol）。
    /// </summary>
    public static async Task<int> TlsAsync(ProbeContext ctx)
    {
        foreach (var alpn in new[] { new[] { "h2", "http/1.1" }, new[] { "acme-tls/1" } })
        {
            var r = await ProbeTls.HandshakeAsync(ctx.ConnectHost, ctx.Port, ctx.Sni, alpn);
            ctx.Emit(new { @event = "tls", sni = ctx.Sni, offered = alpn, r.Ok, r.Negotiated, r.Thumbprint, r.Subject, r.Issuer, r.DnsNames, r.Error, r.AcmeExtension, r.ElapsedMs });
        }
        return 0;
    }

    /// <summary>
    /// conc（第 2 階段版）：同時開 N 條 TLS 連線（每條送一個靜態檔請求後保持 keep-alive），全部開著時再從其中 M 條同時送 GET /healthz；
    /// 讀 /readyz（本機迴路）看伺服器記到的連線數與私有記憶體。設計 11.3 的「conc（200 條連線）」；hub 連線的部分見 conc-hub（第 3 階段）。
    /// 靜態檔不經過速率限制；/healthz 屬匿名請求（每 IP 每分鐘 120 次），所以 M 預設 30。
    /// </summary>
    public static async Task<int> ConcurrencyAsync(ProbeContext ctx)
    {
        var n = ctx.IntOpt("n", 200);
        var m = Math.Min(ctx.IntOpt("m", 30), n);
        var staticPath = ctx.Opt("path", "/auth.css");
        var hold = ctx.IntOpt("hold", 2);
        var before = await ctx.ReadyzAsync();

        var sw = Stopwatch.StartNew();
        var opened = await Task.WhenAll(Enumerable.Range(0, n).Select(async _ =>
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var connection = await KeepAliveConnection.OpenAsync(ctx.ConnectHost, ctx.Port, ctx.Sni);
                var handshakeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var (status, _) = await connection.GetAsync(staticPath);
                return (Connection: connection, HandshakeMs: handshakeMs, Status: status, Error: (string?)null);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
            {
                return (Connection: (KeepAliveConnection?)null, HandshakeMs: Stopwatch.GetElapsedTime(start).TotalMilliseconds, Status: 0, Error: ex.GetType().Name + ": " + ex.Message);
            }
        }));
        var connectAllMs = sw.ElapsedMilliseconds;
        var live = opened.Where(o => o.Connection is not null).Select(o => o.Connection!).ToList();
        await Task.Delay(TimeSpan.FromSeconds(hold));
        var during = await ctx.ReadyzAsync();

        // 全部開著時，從其中 M 條同時送請求：全部很快完成＝沒有排隊
        sw.Restart();
        var requests = await Task.WhenAll(live.Take(m).Select(async c =>
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var (status, _) = await c.GetAsync("/healthz");
                return (Status: status, Ms: Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            catch (IOException)
            {
                return (Status: 0, Ms: Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }));
        var requestsAllMs = sw.ElapsedMilliseconds;
        var stillOpen = 0;
        foreach (var c in live)
        {
            if (c.IsOpen)
                stillOpen++;
            await c.DisposeAsync();
        }
        await Task.Delay(1500);
        var after = await ctx.ReadyzAsync();
        using var me = Process.GetCurrentProcess();
        var handshakes = opened.Select(o => o.HandshakeMs).ToList();
        ctx.Emit(new
        {
            @event = "conc",
            connections = n,
            opened = live.Count,
            failed = opened.Count(o => o.Connection is null),
            failures = opened.Where(o => o.Error is not null).Select(o => o.Error).Distinct().Take(5),
            connectAllMs,
            handshakeP50Ms = Stats.P(handshakes, 50),
            handshakeP99Ms = Stats.P(handshakes, 99),
            handshakeMaxMs = Math.Round(handshakes.DefaultIfEmpty(0).Max(), 1),
            staticPath,
            staticStatus = opened.GroupBy(o => o.Status).ToDictionary(g => g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g => g.Count()),
            concurrentRequests = m,
            requestsAllCompletedMs = requestsAllMs,
            requestP50Ms = Stats.P(requests.Select(r => r.Ms).ToList(), 50),
            requestP99Ms = Stats.P(requests.Select(r => r.Ms).ToList(), 99),
            requestStatus = requests.GroupBy(r => r.Status).ToDictionary(g => g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g => g.Count()),
            stillOpenAfterRequests = stillOpen,
            server = new
            {
                activeBefore = Active(before),
                activeDuring = Active(during),
                activeAfter = Active(after),
                privateMbBefore = PrivateMb(before),
                privateMbDuring = PrivateMb(during),
                privateMbAfter = PrivateMb(after),
                threadsDuring = Threads(during),
            },
            clientPrivateMb = Math.Round(me.PrivateMemorySize64 / 1048576.0, 1),
        });
        return 0;
    }

    private static long? Active(JsonElement? r) =>
        r is { } e && e.TryGetProperty("connections", out var c) && c.TryGetProperty("active", out var a) ? a.GetInt64() : null;

    private static double? PrivateMb(JsonElement? r) =>
        r is { } e && e.TryGetProperty("process", out var p) && p.TryGetProperty("privateMb", out var v) ? v.GetDouble() : null;

    private static int? Threads(JsonElement? r) =>
        r is { } e && e.TryGetProperty("process", out var p) && p.TryGetProperty("threads", out var v) ? v.GetInt32() : null;
}

internal static class Stats
{
    public static double P(List<double> v, int pct)
    {
        if (v.Count == 0)
            return 0;
        var s = v.OrderBy(x => x).ToList();
        return Math.Round(s[Math.Min(s.Count - 1, (int)Math.Ceiling(pct / 100.0 * s.Count) - 1)], 1);
    }
}
