using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Update;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>共用設定要用的帳戶快照（背景執行緒用：AgentAccount 只在 UI 執行緒改）</summary>
internal sealed record SharedAccount(string Id, string Label, AgentProvider Provider, string ConfigDirectory);

/// <summary>帳戶設定資料夾裡的共用狀態（.myriagent-shared\state.json）</summary>
internal sealed class SharedCustomizationState
{
    /// <summary>目前是共用中（關掉共用時改成 false；再打開時把這段期間帳戶自己寫的記憶合併過去）</summary>
    public bool Active { get; set; }

    /// <summary>上次同步完的全域指示雜湊（SharedCustomizationRules.SyncedHash）；null＝還沒同步過</summary>
    public string? InstructionHash { get; set; }

    /// <summary>
    /// 上次把帳戶的記憶合併進來源的時間：再打開共用時只合併之後改過的檔。用 DateTimeOffset：JsonFile 把 DateTime 讀回成本機時間，
    /// 和檔案的 UTC 時間直接比會差一個時區
    /// </summary>
    public DateTimeOffset? MergedAt { get; set; }
}

/// <summary>跨四家 CLI 共用設定池的同步狀態（cross-cli-shared\state.json）</summary>
internal sealed class CrossCliSyncState
{
    public bool Active { get; set; }

    public string? PoolHash { get; set; }

    public Dictionary<string, string> ProviderHashes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>開啟跨 CLI 共用時原本沒有全域指示檔（由同步新建立）的代理種類名稱，關閉共用時若未被手改則刪除還原</summary>
    public HashSet<string> CreatedProviders { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 隔離帳戶共用這台系統預設設定資料夾（~/.claude、~/.codex、~/.grok；繼承的 CLAUDE_CONFIG_DIR／CODEX_HOME／GROK_HOME）的設定，
/// 以及選用的「跨四家 CLI 共用 Skills 與全域指示」（打通 Claude、Codex、Antigravity、Grok），登入憑證各自分開：
/// <list type="bullet">
/// <item>全域指示（CLAUDE.md／AGENTS.md／GEMINI.md）：每次啟動 CLI 前雙向同步（SharedCustomizationRules.DecideInstructionSync）。檔案層級的符號連結要系統管理員或開發人員模式，
/// 硬連結又會被「寫暫存檔再改名」的編輯器拆開，所以用複製</item>
/// <item>skills（逐一）與 agents、commands、output-styles、Codex 的 prompts、Grok 的 rules（整個資料夾）：junction 連到來源（不需要系統管理員）。
/// 帳戶原有的先合併進來源、原件移到 .myriagent-shared\backup\，關掉共用時移回來</item>
/// <item>Claude 的自動記憶：啟動時以 --settings 的 autoMemoryDirectory 指到來源同一個專案的 memory\（不搬檔、不建連結）；第一次共用時把帳戶原有的記憶合併過去</item>
/// <item>MCP：Claude 以 --mcp-config 帶來源 .claude.json 的 user 層與這個專案的 local 層，Codex 以 -c 帶來源 config.toml 的 mcp_servers；帳戶自己有的同名伺服器優先</item>
/// </list>
/// 執行緒安全（所有帳戶共用一把鎖：同時寫來源的只有這裡）；任何一步失敗只記錄、不擋對話（少共用一項總比開不了對話好）
/// </summary>
internal sealed class SharedCustomizations
{
    private const string StateFileName = "state.json";
    private const string BackupFolderName = "backup";
    private const string CrossBackupFolderName = "cross-backup";
    private const string PoolInstructionsFileName = "INSTRUCTIONS.md";
    private const string McpFilePrefix = "mcp-";

    private static readonly object Gate = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<AgentProvider, string> sourceDirectory;
    private readonly Func<string> claudeSourceGlobalConfig;
    private readonly Func<string> crossCliPoolDirectory;

    /// <summary>來源是這台的系統預設設定資料夾</summary>
    public SharedCustomizations()
        : this(DefaultSourceDirectory, DefaultClaudeSourceGlobalConfig, null)
    {
    }

    /// <param name="sourceDirectory">各 CLI 的來源設定資料夾</param>
    /// <param name="claudeSourceGlobalConfig">來源的 .claude.json（Claude 的 user 層 MCP 在這裡，不在設定資料夾裡）</param>
    /// <param name="crossCliPoolDirectory">跨四家 CLI 共用池的資料夾（測試指定暫存資料夾）</param>
    public SharedCustomizations(
        Func<AgentProvider, string> sourceDirectory,
        Func<string> claudeSourceGlobalConfig,
        Func<string>? crossCliPoolDirectory = null)
    {
        this.sourceDirectory = sourceDirectory;
        this.claudeSourceGlobalConfig = claudeSourceGlobalConfig;
        this.crossCliPoolDirectory = crossCliPoolDirectory ?? DefaultCrossCliPoolDirectory;
    }

    /// <summary>這個帳戶現在要共用就回快照（在 UI 執行緒取）；不共用回 null</summary>
    public static SharedAccount? Snapshot(AgentAccount account)
        => SharedCustomizationRules.IsActive(account) ? new SharedAccount(account.Id, account.Label, account.Provider, account.ConfigDirectory!) : null;

    private static string DefaultSourceDirectory(AgentProvider provider)
        => AccountRules.ResolveConfigDirectory(new AgentAccount { Provider = provider });

    private static string DefaultCrossCliPoolDirectory() => Path.Combine(AppPaths.Root, "cross-cli-shared");

    private static string DefaultClaudeSourceGlobalConfig()
    {
        var (legacy, config) = AccountRules.GetClaudeGlobalConfigPaths(new AgentAccount { Provider = AgentProvider.Claude });
        return File.Exists(legacy) ? legacy : config;
    }

    /// <summary>
    /// 啟動 CLI 之前（背景執行緒）：同步全域指示、補上連結、第一次共用時合併帳戶原有的內容，回傳要多帶的啟動參數。
    /// 來源就是帳戶自己的資料夾（不該發生）時什麼都不做
    /// </summary>
    public AgentSharedLaunch Prepare(SharedAccount account, string workingDirectory)
        => Prepare(account, workingDirectory, crossCli: false);

    /// <summary>
    /// 啟動 CLI 之前（背景執行緒）：支援單家隔離帳戶共用（<paramref name="account"/> 不為 null）與跨四家 CLI 共用（<paramref name="crossCli"/> 為 true）
    /// </summary>
    public AgentSharedLaunch Prepare(SharedAccount? account, string workingDirectory, bool crossCli)
    {
        if (account == null && !crossCli)
            return AgentSharedLaunch.None;

        lock (Gate)
        {
            if (account == null)
            {
                SyncCrossCliCore();
                return AgentSharedLaunch.None;
            }

            var source = sourceDirectory(account.Provider);
            if (SamePath(source, account.ConfigDirectory))
            {
                if (crossCli)
                    SyncCrossCliCore();
                return AgentSharedLaunch.None;
            }

            var launch = PrepareCore(account, source, workingDirectory);
            if (crossCli)
            {
                SyncCrossCliCore();
                var statePath = StatePath(account.ConfigDirectory);
                var state = JsonFile.Read<SharedCustomizationState>(statePath) ?? new SharedCustomizationState();
                Try(account, "跨 CLI 後補同步全域指示", () => SyncInstructions(account, source, state));
                Try(account, "跨 CLI 後補連結 skills", () => LinkSkills(account, source));
                Try(account, "存共用狀態", () => JsonFile.Write(statePath, state));
            }
            return launch;
        }
    }

    private AgentSharedLaunch PrepareCore(SharedAccount account, string source, string workingDirectory)
    {
        var statePath = StatePath(account.ConfigDirectory);
        var state = JsonFile.Read<SharedCustomizationState>(statePath) ?? new SharedCustomizationState();
        // 合併失敗（來源暫時寫不進去）不標成共用中：下次啟動再試
        if (!state.Active && Try(account, "合併原有的記憶", () => MergeMemory(account, source, state.MergedAt)))
        {
            state.Active = true;
            state.MergedAt = DateTimeOffset.UtcNow;
        }
        Try(account, "同步全域指示", () => SyncInstructions(account, source, state));
        Try(account, "連結 skills", () => LinkSkills(account, source));
        foreach (var name in SharedCustomizationRules.LinkedDirectories(account.Provider))
            Try(account, $"連結 {name}", () => LinkDirectory(account, source, name));
        Try(account, "存共用狀態", () => JsonFile.Write(statePath, state));

        switch (account.Provider)
        {
            case AgentProvider.Claude:
            {
                string? memory = null, mcp = null;
                Try(account, "找記憶資料夾與 MCP", () =>
                {
                    var root = SharedCustomizationRules.MemoryProjectRoot(workingDirectory, Directory.Exists, ReadSmallFile);
                    memory = SharedCustomizationRules.SharedMemoryDirectory(source, root);
                    mcp = WriteClaudeMcpConfig(account, SharedCustomizationRules.ClaudeProjectKeys(root, workingDirectory));
                });
                return new AgentSharedLaunch(memory, mcp, Array.Empty<string>());
            }
            case AgentProvider.Codex:
            {
                IReadOnlyList<string> overrides = Array.Empty<string>();
                Try(account, "讀 MCP 設定", () =>
                {
                    var own = CodexConfigRules.ParseMcpServerNames(CodexConfigRules.ReadConfig(account.ConfigDirectory));
                    overrides = CodexConfigRules.BuildSharedMcpOverrides(CodexConfigRules.ReadConfig(source), own);
                });
                return new AgentSharedLaunch(null, null, overrides);
            }
            case AgentProvider.Antigravity:
            case AgentProvider.Grok:
                return AgentSharedLaunch.None;
            default:
                throw new ArgumentOutOfRangeException(nameof(account), account.Provider, null);
        }
    }

    /// <summary>
    /// 關掉共用：拿掉指向來源的 junction、把共用前移走的原件移回來、刪掉 MCP 設定檔。全域指示留著（帳戶照最後一次同步的內容繼續用），
    /// 共用期間寫在來源的記憶也留在來源（帳戶回頭讀自己原本的記憶）
    /// </summary>
    public void Unshare(AgentProvider provider, string configDirectory)
    {
        if (!AccountRules.SupportsSharedCustomizations(provider))
            return;
        var source = sourceDirectory(provider);
        if (SamePath(source, configDirectory))
            return;
        var account = new SharedAccount(string.Empty, string.Empty, provider, configDirectory);
        lock (Gate)
        {
            var statePath = StatePath(configDirectory);
            var state = JsonFile.Read<SharedCustomizationState>(statePath);
            Try(account, "拿掉 skills 的連結", () =>
            {
                var skills = Path.Combine(configDirectory, SharedCustomizationRules.SkillsDirectoryName);
                var sourceSkills = Path.Combine(source, SharedCustomizationRules.SkillsDirectoryName);
                if (Directory.Exists(skills))
                {
                    foreach (var entry in Directory.EnumerateDirectories(skills))
                    {
                        if (Junction.GetTarget(entry) is { } target && IsUnder(target, sourceSkills))
                            Directory.Delete(entry);
                    }
                }
                RestoreBackups(configDirectory, SharedCustomizationRules.SkillsDirectoryName);
            });
            foreach (var name in SharedCustomizationRules.LinkedDirectories(provider))
            {
                Try(account, $"拿掉 {name} 的連結", () =>
                {
                    var link = Path.Combine(configDirectory, name);
                    if (Junction.GetTarget(link) is { } target && SamePath(target, Path.Combine(source, name)))
                        Directory.Delete(link);
                    var backup = Path.Combine(configDirectory, SharedCustomizationRules.StateFolderName, BackupFolderName, name);
                    if (Directory.Exists(backup) && !Exists(link))
                        Directory.Move(backup, link);
                });
            }
            Try(account, "刪掉 MCP 設定檔", () => DeleteMcpFiles(configDirectory, keep: null));
            if (state != null)
            {
                state.Active = false;
                Try(account, "存共用狀態", () => JsonFile.Write(statePath, state));
            }
        }
    }

    // ───── 跨四家 CLI 共用（Claude、Codex、Antigravity、Grok） ─────

    /// <summary>同步四家 CLI 系統預設設定資料夾的全域指示與使用者層 skills（執行緒安全）</summary>
    public void SyncCrossCli()
    {
        lock (Gate)
            SyncCrossCliCore();
    }

    /// <summary>關閉跨四家 CLI 共用：移除指向跨 CLI 共用池的 skill junction、還原四家各自的備份原件，並清空共用池狀態</summary>
    public void UnshareCrossCli()
    {
        lock (Gate)
        {
            var poolDir = crossCliPoolDirectory();
            var poolSkills = Path.Combine(poolDir, SharedCustomizationRules.SkillsDirectoryName);
            var statePath = Path.Combine(poolDir, StateFileName);
            var state = JsonFile.Read<CrossCliSyncState>(statePath);

            foreach (var provider in AgentProviders.All)
            {
                TryCross(provider, "還原跨 CLI 共用設定", () =>
                {
                    var source = sourceDirectory(provider);
                    var skillsDir = SharedCustomizationRules.CrossCliSkillsDirectory(provider, source);
                    if (Directory.Exists(skillsDir))
                    {
                        foreach (var entry in Directory.EnumerateDirectories(skillsDir))
                        {
                            if (Junction.GetTarget(entry) is { } target && IsUnder(target, poolSkills))
                                Directory.Delete(entry);
                        }
                    }

                    var backupSkills = Path.Combine(source, SharedCustomizationRules.StateFolderName, CrossBackupFolderName, SharedCustomizationRules.SkillsDirectoryName);
                    if (Directory.Exists(backupSkills))
                    {
                        foreach (var entry in Directory.EnumerateDirectories(backupSkills))
                        {
                            var dest = Path.Combine(skillsDir, Path.GetFileName(entry));
                            if (Exists(dest))
                                continue;
                            Directory.CreateDirectory(skillsDir);
                            Directory.Move(entry, dest);
                        }
                    }

                    var instPath = SharedCustomizationRules.CrossCliInstructionPath(provider, source);
                    var backupInst = Path.Combine(source, SharedCustomizationRules.StateFolderName, CrossBackupFolderName, Path.GetFileName(instPath));
                    if (File.Exists(backupInst))
                    {
                        var backupContent = ReadText(backupInst) ?? string.Empty;
                        var curContent = ReadText(instPath);
                        if (!SharedCustomizationRules.IsBlank(curContent))
                        {
                            var curHash = SharedCustomizationRules.SyncedHash(curContent);
                            if (curHash != state?.PoolHash && curHash != SharedCustomizationRules.SyncedHash(backupContent))
                                BackupCrossFile(source, Path.GetFileName(instPath), curContent!, preserveFirst: false);
                        }
                        WriteText(instPath, backupContent);
                        File.Delete(backupInst);
                    }
                    else if (state?.CreatedProviders.Contains(provider.ToString()) == true && File.Exists(instPath))
                    {
                        var curContent = ReadText(instPath);
                        if (SharedCustomizationRules.IsBlank(curContent)
                            || SharedCustomizationRules.SyncedHash(curContent) == state.PoolHash)
                        {
                            File.Delete(instPath);
                        }
                    }
                });
            }

            TryCross(null, "清理跨 CLI 共用池", () =>
            {
                var poolFile = Path.Combine(poolDir, PoolInstructionsFileName);
                if (File.Exists(poolFile))
                    File.Delete(poolFile);
                if (Directory.Exists(poolSkills))
                    DirectoryRemover.TryDelete(poolSkills);
            });

            if (state != null)
            {
                state.Active = false;
                state.PoolHash = null;
                state.ProviderHashes.Clear();
                state.CreatedProviders.Clear();
                TryCross(null, "存跨 CLI 共用狀態", () => JsonFile.Write(statePath, state));
            }
        }
    }

    private void SyncCrossCliCore()
    {
        var poolDir = crossCliPoolDirectory();
        var statePath = Path.Combine(poolDir, StateFileName);
        var state = JsonFile.Read<CrossCliSyncState>(statePath) ?? new CrossCliSyncState();

        var sources = new Dictionary<AgentProvider, string>();
        foreach (var provider in AgentProviders.All)
        {
            TryCross(provider, "解析來源資料夾", () => sources[provider] = sourceDirectory(provider));
        }
        if (sources.Count == 0)
            return;

        bool grokCompat = sources.TryGetValue(AgentProvider.Grok, out var grokHome)
            && SharedCustomizationRules.IsGrokClaudeCompatEnabled(ReadText(Path.Combine(grokHome, "config.toml")));

        TryCross(null, "同步跨 CLI 全域指示", () => SyncCrossCliInstructions(poolDir, sources, state, grokCompat));
        TryCross(null, "同步跨 CLI skills", () => SyncCrossCliSkills(poolDir, sources, grokCompat));

        state.Active = true;
        TryCross(null, "存跨 CLI 共用狀態", () => JsonFile.Write(statePath, state));
    }

    private static void SyncCrossCliInstructions(
        string poolDir,
        IReadOnlyDictionary<AgentProvider, string> sources,
        CrossCliSyncState state,
        bool grokCompat)
    {
        var poolFile = Path.Combine(poolDir, PoolInstructionsFileName);
        string? grokAbsorb = null;
        if (grokCompat && sources.TryGetValue(AgentProvider.Grok, out var grokSource))
        {
            var grokInst = SharedCustomizationRules.CrossCliInstructionPath(AgentProvider.Grok, grokSource);
            if (File.Exists(grokInst))
            {
                grokAbsorb = ReadText(grokInst);
                if (!SharedCustomizationRules.IsBlank(grokAbsorb))
                    BackupCrossFile(grokSource, Path.GetFileName(grokInst), grokAbsorb!, preserveFirst: true);
                File.Delete(grokInst);
            }
            state.ProviderHashes.Remove(AgentProvider.Grok.ToString());
        }

        var activeTargets = AgentProviders.All
            .Where(p => sources.ContainsKey(p) && !(p == AgentProvider.Grok && grokCompat))
            .ToList();

        if (!state.Active || state.PoolHash == null)
        {
            state.CreatedProviders.Clear();
            if (grokCompat && grokAbsorb == null && sources.ContainsKey(AgentProvider.Grok))
                state.CreatedProviders.Add(AgentProvider.Grok.ToString());

            var seenHashes = new HashSet<string>(StringComparer.Ordinal);
            string? merged = null;

            void AppendUnique(string? text, string label)
            {
                if (SharedCustomizationRules.IsBlank(text))
                    return;
                var h = SharedCustomizationRules.Hash(text!);
                if (!seenHashes.Add(h))
                    return;
                merged = merged == null
                    ? text!.Replace("\r\n", "\n").Trim() + "\n"
                    : SharedCustomizationRules.MergeCrossCliInstructions(merged, text!, label);
            }

            if (File.Exists(poolFile))
                AppendUnique(ReadText(poolFile), "共用池");
            foreach (var p in activeTargets)
                AppendUnique(ReadText(SharedCustomizationRules.CrossCliInstructionPath(p, sources[p])), AccountRules.ProviderName(p));
            if (grokAbsorb != null)
                AppendUnique(grokAbsorb, AccountRules.ProviderName(AgentProvider.Grok));

            if (SharedCustomizationRules.IsBlank(merged))
            {
                if (File.Exists(poolFile))
                    File.Delete(poolFile);
                state.PoolHash = SharedCustomizationRules.BlankHash;
                foreach (var p in activeTargets)
                    state.ProviderHashes[p.ToString()] = SharedCustomizationRules.BlankHash;
                return;
            }

            WriteIfChanged(poolFile, merged!);
            var finalHash = SharedCustomizationRules.SyncedHash(merged);
            foreach (var p in activeTargets)
            {
                var path = SharedCustomizationRules.CrossCliInstructionPath(p, sources[p]);
                var cur = ReadText(path);
                if (SharedCustomizationRules.IsBlank(cur))
                    state.CreatedProviders.Add(p.ToString());
                else
                    BackupCrossFile(sources[p], Path.GetFileName(path), cur!, preserveFirst: true);
                WriteIfChanged(path, merged!);
                state.ProviderHashes[p.ToString()] = finalHash;
            }
            state.PoolHash = finalHash;
            return;
        }

        var changedNonBlank = new List<(AgentProvider Provider, string Text)>();
        var deletedProviders = new List<AgentProvider>();
        foreach (var p in activeTargets)
        {
            var path = SharedCustomizationRules.CrossCliInstructionPath(p, sources[p]);
            var cur = ReadText(path);
            var curHash = SharedCustomizationRules.SyncedHash(cur);
            if (state.ProviderHashes.TryGetValue(p.ToString(), out var prevHash))
            {
                if (curHash != prevHash)
                {
                    if (!SharedCustomizationRules.IsBlank(cur))
                        changedNonBlank.Add((p, cur!));
                    else if (prevHash != SharedCustomizationRules.BlankHash)
                        deletedProviders.Add(p);
                }
            }
            else if (!SharedCustomizationRules.IsBlank(cur) && curHash != state.PoolHash)
            {
                changedNonBlank.Add((p, cur!));
            }
        }

        string? targetContent;
        if (changedNonBlank.Count > 0)
        {
            targetContent = changedNonBlank[0].Text;
            var targetHash = SharedCustomizationRules.SyncedHash(targetContent);
            foreach (var extra in changedNonBlank.Skip(1))
            {
                if (SharedCustomizationRules.SyncedHash(extra.Text) != targetHash)
                {
                    var name = Path.GetFileName(SharedCustomizationRules.CrossCliInstructionPath(extra.Provider, sources[extra.Provider]));
                    BackupCrossFile(sources[extra.Provider], name, extra.Text, preserveFirst: false);
                }
            }
        }
        else if (!SharedCustomizationRules.IsBlank(grokAbsorb) && SharedCustomizationRules.SyncedHash(grokAbsorb) != state.PoolHash)
        {
            var poolText = ReadText(poolFile);
            targetContent = SharedCustomizationRules.IsBlank(poolText)
                ? grokAbsorb!.Replace("\r\n", "\n").Trim() + "\n"
                : SharedCustomizationRules.MergeCrossCliInstructions(poolText!, grokAbsorb!, AccountRules.ProviderName(AgentProvider.Grok));
        }
        else if (deletedProviders.Count > 0)
        {
            var poolText = ReadText(poolFile);
            if (deletedProviders.Count < activeTargets.Count && !SharedCustomizationRules.IsBlank(poolText))
            {
                // 只有部分廠牌的指示檔消失（例如重裝或清理單一目錄）：從共用池補回，避免誤刪其餘三家的指示檔
                targetContent = poolText;
            }
            else
            {
                if (!SharedCustomizationRules.IsBlank(poolText))
                    WriteText(Path.Combine(poolDir, $"{PoolInstructionsFileName}.{DateTime.Now:yyyyMMdd-HHmmss}.bak"), poolText!);
                targetContent = null;
            }
        }
        else
        {
            targetContent = ReadText(poolFile);
        }

        if (SharedCustomizationRules.IsBlank(targetContent))
        {
            if (File.Exists(poolFile))
                File.Delete(poolFile);
            foreach (var p in activeTargets)
            {
                var path = SharedCustomizationRules.CrossCliInstructionPath(p, sources[p]);
                if (File.Exists(path))
                    File.Delete(path);
                state.ProviderHashes[p.ToString()] = SharedCustomizationRules.BlankHash;
            }
            state.PoolHash = SharedCustomizationRules.BlankHash;
        }
        else
        {
            WriteIfChanged(poolFile, targetContent!);
            var finalHash = SharedCustomizationRules.SyncedHash(targetContent);
            foreach (var p in activeTargets)
            {
                var path = SharedCustomizationRules.CrossCliInstructionPath(p, sources[p]);
                WriteIfChanged(path, targetContent!);
                state.ProviderHashes[p.ToString()] = finalHash;
            }
            state.PoolHash = finalHash;
        }
    }

    private static void SyncCrossCliSkills(
        string poolDir,
        IReadOnlyDictionary<AgentProvider, string> sources,
        bool grokCompat)
    {
        var poolSkills = Path.Combine(poolDir, SharedCustomizationRules.SkillsDirectoryName);

        foreach (var provider in AgentProviders.All)
        {
            if (!sources.TryGetValue(provider, out var source))
                continue;
            var skillsDir = SharedCustomizationRules.CrossCliSkillsDirectory(provider, source);
            if (!Directory.Exists(skillsDir))
                continue;

            bool skipLinkBack = provider == AgentProvider.Grok && grokCompat;
            foreach (var entry in Directory.EnumerateDirectories(skillsDir).ToList())
            {
                var name = Path.GetFileName(entry);
                if (!SharedCustomizationRules.IsSharedSkill(provider, name))
                    continue;

                var poolEntry = Path.Combine(poolSkills, name);
                if (IsReparsePoint(entry))
                {
                    if (Junction.GetTarget(entry) is { } target && IsUnder(target, poolSkills) && (skipLinkBack || !Directory.Exists(target)))
                        Directory.Delete(entry);
                    continue;
                }

                CopyTree(entry, poolEntry, provider.ToString());
                MoveToCrossBackup(source, entry, Path.Combine(SharedCustomizationRules.SkillsDirectoryName, name));
                if (!skipLinkBack)
                    Junction.Create(entry, poolEntry);
            }
        }

        if (!Directory.Exists(poolSkills))
            return;

        foreach (var provider in AgentProviders.All)
        {
            if (!sources.TryGetValue(provider, out var source) || (provider == AgentProvider.Grok && grokCompat))
                continue;
            var skillsDir = SharedCustomizationRules.CrossCliSkillsDirectory(provider, source);
            foreach (var poolEntry in Directory.EnumerateDirectories(poolSkills))
            {
                var name = Path.GetFileName(poolEntry);
                if (!SharedCustomizationRules.IsSharedSkill(provider, name))
                    continue;
                var entry = Path.Combine(skillsDir, name);
                if (Exists(entry) || !Directory.Exists(poolEntry))
                    continue;
                Directory.CreateDirectory(skillsDir);
                Junction.Create(entry, poolEntry);
            }
        }
    }

    private static void BackupCrossFile(string source, string fileName, string content, bool preserveFirst)
    {
        var backup = Path.Combine(source, SharedCustomizationRules.StateFolderName, CrossBackupFolderName, fileName);
        if (File.Exists(backup))
        {
            if (preserveFirst)
                return;
            backup += "." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }
        WriteText(backup, content);
    }

    private static void MoveToCrossBackup(string source, string fullEntryPath, string relativePath)
    {
        var backup = Path.Combine(source, SharedCustomizationRules.StateFolderName, CrossBackupFolderName, relativePath);
        if (Exists(backup))
            backup += "." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.Move(fullEntryPath, backup);
    }

    private static bool TryCross(AgentProvider? provider, string step, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"跨 CLI 共用設定：{(provider is { } p ? AccountRules.ProviderName(p) + " " : string.Empty)}{step}失敗：{ex.Message}");
            return false;
        }
    }

    private static string StatePath(string configDirectory)
        => Path.Combine(configDirectory, SharedCustomizationRules.StateFolderName, StateFileName);

    // ───── 全域指示 ─────

    private static void SyncInstructions(SharedAccount account, string source, SharedCustomizationState state)
    {
        var fileName = SharedCustomizationRules.InstructionFileName(account.Provider);
        var sourcePath = Path.Combine(source, fileName);
        var accountPath = Path.Combine(account.ConfigDirectory, fileName);
        var sourceText = ReadText(sourcePath);
        var accountText = ReadText(accountPath);
        var action = SharedCustomizationRules.DecideInstructionSync(sourceText, accountText, state.InstructionHash);
        switch (action)
        {
            case InstructionSyncAction.None:
                state.InstructionHash = SharedCustomizationRules.SyncedHash(sourceText ?? accountText);
                return;
            case InstructionSyncAction.CopyToAccount:
                WriteText(accountPath, sourceText ?? string.Empty);
                state.InstructionHash = SharedCustomizationRules.SyncedHash(sourceText);
                break;
            case InstructionSyncAction.CopyToSource:
                WriteText(sourcePath, accountText!);
                state.InstructionHash = SharedCustomizationRules.SyncedHash(accountText);
                break;
            case InstructionSyncAction.MergeIntoSource:
            {
                var merged = SharedCustomizationRules.MergeInstructions(sourceText!, accountText!, account.Label);
                WriteText(sourcePath, merged);
                WriteText(accountPath, merged);
                state.InstructionHash = SharedCustomizationRules.SyncedHash(merged);
                break;
            }
            case InstructionSyncAction.KeepSourceBackupAccount:
            {
                var backup = Path.Combine(account.ConfigDirectory, SharedCustomizationRules.StateFolderName, BackupFolderName,
                    $"{fileName}.{DateTime.Now:yyyyMMdd-HHmmss}");
                WriteText(backup, accountText!);
                if (SharedCustomizationRules.IsBlank(sourceText))
                    File.Delete(accountPath);
                else
                    WriteText(accountPath, sourceText!);
                state.InstructionHash = SharedCustomizationRules.SyncedHash(sourceText);
                Log.Warn($"共用設定：帳戶 {account.Id} 的 {fileName} 和來源都改過，以來源為準，帳戶那份另存在 {backup}");
                break;
            }
            case InstructionSyncAction.DeleteAccount:
                File.Delete(accountPath);
                state.InstructionHash = SharedCustomizationRules.BlankHash;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
        Log.Info($"共用設定：帳戶 {account.Id} 的 {fileName} {action}");
    }

    // ───── 連結 ─────

    /// <summary>
    /// skills\ 逐一連結：來源有、帳戶沒有的建 junction；帳戶自己有、來源沒有的先複製到來源、原件移到備份再連結；
    /// 兩邊都有同名的真資料夾就留著帳戶自己的（它的版本優先）；指向來源但來源已刪掉的連結拿掉
    /// </summary>
    private static void LinkSkills(SharedAccount account, string source)
    {
        var skills = Path.Combine(account.ConfigDirectory, SharedCustomizationRules.SkillsDirectoryName);
        var sourceSkills = Path.Combine(source, SharedCustomizationRules.SkillsDirectoryName);
        if (Directory.Exists(skills))
        {
            foreach (var entry in Directory.EnumerateDirectories(skills))
            {
                var name = Path.GetFileName(entry);
                if (!SharedCustomizationRules.IsSharedSkill(account.Provider, name))
                    continue;
                var sourceEntry = Path.Combine(sourceSkills, name);
                if (IsReparsePoint(entry))
                {
                    if (Junction.GetTarget(entry) is { } target && IsUnder(target, sourceSkills) && !Directory.Exists(target))
                        Directory.Delete(entry);
                    continue;
                }
                if (Directory.Exists(sourceEntry))
                    continue;
                CopyTree(entry, sourceEntry, account.Id);
                MoveToBackup(account.ConfigDirectory, Path.Combine(SharedCustomizationRules.SkillsDirectoryName, name));
                Junction.Create(entry, sourceEntry);
                Log.Info($"共用設定：帳戶 {account.Id} 的 skill「{name}」合併進來源");
            }
        }

        if (!Directory.Exists(sourceSkills))
            return;
        foreach (var sourceEntry in Directory.EnumerateDirectories(sourceSkills))
        {
            var name = Path.GetFileName(sourceEntry);
            var entry = Path.Combine(skills, name);
            if (!SharedCustomizationRules.IsSharedSkill(account.Provider, name) || Exists(entry) || !Directory.Exists(sourceEntry))
                continue;
            Directory.CreateDirectory(skills);
            Junction.Create(entry, sourceEntry);
        }
    }

    /// <summary>
    /// 整個資料夾連結到來源：帳戶沒有就建 junction（來源也沒有就等來源出現）；帳戶有真資料夾就先把裡面的檔合併進來源（同名不同內容以帶帳戶代碼的檔名另存）、
    /// 原件移到備份再連結；空的直接換成連結。指向來源但來源已刪掉的連結拿掉，指向別處的（使用者自己建的）不動
    /// </summary>
    private static void LinkDirectory(SharedAccount account, string source, string name)
    {
        var link = Path.Combine(account.ConfigDirectory, name);
        var target = Path.Combine(source, name);
        if (IsReparsePoint(link))
        {
            if (Junction.GetTarget(link) is { } current && SamePath(current, target) && !Directory.Exists(target))
                Directory.Delete(link);
            return;
        }
        if (Directory.Exists(link))
        {
            if (Directory.EnumerateFileSystemEntries(link).Any())
            {
                CopyTree(link, target, account.Id);
                MoveToBackup(account.ConfigDirectory, name);
                Log.Info($"共用設定：帳戶 {account.Id} 的 {name} 合併進來源");
            }
            else
            {
                Directory.Delete(link);
            }
        }
        if (Directory.Exists(target) && !Exists(link))
            Junction.Create(link, target);
    }

    private static void MoveToBackup(string configDirectory, string relativePath)
    {
        var backup = Path.Combine(configDirectory, SharedCustomizationRules.StateFolderName, BackupFolderName, relativePath);
        if (Exists(backup))
            backup += "." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.Move(Path.Combine(configDirectory, relativePath), backup);
    }

    /// <summary>把備份資料夾 backup\&lt;子資料夾&gt;\ 底下的項目移回帳戶的 &lt;子資料夾&gt;\（帳戶那裡已有同名的不動）</summary>
    private static void RestoreBackups(string configDirectory, string folder)
    {
        var backups = Path.Combine(configDirectory, SharedCustomizationRules.StateFolderName, BackupFolderName, folder);
        if (!Directory.Exists(backups))
            return;
        foreach (var entry in Directory.EnumerateDirectories(backups))
        {
            var destination = Path.Combine(configDirectory, folder, Path.GetFileName(entry));
            if (Exists(destination))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(entry, destination);
        }
    }

    // ───── 記憶 ─────

    /// <summary>
    /// 帳戶原有的自動記憶（projects\&lt;專案&gt;\memory\）合併進來源同名的專案資料夾；<paramref name="since"/> 不為 null 時只合併之後改過的檔
    /// （關掉共用期間帳戶自己寫的）。帳戶的原件不動（共用期間 CLI 不會讀它，關掉共用時照舊）
    /// </summary>
    private static void MergeMemory(SharedAccount account, string source, DateTimeOffset? since)
    {
        if (account.Provider != AgentProvider.Claude)
            return;
        var projects = Path.Combine(account.ConfigDirectory, "projects");
        if (!Directory.Exists(projects))
            return;
        int merged = 0;
        foreach (var project in Directory.EnumerateDirectories(projects))
        {
            var memory = Path.Combine(project, "memory");
            if (!Directory.Exists(memory) || IsReparsePoint(memory))
                continue;
            var targetMemory = Path.Combine(source, "projects", Path.GetFileName(project), "memory");
            // 索引先合併：帳戶索引裡的行排在另存檔的說明行前面
            var files = EnumerateFiles(memory)
                .OrderBy(f => SharedCustomizationRules.PlanMemoryFile(Path.GetRelativePath(memory, f), true, false) == MemoryMergeAction.AppendIndex ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (since is { } after && File.GetLastWriteTimeUtc(file) <= after.UtcDateTime)
                    continue;
                var relative = Path.GetRelativePath(memory, file);
                var target = Path.Combine(targetMemory, relative);
                var content = File.ReadAllBytes(file);
                bool exists = File.Exists(target);
                var plan = SharedCustomizationRules.PlanMemoryFile(relative, exists, exists && File.ReadAllBytes(target).AsSpan().SequenceEqual(content));
                switch (plan)
                {
                    case MemoryMergeAction.Skip:
                        continue;
                    case MemoryMergeAction.Copy:
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, content);
                        break;
                    case MemoryMergeAction.AppendIndex:
                        WriteIfChanged(target, SharedCustomizationRules.AppendIndexLines(ReadText(target) ?? string.Empty, Utf8.GetString(content)));
                        break;
                    case MemoryMergeAction.CopyRenamed:
                    {
                        var renamed = SharedCustomizationRules.RenamedForAccount(relative, account.Id);
                        var renamedPath = Path.Combine(targetMemory, renamed);
                        if (File.Exists(renamedPath) && File.ReadAllBytes(renamedPath).AsSpan().SequenceEqual(content))
                            continue;
                        File.WriteAllBytes(renamedPath, content);
                        var index = Path.Combine(targetMemory, SharedCustomizationRules.MemoryIndexFileName);
                        WriteIfChanged(index, SharedCustomizationRules.AppendIndexLines(ReadText(index) ?? string.Empty,
                            SharedCustomizationRules.IndexLineForRenamed(renamed, relative, account.Label)));
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(plan), plan, null);
                }
                merged++;
            }
        }
        if (merged > 0)
            Log.Info($"共用設定：帳戶 {account.Id} 有 {merged} 個記憶檔合併進來源");
    }

    // ───── MCP ─────

    /// <summary>
    /// 寫 Claude 的 --mcp-config 檔，回路徑（沒有要共用的回 null）。檔名照內容的雜湊：內容一樣共用一個檔、寫好就不再改，
    /// 同一個帳戶的兩個對話在不同專案同時啟動也不會蓋掉對方正要讀的檔。裡面可能有 MCP 的環境變數（金鑰），所以放在帳戶的設定資料夾裡（和登入憑證同一層保護）
    /// </summary>
    private string? WriteClaudeMcpConfig(SharedAccount account, IReadOnlyList<string> projectKeys)
    {
        var (sourceUser, sourceProject) = ReadClaudeMcp(claudeSourceGlobalConfig(), projectKeys);
        var (legacy, configPath) = AccountRules.GetClaudeGlobalConfigPaths(new AgentAccount { Provider = AgentProvider.Claude, ConfigDirectory = account.ConfigDirectory });
        var (ownUser, ownProject) = ReadClaudeMcp(File.Exists(legacy) ? legacy : configPath, projectKeys);
        var config = SharedCustomizationRules.BuildClaudeMcpConfig(sourceUser, sourceProject, SharedCustomizationRules.ServerNames(ownUser, ownProject));
        if (config == null)
        {
            DeleteMcpFiles(account.ConfigDirectory, keep: null);
            return null;
        }
        var json = config.ToString(Formatting.Indented);
        var path = Path.Combine(account.ConfigDirectory, SharedCustomizationRules.StateFolderName,
            McpFilePrefix + SharedCustomizationRules.Hash(json)[..16] + ".json");
        if (!File.Exists(path))
            WriteText(path, json);
        DeleteMcpFiles(account.ConfigDirectory, keep: path);
        return path;
    }

    private static (JObject? User, JObject? Project) ReadClaudeMcp(string path, IReadOnlyList<string> projectKeys)
    {
        if (!File.Exists(path))
            return (null, null);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return SharedCustomizationRules.ReadClaudeMcpServers(reader, projectKeys);
    }

    /// <summary>刪掉舊的 MCP 設定檔（留 keep；一天內寫的也留著：可能有剛啟動、還沒讀完的 CLI）</summary>
    private static void DeleteMcpFiles(string configDirectory, string? keep)
    {
        var folder = Path.Combine(configDirectory, SharedCustomizationRules.StateFolderName);
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, McpFilePrefix + "*.json"))
        {
            if (keep != null && (SamePath(file, keep) || File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-1)))
                continue;
            File.Delete(file);
        }
    }

    // ───── 檔案 ─────

    /// <summary>複製整個資料夾（不走進連結）；目的地已有同名不同內容的檔，以帶帳戶代碼的檔名另存</summary>
    private static void CopyTree(string from, string to, string accountId)
    {
        Directory.CreateDirectory(to);
        foreach (var file in EnumerateFiles(from))
        {
            var relative = Path.GetRelativePath(from, file);
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                    continue;
                target = Path.Combine(to, SharedCustomizationRules.RenamedForAccount(relative, accountId));
            }
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>資料夾底下所有的檔（含子資料夾，不走進 junction 與符號連結）</summary>
    private static IEnumerable<string> EnumerateFiles(string root)
        => Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        });

    private static string? ReadText(string path)
    {
        if (!File.Exists(path))
            return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 原地寫入（UTF-8 無 BOM）：來源的 CLAUDE.md 可能是指到 dotfiles 的符號連結，寫暫存檔再改名會把連結換成一般檔，所以直接寫進原本的檔
    /// </summary>
    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8);
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (ReadText(path) != content)
            WriteText(path, content);
    }

    /// <summary>讀 .git 檔、commondir、gitdir 這類小檔；讀不到或太大回 null</summary>
    private static string? ReadSmallFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 4096 ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return p.Length > f.Length && p.StartsWith(f, StringComparison.OrdinalIgnoreCase) && p[f.Length] == Path.DirectorySeparatorChar;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>執行一步；失敗只記錄（回 false），不擋對話</summary>
    private static bool Try(SharedAccount account, string step, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"共用設定：帳戶 {account.Id}（{account.ConfigDirectory}）{step}失敗：{ex.Message}");
            return false;
        }
    }
}
