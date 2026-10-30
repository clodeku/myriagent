namespace Myriagent.Update;

/// <summary>為什麼不自動更新（設計 8.2，依序判斷；None＝啟用）</summary>
internal enum UpdateBlockReason
{
    None,
    NotManaged,
    CustomData,
    Disabled,
    NotPaired,
}

/// <summary>受管理的安裝、自動更新啟用條件與是否提供安裝的純函式規則（設計 8.1～8.3）</summary>
internal static class InstallLayoutRules
{
    /// <summary>
    /// 這個程式資料夾是不是受管理的安裝、是哪一版：從 current（junction）執行就以它的目標為準，直接從 versions\&lt;release&gt;\ 執行時
    /// 必須正好是 current 指向的那一版（手動執行舊版資料夾的不算，免得舊版把 current 改掉）。資料夾名稱必須是合法的 release 名稱
    /// </summary>
    /// <param name="currentTarget">current 的 junction 目標（沒有 current 為 null）</param>
    public static string? ManagedRelease(string baseDirectory, InstallLayout layout, string? currentTarget)
    {
        if (string.IsNullOrWhiteSpace(currentTarget))
            return null;
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentTarget));
        var resolved = string.Equals(dir, layout.Current, StringComparison.OrdinalIgnoreCase) ? target : dir;
        if (!string.Equals(resolved, target, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(Path.GetDirectoryName(resolved), layout.VersionsDirectory, StringComparison.OrdinalIgnoreCase))
            return null;
        var name = Path.GetFileName(resolved);
        return ReleaseIdentity.TryParseRelease(name, out _) ? name : null;
    }

    /// <summary>要不要提供「安裝到這台電腦」（設計 8.3）：不是從安裝位置執行、不是開發版，而且還沒安裝或安裝的比自己舊</summary>
    /// <param name="installedRelease">current 指向的資料夾名稱（沒有 current 為 null；不是合法的 release 名稱時當成沒安裝）</param>
    public static bool CanOfferInstall(string? managedRelease, ReleaseIdentity current, string? installedRelease)
    {
        if (managedRelease != null || current.IsDev)
            return false;
        return !ReleaseIdentity.TryParseRelease(installedRelease, out var installed) || installed!.Version < current.Version;
    }

    /// <param name="hasTestSource">端到端測試的本機假來源（MYRIAGENT_UPDATE_E2E_SOURCE）</param>
    /// <param name="e2e">MYRIAGENT_UPDATE_E2E=1：允許 MYRIAGENT_AGENT_DATA 指到暫存資料夾、以假來源代替配對</param>
    public static UpdateBlockReason Evaluate(bool managed, bool usesDefaultData, bool autoUpdate, bool paired, bool hasTestSource, bool e2e)
    {
        if (!managed)
            return UpdateBlockReason.NotManaged;
        if (!usesDefaultData && !e2e)
            return UpdateBlockReason.CustomData;
        if (!autoUpdate)
            return UpdateBlockReason.Disabled;
        if (!paired && !(e2e && hasTestSource))
            return UpdateBlockReason.NotPaired;
        return UpdateBlockReason.None;
    }

    public static string Describe(UpdateBlockReason reason) => reason switch
    {
        UpdateBlockReason.NotManaged => "開發版（不是從安裝位置執行），不自動更新",
        UpdateBlockReason.CustomData => "以其他資料夾（--data）執行，不自動更新",
        UpdateBlockReason.Disabled => "已關閉自動更新",
        UpdateBlockReason.NotPaired => "這台電腦還沒有配對，配對後才會自動更新",
        _ => string.Empty,
    };
}
