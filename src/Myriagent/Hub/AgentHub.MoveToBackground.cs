using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 把執行中的前景指令與子代理移到背景（web-protocol 的 backgroundTasks 與 Header.canBackground；2026-11-11 使用者要求）：等於終端機的 Ctrl+B，
/// 被移走的工具立刻以「在背景執行」結束、這一輪接著做，工作在背景跑完時照背景工作回報（等背景工作的那一行可以看詳細、停止）。
/// 只有這台的對話、會話支援（IAgentSession.CanMoveToBackground：Claude），前景有指令或子代理在跑時才有
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>移到背景的請求還在進行（連按只送一次；這段時間標頭不帶 canBackground）</summary>
        public bool MovingToBackground;

        /// <summary>
        /// CLI 已登記成前景工作的工具（task_started 的 is_backgrounded 是 false；鍵是工具 id）：只有它們移得動。
        /// 指令開始約 3 秒後才登記（實測 2.1.332），之前送 background_tasks 會回成功卻沒有作用；子代理一開始就登記
        /// </summary>
        public readonly HashSet<string> MovableTools = new(StringComparer.Ordinal);
    }

    /// <summary>前景有能移到背景的工作：CLI 已登記的執行中指令（Bash、PowerShell）或子代理</summary>
    private static bool CanMoveToBackground(ConversationState c)
    {
        if (!c.Running || c.IsReplica || c.IsDraft || c.MovingToBackground || c.MovableTools.Count == 0 || c.Session is not { CanMoveToBackground: true })
            return false;
        var running = ForegroundRunningTools(c).Where(i => i.ToolKind is "bash" or "agent").ToHashSet();
        return running.Count > 0 && c.MovableTools.Any(id => c.ToolItems.TryGetValue(id, out var item) && running.Contains(item));
    }

    /// <summary>CLI 登記了一個前景工作：移得動了</summary>
    private void OnForegroundTaskStarted(ConversationState c, string toolUseId)
    {
        if (c.MovableTools.Add(toolUseId))
            MarkHeaderDirty(c);
    }

    private async Task MoveToBackgroundAsync(IAgentView view, ConversationState? c)
    {
        if (c == null || c.IsReplica || c.IsDraft || c.MovingToBackground)
            return;
        if (!CanMoveToBackground(c) || c.Session is not { } session)
        {
            Toast("目前沒有可以移到背景的指令或子代理", view: view);
            return;
        }

        c.MovingToBackground = true;
        MarkHeaderDirty(c);
        string? error;
        try
        {
            error = await session.MoveToBackgroundAsync(null);
        }
        catch (Exception ex)
        {
            Log.Warn($"移到背景失敗：{ex.Message}");
            error = ex.Message;
        }
        finally
        {
            c.MovingToBackground = false;
        }
        if (disposed || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        MarkHeaderDirty(c);
        // 背景活動日誌：使用者把前景的工作移到背景與結果
        Journal("bg.moved", c, new JObject { ["error"] = error });
        if (error != null)
            Toast("沒有移到背景：" + error, isError: true, view: view);
    }
}
