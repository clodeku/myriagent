using Myriagent.Agents;
using Myriagent.Rendering;

namespace Myriagent.Tests.Agents;

/// <summary>代理回覆的 Markdown → HTML：停用原始 HTML、危險連結中和、外站圖片不自動載入、本機路徑改走 /media 與 /open</summary>
public class AgentMarkdownRendererTests
{
    [Fact]
    public void 原始HTML一律當文字()
    {
        var html = AgentMarkdownRenderer.ToHtml("哈囉 <script>alert(1)</script> <img src=x onerror=alert(1)>");
        html.Should().NotContain("<script>");
        html.Should().NotContain("<img");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Javascript與Data連結被中和()
    {
        var html = AgentMarkdownRenderer.ToHtml("[點我](javascript:alert(1)) [資料](data:text/html,hi) <javascript:alert(2)> ![x](javascript:alert(3))");
        html.Should().NotContain("href=\"javascript");
        html.Should().NotContain("src=\"javascript");
        html.Should().NotContain("href=\"data:");
        html.Should().NotContain("<img");
    }

    [Fact]
    public void 外站圖片改成連結_不自動載入()
    {
        var html = AgentMarkdownRenderer.ToHtml("![秘密](https://evil.example/x.png?d=secret)");
        html.Should().NotContain("<img");
        html.Should().Contain("href=\"https://evil.example/x.png?d=secret\"");

        AgentMarkdownRenderer.ToHtml("![](https://evil.example/y.png)").Should().Contain("外部圖片：https://evil.example/y.png", "沒有替代文字時要讓使用者看得到是哪張圖");
    }

    [Fact]
    public void 本機圖片走媒體端點並回報路徑_本機連結走Open端點()
    {
        var images = new List<string>();
        var html = AgentMarkdownRenderer.ToHtml(@"結果：![圖表](C:/out/chart.png) 與 [報表](C:\out\r.xlsx)", images);

        images.Should().Equal(@"C:\out\chart.png");
        html.Should().Contain("src=\"https://myriagent.local/media?path=C%3A%5Cout%5Cchart.png\"");
        html.Should().Contain("href=\"https://myriagent.local/open?path=C%3A%5Cout%5Cr.xlsx\"");
    }

    [Theory]
    [InlineData(@"![shot](C:\repo\app\.screenshots\home.png)", @"C:\repo\app\.screenshots\home.png")]
    [InlineData(@"![x](C:\repo\_site\img\logo.png)", @"C:\repo\_site\img\logo.png")]
    [InlineData(@"![x](C:\repo\node\@types\a.png)", @"C:\repo\node\@types\a.png")]
    [InlineData(@"![x](C:\tmp\[1]\a.png)", @"C:\tmp\[1]\a.png")]
    [InlineData(@"![x](<C:\my files\.cache\a.png>)", @"C:\my files\.cache\a.png")]
    [InlineData(@"![x](C:\a\R&amp;D\.b\c.png)", @"C:\a\R&D\.b\c.png")]
    [InlineData(@"![x](C:\\repo\\app\\.github\\a.png)", @"C:\repo\app\.github\a.png")]
    [InlineData("> 引言\n> - 清單 ![x](C:\\repo\\.playwright-mcp\\a.png \"標題\")", @"C:\repo\.playwright-mcp\a.png")]
    [InlineData("參考 ![x][shot]\r\n\r\n[shot]: C:\\repo\\.out\\a.png", @"C:\repo\.out\a.png")]
    public void 本機圖片的Windows路徑以原文為準_反斜線接標點不當跳脫(string markdown, string expected)
    {
        // CommonMark 把「\」接 ASCII 標點當跳脫：C:\repo\app\.screenshots 會被解析成 C:\repo\app.screenshots
        var images = new List<string>();
        var html = AgentMarkdownRenderer.ToHtml(markdown, images);

        images.Should().Equal(expected);
        html.Should().Contain($"src=\"{AgentMarkdownRenderer.MediaUrl(expected)}\"");
    }

    [Fact]
    public void 本機連結的Windows路徑以原文為準_程式碼區塊裡的照原樣顯示()
    {
        var html = AgentMarkdownRenderer.ToHtml("[ci.yml](C:\\repo\\app\\.github\\workflows\\ci.yml)\n\n`[x](C:\\a\\.b\\c.txt)`");

        html.Should().Contain($"href=\"{AgentMarkdownRenderer.OpenUrl(@"C:\repo\app\.github\workflows\ci.yml")}\"");
        html.Should().Contain(@"<code>[x](C:\a\.b\c.txt)</code>");
    }

    [Fact]
    public void 相對路徑以工作資料夾解析_去掉行號錨點()
    {
        const string cwd = @"C:\repo\proj";
        AgentMarkdownRenderer.ToHtml("[app.js:42](src/Web/app.js#L42)", null, cwd)
            .Should().Contain($"href=\"{AgentMarkdownRenderer.OpenUrl(@"C:\repo\proj\src\Web\app.js")}\"");
        AgentMarkdownRenderer.ToHtml("[上層](../README.md) [同層](./docs/a%20b.md#L3-L5)", null, cwd)
            .Should().Contain($"href=\"{AgentMarkdownRenderer.OpenUrl(@"C:\repo\README.md")}\"")
            .And.Contain($"href=\"{AgentMarkdownRenderer.OpenUrl(@"C:\repo\proj\docs\a b.md")}\"");
        AgentMarkdownRenderer.ToHtml("[a.cs:12](C:/repo/a.cs#L12)")
            .Should().Contain($"href=\"{AgentMarkdownRenderer.OpenUrl(@"C:\repo\a.cs")}\"", "完整路徑的行號錨點也要去掉");

        var images = new List<string>();
        AgentMarkdownRenderer.ToHtml("![截圖](shots/home.png)", images, cwd);
        images.Should().Equal(@"C:\repo\proj\shots\home.png");
    }

    [Theory]
    [InlineData("[章節](#安裝)")]
    [InlineData("[根目錄](/etc/passwd)")]
    [InlineData("[協定相對](//evil.example/x)")]
    [InlineData(@"[UNC](\\evil\share\a.txt)")]
    [InlineData("[編碼的協定相對](%2F%2Fevil.example/x)")]
    [InlineData("[資料流](a.txt:secret)")]
    [InlineData("![圖](//evil.example/x.png)")]
    public void 相對路徑_錨點根目錄協定相對UNC與資料流都不解析(string markdown)
    {
        var images = new List<string>();
        var html = AgentMarkdownRenderer.ToHtml(markdown, images, @"C:\repo\proj");

        images.Should().BeEmpty();
        html.Should().NotContain("/open?path=").And.NotContain("/media?path=");
    }

    [Fact]
    public void 相對路徑_沒有工作資料夾或工作資料夾不是本機磁碟時不解析()
    {
        AgentMarkdownRenderer.ToHtml("[a](src/a.cs)").Should().Contain("href=\"#\"");
        AgentMarkdownRenderer.ToHtml("[a](src/a.cs)", null, @"\\server\share\proj").Should().Contain("href=\"#\"");
        AgentMarkdownRenderer.ToHtml("見 www.example.com/a", null, @"C:\repo\proj").Should().Contain("href=\"http://www.example.com/a\"", "自動連結不是檔案路徑");
    }

    [Fact]
    public void 波浪號上標插入標記符號照原樣顯示_只有雙波浪號是刪除線()
    {
        var html = AgentMarkdownRenderer.ToHtml("大約需要 3~5 天，加急的話 1~2 天。\n\nO(n^2) 降到 O(n^1.5)，C++20 與 C++17，a==b 與 c==d\n\n這是 ~~刪除線~~");

        html.Should().Contain("3~5 天，加急的話 1~2 天").And.Contain("O(n^2) 降到 O(n^1.5)").And.Contain("C++20 與 C++17").And.Contain("a==b 與 c==d");
        html.Should().NotContain("<sub>").And.NotContain("<sup>").And.NotContain("<ins>").And.NotContain("<mark>");
        html.Should().Contain("<del>刪除線</del>");
    }

    [Theory]
    [InlineData("詳見 https://example.com/docs，請參考。", "詳見 <a href=\"https://example.com/docs\">https://example.com/docs</a>，請參考。")]
    [InlineData("網址是 https://example.com/a（括號說明）", "網址是 <a href=\"https://example.com/a\">https://example.com/a</a>（括號說明）")]
    [InlineData("官方文件：https://example.com/docs。", "官方文件：<a href=\"https://example.com/docs\">https://example.com/docs</a>。")]
    [InlineData("「https://example.com/q」", "「<a href=\"https://example.com/q\">https://example.com/q</a>」")]
    [InlineData("見 https://example.com，好", "見 <a href=\"https://example.com\">https://example.com</a>，好")]
    [InlineData("見 https://example.com/a，https://example.org/b。", "見 <a href=\"https://example.com/a\">https://example.com/a</a>，<a href=\"https://example.org/b\">https://example.org/b</a>。")]
    [InlineData("https://zh.wikipedia.org/wiki/臺灣 很好", "<a href=\"https://zh.wikipedia.org/wiki/%E8%87%BA%E7%81%A3\">https://zh.wikipedia.org/wiki/臺灣</a> 很好")]
    [InlineData("請開啟 https://example.com/x 確認", "請開啟 <a href=\"https://example.com/x\">https://example.com/x</a> 確認")]
    public void 自動連結遇到全形標點就結束_全形標點後的網址也認得(string markdown, string expected)
        => AgentMarkdownRenderer.ToHtml(markdown).Should().Be($"<p>{expected}</p>\n");

    [Fact]
    public void 文字裡的本機路徑變成檔案連結()
        => AgentMarkdownRenderer.ToHtml(@"截圖：C:\out\最近的指令-桌面.png。")
            .Should().Be($"<p>截圖：<a href=\"{AgentMarkdownRenderer.OpenUrl(@"C:\out\最近的指令-桌面.png")}\">C:\\out\\最近的指令-桌面.png</a>。</p>\n");

    [Theory]
    [InlineData(@"- 桌面：C:\Users\User\AppData\Local\x\screenshots\最近的指令-桌面.png", @"C:\Users\User\AppData\Local\x\screenshots\最近的指令-桌面.png", @"C:\Users\User\AppData\Local\x\screenshots\最近的指令-桌面.png")]
    [InlineData(@"設定在 C:\repo\app\.github\workflows\ci.yml，", @"C:\repo\app\.github\workflows\ci.yml", @"C:\repo\app\.github\workflows\ci.yml")]
    [InlineData("已寫入 C:/out/a.png.", "C:/out/a.png", @"C:\out\a.png")]
    [InlineData(@"錯誤在 C:\repo\a.cs:42:7，請看", @"C:\repo\a.cs:42:7", @"C:\repo\a.cs")]
    [InlineData(@"見 C:\repo\a.cs#L42 那行", @"C:\repo\a.cs#L42", @"C:\repo\a.cs")]
    [InlineData(@"報表（C:\out\r.xlsx）", @"C:\out\r.xlsx", @"C:\out\r.xlsx")]
    [InlineData(@"報表 (C:\out\r.xlsx) 好了", @"C:\out\r.xlsx", @"C:\out\r.xlsx")]
    [InlineData(@"存在C:\out\a.txt", @"C:\out\a.txt", @"C:\out\a.txt")]
    [InlineData(@"「C:\tmp\[1]\a.png」", @"C:\tmp\[1]\a.png", @"C:\tmp\[1]\a.png")]
    [InlineData(@"小寫 c:\out\b.txt", @"c:\out\b.txt", @"c:\out\b.txt")]
    [InlineData(@"資料夾 C:\repo\proj\ 裡", @"C:\repo\proj\", @"C:\repo\proj\")]
    [InlineData(@"參數 --out=C:\out\x.json", @"C:\out\x.json", @"C:\out\x.json")]
    [InlineData("> 引言 C:\\out\\q.md", @"C:\out\q.md", @"C:\out\q.md")]
    [InlineData("| 檔案 | 說明 |\n|---|---|\n| C:\\out\\t.png | 截圖 |", @"C:\out\t.png", @"C:\out\t.png")]
    [InlineData("刪除 ~~C:\\old\\a.txt~~", @"C:\old\a.txt", @"C:\old\a.txt")]
    public void 文字裡的本機路徑以原文顯示_連結去掉行號與尾端標點(string markdown, string text, string path)
    {
        var html = AgentMarkdownRenderer.ToHtml(markdown);

        html.Should().Contain($"<a href=\"{AgentMarkdownRenderer.OpenUrl(path)}\">{text}</a>");
        html.Split("<a ").Should().HaveCount(2, "只有一個連結");
    }

    [Fact]
    public void 文字裡的本機路徑_底線與星號不當強調_粗體裡的照常()
    {
        AgentMarkdownRenderer.ToHtml(@"檔案 C:\out\my_file_name_v2.txt 與 C:\a\b_c_d.md").Should().NotContain("<em>")
            .And.Contain(@">C:\out\my_file_name_v2.txt</a>").And.Contain(@">C:\a\b_c_d.md</a>");
        AgentMarkdownRenderer.ToHtml(@"**C:\out\a.png**")
            .Should().Be($"<p><strong><a href=\"{AgentMarkdownRenderer.OpenUrl(@"C:\out\a.png")}\">C:\\out\\a.png</a></strong></p>\n");
    }

    [Theory]
    [InlineData(@"abC:\x\y.txt")]
    [InlineData("file:///C:/x/y.txt")]
    [InlineData("C:\\ 根目錄")]
    [InlineData(@"\\server\share\a.txt")]
    [InlineData("時間 12:30、比例 a:b、網址 x:")]
    [InlineData("[C:\\out\\a.txt] 不是連結")]
    [InlineData("```\nC:\\out\\a.txt\n```")]
    [InlineData("    C:\\out\\a.txt")]
    public void 不是路徑開頭的或程式碼區塊裡的不變成連結(string markdown)
        => AgentMarkdownRenderer.ToHtml(markdown).Should().NotContain("/open?path=");

    [Fact]
    public void 網址與Markdown連結裡的路徑不重複變成連結()
    {
        AgentMarkdownRenderer.ToHtml("見 https://example.com/C:/x/y 好").Should().Contain("href=\"https://example.com/C:/x/y\"").And.NotContain("/open?path=");
        var html = AgentMarkdownRenderer.ToHtml(@"[C:\out\a.txt](C:\out\a.txt) 與 [`C:\out\b.txt`](C:\out\b.txt)");
        html.Split("<a ").Should().HaveCount(3, "連結文字裡的路徑不再包一層");
        html.Should().Contain($"<a href=\"{AgentMarkdownRenderer.OpenUrl(@"C:\out\b.txt")}\"><code>C:\\out\\b.txt</code></a>");
    }

    [Theory]
    [InlineData(@"`C:\My Files\報表 v2.xlsx`", @"C:\My Files\報表 v2.xlsx", @"C:\My Files\報表 v2.xlsx")]
    [InlineData("`C:/repo/a.cs:12`", "C:/repo/a.cs:12", @"C:\repo\a.cs")]
    [InlineData(@"`C:\repo\.github\ci.yml`", @"C:\repo\.github\ci.yml", @"C:\repo\.github\ci.yml")]
    public void 整段是本機路徑的行內程式碼包進連結(string markdown, string text, string path)
        => AgentMarkdownRenderer.ToHtml(markdown).Should().Contain($"<a href=\"{AgentMarkdownRenderer.OpenUrl(path)}\"><code>{text}</code></a>");

    [Theory]
    [InlineData(@"`C:\tools\x.exe --help`")]
    [InlineData(@"`C:\Windows\System32\cmd.exe /c dir`")]
    [InlineData(@"`dotnet run C:\a.csproj`")]
    [InlineData(@"`C:\out\*.png`")]
    [InlineData("`C:\\`")]
    public void 帶參數的指令或萬用字元的行內程式碼不包連結(string markdown)
        => AgentMarkdownRenderer.ToHtml(markdown).Should().NotContain("<a ");

    /// <summary>別台電腦的對話（NullMediaUrlBuilder、ReplicaMediaUrls）：本機檔案連結一律沒有網址</summary>
    private sealed class NoLinkUrls : IMediaUrlBuilder
    {
        public string MediaUrl(string path) => "#";

        public string ThumbUrl(string path, int size, string? version = null) => "#";

        public string MarkdownImageUrl(string path) => "#";

        public string MarkdownLinkUrl(string path) => "#";

        public void AllowMarkdownImage(string path)
        {
        }
    }

    [Fact]
    public void 網址實作沒有網址可給時_文字裡的路徑照原文顯示()
        => AgentMarkdownRenderer.ToHtml(@"見 C:\out\a.png 與 `C:\out\b.txt`。", null, null, new NoLinkUrls())
            .Should().Be("<p>見 C:\\out\\a.png 與 <code>C:\\out\\b.txt</code>。</p>\n");

    /// <summary>
    /// 2026-10-27 的記錄：其他電腦的對話裡回覆引用的本機圖片畫成 &lt;img src="#"&gt;，破圖、網頁回報「img 載入失敗（…/index.html#）」。
    /// 沒有網址可給時照原文顯示說明文字（沒有說明寫檔名），不留 img、也不登記這張圖
    /// </summary>
    [Fact]
    public void 網址實作沒有網址可給時_本機圖片照原文顯示說明_不畫破圖()
    {
        var images = new List<string>();
        var html = AgentMarkdownRenderer.ToHtml(@"截圖：![首頁](C:\out\home.png) 與 ![](C:\out\pair-01.jpg)", images, null, new NoLinkUrls());
        html.Should().Be("<p>截圖：首頁 與 圖片：pair-01.jpg</p>\n");
        images.Should().BeEmpty("沒有網址的圖片不登記");
    }

    [Fact]
    public void 網址實作有網址時_本機圖片照舊畫成img並登記()
    {
        var images = new List<string>();
        var html = AgentMarkdownRenderer.ToHtml(@"![首頁](C:\out\home.png)", images, null, new ServerUrls());
        html.Should().Be("<p><img src=\"/m/C%3A%5Cout%5Chome.png\" alt=\"首頁\" /></p>\n");
        images.Should().Equal(@"C:\out\home.png");
    }

    [Fact]
    public void 指定網址實作時_文字裡的路徑也用它()
        => AgentMarkdownRenderer.ToHtml(@"見 C:\out\a.png 與 `C:\out\b.txt`", null, null, new ServerUrls())
            .Should().Contain("<a href=\"/f/C%3A%5Cout%5Ca.png\">").And.Contain("<a href=\"/f/C%3A%5Cout%5Cb.txt\"><code>");

    [Theory]
    [InlineData(@"C:\a\b.cs", @"C:\a\b.cs")]
    [InlineData("C:/a//b.cs:42", @"C:\a\b.cs")]
    [InlineData(@"C:\a\b.cs:42:7", @"C:\a\b.cs")]
    [InlineData(@"C:\a\b%20c.txt", @"C:\a\b%20c.txt")]
    [InlineData(@"C:\", null)]
    [InlineData(@"C:\:42", null)]
    [InlineData(@"\\server\share\a.txt", null)]
    [InlineData("a/b.txt", null)]
    public void WrittenPathTarget_去掉行號_不做網址解碼(string written, string? expected)
        => AgentMarkdownRenderer.WrittenPathTarget(written).Should().Be(expected);

    [Fact]
    public void FileUrl的本機圖片也認得()
    {
        var images = new List<string>();
        AgentMarkdownRenderer.ToHtml("![a](file:///C:/out/a%20b.png)", images);
        images.Should().Equal(@"C:\out\a b.png");
    }

    [Fact]
    public void UNC圖片永遠不請求()
    {
        var images = new List<string>();
        var html = AgentMarkdownRenderer.ToHtml(@"![x](\\evil\share\a.png) ![y](file://evil/share/b.png)", images);
        images.Should().BeEmpty();
        html.Should().NotContain("evil");
    }

    [Fact]
    public void 本頁來源的連結不當外站_其他網址連結保留_表格與程式碼區塊正常()
    {
        AgentMarkdownRenderer.ToHtml("[x](https://myriagent.local/media?path=C%3A%5Cwin.ini)").Should().NotContain("href=\"https://myriagent.local/media");
        var html = AgentMarkdownRenderer.ToHtml("[官網](https://example.com) [信](mailto:a@b.c)\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```python\nprint('x')\n```");
        html.Should().Contain("href=\"https://example.com\"");
        html.Should().Contain("href=\"mailto:a@b.c\"");
        html.Should().Contain("<table>");
        html.Should().Contain("language-python");
    }

    [Fact]
    public void 空字串與Null回空字串()
    {
        AgentMarkdownRenderer.ToHtml(null).Should().BeEmpty();
        AgentMarkdownRenderer.ToHtml(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void 使用者訊息的純文字_編碼並保留換行()
        => AgentMarkdownRenderer.PlainToHtml("a < b\r\n第二行\n第三行").Should().Be("a &lt; b<br>第二行<br>第三行");

    [Fact]
    public void 媒體縮圖與開檔網址_路徑完整跳脫()
    {
        AgentMarkdownRenderer.MediaUrl(@"C:\資料\a+b.png").Should().Be("https://myriagent.local/media?path=C%3A%5C%E8%B3%87%E6%96%99%5Ca%2Bb.png");
        AgentMarkdownRenderer.ThumbUrl(@"C:\a.png", 160).Should().Be("https://myriagent.local/thumb?path=C%3A%5Ca.png&size=160");
        AgentMarkdownRenderer.OpenUrl(@"C:\a.pdf").Should().StartWith("https://myriagent.local/open?path=");
    }

    [Theory]
    [InlineData(@"C:\a\b.png", @"C:\a\b.png")]
    [InlineData("C:/a/b.png", @"C:\a\b.png")]
    [InlineData("<C:/a/b%20c.png>", @"C:\a\b c.png")]
    [InlineData(@"C:\\a\\b.png", @"C:\a\b.png")]
    [InlineData("C://a//b.png", @"C:\a\b.png")]
    [InlineData(@"\\server\share\a.png", null)]
    [InlineData("file://server/share/a.png", null)]
    [InlineData("a/b.png", null)]
    [InlineData(" ", null)]
    public void TryGetLocalPath_只認磁碟路徑(string url, string? expected)
        => AgentMarkdownRenderer.TryGetLocalPath(url).Should().Be(expected);

    /// <summary>伺服器那種網址（共用渲染器傳進來的 IMediaUrlBuilder）：Markdown 只會用到圖片與檔案連結兩種</summary>
    private sealed class ServerUrls : IMediaUrlBuilder
    {
        public string MediaUrl(string path) => throw new InvalidOperationException("Markdown 轉換不會用到");

        public string ThumbUrl(string path, int size, string? version = null) => throw new InvalidOperationException("Markdown 轉換不會用到");

        public string MarkdownImageUrl(string path) => "/m/" + Uri.EscapeDataString(path);

        public string MarkdownLinkUrl(string path) => "/f/" + Uri.EscapeDataString(path);

        public void AllowMarkdownImage(string path) => throw new InvalidOperationException("登記由共用渲染器在轉換成功後呼叫");
    }

    [Fact]
    public void 指定網址實作時_本機圖片與檔案連結改用它_其他連結照舊()
    {
        var images = new List<string>();
        var html = AgentMarkdownRenderer.ToHtml(
            @"![圖](C:\out\a.png) [檔](r.xlsx) [官網](https://example.com) ![外站](https://evil.example/x.png) [危險](javascript:alert(1)) [信](mailto:a@b.c)",
            images, @"C:\out", new ServerUrls());

        images.Should().Equal(@"C:\out\a.png");
        html.Should().Contain("src=\"/m/C%3A%5Cout%5Ca.png\"")
            .And.Contain("href=\"/f/C%3A%5Cout%5Cr.xlsx\"")
            .And.Contain("href=\"https://example.com\"")
            .And.Contain("href=\"https://evil.example/x.png\"")
            .And.Contain("href=\"#\"")
            .And.Contain("href=\"mailto:a@b.c\"");
        html.Should().NotContain(AgentMarkdownRenderer.Host);
        AgentMarkdownRenderer.RewriteUrl(@"C:\out\b.png", isImage: true, new ServerUrls()).Should().Be("/m/C%3A%5Cout%5Cb.png");
        AgentMarkdownRenderer.RewriteUrl(@"C:\out\b.pdf", isImage: false, new ServerUrls()).Should().Be("/f/C%3A%5Cout%5Cb.pdf");
        AgentMarkdownRenderer.RewriteUrl(@"C:\out\b.pdf", isImage: false).Should().Be(AgentMarkdownRenderer.OpenUrl(@"C:\out\b.pdf"), "沒有指定就是桌面的 /open");
    }

    [Theory]
    [InlineData("NOTE", "md-alert-note", "備註")]
    [InlineData("TIP", "md-alert-tip", "提示")]
    [InlineData("IMPORTANT", "md-alert-important", "重要")]
    [InlineData("WARNING", "md-alert-warning", "警告")]
    [InlineData("CAUTION", "md-alert-caution", "注意")]
    public void GitHub風格提示區塊_輸出blockquote與md_alert類別並保留內文與安全過濾(string kind, string expectedClass, string expectedLabel)
    {
        var html = AgentMarkdownRenderer.ToHtml($"> [!{kind}]\n> 請先備份 `C:\\data\\db.json` 再執行 <script>alert(1)</script>");
        html.Should().Contain($"<blockquote class=\"md-alert {expectedClass}\">")
            .And.Contain($"<p class=\"md-alert-title\"><strong>{expectedLabel}</strong></p>")
            .And.Contain("&lt;script&gt;alert(1)&lt;/script&gt;")
            .And.NotContain("<div");
    }

    [Fact]
    public void ItemRenderer_支援Tool產物Markdown預覽與Question計畫確認全文()
    {
        var renderer = new ItemRenderer(new ServerUrls());

        var toolItem = new AgentChatItem
        {
            Id = "t-art",
            Kind = AgentItemKind.Tool,
            Status = AgentItemStatus.Done,
            ToolName = "寫入檔案",
            ToolKind = "write",
            Title = @"C:\Users\u\.gemini\antigravity-cli\brain\c1\report.md",
            FilePath = @"C:\Users\u\.gemini\antigravity-cli\brain\c1\report.md",
            Text = "# 分析報告\n\n> [!TIP]\n> 架構穩定",
        };
        var renderedTool = renderer.Render(toolItem, @"C:\repo");
        renderedTool.Value<string>("detailHtml").Should().Contain("<h1>分析報告</h1>").And.Contain("md-alert-tip");

        var regularTool = new AgentChatItem
        {
            Id = "t-plain",
            Kind = AgentItemKind.Tool,
            Status = AgentItemStatus.Done,
            ToolName = "執行指令",
            ToolKind = "bash",
            Title = "dotnet test",
        };
        renderer.Render(regularTool, @"C:\repo").ContainsKey("detailHtml").Should().BeFalse("一般工具卡不帶 detailHtml，保持黃金檔相容");

        var planQuestion = new AgentChatItem
        {
            Id = "q-plan",
            Kind = AgentItemKind.Question,
            Status = AgentItemStatus.Waiting,
            Detail = "## 步驟一\n- 建立模組",
            FilePath = @"C:\Users\u\.gemini\antigravity-cli\brain\c1\plan.md",
            AnswerAsMessage = true,
            Questions = new List<AgentQuestion> { new() { Id = "agy-plan-feedback", Text = "確認計畫" } },
        };
        var renderedQuestion = renderer.Render(planQuestion, @"C:\repo");
        renderedQuestion.Value<string>("detailHtml").Should().Contain("<h2>步驟一</h2>");
        renderedQuestion.Value<string>("filePath").Should().Be(@"C:\Users\u\.gemini\antigravity-cli\brain\c1\plan.md");
    }
}
