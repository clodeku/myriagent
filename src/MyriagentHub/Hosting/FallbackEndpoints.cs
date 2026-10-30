using MyriagentHub.Auth;

namespace MyriagentHub.Hosting;

internal static class FallbackEndpoints
{
    /// <summary>受保護的前綴（S10：未登入存取任何 /api/v1、/hubs/*、/m/* 一律 401）</summary>
    public static readonly IReadOnlyList<string> ProtectedPrefixes = ["/api", "/hubs", "/m", "/t", "/f"];

    /// <summary>
    /// 沒有對應端點的 /api、/hubs、/m、/t、/f 路徑：未認證一律 401（匿名者探測不出哪些端點存在），已認證才回 404。
    /// 其他沒有對應端點、也不是靜態檔的路徑（瀏覽器自動要的 /favicon.ico、/robots.txt、打錯的網址）：不論是否登入一律 404
    /// （沒有這個端點時，授權的 FallbackPolicy 對「沒有端點」的請求也生效，匿名會得到 401）。
    /// catch-all 的路由優先序最低，不影響任何具體端點；受保護前綴自己的 catch-all 比它具體，所以 /api/… 仍是 401。
    /// </summary>
    public static void MapHubFallbacks(this WebApplication app)
    {
        foreach (var prefix in ProtectedPrefixes)
        {
            app.Map(prefix + "/{**rest}", () => Results.NotFound())
                .RequireAuthorization(HubPolicies.Authenticated)
                .WithDisplayName("fallback " + prefix);
        }
        app.MapFallback("{**path}", () => Results.NotFound())
            .AllowAnonymous()
            .WithDisplayName("fallback 404");
    }
}
