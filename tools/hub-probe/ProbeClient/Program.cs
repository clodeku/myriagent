using System.Globalization;
using System.Net;
using System.Text.Json;

namespace MyriagentHub.ProbeClient;

/// <summary>
/// Myriagent Hub 的實驗用戶端（移植研究原型）。用法：MyriagentHub.ProbeClient &lt;模式&gt; [--base https://localhost:5443] [其他參數]，
/// 每個結果印一行 JSON。結束碼：0 完成、1 例外、2 用法錯誤、3 伺服器不支援這個模式（原型的測試端點）、4 load 因伺服器記憶體超過 400 MB 自己中止、
/// 5 正式站守門拒絕（WP4.9 計畫 D18：所有模式連線之前先確認是這台電腦自己啟動的 MyriagentHub）。
/// <list type="bullet">
/// <item><c>tls</c>（一般連線與只提 acme-tls/1 的連線各拿到什麼）、<c>conc</c>（N 條 TLS 連線同時開著＋M 個同時請求，伺服器連線數與記憶體）。</item>
/// <item><c>load</c>（WP4.9 步 9；S9：假裝置群分批配對、瀏覽器用戶端、卡住的瀏覽器、CPU 依時間窗加權，見 <see cref="LoadMode"/>）。</item>
/// <item>原型的模式：<c>conc-hub</c>、<c>size</c>、<c>invoke</c>、<c>replay</c>、<c>soak</c>、<c>blip</c>、<c>backpressure</c>、<c>stall</c>、
/// <c>pubrate</c>、<c>certswap</c>、<c>pair</c>——程式保留原型的作法（原型的 /test/* 測試端點與 hub 方法），執行前先檢查伺服器有沒有這些端點（正式的 MyriagentHub 沒有：結束碼 3）。</item>
/// </list>
/// </summary>
internal static class Program
{
    public const int ExitUsage = 2;

    public const int ExitUnsupported = 3;

    public const int ExitMemoryAbort = 4;

    public const int ExitGuard = 5;

    /// <summary>不帶值的參數</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "backfill-only", "relay-burst" };

    private static readonly HashSet<string> PrototypeModes = new(StringComparer.Ordinal)
    {
        "conc-hub", "size", "invoke", "replay", "soak", "blip", "backpressure", "stall", "pubrate", "certswap", "pair",
    };

    public static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "help";
        Dictionary<string, string> opts;
        try
        {
            opts = ParseArgs(args.Skip(1).ToArray());
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (opts.TryGetValue("minthreads", out var minThreads))
            ThreadPool.SetMinThreads(int.Parse(minThreads, CultureInfo.InvariantCulture), int.Parse(minThreads, CultureInfo.InvariantCulture)); // 排除「用戶端自己的執行緒集區暖身慢」

        if (mode is not ("tls" or "conc" or "load") && !PrototypeModes.Contains(mode))
            return Usage();
        var context = new ProbeContext(opts);
        try
        {
            // 正式站守門（D18）：load 帶 --hub-exe 時伺服器還沒啟動，由 load 在啟動之後自己檢查
            if (!(mode == "load" && context.Has("hub-exe")))
            {
                try
                {
                    var facts = await ProbeGuard.VerifyAsync(context.BaseUrl, ProbeGuard.DefaultTimeout);
                    context.Emit(new { @event = "guard", @base = context.BaseUrl, environment = facts.Environment });
                }
                catch (ProbeGuardException ex)
                {
                    context.Emit(new { @event = "guard", refused = true, mode, reason = ex.Message });
                    return ExitGuard;
                }
            }

            return mode switch
            {
                "tls" => await Phase2Modes.TlsAsync(context),
                "conc" => await Phase2Modes.ConcurrencyAsync(context),
                "load" => await LoadMode.RunAsync(context),
                _ => await HubModes.RunAsync(context, mode),
            };
        }
        catch (Exception ex)
        {
            context.Emit(new { @event = "fatal", mode, error = ex.ToString() });
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("modes: tls conc load");
        Console.WriteLine("modes（原型的測試端點；正式的 MyriagentHub 沒有，結束碼 3）: conc-hub size invoke replay soak blip backpressure stall pubrate certswap pair");
        Console.WriteLine("共用參數: --base https://localhost:5443 [--sni <主機名>] [--hub-path /hubs/v1/device] [--minthreads N]（--base 只收迴路的 https；連線之前先過正式站守門）");
        Console.WriteLine("load: (--base https://127.0.0.1:<埠> | --hub-exe <MyriagentHub.exe>) --fake-device <FakeDevice.exe> --work <空資料夾> --owner-password-env <變數> [--owner-user admin] [--owner-origin <PublicOrigin>]");
        Console.WriteLine("      [--devices 10] [--browsers 5] [--streams 5] [--persistent 1] [--minutes 10] [--sample-seconds 5] [--stalled-browser 1] [--idle-minutes 2]");
        Console.WriteLine("      [--seed-conversations N] [--backfill-only] [--delay-ms N] [--slack-seconds 20] [--server-processors N] [--relay-burst [--relay-cap 64]] [--out <json>]");
        Console.WriteLine("結束碼: 0 完成、1 例外、2 用法錯誤、3 伺服器不支援、4 load 因記憶體超過 400 MB 中止、5 正式站守門拒絕");
        return ExitUsage;
    }

    private static Dictionary<string, string> ParseArgs(string[] a)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("看不懂的參數：" + a[i]);
            var key = a[i][2..];
            if (Flags.Contains(key))
            {
                d[key] = "true";
                continue;
            }

            if (i + 1 >= a.Length)
                throw new ArgumentException("參數沒有值：" + a[i]);
            d[key] = a[++i];
        }
        return d;
    }
}

/// <summary>各模式共用：參數、伺服器位址、接受自簽憑證的 HttpClient（只給本機與測試伺服器用）、JSON 輸出</summary>
internal sealed class ProbeContext
{
    private readonly Dictionary<string, string> opts;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public ProbeContext(Dictionary<string, string> opts)
    {
        this.opts = opts;
        BaseUrl = Opt("base", "https://localhost:5443").TrimEnd('/');
        BaseUri = new Uri(BaseUrl);
        Sni = Opt("sni", BaseUri.Host);
        HubPath = Opt("hub-path", "/hubs/v1/device");
        Http = NewHttp();
    }

    public string BaseUrl { get; }

    public Uri BaseUri { get; }

    /// <summary>TLS 的 SNI（預設 --base 的主機名；連 127.0.0.1 但要拿 hub.myriagent.test 的憑證時另外指定）</summary>
    public string Sni { get; }

    /// <summary>裝置 hub 的路徑（正式的 /hubs/v1/device；原型是 /hubs/agent）</summary>
    public string HubPath { get; }

    public HttpClient Http { get; }

    /// <summary>實際連線的主機：localhost 直接用 127.0.0.1（只聽 IPv4 的轉送會讓 ::1 白等 2 秒）</summary>
    public string ConnectHost => BaseUri.Host == "localhost" ? "127.0.0.1" : BaseUri.Host;

    public int Port => BaseUri.Port;

    public string Opt(string key, string fallback) => opts.GetValueOrDefault(key, fallback);

    public bool Has(string key) => opts.ContainsKey(key);

    /// <summary>同樣的參數、換一個伺服器位址（load 的 --hub-exe 啟動之後）</summary>
    public ProbeContext WithBase(string baseUrl) => new(new Dictionary<string, string>(opts, StringComparer.OrdinalIgnoreCase) { ["base"] = baseUrl });

    public int IntOpt(string key, int fallback) => opts.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o, json));

    public static HttpClient NewHttp(string? bearer = null)
    {
        var h = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("myriagent-hub-probe/1.0");
        if (bearer != null)
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    /// <summary>/readyz（本機迴路免認證）；不是本機或沒權限時回 null</summary>
    public async Task<JsonElement?> ReadyzAsync()
    {
        try
        {
            using var r = await Http.GetAsync(BaseUrl + "/readyz");
            if (r.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
                return null;
            return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
