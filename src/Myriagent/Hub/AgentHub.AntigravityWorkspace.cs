using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>
/// Antigravity 工作資料夾專案層級自訂項（<c>.agents/</c> 的 Skills／Workflows 與 <c>agents/*.md</c> 自訂代理）的背景快取：
/// 絕不在 UI 執行緒或 <c>RenderHeader</c> 內直接做同步磁碟掃描；開對話、切分頁、換工作資料夾、換帳戶與一輪結束時在背景掃描，
/// 結果有變化才標記對話標頭需要重送
/// </summary>
internal sealed partial class AgentHub
{
    private static readonly TimeSpan AgyWorkspaceRescanInterval = TimeSpan.FromSeconds(10);

    private sealed class AgyWorkspaceEntry
    {
        public AntigravityWorkspaceCustomizations Customizations = AntigravityWorkspaceCustomizations.Empty;
        public DateTime? ScannedAtUtc;
        public bool Scanning;
        public bool PendingRescan;
    }

    private readonly Dictionary<string, AgyWorkspaceEntry> agyWorkspaceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取得目前快取的 Antigravity 工作資料夾自訂項（不碰磁碟；尚未掃描完成時回傳 Empty 並排程背景掃描）</summary>
    private AntigravityWorkspaceCustomizations GetCachedAntigravityWorkspace(ConversationState c, AgentAccount? account)
    {
        if (c.IsReplica || (c.IsDraft && c.PeerDeviceId != null) || c.Meta.Provider != AgentProvider.Antigravity || account is { SafeMode: true })
            return AntigravityWorkspaceCustomizations.Empty;

        var key = AgyWorkspaceKey(c, account, out _, out _);
        if (key == null)
            return AntigravityWorkspaceCustomizations.Empty;

        if (agyWorkspaceCache.TryGetValue(key, out var entry) && entry.ScannedAtUtc != null)
            return entry.Customizations;

        RequestAntigravityWorkspaceScan(c);
        return entry?.Customizations ?? AntigravityWorkspaceCustomizations.Empty;
    }

    /// <summary>排程在背景掃描 Antigravity 對話的工作資料夾與設定目錄</summary>
    private void RequestAntigravityWorkspaceScan(ConversationState? c, bool force = false)
    {
        if (disposed || c == null || c.IsReplica || (c.IsDraft && c.PeerDeviceId != null) || c.Meta.Provider != AgentProvider.Antigravity)
            return;

        var account = accounts.Find(c.Meta.AccountId);
        if (account is { SafeMode: true })
            return;

        var key = AgyWorkspaceKey(c, account, out var workingDir, out var agyConfigDir);
        if (key == null)
            return;

        var now = DateTime.UtcNow;
        if (!agyWorkspaceCache.TryGetValue(key, out var entry))
        {
            entry = new AgyWorkspaceEntry();
            agyWorkspaceCache[key] = entry;
        }

        if (entry.Scanning)
        {
            if (force)
                entry.PendingRescan = true;
            return;
        }

        if (!force && entry.ScannedAtUtc is { } last && now - last < AgyWorkspaceRescanInterval)
        {
            ValidateDraftAgentSelection(c, entry.Customizations);
            return;
        }

        entry.Scanning = true;
        entry.PendingRescan = false;
        _ = RunAntigravityWorkspaceScanAsync(key, workingDir, agyConfigDir);
    }

    private async Task RunAntigravityWorkspaceScanAsync(string key, string workingDir, string? agyConfigDir)
    {
        AntigravityWorkspaceCustomizations scanned;
        try
        {
            scanned = await Task.Run(() => AntigravityWorkspaceScanner.Scan(workingDir, agyConfigDir));
        }
        catch (Exception ex)
        {
            Log.Warn($"掃描 Antigravity 工作資料夾自訂項失敗（{workingDir}）：{ex.Message}");
            scanned = AntigravityWorkspaceCustomizations.Empty;
        }

        host.BeginInvoke(() =>
        {
            if (disposed || !agyWorkspaceCache.TryGetValue(key, out var entry))
                return;

            bool changed = entry.ScannedAtUtc == null || !AntigravityWorkspaceScanner.Same(entry.Customizations, scanned);
            entry.Customizations = scanned;
            entry.ScannedAtUtc = DateTime.UtcNow;
            entry.Scanning = false;

            foreach (var conv in conversations.Values)
            {
                if (conv.IsReplica || conv.Meta.Provider != AgentProvider.Antigravity)
                    continue;
                var acc = accounts.Find(conv.Meta.AccountId);
                if (acc is { SafeMode: true })
                    continue;
                if (!string.Equals(AgyWorkspaceKey(conv, acc, out _, out _), key, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool agentReset = ValidateDraftAgentSelection(conv, scanned);
                if ((changed || agentReset) && conv.ViewIds.Count > 0)
                    MarkHeaderDirty(conv);
            }

            if (entry.PendingRescan)
            {
                entry.PendingRescan = false;
                entry.Scanning = true;
                _ = RunAntigravityWorkspaceScanAsync(key, workingDir, agyConfigDir);
            }
        });
    }

    /// <summary>還沒建立 CLI session 的對話若換了資料夾、原本選的自訂代理在新資料夾已不存在，自動退回預設代理</summary>
    private bool ValidateDraftAgentSelection(ConversationState c, AntigravityWorkspaceCustomizations customizations)
    {
        if (c.Running || c.Meta.SessionStarted || string.IsNullOrWhiteSpace(c.Meta.Agent))
            return false;
        if (customizations.Agents.Any(a => string.Equals(a.Name, c.Meta.Agent, StringComparison.OrdinalIgnoreCase)))
            return false;
        c.Meta.Agent = null;
        if (!c.IsDraft)
            SaveConversation(c, saveItems: false);
        return true;
    }

    /// <summary>設定對話的自訂主代理（<c>setAgent</c> 本機指令）：Antigravity 在首輪建立時使用，Claude 見 <see cref="SetClaudeAgent"/></summary>
    private void SetAgent(IAgentView view, ConversationState? c, string? agent)
    {
        if (c == null || c.IsReplica || (c.IsDraft && c.PeerDeviceId != null))
            return;
        if (c.Meta.Provider == AgentProvider.Claude)
        {
            SetClaudeAgent(view, c, agent);
            return;
        }
        if (c.Meta.Provider != AgentProvider.Antigravity)
            return;
        if (c.Running || c.Meta.SessionStarted)
        {
            Toast("自訂代理只能在送出第一則訊息前選擇；請開新對話再指定代理", view: view);
            return;
        }
        var account = accounts.Find(c.Meta.AccountId);
        if (account is { SafeMode: true })
        {
            Toast("這個帳戶開啟了精簡模式，不載入自訂代理", view: view);
            return;
        }

        var normalized = AntigravityStreamRules.NormalizeAgent(agent);
        if (normalized != null && !AntigravityWorkspaceScanner.IsValidAgentName(normalized))
            return;

        if (string.Equals(c.Meta.Agent, normalized, StringComparison.Ordinal))
            return;
        c.Meta.Agent = normalized;
        if (!c.IsDraft)
            SaveConversation(c, saveItems: false);
        MarkHeaderDirty(c);
    }

    private string? AgyWorkspaceKey(ConversationState c, AgentAccount? account, out string workingDir, out string? agyConfigDir)
    {
        workingDir = GetWorkingDirectory(c);
        agyConfigDir = account != null ? AccountRules.ResolveConfigDirectory(account) : null;
        if (string.IsNullOrWhiteSpace(workingDir))
            return null;
        return $"{agyConfigDir ?? string.Empty}|{workingDir}";
    }
}
