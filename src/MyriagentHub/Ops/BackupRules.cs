using System.Globalization;
using MyriagentHub.Hosting;

namespace MyriagentHub.Ops;

/// <summary>每日備份的純函式規則（9.9）：執行時點、資料夾命名、保留（每日＋每週的份數、合計上限）、備份前的磁碟空間</summary>
internal static class BackupRules
{
    public const string DayFormat = "yyyyMMdd";
    public const string ManualFormat = "yyyyMMdd-HHmmss";

    /// <summary>「HH:mm」→ 當天的時刻；格式不對丟例外（設定錯誤在啟動時就暴露）</summary>
    public static TimeOnly ParseDailyAt(string? value)
    {
        if (TimeOnly.TryParseExact((value ?? string.Empty).Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;
        throw new InvalidOperationException($"Hub:Backup:DailyAt 必須是 HH:mm：{value}");
    }

    /// <summary>下一次排程時間（本機時區）：今天的時刻還沒到就是今天，否則明天</summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowLocal, TimeOnly at)
    {
        var today = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, at.Hour, at.Minute, 0, nowLocal.Offset);
        return today > nowLocal ? today : today.AddDays(1);
    }

    /// <summary>備份資料夾名稱：排程的用日期，手動（backup-now）的加時分秒</summary>
    public static string FolderName(DateTimeOffset nowLocal, bool scheduled) =>
        nowLocal.ToString(scheduled ? DayFormat : ManualFormat, CultureInfo.InvariantCulture);

    public static DateTime? ParseFolder(string name)
    {
        if (DateTime.TryParseExact(name, ManualFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var manual))
            return manual;
        if (DateTime.TryParseExact(name, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day;
        return null;
    }

    /// <summary>
    /// 保留：最新的 <paramref name="keepDaily"/> 份全部留；更舊的每個 ISO 週只留最新一份，最多 <paramref name="keepWeekly"/> 週；
    /// 回傳要刪的資料夾名稱。名稱看不懂的資料夾一律不動（以免刪到人放的東西）。
    /// </summary>
    public static IReadOnlyList<string> SelectExpired(IEnumerable<string> folderNames, int keepDaily, int keepWeekly)
    {
        var dated = folderNames
            .Select(n => (Name: n, At: ParseFolder(n)))
            .Where(x => x.At is not null)
            .OrderByDescending(x => x.At)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .ToList();
        var keep = new HashSet<string>(dated.Take(keepDaily).Select(x => x.Name), StringComparer.Ordinal);
        var weeks = new HashSet<(int Year, int Week)>();
        foreach (var (name, at) in dated.Skip(keepDaily))
        {
            var week = (ISOWeek.GetYear(at!.Value), ISOWeek.GetWeekOfYear(at.Value));
            if (weeks.Count < keepWeekly && weeks.Add(week))
                keep.Add(name);
        }
        return dated.Where(x => !keep.Contains(x.Name)).Select(x => x.Name).ToList();
    }

    /// <summary>
    /// 備份之前的磁碟空間（2026-10-25）：存了大約 <paramref name="estimate"/> 個位元組之後，可用空間要不低於 <paramref name="minFreeBytes"/>。
    /// 不夠時從最舊的備份刪起、直到夠為止，最新的一份一定留；刪光了還是不夠（或可用空間量不到，負的）就不備份，這時一份都不刪
    /// （這次不會有新的，舊的就是僅有的備份）。名稱看不懂的資料夾不算、不動
    /// </summary>
    public static BackupSpacePlan PlanSpace(IEnumerable<BackupFolderSize> folders, long estimate, long freeBytes, long minFreeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimate);
        if (freeBytes < 0)
            return new BackupSpacePlan(false, []);
        var need = (Int128)estimate + minFreeBytes;
        if (freeBytes >= need)
            return new BackupSpacePlan(true, []);
        var delete = new List<string>();
        Int128 free = freeBytes;
        foreach (var folder in NewestFirst(folders).Skip(1).Reverse())
        {
            delete.Add(folder.Name);
            free += Math.Max(0, folder.Bytes);
            if (free >= need)
                return new BackupSpacePlan(true, delete);
        }
        return new BackupSpacePlan(false, []);
    }

    /// <summary>
    /// 合計的上限（2026-10-25）：<paramref name="folders"/> 合計超過 <paramref name="maxTotalBytes"/> 時從最舊的刪起，最新的一份一定留；
    /// 回傳要刪的資料夾名稱。名稱看不懂的資料夾不算、不動
    /// </summary>
    public static IReadOnlyList<string> SelectOverBudget(IEnumerable<BackupFolderSize> folders, long maxTotalBytes)
    {
        var dated = NewestFirst(folders);
        Int128 total = 0;
        foreach (var folder in dated)
            total += Math.Max(0, folder.Bytes);
        var delete = new List<string>();
        for (var i = dated.Count - 1; i > 0 && total > maxTotalBytes; i--)
        {
            delete.Add(dated[i].Name);
            total -= Math.Max(0, dated[i].Bytes);
        }
        return delete;
    }

    /// <summary><c>Hub:Backup</c> 的保留與空間設定的第一個問題（admin preflight 的 <c>config.backup</c>）；沒問題回 null</summary>
    public static string? Validate(BackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.KeepDaily < 1)
            return "Hub:Backup:KeepDaily 至少 1：" + settings.KeepDaily.ToString(CultureInfo.InvariantCulture);
        if (settings.KeepWeekly < 0)
            return "Hub:Backup:KeepWeekly 不能是負數：" + settings.KeepWeekly.ToString(CultureInfo.InvariantCulture);
        if (settings.MaxTotalBytes <= 0)
            return "Hub:Backup:MaxTotalBytes 必須大於 0：" + settings.MaxTotalBytes.ToString(CultureInfo.InvariantCulture);
        if (settings.MinFreeBytes < 0)
            return "Hub:Backup:MinFreeBytes 不能是負數：" + settings.MinFreeBytes.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>最後一次成功的備份超過 36 小時＝告警</summary>
    public static bool IsStale(long? lastSuccessAt, long now, long startedAt) =>
        lastSuccessAt is { } last ? now - last > 36 * 3_600_000L : now - startedAt > 36 * 3_600_000L;

    /// <summary>名稱看得懂的資料夾，新的在前（排序同 <see cref="SelectExpired"/>）</summary>
    private static List<BackupFolderSize> NewestFirst(IEnumerable<BackupFolderSize> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return folders
            .Select(f => (Folder: f, At: ParseFolder(f.Name)))
            .Where(x => x.At is not null)
            .OrderByDescending(x => x.At)
            .ThenByDescending(x => x.Folder.Name, StringComparer.Ordinal)
            .Select(x => x.Folder)
            .ToList();
    }
}

/// <summary>一個備份資料夾與它的大小（位元組）</summary>
internal readonly record struct BackupFolderSize(string Name, long Bytes);

/// <summary>備份之前的空間計畫：<paramref name="Fits"/>＝刪掉 <paramref name="Delete"/> 之後放得下</summary>
internal sealed record BackupSpacePlan(bool Fits, IReadOnlyList<string> Delete);
