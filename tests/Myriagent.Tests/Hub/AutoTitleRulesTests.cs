using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Usage;

namespace Myriagent.Tests.Hub;

/// <summary>自動重新命名（標題的右鍵選單）的規則：給模型的節錄、整理模型的回覆、退而求其次的標題、CLI 參數與結果判讀</summary>
public sealed class AutoTitleRulesTests
{
    private static AgentChatItem User(string text) => new() { Kind = AgentItemKind.User, Text = text };

    private static AgentChatItem Reply(string text) => new() { Kind = AgentItemKind.Assistant, Text = text };

    private static AgentChatItem Tool(string name, string title) => new() { Kind = AgentItemKind.Tool, ToolName = name, Title = title };

    private static AgentChatItem Plan(params (string Text, string Status)[] steps)
        => new() { Kind = AgentItemKind.Plan, PlanSteps = steps.Select(s => new AgentPlanStep { Text = s.Text, Status = s.Status }).ToList() };

    private static AgentChatItem Clear() => new() { Kind = AgentItemKind.Notice, NoticeKind = AgentNoticeKind.Clear, Text = AgentHubRules.ClearNoticeText };

    private const string Latest = AutoTitleRules.LatestRequestLabel;

    [Fact]
    public void 代理種類都有產生方式_四家CLI皆支援單發呼叫模型()
    {
        foreach (var provider in AgentProviders.All)
            AutoTitleRules.MethodFor(provider).Should().NotBe(AutoTitleMethod.None);
        AutoTitleRules.MethodFor(AgentProvider.Claude).Should().Be(AutoTitleMethod.ClaudePrint);
        AutoTitleRules.MethodFor(AgentProvider.Codex).Should().Be(AutoTitleMethod.CodexExec);
        AutoTitleRules.MethodFor(AgentProvider.Antigravity).Should().Be(AutoTitleMethod.AntigravityPrint);
        AutoTitleRules.MethodFor(AgentProvider.Grok).Should().Be(AutoTitleMethod.GrokPrint);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));

    /// <summary>各種帳戶：claude-2 的 Opus 每週上限用完（不影響 sonnet）、claude-full 的 5 小時窗用滿、claude-out 未登入、claude-api 走 API Key、claude-exh 剛用完額度</summary>
    private static List<AutoTitleCandidate> Candidates() => new()
    {
        new("grok", AgentProvider.Grok, AccountStatus.Ready, null, 0),
        new("codex-b", AgentProvider.Codex, AccountStatus.Ready, null, 2),
        new("codex-a", AgentProvider.Codex, AccountStatus.Ready, null, 1),
        new("claude-own", AgentProvider.Claude, AccountStatus.Ready, null, 0),
        new("claude-2", AgentProvider.Claude, AccountStatus.Ready, new[] { new UsageWindow($"{UsageRules.WeeklyKey}:Opus", "每週・Opus", 100, Now.AddDays(2), Locked: true) }, 3),
        new("claude-full", AgentProvider.Claude, AccountStatus.Ready, new[] { new UsageWindow("session", "5 小時", 100, Now.AddHours(1)) }, 1),
        new("claude-out", AgentProvider.Claude, AccountStatus.NotLoggedIn, null, 2),
        new("claude-api", AgentProvider.Claude, AccountStatus.ApiKey, null, 4),
        new("claude-exh", AgentProvider.Claude, AccountStatus.Ready, null, 5),
        new("agy", AgentProvider.Antigravity, AccountStatus.Ready, null, 0),
    };

    /// <summary>claude-exh 十分鐘後才能再用；codex-b 的已經過了</summary>
    private static readonly Dictionary<string, DateTimeOffset> Exhausted = new() { ["claude-exh"] = Now.AddMinutes(10), ["codex-b"] = Now.AddMinutes(-1) };

    [Fact]
    public void 產生標題的帳戶_先用對話自己的_再借同種代理_再照代理種類_略過不能用的()
    {
        AutoTitleRules.PlanAccounts("claude-own", Candidates(), Exhausted, Now)
            .Should().Equal("claude-own", "claude-2", "codex-a", "codex-b", "agy", "grok");
        AutoTitleRules.PlanAccounts("codex-a", Candidates(), Exhausted, Now)
            .Should().Equal("codex-a", "codex-b", "claude-own", "claude-2", "agy", "grok");
    }

    [Fact]
    public void 產生標題的帳戶_Grok與Antigravity的對話先用自己_再借其他已登入帳戶()
    {
        AutoTitleRules.PlanAccounts("grok", Candidates(), Exhausted, Now)
            .Should().Equal("grok", "claude-own", "claude-2", "codex-a", "codex-b", "agy");
        AutoTitleRules.PlanAccounts("agy", Candidates(), Exhausted, Now)
            .Should().Equal("agy", "claude-own", "claude-2", "codex-a", "codex-b", "grok");
        AutoTitleRules.PlanAccounts("已移除的帳戶", Candidates(), Exhausted, Now)
            .Should().Equal("claude-own", "claude-2", "codex-a", "codex-b", "agy", "grok");
    }

    [Fact]
    public void 產生標題的帳戶_自己的走API_Key也用_未登入_額度用完_最近用完的不用()
    {
        AutoTitleRules.PlanAccounts("claude-api", Candidates(), Exhausted, Now).Should().StartWith("claude-api", "對話本來就用這個帳戶計費");
        AutoTitleRules.PlanAccounts("claude-out", Candidates(), Exhausted, Now)
            .Should().Equal("claude-own", "claude-2", "codex-a", "codex-b", "agy", "grok");
        AutoTitleRules.PlanAccounts("claude-full", Candidates(), Exhausted, Now)
            .Should().Equal("claude-own", "claude-2", "codex-a", "codex-b", "agy", "grok");
        AutoTitleRules.PlanAccounts("claude-exh", Candidates(), Exhausted, Now)
            .Should().Equal("claude-own", "claude-2", "codex-a", "codex-b", "agy", "grok");
        AutoTitleRules.PlanAccounts("claude-own", Array.Empty<AutoTitleCandidate>(), Exhausted, Now).Should().BeEmpty();
    }

    [Fact]
    public void 節錄依時間順序_收使用者訊息_代理回覆_工具與最新的待辦_略過思考()
    {
        var items = new List<AgentChatItem>
        {
            User("幫我修登入頁的錯誤"),
            new() { Kind = AgentItemKind.Thinking, Text = "先看看程式" },
            Tool("讀取檔案", "src/login.ts"),
            Plan(("找出原因", "completed"), ("修正", "pending")),
            Plan(("找出原因", "completed"), ("修正", "inProgress")),
            Reply("找到原因了，正在修正。"),
        };

        var excerpt = AutoTitleRules.BuildExcerpt(items, running: true)!;

        excerpt.Should().StartWith($"（代理正在執行這一輪）\n<conversation>\n<recent>\n{Latest}幫我修登入頁的錯誤").And.EndWith("\n</recent>\n</conversation>\n" + AutoTitleRules.ExcerptReminder, "節錄最後再提醒一次輸出格式");
        excerpt.Should().NotContain("<earlier>", "最近的要求之前沒有內容就不寫背景");
        excerpt.Should().NotContain("先看看程式");
        excerpt.IndexOf(Latest + "幫我修登入頁的錯誤", StringComparison.Ordinal).Should().BeLessThan(excerpt.IndexOf("【工具】讀取檔案：src/login.ts", StringComparison.Ordinal));
        excerpt.IndexOf("【工具】", StringComparison.Ordinal).Should().BeLessThan(excerpt.IndexOf("【待辦】", StringComparison.Ordinal));
        excerpt.IndexOf("【待辦】", StringComparison.Ordinal).Should().BeLessThan(excerpt.IndexOf("【代理】找到原因了", StringComparison.Ordinal));
        excerpt.Should().Contain("- [進行中] 修正").And.Contain("- [完成] 找出原因");
        excerpt.Split("【待辦】").Should().HaveCount(2, "只收最新的待辦清單");
        AutoTitleRules.BuildExcerpt(items, running: false)!.Should().StartWith("（代理已結束最後一輪）");
    }

    [Fact]
    public void 節錄只收最後一次清除之後的()
    {
        var items = new List<AgentChatItem> { User("舊的工作"), Reply("舊的回覆"), Clear(), User("新的工作") };

        var excerpt = AutoTitleRules.BuildExcerpt(items, running: false)!;

        excerpt.Should().Contain("新的工作").And.NotContain("舊的");
    }

    [Fact]
    public void 沒有使用者訊息也沒有代理回覆_節錄是null()
    {
        AutoTitleRules.BuildExcerpt(new List<AgentChatItem>(), running: false).Should().BeNull();
        AutoTitleRules.BuildExcerpt(new List<AgentChatItem> { Tool("執行指令", "dir") }, running: false).Should().BeNull();
        AutoTitleRules.BuildExcerpt(new List<AgentChatItem> { User("舊的"), Clear() }, running: false).Should().BeNull();
    }

    [Fact]
    public void 工具最多收十個_最近的優先()
    {
        var items = new List<AgentChatItem> { User("跑測試") };
        for (int i = 0; i < 30; i++)
            items.Add(Tool("執行指令", $"step-{i:00}"));

        var excerpt = AutoTitleRules.BuildExcerpt(items, running: true)!;

        excerpt.Split("【工具】").Length.Should().Be(AutoTitleRules.MaxToolLines + 1);
        excerpt.Should().Contain("step-29").And.Contain("step-20").And.NotContain("step-19");
        excerpt.Should().Contain(Latest + "跑測試");
    }

    [Fact]
    public void 節錄超過上限_仍收最近的要求_這一輪的回覆最新的優先_背景沒有空間就不收()
    {
        var items = new List<AgentChatItem> { User("最早的要求" + new string('早', 400)) };
        for (int i = 1; i <= 3; i++)
            items.Add(User($"要求{i}" + new string('中', 250)));
        items.Add(User("最後這則：把報表匯出成 Excel"));
        for (int i = 0; i < 20; i++)
            items.Add(Reply(new string('長', 700) + i));

        var excerpt = AutoTitleRules.BuildExcerpt(items, running: false, maxChars: 2_500)!;

        var recent = Between(excerpt, "<recent>\n", "\n</recent>\n");
        recent.Should().StartWith("【使用者】要求1").And.Contain("【使用者】要求2").And.Contain("【使用者】要求3").And.Contain($"{Latest}最後這則：把報表匯出成 Excel\n");
        excerpt.Should().NotContain("最早的要求", "第五個要求以前是背景，總長用完就不收").And.NotContain("<earlier>");
        recent.Should().Contain("長19", "這一輪的結論").And.Contain("長18", "其餘最新的優先").And.NotContain("長17");
        excerpt.Length.Should().BeLessThan(2_500 + 100);
    }

    [Fact]
    public void 節錄最近四個要求_之前的只當背景_只收要求與各輪的結論_每則截短_總長有上限()
    {
        var items = new List<AgentChatItem>();
        for (int i = 0; i < 20; i++)
        {
            items.Add(User($"舊的要求{i:00}：" + new string('舊', 200)));
            items.Add(Reply($"中途{i:00}：先看看檔案"));
            items.Add(Tool("讀取檔案", $"file{i:00}.txt"));
            items.Add(Plan(($"舊步驟{i:00}", "completed")));
            items.Add(Reply($"結論{i:00}：做完了。" + new string('A', 1_000)));
        }
        items.Add(User("接著修登入頁的錯誤"));
        items.Add(Reply("先跑測試看看"));
        items.Add(Tool("執行指令", "npm test"));
        items.Add(Reply("登入頁修好了"));

        var excerpt = AutoTitleRules.BuildExcerpt(items, running: false)!;

        var earlier = Between(excerpt, "<earlier>\n", "\n</earlier>\n");
        var recent = Between(excerpt, "<recent>\n", "\n</recent>\n");
        excerpt.IndexOf("</earlier>", StringComparison.Ordinal).Should().BeLessThan(excerpt.IndexOf("<recent>", StringComparison.Ordinal));
        recent.Should().StartWith("【使用者】舊的要求17", "最新的要求加上之前三個").And.Contain("【使用者】舊的要求18").And.Contain("【使用者】舊的要求19")
            .And.Contain($"{Latest}接著修登入頁的錯誤").And.EndWith("【代理】登入頁修好了");
        recent.Should().Contain("【代理】結論17").And.Contain("【代理】結論19").And.NotContain("中途", "更早幾輪只收結論");
        recent.Should().Contain("【代理】先跑測試看看", "最新要求之後的回覆都收").And.Contain("【工具】執行指令：npm test").And.NotContain("讀取檔案", "只收最新要求之後的工具");
        recent.Should().Contain("- [完成] 舊步驟19").And.NotContain("舊步驟18", "只收最新的待辦清單");
        earlier.Should().EndWith("【代理】結論16：做完了。" + new string('A', AutoTitleRules.MaxEarlierEntryChars - "結論16：做完了。".Length));
        earlier.Should().NotContain("中途").And.NotContain("【工具】").And.NotContain("【待辦】", "背景只收要求與各輪的結論");
        earlier.Should().Contain("舊的要求16").And.NotContain("舊的要求00", "背景由新到舊收到上限");
        earlier.Replace("\n\n", string.Empty).Length.Should().BeLessThanOrEqualTo(AutoTitleRules.MaxEarlierChars);
    }

    [Fact]
    public void 這一輪的中途回報很多_前一輪的結論仍收進節錄()
    {
        // 「先做 1、4」要看前一輪的結論才知道指的是什麼；這一輪幾十則中途的回報不能把它擠掉
        var items = new List<AgentChatItem>
        {
            User("幫我看這個專案的計劃，還有哪些未完成"),
            Reply("先讀計畫"),
            Reply("還沒完成的：1. 多電腦同步第 5 階段 2. 安全測試 3. 文件 4. 效能"),
            User("先做1,4"),
        };
        for (int i = 0; i < 40; i++)
            items.Add(Reply($"代理還在跑{i:00}" + new string('等', 300)));

        var recent = Between(AutoTitleRules.BuildExcerpt(items, running: true)!, "<recent>\n", "\n</recent>\n");

        recent.Should().Contain("【代理】還沒完成的：1. 多電腦同步第 5 階段").And.Contain($"{Latest}先做1,4").And.NotContain("先讀計畫");
        recent.Should().Contain("代理還在跑39").And.NotContain("代理還在跑00", "中途的回報由新到舊收到上限");
        recent.IndexOf("還沒完成的", StringComparison.Ordinal).Should().BeLessThan(recent.IndexOf(Latest, StringComparison.Ordinal), "依時間順序");
    }

    [Fact]
    public void 最近的起點_往前數四個要求_只是回應的不算_不夠就從頭()
    {
        var items = new List<AgentChatItem>
        {
            User("要求A"), Reply("a"), User("要求B"), User("好"), User("要求C"), User("繼續"), User("要求D"), User("ok"), User("要求E"), User("1"),
        };
        int task = AutoTitleRules.LatestTaskIndex(items);

        task.Should().Be(8);
        AutoTitleRules.RecentStart(items, 0, task).Should().Be(2, "E、D、C、B 四個要求");
        AutoTitleRules.RecentStart(items, 4, task).Should().Be(4, "清除之後不到四個就從起點");
        AutoTitleRules.RecentStart(items, 0, -1).Should().Be(0);
        var all = new List<AgentChatItem> { User("繼續"), User("好") };
        AutoTitleRules.RecentStart(all, 0, AutoTitleRules.LatestTaskIndex(all)).Should().Be(0, "全都是回應時最新的那則也算一個要求");
    }

    [Fact]
    public void 最新任務是最後一則提出要求的訊息_繼續之類的回應往前找()
    {
        var items = new List<AgentChatItem>
        {
            User("先整理 README"),
            Reply("整理好了"),
            User("把報表匯出成 Excel"),
            Reply("要匯出全部欄位嗎？"),
            User("好，繼續！"),
            Reply("匯出中"),
        };

        AutoTitleRules.LatestTaskIndex(items).Should().Be(2);
        var excerpt = AutoTitleRules.BuildExcerpt(items, running: true)!;
        Between(excerpt, "<recent>\n", "\n</recent>\n").Should().Be(
            $"【使用者】先整理 README\n\n【代理】整理好了\n\n{Latest}把報表匯出成 Excel\n\n【代理】要匯出全部欄位嗎？\n\n【使用者】好，繼續！\n\n【代理】匯出中");
        excerpt.Should().NotContain("<earlier>", "不到四個要求，全部都在最近的工作裡");
    }

    [Fact]
    public void 節錄收已回答的問卷_秘密答案不送_沒回答的不收()
    {
        var answered = new AgentChatItem
        {
            Kind = AgentItemKind.Question,
            Questions =
            {
                new AgentQuestion { Id = "q1", Text = "要先做哪一項？" },
                new AgentQuestion { Id = "q2", Text = "密碼是？", IsSecret = true },
            },
            Answers = { ["q1"] = new List<string> { "同步", "發佈" }, ["q2"] = new List<string> { "hunter2" } },
        };
        var pending = new AgentChatItem { Kind = AgentItemKind.Question, Questions = { new AgentQuestion { Id = "q3", Text = "還沒回答的題目" } } };
        var items = new List<AgentChatItem> { User("規劃下一步"), answered, Reply("好，先做同步"), pending };

        var recent = Between(AutoTitleRules.BuildExcerpt(items, running: true)!, "<recent>\n", "\n</recent>\n");

        recent.Should().Contain("【提問】要先做哪一項？ → 同步、發佈\n密碼是？ → （不公開）").And.NotContain("hunter2").And.NotContain("還沒回答的題目");
    }

    [Fact]
    public void 最新任務_全都是回應時用最後一則_只附檔案的算要求_沒有使用者訊息是負一()
    {
        AutoTitleRules.LatestTaskIndex(new List<AgentChatItem> { User("繼續"), Reply("好的"), User("ok") }).Should().Be(2);
        var attached = new AgentChatItem { Kind = AgentItemKind.User, Text = "好", Attachments = { new AgentAttachment { FileName = "報價單.pdf" } } };
        AutoTitleRules.LatestTaskIndex(new List<AgentChatItem> { User("整理專案"), attached, User("繼續") }).Should().Be(1);
        AutoTitleRules.LatestTaskIndex(new List<AgentChatItem> { Reply("回覆"), Tool("執行指令", "dir") }).Should().Be(-1);
        AutoTitleRules.LatestTaskIndex(new List<AgentChatItem> { User("舊的"), Clear(), User("繼續") }, start: 2).Should().Be(2);
    }

    [Theory]
    [InlineData("繼續", true)]
    [InlineData(" 好的。 ", true)]
    [InlineData("好，繼續！", true)]
    [InlineData("OK", true)]
    [InlineData("ok 👍", true)]
    [InlineData("go on", true)]
    [InlineData("Yes, go ahead", true)]
    [InlineData("1", true)]
    [InlineData("B", true)]
    [InlineData("繼續把測試補完", false)]
    [InlineData("好，但標題要短一點", false)]
    [InlineData("改成右鍵選單", false)]
    [InlineData("123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("👍", false)]
    public void 只是回應的訊息(string? text, bool expected)
    {
        AutoTitleRules.IsFollowUp(text).Should().Be(expected);
    }

    private static string Between(string text, string open, string close)
    {
        int from = text.IndexOf(open, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0, $"要有 {open.Trim()}");
        from += open.Length;
        int to = text.IndexOf(close, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"要有 {close.Trim()}");
        return text[from..to];
    }

    [Fact]
    public void 單則訊息截到上限()
    {
        var excerpt = AutoTitleRules.BuildExcerpt(new List<AgentChatItem> { User(new string('字', 5_000)) }, running: false)!;

        excerpt.Should().Contain(Latest + new string('字', AutoTitleRules.MaxEntryChars)).And.NotContain(new string('字', AutoTitleRules.MaxEntryChars + 1));
    }

    [Fact]
    public void 只附了檔案的使用者訊息_節錄寫檔名()
    {
        var item = new AgentChatItem { Kind = AgentItemKind.User, Attachments = { new AgentAttachment { FileName = "報價單.pdf" } } };

        AutoTitleRules.BuildExcerpt(new List<AgentChatItem> { item }, running: false).Should().Contain("（附件：報價單.pdf）");
    }

    [Fact]
    public void 退而求其次_最後一則使用者訊息之後有進行中的待辦步驟就用它()
    {
        var items = new List<AgentChatItem>
        {
            User("整理專案"),
            Plan(("盤點檔案", "completed"), ("改寫 README", "inProgress"), ("提交", "pending")),
        };

        AutoTitleRules.Fallback(items).Should().Be("改寫 README");
    }

    [Fact]
    public void 退而求其次_沒有進行中的步驟_用最後一則使用者訊息的第一行()
    {
        var items = new List<AgentChatItem>
        {
            User("第一個工作"),
            Plan(("舊的步驟", "inProgress")),
            User("  幫我把圖表改成長條圖\n細節之後再說"),
            Plan(("改圖表", "completed")),
            Reply("改好了"),
        };

        AutoTitleRules.Fallback(items).Should().Be("幫我把圖表改成長條圖", "較早的待辦清單不是現在在做的事");
    }

    [Fact]
    public void 退而求其次_最後一則只是回應時用它延續的要求()
    {
        var withPlan = new List<AgentChatItem>
        {
            User("把報表匯出成 Excel"),
            Plan(("加匯出按鈕", "inProgress"), ("寫測試", "pending")),
            Reply("要匯出全部欄位嗎？"),
            User("好"),
        };
        AutoTitleRules.Fallback(withPlan).Should().Be("加匯出按鈕", "回應之前的待辦清單仍是這個任務的");

        AutoTitleRules.Fallback(new List<AgentChatItem> { User("把報表匯出成 Excel"), Reply("完成"), User("繼續") }).Should().Be("把報表匯出成 Excel");
        AutoTitleRules.Fallback(new List<AgentChatItem> { User("繼續") }).Should().Be("繼續", "全都是回應時用最後一則");
    }

    [Fact]
    public void 指示說明節錄的兩段與最新要求的標記_要寫主要任務而不是最新的步驟()
    {
        AutoTitleRules.Instructions.Should().Contain("<recent>").And.Contain("<earlier>").And.Contain("<conversation>").And.Contain("「最新的要求」");
        AutoTitleRules.LatestRequestLabel.Should().Contain("最新的要求");
        AutoTitleRules.Instructions.Should().Contain("主要任務").And.Contain("不要照抄或改寫最新一則訊息");
    }

    [Fact]
    public void 退而求其次_只收清除之後的_沒有使用者訊息回null()
    {
        AutoTitleRules.Fallback(new List<AgentChatItem> { User("舊的"), Clear(), Reply("回覆") }).Should().BeNull();
        AutoTitleRules.Fallback(new List<AgentChatItem>()).Should().BeNull();
        AutoTitleRules.Fallback(new List<AgentChatItem> { User(new string('長', 60)) }).Should().Be(new string('長', AgentHubRules.MaxTitleLength) + "…");
    }

    [Theory]
    [InlineData("修正登入頁的錯誤", "修正登入頁的錯誤")]
    [InlineData("  「修正登入頁的錯誤」。\n", "修正登入頁的錯誤")]
    [InlineData("標題：修正登入頁的錯誤", "修正登入頁的錯誤")]
    [InlineData("Title: Fix the login page", "Fix the login page")]
    [InlineData("# **修正登入頁**", "修正登入頁")]
    [InlineData("```\n修正登入頁\n```", "修正登入頁")]
    [InlineData("\n\n- 匯出報表   成 Excel\n說明：這是第二行", "匯出報表 成 Excel")]
    [InlineData("\"Fix login\"", "Fix login")]
    [InlineData("這段對話的最新任務是：調整標題規則", "調整標題規則")]
    [InlineData("最新任務：修正登入頁", "修正登入頁")]
    [InlineData("這個對話目前的主要任務是：修正登入頁", "修正登入頁")]
    [InlineData("主要任務：匯出報表", "匯出報表")]
    [InlineData("調整對話視窗的「自動重新命名」規則，讓標題更聚焦在輸入框最新的任務摘要上", "調整對話視窗的「自動重新命名」規則")]
    [InlineData("Reels 發佈，Threads 待補", "Reels 發佈，Threads 待補")]
    [InlineData("Fix the login page timeout, then add retry logic to the payment API", "Fix the login page timeout")]
    [InlineData("修正\u200b登入頁\t錯誤", "修正 登入頁 錯誤")]
    public void 整理模型的回覆(string reply, string expected)
    {
        AutoTitleRules.CleanTitle(reply).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    [InlineData("「」")]
    [InlineData("```\n```")]
    public void 整理後是空的回null(string? reply)
    {
        AutoTitleRules.CleanTitle(reply).Should().BeNull();
    }

    [Fact]
    public void 標題截到上限_不切開代理字對()
    {
        AutoTitleRules.CleanTitle(new string('字', 100))!.Length.Should().Be(AutoTitleRules.MaxTitleLength);
        var emoji = new string('字', AutoTitleRules.MaxTitleLength - 1) + "😀";
        AutoTitleRules.CleanTitle(emoji).Should().Be(new string('字', AutoTitleRules.MaxTitleLength - 1));
    }

    [Fact]
    public void Claude的參數_提示從stdin讀_sonnet_不帶工具_精簡模式_不留紀錄()
    {
        var args = AutoTitleRules.BuildClaudeArgs();

        AutoTitleRules.ClaudeModel.Should().Be("sonnet");
        args[0].Should().Be("-p");
        args.Should().ContainInOrder("--model", "sonnet").And.ContainInOrder("--output-format", "json").And.Contain("--no-session-persistence")
            .And.ContainInOrder("--safe-mode", "--setting-sources", "project,local").And.ContainInOrder("--system-prompt", AutoTitleRules.Instructions);
        args.TakeLast(2).Should().Equal(new[] { "--tools", "" }, "--tools 是可變長度選項，放最後（後面沒有位置參數）");
    }

    [Fact]
    public void Codex的參數_不留紀錄_唯讀沙箱_低推理強度_回覆寫進檔案_指示是最後的位置參數()
    {
        var args = AutoTitleRules.BuildCodexArgs(@"C:\temp\out.txt");

        args[0].Should().Be("exec");
        args.Should().Contain("--ephemeral").And.Contain("--skip-git-repo-check").And.ContainInOrder("--sandbox", "read-only")
            .And.ContainInOrder("-c", "model_reasoning_effort=\"low\"").And.ContainInOrder("--output-last-message", @"C:\temp\out.txt");
        args[^1].Should().Be(AutoTitleRules.Instructions);
    }

    [Fact]
    public void Claude的結果_成功取result_失敗說明原因()
    {
        AutoTitleRules.ParseClaude(new CliRunResult(0, "{\"type\":\"result\",\"is_error\":false,\"result\":\"「修正登入頁」\"}", "", false))
            .Should().Be(new AutoTitleResult("修正登入頁", null));
        AutoTitleRules.ParseClaude(new CliRunResult(1, "{\"type\":\"result\",\"is_error\":true,\"result\":\"Credit balance is too low\"}", "", false))
            .Should().Be(new AutoTitleResult(null, "結束碼 1：Credit balance is too low"));
        AutoTitleRules.ParseClaude(new CliRunResult(0, "{\"type\":\"result\",\"is_error\":false,\"result\":\"  \"}", "", false))
            .Should().Be(new AutoTitleResult(null, "模型沒有回標題"));
        AutoTitleRules.ParseClaude(new CliRunResult(1, "", "Not logged in\nPlease run /login", false))
            .Should().Be(new AutoTitleResult(null, "結束碼 1：Not logged in"));
        AutoTitleRules.ParseClaude(new CliRunResult(-1, "", "執行逾時", true)).Should().Be(new AutoTitleResult(null, "執行逾時"));
    }

    [Fact]
    public void Codex的結果_成功取回覆檔_失敗取stderr的錯誤行()
    {
        AutoTitleRules.ParseCodex(new CliRunResult(0, "進度文字", "", false), "匯出報表\n").Should().Be(new AutoTitleResult("匯出報表", null));
        AutoTitleRules.ParseCodex(new CliRunResult(0, "", "", false), null).Should().Be(new AutoTitleResult(null, "模型沒有回標題"));
        AutoTitleRules.ParseCodex(new CliRunResult(1, "", "OpenAI Codex v0.163\nERROR: unexpected status 401 Unauthorized\n", false), null)
            .Should().Be(new AutoTitleResult(null, "結束碼 1：ERROR: unexpected status 401 Unauthorized"));
        AutoTitleRules.ParseCodex(new CliRunResult(2, "", "something odd\nlast line\n", false), null).Should().Be(new AutoTitleResult(null, "結束碼 2：last line"));
        AutoTitleRules.ParseCodex(new CliRunResult(-1, "", "執行逾時", true), "x").Should().Be(new AutoTitleResult(null, "執行逾時"));
    }

    [Fact]
    public void Antigravity與Grok的參數與結果判讀()
    {
        var agyArgs = AutoTitleRules.BuildAntigravityArgs("<conversation>...</conversation>", @"C:\temp\agy.log");
        agyArgs.Take(2).Should().Equal("--log-file", @"C:\temp\agy.log");
        agyArgs.Should().ContainInOrder("--output-format", "json")
            .And.Contain("--disable-slash-commands")
            .And.Contain("--sandbox")
            .And.ContainInOrder("--print-timeout", "20s")
            .And.ContainInOrder("--mode", "plan")
            .And.ContainInOrder("--effort", "low");
        agyArgs[^2].Should().Be("-p");
        agyArgs[^1].Should().StartWith(AutoTitleRules.Instructions);

        // Antigravity 未指定 --model（可能使用 Claude/GPT 預設模型）：任一額度窗鎖定時借用應略過
        var agy3pLocked = new AutoTitleCandidate(
            "agy-locked",
            AgentProvider.Antigravity,
            AccountStatus.Ready,
            new[] { new UsageWindow("agy-3p-5h", "5 小時・Claude/GPT", 100, Now.AddHours(2), Locked: true) },
            0);
        AutoTitleRules.PlanAccounts("claude-own", new[] { Candidates()[3], agy3pLocked }, Exhausted, Now)
            .Should().Equal("claude-own");

        var agyOk = AutoTitleRules.ParseAntigravity(
            new CliRunResult(0, """{"conversation_id":"11111111-2222-3333-4444-555555555555","status":"SUCCESS","response":"「修正登入頁」"}""", "", false),
            out var convId);
        agyOk.Should().Be(new AutoTitleResult("修正登入頁", null));
        convId.Should().Be("11111111-2222-3333-4444-555555555555");

        AutoTitleRules.ParseAntigravity(
            new CliRunResult(1, """{"conversation_id":"11111111-2222-3333-4444-555555555555","status":"ERROR","error":"authentication required"}""", "", false))
            .Should().Be(new AutoTitleResult(null, "結束碼 1：authentication required"));

        var grokArgs = AutoTitleRules.BuildGrokArgs(@"C:\temp\prompt.txt", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        grokArgs.Should().NotContain("-p", "Grok CLI 不可同時帶 -p 與 --prompt-file")
            .And.ContainInOrder("--prompt-file", @"C:\temp\prompt.txt")
            .And.ContainInOrder("-s", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
            .And.ContainInOrder("--system-prompt-override", AutoTitleRules.Instructions)
            .And.ContainInOrder("--reasoning-effort", "low")
            .And.ContainInOrder("--tools", "");

        AutoTitleRules.ParseGrok(new CliRunResult(0, """{"type":"result","is_error":false,"result":"「匯出 Excel 報表」"}""", "", false))
            .Should().Be(new AutoTitleResult("匯出 Excel 報表", null));
        AutoTitleRules.ParseGrok(new CliRunResult(0, "匯出 Excel 報表\n", "", false))
            .Should().Be(new AutoTitleResult("匯出 Excel 報表", null));
    }
}
