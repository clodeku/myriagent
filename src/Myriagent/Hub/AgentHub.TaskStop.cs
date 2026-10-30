using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 強制停止背景工作（web-protocol 的 taskStop；2026-10-25 使用者要求「背景工作執行中時要有按鈕可以強制停止」）：等背景工作回報那一行的「停止」
/// 帶 Header.background.tasks[].stopId 停一個，不帶是全部。只認目前回報的背景工作（網頁不能指定別的 id）；背景工作屬於活著的程序，沒有程序不開新的。
/// 停了就在對話加一則提示（代理不會因此自動接著處理：Claude 的 stop_task 不讓 CLI 開一輪、Codex 停掉的指令不接續），沒停掉的原因只提示送指令的視窗
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>停止背景工作還在進行（連按只做一次）</summary>
        public bool BackgroundStopping;
    }

    private async Task StopBackgroundTaskAsync(IAgentView view, ConversationState? c, string? stopId)
    {
        if (c == null || c.IsReplica || c.IsDraft || c.BackgroundStopping)
            return;
        var work = BackgroundOf(c);
        if (work == null || c.Session is not { } session)
        {
            Toast("背景工作已經結束了", view: view);
            return;
        }
        AgentBackgroundTool? tool = null;
        if (!string.IsNullOrEmpty(stopId))
        {
            tool = work.Tools.FirstOrDefault(t => t.StopId == stopId);
            if (tool == null)
            {
                Toast("這個背景工作已經結束了", view: view);
                return;
            }
        }

        c.BackgroundStopping = true;
        AgentBackgroundStopResult result;
        try
        {
            result = await session.StopBackgroundWorkAsync(tool?.StopId);
        }
        catch (Exception ex)
        {
            Log.Warn($"停止背景工作失敗：{ex.Message}");
            result = new AgentBackgroundStopResult(0, ex.Message);
        }
        finally
        {
            c.BackgroundStopping = false;
        }
        if (disposed || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        // 背景活動日誌：使用者按了停止（全部或其中一個）與結果
        Journal("bg.stop", c, new JObject
        {
            ["all"] = tool == null,
            ["tool"] = tool != null ? BackgroundJournalRules.Tool(tool) : null,
            ["count"] = work.Count,
            ["stopped"] = result.Stopped,
            ["error"] = BackgroundJournalRules.Clip(result.Error),
        });
        if (result.Stopped > 0)
        {
            var notice = tool != null ? BackgroundWorkRules.StoppedNotice(1, [tool]) : BackgroundWorkRules.StoppedNotice(work.Count, work.Tools);
            AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = notice, NoticeKind = AgentNoticeKind.BackgroundTask });
            SaveConversation(c, saveItems: true);
        }
        if (result.Error != null)
            Toast((result.Stopped > 0 ? "有背景工作沒有停止：" : "沒有停止背景工作：") + result.Error, isError: true, view: view);
    }
}
