namespace Myriagent.Update;

// app 下載端點的訊息與路由（app 自我更新 U2；docs/sync-protocol.md 第 15 節）。線上格式經 SyncJson（camelCase、null 省略、
// 不認得的欄位忽略）。形狀只加選用欄位：新參數一律加在最後、有預設值（docs/sync-protocol.md 1.4 的不變式，HandshakeShapeInvariantTests 把關）。

/// <summary>HelloResponse 的 <c>latestApp</c>：伺服器目前最新的可用桌面版（沒有就省略）</summary>
internal sealed record AppReleaseInfo(string Release, string Version);

/// <summary>
/// <c>GET /api/v1/device/app/releases/latest</c> 的 200 回應。<c>Manifest</c> 是 manifest 原文位元組的標準 base64（不重新序列化：
/// 簽章的對象就是這串位元組）；<c>Sig</c> 以 <see cref="ReleaseManifestRules.SerializeSignature"/> 組回 .manifest.sig 的內容，裝置以自己內建的
/// 信任清單驗（伺服器不送任何金鑰）；<c>ZipBytes</c> 是套件的位元組數
/// </summary>
internal sealed record LatestAppRelease(
    string Release,
    string Version,
    string Build,
    string Manifest,
    ReleaseSignature Sig,
    long ZipBytes);

/// <summary>
/// 裝置的 app 下載端點（<c>Authorization: Bearer cha_…</c>，伺服器的 <c>HubPolicies.DeviceAccess</c>）。都在 <c>/api/v1/device/</c> 底下：
/// 存取權杖只在那裡認證得過
/// </summary>
internal static class AppReleaseRoutes
{
    public const string DevicePrefix = "/api/v1/device/app/";

    public const string Latest = "/api/v1/device/app/releases/latest";

    /// <summary>minimal API 的路由樣板；<c>{release}</c> 沒通過 <see cref="ReleaseIdentity.TryParseRelease"/> 的回 404</summary>
    public const string PackagePattern = "/api/v1/device/app/releases/{release}/package";

    /// <summary>
    /// 一個版本的套件路徑。release 必須通過 <see cref="ReleaseIdentity.TryParseRelease"/>（只有數字、小寫十六進位、「.」與「-」，不必跳脫），
    /// 否則丟 <see cref="ArgumentException"/>：不讓呼叫端拿沒驗證過的字串組路徑（同 <see cref="ReleasePackageRules.FileNames"/>）
    /// </summary>
    public static string Package(string release) =>
        ReleaseIdentity.TryParseRelease(release, out _)
            ? $"/api/v1/device/app/releases/{release}/package"
            : throw new ArgumentException($"不是合法的 release 名稱：{release}", nameof(release));
}
