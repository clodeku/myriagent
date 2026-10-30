using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Myriagent.Health;

/// <summary>指示檔的量測結果（CLI 實際送進脈絡的內容：HTML 註解已去掉）</summary>
/// <param name="Bytes">檔案大小（UTF-8 位元組，原檔）</param>
/// <param name="Chars">字元數（去掉 HTML 註解之後；代理對算一個）</param>
/// <param name="Lines">行數（去掉 HTML 註解之後）</param>
/// <param name="Tokens">粗估的 token 數（<see cref="HealthTextRules.EstimateTokens"/>）</param>
internal sealed record TextMeasure(long Bytes, int Chars, int Lines, int Tokens)
{
    public static TextMeasure Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// 健康度分析用的純文字規則：粗估 token、去掉 CLI 不送的 HTML 註解、找出 CLAUDE.md 的 <c>@路徑</c> 匯入、讀 YAML 前置資料（skill 的描述、規則的 paths）。
/// 只做近似：不跑各家的 tokenizer，CJK 字元一字算一個 token、其他字元四個算一個
/// </summary>
internal static class HealthTextRules
{
    /// <summary>CJK 與全形字元的起點（CJK 部首補充 U+2E80；以下的拉丁、符號、標點四字元一個 token）</summary>
    private const int WideStart = 0x2E80;

    /// <summary>粗估 token 數：CJK（U+2E80 以上、含代理對）每字 1 個，其他每 4 字元 1 個（無條件進位）</summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        int wide = 0, narrow = 0;
        foreach (var ch in text)
        {
            if (char.IsLowSurrogate(ch))
                continue;
            if (ch >= WideStart)
                wide++;
            else
                narrow++;
        }
        return wide + (narrow + 3) / 4;
    }

    /// <summary>字元數（代理對算一個字）</summary>
    public static int CountChars(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var n = 0;
        foreach (var ch in text)
        {
            if (!char.IsLowSurrogate(ch))
                n++;
        }
        return n;
    }

    /// <summary>行數：空字串 0 行；最後一行沒有換行也算一行</summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var n = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                n++;
        }
        return text[^1] == '\n' ? n : n + 1;
    }

    /// <summary>量測一份指示：原檔位元組數另外給（讀檔時就知道），其他以去掉 HTML 註解之後的內容計</summary>
    public static TextMeasure Measure(string text, long bytes)
    {
        var sent = StripHtmlComments(text);
        return new TextMeasure(bytes, CountChars(sent), CountLines(sent), EstimateTokens(sent));
    }

    /// <summary>
    /// 去掉程式碼區塊以外的 HTML 註解（Claude Code 送進脈絡之前會去掉，維護者的備註不花 token）。
    /// 換行一律當 \n；沒有結尾的註解視為到檔尾
    /// </summary>
    public static string StripHtmlComments(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("<!--", StringComparison.Ordinal))
            return text ?? string.Empty;
        var lines = new List<string>();
        string? fence = null;
        var inComment = false;
        foreach (var line in SplitLines(text))
        {
            if (!inComment && FenceMarker(line) is { } marker)
            {
                if (fence == null)
                    fence = marker;
                else if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal))
                    fence = null;
                lines.Add(line);
                continue;
            }
            if (fence != null)
            {
                lines.Add(line);
                continue;
            }
            var kept = new StringBuilder();
            var i = 0;
            while (i < line.Length)
            {
                if (inComment)
                {
                    var end = line.IndexOf("-->", i, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        i = line.Length;
                        break;
                    }
                    inComment = false;
                    i = end + 3;
                }
                else
                {
                    var start = line.IndexOf("<!--", i, StringComparison.Ordinal);
                    if (start < 0)
                    {
                        kept.Append(line, i, line.Length - i);
                        break;
                    }
                    kept.Append(line, i, start - i);
                    inComment = true;
                    i = start + 4;
                }
            }
            // 整行都是註解（或註解跨行的中間）就整行拿掉，不留空行
            if (kept.Length > 0 || (!inComment && line.Length == 0))
                lines.Add(kept.ToString());
        }
        return string.Join('\n', lines);
    }

    /// <summary>空白或括號之後的 @路徑；路徑遇到空白、引號、括號、中文標點或下一個 @ 就停</summary>
    private static readonly Regex ImportPattern = new(@"(?<![^\s(（])@(?<p>[^\s`'""<>()（）、，。；：！？「」@]+)", RegexOptions.Compiled);

    /// <summary>
    /// CLAUDE.md 的 <c>@路徑</c> 匯入（程式碼區塊與行內程式碼裡的不算）：回傳原樣的路徑字串，去掉結尾的標點。
    /// 只挑「看起來是路徑」的（含 / \ . 或 ~ 開頭）；是不是真的檔案由呼叫端解析後判斷，誤認的不存在就不算
    /// </summary>
    public static IReadOnlyList<string> FindImports(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text) || !text.Contains('@'))
            return result;
        string? fence = null;
        foreach (var line in SplitLines(StripHtmlComments(text)))
        {
            if (FenceMarker(line) is { } marker)
            {
                if (fence == null)
                    fence = marker;
                else if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal))
                    fence = null;
                continue;
            }
            if (fence != null || !line.Contains('@'))
                continue;
            foreach (Match m in ImportPattern.Matches(RemoveInlineCode(line)))
            {
                var path = m.Groups["p"].Value.TrimEnd('.', ',', ';', ':', '!', '?', '。', '，', '；', '：', '、', '）', ')', ']', '」');
                if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal))
                    continue;
                if (path[0] == '~' || path.Contains('/') || path.Contains('\\') || path.Contains('.'))
                {
                    if (!result.Contains(path, StringComparer.Ordinal))
                        result.Add(path);
                }
            }
        }
        return result;
    }

    /// <summary>匯入路徑的實際位置：<c>~</c> 開頭以使用者資料夾、相對路徑以匯入它的檔案所在資料夾為基準；格式不對回 null</summary>
    public static string? ResolveImport(string importPath, string containingFile, string userProfile)
    {
        try
        {
            string path;
            if (importPath == "~")
                return null;
            if (importPath.StartsWith("~/", StringComparison.Ordinal) || importPath.StartsWith("~\\", StringComparison.Ordinal))
                path = Path.Combine(userProfile, importPath[2..]);
            else if (Path.IsPathFullyQualified(importPath))
                path = importPath;
            else
                path = Path.Combine(Path.GetDirectoryName(containingFile) ?? string.Empty, importPath);
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// YAML 前置資料（檔頭 <c>---</c> 到下一個 <c>---</c>）的最上層鍵與值：縮排的續行與清單併進前一個鍵（以 \n 連接）。
    /// 沒有前置資料回空的；只做 skill、規則用得到的簡單形狀，不是完整的 YAML
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseFrontMatter(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
            return result;
        var lines = SplitLines(text.TrimStart((char)0xFEFF)).ToList();
        if (lines.Count == 0 || lines[0].TrimEnd() != "---")
            return result;
        string? key = null;
        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.TrimEnd() == "---")
                return result;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '-' && line[0] != '#')
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    key = line[..colon].Trim();
                    result[key] = line[(colon + 1)..].Trim();
                    continue;
                }
            }
            if (key != null && line.Trim().Length > 0)
                result[key] = result[key].Length == 0 ? line.Trim() : result[key] + "\n" + line.Trim();
        }
        // 沒有結尾的 --- 就不是前置資料
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>前置資料的文字值：去掉區塊標記（<c>&gt;</c>、<c>|</c>）與外層引號，續行以空白連接</summary>
    public static string FrontMatterText(IReadOnlyDictionary<string, string> frontMatter, string key)
    {
        if (!frontMatter.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var parts = value.Split('\n').Select(p => p.Trim()).ToList();
        if (parts.Count > 0 && parts[0] is ">" or "|" or ">-" or "|-" or ">+" or "|+")
            parts.RemoveAt(0);
        var joined = string.Join(" ", parts.Where(p => p.Length > 0)).Trim();
        if (joined.Length >= 2 && ((joined[0] == '"' && joined[^1] == '"') || (joined[0] == '\'' && joined[^1] == '\'')))
            joined = joined[1..^1];
        return joined.Trim();
    }

    /// <summary>規則檔有沒有 <c>paths</c>（只在碰到相符的檔案時才載入）；沒有的每次都載入</summary>
    public static bool HasPathScope(IReadOnlyDictionary<string, string> frontMatter)
        => frontMatter.TryGetValue("paths", out var paths) && !string.IsNullOrWhiteSpace(paths) && paths.Trim() != "[]";

    /// <summary>Antigravity 規則檔是否為按需觸發（trigger 為 model_decision／glob／manual，或指定了 globs／paths；預設與 always_on 每次都載入）</summary>
    public static bool IsAntigravityScopedRule(IReadOnlyDictionary<string, string> frontMatter)
    {
        if (HasPathScope(frontMatter))
            return true;
        if (frontMatter.TryGetValue("globs", out var globs) && !string.IsNullOrWhiteSpace(globs) && globs.Trim() != "[]")
            return true;
        if (frontMatter.TryGetValue("glob", out var glob) && !string.IsNullOrWhiteSpace(glob) && glob.Trim() != "[]")
            return true;
        var trigger = FrontMatterText(frontMatter, "trigger");
        return trigger.Length > 0 && !trigger.Equals("always_on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>skill 在清單裡的一筆的字元數：名稱＋描述與 when_to_use（描述部分最多 <paramref name="descriptionCap"/> 字元）</summary>
    public static int SkillListingChars(string name, IReadOnlyDictionary<string, string> frontMatter, int descriptionCap)
    {
        var description = FrontMatterText(frontMatter, "description");
        var whenToUse = FrontMatterText(frontMatter, "when_to_use");
        var text = whenToUse.Length > 0 ? description + " " + whenToUse : description;
        return CountChars(name) + Math.Min(CountChars(text), descriptionCap);
    }

    /// <summary>token 數的顯示：「約 850 token」「約 3,200 token」「約 1.3 萬 token」</summary>
    public static string FormatTokens(int tokens)
        => tokens < 10_000
            ? "約 " + tokens.ToString("N0", CultureInfo.InvariantCulture) + " token"
            : "約 " + (tokens / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " 萬 token";

    /// <summary>位元組的顯示：「820 B」「24.6 KB」「3.1 MB」</summary>
    public static string FormatBytes(long bytes)
        => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
            : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
            : (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    /// <summary>數字加千分位</summary>
    public static string FormatCount(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>以 \n 切行（\r\n 的 \r 去掉）</summary>
    internal static IEnumerable<string> SplitLines(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            yield return line;
    }

    /// <summary>程式碼區塊的圍欄（<c>```</c> 或 <c>~~~</c>，可縮排三格以內）；不是圍欄回 null</summary>
    internal static string? FenceMarker(string line)
    {
        var t = line.TrimStart();
        if (line.Length - t.Length > 3)
            return null;
        if (t.StartsWith("```", StringComparison.Ordinal))
            return "```";
        if (t.StartsWith("~~~", StringComparison.Ordinal))
            return "~~~";
        return null;
    }

    /// <summary>行內程式碼（反引號包起來的）換成空白</summary>
    private static string RemoveInlineCode(string line)
    {
        if (!line.Contains('`'))
            return line;
        var sb = new StringBuilder(line.Length);
        var inCode = false;
        foreach (var ch in line)
        {
            if (ch == '`')
            {
                inCode = !inCode;
                sb.Append(' ');
                continue;
            }
            sb.Append(inCode ? ' ' : ch);
        }
        return sb.ToString();
    }
}
