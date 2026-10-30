using System.Text;
using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>對話搜尋的一筆結果：<paramref name="Snippet"/> 是內文裡第一個符合處的片段；只有標題符合時是 null</summary>
internal sealed record ConversationSearchMatch(string? Snippet);

/// <summary>
/// 側欄的對話搜尋（標題與內文；web-protocol 的 <c>searchConversations</c>／<c>conversationSearch</c>）的純函式規則，桌面與伺服器共用。
/// 查詢以空白分成關鍵字，每個關鍵字都要出現在標題或內文（不分大小寫，同網頁 <c>searchrules.js</c> 的標題比對）；
/// 內文只看使用者訊息、代理回覆與計畫確認的計畫內容（工具輸出、思考與提示不算：太雜，搜出來的對話多半不是要找的）
/// </summary>
internal static class ConversationSearchRules
{
    /// <summary>查詢字串超過這個長度先截斷（網頁也照這個截）</summary>
    public const int MaxQueryLength = 200;

    /// <summary>最多幾個關鍵字（多的不看）</summary>
    public const int MaxTerms = 8;

    /// <summary>片段裡符合處之前最多留幾個字（側欄一行約 20 個中文字、片段最多顯示兩行：符合處要落在第一行）</summary>
    public const int SnippetBefore = 12;

    /// <summary>片段從符合處起最多幾個字（含關鍵字本身；比側欄兩行多一些，視窗拉寬時也夠）</summary>
    public const int SnippetAfter = 60;

    /// <summary>
    /// 查詢 → 關鍵字：以空白（含全形空白、換行）分開，不分大小寫去掉重複，保留第一次出現的寫法與順序，最多 <see cref="MaxTerms"/> 個；
    /// 整串先截到 <see cref="MaxQueryLength"/> 字。null 或全是空白回空清單（沒有要搜尋的）
    /// </summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();
        var text = query.Length > MaxQueryLength ? query[..MaxQueryLength] : query;
        var terms = new List<string>();
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (terms.Any(t => string.Equals(t, part, StringComparison.OrdinalIgnoreCase)))
                continue;
            terms.Add(part);
            if (terms.Count == MaxTerms)
                break;
        }
        return terms;
    }

    /// <summary>內文搜得到的項目：使用者訊息、代理回覆、計畫確認（Claude 規劃模式的計畫內容）</summary>
    public static bool IsSearchable(AgentItemKind kind) => kind is AgentItemKind.User or AgentItemKind.Assistant or AgentItemKind.PlanApproval;

    /// <summary>
    /// 對話符不符合：每個關鍵字都出現在標題或任一段內文（OrdinalIgnoreCase）才符合，否則回 null。
    /// 片段取依序第一段含任一關鍵字的內文、從其中最早的符合處截（<see cref="Snippet"/>）；內文都沒有關鍵字（只有標題符合）時片段是 null。
    /// <paramref name="texts"/> 依對話順序（舊到新）；沒有關鍵字時回 null
    /// </summary>
    public static ConversationSearchMatch? Match(string? title, IReadOnlyList<string?> texts, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0)
            return null;
        var remaining = new List<string>(terms.Count);
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(title) || title.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                remaining.Add(term);
        }

        string? snippet = null;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            if (snippet is null)
            {
                int first = -1, firstLength = 0;
                foreach (var term in terms)
                {
                    var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0 && (first < 0 || at < first))
                        (first, firstLength) = (at, term.Length);
                }
                if (first >= 0)
                    snippet = Snippet(text, first, firstLength);
            }
            if (remaining.Count > 0)
                remaining.RemoveAll(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (remaining.Count == 0 && snippet is not null)
                break;
        }
        return remaining.Count == 0 ? new ConversationSearchMatch(snippet) : null;
    }

    /// <summary>
    /// 符合處（<paramref name="index"/> 起 <paramref name="length"/> 字）前後的一行片段：連續的空白與換行併成一個空格，
    /// 前面最多留 <see cref="SnippetBefore"/> 字、從符合處起最多 <see cref="SnippetAfter"/> 字（關鍵字本身比這個長時整個留著），
    /// 有截掉內容的那一端加「…」；不從代理字元對的中間切
    /// </summary>
    public static string Snippet(string text, int index, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        index = Math.Clamp(index, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - index);

        // 只看符合處前後一段原文（併掉空白後才截長度；夠長的空白也只會併成一個空格）
        var beforeStart = Math.Max(0, index - SnippetWindow);
        var before = Collapse(text, beforeStart, index).TrimStart();
        var cutStart = before.Length > SnippetBefore || HasContent(text, 0, beforeStart);
        if (before.Length > SnippetBefore)
        {
            before = before[^SnippetBefore..];
            if (char.IsLowSurrogate(before[0]))
                before = before[1..];
            before = before.TrimStart();
        }

        var limit = Math.Max(SnippetAfter, length);
        var afterEnd = (int)Math.Min(text.Length, (long)index + limit + SnippetWindow);
        var after = Collapse(text, index, afterEnd).TrimEnd();
        var cutEnd = after.Length > limit || HasContent(text, afterEnd, text.Length);
        if (after.Length > limit)
        {
            after = after[..limit];
            if (char.IsHighSurrogate(after[^1]))
                after = after[..^1];
            after = after.TrimEnd();
        }
        return (cutStart ? "…" : string.Empty) + before + after + (cutEnd ? "…" : string.Empty);
    }

    /// <summary>片段前後各看多少原文（併掉空白之前）</summary>
    private const int SnippetWindow = 400;

    /// <summary>[start, end) 的原文，連續的空白（含換行、全形空白）併成一個空格</summary>
    private static string Collapse(string text, int start, int end)
    {
        var sb = new StringBuilder(end - start);
        var space = false;
        for (var k = start; k < end; k++)
        {
            var ch = text[k];
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        if (space)
            sb.Append(' ');
        return sb.ToString();
    }

    /// <summary>[start, end) 有沒有空白以外的字（片段截掉的部分只有空白時不加「…」）</summary>
    private static bool HasContent(string text, int start, int end)
    {
        for (var k = start; k < end; k++)
        {
            if (!char.IsWhiteSpace(text[k]))
                return true;
        }
        return false;
    }
}
