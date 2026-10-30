using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.AspNetCore.Identity;

namespace MyriagentHub.Auth;

/// <summary>
/// 單一擁有者帳號（設計 14.4）：首次啟動建立 <see cref="OwnerAccount.UserName"/>／<see cref="OwnerAccount.DefaultPassword"/>，登入後可在網頁改密碼。
/// 忘記密碼時在主機上執行 <c>MyriagentHub.exe admin reset-owner</c>：密碼改回預設、撤銷所有瀏覽器工作階段。
/// 第 2 階段 passkey 版留下的（沒有密碼的 owner 帳號與它的 passkey、kv 的設定碼狀態、<c>setup\</c> 資料夾）<b>一律不動</b>：
/// 部署失敗的自動回滾只換回程式、不還原資料庫，上一版要照樣能以 passkey 登入。這一版不認 owner 帳號
/// （登入只找 admin、owner 的工作階段在 <see cref="SqliteTicketStore"/> 當成沒有），下一版的遷移才刪掉這些。
/// 另記下擁有者是否仍是預設密碼（<see cref="IsDefaultPassword"/>，WP4.5 D25）：遠端指令信封的 <c>auth.defaultPassword</c> 與瀏覽器的
/// <c>control</c> 訊息用它（擁有者 2026-10-18 的 Q5：預設密碼只警告、不擋）。
/// </summary>
internal sealed class OwnerAccountService(
    IServiceScopeFactory scopes,
    HubWriteLock writeLock,
    SessionService sessions,
    AuditLog audit,
    ILogger<OwnerAccountService> log)
{
    /// <summary>1＝仍是預設密碼；啟動計算之前當成是（寧可多警告）。跨執行緒讀寫一律經 <see cref="Volatile"/>／<see cref="Interlocked"/></summary>
    private int defaultPassword = 1;

    /// <summary>
    /// 擁有者的密碼是不是預設的 <see cref="OwnerAccount.DefaultPassword"/>（WP4.5 D25）：啟動時（<see cref="InitializeAsync"/>）以資料庫的雜湊比對一次，
    /// 之後改密碼（<see cref="OnPasswordChanged"/>）、重設擁有者（<see cref="ResetAsync"/>）時更新，不再算雜湊
    /// </summary>
    public bool IsDefaultPassword => Volatile.Read(ref defaultPassword) != 0;

    /// <summary>
    /// <see cref="IsDefaultPassword"/> 的值改變時觸發（改密碼、重設擁有者；啟動時的第一次計算不觸發，那時還沒有任何連線）。
    /// 在改密碼的請求或管理收件匣的執行緒上同步呼叫：處理者不要做久的事，丟出的例外記錄後吞掉（密碼已經換了，不能讓請求失敗）
    /// </summary>
    public event Action? PasswordStateChanged;

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var owner = await users.FindByNameAsync(OwnerAccount.UserName);
            var legacyOwner = await users.FindByNameAsync(OwnerAccount.LegacyUserName) is not null;
            var created = false;
            if (owner is null)
            {
                owner = new IdentityUser(OwnerAccount.UserName);
                Check(await users.CreateAsync(owner, OwnerAccount.DefaultPassword), "建立擁有者帳號");
                created = true;
            }
            else if (!await users.HasPasswordAsync(owner))
            {
                Check(await users.AddPasswordAsync(owner, OwnerAccount.DefaultPassword), "設定擁有者密碼");
                created = true;
            }

            if (created)
            {
                await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.OwnerCreated, OwnerAccount.UserName, AuditOutcomes.Ok,
                    new { legacyOwner }), cancellationToken);
                log.LogWarning("擁有者帳號 {UserName} 已以預設密碼建立；登入後可在網頁改密碼", OwnerAccount.UserName);
            }
            // 剛建立就是預設；否則以存下的雜湊比對一次（約 100 ms，只在啟動時）。只讀：不用 CheckPasswordAsync（它在需要時會重算雜湊、換安全戳記）
            var isDefault = created || (owner.PasswordHash is { } hash
                && users.PasswordHasher.VerifyHashedPassword(owner, hash, OwnerAccount.DefaultPassword) != PasswordVerificationResult.Failed);
            Volatile.Write(ref defaultPassword, isDefault ? 1 : 0);
        }, cancellationToken);

    /// <summary>
    /// 改密碼成功之後（<c>POST /api/v1/auth/password</c>）：新密碼是不是預設值由呼叫端以字串比對（不必再算雜湊）；值變了才觸發 <see cref="PasswordStateChanged"/>
    /// </summary>
    internal void OnPasswordChanged(bool isDefault)
    {
        if (Interlocked.Exchange(ref defaultPassword, isDefault ? 1 : 0) == (isDefault ? 1 : 0))
            return;
        foreach (var handler in PasswordStateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "擁有者密碼狀態改變的處理者失敗");
            }
        }
    }

    /// <summary>管理收件匣的 reset-owner：密碼改回預設（同一次更新換掉雜湊與安全戳記）、撤銷所有瀏覽器工作階段</summary>
    public async Task<OwnerResetResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var owner = await users.FindByNameAsync(OwnerAccount.UserName) ?? throw new InvalidOperationException("沒有擁有者帳號");
            owner.PasswordHash = users.PasswordHasher.HashPassword(owner, OwnerAccount.DefaultPassword);
            Check(await users.UpdateSecurityStampAsync(owner), "重設擁有者密碼");
        }, cancellationToken);
        OnPasswordChanged(isDefault: true);
        // 密碼已經換了：撤銷與稽核不能因為服務正在停止而只做一半
        var revokedSessions = await sessions.RevokeAllAsync(CancellationToken.None);
        await audit.WriteAsync(new AuditEntry(AuditActors.Admin, null, null, AuditActions.OwnerReset, OwnerAccount.UserName, AuditOutcomes.Ok,
            new { revokedSessions }), CancellationToken.None);
        log.LogWarning("擁有者密碼已改回預設，撤銷 {Count} 個瀏覽器工作階段", revokedSessions);
        return new OwnerResetResult(OwnerAccount.UserName, revokedSessions);
    }

    private static void Check(IdentityResult result, string what)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(what + "失敗：" + string.Join("; ", result.Errors.Select(e => e.Code)));
    }
}

internal sealed record OwnerResetResult(string UserName, int RevokedSessions);
