using Myriagent.Security;

namespace Myriagent.Sync;

/// <summary>
/// 伺服器身分釘選與裝置名冊（只在引擎執行緒使用）：釘選存在 device.json（經 <see cref="DeviceState"/>），名冊存在 sync.db。
/// 名冊由伺服器授權（設計 14.4）：裝置只檢查格式與自己那一筆，版本只要不同就整份取代（伺服器還原到舊備份時版本會變小，sync-protocol 7.2）。
/// </summary>
internal sealed class TrustStore
{
    private readonly DeviceState device;
    private readonly SyncStore store;

    public TrustStore(DeviceState device, SyncStore store)
    {
        this.device = device;
        this.store = store;
        var (rev, entries) = store.LoadRegistry();
        RegistryRev = rev;
        Registry = entries;
    }

    public string PinnedHubSpki => device.Current.HubIdentitySpki;

    public string HubFingerprint => device.Current.HubFingerprint;

    public long RegistryRev { get; private set; }

    public IReadOnlyList<DeviceRegistryEntry> Registry { get; private set; }

    /// <summary>改釘新的伺服器身分公鑰：先經 DeviceState 原子寫 device.json，成功才換記憶體；公鑰格式不對或存檔失敗回 false</summary>
    public bool TryRepin(string newSpki)
    {
        if (!P256KeyRules.IsValidSpki(newSpki))
            return false;
        return device.TryUpdate(d => d with { HubIdentitySpki = newSpki });
    }

    /// <summary>
    /// 套用伺服器給的名冊：Accept 時整份存進 sync.db（不論版本大小）；Unchanged、Invalid 沿用上一份；
    /// SelfRevoked、NotInRegistry 不存（引擎停止同步）
    /// </summary>
    public TrustCheck ApplyBundle(TrustBundle bundle)
    {
        var check = TrustRules.CheckBundle(bundle, device.Current.DeviceId, device.Current.DevicePubKey);
        if (check == TrustCheck.Accept)
        {
            var entries = bundle.Registry!;
            store.SaveRegistry(bundle.RegistryRev, entries);
            RegistryRev = bundle.RegistryRev;
            Registry = entries;
        }

        return check;
    }
}
