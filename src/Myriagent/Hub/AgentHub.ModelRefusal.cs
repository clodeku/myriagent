using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 安全機制把對話換到別的模型（Claude 的 model_refusal_fallback；web-protocol 的 Header.modelSwitch、modelSwitchBack；2026-11-25 使用者要求）：
/// CLI 攔下一則回覆之後整個對話改用接手的模型（例：Fable 5.2 換成 Opus 4.8），畫面以前沒有任何提示。
/// 現在加一則提示、撤回的回覆從對話拿掉，輸入框上方常駐一行直到換回來（換回鈕、自己選模型、程序重開、實際的回覆又是原本的模型）。
/// 只在記憶體：換過去的是活著的那個 CLI 程序，程序重開就照對話設定的模型
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>安全機制換過的模型；null＝沒有</summary>
        public ModelSwitchState? ModelSwitch;

        /// <summary>換回的請求還在進行（連按只送一次）</summary>
        public bool SwitchingModelBack;
    }

    /// <param name="Original">原本的模型（CLI 給的完整名稱）</param>
    /// <param name="Fallback">接手的模型</param>
    /// <param name="Instance">換過去的程序：換了程序就不算數</param>
    private sealed record ModelSwitchState(string? Original, string Fallback, string? Instance);

    private void OnModelRefusal(ConversationState c, AgentModelRefusalEvent refusal)
    {
        AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = refusal.Notice });
        if (refusal.Reverted)
        {
            ForgetModelSwitch(c);
            return;
        }
        // 只有子代理或旁支提問被攔、沒有接手的模型：對話本身沒換，不動已有的
        if (refusal is not { SessionSwitched: true, FallbackModel: { } fallback })
            return;
        // 接手的模型又被攔、再換一個：「換回」仍是最早的那個
        c.ModelSwitch = new ModelSwitchState(RenderModelSwitch(c) != null ? c.ModelSwitch!.Original : refusal.OriginalModel, fallback, c.Session?.ProcessInstance);
        Log.Info($"對話 {c.Meta.Id} 被安全機制換到 {fallback}（原本 {refusal.OriginalModel ?? "不明"}）");
        MarkHeaderDirty(c);
    }

    /// <summary>主對話的回覆實際是哪個模型：不是接手的那個，就表示換回來了</summary>
    private void OnReplyModel(ConversationState c, string model)
    {
        if (c.ModelSwitch is { } current && !ClaudeEventRules.SameModel(model, current.Fallback))
            ForgetModelSwitch(c);
    }

    /// <summary>這個對話現在被安全機制換到別的模型（測試）</summary>
    internal bool HasModelSwitch(string conversationId) => ModelSwitchOf(conversationId) != null;

    /// <summary>（原本的模型，接手的模型）；沒換過是 null（測試）</summary>
    internal (string? Original, string Fallback)? ModelSwitchOf(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) && c.ModelSwitch is { } s ? (s.Original, s.Fallback) : null;

    private void ForgetModelSwitch(ConversationState c)
    {
        if (c.ModelSwitch == null)
            return;
        c.ModelSwitch = null;
        MarkHeaderDirty(c);
    }

    /// <summary>CLI 撤回的回覆與思考（AgentBlocksRetractedEvent）：從對話拿掉，其他電腦與瀏覽器版跟著拿掉</summary>
    private void OnBlocksRetracted(ConversationState c, IReadOnlyList<string> blockIds)
    {
        foreach (var blockId in blockIds)
        {
            if (!c.BlockItems.Remove(blockId, out var item) || item.Kind is not (AgentItemKind.Assistant or AgentItemKind.Thinking) || !c.Items.Contains(item))
                continue;
            RemoveItem(c, item);
        }
    }

    /// <summary>換回對話設定的模型（modelSwitchBack）：活著的程序立即改用</summary>
    private async Task SwitchModelBackAsync(IAgentView view, ConversationState? c)
    {
        if (c?.ModelSwitch is not { } current || c.SwitchingModelBack || c.Session is not { } session)
            return;
        c.SwitchingModelBack = true;
        bool applied;
        try
        {
            applied = await session.SetModelAsync(c.Meta.Model, c.Meta.Effort);
        }
        catch (Exception ex)
        {
            Log.Warn($"換回原本的模型失敗：{ex.Message}");
            applied = false;
        }
        finally
        {
            c.SwitchingModelBack = false;
        }
        if (disposed || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        if (applied && ReferenceEquals(c.ModelSwitch, current))
            ForgetModelSwitch(c);
        if (!applied)
            Toast("沒有換回原本的模型：Claude 沒有回應，或程序已經結束（下一則訊息會照對話設定的模型）", isError: true, view: view);
    }

    /// <summary>標頭的 modelSwitch：{from, to}（顯示用的名稱）；沒換過、程序換了、副本是 null</summary>
    private static JObject? RenderModelSwitch(ConversationState c)
    {
        if (c.IsReplica || c.ModelSwitch is not { } current || current.Instance != c.Session?.ProcessInstance)
            return null;
        return new JObject
        {
            ["from"] = ClaudeEventRules.ModelLabel(current.Original),
            ["to"] = ClaudeEventRules.ModelLabel(current.Fallback),
        };
    }
}
