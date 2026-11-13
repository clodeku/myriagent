using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Schedules;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 各電腦的排程（排程頁的 S3，docs/sync-protocol.md 14.12，設計 docs/plans/2026-10-26-schedule-manager.md 第 4 節）：
// 上傳：每次排程頁的內容變了，中樞以 ScheduleSyncRules.ForSync 做成摘要（只留列入管理、疑似與定時訊息，動作參數遮蔽秘密，不帶 XML），
// 存成不會再被改動的物件給同步引擎讀（任何執行緒），並通知它排一次 SchedulesPublish。
// 下載：伺服器送來的其他電腦最後回報的摘要放進排程頁的 devices。
// 遠端指令：其他電腦或手機下的 scheduleAction（立即執行、停止、啟用、停用）與 scheduleAgent（在那台的管理對話送出說明），
// 照一般的遠端指令過閘門（政策要「可以操作」，擁有者 2026-10-26 決定不另開開關）；不再列出、納入管理是這台自己的選擇，遠端不改。
// Myriagent 的定時訊息（timedSave、timedAction，2026-11-13 使用者要求：跨電腦也能設定與編輯）也在這裡：照排程頁本機的同一條路做
internal sealed partial class AgentHub
{
    private JObject? scheduleSyncSnapshot;
    private string? scheduleSyncJson;
    private IReadOnlyList<DeviceSchedulesView> otherSchedules = Array.Empty<DeviceSchedulesView>();

    /// <summary>同步引擎讀的這台摘要（任何執行緒；物件建好之後不再改動）</summary>
    internal JObject? ScheduleSyncSnapshot() => Volatile.Read(ref scheduleSyncSnapshot);

    /// <summary>排程頁的內容（RenderSchedules 的結果）算成摘要，和上次不同就換掉並通知同步（UI 執行緒）</summary>
    private void UpdateScheduleSyncSnapshot(JObject schedules)
    {
        if (scheduleScan == null)
            return;
        var snapshot = ScheduleSyncRules.ForSync(schedules);
        var json = snapshot.ToString(Formatting.None);
        if (json == scheduleSyncJson)
            return;
        scheduleSyncJson = json;
        Volatile.Write(ref scheduleSyncSnapshot, snapshot);
        sync?.OnSchedulesChanged();
    }

    /// <summary>伺服器送來的其他電腦的摘要（協調者在 UI 執行緒呼叫；解除配對時是空的）</summary>
    internal void ApplyOtherSchedules(IReadOnlyList<DeviceSchedulesView> devices)
    {
        otherSchedules = devices;
        PostSchedules(force: true);
    }

    private void RegisterScheduleRemoteHandlers(List<IRemoteCommandHandler> into)
    {
        into.Add(new ScheduleRemoteHandler(this));
        into.Add(new TimedRemoteHandler(this));
    }

    /// <summary><c>timedSave</c>、<c>timedAction</c> 的處理者（其他指令回 false）：同排程頁本機的新增、修改、立即送、暫停、繼續、刪除</summary>
    private sealed class TimedRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            if (cmd is not (RemoteCommandCatalog.TimedSave or RemoteCommandCatalog.TimedAction))
                return false;
            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }
            if (hub.timedMessages is null)
            {
                context.Reject(RemoteResultCodes.Unsupported, "這台的 Myriagent 沒有開定時訊息（沒有排程頁）", RemoteGateSteps.Handler);
                return true;
            }
            string? problem;
            if (cmd == RemoteCommandCatalog.TimedAction)
            {
                problem = hub.TimedAction(command.Value<string>("id"), command.Value<string>("action"));
            }
            else
            {
                // 對話是線上 id：換成這台的；文字沒帶＝修改時保留原文（網頁拿到的摘要不是原文）
                var wire = command.Value<string>("conversation");
                var local = hub.conversations.Keys.FirstOrDefault(k => ConversationIds.ToWire(k) == wire);
                var id = command.Value<string>("id");
                var text = command.Value<string>("text");
                if (text == null && id != null)
                    text = hub.timedMessages?.FirstOrDefault(m => m.Id == id)?.Text;
                (_, problem) = hub.SaveTimed(id, text, command.Value<string>("label"), command.Value<string>("whenKind"), command.Value<string>("whenValue"),
                    local, command.Value<bool?>("newConversation") == true, null);
            }
            if (problem != null)
                context.Reject(problem.StartsWith("找不到", StringComparison.Ordinal) ? RemoteResultCodes.Conflict : RemoteResultCodes.Invalid, problem, RemoteGateSteps.Handler);
            else
                context.Complete();
            return true;
        }
    }

    /// <summary><c>scheduleAction</c>、<c>scheduleAgent</c> 的處理者（其他指令回 false）</summary>
    private sealed class ScheduleRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            if (cmd is not (RemoteCommandCatalog.ScheduleAction or RemoteCommandCatalog.ScheduleAgent))
                return false;
            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }
            if (hub.scheduleReader is null || hub.scheduleStore is null || hub.scheduleScan is null)
            {
                context.Reject(RemoteResultCodes.Unsupported, "這台的 Clode Myriagent 沒有排程頁（或還沒讀過排程）", RemoteGateSteps.Handler);
                return true;
            }
            var path = command.Value<string>("path");
            var task = hub.scheduleScan.Tasks.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (task == null)
            {
                context.Reject(RemoteResultCodes.Conflict, "這台找不到這個排程（可能已經刪除），請等排程頁更新", RemoteGateSteps.Handler);
                return true;
            }
            if (cmd == RemoteCommandCatalog.ScheduleAction)
                hub.RemoteScheduleAction(context, task, command.Value<string>("action") ?? "");
            else
                hub.RemoteScheduleAgent(context, task, command.Value<string>("task") ?? "", command.Value<string>("note"), command.Value<string>("accountId"));
            return true;
        }
    }

    /// <summary>遠端的快速操作：在背景經讀取器做，做完回報結果並重讀</summary>
    private void RemoteScheduleAction(RemoteViewContext context, ScheduledTaskInfo t, string action)
    {
        var reader = scheduleReader!;
        var target = t.Path;
        scheduleBusyPaths.Add(target);
        PostSchedules(force: true);
        _ = Task.Run(() =>
            {
                switch (action)
                {
                    case "run":
                        reader.Run(target);
                        break;
                    case "stop":
                        reader.Stop(target);
                        break;
                    default:
                        reader.SetEnabled(target, action == "enable");
                        break;
                }
            })
            .ContinueWith(op => host.BeginInvoke(() =>
            {
                scheduleBusyPaths.Remove(target);
                if (op.Exception?.GetBaseException() is { } ex)
                {
                    Log.Error($"排程：遠端的 {action} {target} 失敗", ex);
                    context.Reject(RemoteResultCodes.Conflict, $"排程 {t.Name}：{ex.Message}", RemoteGateSteps.Handler);
                }
                else
                {
                    context.Complete();
                }
                RequestScheduleScan();
            }), TaskScheduler.Default);
    }

    /// <summary>遠端的交給代理：在這台的管理對話（沒有就開一個）送出說明（遠端看不到這台的草稿，所以直接送）；執行中排進佇列</summary>
    private void RemoteScheduleAgent(RemoteViewContext context, ScheduledTaskInfo t, string task, string? note, string? accountId)
    {
        if (!ScheduleAgentPromptRules.IsTask(task) || task == "auto")
        {
            context.Reject(RemoteResultCodes.Invalid, "不認得要代理做的事", RemoteGateSteps.Handler);
            return;
        }
        var brief = BriefOf(t, scheduleScan!.Environment);
        var prompt = ScheduleAgentPromptRules.Build(task, brief);
        if (!string.IsNullOrWhiteSpace(note))
            prompt += "\n## 補充（" + (string.IsNullOrWhiteSpace(context.IssuerLabel) ? "其他電腦" : context.IssuerLabel) + "）\n\n" + note.Trim() + "\n";
        var echo = new ScheduleEchoView(this);
        var c = ManagementConversationOf(t.Path);
        if (c is { IsDraft: true })
            c = null;
        if (c == null)
        {
            var account = accounts.Find(accountId) is { } chosen && ScheduleAccountUsable(chosen) ? chosen : PickScheduleAccount(null);
            if (account == null)
            {
                context.Reject(RemoteResultCodes.Conflict, "這台沒有可用的帳戶", RemoteGateSteps.Handler);
                return;
            }
            c = CreateManagementConversation(echo, account, t, brief.WorkFolder);
            scheduleStore!.Save();
        }
        var label = ScheduleAgentPromptRules.TaskLabel(task);
        var display = $"（{(string.IsNullOrWhiteSpace(context.IssuerLabel) ? "其他電腦" : context.IssuerLabel)} 交給代理）{label}：{t.Path}" + (string.IsNullOrWhiteSpace(note) ? "" : "\n" + note.Trim());
        if (c.Running)
        {
            c.Queue.Add(new QueuedMessage { Text = prompt, DisplayText = display });
            PostComposer(c);
        }
        else
        {
            _ = SendAsync(echo, c, prompt, displayText: display);
        }
        context.Complete();
        PostSchedules(force: true);
    }
}
