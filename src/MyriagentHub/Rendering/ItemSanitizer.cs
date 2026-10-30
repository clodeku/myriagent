using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MyriagentHub.Rendering;

/// <summary>
/// 伺服器送給瀏覽器的 <c>html</c>／<c>detailHtml</c> 的白名單淨化（設計 8.4；WP4.2 計畫 D19、SEC-06、SEC-12）。
/// 輸入一律是共用渲染器（Markdig 停用原始 HTML）的輸出，這裡是第二道防線：嚴格的分詞器只認 Markdig 輸出的形狀——
/// 小寫的標籤與屬性名稱、屬性之間一個空白、雙引號的屬性值、<c> /&gt;</c> 只用在空元素、文字與屬性值裡的 <c>&amp;</c> 一定是
/// 合法的字元參照（數字參照，或 amp、lt、gt、quot、apos、nbsp）、文字裡沒有 <c>&gt;</c>、屬性值裡沒有 <c>&lt;</c>／<c>&gt;</c>。
/// 任何一處不合語法（註解、單引號或沒有引號的屬性、大寫、重複的屬性、未知的實體、太深的巢狀…）就整段退回純文字（<see cref="WebUtility.HtmlEncode"/>）。
/// 合語法時以白名單改寫：
/// <list type="bullet">
/// <item>白名單以外的標籤（script、svg、iframe…）整個標籤當成文字顯示（跳脫），不會變成元素；裡面的文字照常是文字。</item>
/// <item>屬性只留白名單的：<c>a[href|title]</c>、<c>img[src|alt|title]</c>、<c>class</c>（只留允許的值）、<c>ol[start]</c>、
/// <c>td|th[colspan|rowspan|style]</c>、核取方塊的 <c>type|disabled|checked</c>；其他（含所有 on* 事件屬性）一律拿掉。</item>
/// <item><c>href</c> 解碼實體、去掉控制字元與空白之後，只收 <c>https:</c>、<c>http:</c>、<c>mailto:</c>、<c>#</c>、本站的 <c>/m/</c>、<c>/f/</c>；
/// 不合格的拿掉 href、留文字。<c>src</c> 只收本站的 <c>/m/</c>、<c>/t/</c>，不合格的整個 img 換成 alt 文字。</item>
/// <item><c>a</c> 一律改成 <c>rel="noopener noreferrer" target="_blank"</c>（原本的 rel、target 拿掉）。</item>
/// <item>沒有閉合的元素補上結尾標籤，對不到的結尾標籤丟掉；核取方塊要有 <c>type="checkbox"</c> 與 <c>disabled</c>，否則整個拿掉。</item>
/// </list>
/// 冪等（淨化兩次等於一次）、純函式、執行緒安全。Markdig 的正常輸出除了 <c>a</c> 多出的 rel、target 之外原樣通過（ItemSanitizerTests）。
/// </summary>
internal static partial class ItemSanitizer
{
    /// <summary><c>a</c> 一律補上的屬性（設計 8.4）</summary>
    public const string LinkAttributes = " rel=\"noopener noreferrer\" target=\"_blank\"";

    /// <summary>
    /// 同時開著（還沒閉合）的元素層數上限：超過就整段退回純文字。Markdig 的巢狀上限是 128 層（更深的整段改用純文字顯示），
    /// 每層最多兩個元素（例如 blockquote＋p、li＋ul），所以正常的輸出到不了
    /// </summary>
    public const int MaxDepth = 512;

    /// <summary>設計 8.4 的標籤白名單</summary>
    public static IReadOnlySet<string> Tags { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "del", "code", "pre", "blockquote",
        "ul", "ol", "li", "table", "thead", "tbody", "tr", "th", "td", "a", "img", "span", "input",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "br", "hr", "img", "input" };

    /// <summary>
    /// 文字與屬性值裡認得的具名實體：Markdig 只輸出 amp、lt、gt、quot（其他實體原文解碼成字元），<see cref="WebUtility.HtmlEncode"/> 只輸出數字參照
    /// </summary>
    private static readonly HashSet<string> NamedReferences = new(StringComparer.Ordinal) { "amp", "lt", "gt", "quot", "apos", "nbsp" };

    /// <summary>標籤名稱的長度上限（白名單裡最長的是 blockquote）：更長的照樣是合法的語法，只是不會在白名單裡</summary>
    private const int MaxNameLength = 32;

    /// <summary>淨化；null 與空字串回空字串</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;
        return TryRewrite(html, out var result) ? result : WebUtility.HtmlEncode(html);
    }

    /// <summary>
    /// <c>href</c> 的值（屬性原文，含實體）可以留下：解碼實體、去掉控制字元與空白之後，是 https:、http:、mailto:（不分大小寫）、#，
    /// 或本站的 <c>/m/{ref}</c>、<c>/f/{ref}</c>
    /// </summary>
    public static bool IsAllowedHref(string value)
    {
        var url = Clean(value);
        return url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith('#')
               || LocalLink().IsMatch(url);
    }

    /// <summary><c>src</c> 的值（屬性原文）可以留下：解碼實體、去掉控制字元與空白之後，是本站的 <c>/m/{ref}</c> 或 <c>/t/{ref}</c></summary>
    public static bool IsAllowedSrc(string value) => LocalImage().IsMatch(Clean(value));

    /// <summary>class 的一個值可以留下（語言標記的字元同網頁 items.js 讀的 <c>language-([\w+#.-]+)</c>）</summary>
    public static bool IsAllowedClass(string value) => AllowedClass().IsMatch(value);

    /// <summary>解碼實體，去掉控制字元與空白（瀏覽器解析網址時會忽略 tab、換行，開頭結尾的控制字元與空白）</summary>
    private static string Clean(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var builder = new StringBuilder(decoded.Length);
        foreach (var c in decoded)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
                builder.Append(c);
        }
        return builder.ToString();
    }

    private static bool TryRewrite(string html, out string result)
    {
        result = string.Empty;
        var output = new StringBuilder(html.Length + 64);
        var open = new List<string>();
        var i = 0;
        while (i < html.Length)
        {
            var next = html.AsSpan(i).IndexOfAny('<', '>', '&');
            if (next < 0)
            {
                output.Append(html, i, html.Length - i);
                break;
            }

            output.Append(html, i, next);
            i += next;
            switch (html[i])
            {
                case '>':
                    // Markdig 的文字一定把 > 寫成 &gt;
                    return false;
                case '&':
                    var length = ReferenceLength(html, i, html.Length);
                    if (length == 0)
                        return false;
                    output.Append(html, i, length);
                    i += length;
                    break;
                default:
                    if (!TryReadTag(html, ref i, out var tag) || !Write(tag, output, open))
                        return false;
                    break;
            }
        }

        for (var k = open.Count - 1; k >= 0; k--)
            output.Append("</").Append(open[k]).Append('>');
        result = output.ToString();
        return true;
    }

    /// <summary>一個標籤；<see cref="Raw"/> 是原文（白名單以外的標籤照它跳脫成文字）</summary>
    private sealed record Tag(string Raw, string Name, bool End, bool SelfClosing, List<KeyValuePair<string, string>> Attributes)
    {
        public string? Get(string name) => Attributes.FirstOrDefault(a => a.Key == name).Value;
    }

    /// <summary>
    /// 從 <paramref name="i"/>（「&lt;」）讀一個標籤：<c>&lt;name( attr="value")*( /)?&gt;</c> 或 <c>&lt;/name&gt;</c>。
    /// 不合這個形狀回 false（呼叫端整段退回純文字）
    /// </summary>
    private static bool TryReadTag(string html, ref int i, out Tag tag)
    {
        tag = null!;
        var start = i;
        var p = i + 1;
        var end = p < html.Length && html[p] == '/';
        if (end)
            p++;
        if (!TryReadName(html, ref p, allowHyphen: false, out var name))
            return false;

        var attributes = new List<KeyValuePair<string, string>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (end)
        {
            if (p >= html.Length || html[p] != '>')
                return false;
            i = p + 1;
            tag = new Tag(html[start..i], name, End: true, SelfClosing: false, attributes);
            return true;
        }

        bool selfClosing;
        while (true)
        {
            if (p >= html.Length)
                return false;
            if (html[p] == '>')
            {
                p++;
                selfClosing = false;
                break;
            }

            if (html[p] != ' ')
                return false;
            p++;
            if (p < html.Length && html[p] == '/')
            {
                if (p + 1 >= html.Length || html[p + 1] != '>')
                    return false;
                p += 2;
                selfClosing = true;
                break;
            }

            if (!TryReadName(html, ref p, allowHyphen: true, out var attribute))
                return false;
            if (p + 1 >= html.Length || html[p] != '=' || html[p + 1] != '"')
                return false;
            p += 2;
            var close = html.IndexOf('"', p);
            if (close < 0 || !IsValidValue(html, p, close))
                return false;
            if (!names.Add(attribute))
                return false;
            attributes.Add(new KeyValuePair<string, string>(attribute, html[p..close]));
            p = close + 1;
        }

        i = p;
        tag = new Tag(html[start..i], name, End: false, selfClosing, attributes);
        return true;
    }

    /// <summary>標籤或屬性名稱：小寫英文字母開頭，接著小寫英文字母、數字（屬性另可有連字號）</summary>
    private static bool TryReadName(string html, ref int p, bool allowHyphen, out string name)
    {
        name = string.Empty;
        var start = p;
        if (p >= html.Length || html[p] is < 'a' or > 'z')
            return false;
        while (p < html.Length && (html[p] is >= 'a' and <= 'z' or >= '0' and <= '9' || (allowHyphen && html[p] == '-')))
            p++;
        if (p - start > MaxNameLength)
            return false;
        name = html[start..p];
        return true;
    }

    /// <summary>屬性值（雙引號之間）：沒有 &lt;、&gt;，&amp; 都是合法的字元參照</summary>
    private static bool IsValidValue(string html, int start, int end)
    {
        for (var q = start; q < end;)
        {
            switch (html[q])
            {
                case '<' or '>':
                    return false;
                case '&':
                    var length = ReferenceLength(html, q, end);
                    if (length == 0)
                        return false;
                    q += length;
                    break;
                default:
                    q++;
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// 從 <paramref name="i"/>（「&amp;」）開始的字元參照長度（含結尾的分號），不合格回 0：<c>&amp;#十進位;</c>（1～7 位）、
    /// <c>&amp;#x十六進位;</c>（1～6 位）、<see cref="NamedReferences"/> 的具名實體。不超過 <paramref name="limit"/>
    /// </summary>
    private static int ReferenceLength(string s, int i, int limit)
    {
        var p = i + 1;
        if (p < limit && s[p] == '#')
        {
            p++;
            var hex = p < limit && s[p] is 'x' or 'X';
            if (hex)
                p++;
            var digits = p;
            while (p < limit && p - digits < (hex ? 6 : 7) && (hex ? char.IsAsciiHexDigit(s[p]) : char.IsAsciiDigit(s[p])))
                p++;
            return p > digits && p < limit && s[p] == ';' ? p + 1 - i : 0;
        }

        var name = p;
        while (p < limit && p - name < 8 && char.IsAsciiLetterOrDigit(s[p]))
            p++;
        return p > name && p < limit && s[p] == ';' && NamedReferences.Contains(s[name..p]) ? p + 1 - i : 0;
    }

    /// <summary>照白名單寫出一個標籤；不合語法（非空元素自己閉合、太深）回 false</summary>
    private static bool Write(Tag tag, StringBuilder output, List<string> open)
    {
        if (!Tags.Contains(tag.Name))
        {
            output.Append(WebUtility.HtmlEncode(tag.Raw));
            return true;
        }

        var isVoid = VoidTags.Contains(tag.Name);
        if (tag.End)
        {
            // 空元素的結尾標籤、對不到開頭的結尾標籤都丟掉；對到的把中間沒閉合的一併補上結尾
            var index = isVoid ? -1 : open.LastIndexOf(tag.Name);
            if (index < 0)
                return true;
            for (var k = open.Count - 1; k >= index; k--)
                output.Append("</").Append(open[k]).Append('>');
            open.RemoveRange(index, open.Count - index);
            return true;
        }

        if (tag.SelfClosing && !isVoid)
            return false;

        switch (tag.Name)
        {
            case "img":
                WriteImage(tag, output);
                return true;
            case "input":
                WriteCheckbox(tag, output);
                return true;
            case "br" or "hr":
                output.Append('<').Append(tag.Name).Append(tag.SelfClosing ? " />" : ">");
                return true;
        }

        if (open.Count >= MaxDepth)
            return false;
        output.Append('<').Append(tag.Name);
        foreach (var (name, value) in tag.Attributes)
        {
            if (Keep(tag.Name, name, value) is { } kept)
                output.Append(' ').Append(name).Append("=\"").Append(kept).Append('"');
        }

        if (tag.Name == "a")
            output.Append(LinkAttributes);
        output.Append('>');
        open.Add(tag.Name);
        return true;
    }

    /// <summary>非空元素的一個屬性：留下回要寫出的值（原文，或 class 過濾後的值），拿掉回 null</summary>
    private static string? Keep(string tag, string name, string value)
    {
        switch (name)
        {
            case "class":
                var kept = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(IsAllowedClass));
                return kept.Length > 0 ? kept : null;
            case "href":
                return tag == "a" && IsAllowedHref(value) ? value : null;
            case "title":
                return tag == "a" ? value : null;
            case "start":
                return tag == "ol" && ListStart().IsMatch(value) ? value : null;
            case "colspan" or "rowspan":
                return tag is "td" or "th" && CellSpan().IsMatch(value) ? value : null;
            case "style":
                return tag is "td" or "th" && CellAlign().IsMatch(value) ? value : null;
            default:
                return null;
        }
    }

    /// <summary>img：src 合格時只留 src、alt、title；不合格（或沒有 src）的整個換成 alt 文字（屬性值本身就是跳脫過的文字）</summary>
    private static void WriteImage(Tag tag, StringBuilder output)
    {
        if (tag.Get("src") is not { } src || !IsAllowedSrc(src))
        {
            output.Append(tag.Get("alt"));
            return;
        }

        output.Append("<img");
        foreach (var (name, value) in tag.Attributes)
        {
            if (name is "src" or "alt" or "title")
                output.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
        }
        output.Append(tag.SelfClosing ? " />" : ">");
    }

    /// <summary>待辦清單的核取方塊（Markdig 的 TaskList）：要有 type="checkbox" 與 disabled，否則整個拿掉；只留 type、disabled、checked</summary>
    private static void WriteCheckbox(Tag tag, StringBuilder output)
    {
        if (tag.Get("type") != "checkbox" || tag.Get("disabled") is not ("disabled" or "")
            || tag.Get("checked") is { } check && check is not ("checked" or ""))
            return;

        output.Append("<input");
        foreach (var (name, value) in tag.Attributes)
        {
            if (name is "type" or "disabled" or "checked")
                output.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
        }
        output.Append(tag.SelfClosing ? " />" : ">");
    }

    /// <summary>本站的連結：/m/{ref}（媒體）、/f/{ref}（預覽或下載）；ref 只有英數字、底線、連字號，可以有簡單的查詢字串（沒有句點與斜線，不能往上層走）</summary>
    [GeneratedRegex(@"^/[mf]/[A-Za-z0-9_-]+(?:\?[A-Za-z0-9_=&-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalLink();

    /// <summary>本站的圖片：/m/{ref}（媒體）、/t/{ref}（縮圖，例如 ?s=160）</summary>
    [GeneratedRegex(@"^/[mt]/[A-Za-z0-9_-]+(?:\?[A-Za-z0-9_=&-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalImage();

    /// <summary>
    /// 設計 8.4 的 <c>language-*</c>、<c>md-*</c>，加上 Markdig 待辦清單的 <c>task-list-item</c>、<c>contains-task-list</c>（D19）
    /// </summary>
    [GeneratedRegex(@"^(?:language-[A-Za-z0-9_+#.-]{1,32}|md-[a-z0-9-]{1,32}|task-list-item|contains-task-list)$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedClass();

    /// <summary>有序清單的起始編號（Markdig 不是從 1 開始時輸出；CommonMark 最多 9 位數）</summary>
    [GeneratedRegex(@"^[0-9]{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex ListStart();

    /// <summary>colspan、rowspan：1～100</summary>
    [GeneratedRegex(@"^(?:[1-9][0-9]?|100)$", RegexOptions.CultureInvariant)]
    private static partial Regex CellSpan();

    /// <summary>表格欄的對齊（Markdig 的寫法）</summary>
    [GeneratedRegex(@"^text-align: (?:left|right|center);$", RegexOptions.CultureInvariant)]
    private static partial Regex CellAlign();
}
