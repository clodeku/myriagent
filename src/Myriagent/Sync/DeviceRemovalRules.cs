using Myriagent.Security;

namespace Myriagent.Sync;

/// <summary>
/// 已移除的電腦（設計 14.22、sync-protocol 7.1）：擁有者在網頁版把已撤銷的電腦移除之後，伺服器的名冊不再列它，目錄裡的 device.upsert／device.revoked 照舊留著
/// （伺服器不發新的事件種類，舊版桌面只是照舊把它列成已撤銷）。這台據此把「已撤銷（目錄有 device.revoked）而且不在名冊」的電腦當成已移除：
/// 電腦頁、矩陣不列，它擁有的複本丟掉（伺服器已刪掉那些對話，Pull 也不再回它們的事件）。
/// 名冊裡沒有這台自己時（還沒拿到名冊、或不是完整的名冊）一律不判斷：寧可多列一台已撤銷的，不可誤丟複本
/// </summary>
internal static class DeviceRemovalRules
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>已移除的電腦 id（不含這台自己）</summary>
    public static IReadOnlySet<string> Removed(string selfDeviceId, IReadOnlyList<DeviceRegistryEntry?> registry, IReadOnlyDictionary<string, DeviceRevokedPayload> revoked)
    {
        if (revoked.Count == 0)
            return None;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in registry)
        {
            if (entry?.DeviceId is { } id)
                listed.Add(id);
        }

        if (!listed.Contains(selfDeviceId))
            return None;
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in revoked.Keys)
        {
            if (!listed.Contains(id) && !string.Equals(id, selfDeviceId, StringComparison.Ordinal))
                removed.Add(id);
        }

        return removed.Count == 0 ? None : removed;
    }

    /// <summary>目錄檢視裡已移除的電腦</summary>
    public static IReadOnlySet<string> Removed(SyncCatalogView view) => Removed(view.SelfDeviceId, view.Registry, view.Revoked);
}
