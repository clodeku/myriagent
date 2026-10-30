using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Data;

/// <summary>
/// hub.db：EF Core 管理的 Identity（Stores.SchemaVersion＝Version3）與裝置、工作階段、稽核、kv、收進來的桌面版（app_releases）。
/// 寫入只發生在登入、配對、管理動作，很少；全部經 <see cref="HubWriteLock"/> 串列化（單一寫入者）。
/// 結構變更一律新增遷移（Data/Migrations），只往前、先加欄位、下一版才移除：部署失敗回滾時資料庫不會跟著還原，舊版要讀得動新版的資料庫。
/// 第 3 階段改成帳號密碼登入、伺服器授權之後停用的（AspNetUserPasskeys、owner_key_log、devices 的 registry_*、browser_sessions.passkey_credential_id）
/// 照這個規則留到下一版的遷移才移除。
/// </summary>
internal sealed class HubDbContext(DbContextOptions<HubDbContext> options) : IdentityDbContext<IdentityUser, IdentityRole, string>(options)
{
    public DbSet<DeviceRecord> Devices => Set<DeviceRecord>();

    public DbSet<BrowserSessionRecord> BrowserSessions => Set<BrowserSessionRecord>();

    public DbSet<AuditRecord> Audit => Set<AuditRecord>();

    public DbSet<KvRecord> Kv => Set<KvRecord>();

    public DbSet<PairingRequestRecord> PairingRequests => Set<PairingRequestRecord>();

    /// <summary>（停用）見類別說明</summary>
    public DbSet<OwnerKeyLogRecord> OwnerKeyLog => Set<OwnerKeyLogRecord>();

    /// <summary>收進來的桌面版（U2）：只經 Releases/AppReleaseStore 讀寫</summary>
    public DbSet<AppReleaseRecord> AppReleases => Set<AppReleaseRecord>();

    /// <summary>協作人（2026-10-30）：只經 Auth/CollaboratorDirectory 讀寫</summary>
    public DbSet<CollaboratorRecord> Collaborators => Set<CollaboratorRecord>();

    public DbSet<CollaboratorShareRecord> CollaboratorShares => Set<CollaboratorShareRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Identity 的結構版本取自 DI 的 IdentityOptions：少了 Version3 模型就少一張表、和遷移對不上，寧可啟動失敗也不要悄悄不一致
        if (builder.Model.FindEntityType(typeof(IdentityUserPasskey<string>)) is null)
            throw new InvalidOperationException("Identity 結構版本不是 Version3（沒有 AspNetUserPasskeys）：請以 HubDbContextFactory 或 AddHubData 建立 HubDbContext");

        builder.Entity<DeviceRecord>(e =>
        {
            e.ToTable("devices");
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.TokenHash).IsUnique();
            e.HasIndex(d => d.PrevTokenHash);
        });
        builder.Entity<BrowserSessionRecord>(e =>
        {
            e.ToTable("browser_sessions");
            e.HasKey(s => s.KeyHash);
            e.HasIndex(s => s.AbsoluteExpiresAt);
            e.HasIndex(s => s.UserId);
        });
        builder.Entity<AuditRecord>(e =>
        {
            e.ToTable("audit");
            e.HasKey(a => a.Seq);
            e.Property(a => a.Seq).ValueGeneratedOnAdd();
            e.HasIndex(a => a.At);
        });
        builder.Entity<KvRecord>(e =>
        {
            e.ToTable("kv");
            e.HasKey(k => k.Key);
        });
        builder.Entity<PairingRequestRecord>(e =>
        {
            e.ToTable("pairing_requests");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.DeviceCodeHash).IsUnique();
            e.HasIndex(p => p.UserCode);
        });
        builder.Entity<OwnerKeyLogRecord>(e =>
        {
            e.ToTable("owner_key_log");
            e.HasKey(o => o.Rev);
            e.Property(o => o.Rev).ValueGeneratedNever();
        });
        builder.Entity<AppReleaseRecord>(e =>
        {
            e.ToTable("app_releases");
            e.HasKey(r => r.Release);
        });
        builder.Entity<CollaboratorRecord>(e =>
        {
            e.ToTable("collaborators");
            e.HasKey(c => c.UserId);
        });
        builder.Entity<CollaboratorShareRecord>(e =>
        {
            e.ToTable("collaborator_shares");
            e.HasKey(c => new { c.ConversationId, c.UserId });
            e.HasIndex(c => c.UserId);
        });

        // 本伺服器自己的表一律 snake_case（設計文件與手動查詢都用這個名稱）；Identity 的表維持預設
        foreach (var entity in builder.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(DeviceRecord).Namespace))
        {
            var table = entity.GetTableName()!;
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase(property.Name));
            foreach (var key in entity.GetKeys())
                key.SetName("pk_" + table);
            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName("ix_" + table + "_" + string.Join("_", index.Properties.Select(p => SnakeCase(p.Name))));
        }
    }

    internal static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
