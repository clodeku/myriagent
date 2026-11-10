namespace Myriagent.Sync;

/// <summary>裝置 → 伺服器（Invoke，draft1）：這台某個對話輸入框打到一半的字；空白＝清掉（送出了、刪光了）</summary>
internal sealed record ComposerDraftPublish(string ConversationId, string Text);

/// <summary>
/// 一個對話的草稿（伺服器存的最後一份）。<paramref name="Rev"/> 是伺服器給的遞增號碼（整台伺服器共用一個計數，比大小就知道誰新）；
/// <paramref name="UpdatedAt"/> 是伺服器收到的時間（Unix 毫秒）；<paramref name="From"/> 是打字的那一邊（電腦名稱或「網頁版」），只供顯示。
/// 文字是空字串表示清掉了
/// </summary>
internal sealed record ComposerDraftView(string ConversationId, string Text, long Rev, long UpdatedAt, string? From);

/// <summary>伺服器 → 裝置（Send，draft1）：<paramref name="Full"/> 為 true 時是整份（取代這台記得的），否則是變了的那幾則</summary>
internal sealed record ComposerDraftUpdate(IReadOnlyList<ComposerDraftView> Drafts, bool Full);

/// <summary>
/// 輸入框草稿的同步（docs/sync-protocol.md 14.17，draft1；2026-11-10 使用者要求：在某台電腦或手機打到一半沒送出的字，其他電腦與瀏覽器版也看得到）。
/// 給人看的、可丟：伺服器只存每個對話最後一份（重開機還在），不寫事件、不經對話的同步政策以外的任何判斷。
/// 誰的蓋過誰由網頁決定（<c>Web/draftsyncrules.js</c>）：這一邊沒改過就換成新的，正在打的不蓋掉、改成提示
/// </summary>
internal static class ComposerDraftRules
{
    /// <summary>一則草稿的上限（字元；同瀏覽器送給代理的文字 <c>RemoteCommandShapeRules.MaxAgentTextLength</c>：超過的不同步，輸入框照樣保留）</summary>
    public const int MaxTextChars = 16_000;

    /// <summary>伺服器最多存幾個對話的草稿（超過丟掉最舊的）；一則 ComposerDraftUpdate 最多幾則</summary>
    public const int MaxDrafts = 500;

    /// <summary>來源名稱的上限（字元）</summary>
    public const int MaxFromChars = 64;

    /// <summary>瀏覽器版打的草稿的來源名稱</summary>
    public const string BrowserFrom = "網頁版";

    /// <summary>伺服器多久之後丟掉沒再改過的草稿</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>伺服器在連線證明完成之後等多久才送第一份整份的（同 tok2：ProveResponse 還沒到裝置）</summary>
    public static readonly TimeSpan FirstSendDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 能不能送上伺服器：疑似權杖（<see cref="CredentialLeakRules.HasLeak"/>，同對話內容的 DLP，設計 8.8）的不送、伺服器也不存——
    /// 打進輸入框的密碼或金鑰不離開這台；別處留著的是含權杖之前的那一份。清掉一律可以
    /// </summary>
    public static bool Syncable(string? text) => IsEmpty(text) || !CredentialLeakRules.HasLeak(text);

    /// <summary>空白（含只有空白字元）算清掉</summary>
    public static bool IsEmpty(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>裝置送的、瀏覽器送的：對話 id 是線上格式、文字不是 null 而且沒超過上限；合格回 null</summary>
    public static string? Check(string? conversationId, string? text)
    {
        if (!ConversationIds.IsWireId(conversationId))
            return "conversationId";
        if (text is null)
            return "text";
        if (text.Length > MaxTextChars)
            return "text.length";
        return null;
    }

    public static string? CheckPublish(ComposerDraftPublish? publish)
        => publish is null ? "null" : Check(publish.ConversationId, publish.Text);

    /// <summary>桌面收到的：則數、每則的 id、文字、來源；合格回 null</summary>
    public static string? CheckUpdate(ComposerDraftUpdate? update)
    {
        if (update?.Drafts is null)
            return "drafts";
        if (update.Drafts.Count > MaxDrafts)
            return "drafts.count";
        foreach (var d in update.Drafts)
        {
            if (d is null)
                return "draft";
            if (Check(d.ConversationId, d.Text) is { } problem)
                return problem;
            if (d.Rev < 0 || d.UpdatedAt < 0)
                return "rev";
            if (d.From is { Length: > MaxFromChars })
                return "from";
        }
        return null;
    }

    /// <summary>來源名稱：去掉控制字元、截到上限；空的回 null</summary>
    public static string? FromLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var clean = new string(name.Trim().Where(ch => !char.IsControl(ch)).ToArray());
        if (clean.Length == 0)
            return null;
        return clean.Length > MaxFromChars ? clean[..MaxFromChars] : clean;
    }
}
