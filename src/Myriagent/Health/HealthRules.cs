using System.Globalization;
using Myriagent.Agents;
using Newtonsoft.Json.Linq;
using static Myriagent.Health.HealthTextRules;

namespace Myriagent.Health;

/// <summary>健康度明細裡列的一個檔案</summary>
/// <param name="Path">路徑</param>
/// <param name="Label">顯示名稱（「CLAUDE.md」「匯入 docs/a.md」）</param>
/// <param name="Note">量測（「84 行・約 1.3 萬 token」）</param>
/// <param name="Level">這個檔的等級</param>
/// <param name="Target">符號連結指向的實際檔案</param>
internal sealed record HealthFileView(string Path, string Label, string Note, HealthLevel Level, string? Target = null);

/// <summary>健康度的一項檢查</summary>
/// <param name="Key">instructions／memory／skills／mcp／plugins／settings</param>
/// <param name="Title">標題（「全域指示」）</param>
/// <param name="Level">等級</param>
/// <param name="Value">量測的摘要（「84 行・約 1.3 萬 token」）</param>
/// <param name="Detail">說明與建議（多段以 \n 分開）</param>
/// <param name="Files">相關的檔案</param>
/// <param name="Headline">這一項是帳戶最差的一項時，卡片摘要用的短句（「CLAUDE.md 偏大」）；沒問題是 null</param>
internal sealed record HealthItem(string Key, string Title, HealthLevel Level, string Value, string Detail, IReadOnlyList<HealthFileView> Files, string? Headline);

/// <summary>一個帳戶的健康度</summary>
/// <param name="Load">每次對話開始前固定載入的量（「約 1.3 萬 token 指示＋約 6,300 token skill 清單」）；算不出是 null</param>
/// <param name="CanTrim">全域指示偏大，可以「用代理精簡」</param>
/// <param name="CanTrimMemory">有自動記憶索引超過或接近上限，可以「用代理整理」</param>
internal sealed record AccountHealth(string AccountId, HealthLevel Level, string Summary, string? Load, IReadOnlyList<HealthItem> Items, bool CanTrim, bool CanTrimMemory);

/// <summary>
/// 帳戶健康度的門檻與評估（純函式）：每次對話都會載入的全域指示、Claude 自動記憶的索引、skill 清單、MCP 伺服器、外掛、設定檔。
/// 門檻取自各家文件：Claude Code 建議每份 CLAUDE.md 200 行以內、單檔 40,000 字元以上啟動時警告、MEMORY.md 只載入前 200 行或 25KB、
/// skill 清單的預算約為脈絡的 1%；Antigravity 的規則檔單檔 24,000 位元組（超過截斷）。token 數一律是粗估
/// </summary>
internal static class HealthRules
{
    /// <summary>全域指示合計超過這個 token 數算偏大（約是官方建議的 200 行的兩倍）</summary>
    public const int InstructionWarnTokens = 5_000;

    /// <summary>全域指示合計超過這個 token 數算過大</summary>
    public const int InstructionBadTokens = 15_000;

    /// <summary>Claude 官方建議每份 CLAUDE.md 的行數上限</summary>
    public const int InstructionWarnLines = 200;

    /// <summary>Claude Code 對單一指示檔的字元數警告（200K 脈絡）</summary>
    public const int ClaudeFileWarnChars = 40_000;

    /// <summary>Antigravity 規則檔單檔上限（位元組，超過在行的邊界截斷）</summary>
    public const long AntigravityFileMaxBytes = 24_000;

    /// <summary>Claude 自動記憶索引只載入的行數</summary>
    public const int MemoryIndexMaxLines = 200;

    /// <summary>Claude 自動記憶索引只載入的大小（位元組）</summary>
    public const long MemoryIndexMaxBytes = 25_000;

    /// <summary>到上限的這個比例以上算接近上限</summary>
    public const double MemoryNearRatio = 0.9;

    /// <summary>skill 清單的描述單筆上限（字元）</summary>
    public const int SkillDescriptionCap = 1_536;

    /// <summary>Claude Code 的 skill 清單預算（200K 脈絡的 1%，約 2,000 token、8,000 字元）</summary>
    public const int SkillListingBudgetChars = 8_000;

    /// <summary>1M 脈絡的 skill 清單預算（約 1 萬 token、4 萬字元）</summary>
    public const int SkillListingBudgetLargeChars = 40_000;

    /// <summary>MCP 伺服器到這個數量算偏多</summary>
    public const int McpWarnServers = 10;

    /// <summary>Claude 的 .claude.json 超過這個大小算偏大</summary>
    public const long ClaudeStateWarnBytes = 5L * 1024 * 1024;

    /// <summary>脈絡大小（百分比的基準）</summary>
    public const int ContextTokens = 200_000;

    /// <summary>明細最多列幾個檔</summary>
    public const int MaxListedFiles = 12;

    /// <summary>各代理的全域指示主檔名</summary>
    public static string InstructionFileName(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "CLAUDE.md",
        AgentProvider.Codex => "AGENTS.md",
        AgentProvider.Grok => "AGENTS.md",
        AgentProvider.Antigravity => "GEMINI.md",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>整體等級：info 不拉低，取最差的</summary>
    public static HealthLevel Worst(IEnumerable<HealthLevel> levels)
    {
        var worst = HealthLevel.Ok;
        foreach (var level in levels)
        {
            if (Rank(level) > Rank(worst))
                worst = level;
        }
        return worst;
    }

    private static int Rank(HealthLevel level) => level switch
    {
        HealthLevel.Ok or HealthLevel.Info => 0,
        HealthLevel.Warn => 1,
        HealthLevel.Bad => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>每次呼叫工具都要跑的 hook（PreToolUse、PostToolUse、PostToolUseFailure）這麼多個以上時提醒</summary>
    public const int HooksPerToolWarn = 12;

    /// <summary>
    /// 評估一個帳戶。cli＝Claude CLI 自己回報的清單（儀表板的 MCP 檢查順便問的；精簡模式與其他代理不用）：
    /// 有它時 skill 清單照 CLI 實際列出的算，另外評估 hooks、記憶那一項多寫自動記憶的開關
    /// </summary>
    public static AccountHealth Evaluate(AccountScan scan, ClaudeCliInventory? cli = null)
    {
        if (scan.Account.Provider != AgentProvider.Claude || scan.Account.SafeMode)
            cli = null;
        var items = new List<HealthItem> { EvaluateInstructions(scan) };
        if (scan.MemoryIndexes != null)
            items.Add(EvaluateMemory(scan, cli));
        if (cli?.Skills != null)
            items.Add(EvaluateCliSkills(scan, cli.Skills));
        else if (scan.Skills != null)
            items.Add(EvaluateSkills(scan));
        if (cli?.Hooks != null)
            items.Add(EvaluateHooks(cli.Hooks));
        if (scan.McpServers != null)
            items.Add(EvaluateMcp(scan));
        if (scan.Plugins != null)
            items.Add(EvaluatePlugins(scan));
        if (scan.SettingsPath != null || scan.StateFilePath != null)
            items.Add(EvaluateSettings(scan));

        var level = Worst(items.Select(i => i.Level));
        var summary = level == HealthLevel.Ok
            ? "良好"
            : string.Join("、", items.Where(i => i.Level == level && i.Headline != null).Select(i => i.Headline).Take(2));
        var instructionTokens = InstructionsLoaded(scan) ? scan.Instructions.Sum(f => f.Measure.Tokens) : 0;
        var parts = new List<string>();
        if (instructionTokens > 0)
            parts.Add(FormatTokens(instructionTokens) + " 指示");
        var skillTokens = cli?.Skills != null ? CliSkillTokens(cli.Skills) : SkillsLoaded(scan) ? scan.SkillListingTokens : 0;
        if (skillTokens > 0)
            parts.Add(FormatTokens(skillTokens) + " skill 清單");
        var canTrim = items[0].Level is HealthLevel.Warn or HealthLevel.Bad;
        var canTrimMemory = scan.MemoryIndexes?.Any(MemoryNeedsTrim) == true;
        return new AccountHealth(scan.Account.Id, level, summary, parts.Count > 0 ? string.Join("＋", parts) : null, items, canTrim, canTrimMemory);
    }

    /// <summary>精簡模式下 Claude、Codex 不載入全域指示（--safe-mode）；Antigravity 的 --disable-slash-commands 仍載入 GEMINI.md</summary>
    private static bool InstructionsLoaded(AccountScan scan)
        => !(scan.Account.SafeMode && scan.Account.Provider is AgentProvider.Claude or AgentProvider.Codex);

    /// <summary>Antigravity 精簡模式（--disable-slash-commands）不載入斜線指令與 skill 清單</summary>
    private static bool SkillsLoaded(AccountScan scan)
        => !(scan.Account.SafeMode && scan.Account.Provider == AgentProvider.Antigravity);

    private static string Percent(int tokens)
        => (tokens * 100.0 / ContextTokens).ToString(tokens * 100.0 / ContextTokens < 1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    private static string KindLabel(string kind) => kind switch
    {
        "main" => "主檔",
        "import" => "匯入",
        "rule" => "規則",
        "compat" => "Claude 相容",
        _ => kind,
    };

    private static HealthItem EvaluateInstructions(AccountScan scan)
    {
        var provider = scan.Account.Provider;
        var name = InstructionFileName(provider);
        const string Title = "全域指示";
        if (!InstructionsLoaded(scan))
            return new HealthItem("instructions", Title, HealthLevel.Info, "精簡模式不載入",
                "這個帳戶開著精簡模式，對話不載入 " + name + "、外掛、hooks 與 MCP。", Array.Empty<HealthFileView>(), null);
        var files = scan.Instructions;
        if (files.Count == 0)
        {
            return new HealthItem("instructions", Title, HealthLevel.Info, "沒有",
                "這個帳戶沒有使用者層的 " + name + "，對話不會帶任何電腦層級的指示。要讓這台的各個 CLI 遵守同一套習慣，可以在下方「電腦層級的指示檔」合併。",
                scan.MainInstructionPath != null ? new[] { new HealthFileView(scan.MainInstructionPath, name, "不存在", HealthLevel.Info) } : Array.Empty<HealthFileView>(),
                null);
        }

        var tokens = files.Sum(f => f.Measure.Tokens);
        var lines = files.Sum(f => f.Measure.Lines);
        var views = new List<HealthFileView>();
        var reasons = new List<string>();
        var level = HealthLevel.Ok;
        foreach (var f in files)
        {
            var fileLevel = HealthLevel.Ok;
            var fileName = Path.GetFileName(f.Path);
            if (provider == AgentProvider.Claude && f.Measure.Chars > ClaudeFileWarnChars)
            {
                fileLevel = HealthLevel.Bad;
                reasons.Add($"{fileName} 有 {FormatCount(f.Measure.Chars)} 字元，超過 Claude Code 的 {FormatCount(ClaudeFileWarnChars)} 字元門檻（啟動時會警告「Large CLAUDE.md」）。");
            }
            else if (provider == AgentProvider.Antigravity && f.Measure.Bytes > AntigravityFileMaxBytes)
            {
                fileLevel = HealthLevel.Bad;
                reasons.Add($"{fileName} 有 {FormatBytes(f.Measure.Bytes)}，超過 Antigravity 單檔 {FormatCount(AntigravityFileMaxBytes)} 位元組的上限，後面的內容會被截掉。");
            }
            else if (f.Measure.Lines > InstructionWarnLines)
            {
                fileLevel = HealthLevel.Warn;
                reasons.Add($"{fileName} 有 {FormatCount(f.Measure.Lines)} 行，Claude 官方建議每份在 {InstructionWarnLines} 行以內。");
            }
            level = Worst(new[] { level, fileLevel });
            views.Add(new HealthFileView(f.Path, KindLabel(f.Kind) + "　" + fileName,
                FormatCount(f.Measure.Lines) + " 行・" + FormatTokens(f.Measure.Tokens), fileLevel, f.LinkTarget));
        }
        if (tokens > InstructionBadTokens)
            level = HealthLevel.Bad;
        else if (tokens > InstructionWarnTokens)
            level = Worst(new[] { level, HealthLevel.Warn });
        if (scan.MissingImports.Count > 0)
        {
            level = Worst(new[] { level, HealthLevel.Warn });
            reasons.Add("找不到這些 @ 匯入的檔案：" + string.Join("、", scan.MissingImports.Take(5)) + (scan.MissingImports.Count > 5 ? " 等" : "") + "。");
        }

        var detail = new List<string>
        {
            $"每次對話開始都會載入這些指示，合計{FormatTokens(tokens)}，約占 200K 脈絡的 {Percent(tokens)}。",
        };
        detail.AddRange(reasons);
        if (level is HealthLevel.Warn or HealthLevel.Bad)
        {
            detail.Add(tokens > InstructionWarnTokens
                ? $"合計超過 {FormatCount(InstructionWarnTokens)} token：指示越長越佔脈絡，也越容易有些規則沒被遵守。"
                : "指示越長越佔脈絡，也越容易有些規則沒被遵守。");
            detail.Add(provider == AgentProvider.Claude
                ? "建議：只在特定情境需要的細節搬到另外的文件，指示檔只留一行「情境 → 檔案」的索引；或改成 ~/.claude/rules/ 裡有 paths 的規則，碰到相符的檔案才載入。@ 匯入不會減少載入量。"
                : "建議：只在特定情境需要的細節搬到另外的文件，指示檔只留一行「情境 → 檔案」的索引。");
        }
        if (scan.ScopedRules > 0)
        {
            detail.Add(provider == AgentProvider.Antigravity
                ? $"另有 {scan.ScopedRules} 個按需觸發的規則（model_decision／glob），預設不佔固定脈絡（不計入）。"
                : $"另有 {scan.ScopedRules} 個有 paths 的規則，只在碰到相符的檔案時才載入（不計入）。");
        }

        var value = FormatCount(lines) + " 行・" + FormatTokens(tokens) + (files.Count > 1 ? "・" + files.Count + " 個檔" : "");
        // 摘要以最大的那個檔命名（Grok 的大小多半來自 Claude 相容讀的 CLAUDE.md，不是它自己的 AGENTS.md）
        var largest = Path.GetFileName(files.OrderByDescending(f => f.Measure.Tokens).First().Path);
        var headline = level switch
        {
            HealthLevel.Bad => largest + " 過大",
            HealthLevel.Warn => scan.MissingImports.Count > 0 && tokens <= InstructionWarnTokens ? "匯入的檔案找不到" : largest + " 偏大",
            _ => null,
        };
        return new HealthItem("instructions", Title, level, value, string.Join("\n", detail), views, headline);
    }

    private static HealthItem EvaluateMemory(AccountScan scan, ClaudeCliInventory? cli)
    {
        var item = EvaluateMemory(scan);
        if (cli?.AutoMemory == null && cli?.AutoDream == null)
            return item;
        var status = new List<string>();
        if (cli.AutoMemory is { } memory)
            status.Add("自動記憶" + (memory ? "開著" : "關著"));
        if (cli.AutoDream is { } dream)
            status.Add("auto dream " + (dream ? "開著" : "關著"));
        return item with { Detail = item.Detail + "\n" + string.Join("、", status) + "（Claude CLI 回報）。" };
    }

    private static HealthItem EvaluateMemory(AccountScan scan)
    {
        const string Title = "自動記憶索引";
        var indexes = scan.MemoryIndexes!;
        var shared = scan.Account.SharedSourceDirectory != null ? "這個帳戶共用系統預設帳戶的記憶。" : string.Empty;
        if (indexes.Count == 0)
            return new HealthItem("memory", Title, HealthLevel.Ok, "還沒有", "還沒有任何專案的自動記憶（MEMORY.md）。" + shared, Array.Empty<HealthFileView>(), null);

        var over = indexes.Where(MemoryOverLimit).OrderByDescending(m => m.Bytes).ToList();
        var near = indexes.Where(MemoryNearLimit).OrderByDescending(m => m.Bytes).ToList();
        var level = over.Count > 0 ? HealthLevel.Bad : near.Count > 0 ? HealthLevel.Warn : HealthLevel.Ok;
        var value = indexes.Count + " 個專案" + (over.Count > 0 ? "・" + over.Count + " 個超過上限" : near.Count > 0 ? "・" + near.Count + " 個接近上限" : string.Empty);
        var files = over.Select(m => new HealthFileView(m.Path, m.Project, MemoryNote(m), HealthLevel.Bad))
            .Concat(near.Select(m => new HealthFileView(m.Path, m.Project, MemoryNote(m), HealthLevel.Warn)))
            .Take(MaxListedFiles).ToList();
        var largest = indexes.OrderByDescending(m => m.Bytes).First();
        var detail = new List<string>
        {
            $"每個專案的 MEMORY.md 只載入前 {MemoryIndexMaxLines} 行或 25KB（先到者為準），超過的部分不會進入對話。" + shared,
        };
        if (over.Count > 0)
            detail.Add("超過上限的索引後面幾條記憶等於沒記：可以用代理整理——把索引改成一行一條、細節移到主題檔，合併或刪掉過時的條目。");
        else if (near.Count > 0)
            detail.Add("接近上限：再記幾條就會開始被截掉，建議先整理。");
        else
            detail.Add($"最大的是 {largest.Project}（{MemoryNote(largest)}）。");
        var headline = level switch
        {
            HealthLevel.Bad => "記憶索引超過上限",
            HealthLevel.Warn => "記憶索引接近上限",
            _ => null,
        };
        return new HealthItem("memory", Title, level, value, string.Join("\n", detail), files, headline);
    }

    /// <summary>記憶索引超過載入上限（超過的部分不會進入對話）</summary>
    public static bool MemoryOverLimit(MemoryIndexStat m) => m.Lines > MemoryIndexMaxLines || m.Bytes > MemoryIndexMaxBytes;

    /// <summary>記憶索引到上限的九成以上（還沒超過）</summary>
    public static bool MemoryNearLimit(MemoryIndexStat m)
        => !MemoryOverLimit(m) && (m.Lines >= MemoryIndexMaxLines * MemoryNearRatio || m.Bytes >= MemoryIndexMaxBytes * MemoryNearRatio);

    /// <summary>超過或接近上限：該整理了（「用代理整理」的對象）</summary>
    public static bool MemoryNeedsTrim(MemoryIndexStat m) => MemoryOverLimit(m) || MemoryNearLimit(m);

    private static string MemoryNote(MemoryIndexStat m) => FormatCount(m.Lines) + " 行・" + FormatBytes(m.Bytes);

    private static HealthItem EvaluateSkills(AccountScan scan)
    {
        const string Title = "Skills";
        var provider = scan.Account.Provider;
        if (!SkillsLoaded(scan))
            return new HealthItem("skills", Title, HealthLevel.Info, "精簡模式不載入",
                "這個帳戶開著精簡模式（--disable-slash-commands），對話不載入斜線指令與 skill 清單。", Array.Empty<HealthFileView>(), null);
        var count = scan.Skills!.Value;
        var value = count == 0 ? "沒有" : count + " 個・清單 " + FormatCount(scan.SkillListingChars) + " 字元";
        if (scan.Agents > 0)
            value += "・" + scan.Agents + " 個子代理";
        if (count == 0)
            return new HealthItem("skills", Title, HealthLevel.Ok, value, "沒有使用者層或外掛的 skill。", Array.Empty<HealthFileView>(), null);
        var plugin = scan.PluginSkills > 0 ? $"其中 {scan.PluginSkills} 個是外掛帶來的。" : string.Empty;
        if (provider == AgentProvider.Claude)
        {
            // 預算看模型的脈絡大小（200K 約 8,000 字元、1M 約 4 萬字元）；不知道對話用哪個，兩個都講
            var over = scan.SkillListingChars > SkillListingBudgetChars;
            var overLarge = scan.SkillListingChars > SkillListingBudgetLargeChars;
            var detail = new List<string>
            {
                $"skill 的名稱與描述每一輪都在脈絡裡（{FormatTokens(scan.SkillListingTokens)}）。" + plugin,
                overLarge
                    ? $"Claude Code 的清單預算約為脈絡的 1%，連 1M 脈絡的預算（約 {FormatCount(SkillListingBudgetLargeChars)} 字元）都超過了：較少用的 skill 會被拿掉描述，Claude 就比較認不出什麼時候該用它。"
                    : over
                        ? $"Claude Code 的清單預算約為脈絡的 1%：200K 脈絡的模型（約 {FormatCount(SkillListingBudgetChars)} 字元）已經超過，較少用的 skill 會被拿掉描述，Claude 就比較認不出什麼時候該用它；1M 脈絡（約 {FormatCount(SkillListingBudgetLargeChars)} 字元）還在範圍內。"
                        : $"Claude Code 的清單預算約為脈絡的 1%（200K 約 {FormatCount(SkillListingBudgetChars)} 字元、1M 約 {FormatCount(SkillListingBudgetLargeChars)} 字元），目前在範圍內。",
            };
            if (over)
                detail.Add("建議：停用用不到的外掛，或在 settings.json 的 skillOverrides 把少用的設成 name-only。");
            return new HealthItem("skills", Title, over ? HealthLevel.Warn : HealthLevel.Ok, value, string.Join("\n", detail), Array.Empty<HealthFileView>(),
                overLarge ? "skill 清單超過預算" : over ? "skill 清單偏大" : null);
        }
        return new HealthItem("skills", Title, HealthLevel.Info, value,
            $"skill 的名稱與描述每一輪都在脈絡裡（{FormatTokens(scan.SkillListingTokens)}）。" + plugin, Array.Empty<HealthFileView>(), null);
    }

    /// <summary>CLI 列給模型的 skill 清單合計的 token 數（沒有列出的不算）</summary>
    private static int CliSkillTokens(IReadOnlyList<CliSkill> skills) => skills.Where(s => s.Advertised).Sum(s => s.Tokens);

    /// <summary>Claude Code 的 skill 清單預算（token；約為脈絡的 1%）：200K 約 2,000、1M 約 10,000</summary>
    public const int SkillListingBudgetTokens = 2_000;

    public const int SkillListingBudgetLargeTokens = 10_000;

    /// <summary>skill 清單照 Claude CLI 實際列出的（get_skills_dialog；含 claude.ai 同步過來的 skill、CLI 自己算的 token 數）</summary>
    private static HealthItem EvaluateCliSkills(AccountScan scan, IReadOnlyList<CliSkill> skills)
    {
        const string Title = "Skills";
        var listed = skills.Where(s => s.Advertised).ToList();
        var tokens = CliSkillTokens(skills);
        var value = listed.Count == 0 ? "沒有" : listed.Count + " 個・清單 " + FormatTokens(tokens);
        if (scan.Agents > 0)
            value += "・" + scan.Agents + " 個子代理";
        var sources = new List<string>();
        if (listed.Count(s => s.Source == "plugin") is > 0 and var plugin)
            sources.Add($"{plugin} 個是外掛帶來的");
        if (listed.Count(s => s.Source == "claude.ai sync") is > 0 and var synced)
            sources.Add($"{synced} 個是 claude.ai 同步過來的");
        var origin = sources.Count > 0 ? "其中" + string.Join("、", sources) + "。" : string.Empty;
        var hidden = skills.Count - listed.Count;
        var detail = new List<string> { $"數字取自 Claude CLI 實際列給模型的清單：名稱與描述每一輪都在脈絡裡（{FormatTokens(tokens)}）。" + origin };
        if (hidden > 0)
            detail.Add($"另有 {hidden} 個沒有列給 Claude（只留名稱或停用）。");
        if (listed.Count == 0)
            return new HealthItem("skills", Title, HealthLevel.Ok, value, string.Join("\n", detail), Array.Empty<HealthFileView>(), null);
        var over = tokens > SkillListingBudgetTokens;
        var overLarge = tokens > SkillListingBudgetLargeTokens;
        detail.Add(overLarge
            ? $"Claude Code 的清單預算約為脈絡的 1%，連 1M 脈絡的預算（約 {FormatCount(SkillListingBudgetLargeTokens)} token）都超過了：較少用的 skill 會被拿掉描述，Claude 就比較認不出什麼時候該用它。"
            : over
                ? $"Claude Code 的清單預算約為脈絡的 1%：200K 脈絡的模型（約 {FormatCount(SkillListingBudgetTokens)} token）已經超過，較少用的 skill 會被拿掉描述；1M 脈絡（約 {FormatCount(SkillListingBudgetLargeTokens)} token）還在範圍內。"
                : $"Claude Code 的清單預算約為脈絡的 1%（200K 約 {FormatCount(SkillListingBudgetTokens)} token、1M 約 {FormatCount(SkillListingBudgetLargeTokens)} token），目前在範圍內。");
        if (over)
            detail.Add("建議：停用用不到的外掛，或在 settings.json 的 skillOverrides 把少用的設成 name-only。");
        return new HealthItem("skills", Title, over ? HealthLevel.Warn : HealthLevel.Ok, value, string.Join("\n", detail), Array.Empty<HealthFileView>(),
            overLarge ? "skill 清單超過預算" : over ? "skill 清單偏大" : null);
    }

    /// <summary>hooks（get_hooks_listing）：每次呼叫工具都要跑的太多時提醒（工具會變慢）</summary>
    private static HealthItem EvaluateHooks(IReadOnlyList<CliHook> hooks)
    {
        const string Title = "Hooks";
        if (hooks.Count == 0)
            return new HealthItem("hooks", Title, HealthLevel.Ok, "沒有", "沒有設定任何 hook。", Array.Empty<HealthFileView>(), null);
        var perTool = hooks.Count(h => h.Event is "PreToolUse" or "PostToolUse" or "PostToolUseFailure");
        var value = hooks.Count + " 個" + (perTool > 0 ? $"・每次呼叫工具 {perTool} 個" : string.Empty);
        var byEvent = hooks.GroupBy(h => h.Event).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        var byPlugin = hooks.Where(h => h.Plugin != null).GroupBy(h => h.Plugin!).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}（{g.Count()}）").ToList();
        var detail = new List<string> { "數字取自 Claude CLI：" + string.Join("、", byEvent) + "。" };
        if (byPlugin.Count > 0)
            detail.Add("外掛帶來的：" + string.Join("、", byPlugin) + "。");
        var heavy = perTool >= HooksPerToolWarn;
        if (heavy)
            detail.Add($"每次呼叫工具前後都要跑這 {perTool} 個 hook，工具會變慢；用不到的外掛可以停用。");
        return new HealthItem("hooks", Title, heavy ? HealthLevel.Warn : HealthLevel.Info, value, string.Join("\n", detail), Array.Empty<HealthFileView>(),
            heavy ? "每次呼叫工具要跑很多 hook" : null);
    }

    private static HealthItem EvaluateMcp(AccountScan scan)
    {
        const string Title = "MCP 伺服器";
        var servers = scan.McpServers!;
        if (scan.Account.SafeMode && scan.Account.Provider == AgentProvider.Claude)
            return new HealthItem("mcp", Title, HealthLevel.Info, "精簡模式不載入", "精簡模式下不啟動 MCP 伺服器。", Array.Empty<HealthFileView>(), null);
        if (servers.Count == 0)
            return new HealthItem("mcp", Title, HealthLevel.Ok, "沒有", "沒有使用者層或外掛的 MCP 伺服器。", Array.Empty<HealthFileView>(), null);
        var many = servers.Count >= McpWarnServers;
        var detail = new List<string>
        {
            "使用者層與外掛的 MCP 伺服器：" + string.Join("、", servers.Take(20)) + (servers.Count > 20 ? " 等" : "") + "。",
            "每個對話開始時都會啟動這些伺服器；工具定義大多延後載入，但伺服器多了會拖慢啟動、也多佔記憶體。",
        };
        if (many)
            detail.Add("建議：停用不常用的伺服器，或改成只在需要的專案（.mcp.json）啟用。");
        return new HealthItem("mcp", Title, many ? HealthLevel.Warn : HealthLevel.Ok, servers.Count + " 個", string.Join("\n", detail),
            Array.Empty<HealthFileView>(), many ? "MCP 伺服器偏多" : null);
    }

    private static HealthItem EvaluatePlugins(AccountScan scan)
    {
        const string Title = "外掛";
        var plugins = scan.Plugins!;
        if (scan.Account.SafeMode && scan.Account.Provider != AgentProvider.Antigravity)
            return new HealthItem("plugins", Title, HealthLevel.Info, "精簡模式不載入", "精簡模式下不載入外掛。", Array.Empty<HealthFileView>(), null);
        if (plugins.Count == 0)
            return new HealthItem("plugins", Title, HealthLevel.Ok, "沒有", "沒有啟用的外掛。", Array.Empty<HealthFileView>(), null);
        var installed = plugins.Where(p => !p.Missing).ToList();
        var missing = plugins.Where(p => p.Missing).Select(p => p.Id).ToList();
        var detail = new List<string>
        {
            $"啟用 {plugins.Count} 個，帶來 {installed.Sum(p => p.Skills)} 個 skill、{installed.Sum(p => p.Commands)} 個斜線指令、{installed.Sum(p => p.Agents)} 個子代理、"
            + $"{installed.Count(p => p.Mcp)} 個附 MCP、{installed.Count(p => p.Hooks)} 個附 hooks。",
        };
        var heavy = installed.OrderByDescending(p => p.Skills + p.Commands + p.Agents).Where(p => p.Skills + p.Commands + p.Agents > 0).Take(5)
            .Select(p => $"{p.Id}（{p.Skills + p.Commands + p.Agents}）").ToList();
        if (heavy.Count > 0)
            detail.Add("帶最多 skill、指令與子代理的：" + string.Join("、", heavy) + "。");
        if (missing.Count > 0)
            detail.Add("settings.json 啟用了但這台沒有安裝：" + string.Join("、", missing) + "。");
        var level = missing.Count > 0 ? HealthLevel.Warn : HealthLevel.Info;
        return new HealthItem("plugins", Title, level, plugins.Count + " 個啟用", string.Join("\n", detail), Array.Empty<HealthFileView>(),
            missing.Count > 0 ? "有啟用卻沒安裝的外掛" : null);
    }

    private static HealthItem EvaluateSettings(AccountScan scan)
    {
        const string Title = "設定檔";
        var files = new List<HealthFileView>();
        var detail = new List<string>();
        var level = HealthLevel.Ok;
        string? headline = null;
        var settingsName = scan.SettingsPath != null ? Path.GetFileName(scan.SettingsPath) : "settings.json";
        if (scan.SettingsError != null)
        {
            level = HealthLevel.Bad;
            headline = settingsName + " 讀不懂";
            detail.Add(settingsName + " 讀不懂：" + scan.SettingsError + "。CLI 可能忽略整份設定（權限、外掛、環境變數都不生效）。");
            if (scan.SettingsPath != null)
                files.Add(new HealthFileView(scan.SettingsPath, settingsName, "讀不懂", HealthLevel.Bad));
        }
        if (scan.StateFileBytes is { } state && scan.StateFilePath != null)
        {
            var big = state > ClaudeStateWarnBytes;
            if (big)
            {
                level = Worst(new[] { level, HealthLevel.Warn });
                headline ??= ".claude.json 過大";
                detail.Add($".claude.json 有 {FormatBytes(state)}（各專案的紀錄累積下來的），CLI 每次啟動與結束都要讀寫它，太大會拖慢啟動。可以刪掉 projects 裡已經不用的專案。");
            }
            files.Add(new HealthFileView(scan.StateFilePath, ".claude.json", FormatBytes(state), big ? HealthLevel.Warn : HealthLevel.Ok));
        }
        if (detail.Count == 0)
            detail.Add("設定檔讀得懂，大小正常。");
        var value = level switch
        {
            HealthLevel.Bad => "讀不懂",
            HealthLevel.Warn => "偏大",
            _ => "正常",
        };
        return new HealthItem("settings", Title, level, value, string.Join("\n", detail), files, headline);
    }

    /// <summary>網頁的等級字串</summary>
    public static string LevelName(HealthLevel level) => level switch
    {
        HealthLevel.Ok => "ok",
        HealthLevel.Info => "info",
        HealthLevel.Warn => "warn",
        HealthLevel.Bad => "bad",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>AccountHealth 的 JSON（web-protocol 的 AccountHealthView）</summary>
    public static JObject Render(AccountHealth health) => new()
    {
        ["level"] = LevelName(health.Level),
        ["summary"] = health.Summary,
        ["load"] = health.Load,
        ["canTrim"] = health.CanTrim,
        ["canTrimMemory"] = health.CanTrimMemory,
        ["items"] = new JArray(health.Items.Select(i => new JObject
        {
            ["key"] = i.Key,
            ["title"] = i.Title,
            ["level"] = LevelName(i.Level),
            ["value"] = i.Value,
            ["detail"] = i.Detail,
            ["files"] = new JArray(i.Files.Select(RenderFile)),
        })),
    };

    private static JObject RenderFile(HealthFileView f) => new()
    {
        ["path"] = f.Path,
        ["label"] = f.Label,
        ["note"] = f.Note,
        ["level"] = LevelName(f.Level),
        ["target"] = f.Target,
    };
}
