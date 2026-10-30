using System.Globalization;
using System.Threading.RateLimiting;
using MyriagentHub.Auth;
using Microsoft.AspNetCore.RateLimiting;

namespace MyriagentHub.Hosting;

/// <summary>速率限制政策名稱</summary>
internal static class RateLimitPolicies
{
    public const string Login = "login";

    /// <summary>第 3 階段的 /api/v1/pair/start 掛這個政策（每 IP 每小時 5 次）</summary>
    public const string PairStart = "pair-start";

    /// <summary>瀏覽器 hub 的 negotiate 與 WebSocket 升級：已登入以工作階段分區、未登入以 IP 分區（設計 14.6）</summary>
    public const string BrowserHub = "browser-hub";
}

internal static class RateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// 分區速率限制（8.5、SEC-07）：未認證的請求以 IP（IPv6 /64）分區；已認證的流量不受 IP 限制（以工作階段或裝置分區，
    /// 第 4 階段的瀏覽器指令與上傳另有工作階段分區的政策）。停用時政策照樣存在（端點引用的名稱不能不見），只是不限制。
    /// </summary>
    public static IServiceCollection AddHubRateLimiting(this IServiceCollection services, RateLimitSettings settings)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited" }, cancellationToken);
            };

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                !settings.Enabled || context.User.Identity?.IsAuthenticated == true
                    ? RateLimitPartition.GetNoLimiter("authenticated")
                    : Fixed("anon:" + context.ClientPartition(), settings.AnonymousPerMinute, TimeSpan.FromMinutes(1)));

            // 登入、再驗證、改密碼：已登入的以工作階段分區（同一個 IP 的匿名猜測不會用掉擁有者再驗證的額度），未登入的以 IP
            o.AddPolicy(RateLimitPolicies.Login, context => settings.Enabled
                ? Fixed(context.SessionId() is { } sid ? "login:sid:" + sid : "login:" + context.ClientPartition(), settings.LoginPerMinute, TimeSpan.FromMinutes(1))
                : RateLimitPartition.GetNoLimiter("off"));
            o.AddPolicy(RateLimitPolicies.PairStart, context => settings.Enabled
                ? Fixed("pair:" + context.ClientPartition(), settings.PairStartPerHour, TimeSpan.FromHours(1))
                : RateLimitPartition.GetNoLimiter("off"));
            // 在認證之後執行：帶有效 cookie 的拿得到 sid（不吃同一個 IP 的匿名額度），匿名的在被授權擋下之前先計入 IP 的額度
            o.AddPolicy(RateLimitPolicies.BrowserHub, context => !settings.Enabled
                ? RateLimitPartition.GetNoLimiter("off")
                : context.SessionId() is { } sid
                    ? Fixed("browserhub:sid:" + sid, settings.BrowserHubPerSessionPerMinute, TimeSpan.FromMinutes(1))
                    : Fixed("browserhub:" + context.ClientPartition(), settings.BrowserHubAnonymousPerMinute, TimeSpan.FromMinutes(1)));
        });
        return services;
    }

    private static RateLimitPartition<string> Fixed(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permits),
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}
