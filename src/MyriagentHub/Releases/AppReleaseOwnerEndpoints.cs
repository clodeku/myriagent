using Myriagent.Update;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Ops;

namespace MyriagentHub.Releases;

/// <summary>版本頁與 <c>admin app-releases</c> 的一個版本：app_releases 的一列，不含 manifest／zip 的雜湊、協定版本與 runtime</summary>
internal sealed record AppReleaseView(string Release, string Version, string Build, string Commit, long ZipBytes, string Kid, string State,
    long IngestedAt, long? WithdrawnAt)
{
    public static AppReleaseView From(AppReleaseRecord record) =>
        new(record.Release, record.Version, record.Build, record.Commit, record.ZipBytes, record.Kid, record.State, record.IngestedAt, record.WithdrawnAt);
}

/// <summary><c>GET /api/v1/app/releases</c> 的回應：版本（收件時間由新到舊）、目前提供給裝置的版本（沒有是 null）、各電腦（不含已撤銷的）</summary>
internal sealed record AppReleasesResponse(IReadOnlyList<AppReleaseView> Releases, string? Latest, IReadOnlyList<AppDeviceState> Devices);

/// <summary><c>POST /api/v1/app/releases/{release}/withdraw</c> 的回應：<see cref="AlreadyWithdrawn"/> 是先前就撤回了（這次沒有改變、不寫稽核）</summary>
internal sealed record AppReleaseWithdrawResponse(string Release, string State, bool AlreadyWithdrawn);

/// <summary>
/// 版本頁的擁有者 API（U2 T9，規格 §7.7）：掛既有的擁有者政策（cookie），回應以 System.Text.Json 的 Web 預設序列化（同其他擁有者 API），錯誤是 <c>{error}</c>。
/// 非 GET 照既有的 OriginGuard 要求同源 Origin 與 <c>X-Myriagent-Csrf: 1</c>。撤回只要密碼工作階段、不要求近期驗證，擁有者下載不寫稽核
/// （使用者 2026-10-19 決定，U2 設計 D21）。網頁是 auth.html 的 <c>/#releases</c>（releases.js）
/// </summary>
internal static class AppReleaseOwnerEndpoints
{
    /// <summary>要撤回的 release 不是標準寫法（<see cref="ReleaseIdentity.TryParseRelease"/>）：400</summary>
    public const string InvalidRelease = "invalid_release";

    /// <summary><see cref="ReleasesRegistration.MapHubReleases"/> 呼叫</summary>
    public static IEndpointRouteBuilder MapOwnerReleases(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api/v1").RequireAuthorization(HubPolicies.Owner);
        owner.MapGet("/app/releases", ListAsync);
        owner.MapPost("/app/releases/{release}/withdraw", WithdrawAsync);
        owner.MapGet("/app/releases/{release}/package", DownloadAsync);
        return app;
    }

    /// <summary>版本清單（<see cref="AppReleaseStore.ListAsync"/>）、目前提供的版本（<see cref="AppReleaseStore.Latest"/>，同裝置的 latest 與 Hello 的 latestApp）、各電腦</summary>
    private static async Task<IResult> ListAsync(AppReleaseStore store, AppReleaseDeviceStates devices, HttpContext http)
    {
        var rows = await store.ListAsync(http.RequestAborted);
        return Results.Ok(new AppReleasesResponse(rows.Select(AppReleaseView.From).ToList(), store.Latest?.Release, devices.Read()));
    }

    /// <summary>
    /// 撤回：release 不是標準寫法 400 <c>invalid_release</c>、不存在 404 <c>not_found</c>；先前就撤回了回 200 <c>alreadyWithdrawn:true</c>、不再寫稽核。
    /// 撤回成功寫稽核 <c>app.release.withdrawn</c>（actor owner、工作階段短 id、IP）；撤回的是目前提供的版本時，store 的快取立刻換成前一個可用版本
    /// （之後的 Hello 與 latest 端點讀到新的；已連線的裝置不另外通知，<see cref="AppReleaseStore.LatestChanged"/> 目前沒有正式的訂閱者），
    /// 資料夾由下一次保存刪除（下載中的照樣傳完）。不讀本文（沒有參數要收）。撤回與稽核不跟著請求取消：資料列改了就一定留下稽核
    /// </summary>
    private static async Task<IResult> WithdrawAsync(string release, AppReleaseStore store, AuditLog audit, HttpContext http)
    {
        if (!ReleaseIdentity.TryParseRelease(release, out _))
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, InvalidRelease);
        var outcome = await store.WithdrawAsync(release, CancellationToken.None);
        switch (outcome)
        {
            case AppReleaseWithdrawOutcome.NotFound:
                return AuthEndpoints.Error(StatusCodes.Status404NotFound, AppReleaseErrorCodes.NotFound);
            case AppReleaseWithdrawOutcome.AlreadyWithdrawn:
                return Results.Ok(new AppReleaseWithdrawResponse(release, AppReleaseStates.Withdrawn, AlreadyWithdrawn: true));
            case AppReleaseWithdrawOutcome.Withdrawn:
                await audit.WriteAsync(new AuditEntry(AuditActors.Owner, ShortSid(http), http.ClientIp(), AuditActions.AppReleaseWithdrawn, release, AuditOutcomes.Ok),
                    CancellationToken.None);
                return Results.Ok(new AppReleaseWithdrawResponse(release, AppReleaseStates.Withdrawn, AlreadyWithdrawn: false));
            default:
                throw new InvalidOperationException("不認得的撤回結果：" + outcome);
        }
    }

    /// <summary>擁有者以 cookie 下載 zip：跟裝置的套件端點同一段（<see cref="AppReleaseEndpoints.ServePackageAsync"/>：Range、ETag、503、410、404）；不寫稽核（D21）</summary>
    private static Task<IResult> DownloadAsync(string release, AppReleaseStore store, DrainState drain, ILoggerFactory loggers, HttpContext http) =>
        AppReleaseEndpoints.ServePackageAsync(release, store, drain, loggers.CreateLogger(typeof(AppReleaseOwnerEndpoints)), http.RequestAborted);

    private static string? ShortSid(HttpContext http) => http.SessionId() is { } sid ? SessionRules.ShortId(sid) : null;
}
