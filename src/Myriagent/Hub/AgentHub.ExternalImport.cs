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
            .Select(c => (c.Meta.Provider, c.Meta.SessionId!))
            .ToList();

        _ = Task.Run(() => ExternalConversationImporter.Scan(targets, existingSessions)).ContinueWith(task =>
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

        if (FindExistingBySession(candidate.Provider, candidate.SessionId) is { } already)
        {
            externalImportCache.Remove(sourceKey);
            Open(view, already);
            Post(view, new JObject { ["type"] = "focusConversation", ["conversationId"] = already.Meta.Id });
            Toast("這個對話已在清單中，已為你開啟", view: view);
            return;
        }

        _ = Task.Run(() =>
        {
            var data = ExternalConversationImporter.Load(candidate);
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
                    Toast("無法讀取這筆外部對話紀錄（檔案可能已被移除或無對話內容）", isError: true, view: view);
                    return;
                }

                if (FindExistingBySession(candidate.Provider, loaded.SessionId) is { } existing)
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
                    ?? accounts.Accounts.FirstOrDefault(a => a.Provider == candidate.Provider);
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

    private ConversationState? FindExistingBySession(AgentProvider provider, string sessionId)
        => conversations.Values.FirstOrDefault(c =>
            !c.IsDraft
            && !c.IsReplica
            && c.Meta.Provider == provider
            && string.Equals(c.Meta.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
}
