using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Data;

/// <summary>hub.db 的 kv 表：小型狀態（acme_state、backup_state…）以 JSON 存放；寫入在單一寫入者鎖內做「讀後寫」</summary>
internal sealed class KvStore(IServiceScopeFactory scopes, HubWriteLock writeLock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        return await GetAsync<T>(scope.ServiceProvider.GetRequiredService<HubDbContext>(), key, cancellationToken);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) =>
        writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            await SetAsync(scope.ServiceProvider.GetRequiredService<HubDbContext>(), key, value, cancellationToken);
        }, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            await db.Kv.Where(k => k.Key == key).ExecuteDeleteAsync(cancellationToken);
        }, cancellationToken);

    public static async Task<T?> GetAsync<T>(HubDbContext db, string key, CancellationToken cancellationToken = default)
    {
        var value = await db.Kv.AsNoTracking().Where(k => k.Key == key).Select(k => k.Value).FirstOrDefaultAsync(cancellationToken);
        return value is null ? default : JsonSerializer.Deserialize<T>(value, Json);
    }

    /// <summary>直接對指定的 DbContext 寫入（呼叫端在單一寫入者鎖內）</summary>
    public static async Task SetAsync<T>(HubDbContext db, string key, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, Json);
        var row = await db.Kv.FirstOrDefaultAsync(k => k.Key == key, cancellationToken);
        if (row is null)
            db.Kv.Add(new KvRecord { Key = key, Value = json });
        else
            row.Value = json;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>kv 的鍵名（集中一處，避免各模組拼錯）</summary>
internal static class KvKeys
{
    /// <summary>第 2 階段 passkey 首次設定的狀態：帳號密碼登入後不用，但不刪（部署回滾時上一版還要用），下一版的遷移才刪</summary>
    public const string LegacySetupState = "setup_state";
    public const string AcmeState = "acme_state";
    public const string BackupState = "backup_state";
    public const string RestoreGeneration = "restore_generation";
    public const string AuditAnchor = "audit_anchor";
    public const string LastRunningBuild = "last_running_build";

    /// <summary>裝置名冊的版本（配對核准、撤銷時＋1；GetTrust 與 TrustChanged 用）</summary>
    public const string RegistryRev = "registry_rev";

    /// <summary>伺服器身分金鑰最近一次的輪換聲明（HelloResponse 一律附上，設計 4.2）</summary>
    public const string HubIdentityRotation = "hub_identity_rotation";

    /// <summary>擁有者金鑰紀錄的重設待處理（第 3 階段改成帳號密碼之前的版本用；不刪，下一版的遷移才刪）</summary>
    public const string LegacyOwnerKeyResetPending = "owner_key_reset_pending";
}
