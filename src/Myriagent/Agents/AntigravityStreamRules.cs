using System.Text.RegularExpressions;
using Myriagent.Accounts;
using Myriagent.Cli;
using Myriagent.Health;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// Antigravity CLI（agy）的純函式規則：資料夾位置、啟動參數、輸入訊息、輸出事件的解讀。
/// 實測紀錄見 docs/protocol-notes.md 的 Antigravity 一節（agy 1.3.5）。
/// </summary>
internal static partial class AntigravityStreamRules
{
    /// <summary>agy 的資料夾（%USERPROFILE%\.gemini\antigravity-cli）：settings.json、conversations、log…；登入不在這裡（Windows 認證管理員）</summary>
    public static string ResolveHome(string userProfile) => Path.Combine(userProfile, ".gemini", "antigravity-cli");

    /// <summary>
    /// 對話專屬的 brain 資料夾（%USERPROFILE%\.gemini\antigravity-cli\brain\&lt;conversationId&gt;）：plan.md、artifacts 與 .system_generated\logs\transcript_full.jsonl 都在這裡。
    /// conversationId 含路徑分隔字元、冒號或「..」時回 null（防目錄穿越）
    /// </summary>
    public static string? ResolveBrainDirectory(string userProfile, string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(userProfile) || string.IsNullOrWhiteSpace(conversationId))
            return null;
        var id = conversationId.Trim();
        if (id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('/') || id.Contains('\\'))
            return null;
        return Path.Combine(ResolveHome(userProfile), "brain", id);
    }

    /// <summary>對話的完整逐字稿路徑（brain\&lt;conversationId&gt;\.system_generated\logs\transcript_full.jsonl）</summary>
    public static string? ResolveTranscriptPath(string userProfile, string? conversationId)
        => ResolveBrainDirectory(userProfile, conversationId) is { } brain
            ? Path.Combine(brain, ".system_generated", "logs", "transcript_full.jsonl")
            : null;

    /// <summary>
    /// app 啟動的 agy 一律不在背景自動更新（換版由 app 協調）。
    /// 實測 agy 1.4.6（auto_updater.go）：值必須是 "true"（設成 "1" 無效，仍會派生背景更新子程序）
    /// </summary>
    public const string DisableAutoUpdateVariable = "AGY_CLI_DISABLE_AUTO_UPDATE";

    /// <summary><see cref="DisableAutoUpdateVariable"/> 唯一有效的值（agy 只認 "true"）</summary>
    public const string DisableAutoUpdateValue = "true";

    /// <summary>「預設」模型可選的推理強度（agy --effort；模型 id 本身帶強度時不另外傳，見 ModelEncodesEffort）</summary>
    public static IReadOnlyList<string> DefaultModelEfforts { get; } = new[] { "low", "medium", "high" };

    /// <summary>列出模型（要登入才列得出來，所以同時用來判斷登入狀態）</summary>
    public static IReadOnlyList<string> ModelsArguments { get; } = new[] { "models" };

    /// <summary>
    /// 同 <see cref="ModelsArguments"/>，另把這一次的記錄寫到 <paramref name="logFile"/>（不寫預設的 log 資料夾），從裡面取登入的信箱
    /// （<see cref="ParseSignedInEmail"/>）。<c>--log-file</c> 要在子指令前面：放在 <c>models</c> 後面是 exit 1、印用法（agy 1.4.5 實測）
    /// </summary>
    public static IReadOnlyList<string> ModelsArgumentsWithLog(string logFile) => new[] { "--log-file", logFile, "models" };

    /// <summary>
    /// 無頭查額度（agy 1.1.11 起支援單發列印模式的唯讀斜線指令，1.4.6 實測）：CLI 直接回答 command.data.groups，
    /// num_turns=0、不花額度、不在 conversations\ 留下對話檔；未登入且 stdin 已關閉時約 1 秒結束並印 authentication required
    /// </summary>
    public static IReadOnlyList<string> UsageArguments { get; } = new[] { "-p", "/usage", "--output-format", "json" };

    /// <summary>同 <see cref="UsageArguments"/>，另把記錄寫到 <paramref name="logFile"/> 以便一次取得登入的信箱與額度</summary>
    public static IReadOnlyList<string> UsageArgumentsWithLog(string logFile) => new[] { "--log-file", logFile, "-p", "/usage", "--output-format", "json" };

    /// <summary>無頭列出可用的 Skills（agy 1.4.6 實測：command.data.skills 含內建、使用者層與工作區 Skills 的名稱與描述）</summary>
    public static IReadOnlyList<string> SkillsArguments { get; } = new[] { "-p", "/skills", "--output-format", "json" };

    /// <summary>無頭查 AI 點數餘額（agy 1.4.6 實測：command.data.remaining_credits）</summary>
    public static IReadOnlyList<string> CreditsArguments { get; } = new[] { "-p", "/credits", "--output-format", "json" };

    /// <summary>
    /// agy 對話中可展開的內建工作流斜線指令（stream-json 可正常送給模型觸發對應工作流；不含 CLI 本地唯讀指令）
    /// </summary>
    public static IReadOnlyList<AgentSlashCommand> BuiltinWorkflowCommands { get; } = new AgentSlashCommand[]
    {
        new("plan", "先分析並擬定逐步實作計畫，確認後再動手", null),
        new("grill-me", "透過互動訪談釐清需求細節與設計決策，對齊實作計畫", null),
        new("goal", "設定長時間目標，持續徹底執行並驗證直到目標完全達成", null),
        new("teamwork", "啟動多個自主子代理分工協作處理大型任務", null),
        new("boost", "深度思考、多角度策略規劃與嚴格驗證模式", null),
        new("learn", "將這次的修正或複雜設定整理成規則供日後對話沿用", null),
        new("browser", "使用瀏覽器搜尋網頁或與網頁應用程式互動", null),
        new("schedule", "設定單次計時器或定期排程執行指令", null),
        new("compact", "壓縮並摘要目前的對話脈絡，釋放上下文空間", null),
    };

    /// <summary>信箱的長度上限（同步的目錄也是 320）</summary>
    public const int MaxEmailLength = 320;

    /// <summary>
    /// agy 記錄裡登入成功的信箱：<c>server_oauth.go:229] OAuth: authenticated successfully as &lt;信箱&gt;</c>（agy 1.4.5 實測，每次啟動載入登入後寫一次；
    /// 設計 2026-10-22-grok-antigravity-sync 的 D1 以它認 Antigravity 的身分，不讀認證管理員）。有好幾行時取最後一行；
    /// 沒有這一行（沒登入、記錄格式改了）回 null——身分未知，不猜。記錄裡大量的「You are not logged into Antigravity.」是載入登入之前的
    /// 過渡訊息，已登入時也有，不拿來判斷（登入狀態照 <see cref="EvaluateModelsCommand"/>／<see cref="EvaluateUsageCommand"/> 的輸出）
    /// </summary>
    public static string? ParseSignedInEmail(string? log)
    {
        if (string.IsNullOrEmpty(log))
            return null;
        string? email = null;
        foreach (Match match in SignedInLine().Matches(log))
            email = match.Groups[1].Value;
        return email is { Length: <= MaxEmailLength } ? email : null;
    }

    [GeneratedRegex(@"OAuth: authenticated successfully as ([^\s@]+@[^\s@]+\.[^\s@]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SignedInLine();

    /// <summary>程序被結束（停止、重開）時還在執行的工具卡的說明</summary>
    public const string InterruptedToolOutput = "agy 程序已結束，這個工具被中斷，結果未知";

    /// <summary>沒登入時 agy -p 會印登入網址、等 60 秒讓人貼授權碼：會話看到這行就結束程序，請使用者到帳戶頁登入</summary>
    public const string NotSignedInMessage = "Antigravity 尚未登入，請到「帳戶」頁按登入，完成 Google 登入後再送出";

    // ───────────────────────────── 帳戶：agy models 與 agy -p /usage ─────────────────────────────

    /// <summary>agy models 的判讀結果</summary>
    internal sealed record ModelsEvaluation(AccountStatus Status, IReadOnlyList<AgentModelOption> Models, string? Error);

    /// <summary>agy -p /usage --output-format json 的判讀結果（UsageData 為 command.data）</summary>
    internal sealed record UsageEvaluation(AccountStatus Status, JObject? UsageData, string? Error);

    /// <summary>
    /// agy models 的結果 → 登入狀態與模型清單。輸出含「Please sign in」／「not logged into」／「Authentication required」＝未登入；
    /// 結束碼 0＝已登入（模型清單可能是空的）；逾時或其他失敗＝檢查失敗（訊息取輸出的最後一行）
    /// </summary>
    public static ModelsEvaluation EvaluateModelsCommand(int exitCode, string? stdout, string? stderr, bool timedOut)
    {
        var all = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
        if (NotSignedIn().IsMatch(all))
            return new ModelsEvaluation(AccountStatus.NotLoggedIn, Array.Empty<AgentModelOption>(), null);
        if (timedOut)
            return new ModelsEvaluation(AccountStatus.Error, Array.Empty<AgentModelOption>(), "agy models 逾時沒有回應");
        if (exitCode != 0)
        {
            var last = all.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            return new ModelsEvaluation(AccountStatus.Error, Array.Empty<AgentModelOption>(), last ?? $"agy models 結束碼 {exitCode}");
        }
        return new ModelsEvaluation(AccountStatus.Ready, ParseModels(stdout), null);
    }

    /// <summary>
    /// agy -p /usage --output-format json 的結果 → 登入狀態與 command.data。
    /// 未登入（stdin 關閉時印 Error: authentication required / error: authentication failed or timed out）＝NotLoggedIn；
    /// 結束碼 0 且有 command.name=="usage" 的 data＝Ready；結束碼 0 但沒有結構化 command.data（舊版 agy）＝Ready＋UsageData=null
    /// </summary>
    public static UsageEvaluation EvaluateUsageCommand(int exitCode, string? stdout, string? stderr, bool timedOut)
    {
        var all = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
        if (NotSignedIn().IsMatch(all))
            return new UsageEvaluation(AccountStatus.NotLoggedIn, null, null);
        if (timedOut)
            return new UsageEvaluation(AccountStatus.Error, null, "agy -p /usage 逾時沒有回應");

        var root = TryParseJsonPayload(stdout);
        if (root != null && string.Equals(root.Value<string>("status"), "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            var err = AntigravityStepRules.ErrorText(root["error"]);
            if (!string.IsNullOrEmpty(err) && NotSignedIn().IsMatch(err))
                return new UsageEvaluation(AccountStatus.NotLoggedIn, null, null);
            return new UsageEvaluation(AccountStatus.Error, null, !string.IsNullOrWhiteSpace(err) ? err : "agy -p /usage 執行失敗");
        }
        if (exitCode != 0)
        {
            var last = all.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith('{'));
            return new UsageEvaluation(AccountStatus.Error, null, last ?? $"agy -p /usage 結束碼 {exitCode}");
        }
        if (root?["command"] is JObject cmd
            && string.Equals(cmd.Value<string>("name"), "usage", StringComparison.OrdinalIgnoreCase)
            && cmd["data"] is JObject data)
        {
            return new UsageEvaluation(AccountStatus.Ready, data, null);
        }
        return new UsageEvaluation(AccountStatus.Ready, null, "用量回應的格式無法解析");
    }

    /// <summary>
    /// agy -p /skills --output-format json 的結果 → 內建工作流指令＋探索到的 Skills 清單（去重）。
    /// 即使 /skills 失敗或舊版不支援，仍回傳 <see cref="BuiltinWorkflowCommands"/> 保底
    /// </summary>
    public static IReadOnlyList<AgentSlashCommand> EvaluateSkillsCommand(int exitCode, string? stdout)
    {
        var result = new List<AgentSlashCommand>(BuiltinWorkflowCommands);
        var seen = new HashSet<string>(result.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        if (exitCode != 0)
            return result;
        var root = TryParseJsonPayload(stdout);
        if (root?["command"] is not JObject cmd
            || !string.Equals(cmd.Value<string>("name"), "skills", StringComparison.OrdinalIgnoreCase)
            || cmd["data"]?["skills"] is not JArray skills)
        {
            return result;
        }
        foreach (var item in skills.OfType<JObject>())
        {
            var rawName = item.Value<string>("name")?.Trim().TrimStart('/');
            if (string.IsNullOrWhiteSpace(rawName) || !seen.Add(rawName))
                continue;
            var rawDesc = item.Value<string>("description");
            var desc = string.IsNullOrWhiteSpace(rawDesc)
                ? null
                : ClaudeStreamRules.Truncate(WhitespaceRun().Replace(rawDesc.Trim(), " "), 160);
            result.Add(new AgentSlashCommand(rawName, desc, null));
        }
        return result;
    }

    /// <summary>
    /// 唯讀掃描本機 Antigravity 資料夾（builtin\skills、config\skills、已啟用的 config\plugins\*\skills）組出斜線指令選單，
    /// 不必額外啟動 190 MB 的 agy 子程序與 language server；資料夾不存在時回傳 <see cref="BuiltinWorkflowCommands"/>
    /// </summary>
    /// <param name="agyConfigDir">Antigravity CLI 資料夾（~/.gemini/antigravity-cli）</param>
    public static IReadOnlyList<AgentSlashCommand> DiscoverLocalCommands(string? agyConfigDir)
    {
        var result = new List<AgentSlashCommand>(BuiltinWorkflowCommands);
        if (string.IsNullOrWhiteSpace(agyConfigDir))
            return result;

        var seen = new HashSet<string>(result.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        ScanSkillFolder(Path.Combine(agyConfigDir, "builtin", "skills"), seen, result);

        var geminiRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(agyConfigDir));
        if (!string.IsNullOrEmpty(geminiRoot))
        {
            var customConfig = Path.Combine(geminiRoot, "config");
            ScanSkillFolder(Path.Combine(customConfig, "skills"), seen, result);
            ScanPluginSkills(customConfig, Path.Combine(agyConfigDir, "config.json"), seen, result);
        }
        return result;
    }

    private static void ScanSkillFolder(string skillsDir, HashSet<string> seen, List<AgentSlashCommand> target)
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
            if (string.IsNullOrEmpty(folderName) || folderName[0] == '.')
                continue;
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile))
                continue;
            try
            {
                using var stream = new FileStream(skillFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var fm = HealthTextRules.ParseFrontMatter(reader.ReadToEnd());
                if (AntigravityWorkspaceScanner.IsSlashCommandDisabled(fm))
                    continue;
                var name = HealthTextRules.FrontMatterText(fm, "name");
                if (string.IsNullOrWhiteSpace(name))
                    name = folderName;
                name = name.Trim().TrimStart('/');
                if (name.Length == 0 || !seen.Add(name))
                    continue;
                var rawDesc = HealthTextRules.FrontMatterText(fm, "description");
                var desc = string.IsNullOrWhiteSpace(rawDesc)
                    ? null
                    : ClaudeStreamRules.Truncate(WhitespaceRun().Replace(rawDesc, " "), 160);
                target.Add(new AgentSlashCommand(name, desc, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ScanPluginSkills(string customConfigDir, string agyConfigJsonPath, HashSet<string> seen, List<AgentSlashCommand> target)
    {
        var pluginsDir = Path.Combine(customConfigDir, "plugins");
        if (!Directory.Exists(pluginsDir))
            return;

        Dictionary<string, bool>? overrides = null;
        try
        {
            if (File.Exists(agyConfigJsonPath))
            {
                using var stream = new FileStream(agyConfigJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (JToken.Parse(reader.ReadToEnd()) is JObject cfg && cfg["plugins"] is JObject pluginsObj)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
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
            else if (IsPluginManifestDisabled(dir))
            {
                continue;
            }
            ScanSkillFolder(Path.Combine(dir, "skills"), seen, target);
        }
    }

    private static bool IsPluginManifestDisabled(string pluginDir)
    {
        foreach (var rel in new[] { "plugin.json", Path.Combine(".antigravity-plugin", "plugin.json"), Path.Combine(".gemini-plugin", "plugin.json") })
        {
            var path = Path.Combine(pluginDir, rel);
            if (!File.Exists(path))
                continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (JToken.Parse(reader.ReadToEnd()) is JObject obj && obj["disabled"] is JValue { Type: JTokenType.Boolean, Value: true })
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            break;
        }
        return false;
    }

    /// <summary>
    /// agy -p /credits --output-format json 的結果 → 點數餘額說明字串；remaining_credits &lt;= 0 或讀不到時回 null（一般無點數包的訂閱不佔畫面）
    /// </summary>
    public static string? ParseCreditsBalance(int exitCode, string? stdout)
    {
        if (exitCode != 0)
            return null;
        var root = TryParseJsonPayload(stdout);
        if (root?["command"] is not JObject cmd
            || !string.Equals(cmd.Value<string>("name"), "credits", StringComparison.OrdinalIgnoreCase)
            || cmd["data"] is not JObject data)
        {
            return null;
        }
        var token = data["remaining_credits"] ?? data["remainingCredits"] ?? data["balance"];
        if (token is not JValue { Value: not null } val
            || !double.TryParse(Convert.ToString(val.Value, System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var credits)
            || !double.IsFinite(credits)
            || credits <= 0)
        {
            return null;
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"AI 點數餘額 {credits:0.##}");
    }

    private static JObject? TryParseJsonPayload(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        var trimmed = stdout.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                return JObject.Parse(trimmed);
            }
            catch (JsonException)
            {
            }
        }
        foreach (var raw in trimmed.Replace("\r", string.Empty).Split('\n').Reverse())
        {
            var line = raw.Trim();
            if (!line.StartsWith('{'))
                continue;
            try
            {
                return JObject.Parse(line);
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// agy models 的模型清單（實測 1.3.5：每行「id&lt;TAB&gt;顯示名稱」，例如 gemini-3.8-flash-low、Gemini 3.8 Flash (Low)；
    /// 「Fetching available models...」印在 stderr）。也接受以空白分隔、或只有 id；id 必須含連字號，說明行不會被當成模型。
    /// 每個 id 都帶推理強度，同一個模型的幾個強度合併成一項：值是去掉強度的基本 id、顯示名稱去掉「(Low)」，
    /// 推理強度選單列出它有的強度（依 low→max 排序）。實測 --model gemini-3.8-flash --effort low 可以用，
    /// 帶強度的 id 再加 --effort 則會報「conflicts with --effort」
    /// </summary>
    public static IReadOnlyList<AgentModelOption> ParseModels(string? stdout)
    {
        var groups = new List<(string Id, string Label, List<string> Efforts)>();
        foreach (var (id, label) in ParseModelLines(stdout))
        {
            var suffix = EffortSuffix().Match(id);
            var baseId = suffix.Success ? id[..suffix.Index] : id;
            var index = groups.FindIndex(g => string.Equals(g.Id, baseId, StringComparison.Ordinal));
            if (index < 0)
            {
                groups.Add((baseId, suffix.Success ? EffortLabelSuffix().Replace(label, string.Empty).Trim() : label, new List<string>()));
                index = groups.Count - 1;
            }
            var effort = suffix.Success ? suffix.Groups[1].Value.ToLowerInvariant() : null;
            if (effort != null && !groups[index].Efforts.Contains(effort))
                groups[index].Efforts.Add(effort);
        }
        return groups
            .Select(g => new AgentModelOption(g.Id, g.Label.Length > 0 ? g.Label : g.Id, null,
                g.Efforts.OrderBy(e => Array.IndexOf(EffortOrder, e)).ToArray()))
            .ToList();
    }

    /// <summary>推理強度由低到高（模型選單的排序）</summary>
    private static readonly string[] EffortOrder = { "minimal", "low", "medium", "high", "xhigh", "max" };

    /// <summary>agy models 的每一行 → (id, 顯示名稱)；重複的 id 只留第一個</summary>
    private static List<(string Id, string Label)> ParseModelLines(string? stdout)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (stdout ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            string id, label;
            var tab = line.IndexOf('\t');
            if (tab > 0)
            {
                id = line[..tab].Trim();
                label = line[(tab + 1)..].Trim();
            }
            else
            {
                var match = LeadingToken().Match(line);
                if (!match.Success)
                    continue;
                id = match.Groups[1].Value;
                label = match.Groups[2].Value.Trim();
            }
            if (!ModelId().IsMatch(id) || !seen.Add(id))
                continue;
            result.Add((id, label));
        }
        return result;
    }

    /// <summary>
    /// 模型 id 本身帶推理強度（gemini-3.8-flash-high 這種）：再傳 --effort 會直接報錯（實測 exit 1）。
    /// 選單給的是基本 id；這裡擋的是舊設定或使用者在 agy 設定檔裡指定的完整 id
    /// </summary>
    public static bool ModelEncodesEffort(string? model)
        => !string.IsNullOrEmpty(model) && EffortSuffix().IsMatch(model);

    // ───────────────────────────── 啟動參數與輸入 ─────────────────────────────

    /// <summary>
    /// 對話程序的參數：--print=（空的提示，訊息改從 stdin 一行一則送）＋雙向 stream-json；續接用 --conversation；
    /// 新對話且非精簡模式時若選了自訂代理則傳 --agent；模型「預設」不傳 --model（用 agy 自己記住的）；模型 id 帶強度時不傳 --effort；
    /// 權限模式見 PermissionArguments；精簡模式（useLocalCustomizations＝false）傳 --disable-slash-commands；對話資料夾（附件）以 --add-dir 加進工作區
    /// </summary>
    public static List<string> BuildArguments(
        string? conversationId,
        string? model,
        string? effort,
        AgentPermissionMode mode,
        string conversationDirectory,
        bool useLocalCustomizations = true,
        string? agent = null)
    {
        var args = new List<string> { "--print=", "--input-format", "stream-json", "--output-format", "stream-json" };
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            args.Add("--conversation");
            args.Add(conversationId);
        }
        else if (useLocalCustomizations && NormalizeAgent(agent) is { } normalizedAgent)
        {
            args.Add("--agent");
            args.Add(normalizedAgent);
        }
        var normalizedModel = NormalizeModel(model);
        if (normalizedModel != null)
        {
            args.Add("--model");
            args.Add(normalizedModel);
        }
        if (!string.IsNullOrWhiteSpace(effort) && !ModelEncodesEffort(normalizedModel))
        {
            args.Add("--effort");
            args.Add(effort);
        }
        args.AddRange(PermissionArguments(mode));
        if (!useLocalCustomizations)
            args.Add("--disable-slash-commands");
        if (!string.IsNullOrWhiteSpace(conversationDirectory))
        {
            args.Add("--add-dir");
            args.Add(conversationDirectory);
        }
        return args;
    }

    /// <summary>
    /// 權限模式 → agy 參數：自動（沙箱）＝--dangerously-skip-permissions --sandbox；完全自動＝--dangerously-skip-permissions；
    /// 規劃＝--mode plan；其他＝--mode accept-edits（可讀寫檔案、執行指令被自動拒絕）。
    /// 每步詢問做不到（stream-json 沒有核准協定，AgentPermissionModes.Normalize 已先換成自動接受編輯）
    /// </summary>
    public static IReadOnlyList<string> PermissionArguments(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Auto => new[] { "--dangerously-skip-permissions", "--sandbox" },
        AgentPermissionMode.FullAccess => new[] { "--dangerously-skip-permissions" },
        AgentPermissionMode.Plan => new[] { "--mode", "plan" },
        _ => new[] { "--mode", "accept-edits" },
    };

    /// <summary>「預設」（或沒選）＝不傳 --model</summary>
    public static string? NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) || model.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : model;

    /// <summary>「預設」（或沒選）＝不傳 --agent</summary>
    public static string? NormalizeAgent(string? agent)
        => string.IsNullOrWhiteSpace(agent) || agent.Trim().Equals("default", StringComparison.OrdinalIgnoreCase) ? null : agent.Trim();

    /// <summary>
    /// 閒置中的 agy 程序因背景工作完成、排程計時器觸發或子代理回報而自己開始新一輪：
    /// 收到非 system_message、步驟編號不早於 <paramref name="minStepIndex"/>、且非失敗／取消／遲到工具完成的 step_update JSON 行即代表新一輪已開始
    /// </summary>
    public static bool StartsIdleTurn(string? line, int minStepIndex = -1)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('{'))
            return false;
        try
        {
            using var reader = new JsonTextReader(new StringReader(trimmed)) { DateParseHandling = DateParseHandling.None };
            if (JToken.ReadFrom(reader) is not JObject obj)
                return false;
            var kind = obj.Value<string>("event") ?? obj.Value<string>("type");
            if (!string.Equals(kind, "step_update", StringComparison.Ordinal))
                return false;
            var payload = obj["step_update"] as JObject ?? obj;
            var stepType = payload.Value<string>("step_type");
            if (string.Equals(stepType, "system_message", StringComparison.OrdinalIgnoreCase))
                return false;
            if (minStepIndex >= 0
                && payload["step_index"] is JValue { Value: not null } idxVal
                && int.TryParse(Convert.ToString(idxVal.Value, System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var stepIndex)
                && stepIndex < minStepIndex)
            {
                return false;
            }
            var rawState = payload.Value<string>("state");
            if (!string.IsNullOrWhiteSpace(rawState))
            {
                var classified = AntigravityStepRules.ClassifyState(rawState);
                if (classified is AntigravityStepState.Cancelled or AntigravityStepState.Failed)
                    return false;
                if (classified == AntigravityStepState.Completed
                    && string.Equals(stepType, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 要以新的參數重開程序的設定組合：帳戶、執行檔、工作資料夾、模型、推理強度、權限模式、精簡模式、自訂代理（agy 都只能在啟動時指定）；
    /// 執行檔身分另外比（CliBinaryIdentity.SameOrUnknown）
    /// </summary>
    public static string Signature(
        string accountKey,
        string? launcher,
        string workingDirectory,
        string? model,
        string? effort,
        AgentPermissionMode mode,
        string conversationDirectory,
        bool useLocalCustomizations = true,
        string? agent = null)
        => string.Join("|", accountKey, launcher, workingDirectory, NormalizeModel(model), effort,
            AgentPermissionModes.Normalize(AgentProvider.Antigravity, mode), conversationDirectory,
            useLocalCustomizations ? "1" : "0",
            useLocalCustomizations ? NormalizeAgent(agent) : null);

    public static bool NeedsNewProcess(string? runningSignature, CliBinaryIdentity? runningCli, string nextSignature, CliBinaryIdentity? nextCli)
        => !string.Equals(runningSignature, nextSignature, StringComparison.Ordinal) || !CliBinaryIdentity.SameOrUnknown(runningCli, nextCli);

    /// <summary>一則使用者訊息（stream-json 輸入：每行一則，content 只能是文字）</summary>
    public static string BuildUserMessage(string text)
        => new JObject
        {
            ["event"] = "user",
            ["message"] = new JObject { ["content"] = text ?? string.Empty },
        }.ToString(Formatting.None);

    /// <summary>
    /// 這一輪要送的文字：新對話（CLI 端還沒建立）先放背景說明；續接不到、改開新對話時放前文節錄；換過工作資料夾時開頭告知；
    /// 最後是使用者的訊息與附件說明（附件只能給路徑）
    /// </summary>
    public static string BuildTurnText(string? instructions, string? historyRecap, string? workingDirectoryNotice, string? text, IReadOnlyList<AgentAttachment> attachments)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(instructions))
            parts.Add($"<myriagent_instructions>\n{instructions.Trim()}\n</myriagent_instructions>");
        if (!string.IsNullOrWhiteSpace(historyRecap))
            parts.Add(historyRecap.Trim());
        if (!string.IsNullOrWhiteSpace(workingDirectoryNotice))
            parts.Add(workingDirectoryNotice);
        parts.Add((text ?? string.Empty) + AgentAttachmentRules.BuildAttachmentNote(attachments, imagesInline: false));
        return string.Join("\n\n", parts);
    }

    /// <summary>工作資料夾換了（程序以新的資料夾重開、以 --conversation 續接）：這一則訊息開頭告訴代理</summary>
    public static string BuildWorkingDirectoryNotice(string workingDirectory)
        => $"（系統通知：工作資料夾已改為 {workingDirectory}，之後的檔案與指令請以這個資料夾為準）";

    /// <summary>stdout 或 stderr 的這一行表示 agy 沒有登入（正在等人貼授權碼）</summary>
    public static bool IsSignInPrompt(string? line)
        => !string.IsNullOrEmpty(line) && SignInPrompt().IsMatch(line);

    /// <summary>
    /// stderr 的「找不到對話」警告：agy 找不到 --conversation 指定的對話時只印「warning: conversation "…" not found」，
    /// 然後默默開一個空白的新對話（實測，status 仍是 SUCCESS）。回傳警告裡的對話 id，不是這個警告回 null。
    /// 只認這個精確格式：agy 的執行檔還有「GetConversationDetail: conversation %s not found locally…」「parent conversation %s not found」
    /// 這類字串，寬鬆的比對會把正常的續接也殺掉重送
    /// </summary>
    public static string? ParseConversationNotFoundWarning(string? line)
    {
        var match = ConversationNotFoundWarning().Match(line ?? string.Empty);
        return match.Success ? match.Groups["id"].Value : null;
    }

    /// <summary>
    /// 續接失敗的錯誤訊息（result 的 error、程序結束時的 stderr 尾段）：舊版或日後 agy 若改成以錯誤回報，也改開新對話。
    /// 只用在這一輪已經失敗的時候（會話不會為了它殺掉還在跑的程序）
    /// </summary>
    public static bool IsConversationNotFound(string? text)
        => !string.IsNullOrEmpty(text) && ConversationNotFound().IsMatch(text);

    /// <summary>
    /// 這一輪因帳戶訂閱額度或點數耗盡而失敗（實測 agy 1.4.6 非重試錯誤：credits balance is too low to continue、individual quota reached、exhausted quota…）：
    /// 觸發 AgentQuotaExhaustedEvent 與立即重查額度；不含暫時性的 429／rate limit
    /// </summary>
    public static bool IsQuotaExhausted(string? error)
        => !string.IsNullOrEmpty(error) && QuotaExhaustedPattern().IsMatch(error);

    /// <summary>
    /// 這一輪因 API 伺服器暫時無法服務而失敗（實錄 agy 1.4.7：result 的 error「API error (attempt 1): UNAVAILABLE (code 503): The service is currently unavailable.」）：
    /// 中樞 60 秒後自動重試（AgentServerBusy.Unavailable，OverloadRetryRules）。額度或點數耗盡不算（那要換帳戶，不是等一下就好）
    /// </summary>
    public static bool IsServiceUnavailable(string? error)
        => !string.IsNullOrEmpty(error) && ServiceUnavailablePattern().IsMatch(error) && !IsQuotaExhausted(error);

    /// <summary>
    /// 這一輪因 agy 呼叫模型 API 時底層網路／傳輸連線中斷而失敗（實錄：「API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": write tcp …: wsasend: An existing connection was forcibly closed by the remote host.」）：
    /// 常駐 agy 程序的 Go HTTP/TLS 連線池重用到被遠端或 NAT 關閉的閒置連線、或短暫網路抖動；會話在無存活背景工作時先關掉舊程序清掉連線池，中樞 10 秒後自動重試（AgentServerBusy.NetworkError）。
    /// 只認 API 呼叫層的暫時性連線中斷；額度耗盡、未登入、HTTP 4xx/500、x509 憑證錯誤都不算
    /// </summary>
    public static bool IsTransientNetworkError(string? error)
        => !string.IsNullOrEmpty(error)
           && ApiErrorContextPattern().IsMatch(error)
           && TransientNetworkPattern().IsMatch(error)
           && !IsQuotaExhausted(error)
           && !NotSignedIn().IsMatch(error);

    /// <summary>失敗的 result.error 屬於哪一種可自動重試的 API 暫時異常；其他錯誤回 None</summary>
    public static AgentServerBusy ClassifyServerBusy(string? error)
    {
        if (IsServiceUnavailable(error))
            return AgentServerBusy.Unavailable;
        if (IsTransientNetworkError(error))
            return AgentServerBusy.NetworkError;
        return AgentServerBusy.None;
    }

    /// <summary>只在 CLI 本地處理、送進 --input-format stream-json 會害 agy exit 2 的斜線指令分類</summary>
    internal enum CliOnlySlashKind
    {
        None,
        UiManaged,
        SinglePrompt,
    }

    internal sealed record CliOnlySlashClassification(CliOnlySlashKind Kind, string? PromptCommand = null, string? UiHint = null);

    private static readonly HashSet<string> SinglePromptOnlySlashNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "usage", "quota", "credits", "skills", "agents", "plugin", "hooks", "config", "settings", "changelog",
    };

    /// <summary>
    /// 辨識不可送進 stream-json 的 CLI 本地唯讀斜線指令（agy 1.4.6 實測送進 stream-json 會報錯並 exit 2）：
    /// model／effort／permissions 提示改用選單；usage／credits／skills／help 等改以單發 -p 執行並回報結果
    /// </summary>
    public static CliOnlySlashClassification ClassifyCliOnlySlashCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CliOnlySlashClassification(CliOnlySlashKind.None);
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/') || trimmed.Contains('\n') || trimmed.Contains('\r'))
            return new CliOnlySlashClassification(CliOnlySlashKind.None);

        var space = trimmed.IndexOf(' ');
        var name = (space > 0 ? trimmed[1..space] : trimmed[1..]).Trim();
        if (name.Length == 0)
            return new CliOnlySlashClassification(CliOnlySlashKind.None);

        if (name.Equals("model", StringComparison.OrdinalIgnoreCase))
            return new CliOnlySlashClassification(CliOnlySlashKind.UiManaged, UiHint: "Antigravity 的 stream-json 模式不接受 /model 指令；請直接由對話輸入框下方的模型選單切換（下一則訊息生效）。");
        if (name.Equals("effort", StringComparison.OrdinalIgnoreCase))
            return new CliOnlySlashClassification(CliOnlySlashKind.UiManaged, UiHint: "Antigravity 的 stream-json 模式不接受 /effort 指令；請直接由對話輸入框下方的推理強度選單切換（下一則訊息生效）。");
        if (name.Equals("permissions", StringComparison.OrdinalIgnoreCase))
            return new CliOnlySlashClassification(CliOnlySlashKind.UiManaged, UiHint: "Antigravity 的 stream-json 模式不接受 /permissions 指令；請直接由對話輸入框下方的權限選單切換（下一則訊息生效）。");

        if (SinglePromptOnlySlashNames.Contains(name))
            return new CliOnlySlashClassification(CliOnlySlashKind.SinglePrompt, PromptCommand: trimmed);

        return new CliOnlySlashClassification(CliOnlySlashKind.None);
    }

    // ───────────────────────────── 規劃模式與 RequestFeedback Artifacts 確認卡 ─────────────────────────────

    public const string PlanFeedbackQuestionId = "agy-plan-feedback";
    public const string PlanProceedSandboxOption = "照計畫執行（自動・沙箱）";
    public const string PlanProceedAcceptEditsOption = "照計畫執行（自動接受編輯）";

    /// <summary>
    /// 代理在規劃模式寫出 plan.md、或建立 RequestFeedback=true 的 Artifact 時，於這一輪結束前發出的互動確認題（AnswerAsMessage=true）
    /// </summary>
    public static AgentQuestion BuildPlanFeedbackQuestion(string? filePath, string? summary)
    {
        var fileName = !string.IsNullOrWhiteSpace(filePath) ? Path.GetFileName(filePath) : "plan.md";
        var cleanSummary = string.IsNullOrWhiteSpace(summary)
            ? null
            : ClaudeStreamRules.Truncate(ClaudeStreamRules.FirstLine(summary.Trim()), 120);
        var prompt = cleanSummary != null
            ? $"代理已提出計畫（{fileName}）：{cleanSummary}\n要照計畫開始執行，還是補充修改意見？"
            : $"代理已完成計畫檔案（{fileName}），要照計畫開始執行，還是補充修改意見？";
        return new AgentQuestion
        {
            Id = PlanFeedbackQuestionId,
            Header = "計畫確認",
            Text = prompt,
            MultiSelect = false,
            AllowOther = true,
            Options = new List<AgentQuestionOption>
            {
                new()
                {
                    Label = PlanProceedSandboxOption,
                    Description = "切換為「自動（沙箱）」並送出 Proceed，在沙箱保護下自動執行指令與修改檔案",
                },
                new()
                {
                    Label = PlanProceedAcceptEditsOption,
                    Description = "切換為「自動接受編輯」並送出 Proceed，允許修改檔案（不執行終端機指令）",
                },
            },
        };
    }

    /// <summary>
    /// 解析使用者在 Antigravity 計畫確認卡上的回答：選執行計畫時回傳要切換的權限模式與送給 agy 的 "Proceed"；
    /// 自行填寫修改意見時保持原權限模式並直接把修改意見送給 agy
    /// </summary>
    public static bool TryResolvePlanFeedbackDecision(
        IReadOnlyList<AgentQuestion> questions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        out AgentPermissionMode? targetMode,
        out string? cliMessage)
    {
        targetMode = null;
        cliMessage = null;
        if (questions.Count != 1 || questions[0]?.Id != PlanFeedbackQuestionId)
            return false;
        if (!answers.TryGetValue(PlanFeedbackQuestionId, out var selected) || selected.Count == 0)
            return false;
        var choice = selected[0]?.Trim() ?? string.Empty;
        if (choice.Length == 0)
            return false;
        if (string.Equals(choice, PlanProceedSandboxOption, StringComparison.Ordinal))
        {
            targetMode = AgentPermissionMode.Auto;
            cliMessage = "Proceed";
            return true;
        }
        if (string.Equals(choice, PlanProceedAcceptEditsOption, StringComparison.Ordinal))
        {
            targetMode = AgentPermissionMode.AcceptEdits;
            cliMessage = "Proceed";
            return true;
        }
        cliMessage = choice;
        return true;
    }

    [GeneratedRegex(@"please sign in|not logged in(to)?|sign in to view|authentication (required|failed)", RegexOptions.IgnoreCase)]
    private static partial Regex NotSignedIn();

    /// <summary>
    /// agy 自己的登入提示（實測「Authentication required. Please visit the URL to log in:」）。不認單獨的「authentication required」：
    /// MCP 伺服器、工具的錯誤也可能這樣寫，被當成沒登入會把正常的一輪結束掉
    /// </summary>
    [GeneratedRegex(@"please visit the url to log in|paste the authorization code", RegexOptions.IgnoreCase)]
    private static partial Regex SignInPrompt();

    [GeneratedRegex(@"conversation\b.*\b(not found|does not exist|no such|unknown|invalid)|(no|unknown|invalid) conversation", RegexOptions.IgnoreCase)]
    private static partial Regex ConversationNotFound();

    [GeneratedRegex(@"credits balance is too low|individual quota reached|exhausted (?:your )?(?:(?:daily|weekly|monthly|five[\s-]hour|5h) )?quota|quota (?:has been )?exhausted|(?:usage|daily|weekly|monthly) limit (?:has been )?(?:reached|exceeded)", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaExhaustedPattern();

    [GeneratedRegex(@"\bUNAVAILABLE\s*\(code\s*503\)|\b503\b[^\r\n]{0,40}?\bunavailable\b|\bservice is (?:currently |temporarily )?unavailable\b", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceUnavailablePattern();

    [GeneratedRegex(@"\bAPI error\b|cloudcode-pa\.googleapis\.com|\bstreamGenerateContent\b", RegexOptions.IgnoreCase)]
    private static partial Regex ApiErrorContextPattern();

    [GeneratedRegex(@"\b(?:wsasend|wsarecv)\b|forcibly closed by the remote host|established connection was aborted|connection reset by peer|\bbroken pipe\b|\bunexpected EOF\b|:\s*EOF\s*$|http2:\s*(?:client connection lost|server sent GOAWAY|transport received GOAWAY)|stream error:\s*stream ID\b|\bi/o timeout\b|TLS handshake timeout|Client\.Timeout exceeded|\bdial tcp\b", RegexOptions.IgnoreCase)]
    private static partial Regex TransientNetworkPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"^(\S+)\s*(.*)$")]
    private static partial Regex LeadingToken();

    /// <summary>模型 id：英數開頭、只含英數與 . _ : / @ -，而且至少有一個連字號</summary>
    [GeneratedRegex(@"^(?=[^\s]*-)[A-Za-z0-9][A-Za-z0-9._:/@-]*$")]
    private static partial Regex ModelId();

    [GeneratedRegex(@"-(minimal|low|medium|high|xhigh|max)$", RegexOptions.IgnoreCase)]
    private static partial Regex EffortSuffix();

    [GeneratedRegex(@"^\s*warning: conversation ""(?<id>[^""]+)"" not found\s*$")]
    private static partial Regex ConversationNotFoundWarning();

    /// <summary>顯示名稱尾端的強度：「Gemini 3.8 Flash (Low)」的「 (Low)」</summary>
    [GeneratedRegex(@"\s*\((minimal|low|medium|high|xhigh|max|extra high)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EffortLabelSuffix();
}
