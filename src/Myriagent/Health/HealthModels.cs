using Myriagent.Agents;

namespace Myriagent.Health;

/// <summary>健康度的等級（網頁的 level：ok／info／warn／bad）；info 只是說明，不拉低整體</summary>
internal enum HealthLevel
{
    Ok,
    Info,
    Warn,
    Bad,
}

/// <summary>
/// 要分析的一個本機帳戶（在 UI 執行緒從 AgentAccount 取的快照；資料夾都已解析好，掃描器不碰 AccountRules）
/// </summary>
/// <param name="Id">帳戶 id</param>
/// <param name="Label">帳戶名稱</param>
/// <param name="Provider">代理種類</param>
/// <param name="IsSystemDefault">系統預設帳戶（用使用者原本的設定資料夾）</param>
/// <param name="ConfigDirectory">設定資料夾（Claude 的 CLAUDE_CONFIG_DIR、Codex 的 CODEX_HOME、Grok 的 GROK_HOME、Antigravity 的 agy 資料夾）</param>
/// <param name="SafeMode">精簡模式（不載入全域指示、外掛、MCP）</param>
/// <param name="SharedSourceDirectory">共用系統預設帳戶的設定時，來源（系統預設帳戶）的設定資料夾；不共用是 null</param>
/// <param name="ClaudeGlobalConfig">Claude 的 .claude.json（使用者層 MCP 伺服器在這裡）；其他種類是 null</param>
internal sealed record HealthAccountInput(
    string Id, string Label, AgentProvider Provider, bool IsSystemDefault, string ConfigDirectory, bool SafeMode,
    string? SharedSourceDirectory, string? ClaudeGlobalConfig);

/// <summary>這台電腦的位置（測試換成暫存資料夾）</summary>
/// <param name="UserProfile">使用者資料夾（<c>~</c>）</param>
internal sealed record HealthMachine(string UserProfile);

/// <summary>一個每次對話都會載入的指示檔</summary>
/// <param name="Path">路徑（使用者看到的位置）</param>
/// <param name="Kind">main（主檔）、import（<c>@</c> 匯入）、rule（使用者層規則）、compat（Grok 讀的 Claude 檔）</param>
/// <param name="Measure">量測（去掉 HTML 註解之後）</param>
/// <param name="LinkTarget">路徑是符號連結時指向的實際檔案；不是連結是 null</param>
internal sealed record InstructionFileStat(string Path, string Kind, TextMeasure Measure, string? LinkTarget);

/// <summary>Claude 自動記憶的一個索引檔（每個專案的 memory\MEMORY.md）</summary>
internal sealed record MemoryIndexStat(string Project, string Path, int Lines, long Bytes);

/// <summary>一個外掛帶來的東西（Claude 的 enabledPlugins）</summary>
internal sealed record PluginStat(string Id, int Skills, int Commands, int Agents, bool Mcp, bool Hooks, bool Missing);

/// <summary>掃描到的一個 skill 或斜線指令（儀表板逐一列出用途與健康度，SkillHealthRules 評估）</summary>
/// <param name="Name">CLI 叫它的名稱：skill 是資料夾名稱、指令是檔名；外掛的另外看 Plugin</param>
/// <param name="Kind">skill 或 command（斜線指令）</param>
/// <param name="Source">user（使用者層）、plugin（外掛）、synced（claude.ai 同步過來的）</param>
/// <param name="Plugin">外掛的名稱（不帶 @ 之後的市集）；不是外掛帶的是 null</param>
/// <param name="File">SKILL.md 或指令檔的路徑</param>
/// <param name="Readable">讀得到內容</param>
/// <param name="HasFrontMatter">有 --- 前置資料</param>
/// <param name="DeclaredName">前置資料的 name；沒有是 null</param>
/// <param name="Description">前置資料的 description（續行以空白連接）</param>
/// <param name="WhenToUse">前置資料的 when_to_use</param>
/// <param name="BodyLines">前置資料之後的行數</param>
/// <param name="MissingRefs">本文以相對路徑連結、卻不存在的檔（最多 10 個）</param>
/// <param name="FrontMatterProblem">前置資料的 YAML 會讀錯的寫法（HealthTextRules.FrontMatterProblem）；沒有是 null</param>
internal sealed record SkillStat(
    string Name, string Kind, string Source, string? Plugin, string File, bool Readable, bool HasFrontMatter,
    string? DeclaredName, string Description, string WhenToUse, int BodyLines, IReadOnlyList<string> MissingRefs,
    string? FrontMatterProblem = null);

/// <summary>一個帳戶的量測（掃描器的輸出、HealthRules 的輸入）</summary>
internal sealed class AccountScan
{
    public required HealthAccountInput Account { get; init; }

    /// <summary>每次對話都會載入的指示（主檔、匯入、沒有 paths 的規則、Grok 的 Claude 相容）</summary>
    public List<InstructionFileStat> Instructions { get; } = new();

    /// <summary>主檔的路徑（不存在也有，給「開啟」用）；Antigravity 是 GEMINI.md</summary>
    public string? MainInstructionPath { get; set; }

    /// <summary>只在碰到相符檔案時才載入的規則（有 paths 的）數量</summary>
    public int ScopedRules { get; set; }

    /// <summary>匯入的檔案找不到的（路徑原樣）</summary>
    public List<string> MissingImports { get; } = new();

    /// <summary>自動記憶的索引（Claude；共用設定時是來源的）；null＝這種代理沒有</summary>
    public List<MemoryIndexStat>? MemoryIndexes { get; set; }

    /// <summary>skill 與斜線指令（會列進 skill 清單的）數量；null＝這種代理不分析</summary>
    public int? Skills { get; set; }

    /// <summary>逐一的 skill 與斜線指令（會列進 skill 清單的；照掃描的順序）</summary>
    public List<SkillStat> SkillList { get; } = new();

    /// <summary>Claude：claude.ai 同步下來、在 skills\synced\&lt;帳號&gt;\&lt;名稱&gt;\ 的 skill（名稱→最新的 SKILL.md）；給 CLI 回報的 claude.ai skill 對檔案用</summary>
    public Dictionary<string, SkillStat> SyncedSkills { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>其中外掛帶來的</summary>
    public int PluginSkills { get; set; }

    /// <summary>skill 清單的字元數（名稱＋描述）</summary>
    public int SkillListingChars { get; set; }

    /// <summary>skill 清單粗估的 token 數</summary>
    public int SkillListingTokens { get; set; }

    /// <summary>自訂子代理數量（Claude：使用者層＋外掛）</summary>
    public int Agents { get; set; }

    /// <summary>MCP 伺服器的名稱（使用者層＋外掛）；null＝這種代理不分析</summary>
    public List<string>? McpServers { get; set; }

    /// <summary>啟用的外掛（Claude）；null＝這種代理沒有</summary>
    public List<PluginStat>? Plugins { get; set; }

    /// <summary>設定檔讀不懂的說明（settings.json 不是有效的 JSON）；沒問題是 null</summary>
    public string? SettingsError { get; set; }

    /// <summary>設定檔的路徑（settings.json、config.toml）</summary>
    public string? SettingsPath { get; set; }

    /// <summary>Claude 的 .claude.json 大小；沒有是 null</summary>
    public long? StateFileBytes { get; set; }

    public string? StateFilePath { get; set; }
}

/// <summary>電腦層級的一份全域指示（各 CLI 使用者層的 CLAUDE.md、AGENTS.md、GEMINI.md）</summary>
internal sealed class InstructionDoc
{
    /// <summary>比較用的鍵（路徑的小寫）</summary>
    public required string Key { get; init; }

    public required AgentProvider Provider { get; init; }

    /// <summary>顯示名稱：「Claude Code」「Codex・工作帳戶」</summary>
    public required string Label { get; init; }

    /// <summary>主檔路徑（不存在也有）</summary>
    public required string Path { get; init; }

    /// <summary>符號連結指向的實際檔案</summary>
    public string? LinkTarget { get; init; }

    /// <summary>檔案存在</summary>
    public bool Exists { get; init; }

    /// <summary>內容（同一層有多個檔時串起來；不存在是空字串）</summary>
    public string Text { get; init; } = string.Empty;

    public TextMeasure Measure { get; init; } = TextMeasure.Empty;

    /// <summary>用這份的帳戶 id</summary>
    public List<string> AccountIds { get; } = new();

    /// <summary>這個 CLI 另外會一併讀的另一份（Grok 的 Claude 相容會讀 ~/.claude/CLAUDE.md）：那一份的 Key；沒有是 null</summary>
    public string? AlsoLoadsKey { get; init; }

    /// <summary>各家的單檔上限（Antigravity 24,000 位元組，超過截斷）；沒有是 null</summary>
    public long? MaxBytes { get; init; }
}

/// <summary>一次掃描的結果</summary>
internal sealed class HealthScanResult
{
    public required DateTime CheckedAt { get; init; }

    public List<AccountScan> Accounts { get; } = new();

    public List<InstructionDoc> Docs { get; } = new();
}

/// <summary>掃描器（背景執行緒呼叫；測試換成假的）</summary>
internal interface IHealthScanner
{
    HealthScanResult Scan(IReadOnlyList<HealthAccountInput> accounts, CancellationToken ct);
}
