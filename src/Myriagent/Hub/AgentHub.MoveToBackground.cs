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
    }

    /// <summary>前景有能移到背景的工作：執行中的指令（Bash、PowerShell）或子代理</summary>
    private static bool CanMoveToBackground(ConversationState c)
        => c.Running && !c.IsReplica && !c.IsDraft && !c.MovingToBackground && c.Session is { CanMoveToBackground: true }
           && ForegroundRunningTools(c).Any(i => i.ToolKind is "bash" or "agent");

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
