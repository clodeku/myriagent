using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// Claude 對話的主代理與輸出風格（輸入列的「代理」「風格」選單；web-protocol 的 Header.agent／agents／outputStyle／outputStyles、
/// setAgent、setOutputStyle）。清單是各帳戶上次 CLI 在 initialize 回報的；主代理只在啟動時套用（換了下一則訊息重開程序、續接同一個 session），
/// 輸出風格對活著的程序以 apply_flag_settings 立即套用（沒套上的下一次啟動生效）
/// </summary>
internal sealed partial class AgentHub
{
    private void OnClaudeCustomizations(ConversationState c, AgentCustomizationsEvent e)
    {
        var accountId = c.Meta.AccountId;
        bool agentsSame = ClaudeCustomizationRules.SameAgents(settings.ClaudeAgents.GetValueOrDefault(accountId), e.Agents);
        bool stylesSame = ClaudeCustomizationRules.SameStyles(settings.OutputStyles.GetValueOrDefault(accountId), e.OutputStyles);
        if (agentsSame && stylesSame)
            return;
        settings.ClaudeAgents[accountId] = e.Agents.ToList();
        settings.OutputStyles[accountId] = e.OutputStyles.ToList();
        settings.Save();
        foreach (var other in conversations.Values.Where(x => x.Meta.AccountId == accountId && x.ViewIds.Count > 0))
            MarkHeaderDirty(other);
    }

    /// <summary>Claude 的主代理（<c>setAgent</c>）：隨時可以換，下一則訊息起生效（程序重開、續接同一個 session）</summary>
    private void SetClaudeAgent(IAgentView view, ConversationState c, string? agent)
    {
        var account = accounts.Find(c.Meta.AccountId);
        if (account is { SafeMode: true })
        {
            Toast("這個帳戶開啟了精簡模式，不載入自訂代理", view: view);
            return;
        }
        var normalized = ClaudeCustomizationRules.NormalizeAgent(agent);
        if (normalized != null && !ClaudeStreamRules.IsValidAgentName(normalized))
            return;
        if (normalized != null && settings.ClaudeAgents.GetValueOrDefault(c.Meta.AccountId) is { Count: > 0 } known && !known.Any(a => a.Value == normalized))
        {
            Toast($"這個帳戶沒有「{normalized}」這個代理", view: view);
            return;
        }
        if (string.Equals(c.Meta.Agent, normalized, StringComparison.Ordinal))
            return;
        c.Meta.Agent = normalized;
        if (!c.IsDraft)
            SaveConversation(c, saveItems: false);
        MarkHeaderDirty(c);
        if (c.Running)
            Toast("主代理從下一則訊息起生效", view: view);
    }

    /// <summary>Claude 的輸出風格（<c>setOutputStyle</c>；null＝照帳戶的設定）</summary>
    private void SetOutputStyle(IAgentView view, ConversationState? c, string? style)
    {
        if (c == null || c.IsReplica || (c.IsDraft && c.PeerDeviceId != null) || c.Meta.Provider != AgentProvider.Claude)
            return;
        var normalized = ClaudeCustomizationRules.NormalizeStyle(style);
        if (normalized != null && (normalized.Length > 100
            || settings.OutputStyles.GetValueOrDefault(c.Meta.AccountId) is { Count: > 0 } known && !known.Contains(normalized, StringComparer.Ordinal)))
        {
            Toast($"這個帳戶沒有「{normalized}」這個輸出風格", view: view);
            return;
        }
        if (string.Equals(c.Meta.OutputStyle, normalized, StringComparison.Ordinal))
            return;
        c.Meta.OutputStyle = normalized;
        if (!c.IsDraft)
            SaveConversation(c, saveItems: false);
        MarkHeaderDirty(c);
        // 活著的程序立即套用（回到「照設定」要拿掉旗標的設定，下一次啟動才準：留給 NeedsRestart）
        if (normalized != null && c.Session is { } session)
            _ = session.SetOutputStyleAsync(normalized);
    }

    /// <summary>標頭的 Claude 主代理與輸出風格（副本、精簡模式的帳戶不給）</summary>
    private void RenderClaudeCustomizations(ConversationState c, AgentAccount? account, JObject header)
    {
        if (c.IsReplica || c.Meta.Provider != AgentProvider.Claude || account is { SafeMode: true })
            return;
        var accountId = c.Meta.AccountId;
        var agents = settings.ClaudeAgents.GetValueOrDefault(accountId);
        header["agent"] = c.Meta.Agent ?? "default";
        if (agents is { Count: > 0 } || c.Meta.Agent != null)
        {
            var items = new JArray
            {
                new JObject { ["value"] = "default", ["label"] = "預設", ["description"] = "Claude Code 內建的主代理" },
            };
            foreach (var a in agents ?? [])
                items.Add(new JObject { ["value"] = a.Value, ["label"] = a.Value, ["description"] = a.Description });
            header["agents"] = items;
        }
        header["outputStyle"] = c.Meta.OutputStyle;
        header["outputStyles"] = new JArray(settings.OutputStyles.GetValueOrDefault(accountId) ?? []);
    }
}
