using Myriagent.Agents;
using Myriagent.Remote;

namespace Myriagent.Hub;

/// <summary>
/// Codex、Grok 拒絕核准時附的說明（這一輪結束後當下一則訊息送出）的來源（WP4.4 計畫 D22；契約 D13）：附言是代理輸入，
/// 從佇列取出時要能照 <see cref="RemoteQueueRules.CanDequeue"/> 重查，所以合成一則訊息時帶上最嚴的那個遠端來源
/// </summary>
internal static class DenyFeedbackRules
{
    /// <summary>
    /// 附言的來源：全部是本機的（或沒有附言）回 null；有遠端的回最嚴的——層級最高的那一則（同層級取先出現的，<c>CommandId</c> 是它的）、
    /// FullAccess 取所有遠端的 OR、模式取最不寬鬆的（有任何一則的模式不明就是 null，取出時一律留下）
    /// </summary>
    public static RemoteOrigin? OriginOf(IReadOnlyList<DeniedOperation> denials)
    {
        if (denials == null)
            return null;
        RemoteOrigin? strictest = null;
        var fullAccess = false;
        var modeUnknown = false;
        AgentPermissionMode? lowest = null;
        foreach (var denial in denials)
        {
            if (denial?.RemoteOrigin is not { } origin)
                continue;
            fullAccess |= origin.FullAccess;
            if (strictest == null || origin.Tier > strictest.Tier)
                strictest = origin;
            if (origin.EffectiveMode is not { } mode || RemoteQueueRules.Rank(mode) is not { } rank)
                modeUnknown = true;
            else if (lowest is not { } current || rank < RemoteQueueRules.Rank(current))
                lowest = mode;
        }

        return strictest == null ? null : strictest with { FullAccess = fullAccess, EffectiveMode = modeUnknown ? null : lowest };
    }
}
