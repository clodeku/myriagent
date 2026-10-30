using System.Globalization;
using Myriagent.Agents;
using Myriagent.Usage;

namespace Myriagent.Hub;

/// <summary>
/// 輸入框下方那一行的 token 數（標頭的 lastTurn、turnLive）：一輪進行中顯示這一輪到目前為止 CLI 即時回報的累計
/// （每次模型呼叫的增量加總，同側欄今日 token 的來源 AgentTokenUsageEvent；網頁數字變了就解碼），還沒收到任何增量時照舊顯示上一輪；
/// 一輪結束後是 CLI 回報的這一輪最終用量。Grok 只在一輪結束時回報，進行中不會變
/// </summary>
internal static class TurnTokenRules
{
    /// <summary>要顯示哪一份：執行中而且這一輪已有即時的累計 → 即時的（Live＝true），其他 → 上一輪</summary>
    public static (AgentUsage? Usage, bool Live) Shown(bool running, AgentUsage? live, AgentUsage? last) =>
        running && live != null && !TokenTallyRules.IsZero(live) ? (live, true) : (last, false);

    /// <summary>
    /// 一輪結束後的「上一輪」：CLI 回報了這一輪的用量就用它；沒回報（停止、失敗）但這一輪即時記到了，用記到的，免得畫面從即時的數字跳回更早一輪；
    /// 都沒有回 null（不改上一輪）
    /// </summary>
    public static AgentUsage? Final(AgentUsage? reported, AgentUsage? live) =>
        reported ?? (live != null && !TokenTallyRules.IsZero(live) ? live : null);

    /// <summary>網頁認得的格式（fxrules.parseTurnTokens；同伺服器的 ConversationViewRules.LastTurnText，以不變文化特性加千分位）；沒有用量是 null</summary>
    public static string? Text(AgentUsage? usage) =>
        usage == null ? null : string.Create(CultureInfo.InvariantCulture, $"輸入 {usage.InputTokens:N0}・輸出 {usage.OutputTokens:N0} tokens");
}
