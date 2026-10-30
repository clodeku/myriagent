using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace Myriagent.Sync;

/// <summary>一次 <c>/pair/token</c> 輪詢的結果</summary>
internal abstract record PairPollResult
{
    /// <summary>200：核准了，換到權杖與信任資料（只出現這一次；絕不寫進記錄）</summary>
    public sealed record Approved(PairTokenResponse Response) : PairPollResult
    {
        /// <summary>ToString 不印回應（D16：裡面有 chd_）</summary>
        protected override bool PrintMembers(StringBuilder builder)
        {
            builder.Append("Response = …");
            return true;
        }
    }

    /// <summary>400 {error}（authorization_pending、slow_down、expired_token、access_denied…）</summary>
    public sealed record Error(PairError Body) : PairPollResult;

    /// <summary>429：照 Retry-After 延後</summary>
    public sealed record RateLimited(TimeSpan? RetryAfter) : PairPollResult;

    /// <summary>連不上、逾時、5xx：照間隔重試</summary>
    public sealed record NetworkError(string Detail) : PairPollResult;
}

/// <summary>配對失敗（<see cref="Code"/> 是伺服器的錯誤代碼或 <see cref="PairingCodes"/>）</summary>
internal sealed class PairingException(string code, TimeSpan? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>桌面自己的配對失敗代碼（伺服器的代碼見 <see cref="PairingErrors"/>）</summary>
internal static class PairingCodes
{
    public const string RateLimited = "rate_limited";

    /// <summary>伺服器的回應不是預期的格式</summary>
    public const string BadResponse = "bad_response";

    public const string NetworkError = "network_error";

    public const string InvalidDeviceName = "invalid_device_name";

    public const string KeyFailed = "key_failed";

    /// <summary>核准的回應驗證不過（名冊沒有自己、伺服器身分公鑰不合格、權杖格式不對）</summary>
    public const string InvalidApproval = "invalid_approval";

    /// <summary>核准了但 sync.db／device.json 存不了</summary>
    public const string CommitFailed = "commit_failed";
}

/// <summary>
/// 配對端點的 HTTP 用戶端（sync-protocol 2.1）：匿名、不送 Authorization（送了伺服器會改走裝置權杖驗證）、不送 Origin、不用 cookie；
/// 請求與回應都是 <see cref="SyncJson"/>，錯誤是 400 <c>{"error":…}</c>、速率限制是 429（Retry-After）。每個請求 15 秒逾時。
/// 憑證一律走 WebPKI；只有開發信任（<see cref="DevTrustRules.AcceptCertificate"/>）時才接受迴路位址的自簽憑證。
/// 配對輪詢期間只用 HttpClient 與 CNG，不載入 SignalR／SQLite（S6）。
/// </summary>
internal sealed class PairingClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private const string StartPath = "/api/v1/pair/start";

    private const string TokenPath = "/api/v1/pair/token";

    private readonly HttpClient http;
    private readonly Uri server;

    /// <param name="server">要連的伺服器（可能是 MYRIAGENT_SYNC_SERVER 覆寫的）</param>
    /// <param name="pairedServer">配對的伺服器（記進 device.json 的；開發信任要兩者都是迴路位址）</param>
    /// <param name="handler">測試注入；null 時自建（開發信任的憑證回呼）</param>
    public PairingClient(Uri server, Uri pairedServer, bool devTrustEnv, HttpMessageHandler? handler = null)
    {
        this.server = server;
        var inner = handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) => DevTrustRules.AcceptCertificate(server, pairedServer, errors, devTrustEnv),
            },
        };
        http = new HttpClient(inner, disposeHandler: handler is null) { Timeout = RequestTimeout };
    }

    /// <summary>開始配對：400 {error}→<see cref="PairingException"/>（伺服器的代碼）；429→rate_limited（帶 Retry-After）；連不上→network_error</summary>
    public async Task<PairStartResponse> StartAsync(PairStartRequest request, CancellationToken ct)
    {
        var (sent, _) = await SendAsync(StartPath, SyncJson.Serialize(request), ct);
        using var response = sent ?? throw new PairingException(PairingCodes.NetworkError);
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            throw new PairingException(PairingCodes.NetworkError);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return Decode<PairStartResponse>(body) is { } start && IsStart(start)
                    ? start
                    : throw new PairingException(PairingCodes.BadResponse);
            case HttpStatusCode.TooManyRequests:
                throw new PairingException(PairingCodes.RateLimited, RetryAfter(response));
            case HttpStatusCode.BadRequest:
                throw new PairingException(Decode<PairError>(body)?.Error is { Length: > 0 } code ? code : PairingCodes.BadResponse);
            default:
                throw new PairingException(PairingCodes.NetworkError);
        }
    }

    /// <summary>輪詢一次；取消時丟 <see cref="OperationCanceledException"/>，其他失敗都包成結果</summary>
    public async Task<PairPollResult> PollAsync(string deviceCode, CancellationToken ct)
    {
        var (sent, failure) = await SendAsync(TokenPath, SyncJson.Serialize(new PairTokenRequest(deviceCode)), ct);
        if (sent is not { } response)
            return new PairPollResult.NetworkError(failure ?? "連不上伺服器");
        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                return new PairPollResult.NetworkError(ex.Message);
            }

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return Decode<PairTokenResponse>(body) is { } token
                        ? new PairPollResult.Approved(token)
                        : new PairPollResult.Error(new PairError(PairingCodes.BadResponse));
                case HttpStatusCode.TooManyRequests:
                    return new PairPollResult.RateLimited(RetryAfter(response));
                case HttpStatusCode.BadRequest:
                    return new PairPollResult.Error(Decode<PairError>(body) is { Error: { Length: > 0 } } error ? error : new PairError(PairingCodes.BadResponse));
                default:
                    return new PairPollResult.NetworkError($"HTTP {(int)response.StatusCode}");
            }
        }
    }

    public void Dispose() => http.Dispose();

    /// <summary>送出；連不上、逾時、TLS 失敗回 (null, 說明)（說明不含請求內容），取消時丟 <see cref="OperationCanceledException"/></summary>
    private async Task<(HttpResponseMessage? Response, string? Failure)> SendAsync(string path, string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, path))
        {
            Content = new StringContent(json, new UTF8Encoding(false), "application/json"),
        };
        try
        {
            return (await http.SendAsync(request, ct), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // ct 沒取消的 TaskCanceledException＝HttpClient 的逾時
            return (null, "逾時");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, ex.Message);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static bool IsStart(PairStartResponse r) =>
        PairingRules.IsDeviceCode(r.DeviceCode)
        && !string.IsNullOrEmpty(r.UserCode)
        && !string.IsNullOrEmpty(r.VerificationUri)
        && !string.IsNullOrEmpty(r.VerificationUriComplete)
        && r.ExpiresIn > 0
        && r.Interval > 0;

    private static T? Decode<T>(string body) where T : class
    {
        try
        {
            return SyncJson.Deserialize<T>(body);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidCastException)
        {
            return null;
        }
    }
}
