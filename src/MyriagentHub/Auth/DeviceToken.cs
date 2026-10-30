using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Myriagent.Sync;
using MyriagentHub.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Auth;

/// <summary>
/// 裝置權杖規則（8.2.4）：不透明的 chd_＋256 位元亂數（base64url 43 字）；伺服器只存 SHA-256。
/// 每次請求都查資料庫（不是 JWT），撤銷立即生效。只收 <c>Authorization: Bearer</c> 標頭，絕不收 access_token 查詢參數。
/// </summary>
internal static class DeviceTokenRules
{
    public const string Prefix = "chd_";
    public const int RandomChars = 43;

    public static string NewToken() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static bool IsWellFormed(string? token) =>
        token is not null
        && token.Length == Prefix.Length + RandomChars
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && token.AsSpan(Prefix.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    /// <summary>只認「Bearer 單一權杖」：多個 Authorization 標頭、其他方案、含空白的值一律不認</summary>
    public static bool TryReadBearer(IReadOnlyList<string?> authorizationHeaders, out string token)
    {
        token = string.Empty;
        if (authorizationHeaders.Count != 1 || authorizationHeaders[0] is not { } header)
            return false;
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        var value = header[scheme.Length..].Trim();
        if (value.Length == 0 || value.Contains(' '))
            return false;
        token = value;
        return true;
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>權杖有效：目前的權杖，或輪換後 24 小時內的舊權杖；撤銷的裝置一律無效</summary>
    public static bool Matches(DeviceRecord device, byte[] hash, long now) =>
        device.RevokedAt is null
        && (CryptographicOperations.FixedTimeEquals(device.TokenHash, hash)
            || (device.PrevTokenHash is { } prev && device.PrevTokenExpires is { } expires && now < expires && CryptographicOperations.FixedTimeEquals(prev, hash)));
}

/// <summary>
/// 裝置權杖認證：<c>chd_</c>（配對時發的長效權杖，查 hub.db，撤銷立即生效）與 <c>cha_</c>（Prove 後發的 15 分鐘存取權杖，
/// 只認 <see cref="AccessTokenStore.PathPrefix"/> 底下的路徑）
/// </summary>
internal sealed class DeviceTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    HubDbContext db,
    AccessTokenStore accessTokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const long TouchIntervalMs = 60_000;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!DeviceTokenRules.TryReadBearer(Request.Headers.Authorization.ToArray(), out var token))
            return AuthenticateResult.NoResult();
        if (token.StartsWith(SyncProtocol.AccessTokenPrefix, StringComparison.Ordinal))
            return AuthenticateAccessToken(token);
        if (!DeviceTokenRules.IsWellFormed(token))
            return AuthenticateResult.Fail("權杖格式不正確");

        var hash = DeviceTokenRules.Hash(token);
        var now = TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.TokenHash == hash || d.PrevTokenHash == hash, Context.RequestAborted);
        if (device is null || !DeviceTokenRules.Matches(device, hash, now))
            return AuthenticateResult.Fail("裝置權杖無效或已撤銷");

        if (device.LastSeenAt is not { } seen || now - seen >= TouchIntervalMs)
        {
            device.LastSeenAt = now;
            device.LastIp = Context.ClientIp();
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "device:" + device.Id),
                new Claim(ClaimTypes.Name, device.Name),
                new Claim(HubClaims.DeviceId, device.Id),
                new Claim(HubClaims.TokenKind, HubTokenKinds.Device),
            ],
            HubAuthSchemes.Device);
        // 輪換後 24 小時內的舊權杖：裝置沒收到（或沒存好）新的，Prove 時再發一次
        if (!CryptographicOperations.FixedTimeEquals(device.TokenHash, hash))
            identity.AddClaim(new Claim(HubClaims.PreviousToken, "1"));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), HubAuthSchemes.Device));
    }

    private AuthenticateResult AuthenticateAccessToken(string token)
    {
        if (!AccessTokenStore.IsWellFormed(token))
            return AuthenticateResult.Fail("權杖格式不正確");
        if (!(Request.Path.Value ?? string.Empty).StartsWith(AccessTokenStore.PathPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("存取權杖只能用在裝置端點");
        if (accessTokens.Validate(token) is not { } deviceId)
            return AuthenticateResult.Fail("存取權杖無效或已過期");
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "device:" + deviceId),
                new Claim(HubClaims.DeviceId, deviceId),
                new Claim(HubClaims.TokenKind, HubTokenKinds.Access),
            ],
            HubAuthSchemes.Device);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), HubAuthSchemes.Device));
    }
}
