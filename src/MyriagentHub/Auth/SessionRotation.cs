using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Auth;

/// <summary>
/// 每次登入都發新的工作階段 key（防固定工作階段、讓舊 cookie 失效；8.2.2）。
/// ASP.NET Core 的 cookie 處理常式在請求已帶有效工作階段時，登入只會 RenewAsync 原本的 key；
/// 所以登入類端點在認證之前先把舊 cookie 從請求裡拿掉（處理常式就會 StoreAsync 一個新 key），
/// 登入成功後再把舊 key 的工作階段刪掉。登入失敗時瀏覽器的舊 cookie 不受影響。
/// </summary>
internal static class SessionRotation
{
    /// <summary>會簽發工作階段的端點（POST）</summary>
    public static readonly IReadOnlyList<string> SignInPaths = ["/api/v1/auth/login"];

    // 改名（MyriagentHub → MyriagentHub，2026-10-30）之前的值：已存進票證，不能改
    private const string OldKeyItem = "myriagenthub.old-session-key";

    /// <summary>cookie 處理常式放在 cookie 票證裡的工作階段 key claim</summary>
    private const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    /// <summary>結尾的「/」不算（端點路由把 /api/v1/auth/login/ 也送到登入，沒認出來就不會換新的工作階段 key，2026-11-07 資安檢查）</summary>
    public static bool IsSignInRequest(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
            return false;
        var path = request.Path.Value ?? string.Empty;
        if (path.Length > 1)
            path = path.TrimEnd('/');
        return SignInPaths.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>登入成功、新 cookie 已簽發後呼叫：刪除舊工作階段</summary>
    public static async Task RetireOldSessionAsync(HttpContext context, ITicketStore store)
    {
        if (context.Items[OldKeyItem] is string oldKey)
            await store.RemoveAsync(oldKey, context, context.RequestAborted);
    }

    /// <summary>從 Cookie 標頭拿掉指定名稱（含分塊 Name、NameC1…）的 cookie</summary>
    public static string StripCookie(string cookieHeader, string name) =>
        string.Join("; ", cookieHeader.Split(';')
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Where(c =>
            {
                var eq = c.IndexOf('=');
                var key = eq < 0 ? c : c[..eq];
                return !(key == name || (key.StartsWith(name + "C", StringComparison.Ordinal) && key[(name.Length + 1)..].All(char.IsAsciiDigit)));
            }));

    internal sealed class Middleware(RequestDelegate next, IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
    {
        public Task InvokeAsync(HttpContext context)
        {
            if (IsSignInRequest(context.Request))
            {
                var options = cookieOptions.Get(IdentityConstants.ApplicationScheme);
                var name = options.Cookie.Name!;
                var cookie = options.CookieManager.GetRequestCookie(context, name);
                if (!string.IsNullOrEmpty(cookie))
                {
                    var oldKey = options.TicketDataFormat.Unprotect(cookie)?.Principal.FindFirst(SessionIdClaim)?.Value;
                    if (oldKey is not null)
                        context.Items[OldKeyItem] = oldKey;
                    context.Request.Headers.Cookie = StripCookie(string.Join("; ", context.Request.Headers.Cookie.ToArray()), name);
                }
            }
            return next(context);
        }
    }
}
