using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>中樞的純函式規則：標題、分組、工具輸出、背景說明、本機路徑辨識、前文節錄、閒置回收、最近資料夾</summary>
public class AgentHubRulesTests
{
    [Fact]
    public void DescribeRemoteUserMessage_附圖或檔案時補一行數量()
    {
        AgentHubRules.DescribeRemoteUserMessage(" 這是什麼 \n", 0).Should().Be("這是什麼");
        AgentHubRules.DescribeRemoteUserMessage("這是什麼", 2).Should().Be("這是什麼\n\n（附 2 張圖片，請在 claude.ai 或 Claude App 查看）");
        AgentHubRules.DescribeRemoteUserMessage("", 1).Should().Be("（附 1 張圖片，請在 claude.ai 或 Claude App 查看）");
        AgentHubRules.DescribeRemoteUserMessage(null, 0).Should().BeEmpty();
        AgentHubRules.DescribeRemoteUserMessage("看這份", 0, 1).Should().Be("看這份\n\n（附 1 個檔案，請在 claude.ai 或 Claude App 查看）");
        AgentHubRules.DescribeRemoteUserMessage(null, 1, 2).Should().Be("（附 1 張圖片、2 個檔案，請在 claude.ai 或 Claude App 查看）");
    }

    [Fact]
    public void NormalizeContinueText_去頭尾空白_空的或等於預設存成null_太長截斷()
    {
        AgentHubRules.DefaultContinueText.Should().Be("進度如何呢如果還沒做完就繼續");
        AgentHubRules.NormalizeContinueText("  做完了嗎？沒做完就接著做  ").Should().Be("做完了嗎？沒做完就接著做");
        AgentHubRules.NormalizeContinueText("第一行\n第二行").Should().Be("第一行\n第二行", "可以多行");
        AgentHubRules.NormalizeContinueText(" " + AgentHubRules.DefaultContinueText + "\n").Should().BeNull("改回預設內容就存成 null");
        AgentHubRules.NormalizeContinueText("  \r\n\t").Should().BeNull();
        AgentHubRules.NormalizeContinueText(null).Should().BeNull();
        AgentHubRules.NormalizeContinueText(new string('字', AgentHubRules.ContinueTextMaxLength + 5)).Should().Be(new string('字', AgentHubRules.ContinueTextMaxLength));
        AgentHubRules.NormalizeContinueText(new string('字', AgentHubRules.ContinueTextMaxLength)).Should().HaveLength(AgentHubRules.ContinueTextMaxLength, "剛好等於上限不截斷");
        // 上限剛好落在代理字元對中間：整個字元不要
        var emoji = new string('字', AgentHubRules.ContinueTextMaxLength - 1) + "😀";
        AgentHubRules.NormalizeContinueText(emoji).Should().Be(new string('字', AgentHubRules.ContinueTextMaxLength - 1));
    }

    [Fact]
    public void MakeTitle_取第一行並壓縮空白_太長截斷_沒有文字用附件名()
    {
        AgentHubRules.MakeTitle("\n  幫我   整理下載資料夾 \n第二行").Should().Be("幫我 整理下載資料夾");
        AgentHubRules.MakeTitle(new string('字', 50)).Should().Be(new string('字', AgentHubRules.MaxTitleLength) + "…");
        AgentHubRules.MakeTitle(new string('字', AgentHubRules.MaxTitleLength)).Should().Be(new string('字', AgentHubRules.MaxTitleLength), "剛好等於上限不截斷");
        AgentHubRules.MakeTitle("", new[] { new AgentAttachment { FileName = "報表.xlsx" } }).Should().Be("報表.xlsx");
        AgentHubRules.MakeTitle("  \r\n\t", null).Should().Be("新對話");
        AgentHubRules.MakeTitle(null).Should().Be("新對話");
    }

    [Fact]
    public void GroupLabel_像ChatGPT一樣依日期分組()
    {
        var now = new DateTime(2026, 10, 8, 10, 0, 0);
        AgentHubRules.GetGroupLabel(now.AddHours(-9), now).Should().Be("今天");
        AgentHubRules.GetGroupLabel(now.AddHours(3), now).Should().Be("今天", "時間在未來（時鐘調整）也算今天");
        AgentHubRules.GetGroupLabel(new DateTime(2026, 10, 7, 23, 0, 0), now).Should().Be("昨天");
        AgentHubRules.GetGroupLabel(new DateTime(2026, 10, 1), now).Should().Be("過去 7 天", "剛好 7 天前");
        AgentHubRules.GetGroupLabel(new DateTime(2026, 9, 30), now).Should().Be("過去 30 天", "8 天前");
        AgentHubRules.GetGroupLabel(new DateTime(2026, 9, 8), now).Should().Be("過去 30 天", "剛好 30 天前");
        AgentHubRules.GetGroupLabel(new DateTime(2026, 9, 7), now).Should().Be("9 月");
        AgentHubRules.GetGroupLabel(new DateTime(2026, 6, 1), now).Should().Be("6 月");
        AgentHubRules.GetGroupLabel(new DateTime(2025, 12, 1), now).Should().Be("2025 年 12 月");
    }

    [Fact]
    public void TrimToolOutput_只保留尾段並註明省略多少()
    {
        var output = new string('a', 20_000) + "最後";
        var trimmed = AgentHubRules.TrimToolOutput(output)!;
        trimmed.Should().EndWith("最後");
        trimmed.Should().StartWith("…（前 8,002 字省略）");
        trimmed.Length.Should().BeLessThan(AgentHubRules.MaxToolOutputChars + 40);
        AgentHubRules.TrimToolOutput("短").Should().Be("短");
        AgentHubRules.TrimToolOutput(null).Should().BeNull();
    }

    [Fact]
    public void AppendToolOutput_串流中多次截斷_省略字數累計_前綴不算內容()
    {
        // 審查發現：每次把已截斷的輸出（含前綴）接上新片段再截斷，省略字數只算到最後一次的差額（10 萬字顯示「前 1,015 字省略」）
        string? shown = null;
        var all = new System.Text.StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            var chunk = new string((char)('a' + i % 26), 999) + "\n";
            all.Append(chunk);
            shown = AgentHubRules.AppendToolOutput(shown, chunk);
            if (i == 12)
                shown.Should().StartWith("…（前 1,000 字省略）\n", "第一次超過上限");
        }

        var full = all.ToString();
        full.Length.Should().Be(100_000);
        shown.Should().Be("…（前 88,000 字省略）\n" + full[^AgentHubRules.MaxToolOutputChars..]);
        AgentHubRules.AppendToolOutput(null, "短").Should().Be("短");
        AgentHubRules.AppendToolOutput("前面", "後面").Should().Be("前面後面");
        AgentHubRules.AppendToolOutput(AgentHubRules.TrimToolOutput(new string('x', 20_000)), "尾").Should().StartWith("…（前 8,001 字省略）\n").And.EndWith("尾");
    }

    [Fact]
    public void OmittedPrefix與OmittedCount_和截斷的前綴同一個格式()
    {
        // WP4.3 的 PublishFitRules 截斷要上傳的工具輸出時沿用同一個前綴（CredentialLeakRules.MaskTruncated 靠它認出切點）
        var trimmed = AgentHubRules.TrimToolOutput(new string('x', AgentHubRules.MaxToolOutputChars + 1_234_567))!;
        trimmed.Should().StartWith(AgentHubRules.OmittedPrefix(1_234_567));
        AgentHubRules.OmittedPrefix(1_234_567).Should().Be("…（前 1,234,567 字省略）\n");
        AgentHubRules.OmittedPrefixLength(trimmed).Should().Be(AgentHubRules.OmittedPrefix(1_234_567).Length);
        AgentHubRules.OmittedCount(trimmed).Should().Be(1_234_567);
        AgentHubRules.OmittedCount(AgentHubRules.OmittedPrefix(5) + "內容").Should().Be(5);
        AgentHubRules.OmittedCount("沒有截斷").Should().Be(0);
        AgentHubRules.OmittedCount("…（前 很多 字省略）\n").Should().Be(0, "數字讀不懂");
        AgentHubRules.OmittedCount(null).Should().Be(0);
    }

    [Fact]
    public void BuildAgentInstructions_預設範本代換工具名稱與資料夾()
    {
        var claude = AgentHubRules.BuildAgentInstructions(null, AgentProvider.Claude, @"C:\w", @"C:\c\attachments");
        claude.Should().Contain("AskUserQuestion").And.Contain(@"工作資料夾：C:\w").And.Contain(@"C:\c\attachments").And.Contain("臺灣繁體中文");
        claude.Should().NotContain("{cwd}").And.NotContain("{attachments}").And.NotContain("{questionTool}");

        var codex = AgentHubRules.BuildAgentInstructions("   ", AgentProvider.Codex, @"C:\w", @"C:\a");
        codex.Should().Contain("request_user_input").And.NotContain("AskUserQuestion");
    }

    [Fact]
    public void BuildAgentInstructions_長時間指令用各家的背景機制_不要另開()
    {
        // 代理以 Start-Process 另開的程式 CLI 不追蹤、跑完也不叫回代理（2026-10-24）：說明裡指名各家會回報的背景機制
        var claude = AgentHubRules.BuildAgentInstructions(null, AgentProvider.Claude, @"C:\w", @"C:\a");
        claude.Should().Contain("run_in_background").And.Contain("Start-Process").And.NotContain("{backgroundTool}");
        AgentHubRules.BuildAgentInstructions(null, AgentProvider.Codex, @"C:\w", @"C:\a").Should().Contain("終端機的工作階段").And.NotContain("run_in_background");
        foreach (var provider in new[] { AgentProvider.Antigravity, AgentProvider.Grok })
            AgentHubRules.BuildAgentInstructions(null, provider, @"C:\w", @"C:\a").Should().Contain("直接執行並等它結束");

        AgentHubRules.BuildAgentInstructions("背景：{backgroundTool}", AgentProvider.Claude, @"C:\w", @"C:\a")
            .Should().Be("背景：用 Bash 工具的 run_in_background 在背景執行（跑完會通知你）", "自訂範本也代換");
        foreach (var provider in AgentProviders.All)
            AgentHubRules.BackgroundToolHint(provider).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void BuildAgentInstructions_自訂範本也代換()
    {
        AgentHubRules.BuildAgentInstructions("工作：{cwd}／附件：{attachments}／提問：{questionTool}／沒有的 {other} 保留", AgentProvider.Claude, @"D:\proj", @"D:\att")
            .Should().Be(@"工作：D:\proj／附件：D:\att／提問：AskUserQuestion／沒有的 {other} 保留");
    }

    [Fact]
    public void BuildAgentInstructions_代入的路徑含預留字樣時不再被代換()
    {
        // 審查發現：依序代換時，工作資料夾 C:\work\{attachments}（合法的資料夾名稱）會被第二次代換成附件資料夾
        var text = AgentHubRules.BuildAgentInstructions(null, AgentProvider.Claude, @"C:\work\{attachments}", @"C:\data\att");
        text.Should().Contain(@"工作資料夾：C:\work\{attachments}" + "\n").And.Contain(@"使用者上傳的附件放在：C:\data\att");

        AgentHubRules.BuildAgentInstructions("{cwd}|{attachments}|{questionTool}", AgentProvider.Claude, @"D:\{questionTool}", @"D:\{cwd}")
            .Should().Be(@"D:\{questionTool}|D:\{cwd}|AskUserQuestion");
    }

    [Fact]
    public void BuildAgentInstructions_Codex的工作資料夾註明之後以每一輪的cwd為準()
    {
        // Codex 只在建立 thread 時收到背景說明，之後換工作資料夾也不會更新；說明裡的路徑要註明以每一輪的 cwd 為準，才不會和 turn/start 的 cwd 矛盾
        var codex = AgentHubRules.BuildAgentInstructions(null, AgentProvider.Codex, @"C:\repoA", @"C:\c\attachments");
        codex.Should().Contain(@"工作資料夾：C:\repoA" + AgentHubRules.CodexWorkingDirectoryNote + "\n");
        AgentHubRules.CodexWorkingDirectoryNote.Should().Contain("cwd");

        AgentHubRules.BuildAgentInstructions(null, AgentProvider.Claude, @"C:\repoA", @"C:\a")
            .Should().Contain(@"工作資料夾：C:\repoA" + "\n", "Claude 換資料夾會重開程序、帶新的說明")
            .And.NotContain(AgentHubRules.CodexWorkingDirectoryNote);
    }

    [Fact]
    public void BuildDenyFeedbackMessage_一則照原格式_多則依序列出操作與說明()
    {
        AgentHubRules.BuildDenyFeedbackMessage(Array.Empty<DeniedOperation>()).Should().BeNull();
        AgentHubRules.BuildDenyFeedbackMessage(new[] { new DeniedOperation("修改檔案", @"C:\w\a.cs", "不要改 A 的公開介面") })
            .Should().Be("（我剛才拒絕了那個操作）不要改 A 的公開介面");

        // 審查發現：同一輪拒絕多次並附說明時只送出最後一則；要依序全部送出，並標示對應的操作
        AgentHubRules.BuildDenyFeedbackMessage(new[]
            {
                new DeniedOperation("修改檔案", "C:\\w\\a.cs\n第二行", "不要改 A 的公開介面"),
                new DeniedOperation(null, "  ", "B 也先不要動"),
                new DeniedOperation("執行指令", null, "改用資源回收筒"),
            })
            .Should().Be("（我剛才拒絕了這幾個操作）\n1. 「修改檔案：C:\\w\\a.cs」：不要改 A 的公開介面\n2. B 也先不要動\n3. 「執行指令」：改用資源回收筒");
    }

    [Fact]
    public void ExtractLocalPaths_找出磁碟路徑_不認UNC_去掉結尾標點()
    {
        var text = @"已存檔：C:\out\chart.png。另見 `C:\My Files\報表 1.xlsx` 與 \\server\share\x.png，以及 D:/data/a.csv）";
        AgentHubRules.ExtractLocalPaths(text).Should().Equal(@"C:\out\chart.png", @"C:\My", @"D:\data\a.csv");
        AgentHubRules.ExtractCodeSpanPaths(text).Should().Equal(@"C:\My Files\報表 1.xlsx");
    }

    [Fact]
    public void ExtractLocalPaths_重複只算一次_網址與檔案網址裡的路徑不算()
    {
        AgentHubRules.ExtractLocalPaths(@"C:\a\b.png 與 c:\A\B.png、file:///C:/x/y.png、https://h/C:/z.png").Should().Equal(@"C:\a\b.png");
        AgentHubRules.ExtractLocalPaths(@"磁碟 C:\ 本身太短").Should().BeEmpty();
        AgentHubRules.ExtractLocalPaths(null).Should().BeEmpty();
        AgentHubRules.ExtractCodeSpanPaths(null).Should().BeEmpty();
    }

    [Fact]
    public void FindMediaToShow_存在且可顯示_Markdown已顯示的不重複()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\out\a.png", @"C:\out\b.png", @"C:\out\c.mp4", @"C:\out\d.xlsx" };
        var text = @"圖：C:\out\a.png、![b](C:\out\b.png)、影片 `C:\out\c.mp4`、報表 C:\out\d.xlsx、不存在 C:\out\z.png";

        AgentHubRules.FindMediaToShow(text, existing.Contains).Should().Equal(@"C:\out\c.mp4", @"C:\out\a.png");
    }

    [Fact]
    public void ExtractRelativeMediaPaths_只收可顯示媒體的相對路徑_網址與完整路徑不收()
    {
        var text = "成片：videos/1.mp4\n`images\\a.png`、./out/b.jpg、../up/c.webp，報表 out/d.xlsx，"
                   + "網址 https://x.com/e.png、完整路徑 C:\\f\\g.png、/root/h.png、19.5 秒、videos/1.mp4 再一次、示範_外套.mp4。";

        AgentHubRules.ExtractRelativeMediaPaths(text).Should().Equal(
            @"videos\1.mp4", @"images\a.png", @".\out\b.jpg", @"..\up\c.webp", "示範_外套.mp4");
        AgentHubRules.ExtractRelativeMediaPaths(null).Should().BeEmpty();
    }

    [Fact]
    public void FindMediaToShow_相對路徑依序以基準資料夾解析_跑出基準的不收()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\grok\s1\videos\1.mp4", @"C:\work\videos\1.mp4", @"C:\work\videos\2.mp4", @"C:\secret.png", @"C:\work\pic.png",
        };
        var text = "videos/1.mp4、videos/2.mp4、videos/3.mp4、../../secret.png、![圖](pic.png)、[影片](videos/2.mp4)";

        // 工作階段資料夾優先；Markdown 圖片在工作資料夾找得到的由 Markdown 顯示；Markdown 連結在那裡找得到的也是（同完整路徑的規則）；跑出基準的不收
        AgentHubRules.FindMediaToShow(text, existing.Contains, [@"C:\grok\s1", @"C:\work"], @"C:\work")
            .Should().Equal(@"C:\grok\s1\videos\1.mp4");
        // 沒給基準（其他代理）：相對路徑照舊不找
        AgentHubRules.FindMediaToShow(text, existing.Contains).Should().BeEmpty();
        // 基準是 null、UNC：略過、不查存在
        var checkedPaths = new List<string>();
        AgentHubRules.FindMediaToShow("videos/1.mp4", p => { checkedPaths.Add(p); return false; }, [null, @"\\evil\share"], null).Should().BeEmpty();
        checkedPaths.Should().BeEmpty();
    }

    [Fact]
    public void FindMediaToShow_UNC路徑連存在都不查()
    {
        var checkedPaths = new List<string>();
        AgentHubRules.FindMediaToShow(@"\\evil\share\a.png 與 `\\evil\share\b.png`", p => { checkedPaths.Add(p); return true; }).Should().BeEmpty();
        checkedPaths.Should().BeEmpty("檢查 UNC 路徑存不存在就會對外送出 Windows 登入雜湊");
        AgentHubRules.FindMediaToShow(null, _ => true).Should().BeEmpty();
    }

    [Fact]
    public void HistoryRecap_由新到舊收到上限再依時間排回()
    {
        var items = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.User, Text = "第一題" },
            new() { Kind = AgentItemKind.Assistant, Text = "第一答" },
            new() { Kind = AgentItemKind.Tool, Title = "ls" },
            new() { Kind = AgentItemKind.Notice, Text = "已從 A 改用 B" },
            new() { Kind = AgentItemKind.User, Text = "", Attachments = new List<AgentAttachment> { new() { FileName = "a.png" }, new() { FileName = "b.pdf" } } },
            new() { Kind = AgentItemKind.Assistant, Text = "第二答" },
        };

        var recap = AgentHubRules.BuildHistoryRecap(items)!;
        recap.Should().StartWith("（系統說明：原本的對話紀錄已無法續接");
        recap.Should().Contain("【使用者】第一題").And.Contain("【代理】第一答");
        recap.IndexOf("第一題", StringComparison.Ordinal).Should().BeLessThan(recap.IndexOf("第二答", StringComparison.Ordinal));
        recap.Should().Contain("（附件：a.png、b.pdf）");
        recap.Should().NotContain("ls").And.NotContain("改用");
        recap.Should().EndWith("（節錄結束）\n\n");

        var limited = AgentHubRules.BuildHistoryRecap(items, maxChars: 20)!;
        limited.Should().Contain("第二答");
        limited.Should().NotContain("第一題");
        AgentHubRules.BuildHistoryRecap(new List<AgentChatItem>()).Should().BeNull();
        AgentHubRules.BuildHistoryRecap(new List<AgentChatItem> { new() { Kind = AgentItemKind.Tool, Title = "x" } }).Should().BeNull("沒有可用內容");
    }

    [Fact]
    public void HistoryRecap_單則太長截到兩千字()
    {
        var recap = AgentHubRules.BuildHistoryRecap(new List<AgentChatItem> { new() { Kind = AgentItemKind.Assistant, Text = new string('長', 5000) } })!;
        recap.Should().Contain("…（略）");
        recap.Length.Should().BeLessThan(2200);
    }

    [Fact]
    public void HistoryRecap_只收最後一次clear之後的()
    {
        AgentChatItem Clear() => new() { Kind = AgentItemKind.Notice, Text = AgentHubRules.ClearNoticeText, NoticeKind = AgentNoticeKind.Clear };
        var items = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.User, Text = "清除前的問題" },
            Clear(),
            new() { Kind = AgentItemKind.Assistant, Text = "第一次清除之後" },
            Clear(),
            new() { Kind = AgentItemKind.User, Text = "清除後的問題" },
            new() { Kind = AgentItemKind.Notice, Text = "一般提示不是分界", NoticeKind = AgentNoticeKind.CliUpdate },
            new() { Kind = AgentItemKind.Assistant, Text = "清除後的回答" },
        };

        var recap = AgentHubRules.BuildHistoryRecap(items)!;
        recap.Should().Contain("清除後的問題").And.Contain("清除後的回答");
        recap.Should().NotContain("清除前的問題").And.NotContain("第一次清除之後");
        AgentHubRules.BuildHistoryRecap(items.Take(4).ToList()).Should().BeNull("最後一則就是清除提示：沒有前文");
    }

    [Fact]
    public void Clear指令_只認整則是小寫的clear_前後空白不算()
    {
        AgentHubRules.IsClearCommand("/clear").Should().BeTrue();
        AgentHubRules.IsClearCommand("  /clear\n").Should().BeTrue();
        foreach (var other in new[] { null, "", "clear", "/Clear", "/CLEAR", "/clear 一下", "/clearer", "請 /clear", "/compact" })
            AgentHubRules.IsClearCommand(other).Should().BeFalse(other ?? "null");

        AgentHubRules.IsClearNotice(new AgentChatItem { Kind = AgentItemKind.Notice, NoticeKind = AgentNoticeKind.Clear }).Should().BeTrue();
        AgentHubRules.IsClearNotice(new AgentChatItem { Kind = AgentItemKind.Error, NoticeKind = AgentNoticeKind.Clear }).Should().BeFalse("只有 Notice 是分界");
        AgentHubRules.IsClearNotice(new AgentChatItem { Kind = AgentItemKind.Notice, Text = AgentHubRules.ClearNoticeText }).Should().BeFalse("看種類不看文字");
    }

    [Fact]
    public void 最近的指令_最後一則使用者訊息_之後清除過就沒有()
    {
        AgentChatItem Clear() => new() { Kind = AgentItemKind.Notice, Text = AgentHubRules.ClearNoticeText, NoticeKind = AgentNoticeKind.Clear };
        var first = new AgentChatItem { Kind = AgentItemKind.User, Text = "第一個問題" };
        var second = new AgentChatItem { Kind = AgentItemKind.User, Text = "第二個問題" };
        var items = new List<AgentChatItem>
        {
            first,
            new() { Kind = AgentItemKind.Assistant, Text = "回答" },
            second,
            new() { Kind = AgentItemKind.Tool, Title = "npm test" },
            new() { Kind = AgentItemKind.Notice, Text = "一般提示不是分界", NoticeKind = AgentNoticeKind.CliUpdate },
            new() { Kind = AgentItemKind.Assistant, Text = "回答" },
        };

        AgentHubRules.LastPrompt(items).Should().BeSameAs(second, "隔著工具、提示與回覆也找得到");
        AgentHubRules.LastPrompt(items.Take(2).ToList()).Should().BeSameAs(first);
        items.Add(Clear());
        AgentHubRules.LastPrompt(items).Should().BeNull("清除之後還沒送新的訊息");
        var third = new AgentChatItem { Kind = AgentItemKind.User, Text = "清除後的問題" };
        items.Add(third);
        AgentHubRules.LastPrompt(items).Should().BeSameAs(third);
        AgentHubRules.LastPrompt([new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "只有回覆" }]).Should().BeNull();
        AgentHubRules.LastPrompt([]).Should().BeNull();
        AgentHubRules.LastPrompt(null).Should().BeNull();
    }

    [Fact]
    public void 最近的指令_文字超過上限才截斷_不切開代理字對()
    {
        AgentHubRules.LastPromptText(null).Should().BeEmpty();
        AgentHubRules.LastPromptText("短").Should().Be("短");
        AgentHubRules.LastPromptText(new string('a', 5), 5).Should().Be("aaaaa", "剛好等於上限不截");
        AgentHubRules.LastPromptText("abcdefg", 5).Should().Be("abcde…");
        AgentHubRules.LastPromptText("abcd😀ef", 5).Should().Be("abcd…", "第 5 個字元是代理字對的前半：連同它一起捨掉");
        AgentHubRules.LastPromptText(new string('x', AgentHubRules.LastPromptMaxChars + 10)).Should().HaveLength(AgentHubRules.LastPromptMaxChars + 1);
    }

    [Fact]
    public void 歷史指令_由新到舊最多10則_清除之前的也列_略過空白太長語音與重複()
    {
        AgentChatItem User(string text, int files = 0)
        {
            var item = new AgentChatItem { Kind = AgentItemKind.User, Text = text };
            for (int i = 0; i < files; i++)
                item.Attachments.Add(new AgentAttachment { FileName = $"f{i}.png" });
            return item;
        }
        var oldest = User("清除之前的指令");
        var dup = User("重複的指令");
        var items = new List<AgentChatItem>
        {
            oldest,
            new() { Kind = AgentItemKind.Notice, Text = AgentHubRules.ClearNoticeText, NoticeKind = AgentNoticeKind.Clear },
            dup,
            new() { Kind = AgentItemKind.Assistant, Text = "回答不算" },
            User("  ", files: 1),
            User("語音訊息（0:12）", files: 1),
            User(new string('x', AgentHubRules.PromptHistoryMaxChars + 1)),
            User("有附件的指令", files: 2),
            User("重複的指令"),
        };

        var list = AgentHubRules.RecentPrompts(items);
        list.Select(i => i.Text).Should().Equal(["重複的指令", "有附件的指令", "清除之前的指令"], "清除之前的也是送過的指令；重複的只留最新的");
        list[0].Should().BeSameAs(items[^1], "重複的留最新那一則（時間、附件照它）");
        list[1].Attachments.Should().HaveCount(2);
        AgentHubRules.RecentPrompts([User("語音訊息（0:12）")]).Should().ContainSingle("沒有附件的同名文字是使用者自己打的");

        var many = Enumerable.Range(1, 15).Select(i => User($"第 {i} 則")).ToList();
        AgentHubRules.RecentPrompts(many).Select(i => i.Text).Should().Equal(Enumerable.Range(6, 10).Reverse().Select(i => $"第 {i} 則"));
        AgentHubRules.RecentPrompts(many, 3).Should().HaveCount(3);
        AgentHubRules.RecentPrompts(null).Should().BeEmpty();
        AgentHubRules.RecentPrompts([]).Should().BeEmpty();
        AgentHubRules.PromptHistoryLimit.Should().Be(10);
    }

    [Fact]
    public void Clear指令_只有Claude交給CLI_每種代理都有答案()
    {
        AgentProviders.All.Where(AgentHubRules.CliHandlesClear).Should().Equal(AgentProvider.Claude);
        FluentActions.Invoking(() => AgentHubRules.CliHandlesClear((AgentProvider)99)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Clear指令_輸入區的清單沒有clear才補在最後()
    {
        AgentHubRules.WithClearCommand(Array.Empty<AgentSlashCommand>()).Select(c => c.Name).Should().Equal("clear");
        var fromCli = new List<AgentSlashCommand> { new("compact", "壓縮", null), new("clear", "Clear conversation history", null) };
        AgentHubRules.WithClearCommand(fromCli).Should().BeSameAs(fromCli, "CLI 的清單本來就有（Claude），照它的說明");
        AgentHubRules.WithClearCommand(new List<AgentSlashCommand> { new("review", null, null) }).Select(c => c.Name).Should().Equal("review", "clear");
    }

    [Fact]
    public void ShouldRecycle_只回收閒置夠久且沒在等使用者的()
    {
        var now = DateTime.Now;
        AgentHubRules.ShouldRecycle(false, false, false, now.AddMinutes(-20), now).Should().BeTrue();
        AgentHubRules.ShouldRecycle(false, false, false, now - AgentHubRules.IdleProcessTimeout, now).Should().BeTrue("剛好 15 分鐘");
        AgentHubRules.ShouldRecycle(true, false, false, now.AddMinutes(-20), now).Should().BeFalse();
        AgentHubRules.ShouldRecycle(false, true, false, now.AddMinutes(-20), now).Should().BeFalse("等使用者回答問卷或核准時不能關程序");
        AgentHubRules.ShouldRecycle(false, false, false, now.AddMinutes(-14), now).Should().BeFalse();
    }

    [Fact]
    public void ShouldRecycle_開著RemoteControl的程序閒置2小時才回收()
    {
        var now = DateTime.Now;
        AgentHubRules.RemoteControlIdleTimeout.Should().Be(TimeSpan.FromHours(2));
        AgentHubRules.ShouldRecycle(false, false, true, now.AddMinutes(-20), now).Should().BeFalse("一般程序 15 分鐘就回收，開著 Remote Control 的要等 2 小時");
        AgentHubRules.ShouldRecycle(false, false, true, now.AddMinutes(-119), now).Should().BeFalse();
        AgentHubRules.ShouldRecycle(false, false, true, now - AgentHubRules.RemoteControlIdleTimeout, now).Should().BeTrue("剛好 2 小時");
        AgentHubRules.ShouldRecycle(false, false, true, now.AddDays(-3), now).Should().BeTrue();
        AgentHubRules.ShouldRecycle(true, false, true, now.AddDays(-3), now).Should().BeFalse("執行中不關");
        AgentHubRules.ShouldRecycle(false, true, true, now.AddDays(-3), now).Should().BeFalse("等使用者回應時不關");
    }

    [Fact]
    public void ShouldReattachRemoteControl_換版後只接回2小時內有動靜的()
    {
        var now = DateTime.Now;
        AgentHubRules.ShouldReattachRemoteControl(now.AddMinutes(-5), now).Should().BeTrue();
        AgentHubRules.ShouldReattachRemoteControl(now.AddMinutes(-119), now).Should().BeTrue();
        AgentHubRules.ShouldReattachRemoteControl(now - AgentHubRules.RemoteControlIdleTimeout, now).Should().BeFalse("接回之後馬上就會被回收");
        AgentHubRules.ShouldReattachRemoteControl(now.AddHours(-15), now).Should().BeFalse();
    }

    [Fact]
    public void SupportsSteering_Claude與Codex可以插進這一輪_Antigravity與Grok排隊_每種代理都有答案()
    {
        AgentHubRules.SupportsSteering(AgentProvider.Claude).Should().BeTrue("stdin 寫入的訊息在工具結果之後併進同一輪");
        AgentHubRules.SupportsSteering(AgentProvider.Codex).Should().BeTrue("turn/steer");
        AgentHubRules.SupportsSteering(AgentProvider.Antigravity).Should().BeFalse("stream-json 沒有併進進行中一輪的協定");
        AgentHubRules.SupportsSteering(AgentProvider.Grok).Should().BeFalse("ACP 一輪進行中不能再送 session/prompt");
        foreach (var provider in AgentProviders.All)
            FluentActions.Invoking(() => AgentHubRules.SupportsSteering(provider)).Should().NotThrow();
        FluentActions.Invoking(() => AgentHubRules.SupportsSteering((AgentProvider)99)).Should().Throw<ArgumentOutOfRangeException>("新種類不能靜默落進其中一邊");
    }

    [Fact]
    public void PausesForAsyncQuestion_只有Codex的非同步提問要停下這一輪_每種代理都有答案()
    {
        AgentHubRules.PausesForAsyncQuestion(AgentProvider.Codex).Should().BeTrue("request_user_input_async 立刻回已收到，模型會接著做");
        AgentHubRules.PausesForAsyncQuestion(AgentProvider.Claude).Should().BeFalse("AskUserQuestion 本來就擋住這一輪");
        AgentHubRules.PausesForAsyncQuestion(AgentProvider.Antigravity).Should().BeFalse("計畫確認卡在這一輪結束前才發");
        AgentHubRules.PausesForAsyncQuestion(AgentProvider.Grok).Should().BeFalse("ask_user_question 本來就擋住這一輪");
        foreach (var provider in AgentProviders.All)
            FluentActions.Invoking(() => AgentHubRules.PausesForAsyncQuestion(provider)).Should().NotThrow();
        FluentActions.Invoking(() => AgentHubRules.PausesForAsyncQuestion((AgentProvider)99)).Should().Throw<ArgumentOutOfRangeException>("新種類不能靜默落進其中一邊");
    }

    [Fact]
    public void SupportsHandoff_Claude與Codex能換電腦_Grok與Antigravity不能_每種代理都有答案()
    {
        AgentHubRules.SupportsHandoff(AgentProvider.Claude).Should().BeTrue("以完整逐字稿或前文節錄續接");
        AgentHubRules.SupportsHandoff(AgentProvider.Codex).Should().BeTrue();
        AgentHubRules.SupportsHandoff(AgentProvider.Grok).Should().BeFalse("擁有者決定 1");
        AgentHubRules.SupportsHandoff(AgentProvider.Antigravity).Should().BeFalse();
        foreach (var provider in AgentProviders.All)
            FluentActions.Invoking(() => AgentHubRules.SupportsHandoff(provider)).Should().NotThrow();
        FluentActions.Invoking(() => AgentHubRules.SupportsHandoff((AgentProvider)99)).Should().Throw<ArgumentOutOfRangeException>("新種類不能靜默落進其中一邊");
    }

    [Fact]
    public void PushRecentFolder_放最前面_不分大小寫與結尾斜線去重()
    {
        var result = AgentHubRules.PushRecentFolder(new[] { @"C:\a", @"D:\b\", @"c:\A\", @"E:\e" }, @"C:\A");
        result.Should().Equal(@"C:\A", @"D:\b\", @"E:\e");

        AgentHubRules.PushRecentFolder(null, @"C:\x").Should().Equal(@"C:\x");
    }

    [Fact]
    public void PushRecentFolder_最多保留上限個()
    {
        var recent = Enumerable.Range(1, 20).Select(i => $@"C:\p{i}").ToList();

        var result = AgentHubRules.PushRecentFolder(recent, @"D:\new");

        result.Should().HaveCount(12);
        result[0].Should().Be(@"D:\new");
        result[^1].Should().Be(@"C:\p11");
        AgentHubRules.PushRecentFolder(recent, @"D:\new", max: 3).Should().Equal(@"D:\new", @"C:\p1", @"C:\p2");
    }

    [Fact]
    public void EffortForModel_新模型不支援目前的強度就回到預設_找模型的方式同網頁()
    {
        var models = new[]
        {
            new AgentModelOption("default", "預設", null, new[] { "low", "medium", "high", "xhigh", "max" }),
            new AgentModelOption("sonnet", "Sonnet", null, new[] { "low", "medium", "high" }),
            new AgentModelOption("haiku", "Haiku", null, Array.Empty<string>()),
        };

        AgentHubRules.EffortForModel("high", "sonnet", models).Should().Be("high", "新模型也支援就保留");
        AgentHubRules.EffortForModel("max", "sonnet", models).Should().BeNull("Sonnet 沒有 max");
        AgentHubRules.EffortForModel("high", "haiku", models).Should().BeNull("模型不支援推理強度");
        AgentHubRules.EffortForModel("max", null, models).Should().Be("max", "null＝帳戶預設模型，看 default 項目");
        AgentHubRules.EffortForModel("max", "未知模型", models).Should().Be("max", "清單沒有這個模型時看 default 項目（同網頁）");
        AgentHubRules.EffortForModel(null, "haiku", models).Should().BeNull();
        AgentHubRules.EffortForModel("max", "sonnet", Array.Empty<AgentModelOption>()).Should().Be("max", "還不知道模型清單時保留");
        AgentHubRules.EffortForModel("Max", "sonnet", models).Should().BeNull("大小寫不同就不是清單裡的值（網頁也不會顯示）");

        var noDefault = new[] { new AgentModelOption("gpt-6", "GPT-6", null, new[] { "low" }), new AgentModelOption("gpt-6-mini", "mini", null, new[] { "low", "high" }) };
        AgentHubRules.EffortForModel("high", null, noDefault).Should().BeNull("沒有 default 項目時看第一個模型");
        AgentHubRules.EffortForModel("high", "gpt-6-mini", noDefault).Should().Be("high");
    }
}
