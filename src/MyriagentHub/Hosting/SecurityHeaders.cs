using System.Text.RegularExpressions;

namespace MyriagentHub.Hosting;

/// <summary>安全標頭與 CSP 的純函式規則（8.4）</summary>
internal static partial class SecurityHeaderRules
{
    /// <summary>
    /// 麥克風只給本站（輸入區的語音訊息在瀏覽器錄音，2026-10-24 起；設計 14.26）：寫成 <c>microphone=()</c> 時 getUserMedia 一律
    /// NotAllowedError，使用者在瀏覽器怎麼允許都沒用。仍然不給任何框架（frame-ancestors 'none'），開麥克風只在使用者按下錄音之後
    /// </summary>
    public const string PermissionsPolicy =
        "camera=(), microphone=(self), geolocation=(), usb=(), publickey-credentials-get=(self), publickey-credentials-create=(self)";

    /// <summary>版本化的程式庫（/lib/ 底下檔名帶版本號）：換版就換網址，可以長期快取</summary>
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary>
    /// 網頁的 CSP：與桌面一致、只多本站的 wss。不准 inline script、eval、外站腳本；style-src 的 'unsafe-inline'
    /// 沿用桌面（Markdig 表格對齊會產生 style 屬性）。
    /// </summary>
    public static string ContentSecurityPolicy(string publicOrigin)
    {
        var wss = "wss://" + publicOrigin[(publicOrigin.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; "
            + $"font-src 'self' data:; connect-src 'self' {wss}; manifest-src 'self'; base-uri 'none'; form-action 'self'; "
            + "frame-ancestors 'none'; object-src 'none'; worker-src 'none'";
    }

    /// <summary>
    /// 媒體的路徑（WP4.7 計畫 D9）：<c>/m/</c>、<c>/t/</c>、<c>/f/</c> 與桌面複本的代理 <c>/api/v1/device/media/</c>，以路徑段比對、不分大小寫
    /// （路由本身不分大小寫；<c>/mx</c> 不是）
    /// </summary>
    public static bool IsMediaPath(PathString path) =>
        path.StartsWithSegments("/m", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/t", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/f", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/v1/device/media", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 這個路徑的 CSP：媒體路徑一律是媒體專用的 sandbox CSP（<see cref="Myriagent.Media.MediaHeaderRules.MediaCsp"/>：不載入任何東西、不執行腳本，
    /// 直接打開被宣告成 SVG／HTML 的檔也不會在本站的來源下執行），其他照網頁的 <paramref name="webPolicy"/>（<see cref="ContentSecurityPolicy"/>）。
    /// 中介程式無條件寫入，端點不能自己放寬
    /// </summary>
    public static string ContentSecurityPolicyFor(PathString path, string webPolicy) =>
        IsMediaPath(path) ? Myriagent.Media.MediaHeaderRules.MediaCsp : webPolicy;

    /// <summary>
    /// Cache-Control（<paramref name="path"/> 是請求路徑）：HTML 與 API 一律 no-store；/lib/ 底下檔名帶版本號的（signalr-10.0.13.min.js）長期快取
    /// （設計 4.9.1：換版就換網址）；其他靜態檔 no-cache（每次以 ETag 重新驗證，避免部署後「新 HTML 配舊 JS」）。
    /// </summary>
    public static string CacheControlFor(string path, bool isStaticFile)
    {
        if (!isStaticFile)
            return "no-store";
        if (IsVersionedLibrary(path))
            return ImmutableCacheControl;
        return path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-store" : "no-cache";
    }

    /// <summary>以 /lib/ 開頭（不分大小寫）而且檔名以 -主.次.修.js 或 -主.次.修.min.js 結尾</summary>
    internal static bool IsVersionedLibrary(string path) =>
        path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase) && VersionedFileName().IsMatch(path[(path.LastIndexOf('/') + 1)..]);

    [GeneratedRegex(@"-[0-9]+\.[0-9]+\.[0-9]+(\.min)?\.js$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionedFileName();
}

/// <summary>每個回應都帶的安全標頭；靜態檔已設定的 Cache-Control 不覆寫</summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next, string publicOrigin)
{
    private readonly string csp = SecurityHeaderRules.ContentSecurityPolicy(publicOrigin);

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (ctx, policy) = ((HttpContext, string))state;
            var headers = ctx.Response.Headers;
            headers.ContentSecurityPolicy = SecurityHeaderRules.ContentSecurityPolicyFor(ctx.Request.Path, policy);
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = SecurityHeaderRules.PermissionsPolicy;
            if (ctx.Request.IsHttps)
                headers.StrictTransportSecurity = "max-age=31536000";
            if (string.IsNullOrEmpty(headers.CacheControl))
                headers.CacheControl = SecurityHeaderRules.CacheControlFor(ctx.Request.Path.Value ?? "/", isStaticFile: false);
            return Task.CompletedTask;
        }, (context, csp));
        return next(context);
    }
}
