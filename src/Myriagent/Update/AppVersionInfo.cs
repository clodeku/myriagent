using System.Reflection;

namespace Myriagent.Update;

/// <summary>目前執行的 app 是哪一版（組件的 InformationalVersion；設計 6.1）：介面、日誌、User-Agent、Codex clientInfo、更新判斷都用它</summary>
internal static class AppVersionInfo
{
    private static readonly Lazy<ReleaseIdentity> current = new(() =>
    {
        var assembly = typeof(AppVersionInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ReleaseIdentity.FromInformationalVersion(informational, assembly.GetName().Version);
    });

    public static ReleaseIdentity Current => current.Value;

    public static string VersionText => Current.Version.ToString();

    public static string Display => Current.IsDev ? $"{VersionText}（開發版）" : $"{VersionText}（{Current.Build}）";

    private static readonly Lazy<DateTimeOffset?> commitTime = new(() => CommitTimeRules.FromAssembly(typeof(AppVersionInfo).Assembly));

    /// <summary>這一版的提交時間（網頁在版號前面顯示的日期時間）；建置時沒有 git 是 null</summary>
    public static DateTimeOffset? CommitTime => commitTime.Value;

    /// <summary>給網頁的 commitAt（init.app、AppUpdateView）：Unix 毫秒或 null</summary>
    public static long? CommitAtMs => CommitTimeRules.ToUnixMs(CommitTime);
}
