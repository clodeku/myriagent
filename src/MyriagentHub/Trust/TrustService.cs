using System.Globalization;
using System.Text.Json;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Trust;

/// <summary>status\trust.json：給 <c>admin status</c> 印出、供各電腦本機比對的信任資訊（都是公開資料）</summary>
internal sealed record TrustStatus(
    string HubIdentityFingerprint,
    string HubIdentitySpki,
    long RegistryRev,
    long UpdatedAt);

/// <summary>
/// 信任資料的讀取與發布（sync-protocol 第 7 節）：裝置名冊與伺服器身分。名冊就是 hub.db 的 devices（含已撤銷的）、版本在 kv 的 registry_rev；
/// 伺服器是授權者（擁有者以密碼登入的瀏覽器核准配對，設計 14.4），名冊不另外簽署，裝置經釘選伺服器身分金鑰的通道取得。
/// 任何一項變更後呼叫 <see cref="PublishAsync"/>：寫 status\trust.json，並觸發 <see cref="Changed"/>（裝置 hub 送 TrustChanged）。
/// </summary>
internal sealed class TrustService(
    HubIdentity identity,
    IServiceScopeFactory scopes,
    HubPaths paths,
    TimeProvider time,
    ILogger<TrustService> log)
{
    /// <summary>名冊變了；處理常式不可阻塞</summary>
    public event Action<TrustRevs>? Changed;

    public async Task<TrustRevs> GetRevsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await GetRevsAsync(db, cancellationToken);
    }

    /// <summary>GetTrust、配對回應：<paramref name="registryAfter"/> 與目前版本不同時給整份名冊（伺服器還原到舊備份時版本會比裝置的舊，也給整份）</summary>
    public async Task<TrustBundle> GetBundleAsync(long registryAfter, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var revs = await GetRevsAsync(db, cancellationToken);
        var registry = registryAfter != revs.Registry ? await ReadRegistryAsync(db, cancellationToken) : null;
        return new TrustBundle(revs.Registry, registry);
    }

    /// <summary>寫 status\trust.json、通知在線的裝置</summary>
    public async Task PublishAsync(CancellationToken cancellationToken = default)
    {
        var revs = await GetRevsAsync(cancellationToken);
        var status = new TrustStatus(identity.Fingerprint, identity.Spki, revs.Registry, time.GetUtcNow().ToUnixTimeMilliseconds());
        try
        {
            Directory.CreateDirectory(paths.StatusDir);
            var temp = paths.TrustStatusFile + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(status, StatusJson.Options), cancellationToken);
            File.Move(temp, paths.TrustStatusFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "寫不進 {Path}", paths.TrustStatusFile);
        }
        Changed?.Invoke(revs);
    }

    /// <summary>
    /// 名冊：每台配對過的裝置（含已撤銷的，撤銷由伺服器發布；不含已移除的，設計 14.22：桌面把「已撤銷而且不在名冊」當成已移除）；
    /// 舊資料格式不合格的跳過（裝置會整份不採用）
    /// </summary>
    internal static async Task<IReadOnlyList<DeviceRegistryEntry>> ReadRegistryAsync(HubDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Devices.AsNoTracking()
            .Where(d => d.RemovedAt == null)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(EntryOf).OfType<DeviceRegistryEntry>().ToList();
    }

    /// <summary>devices 的一列 → 名冊的一筆；格式不合格（舊資料）回 null</summary>
    internal static DeviceRegistryEntry? EntryOf(DeviceRecord device)
    {
        var entry = new DeviceRegistryEntry(device.Id, device.Name, Base64UrlRules.Encode(device.DevicePubkey), device.CreatedAt, device.RevokedAt,
            device.RevokedAt is null ? null : device.RevokedReason ?? DeviceRegistryRules.RevokedRetired);
        return DeviceRegistryRules.IsValid(entry) ? entry : null;
    }

    /// <summary>名冊版本＋1（只改追蹤中的 kv 列；呼叫端在同一個 SaveChanges 裡一起寫裝置的變更）</summary>
    internal static async Task<long> NextRegistryRevAsync(HubDbContext db, CancellationToken cancellationToken)
    {
        var row = await db.Kv.FirstOrDefaultAsync(k => k.Key == KvKeys.RegistryRev, cancellationToken);
        var next = (row is not null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current) ? current : 0) + 1;
        var text = next.ToString(CultureInfo.InvariantCulture);
        if (row is null)
            db.Kv.Add(new KvRecord { Key = KvKeys.RegistryRev, Value = text });
        else
            row.Value = text;
        return next;
    }

    private static async Task<TrustRevs> GetRevsAsync(HubDbContext db, CancellationToken cancellationToken)
    {
        var registryText = await db.Kv.AsNoTracking().Where(k => k.Key == KvKeys.RegistryRev).Select(k => k.Value).FirstOrDefaultAsync(cancellationToken);
        var registry = long.TryParse(registryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return new TrustRevs(registry);
    }
}
