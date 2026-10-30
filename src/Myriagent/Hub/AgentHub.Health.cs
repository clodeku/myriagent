using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Health;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 儀表板的健康度（web-protocol 的 health／healthCheck／healthFix）：每個本機帳戶每次對話都會載入的東西（全域指示、自動記憶索引、skill 清單、
// MCP、外掛、設定檔）有沒有太肥，以及電腦層級的 CLAUDE.md／AGENTS.md／GEMINI.md 要不要合併與同步。網頁切到儀表板時送 healthCheck，
// 30 秒內分析過、帳戶也沒變就不重做；「重新分析」帶 force。掃描在背景執行緒（只讀），結果回 UI 執行緒才存與送。
// 「用代理合併」「用代理精簡」「用代理整理」（記憶索引）以選的帳戶開草稿，輸入框預先填好說明與流程（不自動送出，使用者看過再送）。
// 沒掛掃描器（AttachHealth；測試與 HubHarness 預設）時什麼都不做、也不送 health。瀏覽器版與其他電腦不送（只給這台的主視窗）
internal sealed partial class AgentHub
{
    /// <summary>不帶 force 的 healthCheck 在這段時間內分析過（帳戶也沒變）就不重做</summary>
    internal static readonly TimeSpan HealthMinInterval = TimeSpan.FromSeconds(30);

    private IHealthScanner? healthScanner;
    private HealthScanResult? healthResult;
    private List<AccountHealth> healthAccounts = new();
    private InstructionsReport? healthInstructions;
    private bool healthRunning;
    private bool healthAgain;
    private long healthFinishedTick;
    private string? healthSignature;
    private readonly CancellationTokenSource healthCancel = new();

    /// <summary>各 Claude 帳戶最近一次由 CLI 回報的清單（儀表板的 MCP 檢查順便問的；只在記憶體）</summary>
    private readonly Dictionary<string, ClaudeCliInventory> cliInventory = new(StringComparer.Ordinal);

    /// <summary>MCP 檢查拿到 CLI 的清單（UI 執行緒）：已經分析過就以新的清單重新評估、送出</summary>
    private void OnCliInventory(string accountId, ClaudeCliInventory inventory)
    {
        cliInventory[accountId] = inventory;
        if (healthResult == null || healthScanner == null)
            return;
        healthAccounts = healthResult.Accounts.Select(s => HealthRules.Evaluate(s, cliInventory.GetValueOrDefault(s.Account.Id))).ToList();
        PostHealth();
    }

    /// <summary>掛上健康度的掃描器（AppController；只掛一次）。沒掛時不分析、不送</summary>
    internal void AttachHealth(IHealthScanner scanner) => healthScanner = scanner ?? throw new ArgumentNullException(nameof(scanner));

    /// <summary>要分析的本機帳戶（UI 執行緒取快照：資料夾在這裡解析好）</summary>
    private List<HealthAccountInput> HealthInputs()
        => accounts.Accounts.Select(a =>
        {
            string? globalConfig = null;
            if (a.Provider == AgentProvider.Claude)
            {
                var (legacy, config) = AccountRules.GetClaudeGlobalConfigPaths(a);
                globalConfig = File.Exists(legacy) ? legacy : config;
            }
            var shared = SharedCustomizationRules.IsActive(a) ? AccountRules.ResolveConfigDirectory(new AgentAccount { Provider = a.Provider }) : null;
            return new HealthAccountInput(a.Id, a.Label, a.Provider, a.IsSystemDefault, AccountRules.ResolveConfigDirectory(a), a.SafeMode, shared, globalConfig);
        }).ToList();

    /// <summary>帳戶的組成（id、資料夾、精簡模式、共用、跨 CLI 共用開關）：變了就不受 30 秒的限制</summary>
    private string HealthSignatureOf(List<HealthAccountInput> inputs)
        => $"{settings.ShareCrossCliCustomizations}|" + string.Join("|", inputs.Select(i => $"{i.Id};{i.ConfigDirectory};{i.SafeMode};{i.SharedSourceDirectory};{i.Label}"));

    /// <summary>healthCheck：分析中就排一次（force 才排）；不帶 force 時剛分析過、帳戶沒變就只重送目前的結果</summary>
    private void RequestHealth(IAgentView? view, bool force)
    {
        if (healthScanner == null || disposed)
            return;
        if (healthRunning)
        {
            healthAgain |= force;
            return;
        }
        var inputs = HealthInputs();
        var signature = HealthSignatureOf(inputs);
        if (!force && healthResult != null && signature == healthSignature
            && Environment.TickCount64 - healthFinishedTick < HealthMinInterval.TotalMilliseconds)
        {
            if (view != null)
                TryPost(new[] { view }, RenderHealthMessage);
            return;
        }
        healthRunning = true;
        healthAgain = false;
        PostHealth();
        var scanner = healthScanner;
        var ct = healthCancel.Token;
        _ = Task.Run(() => scanner.Scan(inputs, ct), ct).ContinueWith(t => host.BeginInvoke(() => OnHealthScanned(t, signature)), TaskScheduler.Default);
    }

    private void OnHealthScanned(Task<HealthScanResult> task, string signature)
    {
        healthRunning = false;
        if (disposed)
            return;
        if (task.IsCompletedSuccessfully)
        {
            var result = task.Result;
            healthResult = result;
            healthAccounts = result.Accounts.Select(s => HealthRules.Evaluate(s, cliInventory.GetValueOrDefault(s.Account.Id))).ToList();
            healthInstructions = InstructionCompareRules.Compare(result.Docs);
            healthSignature = signature;
        }
        else if (!task.IsCanceled)
        {
            Log.Error("健康度分析失敗", task.Exception?.GetBaseException() ?? new InvalidOperationException("健康度分析失敗"));
            Toast("健康度分析失敗：" + (task.Exception?.GetBaseException().Message ?? "未知的錯誤"), isError: true);
        }
        healthFinishedTick = Environment.TickCount64;
        PostHealth();
        if (healthAgain)
            RequestHealth(null, force: true);
    }

    /// <summary>送給這台的主視窗（瀏覽器版、彈出視窗、同步鏡像都不送）</summary>
    private void PostHealth()
    {
        if (healthScanner == null)
            return;
        var targets = views.Values.Where(v => v.Id != SyncViewId && !v.IsPopout).ToList();
        if (targets.Count > 0)
            TryPost(targets, RenderHealthMessage);
    }

    private JObject RenderHealthMessage() => new() { ["type"] = "health", ["health"] = RenderHealth() };

    /// <summary>init 與 health 訊息的 health（HealthView）；沒掛掃描器是 null</summary>
    internal JObject? RenderHealth()
    {
        if (healthScanner == null)
            return null;
        var accountsView = new JObject();
        foreach (var a in healthAccounts)
            accountsView[a.AccountId] = HealthRules.Render(a);
        return new JObject
        {
            ["running"] = healthRunning,
            ["checkedAt"] = healthResult != null ? new DateTimeOffset(DateTime.SpecifyKind(healthResult.CheckedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null,
            ["accounts"] = accountsView,
            ["instructions"] = healthInstructions != null ? InstructionCompareRules.Render(healthInstructions) : null,
        };
    }

    /// <summary>
    /// healthFix：以 accountId 的帳戶開草稿、輸入框預先填好說明（task＝merge：合併電腦層級的指示檔；trim：精簡 targetAccountId 的全域指示；
    /// memory：整理 targetAccountId 超過或接近上限的自動記憶索引）。
    /// 工作資料夾是使用者資料夾（各 CLI 的設定資料夾都在底下）。還沒分析過、沒有可合併的、帳戶不在時提示原因
    /// </summary>
    private void HealthFix(IAgentView view, string? task, string? accountId, string? targetAccountId, string? paneId)
    {
        if (healthScanner == null)
            return;
        var account = accounts.Find(accountId);
        if (account == null)
        {
            Toast("找不到要用的帳戶", isError: true, view);
            return;
        }
        string prompt;
        var now = DateTime.Now;
        switch (task)
        {
            case "merge":
                if (healthInstructions is not { Mergeable: true } report)
                {
                    Toast("目前沒有需要合併的指示檔，請先重新分析", isError: true, view);
                    return;
                }
                prompt = InstructionCompareRules.BuildMergePrompt(report, now);
                break;
            case "trim":
                var scan = healthResult?.Accounts.FirstOrDefault(s => s.Account.Id == targetAccountId);
                var health = healthAccounts.FirstOrDefault(h => h.AccountId == targetAccountId);
                if (scan == null || health == null || scan.Instructions.Count == 0)
                {
                    Toast("這個帳戶沒有可以精簡的全域指示，請先重新分析", isError: true, view);
                    return;
                }
                prompt = InstructionCompareRules.BuildTrimPrompt(scan, health, now);
                break;
            case "memory":
                var memoryScan = healthResult?.Accounts.FirstOrDefault(s => s.Account.Id == targetAccountId);
                if (memoryScan?.MemoryIndexes is not { } indexes || !indexes.Any(HealthRules.MemoryNeedsTrim))
                {
                    Toast("這個帳戶的記憶索引沒有超過或接近上限，請先重新分析", isError: true, view);
                    return;
                }
                prompt = InstructionCompareRules.BuildMemoryPrompt(memoryScan, now);
                break;
            default:
                return;
        }
        var state = CreateDraft(view, account);
        state.Meta.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        composerDrafts[state.Meta.Id] = prompt;
        PostConversation(view, state, paneId);
        MarkAccountsDirty();
    }

    /// <summary>中樞關閉：停掉進行中的掃描</summary>
    private void StopHealth()
    {
        try
        {
            healthCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
