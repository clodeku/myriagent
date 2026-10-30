using System.Diagnostics.CodeAnalysis;
using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Remote;

/// <summary>
/// 遠端回應卡片的檢查（設計 14.11 D36；docs/sync-protocol.md 14.5；閘門 5h）：approve、approvePlan、answer 一定要帶卡片的 rd1 摘要，
/// 電腦以目前的卡片重算（含只在這台記憶體裡的 RequestKey），不同就回 <c>stale</c>——本機的 RejectStaleCard 在值是 null 時放行，遠端不放行。
/// 指令要先通過 <see cref="RemoteCommandShapeRules.Check"/>
/// </summary>
internal static class RemoteCardRules
{
    /// <summary>這個指令要比對卡片（帶 requestDigest 的）</summary>
    public static bool IsCardCommand(string? cmd) => RemoteCommandCatalog.Find(cmd)?.IsCard == true;

    /// <summary>卡片指令對應的項目種類</summary>
    public static AgentItemKind? CardKind(string? cmd) => cmd switch
    {
        "approve" => AgentItemKind.Approval,
        "approvePlan" => AgentItemKind.PlanApproval,
        "answer" => AgentItemKind.Question,
        _ => null,
    };

    /// <summary>
    /// null＝通過（不是卡片指令的一律通過）。卡片不存在、種類不對、不在等待、RequestKey 是 null、摘要不符回 <see cref="RemoteResultCodes.Stale"/>；
    /// 卡片不讓「本對話都允許」卻選了 allowSession（D5；本機的 ApproveAsync 不檢查）、答案對不回題目回 <see cref="RemoteResultCodes.Invalid"/>
    /// </summary>
    /// <param name="card">對話裡 <c>itemId</c> 那一筆（找不到是 null）</param>
    public static string? Check(string cmd, JObject command, AgentChatItem? card)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (CardKind(cmd) is not { } kind)
            return null;
        if (card == null || card.Kind != kind || card.Status != AgentItemStatus.Waiting || card.RequestKey == null
            || !RequestDigestRules.Matches(card, command.Value<string>("requestDigest")))
            return RemoteResultCodes.Stale;
        if (cmd == "approve" && command.Value<string>("choice") == "allowSession" && !card.CanAllowForSession)
            return RemoteResultCodes.Invalid;
        if (cmd == "answer" && !TryMapAnswers(card, command["answers"] as JObject, out _))
            return RemoteResultCodes.Invalid;
        return null;
    }

    /// <summary>
    /// 遠端的答案 → 中樞的答案（以原本的題目 id 為鍵）。線上的題目 id 經過權杖 DLP（<see cref="CredentialLeakRules.MaskItem"/>：Claude 的題目 id
    /// 就是題目文字），所以每個鍵以 <c>Mask(原本的 id)</c> 比對找回原題。不認得的鍵、兩題遮蔽後相同（分不出是哪一題）、同一題出現兩次、
    /// 沒有任何非空白的答案都回 false。空白的值略過（同本機的 AnswerAsync）
    /// </summary>
    public static bool TryMapAnswers(AgentChatItem card, JObject? answers, [NotNullWhen(true)] out Dictionary<string, IReadOnlyList<string>>? mapped)
    {
        mapped = null;
        if (answers == null)
            return false;
        var byMasked = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var question in card.Questions ?? [])
        {
            if (question?.Id == null)
                continue;
            var masked = CredentialLeakRules.Mask(question.Id);
            // 遮蔽後撞在一起的題目：值設成 null，之後拿它來回答的一律不收
            byMasked[masked] = byMasked.ContainsKey(masked) ? null : question.Id;
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in answers.Properties())
        {
            if (!byMasked.TryGetValue(property.Name, out var original) || original == null || result.ContainsKey(original))
                return false;
            if (property.Value is not JArray array)
                return false;
            var values = new List<string>();
            foreach (var value in array)
            {
                if (!RemoteCommandShapeRules.IsString(value, out var text))
                    return false;
                if (!string.IsNullOrWhiteSpace(text))
                    values.Add(text);
            }

            if (values.Count > 0)
                result[original] = values;
        }

        if (result.Count == 0)
            return false;
        mapped = result;
        return true;
    }
}
