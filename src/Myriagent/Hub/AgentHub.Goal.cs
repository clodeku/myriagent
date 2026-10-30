using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// Claude 的持續目標（/goal；web-protocol 的 Header.goal、goalClear）：目標是程序的 Stop hook，按了停止之後還在，
/// 下一則訊息又會接著追它。標頭顯示進行中的目標（ClaudeAgentSession 一輪結束後讀逐字稿的 goal_status），網頁的晶片可以清除
/// （照一般的訊息送出 <c>/goal clear</c>）。只在記憶體：程序開始、結束時清掉（新的程序沒有之前的目標）
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>進行中的目標（還沒達成）；null＝沒有</summary>
        public string? Goal;
    }

    /// <summary>程序開始或結束：目標跟著程序沒了</summary>
    private void ForgetGoal(ConversationState c)
    {
        if (c.Goal == null)
            return;
        c.Goal = null;
        MarkHeaderDirty(c);
    }

    /// <summary>標頭的目標（只有這台的 Claude 對話、有進行中的目標時才帶）</summary>
    private void RenderGoal(ConversationState c, JObject header)
    {
        if (c.Goal != null && !c.IsReplica && c.Meta.Provider == AgentProvider.Claude)
            header["goal"] = new JObject { ["condition"] = c.Goal };
    }

    /// <summary>清除目標（<c>goalClear</c>）：照一般的訊息送出 /goal clear；這一輪還在執行時先要使用者停止（插進這一輪的斜線指令不會被當成指令）</summary>
    private void ClearGoal(IAgentView view, ConversationState? c)
    {
        if (c == null || c.IsReplica || c.IsDraft || c.Meta.Provider != AgentProvider.Claude || c.Goal == null)
            return;
        if (c.Running)
        {
            Toast("這一輪還在執行：先按停止，再清除目標", view: view);
            return;
        }
        _ = SendAsync(view, c, "/goal clear");
    }
}
