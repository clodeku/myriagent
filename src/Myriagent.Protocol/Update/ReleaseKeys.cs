using System.Reflection;

namespace Myriagent.Update;

/// <summary>
/// 內建的發佈公鑰（app 自我更新）：桌面只安裝、Hub 只收以這裡的金鑰簽章的版本。公開版沒有內建任何金鑰：
/// 要用自我更新，先以 <c>myriagent-release keygen --dpapi-file</c> 產生自己的金鑰（docs/release.md），把印出的
/// <c>new ReleaseKey("&lt;kid&gt;", "&lt;spki&gt;")</c> 加進 <see cref="Production"/>，重新建置桌面與 Hub。
/// 測試金鑰只經 build 屬性 MyriagentUpdateTestKeySpki 以組件中繼資料注入，而且呼叫端要明確允許（只在 MYRIAGENT_UPDATE_E2E=1 時）
/// </summary>
internal static class ReleaseKeys
{
    public const string TestKeyMetadata = "MyriagentUpdateTestKeySpki";

    /// <summary>測試金鑰的中繼資料只寫在桌面組件（Myriagent.csproj），也只從它讀</summary>
    private const string DesktopAssemblyName = "Myriagent";

    public static IReadOnlyList<ReleaseKey> Production { get; } =
    [
        // 在這裡加上自己的發佈公鑰（docs/release.md），例如：
        // new ReleaseKey("0123456789abcdef", "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE..."),
    ];

    public static IReadOnlyList<ReleaseKey> Trusted(bool allowTestKey)
    {
        if (!allowTestKey || TestKeySpki() is not { } spki)
            return Production;
        return Production.Append(new ReleaseKey(ReleaseManifestRules.ComputeKid(spki), spki)).ToList();
    }

    /// <summary>
    /// 桌面組件中繼資料裡的測試公鑰（沒有為 null）；正式發佈的 workflow 檢查它一定不存在。
    /// 只看已載入、名稱是 Myriagent 的組件，與守門測試 ReleaseKeysBuildTests 檢查的範圍相同：別的組件帶著這筆中繼資料也不認
    /// </summary>
    public static string? TestKeySpki()
    {
        var desktop = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == DesktopAssemblyName);
        if (desktop == null)
            return null;
        foreach (var meta in desktop.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (meta.Key == TestKeyMetadata && !string.IsNullOrWhiteSpace(meta.Value))
                return meta.Value;
        }
        return null;
    }
}
