using System.Globalization;
using Myriagent.Update;
using MyriagentHub.Data;
using MyriagentHub.Hosting;

namespace MyriagentHub.Releases;

/// <summary>app_releases.state 的值：規格 §7.2 的 available、withdrawn，加上 U2 D5 的 pruned</summary>
internal static class AppReleaseStates
{
    /// <summary>可提供：latest 端點與 Hello 的 latestApp 只看這個狀態</summary>
    public const string Available = "available";

    /// <summary>擁有者撤回（版本頁或 admin app-release-withdraw）：同名的版本永遠不能再收（U2 D14）</summary>
    public const string Withdrawn = "withdrawn";

    /// <summary>保存規則清掉的版本（不是擁有者撤回的，U2 D5）；套件端點跟撤回一樣回 410</summary>
    public const string Pruned = "pruned";
}

/// <summary>套件端點開 zip 失敗的分類（<see cref="AppReleaseRules.ClassifyOpenFailure"/>；<see cref="AppReleaseEndpoints.ServePackageAsync"/> 依它回應）</summary>
internal enum AppReleaseOpenFailure
{
    /// <summary>失敗之後再讀的資料列已經不是 available（撤回或清除剛好發生在讀資料列與開檔之間）：410 gone，記資訊</summary>
    NoLongerAvailable,

    /// <summary>仍是可用版本，但 zip 或版本資料夾不在（有人動了檔案）：410 gone，記錯誤</summary>
    Missing,

    /// <summary>仍是可用版本、檔案也在，只是暫時開不了（防毒或備份獨占開著）：503 加 Retry-After，記警告</summary>
    Busy,
}

/// <summary>
/// 桌面版發佈在伺服器上的純函式規則（U2）：最新版、收件的版本比較、保存（R29）、設定驗證、套件端點開 zip 失敗的分類。
/// release 解析不了的資料列一律不參與：收件時驗過，不會出現；資料夾名稱也組不出來
/// </summary>
internal static class AppReleaseRules
{
    /// <summary>
    /// Hub:Releases 的驗證（服務啟動與 admin preflight 共用）：通過回 null，否則回原因。
    /// 範圍：InboxPollSeconds 1～3600、KeepAvailable 1～20、MaxZipMb 1～4096、MinFreeDiskMb ≥ 0
    /// </summary>
    public static string? ValidateSettings(ReleaseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return OutOfRange("Hub:Releases:InboxPollSeconds", settings.InboxPollSeconds, 1, 3600)
            ?? OutOfRange("Hub:Releases:KeepAvailable", settings.KeepAvailable, 1, 20)
            ?? OutOfRange("Hub:Releases:MaxZipMb", settings.MaxZipMb, 1, 4096)
            ?? OutOfRange("Hub:Releases:MinFreeDiskMb", settings.MinFreeDiskMb, 0, null);
    }

    /// <summary>available 之中版本號最大的（數字比較：0.2.1000 比 0.2.999 新）；沒有就是 null</summary>
    public static AppReleaseRecord? PickLatest(IEnumerable<AppReleaseRecord> rows) =>
        AvailableNewestFirst(rows).Select(x => x.Row).FirstOrDefault();

    /// <summary>
    /// 收件的版本比較：比所有 available 的版本都新。撤回與清除的不算：撤回 0.2.300 之後可以收 0.2.301
    /// （同名的 0.2.300 另由主鍵擋，U2 D14）
    /// </summary>
    public static bool IsNewerThanAvailable(ReleaseVersion candidate, IEnumerable<AppReleaseRecord> rows) =>
        AvailableNewestFirst(rows).All(x => candidate > x.Version);

    /// <summary>
    /// 保存規則要清掉的版本（R29）：可用版本中最新 <paramref name="keepAvailable"/> 個以外的、撤回的、已經 pruned 的。
    /// 純函式看不到磁碟：撤回與 pruned 的一律列出，資料夾早就不在的由呼叫端略過。撤回與清除的版本不佔保留名額；
    /// 不認得的狀態（例如回滾前的新版寫的）不碰。依 release 序數排序
    /// </summary>
    public static IReadOnlyList<string> FoldersToDelete(IEnumerable<AppReleaseRecord> rows, int keepAvailable)
    {
        var list = rows.ToList();
        var beyond = AvailableNewestFirst(list).Skip(Math.Max(0, keepAvailable)).Select(x => x.Row.Release);
        var gone = list
            .Where(r => (r.State is AppReleaseStates.Withdrawn or AppReleaseStates.Pruned) && ReleaseIdentity.TryParseRelease(r.Release, out _))
            .Select(r => r.Release);
        return beyond.Concat(gone).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 套件端點開 zip 失敗時怎麼回：<paramref name="stateNow"/> 是失敗之後再讀一次的資料列狀態（沒有這一列是 null）。
    /// 不是 available 一律 <see cref="AppReleaseOpenFailure.NoLongerAvailable"/>：保存規則刪檔的那一瞬間開檔，丟的是共用違規的 IOException，
    /// 刪除待決的檔案丟 UnauthorizedAccessException，都不是 FileNotFoundException。仍是 available 時，FileNotFoundException 與
    /// DirectoryNotFoundException 是 <see cref="AppReleaseOpenFailure.Missing"/>，其他（共用違規等）是 <see cref="AppReleaseOpenFailure.Busy"/>
    /// </summary>
    public static AppReleaseOpenFailure ClassifyOpenFailure(string? stateNow, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (stateNow != AppReleaseStates.Available)
            return AppReleaseOpenFailure.NoLongerAvailable;
        return error is FileNotFoundException or DirectoryNotFoundException ? AppReleaseOpenFailure.Missing : AppReleaseOpenFailure.Busy;
    }

    /// <summary>app_releases.min_runtime 的文字，例「Microsoft.WindowsDesktop.App 10.0.0」</summary>
    public static string MinRuntimeText(ReleaseRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Name + " " + runtime.Version;
    }

    /// <summary>available、release 解析得了的列，依版本號由新到舊（同版本依 release 序數，結果固定）</summary>
    private static IEnumerable<(AppReleaseRecord Row, ReleaseVersion Version)> AvailableNewestFirst(IEnumerable<AppReleaseRecord> rows) =>
        rows.Where(r => r.State == AppReleaseStates.Available)
            .Select(r => (Row: r, Parsed: ReleaseIdentity.TryParseRelease(r.Release, out var identity), Identity: identity))
            .Where(x => x.Parsed)
            .Select(x => (Row: x.Row, Version: x.Identity!.Version))
            .OrderByDescending(x => x.Version)
            .ThenBy(x => x.Row.Release, StringComparer.Ordinal);

    private static string? OutOfRange(string key, int value, int min, int? max)
    {
        if (value >= min && (max is null || value <= max))
            return null;
        return max is { } upper
            ? string.Create(CultureInfo.InvariantCulture, $"{key} 必須在 {min}～{upper} 之間：{value}")
            : string.Create(CultureInfo.InvariantCulture, $"{key} 不能小於 {min}：{value}");
    }
}
