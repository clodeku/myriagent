using System.Text.RegularExpressions;

namespace Myriagent.Schedules;

/// <summary>
/// 排程動作參數裡的秘密（權杖、密碼、API 金鑰、網址裡的帳密）換成「••••」：送出這台之外（多電腦同步的排程清單）之前用。
/// 寧可多遮：遮錯了只是看不到，漏遮會把秘密送上伺服器
/// </summary>
internal static class ScheduleRedactRules
{
    public const string Mask = "••••";

    private static readonly Regex NamedSecret = new(
        @"(?<flag>(?:--?|/)(?:[\w-]*?)(?:token|password|passwd|pwd|secret|api[-_]?key|apikey|access[-_]?key|auth|bearer|credential|cookie|session)[\w-]*)(?<sep>=|:|\s+)(?<value>""[^""]*""|'[^']*'|[^\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AssignedSecret = new(
        @"(?<name>\b[\w-]*(?:token|password|passwd|pwd|secret|api[-_]?key|apikey|access[-_]?key|credential)[\w-]*)(?<sep>\s*=\s*)(?<value>""[^""]*""|'[^']*'|[^\s""';&]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex KnownKey = new(
        @"\b(?:sk-[A-Za-z0-9_-]{16,}|sk-ant-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})",
        RegexOptions.Compiled);

    private static readonly Regex UrlCredentials = new(@"(?<scheme>[a-z][a-z0-9+.-]*://)(?<user>[^:/@\s]+):(?<pass>[^@/\s]+)@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>長的隨機字串（同時有字母與數字、32 字以上、沒有路徑字元）：多半是金鑰</summary>
    private static readonly Regex LongRandom = new(@"(?<![\w\\/.:-])[A-Za-z0-9_+=-]{32,}(?![\w\\/.:-])", RegexOptions.Compiled);

    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var s = UrlCredentials.Replace(text, m => $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:{Mask}@");
        s = NamedSecret.Replace(s, m => m.Groups["flag"].Value + m.Groups["sep"].Value + Mask);
        s = AssignedSecret.Replace(s, m => m.Groups["name"].Value + m.Groups["sep"].Value + Mask);
        s = KnownKey.Replace(s, Mask);
        s = LongRandom.Replace(s, m => m.Value.Any(char.IsDigit) && m.Value.Any(char.IsLetter) ? Mask : m.Value);
        return s;
    }
}
