using MyriagentHub.Auth;

namespace MyriagentHub.Trust;

/// <summary>信任資料的擁有者 API（sync-protocol 7）：伺服器身分指紋（各電腦本機比對用）與名冊版本</summary>
internal static class TrustEndpoints
{
    public static void MapHubTrust(this WebApplication app)
    {
        var trust = app.MapGroup("/api/v1/trust").RequireAuthorization(HubPolicies.Owner);
        trust.MapGet("/", OverviewAsync);
    }

    private static async Task<IResult> OverviewAsync(TrustService trust, HubIdentity identity, HttpContext http)
    {
        var revs = await trust.GetRevsAsync(http.RequestAborted);
        return Results.Ok(new
        {
            hubIdentity = new { fingerprint = identity.Fingerprint, spki = identity.Spki },
            registryRev = revs.Registry,
        });
    }
}
