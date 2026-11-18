using System.Text.RegularExpressions;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.UI;

/// <summary>
/// 網頁 CSS 的效能規則（2026-10-23）：:has() 的對象不可以是包住整個畫面、整個側欄或整串訊息的容器，
/// 除非參數只看直接子元素或相鄰的兄弟（以 &gt;、+、~ 開頭）。fx.css 曾有 .app:has(.sidebar .macct-run:not([hidden]))：
/// 畫面上任何變動（串流的文字、按鈕的顯示隱藏）都要重新檢查最外層的 :has()，六個對話同時串流時樣式重算多一倍以上、
/// 打字要排隊等（tools/ui-probe 的壓力情境）。要依「裡面有沒有某個狀態」改外層的樣式，改由 JS 在狀態改變時切換 class
/// </summary>
public class WebCssPerfTests
{
    /// <summary>包住大片畫面的容器（index.html 的骨架、窗格、對話、訊息串、側欄的清單、電腦狀態面板）</summary>
    private static readonly string[] Containers =
    {
        "html", "body", ":root", ".app", "#app", ".body", ".ws", "#workspace", ".sidebar", "#sidebar", ".dash", "#dashboard",
        ".pane", ".pane-body", ".conv", ".conv-main", ".thread", ".items", ".sb-list", ".sb-scroll", "#monitor",
    };

    private static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Singleline);

    /// <summary>
    /// 容器（後面可以接同一個元素的其他條件，例如 .app.x:not(.y)）緊接著 :has(，而且參數不是以 &gt;、+、~ 開頭
    /// </summary>
    private static readonly Regex WideHas = new(
        @"(?<![\w-])(?:" + string.Join("|", Containers.Select(Regex.Escape)) + @")(?![\w-])[^\s,{}>+~]*?:has\(\s*(?![>+~])",
        RegexOptions.Compiled);

    /// <summary>CSS 裡違規的選擇器片段（行號：片段）；註解不算</summary>
    internal static IReadOnlyList<string> WideHasSelectors(string css)
    {
        // 註解換成同樣多的換行，行號不變
        var code = Comment.Replace(css, m => new string('\n', m.Value.Count(ch => ch == '\n')));
        return WideHas.Matches(code)
            .Select(m => (code.Take(m.Index).Count(ch => ch == '\n') + 1) + "：" + m.Value)
            .ToList();
    }

    [Theory]
    [InlineData(".app:has(.sidebar .macct-run:not([hidden])) .brand-mark::after { animation: x 1s; }")]
    [InlineData("body:has(.x) { color: red; }")]
    [InlineData(":root:has(dialog[open]) { overflow: hidden; }")]
    [InlineData(".conv.running:has(.it-card.waiting) .ch { color: red; }")]
    [InlineData(".a, .items:not(.x):has( .it) { color: red; }")]
    [InlineData("#app:has(.x) { color: red; }")]
    public void 包住大片畫面的容器用has會被抓到(string css)
    {
        WideHasSelectors(css).Should().HaveCount(1);
    }

    [Theory]
    [InlineData(".pane-body:has(> .pane-empty)::before { inset: 0; }")]
    [InlineData(".app-ver:has(> .app-ver-time:not([hidden])) { height: 28px; }")]
    [InlineData(".macct:has(.macct-run:not([hidden])) .mmeter::before { color: red; }")]
    [InlineData(".items > .it-step:not(:has(+ .it-step))::before { bottom: 0; }")]
    [InlineData(".chr-acts:not(:has(> .btn:not([hidden]))) { display: none; }")]
    [InlineData(".app.acct-running .brand-mark::after { animation: x 1s; }")]
    [InlineData("/* 以前寫成 .app:has(.sidebar .macct-run) */ .a { color: red; }")]
    [InlineData(".conv ~ .x:has(.y) { color: red; }")]
    public void 小範圍或只看子元素兄弟的has不算(string css)
    {
        WideHasSelectors(css).Should().BeEmpty();
    }

    [Fact]
    public void 網頁的CSS沒有掛在大容器上的has()
    {
        var dir = Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web");
        var files = Directory.GetFiles(dir, "*.css");
        files.Select(Path.GetFileName).Should().Contain(new[] { "app.css", "chat.css", "fx.css" }, "要讀得到 Web/ 的樣式表");
        foreach (var file in files)
        {
            WideHasSelectors(File.ReadAllText(file)).Should().BeEmpty(
                $"{Path.GetFileName(file)} 的 :has() 掛在包住大片畫面的容器上：畫面任何變動都要重新檢查，改由 JS 在狀態改變時切換 class（例：sidebar.js 的 markAnyRunning）");
        }
    }

    private static readonly Regex AttributePart = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    private static readonly Regex NthPart = new(@":nth-[\w-]+\((?:[^()]|\([^()]*\))*\)", RegexOptions.Compiled);

    /// <summary>最外層的一組括號（裡面可以再有括號）</summary>
    private static readonly Regex TopParens = new(@"\((?<inner>(?>[^()]+|\((?<depth>)|\)(?<-depth>))*(?(depth)(?!)))\)", RegexOptions.Compiled);

    /// <summary>規則的選擇器（前一個 {、}、; 之後到 { 之前）</summary>
    private static readonly Regex RuleHead = new(@"([^{};]+)\{", RegexOptions.Compiled);

    /// <summary>兄弟組合子（+、~）與它左邊緊接著的那個複合選擇器</summary>
    private static readonly Regex SiblingCombinator = new(@"(?<left>[^\s>+~,]*)\s*[+~]\s*(?=[\w.#\[:*])", RegexOptions.Compiled);

    /// <summary>
    /// 兄弟組合子（+、~）左邊那個複合選擇器沒有 class、id、屬性（只有標籤、* 或虛擬類別）的選擇器（行號：選擇器）。
    /// 這種規則 Chrome 當成「任何元素插入都可能影響」：有相鄰規則的父元素底下插入或移除一個子元素，那個父元素整個子樹裡
    /// 符合右邊條件的元素（例如所有 span）都要重算樣式。曾經有 .md th + th、.turn-rule &gt; span + span、.turn-sum &gt; span + span
    /// 三條，讓項目區每插入一則就重算整個對話的這些元素——手機的項目照實際高度排版、沒有 content-visibility 擋著，
    /// 每個新項目、每次載入較早的訊息都付全額。改寫成 :not(:first-child) 之類不經兄弟組合子的寫法，或讓左邊帶 class。
    /// :has(+ …) 這種相對選擇器（組合子前面沒有複合選擇器）不算；:nth-child(n+6)、[a~=b] 裡的符號不是組合子
    /// </summary>
    internal static IReadOnlyList<string> BareSiblingSelectors(string css)
    {
        var code = Comment.Replace(css, m => new string('\n', m.Value.Count(ch => ch == '\n')));
        var found = new List<string>();
        foreach (Match rule in RuleHead.Matches(code))
        {
            var selector = rule.Groups[1].Value;
            if (selector.TrimStart().StartsWith('@'))
                continue;
            var line = code.Take(rule.Groups[1].Index).Count(ch => ch == '\n') + 1 + selector.TakeWhile(ch => ch == '\n' || ch == '\r' || ch == ' ').Count(ch => ch == '\n');
            if (HasBareSibling(NthPart.Replace(AttributePart.Replace(selector, "[]"), ":nth")))
                found.AddRange(Enumerable.Repeat(line + "：" + selector.Trim(), 1));
        }

        return found;
    }

    /// <summary>這段選擇器（屬性與 :nth-*() 的內容已經拿掉）有沒有左邊不帶 class、id、屬性的兄弟組合子；括號裡的（:not()、:is()…的參數）各自再看</summary>
    private static bool HasBareSibling(string text)
    {
        foreach (Match inner in TopParens.Matches(text))
        {
            if (HasBareSibling(inner.Groups["inner"].Value))
                return true;
        }

        // 虛擬類別括號裡的條件不算在左邊那個複合選擇器上（:not(.x) + y：任何沒有 .x 的元素都符合）
        var flat = TopParens.Replace(text, "()");
        foreach (Match m in SiblingCombinator.Matches(flat))
        {
            var left = m.Groups["left"].Value;
            // 相對選擇器（:has(+ .x) 的參數）：組合子前面沒有複合選擇器
            if (left.Length > 0 && left.IndexOfAny(['.', '#', '[']) < 0)
                return true;
        }

        return false;
    }

    [Theory]
    [InlineData(".md th + th, .md td + td { border-left: 1px solid red; }", 1)]
    [InlineData(".a { color: red; }\n.md th + th { x: y; }\n.t > span + span::before { x: y; }", 2)]
    [InlineData(".items > .it:not(th + th) { color: red; }", 1)]
    [InlineData(".turn-rule > span + span::before { content: ''; }", 1)]
    [InlineData("li ~ li { margin: 0; }", 1)]
    [InlineData(".md > * + * { margin-top: 1em; }", 1)]
    [InlineData(".a > :not(.x) + .y { color: red; }", 1)]
    [InlineData("h2 + p,\n.b { color: red; }", 1)]
    public void 左邊只有標籤的兄弟選擇器會被抓到(string css, int count)
    {
        BareSiblingSelectors(css).Should().HaveCount(count);
    }

    [Theory]
    [InlineData(".it-user + .it { margin-top: 6px; }")]
    [InlineData(".items > .it-step:not(.it-step + .it-step)::before { top: 0; }")]
    [InlineData(".items > .it-step:not(:has(+ .it-step))::before { bottom: 0; }")]
    [InlineData(".mon-bars[data-bars=\"3\"] i:nth-child(-n+3) { opacity: 1; }")]
    [InlineData(".fx-view-in :is(.dash-inner, .sch-main) > :nth-child(n+6) { animation-delay: 1s; }")]
    [InlineData("[data-x~=\"a\"] .b { color: red; }")]
    [InlineData(".cbar > .ibtn ~ .ibtn { margin: 0; }")]
    [InlineData("input[type=\"checkbox\"] + .lbl { color: red; }")]
    [InlineData("#a + b { color: red; }")]
    [InlineData(".md th:not(:first-child) { border-left: 0; }")]
    [InlineData("/* 以前寫成 th + th */ .a { width: calc(1px + 2px); }")]
    [InlineData("@media (min-width: 10px) { .a + .b { color: red; } }")]
    public void 左邊帶class的兄弟選擇器與不是組合子的符號不算(string css)
    {
        BareSiblingSelectors(css).Should().BeEmpty();
    }

    [Fact]
    public void 網頁的CSS沒有左邊只有標籤的兄弟選擇器()
    {
        var dir = Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web");
        var files = Directory.GetFiles(dir, "*.css");
        files.Select(Path.GetFileName).Should().Contain(new[] { "app.css", "chat.css", "fx.css" }, "要讀得到 Web/ 的樣式表");
        foreach (var file in files)
        {
            BareSiblingSelectors(File.ReadAllText(file)).Should().BeEmpty(
                $"{Path.GetFileName(file)} 有左邊只有標籤的兄弟選擇器（例：th + th）：任何元素插入時整個子樹的同名標籤都要重算樣式，改寫成 :not(:first-child) 或讓左邊帶 class（見 chat.css 的 .md th:not(:first-child)）");
        }
    }

    /// <summary>
    /// token 計數器（2026-10-25）：帳戶卡有 .macct:has(.macct-run:not([hidden])) 的規則，卡片底下每插入、移除一個節點，Chrome 都要重新判斷 :has()
    /// 並重算整張卡。解碼每 45 ms 換一次每一格的字，以前以 textContent 換掉文字節點（失效追蹤：背景分頁執行中的情境 2 秒 822 次、主執行緒約三成），
    /// 改成只改原本那個文字節點的內容（setChar 的 data）
    /// </summary>
    [Fact]
    public void token計數器換字只改文字節點的內容_不換掉節點()
    {
        var source = File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Web", "tokenfx.js"));
        source.Should().Contain("t.data = ch", "換字改原本那個文字節點的內容");
        Regex.Matches(source, @"\.textContent\s*=(?!=)").Count.Should().Be(1, "以 textContent 賦值只能是 setChar 的後備（格子裡還沒有文字節點時）；paint、settle 換字一律經 setChar");
        Regex.Matches(source, @"setChar\(c, ").Count.Should().Be(2, "paint 與 settle 都經 setChar");
    }
}
