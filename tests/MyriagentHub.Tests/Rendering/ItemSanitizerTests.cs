using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Rendering;
using Myriagent.Tests.Rendering;
using MyriagentHub.Rendering;

namespace MyriagentHub.Tests.Rendering;

/// <summary>
/// 伺服器端的 HTML 白名單淨化（WP4.2 計畫步 6、D19；SEC-06、SEC-12）。
/// 正向：桌面的渲染語料（DT 的 ItemRendererCorpus，只讀連結）以伺服器形狀的網址渲染後，淨化前後除了 a 補的 rel、target 之外逐字相同；
/// 反向：XSS 語料（ItemSanitizerXssCorpus，≥200 筆）淨化後，以測試裡獨立的正規式檢查器確認沒有可執行的內容。
/// </summary>
public sealed class ItemSanitizerTests
{
    private const string Rel = ItemSanitizer.LinkAttributes;

    public static TheoryData<string> XssNames => new(ItemSanitizerXssCorpus.All.Select(c => c.Name));

    // ───────────────────────────── 正向：Markdig 的正常輸出原樣通過 ─────────────────────────────

    [Fact]
    public void 桌面語料以伺服器形狀的網址渲染_淨化前後相同()
    {
        var urls = new ServerShapedUrls();
        var html = new List<string>();
        foreach (var corpus in ItemRendererCorpus.All())
        {
            var renderer = new ItemRenderer(urls);
            foreach (var item in corpus.Items)
            {
                var rendered = renderer.Render(item, corpus.WorkingDirectory);
                foreach (var key in new[] { "html", "detailHtml" })
                {
                    if (rendered[key]?.Type != Newtonsoft.Json.Linq.JTokenType.String)
                        continue;
                    var original = (string)rendered[key]!;
                    Unlink(ItemSanitizer.Sanitize(original)).Should().Be(original, $"{item.Id} 的 {key}");
                    html.Add(original);
                }
            }
        }

        // 確認語料真的涵蓋了白名單的各種形狀（不是全部都是純文字）
        var all = string.Concat(html);
        foreach (var shape in new[] { "<a href=\"#\">", "<a href=\"https://", "<a href=\"mailto:", "<img src=\"/m/test\"", "<table>", "<th>", "<pre><code class=\"language-",
                     "<ul class=\"contains-task-list\">", "<li class=\"task-list-item\"><input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" />",
                     "<blockquote>", "<del>", "<em>", "<strong>", "<h3>", "<h4>", "<ol>", "<br />", "<br>", "&amp;", "&lt;", "&#39;", "&quot;" })
            all.Should().Contain(shape);
    }

    public static TheoryData<string, string> MarkdigSamples => new()
    {
        { "有序清單的起始編號", "3. 第三\n4. 第四" },
        { "表格的對齊", "| 左 | 中 | 右 | 無 |\n|:---|:---:|---:|---|\n| a | b | c | d |" },
        { "六級標題", "# 一\n## 二\n### 三\n#### 四\n##### 五\n###### 六" },
        { "分隔線", "前\n\n---\n\n後" },
        { "圖片與連結的標題", "![圖](C:\\repo\\a.png \"圖的標題\") [連結](https://example.com \"連結的標題\")" },
        { "巢狀引用", "> 一\n>> 二\n>>> 三" },
        { "縮排的程式碼", "    縮排的程式碼\n    第二行 <b>&amp;</b>" },
        { "大寫與井號的語言", "```C#\nvar x = 1;\n```" },
        { "連字號的語言", "```objective-c\nx\n```" },
        { "句點的語言", "```vb.net\nx\n```" },
        { "硬換行", "a  \nb\\\nc" },
        { "強調", "**粗** *斜* ~~刪~~ `碼` ***粗斜***" },
        { "角括號的自動連結", "<a@b.c> <https://example.com/?a=1&b=2>" },
        { "待辦與巢狀清單", "- [ ] 待辦\n- [x] 完成\n  1. 巢狀\n  2. 第二" },
        { "深的引用", new string('>', 100) + " 深" },
        { "實體", "&copy; &#x1F600; &lt;b&gt; &quot;引號&quot; R&D" },
        { "危險連結已改成井號", "[x](javascript:alert(1)) <javascript:alert(1)> ![y](javascript:alert(2))" },
        { "中文網址與www", "https://example.com/中文 與 www.example.com" },
        { "表格裡的程式碼與跳脫的直線", "| a | b |\n|---|---|\n| `x \\| y` | <c> |" },
        { "原始HTML是文字", "<script>alert(1)</script> <img src=x onerror=alert(1)>" },
        { "本機檔案連結", "[檔案](C:\\repo\\a.cs) [相對](docs/b.md#L3) ![相對圖](assets/c.png)" },
    };

    [Theory]
    [MemberData(nameof(MarkdigSamples))]
    public void Markdig的其他輸出_淨化前後相同(string name, string markdown)
    {
        var html = AgentMarkdownRenderer.ToHtml(markdown, null, @"C:\repo\myriagent", new ServerShapedUrls());
        html.Should().NotBeEmpty(name);

        Unlink(ItemSanitizer.Sanitize(html)).Should().Be(html, name);
    }

    [Fact]
    public void Markdig樣本涵蓋對齊與起始編號()
    {
        Render("3. 第三").Should().Contain("<ol start=\"3\">");
        var table = Render("| 左 | 中 | 右 |\n|:---|:---:|---:|\n| a | b | c |");
        table.Should().Contain("<th style=\"text-align: left;\">").And.Contain("<th style=\"text-align: center;\">").And.Contain("<td style=\"text-align: right;\">");
        Render("```C#\nx\n```").Should().Contain("class=\"language-C#\"");

        static string Render(string markdown) => ItemSanitizer.Sanitize(AgentMarkdownRenderer.ToHtml(markdown, null, null, new ServerShapedUrls()));
    }

    // ───────────────────────────── 反向：XSS 語料 ─────────────────────────────

    [Fact]
    public void XSS語料至少200筆_名稱不重複()
    {
        ItemSanitizerXssCorpus.All.Should().HaveCountGreaterThanOrEqualTo(200);
        ItemSanitizerXssCorpus.All.Select(c => c.Name).Should().OnlyHaveUniqueItems();
        ItemSanitizerXssCorpus.All.Select(c => c.Html).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// 步 15：XSS 語料淨化後的輸出寫成黃金檔 hub-xss.json，給 tools/ui-probe/conv-probe.mjs 當成 Item 的 html、detailHtml 送進真的 Chrome
    /// （SEC-06、SEC-12 的瀏覽器一半）。刻意改了語料或淨化器時以 MYRIAGENT_GOLDEN_RECORD=1 重錄
    /// </summary>
    [Fact]
    public void XSS語料淨化後的黃金檔逐位元組相同()
    {
        var golden = new Newtonsoft.Json.Linq.JObject
        {
            ["description"] = "ItemSanitizerXssCorpus 每一筆經 ItemSanitizer.Sanitize 之後的輸出（WP4.2 步 15）：conv-probe.mjs 把 html 放進 Item 的 html 與 detailHtml，"
                              + "在真的瀏覽器裡確認沒有任何腳本執行、沒有 CSP 違規",
            ["cases"] = new Newtonsoft.Json.Linq.JArray(ItemSanitizerXssCorpus.All.Select(c => new Newtonsoft.Json.Linq.JObject
            {
                ["name"] = c.Name,
                ["html"] = ItemSanitizer.Sanitize(c.Html),
            })),
        };

        HubGoldenFile.Verify("hub-xss", golden);
    }

    [Theory]
    [MemberData(nameof(XssNames))]
    public void XSS語料逐筆_沒有可執行的內容(string name)
    {
        var input = ItemSanitizerXssCorpus.Get(name).Html;

        var output = ItemSanitizer.Sanitize(input);

        SafeHtmlChecker.AssertSafe(output, name);
        ItemSanitizer.Sanitize(output).Should().Be(output, $"{name} 淨化兩次要等於一次");
    }

    [Fact]
    public void 危險網址的href拿掉_img換成alt()
    {
        for (var i = 0; i < ItemSanitizerXssCorpus.DangerousUrls.Count; i++)
        {
            var url = ItemSanitizerXssCorpus.DangerousUrls[i];
            var link = $"<a href=\"{url}\">點我</a>";
            var image = $"<img src=\"{url}\" alt=\"圖\" />";
            var safeLink = ItemSanitizer.Sanitize(link);
            var safeImage = ItemSanitizer.Sanitize(image);

            // 語法不合格（未知的實體 &colon; 等）的整段退回純文字，其他的只拿掉 href、img 換成 alt
            if (safeLink != WebUtility.HtmlEncode(link))
                safeLink.Should().Be($"<a{Rel}>點我</a>", url);
            if (safeImage != WebUtility.HtmlEncode(image))
                safeImage.Should().Be("圖", url);
        }
    }

    // ───────────────────────────── 突變敏感的單例 ─────────────────────────────

    [Theory]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("&#106;avascript:alert(1)")]
    [InlineData("&#x6A;avascript:alert(1)")]
    [InlineData("&#X4A;AVASCRIPT:alert(1)")]
    [InlineData("java&#x09;script:alert(1)")]
    [InlineData("java&#10;script:alert(1)")]
    [InlineData("\tjavascript:alert(1)")]
    [InlineData("vBsCrIpT:msgbox(1)")]
    [InlineData("&#100;ata:text/html,x")]
    public void 大小寫與實體編碼的javascript協定都擋(string url)
    {
        ItemSanitizer.Sanitize($"<p><a href=\"{url}\">x</a></p>").Should().Be($"<p><a{Rel}>x</a></p>");
        ItemSanitizer.Sanitize($"<p><img src=\"{url}\" alt=\"圖\" /></p>").Should().Be("<p>圖</p>");
    }

    [Theory]
    [InlineData("HTTPS://example.com/a")]
    [InlineData("Http://example.com/a")]
    [InlineData("MAILTO:a@b.c")]
    [InlineData("&#104;ttps://example.com/a")]
    [InlineData("&#x68;ttps://example.com/a")]
    [InlineData("ht&#x09;tps://example.com/a")]
    [InlineData(" https://example.com/a")]
    [InlineData("#&#x41;")]
    public void 大寫與實體編碼的合格連結照收(string url)
    {
        // 協定的大小寫、實體、空白與控制字元先正規化再比對；寫出的仍是原文（瀏覽器的解讀與這裡相同）
        var html = $"<a href=\"{url}\">x</a>";
        ItemSanitizer.Sanitize(html).Should().Be($"<a href=\"{url}\"{Rel}>x</a>");
    }

    [Theory]
    [InlineData("/m/abcDEF012_-")]
    [InlineData("/f/abc")]
    [InlineData("/m/abc?x=1&amp;y=2")]
    public void 本站的連結照收(string url)
        => ItemSanitizer.Sanitize($"<a href=\"{url}\">x</a>").Should().Be($"<a href=\"{url}\"{Rel}>x</a>");

    [Theory]
    [InlineData("/t/abc")]
    [InlineData("/m/../api")]
    [InlineData("/m/a/b")]
    [InlineData("/m/a.b")]
    [InlineData("/m/%2e%2e")]
    [InlineData("//evil.example")]
    [InlineData("/m/")]
    [InlineData("m/abc")]
    [InlineData("/api/v1/ops/status")]
    public void 本站以外或往上層走的連結_拿掉href(string url)
        => ItemSanitizer.Sanitize($"<a href=\"{url}\">x</a>").Should().Be($"<a{Rel}>x</a>");

    [Theory]
    [InlineData("/m/abc", true)]
    [InlineData("/t/abc?s=160", true)]
    [InlineData("/f/abc", false)]
    [InlineData("#", false)]
    [InlineData("https://example.com/x.png", false)]
    [InlineData("/m/../x", false)]
    public void img只收本站的媒體與縮圖(string src, bool kept)
    {
        var html = $"<img src=\"{src}\" alt=\"說明\" />";
        ItemSanitizer.Sanitize(html).Should().Be(kept ? html : "說明");
    }

    [Fact]
    public void 外站img_換成alt文字()
    {
        ItemSanitizer.Sanitize("<p><img src=\"https://evil.example/x.png?d=secret\" alt=\"說明\" /></p>").Should().Be("<p>說明</p>");
        ItemSanitizer.Sanitize("<p>前<img src=\"data:image/png;base64,AAAA\" alt=\"a &amp; &lt;b&gt;\" title=\"t\" />後</p>").Should().Be("<p>前a &amp; &lt;b&gt;後</p>");
        ItemSanitizer.Sanitize("<p><img alt=\"沒有 src\" /></p>").Should().Be("<p>沒有 src</p>");
        ItemSanitizer.Sanitize("<p><img src=\"https://evil.example/x.png\" /></p>").Should().Be("<p></p>");
        ItemSanitizer.Sanitize("<img src=\"/m/ok\" alt=\"本站\" title=\"標題\" onerror=\"alert(1)\" />").Should().Be("<img src=\"/m/ok\" alt=\"本站\" title=\"標題\" />");
    }

    [Fact]
    public void class只留允許的()
    {
        ItemSanitizer.Sanitize("<code class=\"language-js evil md-x hljs task-list-item\">x</code>").Should().Be("<code class=\"language-js md-x task-list-item\">x</code>");
        ItemSanitizer.Sanitize("<ul class=\"contains-task-list\"><li class=\"task-list-item\">x</li></ul>").Should().Be("<ul class=\"contains-task-list\"><li class=\"task-list-item\">x</li></ul>");
        ItemSanitizer.Sanitize("<span class=\"evil other\">x</span>").Should().Be("<span>x</span>");
        ItemSanitizer.Sanitize("<span class=\"md-UPPER language-\">x</span>").Should().Be("<span>x</span>");
        ItemSanitizer.Sanitize("<code class=\"language-" + new string('a', 33) + "\">x</code>").Should().Be("<code>x</code>");
        ItemSanitizer.Sanitize("<code class=\"language-&quot;x\">x</code>").Should().Be("<code>x</code>");
    }

    [Fact]
    public void a一律補rel與target_原有的target被覆寫()
    {
        ItemSanitizer.Sanitize("<a href=\"https://example.com\" target=\"_self\" rel=\"opener\">x</a>").Should().Be($"<a href=\"https://example.com\"{Rel}>x</a>");
        ItemSanitizer.Sanitize("<a href=\"#\">x</a>").Should().Be($"<a href=\"#\"{Rel}>x</a>");
        ItemSanitizer.Sanitize("<a href=\"https://example.com\" title=\"說明\">x</a>").Should().Be($"<a href=\"https://example.com\" title=\"說明\"{Rel}>x</a>");
        ItemSanitizer.Sanitize("<a>沒有 href</a>").Should().Be($"<a{Rel}>沒有 href</a>");
    }

    [Fact]
    public void 未閉合的標籤補齊()
    {
        ItemSanitizer.Sanitize("<p><strong>粗").Should().Be("<p><strong>粗</strong></p>");
        ItemSanitizer.Sanitize("<ul><li>a</ul>").Should().Be("<ul><li>a</li></ul>");
        ItemSanitizer.Sanitize("<p><em>a</p>b").Should().Be("<p><em>a</em></p>b");
        ItemSanitizer.Sanitize("<a href=\"#\">x").Should().Be($"<a href=\"#\"{Rel}>x</a>");
        ItemSanitizer.Sanitize("</strong>沒有開頭</p>").Should().Be("沒有開頭");
        ItemSanitizer.Sanitize("<br></br><hr />").Should().Be("<br><hr />");
    }

    [Theory]
    [InlineData("<p>a <b onclick=x>b</b></p>")]
    [InlineData("<a href='#'>x</a>")]
    [InlineData("<!-- 註解 --><p>x</p>")]
    [InlineData("<P>x</P>")]
    [InlineData("<p>a > b</p>")]
    [InlineData("<p>a & b</p>")]
    [InlineData("<p>&copy;</p>")]
    [InlineData("<p title=\"a\" title=\"b\">x</p>")]
    [InlineData("<p />")]
    [InlineData("<br/>")]
    [InlineData("<p  class=\"x\">x</p>")]
    [InlineData("<p>未完<")]
    public void 不合語法_退回純文字(string html)
        => ItemSanitizer.Sanitize(html).Should().Be(WebUtility.HtmlEncode(html));

    [Fact]
    public void 白名單以外的標籤當成文字_內容照留()
    {
        ItemSanitizer.Sanitize("<p>前<script>alert(1)</script>後</p>").Should().Be("<p>前&lt;script&gt;alert(1)&lt;/script&gt;後</p>");
        ItemSanitizer.Sanitize("<svg onload=\"alert(1)\"><a href=\"javascript:x\">y</a></svg>")
            .Should().Be($"&lt;svg onload=&quot;alert(1)&quot;&gt;<a{Rel}>y</a>&lt;/svg&gt;");
        ItemSanitizer.Sanitize("<div><p>x</p></div>").Should().Be("&lt;div&gt;<p>x</p>&lt;/div&gt;");
    }

    [Fact]
    public void 核取方塊要有type與disabled()
    {
        ItemSanitizer.Sanitize("<input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" />").Should().Be("<input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" />");
        ItemSanitizer.Sanitize("<input disabled=\"disabled\" type=\"checkbox\" onclick=\"x\" name=\"n\" />").Should().Be("<input disabled=\"disabled\" type=\"checkbox\" />");
        ItemSanitizer.Sanitize("<input type=\"checkbox\" />").Should().BeEmpty();
        ItemSanitizer.Sanitize("<input disabled=\"disabled\" type=\"text\" />").Should().BeEmpty();
        ItemSanitizer.Sanitize("<input disabled=\"disabled\" type=\"checkbox\" checked=\"no\" />").Should().BeEmpty();
        ItemSanitizer.Sanitize("<input disabled=\"x\" type=\"checkbox\" />").Should().BeEmpty();
    }

    [Fact]
    public void 表格的對齊與合併照收_其他style拿掉()
    {
        ItemSanitizer.Sanitize("<td style=\"text-align: center;\" colspan=\"2\" rowspan=\"100\">x</td>").Should().Be("<td style=\"text-align: center;\" colspan=\"2\" rowspan=\"100\">x</td>");
        ItemSanitizer.Sanitize("<td style=\"text-align: left; color: red;\" colspan=\"0\" rowspan=\"101\">x</td>").Should().Be("<td>x</td>");
        ItemSanitizer.Sanitize("<p style=\"text-align: left;\">x</p>").Should().Be("<p>x</p>");
        ItemSanitizer.Sanitize("<ol start=\"12\"><li>x</li></ol>").Should().Be("<ol start=\"12\"><li>x</li></ol>");
        ItemSanitizer.Sanitize("<ul start=\"12\"><li>x</li></ul>").Should().Be("<ul><li>x</li></ul>");
        ItemSanitizer.Sanitize("<ol start=\"-1\"><li>x</li></ol>").Should().Be("<ol><li>x</li></ol>");
    }

    [Fact]
    public void 巢狀超過上限_退回純文字_上限內照常補齊()
    {
        var deep = string.Concat(Enumerable.Repeat("<blockquote>", ItemSanitizer.MaxDepth + 1)) + "x";
        ItemSanitizer.Sanitize(deep).Should().Be(WebUtility.HtmlEncode(deep));

        var ok = string.Concat(Enumerable.Repeat("<blockquote>", ItemSanitizer.MaxDepth)) + "x";
        ItemSanitizer.Sanitize(ok).Should().Be(ok + string.Concat(Enumerable.Repeat("</blockquote>", ItemSanitizer.MaxDepth)));
    }

    [Fact]
    public void null與空字串回空字串()
    {
        ItemSanitizer.Sanitize(null).Should().BeEmpty();
        ItemSanitizer.Sanitize(string.Empty).Should().BeEmpty();
        ItemSanitizer.Sanitize("純文字 &amp; 控制字元" + (char)1 + (char)27).Should().Be("純文字 &amp; 控制字元" + (char)1 + (char)27);
    }

    [Fact]
    public void 冪等_淨化兩次等於一次()
    {
        foreach (var html in AllInputs())
        {
            var once = ItemSanitizer.Sanitize(html);
            ItemSanitizer.Sanitize(once).Should().Be(once, Short(html));
        }
    }

    [Fact]
    public void 隨機片段_檢查器通過而且冪等()
    {
        foreach (var html in Fuzz())
        {
            var once = ItemSanitizer.Sanitize(html);
            SafeHtmlChecker.AssertSafe(once, Short(html));
            ItemSanitizer.Sanitize(once).Should().Be(once, Short(html));
        }
    }

    [Fact]
    public void 輸出長度不超過輸入兩倍加常數()
    {
        // Markdig 的正常輸出：多出來的只有每個 a 的 rel、target
        foreach (var html in DesktopHtml().Concat(MarkdigSamples.Select(row => AgentMarkdownRenderer.ToHtml((string)row[1], null, null, new ServerShapedUrls()))))
            ItemSanitizer.Sanitize(html).Length.Should().BeLessThanOrEqualTo(2 * html.Length + 128, Short(html));
    }

    [Fact]
    public void 病態輸入的輸出長度仍是線性()
    {
        // 最壞的是一連串沒閉合的 <a>（3 個字元 → 補上 rel、target 與結尾標籤共 49 個字元）；退回純文字最多 6 倍（&quot;）
        foreach (var html in AllInputs().Concat(Fuzz()))
            ItemSanitizer.Sanitize(html).Length.Should().BeLessThanOrEqualTo(17 * html.Length + 64, Short(html));
        ItemSanitizer.Sanitize(string.Concat(Enumerable.Repeat("<a>", 100))).Length.Should().Be(49 * 100);
    }

    // ───────────────────────────── 輔助 ─────────────────────────────

    /// <summary>淨化後去掉 a 補的 rel、target（和 Markdig 的原輸出比對用）</summary>
    private static string Unlink(string html) => html.Replace(Rel, string.Empty, StringComparison.Ordinal);

    private static string Short(string html) => html.Length <= 120 ? html : html[..120] + "…";

    private static IEnumerable<string> DesktopHtml()
    {
        var urls = new ServerShapedUrls();
        foreach (var corpus in ItemRendererCorpus.All())
        {
            var renderer = new ItemRenderer(urls);
            foreach (var item in corpus.Items)
            {
                var rendered = renderer.Render(item, corpus.WorkingDirectory);
                foreach (var key in new[] { "html", "detailHtml" })
                {
                    if (rendered[key]?.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        yield return (string)rendered[key]!;
                }
            }
        }
    }

    private static IEnumerable<string> AllInputs() => ItemSanitizerXssCorpus.All.Select(c => c.Html).Concat(DesktopHtml());

    /// <summary>以固定種子從 HTML 片段隨機組出的字串（3,000 筆、每筆 1～40 段）</summary>
    private static IEnumerable<string> Fuzz()
    {
        var pieces = new[]
        {
            "<p>", "</p>", "<a href=\"#\">", "</a>", "<a href=\"javascript:alert(1)\">", "<img src=\"/m/x\" alt=\"a\" />", "<img src=\"x\" alt=\"b\" />",
            "<script>", "</script>", "&amp;", "&", "<", ">", "\"", "'", "x", "中", "<br />", "<br>", "<ul>", "<li>", "</li>", "</ul>",
            "<td style=\"text-align: left;\">", "<td style=\"color:red\">", "<code class=\"language-js evil\">", "</code>", " onerror=\"x\"", " ",
            "&#106;", "&#x3C;", "<svg onload=\"alert(1)\">", "</svg>", "<input disabled=\"disabled\" type=\"checkbox\" />", "<input type=\"checkbox\">",
            "<!--", "-->", "<blockquote>", "</blockquote>", "<strong>", "</em>", "<P>", "<a href=\"/m/x\">", "<a title=\"t\" href=\"https://x\">",
            "\n", ((char)0).ToString(), "<table>", "</table>", "<ol start=\"3\">", "</ol>", "&lt;", "&bogus;",
        };
        var random = new Random(42);
        for (var i = 0; i < 3_000; i++)
        {
            var builder = new StringBuilder();
            var count = random.Next(1, 41);
            for (var k = 0; k < count; k++)
                builder.Append(pieces[random.Next(pieces.Length)]);
            yield return builder.ToString();
        }
    }

    /// <summary>伺服器形狀的網址（計畫步 6）：Markdown 的本機圖片回 /m/test、本機連結回 #；附件與媒體不進 html</summary>
    private sealed class ServerShapedUrls : IMediaUrlBuilder
    {
        public string MediaUrl(string path) => "/m/test";

        public string ThumbUrl(string path, int size, string? version = null) => "/t/test";

        public string MarkdownImageUrl(string path) => "/m/test";

        public string MarkdownLinkUrl(string path) => "#";

        public void AllowMarkdownImage(string path)
        {
        }
    }
}

/// <summary>
/// 測試裡獨立的檢查器（不呼叫 ItemSanitizer 的任何規則）：把輸出拆成標籤與文字，標籤只能是白名單、屬性只能是各標籤允許的、
/// 沒有 on* 屬性、href 與 src 只有允許的開頭、style 只有 text-align；拆掉標籤之後的文字不能再有 &lt; 或 &gt;
/// </summary>
internal static class SafeHtmlChecker
{
    private static readonly Regex TagToken = new(@"<(/?)([a-z][a-z0-9]*)((?: [a-z][a-z0-9-]*=""[^""<>]*"")*)( /)?>", RegexOptions.CultureInvariant);

    private static readonly Regex AttributeToken = new(@" ([a-z][a-z0-9-]*)=""([^""<>]*)""", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Allowed =
    [
        "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "del", "code", "pre", "blockquote",
        "ul", "ol", "li", "table", "thead", "tbody", "tr", "th", "td", "a", "img", "span", "input",
    ];

    public static void AssertSafe(string html, string because)
    {
        var text = TagToken.Replace(html, m =>
        {
            CheckTag(m, because);
            return string.Empty;
        });
        text.Should().NotContain("<", because).And.NotContain(">", because);
    }

    private static void CheckTag(Match tag, string because)
    {
        var name = tag.Groups[2].Value;
        Allowed.Should().Contain(name, because);
        var attributes = AttributeToken.Matches(tag.Groups[3].Value).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        if (tag.Groups[1].Value == "/")
        {
            attributes.Should().BeEmpty(because);
            return;
        }

        foreach (var (attribute, value) in attributes)
        {
            attribute.Should().NotStartWith("on", because);
            var ok = (name, attribute) switch
            {
                ("a", "href") => Regex.IsMatch(Url(value), @"^(?:(?i:https?:|mailto:)|#|/[mf]/[A-Za-z0-9_-]+(?:\?[A-Za-z0-9_=&-]*)?$)"),
                ("a", "rel") => value == "noopener noreferrer",
                ("a", "target") => value == "_blank",
                ("a" or "img", "title") => true,
                ("img", "src") => Regex.IsMatch(Url(value), @"^/[mt]/[A-Za-z0-9_-]+(?:\?[A-Za-z0-9_=&-]*)?$"),
                ("img", "alt") => true,
                ("input", "type") => value == "checkbox",
                ("input", "disabled") => value is "disabled" or "",
                ("input", "checked") => value is "checked" or "",
                ("ol", "start") => Regex.IsMatch(value, "^[0-9]{1,9}$"),
                ("td" or "th", "colspan" or "rowspan") => int.TryParse(value, out var span) && span is >= 1 and <= 100,
                ("td" or "th", "style") => Regex.IsMatch(value, "^text-align: (?:left|right|center);$"),
                (not ("br" or "hr" or "img" or "input"), "class") => value.Split(' ').All(c =>
                    Regex.IsMatch(c, @"^(?:language-[A-Za-z0-9_+#.-]{1,32}|md-[a-z0-9-]{1,32}|task-list-item|contains-task-list)$")),
                _ => false,
            };
            ok.Should().BeTrue($"{because}：<{name}> 的 {attribute}=\"{(value.Length > 80 ? value[..80] + "…" : value)}\"");
        }

        if (name == "a")
        {
            attributes.Should().ContainKey("rel", because).And.ContainKey("target", because);
        }
        if (name == "img")
            attributes.Should().ContainKey("src", because);
        if (name == "input")
            attributes.Should().ContainKey("disabled", because).And.ContainKey("type", because);
    }

    /// <summary>瀏覽器看到的網址：解碼實體，去掉空白與控制字元</summary>
    private static string Url(string value) => new(WebUtility.HtmlDecode(value).Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
}
