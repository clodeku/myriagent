using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Health;

/// <summary>
/// 健康度的掃描（背景執行緒）：只讀，不寫任何檔。每個帳戶讀它每次對話都會載入的指示（主檔、@ 匯入、使用者層規則、Grok 的 Claude 相容）、
/// Claude 的自動記憶索引、skill 與外掛、MCP 伺服器、設定檔；再整理出電腦層級的指示檔（各 CLI 使用者層的那一份）給 InstructionCompareRules 比較。
/// 讀不到的檔一律當不存在（記錄檔留一行），不讓一個檔案的錯誤拖垮整份分析
/// </summary>
internal sealed class HealthScanner(HealthMachine machine) : IHealthScanner
{
    /// <summary>超過這個大小的指示檔不讀內容（Claude Code 也會略過 4 MiB 以上的檔）</summary>
    private const long MaxReadBytes = 4L * 1024 * 1024;

    /// <summary>@ 匯入最多幾層（Claude Code：最多四跳）</summary>
    private const int MaxImportDepth = 4;

    /// <summary>一個帳戶最多追幾個匯入的檔（避免循環與失控）</summary>
    private const int MaxImportFiles = 50;

    /// <summary>Grok 在 GROK_HOME 那一層認的檔名（依序；同一個檔不分大小寫只算一次）</summary>
    private static readonly string[] GrokFileNames = ["Agents.md", "Claude.md", "CLAUDE.md", "CLAUDE.local.md", "AGENT.md", "AGENTS.md"];

    /// <summary>Grok 的 Claude 相容會讀的使用者層檔名</summary>
    private static readonly string[] GrokClaudeCompatNames = ["CLAUDE.md", "CLAUDE.local.md"];

    public HealthScanResult Scan(IReadOnlyList<HealthAccountInput> accounts, CancellationToken ct)
    {
        var result = new HealthScanResult { CheckedAt = DateTime.UtcNow };
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                result.Accounts.Add(ScanAccount(account, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"健康度分析帳戶 {account.Id} 失敗：{ex.Message}");
            }
        }
        BuildDocs(accounts, result);
        return result;
    }

    private AccountScan ScanAccount(HealthAccountInput account, CancellationToken ct)
    {
        var scan = new AccountScan { Account = account };
        switch (account.Provider)
        {
            case AgentProvider.Claude:
                ScanClaude(scan, ct);
                break;
            case AgentProvider.Codex:
                ScanCodex(scan);
                break;
            case AgentProvider.Grok:
                ScanGrok(scan);
                break;
            case AgentProvider.Antigravity:
                ScanAntigravity(scan);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(account), account.Provider, null);
        }
        return scan;
    }

    // ───── Claude ─────
    private void ScanClaude(AccountScan scan, CancellationToken ct)
    {
        var dir = scan.Account.ConfigDirectory;
        var main = Path.Combine(dir, "CLAUDE.md");
        scan.MainInstructionPath = main;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddWithImports(scan, main, "main", 0, seen);
        ScanRules(scan, Path.Combine(dir, "rules"), seen);

        // 自動記憶：共用設定時記憶寫在來源（系統預設帳戶）的 projects\ 底下
        scan.MemoryIndexes = ScanMemory(Path.Combine(scan.Account.SharedSourceDirectory ?? dir, "projects"), ct);

        // settings.json：外掛與 skill 清單的設定
        var settingsPath = Path.Combine(dir, "settings.json");
        JObject? settings = null;
        if (File.Exists(settingsPath))
        {
            scan.SettingsPath = settingsPath;
            var text = ReadText(settingsPath);
            if (text != null && !string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    settings = JToken.Parse(text) as JObject;
                    if (settings == null)
                        scan.SettingsError = "最上層不是物件";
                }
                catch (JsonException ex)
                {
                    scan.SettingsError = Shorten(ex.Message);
                }
            }
        }

        // skill：使用者層（共用設定時是連到來源的 junction，照樣列得到）與 claude.ai 同步下來的
        var listing = new StringBuilder();
        var skills = 0;
        foreach (var skill in SkillFiles(Path.Combine(dir, "skills")))
        {
            skills++;
            AddListing(listing, scan, skill.Name, skill.File);
        }
        foreach (var command in MarkdownFiles(Path.Combine(dir, "commands")))
        {
            skills++;
            AddListing(listing, scan, Path.GetFileNameWithoutExtension(command), command);
        }
        var agents = MarkdownFiles(Path.Combine(dir, "agents")).Count();

        // 外掛：enabledPlugins 為 true 的，照 installed_plugins.json 的安裝位置數它帶來的東西
        var mcp = new List<string>();
        var plugins = new List<PluginStat>();
        var pluginSkills = 0;
        var installed = ReadInstalledPlugins(Path.Combine(dir, "plugins", "installed_plugins.json"));
        if (settings?["enabledPlugins"] is JObject enabled)
        {
            foreach (var (id, value) in enabled)
            {
                if (value?.Type != JTokenType.Boolean || !value.Value<bool>())
                    continue;
                if (!installed.TryGetValue(id, out var path) || !Directory.Exists(path))
                {
                    plugins.Add(new PluginStat(id, 0, 0, 0, false, false, Missing: true));
                    continue;
                }
                var pluginSkillFiles = SkillFiles(Path.Combine(path, "skills")).ToList();
                var pluginCommands = MarkdownFiles(Path.Combine(path, "commands")).ToList();
                var pluginAgents = MarkdownFiles(Path.Combine(path, "agents")).Count();
                foreach (var skill in pluginSkillFiles)
                    AddListing(listing, scan, skill.Name, skill.File);
                foreach (var command in pluginCommands)
                    AddListing(listing, scan, Path.GetFileNameWithoutExtension(command), command);
                pluginSkills += pluginSkillFiles.Count + pluginCommands.Count;
                agents += pluginAgents;
                var pluginMcp = PluginMcpServers(path);
                foreach (var name in pluginMcp)
                    mcp.Add(name + "（" + PluginName(id) + "）");
                plugins.Add(new PluginStat(id, pluginSkillFiles.Count, pluginCommands.Count, pluginAgents, pluginMcp.Count > 0,
                    File.Exists(Path.Combine(path, "hooks", "hooks.json")), Missing: false));
            }
        }
        scan.Skills = skills + pluginSkills;
        scan.PluginSkills = pluginSkills;
        scan.Agents = agents;
        scan.Plugins = plugins;
        scan.SkillListingTokens = HealthTextRules.EstimateTokens(listing.ToString());

        // 使用者層的 MCP 伺服器在 .claude.json 的 mcpServers
        if (scan.Account.ClaudeGlobalConfig is { } globalConfig && File.Exists(globalConfig))
        {
            scan.StateFilePath = globalConfig;
            scan.StateFileBytes = FileLength(globalConfig);
            if (scan.StateFileBytes is { } size && size <= 64L * 1024 * 1024 && ReadText(globalConfig, 64L * 1024 * 1024) is { } json)
            {
                try
                {
                    if (JToken.Parse(json) is JObject config && config["mcpServers"] is JObject servers)
                        mcp.InsertRange(0, servers.Properties().Select(p => p.Name));
                }
                catch (JsonException)
                {
                    // .claude.json 讀不懂：CLI 自己會處理，這裡只是少列 MCP
                }
            }
        }
        scan.McpServers = mcp;
    }

    private static string PluginName(string id) => id.Split('@')[0];

    /// <summary>skill 清單的一筆：名稱＋描述（單筆描述最多 1,536 字元）</summary>
    private void AddListing(StringBuilder listing, AccountScan scan, string name, string file)
    {
        var text = ReadText(file, 256 * 1024);
        var fm = text != null ? HealthTextRules.ParseFrontMatter(text) : new Dictionary<string, string>();
        var skillName = HealthTextRules.FrontMatterText(fm, "name") is { Length: > 0 } declared ? declared : name;
        scan.SkillListingChars += HealthTextRules.SkillListingChars(skillName, fm, HealthRules.SkillDescriptionCap);
        var description = HealthTextRules.FrontMatterText(fm, "description");
        var whenToUse = HealthTextRules.FrontMatterText(fm, "when_to_use");
        var desc = whenToUse.Length > 0 ? description + " " + whenToUse : description;
        if (desc.Length > HealthRules.SkillDescriptionCap)
            desc = desc[..HealthRules.SkillDescriptionCap];
        listing.Append(skillName).Append(": ").Append(desc).Append('\n');
    }

    /// <summary>skills\ 底下的 skill（每個資料夾的 SKILL.md）；點開頭的略過，synced\ 再往下一層</summary>
    private static IEnumerable<(string Name, string File)> SkillFiles(string skillsDir)
    {
        foreach (var sub in Directories(skillsDir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.'))
                continue;
            var file = Path.Combine(sub, "SKILL.md");
            if (File.Exists(file))
            {
                yield return (name, file);
                continue;
            }
            if (name.Equals("synced", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var synced in Directories(sub))
                {
                    var syncedFile = Path.Combine(synced, "SKILL.md");
                    if (!Path.GetFileName(synced).StartsWith('.') && File.Exists(syncedFile))
                        yield return (Path.GetFileName(synced), syncedFile);
                }
            }
        }
    }

    private static IEnumerable<string> MarkdownFiles(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly).Where(f => !Path.GetFileName(f).StartsWith('.')).ToList()
                : Enumerable.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    private static IEnumerable<string> Directories(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : Enumerable.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>installed_plugins.json（v2：plugins 的每個 id 是安裝紀錄的陣列）→ id 對安裝位置</summary>
    private static Dictionary<string, string> ReadInstalledPlugins(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadText(path, 8L * 1024 * 1024) is not { } text)
            return result;
        try
        {
            if (JToken.Parse(text) is JObject root && root["plugins"] is JObject plugins)
            {
                foreach (var (id, entries) in plugins)
                {
                    var entry = entries is JArray list ? list.OfType<JObject>().FirstOrDefault() : entries as JObject;
                    if (entry?["installPath"]?.Type == JTokenType.String)
                        result[id] = entry.Value<string>("installPath")!;
                }
            }
        }
        catch (JsonException)
        {
            // 讀不懂就當沒有安裝紀錄
        }
        return result;
    }

    /// <summary>外掛的 MCP 伺服器：.mcp.json 或 .claude-plugin\plugin.json 的 mcpServers</summary>
    private static List<string> PluginMcpServers(string pluginDir)
    {
        var names = new List<string>();
        foreach (var file in new[] { Path.Combine(pluginDir, ".mcp.json"), Path.Combine(pluginDir, ".claude-plugin", "plugin.json") })
        {
            if (ReadText(file, 1024 * 1024) is not { } text)
                continue;
            try
            {
                if (JToken.Parse(text) is JObject root)
                {
                    var servers = root["mcpServers"] as JObject ?? (file.EndsWith(".mcp.json", StringComparison.OrdinalIgnoreCase) ? root : null);
                    if (servers != null)
                        names.AddRange(servers.Properties().Where(p => p.Value is JObject).Select(p => p.Name).Where(n => !names.Contains(n)));
                }
            }
            catch (JsonException)
            {
                // 讀不懂就當沒有
            }
        }
        return names;
    }

    /// <summary>每個專案的 memory\MEMORY.md：行數與大小（不讀超過 4 MiB 的內容）</summary>
    private static List<MemoryIndexStat> ScanMemory(string projectsDir, CancellationToken ct)
    {
        var result = new List<MemoryIndexStat>();
        foreach (var project in Directories(projectsDir))
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(project, "memory", "MEMORY.md");
            if (!File.Exists(file))
                continue;
            var bytes = FileLength(file) ?? 0;
            var text = ReadText(file);
            result.Add(new MemoryIndexStat(Path.GetFileName(project), file, text != null ? HealthTextRules.CountLines(text) : 0, bytes));
        }
        return result;
    }

    /// <summary>使用者層規則（rules\ 底下所有 .md，含子資料夾）：沒有 paths 的每次都載入</summary>
    private void ScanRules(AccountScan scan, string rulesDir, HashSet<string> seen)
    {
        if (!Directory.Exists(rulesDir))
            return;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(rulesDir, "*.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var file in files)
        {
            if (!seen.Add(Path.GetFullPath(file)))
                continue;
            var text = ReadText(file);
            if (text == null)
                continue;
            if (HealthTextRules.HasPathScope(HealthTextRules.ParseFrontMatter(text)))
            {
                scan.ScopedRules++;
                continue;
            }
            scan.Instructions.Add(new InstructionFileStat(file, "rule", HealthTextRules.Measure(text, FileLength(file) ?? 0), LinkTarget(file)));
        }
    }

    /// <summary>加一個指示檔與它的 @ 匯入（最多四跳、50 個檔）；不存在的主檔不加</summary>
    private void AddWithImports(AccountScan scan, string file, string kind, int depth, HashSet<string> seen)
    {
        var full = Path.GetFullPath(file);
        if (!seen.Add(full) || !File.Exists(full))
            return;
        var text = ReadText(full);
        if (text == null)
            return;
        scan.Instructions.Add(new InstructionFileStat(full, kind, HealthTextRules.Measure(text, FileLength(full) ?? 0), LinkTarget(full)));
        if (depth >= MaxImportDepth)
            return;
        // 匯入路徑以連結的實際位置為基準（CLI 讀的是連結指向的檔）
        var baseFile = LinkTarget(full) ?? full;
        foreach (var import in HealthTextRules.FindImports(text))
        {
            if (seen.Count >= MaxImportFiles)
                return;
            var resolved = HealthTextRules.ResolveImport(import, baseFile, machine.UserProfile);
            if (resolved == null)
                continue;
            if (File.Exists(resolved))
                AddWithImports(scan, resolved, "import", depth + 1, seen);
            else if (!Directory.Exists(resolved) && LooksLikeFile(import) && !scan.MissingImports.Contains(import))
                scan.MissingImports.Add(import);
        }
    }

    /// <summary>只把像檔案的（有副檔名）找不到時列為「匯入找不到」：@某人、@套件名稱這類誤認的不提</summary>
    private static bool LooksLikeFile(string import)
        => Path.HasExtension(import) && (import.Contains('/') || import.Contains('\\') || import.StartsWith('~') || import.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    // ───── Codex ─────
    private static void ScanCodex(AccountScan scan)
    {
        var dir = scan.Account.ConfigDirectory;
        var main = CodexInstructionFile(dir);
        scan.MainInstructionPath = main;
        if (File.Exists(main) && ReadText(main) is { } text && !string.IsNullOrWhiteSpace(text))
            scan.Instructions.Add(new InstructionFileStat(main, "main", HealthTextRules.Measure(text, FileLength(main) ?? 0), LinkTarget(main)));

        var listing = new StringBuilder();
        var skills = 0;
        foreach (var skill in SkillFiles(Path.Combine(dir, "skills")))
        {
            skills++;
            var skillText = ReadText(skill.File, 256 * 1024);
            var fm = skillText != null ? HealthTextRules.ParseFrontMatter(skillText) : new Dictionary<string, string>();
            scan.SkillListingChars += HealthTextRules.SkillListingChars(skill.Name, fm, HealthRules.SkillDescriptionCap);
            listing.Append(skill.Name).Append(": ").Append(HealthTextRules.FrontMatterText(fm, "description")).Append('\n');
        }
        scan.Skills = skills;
        scan.SkillListingTokens = HealthTextRules.EstimateTokens(listing.ToString());

        var config = Path.Combine(dir, "config.toml");
        if (File.Exists(config))
        {
            scan.SettingsPath = config;
            scan.McpServers = ReadText(config) is { } toml ? HealthTomlRules.TableNames(toml, "mcp_servers") : new List<string>();
        }
        else
            scan.McpServers = new List<string>();
    }

    /// <summary>Codex 的全域指示：AGENTS.override.md 有內容就用它，否則 AGENTS.md</summary>
    private static string CodexInstructionFile(string dir)
    {
        var over = Path.Combine(dir, "AGENTS.override.md");
        return File.Exists(over) && ReadText(over) is { } text && !string.IsNullOrWhiteSpace(text) ? over : Path.Combine(dir, "AGENTS.md");
    }

    // ───── Grok ─────
    private void ScanGrok(AccountScan scan)
    {
        var dir = scan.Account.ConfigDirectory;
        scan.MainInstructionPath = Path.Combine(dir, "AGENTS.md");
        foreach (var file in GrokHomeFiles(dir))
        {
            if (ReadText(file) is { } text && !string.IsNullOrWhiteSpace(text))
                scan.Instructions.Add(new InstructionFileStat(file, "main", HealthTextRules.Measure(text, FileLength(file) ?? 0), LinkTarget(file)));
        }
        foreach (var rule in MarkdownFiles(Path.Combine(dir, "rules")).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (ReadText(rule) is { } text && !string.IsNullOrWhiteSpace(text))
                scan.Instructions.Add(new InstructionFileStat(rule, "rule", HealthTextRules.Measure(text, FileLength(rule) ?? 0), LinkTarget(rule)));
        }
        if (GrokClaudeCompat(dir))
        {
            var claudeHome = Path.Combine(machine.UserProfile, ".claude");
            foreach (var name in GrokClaudeCompatNames)
            {
                var file = Path.Combine(claudeHome, name);
                if (File.Exists(file) && ReadText(file) is { } text && !string.IsNullOrWhiteSpace(text))
                    scan.Instructions.Add(new InstructionFileStat(file, "compat", HealthTextRules.Measure(text, FileLength(file) ?? 0), LinkTarget(file)));
            }
        }
    }

    /// <summary>GROK_HOME 那一層 grok 會讀的檔（不分大小寫去重）</summary>
    private static List<string> GrokHomeFiles(string dir)
    {
        var result = new List<string>();
        Dictionary<string, string> actual;
        try
        {
            // 取實際的檔名（Windows 不分大小寫：Agents.md 與 AGENTS.md 是同一個檔，顯示時用磁碟上的寫法）
            actual = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly).ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return result;
        }
        foreach (var name in GrokFileNames)
        {
            if (actual.Remove(name, out var file))
                result.Add(file);
        }
        return result;
    }

    /// <summary>grok 的 Claude 相容有沒有開：config.toml 的 [claude_compat] 沒有 imported = true 就會讀 ~/.claude</summary>
    private static bool GrokClaudeCompat(string grokHome)
    {
        var config = Path.Combine(grokHome, "config.toml");
        return !(ReadText(config) is { } toml && HealthTomlRules.BoolValue(toml, "claude_compat", "imported") == true);
    }

    // ───── Antigravity ─────
    private void ScanAntigravity(AccountScan scan)
    {
        // agy 的資料夾是 ~/.gemini/antigravity-cli；全域規則與自訂設定在上一層 ~/.gemini/GEMINI.md 與 ~/.gemini/config/
        var dir = Path.GetFullPath(scan.Account.ConfigDirectory);
        var gemini = Path.GetDirectoryName(dir) ?? dir;
        var globalConfig = Path.Combine(gemini, "config");
        var main = Path.Combine(gemini, "GEMINI.md");
        scan.MainInstructionPath = main;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { main, Path.Combine(globalConfig, "GEMINI.md"), Path.Combine(globalConfig, "AGENTS.md") })
        {
            var full = Path.GetFullPath(candidate);
            if (!seen.Add(full) || !File.Exists(full))
                continue;
            if (ReadText(full) is { } text && !string.IsNullOrWhiteSpace(text))
                scan.Instructions.Add(new InstructionFileStat(full, "main", HealthTextRules.Measure(text, FileLength(full) ?? 0), LinkTarget(full)));
        }
        ScanAntigravityRules(scan, Path.Combine(globalConfig, "rules"), seen);

        // config.json：Antigravity CLI 的設定檔（含 plugins 的 enabled 覆寫）
        var configJsonPath = Path.Combine(dir, "config.json");
        JObject? agyConfig = TryReadJsonConfig(scan, configJsonPath);

        // hooks.json（全域生命週期 hooks）：有語法錯誤時一併提醒
        var globalHooksPath = Path.Combine(globalConfig, "hooks.json");
        if (File.Exists(globalHooksPath))
            _ = TryReadJsonConfig(scan, globalHooksPath);

        // 使用者層 Skills（~/.gemini/config/skills/<name>/SKILL.md）
        var listing = new StringBuilder();
        var skills = 0;
        foreach (var skill in SkillFiles(Path.Combine(globalConfig, "skills")))
        {
            skills++;
            AddListing(listing, scan, skill.Name, skill.File);
        }

        // 外掛（~/.gemini/config/plugins/<plugin>/plugin.json）：預設啟用，config.json 的 plugins[dirName].enabled 優先於 plugin.json 的 disabled
        var mcp = new List<string>();
        var plugins = new List<PluginStat>();
        var pluginSkills = 0;
        var pluginOverrides = agyConfig?["plugins"] as JObject;
        foreach (var pluginDir in Directories(Path.Combine(globalConfig, "plugins")))
        {
            var dirName = Path.GetFileName(pluginDir);
            if (dirName.StartsWith('.'))
                continue;
            var manifestPath = FindAntigravityPluginManifest(pluginDir);
            if (manifestPath == null)
                continue;

            JObject? manifest = null;
            if (ReadText(manifestPath, 512 * 1024) is { } manifestText && !string.IsNullOrWhiteSpace(manifestText))
            {
                try
                {
                    manifest = JToken.Parse(manifestText) as JObject;
                }
                catch (JsonException)
                {
                }
            }

            if (!IsAntigravityPluginEnabled(dirName, manifest, pluginOverrides))
                continue;

            var displayName = manifest?.Value<string>("name") is { Length: > 0 } n ? n : dirName;
            var pSkills = SkillFiles(Path.Combine(pluginDir, "skills")).ToList();
            foreach (var skill in pSkills)
                AddListing(listing, scan, skill.Name, skill.File);
            pluginSkills += pSkills.Count;

            ScanAntigravityRules(scan, Path.Combine(pluginDir, "rules"), seen);

            var pMcp = AntigravityMcpServers(Path.Combine(pluginDir, "mcp_config.json"));
            foreach (var serverName in pMcp)
                mcp.Add(serverName + "（" + displayName + "）");

            var hasHooks = File.Exists(Path.Combine(pluginDir, "hooks.json"));
            plugins.Add(new PluginStat(displayName, pSkills.Count, 0, 0, pMcp.Count > 0, hasHooks, Missing: false));
        }

        // 全域 MCP 伺服器（~/.gemini/config/mcp_config.json）
        var globalMcpPath = Path.Combine(globalConfig, "mcp_config.json");
        if (File.Exists(globalMcpPath))
        {
            var mcpObj = TryReadJsonConfig(scan, globalMcpPath);
            if (mcpObj?["mcpServers"] is JObject servers)
                mcp.InsertRange(0, servers.Properties().Where(p => p.Value is JObject).Select(p => p.Name));
        }

        scan.Skills = skills + pluginSkills;
        scan.PluginSkills = pluginSkills;
        scan.Plugins = plugins;
        scan.McpServers = mcp;
        scan.SkillListingTokens = HealthTextRules.EstimateTokens(listing.ToString());
    }

    private static void ScanAntigravityRules(AccountScan scan, string rulesDir, HashSet<string> seen)
    {
        if (!Directory.Exists(rulesDir))
            return;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(rulesDir, "*.md", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            if (!seen.Add(full))
                continue;
            var text = ReadText(full);
            if (text == null || string.IsNullOrWhiteSpace(text))
                continue;
            if (HealthTextRules.IsAntigravityScopedRule(HealthTextRules.ParseFrontMatter(text)))
            {
                scan.ScopedRules++;
                continue;
            }
            scan.Instructions.Add(new InstructionFileStat(full, "rule", HealthTextRules.Measure(text, FileLength(full) ?? 0), LinkTarget(full)));
        }
    }

    private static string? FindAntigravityPluginManifest(string pluginDir)
    {
        foreach (var rel in new[]
                 {
                     "plugin.json",
                     Path.Combine(".antigravity-plugin", "plugin.json"),
                     Path.Combine(".gemini-plugin", "plugin.json"),
                 })
        {
            var candidate = Path.Combine(pluginDir, rel);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static bool IsAntigravityPluginEnabled(string dirName, JObject? manifest, JObject? configOverrides)
    {
        if (configOverrides?[dirName] is JObject entry && entry["enabled"]?.Type == JTokenType.Boolean)
            return entry.Value<bool>("enabled");
        if (manifest?["disabled"]?.Type == JTokenType.Boolean && manifest.Value<bool>("disabled"))
            return false;
        return true;
    }

    private static List<string> AntigravityMcpServers(string mcpConfigPath)
    {
        var names = new List<string>();
        if (ReadText(mcpConfigPath, 1024 * 1024) is not { } text || string.IsNullOrWhiteSpace(text))
            return names;
        try
        {
            if (JToken.Parse(text) is JObject root && root["mcpServers"] is JObject servers)
                names.AddRange(servers.Properties().Where(p => p.Value is JObject).Select(p => p.Name));
        }
        catch (JsonException)
        {
        }
        return names;
    }

    private static JObject? TryReadJsonConfig(AccountScan scan, string path)
    {
        if (!File.Exists(path))
            return null;
        scan.SettingsPath ??= path;
        var text = ReadText(path);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            if (JToken.Parse(text) is JObject obj)
                return obj;
            if (scan.SettingsError == null)
            {
                scan.SettingsPath = path;
                scan.SettingsError = "最上層不是物件";
            }
        }
        catch (JsonException ex)
        {
            if (scan.SettingsError == null)
            {
                scan.SettingsPath = path;
                scan.SettingsError = Shorten(ex.Message);
            }
        }
        return null;
    }

    // ───── 電腦層級的指示檔 ─────
    private void BuildDocs(IReadOnlyList<HealthAccountInput> accounts, HealthScanResult result)
    {
        var docs = new Dictionary<string, InstructionDoc>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            // 共用系統預設帳戶設定的隔離帳戶：指示檔每次啟動前與來源同步，不另外列
            if (account.SharedSourceDirectory != null)
                continue;
            var (path, files, maxBytes) = account.Provider switch
            {
                AgentProvider.Claude => (Path.Combine(account.ConfigDirectory, "CLAUDE.md"), new List<string> { Path.Combine(account.ConfigDirectory, "CLAUDE.md") }, (long?)null),
                AgentProvider.Codex => (CodexInstructionFile(account.ConfigDirectory), new List<string> { CodexInstructionFile(account.ConfigDirectory) }, null),
                AgentProvider.Grok => (Path.Combine(account.ConfigDirectory, "AGENTS.md"), GrokHomeFiles(account.ConfigDirectory), null),
                AgentProvider.Antigravity => GeminiDoc(account),
                _ => throw new ArgumentOutOfRangeException(nameof(accounts), account.Provider, null),
            };
            var key = Path.GetFullPath(path).ToLowerInvariant();
            if (docs.TryGetValue(key, out var existing))
            {
                existing.AccountIds.Add(account.Id);
                continue;
            }
            var texts = files.Where(File.Exists).Select(f => ReadText(f) ?? string.Empty).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var text = string.Join("\n\n", texts);
            var exists = files.Any(File.Exists);
            var bytes = files.Where(File.Exists).Sum(f => FileLength(f) ?? 0);
            var doc = new InstructionDoc
            {
                Key = key,
                Provider = account.Provider,
                Label = DocLabel(account),
                Path = path,
                LinkTarget = File.Exists(path) ? LinkTarget(path) : null,
                Exists = exists,
                Text = text,
                Measure = HealthTextRules.Measure(text, bytes),
                MaxBytes = maxBytes,
                AlsoLoadsKey = account.Provider == AgentProvider.Grok && GrokClaudeCompat(account.ConfigDirectory)
                    ? Path.GetFullPath(Path.Combine(machine.UserProfile, ".claude", "CLAUDE.md")).ToLowerInvariant()
                    : null,
            };
            doc.AccountIds.Add(account.Id);
            docs[key] = doc;
        }
        // Grok 的 Claude 相容指向的檔不在清單裡（沒有 Claude 帳戶、或 Claude 改了 CLAUDE_CONFIG_DIR）就不算
        foreach (var doc in docs.Values.Where(d => d.AlsoLoadsKey != null && !docs.ContainsKey(d.AlsoLoadsKey)).ToList())
            docs[doc.Key] = CloneWithoutAlso(doc);
        result.Docs.AddRange(docs.Values.OrderBy(d => ProviderOrder(d.Provider)).ThenBy(d => d.Label, StringComparer.Ordinal));
    }

    private static InstructionDoc CloneWithoutAlso(InstructionDoc doc)
    {
        var clone = new InstructionDoc
        {
            Key = doc.Key,
            Provider = doc.Provider,
            Label = doc.Label,
            Path = doc.Path,
            LinkTarget = doc.LinkTarget,
            Exists = doc.Exists,
            Text = doc.Text,
            Measure = doc.Measure,
            MaxBytes = doc.MaxBytes,
        };
        clone.AccountIds.AddRange(doc.AccountIds);
        return clone;
    }

    private static (string Path, List<string> Files, long? MaxBytes) GeminiDoc(HealthAccountInput account)
    {
        var gemini = Path.GetDirectoryName(Path.GetFullPath(account.ConfigDirectory)) ?? account.ConfigDirectory;
        var globalConfig = Path.Combine(gemini, "config");
        var main = Path.Combine(gemini, "GEMINI.md");
        var files = new List<string>
        {
            main,
            Path.Combine(globalConfig, "GEMINI.md"),
            Path.Combine(globalConfig, "AGENTS.md"),
        };
        return (main, files, HealthRules.AntigravityFileMaxBytes);
    }

    private static int ProviderOrder(AgentProvider provider) => Array.IndexOf(AgentProviders.All.ToArray(), provider);

    /// <summary>電腦層級指示檔的名稱：系統預設帳戶用產品名，隔離帳戶加上帳戶名稱</summary>
    private static string DocLabel(HealthAccountInput account)
    {
        var product = AgentProviders.ProductName(account.Provider);
        return account.IsSystemDefault ? product : product + "・" + account.Label;
    }

    // ───── 檔案 ─────
    /// <summary>讀 UTF-8 文字（共享讀寫刪，CLI 正在寫也讀得到）；不存在、太大、讀不到回 null</summary>
    internal static string? ReadText(string path, long maxBytes = MaxReadBytes)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes)
                return null;
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"健康度分析讀不到 {path}：{ex.Message}");
            return null;
        }
    }

    private static long? FileLength(string path)
    {
        try
        {
            // 符號連結本身的大小是 0：取它指向的實際檔案
            var info = new FileInfo(path);
            return info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true } target ? target.Length : info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>符號連結（檔案或上層資料夾是連結都算）指向的實際檔案；不是連結回 null</summary>
    internal static string? LinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var direct = info.LinkTarget != null ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName : null;
            if (direct != null)
                return direct;
            // 上層資料夾是 junction／符號連結（例：~/.claude\docs → dotfiles）
            var dir = info.Directory;
            var tail = info.Name;
            while (dir != null)
            {
                if (dir.LinkTarget != null && dir.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    return Path.Combine(target.FullName, tail);
                tail = Path.Combine(dir.Name, tail);
                dir = dir.Parent;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string Shorten(string message) => message.Length > 160 ? message[..160] + "…" : message;
}
