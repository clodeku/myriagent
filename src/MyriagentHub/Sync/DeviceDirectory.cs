using System.Collections.Concurrent;
using Myriagent.Security;
using MyriagentHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Sync;

/// <summary>已配對裝置的不變資料（hub.db 的 devices；顯示名稱、上線時間、版本在 sync.db 的 device_streams）</summary>
internal sealed record DeviceInfo(
    string Id,
    string Name,
    string? MachineName,
    string? WindowsUser,
    string? AppVersion,
    string KeyStorage,
    string DevicePubKey,
    string Fingerprint,
    long CreatedAt,
    long? RevokedAt,
    string? RevokedReason,
    long? RemovedAt = null)
{
    public bool Revoked => RevokedAt is not null;

    /// <summary>擁有者把它從清單移除了（一定也已撤銷；設計 14.22）：不列在任何清單，查單台照樣查得到（已撤銷的判斷不變）</summary>
    public bool Removed => RemovedAt is not null;

    public static DeviceInfo From(DeviceRecord record)
    {
        var spki = Base64UrlRules.Encode(record.DevicePubkey);
        return new DeviceInfo(record.Id, record.Name, record.MachineName, record.WindowsUser, record.AppVersion, record.KeyStorage,
            spki, P256KeyRules.Fingerprint(spki), record.CreatedAt, record.RevokedAt, record.RevokedReason, record.RemovedAt);
    }
}

/// <summary>
/// hub.db devices 的記憶體快照：sync.db 的寫入交易（同步的、在寫入執行緒上）組 <c>device.upsert</c> 時要讀裝置資料，不能在交易裡查 hub.db。
/// 啟動時整份載入；配對核准、撤銷時由寫 hub.db 的那一方在提交之後更新。
/// </summary>
internal sealed class DeviceDirectory(IServiceScopeFactory scopes)
{
    private readonly ConcurrentDictionary<string, DeviceInfo> devices = new(StringComparer.Ordinal);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var rows = await db.Devices.AsNoTracking().ToListAsync(cancellationToken);
        devices.Clear();
        foreach (var row in rows)
            devices[row.Id] = DeviceInfo.From(row);
    }

    public DeviceInfo? Get(string deviceId) => devices.TryGetValue(deviceId, out var info) ? info : null;

    /// <summary>全部（含已撤銷與已移除的），依配對時間</summary>
    public IReadOnlyList<DeviceInfo> All => devices.Values.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();

    /// <summary>列在清單上的（<see cref="All"/> 去掉已移除的）：電腦頁、矩陣、版本頁</summary>
    public IReadOnlyList<DeviceInfo> Listed => All.Where(d => !d.Removed).ToList();

    public void Put(DeviceInfo info) => devices[info.Id] = info;
}
