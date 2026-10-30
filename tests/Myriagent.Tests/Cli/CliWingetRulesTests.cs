using Myriagent.Agents;
using Myriagent.Cli;

namespace Myriagent.Tests.Cli;

/// <summary>
/// WinGet 安裝的 CLI（CliWingetRules）：從執行檔路徑認出 portable 套件、PATH 上的符號連結照目的地判斷、更新路線、
/// winget list／upgrade 的輸出（2026-10-24 在這台電腦以 winget v1.29.380 實測的字串，繁中與英文）、給使用者看的說明
/// </summary>
public sealed class CliWingetRulesTests
{
    private const string UserPackage = @"C:\Users\U\AppData\Local\Microsoft\WinGet\Packages\xAI.GrokBuild_Microsoft.Winget.Source_8wekyb3d8bbwe";
    private const string UserExe = UserPackage + @"\grok.exe";
    private const string LinksExe = @"C:\Users\U\AppData\Local\Microsoft\WinGet\Links\grok.exe";
    private static readonly string[] MachineRoots = { @"C:\Program Files", @"C:\Program Files (x86)" };

    private static CliInstallation Other(string exe, AgentProvider provider = AgentProvider.Grok)
        => new(provider, new AgentLauncher(exe, Array.Empty<string>()), CliInstallKind.Other, exe) { NativeExecutable = exe };

    // ───── 認出 portable 套件 ─────

    [Fact]
    public void FindPackage_套件資料夾名稱是識別碼加社群來源_取出識別碼與資料夾()
    {
        CliWingetRules.FindPackage(UserExe, MachineRoots).Should().Be(new WingetPackage("xAI.GrokBuild", UserPackage, MachineScope: false));
        CliWingetRules.FindPackage(UserPackage + @"\bin\grok.exe", MachineRoots)!.Directory.Should().Be(UserPackage, "封存套件的執行檔可能在下一層");
        CliWingetRules.FindPackage(UserExe.ToLowerInvariant(), MachineRoots)!.Id.Should().Be("xai.grokbuild", "不分大小寫");

        const string machine = @"C:\Program Files\WinGet\Packages\xAI.GrokBuild_Microsoft.Winget.Source_8wekyb3d8bbwe";
        CliWingetRules.FindPackage(machine + @"\grok.exe", MachineRoots).Should().Be(new WingetPackage("xAI.GrokBuild", machine, MachineScope: true));
    }

    [Theory]
    [InlineData(@"C:\Users\U\.grok\bin\grok.exe")]
    [InlineData(LinksExe)]
    [InlineData(@"C:\Users\U\AppData\Local\Microsoft\WinGet\Packages\_Microsoft.Winget.Source_8wekyb3d8bbwe\grok.exe")]
    [InlineData(@"C:\Users\U\AppData\Local\Microsoft\WinGet\Packages\bad id_Microsoft.Winget.Source_8wekyb3d8bbwe\grok.exe")]
    [InlineData(@"C:\Users\U\AppData\Local\Microsoft\WinGet\Packages\Pkg.Id_contoso\grok.exe")]
    [InlineData("")]
    public void FindPackage_不是社群來源的portable套件_回null(string path)
        => CliWingetRules.FindPackage(path, MachineRoots).Should().BeNull();

    [Theory]
    [InlineData("xAI.GrokBuild", true)]
    [InlineData("Anthropic.ClaudeCode", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Some_Vendor.Tool-2", true)]
    [InlineData("NoDot", false)]
    [InlineData(".Leading", false)]
    [InlineData("Trailing.", false)]
    [InlineData("has space.x", false)]
    [InlineData("quote\".x", false)]
    public void IsPackageId_只收WinGet識別碼的字元(string id, bool expected)
        => CliWingetRules.IsPackageId(id).Should().Be(expected);

    [Fact]
    public void WithWinget_只看其他安裝方式_符號連結照目的地判斷_更新改走WinGet屏障()
    {
        var direct = CliWingetRules.WithWinget(Other(UserExe), _ => null, MachineRoots)!;
        direct.Winget.Should().Be(new WingetPackage("xAI.GrokBuild", UserPackage, false));
        direct.NativeExecutable.Should().Be(UserExe);
        CliUpdateRules.RouteFor(direct).Should().Be(CliUpdateRoute.WingetBarrier);

        // 開了開發人員模式：PATH 上的是 WinGet\Links 的符號連結，啟動照舊用連結、換版看目的地
        var linked = CliWingetRules.WithWinget(Other(LinksExe), p => p == LinksExe ? UserExe : null, MachineRoots)!;
        linked.Winget!.Id.Should().Be("xAI.GrokBuild");
        linked.NativeExecutable.Should().Be(UserExe, "程序的執行檔路徑與換掉的檔案都是目的地");
        linked.Launcher.FileName.Should().Be(LinksExe);
        linked.DisplayPath.Should().Be(LinksExe);

        var official = Other(@"C:\Users\U\.grok\bin\grok.exe");
        CliWingetRules.WithWinget(official, _ => null, MachineRoots).Should().BeSameAs(official);
        CliUpdateRules.RouteFor(official).Should().Be(CliUpdateRoute.SelfUpdate, "官方安裝照舊 grok update");

        var npm = new CliInstallation(AgentProvider.Codex, new AgentLauncher(@"C:\node.exe", Array.Empty<string>()), CliInstallKind.Npm, UserPackage + @"\codex.cmd");
        CliWingetRules.WithWinget(npm, _ => UserExe, MachineRoots).Should().BeSameAs(npm, "npm、Claude 原生、受管 Codex 不是 WinGet 安裝");
        CliWingetRules.WithWinget(null, _ => UserExe, MachineRoots).Should().BeNull();
    }

    // ───── winget list --upgrade-available ─────

    [Fact]
    public void ParseUpgradeCheck_有新版時那一列依序是已安裝與可用的版本()
    {
        const string zh = "名稱       識別碼        版本   可用   來源\r\n---------------------------------------------\r\nGrok Build xAI.GrokBuild 1.0.67 1.0.70 winget\r\n1 個升級可供使用。\r\n";
        CliWingetRules.ParseUpgradeCheck(0, zh, string.Empty, "xAI.GrokBuild").Should().Be(new WingetUpgradeCheck(true, "1.0.70", null));

        const string en = "Name       Id            Version Available Source\r\n------------------------------------------------\r\nGrok Build xAI.GrokBuild 1.0.67  1.0.70    winget\r\n1 upgrades available.\r\n";
        CliWingetRules.ParseUpgradeCheck(0, en, string.Empty, "xai.grokbuild").Should().Be(new WingetUpgradeCheck(true, "1.0.70", null), "欄位名稱會翻譯，只看識別碼那一列");

        CliWingetRules.ParseUpgradeCheck(0, "Grok Build xAI.GrokBuild 1.0.67\r\n", null, "xAI.GrokBuild").Should().Be(new WingetUpgradeCheck(true, null, null), "讀不出可用的版本仍算有新版");
    }

    [Fact]
    public void ParseUpgradeCheck_沒有新版時沒有那一列_結束碼可能是0也可能是找不到套件_其他結束碼是查詢失敗()
    {
        CliWingetRules.ParseUpgradeCheck(0, "未找到符合輸入條件的已安裝套件。\r\n", string.Empty, "xAI.GrokBuild")
            .Should().Be(new WingetUpgradeCheck(false, null, null), "v1.29 對已是最新版的套件回結束碼 0");
        CliWingetRules.ParseUpgradeCheck(CliWingetRules.NoApplicationsFound, "未找到符合輸入條件的已安裝套件。\r\n", string.Empty, "xAI.GrokBuild")
            .Should().Be(new WingetUpgradeCheck(false, null, null));

        var failed = CliWingetRules.ParseUpgradeCheck(unchecked((int)0x8A15000F), "Failed when opening source(s); try the 'source reset' command if the problem persists.\r\n", string.Empty, "xAI.GrokBuild");
        failed.Available.Should().BeFalse();
        failed.Error.Should().Be("向 WinGet 查詢新版失敗（結束碼 0x8A15000F）：Failed when opening source(s); try the 'source reset' command if the problem persists.");
    }

    // ───── winget upgrade ─────

    [Fact]
    public void EvaluateUpgrade_成功_沒有可用的升級_找不到套件()
    {
        CliWingetRules.EvaluateUpgrade(AgentProvider.Grok, 0, "已成功安裝\r\n", string.Empty, "xAI.GrokBuild").Should().Be(new WingetUpgradeVerdict(true, false, null));
        CliWingetRules.EvaluateUpgrade(AgentProvider.Grok, CliWingetRules.UpdateNotApplicable, "找不到可用的升級。\r\n沒有較新的套件版本可從設定的來源使用。\r\n", string.Empty, "xAI.GrokBuild")
            .Should().Be(new WingetUpgradeVerdict(false, true, null));
        CliWingetRules.EvaluateUpgrade(AgentProvider.Grok, CliWingetRules.NoApplicationsFound, string.Empty, string.Empty, "xAI.GrokBuild").Error
            .Should().Be("WinGet 找不到已安裝的 xAI.GrokBuild，請在終端機執行 winget list --id xAI.GrokBuild 確認安裝方式");
    }

    [Fact]
    public void EvaluateUpgrade_執行中的exe擋住換檔_取最後兩行並說明要先關掉()
    {
        // 2026-10-24 實測：系統管理員終端機開著 grok 時 winget upgrade --id xAI.GrokBuild -e 的完整輸出（結束碼 0x8A150052，舊檔完好）
        var stdout = string.Join("\r\n",
            "找到 Grok Build [xAI.GrokBuild] 版本 1.0.70",
            "此應用程式已由其擁有者授權給您。",
            "Microsoft 不負任何責任，也不會授與協力廠商封裝的任何授權。",
            "已略過相依性。",
            "已成功驗證安裝程式雜湊",
            "正在啟動套件安裝...",
            "執行命令時，發生意外的錯誤： ",
            "remove: Access is denied.: \"" + UserExe + "\"",
            "安裝程式失敗，結束代碼為: 0x8a150003 : 執行命令失敗",
            string.Empty);

        var verdict = CliWingetRules.EvaluateUpgrade(AgentProvider.Grok, unchecked((int)0x8A150052), stdout, string.Empty, "xAI.GrokBuild");

        verdict.Succeeded.Should().BeFalse();
        verdict.NotApplicable.Should().BeFalse();
        verdict.Error.Should().Be("WinGet 更新失敗（結束碼 0x8A150052）：remove: Access is denied.: \"" + UserExe + "\" 安裝程式失敗，結束代碼為: 0x8a150003 : 執行命令失敗"
                                  + "（執行檔可能還在使用中：請關閉所有的 Grok Build 再更新）");
    }

    [Fact]
    public void EvaluateUpgrade_以CR覆寫的進度只留最後一段_轉圈與進度條不當成原因()
    {
        var stdout = "  - \r  \\ \r  ██████████▒▒▒▒  50.0 MB / 175 MB\r\n下載失敗\r\n\r\n";
        CliWingetRules.EvaluateUpgrade(AgentProvider.Grok, unchecked((int)0x8A150008), stdout, string.Empty, "xAI.GrokBuild").Error
            .Should().Be("WinGet 更新失敗（結束碼 0x8A150008）：下載失敗");
        CliWingetRules.EvaluateUpgrade(AgentProvider.Grok, 1, string.Empty, "boom\r\n", "xAI.GrokBuild").Error
            .Should().Be("WinGet 更新失敗（結束碼 0x00000001）：boom", "stdout 沒有內容就看 stderr");
    }

    // ───── 參數與說明 ─────

    [Fact]
    public void 參數_查詢只列這個套件_更新不跳任何提示也不重新處理相依性()
    {
        CliWingetRules.ListUpgradeArguments("xAI.GrokBuild").Should().Equal(
            "list", "--id", "xAI.GrokBuild", "--exact", "--upgrade-available", "--accept-source-agreements", "--disable-interactivity");
        CliWingetRules.UpgradeArguments("xAI.GrokBuild").Should().Equal(
            "upgrade", "--id", "xAI.GrokBuild", "--exact", "--silent", "--disable-interactivity",
            "--accept-source-agreements", "--accept-package-agreements", "--skip-dependencies");
        CliWingetRules.FormatExitCode(CliWingetRules.UpdateNotApplicable).Should().Be("0x8A15002B");
    }

    [Fact]
    public void 說明_還沒上WinGet_給所有使用者_有程序在用_找不到winget()
    {
        CliWingetRules.DescribeNotYetOnWinget(AgentProvider.Grok, "1.0.67", "1.0.70")
            .Should().Be("WinGet 上還沒有比 1.0.67 新的 Grok Build（官方已發布 1.0.70，新版通常要幾天才會上 WinGet），請過幾天再更新");
        CliWingetRules.DescribeMachineScope(AgentProvider.Grok, new WingetPackage("xAI.GrokBuild", @"C:\Program Files\WinGet\Packages\x", true))
            .Should().Contain("系統管理員終端機執行 winget upgrade --id xAI.GrokBuild -e");
        CliWingetRules.DescribeMissingWinget(AgentProvider.Grok).Should().StartWith("找不到 winget");

        var grok = CliWingetRules.DescribeForeignProcesses(AgentProvider.Grok, new[] { new CliProcessUse(90068, UserExe, false) });
        grok.Should().Be("還有 app 以外的 Grok Build 程序在使用 WinGet 安裝的執行檔（PID 90068），WinGet 無法替換執行中的檔案，這次沒有更新；"
                         + "請先關閉終端機或其他程式裡的 Grok Build 再更新（背景的 leader 可在終端機執行 grok leader kill 結束；以系統管理員身分開的，要從系統管理員終端機結束）");
        var many = Enumerable.Range(1, 7).Select(i => new CliProcessUse(i, UserExe, false)).ToList();
        CliWingetRules.DescribeForeignProcesses(AgentProvider.Codex, many).Should().Contain("PID 1、PID 2、PID 3、PID 4、PID 5 等 7 個").And.NotContain("leader");
    }
}
