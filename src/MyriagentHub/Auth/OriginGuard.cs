using MyriagentHub.Hosting;

namespace MyriagentHub.Auth;

/// <summary>
/// CSRF 與跨站 WebSocket 劫持的判斷（8.4，SEC-05）：
/// <list type="bullet">
/// <item>所有非 GET 的 <c>/api</c> 請求：Origin 必須等於本站、且帶 <c>X-Myriagent-Csrf: 1</c>（表單送不出自訂標頭）；GET 一律不改狀態。</item>
/// <item><c>/hubs/v1/browser</c>（瀏覽器 hub 的 negotiate 與升級）：Origin 必須等於本站（SignalR 預設不檢查）。</item>
/// <item>其他 <c>/hubs</c>：有 Origin 就必須是本站（裝置的 .NET 用戶端不送 Origin）。</item>
/// </list>
/// 不使用 cookie 的端點（第 3 階段桌面 app 呼叫的配對端點）以 <see cref="CsrfExemptAttribute"/> 標記豁免。
/// </summary>
internal static class OriginRules
{
    public const string CsrfHeader = "X-Myriagent-Csrf";
    public const string BrowserHubPrefix = "/hubs/v1/browser";

    public static bool IsStateChanging(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    public static bool IsSameOrigin(string? origin, string publicOrigin) =>
        HostingRules.NormalizeOrigin(origin) is { } o && string.Equals(o, publicOrigin, StringComparison.Ordinal);

    /// <summary>回傳 null＝放行；否則是拒絕原因（只寫進記錄，不回給用戶端細節）</summary>
    public static string? Check(string path, string method, string? origin, string? csrfHeader, bool csrfExempt, string publicOrigin)
    {
        if (path.StartsWith(BrowserHubPrefix, StringComparison.OrdinalIgnoreCase))
            return IsSameOrigin(origin, publicOrigin) ? null : "browser-hub-origin";
        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) || path.Equals("/hubs", StringComparison.OrdinalIgnoreCase))
            return origin is null || IsSameOrigin(origin, publicOrigin) ? null : "hub-origin";
        if (!(path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.Equals("/api", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!IsStateChanging(method) || csrfExempt)
            return null;
        if (!IsSameOrigin(origin, publicOrigin))
            return "api-origin";
        return csrfHeader == "1" ? null : "api-csrf-header";
    }
}

/// <summary>標記不使用 cookie、不需要 CSRF 標頭的端點（例如桌面 app 以 Authorization 標頭呼叫的端點）</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class CsrfExemptAttribute : Attribute
{
}

internal static class CsrfEndpointExtensions
{
    public static TBuilder CsrfExempt<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new CsrfExemptAttribute());
}

/// <summary>在路由之後、認證之前執行：拒絕時直接 403，不做任何認證與資料庫查詢</summary>
internal sealed class OriginGuardMiddleware(RequestDelegate next, string publicOrigin, ILogger<OriginGuardMiddleware> log)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var exempt = context.GetEndpoint()?.Metadata.GetMetadata<CsrfExemptAttribute>() is not null;
        var origin = request.Headers.Origin.Count > 0 ? request.Headers.Origin.ToString() : null;
        var reason = OriginRules.Check(request.Path.Value ?? "/", request.Method, origin, request.Headers[OriginRules.CsrfHeader].ToString(), exempt, publicOrigin);
        if (reason is null)
            return next(context);
        log.LogWarning("拒絕跨站請求：{Reason} {Method} {Path}", reason, request.Method, request.Path.Value);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new { error = "forbidden_origin" });
    }
}
