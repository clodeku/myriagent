using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Myriagent.Rendering;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.AutoLinks;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Myriagent.Agents;

/// <summary>
/// 代理回覆的 Markdown → HTML（給 WebView2 顯示）。
/// 安全：停用原始 HTML（回覆裡的 &lt;script&gt; 等一律當文字）、不開 GenericAttributes 這類能塞屬性的擴充；
/// 連結只放行 http／https／mailto，其他（javascript:、data: 等）一律改成「#」；
/// 外站圖片不自動載入（改成連結）——代理被惡意網頁誘導時，可能把資料藏在圖片網址裡外送；
/// 本機路徑（只認磁碟代號開頭，UNC 一律不認；相對路徑以對話的工作資料夾解析）的圖片改走 /media 端點、連結改走 /open（由宿主決定怎麼開）；
/// 這兩種網址也可以由呼叫端以 IMediaUrlBuilder 指定（伺服器改成 /m/{ref}、/f/{ref}）。
/// 文字裡直接寫出、或整段行內程式碼就是的本機完整路徑也變成檔案連結（LocalPathInlineParser、LinkCodePath），網址實作沒有網址可給時照原文顯示。
/// 網頁另有 CSP（img-src 只允許本頁來源）當第二道防線。
/// </summary>
internal static partial class AgentMarkdownRenderer
{
    /// <summary>WebView2 載入介面的虛擬網域</summary>
    public const string Host = "myriagent.local";

    public const string Origin = "https://" + Host;

    // 強調只開 GFM 的「~~刪除線~~」：預設的下標「~」、上標「^」、插入「++」、標記「==」會把「3~5 天」「O(n^2)」「C++20」裡的符號吃掉
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAlertBlocks()
        .Use<GitHubAlertExtension>()
        .Use<CjkAutoLinkExtension>()
        .Use<LocalPathLinkExtension>()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    public static string MediaUrl(string path) => $"{Origin}/media?path={Uri.EscapeDataString(path ?? string.Empty)}";

    /// <param name="version">檔案版本（修改時間＋大小）：檔案被覆寫後網址跟著變，網頁不會沿用瀏覽器快取裡的舊縮圖；宿主不讀這個參數</param>
    public static string ThumbUrl(string path, int size, string? version = null)
        => $"{Origin}/thumb?path={Uri.EscapeDataString(path ?? string.Empty)}&size={size}"
           + (string.IsNullOrEmpty(version) ? string.Empty : "&v=" + Uri.EscapeDataString(version));

    public static string OpenUrl(string path) => $"{Origin}/open?path={Uri.EscapeDataString(path ?? string.Empty)}";

    public static string ToHtml(string? markdown) => ToHtml(markdown, null);

    /// <param name="localImages">轉換時遇到的本機圖片路徑（宿主據此登記媒體端點的白名單）</param>
    /// <param name="baseDirectory">相對路徑的基準（對話的工作資料夾）；null＝相對路徑不當本機檔案</param>
    /// <param name="urls">本機圖片與檔案連結的網址（IMediaUrlBuilder.MarkdownImageUrl、MarkdownLinkUrl）；null＝桌面的 /media、/open</param>
    public static string ToHtml(string? markdown, ICollection<string>? localImages, string? baseDirectory = null, IMediaUrlBuilder? urls = null)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var code in document.Descendants<CodeInline>().ToList())
            LinkCodePath(code);

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link is LocalPathLinkInline pathLink)
            {
                // 沒有網址可給（別台電腦的對話）就照原文顯示，不留點了沒反應的連結
                var href = LinkUrl(pathLink.Path, urls);
                if (href == "#")
                    Unwrap(pathLink);
                else
                    pathLink.Url = href;
                continue;
            }

            var url = link.Url?.Trim();
            if (link.IsImage && IsAllowedWebUrl(url))
            {
                link.IsImage = false;
                if (link.FirstChild == null)
                    link.AppendChild(new LiteralInline($"外部圖片：{url}"));
                continue;
            }

            // 本機路徑先用原文判斷：CommonMark 把「\」接 ASCII 標點當跳脫，解析後的 C:\app\.github 會變成 C:\app.github
            var localPath = link.IsAutoLink ? null : ResolveLocalPath(SourceUrl(markdown, link), baseDirectory) ?? ResolveLocalPath(url, baseDirectory);
            if (link.IsImage)
            {
                if (localPath == null)
                {
                    link.IsImage = false;
                    link.Url = "#";
                    continue;
                }
                var src = ImageUrl(localPath, urls);
                if (src == "#")
                {
                    // 沒有網址可給（別台電腦的對話、伺服器）：不畫成 <img src="#">（破圖，網頁還會回報載入失敗），照原文顯示說明文字，
                    // 沒有說明就寫檔名（同伺服器 ItemSanitizer 把不合格的 img 換成 alt 文字）
                    if (link.FirstChild == null)
                        link.AppendChild(new LiteralInline($"圖片：{Path.GetFileName(localPath)}"));
                    Unwrap(link);
                    continue;
                }
                localImages?.Add(localPath);
                link.Url = src;
                continue;
            }
            link.Url = localPath == null ? RewriteUrl(url, link.IsImage, urls) : LinkUrl(localPath, urls);
        }

        foreach (var autolink in document.Descendants<AutolinkInline>())
        {
            if (!autolink.IsEmail && !IsAllowedWebUrl(autolink.Url))
                autolink.Url = "#";
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>純文字轉 HTML（使用者訊息：保留換行、不解讀 Markdown）</summary>
    public static string PlainToHtml(string? text)
        => WebUtility.HtmlEncode(text ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "<br>");

    internal static string RewriteUrl(string? url, bool isImage, IMediaUrlBuilder? urls = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "#";

        var trimmed = url.Trim();
        if (IsAllowedWebUrl(trimmed))
            return isImage ? "#" : trimmed;
        if (!isImage && trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var localPath = TryGetLocalPath(trimmed);
        if (localPath != null)
            return isImage ? ImageUrl(localPath, urls) : LinkUrl(localPath, urls);

        return "#";
    }

    /// <summary>本機圖片的 &lt;img src&gt;：呼叫端指定的網址，沒有指定就是桌面的 /media</summary>
    private static string ImageUrl(string path, IMediaUrlBuilder? urls) => urls == null ? MediaUrl(path) : urls.MarkdownImageUrl(path);

    /// <summary>本機檔案的 &lt;a href&gt;：呼叫端指定的網址，沒有指定就是桌面的 /open</summary>
    private static string LinkUrl(string path, IMediaUrlBuilder? urls) => urls == null ? OpenUrl(path) : urls.MarkdownLinkUrl(path);

    private static bool IsAllowedWebUrl(string? url)
        => url != null
           && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
           && !url.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);

    /// <summary>本機磁碟路徑或 file:/// 網址 → Windows 路徑；UNC 與其他回 null</summary>
    internal static string? TryGetLocalPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var value = url.Trim().Trim('<', '>');
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc)
                value = uri.LocalPath;
            else
                return null;
        }
        else
        {
            try
            {
                value = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        // 分隔字元一律換成單一「\」（照 CommonMark 寫成「\\」的路徑也是同一個檔案）
        return AgentAttachmentRules.IsLocalDrivePath(value) ? Separators().Replace(value, "\\") : null;
    }

    /// <summary>相對路徑 → 以工作資料夾解析成完整路徑；錨點、根目錄或 UNC 開頭、含「:」（網址、磁碟代號、資料流）的不認</summary>
    internal static string? TryResolveRelativePath(string? url, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(url) || !AgentAttachmentRules.IsLocalDrivePath(baseDirectory))
            return null;

        string value;
        try
        {
            value = Uri.UnescapeDataString(url.Trim().Trim('<', '>'));
        }
        catch (UriFormatException)
        {
            return null;
        }
        if (value.Length == 0 || value[0] is '/' or '\\' or '#' or '~' || value.Contains(':'))
            return null;

        try
        {
            var full = Path.GetFullPath(Path.Combine(baseDirectory!, value));
            return AgentAttachmentRules.IsLocalDrivePath(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>連結目的地 → 本機檔案（完整路徑、file:/// 網址或相對於工作資料夾的路徑），先去掉 #L42、#L3-L5 這類行號錨點</summary>
    private static string? ResolveLocalPath(string? url, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var path = LineAnchor().Replace(url.Trim(), string.Empty);
        return TryGetLocalPath(path) ?? TryResolveRelativePath(path, baseDirectory);
    }

    /// <summary>
    /// 連結目的地在 Markdown 原文裡的寫法：「\」照原樣（Windows 路徑的「\」都是分隔字元）、HTML 實體解碼、去掉角括號；
    /// 原文照 CommonMark 規則跳脫後要等於解析出的網址，才確定是這個連結的目的地，對不上回 null
    /// </summary>
    private static string? SourceUrl(string markdown, LinkInline link)
    {
        var span = link.UrlSpan;
        if (span.Start < 0 || span.End < span.Start || span.End >= markdown.Length)
            return null;
        var raw = markdown.Substring(span.Start, span.Length);
        if (raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>')
            raw = raw[1..^1];
        return HtmlHelper.Unescape(raw) == link.Url ? HtmlHelper.Unescape(raw, removeBackSlash: false) : null;
    }

    /// <summary>
    /// 文字或行內程式碼裡寫出的路徑 → 連結目的地：去掉行號（C:\a.cs:42、C:\a.cs:42:7、C:\a.cs#L42）、分隔字元統一成單一「\」；
    /// 不是本機磁碟的完整路徑、或磁碟代號後面什麼都沒有回 null。不做網址解碼：文字裡的 %20 就是檔名的一部分
    /// </summary>
    internal static string? WrittenPathTarget(string? written)
    {
        if (written == null || written.Length <= 3 || !AgentAttachmentRules.IsLocalDrivePath(written))
            return null;
        var path = LineAnchor().Replace(LineSuffix().Replace(written, string.Empty), string.Empty);
        return path.Length > 3 ? Separators().Replace(path, "\\") : null;
    }

    /// <summary>
    /// 整段都是本機完整路徑的行內程式碼（`C:\out\報表 v2.xlsx`，可含空白）→ 連結目的地；
    /// 含路徑不能有的字元、帶參數的指令（`C:\tools\x.exe --help`）不算，回 null
    /// </summary>
    internal static string? CodePathTarget(string? content)
    {
        var value = content?.Trim();
        if (value == null || value.Any(c => char.IsControl(c) || c is '<' or '>' or '|' or '"' or '?' or '*') || CommandArgument().IsMatch(value))
            return null;
        return WrittenPathTarget(value);
    }

    /// <summary>行內程式碼包進檔案連結（網址等連結都處理完再由 ToHtml 換上）；已在連結裡的不動</summary>
    private static void LinkCodePath(CodeInline code)
    {
        if (CodePathTarget(code.Content) is not { } path)
            return;
        for (var parent = code.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is LinkInline)
                return;
        }
        var link = new LocalPathLinkInline(path) { Span = code.Span, Line = code.Line, Column = code.Column };
        code.ReplaceBy(link, copyChildren: false);
        link.AppendChild(code);
    }

    /// <summary>拿掉連結、內容照原位置留下</summary>
    private static void Unwrap(ContainerInline container)
    {
        while (container.FirstChild is { } child)
        {
            child.Remove();
            container.InsertBefore(child);
        }
        container.Remove();
    }

    /// <summary>文字裡直接寫出的本機路徑變成的連結；Path 是連結目的地（WrittenPathTarget），網址由 ToHtml 依網址實作換上</summary>
    private sealed class LocalPathLinkInline : LinkInline
    {
        public LocalPathLinkInline(string path)
        {
            Path = path;
            IsClosed = true;
        }

        public string Path { get; }
    }

    private sealed class LocalPathLinkExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
            if (!pipeline.InlineParsers.Contains<LocalPathInlineParser>())
                pipeline.InlineParsers.Add(new LocalPathInlineParser());
        }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
        }
    }

    /// <summary>
    /// 文字裡的本機完整路徑（C:\…、C:/…）。觸發字元是「:」而不是磁碟代號（每個英文字母都觸發的話，一般文字每個字都要多跑一次判斷）：
    /// 磁碟代號已經在前一段文字的結尾，確認後從那段拿掉、整段路徑改成連結。
    /// 路徑以原文為準（反斜線接 ASCII 標點不當跳脫、底線不當強調），延伸到空白、引號、角括號、括號、全形標點為止（同 AgentHubRules.ExtractLocalPaths），
    /// 尾端的句號逗號等標點不算；含空白的路徑只能靠反引號或 Markdown 連結。前面接著英數字或網址字元（abC:\、/C:/）、在連結文字裡的不認
    /// </summary>
    private sealed class LocalPathInlineParser : InlineParser
    {
        /// <summary>路徑在這些字元前結束</summary>
        private const string Terminators = "\"'`<>|*?()（）「」『』【】。，、；！？：";

        /// <summary>路徑尾端不算進去的標點（「見 C:\a\b.png。」「C:\a.cs:」）</summary>
        private const string TrailingPunctuation = ".,;:!~]";

        /// <summary>磁碟代號前面是這些字元時不是路徑的開頭（網址、檔名、參數的一部分）</summary>
        private const string InvalidPrevious = "_/\\:.-%$@&#";

        public LocalPathInlineParser() => OpeningCharacters = new[] { ':' };

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var text = slice.Text;
            int start = slice.Start - 1;
            if (start < 0 || !char.IsAsciiLetter(text[start]) || slice.PeekChar(1) is not ('\\' or '/'))
                return false;
            if (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1]) || InvalidPrevious.Contains(text[start - 1])))
                return false;
            // 磁碟代號要是前一段文字的最後一個字（被其他語法吃掉的不算）
            if (processor.Inline is not LiteralInline previous || !ReferenceEquals(previous.Content.Text, text)
                || previous.Content.End != start || previous.Content.Start > start || IsInLinkLabel(processor))
                return false;

            int end = slice.Start + 2;
            while (end <= slice.End && !char.IsWhiteSpace(text[end]) && !char.IsControl(text[end]) && !Terminators.Contains(text[end]))
                end++;
            while (end > start && TrailingPunctuation.Contains(text[end - 1]) && !(text[end - 1] == ']' && text.AsSpan(start, end - start).Contains('[')))
                end--;
            var written = text[start..end];
            if (WrittenPathTarget(written) is not { } path)
                return false;

            previous.Content.End--;
            previous.Span.End--;
            if (previous.Content.IsEmpty)
                previous.Remove();

            int position = processor.GetSourcePosition(start, out int line, out int column);
            var span = new SourceSpan(position, position + written.Length - 1);
            var link = new LocalPathLinkInline(path) { Span = span, Line = line, Column = column };
            link.AppendChild(new LiteralInline(written) { Span = span, Line = line, Column = column, IsClosed = true });
            processor.Inline = link;
            slice.Start = end;
            return true;
        }

        /// <summary>還沒配對的「[」裡（可能是 Markdown 連結的文字）：連結不能包連結（同 Markdig 的 AutoLinkParser）</summary>
        private static bool IsInLinkLabel(InlineProcessor processor)
        {
            int open = 0;
            for (var inline = processor.Inline; inline != null; inline = inline.Parent)
            {
                if (inline is LinkDelimiterInline { IsActive: true } delimiter)
                    open += delimiter.Type == DelimiterType.Open ? 1 : delimiter.Type == DelimiterType.Close ? -1 : 0;
            }
            return open > 0;
        }
    }

    [GeneratedRegex(@"(?::[0-9]+){1,2}$")]
    private static partial Regex LineSuffix();

    /// <summary>指令接參數：空白後接「-」或「/」</summary>
    [GeneratedRegex(@"\s[-/]")]
    private static partial Regex CommandArgument();

    [GeneratedRegex(@"[\\/]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"#L[0-9]+(?:C[0-9]+)?(?:-L?[0-9]+(?:C[0-9]+)?)?$")]
    private static partial Regex LineAnchor();

    /// <summary>
    /// 自動連結（文字裡直接寫出的網址）：Markdig 判斷網址結尾只認 ASCII 空白與標點，中文句子裡緊接的「，請參考。」「（說明）」會被併進網址，
    /// 前面是「：」「（」「「」時又完全不辨識，所以改用 CjkAutoLinkParser
    /// </summary>
    private sealed class CjkAutoLinkExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
            if (!pipeline.InlineParsers.Contains<AutoLinkParser>())
                pipeline.InlineParsers.Insert(0, new CjkAutoLinkParser());
        }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
        }
    }

    /// <summary>網址遇到非 ASCII 的標點或空白就結束（網址裡的中文字照常保留，例如維基百科條目）；網址前面可以是全形標點</summary>
    private sealed class CjkAutoLinkParser : AutoLinkParser
    {
        /// <summary>Markdig 預設的「*_~(」，加上常放在網址前面的全形標點與全形空白</summary>
        private const string ValidPreviousCharacters = "*_~(：（「『【〈《〔，。、；！？“‘　";

        private static readonly string[] UrlPrefixes = { "http://", "https://", "ftp://", "mailto:", "tel:", "www." };

        public CjkAutoLinkParser()
            : base(new AutoLinkOptions { ValidPreviousCharacters = ValidPreviousCharacters })
        {
        }

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            // 是網址開頭才往後找：可讀範圍暫時截到第一個非 ASCII 標點或空白之前，Markdig 的網址就在那裡結束
            int end = slice.End;
            if (StartsWithUrlPrefix(slice))
            {
                for (int i = slice.Start; i <= end && slice.Text[i] > ' '; i++)
                {
                    if (!char.IsAscii(slice.Text[i]) && IsUrlTerminator(slice.Text[i]))
                    {
                        slice.End = i - 1;
                        break;
                    }
                }
            }

            try
            {
                return base.Match(processor, ref slice);
            }
            finally
            {
                slice.End = end;
            }
        }

        private static bool StartsWithUrlPrefix(StringSlice slice)
        {
            foreach (var prefix in UrlPrefixes)
            {
                if (slice.MatchLowercase(prefix))
                    return true;
            }
            return false;
        }

        private static bool IsUrlTerminator(char c) => char.GetUnicodeCategory(c) is
            UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.SpaceSeparator;
    }

    /// <summary>
    /// GitHub 風格提示區塊（&gt; [!NOTE]／[!TIP]／[!IMPORTANT]／[!WARNING]／[!CAUTION]）：
    /// 輸出純 &lt;blockquote class="md-alert md-alert-{slug}"&gt;&lt;p class="md-alert-title"&gt;&lt;strong&gt;{標題}&lt;/strong&gt;&lt;/p&gt;…&lt;/blockquote&gt;，
    /// 完全使用淨化器既有放行的元素（blockquote、p、strong）與 md-* 類別前綴，不必放寬 HTML 白名單
    /// </summary>
    private sealed class GitHubAlertExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline)
        {
        }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer htmlRenderer)
                return;
            htmlRenderer.ObjectRenderers.RemoveAll(r => r is AlertBlockRenderer);
            if (!htmlRenderer.ObjectRenderers.Contains<GitHubAlertBlockRenderer>())
                htmlRenderer.ObjectRenderers.Insert(0, new GitHubAlertBlockRenderer());
        }
    }

    private sealed class GitHubAlertBlockRenderer : HtmlObjectRenderer<AlertBlock>
    {
        protected override void Write(HtmlRenderer renderer, AlertBlock obj)
        {
            var rawKind = obj.Kind.ToString().ToLowerInvariant();
            var (slug, label) = rawKind switch
            {
                "tip" => ("tip", "提示"),
                "important" => ("important", "重要"),
                "warning" => ("warning", "警告"),
                "caution" => ("caution", "注意"),
                _ => ("note", "備註"),
            };

            renderer.EnsureLine();
            renderer.Write("<blockquote class=\"md-alert md-alert-").Write(slug).WriteLine("\">");
            renderer.Write("<p class=\"md-alert-title\"><strong>").Write(label).WriteLine("</strong></p>");
            var savedImplicitParagraph = renderer.ImplicitParagraph;
            renderer.ImplicitParagraph = false;
            renderer.WriteChildren(obj);
            renderer.ImplicitParagraph = savedImplicitParagraph;
            renderer.WriteLine("</blockquote>");
        }
    }
}
