using Myriagent.Agents;
using Myriagent.Remote;

namespace Myriagent.Hub;

/// <summary>
/// 旁支提問（/btw，2026-10-24 使用者要求）：送出「/btw 問題」時中樞接住、不送給代理，以 Claude 的 side_question 用目前對話的上下文問一個問題——
/// 不打斷這一輪（執行中也能問）、問答不寫進 CLI 的逐字稿也不進之後的上下文（實測，docs/protocol-notes.md「旁支提問」）。
/// 問題與答案畫成一則提示（noticeKind sideQuestion），照一般項目存檔與同步；瀏覽器版與其他電腦送來的 /btw 走同一條（都經 SendAsync）
/// </summary>
internal sealed partial class AgentHub
{
    /// <summary>
    /// 送出的是 /btw：處理完回 true（問了、或以 sendRejected 拒絕）；不是 /btw 回 false，照一般送出。
    /// 只有 Claude 支援；草稿（還沒有對話）、問題是空的、太長時拒絕。輸入區的附件不動（旁支提問只帶文字）
    /// </summary>
    private bool TryHandleSideQuestion(IAgentView view, ConversationState c, string text, RemoteOrigin? remoteOrigin)
    {
        if (AgentHubRules.SideQuestionOf(text) is not { } question)
            return false;
        if (!AgentHubRules.SupportsSideQuestion(c.Meta.Provider))
        {
            RejectSend(view, c, text, "只有 Claude 的對話支援 /btw 旁支提問");
            return true;
        }
        if (question.Length == 0)
        {
            RejectSend(view, c, text, "用法：/btw 你的問題（不打斷這一輪，問答不進對話的上下文）");
            return true;
        }
        if (c.IsDraft)
        {
            RejectSend(view, c, text, "先送出第一則訊息，之後才能用 /btw 旁支提問");
            return true;
        }
        if (question.Length > ClaudeMcpRules.MaxSideQuestionLength)
        {
            RejectSend(view, c, text, $"旁支提問最多 {ClaudeMcpRules.MaxSideQuestionLength} 字，比較長的請直接送出");
            return true;
        }
        EnsureItems(c, notifyUnreadable: false);
        if (!c.ItemsLoaded)
        {
            RejectSend(view, c, text, "對話紀錄暫時讀不到，請稍後再試");
            return true;
        }

        var item = new AgentChatItem
        {
            Kind = AgentItemKind.Notice,
            NoticeKind = AgentNoticeKind.SideQuestion,
            Status = AgentItemStatus.Running,
            Text = SideQuestionRules.Text(question, null, null),
        };
        AddItem(c, item);
        // 程序閒置太久會被回收：問的時候算一次動靜（問完之前不會被關掉）
        c.LastActivity = DateTime.Now;
        FlushNow();
        SaveConversation(c, saveItems: true);
        RemoteInputAccepted(remoteOrigin);
        _ = AskSideQuestionAsync(c, item, question);
        return true;
    }

    private async Task AskSideQuestionAsync(ConversationState c, AgentChatItem item, string question)
    {
        AgentSideAnswer answer;
        try
        {
            var settings = BuildSessionSettings(c, historyRecap: null);
            answer = await EnsureSession(c).AskSideQuestionAsync(question, settings);
        }
        catch (Exception ex)
        {
            answer = new AgentSideAnswer(null, ex.Message);
        }

        // 回到 UI 執行緒：對話已刪除或換掉、紀錄被卸下又重新載入（那則已經不是這個物件，載入時已改寫成沒有回答）就不寫
        if (disposed || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c) || !c.ItemsLoaded
            || !c.Items.Any(i => ReferenceEquals(i, item)))
            return;
        item.Text = SideQuestionRules.Text(question, answer.Text, answer.Error);
        item.Status = answer.Text != null ? AgentItemStatus.Done : AgentItemStatus.Failed;
        c.LastActivity = DateTime.Now;
        MarkItemDirty(c, item);
        SaveConversation(c, saveItems: true);
    }
}
