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
