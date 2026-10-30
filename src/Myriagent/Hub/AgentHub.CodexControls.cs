using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>桌面 Codex 對話工具；與 Claude 的主代理、目標控制一樣由擁有該程序的本機視窗操作。</summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        public JObject? CodexGoal;
        public bool CodexControlBusy;
    }

    private void RenderCodexControls(ConversationState c, JObject header)
    {
        if (c.IsReplica || c.Meta.Provider != AgentProvider.Codex) return;
        header["sessionStarted"] = c.Meta.SessionStarted;
        header["codex"] = new JObject
        {
            ["goal"] = c.CodexGoal?.DeepClone(), ["busy"] = c.CodexControlBusy,
            ["serviceTier"] = c.Meta.CodexServiceTier,
            ["selectedSkills"] = new JArray((c.Meta.CodexSkills ?? []).Select(s => new JObject { ["name"] = s.Name, ["path"] = s.Path })),
        };
    }

    private async Task HandleCodexControlAsync(IAgentView view, ConversationState? c, JObject command)
    {
        var reply = new JObject { ["type"] = "codexControl", ["conversationId"] = c?.Meta.Id,
            ["requestId"] = command["requestId"]?.DeepClone() };
        var action = command.Value<string>("action") ?? string.Empty;
        if (c == null || c.IsReplica || c.Meta.Provider != AgentProvider.Codex)
        {
            reply["error"] = "請在這台電腦的 Codex 對話操作";
            Post(view, reply);
            return;
        }
        if (c.CodexControlBusy)
        {
            reply["error"] = "上一個 Codex 操作還在處理，請稍候";
            Post(view, reply);
            return;
        }
        c.CodexControlBusy = true;
        c.LastActivity = DateTime.Now;
        MarkHeaderDirty(c);
        var accountId = c.Meta.AccountId;
        var cwd = GetWorkingDirectory(c);
        try
        {
            if (!CodexControlRules.Actions.Contains(action, StringComparer.Ordinal))
                throw new ArgumentException("不支援的 Codex 操作");
            if (action.StartsWith("goal", StringComparison.Ordinal) && (c.IsDraft || !c.Meta.SessionStarted))
                throw new InvalidOperationException("請先送出第一則訊息，再設定持續目標");
            if (action is "goalSet" or "goalResume" && c.Running)
                throw new InvalidOperationException("請先停止這一輪，再設定或繼續持續目標");
            var session = EnsureSession(c);
            var result = await session.CodexControlAsync(action, command, BuildSessionSettings(c, historyRecap: null));
            if (disposed || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(c, live)
                || !ReferenceEquals(c.Session, session) || c.Meta.AccountId != accountId || GetWorkingDirectory(c) != cwd)
                throw new InvalidOperationException("對話的帳戶或工作資料夾已變更，請重新開啟 Codex 工具");
            if (action == "skillSelect")
                c.Meta.CodexSkills = (result["selectedSkills"] as JArray ?? []).OfType<JObject>()
                    .Select(s => new AgentSkillSelection(s.Value<string>("name")!, s.Value<string>("path")!)).ToList();
            if (action == "speed") c.Meta.CodexServiceTier = result.Value<string>("serviceTier");
            if (action is "goalSet" or "goalPause" or "goalResume" || action == "inspect" && result.Property("goal") != null)
                c.CodexGoal = result["goal"]?.DeepClone() as JObject;
            if (action == "goalClear") c.CodexGoal = null;
            if (!c.IsDraft && action is "skillSelect" or "speed") SaveConversation(c, saveItems: false);
            reply["result"] = result;
        }
        catch (Exception ex) { reply["error"] = ex.Message; }
        finally
        {
            c.CodexControlBusy = false;
            c.LastActivity = DateTime.Now;
            if (!disposed)
            {
                MarkHeaderDirty(c);
                Post(view, reply);
            }
        }
    }
}
