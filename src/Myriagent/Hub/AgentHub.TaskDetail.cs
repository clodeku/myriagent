using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 背景工作的「詳細」（web-protocol 的 taskDetail；2026-10-25）：等背景工作回報那一行的「詳細」以 Header.background.tasks[].key 問，
/// 回這個背景工作在做什麼（BackgroundTaskDetailRules）、指令全文或交代的內容、目前的輸出，結果只回給問的那個視窗（對話框開著時每 3 秒問一次）。
/// 輸出：Claude 的背景指令與 Monitor 問活著的程序（get_task_output），Codex 用工具卡收到的輸出，其他寫看不到的原因。
/// 只認目前回報的背景工作；背景工作屬於活著的程序，沒有程序不開新的
/// </summary>
internal sealed partial class AgentHub
{
    private async Task TaskDetailAsync(IAgentView view, ConversationState? c, string? key, string? requestId)
    {
        if (c == null || c.IsReplica || c.IsDraft || string.IsNullOrEmpty(key))
            return;
        var reply = new JObject
        {
            ["type"] = "taskDetail",
            ["conversationId"] = c.Meta.Id,
            ["requestId"] = requestId,
            ["key"] = key,
        };
        var work = BackgroundOf(c);
        int index = work == null ? -1 : FindTool(work.Tools, key);
        if (work == null || index < 0 || c.Session is not { } session)
        {
            reply["error"] = "這個背景工作已經結束了";
            Post(view, reply);
            return;
        }

        var tool = work.Tools[index];
        var detail = BackgroundTaskDetailRules.Describe(tool);
        reply["tool"] = tool.ToolName;
        reply["title"] = tool.Title;
        reply["explain"] = detail.Explain;
        reply["agentNote"] = detail.AgentNote;
        reply["content"] = detail.Content;
        reply["contentLabel"] = detail.ContentLabel;
        if (!string.IsNullOrEmpty(tool.TaskId))
        {
            AgentTaskOutput output;
            try
            {
                output = await session.GetTaskOutputAsync(tool.TaskId);
            }
            catch (Exception ex)
            {
                output = new AgentTaskOutput(null, 0, false, ex.Message);
            }
            if (disposed)
                return;
            if (output.Error != null)
            {
                reply["outputNote"] = output.Error;
            }
            else
            {
                reply["output"] = output.Output ?? string.Empty;
                reply["totalBytes"] = output.TotalBytes;
                reply["truncated"] = output.Truncated;
            }
        }
        else if (BackgroundCard(c, work, index) is { } card)
        {
            reply["output"] = card.Output ?? string.Empty;
        }
        else
        {
            reply["outputNote"] = BackgroundTaskDetailRules.NoOutputNote(c.Meta.Provider, tool.ToolKind);
        }
        Post(view, reply);
    }

    private static int FindTool(IReadOnlyList<AgentBackgroundTool> tools, string key)
    {
        for (int i = 0; i < tools.Count; i++)
        {
            if (BackgroundTaskDetailRules.Key(tools[i]) == key)
                return i;
        }
        return -1;
    }

    /// <summary>第幾個背景工作的工具卡（會話帶了和工具同順序的卡片 id 才對得到：Codex，Grok 每個都有 id 時）；對不到是 null</summary>
    private static AgentChatItem? BackgroundCard(ConversationState c, AgentBackgroundWorkEvent work, int index)
        => work.ToolIds is { } ids && ids.Count == work.Tools.Count && index < ids.Count
           && (c.ToolItems.TryGetValue(ids[index], out var item) || c.BackgroundToolItems.TryGetValue(ids[index], out item))
            ? item
            : null;
}
