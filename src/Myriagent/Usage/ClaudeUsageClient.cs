using System.Net;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Usage;

/// <summary>Claude 用量查詢的結果種類</summary>
internal enum ClaudeUsageOutcome
{
    Success,

    /// <summary>沒有登入憑證</summary>
    NoCredentials,

    /// <summary>權杖已過期（本程式不自己更新權杖，等 CLI 下次執行時更新）</summary>
    TokenExpired,

    /// <summary>伺服器拒絕權杖（401／403）</summary>
    Unauthorized,

    /// <summary>查詢太頻繁（429）</summary>
    RateLimited,
    Failed,
}

/// <summary>
/// 讀 Claude 帳戶的 OAuth 權杖、查官方用量端點 GET /api/oauth/usage（與 CLI 的 /usage 相同）。
/// **只讀權杖、絕不更新**：自己 refresh 會輪換 refresh token，CLI 手上的舊 token 失效就等於被登出（見 docs/protocol-notes.md）。
/// </summary>
internal sealed class ClaudeUsageClient
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>權杖到期前多久就當作過期（避免查到一半剛好過期）</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private readonly HttpClient http;

    public ClaudeUsageClient(HttpClient http)
    {
        this.http = http;
    }

    /// <summary>.credentials.json → 權杖與到期時間；沒有回 null</summary>
    internal static (string Token, DateTimeOffset? ExpiresAt)? ReadToken(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
            return null;
        try
        {
            var oauth = JObject.Parse(credentialsJson)["claudeAiOauth"] as JObject;
            var token = oauth?.Value<string>("accessToken");
            if (string.IsNullOrWhiteSpace(token))
                return null;
            var expires = oauth!.Value<long?>("expiresAt");
            return (token, expires.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(expires.Value) : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool IsExpired(DateTimeOffset? expiresAt, DateTimeOffset now)
        => expiresAt.HasValue && expiresAt.Value - ExpirySkew <= now;

    /// <summary>
    /// 讀憑證檔（不存在回 null）。以 ReadWrite｜Delete 共用模式開檔：讀取期間 CLI 照樣能寫入或以改名覆寫它
    /// （預設的 FileShare.Read 會讓 CLI 換發權杖時寫不進去，新權杖沒存到就等於被登出）
    /// </summary>
    internal static string? ReadCredentials(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task<(ClaudeUsageOutcome Outcome, JObject? Json, string? Error)> FetchAsync(string credentialFile, CancellationToken ct)
    {
        string? text;
        try
        {
            text = ReadCredentials(credentialFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ClaudeUsageOutcome.Failed, null, $"讀不到登入憑證：{ex.Message}");
        }

        var token = ReadToken(text);
        if (token == null)
            return (ClaudeUsageOutcome.NoCredentials, null, null);
        if (IsExpired(token.Value.ExpiresAt, DateTimeOffset.UtcNow))
            return (ClaudeUsageOutcome.TokenExpired, null, "登入權杖已過期，下次使用這個帳戶時會自動更新");

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value.Token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var json = JsonConvert.DeserializeObject<JObject>(body, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                    return (ClaudeUsageOutcome.Success, json, null);
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return (ClaudeUsageOutcome.Unauthorized, null, "用量查詢被拒（權杖失效），請重新登入這個帳戶");
                case HttpStatusCode.TooManyRequests:
                    return (ClaudeUsageOutcome.RateLimited, null, "用量查詢太頻繁，稍後再試");
                default:
                    return (ClaudeUsageOutcome.Failed, null, $"用量查詢失敗（HTTP {(int)response.StatusCode}）");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (ClaudeUsageOutcome.Failed, null, $"用量查詢失敗：{ex.Message}");
        }
    }
}
