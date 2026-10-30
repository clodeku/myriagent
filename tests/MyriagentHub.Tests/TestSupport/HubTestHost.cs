using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 測試用的 Myriagent Hub：與正式相同的 HubApplication.Build（同一套中介程式、端點、授權），只換掉時鐘、
/// 用 TestServer（預設）或真的 Kestrel（隨機埠＋自簽憑證），每個測試自己的資料根。
/// </summary>
internal sealed class HubTestHost : IAsyncDisposable
{
    public const string Origin = "https://localhost";

    private readonly bool deleteOnDispose;

    private HubTestHost(WebApplication app, string dataDir, ManualTimeProvider clock, bool kestrel, bool deleteOnDispose)
    {
        App = app;
        DataDir = dataDir;
        Clock = clock;
        IsKestrel = kestrel;
        this.deleteOnDispose = deleteOnDispose;
        Paths = new HubPaths(dataDir);
    }

    public WebApplication App { get; }

    public IServiceProvider Services => App.Services;

    public string DataDir { get; }

    public HubPaths Paths { get; }

    public ManualTimeProvider Clock { get; }

    public bool IsKestrel { get; }

    /// <summary>真的 Kestrel 時的位址（https://127.0.0.1:埠）</summary>
    public Uri? KestrelAddress { get; private set; }

    /// <param name="services">在正式的登記之後再登記（同一個服務型別以後登記的為準），測試換掉單一元件用（例如瀏覽器閘道的快照來源）</param>
    /// <param name="withoutDeviceFeatures">
    /// 拿掉正式登記的裝置 hub 功能宣告（在 <paramref name="services"/> 之前）：HelloResponse 回到第 3 階段的形狀，測試再以 services 登記自己要的。
    /// 正式程式沒有開關（WP4.2 答 1），只有測試能拿掉宣告
    /// </param>
    /// <param name="withoutConversationGateway">
    /// 主機自己的瀏覽器閘道以 <c>views: null</c> 重新登記（在 <paramref name="services"/> 之前），不取對話的變更通知：測試自己取
    /// <see cref="MyriagentHub.Sync.ConversationChangeFeed"/> 時用。正式的閘道是變更通知唯一的消費者，沒有瀏覽器連線時也會取走並丟掉（WP4.2 步 10），
    /// 與測試搶著取
    /// </param>
    public static async Task<HubTestHost> StartAsync(
        string? dataDir = null,
        IDictionary<string, string?>? settings = null,
        bool kestrel = false,
        ManualTimeProvider? clock = null,
        bool deleteOnDispose = true,
        Action<IServiceCollection>? services = null,
        bool withoutDeviceFeatures = false,
        bool withoutConversationGateway = false)
    {
        var dir = dataDir ?? TempDir.Create();
        clock ??= new ManualTimeProvider();
        var args = Arguments(dir, kestrel ? 0 : null, settings);

        var app = HubApplication.Build(args, builder =>
        {
            if (!kestrel)
                builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TimeProvider>(clock);
            if (!kestrel)
                builder.Services.AddSingleton<IStartupFilter, TestIpStartupFilter>();
            if (withoutDeviceFeatures)
                builder.Services.RemoveAll<DeviceHubFeature>();
            if (withoutConversationGateway)
            {
                builder.Services.RemoveAll<BrowserGateway>();
                builder.Services.AddSingleton(sp => new BrowserGateway(sp.GetRequiredService<MyriagentHub.Sync.IBrowserSnapshotSource>(), sp.GetRequiredService<BrowserConnections>(),
                    sp.GetRequiredService<IOptions<HubOptions>>(), sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BrowserGateway>>()));
            }
            services?.Invoke(builder.Services);
        });
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            // 啟動失敗時也刪掉自己建的暫存資料根（呼叫端給的資料根不動；連線不用池，主機釋放後檔案已關）
            if (dataDir is null)
                TempDir.TryDelete(dir);
            throw;
        }
        var host = new HubTestHost(app, dir, clock, kestrel, deleteOnDispose && dataDir is null);
        if (kestrel)
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            host.KestrelAddress = new Uri(address);
        }
        return host;
    }

    /// <summary>測試主機的命令列（同一套設定也給子程序的 <see cref="HubProcess"/>）；<paramref name="kestrelPort"/> 是 null＝TestServer，0＝隨機埠</summary>
    public static string[] Arguments(string dataDir, int? kestrelPort, IDictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hub:ServerDomain"] = "localhost",
            ["Hub:HttpsPort"] = kestrelPort is { } port ? port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "443",
            ["Hub:BindAddress"] = "127.0.0.1",
            ["Hub:BelowNormalPriority"] = "false",
            ["Hub:SelfTest:Enabled"] = "false",
            ["Hub:Acme:Enabled"] = "false",
            ["Hub:Backup:Enabled"] = "false",
            // 手動備份（RunAsync）照樣看磁碟：測試機的可用空間不一定有 40 GB，要測空間的測試自己設並換成 FakeDiskSpace
            ["Hub:Backup:MinFreeBytes"] = "0",
            ["Hub:FileLog:Enabled"] = "false",
            ["Hub:AdminInbox:PollSeconds"] = "1",
            ["Hub:Releases:InboxPollSeconds"] = "1",
            ["Hub:RateLimits:Enabled"] = "false",
            ["Hub:Tls:CertStore"] = "file",
            ["Hub:MemoryFuse:SampleSeconds"] = "3600",
            // 同一個測試程序裡平行跑許多主機：保險絲量到的是整個測試程序的私有記憶體，超過 400 MB 告警會讓 /healthz 偶發 degraded
            ["Hub:MemoryFuse:Enabled"] = "false",
            // 閒置修剪的積極回收停住整個測試程序、DLP 樣式是全程序共用的：平行的主機不要互相干擾（要測的測試自己打開）
            ["Hub:IdleTrim:Enabled"] = "false",
        };
        if (kestrelPort is not null)
            values["Hub:PublicOrigin"] = "https://localhost";
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
                values[key] = value;
        }
        var args = new List<string> { "--data", dataDir, "--environment", "Testing" };
        args.AddRange(values.Where(kv => kv.Value is not null).Select(kv => $"--{kv.Key}={kv.Value}"));
        return args.ToArray();
    }

    /// <summary>TestServer 的用戶端（https://localhost；預設帶 cookie 容器）</summary>
    public HubClient CreateClient(string ip = TestIpStartupFilter.DefaultIp, bool cookies = true)
    {
        var server = App.GetTestServer();
        HttpMessageHandler handler = server.CreateHandler();
        CookieHandler? cookieHandler = null;
        if (cookies)
        {
            cookieHandler = new CookieHandler(new CookieContainer()) { InnerHandler = handler };
            handler = cookieHandler;
        }
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost") };
        http.DefaultRequestHeaders.Add(TestIpStartupFilter.Header, ip);
        return new HubClient(http, cookieHandler);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await App.StopAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        await App.DisposeAsync();
        // 伺服器的 SQLite 連線不用池（見 SqliteSetup）：主機釋放後資料庫檔都已關閉，不必清池就刪得掉
        if (deleteOnDispose)
            TempDir.TryDelete(DataDir);
    }
}

/// <summary>測試用 API 用戶端：改狀態的請求自動帶本站 Origin 與 X-Myriagent-Csrf</summary>
internal sealed class HubClient(HttpClient http, CookieHandler? cookies) : IDisposable
{
    public HttpClient Http { get; } = http;

    public CookieHandler? Cookies { get; } = cookies;

    public Task<HttpResponseMessage> GetAsync(string path) => Http.GetAsync(path);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, bool csrf = true, string? origin = HubTestHost.Origin) =>
        SendAsync(HttpMethod.Post, path, body, csrf, origin);

    public Task<HttpResponseMessage> DeleteAsync(string path, bool csrf = true, string? origin = HubTestHost.Origin) =>
        SendAsync(HttpMethod.Delete, path, null, csrf, origin);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool csrf = true, string? origin = HubTestHost.Origin)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        else if (method != HttpMethod.Get)
            request.Content = JsonContent.Create(new { });
        if (csrf)
            request.Headers.Add("X-Myriagent-Csrf", "1");
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return await Http.SendAsync(request);
    }

    public async Task<JsonElement> PostJsonAsync(string path, object? body = null)
    {
        using var response = await PostAsync(path, body);
        return await ReadAsync(response, path);
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await GetAsync(path);
        return await ReadAsync(response, path);
    }

    public async Task<string> PostRawAsync(string path, object? body = null)
    {
        using var response = await PostAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{path} → {(int)response.StatusCode} {text}");
        return text;
    }

    public void Dispose() => Http.Dispose();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, string path)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{path} → {(int)response.StatusCode} {text}", null, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
}

/// <summary>擁有者的常用流程（經 API，同瀏覽器的 auth.js）</summary>
internal static class OwnerFlows
{
    public static Task<HttpResponseMessage> LoginAsync(HubClient client, string password = OwnerAccount.DefaultPassword, string userName = OwnerAccount.UserName) =>
        client.PostAsync("/api/v1/auth/login", new { userName, password });

    public static Task<HttpResponseMessage> StepUpAsync(HubClient client, string password = OwnerAccount.DefaultPassword) =>
        client.PostAsync("/api/v1/auth/stepup", new { password });

    public static Task<HttpResponseMessage> ChangePasswordAsync(HubClient client, string currentPassword, string newPassword) =>
        client.PostAsync("/api/v1/auth/password", new { currentPassword, newPassword });

    /// <summary>以預設密碼登入（之後這個用戶端就是已登入的擁有者）</summary>
    public static async Task LoginOwnerAsync(HubClient client, string password = OwnerAccount.DefaultPassword)
    {
        using var response = await LoginAsync(client, password);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("登入失敗：" + (int)response.StatusCode + " " + text);
    }
}

/// <summary>測試主機（擁有者帳號 admin 在首次啟動時已建立，密碼是預設的 <see cref="OwnerAccount.DefaultPassword"/>）</summary>
internal sealed class OwnerHost(HubTestHost host) : IAsyncDisposable
{
    public HubTestHost Host { get; } = host;

    public static async Task<OwnerHost> StartAsync(IDictionary<string, string?>? settings = null) =>
        new(await HubTestHost.StartAsync(settings: settings));

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
