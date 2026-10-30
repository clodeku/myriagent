using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Myriagent.Cli;

/// <summary>CLI 版本字串的解析與比較（「2.1.295 (Claude Code)」「codex-cli 0.160.1」）</summary>
internal static partial class CliVersionRules
{
    /// <summary>從 --version 輸出取版本號（第一個 x.y.z，可帶 -prerelease）；沒有回 null</summary>
    public static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        var m = VersionPattern().Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>比較兩個版本：a&lt;b 回負數；無法解析的視為最小</summary>
    public static int Compare(string? a, string? b)
    {
        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        for (int i = 0; i < Math.Max(na.Count, nb.Count); i++)
        {
            int x = i < na.Count ? na[i] : 0;
            int y = i < nb.Count ? nb[i] : 0;
            if (x != y)
                return x.CompareTo(y);
        }
        // 正式版大於同版號的預發布版（1.0.0 > 1.0.0-beta）
        if (pa == null && pb != null)
            return 1;
        if (pa != null && pb == null)
            return -1;
        return string.CompareOrdinal(pa ?? string.Empty, pb ?? string.Empty);
    }

    /// <summary>latest 比 current 新（任一無法解析回 false）</summary>
    public static bool IsNewer(string? latest, string? current)
        => ParseVersion(latest) != null && ParseVersion(current) != null && Compare(ParseVersion(latest), ParseVersion(current)) > 0;

    /// <summary>版本的 x.y.z 部分（去掉預發布字尾）；無法解析回 null</summary>
    public static string? CoreVersion(string? version)
    {
        var v = ParseVersion(version);
        if (v == null)
            return null;
        var dash = v.IndexOf('-');
        return dash >= 0 ? v[..dash] : v;
    }

    /// <summary>
    /// 兩個版本來源是不是同一版（安裝穩定判斷的「版本來源一致」）。coreOnly＝其中一方來自 Windows 檔案版本資訊
    /// （只有四段數字「2.1.296.0」，表達不了預發布字尾），只比 x.y.z；否則連預發布字尾一起比（不分大小寫）。任一方無法解析回 false
    /// </summary>
    public static bool SameRelease(string? a, string? b, bool coreOnly = false)
    {
        var x = coreOnly ? CoreVersion(a) : ParseVersion(a);
        var y = coreOnly ? CoreVersion(b) : ParseVersion(b);
        return x != null && y != null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// package.json 與 vendor\…\codex-package.json 的 version 欄位 → 版本；不是 JSON、沒有 version 或格式不符回 null。
    /// platformPackage＝npm 平台套件（@openai/codex-win32-x64）的 package.json：版本帶平台字尾，要先去掉（StripPlatformSuffix）
    /// </summary>
    public static string? ParsePackageJsonVersion(string? json, bool platformPackage = false)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var raw = JToken.Parse(json) is JObject o && o["version"] is JValue { Type: JTokenType.String } v ? (string?)v : null;
            return platformPackage ? StripPlatformSuffix(raw) : ParseVersion(raw);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// npm 平台套件的版本帶平台字尾（@openai/codex-win32-x64 的 package.json 是「0.160.1-win32-x64」）：去掉字尾才是 CLI 的版本；
    /// 沒有平台字尾的照 ParseVersion 解析（真正的預發布版本「0.163.0-alpha.3」不動）
    /// </summary>
    public static string? StripPlatformSuffix(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var text = version.Trim();
        var m = PlatformSuffixPattern().Match(text);
        return ParseVersion(m.Success ? text[..m.Index] : text);
    }

    /// <summary>
    /// Codex app-server 的 initialize 回應的 userAgent（「&lt;clientInfo.name&gt;/&lt;Codex 版本&gt; (Windows 10.0.26300; x86_64) …」）→ Codex 版本；
    /// 格式不符回 null。執行中的對話靠它知道自己的程序跑的是哪一版（IAgentSession.RunningVersion）
    /// </summary>
    public static string? ParseCodexUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;
        var m = UserAgentPattern().Match(userAgent.Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>npm registry 的 /{套件}/latest 回應 → 版本號</summary>
    /// <summary>agy 官方 manifest（{"version":"1.3.5","url":…,"sha512":…}）的版本：與 npm registry 的 latest 同樣取 version 欄位</summary>
    public static string? ParseAntigravityManifest(string? json) => ParseNpmLatest(json);

    public static string? ParseNpmLatest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return ParseVersion(JObject.Parse(json).Value<string>("version"));
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private static (List<int> Numbers, string? Prerelease) Split(string? version)
    {
        var v = ParseVersion(version);
        if (v == null)
            return (new List<int>(), null);
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..] : null;
        var numbers = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
        return (numbers, pre);
    }

    [GeneratedRegex(@"(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)")]
    private static partial Regex VersionPattern();

    /// <summary>userAgent 開頭的「名稱/版本」：名稱不含空白與斜線，版本緊接在第一個斜線之後</summary>
    [GeneratedRegex(@"^[^\s/]+/(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)(?=$|[\s(;])")]
    private static partial Regex UserAgentPattern();

    /// <summary>npm 平台套件版本的平台字尾（-win32-x64、-linux-arm64…，可能還接 -musl）</summary>
    [GeneratedRegex(@"-(?:win32|linux|darwin)-(?:x64|arm64)(?:-musl)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformSuffixPattern();
}
