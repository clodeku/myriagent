using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;

namespace MyriagentHub.Pairing;

internal sealed record UserCodeBody(string? UserCode);

/// <summary>
/// 配對端點（sync-protocol 2）：
/// <list type="bullet">
/// <item><c>/api/v1/pair/start</c>、<c>/pair/token</c>：裝置呼叫，匿名、不用 cookie（免 CSRF 標頭）；start 每 IP 每小時 5 次。回應以 <see cref="SyncJson"/> 序列化（與裝置 hub 同一種線上格式）。</item>
/// <item><c>/pair/pending</c>、<c>approve</c>、<c>deny</c>：擁有者（cookie＋Origin＋CSRF 標頭）；核准要近期驗證過密碼（新裝置能同步所有資料）。</item>
/// </list>
/// </summary>
internal static class PairingEndpoints
{
    public static IServiceCollection AddHubPairing(this IServiceCollection services)
    {
        services.AddSingleton<PairingService>();
        return services;
    }

    public static void MapHubPairing(this WebApplication app)
    {
        var device = app.MapGroup("/api/v1/pair").AllowAnonymous().CsrfExempt();
        device.MapPost("/start", StartAsync).RequireRateLimiting(RateLimitPolicies.PairStart);
        device.MapPost("/token", TokenAsync);

        var owner = app.MapGroup("/api/v1/pair").RequireAuthorization(HubPolicies.Owner);
        owner.MapGet("/pending", PendingAsync);
        owner.MapPost("/approve", ApproveAsync).RequireRecentAuth();
        owner.MapPost("/deny", DenyAsync);
    }

    private static Task<IResult> StartAsync(PairStartRequest? body, PairingService pairing, HttpContext http) =>
        Run(async () => Wire(await pairing.StartAsync(body, http.ClientIp(), http.UserAgentSummary(), http.RequestAborted)));

    private static Task<IResult> TokenAsync(PairTokenRequest? body, PairingService pairing, HttpContext http) =>
        Run(async () => Wire(await pairing.TokenAsync(body, http.RequestAborted)));

    private static Task<IResult> PendingAsync(string? code, PairingService pairing, HttpContext http) =>
        Run(async () => Results.Ok(await pairing.PendingAsync(code, http.RequestAborted)));

    private static Task<IResult> ApproveAsync(UserCodeBody body, PairingService pairing, HttpContext http) =>
        Run(async () =>
        {
            await pairing.ApproveAsync(body.UserCode, http.SessionId(), http.ClientIp(), http.RequestAborted);
            return Results.Ok(new { ok = true });
        });

    private static Task<IResult> DenyAsync(UserCodeBody body, PairingService pairing, HttpContext http) =>
        Run(async () =>
        {
            await pairing.DenyAsync(body.UserCode, http.SessionId(), http.ClientIp(), http.RequestAborted);
            return Results.Ok(new { ok = true });
        });

    private static async Task<IResult> Run(Func<Task<IResult>> work)
    {
        try
        {
            return await work();
        }
        catch (PairingException ex)
        {
            return Results.Text(SyncJson.Serialize(new PairError(ex.Code, ex.Interval)), "application/json", statusCode: ex.Status);
        }
    }

    private static IResult Wire(object value) => Results.Text(SyncJson.Serialize(value), "application/json");
}
