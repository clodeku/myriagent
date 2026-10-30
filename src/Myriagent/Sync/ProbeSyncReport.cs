using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 實機探針（ProbeRunner）report.json 的 sync 段：配對與連線狀態、待送筆數、載入了哪些同步才該用的組件（S6）、app 程序的私有記憶體。
/// 未配對時 loadedForbidden 應該是空的；已配對時會列出 SignalR、Sqlite 與 e_sqlite3.dll（量測用，不是錯誤）
/// </summary>
internal static class ProbeSyncReport
{
    /// <summary>未配對時不該載入的組件前綴（和測試的 AssemblyIsolationScanner 相同）</summary>
    public static readonly IReadOnlyList<string> ForbiddenPrefixes =
        ["Microsoft.AspNetCore.", "Microsoft.Extensions.", "Microsoft.Data.Sqlite", "SQLitePCLRaw"];

    /// <summary>SQLite 的原生模組</summary>
    public const string ForbiddenNativeModule = "e_sqlite3.dll";

    public static bool IsForbiddenAssembly(string name) =>
        ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <param name="loadedAssemblyNames">已載入的組件簡單名稱</param>
    /// <param name="nativeModules">程序載入的原生模組檔名（e_sqlite3.dll 用）</param>
    /// <param name="notOnline">已配對、但等 30 秒仍沒連上（記錄量測時的狀態）</param>
    /// <param name="heartbeatsDuringSample">已配對而且連上時，閒置 CPU 取樣期間送出的心跳數（0＝取樣窗裡沒有週期性的同步工作，量測無效）；其他情況 null（不寫）</param>
    public static JObject Build(IEnumerable<string> loadedAssemblyNames, IEnumerable<string> nativeModules, SyncStatusSnapshot? status, bool paired,
        double? appPrivateMb, bool notOnline, long? heartbeatsDuringSample = null)
    {
        var forbidden = loadedAssemblyNames.Where(n => !string.IsNullOrEmpty(n) && IsForbiddenAssembly(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (nativeModules.Any(m => string.Equals(m, ForbiddenNativeModule, StringComparison.OrdinalIgnoreCase)))
            forbidden.Add(ForbiddenNativeModule);
        var report = new JObject
        {
            ["paired"] = paired,
            ["state"] = status is null ? null : SyncStatusRules.WebState(status.State, status.Pairing is not null),
            ["pending"] = status?.Pending,
            ["loadedForbidden"] = new JArray(forbidden),
            ["appPrivateMb"] = appPrivateMb,
        };
        if (notOnline)
            report["notOnline"] = true;
        if (heartbeatsDuringSample is { } beats)
            report["heartbeatsDuringSample"] = beats;
        return report;
    }
}
