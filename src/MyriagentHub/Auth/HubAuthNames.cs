using System.Net;
using Microsoft.AspNetCore.Identity;

namespace MyriagentHub.Auth;

/// <summary>認證方案名稱</summary>
internal static class HubAuthSchemes
{
    /// <summary>擁有者的瀏覽器 cookie（Identity）</summary>
    public const string Owner = "Identity.Application";

    /// <summary>裝置權杖（只收 Authorization 標頭）</summary>
    public const string Device = "DeviceToken";

    /// <summary>預設方案：有 Authorization 標頭就交給裝置權杖，否則交給 cookie（讓 HttpContext.User 在限流與記錄時就有身分）</summary>
    public const string Selector = "MyriagentHub";
}

/// <summary>授權政策名稱</summary>
internal static class HubPolicies
{
    public const string Owner = "Owner";
    public const string Device = "Device";

    /// <summary>
    /// 只收存取權杖 <c>cha_</c>（第 4 階段裝置取 blob、媒體轉送的 <c>/api/v1/device/*</c> 端點，WP4.7；WP4.1 D56）：長效的 <c>chd_</c> 只能用在裝置 hub。
    /// WP4.1 只立政策，沒有端點用它
    /// </summary>
    public const string DeviceAccess = "DeviceAccess";

    /// <summary>/readyz：擁有者、裝置，或本機 127.0.0.1／::1</summary>
    public const string OpsRead = "OpsRead";

    /// <summary>擁有者或裝置（未對應路徑的 catch-all 用；協作人不算）</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>
    /// 擁有者或協作人的瀏覽器 cookie（多人協作，2026-10-30）：只用在網頁版會用到的端點——瀏覽器 hub、媒體與上傳（另外逐對話檢查）、
    /// <c>auth/me</c>、<c>auth/logout</c>、<c>auth/password</c>。其他擁有者的端點一律 <see cref="Owner"/>（只收 admin）
    /// </summary>
    public const string Browser = "Browser";
}

/// <summary>伺服器自己加的 claim（改名（MyriagentHub → MyriagentHub，2026-10-30）之前的值：已序列化進資料庫裡的工作階段票證，不能改）</summary>
internal static class HubClaims
{
    /// <summary>目前瀏覽器工作階段的 id（key 的 SHA-256 的 base64url；不是 cookie 裡的 key 本身）</summary>
    public const string SessionId = "myriagenthub:sid";

    public const string DeviceId = "myriagenthub:device";

    /// <summary><see cref="HubTokenKinds"/></summary>
    public const string TokenKind = "myriagenthub:token_kind";

    /// <summary>以輪換後 24 小時內的舊裝置權杖認證（Prove 時再發新的）</summary>
    public const string PreviousToken = "myriagenthub:token_prev";

    /// <summary>協作人登入的工作階段：值是協作人的 Identity 使用者 id（多人協作；票證取回時對名冊檢查還在、沒停用）</summary>
    public const string Collaborator = "myriagenthub:collab";
}

internal static class HubTokenKinds
{
    /// <summary>長效裝置權杖 chd_（只用在裝置 hub 的連線與 /readyz）</summary>
    public const string Device = "chd";

    /// <summary>15 分鐘存取權杖 cha_（只用在 /api/v1/device/*）</summary>
    public const string Access = "cha";
}

/// <summary>存在 AuthenticationProperties.Items（只在伺服器端的工作階段裡）的鍵（改名（MyriagentHub → MyriagentHub，2026-10-30）之前的值：已存進票證，不能改）</summary>
internal static class SessionItems
{
    /// <summary>登入時剛輸入過密碼（工作階段建立時就算一次近期驗證）</summary>
    public const string UserVerified = ".myriagenthub.uv";
}

internal static class OwnerAccount
{
    /// <summary>單一擁有者的帳號（帳號密碼登入，設計 14.4）</summary>
    public const string UserName = "admin";

    /// <summary>預設密碼：首次啟動建立擁有者、<c>admin reset-owner</c> 時用；登入後可在網頁改</summary>
    public const string DefaultPassword = "changeme2026";

    /// <summary>第 2 階段 passkey 版的擁有者帳號（沒有密碼）：這一版不認它、也不動它（部署回滾時上一版還要用），下一版的遷移才刪</summary>
    public const string LegacyUserName = "owner";

    public static bool IsOwner(IdentityUser? user) => user is not null && string.Equals(user.UserName, UserName, StringComparison.Ordinal);

    /// <summary>擁有者 cookie 的身分：帳號是 admin、不是協作人的工作階段（授權政策與瀏覽器 hub 用）</summary>
    public static bool IsOwnerPrincipal(System.Security.Claims.ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && user.FindFirst(HubClaims.Collaborator) is null
        // 裝置權杖的身分名稱是使用者取的裝置名稱：叫「admin」的裝置不能被當成擁有者（2026-11-07 資安檢查；目前只在 cookie 的政策下用到，先擋住）
        && user.FindFirst(HubClaims.DeviceId) is null
        && string.Equals(user.Identity.Name, UserName, StringComparison.Ordinal);
}

internal static class HttpContextCollaboratorExtensions
{
    /// <summary>協作人的工作階段：協作人的 Identity 使用者 id；擁有者、裝置、未登入回 null</summary>
    public static string? CollaboratorId(this System.Security.Claims.ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true ? user.FindFirst(HubClaims.Collaborator)?.Value : null;
}

/// <summary>匿名端點的分區鍵：IPv4 原樣、IPv4 對應的 IPv6 轉回 IPv4、IPv6 以 /64 為一區（避免同一網段換位址繞過限流）</summary>
internal static class ClientKeyRules
{
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes) + "/64";
        }
        return address.ToString();
    }

    /// <summary>記錄與稽核用的 IP 字串（IPv4 對應的 IPv6 轉回 IPv4）</summary>
    public static string? Display(IPAddress? address)
    {
        if (address is null)
            return null;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }
}

internal static class HttpContextAuthExtensions
{
    public static string? ClientIp(this HttpContext context) => ClientKeyRules.Display(context.Connection.RemoteIpAddress);

    public static string ClientPartition(this HttpContext context) => ClientKeyRules.PartitionKey(context.Connection.RemoteIpAddress);

    public static string? SessionId(this HttpContext context) => context.User.FindFirst(HubClaims.SessionId)?.Value;

    public static string? UserAgentSummary(this HttpContext context)
    {
        var ua = context.Request.Headers.UserAgent.ToString();
        return ua.Length == 0 ? null : ua.Length > 200 ? ua[..200] : ua;
    }
}
