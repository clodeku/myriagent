using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>共用的全域指示檔（CLAUDE.md／AGENTS.md）這次啟動前要怎麼同步</summary>
internal enum InstructionSyncAction
{
    /// <summary>兩邊一樣（或兩邊都沒有）</summary>
    None,

    /// <summary>來源改過（或第一次、帳戶沒有自己的內容）：來源 → 帳戶</summary>
    CopyToAccount,

    /// <summary>只有帳戶這份改過（或來源沒有、帳戶有）：帳戶 → 來源</summary>
    CopyToSource,

    /// <summary>第一次共用、兩邊都有不同的內容：帳戶的內容附加到來源最後，再整份複製回帳戶</summary>
    MergeIntoSource,

    /// <summary>上次同步之後兩邊都改過：保留來源，帳戶那份另存備份，再以來源取代</summary>
    KeepSourceBackupAccount,

    /// <summary>來源被刪掉、帳戶這份沒改過：帳戶的也刪掉</summary>
    DeleteAccount,
}

/// <summary>帳戶原有的一個記憶檔要怎麼併進來源</summary>
internal enum MemoryMergeAction
{
    /// <summary>來源沒有：照原檔名複製過去</summary>
    Copy,

    /// <summary>來源有、內容相同</summary>
    Skip,

    /// <summary>索引（MEMORY.md）兩邊不同：把來源沒有的行附加上去</summary>
    AppendIndex,

    /// <summary>來源有同名但內容不同：以帶帳戶代碼的檔名另存一份，並在索引加一行</summary>
    CopyRenamed,
}

/// <summary>
/// 隔離帳戶共用系統預設帳戶設定的純函式規則（SharedCustomizations 的決策）：哪些項目、全域指示的雙向同步、記憶的合併、
/// CLI 自動記憶資料夾用的專案根目錄、要轉給帳戶的 Claude MCP 伺服器。登入憑證（.credentials.json、auth.json、.claude.json 的身分）一律不共用
/// </summary>
internal static class SharedCustomizationRules
{
    /// <summary>帳戶設定資料夾裡放共用狀態、MCP 設定檔與備份的資料夾（CLI 不認得的名稱，不會讀它）</summary>
    public const string StateFolderName = ".myriagent-shared";

    /// <summary>CLI 自動記憶的索引檔（每個專案的 memory\ 底下；只有它會整份載入）</summary>
    public const string MemoryIndexFileName = "MEMORY.md";

    /// <summary>這個帳戶現在要不要共用：隔離帳戶、這種 CLI 支援、使用者沒關掉、不在精簡模式（精簡模式本來就不載入這些）</summary>
    public static bool IsActive(AgentAccount account)
        => !account.IsSystemDefault
           && AccountRules.SupportsSharedCustomizations(account.Provider)
           && account.ShareCustomizations
           && !account.SafeMode;

    /// <summary>全域指示檔的檔名（在設定資料夾的最上層）</summary>
    public static string InstructionFileName(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "CLAUDE.md",
        AgentProvider.Codex or AgentProvider.Grok => "AGENTS.md",
        AgentProvider.Antigravity => throw new ArgumentException($"{provider} 不支援隔離帳戶共用設定", nameof(provider)),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 整個資料夾以 junction 連到來源的項目：Claude 的自訂子代理（agents）、斜線指令（commands）、輸出風格（output-styles）；
    /// Codex 的自訂提示（prompts）；Grok 的規則資料夾（rules）。裡面沒有任何帳戶自己的東西
    /// </summary>
    public static IReadOnlyList<string> LinkedDirectories(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => new[] { "agents", "commands", "output-styles" },
        AgentProvider.Codex => new[] { "prompts" },
        AgentProvider.Grok => new[] { "rules" },
        AgentProvider.Antigravity => Array.Empty<string>(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>skills 資料夾的名稱（逐一以 junction 連過去：資料夾裡有帳戶自己的東西，不能整個連）</summary>
    public const string SkillsDirectoryName = "skills";

    /// <summary>Antigravity 內建的保留工作流名稱（跨 CLI 共用時不把同名 skill 連進 ~/.gemini/config/skills，避免蓋掉內建指令）</summary>
    private static readonly HashSet<string> AntigravityReservedSkillNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "goal", "compact", "schedule", "browser", "plan", "grill-me", "teamwork", "learn", "boost",
    };

    /// <summary>
    /// skills\ 底下這個名稱要不要共用：點開頭的一律不要（Codex 內建的 .system、CLI 的暫存）；Claude 的 synced\ 是各帳戶從 claude.ai 同步下來的；
    /// Antigravity 排除內建工作流名稱
    /// </summary>
    public static bool IsSharedSkill(AgentProvider provider, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name[0] == '.')
            return false;
        return provider switch
        {
            AgentProvider.Claude => !name.Equals("synced", StringComparison.OrdinalIgnoreCase),
            AgentProvider.Codex or AgentProvider.Grok => true,
            AgentProvider.Antigravity => !AntigravityReservedSkillNames.Contains(name),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }

    /// <summary>
    /// 跨四家 CLI 共用時，各 CLI 系統預設來源設定資料夾對應的全域指示檔完整路徑：
    /// Antigravity 的來源設定資料夾是 ~/.gemini/antigravity-cli，全域指示在上一層 ~/.gemini/GEMINI.md
    /// </summary>
    public static string CrossCliInstructionPath(AgentProvider provider, string sourceDirectory)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        return provider switch
        {
            AgentProvider.Claude => Path.Combine(dir, "CLAUDE.md"),
            AgentProvider.Codex or AgentProvider.Grok => Path.Combine(dir, "AGENTS.md"),
            AgentProvider.Antigravity => Path.Combine(Path.GetDirectoryName(dir) ?? dir, "GEMINI.md"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }

    /// <summary>
    /// 跨四家 CLI 共用時，各 CLI 系統預設來源設定資料夾對應的使用者層 skills 資料夾完整路徑：
    /// Antigravity 的全域 skills 在上一層的 ~/.gemini/config/skills
    /// </summary>
    public static string CrossCliSkillsDirectory(AgentProvider provider, string sourceDirectory)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        return provider switch
        {
            AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Grok => Path.Combine(dir, SkillsDirectoryName),
            AgentProvider.Antigravity => Path.Combine(Path.GetDirectoryName(dir) ?? dir, "config", SkillsDirectoryName),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }

    /// <summary>
    /// Grok 的系統預設資料夾（~/.grok）是否開著 Claude 相容（預設開啟：自動載入 ~/.claude/CLAUDE.md 與 ~/.claude/skills）：
    /// 只有 config.toml 的 [claude_compat] 明確寫 imported = true 時才不會讀 ~/.claude
    /// </summary>
    public static bool IsGrokClaudeCompatEnabled(string? grokConfigToml)
        => !(grokConfigToml != null && Health.HealthTomlRules.BoolValue(grokConfigToml, "claude_compat", "imported") == true);

    /// <summary>跨 CLI 第一次共用時合併不同 CLI 原有的全域指示（標明來源 CLI；換行一律 \n）</summary>
    public static string MergeCrossCliInstructions(string existing, string incoming, string providerLabel)
        => existing.Replace("\r\n", "\n").TrimEnd() + "\n\n"
           + $"<!-- 以下來自 {providerLabel}：Clode Myriagent 開啟跨 CLI 共用設定時合併進來 -->\n"
           + incoming.Replace("\r\n", "\n").Trim() + "\n";

    /// <summary>內容的雜湊（比對同步狀態用；換行一律當 \n，避免編輯器換行改變就當成改過）</summary>
    public static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")))).ToLowerInvariant();

    /// <summary>沒有內容：檔案不存在或只有空白（Codex 預設會留一個 0 位元組的 AGENTS.md）</summary>
    public static bool IsBlank(string? content) => string.IsNullOrWhiteSpace(content);

    /// <summary>
    /// 全域指示的雙向同步：以上次同步完的雜湊判斷是哪一邊改的。<paramref name="lastHash"/> 為 null＝第一次共用（或狀態檔不見了）：
    /// 帳戶沒有自己的內容就拿來源的，來源沒有就拿帳戶的，兩邊都有不同的內容就合併進來源（使用者選的「合併進來源」）。
    /// 之後只有一邊改過就以那一邊為準；兩邊都改過以來源為準、帳戶那份另存備份。帳戶的檔被刪掉不會刪來源（改從來源補回）
    /// </summary>
    public static InstructionSyncAction DecideInstructionSync(string? source, string? account, string? lastHash)
    {
        bool sourceBlank = IsBlank(source), accountBlank = IsBlank(account);
        if (sourceBlank && accountBlank)
            return InstructionSyncAction.None;
        if (!sourceBlank && !accountBlank && Hash(source!) == Hash(account!))
            return InstructionSyncAction.None;

        if (lastHash == null)
        {
            if (accountBlank)
                return InstructionSyncAction.CopyToAccount;
            return sourceBlank ? InstructionSyncAction.CopyToSource : InstructionSyncAction.MergeIntoSource;
        }

        bool accountChanged = accountBlank ? lastHash != BlankHash : Hash(account!) != lastHash;
        bool sourceChanged = sourceBlank ? lastHash != BlankHash : Hash(source!) != lastHash;
        if (!accountChanged)
            return sourceBlank ? InstructionSyncAction.DeleteAccount : InstructionSyncAction.CopyToAccount;
        if (!sourceChanged)
            return accountBlank ? InstructionSyncAction.CopyToAccount : InstructionSyncAction.CopyToSource;
        return accountBlank ? InstructionSyncAction.CopyToAccount : InstructionSyncAction.KeepSourceBackupAccount;
    }

    /// <summary>同步完沒有內容時記下的雜湊（兩邊都沒有、或刪掉之後）</summary>
    public static readonly string BlankHash = Hash(string.Empty);

    /// <summary>同步完記下的雜湊：沒有內容記 BlankHash</summary>
    public static string SyncedHash(string? content) => IsBlank(content) ? BlankHash : Hash(content!);

    /// <summary>第一次共用時把帳戶原有的全域指示附加到來源最後（標明來自哪個帳戶；換行一律 \n）</summary>
    public static string MergeInstructions(string source, string account, string accountLabel)
        => source.Replace("\r\n", "\n").TrimEnd() + "\n\n"
           + $"<!-- 以下來自帳戶「{accountLabel}」：Clode Myriagent 開始共用設定時合併進來 -->\n"
           + account.Replace("\r\n", "\n").Trim() + "\n";

    /// <summary>帳戶原有的記憶檔要怎麼併進來源（relativePath 是 memory\ 底下的相對路徑）</summary>
    public static MemoryMergeAction PlanMemoryFile(string relativePath, bool targetExists, bool sameContent)
    {
        if (!targetExists)
            return MemoryMergeAction.Copy;
        if (sameContent)
            return MemoryMergeAction.Skip;
        return string.Equals(relativePath, MemoryIndexFileName, StringComparison.OrdinalIgnoreCase)
            ? MemoryMergeAction.AppendIndex
            : MemoryMergeAction.CopyRenamed;
    }

    /// <summary>同名衝突時另存的名稱：副檔名前加帳戶 id 的前 8 碼（notes.md → notes.1a2b3c4d.md）</summary>
    public static string RenamedForAccount(string relativePath, string accountId)
    {
        var suffix = accountId.Length > 8 ? accountId[..8] : accountId;
        var directory = Path.GetDirectoryName(relativePath);
        var name = Path.GetFileNameWithoutExtension(relativePath) + "." + suffix + Path.GetExtension(relativePath);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    /// <summary>把帳戶索引裡來源沒有的行（去掉前後空白比對、略過空行）依序附加到來源索引最後；沒有要加的回原文</summary>
    public static string AppendIndexLines(string sourceIndex, string accountIndex)
    {
        var existing = new HashSet<string>(SplitLines(sourceIndex).Select(l => l.Trim()), StringComparer.Ordinal);
        var added = SplitLines(accountIndex).Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0 && existing.Add(l.Trim())).ToList();
        if (added.Count == 0)
            return sourceIndex;
        var head = sourceIndex.Replace("\r\n", "\n").TrimEnd('\n');
        return (head.Length > 0 ? head + "\n" : string.Empty) + string.Join("\n", added) + "\n";
    }

    /// <summary>另存的記憶檔在索引的那一行（照 CLI 索引的格式：- [標題](檔名) — 說明）</summary>
    public static string IndexLineForRenamed(string renamedRelativePath, string originalRelativePath, string accountLabel)
        => $"- [{Path.GetFileNameWithoutExtension(originalRelativePath)}（帳戶「{accountLabel}」的版本）]({renamedRelativePath.Replace('\\', '/')}) — 開始共用設定時兩邊內容不同，另存一份";

    private static IEnumerable<string> SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// CLI 自動記憶（&lt;設定資料夾&gt;\projects\&lt;名稱&gt;\memory）用的專案根目錄，照 CLI 2.1.308 的 defaultKey：
    /// 在 git worktree 裡用主 repo 的根目錄（working_copy）、在 git 裡用 repo 根目錄（git）、都不是就是工作資料夾（project_root）。
    /// worktree 的判斷同 CLI：.git 檔的 gitdir 指到 &lt;共同 .git&gt;\worktrees\&lt;名稱&gt;、那裡的 commondir 指回共同 .git、gitdir 反指回這個 worktree，
    /// 任何一項對不上就當成一般的 repo 根目錄。<paramref name="readFile"/> 讀不到回 null
    /// </summary>
    public static string MemoryProjectRoot(string workingDirectory, Func<string, bool> directoryExists, Func<string, string?> readFile)
    {
        var start = Path.GetFullPath(workingDirectory);
        for (var dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var dotGit = Path.Combine(dir, ".git");
            if (directoryExists(dotGit))
                return Path.TrimEndingDirectorySeparator(dir);
            if (readFile(dotGit) is { } gitFile)
                return CanonicalWorktreeRoot(Path.TrimEndingDirectorySeparator(dir), gitFile, readFile);
        }
        return Path.TrimEndingDirectorySeparator(start);
    }

    private static string CanonicalWorktreeRoot(string root, string gitFile, Func<string, string?> readFile)
    {
        try
        {
            var line = gitFile.Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                return root;
            var gitDir = Path.GetFullPath(Path.Combine(root, line[7..].Trim()));
            if (readFile(Path.Combine(gitDir, "commondir")) is not { } commonDirText)
                return root;
            var commonDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(gitDir, commonDirText.Trim())));
            if (!SamePath(Path.GetDirectoryName(gitDir), Path.Combine(commonDir, "worktrees")))
                return root;
            if (readFile(Path.Combine(gitDir, "gitdir")) is not { } backText)
                return root;
            var back = Path.GetFullPath(Path.Combine(gitDir, backText.Trim()));
            if (!SamePath(back, Path.Combine(root, ".git")))
                return root;
            // 共同的 .git 在主 repo 的根目錄底下；bare repo 的 worktree 沒有主要的工作資料夾，照一般 repo 處理
            if (!string.Equals(Path.GetFileName(commonDir), ".git", StringComparison.OrdinalIgnoreCase))
                return root;
            return Path.GetDirectoryName(commonDir) ?? root;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return root;
        }
    }

    private static bool SamePath(string? a, string? b)
        => a != null && b != null
           && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>共用的自動記憶資料夾：來源設定資料夾的 projects\&lt;專案根目錄的資料夾名稱&gt;\memory（和系統預設帳戶在同一個專案寫的是同一個資料夾）</summary>
    public static string SharedMemoryDirectory(string sourceConfigDirectory, string projectRoot)
        => Path.Combine(sourceConfigDirectory, "projects", ClaudeStreamRules.ProjectFolderName(projectRoot), "memory");

    /// <summary>.claude.json 的 projects 用的鍵：正斜線的完整路徑（實測 "C:/repo/SampleShop"）；專案根目錄與工作資料夾各一個，相同就只有一個</summary>
    public static IReadOnlyList<string> ClaudeProjectKeys(string projectRoot, string workingDirectory)
    {
        var keys = new List<string> { ToProjectKey(projectRoot) };
        var cwd = ToProjectKey(workingDirectory);
        if (!keys.Contains(cwd, StringComparer.OrdinalIgnoreCase))
            keys.Add(cwd);
        return keys;
    }

    private static string ToProjectKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');

    /// <summary>
    /// 讀 .claude.json 的 MCP 伺服器：最上層的 mcpServers（user 層）與 projects 裡這幾個鍵（不分大小寫、斜線方向不拘）的 mcpServers（local 層，
    /// 後面的鍵蓋過前面的同名）。設定檔可能有好幾 MB：逐一略過其他欄位，不整份載入。讀不懂回兩個 null
    /// </summary>
    public static (JObject? User, JObject? Project) ReadClaudeMcpServers(TextReader json, IReadOnlyList<string> projectKeys)
    {
        try
        {
            using var reader = new JsonTextReader(json) { DateParseHandling = DateParseHandling.None, CloseInput = false };
            if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                return (null, null);
            JObject? user = null;
            var found = new Dictionary<int, JObject>();
            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                var name = (string?)reader.Value;
                if (!reader.Read())
                    return (null, null);
                if (name == "mcpServers" && reader.TokenType == JsonToken.StartObject)
                {
                    user = JObject.Load(reader);
                }
                else if (name == "projects" && reader.TokenType == JsonToken.StartObject)
                {
                    while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                    {
                        var key = ((string?)reader.Value ?? string.Empty).Replace('\\', '/').TrimEnd('/');
                        if (!reader.Read())
                            return (null, null);
                        int index = IndexOfKey(projectKeys, key);
                        if (index >= 0 && reader.TokenType == JsonToken.StartObject && JObject.Load(reader)["mcpServers"] is JObject servers)
                            found[index] = servers;
                        else
                            reader.Skip();
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            JObject? project = null;
            foreach (var pair in found.OrderBy(p => p.Key))
            {
                project ??= new JObject();
                foreach (var server in pair.Value.Properties())
                    project[server.Name] = server.Value.DeepClone();
            }
            return (user, project);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static int IndexOfKey(IReadOnlyList<string> keys, string key)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (string.Equals(keys[i].TrimEnd('/'), key, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 要以 --mcp-config 帶給隔離帳戶的 Claude MCP 設定（{"mcpServers":{…}}）：來源的 user 層加上這個專案的 local 層（同名以 local 為準），
    /// 帳戶自己的設定（它的 .claude.json）已經有的名稱不給——帳戶自己的優先，也不會出現兩個同名的伺服器。沒有可給的回 null
    /// </summary>
    public static JObject? BuildClaudeMcpConfig(JObject? userServers, JObject? projectServers, IReadOnlyCollection<string> accountServerNames)
    {
        var own = new HashSet<string>(accountServerNames, StringComparer.Ordinal);
        var servers = new JObject();
        foreach (var source in new[] { userServers, projectServers })
        {
            if (source == null)
                continue;
            foreach (var server in source.Properties())
            {
                if (server.Value is JObject definition && !own.Contains(server.Name))
                    servers[server.Name] = definition.DeepClone();
            }
        }
        return servers.Count == 0 ? null : new JObject { ["mcpServers"] = servers };
    }

    /// <summary>帳戶自己的 Claude MCP 伺服器名稱（user 層與這個專案的 local 層）</summary>
    public static IReadOnlyList<string> ServerNames(JObject? userServers, JObject? projectServers)
        => new[] { userServers, projectServers }.Where(o => o != null).SelectMany(o => o!.Properties().Select(p => p.Name)).Distinct(StringComparer.Ordinal).ToList();
}
