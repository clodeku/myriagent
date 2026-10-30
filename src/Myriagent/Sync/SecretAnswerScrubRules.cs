using Myriagent.Agents;
using Myriagent.Remote;

namespace Myriagent.Sync;

/// <summary>秘密回答清除跨頁的狀態（<see cref="SecretAnswerScrubRules.ScrubHistory(IReadOnlyList{AgentChatItem}, SecretScrubState)"/>；第一頁傳新的）</summary>
internal sealed class SecretScrubState
{
    /// <summary>前面各頁留下、還沒被認領的候選卡片（清單順序）</summary>
    public List<AgentChatItem> Unclaimed { get; } = new();

    /// <summary>前面出現過含秘密題目的已作答卡片（之後對不上非秘密卡片的「我的回答：」一律不照原文送）</summary>
    public bool SecretSeen { get; set; }
}

/// <summary>
/// 歷史裡秘密回答的清除（WP4.3 計畫 D18；契約:464 的 R3）：WP4.3 之前的中樞以原文組「我的回答：」訊息（非同步提問 <c>AnswerAsMessage</c>），
/// 秘密題目（<c>IsSecret</c>）的答案原文留在 User 項目的文字與 queue.json。發布前（引擎執行緒、<c>MaskItem</c> 之前）以這裡換掉：
/// 從卡片的結構重組顯示文字（<see cref="AnswerMessageRules.Compose"/> 的 DisplayText；卡片上的答案本來就存成「（已隱藏）」），
/// 不從文字反推（多行的答案、題目含「：」、答案含「、」都會讓逐行比對錯位）。對不上的情況一律換成固定的佔位文字（fail-closed）。純函式
/// </summary>
internal static class SecretAnswerScrubRules
{
    /// <summary>認不出是哪一張卡片的答案、而且可能含秘密回答時送的文字</summary>
    public const string AmbiguousPlaceholder = "我的回答：\n（含已隱藏的回答，內容未同步）";

    private const string AnsweredDecision = "answered";

    /// <summary>
    /// 要換掉文字的 User 項目（id → 要送的文字；不在結果裡的照原文送）。依清單順序：<c>AnswerAsMessage</c> 而且已作答的問卷卡片是候選；
    /// 每則以 <see cref="AnswerMessageRules.Prefix"/> 開頭的 User 項目依序判斷（使用者可以先答後面的卡片，所以不照位置認領，合併前審查 P2）：
    /// <list type="number">
    /// <item>逐字等於某張沒被認領、沒有秘密題目的候選重組的文字（舊版對非秘密題目的組法與 Compose 逐字相同）：就是它的答案，照原文。</item>
    /// <item>這個對話到這裡還沒出現過含秘密題目的已作答卡片：照原文（認領最前面的候選，讓之後的候選數正確；之前沒有候選＝使用者自己打的字）。</item>
    /// <item>其餘（可能是秘密卡片的答案原文）：沒被認領的候選裡有含秘密題目的就認領第一張——只剩它一張時換成它重組的 DisplayText，還有別的候選
    /// （認不準是哪一張）換成 <see cref="AmbiguousPlaceholder"/>；沒有含秘密題目的候選（例如使用者自己打了開頭、先認領了秘密卡片）也換成佔位文字。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ScrubHistory(IReadOnlyList<AgentChatItem> items) => ScrubHistory(items, new SecretScrubState());

    /// <summary>
    /// 分頁版（引擎依 Ord 一頁一頁取快照，D12）：<paramref name="state"/> 是前面各頁留下的狀態（還沒被認領的候選、出現過秘密卡片沒有）——
    /// 進來時是上一頁之後的狀態，出去時是這一頁之後的（第一頁傳新的）。依序對每一頁呼叫，結果的聯集與對整份清單呼叫一次
    /// <see cref="ScrubHistory(IReadOnlyList{AgentChatItem})"/> 相同
    /// </summary>
    public static IReadOnlyDictionary<string, string> ScrubHistory(IReadOnlyList<AgentChatItem> items, SecretScrubState state)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(state);
        var unclaimed = state.Unclaimed;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item == null)
                continue;
            if (IsAnsweredCard(item))
            {
                unclaimed.Add(item);
                state.SecretSeen |= HasSecret(item);
                continue;
            }

            if (item.Kind != AgentItemKind.User || item.Text == null || !item.Text.StartsWith(AnswerMessageRules.Prefix, StringComparison.Ordinal))
                continue;

            var text = item.Text;
            var exact = unclaimed.FindIndex(c => !HasSecret(c) && string.Equals(Display(c), text, StringComparison.Ordinal));
            if (exact >= 0)
            {
                unclaimed.RemoveAt(exact);
                continue;
            }

            if (!state.SecretSeen)
            {
                if (unclaimed.Count > 0)
                    unclaimed.RemoveAt(0);
                continue;
            }

            var secret = unclaimed.FindIndex(HasSecret);
            if (secret < 0)
            {
                result[item.Id] = AmbiguousPlaceholder;
                continue;
            }

            var card = unclaimed[secret];
            var ambiguous = unclaimed.Count >= 2;
            unclaimed.RemoveAt(secret);
            result[item.Id] = ambiguous ? AmbiguousPlaceholder : Display(card);
        }

        return result;
    }

    /// <summary>
    /// 佇列裡一則訊息要送的顯示文字：有 <paramref name="displayText"/>（中樞已經遮蔽好的）就用它；沒有時，是問卷的答案
    /// （<paramref name="answerFor"/>）就從卡片重組；也不是卡片、但文字是「我的回答：」開頭的（重開程式後答案與卡片的關聯不存檔，
    /// 認不出是哪一張）換成 <see cref="AmbiguousPlaceholder"/>；其餘是原文
    /// </summary>
    public static string QueuedDisplay(AgentChatItem? answerFor, string? displayText, string text)
    {
        if (displayText != null)
            return displayText;
        if (answerFor != null)
            return Display(answerFor);
        return text != null && text.StartsWith(AnswerMessageRules.Prefix, StringComparison.Ordinal) ? AmbiguousPlaceholder : text ?? string.Empty;
    }

    private static bool IsAnsweredCard(AgentChatItem item)
        => item.Kind == AgentItemKind.Question && item.AnswerAsMessage && string.Equals(item.Decision, AnsweredDecision, StringComparison.Ordinal);

    private static bool HasSecret(AgentChatItem card) => card.Questions?.Any(q => q is { IsSecret: true }) == true;

    /// <summary>卡片重組的顯示文字（秘密題目的答案一律是「（已隱藏）」，存檔裡的 null 清單與 null 答案當成沒有）</summary>
    private static string Display(AgentChatItem card)
    {
        var questions = (card.Questions ?? new List<AgentQuestion>()).Where(q => q != null).ToList();
        var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in card.Answers ?? new Dictionary<string, List<string>>())
        {
            if (key != null && values != null)
                answers[key] = values.Where(v => v != null).ToList();
        }

        return AnswerMessageRules.Compose(questions, answers).DisplayText;
    }
}
