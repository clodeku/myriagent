using System.Text.RegularExpressions;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Update;

namespace Myriagent.Hub;

/// <summary>
/// 標題與側欄對話清單右鍵選單的「自動重新命名」（autoTitle）：產生對話目前主要任務的簡短摘要當標題後改名（同 rename，Claude 的工作階段名稱也跟著改）。
/// 節錄與要試的帳戶在 UI 執行緒決定好，背景依序跑各帳戶的 CLI 直到有標題（AutoTitleRules），結果回 UI 執行緒套用
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>正在產生標題（只在記憶體；標頭與清單的 titling，網頁據此在標題與側欄那一列轉圈、選單項目停用）</summary>
        public bool Titling;
    }

    /// <summary>app 結束時中止還在跑的標題產生</summary>
    private readonly CancellationTokenSource titleCancel = new();

    /// <summary>產生標題（背景執行緒呼叫，每個帳戶一次）；測試換成假的，預設跑那個帳戶的 CLI 一次</summary>
    internal Func<AutoTitleRequest, CancellationToken, Task<AutoTitleResult>> TitleGenerator { get; set; } = AutoTitleRunner.RunAsync;

    /// <summary>找產生標題用的 CLI；測試換成假的（預設 CliManager.GetLauncher，會找這台真的 CLI）</summary>
    internal Func<AgentProvider, AgentLauncher?>? TitleLauncher { get; set; }

    private void AutoTitle(IAgentView view, ConversationState? c)
    {
        if (c == null || c.IsDraft || c.IsReplica || c.Titling)
            return;
        var items = EnsureItems(c);
        var excerpt = AutoTitleRules.BuildExcerpt(items, c.Running);
        var fallback = AutoTitleRules.Fallback(items);
        if (excerpt == null && fallback == null)
        {
            Toast("這個對話還沒有內容可以命名", view: view);
            return;
        }

        var requests = excerpt == null ? new List<AutoTitleRequest>() : TitleRequests(c, excerpt);
        // 對話開著的 Claude 程序：先在程序裡產生（generate_session_title，不必另開 CLI；同一個程序只有第一次是新的標題）
        var live = excerpt != null && c.Meta.Provider == AgentProvider.Claude && c.Session is { CanGenerateTitle: true } session ? session : null;
        if (requests.Count == 0 && live == null)
        {
            // 不能呼叫模型（沒有能產生標題的帳戶、CLI 不在或更新中）：直接用進行中的待辦步驟或最後一則使用者訊息
            if (fallback != null)
                Rename(c, fallback);
            else
                Toast("目前無法產生標題", view: view);
            return;
        }

        c.Titling = true;
        MarkHeaderDirty(c);
        MarkListDirty();
        var id = c.Meta.Id;
        var before = c.Meta.Title;
        var generator = TitleGenerator;
        var token = titleCancel.Token;
        Log.Info($"對話 {id} 依目前的工作產生標題（{string.Join("→", requests.Select(r => $"{r.AccountLabel}／{r.Method}"))}）");
        _ = Task.Run(async () =>
        {
            // 依序試，第一個有標題的就用；全部失敗時說明第一個（這個對話自己的帳戶）的原因
            var failures = new List<(AutoTitleRequest Request, AutoTitleResult Result)>();
            AutoTitleResult? success = null;
            AutoTitleRequest? used = null;
            if (live != null)
            {
                try
                {
                    if (AutoTitleRules.CleanTitle(await live.GenerateTitleAsync(excerpt!)) is { } inProcess)
                        success = new AutoTitleResult(inProcess, null);
                }
                catch (Exception ex)
                {
                    Log.Warn($"對話 {id} 以開著的 Claude 程序產生標題失敗：{ex.Message}");
                }
            }
            foreach (var request in success != null ? [] : requests)
            {
                AutoTitleResult result;
                try
                {
                    result = await generator(request, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    result = new AutoTitleResult(null, ex.Message);
                }
                if (result.Title != null)
                {
                    success = result;
                    used = request;
                    break;
                }
                failures.Add((request, result));
            }
            host.BeginInvoke(() => OnAutoTitleDone(view, id, before, fallback, success, used, failures));
        });
    }

    /// <summary>
    /// 要試的帳戶（AutoTitleRules.PlanAccounts 的順序）：略過已移除、CLI 更新中、找不到 CLI 的，最多 MaxAttempts 個；
    /// 環境照各自帳戶（AccountRules.ApplyCommandEnvironment），所以借用的帳戶也只用它自己的登入
    /// </summary>
    private List<AutoTitleRequest> TitleRequests(ConversationState c, string excerpt)
    {
        var candidates = accounts.Accounts.Select(a => new AutoTitleCandidate(a.Id, a.Provider, accounts.GetState(a.Id).Status, accounts.GetUsage(a.Id)?.Windows, a.Order));
        var requests = new List<AutoTitleRequest>();
        foreach (var accountId in AutoTitleRules.PlanAccounts(c.Meta.AccountId, candidates, exhaustedAccounts, AccountSwitchClock()))
        {
            if (requests.Count >= AutoTitleRules.MaxAttempts)
                break;
            if (accounts.Find(accountId) is not { } account || IsCliUpdateBlocking(account.Provider))
                continue;
            var launcher = TitleLauncher != null ? TitleLauncher(account.Provider) : cli.GetLauncher(account.Provider);
            if (launcher == null)
                continue;
            requests.Add(new AutoTitleRequest(
                AutoTitleRules.MethodFor(account.Provider),
                launcher,
                env => AccountRules.ApplyCommandEnvironment(account, env),
                excerpt,
                account.Label,
                AccountRules.ResolveConfigDirectory(account)));
        }
        return requests;
    }

    /// <summary>
    /// 標題產生完（UI 執行緒）：有標題就改名；每個帳戶都失敗時改用退而求其次的標題並說明原因。
    /// 產生期間標題被改過（手動改名、在 claude.ai 改名）就不蓋掉；對話已刪除就不處理
    /// </summary>
    private void OnAutoTitleDone(IAgentView view, string id, string before, string? fallback, AutoTitleResult? success, AutoTitleRequest? used,
        IReadOnlyList<(AutoTitleRequest Request, AutoTitleResult Result)> failures)
    {
        foreach (var (request, failed) in failures)
            Log.Warn($"對話 {id} 以「{request.AccountLabel}」產生標題失敗：{failed.Error}");
        if (disposed || !conversations.TryGetValue(id, out var c))
            return;
        c.Titling = false;
        MarkHeaderDirty(c);
        MarkListDirty();
        if (!string.Equals(c.Meta.Title, before, StringComparison.Ordinal))
            return;
        if (success?.Title is { } title)
        {
            if (failures.Count > 0)
                Log.Info($"對話 {id} 改用「{used?.AccountLabel}」產生了標題");
            Rename(c, title);
            return;
        }
        // 只試了開著的程序（沒有能產生標題的帳戶）而且它沒給新的標題：同不能呼叫模型，直接用退而求其次的標題
        if (failures.Count == 0)
        {
            if (fallback != null)
                Rename(c, fallback);
            else
                Toast("目前無法產生標題", view: view);
            return;
        }
        var result = failures[0].Result;
        if (fallback != null)
        {
            Rename(c, fallback);
            Toast($"沒能用 AI 產生標題（{result.Error}），已改用最近的訊息命名", view: view);
        }
        else
        {
            Toast($"沒能產生標題：{result.Error}", isError: true, view: view);
        }
    }

    /// <summary>app 結束：中止還在跑的標題產生（CliRunner 結束程序樹）</summary>
    private void CancelAutoTitles()
    {
        try
        {
            titleCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>實際跑 CLI 產生標題（背景執行緒）：工作資料夾是快取下的空資料夾（不讀到任何專案的 CLAUDE.md、AGENTS.md）</summary>
internal static class AutoTitleRunner
{
    private static readonly Regex AntigravityConvIdRegex = new(
        @"(?:brain[\\/]|""?(?:conversation_id|conversationId)""?\s*[=:]\s*""?)([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<AutoTitleResult> RunAsync(AutoTitleRequest request, CancellationToken ct)
    {
        var workDirectory = Path.Combine(AppPaths.CacheDirectory, "autotitle-cwd");
        var tempDirectory = Path.Combine(AppPaths.CacheDirectory, "autotitle-tmp");
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(tempDirectory);
        switch (request.Method)
        {
            case AutoTitleMethod.ClaudePrint:
            {
                var run = await CliRunner.RunAsync(request.Launcher, AutoTitleRules.BuildClaudeArgs(), request.ConfigureEnvironment, AutoTitleRules.Timeout, ct,
                    workDirectory, input: request.Excerpt);
                return AutoTitleRules.ParseClaude(run);
            }
            case AutoTitleMethod.CodexExec:
            {
                var output = Path.Combine(tempDirectory, $"codex-{Guid.NewGuid():N}.txt");
                try
                {
                    var run = await CliRunner.RunAsync(request.Launcher, AutoTitleRules.BuildCodexArgs(output), request.ConfigureEnvironment, AutoTitleRules.Timeout, ct,
                        workDirectory, input: request.Excerpt);
                    var lastMessage = File.Exists(output) ? await File.ReadAllTextAsync(output, ct) : null;
                    return AutoTitleRules.ParseCodex(run, lastMessage);
                }
                finally
                {
                    DeleteFileQuietly(output);
                }
            }
            case AutoTitleMethod.AntigravityPrint:
            {
                var logFile = Path.Combine(tempDirectory, $"agy-{Guid.NewGuid():N}.log");
                string? convId = null;
                try
                {
                    var run = await CliRunner.RunAsync(request.Launcher, AutoTitleRules.BuildAntigravityArgs(request.Excerpt, logFile),
                        request.ConfigureEnvironment, AutoTitleRules.Timeout, ct, workDirectory, input: null);
                    return AutoTitleRules.ParseAntigravity(run, out convId);
                }
                finally
                {
                    convId ??= TryExtractAntigravityConversationId(logFile);
                    DeleteFileQuietly(logFile);
                    CleanupAntigravityArtifacts(request.ConfigDirectory, convId);
                }
            }
            case AutoTitleMethod.GrokPrint:
            {
                var sessionId = Guid.NewGuid().ToString("D");
                var promptFile = Path.Combine(tempDirectory, $"grok-{Guid.NewGuid():N}.txt");
                try
                {
                    await File.WriteAllTextAsync(promptFile, request.Excerpt, ct);
                    var run = await CliRunner.RunAsync(request.Launcher, AutoTitleRules.BuildGrokArgs(promptFile, sessionId),
                        request.ConfigureEnvironment, AutoTitleRules.Timeout, ct, workDirectory, input: null);
                    return AutoTitleRules.ParseGrok(run);
                }
                finally
                {
                    DeleteFileQuietly(promptFile);
                    CleanupGrokSession(request.ConfigDirectory, sessionId);
                }
            }
            case AutoTitleMethod.None:
                return new AutoTitleResult(null, "這種代理不支援");
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Method, null);
        }
    }

    internal static string? TryExtractAntigravityConversationId(string logFile)
    {
        try
        {
            if (!File.Exists(logFile))
                return null;
            using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            char[] buf = new char[16 * 1024];
            int n = reader.ReadBlock(buf, 0, buf.Length);
            if (n <= 0)
                return null;
            var match = AntigravityConvIdRegex.Match(new string(buf, 0, n));
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void CleanupAntigravityArtifacts(string? agyHome, string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(agyHome) || string.IsNullOrWhiteSpace(conversationId) || !Guid.TryParse(conversationId.Trim(), out var guid))
            return;
        var safeId = guid.ToString("D");
        var convDir = Path.Combine(agyHome, "conversations");
        foreach (var suffix in new[] { ".db", ".db-wal", ".db-shm" })
            DeleteFileQuietly(Path.Combine(convDir, safeId + suffix));
        var brainDir = Path.Combine(agyHome, "brain", safeId);
        if (Directory.Exists(brainDir))
            DirectoryRemover.TryDelete(brainDir);
    }

    internal static void CleanupGrokSession(string? grokHome, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(grokHome) || string.IsNullOrWhiteSpace(sessionId) || !Guid.TryParse(sessionId.Trim(), out var guid))
            return;
        var safeId = guid.ToString("D");
        var sessionsRoot = Path.Combine(grokHome, "sessions");
        if (!Directory.Exists(sessionsRoot))
            return;
        try
        {
            foreach (var projectDir in Directory.EnumerateDirectories(sessionsRoot))
            {
                var target = Path.Combine(projectDir, safeId);
                if (!Directory.Exists(target))
                    continue;
                DirectoryRemover.TryDelete(target);
                var remaining = Directory.GetFileSystemEntries(projectDir);
                if (remaining.Length == 0
                    || (remaining.Length == 1 && !Directory.Exists(remaining[0])
                        && string.Equals(Path.GetFileName(remaining[0]), "prompt_history.jsonl", StringComparison.OrdinalIgnoreCase)))
                {
                    DirectoryRemover.TryDelete(projectDir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
