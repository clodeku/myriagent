using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        public DelegationMcpServer? DelegationMcp;
        public bool LocalDelegationTurn;
    }

    private sealed record DelegatedWork(string ParentId, TaskCompletionSource<JObject> Completion);
    private readonly Dictionary<string, DelegatedWork> delegatedWork = new(StringComparer.Ordinal);

    private bool SupportsAccountDelegation(ConversationState c)
        => !c.IsReplica && c.Meta.ParentConversationId == null
           && c.Meta.Provider is AgentProvider.Claude or AgentProvider.Codex
           && accounts.Find(c.Meta.AccountId) is { SafeMode: false };

    /// <summary>
    /// 對話 @ 過帳戶才掛上 MCP：沒用到委派的對話不多開橋接程序。第一次 @ 時管道名稱從無到有，
    /// 會話在回合之間重開程序（Claude 以 --resume 接回、Codex 重開 app-server），之後同一個中樞期間名稱不變。
    /// </summary>
    private DelegationMcpServer? DelegationMcpFor(ConversationState c)
    {
        if (!SupportsAccountDelegation(c) || c.Meta.DelegatedAccountIds is not { Count: > 0 })
            return null;
        return c.DelegationMcp ??= new DelegationMcpServer((name, args, ct) => CallAccountToolOnUiAsync(c, name, args, ct));
    }

    /// <summary>主對話的這則訊息 @ 了 AI 帳戶（子對話的提及只當文字）：要經 SendCoreAsync 才會掛上 MCP，不插進執行中的一輪</summary>
    private static bool MentionsAccounts(ConversationState c, string text)
        => c.Meta.ParentConversationId == null && DelegationMcpRules.MentionedAccounts(text).Count > 0;

    private string PrepareAccountDelegation(ConversationState c, string text, RemoteOrigin? origin)
    {
        c.LocalDelegationTurn = origin == null && SupportsAccountDelegation(c);
        // 子對話不再往下委派：主 AI 寫進工作說明的提及格式只是文字，不因此讓子工作失敗
        if (c.Meta.ParentConversationId != null)
            return string.Empty;
        var mentioned = DelegationMcpRules.MentionedAccounts(text);
        if (mentioned.Count > 0 && !c.LocalDelegationTurn)
            throw new InvalidOperationException("請在本機 Claude Code 或 Codex 的一般模式對話中 @ AI 帳戶（精簡模式、副本與瀏覽器送出的訊息不支援）");
        var targets = new List<AgentAccount>();
        foreach (var id in mentioned)
        {
            if (id == c.Meta.AccountId)
                throw new InvalidOperationException("請 @ 另一個 AI 帳戶，不能委派給目前對話自己的帳戶");
            targets.Add(accounts.Find(id) ?? throw new InvalidOperationException("@ 的 AI 帳戶已被移除，請重新選取"));
        }
        if (mentioned.Count > 0)
        {
            c.Meta.DelegatedAccountIds = (c.Meta.DelegatedAccountIds ?? []).Concat(mentioned).Distinct(StringComparer.Ordinal).ToList();
            SaveConversation(c, saveItems: false);
        }
        if (!c.LocalDelegationTurn || c.Meta.DelegatedAccountIds is not { Count: > 0 })
            return string.Empty;
        return DelegationMcpRules.DelegationHint(targets.Select(a => (a.Id, a.Label, a.Provider.ToString())).ToList());
    }

    private async Task<JObject> CallAccountToolOnUiAsync(ConversationState parent, string name, JObject args, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = ct.Register(() => completion.TrySetCanceled(ct));
        host.BeginInvoke(() => { _ = RunAsync(); });
        return await completion.Task.ConfigureAwait(false);

        async Task RunAsync()
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(await CallAccountToolAsync(parent, name, args, ct));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex)
            {
                completion.TrySetResult(DelegationMcpRules.ToolResult(new JObject { ["error"] = ex.Message }, error: true));
            }
        }
    }

    private async Task<JObject> CallAccountToolAsync(ConversationState parent, string name, JObject args, CancellationToken ct)
    {
        if (disposed || Abandoned(parent, null) || !parent.Running || parent.StopRequested || !parent.LocalDelegationTurn || !SupportsAccountDelegation(parent))
            throw new InvalidOperationException("主對話已停止，或目前這一輪不允許委派帳戶");
        var allowed = (parent.Meta.DelegatedAccountIds ?? []).Where(id => id != parent.Meta.AccountId)
            .Select(accounts.Find).OfType<AgentAccount>().ToList();
        if (name == "list_accounts")
        {
            return DelegationMcpRules.ToolResult(new JObject
            {
                ["accounts"] = new JArray(allowed.Select(a => new JObject
                {
                    ["id"] = a.Id, ["label"] = a.Label, ["provider"] = a.Provider.ToString(),
                    ["ready"] = accounts.GetState(a.Id).Status == AccountStatus.Ready,
                })),
            });
        }
        if (name != "delegate_task")
            throw new InvalidOperationException("不支援的帳戶工具");
        var target = allowed.FirstOrDefault(a => a.Id == DelegationMcpRules.StringField(args, "accountId"))
                     ?? throw new InvalidOperationException("請先在主對話以 @ 選取要委派的帳戶");
        var prompt = DelegationMcpRules.StringField(args, "prompt")?.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > DelegationMcpRules.MaxTextLength)
            throw new InvalidOperationException("請提供 1～64000 字的工作說明");
        if (parent.Meta.PermissionMode == AgentPermissionMode.Plan)
            throw new InvalidOperationException("主對話仍在規劃模式；請先確認計畫並切換模式，再交辦其他帳戶執行工作");
        if (accounts.GetState(target.Id).Status != AccountStatus.Ready)
            throw new InvalidOperationException($"「{target.Label}」目前尚未就緒，請先確認訂閱帳戶已登入");
        if (IsCliUpdateBlocking(target.Provider))
            throw new InvalidOperationException("目標帳戶的 CLI 正在更新，請稍後再試");
        if (delegatedWork.Values.Count(w => w.ParentId == parent.Meta.Id) >= 3)
            throw new InvalidOperationException("每個主對話最多同時執行 3 個委派工作，請先等現有工作完成");

        var defaults = ProviderDefaults(target.Provider);
        var models = ModelOptions(target.Id, target.Provider);
        var model = ConversationDefaultsRules.ModelFor(defaults, settings.DefaultsFor(target.Id).Model, models);
        var meta = new AgentConversation
        {
            AccountId = target.Id, Provider = target.Provider,
            SessionId = InitialSessionId(target.Provider),
            Model = model, Effort = ConversationDefaultsRules.EffortFor(defaults, model, models),
            PermissionMode = AgentPermissionModes.Normalize(target.Provider, AgentPermissionMode.Ask),
            WorkingDirectory = GetWorkingDirectory(parent),
            ParentConversationId = parent.Meta.Id, SyncPolicy = parent.Meta.SyncPolicy,
            Title = $"@{target.Label}：{AgentHubRules.MakeTitle(prompt, [])}",
        };
        var child = new ConversationState(meta) { ItemsLoaded = true };
        conversations.Add(meta.Id, child);
        var done = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        delegatedWork.Add(meta.Id, new DelegatedWork(parent.Meta.Id, done));
        AddItem(parent, new AgentChatItem { Kind = AgentItemKind.Notice, Text = $"已委派給「{target.Label}」：{meta.Title}。可從對話清單開啟子對話查看進度、回應核准。" });
        SaveConversation(child, saveItems: true);
        MarkListDirty();
        MarkAccountsDirty();
        using var cancellation = ct.Register(() => host.BeginInvoke(() => CancelDelegatedWork(child)));
        _ = SendCoreAsync(child, prompt, []);
        return await done.Task;
    }

    private void OnDelegationTurnCompleted(ConversationState c, AgentTurnOutcome outcome)
    {
        if (delegatedWork.Remove(c.Meta.Id, out var work))
        {
            var (text, truncated) = DelegationMcpRules.TurnAnswer(c.Items.Select(i => (i.Kind == AgentItemKind.User, i.Kind == AgentItemKind.Assistant, i.Text)));
            var result = new JObject
            {
                ["conversationId"] = c.Meta.Id, ["accountId"] = c.Meta.AccountId,
                ["status"] = outcome.ToString(), ["result"] = text,
                ["truncated"] = truncated,
            };
            if (outcome != AgentTurnOutcome.Completed)
                result["error"] = "委派工作失敗或已停止；詳情請查看子對話";
            work.Completion.TrySetResult(DelegationMcpRules.ToolResult(result, outcome != AgentTurnOutcome.Completed));
            if (conversations.TryGetValue(work.ParentId, out var parent))
                AddItem(parent, new AgentChatItem { Kind = AgentItemKind.Notice, Text = $"委派工作{(outcome == AgentTurnOutcome.Completed ? "已完成" : "未完成")}：{c.Meta.Title}" });
        }
        CancelDelegatedChildren(c);
    }

    private void CancelDelegatedWork(ConversationState child)
    {
        OnDelegationTurnCompleted(child, AgentTurnOutcome.Interrupted);
        if (child.Running)
            _ = StopAsync(child);
    }

    private void CancelDelegatedChildren(ConversationState parent)
    {
        foreach (var id in delegatedWork.Where(kv => kv.Value.ParentId == parent.Meta.Id).Select(kv => kv.Key).ToList())
            if (conversations.TryGetValue(id, out var child))
                CancelDelegatedWork(child);
    }

    private void OnDelegationDeleting(ConversationState c)
    {
        OnDelegationTurnCompleted(c, AgentTurnOutcome.Interrupted);
        c.DelegationMcp?.Dispose();
        c.DelegationMcp = null;
    }

    private void DisposeAccountDelegation()
    {
        foreach (var c in conversations.Values.ToList())
            c.DelegationMcp?.Dispose();
        foreach (var work in delegatedWork.Values)
            work.Completion.TrySetCanceled();
        delegatedWork.Clear();
    }
}
