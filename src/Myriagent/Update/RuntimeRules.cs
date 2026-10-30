namespace Myriagent.Update;

/// <summary>共用執行階段版本的純函式規則（設計 8.4 第 3 步）</summary>
internal static class RuntimeRules
{
    /// <summary>
    /// 已安裝的版本（登錄的值名稱，例如「10.0.2」「10.0.0-rc.2.25502.107」）裡有一個夠新就好；
    /// 預覽版比同號的正式版小，只在要求的版本嚴格小於它的三段數字時才算數
    /// </summary>
    public static bool Satisfies(IEnumerable<string> installed, ReleaseVersion required)
    {
        foreach (var name in installed)
        {
            var dash = name.IndexOf('-');
            var core = dash >= 0 ? name[..dash] : name;
            if (!ReleaseVersion.TryParse(core, out var version))
                continue;
            if (dash < 0 ? version >= required : version > required)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 執行中的程序載入的框架資料夾（Microsoft.NETCore.App\10.0.13、Microsoft.WindowsDesktop.App\10.0.13）有沒有被移除：.NET 更新時安裝程式把舊的修補版
    /// 整個刪掉（2026-10-27 實例：05:34 更新成 10.0.14，04:13 啟動的 app 還在用 10.0.13），之後這個程序第一次用到還沒載入的組件（解壓新版用的
    /// System.IO.Compression.ZipFile）就載入失敗、例外訊息還是空的，app 再也裝不了新版。回被移除的那一個資料夾的名稱（版本）；都還在回 null
    /// </summary>
    public static string? RemovedRuntime(IEnumerable<string?> directories, Func<string, bool> exists)
    {
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            var trimmed = Path.TrimEndingDirectorySeparator(directory);
            if (!exists(trimmed))
                return Path.GetFileName(trimmed);
        }
        return null;
    }

    /// <summary>有新版要裝、執行中的程序的 .NET 卻已被移除時的說明（更新橫幅「新版 X 無法安裝：…」、設定頁）</summary>
    public static string RemovedDetail(string removed)
        => $"這台的 .NET 已更新，app 啟動時用的 {removed} 已被移除，要重新啟動 app 才能安裝（閒置時會自動重新啟動）";
}
