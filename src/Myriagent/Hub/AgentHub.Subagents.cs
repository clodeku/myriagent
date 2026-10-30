using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// Claude 子代理（Agent 工具）的即時用量（web-protocol 的 Header.subagents；2026-10-26）：task_progress 每做一步送一次 token 合計、工具次數與最近的工具，
/// 結束時 task_notification 帶最後的數字。中樞以 tool_use_id 對到那張工具卡、以項目 id 放在標頭，網頁畫在卡片上。
/// 只在記憶體：子代理常在背景跑到這一輪結束之後（那時 ToolItems 已清掉），所以 tool_use_id 對到的項目另外記著
/// </summary>
internal sealed partial class AgentHub
{
    /// <summary>標頭最多帶幾個子代理（最近的）</summary>
    internal const int MaxSubagentUsages = 30;

    private sealed partial class ConversationState
    {
        /// <summary>子代理的 tool_use_id → 工具卡的項目 id（一輪結束後還在背景跑的子代理也找得到卡片）</summary>
        public Dictionary<string, string>? SubagentCards;

        /// <summary>項目 id → 子代理最近的用量（依更新的先後，最舊的先丟；Dictionary 刪了再加會補進空出來的位置，順序不可靠）</summary>
        public OrderedDictionary<string, SubagentUsage>? SubagentUsages;
    }

    private void OnSubagentUsage(ConversationState c, SubagentUsage usage)
    {
        c.SubagentCards ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (c.ToolItems.TryGetValue(usage.ToolUseId, out var item) || c.BackgroundToolItems.TryGetValue(usage.ToolUseId, out item))
            c.SubagentCards[usage.ToolUseId] = item.Id;
        if (!c.SubagentCards.TryGetValue(usage.ToolUseId, out var itemId))
            return;
        c.SubagentUsages ??= new OrderedDictionary<string, SubagentUsage>(StringComparer.Ordinal);
        c.SubagentUsages.Remove(itemId);
        c.SubagentUsages[itemId] = usage;
        while (c.SubagentUsages.Count > MaxSubagentUsages)
        {
            var oldest = c.SubagentUsages.GetAt(0).Key;
            c.SubagentUsages.RemoveAt(0);
            foreach (var key in c.SubagentCards.Where(p => p.Value == oldest).Select(p => p.Key).ToList())
                c.SubagentCards.Remove(key);
        }
        MarkHeaderDirty(c);
    }

    /// <summary>標頭的 subagents：{項目 id: {tokens, tools, ms, last, done}}；沒有是 null</summary>
    private static JObject? RenderSubagentUsages(ConversationState c)
    {
        if (c.IsReplica || c.SubagentUsages is not { Count: > 0 } usages)
            return null;
        var result = new JObject();
        foreach (var (itemId, u) in usages)
        {
            result[itemId] = new JObject
            {
                ["tokens"] = u.TotalTokens,
                ["tools"] = u.ToolUses,
                ["ms"] = u.DurationMs,
                ["last"] = u.LastTool,
                ["done"] = u.Done,
            };
        }
        return result;
    }
}
