using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 匯入本機外部 CLI（Claude Code、Codex、Antigravity、Grok）歷史對話（桌面專屬：listExternalConversations／importExternalConversation）
internal sealed partial class AgentHub
{
    /// <summary>最近一次掃描外部對話的快取（sourceKey → 候選摘要與驗證過的本機檔案路徑；只在 UI 執行緒存取）</summary>
    private readonly Dictionary<string, ExternalConversationCandidate> externalImportCache = new(StringComparer.Ordinal);

    /// <summary>測試接縫：覆寫外部對話掃描的帳戶資料夾清單（避免測試讀到真實使用者目錄）</summary>
    internal Func<IReadOnlyList<ExternalAccountScanTarget>>? ExternalScanAccountsOverride { get; set; }

    private void ListExternalConversations(IAgentView view, string? requestId)
    {
        if (view is RemoteResponseView)
            return;

        var targets = ExternalScanAccountsOverride?.Invoke()
            ?? accounts.Accounts
                .Select(a => new ExternalAccountScanTarget(a.Id, a.Label, a.Provider, AccountRules.ResolveConfigDirectory(a)))
                .ToList();

        var existingSessions = conversations.Values
            .Where(c => !c.IsDraft && !c.IsReplica && !string.IsNullOrWhiteSpace(c.Meta.SessionId))
            .Select(c => (c.Meta.Provider, c.Meta.SessionAccountId ?? c.Meta.AccountId, c.Meta.SessionId!))
            .ToList();

        var launcher = accounts.ResolveLauncher(AgentProvider.Codex);
        _ = Task.Run(() => ExternalConversationImporter.ScanAsync(targets, existingSessions, launcher)).ContinueWith(task =>
        {
            host.BeginInvoke(() =>
            {
                if (disposed)
                    return;

                var found = task.Status == TaskStatus.RanToCompletion && task.Result != null
                    ? task.Result
                    : new List<ExternalConversationCandidate>();

                if (task.IsFaulted && task.Exception != null)
                    Log.Warn($"掃描外部 CLI 歷史對話失敗：{task.Exception.GetBaseException().Message}");

                externalImportCache.Clear();
                foreach (var item in found)
                    externalImportCache[item.SourceKey] = item;

                if (!views.ContainsKey(view.Id))
                    return;

                Post(view, new JObject
                {
                    ["type"] = "externalConversations",
                    ["requestId"] = requestId,
                    ["items"] = new JArray(found.Select(c => new JObject
                    {
                        ["sourceKey"] = c.SourceKey,
                        ["provider"] = c.Provider.ToString(),
                        ["accountId"] = c.AccountId,
                        ["accountLabel"] = c.AccountLabel,
                        ["sessionId"] = c.SessionId,
                        ["title"] = c.Title,
                        ["workingDirectory"] = c.WorkingDirectory,
                        ["updatedAt"] = ToUnixMs(c.UpdatedAt),
                        ["messageCount"] = c.MessageCount,
                    })),
                });
            });
        }, TaskScheduler.Default);
    }

    private void ImportExternalConversation(IAgentView view, string? sourceKey, string? paneId)
    {
        if (view is RemoteResponseView || string.IsNullOrWhiteSpace(sourceKey))
            return;

        if (!externalImportCache.TryGetValue(sourceKey, out var candidate))
        {
            Toast("找不到這筆外部對話，請重新整理清單後再試", isError: true, view: view);
            return;
        }

        if (FindExistingBySession(candidate.Provider, candidate.AccountId, candidate.SessionId) is { } already)
        {
            externalImportCache.Remove(sourceKey);
            Open(view, already);
            Post(view, new JObject { ["type"] = "focusConversation", ["conversationId"] = already.Meta.Id });
            Toast("這個對話已在清單中，已為你開啟", view: view);
            return;
        }

        var launcher = candidate.CodexHome == null ? null : accounts.ResolveLauncher(AgentProvider.Codex);
        _ = Task.Run(async () =>
        {
            var data = candidate.CodexHome == null ? ExternalConversationImporter.Load(candidate)
                : launcher == null ? null : await CodexThreadHistory.ImportAsync(launcher, candidate);
            string? validDir = null;
            if (data != null && !string.IsNullOrWhiteSpace(data.WorkingDirectory))
            {
                try
                {
                    if (AgentAttachmentRules.IsLocalDrivePath(data.WorkingDirectory) && Directory.Exists(data.WorkingDirectory))
                        validDir = data.WorkingDirectory;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return (Data: data, ValidDir: validDir);
        }).ContinueWith(task =>
        {
            host.BeginInvoke(() =>
            {
                if (disposed)
                    return;

                if (task.Status != TaskStatus.RanToCompletion || task.Result.Data is not { } loaded)
                {
                    Toast(task.Exception?.GetBaseException().Message ?? "無法讀取這筆外部對話紀錄（檔案可能已被移除或無對話內容）", isError: true, view: view);
                    return;
                }

                if (FindExistingBySession(candidate.Provider, candidate.AccountId, loaded.SessionId) is { } existing)
                {
                    externalImportCache.Remove(sourceKey);
                    if (views.ContainsKey(view.Id))
                    {
                        Open(view, existing);
                        Post(view, new JObject { ["type"] = "focusConversation", ["conversationId"] = existing.Meta.Id });
                        Toast("這個對話已在清單中，已為你開啟", view: view);
                    }
                    return;
                }

                var account = accounts.Find(candidate.AccountId)
                    ?? (candidate.Provider == AgentProvider.Codex ? null : accounts.Accounts.FirstOrDefault(a => a.Provider == candidate.Provider));
                if (account == null)
                {
                    Toast("找不到對應的帳戶，請先新增或確認帳戶存在", isError: true, view: view);
                    return;
                }

                var providerDefaults = ProviderDefaults(account.Provider);
                var models = ModelOptions(account.Id, account.Provider);
                var model = ConversationDefaultsRules.ModelFor(providerDefaults, settings.DefaultsFor(account.Id).Model, models);

                var meta = new AgentConversation
                {
                    AccountId = account.Id,
                    Provider = account.Provider,
                    Title = loaded.Title,
                    CreatedAt = loaded.CreatedAt,
                    UpdatedAt = loaded.UpdatedAt,
                    Model = model,
                    Effort = ConversationDefaultsRules.EffortFor(providerDefaults, model, models),
                    PermissionMode = ConversationDefaultsRules.PermissionFor(account.Provider, providerDefaults),
                    SessionId = loaded.SessionId,
                    SessionStarted = true,
                    SessionAccountId = account.Id,
                    WorkingDirectory = task.Result.ValidDir,
                };

                var state = new ConversationState(meta)
                {
                    IsDraft = false,
                    ItemsLoaded = true,
                    LastActivity = DateTime.Now,
                };
                foreach (var item in loaded.Items)
                {
                    item.Seq = ++state.LastSeq;
                    state.Items.Add(item);
                }

                if (views.ContainsKey(view.Id))
                    state.ViewIds.Add(view.Id);

                conversations[meta.Id] = state;
                externalImportCache.Remove(sourceKey);

                SaveConversation(state, saveItems: true);
                if (conversationSink != null && state.Items.Count > 0)
                {
                    if (PrepareOrdsForPublish(meta.Id, hint: null))
                        store.SaveItems(meta.Id, state.Items);
                    foreach (var item in state.Items)
                        HookItemChanged(state, item);
                }
                RequestAntigravityWorkspaceScan(state);
                MarkListDirty();
                MarkAccountsDirty();
                FlushNow();

                if (views.ContainsKey(view.Id))
                {
                    PostConversation(view, state, paneId);
                    OnConversationOpened(state);
                    Post(view, new JObject { ["type"] = "focusConversation", ["conversationId"] = state.Meta.Id });
                    Toast($"已匯入對話「{state.Meta.Title}」，可直接輸入訊息續接", view: view, level: "success");
                }
            });
        }, TaskScheduler.Default);
    }

    private ConversationState? FindExistingBySession(AgentProvider provider, string accountId, string sessionId)
        => conversations.Values.FirstOrDefault(c =>
            !c.IsDraft
            && !c.IsReplica
            && c.Meta.Provider == provider
            && (provider != AgentProvider.Codex || (c.Meta.SessionAccountId ?? c.Meta.AccountId) == accountId)
            && string.Equals(c.Meta.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    private async Task OpenCodexThreadAsync(IAgentView view, ConversationState? source, JObject command)
    {
        if (view is RemoteResponseView) return;
        var reply = new JObject { ["type"] = "codexThreadPage", ["requestId"] = command["requestId"] };
        try
        {
            if (source == null || source.IsReplica || source.IsDraft || !source.ViewIds.Contains(view.Id)
                || source.Meta.Provider != AgentProvider.Codex)
                throw new InvalidOperationException("請在來源電腦的 Codex 對話開啟連結");
            var owner = source.Meta.SessionAccountId ?? source.Meta.AccountId;
            var account = accounts.Find(owner);
            if (account?.Provider != AgentProvider.Codex || source.Meta.AccountId != owner)
                throw new InvalidOperationException("來源帳戶已變更，無法開啟歷史");
            if (!Guid.TryParseExact(command.Value<string>("threadId"), "D", out var thread))
                throw new ArgumentException("Codex 對話連結無效");
            var turn = command.Value<string>("turnId");
            var cursor = command.Value<string>("cursor");
            if (turn?.Length > 200 || cursor?.Length > 8192) throw new ArgumentException("Codex 分頁參數無效");
            if (turn == null && cursor == null && FindExistingBySession(AgentProvider.Codex, owner, thread.ToString()) is { } existing)
            {
                Open(view, existing);
                Post(view, new JObject { ["type"] = "focusConversation", ["conversationId"] = existing.Meta.Id });
                reply["opened"] = true;
            }
            else
            {
                var launcher = accounts.ResolveLauncher(AgentProvider.Codex) ?? throw new InvalidOperationException("請先安裝 Codex CLI");
                var directory = AccountRules.ResolveConfigDirectory(account);
                var page = await Task.Run(() => CodexThreadHistory.PreviewAsync(launcher, directory, thread.ToString(), turn, cursor));
                if (!ReferenceEquals(accounts.Find(owner), account) || !conversations.TryGetValue(source.Meta.Id, out var current)
                    || current != source || !source.ViewIds.Contains(view.Id) || source.IsReplica || source.Meta.AccountId != owner)
                    throw new InvalidOperationException("來源對話或帳戶已變更");
                reply["page"] = page;
            }
        }
        catch (Exception ex) { reply["error"] = ex.Message; }
        if (!disposed && views.ContainsKey(view.Id)) Post(view, reply);
    }
}
