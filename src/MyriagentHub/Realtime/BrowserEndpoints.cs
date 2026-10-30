using System.Text.Json;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Pairing;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 擁有者在瀏覽器上的不可逆動作（設計 14.4、8.2.3、8.2.4；6.7；14.22）：撤銷裝置、移除已撤銷的裝置、從目錄刪除帳戶。都要近期驗證（不夠新回 403 step_up_required），
/// 非 GET 的 /api 自動要求同源與 <c>X-Myriagent-Csrf: 1</c>（OriginGuard）。成功回 200 <c>{ok:true}</c>，錯誤回 <c>{error}</c>
/// </summary>
internal static class BrowserEndpoints
{
    internal sealed record RevokeDeviceBody(string? Reason);

    public static void Map(IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api/v1").RequireAuthorization(HubPolicies.Owner);
        owner.MapPost("/devices/{id}/revoke", RevokeDeviceAsync).RequireRecentAuth();
        owner.MapPost("/devices/{id}/remove", RemoveDeviceAsync).RequireRecentAuth();
        owner.MapPost("/accounts/{id}/delete", DeleteAccountAsync).RequireRecentAuth();
    }

    /// <summary>
    /// 撤銷裝置：交給 <see cref="PairingService.RevokeAsync"/>（標撤銷、名冊版本＋1、寫稽核 device.revoked，提交之後作廢存取權杖、中斷那台的連線、
    /// 通知在線的裝置、解除它的帳戶連結並發布 device.revoked）。原因只收 retired／compromised（400 invalid_request）；不存在 404、已撤銷 409；
    /// 寫入者停止或其他例外 503 unavailable（hub.db 已提交的話撤銷已生效，sync.db 那一半由重送或下次啟動補做）
    /// </summary>
    private static async Task<IResult> RevokeDeviceAsync(string id, PairingService pairing, ILoggerFactory logs, HttpContext http)
    {
        if (!SyncIds.IsDeviceId(id))
            return AuthEndpoints.Error(StatusCodes.Status404NotFound, "not_found");
        var reason = await ReadReasonAsync(http.Request);
        try
        {
            await pairing.RevokeAsync(id, reason ?? "", ShortSid(http), http.ClientIp(), http.RequestAborted);
            return Results.Ok(new { ok = true });
        }
        catch (PairingException ex)
        {
            return AuthEndpoints.Error(ex.Status, ex.Code);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                logs.CreateLogger(typeof(BrowserEndpoints).FullName!).LogError(ex, "撤銷裝置失敗：{Device}", id);
            return AuthEndpoints.Error(StatusCodes.Status503ServiceUnavailable, BrowserErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// 移除已撤銷的裝置（設計 14.22）：交給 <see cref="PairingService.RemoveAsync"/>（標移除、名冊版本＋1、寫稽核 device.removed，提交之後通知在線的裝置、
    /// 刪掉它擁有的對話、重發它的 device.upsert）。本文不用（沒有選項）。成功回 200 <c>{ok:true, conversations}</c>（這次刪掉的對話數）；
    /// 不存在 404、還沒撤銷 409 <c>not_revoked</c>、已移除 409 <c>already_removed</c>；寫入者停止或其他例外 503 unavailable
    /// （hub.db 已提交的話移除已生效，對話的刪除由重送或下次啟動補做）
    /// </summary>
    private static async Task<IResult> RemoveDeviceAsync(string id, PairingService pairing, ILoggerFactory logs, HttpContext http)
    {
        if (!SyncIds.IsDeviceId(id))
            return AuthEndpoints.Error(StatusCodes.Status404NotFound, "not_found");
        try
        {
            var conversations = await pairing.RemoveAsync(id, ShortSid(http), http.ClientIp(), http.RequestAborted);
            return Results.Ok(new { ok = true, conversations });
        }
        catch (PairingException ex)
        {
            return AuthEndpoints.Error(ex.Status, ex.Code);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                logs.CreateLogger(typeof(BrowserEndpoints).FullName!).LogError(ex, "移除裝置失敗：{Device}", id);
            return AuthEndpoints.Error(StatusCodes.Status503ServiceUnavailable, BrowserErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// 從目錄刪除邏輯帳戶（<see cref="Catalog.DeleteAccount"/>：各台解除連結、本機帳戶保留、發布 account.deleted），同一個寫入工作裡排稽核
    /// account.deleted。不存在或已刪除 404；寫入者停止或其他例外 503 unavailable。被刪的帳戶的用量指派由 UsageCoordinator 收回
    /// </summary>
    private static async Task<IResult> DeleteAccountAsync(string id, SyncWriter writer, Catalog catalog, AuditLog audit, ILoggerFactory logs, HttpContext http)
    {
        if (!SyncIds.IsLogicalAccountId(id))
            return AuthEndpoints.Error(StatusCodes.Status404NotFound, "not_found");
        var actor = ShortSid(http);
        var ip = http.ClientIp();
        CatalogOutcome outcome;
        try
        {
            outcome = await writer.WriteAsync(context =>
            {
                var result = catalog.DeleteAccount(context, id);
                if (result.Rejected is null)
                    context.AuditAfterCommit(audit, new AuditEntry(AuditActors.Owner, actor, ip, AuditActions.AccountDeleted, id, AuditOutcomes.Ok));
                return result;
            }, http.RequestAborted);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                logs.CreateLogger(typeof(BrowserEndpoints).FullName!).LogError(ex, "刪除帳戶失敗：{Account}", id);
            return AuthEndpoints.Error(StatusCodes.Status503ServiceUnavailable, BrowserErrorCodes.Unavailable);
        }
        return outcome.Rejected is null
            ? Results.Ok(new { ok = true })
            : AuthEndpoints.Error(StatusCodes.Status404NotFound, "not_found");
    }

    /// <summary>
    /// 本文 <c>{reason}</c>；沒有本文、不是 JSON、格式不對都當成沒有原因（RevokeAsync 回 400 invalid_request）。自己讀而不用參數繫結：
    /// 繫結 JSON 本文的端點會帶「只收 application/json」的比對條件，沒有 Content-Type 的請求連端點都比對不到，落到 404
    /// </summary>
    private static async Task<string?> ReadReasonAsync(HttpRequest request)
    {
        if (!request.HasJsonContentType())
            return null;
        try
        {
            return (await request.ReadFromJsonAsync<RevokeDeviceBody>(request.HttpContext.RequestAborted))?.Reason;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ShortSid(HttpContext http) => http.SessionId() is { } sid ? SessionRules.ShortId(sid) : null;
}
