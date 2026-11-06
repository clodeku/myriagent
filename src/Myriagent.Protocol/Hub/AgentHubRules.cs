using System.Globalization;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Remote;

namespace Myriagent.Hub;

/// <summary>
/// 這一輪被使用者拒絕、而且附了說明的操作（核准卡的工具名稱與一行摘要）。
/// <paramref name="RemoteOrigin"/>：從遠端拒絕時的來源（設計 14.11 D13）；<paramref name="DisplayText"/>：畫面、紀錄與同步看的文字（D20）。
/// 兩者本機一律 null（不寫進 queue.json）
/// </summary>
internal sealed record DeniedOperation(string? ToolName, string? Title, string Feedback, RemoteOrigin? RemoteOrigin = null, string? DisplayText = null);

/// <summary>
/// 中樞的純函式規則：對話標題與分組、工具輸出截斷、給代理的背景說明、拒絕時附的說明、回覆中本機檔案路徑的辨識、前文節錄、/clear、閒置回收。
/// </summary>
internal static partial class AgentHubRules
{
    /// <summary>閒置多久關掉代理程序（下次送訊息自動續接）</summary>
    public static readonly TimeSpan IdleProcessTimeout = TimeSpan.FromMinutes(15);

    /// <summary>工具輸出在畫面與存檔只保留尾段（指令輸出可能數 MB）</summary>
    public const int MaxToolOutputChars = 12_000;

    public const int MaxTitleLength = 40;

    /// <summary>附加給代理的說明預設值；{cwd}、{attachments}、{questionTool}、{backgroundTool} 會被代換</summary>
    public const string DefaultInstructions =
        "你正在 Clode Myriagent（Windows 桌面程式）的對話視窗中替使用者工作。\n" +
        "使用者看得到你的文字回覆、工具進度卡與圖片，看不到終端機畫面；需要使用者知道的結果請寫在回覆裡。\n" +
        "一律使用臺灣繁體中文回覆。\n" +
        "需要使用者做決定或補充資訊時，使用 {questionTool} 工具提出選擇題，畫面會顯示成問卷讓使用者點選。\n" +
        "產出的檔案請寫出完整的本機路徑；圖片（png／jpg／gif／webp）與影片（mp4／webm／mov）只要寫出完整路徑，畫面就會直接顯示。\n" +
        "要等結果的長時間指令請{backgroundTool}，不要以 Start-Process、start、nohup 另開——另開的程式跑完不會回報給你；" +
        "只有要一直開著的程式（開發伺服器、開給使用者用的程式）才另開，它們會列在對話上方，使用者可以從那裡停止。\n" +
        "工作資料夾：{cwd}\n" +
        "使用者上傳的附件放在：{attachments}";

    /// <summary>輸入區「繼續」鈕預設送出的那一句（2026-10-24 使用者指定的原文，不加標點；網頁 sendrules.js 的 CONTINUE_TEXT 同值）</summary>
    public const string DefaultContinueText = "進度如何呢如果還沒做完就繼續";

    /// <summary>自訂的「繼續」指令最多幾個字元（UTF-16；網頁 sendrules.js 的 CONTINUE_MAX 同值）</summary>
    public const int ContinueTextMaxLength = 2000;

    /// <summary>
    /// 設定的「繼續」指令（setting 的 continueText）怎麼存：去掉頭尾空白，空的或等於預設存成 null（＝預設）；
    /// 超過上限的截掉（不切開代理字元對）
    /// </summary>
    public static string? NormalizeContinueText(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length > ContinueTextMaxLength)
        {
            int cut = ContinueTextMaxLength;
            if (char.IsHighSurrogate(t[cut - 1]))
                cut--;
            t = t[..cut].TrimEnd();
        }
        return t.Length == 0 || t == DefaultContinueText ? null : t;
    }

    public static string MakeTitle(string? firstMessage, IReadOnlyList<AgentAttachment>? attachments = null)
    {
        var line = (firstMessage ?? string.Empty)
            .Replace("\r", string.Empty)
            .Split('\n')
            .Select(l => Whitespace().Replace(l, " ").Trim())
            .FirstOrDefault(l => l.Length > 0);

        if (string.IsNullOrEmpty(line))
        {
            var first = attachments?.FirstOrDefault();
            line = first != null ? first.FileName : "新對話";
        }

        return line.Length <= MaxTitleLength ? line : line[..MaxTitleLength] + "…";
    }

    /// <summary>從 claude.ai 或 Claude App 送來的訊息要顯示的文字：附了圖片或其他檔案時補一行數量（附件不搬到本機）</summary>
    public static string DescribeRemoteUserMessage(string? text, int imageCount, int fileCount = 0)
    {
        var body = (text ?? string.Empty).Trim();
        var parts = new List<string>();
        if (imageCount > 0)
            parts.Add($"{imageCount} 張圖片");
        if (fileCount > 0)
            parts.Add($"{fileCount} 個檔案");
        if (parts.Count == 0)
            return body;
        var note = $"（附 {string.Join("、", parts)}，請在 claude.ai 或 Claude App 查看）";
        return body.Length == 0 ? note : body + "\n\n" + note;
    }

    /// <summary>對話清單的日期分組標題（今天、昨天、過去 7 天、過去 30 天、更早依月份）</summary>
    public static string GetGroupLabel(DateTime updatedAt, DateTime now)
    {
        var days = (now.Date - updatedAt.Date).TotalDays;
        if (days <= 0)
            return "今天";
        if (days <= 1)
            return "昨天";
        if (days <= 7)
            return "過去 7 天";
        if (days <= 30)
            return "過去 30 天";
        return updatedAt.Year == now.Year ? $"{updatedAt.Month} 月" : $"{updatedAt.Year} 年 {updatedAt.Month} 月";
    }

    public static string? TrimToolOutput(string? output)
    {
        if (output == null || output.Length <= MaxToolOutputChars)
            return output;
        return OmittedPrefix(output.Length - MaxToolOutputChars) + output[^MaxToolOutputChars..];
    }

    /// <summary>
    /// 串流中的工具輸出：把新片段接在目前顯示的輸出後面、只保留尾段。
    /// 目前的輸出若已截斷過（開頭是「…（前 N 字省略）」），省略的字數要累計，前綴本身也不算進內容。
    /// </summary>
    public static string AppendToolOutput(string? current, string? delta)
    {
        long omitted = 0;
        var body = current ?? string.Empty;
        var prefix = OmittedPrefixPattern().Match(body);
        if (prefix.Success && long.TryParse(prefix.Groups[1].Value.Replace(",", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var previous))
        {
            omitted = previous;
            body = body[prefix.Length..];
        }

        var combined = body + delta;
        if (combined.Length > MaxToolOutputChars)
        {
            omitted += combined.Length - MaxToolOutputChars;
            combined = combined[^MaxToolOutputChars..];
        }
        return omitted > 0 ? OmittedPrefix(omitted) + combined : combined;
    }

    /// <summary>開頭被截掉的「…（前 N 字省略）」前綴（含換行）；同步發布截斷要上傳的工具輸出時也用它（WP4.3 的 PublishFitRules），格式只有這一份</summary>
    internal static string OmittedPrefix(long omitted) => string.Create(CultureInfo.InvariantCulture, $"…（前 {omitted:N0} 字省略）\n");

    /// <summary>開頭被截掉的工具輸出前綴裡記的省略字數；不是截斷過的輸出（或數字讀不懂）回 0</summary>
    internal static long OmittedCount(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '…')
            return 0;
        var prefix = OmittedPrefixPattern().Match(text);
        return prefix.Success && long.TryParse(prefix.Groups[1].Value.Replace(",", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var omitted) ? omitted : 0;
    }

    /// <summary>
    /// 開頭被截掉的工具輸出（<see cref="TrimToolOutput"/>、<see cref="AppendToolOutput"/>）的「…（前 N 字省略）」前綴長度（含換行）；不是截斷過的輸出回 0。
    /// 權杖 DLP 用它找出切在權杖中間的第一行（CredentialLeakRules.MaskTruncated）
    /// </summary>
    internal static int OmittedPrefixLength(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '…')
            return 0;
        var prefix = OmittedPrefixPattern().Match(text);
        return prefix.Success ? prefix.Length : 0;
    }

    /// <summary>
    /// Codex 的背景說明只在建立 thread 時送一次，之後換工作資料夾也不會更新（每一輪的 cwd 由 turn/start 帶上）：
    /// 路徑後面註明以每一輪的 cwd 為準，代理才不會拿到互相矛盾的資料夾
    /// </summary>
    public const string CodexWorkingDirectoryNote = "（對話中途若更換工作資料夾，以每一輪環境資訊中的 cwd 為準）";

    /// <summary>
    /// Antigravity 的背景說明附在第一則訊息前（agy 沒有附加系統提示的旗標）；之後換工作資料夾時，會話在那一則訊息開頭另外告知
    /// （AntigravityStreamRules.BuildWorkingDirectoryNotice）
    /// </summary>
    public const string AntigravityWorkingDirectoryNote = "（對話中途若更換工作資料夾，會在訊息開頭另外告知）";

    /// <summary>
    /// Grok 的背景說明附在第一則訊息前（ACP 沒有附加系統提示的欄位）；之後換工作資料夾時，會話在那一則訊息開頭另外告知
    /// （GrokAcpRules.BuildWorkingDirectoryNotice）
    /// </summary>
    public const string GrokWorkingDirectoryNote = "（對話中途若更換工作資料夾，會在訊息開頭另外告知）";

    /// <summary>
    /// Antigravity 的 stream-json 沒有提問工具：範本裡提到 {questionTool} 的那一行換成這句（只換那一行，使用者自訂範本的其他內容不動）
    /// </summary>
    /// <summary>
    /// 範本的 {backgroundTool}：要等結果的長時間指令怎麼在背景跑（2026-10-24：代理以 Start-Process 另開的程式 CLI 不追蹤、跑完也不叫回代理，
    /// 只有 CLI 自己的背景機制會回報——Claude 的 run_in_background 跑完通知；Codex 一輪結束時還在終端機工作階段裡的指令，跑完 app 隱藏送出接續訊息）
    /// </summary>
    public static string BackgroundToolHint(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "用 Bash 工具的 run_in_background 在背景執行（跑完會通知你）",
        AgentProvider.Codex => "讓它留在終端機的工作階段裡繼續跑（跑完時 app 會請你接著處理）",
        AgentProvider.Antigravity or AgentProvider.Grok => "直接執行並等它結束",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    public const string AntigravityQuestionLine = "需要使用者做決定或補充資訊時，直接在回覆裡用文字列出問題與選項，然後結束這一輪，等使用者回覆。";

    /// <summary>給代理的背景說明（Claude --append-system-prompt、Codex developerInstructions、Antigravity 與 Grok 第一則訊息的前言）</summary>
    public static string BuildAgentInstructions(string? template, AgentProvider provider, string workingDirectory, string attachmentDirectory)
    {
        var text = string.IsNullOrWhiteSpace(template) ? DefaultInstructions : template;
        var (cwd, questionTool) = provider switch
        {
            AgentProvider.Claude => (workingDirectory, "AskUserQuestion"),
            AgentProvider.Codex => (workingDirectory + CodexWorkingDirectoryNote, "request_user_input"),
            AgentProvider.Antigravity => (workingDirectory + AntigravityWorkingDirectoryNote, (string?)null),
            AgentProvider.Grok => (workingDirectory + GrokWorkingDirectoryNote, "ask_user_question"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        if (questionTool == null)
            text = ReplaceQuestionToolLines(text, AntigravityQuestionLine);
        // 一次掃描全部代換：代入的路徑本身含「{attachments}」這類字樣時不會被再換一次
        return InstructionPlaceholder().Replace(text, m => m.Groups[1].Value switch
        {
            "cwd" => cwd,
            "attachments" => attachmentDirectory,
            "backgroundTool" => BackgroundToolHint(provider),
            _ => questionTool ?? string.Empty,
        });
    }

    /// <summary>範本裡含 {questionTool} 的每一行換成 replacement（同一段連續的這種行只換成一句）；沒有這種行就原樣回傳</summary>
    private static string ReplaceQuestionToolLines(string template, string replacement)
    {
        var lines = template.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (!line.Contains("{questionTool}", StringComparison.Ordinal))
                result.Add(line);
            else if (result.Count == 0 || result[^1] != replacement)
                result.Add(replacement);
        }
        return string.Join("\n", result);
    }

    /// <summary>
    /// 拒絕核准時附的說明要不要改當下一則訊息送（回覆沒有欄位能帶說明的：Codex、Grok）；Claude 放進拒絕訊息，Antigravity 沒有核准卡
    /// </summary>
    public static bool SendsDenyFeedbackAsMessage(AgentProvider provider) => provider switch
    {
        AgentProvider.Codex or AgentProvider.Grok => true,
        AgentProvider.Claude or AgentProvider.Antigravity => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// Codex 拒絕核准時附的說明（Codex 的拒絕沒有欄位能帶說明，這一輪結束後當下一則訊息送出）：
    /// 一則照原本的格式；同一輪拒絕多次就依序列出每個操作與說明，不能只留最後一則。沒有說明回 null。
    /// </summary>
    public static string? BuildDenyFeedbackMessage(IReadOnlyList<DeniedOperation> denials)
    {
        if (denials == null || denials.Count == 0)
            return null;
        if (denials.Count == 1)
            return $"（我剛才拒絕了那個操作）{denials[0].Feedback}";

        var lines = denials.Select((d, i) =>
        {
            var operation = string.Join("：", new[] { d.ToolName, TextRules.FirstLine(d.Title) }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return operation.Length > 0 ? $"{i + 1}. 「{operation}」：{d.Feedback}" : $"{i + 1}. {d.Feedback}";
        });
        return "（我剛才拒絕了這幾個操作）\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// 自回覆文字找出本機檔案的完整路徑（磁碟代號開頭；可被反引號、引號、括號包住）。
    /// UNC（\\server\share）刻意不認，見 AgentAttachmentRules.IsLocalDrivePath。
    /// </summary>
    public static List<string> ExtractLocalPaths(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LocalPathPattern().Matches(text))
        {
            var path = m.Value.TrimEnd('.', ',', '。', '，', '、', ';', '；', ':', '：', ')', '）', ']', '」', '』', '"', '\'', '`', '*');
            path = path.Replace('/', '\\');
            if (path.Length > 3 && seen.Add(path))
                result.Add(path);
        }

        return result;
    }

    /// <summary>反引號包住的完整路徑（可含空白），例如 `C:\My Files\chart.png`</summary>
    public static List<string> ExtractCodeSpanPaths(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;

        foreach (Match m in CodeSpanPathPattern().Matches(text))
        {
            var path = m.Groups[1].Value.Trim().Replace('/', '\\');
            if (path.Length > 3 && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                result.Add(path);
        }

        return result;
    }

    /// <summary>
    /// 回覆文字中要另外顯示在訊息下方的圖片／影片：文字或反引號裡出現的本機完整路徑、可顯示的媒體、檔案存在；
    /// 已用 Markdown 圖片或連結語法寫出的路徑會由 Markdown 自己顯示，不重複。
    /// <paramref name="relativeBases"/>（依序）有給時，相對路徑（videos/1.mp4）也找：解析到某個基準資料夾底下、檔案存在的第一個為準
    /// （Grok 把產生的圖片與影片存在自己的工作階段資料夾，回覆裡寫的是相對那裡的 videos/1.mp4，2026-11-06 使用者回報影片沒顯示）；
    /// 相對路徑的 Markdown 目標在 <paramref name="markdownBase"/>（對話的工作資料夾，Markdown 以它解析）底下找得到的由 Markdown 自己顯示，不重複
    /// </summary>
    public static List<string> FindMediaToShow(string? text, Func<string, bool> fileExists, IReadOnlyList<string?>? relativeBases = null, string? markdownBase = null)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;

        var markdownTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MarkdownTargetPattern().Matches(text))
            markdownTargets.Add(Uri.UnescapeDataString(m.Groups[1].Value).Replace('/', '\\'));

        foreach (var path in ExtractCodeSpanPaths(text).Concat(ExtractLocalPaths(text)))
        {
            if (markdownTargets.Contains(path) || result.Contains(path, StringComparer.OrdinalIgnoreCase))
                continue;
            if (AgentAttachmentRules.IsDisplayableMedia(path) && fileExists(path))
                result.Add(path);
        }

        if (relativeBases is null || relativeBases.Count == 0)
            return result;
        foreach (var relative in ExtractRelativeMediaPaths(text))
        {
            if (markdownTargets.Contains(relative) && ResolveUnder(markdownBase, relative) is { } viaMarkdown && fileExists(viaMarkdown))
                continue;
            foreach (var root in relativeBases)
            {
                if (ResolveUnder(root, relative) is not { } full || !fileExists(full))
                    continue;
                if (!result.Contains(full, StringComparer.OrdinalIgnoreCase))
                    result.Add(full);
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 回覆文字裡可顯示的媒體的相對路徑（videos/1.mp4、./out/a.png、images\x.jpg）：只收字母、數字、「_-./\%」組成、副檔名是可顯示的媒體；
    /// 開頭是「/」「\」、磁碟代號（完整路徑另外找）、前面接著「:」「/」（網址的一部分）的不收。斜線一律換成「\」，去重
    /// </summary>
    public static List<string> ExtractRelativeMediaPaths(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;
        foreach (Match m in RelativeMediaPattern().Matches(text))
        {
            var path = m.Value.Replace('/', '\\');
            if (path.StartsWith('\\') || !AgentAttachmentRules.IsDisplayableMedia(path) || result.Contains(path, StringComparer.OrdinalIgnoreCase))
                continue;
            result.Add(path);
        }
        return result;
    }

    /// <summary>相對路徑接在 root（本機磁碟的完整路徑）底下；跑出 root（..）、root 不合格、組不出路徑回 null</summary>
    private static string? ResolveUnder(string? root, string relative)
    {
        if (!AgentAttachmentRules.IsLocalDrivePath(root))
            return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(root!, relative));
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root!)) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public const int MaxHistoryRecapChars = 12_000;

    /// <summary>前文節錄的開頭（續接失敗或換帳戶）</summary>
    public const string HistoryRecapIntro = "（系統說明：原本的對話紀錄已無法續接，以下是先前對話的節錄，供你了解前情；請直接回應最後這則新訊息。）\n";

    /// <summary>
    /// 續接失敗或換帳戶時給新 session 的前文：由新到舊收使用者訊息與代理回覆，超過上限就停，再依時間順序排回；沒有可用內容回 null。
    /// 只收最後一次 /clear（清除提示，IsClearNotice）之後的：清除前的內容不再給代理。intro＝開頭的系統說明（null＝<see cref="HistoryRecapIntro"/>；延伸的對話另有一句）
    /// </summary>
    public static string? BuildHistoryRecap(IReadOnlyList<AgentChatItem> items, int maxChars = MaxHistoryRecapChars, string? intro = null)
    {
        if (items == null || items.Count == 0)
            return null;

        var lines = new List<string>();
        int total = 0;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (IsClearNotice(item))
                break;
            string? line = item.Kind switch
            {
                AgentItemKind.User when !string.IsNullOrWhiteSpace(item.Text) => $"【使用者】{item.Text.Trim()}",
                AgentItemKind.User when item.Attachments.Count > 0 => $"【使用者】（附件：{string.Join("、", item.Attachments.Select(a => a.FileName))}）",
                AgentItemKind.Assistant when !string.IsNullOrWhiteSpace(item.Text) => $"【代理】{item.Text.Trim()}",
                _ => null,
            };
            if (line == null)
                continue;
            if (line.Length > 2000)
                line = line[..2000] + "…（略）";
            if (total + line.Length > maxChars)
                break;
            lines.Add(line);
            total += line.Length;
        }

        if (lines.Count == 0)
            return null;
        lines.Reverse();
        return (intro ?? HistoryRecapIntro)
               + string.Join("\n\n", lines)
               + "\n（節錄結束）\n\n";
    }

    /// <summary>/clear 之後加的提示（Notice，NoticeKind＝Clear）</summary>
    public const string ClearNoticeText = "已清除對話：代理看不到這之前的內容，接下來的訊息從新的工作階段開始。";

    /// <summary>輸入區沒有 clear 指令時補上的（Claude 的指令清單本來就有，其他代理沒有）</summary>
    public static readonly AgentSlashCommand ClearCommand = new("clear", "清除對話：代理看不到之前的內容，接下來的訊息從新的工作階段開始", null);

    /// <summary>送出的是 /clear：前後空白不算，其他照 CLI 的指令名稱（小寫、不帶參數）</summary>
    public static bool IsClearCommand(string? text) => string.Equals(text?.Trim(), "/clear", StringComparison.Ordinal);

    /// <summary>清除提示：畫面藏起它之前的項目、前文節錄只收它之後的</summary>
    public static bool IsClearNotice(AgentChatItem item) => item.Kind == AgentItemKind.Notice && item.NoticeKind == AgentNoticeKind.Clear;

    /// <summary>標頭「最近的指令」的文字上限：標頭每次狀態改變都整份重送，貼了整份記錄檔的訊息只送開頭</summary>
    public const int LastPromptMaxChars = 2000;

    /// <summary>
    /// 對話上方常駐的「最近的指令」：最後一則使用者訊息（含從 claude.ai、其他電腦送來的）。
    /// 它之後有清除提示（/clear）就沒有：清除前的內容代理已經看不到了；沒有使用者訊息也是 null
    /// </summary>
    public static AgentChatItem? LastPrompt(IReadOnlyList<AgentChatItem>? items)
    {
        if (items == null)
            return null;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (IsClearNotice(item))
                return null;
            if (item.Kind == AgentItemKind.User)
                return item;
        }
        return null;
    }

    /// <summary>「最近的指令」的文字：超過上限截斷並加「…」（不切開代理字對）</summary>
    public static string LastPromptText(string? text, int maxChars = LastPromptMaxChars)
    {
        text ??= string.Empty;
        if (text.Length <= maxChars)
            return text;
        int cut = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..cut] + "…";
    }

    /// <summary>「最近的指令」那一行展開的歷史指令（web-protocol 的 promptHistory，2026-10-25 使用者要求）最多幾則</summary>
    public const int PromptHistoryLimit = 10;

    /// <summary>歷史指令每則最多幾個字：更長的（貼上整份記錄檔之類）不列，同輸入區 ↑↓ 的紀錄（historyrules.HISTORY_MAX_CHARS）</summary>
    public const int PromptHistoryMaxChars = 100_000;

    /// <summary>
    /// 「最近的指令」那一行展開的歷史指令：由新到舊的使用者訊息（含從 claude.ai、其他電腦送來的；/clear 之前的也列——這是送過的指令，
    /// 不是代理還看得到的前文）。文字空白（只有附件）、太長、語音訊息（畫面上的「語音訊息（0:12）」帶回輸入框沒有意義）與內容和較新的一則
    /// 相同的略過，最多 <paramref name="limit"/> 則。紀錄就是對話的 items，app 重開後照樣在
    /// </summary>
    public static List<AgentChatItem> RecentPrompts(IReadOnlyList<AgentChatItem>? items, int limit = PromptHistoryLimit)
    {
        var list = new List<AgentChatItem>();
        if (items == null || limit <= 0)
            return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = items.Count - 1; i >= 0 && list.Count < limit; i--)
        {
            var item = items[i];
            if (item.Kind != AgentItemKind.User || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > PromptHistoryMaxChars)
                continue;
            if (item.Attachments.Count > 0 && VoiceMessageRules.IsDisplayText(item.Text))
                continue;
            if (seen.Add(item.Text))
                list.Add(item);
        }
        return list;
    }

    /// <summary>
    /// /clear 交給 CLI 自己處理：Claude 照常送出（CLI 換新的 session、程序與 Remote Control 照舊開著；中樞在這一輪正常結束後加清除提示）。
    /// 其他代理沒有這個指令（送出去只會變成給模型的一句話），由中樞直接換新的 session、不送給代理
    /// </summary>
    public static bool CliHandlesClear(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => true,
        AgentProvider.Codex => false,
        AgentProvider.Antigravity => false,
        AgentProvider.Grok => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>旁支提問的指令（同 Claude 互動介面的 /btw；CLI 的指令清單沒有它，中樞接住、不送給代理）</summary>
    public static readonly AgentSlashCommand SideQuestionCommand = new("btw", "旁支提問：不打斷這一輪，問答不進對話的上下文", "<問題>");

    /// <summary>
    /// 送出的是 /btw：回傳問題（去頭尾空白；只打 /btw 是空字串）；不是 /btw 回 null。指令名稱後面要接空白或結束（/btwx 不算），
    /// 前後空白不算
    /// </summary>
    public static string? SideQuestionOf(string? text)
    {
        var trimmed = text?.Trim();
        if (trimmed == null || !trimmed.StartsWith("/btw", StringComparison.Ordinal))
            return null;
        if (trimmed.Length == 4)
            return string.Empty;
        return char.IsWhiteSpace(trimmed[4]) ? trimmed[5..].Trim() : null;
    }

    /// <summary>支援旁支提問的代理（Claude 的 control_request side_question；其他代理沒有不進上下文的問法）</summary>
    public static bool SupportsSideQuestion(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => true,
        AgentProvider.Codex => false,
        AgentProvider.Antigravity => false,
        AgentProvider.Grok => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>支援旁支提問的代理在指令清單補上 /btw（已經有就不補）；其他代理原樣</summary>
    public static IReadOnlyList<AgentSlashCommand> WithSideQuestionCommand(IReadOnlyList<AgentSlashCommand> commands, AgentProvider provider)
        => !SupportsSideQuestion(provider) || commands.Any(c => string.Equals(c.Name, SideQuestionCommand.Name, StringComparison.Ordinal))
            ? commands
            : commands.Append(SideQuestionCommand).ToList();

    /// <summary>輸入區的指令清單：沒有 clear 就補在最後（中樞一律接得住 /clear）</summary>
    public static IReadOnlyList<AgentSlashCommand> WithClearCommand(IReadOnlyList<AgentSlashCommand> commands)
        => commands.Any(c => string.Equals(c.Name, ClearCommand.Name, StringComparison.Ordinal)) ? commands : commands.Append(ClearCommand).ToList();

    /// <summary>
    /// 開著 Remote Control 的程序閒置多久才關（2026-10-23 使用者決定）：以前一律不回收，每開一個 Claude 對話就多掛一個程序，
    /// app 換版還會全部重開——實測一天下來 35 個、各約 450 MB、合計約 16 GB，其中 32 個已經超過 6 小時沒動靜
    /// </summary>
    public static readonly TimeSpan RemoteControlIdleTimeout = TimeSpan.FromHours(2);

    /// <summary>
    /// 閒置夠久、沒在等使用者的程序關掉（下次送出自動續接）：一般的程序 IdleProcessTimeout（15 分鐘）；
    /// 開著 Remote Control 的 RemoteControlIdleTimeout（2 小時，這段時間內隨時能從 claude.ai 或 Claude App 接手）
    /// </summary>
    public static bool ShouldRecycle(bool running, bool waitingForUser, bool remoteControl, DateTime lastActivity, DateTime now)
        => !running && !waitingForUser && now - lastActivity >= (remoteControl ? RemoteControlIdleTimeout : IdleProcessTimeout);

    /// <summary>
    /// app 換版（或放棄換版）之後要不要重開程序接回 Remote Control：最後的動靜在 RemoteControlIdleTimeout 之內才接回；
    /// 更久沒用的接回之後也只是等著被回收（以前換版一律全部重開）
    /// </summary>
    public static bool ShouldReattachRemoteControl(DateTime lastActivity, DateTime now) => now - lastActivity < RemoteControlIdleTimeout;

    /// <summary>
    /// 代理在執行中送出非同步提問（<c>AnswerAsMessage</c> 的問卷卡）時，要不要停下這一輪等使用者回答（2026-10-28 使用者要求：
    /// 代理問了問題就要先等回覆再繼續）。Codex 的 <c>request_user_input_async</c> 一送出就回 <c>{"accepted":true}</c>、模型照它內建的指示
    /// 接著做不依賴答案的事，app 只能在收到提問時以 turn/interrupt 停下這一輪；答案照舊當作下一則訊息送出。
    /// Antigravity 的計畫確認卡是這一輪結束前才發，Claude、Grok 的提問本來就擋住這一輪
    /// </summary>
    public static bool PausesForAsyncQuestion(AgentProvider provider) => provider switch
    {
        AgentProvider.Codex => true,
        AgentProvider.Claude => false,
        AgentProvider.Antigravity => false,
        AgentProvider.Grok => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 回覆文字裡的相對路徑（videos/1.mp4）要不要找出來顯示（FindMediaToShow 的 relativeBases）：Grok 把產生的圖片與影片存在自己的工作階段資料夾、
    /// 回覆裡寫相對那裡的路徑（2026-11-06）；其他代理照舊只認完整路徑
    /// </summary>
    public static bool ShowsRelativeReplyMedia(AgentProvider provider) => provider switch
    {
        AgentProvider.Grok => true,
        AgentProvider.Claude => false,
        AgentProvider.Codex => false,
        AgentProvider.Antigravity => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>為了等回答而停下的這一輪，使用者略過了那個問題：送給代理的接續訊息</summary>
    public const string SkippedAsyncQuestionMessage = "我略過了你剛才的問題，請依你的判斷繼續。";

    /// <summary>
    /// 代理執行中送出的訊息能不能直接插進這一輪（IAgentSession.SteerAsync）：Claude（stdin 併進同一輪）、Codex（turn/steer）可以；
    /// Antigravity、Grok（ACP 一輪進行中不能再送 session/prompt）沒有這種協定，照舊排進佇列等這一輪結束
    /// </summary>
    public static bool SupportsSteering(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => true,
        AgentProvider.Codex => true,
        AgentProvider.Antigravity => false,
        AgentProvider.Grok => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 這種代理的對話能不能換電腦（第 5 階段的交接與強制接手；擁有者 2026-10-23 決定 1、計畫 D20）：Claude、Codex 可以（以完整逐字稿或前文節錄續接）；
    /// Grok、Antigravity 不能（介面不顯示換電腦的動作，伺服器以 <c>conversations.provider</c> 再擋一次：<see cref="Myriagent.Sync.LeaseRules.SupportsHandoff"/>）
    /// </summary>
    public static bool SupportsHandoff(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => true,
        AgentProvider.Codex => true,
        AgentProvider.Antigravity => false,
        AgentProvider.Grok => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 換模型後的推理強度：新模型的清單沒有目前的強度（含不支援推理強度的模型）就回到帳戶預設（null）。
    /// 網頁只依模型清單顯示強度，不清掉的話畫面寫「預設」或不顯示，實際卻照舊把舊值傳給 CLI，模型也可能拒絕這一輪。
    /// 找模型的方式與網頁相同：沒有這個模型就看「default」項目，再沒有就看第一個；還不知道模型清單時保留原值。
    /// </summary>
    public static string? EffortForModel(string? effort, string? model, IReadOnlyList<AgentModelOption> models)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return null;
        if (models.Count == 0)
            return effort;
        var value = string.IsNullOrWhiteSpace(model) ? "default" : model;
        var option = models.FirstOrDefault(m => m.Value == value) ?? models.FirstOrDefault(m => m.Value == "default") ?? models[0];
        return option.Efforts.Contains(effort, StringComparer.Ordinal) ? effort : null;
    }

    /// <summary>最近使用的資料夾：放到最前面、去重（不分大小寫）、最多 12 個</summary>
    public static List<string> PushRecentFolder(IReadOnlyList<string>? recent, string folder, int max = 12)
    {
        var result = new List<string> { folder };
        foreach (var f in recent ?? Array.Empty<string>())
        {
            if (!string.Equals(f.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                result.Add(f);
            if (result.Count >= max)
                break;
        }
        return result;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\{(cwd|attachments|questionTool|backgroundTool)\}")]
    private static partial Regex InstructionPlaceholder();

    [GeneratedRegex(@"^…（前 ([0-9,]+) 字省略）\n")]
    private static partial Regex OmittedPrefixPattern();

    /// <summary>
    /// 本機路徑：「C:\」「C:/」開頭，延伸到空白、換行、引號、反引號、角括號、括號為止。
    /// 含空白的路徑只能靠 Markdown 連結或反引號包住才完整辨識。
    /// </summary>
    [GeneratedRegex(@"(?<![\w/\\])[A-Za-z]:[\\/][^\s""'`<>|*?\r\n()（）「」『』【】。，、；！？：]+")]
    private static partial Regex LocalPathPattern();

    [GeneratedRegex(@"`([A-Za-z]:[\\/][^`\r\n]+)`")]
    private static partial Regex CodeSpanPathPattern();

    [GeneratedRegex(@"\]\(\s*<?([^)\s>]+)")]
    private static partial Regex MarkdownTargetPattern();

    /// <summary>
    /// 相對路徑的候選：前面不是字母、數字、「_-./\%:」（網址、完整路徑的一部分不收），可以「./」「../」開頭，由這些字元（「:」除外）組成、
    /// 最後一段帶 2～5 碼的副檔名，後面不再接路徑字元
    /// </summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}_\-./\\%:])(?:\.{1,2}[/\\])*[\p{L}\p{N}_\-%]+(?:\.[\p{L}\p{N}_\-%]+)*(?:[/\\]\.{0,2}[\p{L}\p{N}_\-%]+(?:\.[\p{L}\p{N}_\-%]+)*)*\.[A-Za-z0-9]{2,5}(?![\p{L}\p{N}_\-/\\%])")]
    private static partial Regex RelativeMediaPattern();
}
