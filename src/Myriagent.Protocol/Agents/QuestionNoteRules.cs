using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 問卷的補充說明（2026-11-05 使用者要求：單選題只能「選一個選項」或「自己填」二選一，沒辦法選了之後再補一段說明）。
/// 網頁的 <c>answer</c> 另帶選用的 <c>notes</c>（題目 id → [文字]，形狀同 <c>answers</c>）：
/// Claude、Grok 放進作答的 <c>annotations.notes</c>（實測 tools/probes/claude_question_notes_probe.py：CLI 交給模型「"題目"="選項" notes: …」），
/// Codex 與「答案當下一則訊息送出」的卡片沒有備註欄位，以 <see cref="Merge"/> 併成答案多一筆「補充說明：…」；畫面與存檔也看合併後的
/// </summary>
internal static class QuestionNoteRules
{
    /// <summary>合併進答案時的開頭（畫面、存檔、Codex、答案當下一則訊息）</summary>
    public const string DisplayPrefix = "補充說明：";

    /// <summary>一題的補充說明上限（同遠端指令的文字上限之內）</summary>
    public const int MaxLength = 4000;

    /// <summary>
    /// notes JSON → 題目 id → 文字：只收這張卡片的題目（密碼類的不收：補充會留在畫面與存檔），值是字串或字串陣列（多段以換行接起），
    /// 去掉前後空白、空的不收、超過上限截斷
    /// </summary>
    public static Dictionary<string, string> Parse(IReadOnlyList<AgentQuestion>? questions, JObject? notes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (notes == null || questions == null)
            return result;
        foreach (var property in notes.Properties())
        {
            var question = questions.FirstOrDefault(q => q != null && string.Equals(q.Id, property.Name, StringComparison.Ordinal));
            if (question == null || question.IsSecret)
                continue;
            var parts = property.Value switch
            {
                JArray array => array.Where(v => v.Type == JTokenType.String).Select(v => (string?)v),
                JValue { Type: JTokenType.String } value => new[] { (string?)value },
                _ => Enumerable.Empty<string?>(),
            };
            var text = string.Join("\n", parts.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p))).Trim();
            if (text.Length == 0)
                continue;
            result[question.Id] = text.Length > MaxLength ? text[..MaxLength] : text;
        }
        return result;
    }

    /// <summary>答案接上補充說明（只接有答案的題目：補充不能單獨當成回答）；沒有補充的照原樣</summary>
    public static Dictionary<string, IReadOnlyList<string>> Merge(IReadOnlyDictionary<string, IReadOnlyList<string>> answers, IReadOnlyDictionary<string, string>? notes)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (id, values) in answers)
        {
            result[id] = notes != null && notes.TryGetValue(id, out var note) && !string.IsNullOrWhiteSpace(note)
                ? values.Append(DisplayPrefix + note.Trim()).ToList()
                : values;
        }
        return result;
    }
}
