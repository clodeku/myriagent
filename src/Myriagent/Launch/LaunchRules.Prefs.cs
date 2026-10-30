using System.Text.RegularExpressions;

namespace Myriagent.Launch;

/// <summary>
/// 執行列記得的選擇（<c>launch\prefs.json</c>，2026-10-25 使用者要求「自動延用上次選擇的執行專案」）：每個工作資料夾選的專案、每個專案的執行模式。
/// 最近用的在前，各最多 <see cref="LaunchRules.MaxPrefs"/> 筆。新檔，前一版不讀（設計 8.8）；模式存名稱，不認得的當成沒記
/// </summary>
internal sealed class LaunchPrefsFile
{
    public List<LaunchFolderPref> Folders { get; set; } = [];
    public List<LaunchTargetPref> Targets { get; set; } = [];
}

internal sealed class LaunchFolderPref
{
    /// <summary>工作資料夾（GitStatusRules.FolderKey 的形狀）</summary>
    public string? Folder { get; set; }

    /// <summary>選的目標（LaunchTarget.Id）</summary>
    public string? Selected { get; set; }
}

internal sealed class LaunchTargetPref
{
    public string? TargetId { get; set; }

    /// <summary><see cref="LaunchRules.ModeName"/></summary>
    public string? Mode { get; set; }
}

// 記得的選擇：只有使用者明確的操作（選專案、換模式、按按鈕）才記，偵測時自動選的第一個不記
internal static partial class LaunchRules
{
    /// <summary>狀態資料夾裡記選擇的檔案</summary>
    public const string PrefsFileName = "prefs.json";

    /// <summary>資料夾與專案各最多記幾筆（舊的丟掉）</summary>
    public const int MaxPrefs = 200;

    /// <summary>沒記過的專案用的執行模式</summary>
    public const LaunchMode DefaultMode = LaunchMode.Debug;

    [GeneratedRegex(@"^t[0-9a-f]{12}$")]
    private static partial Regex TargetIdPattern();

    /// <summary>是 <see cref="TargetId"/> 產生的鍵</summary>
    public static bool IsTargetId(string? id) => id != null && TargetIdPattern().IsMatch(id);

    /// <summary>這個資料夾上次選的目標；沒記過（或記的不是目標的鍵）回 null</summary>
    public static string? SelectedFromPrefs(LaunchPrefsFile prefs, string folder)
        => prefs.Folders.FirstOrDefault(f => f != null && string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase))?.Selected is { } id && IsTargetId(id)
            ? id
            : null;

    /// <summary>這個目標選的執行模式；沒記過或不認得的是 <see cref="DefaultMode"/></summary>
    public static LaunchMode ModeFromPrefs(LaunchPrefsFile prefs, string targetId)
        => ParseMode(prefs.Targets.FirstOrDefault(t => t != null && t.TargetId == targetId)?.Mode) ?? DefaultMode;

    /// <summary>記下這個資料夾選的目標（移到最前面）；內容沒變（已經是最前面的同一筆）回 false</summary>
    public static bool RememberSelection(LaunchPrefsFile prefs, string folder, string targetId)
    {
        var first = prefs.Folders.FirstOrDefault();
        if (first != null && string.Equals(first.Folder, folder, StringComparison.OrdinalIgnoreCase) && first.Selected == targetId)
            return false;
        prefs.Folders.RemoveAll(f => f == null || string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase));
        prefs.Folders.Insert(0, new LaunchFolderPref { Folder = folder, Selected = targetId });
        Trim(prefs.Folders);
        return true;
    }

    /// <summary>記下這個目標的執行模式（移到最前面）；內容沒變回 false</summary>
    public static bool RememberMode(LaunchPrefsFile prefs, string targetId, LaunchMode mode)
    {
        var name = ModeName(mode);
        var first = prefs.Targets.FirstOrDefault();
        if (first != null && first.TargetId == targetId && first.Mode == name)
            return false;
        prefs.Targets.RemoveAll(t => t == null || t.TargetId == targetId);
        prefs.Targets.Insert(0, new LaunchTargetPref { TargetId = targetId, Mode = name });
        Trim(prefs.Targets);
        return true;
    }

    private static void Trim<T>(List<T> list)
    {
        if (list.Count > MaxPrefs)
            list.RemoveRange(MaxPrefs, list.Count - MaxPrefs);
    }

    /// <summary>
    /// 這個階段的程式會跟著 app 結束：Debug 模式執行中、暫停中的程式是 app 的子程序（app 自我更新不自動重啟，按「立即重啟」照樣開始）。
    /// 建置與停止中另由 <see cref="BlocksAppRestart"/> 管
    /// </summary>
    public static bool DiesWithApp(LaunchPhase phase, LaunchMode mode) => phase switch
    {
        LaunchPhase.Running or LaunchPhase.Paused => mode == LaunchMode.Debug,
        LaunchPhase.Idle or LaunchPhase.Building or LaunchPhase.Stopping => false,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}
