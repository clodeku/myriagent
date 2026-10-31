using Myriagent.Agents;
using Myriagent.Accounts;
using Newtonsoft.Json.Linq;

namespace Myriagent.Usage;

/// <summary>使用 Codex 免費重置的結果：<see cref="Level"/> 是 toast 的等級（success／info／error）</summary>
internal sealed record CodexResetCreditResult(bool Reset, string Text, string Level);

/// <summary>
/// 儀表板帳戶卡的「使用重置」（web-protocol 的 <c>accountResetCredit</c>）：codex app-server 的 <c>account/rateLimitResetCredit/consume</c>
/// 會真的用掉一次免費重置（protocol-notes「Codex 的免費重置次數」）。只有這台的 Codex 帳戶、網頁先確認過才送
/// </summary>
internal static class CodexResetCreditRules
{
    public const string ConsumeMethod = "account/rateLimitResetCredit/consume";

    /// <summary>等 consume 回應的上限（實測不到 1.2 秒）</summary>
    public static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 送出前的檢查；可以送回 null，否則回給使用者看的原因（什麼都沒做）。
    /// 次數不知道（null，例如剛啟動還沒查到）照送，由 Codex 判斷；明確是 0 就不送
    /// </summary>
    public static string? Block(AgentProvider? provider, AccountStatus status, int? resetCredits, bool busy, bool cliPaused, bool installed)
    {
        if (provider is null)
            return "找不到這個帳戶";
        if (provider != AgentProvider.Codex)
            return "只有 Codex 帳戶有免費重置";
        if (busy)
            return "正在使用免費重置，請稍候";
        if (status != AccountStatus.Ready)
            return "這個帳戶還沒登入";
        if (!installed)
            return "這台電腦沒有安裝 Codex CLI";
        if (cliPaused)
            return "Codex CLI 正在更新，請稍後再試";
        if (resetCredits == 0)
            return "沒有可用的免費重置次數";
        return null;
    }

    /// <summary>consume 的參數：每按一次一個新的 idempotencyKey（同一次重送才沿用；app 不重送）</summary>
    public static JObject BuildParams(Guid idempotencyKey) => new() { ["idempotencyKey"] = idempotencyKey.ToString("D") };

    /// <summary>consume 的回應 → 結果（outcome：reset、nothingToReset、noCredit、alreadyRedeemed）</summary>
    public static CodexResetCreditResult Describe(JObject? result, string? error)
    {
        if (error != null)
            return new(false, $"使用免費重置時發生問題：{error}。請看帳戶卡的次數確認有沒有用掉", "error");
        var outcome = result?["outcome"] is JValue { Type: JTokenType.String } value ? (string?)value : null;
        return outcome switch
        {
            "reset" => new(true, "已使用 1 次免費重置，額度已重置", "success"),
            "alreadyRedeemed" => new(true, "這次的免費重置已經完成", "success"),
            "nothingToReset" => new(false, "Codex 回報目前沒有需要重置的額度", "info"),
            "noCredit" => new(false, "沒有可用的免費重置次數", "info"),
            _ => new(false, $"Codex 回了不認得的結果（{outcome ?? "沒有 outcome"}），請看帳戶卡的次數確認有沒有用掉", "error"),
        };
    }
}
