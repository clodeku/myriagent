using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Myriagent.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>
/// 假裝置的整個 HttpClient（帳戶管理員、CLI 管理員、中樞共用）：只回應 Claude 的官方用量端點（<see cref="ClaudeUsageClient.UsageUrl"/>），
/// 依 Authorization 的假權杖（<see cref="TokenFor"/>）回該帳戶設定的用量並計數；其他任何網址一律 404、不連外網（計入 <see cref="OtherRequests"/>）。
/// 配對與同步連線用自己的 HttpClient／WebSocket（不經這裡）
/// </summary>
internal sealed class FakeUsageEndpoint : HttpMessageHandler
{
    public const string TokenPrefix = "fake-device-";

    private readonly ConcurrentDictionary<string, string> bodies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> requests = new(StringComparer.Ordinal);
    private int other;

    /// <summary>帳戶的假權杖（寫進 .credentials.json；不是任何真的憑證）</summary>
    public static string TokenFor(string accountId) => TokenPrefix + accountId;

    /// <summary>不是用量端點的請求數（應該永遠是 0：假裝置不連外網）</summary>
    public int OtherRequests => Volatile.Read(ref other);

    /// <summary>這個帳戶的官方用量被查了幾次（S12 的查詢計數；Codex 由 <see cref="CountPoll"/> 計）</summary>
    public int Requests(string accountId) => requests.TryGetValue(accountId, out var n) ? n : 0;

    /// <summary>沒有走 HTTP 的查詢（Codex 的 account/rateLimits/read 由假裝置直接放入）也計一次</summary>
    public void CountPoll(string accountId) => requests.AddOrUpdate(accountId, 1, (_, n) => n + 1);

    /// <summary>
    /// 設定 Claude 帳戶下次查到的用量：鍵 <c>session</c>（5 小時）、<c>weekly</c>（每週）、<c>weekly:模型名</c>（每週・該模型），值是百分比
    /// </summary>
    public void SetClaudeUsage(string accountId, IReadOnlyDictionary<string, double> windows, DateTimeOffset now) =>
        bodies[TokenFor(accountId)] = ClaudeUsageJson(windows, now).ToString(Formatting.None);

    /// <summary>官方用量端點的回應格式（只放 UsageRules.ParseClaudeUsage 讀的 limits）</summary>
    public static JObject ClaudeUsageJson(IReadOnlyDictionary<string, double> windows, DateTimeOffset now)
    {
        var limits = new JArray();
        foreach (var (key, percent) in windows)
        {
            var limit = key switch
            {
                UsageRules.SessionKey => new JObject { ["kind"] = "session", ["resets_at"] = Iso(now.AddHours(5)) },
                UsageRules.WeeklyKey => new JObject { ["kind"] = "weekly_all", ["resets_at"] = Iso(now.AddDays(7)) },
                _ when key.StartsWith(UsageRules.WeeklyKey + ":", StringComparison.Ordinal) => new JObject
                {
                    ["kind"] = "weekly_scoped",
                    ["resets_at"] = Iso(now.AddDays(7)),
                    ["scope"] = new JObject { ["model"] = new JObject { ["id"] = null, ["display_name"] = key[(UsageRules.WeeklyKey.Length + 1)..] } },
                },
                _ => throw new ArgumentException($"不認得的 Claude 用量時間窗：{key}（只收 session、weekly、weekly:模型名）", nameof(windows)),
            };
            limit["percent"] = percent;
            limit["severity"] = percent >= 100 ? "blocked" : "normal";
            limits.Add(limit);
        }

        return new JObject { ["limits"] = limits };
    }

    /// <summary>
    /// Codex 的額度（account/rateLimits/read 與對話中 rateLimits/updated 的格式）：鍵 <c>session</c>（primary，300 分鐘）、
    /// <c>weekly</c>（secondary，10080 分鐘），值是百分比
    /// </summary>
    public static JObject CodexRateLimitsJson(IReadOnlyDictionary<string, double> windows, DateTimeOffset now)
    {
        var bucket = new JObject { ["limitId"] = "codex", ["limitName"] = null, ["primary"] = null, ["secondary"] = null, ["rateLimitReachedType"] = null };
        foreach (var (key, percent) in windows)
        {
            var (slot, minutes) = key switch
            {
                UsageRules.SessionKey => ("primary", 300),
                UsageRules.WeeklyKey => ("secondary", 10080),
                _ => throw new ArgumentException($"不認得的 Codex 用量時間窗：{key}（只收 session、weekly）", nameof(windows)),
            };
            bucket[slot] = new JObject
            {
                ["usedPercent"] = percent,
                ["windowDurationMins"] = minutes,
                ["resetsAt"] = now.AddMinutes(minutes).ToUnixTimeSeconds(),
            };
        }

        return new JObject { ["rateLimits"] = bucket };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsoluteUri != ClaudeUsageClient.UsageUrl || request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } token })
        {
            Interlocked.Increment(ref other);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("fake-device：不連外網") });
        }

        if (token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            CountPoll(token[TokenPrefix.Length..]);
        if (!bodies.TryGetValue(token, out var body))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);
}
