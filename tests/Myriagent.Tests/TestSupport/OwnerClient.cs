using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 一次撤銷請求的時間：成功的那一次請求送出前與收到完整回應時的 Stopwatch 時間戳與系統時鐘（再驗證那一來一回不算在內）。
/// 系統時鐘給「和引擎發布狀態的時間比」用（測試的 FakeHostPort 以 TimeProvider.System 記狀態的時間）
/// </summary>
internal sealed record RevokeTiming(long SentTimestamp, DateTimeOffset SentAt, long AnsweredTimestamp, DateTimeOffset AnsweredAt, bool SteppedUp)
{
    /// <summary>請求送出到收到回應</summary>
    public TimeSpan RoundTrip => Stopwatch.GetElapsedTime(SentTimestamp, AnsweredTimestamp);
}

/// <summary>
/// 擁有者的瀏覽器（經 API，同網頁的 auth.js）：以 admin／預設密碼登入，之後帶 cookie。非 GET 一律帶
/// <c>Origin</c>（伺服器的 PublicOrigin，預設 https://localhost）與 <c>X-Myriagent-Csrf: 1</c>。剛登入就算近期驗證過（5 分鐘內輸入過密碼），核准配對不必再輸入
/// </summary>
internal sealed class OwnerClient : IDisposable
{
    public const string UserName = "admin";

    public const string DefaultPassword = "changeme2026";

    private const string StepUpRequired = "step_up_required";

    private readonly HttpClient http;

    private OwnerClient(Uri baseAddress, string origin)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("Origin", origin);
        http.DefaultRequestHeaders.Add("X-Myriagent-Csrf", "1");
    }

    /// <summary>登入的工作階段 cookie（瀏覽器 hub 的 WebSocket 升級帶它，WP4.4 的遠端指令情境）</summary>
    public CookieContainer Cookies { get; } = new();

    /// <summary>
    /// 帶登入 cookie、<c>Origin</c> 與 <c>X-Myriagent-Csrf: 1</c> 的 HTTP 用戶端（WP4.7 的端到端：E4 上傳、<c>/m/</c>、<c>/t/</c> 的 GET）；
    /// 由這個物件擁有，不要 Dispose
    /// </summary>
    public HttpClient Http => http;

    public static async Task<OwnerClient> LoginAsync(MyriagentHubProcess hub)
    {
        var client = new OwnerClient(hub.BaseAddress, hub.PublicOrigin);
        try
        {
            await client.LoginAgainAsync();
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }

    /// <summary>核准配對；近期驗證過期（403 step_up_required：同一個 MyriagentHub 跑超過 5 分鐘）就重新登入再送一次</summary>
    public async Task ApproveAsync(string userCode)
    {
        try
        {
            await PostAsync("/api/v1/pair/approve", new { userCode });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            await LoginAgainAsync();
            await PostAsync("/api/v1/pair/approve", new { userCode });
        }
    }

    public Task DenyAsync(string userCode) => PostAsync("/api/v1/pair/deny", new { userCode });

    /// <summary>
    /// 撤銷裝置，同網頁 hubapi.js 的 revokeDevice：<c>POST /api/v1/devices/{id}/revoke {reason}</c>；近期驗證過期（403 step_up_required）就再驗證
    /// （<c>POST /api/v1/auth/stepup {password}</c>）、重送一次，重送還是 step_up_required 就丟出（不迴圈）。不是 200 都丟出
    /// </summary>
    public async Task<RevokeTiming> RevokeDeviceAsync(string deviceId, string reason)
    {
        var path = $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}/revoke";
        var body = new { reason };
        var (first, firstStatus, firstText) = await TimedPostAsync(path, body);
        if (firstStatus == HttpStatusCode.OK)
            return first;
        if (firstStatus != HttpStatusCode.Forbidden || ErrorCode(firstText) != StepUpRequired)
            throw Failed(path, firstStatus, firstText);

        await PostAsync("/api/v1/auth/stepup", new { password = DefaultPassword });
        var (again, status, text) = await TimedPostAsync(path, body);
        return status == HttpStatusCode.OK ? again with { SteppedUp = true } : throw Failed(path, status, text);
    }

    public void Dispose() => http.Dispose();

    private Task LoginAgainAsync() => PostAsync("/api/v1/auth/login", new { userName = UserName, password = DefaultPassword });

    private async Task PostAsync(string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        if (response.StatusCode != HttpStatusCode.OK)
            throw Failed(path, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>送一次 POST，記下送出前與收到完整回應（PostAsJsonAsync 預設讀完本文才返回）的時間</summary>
    private async Task<(RevokeTiming Timing, HttpStatusCode Status, string Text)> TimedPostAsync(string path, object body)
    {
        var sentAt = DateTimeOffset.UtcNow;
        var sent = Stopwatch.GetTimestamp();
        using var response = await http.PostAsJsonAsync(path, body);
        var answered = Stopwatch.GetTimestamp();
        var answeredAt = DateTimeOffset.UtcNow;
        var text = await response.Content.ReadAsStringAsync();
        return (new RevokeTiming(sent, sentAt, answered, answeredAt, false), response.StatusCode, text);
    }

    /// <summary>錯誤回應 <c>{error}</c> 的代碼（不是 JSON 物件回 null）</summary>
    private static string? ErrorCode(string text)
    {
        try
        {
            return JToken.Parse(text) is JObject json ? json.Value<string>("error") : null;
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    private static HttpRequestException Failed(string path, HttpStatusCode status, string text) =>
        new($"{path} → {(int)status} {text}", null, status);
}
