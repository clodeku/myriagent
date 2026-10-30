using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

internal sealed partial class CodexAgentSession
{
    // 送出準備與介面控制互斥；控制途中延後重開；串流與停止仍由原本的 gate 管理，不會等這把鎖。
    private readonly SemaphoreSlim entryGate = new(1, 1);

    public async Task SendAsync(AgentTurnInput input, AgentSessionSettings settings)
    {
        await entryGate.WaitAsync();
        try { await SendCoreAsync(input, settings); }
        finally { entryGate.Release(); }
    }

    private bool controlsBusy;

    private async Task<AgentProcess> ControlProcessAsync(AgentSessionSettings settings)
    {
        AgentProcess? current;
        lock (gate)
        {
            ThrowIfDisposed();
            if (restart != null)
                throw new AgentRestartInProgressException();
            current = process;
        }
        if (current != null && (current.HasExited || CodexAppServerRules.NeedsNewProcess(processSignature, processCli, Signature(settings), settings.Cli)))
        {
            if (IsRunning)
                throw new InvalidOperationException("設定正在更換，請等這一輪結束後再操作");
            await CloseAsync();
            current = null;
        }
        return current ?? await StartProcessAsync(settings, CancellationToken.None);
    }

    private async Task<JObject> ControlRequestAsync(AgentProcess current, string method, JObject? args)
    {
        var (result, error) = await RequestAsync(current, method, args, TimeSpan.FromSeconds(60));
        if (error != null)
            throw new InvalidOperationException(error.Contains("Method not found", StringComparison.OrdinalIgnoreCase)
                ? "這個 Codex CLI 版本尚不支援此功能，請先更新 CLI" : error);
        return result ?? new JObject();
    }

    private async Task<JObject> ModelsForControlsAsync(AgentProcess current)
    {
        var all = new JArray();
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await ControlRequestAsync(current, "model/list", new JObject { ["cursor"] = cursor });
            foreach (var row in page["data"] as JArray ?? []) all.Add(row.DeepClone());
            cursor = page.Value<string>("nextCursor");
        } while (cursor != null && seen.Add(cursor));
        return new JObject { ["data"] = all };
    }

    private async Task<JObject> SkillsForControlsAsync(AgentProcess current, AgentSessionSettings settings)
        => await ControlRequestAsync(current, "skills/list", new JObject { ["cwds"] = new JArray(settings.WorkingDirectory), ["forceReload"] = true });

    public async Task<JObject> CodexControlAsync(string action, JObject args, AgentSessionSettings settings)
    {
        if (!CodexControlRules.Actions.Contains(action, StringComparer.Ordinal))
            throw new ArgumentException("不支援的 Codex 操作");
        await entryGate.WaitAsync();
        try
        {
            lock (gate)
            {
                if (restart != null) throw new AgentRestartInProgressException();
                controlsBusy = true;
            }
            var current = await ControlProcessAsync(settings);
            string? thread;
            lock (gate) thread = threadId ?? (settings.SessionStarted ? settings.SessionId : null);
            if (action == "inspect")
            {
                var result = new JObject();
                // 個別查詢失敗仍顯示其他功能；讀取持久目標不用 resume，避免只是開面板就讓目標開始執行。
                try { result["tiers"] = CodexControlRules.SpeedTiers(await ModelsForControlsAsync(current), settings.Model); }
                catch (Exception ex) { result["tiersError"] = ex.Message; }
                try
                {
                    var listed = await SkillsForControlsAsync(current, settings);
                    result["skills"] = CodexControlRules.Skills(listed);
                    result["skillErrors"] = new JArray((listed["data"] as JArray ?? []).OfType<JObject>()
                        .SelectMany(e => (e["errors"] as JArray ?? []).OfType<JObject>()).Select(e => e.Value<string>("message")));
                }
                catch (Exception ex) { result["skillsError"] = ex.Message; }
                if (thread != null)
                {
                    try { result["goal"] = (await ControlRequestAsync(current, "thread/goal/get", new JObject { ["threadId"] = thread }))["goal"]; }
                    catch (Exception ex) { result["goalError"] = ex.Message; }
                }
                return result;
            }
            if (action == "skillSelect")
            {
                var paths = (args["paths"] as JArray ?? []).Values<string>().ToHashSet(StringComparer.Ordinal);
                if (paths.Count > 12) throw new ArgumentException("一次最多指定 12 個技能");
                var available = CodexControlRules.Skills(await SkillsForControlsAsync(current, settings));
                var selected = available.OfType<JObject>().Where(s => paths.Contains(s.Value<string>("path"))).ToList();
                if (selected.Count != paths.Count || selected.Any(s => s.Value<bool?>("enabled") != true))
                    throw new InvalidOperationException("選取的技能已停用或不在目前工作資料夾的清單中，請重新整理");
                return new JObject { ["selectedSkills"] = new JArray(selected) };
            }
            if (action == "speed")
            {
                var tier = args.Value<string>("serviceTier") ?? "default";
                var tiers = CodexControlRules.SpeedTiers(await ModelsForControlsAsync(current), settings.Model);
                if (!CodexControlRules.AllowsTier(tiers, tier))
                    throw new InvalidOperationException("目前模型沒有回報這個速度模式，請重新整理");
                if (threadId != null && tier != null)
                    await ControlRequestAsync(current, "thread/settings/update", new JObject { ["threadId"] = threadId, ["serviceTier"] = tier });
                return new JObject { ["serviceTier"] = tier, ["tiers"] = tiers };
            }
            if (thread == null)
                throw new InvalidOperationException("請先送出第一則訊息，再設定這個對話的持續目標");
            if (action == "goalClear")
            {
                var cleared = await ControlRequestAsync(current, "thread/goal/clear", new JObject { ["threadId"] = thread });
                Raise(new AgentCodexGoalEvent(null));
                return cleared;
            }
            var goalParams = CodexControlRules.GoalParams(thread, action, args);
            if (action != "goalPause")
            {
                if (IsRunning) throw new InvalidOperationException("請先停止這一輪，再設定或繼續持續目標");
                await ValidateTierAsync(current, settings);
                // 先暫停原有目標再載入，讓權限、資料夾與模型全部套用後才開始自動執行。
                var previous = await ControlRequestAsync(current, "thread/goal/get", new JObject { ["threadId"] = thread });
                if (previous["goal"] is JObject)
                    await ControlRequestAsync(current, "thread/goal/set", CodexControlRules.GoalParams(thread, "goalPause", new JObject()));
                if (AgentPermissionModes.Normalize(AgentProvider.Codex, settings.PermissionMode) != AgentPermissionMode.FullAccess && sandboxReady == null)
                    sandboxReady = await EnsureSandboxAsync(settings.WorkingDirectory, CancellationToken.None);
                var policy = CodexAppServerRules.GetPolicy(settings.PermissionMode, sandboxReady ?? true);
                NotifyFallbackOnce(policy);
                lock (gate) permissionMode = AgentPermissionModes.Normalize(AgentProvider.Codex, settings.PermissionMode);
                if (threadId == null)
                {
                    var opened = await OpenThreadAsync(settings, policy, CancellationToken.None);
                    if (opened.LostHistory) throw new InvalidOperationException("原對話無法續接，請先送出訊息確認前文後再設定目標");
                }
                await ApplyControlSettingsAsync(current, thread, settings, policy);
            }
            var changed = await ControlRequestAsync(current, "thread/goal/set", goalParams);
            Raise(new AgentCodexGoalEvent(changed["goal"] as JObject));
            if (action == "goalPause") await InterruptAsync();
            return changed;
        }
        finally { lock (gate) controlsBusy = false; entryGate.Release(); }
    }

    private async Task ApplyControlSettingsAsync(AgentProcess current, string thread, AgentSessionSettings settings, CodexPolicy policy)
    {
        await ValidateTierAsync(current, settings);
        var p = new JObject
        {
            ["threadId"] = thread, ["cwd"] = settings.WorkingDirectory,
            ["approvalPolicy"] = policy.ApprovalPolicy,
            ["sandboxPolicy"] = CodexAppServerRules.BuildSandboxPolicy(policy.Sandbox, new[] { settings.ConversationDirectory }),
        };
        if (NormalizeModel(settings.Model) is { } model) p["model"] = model;
        if (!string.IsNullOrEmpty(settings.Effort) && settings.Effort != "default") p["effort"] = settings.Effort;
        if (settings.CodexServiceTier != null) p["serviceTier"] = settings.CodexServiceTier;
        await ControlRequestAsync(current, "thread/settings/update", p);
    }

    private async Task ApplySelectedSkillsAsync(AgentProcess current, JObject turn, AgentSessionSettings settings)
    {
        await ValidateTierAsync(current, settings);
        if (settings.CodexServiceTier != null)
        {
            turn["serviceTier"] = settings.CodexServiceTier;
        }
        if (settings.CodexSkills is not { Count: > 0 } selected) return;
        var available = CodexControlRules.Skills(await SkillsForControlsAsync(current, settings));
        foreach (var skill in selected)
        {
            if (!available.OfType<JObject>().Any(s => s.Value<string>("path") == skill.Path && s.Value<string>("name") == skill.Name && s.Value<bool?>("enabled") == true))
                throw new InvalidOperationException($"指定的技能「{skill.Name}」已停用或不在目前的技能清單，請重新選擇");
            ((JArray)turn["input"]!).Add(new JObject { ["type"] = "skill", ["name"] = skill.Name, ["path"] = skill.Path });
        }
    }

    private async Task ValidateTierAsync(AgentProcess current, AgentSessionSettings settings)
    {
        if (settings.CodexServiceTier is not (null or "default") && !CodexControlRules.AllowsTier(
            CodexControlRules.SpeedTiers(await ModelsForControlsAsync(current), settings.Model), settings.CodexServiceTier))
            throw new InvalidOperationException("目前模型不支援已選的速度模式，請在 Codex 工具改回標準或重新選擇");
    }

    public async Task<AgentMcpStatusResult> GetMcpStatusAsync(AgentSessionSettings settings)
    {
        await entryGate.WaitAsync();
        try
        {
            lock (gate)
            {
                if (restart != null) throw new AgentRestartInProgressException();
                controlsBusy = true;
            }
            var current = await ControlProcessAsync(settings);
            var servers = new List<AgentMcpServer>();
            string? cursor = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var p = new JObject { ["cursor"] = cursor };
                if (threadId != null) p["threadId"] = threadId;
                var page = await ControlRequestAsync(current, "mcpServerStatus/list", p);
                servers.AddRange((page["data"] as JArray ?? []).OfType<JObject>().Select(CodexControlRules.McpServer));
                cursor = page.Value<string>("nextCursor");
            } while (cursor != null && seen.Add(cursor));
            return new AgentMcpStatusResult(servers, null);
        }
        catch (Exception ex) { return new AgentMcpStatusResult(null, ex.Message); }
        finally { lock (gate) controlsBusy = false; entryGate.Release(); }
    }

    public async Task<AgentMcpActionResult> McpActionAsync(string server, AgentMcpAction action, AgentSessionSettings settings)
    {
        await entryGate.WaitAsync();
        try
        {
            lock (gate)
            {
                if (restart != null) throw new AgentRestartInProgressException();
                controlsBusy = true;
            }
            var current = await ControlProcessAsync(settings);
            if (action == AgentMcpAction.Reconnect)
            {
                await ControlRequestAsync(current, "config/mcpServer/reload", new JObject());
                return new AgentMcpActionResult(true, null);
            }
            var result = await ControlRequestAsync(current, "mcpServer/oauth/login", new JObject { ["name"] = server, ["timeoutSecs"] = 300 });
            return new AgentMcpActionResult(true, null, result.Value<string>("authorizationUrl"));
        }
        catch (Exception ex) { return new AgentMcpActionResult(false, ex.Message); }
        finally { lock (gate) controlsBusy = false; entryGate.Release(); }
    }
}
