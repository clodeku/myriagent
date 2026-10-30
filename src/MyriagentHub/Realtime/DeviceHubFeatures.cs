using Myriagent.Sync;

namespace MyriagentHub.Realtime;

/// <summary>伺服器宣告的一項裝置 hub 功能（<see cref="DeviceFeatureRules"/> 的名稱，例如 <c>conv1</c>）；以 <see cref="DeviceHubFeatureRegistration.AddDeviceHubFeature"/> 登記</summary>
internal sealed record DeviceHubFeature(string Name);

/// <summary>
/// 伺服器在 HelloResponse 宣告的功能（sync-protocol 14.1；WP4.1 D45）：各工作包在自己的 <c>Add*</c> 裡登記它開始支援的功能
/// （WP4.2 的 <c>conv1</c>／<c>live1</c>、WP4.5 的 <c>cmd1</c>、WP4.7 的 <c>media1</c>；U2 起再加 <c>AddHubReleases</c> 的 <c>update1</c>：只更新模式，
/// 不對應任何 hub 方法，裝置也宣告的才在交集裡）。沒有任何登記時（WP4.1；之後只有測試會拿掉正式的登記）<see cref="Declared"/> 是 null，
/// HelloResponse 與第 3 階段逐位元組相同，協商出的功能一律是空的，伺服器不送第 14 節的任何訊息。
/// 登記的名稱不合格（<see cref="DeviceFeatureRules.IsValidName"/>）或超過上限時，第一次取用（第一個 Hello）就丟例外：這是程式錯誤，不把不合格的宣告送給裝置
/// </summary>
internal sealed class DeviceHubFeatures
{
    public DeviceHubFeatures(IEnumerable<DeviceHubFeature> registered)
    {
        var names = new List<string>();
        foreach (var feature in registered)
        {
            if (!DeviceFeatureRules.IsValidName(feature.Name))
                throw new InvalidOperationException("裝置 hub 的功能名稱不合格：" + feature.Name);
            if (!names.Contains(feature.Name, StringComparer.Ordinal))
                names.Add(feature.Name);
        }
        if (!DeviceFeatureRules.IsValid(names))
            throw new InvalidOperationException($"裝置 hub 的功能超過 {DeviceFeatureRules.MaxFeatures} 項");
        Declared = names.Count > 0 ? names : null;
    }

    /// <summary>HelloResponse 的 <c>features</c>：依登記的順序、去重；沒有登記任何一項是 null（不寫進 JSON）</summary>
    public IReadOnlyList<string>? Declared { get; }
}

internal static class DeviceHubFeatureRegistration
{
    /// <summary>宣告伺服器支援這項功能（同一個名稱登記兩次只算一次）</summary>
    public static IServiceCollection AddDeviceHubFeature(this IServiceCollection services, string name)
    {
        services.AddSingleton(new DeviceHubFeature(name));
        return services;
    }
}
