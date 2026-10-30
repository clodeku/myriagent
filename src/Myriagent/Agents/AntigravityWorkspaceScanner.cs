using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Health;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>Antigravity 可在首輪以 <c>--agent</c> 指定的自訂代理定義（來自工作區 <c>.agents/agents</c>、全域 <c>~/.gemini/config/agents</c> 或已啟用外掛）</summary>
internal sealed record AntigravityCustomAgent(string Name, string Label, string? Description);

/// <summary>工作資料夾與全域設定動態掃描出的 Antigravity 斜線指令（Skills／Workflows）與自訂代理清單</summary>
internal sealed record AntigravityWorkspaceCustomizations(
    IReadOnlyList<AgentSlashCommand> Commands,
    IReadOnlyList<AntigravityCustomAgent> Agents)
{
    public static AntigravityWorkspaceCustomizations Empty { get; } = new(
        Array.Empty<AgentSlashCommand>(),
        Array.Empty<AntigravityCustomAgent>());
}

/// <summary>
/// 唯讀掃描工作資料夾下的 Antigravity 專案層級自訂項（<c>.agents/</c>、<c>_agents/</c>、<c>.agent/</c> 的 <c>skills/*/SKILL.md</c>、
/// <c>workflows/*.md</c>、<c>agents/*.md</c>）以及全域與外掛的 <c>agents/*.md</c>，不啟動任何 CLI 子程序、絕不鎖檔
/// </summary>
internal static partial class AntigravityWorkspaceScanner
{
    private const int MaxMarkdownFileBytes = 64 * 1024;
    private const int MaxParentChainDepth = 16;
    private static readonly string[] WorkspaceRoots = [".agents", "_agents", ".agent", "_agent"];

    /// <summary>一次掃描指定工作資料夾與 Antigravity 設定資料夾的專案斜線指令與自訂代理</summary>
    public static AntigravityWorkspaceCustomizations Scan(string? workingDirectory, string? agyConfigDir)
        => new(
            DiscoverWorkspaceCommands(workingDirectory),
            DiscoverCustomAgents(workingDirectory, agyConfigDir));

    /// <summary>掃描工作資料夾（並往上追溯至 git 根目錄）內專案層級的 Skills 與 Workflows（排除 frontmatter 標記 <c>disable-slash-command: true</c> 的項目）</summary>
    public static IReadOnlyList<AgentSlashCommand> DiscoverWorkspaceCommands(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !AgentAttachmentRules.IsLocalDrivePath(workingDirectory) || !Directory.Exists(workingDirectory))
            return Array.Empty<AgentSlashCommand>();

        var result = new List<AgentSlashCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in EnumerateWorkspaceChain(workingDirectory))
        {
            foreach (var rootName in WorkspaceRoots)
            {
                var rootDir = Path.Combine(dir, rootName);
                if (!Directory.Exists(rootDir) || IsReparsePoint(rootDir))
                    continue;
                ScanSkillDirectories(Path.Combine(rootDir, "skills"), seen, result);
                ScanWorkflowFiles(Path.Combine(rootDir, "workflows"), seen, result);
            }
        }

        return result;
    }

    /// <summary>合併帳戶層級斜線指令與工作資料夾專案層級斜線指令（不分大小寫去重，工作資料夾有新增說明時優先補齊）</summary>
    public static IReadOnlyList<AgentSlashCommand> MergeCommands(
        IReadOnlyList<AgentSlashCommand> baseCommands,
        IReadOnlyList<AgentSlashCommand> workspaceCommands)
    {
        if (workspaceCommands.Count == 0)
            return baseCommands;
        if (baseCommands.Count == 0)
            return workspaceCommands;

        var merged = new List<AgentSlashCommand>(baseCommands.Count + workspaceCommands.Count);
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in baseCommands)
        {
            if (string.IsNullOrWhiteSpace(cmd.Name) || indexByName.ContainsKey(cmd.Name))
                continue;
            indexByName[cmd.Name] = merged.Count;
            merged.Add(cmd);
        }

        foreach (var ws in workspaceCommands)
        {
            if (string.IsNullOrWhiteSpace(ws.Name))
                continue;
            if (indexByName.TryGetValue(ws.Name, out var existingIdx))
            {
                // 工作資料夾同名指令覆寫非內建描述或補齊缺漏描述
                var existing = merged[existingIdx];
                if (!string.IsNullOrWhiteSpace(ws.Description)
                    && !AntigravityStreamRules.BuiltinWorkflowCommands.Any(b => string.Equals(b.Name, ws.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    merged[existingIdx] = ws;
                }
                else if (string.IsNullOrWhiteSpace(existing.Description) && !string.IsNullOrWhiteSpace(ws.Description))
                {
                    merged[existingIdx] = existing with { Description = ws.Description };
                }
                continue;
            }
            indexByName[ws.Name] = merged.Count;
            merged.Add(ws);
        }

        return merged;
    }

    /// <summary>
    /// 依載入優先順序掃描可用的自訂代理（工作資料夾 <c>.agents/agents/*.md</c> → 全域 <c>~/.gemini/config/agents/*.md</c> → 已啟用外掛 <c>plugins/*/agents/*.md</c>）
    /// </summary>
    public static IReadOnlyList<AntigravityCustomAgent> DiscoverCustomAgents(string? workingDirectory, string? agyConfigDir)
    {
        var result = new List<AntigravityCustomAgent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(workingDirectory) && AgentAttachmentRules.IsLocalDrivePath(workingDirectory) && Directory.Exists(workingDirectory))
        {
            foreach (var dir in EnumerateWorkspaceChain(workingDirectory))
            {
                foreach (var rootName in WorkspaceRoots)
                {
                    var rootDir = Path.Combine(dir, rootName);
                    if (!Directory.Exists(rootDir) || IsReparsePoint(rootDir))
                        continue;
                    ScanAgentFiles(Path.Combine(rootDir, "agents"), seen, result);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(agyConfigDir))
        {
            var geminiRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(agyConfigDir));
            if (!string.IsNullOrEmpty(geminiRoot))
            {
                var customConfig = Path.Combine(geminiRoot, "config");
                ScanAgentFiles(Path.Combine(customConfig, "agents"), seen, result);
                ScanPluginAgents(customConfig, Path.Combine(agyConfigDir, "config.json"), seen, result);
            }
            // 相容直接放在 agyConfigDir\agents 或 builtin\agents 的定義
            ScanAgentFiles(Path.Combine(agyConfigDir, "agents"), seen, result);
        }

        return result;
    }

    /// <summary>兩個掃描結果是否完全相同（決定要不要重送對話標頭）</summary>
    public static bool Same(AntigravityWorkspaceCustomizations? a, AntigravityWorkspaceCustomizations? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        return ConversationDefaultsRules_SameCommands(a.Commands, b.Commands)
               && a.Agents.SequenceEqual(b.Agents);
    }

    private static bool ConversationDefaultsRules_SameCommands(IReadOnlyList<AgentSlashCommand> x, IReadOnlyList<AgentSlashCommand> y)
    {
        if (x.Count != y.Count)
            return false;
        for (int i = 0; i < x.Count; i++)
        {
            if (x[i] != y[i])
                return false;
        }
        return true;
    }

    internal static bool IsSlashCommandDisabled(IReadOnlyDictionary<string, string> frontMatter)
    {
        var disableSlash = HealthTextRules.FrontMatterText(frontMatter, "disable-slash-command");
        if (string.Equals(disableSlash, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        var disableUnderscore = HealthTextRules.FrontMatterText(frontMatter, "disable_slash_command");
        if (string.Equals(disableUnderscore, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        var userInvocable = HealthTextRules.FrontMatterText(frontMatter, "user-invocable");
        if (string.Equals(userInvocable, "false", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    internal static bool IsValidAgentName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length <= 128
           && !string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)
           && SafeIdentifier().IsMatch(name);

    private static void ScanSkillDirectories(string skillsDir, HashSet<string> seen, List<AgentSlashCommand> target)
    {
        if (!Directory.Exists(skillsDir))
            return;
        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(skillsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in subdirs)
        {
            var folderName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(folderName) || folderName[0] == '.' || IsReparsePoint(dir))
                continue;
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (IsReparsePoint(skillFile) || ReadHeadSafe(skillFile) is not { } content)
                continue;
            var fm = HealthTextRules.ParseFrontMatter(content);
            if (IsSlashCommandDisabled(fm))
                continue;
            var name = HealthTextRules.FrontMatterText(fm, "name");
            if (string.IsNullOrWhiteSpace(name))
                name = folderName;
            name = name.Trim().TrimStart('/');
            if (name.Length == 0 || name.Length > 128 || !SafeIdentifier().IsMatch(name) || !seen.Add(name))
                continue;
            var desc = CleanDescription(HealthTextRules.FrontMatterText(fm, "description"));
            var hint = CleanDescription(HealthTextRules.FrontMatterText(fm, "argument-hint"), 80)
                       ?? CleanDescription(HealthTextRules.FrontMatterText(fm, "argument_hint"), 80);
            target.Add(new AgentSlashCommand(name, desc, hint));
        }
    }

    private static void ScanWorkflowFiles(string workflowsDir, HashSet<string> seen, List<AgentSlashCommand> target)
    {
        if (!Directory.Exists(workflowsDir))
            return;
        string[] files;
        try
        {
            files = Directory.GetFiles(workflowsDir, "*.md");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(stem) || stem[0] == '.' || IsReparsePoint(file))
                continue;
            if (ReadHeadSafe(file) is not { } content)
                continue;
            var fm = HealthTextRules.ParseFrontMatter(content);
            if (IsSlashCommandDisabled(fm))
                continue;
            var name = HealthTextRules.FrontMatterText(fm, "name");
            if (string.IsNullOrWhiteSpace(name))
                name = stem;
            name = name.Trim().TrimStart('/');
            if (name.Length == 0 || name.Length > 128 || !SafeIdentifier().IsMatch(name) || !seen.Add(name))
                continue;
            var desc = CleanDescription(HealthTextRules.FrontMatterText(fm, "description"));
            var hint = CleanDescription(HealthTextRules.FrontMatterText(fm, "argument-hint"), 80)
                       ?? CleanDescription(HealthTextRules.FrontMatterText(fm, "argument_hint"), 80);
            target.Add(new AgentSlashCommand(name, desc, hint));
        }
    }

    private static void ScanAgentFiles(string agentsDir, HashSet<string> seen, List<AntigravityCustomAgent> target)
    {
        if (!Directory.Exists(agentsDir) || IsReparsePoint(agentsDir))
            return;
        string[] files;
        try
        {
            files = Directory.GetFiles(agentsDir, "*.md");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(stem) || stem[0] == '.' || stem.Equals("README", StringComparison.OrdinalIgnoreCase) || IsReparsePoint(file))
                continue;
            TryAddAgentFromFile(file, stem, seen, target);
        }

        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(agentsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
        foreach (var subdir in subdirs)
        {
            var folderName = Path.GetFileName(subdir);
            if (string.IsNullOrEmpty(folderName) || folderName[0] == '.' || IsReparsePoint(subdir))
                continue;
            var agentMd = Path.Combine(subdir, "agent.md");
            if (!File.Exists(agentMd) || IsReparsePoint(agentMd))
                continue;
            TryAddAgentFromFile(agentMd, folderName, seen, target);
        }
    }

    private static void TryAddAgentFromFile(string file, string defaultName, HashSet<string> seen, List<AntigravityCustomAgent> target)
    {
        if (ReadHeadSafe(file) is not { } content)
            return;
        var fm = HealthTextRules.ParseFrontMatter(content);
        if (IsAgentHiddenOrSubagentOnly(fm))
            return;
        var rawName = HealthTextRules.FrontMatterText(fm, "name");
        var name = (string.IsNullOrWhiteSpace(rawName) ? defaultName : rawName).Trim();
        if (!IsValidAgentName(name) || !seen.Add(name))
            return;
        var rawLabel = HealthTextRules.FrontMatterText(fm, "label");
        if (string.IsNullOrWhiteSpace(rawLabel))
            rawLabel = HealthTextRules.FrontMatterText(fm, "title");
        var label = string.IsNullOrWhiteSpace(rawLabel)
            ? name
            : ClaudeStreamRules.Truncate(WhitespaceRun().Replace(rawLabel.Trim(), " "), 64) ?? name;
        var desc = CleanDescription(HealthTextRules.FrontMatterText(fm, "description"));
        target.Add(new AntigravityCustomAgent(name, label, desc));
    }

    private static bool IsAgentHiddenOrSubagentOnly(IReadOnlyDictionary<string, string> fm)
    {
        if (string.Equals(HealthTextRules.FrontMatterText(fm, "disabled"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(HealthTextRules.FrontMatterText(fm, "hidden"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(HealthTextRules.FrontMatterText(fm, "mainAgent"), "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(HealthTextRules.FrontMatterText(fm, "main_agent"), "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(HealthTextRules.FrontMatterText(fm, "mode"), "subagent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateWorkspaceChain(string workingDirectory)
    {
        string? current;
        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            yield break;
        }

        for (int depth = 0; depth < MaxParentChainDepth && !string.IsNullOrEmpty(current); depth++)
        {
            yield return current;
            var gitMarker = Path.Combine(current, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                yield break;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                yield break;
            current = parent;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void ScanPluginAgents(string customConfigDir, string agyConfigJsonPath, HashSet<string> seen, List<AntigravityCustomAgent> target)
    {
        var pluginsDir = Path.Combine(customConfigDir, "plugins");
        if (!Directory.Exists(pluginsDir))
            return;

        Dictionary<string, bool>? overrides = null;
        try
        {
            if (File.Exists(agyConfigJsonPath) && ReadHeadSafe(agyConfigJsonPath) is { } cfgText)
            {
                if (JToken.Parse(cfgText) is JObject cfg && cfg["plugins"] is JObject pluginsObj)
                {
                    overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in pluginsObj.Properties())
                    {
                        if (prop.Value is JValue { Type: JTokenType.Boolean } b)
                            overrides[prop.Name] = (bool)b.Value!;
                        else if (prop.Value is JObject pObj && pObj["enabled"] is JValue { Type: JTokenType.Boolean } eb)
                            overrides[prop.Name] = (bool)eb.Value!;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        string[] pluginDirs;
        try
        {
            pluginDirs = Directory.GetDirectories(pluginsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Array.Sort(pluginDirs, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in pluginDirs)
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id) || id[0] == '.')
                continue;
            if (overrides != null && overrides.TryGetValue(id, out var enabled))
            {
                if (!enabled)
                    continue;
            }
            else if (IsPluginDisabled(dir))
            {
                continue;
            }
            ScanAgentFiles(Path.Combine(dir, "agents"), seen, target);
        }
    }

    private static bool IsPluginDisabled(string pluginDir)
    {
        foreach (var rel in new[] { "plugin.json", Path.Combine(".antigravity-plugin", "plugin.json"), Path.Combine(".gemini-plugin", "plugin.json") })
        {
            var path = Path.Combine(pluginDir, rel);
            if (ReadHeadSafe(path) is not { } json)
                continue;
            try
            {
                if (JToken.Parse(json) is JObject obj && obj["disabled"] is JValue { Type: JTokenType.Boolean, Value: true })
                    return true;
            }
            catch (JsonException)
            {
            }
            break;
        }
        return false;
    }

    private static string? ReadHeadSafe(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int toRead = (int)Math.Min(stream.Length, MaxMarkdownFileBytes);
            if (toRead <= 0)
                return string.Empty;
            var buffer = new byte[toRead];
            int total = 0;
            while (total < toRead)
            {
                int n = stream.Read(buffer, total, toRead - total);
                if (n <= 0)
                    break;
                total += n;
            }
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? CleanDescription(string? raw, int maxLen = 160)
        => string.IsNullOrWhiteSpace(raw) ? null : ClaudeStreamRules.Truncate(WhitespaceRun().Replace(raw.Trim(), " "), maxLen);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:@/-]*$")]
    private static partial Regex SafeIdentifier();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
