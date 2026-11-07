namespace Myriagent.Hub;

/// <summary>
/// 帳戶卡的「子代理執行中」（web-protocol 的 AccountView.sessions.subagents；2026-10-28 使用者要求：帳戶列表除了執行中、背景工作，也要統計執行中的子代理）。
/// 一個對話的子代理有兩個來源：執行中的 agent 種類工具卡（Claude 的 Agent、Codex 的子代理、Antigravity 的 invoke_subagent；同背景活動日誌的
/// <see cref="BackgroundJournalRules.IsSubagentTool"/>），以及 Claude 的 local_agent 工作（在背景跑的子代理：工具卡一開始就結束了，子代理跑到 task_notification）。
/// 兩邊以 tool_use_id 對齊，同一個子代理只算一個
/// </summary>
internal static class SubagentCountRules
{
    /// <summary>帳戶卡子代理徽章的說明最多列幾個對話（AccountView.subagentList；第十一批）</summary>
    public const int MaxListed = 5;

    /// <summary>Claude 的子代理工作種類（system/task_started 的 task_type）</summary>
    public const string ClaudeAgentTaskType = "local_agent";

    public static bool IsSubagentTask(string? taskType) => string.Equals(taskType, ClaudeAgentTaskType, StringComparison.Ordinal);

    /// <summary>工作的鍵：有開始它的工具卡就用 tool_use_id（和工具卡對齊），沒有就用 task id</summary>
    public static string TaskKey(string taskId, string? toolUseId) => string.IsNullOrEmpty(toolUseId) ? "task:" + taskId : toolUseId;

    /// <summary>執行中的子代理數：執行中的 agent 工具卡（tool_use_id）與還沒結束的子代理工作（<see cref="TaskKey"/>）的聯集</summary>
    public static int Count(IEnumerable<string> runningAgentTools, IEnumerable<string> liveTaskKeys)
        => runningAgentTools.Concat(liveTaskKeys).Distinct(StringComparer.Ordinal).Count();
}
