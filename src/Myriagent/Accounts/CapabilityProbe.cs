using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>帳戶可用的模型與「/」指令</summary>
internal sealed record AccountCapabilities(IReadOnlyList<AgentModelOption> Models, IReadOnlyList<AgentSlashCommand> Commands);

/// <summary>
/// 不送任何訊息、不消耗額度地探出帳戶的模型清單與「/」指令（新帳戶第一次用，模型選單就正確）：
/// Claude 起 claude -p（stream-json、精簡模式）只送 control_request initialize（實測約 1 秒）與 get_settings（能不能用 ultracode）；
/// Codex 用 app-server 的 model/list。
/// </summary>
internal static class CapabilityProbe
{
    /// <summary>
    /// 探出 Claude 帳戶的模型與「/」指令。短命的 claude 一律先過 AccountRules.CanRunShortLivedClaude（設計 6.5 的統一守門）：
    /// 權杖已過期或 10 分鐘內到期、空權杖、沒有 expiresAt、憑證檔不在而權杖可能存在 Windows 認證管理員（看不到期限）時不跑
    /// （回 null，下次確認登入狀態時再探）——短命的 claude 遇到快到期的權杖會開始換發卻不等結果，可能把 refresh token 用掉。
    /// canStart：起程序前的最後確認（帳戶管理員傳「Claude CLI 沒有在更新」；回 false 就不跑）
    /// </summary>
    public static async Task<AccountCapabilities?> ProbeClaudeAsync(AgentLauncher launcher, AgentAccount account, CancellationToken ct, Func<bool>? canStart = null)
    {
        var credentials = ReadCredentials(AccountRules.GetClaudeCredentialFile(account));
        // 憑證檔不在才需要看 CLI 設定（權杖是不是改存在認證管理員）
        var globalConfig = credentials == null ? AccountManager.ReadClaudeGlobalConfig(account) : ClaudeGlobalConfigInfo.Missing;
        if (!AccountRules.CanRunShortLivedClaude(credentials, DateTimeOffset.UtcNow, globalConfig))
        {
            Log.Info($"「{account.Label}」的權杖快到期（或無法判斷期限），這次不探查模型清單");
            return null;
        }
        if (canStart != null && !canStart())
        {
            Log.Info($"Claude CLI 正在更新，這次不探查「{account.Label}」的模型清單");
            return null;
        }
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--permission-prompt-tool", "stdio", "--safe-mode", "--setting-sources", "project,local",
            "--no-session-persistence",
        };
        using var process = AgentProcess.Start(launcher, args, AppPaths.CacheDirectory, env => AccountRules.ApplyCommandEnvironment(account, env));
        var completion = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new ClaudeStreamParser();
        // 預設強度的探查（set_model、get_settings 一來一回；請求 id 是 eff-…）
        var pending = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<(bool Success, JObject? Response)>>(StringComparer.Ordinal);
        process.LineReceived += (_, line) =>
        {
            var result = parser.Parse(line);
            if (result.ControlResponse is { } any && any.RequestId is { } rid && pending.TryRemove(rid, out var waiter))
                waiter.TrySetResult((any.Success, any.Response));
            else if (result.ControlResponse is { RequestId: "init-probe" } response)
                completion.TrySetResult(response.Success ? response.Response : null);
            // get_settings：帳戶能不能用 ultracode（舊版不認得時回 error，當成不能用）
            else if (result.ControlResponse is { RequestId: "caps-probe" } settings)
                settingsCompletion.TrySetResult(settings.Success && ClaudeStreamRules.ParseUltracodeAvailable(settings.Response));
        };
        process.Exited += (_, _) =>
        {
            completion.TrySetResult(null);
            settingsCompletion.TrySetResult(false);
            foreach (var w in pending.Values)
                w.TrySetResult((false, null));
        };
        process.BeginReading();
        await process.WriteLineAsync(ClaudeStreamRules.BuildInitializeRequest("init-probe"));
        await process.WriteLineAsync(ClaudeStreamRules.BuildGetSettingsRequest("caps-probe"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        JObject? init;
        try
        {
            init = await completion.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            init = null;
        }
        var ultracodeAvailable = false;
        if (init != null)
        {
            try
            {
                ultracodeAvailable = await settingsCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // initialize 已回應、get_settings 沒有：模型清單照樣用，只是不列 ultracode
            }
        }
        var parsed = init == null ? new List<AgentModelOption>() : ClaudeStreamRules.ParseModels(init);
        if (parsed.Count > 0)
            parsed = await ProbeDefaultEffortsAsync(process, parsed, pending, cts.Token);
        await process.CloseAsync(TimeSpan.FromSeconds(3));
        if (init == null)
            return null;
        var models = ClaudeStreamRules.WithUltracode(parsed, ultracodeAvailable);
        return models.Count == 0 ? null : new AccountCapabilities(models, ClaudeStreamRules.ParseCommands(init));
    }

    /// <summary>
    /// 每個有推理強度的模型不指定強度時實際用的強度（2026-11-12 使用者要求：選「預設」時也顯示是哪個強度；實測 tools/probes/default_effort_probe.py：
    /// initialize 的模型清單沒有這個資訊、各模型不同，例如 Fable 5.2 是 medium、Opus 4.7 是 xhigh）：依序 set_model 再 get_settings 讀 applied.effort。
    /// 不送訊息、不用額度；一個模型等不到就停下，其餘照舊沒有預設強度
    /// </summary>
    private static async Task<List<AgentModelOption>> ProbeDefaultEffortsAsync(AgentProcess process, List<AgentModelOption> models,
        System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<(bool Success, JObject? Response)>> pending, CancellationToken ct)
    {
        var result = new List<AgentModelOption>(models.Count);
        var seq = 0;
        var stopped = false;
        async Task<(bool Success, JObject? Response)> Ask(Func<string, string> build)
        {
            var id = "eff-" + (++seq);
            var waiter = new TaskCompletionSource<(bool, JObject?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = waiter;
            await process.WriteLineAsync(build(id));
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        foreach (var m in models)
        {
            if (stopped || m.Efforts.Count == 0)
            {
                result.Add(m);
                continue;
            }
            try
            {
                var model = m.Value.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : m.Value;
                var set = await Ask(id => ClaudeStreamRules.BuildSetModelRequest(id, model));
                var settings = set.Success ? await Ask(ClaudeStreamRules.BuildGetSettingsRequest) : default;
                var effort = settings.Success ? ClaudeStreamRules.ParseAppliedEffort(settings.Response) : null;
                result.Add(effort != null ? m with { DefaultEffort = effort } : m);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                stopped = true;
                result.Add(m);
            }
        }
        return result;
    }

    /// <summary>讀 Claude 憑證檔（ReadWrite｜Delete 共用模式，不擋 CLI 寫入；只用來判斷權杖期限）；讀不到回 "{"（讀不懂＝不跑）、不存在回 null</summary>
    private static string? ReadCredentials(string file)
    {
        try
        {
            return ClaudeUsageClient.ReadCredentials(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "{";
        }
    }

    public static async Task<IReadOnlyList<AgentModelOption>?> ProbeCodexModelsAsync(CodexAppServerClient client)
    {
        var (result, error) = await client.RequestAsync("model/list", new JObject(), TimeSpan.FromSeconds(20));
        if (error != null)
            return null;
        var models = CodexAppServerRules.ParseModels(result);
        return models.Count == 0 ? null : models;
    }
}
