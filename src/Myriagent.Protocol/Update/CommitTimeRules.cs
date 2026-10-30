using System.Globalization;
using System.Reflection;

namespace Myriagent.Update;

/// <summary>
/// 版號前面的日期時間：建置時寫進組件中繼資料的提交時間（Directory.Build.targets 的 MyriagentStampCommitTime：HEAD 的 committer date，ISO 8601）。
/// 桌面（init.app、AppUpdateView）與 MyriagentHub（瀏覽器版的 init.app）都以 commitAt（Unix 毫秒）送給網頁，網頁以使用者的當地時間顯示
/// </summary>
internal static class CommitTimeRules
{
    public const string MetadataKey = "MyriagentCommitTime";

    /// <summary>組件的 MyriagentCommitTime；沒有（git archive 的建置、沒有 git）或格式不對回 null</summary>
    public static DateTimeOffset? FromAssembly(Assembly assembly)
        => Parse(assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value);

    /// <summary>只收 git 的 %cI 格式（「2026-10-23T14:05:12+08:00」或結尾 Z）；其他回 null</summary>
    public static DateTimeOffset? Parse(string? text)
    {
        var s = text?.Trim();
        // K 也收沒有時區的寫法（當成這台的當地時間）：先要求結尾是 Z 或 ±hh:mm
        if (s is not { Length: 20 or 25 } || !(s[^1] == 'Z' || s[^6] is '+' or '-'))
            return null;
        return DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    }

    /// <summary>給網頁的 commitAt：Unix 毫秒；沒有時間回 null</summary>
    public static long? ToUnixMs(DateTimeOffset? time) => time?.ToUnixTimeMilliseconds();
}
