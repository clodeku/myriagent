using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 帳戶卡的「子代理執行中」（<see cref="SubagentCountRules"/>）：每個對話執行中的子代理數，加總成 AccountView.sessions.subagents。
/// 不依賴背景活動日誌（日誌沒接時照樣算）；只在記憶體
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>還沒結束的子代理工作（Claude 的 local_agent）：task id → <see cref="SubagentCountRules.TaskKey"/></summary>
        public Dictionary<string, string>? SubagentTasks;

        /// <summary>記下 <see cref="SubagentTasks"/> 時的會話：換了會話（重開、關掉、換帳戶）就作廢——子代理跟著舊程序沒了</summary>
        public IAgentSession? SubagentTasksSession;
    }

    /// <summary>會話事件（OnSessionEvent 一開始）：子代理工作的開始與結束、程序結束</summary>
    private void TrackSubagentTasks(ConversationState c, AgentEvent ev)
    {
        if (c.IsReplica)
            return;
        switch (ev)
        {
            case AgentTaskLifecycleEvent { Ended: false } task when SubagentCountRules.IsSubagentTask(task.TaskType):
                if (c.SubagentTasks == null || !ReferenceEquals(c.SubagentTasksSession, c.Session))
                {
                    c.SubagentTasks = new Dictionary<string, string>(StringComparer.Ordinal);
                    c.SubagentTasksSession = c.Session;
                }
                c.SubagentTasks[task.TaskId] = SubagentCountRules.TaskKey(task.TaskId, task.ToolUseId);
                RefreshAccountSessionCounts(c);
                break;
            case AgentTaskLifecycleEvent { Ended: true } task when c.SubagentTasks?.Remove(task.TaskId) == true:
                RefreshAccountSessionCounts(c);
                break;
            case AgentProcessExitedEvent when c.SubagentTasks is { Count: > 0 }:
                c.SubagentTasks.Clear();
                RefreshAccountSessionCounts(c);
                break;
        }
    }

    /// <summary>這個對話執行中的子代理數（副本、草稿是 0）</summary>
    private int LiveSubagents(ConversationState c)
    {
        if (c.IsReplica || c.IsDraft)
            return 0;
        var tools = c.ToolItems.Concat(c.BackgroundToolItems)
            .Where(p => p.Value.Status == AgentItemStatus.Running && BackgroundJournalRules.IsSubagentTool(p.Value.ToolKind))
            .Select(p => p.Key);
        IEnumerable<string> tasks = c.SubagentTasks is { Count: > 0 } live && c.Session != null && ReferenceEquals(c.SubagentTasksSession, c.Session)
            ? live.Values
            : [];
        return SubagentCountRules.Count(tools, tasks);
    }

    /// <summary>agent 種類的工具卡開始或結束（AgentHub.Events.cs）：子代理數可能變了</summary>
    private void OnSubagentToolChanged(ConversationState c, AgentChatItem item)
    {
        if (BackgroundJournalRules.IsSubagentTool(item.ToolKind))
            RefreshAccountSessionCounts(c);
    }

    /// <summary>帳戶卡的數字和上次送出的不同才重送（同 MarkHeaderDirty 的判斷）</summary>
    private void RefreshAccountSessionCounts(ConversationState c)
    {
        if (!renderedSessionCounts.TryGetValue(c.Meta.AccountId, out var shown) || shown != SessionCounts(c.Meta.AccountId))
            MarkAccountsDirty();
    }
}
