using System.Diagnostics;

namespace MyriagentHub.ProbeClient;

/// <summary>
/// 子程序（MyriagentHub、FakeDevice）的環境變數：不繼承本 session 的 Claude／agy 工作階段、各代理的設定與金鑰、同步覆寫、ASP.NET Core 環境，
/// 與 GC 設定（<c>DOTNET_GC*</c> 與整個 <c>COMPlus_</c> 前綴：.NET 10 照樣採用 <c>COMPlus_gcServer</c> 等，會蓋掉 MyriagentHub 的 runtimeconfig，
/// 讓 load 的記憶體判定失真；WP4.9 合併前審查 F3）。清單和 hub-e2e 的 lib/child-env.mjs、measure_sync.py 的 STRIP_PREFIXES 完全相同
/// （FleetRulesTests 與 test_measure_sync 比對）。要給子程序的值（擁有者密碼的環境變數）剝掉之後再明確放回
/// </summary>
internal static class ChildEnvironment
{
    /// <summary>不繼承、名稱以這些開頭的環境變數（不分大小寫；和 child-env.mjs 的 STRIP_PREFIXES 相同）</summary>
    public static readonly IReadOnlyList<string> StripPrefixes =
        ["CLAUDE", "ANTHROPIC_", "OPENAI_", "ANTIGRAVITY_", "GROK_", "XAI_", "MYRIAGENT_", "CODEX_", "ASPNETCORE_", "DOTNET_ENVIRONMENT", "DOTNET_GC", "COMPLUS_"];

    public static bool IsStripped(string name) => StripPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>剝掉 <paramref name="info"/> 繼承來的環境變數，再放上 <paramref name="extra"/></summary>
    public static void Apply(ProcessStartInfo info, IReadOnlyDictionary<string, string>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        foreach (var key in info.Environment.Keys.Where(IsStripped).ToList())
            info.Environment.Remove(key);
        if (extra is null)
            return;
        foreach (var (key, value) in extra)
            info.Environment[key] = value;
    }
}
