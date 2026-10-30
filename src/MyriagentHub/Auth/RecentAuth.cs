using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Auth;

/// <summary>標記「要求近期使用者驗證」的端點（測試列舉端點時用）</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class RequiresRecentAuthAttribute : Attribute
{
}

/// <summary>
/// 敏感操作（撤銷工作階段、看稽核、核准配對、撤銷裝置）要求目前工作階段在 N 分鐘內輸入過密碼（登入或再驗證，8.2.3）；
/// 不夠新時回 403 <c>{"error":"step_up_required","stepUp":true}</c>，網頁請擁有者再輸入一次密碼（POST /api/v1/auth/stepup）再重試。
/// </summary>
internal sealed class RecentAuthFilter(SessionService sessions, TimeProvider time, IOptions<HubOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var session = await sessions.FindAsync(http.SessionId(), http.RequestAborted);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (session is null || !SessionRules.HasRecentAuth(session.LastUvAt, now, options.Value.Session.RecentAuthMinutes))
            return Results.Json(new { error = "step_up_required", stepUp = true }, statusCode: StatusCodes.Status403Forbidden);
        return await next(context);
    }
}

internal static class RecentAuthEndpointExtensions
{
    public static TBuilder RequireRecentAuth<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiresRecentAuthAttribute());
        builder.AddEndpointFilter<TBuilder, RecentAuthFilter>();
        return builder;
    }
}
