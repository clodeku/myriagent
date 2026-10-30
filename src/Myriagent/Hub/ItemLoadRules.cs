using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 從 items.json 載入項目時的收尾（中樞的 EnsureItems，與 WP4.3 計畫 D10 引擎唯讀讀未載入的對話共用同一份，
/// 兩邊算出的來源摘要才會相同、中樞之後載入時不必重送）。純函式
/// </summary>
internal static class ItemLoadRules
{
    /// <summary>
    /// 上次沒收尾的項目（app 當掉、關閉）：執行中的工具視為中斷（Cancelled）、其他執行中的項目視為完成（Done），
    /// 等待中的問卷／核准已無法回應（Cancelled；以訊息作答的問卷例外，之後照樣可以回答）。回傳狀態有沒有改
    /// </summary>
    public static bool FinalizeOnLoad(AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var before = item.Status;
        // 旁支提問（/btw）的答案還沒回來 app 就結束了：不會再有答案，改寫成沒有回答（不然一直顯示「思考中…」）
        if (item is { Kind: AgentItemKind.Notice, NoticeKind: AgentNoticeKind.SideQuestion } && item.Text.EndsWith(SideQuestionRules.PendingLine, StringComparison.Ordinal))
        {
            item.Text = item.Text[..^SideQuestionRules.PendingLine.Length] + SideQuestionRules.UnansweredLine;
            item.Status = AgentItemStatus.Failed;
            return true;
        }
        if (item.Status == AgentItemStatus.Running)
            item.Status = item.Kind == AgentItemKind.Tool ? AgentItemStatus.Cancelled : AgentItemStatus.Done;
        else if (item.Status == AgentItemStatus.Waiting && !item.AnswerAsMessage)
            item.Status = AgentItemStatus.Cancelled;
        return item.Status != before;
    }
}
