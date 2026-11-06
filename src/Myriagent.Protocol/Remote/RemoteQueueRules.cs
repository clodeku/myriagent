using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Remote;

/// <summary>
/// 從遠端排進佇列的代理輸入，輪到它時還能不能送（設計 8.11、14.11 D13）：入列時的閘門只核准了當時的模式與政策，
/// 之後模式被調高（例如本機改成完全自動）、多了預先放行規則、政策不再允許或遠端已暫停，這則就留在佇列、佇列暫停並通知本機使用者（WP4.4）。
/// 所有送出的呼叫點都經這一個檢查；本機的訊息（沒有 <see cref="RemoteOrigin"/>）一律可以送
/// </summary>
internal static class RemoteQueueRules
{
    /// <param name="origin">那則訊息的遠端來源；null＝本機輸入</param>
    /// <param name="currentMode">對話現在的權限模式</param>
    /// <param name="preApproved">現在有沒有預先放行規則（不明＝有）</param>
    /// <param name="policy">現在的本機政策（Hello 回報的同一個，暫停時是 Off）</param>
    public static bool CanDequeue(RemoteOrigin? origin, AgentPermissionMode? currentMode, bool? preApproved, DevicePolicy? policy)
    {
        if (origin == null)
            return true;
        if (origin.Tier is < 0 or > 2 || origin.EffectiveMode is not { } entered || currentMode is not { } now
            || Rank(entered) is not { } enteredRank || Rank(now) is not { } nowRank)
            return false;
        if (nowRank > enteredRank)
            return false;
        var current = RemoteTierRules.AgentInput(now, preApproved, low: false);
        if (current.Tier > origin.Tier || current.FullAccess && !origin.FullAccess)
            return false;
        // 協作人送的（Author 有值）：對話現在是完全自動就不送進代理（入列之後才改成完全自動的也一樣；2026-11-07）
        if (!Sync.CollaboratorRules.AllowsFullAccess(origin.Author is not null, now == AgentPermissionMode.FullAccess || current.FullAccess))
            return false;
        return RemotePolicyRules.Check(policy, origin.Tier, origin.FullAccess) == null;
    }

    /// <summary>
    /// 權限模式的寬鬆程度（越大越少詢問）：規劃 0、每步詢問 1、自動接受編輯 2、自動 3、完全自動 4；沒定義的數值是 null
    /// </summary>
    public static int? Rank(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Plan => 0,
        AgentPermissionMode.Ask => 1,
        AgentPermissionMode.AcceptEdits => 2,
        AgentPermissionMode.Auto => 3,
        AgentPermissionMode.FullAccess => 4,
        _ => null,
    };
}
