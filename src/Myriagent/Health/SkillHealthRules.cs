using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using static Myriagent.Health.HealthTextRules;

namespace Myriagent.Health;

/// <summary>一個 skill 的一個發現（等級＋給人看的一句）</summary>
internal sealed record SkillIssue(HealthLevel Level, string Text);

/// <summary>
/// 儀表板逐一列出的一個使用中的 skill 或斜線指令
/// </summary>
/// <param name="Name">CLI 叫它的名稱（外掛的帶命名空間：「superpowers:brainstorming」）</param>
/// <param name="Label">顯示名稱（SKILL.md 宣告的 name；指令前面加 /）</param>
/// <param name="Kind">skill 或 command</param>
/// <param name="Source">user、plugin、synced（claude.ai 同步）、或 CLI 回報的其他來源</param>
/// <param name="Plugin">外掛名稱；不是外掛帶的是 null</param>
/// <param name="Path">SKILL.md 或指令檔；找不到檔（只有 CLI 知道的）是 null</param>
/// <param name="Purpose">用途摘要：描述的第一句（最多 160 字）</param>
/// <param name="Description">完整的描述（含 when_to_use；最多 1,536 字）</param>
/// <param name="Tokens">在 skill 清單裡佔的 token（CLI 算的，沒有 CLI 時粗估）</param>
/// <param name="Listed">有列給模型（false＝只留名稱）</param>
/// <param name="Level">這個 skill 的等級（info 的發現不拉低，ok）</param>
/// <param name="Issues">發現</param>
internal sealed record SkillView(
    string Name, string Label, string Kind, string Source, string? Plugin, string? Path, string Purpose, string Description,
    int Tokens, bool Listed, HealthLevel Level, IReadOnlyList<SkillIssue> Issues)
{
    /// <summary>使用者自己的（使用者層的 skill 與斜線指令）：外掛與 claude.ai 的不是使用者能改的，不拉低帳戶的等級</summary>
    public bool OwnedByUser => Source == "user";
}

/// <summary>
/// 逐一 skill 的用途與健康度（純函式）：用途取描述的第一句；健康度看描述（沒有、太短、超過 1,536 字會被截掉）、
/// 前置資料、SKILL.md 本文長度（官方建議 500 行以內）、連結的參考檔在不在、名稱是否合乎規範、有沒有同名重複佔清單、有沒有列給模型。
/// 有 Claude CLI 的清單（get_skills_dialog）時以它為準（哪些在用、token 數、claude.ai 同步的 skill），檔案的內容從掃描結果對回來
/// </summary>
internal static class SkillHealthRules
{
    /// <summary>描述（含 when_to_use）超過這個字數會被截掉後段</summary>
    public const int DescriptionCap = HealthRules.SkillDescriptionCap;

    /// <summary>描述粗估不到這個 token 數算太短（「Test Plan」約 4）</summary>
    public const int ShortDescriptionTokens = 8;

    /// <summary>SKILL.md 本文建議的行數上限（Anthropic 的 skill 撰寫建議）</summary>
    public const int BodyWarnLines = 500;

    /// <summary>用途摘要的長度上限</summary>
    public const int PurposeMaxChars = 160;

    /// <summary>Agent Skills 規範的名稱：小寫英數與連字號、64 字元內</summary>
    private static readonly Regex NamePattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 使用中的 skill：有 CLI 的清單時照它（state 是 off 的不算），沒有時照掃描到的。
    /// 排序：有問題的在前（bad、warn），再來使用者自己的、指令、claude.ai、外掛，最後照名稱
    /// </summary>
    public static List<SkillView> Evaluate(AccountScan scan, IReadOnlyList<CliSkill>? cli)
    {
        var drafts = new List<Draft>();
        if (cli != null)
        {
            foreach (var c in cli)
            {
                if (IsOff(c.State))
                    continue;
                var source = NormalizeSource(c.Source);
                var stat = Match(scan, c, source);
                drafts.Add(new Draft(c.Name, stat, c, source == "user" && stat?.Kind == "command" ? "user" : source, c.Tokens, c.Advertised));
            }
        }
        else
        {
            foreach (var stat in scan.SkillList)
                drafts.Add(new Draft(stat.Plugin != null ? stat.Plugin + ":" + stat.Name : stat.Name, stat, null, stat.Source, EstimateListingTokens(stat), true));
        }

        // 同名重複：不帶命名空間的名稱相同、都列給模型的
        var duplicates = drafts.Where(d => d.Listed).GroupBy(d => UnqualifiedKey(d), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.Select(d => d.Name).ToList(), StringComparer.OrdinalIgnoreCase);

        var result = drafts.Select(d => Build(d, duplicates)).ToList();
        return result.OrderByDescending(v => Rank(v.Level)).ThenBy(v => SourceOrder(v)).ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>CLI 的清單裡停用的（state 是 off、disabled）</summary>
    public static int DisabledCount(IReadOnlyList<CliSkill>? cli) => cli?.Count(c => IsOff(c.State)) ?? 0;

    private static bool IsOff(string? state) => state is not null && (state.Equals("off", StringComparison.OrdinalIgnoreCase) || state.Equals("disabled", StringComparison.OrdinalIgnoreCase));

    private sealed record Draft(string Name, SkillStat? Stat, CliSkill? Cli, string Source, int Tokens, bool Listed);

    private static string NormalizeSource(string source) => source.Trim().ToLowerInvariant() switch
    {
        "user" or "usersettings" => "user",
        "plugin" => "plugin",
        "claude.ai sync" or "claude.ai" or "synced" => "synced",
        "" => "other",
        var other => other,
    };

    /// <summary>CLI 的一筆對回掃描到的檔：外掛的名稱是「外掛:資料夾」、claude.ai 的看 unqualified_name</summary>
    private static SkillStat? Match(AccountScan scan, CliSkill c, string source)
    {
        var colon = c.Name.IndexOf(':');
        var prefix = colon > 0 ? c.Name[..colon] : null;
        var bare = colon > 0 ? c.Name[(colon + 1)..] : c.Name;
        switch (source)
        {
            case "plugin":
                return scan.SkillList.FirstOrDefault(s => s.Plugin != null && Same(s.Plugin, prefix) && Same(s.Name, bare));
            case "synced":
                var key = c.Unqualified ?? bare;
                return scan.SyncedSkills.GetValueOrDefault(key)
                    ?? scan.SkillList.FirstOrDefault(s => s.Source == "synced" && Same(s.Name, key));
            default:
                return scan.SkillList.FirstOrDefault(s => s.Plugin == null && Same(s.Name, c.Name))
                    ?? scan.SkillList.FirstOrDefault(s => s.Plugin == null && Same(s.DeclaredName, c.Name));
        }
    }

    private static bool Same(string? a, string? b) => a != null && b != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string UnqualifiedKey(Draft d)
    {
        if (d.Cli?.Unqualified is { Length: > 0 } u)
            return u;
        var name = d.Stat?.DeclaredName ?? d.Name;
        var colon = name.LastIndexOf(':');
        return colon >= 0 ? name[(colon + 1)..] : name;
    }

    /// <summary>沒有 CLI 時的清單 token 粗估：名稱＋描述（最多 1,536 字）</summary>
    private static int EstimateListingTokens(SkillStat s)
    {
        var desc = FullDescription(s);
        if (desc.Length > DescriptionCap)
            desc = desc[..DescriptionCap];
        return EstimateTokens((s.DeclaredName ?? s.Name) + ": " + desc);
    }

    private static string FullDescription(SkillStat s) => s.WhenToUse.Length > 0 ? (s.Description + " " + s.WhenToUse).Trim() : s.Description;

    private static SkillView Build(Draft d, Dictionary<string, List<string>> duplicates)
    {
        var stat = d.Stat;
        var kind = stat?.Kind ?? "skill";
        var description = stat != null && (stat.Description.Length > 0 || stat.WhenToUse.Length > 0) ? FullDescription(stat) : d.Cli?.Description ?? string.Empty;
        var issues = new List<SkillIssue>();

        if (stat != null)
        {
            if (!stat.Readable)
                issues.Add(new SkillIssue(HealthLevel.Warn, "讀不到 " + System.IO.Path.GetFileName(stat.File) + "，無法檢查內容。"));
            else
            {
                if (kind == "skill" && !stat.HasFrontMatter)
                    issues.Add(new SkillIssue(HealthLevel.Bad, "SKILL.md 開頭沒有前置資料（--- 包起來的 name、description），CLI 拿不到描述。"));
                if (description.Length == 0)
                {
                    issues.Add(kind == "skill"
                        ? new SkillIssue(HealthLevel.Bad, "沒有描述：模型只看得到名稱，不知道什麼時候該用它。")
                        : new SkillIssue(HealthLevel.Info, "沒有描述：清單裡只有名稱。"));
                }
                var cliDescription = d.Cli?.Description;
                if (cliDescription is { Length: > 0 } && stat.Description.Length > 0 && !SameStart(cliDescription, stat.Description))
                {
                    issues.Add(new SkillIssue(HealthLevel.Warn, $"CLI 讀到的描述是「{Shorten(cliDescription, 60)}」，和 SKILL.md 寫的不一樣，模型看到的是 CLI 讀到的那一句"
                        + (stat.FrontMatterProblem != null ? $"：前置資料的 YAML 讀不懂（{stat.FrontMatterProblem}），把值用引號包起來。" : "。")));
                }
                else if (stat.FrontMatterProblem != null)
                    issues.Add(new SkillIssue(HealthLevel.Warn, $"前置資料的 YAML 寫法容易讀錯（{stat.FrontMatterProblem}），有些工具會讀不到描述；把值用引號包起來。"));
                if (stat.BodyLines > BodyWarnLines)
                    issues.Add(new SkillIssue(HealthLevel.Warn, $"本文 {FormatCount(stat.BodyLines)} 行（建議 {BodyWarnLines} 行以內）：用到時整份載入脈絡，細節可以拆到參考檔、需要時再讀。"));
                if (stat.MissingRefs.Count > 0)
                    issues.Add(new SkillIssue(HealthLevel.Warn, "連結的檔案不存在：" + string.Join("、", stat.MissingRefs) + "。"));
                if (kind == "skill" && stat.Source == "user" && stat.DeclaredName is { } declared && !ValidName(declared))
                    issues.Add(new SkillIssue(HealthLevel.Info, $"名稱「{declared}」不合 Agent Skills 的規範（小寫英數與連字號、64 字元內），其他工具可能不認得。"));
            }
        }
        if (description.Length > 0)
        {
            var chars = CountChars(description);
            if (chars > DescriptionCap)
                issues.Add(new SkillIssue(HealthLevel.Warn, $"描述 {FormatCount(chars)} 字，超過 {FormatCount(DescriptionCap)} 字的部分會被截掉。"));
            else if (kind == "skill" && EstimateTokens(description) < ShortDescriptionTokens)
                issues.Add(new SkillIssue(HealthLevel.Warn, $"描述太短（「{description}」）：模型很難判斷什麼時候該用它，寫清楚做什麼、什麼情況下用。"));
        }
        // 找不到檔、CLI 也沒給描述（舊版 CLI 的清單沒有 description）：不知道，不判斷
        if (d.Listed && duplicates.TryGetValue(UnqualifiedKey(d), out var same))
        {
            var others = same.Where(n => !n.Equals(d.Name, StringComparison.Ordinal)).ToList();
            if (others.Count > 0)
                issues.Add(new SkillIssue(HealthLevel.Warn, "和「" + string.Join("」「", others) + "」同名，兩份描述都佔 skill 清單；用不到的那一份可以停用。"));
        }
        if (!d.Listed)
            issues.Add(new SkillIssue(HealthLevel.Info, "沒有列給模型（只留名稱）：只有直接叫用時才會用到。"));

        var level = HealthRules.Worst(issues.Select(i => i.Level));
        var label = d.Cli?.DisplayName is { Length: > 0 } display && !display.Equals(d.Name, StringComparison.Ordinal) && display.Contains(':')
            ? display
            : LabelOf(d.Name, stat);
        if (description.Length > DescriptionCap)
            description = description[..DescriptionCap] + "…";
        return new SkillView(d.Name, label, kind, d.Source, stat?.Plugin ?? PluginOf(d), stat?.File, Purpose(description), description,
            d.Tokens, d.Listed, level, issues);
    }

    /// <summary>兩份描述的開頭一樣（空白合併後比前 30 字；CLI 可能截短或併進 when_to_use）</summary>
    private static bool SameStart(string a, string b)
    {
        var x = Spaces.Replace(a, " ").Trim().Trim('"', '\'').TrimEnd('…', '.').TrimEnd();
        var y = Spaces.Replace(b, " ").Trim().Trim('"', '\'').TrimEnd('…', '.').TrimEnd();
        var n = Math.Min(30, Math.Min(x.Length, y.Length));
        return string.Compare(x, 0, y, 0, n, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static string Shorten(string text, int max)
    {
        var t = Spaces.Replace(text, " ").Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    private static string? PluginOf(Draft d)
    {
        if (d.Source != "plugin")
            return null;
        var colon = d.Name.IndexOf(':');
        return colon > 0 ? d.Name[..colon] : null;
    }

    /// <summary>顯示名稱：外掛的是「外掛:宣告的名稱」、指令前面加 /</summary>
    private static string LabelOf(string name, SkillStat? stat)
    {
        if (stat == null)
            return name;
        var own = stat.Kind == "skill" && stat.DeclaredName is { Length: > 0 } declared ? declared : stat.Name;
        var full = stat.Plugin != null ? stat.Plugin + ":" + own : own;
        return stat.Kind == "command" ? "/" + full : full;
    }

    public static bool ValidName(string name) => name.Length <= 64 && NamePattern.IsMatch(name);

    /// <summary>
    /// 用途摘要：描述的第一句（中文句號、驚嘆號、問號，或英文的 . ! ? 後面接空白或結尾；太前面的句點〔e.g.〕不算），
    /// 空白合併，超過 160 字截斷加「…」
    /// </summary>
    public static string Purpose(string description)
    {
        var t = Spaces.Replace(description ?? string.Empty, " ").Trim();
        if (t.Length == 0)
            return string.Empty;
        var end = -1;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c is '。' or '！' or '？')
            {
                end = i + 1;
                break;
            }
            if (c is '.' or '!' or '?' && i >= 12 && (i + 1 == t.Length || t[i + 1] == ' ') && !EndsWithAbbreviation(t, i))
            {
                end = i + 1;
                break;
            }
        }
        var s = end > 0 ? t[..end] : t;
        if (s.Length > PurposeMaxChars)
            s = s[..(PurposeMaxChars - 1)].TrimEnd() + "…";
        return s;
    }

    /// <summary>「e.g.」「i.e.」「etc.」「vs.」這類縮寫的句點不是句子的結尾</summary>
    private static bool EndsWithAbbreviation(string t, int dot)
    {
        var start = t.LastIndexOf(' ', dot) + 1;
        var word = t[start..(dot + 1)].TrimStart('(', '"', '\'').ToLowerInvariant();
        return word is "e.g." or "i.e." or "etc." or "vs." or "cf." or "approx.";
    }

    private static int Rank(HealthLevel level) => level switch
    {
        HealthLevel.Bad => 2,
        HealthLevel.Warn => 1,
        _ => 0,
    };

    private static int SourceOrder(SkillView v) => v.Source switch
    {
        "user" => v.Kind == "command" ? 1 : 0,
        "synced" => 2,
        "plugin" => 3,
        _ => 4,
    };

    /// <summary>網頁的 SkillHealthView</summary>
    public static JObject Render(SkillView v) => new()
    {
        ["name"] = v.Name,
        ["label"] = v.Label,
        ["kind"] = v.Kind,
        ["source"] = v.Source,
        ["plugin"] = v.Plugin,
        ["path"] = v.Path,
        ["purpose"] = v.Purpose,
        ["description"] = v.Description,
        ["tokens"] = v.Tokens,
        ["listed"] = v.Listed,
        ["level"] = HealthRules.LevelName(v.Level),
        ["issues"] = new JArray(v.Issues.Select(i => new JObject { ["level"] = HealthRules.LevelName(i.Level), ["text"] = i.Text })),
    };
}
