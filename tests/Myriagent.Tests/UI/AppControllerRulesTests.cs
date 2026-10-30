using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.UI;

namespace Myriagent.Tests.UI;

/// <summary>
/// AppController 的純函式規則：結束前的確認（〔熱更新〕npm 換檔中、更新等對話停下）與系統匣通知
/// （CLI 換版彙總不逐一跳、同時到的合併成一則；對話的通知照舊）
/// </summary>
public sealed class AppControllerRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 9, 30, 0, DateTimeKind.Utc);

    private static CliState Cli(AgentProvider provider, CliInstallKind kind, bool busy = false, CliUpdatePhase phase = CliUpdatePhase.None)
    {
        var installation = new CliInstallation(provider, new AgentLauncher(@"C:\Program Files\nodejs\node.exe", new[] { "codex.js" }), kind,
            provider == AgentProvider.Claude ? @"C:\Users\U\.local\bin\claude.exe" : @"C:\Users\U\AppData\Roaming\npm\codex.cmd");
        return new CliState(provider, installation, "1.0.0", "1.0.1", busy, busy ? "更新中…" : null, null, null)
        {
            UpdatePhase = phase,
            UpdateStartedAt = phase == CliUpdatePhase.None ? null : Now,
        };
    }

    [Fact]
    public void ExitWarnings_沒有執行中的對話也沒有安裝更新_直接結束()
    {
        AppControllerRules.ExitWarnings(0, new[] { Cli(AgentProvider.Claude, CliInstallKind.Native), Cli(AgentProvider.Codex, CliInstallKind.Npm) })
            .Should().BeEmpty();
    }

    [Fact]
    public void ExitWarnings_npm正在換檔_提示結束會讓CLI安裝不完整()
    {
        var installing = Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Installing);
        installing.NpmInstalling.Should().BeTrue("前提：npm 安裝的 CLI 在 Installing 階段");

        var warnings = AppControllerRules.ExitWarnings(0, new[] { Cli(AgentProvider.Claude, CliInstallKind.Native), installing });

        warnings.Should().ContainSingle().Which.Should().Contain("Codex").And.Contain("npm 正在替換檔案").And.Contain("CLI 安裝不完整");
    }

    [Theory]
    [InlineData(nameof(CliUpdatePhase.Draining))]
    [InlineData(nameof(CliUpdatePhase.WaitingForProcesses))]
    public void ExitWarnings_更新還在等對話停下_說明結束會取消這次更新而不是安裝不完整(string phaseName)
    {
        var phase = Enum.Parse<CliUpdatePhase>(phaseName);
        var warnings = AppControllerRules.ExitWarnings(0, new[] { Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: phase) });

        warnings.Should().ContainSingle().Which.Should().Contain("還沒開始換檔").And.Contain("取消這次更新").And.NotContain("安裝不完整");
    }

    [Fact]
    public void NeedsExitReconfirm_確認框開著時排空完成_npm開始換檔_要以新的警告再問一次()
    {
        var draining = new[] { Cli(AgentProvider.Claude, CliInstallKind.Native), Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Draining) };
        var installing = new[] { Cli(AgentProvider.Claude, CliInstallKind.Native), Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Installing) };

        AppControllerRules.NeedsExitReconfirm(0, draining, 0, installing).Should().BeTrue("使用者確認的是「結束會取消這次更新」，按下時 npm 已經在換檔");
        AppControllerRules.ExitWarnings(0, installing).Should().ContainSingle().Which.Should().Contain("安裝不完整", "再問時用新的警告");
    }

    [Fact]
    public void NeedsExitReconfirm_沒變或變輕都不再問_原本沒有執行中的對話現在有了要再問()
    {
        var draining = new[] { Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Draining) };
        var waiting = new[] { Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.WaitingForProcesses) };
        var verifying = new[] { Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Verifying) };
        var idle = new[] { Cli(AgentProvider.Codex, CliInstallKind.Npm) };
        var installing = new[] { Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Installing) };

        AppControllerRules.NeedsExitReconfirm(0, draining, 0, draining).Should().BeFalse();
        AppControllerRules.NeedsExitReconfirm(0, draining, 0, waiting).Should().BeFalse("一樣是還沒換檔：結束照樣會取消（BeginShutdown）");
        AppControllerRules.NeedsExitReconfirm(0, installing, 0, verifying).Should().BeFalse("換檔做完了：變輕不必再問");
        AppControllerRules.NeedsExitReconfirm(0, draining, 0, idle).Should().BeFalse();
        AppControllerRules.NeedsExitReconfirm(2, draining, 1, draining).Should().BeFalse("執行中的對話變少不必再問");
        AppControllerRules.NeedsExitReconfirm(0, draining, 1, draining).Should().BeTrue("確認時沒有在執行的對話，現在有了");
        AppControllerRules.NeedsExitReconfirm(0, idle, 0, installing).Should().BeTrue();
    }

    [Fact]
    public void ExitWarnings_其他安裝或更新進行中_沿用建議等它完成的說明()
    {
        // 原生安裝的 claude update、受管 Codex 的下載、驗證新版：沒有 npm 換檔的風險，但仍建議等它完成
        var warnings = AppControllerRules.ExitWarnings(0, new[]
        {
            Cli(AgentProvider.Claude, CliInstallKind.Native, busy: true),
            Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Verifying),
        });

        warnings.Should().HaveCount(2);
        warnings[0].Should().StartWith("Claude Code CLI 正在安裝或更新");
        warnings[1].Should().StartWith("Codex CLI 正在安裝或更新");
    }

    [Fact]
    public void ExitWarnings_執行中的對話排在最前面_每個CLI各一則()
    {
        var warnings = AppControllerRules.ExitWarnings(3, new[]
        {
            Cli(AgentProvider.Claude, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Installing),
            Cli(AgentProvider.Codex, CliInstallKind.Npm, busy: true, phase: CliUpdatePhase.Draining),
        });

        warnings.Should().HaveCount(3);
        warnings[0].Should().Be("還有 3 個對話正在執行，結束會先停止它們，下次開啟 app 時會自動接續。");
        warnings[1].Should().Contain("Claude Code").And.Contain("CLI 安裝不完整");
        warnings[2].Should().Contain("Codex").And.Contain("取消這次更新");
    }

    [Fact]
    public void ExitWarnings_等背景工作回報的對話_結束會中斷背景工作_排在執行中的後面()
    {
        var warnings = AppControllerRules.ExitWarnings(1, Array.Empty<CliState>(), backgroundConversations: 2);

        warnings.Should().HaveCount(2);
        warnings[0].Should().StartWith("還有 1 個對話正在執行");
        warnings[1].Should().Be("還有 2 個對話的背景工作在執行（代理在等它們回報），結束會中斷這些背景工作，下次開啟 app 時會請代理確認它們做到哪裡、接著做。");
        AppControllerRules.ExitWarnings(0, Array.Empty<CliState>(), backgroundConversations: 1).Should().ContainSingle("只有背景工作也要問");
        AppControllerRules.ExitWarnings(0, Array.Empty<CliState>()).Should().BeEmpty();
    }

    [Fact]
    public void NextTrayNotice_對話的通知照原樣顯示_點了回到那個對話()
    {
        var n = AppControllerRules.NextTrayNotice(null, " 代理完成了工作（工作用 Max） ", " 修正登入流程 ", "c1", Now);

        n.Should().Be(new TrayNotice("代理完成了工作（工作用 Max）", "修正登入流程", "c1", Now));
    }

    [Fact]
    public void NextTrayNotice_CLI彙總沒有對話_點了只開主視窗()
    {
        var n = AppControllerRules.NextTrayNotice(null, "Claude Code 已更新到 2.1.297", "3 個對話已用新版接續", null, Now);

        n.ConversationId.Should().BeNull();
        n.Title.Should().Be("Claude Code 已更新到 2.1.297");
        n.Message.Should().Be("3 個對話已用新版接續");
        n.Merged.Should().BeFalse();
    }

    [Fact]
    public void NextTrayNotice_兩個CLI的彙總同時到_合併成一則_每行帶原本的標題()
    {
        var first = AppControllerRules.NextTrayNotice(null, "Claude Code 已更新到 2.1.297", "3 個對話已用新版接續", null, Now);
        var second = AppControllerRules.NextTrayNotice(first, "Codex 已更新到 0.162.0", "1 個對話已用新版接續", null, Now.AddSeconds(2));

        second.Title.Should().Be(AppControllerRules.MergedCliTitle);
        second.Message.Should().Be("Claude Code 已更新到 2.1.297：3 個對話已用新版接續\nCodex 已更新到 0.162.0：1 個對話已用新版接續");
        second.ConversationId.Should().BeNull();
        second.Merged.Should().BeTrue();
        second.ShownAtUtc.Should().Be(Now.AddSeconds(2), "重新顯示：從這一刻重新計時");

        // 第三則：接在合併後的內容後面，不重複前面的標題
        var third = AppControllerRules.NextTrayNotice(second, "Claude Code 已更新到 2.1.298", "1 個對話已用新版接續", null, Now.AddSeconds(4));
        third.Message.Should().Be(second.Message + "\nClaude Code 已更新到 2.1.298：1 個對話已用新版接續");
    }

    [Fact]
    public void NextTrayNotice_同一個標題的彙總_只把內容接在後面_相同內容不重複()
    {
        var first = AppControllerRules.NextTrayNotice(null, "CLI 已更新", "Claude Code 2.1.297：3 個對話已用新版接續", null, Now);
        var second = AppControllerRules.NextTrayNotice(first, "CLI 已更新", "Codex 0.162.0：1 個對話已用新版接續", null, Now.AddSeconds(1));
        var again = AppControllerRules.NextTrayNotice(second, "CLI 已更新", "Codex 0.162.0：1 個對話已用新版接續", null, Now.AddSeconds(2));

        second.Title.Should().Be("CLI 已更新");
        second.Message.Should().Be("Claude Code 2.1.297：3 個對話已用新版接續\nCodex 0.162.0：1 個對話已用新版接續");
        second.Merged.Should().BeFalse();
        again.Message.Should().Be(second.Message);
    }

    [Fact]
    public void NextTrayNotice_前一則已經收起來或是對話的通知_不合併()
    {
        var summary = AppControllerRules.NextTrayNotice(null, "Claude Code 已更新到 2.1.297", "3 個對話已用新版接續", null, Now);

        var later = AppControllerRules.NextTrayNotice(summary, "Codex 已更新到 0.162.0", "1 個對話已用新版接續", null, Now + AppControllerRules.BalloonDuration);
        later.Should().Be(new TrayNotice("Codex 已更新到 0.162.0", "1 個對話已用新版接續", null, Now + AppControllerRules.BalloonDuration), "前一則已經收起來了");

        var conversation = AppControllerRules.NextTrayNotice(summary, "代理在等你核准", "執行指令：npm test", "c2", Now.AddSeconds(1));
        conversation.ConversationId.Should().Be("c2", "對話的通知一則換一則，點了回到那個對話");
        conversation.Message.Should().Be("執行指令：npm test");

        var afterConversation = AppControllerRules.NextTrayNotice(conversation, "Codex 已更新到 0.162.0", "1 個對話已用新版接續", null, Now.AddSeconds(2));
        afterConversation.Merged.Should().BeFalse("前一則是對話的通知，不能把它併進 CLI 彙總（點了就回不到那個對話）");
        afterConversation.Title.Should().Be("Codex 已更新到 0.162.0");

        var clockBack = AppControllerRules.NextTrayNotice(summary, "Codex 已更新到 0.162.0", "1 個對話已用新版接續", null, Now.AddSeconds(-1));
        clockBack.Merged.Should().BeFalse("時鐘往回調：不能確定前一則還顯示著");
    }

    [Fact]
    public void NextTrayNotice_截到Windows的上限_標題空白時用app名稱()
    {
        var n = AppControllerRules.NextTrayNotice(null, new string('標', 80), new string('字', 400), null, Now);
        n.Title.Should().HaveLength(AppControllerRules.MaxBalloonTitle).And.EndWith("…");
        n.Message.Should().HaveLength(AppControllerRules.MaxBalloonText).And.EndWith("…");

        AppControllerRules.NextTrayNotice(null, "  ", null, null, Now).Should().Be(new TrayNotice(ShellRules.AppTitle, string.Empty, null, Now));
    }

    /// <summary>主螢幕 1920×1040 的工作區，加上一個放在左邊（座標為負）的第二螢幕</summary>
    private static readonly Rectangle[] Screens = { new(0, 0, 1920, 1040), new(-1280, 0, 1280, 984) };

    [Fact]
    public void RestorableBounds_位置照原樣還原_包括座標為負的第二螢幕()
    {
        AppControllerRules.RestorableBounds(new[] { 10, 20, 800, 600 }, Screens).Should().Be(new Rectangle(10, 20, 800, 600));
        AppControllerRules.RestorableBounds(new[] { -1200, 40, 900, 700 }, Screens).Should().Be(new Rectangle(-1200, 40, 900, 700));
        AppControllerRules.RestorableBounds(new[] { 1800, 900, 800, 600 }, Screens).Should().Be(new Rectangle(1800, 900, 800, 600), "和工作區相交就還原（一部分超出螢幕沒關係）");
        AppControllerRules.RestorableBounds(new[] { 0, 0, 101, 101 }, Screens).Should().Be(new Rectangle(0, 0, 101, 101), "寬高都大於 100");
    }

    [Fact]
    public void RestorableBounds_沒有位置或格式不對_用預設位置()
    {
        AppControllerRules.RestorableBounds(null, Screens).Should().BeNull("resume.json 沒記位置");
        AppControllerRules.RestorableBounds(Array.Empty<int>(), Screens).Should().BeNull();
        AppControllerRules.RestorableBounds(new[] { 10, 20, 800 }, Screens).Should().BeNull("要剛好 4 個數字（x, y, w, h）");
        AppControllerRules.RestorableBounds(new[] { 10, 20, 800, 600, 1 }, Screens).Should().BeNull();
    }

    [Fact]
    public void RestorableBounds_太小_用預設位置()
    {
        AppControllerRules.RestorableBounds(new[] { 10, 20, 100, 600 }, Screens).Should().BeNull("寬要大於 100");
        AppControllerRules.RestorableBounds(new[] { 10, 20, 800, 100 }, Screens).Should().BeNull("高要大於 100");
        AppControllerRules.RestorableBounds(new[] { 10, 20, 0, 0 }, Screens).Should().BeNull();
        AppControllerRules.RestorableBounds(new[] { 10, 20, -800, 600 }, Screens).Should().BeNull();
    }

    [Fact]
    public void app在前景_前景視窗屬於這個程序_含原生對話框()
    {
        const int self = 4242;
        // 選檔、選資料夾、MessageBox（結束確認）是原生視窗，不是 WinForms 的 Form（Form.ActiveForm 是 null），但一樣是 app 的視窗
        AppControllerRules.IsAppForeground(4242, foregroundVisible: true, foregroundMinimized: false, self).Should().BeTrue("設計 9.1 條件 1：app 的視窗在前景就是在用");
        AppControllerRules.IsAppForeground(1000, foregroundVisible: true, foregroundMinimized: false, self).Should().BeFalse("其他程式在前景");
        AppControllerRules.IsAppForeground(0, foregroundVisible: false, foregroundMinimized: false, self).Should().BeFalse("沒有前景視窗（切換中、鎖定畫面）");
        AppControllerRules.IsAppForeground(4242, foregroundVisible: true, foregroundMinimized: true, self).Should().BeFalse("最小化的視窗不算在用");
        AppControllerRules.IsAppForeground(4242, foregroundVisible: false, foregroundMinimized: false, self).Should().BeFalse("看不見的視窗（藏到系統匣）不算");
    }

    [Fact]
    public void RestorableBounds_不在任何螢幕上_用預設位置()
    {
        AppControllerRules.RestorableBounds(new[] { 5000, 20, 800, 600 }, Screens).Should().BeNull("拔掉的第三個螢幕上的位置");
        AppControllerRules.RestorableBounds(new[] { -3000, 20, 800, 600 }, Screens).Should().BeNull();
        AppControllerRules.RestorableBounds(new[] { 1920, 0, 800, 600 }, new[] { new Rectangle(0, 0, 1920, 1040) }).Should().BeNull("只碰到邊不算相交");
        AppControllerRules.RestorableBounds(new[] { 10, 20, 800, 600 }, Array.Empty<Rectangle>()).Should().BeNull("沒有螢幕資訊");
    }
}
