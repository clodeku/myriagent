using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>
/// 一則語音通知的兩個版本：<see cref="Full"/> 帶簡要說明（完成了什麼、問什麼，取自對話內容），<see cref="Head"/> 只有「對話標題＋發生什麼事」
/// （標題是對話清單就看得到的中繼資料）。這台的喇叭念 Full；送到伺服器（給網頁版念）時，對話是完整同步才送 Full，只同步清單時送 Head。
/// <see cref="Silent"/>＝這件事不念（例如背景工作還在跑時的「完成」）
/// </summary>
internal sealed record VoiceLine(string Full, string Head)
{
    public static VoiceLine Silent { get; } = new(string.Empty, string.Empty);

    public bool IsSilent => Full.Length == 0;
}

/// <summary>
/// 語音通知（桌面以 Windows 內建語音念，設定 <c>voiceNotifications</c>，聲音的調整見 Speech/SpeechAudioRules；網頁版以瀏覽器的語音念，web-protocol 的 speak）：對話完成、失敗、代理提問、要求核准、計畫等確認時，
/// 用一句話說明是哪個對話、發生什麼事，再接一段簡要說明（完成了什麼、問什麼、要核准什麼）。
/// 句子沒有字數上限（使用者 2026-10-24：原本整句 50 字、標題 12 字，改成沒有上限）：標題整個念、說明照取的那一句或那一行整個念；
/// 只有送到伺服器時照同步協定的上限截短（<see cref="ForWire"/>）。
/// 不看視窗在不在前景（並排窗格開著時，對話「看得到」也常沒在看）；使用者自己按停止的那一輪不念。
/// 念的是純文字：Markdown 符號、網址、程式碼、括號裡的補充念出來只是雜音，先拿掉
/// </summary>
internal static class VoiceNotifyRules
{
    /// <summary>佇列裡最多等幾句：同時完成很多個對話時，後面的不念（系統通知與未讀照常）</summary>
    public const int MaxPending = 3;

    /// <summary>同一句話在這段時間內不重念（同一件事從兩條路徑通知時）</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    /// <summary>打開設定時念的試聽句</summary>
    public const string EnabledSample = "語音通知已開啟";

    /// <summary>調了語速或音調時念的試聽句（一則典型的完成通知；網頁 voicerules.js 的 TUNING_SAMPLE 同句）</summary>
    public const string TuningSample = "「整理報表」已完成：月報已匯出成 Excel";

    /// <summary>偏好的聲音（名稱含這個字的 OneCore 或桌面聲音；使用者 2026-10-23 試聽後選定雅婷），沒有才照 <see cref="VoiceLanguages"/></summary>
    public static readonly IReadOnlyList<string> PreferredVoiceNames = ["Yating"];

    /// <summary>
    /// 選聲音的順序（SAPI 語言屬性是十六進位的 LCID）：臺灣華語、中國華語、香港粵語；每個語言先找傳統的桌面聲音、再找 OneCore 的聲音。
    /// 都沒有就用系統預設的聲音（通常是英文，念不出中文）
    /// </summary>
    public static readonly IReadOnlyList<string> VoiceLanguages = ["404", "804", "C04"];

    /// <summary>完整版與只有標題的版本（<paramref name="build"/> 的參數：要不要帶簡要說明）</summary>
    public static VoiceLine Line(Func<bool, string> build) => new(build(true), build(false));

    /// <summary>
    /// 送到伺服器的 <c>conv.attention.speech</c>：對話是完整同步（內容本來就上線）才送帶簡要說明的版本，只同步清單或暫停時只送「標題＋發生什麼事」；
    /// 沒有句子回 null（不帶這個欄位）、不念回空字串
    /// </summary>
    public static string? ForPolicy(VoiceLine? line, string? policy)
        => line is null ? null : policy == ConversationSyncPolicies.Full ? line.Full : line.Head;

    /// <summary>
    /// 放進 <c>conv.attention.speech</c> 的句子：沒超過同步協定的上限（<see cref="ConversationEventRules.MaxSpeechLength"/>）照原樣，超過的照 <see cref="Fit"/> 截短；
    /// null 照樣是 null，截完是空的回 null（不帶，伺服器改組只有標題的句子）。空字串（不念）照原樣
    /// </summary>
    public static string? ForWire(string? speech)
    {
        if (speech is null || speech.Length <= ConversationEventRules.MaxSpeechLength)
            return speech;
        var fitted = Fit(speech, ConversationEventRules.MaxSpeechLength);
        return fitted.Length == 0 ? null : fitted;
    }

    /// <summary>
    /// 伺服器替沒帶語音句子的事件（舊版桌面、失敗由 conv.turn 帶出的那一筆）組的句子：只有「標題＋發生什麼事」；不認得的種類回 null
    /// </summary>
    public static string? HeadFor(string? attentionKind, string? title) => attentionKind switch
    {
        ConversationAttentionKinds.Completed => Completed(title, null),
        ConversationAttentionKinds.Failed => Failed(title, null),
        ConversationAttentionKinds.Question => Question(title, null),
        ConversationAttentionKinds.Approval => Approval(title, null, null, null),
        ConversationAttentionKinds.Plan => Plan(title, null),
        _ => null,
    };

    /// <summary>完成：接代理最後一則回覆的重點（第一個有內容的句子）</summary>
    public static string Completed(string? title, string? reply) => Compose($"{Name(title)}已完成", Summary(reply));

    /// <summary>失敗：錯誤訊息是中文時接它的第一行（英文的技術錯誤念出來聽不懂，只說失敗）</summary>
    public static string Failed(string? title, string? error)
    {
        var line = TextRules.FirstLine(error);
        return Compose($"{Name(title)}執行失敗", HasCjk(line) ? line : null);
    }

    /// <summary>代理提問：接第一個問題的第一行</summary>
    public static string Question(string? title, string? question)
        => Compose($"{Name(title)}有問題想問你", TextRules.FirstLine(question));

    /// <summary>
    /// 要求核准：工具名稱是中文的顯示名稱（「執行指令」「修改檔案」）時念出來；英文的（MCP 工具的原始名稱）念出來聽不懂，改說「執行操作」。
    /// 說明照順序取：說明的第一行是中文時用它（Claude 的指令說明），否則是檔名
    /// </summary>
    public static string Approval(string? title, string? toolName, string? detail, string? filePath)
    {
        var tool = Clean(toolName, int.MaxValue);
        if (!HasCjk(tool))
            tool = "執行操作";
        var line = TextRules.FirstLine(detail);
        var brief = HasCjk(line) ? line : FileName(filePath);
        return Compose($"{Name(title)}要{tool}，等你核准", brief);
    }

    /// <summary>計畫等確認：接計畫的第一行（通常是計畫的標題）</summary>
    public static string Plan(string? title, string? plan)
        => Compose($"{Name(title)}的計畫等你確認", TextRules.FirstLine(plan));

    /// <summary>「對話標題」；沒有標題時說「對話」</summary>
    public static string Name(string? title)
    {
        var name = Clean(title, int.MaxValue);
        return name.Length == 0 ? "對話" : $"「{name}」";
    }

    /// <summary>「誰、發生什麼事」接「：說明」（不截短；兩段都先 <see cref="Clean"/>、拿掉尾端的標點）；沒有說明就只念「誰、發生什麼事」</summary>
    public static string Compose(string head, string? brief)
    {
        head = Fit(head, int.MaxValue);
        var text = Fit(brief, int.MaxValue);
        return text.Length == 0 ? head : head + "：" + text;
    }

    private static readonly Regex CodeFence = new(@"```[\s\S]*?(```|$)", RegexOptions.CultureInvariant);
    private static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant);

    /// <summary>括號裡的補充（提交編號、路徑、英文原文）：全形與半形，不跨行、不巢狀</summary>
    private static readonly Regex Parenthetical = new(@"[（(][^（）()\n]*[）)]", RegexOptions.CultureInvariant);

    private static readonly Regex ListMarker = new(@"^(>+|[-*+]|\d+[.)、])\s*", RegexOptions.CultureInvariant);

    /// <summary>開頭的客套（「好的，」「完成了。」）：後面要接標點才算，「完成登入頁」的「完成」不拿掉</summary>
    private static readonly Regex Filler = new(@"^((好的|好|OK|Ok|ok|沒問題|完成了|完成|已完成|全部完成|都完成了|做完了|搞定了|搞定)\s*[，。！!,.、]\s*)+", RegexOptions.CultureInvariant);

    /// <summary>
    /// 回覆的重點：拿掉程式碼區塊、圖片、連結網址與括號裡的補充，從第一個不是標題、表格、分隔線的段落取第一句；
    /// 開頭的客套拿掉，太短就接下一句；以「如下」「以下」加冒號帶出清單的那一句跳過、改看下一段。都沒有回空字串
    /// </summary>
    public static string Summary(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return string.Empty;
        var text = CodeFence.Replace(reply.Replace("\r", string.Empty), "\n");
        text = Image.Replace(text, string.Empty);
        text = Link.Replace(text, "$1");
        text = Parenthetical.Replace(text, string.Empty);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('|') || line.StartsWith("---", StringComparison.Ordinal))
                continue;
            line = Filler.Replace(ListMarker.Replace(line, string.Empty), string.Empty);
            var sentence = FirstSentences(line);
            var trimmed = sentence.TrimEnd();
            if (trimmed.EndsWith('：') || trimmed.EndsWith(':'))
            {
                if (trimmed.Contains("如下", StringComparison.Ordinal) || trimmed.Contains("以下", StringComparison.Ordinal))
                    continue;
                sentence = trimmed[..^1];
            }
            if (Clean(sentence, int.MaxValue).Length >= 2)
                return sentence;
        }
        return string.Empty;
    }

    /// <summary>第一句（到。！？或英文句點加空白為止）；第一句不到 6 個字就再接下一句</summary>
    private static string FirstSentences(string line)
    {
        var sb = new StringBuilder();
        var start = 0;
        while (start < line.Length)
        {
            var end = SentenceEnd(line, start);
            sb.Append(line, start, end - start);
            start = end;
            if (Clean(sb.ToString(), int.MaxValue).Length >= 6)
                break;
        }
        return sb.ToString();
    }

    private static int SentenceEnd(string line, int start)
    {
        for (var i = start; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch is '。' or '！' or '？' or '!' or '?')
                return i + 1;
            if (ch == '.' && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])))
                return i + 1;
        }
        return line.Length;
    }

    /// <summary>子句之間的標點：截斷時優先在這裡斷</summary>
    private static bool IsClauseBreak(char ch) => ch is '，' or '、' or '；' or '：' or ',' or ';' or ':' or '。' or '！' or '？';

    /// <summary>尾端念出來沒有意義的標點（問號留著，語調不同）</summary>
    private static string TrimTrailing(string text) => text.TrimEnd(' ', '，', '、', '；', '：', '。', ',', ';', ':', '.', '…');

    /// <summary>
    /// 放進 <paramref name="budget"/> 個字：先 <see cref="Clean"/>，太長時在後半段最後一個子句邊界斷，沒有邊界就直接截（不切開代理對、不加「…」，念不出來）；尾端的標點拿掉。
    /// 組句子時不截（<see cref="int.MaxValue"/>）；只有送到伺服器的句子超過同步協定的上限時用它截短
    /// </summary>
    public static string Fit(string? text, int budget)
    {
        var line = TrimTrailing(Clean(text, int.MaxValue));
        if (line.Length <= budget)
            return line;
        var cut = -1;
        for (var i = budget; i >= budget / 2 && i > 0; i--)
        {
            if (i < line.Length && IsClauseBreak(line[i]))
            {
                cut = i;
                break;
            }
        }
        if (cut < 0)
        {
            cut = budget;
            if (char.IsHighSurrogate(line[cut - 1]))
                cut--;
        }
        return TrimTrailing(line[..cut]);
    }

    /// <summary>
    /// 這一句要不要排進佇列：佇列已滿（<see cref="MaxPending"/>）不排；和上一句相同而且還在 <see cref="RepeatWindow"/> 內不排。
    /// <paramref name="last"/> 是上一句排進去的文字與時間（UTC）
    /// </summary>
    public static bool ShouldQueue(string text, int pending, (string Text, DateTime At)? last, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text) || pending >= MaxPending)
            return false;
        return last is not { } prev || prev.Text != text || now - prev.At >= RepeatWindow || now < prev.At;
    }

    private static readonly Regex Url = new(@"(?:https?|file)://\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Markdown 的強調與程式碼符號（念出來是「星號」）：直接拿掉，「加進**桌面版**」念成「加進桌面版」</summary>
    private static readonly Regex InlineSymbols = new(@"[*`~]+", RegexOptions.CultureInvariant);

    /// <summary>標題、引用、表格符號與底線（識別字裡的底線念成停頓）：換成空白</summary>
    private static readonly Regex SeparatorSymbols = new(@"[_#>|]+", RegexOptions.CultureInvariant);

    /// <summary>念出來的純文字：網址換成「連結」、拿掉 Markdown 符號與控制字元、空白合併成一個，超過 <paramref name="max"/> 字截斷；null 或全是空白回空字串</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var plain = SeparatorSymbols.Replace(InlineSymbols.Replace(Url.Replace(text, " 連結 "), string.Empty), " ");
        var sb = new StringBuilder(plain.Length);
        var space = false;
        foreach (var ch in plain)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = true;
                continue;
            }
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                continue;
            if (space && sb.Length > 0)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        var line = sb.ToString();
        if (line.Length <= max)
            return line;
        var cut = max;
        if (char.IsHighSurrogate(line[cut - 1]))
            cut--;
        return line[..cut].TrimEnd();
    }

    /// <summary>路徑的檔名（核准修改檔案時念它，不念整條路徑）</summary>
    private static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var trimmed = path.Trim().TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    private static bool HasCjk(string? text) => text != null && text.Any(ch => ch is >= (char)0x4E00 and <= (char)0x9FFF or >= (char)0x3400 and <= (char)0x4DBF);
}
