using Myriagent.Agents;
using Myriagent.Health;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Health;

/// <summary>電腦層級指示檔的比較：切條、正規化與相似度、一致／要同步／有分歧、Grok 的 Claude 相容、合併與精簡的開頭訊息</summary>
public sealed class InstructionCompareRulesTests
{
    private const string Claude = """
        # CLAUDE.md（全域）

        ## 語言

        **嚴格使用臺灣繁體中文** 撰寫所有回應、程式碼註解與 commit messages。

        ## 部署

        - push 後驗證部署，`gh run list` 要用 `--commit` 過濾。
        - 上線後務必實際 curl 驗證，勿只看 Actions 綠燈。

        | 情境 | 參考檔 |
        |---|---|
        | IIS 部署 | `~/.claude/docs/deployment-pitfalls.md` |

        <!-- 維護者備註：不送進脈絡 -->
        ```bash
        dotnet build
        ```
        """;

    private static InstructionDoc Doc(AgentProvider provider, string label, string path, string text, bool exists = true, string? also = null)
    {
        var doc = new InstructionDoc
        {
            Key = path.ToLowerInvariant(), Provider = provider, Label = label, Path = path, Exists = exists, Text = text,
            Measure = HealthTextRules.Measure(text, text.Length), AlsoLoadsKey = also,
        };
        doc.AccountIds.Add("acc-" + provider);
        return doc;
    }

    [Fact]
    public void 切條_清單表格段落程式碼各算一條_標題與註解不算()
    {
        var units = InstructionCompareRules.Units(Claude);
        units.Select(u => u.Section).Should().Equal("語言", "部署", "部署", "部署", "部署", "部署");
        units[0].Text.Should().Be("**嚴格使用臺灣繁體中文** 撰寫所有回應、程式碼註解與 commit messages。");
        units[0].Norm.Should().Be("嚴格使用臺灣繁體中文 撰寫所有回應、程式碼註解與 commit messages");
        units[1].Text.Should().StartWith("push 後驗證部署");
        units[3].Text.Should().Be("| 情境 | 參考檔 |");
        units[5].Text.Should().Contain("dotnet build");
        units.Should().NotContain(u => u.Text.Contains("維護者備註"));
    }

    [Fact]
    public void 相似度_小改動算同一條()
    {
        InstructionCompareRules.Similarity("上線後務必實際 curl 驗證，勿只看 actions 綠燈", "上線後務必實際 curl 驗證，不要只看 actions 綠燈").Should().BeGreaterThan(0.8);
        InstructionCompareRules.Similarity("上線後務必實際 curl 驗證", "一律使用臺灣繁體中文").Should().BeLessThan(0.3);
    }

    [Fact]
    public void 只有一份_或都一致()
    {
        var single = InstructionCompareRules.Compare(new[] { Doc(AgentProvider.Claude, "Claude Code", @"C:\h\.claude\CLAUDE.md", Claude) });
        single.Verdict.Should().Be(InstructionVerdict.Single);
        single.Mergeable.Should().BeFalse();

        // 只差 Markdown 符號、空白與結尾標點的算一致
        var codexText = Claude.Replace("**嚴格使用臺灣繁體中文**", "嚴格使用臺灣繁體中文").Replace("過濾。", "過濾");
        var same = InstructionCompareRules.Compare(new[]
        {
            Doc(AgentProvider.Claude, "Claude Code", @"C:\h\.claude\CLAUDE.md", Claude),
            Doc(AgentProvider.Codex, "Codex", @"C:\h\.codex\AGENTS.md", codexText),
        });
        same.Verdict.Should().Be(InstructionVerdict.Same);
        same.Level.Should().Be(HealthLevel.Ok);
        same.Docs.Should().OnlyContain(d => d.Coverage == 1);
    }

    [Fact]
    public void 一份是空的_要同步()
    {
        var report = InstructionCompareRules.Compare(new[]
        {
            Doc(AgentProvider.Claude, "Claude Code", @"C:\h\.claude\CLAUDE.md", Claude),
            Doc(AgentProvider.Codex, "Codex", @"C:\h\.codex\AGENTS.md", ""),
        });
        report.Verdict.Should().Be(InstructionVerdict.NeedsSync);
        report.Level.Should().Be(HealthLevel.Warn);
        report.Mergeable.Should().BeTrue();
        report.Summary.Should().Be("Codex 的 AGENTS.md 是空的，拿不到其他 CLI 的 6 條指示；建議合併並同步。");
        var codex = report.Docs[1];
        codex.Units.Should().Be(0);
        codex.Missing.Should().HaveCount(6);
        report.Docs[0].Unique.Should().HaveCount(6);

        var json = InstructionCompareRules.Render(report);
        json.Value<string>("verdict").Should().Be("needsSync");
        var docs = (JArray)json["docs"]!;
        docs[1].Value<string>("status").Should().Be("empty");
        docs[1].Value<string>("note").Should().Be("空的");
        docs[1].Value<int>("missingCount").Should().Be(6);
        docs[1]["missing"]![0]!.Value<string>("section").Should().Be("語言");
        docs[0].Value<string>("status").Should().Be("complete");
        docs[0].Value<double>("coverage").Should().Be(1);
    }

    [Fact]
    public void 互有缺漏_有分歧()
    {
        var codexText = "## 語言\n\n嚴格使用臺灣繁體中文撰寫所有回應、程式碼註解與 commit messages。\n\n## Codex\n\n- 沙箱一律用 workspace-write。\n";
        var report = InstructionCompareRules.Compare(new[]
        {
            Doc(AgentProvider.Claude, "Claude Code", @"C:\h\.claude\CLAUDE.md", Claude),
            Doc(AgentProvider.Codex, "Codex", @"C:\h\.codex\AGENTS.md", codexText),
        });
        report.Verdict.Should().Be(InstructionVerdict.Diverged);
        report.Level.Should().Be(HealthLevel.Warn);
        report.Summary.Should().StartWith("各份有分歧：Claude Code 缺 1 條、Codex 缺 5 條");
        report.Docs[0].Missing.Single().Text.Should().Be("沙箱一律用 workspace-write。");
        report.Docs[1].Unique.Single().Section.Should().Be("Codex");
        report.Docs[1].Covered.Should().Be(2, "語言那一條只差空白，算同一條");
    }

    [Fact]
    public void Grok的Claude相容_一併讀的算拿得到_自己又寫一次算重複()
    {
        var claudePath = @"C:\h\.claude\CLAUDE.md";
        var report = InstructionCompareRules.Compare(new[]
        {
            Doc(AgentProvider.Claude, "Claude Code", claudePath, Claude),
            Doc(AgentProvider.Grok, "Grok Build", @"C:\h\.grok\AGENTS.md", "## 語言\n\n**嚴格使用臺灣繁體中文** 撰寫所有回應、程式碼註解與 commit messages。\n", also: claudePath.ToLowerInvariant()),
        });
        report.Verdict.Should().Be(InstructionVerdict.Same, "Grok 經相容讀得到 CLAUDE.md 的每一條");
        var grok = report.Docs[1];
        grok.Duplicates.Should().Be(1);
        InstructionCompareRules.Render(report)["docs"]![1]!.Value<string>("alsoLoads").Should().Be("Claude Code");
    }

    [Fact]
    public void 都沒有內容()
    {
        var report = InstructionCompareRules.Compare(new[]
        {
            Doc(AgentProvider.Claude, "Claude Code", @"C:\h\.claude\CLAUDE.md", "", exists: false),
            Doc(AgentProvider.Codex, "Codex", @"C:\h\.codex\AGENTS.md", ""),
        });
        report.Verdict.Should().Be(InstructionVerdict.None);
        InstructionCompareRules.Render(report)["docs"]![0]!.Value<string>("status").Should().Be("missing");
        InstructionCompareRules.Compare(Array.Empty<InstructionDoc>()).Verdict.Should().Be(InstructionVerdict.None);
    }

    [Fact]
    public void 合併的開頭訊息_列出檔案與連結目標_先給計畫_備份檔名()
    {
        var claude = new InstructionDoc
        {
            Key = @"c:\h\.claude\claude.md", Provider = AgentProvider.Claude, Label = "Claude Code", Path = @"C:\h\.claude\CLAUDE.md",
            LinkTarget = @"C:\repo\dotfiles\claude\CLAUDE.md", Exists = true, Text = Claude, Measure = HealthTextRules.Measure(Claude, 2048),
        };
        var report = InstructionCompareRules.Compare(new[] { claude, Doc(AgentProvider.Codex, "Codex", @"C:\h\.codex\AGENTS.md", "") });
        var prompt = InstructionCompareRules.BuildMergePrompt(report, new DateTime(2026, 10, 23, 14, 5, 0));
        prompt.Should().Contain(@"Claude Code：C:\h\.claude\CLAUDE.md（符號連結，實際檔案是 C:\repo\dotfiles\claude\CLAUDE.md）");
        prompt.Should().Contain(@"Codex：C:\h\.codex\AGENTS.md — 空的");
        prompt.Should().Contain("Codex 缺 6 條（在這些段落：語言、部署）");
        prompt.Should().Contain("等我確認才寫入").And.Contain(".bak-20261023-1405");
    }

    [Fact]
    public void 精簡的開頭訊息()
    {
        var scan = new AccountScan { Account = new HealthAccountInput("a1", "主帳戶", AgentProvider.Claude, true, @"C:\h\.claude", false, null, null) };
        scan.Instructions.Add(new InstructionFileStat(@"C:\h\.claude\CLAUDE.md", "main", new TextMeasure(41_471, 23_702, 84, 12_550), @"C:\dot\CLAUDE.md"));
        var health = HealthRules.Evaluate(scan);
        var prompt = InstructionCompareRules.BuildTrimPrompt(scan, health, new DateTime(2026, 10, 23, 9, 0, 0));
        prompt.Should().StartWith("請幫我精簡「主帳戶」（Claude Code）");
        prompt.Should().Contain(@"C:\h\.claude\CLAUDE.md（符號連結，實際檔案是 C:\dot\CLAUDE.md） — 84 行、40.5 KB、約 1.3 萬 token");
        prompt.Should().Contain("~/.claude/rules/").And.Contain(".bak-20261023-0900").And.Contain("約 5,000 token 以下");
    }

    [Fact]
    public void 整理記憶索引的開頭訊息_只列超過或接近上限的_超過的排前面_備份整個資料夾()
    {
        var scan = new AccountScan { Account = new HealthAccountInput("a2", "副帳戶", AgentProvider.Claude, false, @"C:\acc\a2", false, @"C:\h\.claude", null) };
        scan.MemoryIndexes = new List<MemoryIndexStat>
        {
            new("C--repo-near", @"C:\h\.claude\projects\C--repo-near\memory\MEMORY.md", 185, 9_000),
            new("C--repo-ok", @"C:\h\.claude\projects\C--repo-ok\memory\MEMORY.md", 30, 2_000),
            new("C--repo-SampleShop", @"C:\h\.claude\projects\C--repo-SampleShop\memory\MEMORY.md", 126, 25_497),
        };
        var prompt = InstructionCompareRules.BuildMemoryPrompt(scan, new DateTime(2026, 10, 24, 13, 46, 0));
        prompt.Should().StartWith("請幫我整理「副帳戶」（Claude Code）的自動記憶索引：下面這些專案的 MEMORY.md 超過或接近載入上限。");
        prompt.Should().Contain("只載入每個專案 MEMORY.md 的前 200 行或 25KB");
        prompt.Should().Contain("這個帳戶共用系統預設帳戶的記憶");
        prompt.Should().Contain(@"1. C--repo-SampleShop：C:\h\.claude\projects\C--repo-SampleShop\memory\MEMORY.md — 126 行、24.9 KB（超過上限）");
        prompt.Should().Contain(@"2. C--repo-near：C:\h\.claude\projects\C--repo-near\memory\MEMORY.md — 185 行、8.8 KB（接近上限）");
        prompt.Should().NotContain("C--repo-ok");
        prompt.Should().Contain(@"C:\repo\my.app 是 C--repo-my-app");
        prompt.Should().Contain("等我確認才寫入").And.Contain("memory.bak-20261024-1346").And.Contain("150 行、20KB 以內");

        scan.MemoryIndexes.RemoveAll(m => m.Project != "C--repo-ok");
        var act = () => InstructionCompareRules.BuildMemoryPrompt(scan, DateTime.Now);
        act.Should().Throw<ArgumentException>();
    }
}
