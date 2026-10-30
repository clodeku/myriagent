using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Remote;

/// <summary>
/// 問卷答案改以下一則訊息送出（Codex 的非同步提問，<c>AnswerAsMessage</c>）時的文字（設計 14.11 D20）：CLI 收到原文，
/// 畫面、存檔、佇列快照與同步只看到秘密題目（<c>IsSecret</c>）的答案換成「（已隱藏）」的顯示文字。
/// 中樞（<c>AgentHub.AnswerAsync</c>）以這裡的結果送出：CLI 文字給 CLI，顯示文字給項目與佇列（<c>SendCoreAsync</c> 的 displayText、
/// <c>QueuedMessage.DisplayText</c>；契約 R3，WP4.3 計畫步 6 接上）。<see cref="Compose"/> 的 CLI 文字與接上之前的中樞逐字相同
/// </summary>
internal static class AnswerMessageRules
{
    public const string Prefix = "我的回答：\n";

    /// <summary>每題一行「題目：答案、答案」，依題目的順序；沒回答的題目答案是空的。存檔裡的 null 題目略過</summary>
    /// <param name="answers">以原本的題目 id 為鍵（遠端的答案先經 RemoteCardRules.TryMapAnswers 找回原題）</param>
    public static (string CliText, string DisplayText) Compose(IReadOnlyList<AgentQuestion> questions, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(answers);
        var cli = new List<string>();
        var display = new List<string>();
        foreach (var q in questions)
        {
            if (q == null)
                continue;
            var values = q.Id != null && answers.TryGetValue(q.Id, out var v) ? v : Array.Empty<string>();
            cli.Add($"{q.Text}：{string.Join("、", values)}");
            display.Add($"{q.Text}：{(q.IsSecret && values.Count > 0 ? ConversationWire.HiddenAnswer : string.Join("、", values))}");
        }

        return (Prefix + string.Join("\n", cli), Prefix + string.Join("\n", display));
    }
}
