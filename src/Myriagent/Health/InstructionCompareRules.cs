using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Newtonsoft.Json.Linq;
using static Myriagent.Health.HealthTextRules;

namespace Myriagent.Health;

/// <summary>指示裡的一條（一個清單項目、表格的一列、一段文字或一個程式碼區塊）</summary>
/// <param name="Section">所在的標題（沒有標題是空字串）</param>
/// <param name="Text">原文（去掉清單符號，多行以空白連接）</param>
/// <param name="Norm">比對用的正規化文字</param>
internal sealed record InstructionUnit(string Section, string Text, string Norm);

/// <summary>電腦層級指示檔的比較結論</summary>
internal enum InstructionVerdict
{
    /// <summary>這台沒有任何一份有內容</summary>
    None,

    /// <summary>只有一種 CLI 的指示檔，不需要合併</summary>
    Single,

    /// <summary>每一份都涵蓋所有的指示</summary>
    Same,

    /// <summary>有的是空的（拿不到其他 CLI 的指示），要同步</summary>
    NeedsSync,

    /// <summary>都有內容但互有缺漏，建議合併</summary>
    Diverged,
}

/// <summary>一份指示檔的涵蓋情形</summary>
/// <param name="Units">自己的條數</param>
/// <param name="Covered">所有條目（聯集）中這個 CLI 拿得到的（含 Grok 經 Claude 相容一併讀的）</param>
/// <param name="Union">聯集的條數</param>
/// <param name="Missing">拿不到的條目（每條取第一個出現的原文）</param>
/// <param name="Unique">只有這份有的條目</param>
/// <param name="Duplicates">自己有、經相容又讀了一次的條數（Grok 同時讀自己的與 ~/.claude/CLAUDE.md）</param>
internal sealed record DocCoverage(InstructionDoc Doc, int Units, int Covered, int Union, IReadOnlyList<InstructionUnit> Missing, IReadOnlyList<InstructionUnit> Unique, int Duplicates)
{
    public double Coverage => Union == 0 ? 1 : (double)Covered / Union;
}

/// <summary>電腦層級指示檔的比較</summary>
internal sealed record InstructionsReport(InstructionVerdict Verdict, HealthLevel Level, string Summary, IReadOnlyList<DocCoverage> Docs)
{
    /// <summary>可以「用代理合併」：有兩份以上而且還沒一致</summary>
    public bool Mergeable => Verdict is InstructionVerdict.NeedsSync or InstructionVerdict.Diverged;
}

/// <summary>
/// 電腦層級的全域指示（各 CLI 使用者層的 CLAUDE.md、AGENTS.md、GEMINI.md）的比較（純函式）：把每份切成一條條指示，
/// 以正規化文字與字元雙連字的相似度歸成群，算出每份缺哪些、哪些只有一份有，判斷是否需要合併與同步；
/// 另產生「用代理合併」「用代理精簡」「用代理整理」（自動記憶索引）的對話開頭訊息。比對只看文字，某個 CLI 專屬的指示由代理合併時判斷
/// </summary>
internal static partial class InstructionCompareRules
{
    /// <summary>相似度到這個值以上算同一條（字元雙連字的 Dice 係數）</summary>
    public const double FuzzyThreshold = 0.8;

    /// <summary>正規化後比這短的不算一條（「---」、單一個字）</summary>
    public const int MinUnitChars = 4;

    /// <summary>明細最多列幾條</summary>
    public const int MaxListedUnits = 12;

    /// <summary>每份至少涵蓋這個比例才算「大致一致」</summary>
    public const double MostlySameRatio = 0.9;

    [GeneratedRegex(@"^\s{0,3}(?:[-*+]|\d{1,3}[.)])\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(?<t>.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>把一份指示切成一條條（HTML 註解先去掉）</summary>
    public static IReadOnlyList<InstructionUnit> Units(string text)
    {
        var units = new List<InstructionUnit>();
        if (string.IsNullOrWhiteSpace(text))
            return units;
        var section = string.Empty;
        var current = new StringBuilder();
        string? fence = null;

        void Flush()
        {
            if (current.Length == 0)
                return;
            var raw = current.ToString().Trim();
            current.Clear();
            var norm = Normalize(raw);
            if (CountChars(norm) >= MinUnitChars)
                units.Add(new InstructionUnit(section, Spaces().Replace(raw, " "), norm));
        }

        foreach (var line in SplitLines(StripHtmlComments(text)))
        {
            if (fence != null)
            {
                current.Append('\n').Append(line);
                if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                    Flush();
                }
                continue;
            }
            if (FenceMarker(line) is { } marker)
            {
                Flush();
                fence = marker;
                current.Append(line);
                continue;
            }
            if (string.IsNullOrWhiteSpace(line) || ThematicBreak().IsMatch(line))
            {
                Flush();
                continue;
            }
            if (Heading().Match(line) is { Success: true } heading)
            {
                Flush();
                section = heading.Groups["t"].Value.Trim();
                continue;
            }
            var trimmed = line.Trim();
            if (trimmed.StartsWith('|'))
            {
                Flush();
                if (!TableSeparator().IsMatch(line))
                {
                    current.Append(trimmed);
                    Flush();
                }
                continue;
            }
            if (ListMarker().Match(line) is { Success: true } list)
            {
                Flush();
                current.Append(line[list.Length..].Trim());
                continue;
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(trimmed.TrimStart('>').Trim());
        }
        Flush();
        return units;
    }

    /// <summary>比對用的正規化：小寫、去掉 Markdown 符號與多餘空白、去掉結尾標點</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            sb.Append(ch is '*' or '_' or '`' or '#' or '>' or '|' or '\t' or '\r' or '\n' ? ' ' : ch);
        }
        return Spaces().Replace(sb.ToString(), " ").Trim().TrimEnd('.', '。', ';', '；', ':', '：', ',', '，', ' ');
    }

    /// <summary>字元雙連字的 Dice 係數（0～1）</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b)
            return 1;
        var x = Bigrams(a);
        var y = Bigrams(b);
        if (x.Count == 0 || y.Count == 0)
            return 0;
        var common = x.Count(y.Contains);
        return 2.0 * common / (x.Count + y.Count);
    }

    private static HashSet<int> Bigrams(string s)
    {
        var set = new HashSet<int>();
        for (var i = 0; i + 1 < s.Length; i++)
        {
            if (s[i] != ' ' || s[i + 1] != ' ')
                set.Add((s[i] << 16) | s[i + 1]);
        }
        return set;
    }

    private sealed class Cluster(InstructionUnit first, HashSet<int> bigrams)
    {
        public InstructionUnit First { get; } = first;

        public HashSet<int> Bigrams { get; } = bigrams;

        /// <summary>有這條的文件（Key）</summary>
        public HashSet<string> Docs { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>比較電腦層級的指示檔</summary>
    public static InstructionsReport Compare(IReadOnlyList<InstructionDoc> docs)
    {
        if (docs.Count == 0)
            return new InstructionsReport(InstructionVerdict.None, HealthLevel.Ok, "這台沒有可以比較的指示檔。", Array.Empty<DocCoverage>());

        // 內容多的先歸群（群的代表原文取自它）
        var unitsByDoc = docs.ToDictionary(d => d.Key, d => Units(d.Text), StringComparer.Ordinal);
        var clusters = new List<Cluster>();
        var exact = new Dictionary<string, Cluster>(StringComparer.Ordinal);
        foreach (var doc in docs.OrderByDescending(d => unitsByDoc[d.Key].Count))
        {
            foreach (var unit in unitsByDoc[doc.Key])
            {
                if (!exact.TryGetValue(unit.Norm, out var cluster))
                {
                    var grams = Bigrams(unit.Norm);
                    cluster = FindSimilar(clusters, unit.Norm, grams);
                    if (cluster == null)
                    {
                        cluster = new Cluster(unit, grams);
                        clusters.Add(cluster);
                    }
                    exact[unit.Norm] = cluster;
                }
                cluster.Docs.Add(doc.Key);
            }
        }

        var byKey = docs.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var coverages = new List<DocCoverage>();
        foreach (var doc in docs)
        {
            var also = doc.AlsoLoadsKey != null && byKey.ContainsKey(doc.AlsoLoadsKey) ? doc.AlsoLoadsKey : null;
            bool Has(Cluster c) => c.Docs.Contains(doc.Key) || (also != null && c.Docs.Contains(also));
            var missing = clusters.Where(c => !Has(c)).Select(c => c.First).ToList();
            var unique = clusters.Where(c => c.Docs.Count == 1 && c.Docs.Contains(doc.Key)).Select(c => c.First).ToList();
            var duplicates = also == null ? 0 : clusters.Count(c => c.Docs.Contains(doc.Key) && c.Docs.Contains(also));
            coverages.Add(new DocCoverage(doc, unitsByDoc[doc.Key].Count, clusters.Count - missing.Count, clusters.Count, missing, unique, duplicates));
        }

        var withContent = coverages.Count(c => c.Units > 0);
        if (withContent == 0)
            return new InstructionsReport(InstructionVerdict.None, HealthLevel.Ok, "這台的 CLI 都還沒有電腦層級的指示。", coverages);
        if (docs.Count == 1)
            return new InstructionsReport(InstructionVerdict.Single, HealthLevel.Ok, "這台只有一種 CLI 的指示檔，不需要合併。", coverages);
        if (coverages.All(c => c.Missing.Count == 0))
            return new InstructionsReport(InstructionVerdict.Same, HealthLevel.Ok, "各 CLI 的全域指示一致。", coverages);

        // 什麼都拿不到的（Grok 自己沒有、但經 Claude 相容讀得到的不算）
        var empty = coverages.Where(c => c.Covered == 0 && c.Missing.Count > 0).ToList();
        if (empty.Count > 0)
        {
            var names = string.Join("、", empty.Select(c => $"{c.Doc.Label} 的 {Path.GetFileName(c.Doc.Path)}"));
            var summary = $"{names} {(empty.Count == 1 ? "是空的" : "都是空的")}，拿不到其他 CLI 的 {empty.Max(c => c.Missing.Count)} 條指示；建議合併並同步。";
            return new InstructionsReport(InstructionVerdict.NeedsSync, HealthLevel.Warn, summary, coverages);
        }
        var gaps = coverages.Where(c => c.Missing.Count > 0).Select(c => $"{c.Doc.Label} 缺 {c.Missing.Count} 條");
        var mostly = coverages.All(c => c.Coverage >= MostlySameRatio);
        return new InstructionsReport(InstructionVerdict.Diverged, mostly ? HealthLevel.Info : HealthLevel.Warn,
            (mostly ? "大致一致，但" : "各份有分歧：") + string.Join("、", gaps) + "（有些可能是某個 CLI 專屬的）；建議合併。", coverages);
    }

    private static Cluster? FindSimilar(List<Cluster> clusters, string norm, HashSet<int> grams)
    {
        if (grams.Count == 0)
            return null;
        Cluster? best = null;
        var bestScore = FuzzyThreshold;
        foreach (var c in clusters)
        {
            var len = c.First.Norm.Length;
            if (len < norm.Length * 0.6 || len * 0.6 > norm.Length || c.Bigrams.Count == 0)
                continue;
            var common = 0;
            foreach (var g in grams)
            {
                if (c.Bigrams.Contains(g))
                    common++;
            }
            var score = 2.0 * common / (grams.Count + c.Bigrams.Count);
            if (score >= bestScore)
            {
                best = c;
                bestScore = score;
            }
        }
        return best;
    }

    private static string VerdictName(InstructionVerdict verdict) => verdict switch
    {
        InstructionVerdict.None => "none",
        InstructionVerdict.Single => "single",
        InstructionVerdict.Same => "same",
        InstructionVerdict.NeedsSync => "needsSync",
        InstructionVerdict.Diverged => "diverged",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    /// <summary>一份的狀態：missing（檔案不存在）、empty（空的）——都是什麼也拿不到；complete、partial（含自己沒有、經相容讀得到的）</summary>
    private static string Status(DocCoverage c)
        => c.Covered == 0 && c.Units == 0 ? (c.Doc.Exists ? "empty" : "missing") : c.Missing.Count == 0 ? "complete" : "partial";

    /// <summary>InstructionsReport 的 JSON（web-protocol 的 InstructionsView）</summary>
    public static JObject Render(InstructionsReport report)
    {
        var byKey = report.Docs.ToDictionary(c => c.Doc.Key, StringComparer.Ordinal);
        return new JObject
        {
            ["verdict"] = VerdictName(report.Verdict),
            ["level"] = HealthRules.LevelName(report.Level),
            ["summary"] = report.Summary,
            ["mergeable"] = report.Mergeable,
            ["docs"] = new JArray(report.Docs.Select(c => new JObject
            {
                ["key"] = c.Doc.Key,
                ["provider"] = c.Doc.Provider.ToString(),
                ["label"] = c.Doc.Label,
                ["path"] = c.Doc.Path,
                ["target"] = c.Doc.LinkTarget,
                ["exists"] = c.Doc.Exists,
                ["status"] = Status(c),
                ["note"] = !c.Doc.Exists ? "不存在" : c.Units == 0 ? "空的"
                    : FormatCount(c.Doc.Measure.Lines) + " 行・" + FormatTokens(c.Doc.Measure.Tokens),
                ["overLimit"] = c.Doc.MaxBytes is { } max && c.Doc.Measure.Bytes > max,
                ["units"] = c.Units,
                ["covered"] = c.Covered,
                ["union"] = c.Union,
                ["coverage"] = Math.Round(c.Coverage, 3),
                ["missingCount"] = c.Missing.Count,
                ["missing"] = new JArray(c.Missing.Take(MaxListedUnits).Select(RenderUnit)),
                ["uniqueCount"] = c.Unique.Count,
                ["unique"] = new JArray(c.Unique.Take(MaxListedUnits).Select(RenderUnit)),
                ["alsoLoads"] = c.Doc.AlsoLoadsKey != null && byKey.TryGetValue(c.Doc.AlsoLoadsKey, out var other) ? other.Doc.Label : null,
                ["duplicates"] = c.Duplicates,
                ["accounts"] = new JArray(c.Doc.AccountIds),
            })),
        };
    }

    private static JObject RenderUnit(InstructionUnit u) => new()
    {
        ["section"] = u.Section,
        ["text"] = Clip(u.Text, 120),
    };

    private static string Clip(string text, int max)
    {
        var t = text.Replace('\n', ' ');
        if (CountChars(t) <= max)
            return t;
        var sb = new StringBuilder();
        var n = 0;
        foreach (var ch in t)
        {
            if (!char.IsLowSurrogate(ch) && ++n > max)
                break;
            sb.Append(ch);
        }
        return sb.Append('…').ToString();
    }

    private static string DocLine(InstructionDoc d)
    {
        var sb = new StringBuilder();
        sb.Append(d.Label).Append('：').Append(d.Path);
        if (d.LinkTarget != null)
            sb.Append("（符號連結，實際檔案是 ").Append(d.LinkTarget).Append('）');
        sb.Append(" — ");
        sb.Append(!d.Exists ? "不存在" : string.IsNullOrWhiteSpace(d.Text) ? "空的"
            : FormatCount(d.Measure.Lines) + " 行、" + FormatBytes(d.Measure.Bytes) + "、" + FormatTokens(d.Measure.Tokens));
        return sb.ToString();
    }

    /// <summary>「用代理合併」的對話開頭訊息：列出各份的位置與分析結果，約定合併的做法（先給計畫、確認後才寫、寫前備份）</summary>
    public static string BuildMergePrompt(InstructionsReport report, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append("請幫我合併並同步這台電腦各個 AI CLI 的全域指示檔（使用者層、每次對話都會載入的指示）。\n\n");
        sb.Append("檔案：\n");
        var i = 0;
        foreach (var c in report.Docs)
            sb.Append(++i).Append(". ").Append(DocLine(c.Doc)).Append('\n');
        sb.Append("\nClode Myriagent 的分析：").Append(report.Summary).Append('\n');
        foreach (var c in report.Docs.Where(c => c.Missing.Count > 0))
        {
            var sections = c.Missing.Select(u => u.Section).Where(s => s.Length > 0).Distinct().Take(8).ToList();
            sb.Append("- ").Append(c.Doc.Label).Append(" 缺 ").Append(c.Missing.Count).Append(" 條");
            if (sections.Count > 0)
                sb.Append("（在這些段落：").Append(string.Join("、", sections)).Append('）');
            sb.Append('\n');
        }
        foreach (var c in report.Docs.Where(c => c.Doc.AlsoLoadsKey != null))
        {
            var other = report.Docs.FirstOrDefault(o => o.Doc.Key == c.Doc.AlsoLoadsKey);
            if (other != null)
                sb.Append("- ").Append(c.Doc.Label).Append(" 會一併讀取 ").Append(other.Doc.Path)
                    .Append("（Claude 相容）：共通的指示不必再寫進它自己的檔，寫了會重複載入").Append(c.Duplicates > 0 ? $"（目前重複 {c.Duplicates} 條）" : string.Empty).Append('\n');
        }
        var stamp = now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        sb.Append("\n請照這個流程做：\n");
        sb.Append("1. 先完整讀過每一份檔案。符號連結要改它指向的實際檔案，不要把連結換成一般檔案。\n");
        sb.Append("2. 把內容分成「共通的指示」（語言、溝通方式、開發與部署習慣等，每個 CLI 都適用）與「某個 CLI 專屬的」（例如 Claude Code 的工具、settings.json、hooks、skills；Codex 的 config.toml、沙箱；Grok、Antigravity 各自的設定）。\n");
        sb.Append("3. 共通的指示在每一份都要有、文字一致；專屬的只留在對應的檔。不要改寫原意，也不要刪掉仍然有效的指示——只有重複的才併成一條。\n");
        sb.Append("4. 合併後每份都盡量精簡：Claude 建議每份 200 行以內，Antigravity 的 GEMINI.md 單檔上限 24,000 位元組；只在特定情境需要的細節可以搬到另外的文件，指示檔只留一行索引。\n");
        sb.Append("5. 有互相矛盾的指示時先列出來問我，不要自己選。\n");
        sb.Append("6. 先把合併計畫（每份檔案會新增、刪除、改寫哪些段落，合併後的行數與大小）給我看，等我確認才寫入；寫入前把每份原檔備份成同一個資料夾的「<檔名>.bak-").Append(stamp).Append("」。\n");
        sb.Append("7. 寫完之後列出每份的行數與大小，並確認共通的段落已經一致。\n");
        sb.Append("\n之後改共通的指示時，同一則修改要套用到每一份檔案，讓它們保持同步。");
        return sb.ToString();
    }

    /// <summary>「用代理精簡」的對話開頭訊息：一個帳戶每次都載入的指示檔與分析結果</summary>
    public static string BuildTrimPrompt(AccountScan scan, AccountHealth health, DateTime now)
    {
        var item = health.Items.First(i => i.Key == "instructions");
        var provider = scan.Account.Provider;
        var sb = new StringBuilder();
        sb.Append("請幫我精簡「").Append(scan.Account.Label).Append("」（").Append(AgentProviders.ProductName(provider)).Append("）每次對話都會載入的全域指示。\n\n");
        sb.Append("檔案：\n");
        var i = 0;
        foreach (var f in scan.Instructions)
        {
            sb.Append(++i).Append(". ").Append(f.Path);
            if (f.LinkTarget != null)
                sb.Append("（符號連結，實際檔案是 ").Append(f.LinkTarget).Append('）');
            sb.Append(" — ").Append(FormatCount(f.Measure.Lines)).Append(" 行、").Append(FormatBytes(f.Measure.Bytes)).Append('、').Append(FormatTokens(f.Measure.Tokens)).Append('\n');
        }
        sb.Append("\nClode Myriagent 的分析：").Append(item.Value).Append("。\n").Append(item.Detail).Append('\n');
        var stamp = now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        sb.Append("\n請照這個流程做：\n");
        sb.Append("1. 讀完上面的檔案（符號連結要改它指向的實際檔案），標出：可以從程式碼或其他文件推得的、過時的、重複的、只在特定專案或特定情境才需要的。\n");
        sb.Append(provider == AgentProvider.Claude
            ? "2. 只在特定情境需要的細節搬到另外的文件（例如 ~/.claude/docs/<主題>.md），指示檔只留一行「情境 → 檔案」的索引；或改成 ~/.claude/rules/ 裡有 paths 的規則，碰到相符的檔案才載入。@ 匯入不會減少載入量。\n"
            : "2. 只在特定情境需要的細節搬到另外的文件，指示檔只留一行「情境 → 檔案」的索引。\n");
        sb.Append("3. 仍然有效的規則一條都不要刪；拿不準的先問我。\n");
        sb.Append("4. 先給我精簡計畫（刪、搬、改寫哪些段落，精簡後的行數與大小），等我確認才寫入；寫入前把原檔備份成同一個資料夾的「<檔名>.bak-").Append(stamp).Append("」。\n");
        sb.Append("\n目標：每份 200 行以內、每次載入的合計約 ").Append(FormatCount(HealthRules.InstructionWarnTokens)).Append(" token 以下。");
        return sb.ToString();
    }

    /// <summary>整理後每個記憶索引的目標行數（上限 200 行，留空間給之後的記憶）</summary>
    public const int MemoryTargetLines = 150;

    /// <summary>整理後每個記憶索引的目標大小（位元組；上限 25KB）</summary>
    public const long MemoryTargetBytes = 20_000;

    /// <summary>
    /// 「用代理整理」的對話開頭訊息：一個 Claude 帳戶超過或接近載入上限的自動記憶索引（MEMORY.md），
    /// 約定整理的做法（索引一行一條、細節搬到主題檔、合併重複、過時的查證後才刪、先給計畫、寫前備份整個 memory 資料夾）
    /// </summary>
    public static string BuildMemoryPrompt(AccountScan scan, DateTime now)
    {
        var targets = (scan.MemoryIndexes ?? new List<MemoryIndexStat>()).Where(HealthRules.MemoryNeedsTrim)
            .OrderByDescending(HealthRules.MemoryOverLimit).ThenByDescending(m => m.Bytes).ToList();
        if (targets.Count == 0)
            throw new ArgumentException("沒有超過或接近上限的記憶索引", nameof(scan));
        var sb = new StringBuilder();
        sb.Append("請幫我整理「").Append(scan.Account.Label).Append("」（").Append(AgentProviders.ProductName(scan.Account.Provider)).Append("）的自動記憶索引：下面")
            .Append(targets.Count == 1 ? "這個專案" : "這些專案").Append("的 MEMORY.md 超過或接近載入上限。\n\n");
        sb.Append("Claude Code 每次對話開始只載入每個專案 MEMORY.md 的前 ").Append(HealthRules.MemoryIndexMaxLines)
            .Append(" 行或 ").Append(HealthRules.MemoryIndexMaxBytes / 1000).Append("KB（先到者為準），超過的部分不會進入對話——後面的記憶等於沒記。");
        if (scan.Account.SharedSourceDirectory != null)
            sb.Append("這個帳戶共用系統預設帳戶的記憶，下面是那邊的檔案。");
        sb.Append("\n\n檔案：\n");
        var i = 0;
        foreach (var m in targets)
        {
            sb.Append(++i).Append(". ").Append(m.Project).Append('：').Append(m.Path).Append(" — ")
                .Append(FormatCount(m.Lines)).Append(" 行、").Append(FormatBytes(m.Bytes))
                .Append(HealthRules.MemoryOverLimit(m) ? "（超過上限）" : "（接近上限）").Append('\n');
        }
        var stamp = now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        sb.Append("\n請照這個流程做（每個專案分開整理）：\n");
        sb.Append("1. 讀完 MEMORY.md 與同一個 memory 資料夾裡的主題檔。索引的每一條只該是一行指標：「- [標題](主題檔.md) — 一句話說明什麼情境用得到」，內容寫在主題檔裡。\n");
        sb.Append("2. 索引裡直接寫了細節的條目：細節搬進主題檔（已有相關的主題檔就併進去，沒有就新建，照既有主題檔的格式，例如開頭的 frontmatter），索引只留一行。\n");
        sb.Append("3. 重複或講同一件事的條目合併成一條。已經過時、做完了、或和現況不符的才刪（主題檔一併處理）：專案資料夾的名稱是工作資料夾的路徑把英數字以外的字元都換成「-」")
            .Append(@"（例如 C:\repo\my.app 是 C--repo-my-app），拿不準時到那個資料夾查證。仍然有效的記憶一條都不要刪，拿不準的先問我。").Append('\n');
        sb.Append("4. 索引連到不存在的檔、主題檔沒有被索引連到的，列出來讓我決定。\n");
        sb.Append("5. 先給我整理計畫（每個專案：搬、併、刪哪些條目，整理後索引的行數與大小），等我確認才寫入；寫入前把整個 memory 資料夾複製成同一層的「memory.bak-").Append(stamp).Append("」（不要放在 memory 資料夾裡）。\n");
        sb.Append("6. 寫完之後列出每個索引的行數與大小。\n");
        sb.Append("\n目標：每個 MEMORY.md ").Append(MemoryTargetLines).Append(" 行、").Append(MemoryTargetBytes / 1000).Append("KB 以內（留空間給之後的記憶）。");
        return sb.ToString();
    }
}
