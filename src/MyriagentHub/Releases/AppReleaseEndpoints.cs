using System.Globalization;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Auth;
using MyriagentHub.Ops;

namespace MyriagentHub.Releases;

/// <summary>app 下載端點的錯誤代碼（回應的 <c>{"error":…}</c>；裝置依狀態碼處理，代碼給人與記錄看）。排空沿用 <see cref="SyncErrorCodes.Draining"/></summary>
internal static class AppReleaseErrorCodes
{
    /// <summary>404：release 名稱不是標準寫法，或沒有這一版</summary>
    public const string NotFound = "not_found";

    /// <summary>410：撤回或清除（withdrawn、pruned）；可用版本的檔案不在也是</summary>
    public const string Gone = "gone";

    /// <summary>503：可用版本的 zip 暫時開不了（防毒或備份獨占開著），帶 <c>Retry-After</c></summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// 裝置的 app 下載端點（U2 設計 T5；規格 §7.3；sync-protocol 第 15 節）。兩個端點都掛既有的 <see cref="HubPolicies.DeviceAccess"/>
/// （WP4.1 D56）：只收存取權杖 <c>cha_</c>，<c>chd_</c> 回 403、擁有者 cookie 與匿名回 401；跟 WP4.7 的裝置端點（blob、媒體轉送與縮圖，<c>Media/MediaRegistration.cs</c> 的 <c>/api/v1/device</c> 群組）用同一個政策。
/// 權杖不分範圍、不限每台的次數與同時下載數、不寫下載稽核（D21：自用，伺服器與已配對的裝置都可信）。
/// <list type="bullet">
/// <item><c>GET /api/v1/device/app/releases/latest</c>：最新的可用版本（<see cref="AppReleaseStore.LatestPayload"/>）；沒有就 204。排空中照常回應（D17）。</item>
/// <item><c>GET /api/v1/device/app/releases/{release}/package</c>：zip 串流，規則見 <see cref="ServePackageAsync"/>。</item>
/// </list>
/// </summary>
internal static class AppReleaseEndpoints
{
    /// <summary>由 <see cref="ReleasesRegistration.MapHubReleases"/> 呼叫</summary>
    public static IEndpointRouteBuilder MapDeviceReleases(this IEndpointRouteBuilder app)
    {
        app.MapGet(AppReleaseRoutes.Latest, Latest)
            .RequireAuthorization(HubPolicies.DeviceAccess);
        app.MapGet(AppReleaseRoutes.PackagePattern, Package)
            .RequireAuthorization(HubPolicies.DeviceAccess);
        return app;
    }

    /// <summary>成功的回應跟配對端點一樣以 SyncJson 序列化（裝置 hub 的線上格式：camelCase、省略 null）</summary>
    private static IResult Latest(AppReleaseStore store) =>
        store.LatestPayload is { } latest
            ? Results.Text(SyncJson.Serialize(latest), "application/json")
            : Results.NoContent();

    private static Task<IResult> Package(string release, AppReleaseStore store, DrainState drain, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        ServePackageAsync(release, store, drain, loggers.CreateLogger(typeof(AppReleaseEndpoints)), cancellationToken);

    /// <summary>
    /// 送一版的 zip（裝置端點與擁有者下載〔T9〕共用），依序：
    /// <list type="number">
    /// <item>名稱沒通過 <see cref="ReleaseIdentity.TryParseRelease"/>：404 <c>not_found</c>（不拿沒驗證過的字串組路徑）。</item>
    /// <item>排空狀態只讀一次（<see cref="DrainState.Current"/>）：排空中回 503 <c>draining</c>，<c>Retry-After</c> 取排空的建議秒數，不是正數時用
    /// <see cref="AdminInboxRules.DefaultRetryAfterSec"/>。</item>
    /// <item>沒有這一版：404 <c>not_found</c>；撤回或清除（withdrawn、pruned）：410 <c>gone</c>（D5）。</item>
    /// <item>以 <see cref="FileShare.Read"/> 開 zip：不給刪除權，傳輸途中保存規則刪不掉它，資料夾留到下一輪（審查重點 5.4）。
    /// 開不了（IOException、UnauthorizedAccessException）時再讀一次資料列，由 <see cref="AppReleaseRules.ClassifyOpenFailure"/> 決定：
    /// 已經不是 available（撤回或清除剛好發生在讀資料列與開檔之間）410 <c>gone</c> 並記資訊；仍是 available 而檔案或資料夾不在 410 <c>gone</c>
    /// 並記錯誤；其他（防毒或備份獨占開著）503 <c>unavailable</c>，<c>Retry-After</c> 是 <see cref="AdminInboxRules.DefaultRetryAfterSec"/>，並記警告。</item>
    /// <item>交給 <c>Results.File(Stream, …)</c>：串流送出、不讀進記憶體；Range、強 ETag <c>"&lt;zipSha256&gt;"</c>、If-Range 由框架處理；回應結束時（包括中途斷線）由框架關檔。</item>
    /// </list>
    /// </summary>
    internal static async Task<IResult> ServePackageAsync(string release, AppReleaseStore store, DrainState drain, ILogger log, CancellationToken ct)
    {
        if (!ReleaseIdentity.TryParseRelease(release, out _))
            return AuthEndpoints.Error(StatusCodes.Status404NotFound, AppReleaseErrorCodes.NotFound);

        var drainState = drain.Current;
        if (drainState.Draining)
        {
            var retryAfter = drainState.RetryAfterSec > 0 ? drainState.RetryAfterSec : AdminInboxRules.DefaultRetryAfterSec;
            return new RetryAfterResult(AuthEndpoints.Error(StatusCodes.Status503ServiceUnavailable, SyncErrorCodes.Draining), retryAfter);
        }

        var record = await store.FindAsync(release, ct);
        if (record is null)
            return AuthEndpoints.Error(StatusCodes.Status404NotFound, AppReleaseErrorCodes.NotFound);
        if (record.State != AppReleaseStates.Available)
            return AuthEndpoints.Error(StatusCodes.Status410Gone, AppReleaseErrorCodes.Gone);

        FileStream zip;
        try
        {
            zip = new FileStream(store.ZipPathOf(release), FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var now = await store.FindAsync(release, ct);
            switch (AppReleaseRules.ClassifyOpenFailure(now?.State, ex))
            {
                case AppReleaseOpenFailure.NoLongerAvailable:
                    log.LogInformation("版本 {Release} 剛被撤回或清除（開檔時：{Error}），回 410", release, ex.Message);
                    return AuthEndpoints.Error(StatusCodes.Status410Gone, AppReleaseErrorCodes.Gone);
                case AppReleaseOpenFailure.Missing:
                    log.LogError(ex, "可用版本 {Release} 的套件檔不在，回 410", release);
                    return AuthEndpoints.Error(StatusCodes.Status410Gone, AppReleaseErrorCodes.Gone);
                case AppReleaseOpenFailure.Busy:
                    log.LogWarning(ex, "可用版本 {Release} 的套件檔暫時開不了，回 503", release);
                    return new RetryAfterResult(AuthEndpoints.Error(StatusCodes.Status503ServiceUnavailable, AppReleaseErrorCodes.Unavailable),
                        AdminInboxRules.DefaultRetryAfterSec);
                default:
                    throw new InvalidOperationException("不認得的開檔失敗分類");
            }
        }

        return Results.File(zip, "application/zip", fileDownloadName: ReleasePackageRules.FileNames(release).Zip, lastModified: null,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{record.ZipSha256}\""), enableRangeProcessing: true);
    }

    /// <summary>先設 <c>Retry-After</c>（秒）再執行內層的結果：<see cref="ServePackageAsync"/> 沒有 HttpContext，標頭跟著結果帶出去</summary>
    private sealed class RetryAfterResult(IResult inner, int retryAfterSec) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSec.ToString(CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
