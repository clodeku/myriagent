using Myriagent.Agents;
using Myriagent.Cli;

namespace Myriagent.Tests.Cli;

/// <summary>CLI 安裝／更新流程的規則（輸出取自 claude update 2.1.295 與 npm 11 的實際格式）</summary>
public class CliUpdateRulesTests
{
    private const string NpmPrefix = @"C:\Users\U\AppData\Roaming\npm";

    // ───── 失敗原因 ─────

    [Fact]
    public void DescribeFailure_ClaudeUpdate失敗_取stderr的原因而不是stdout第一行的目前版本()
    {
        const string stdout = "Current version: 2.1.295\nChecking for updates to latest version...\n";

        CliUpdateRules.DescribeFailure(stdout, "Failed to check for updates\n").Should().Be("Failed to check for updates");
        CliUpdateRules.DescribeFailure(stdout, "Error: Failed to install native update\n").Should().Be("Error: Failed to install native update");
    }

    [Fact]
    public void DescribeFailure_Npm錯誤_跳過code與path這類細節行和warn行_取說明那一行()
    {
        var stderr = string.Join("\n",
            "npm warn cleanup Failed to remove some directories [",
            "npm error code EBUSY",
            "npm error syscall rename",
            $@"npm error path {NpmPrefix}\node_modules\@openai\codex",
            $@"npm error dest {NpmPrefix}\node_modules\@openai\.codex-AbCd1234",
            "npm error errno -4082",
            $@"npm error EBUSY: resource busy or locked, rename '{NpmPrefix}\node_modules\@openai\codex' -> '{NpmPrefix}\node_modules\@openai\.codex-AbCd1234'",
            @"npm error A complete log of this run can be found in: C:\Users\U\AppData\Local\npm-cache\_logs\2026-10-10T01_00_00_000Z-debug-0.log");

        CliUpdateRules.DescribeFailure(string.Empty, stderr).Should().StartWith("npm error EBUSY: resource busy or locked, rename");

        var network = string.Join("\r\n",
            "npm error code ENOTFOUND",
            "npm error syscall getaddrinfo",
            "npm error errno ENOTFOUND",
            "npm error network request to https://registry.npmjs.org/@openai%2fcodex failed, reason: getaddrinfo ENOTFOUND registry.npmjs.org",
            "npm error network This is a problem related to network connectivity.");
        CliUpdateRules.DescribeFailure(null, network).Should().Be("npm error network request to https://registry.npmjs.org/@openai%2fcodex failed, reason: getaddrinfo ENOTFOUND registry.npmjs.org");

        CliUpdateRules.DescribeFailure(null, "npm error code E401\nnpm error A complete log of this run can be found in: C:\\x.log")
            .Should().Be("npm error code E401", "只有細節行時退回第一個錯誤行");
    }

    [Fact]
    public void DescribeFailure_沒有錯誤字樣_取stderr最後一行_都沒有回原因不明()
    {
        CliUpdateRules.DescribeFailure(string.Empty, "執行逾時").Should().Be("執行逾時");
        CliUpdateRules.DescribeFailure("第一行\n最後一行\n", "  \n").Should().Be("最後一行");
        CliUpdateRules.DescribeFailure("Current version: 2.1.295", "The system cannot find the file specified.").Should().Be("The system cannot find the file specified.");
        CliUpdateRules.DescribeFailure(null, null).Should().Be("原因不明");
        CliUpdateRules.DescribeFailure(" \r\n ", string.Empty).Should().Be("原因不明");
    }

    // ───── 更新指令沒換版時的說明 ─────

    [Fact]
    public void SummarizeOutput_去掉目前版本與正在檢查的進度行_合併成一行()
    {
        CliUpdateRules.SummarizeOutput("Current version: 2.1.286\nChecking for updates to stable version...\nClaude Code is up to date (2.1.286)\n", string.Empty)
            .Should().Be("Claude Code is up to date (2.1.286)");
        CliUpdateRules.SummarizeOutput("Current version: 2.1.286\nClaude is managed by winget.\nTo update, run:\n  winget upgrade Anthropic.ClaudeCode\n", null)
            .Should().Be("Claude is managed by winget. To update, run: winget upgrade Anthropic.ClaudeCode");
        CliUpdateRules.SummarizeOutput("Current version: 2.1.286\r\n", "Another Claude process (PID 4242) is currently running. Please try again in a moment.\r\n")
            .Should().Be("Another Claude process (PID 4242) is currently running. Please try again in a moment.");
    }

    [Fact]
    public void SummarizeOutput_去掉色碼_太長截斷_沒有內容回Null()
    {
        var esc = (char)27;
        CliUpdateRules.SummarizeOutput($"{esc}[32mClaude Code is up to date (2.1.286){esc}[39m", null).Should().Be("Claude Code is up to date (2.1.286)");

        var summary = CliUpdateRules.SummarizeOutput(new string('x', 500), null, maxLength: 300)!;
        summary.Should().HaveLength(301).And.EndWith("…");

        CliUpdateRules.SummarizeOutput("Current version: 2.1.286\nChecking for updates to latest version...", " ").Should().BeNull();
        CliUpdateRules.SummarizeOutput(null, null).Should().BeNull();
    }

    [Theory]
    [InlineData("2.1.286", "2.1.286", true, true)]
    [InlineData("2.1.286", "2.1.295", false, false)]
    [InlineData("2.1.286", "2.1.290", true, false)]
    [InlineData("2.1.286", "2.1.286", false, false)]
    [InlineData("0.160.1", null, false, false)]
    [InlineData(null, null, false, false)]
    public void IsNoEffectUpdate_沒報錯版本卻沒變而且仍有新版(string? before, string? after, bool updateAvailable, bool expected)
        => CliUpdateRules.IsNoEffectUpdate(before, after, updateAvailable).Should().Be(expected);

    // ───── 失敗原因何時不再顯示 ─────

    [Theory]
    [InlineData(null, "1.0.0", "1.0.0", true, true, false)]
    [InlineData("更新失敗", "1.0.0", "1.0.0", true, true, true)]
    [InlineData("更新失敗", "1.0.0", "1.1.0", true, false, false)]
    [InlineData("更新失敗", "1.0.0", "1.1.0", true, true, false)]
    [InlineData("更新失敗", "1.0.0", "1.0.0", true, false, false)]
    [InlineData("安裝失敗", null, null, false, false, true)]
    [InlineData("安裝失敗", null, "1.0.0", true, false, false)]
    public void KeepActionError_版本變了或已是最新版就不再顯示(string? error, string? before, string? now, bool installed, bool updateAvailable, bool expected)
        => CliUpdateRules.KeepActionError(error, before, now, installed, updateAvailable).Should().Be(expected);

    // ───── 查完版本後的狀態 ─────

    private static readonly CliInstallation NpmCodex =
        new(AgentProvider.Codex, new AgentLauncher(@"C:\npm\node.exe", new[] { @"C:\npm\node_modules\@openai\codex\bin\codex.js" }), CliInstallKind.Npm, @"C:\npm\codex.cmd");

    [Fact]
    public void AfterLatestCheck_查到最新版_上次檢查與檢查完成時間都更新()
    {
        var before = new DateTime(2026, 10, 10, 8, 0, 0);
        var now = before.AddHours(6);
        var state = new CliState(AgentProvider.Codex, NpmCodex, "0.160.1", "0.161.0", false, null, before, null)
        {
            CheckedAt = before, LatestError = "無法查詢最新版本：連線逾時", VersionCheckedFor = NpmCodex.DisplayPath,
        };

        var next = CliUpdateRules.AfterLatestCheck(state, "0.162.0", null, now);

        next.Latest.Should().Be("0.162.0");
        next.LatestError.Should().BeNull();
        next.LastCheckedAt.Should().Be(now);
        next.CheckedAt.Should().Be(now);
        next.VersionCheckedFor.Should().Be(NpmCodex.DisplayPath);
        next.UpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public void AfterLatestCheck_查最新版失敗_上次檢查不變_檢查完成時間照樣更新()
    {
        // 「上次檢查」只記查到最新版的時間（離線時不能看起來像剛確認過已是最新版）；
        // 網頁的「立即檢查」等兩個 CLI 的 checkedAt 都變了才恢復按鈕，查不到最新版也要更新，否則會停在「檢查中」到逾時
        var before = new DateTime(2026, 10, 10, 8, 0, 0);
        var now = before.AddHours(6);
        var state = new CliState(AgentProvider.Codex, NpmCodex, "0.160.1", "0.162.0", false, null, before, null) { CheckedAt = before, VersionCheckedFor = NpmCodex.DisplayPath };

        var next = CliUpdateRules.AfterLatestCheck(state, null, "無法查詢最新版本：連線逾時", now);

        next.Latest.Should().Be("0.162.0", "沿用上次查到的最新版");
        next.LatestError.Should().Be("無法查詢最新版本：連線逾時");
        next.LastCheckedAt.Should().Be(before);
        next.CheckedAt.Should().Be(now);
        next.UpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public void AfterLatestCheck_仍有新版時保留上次的失敗原因_已是最新版就清掉()
    {
        // 版本變了（換版）時清掉失敗原因由 CliManager 發布換版時處理（CliManagerUpdateTests）
        var now = new DateTime(2026, 10, 10, 8, 0, 0);
        var state = new CliState(AgentProvider.Codex, NpmCodex, "0.160.1", "0.162.0", false, null, null, null)
        {
            ActionError = "npm 更新失敗：EBUSY", VersionCheckedFor = NpmCodex.DisplayPath,
        };

        CliUpdateRules.AfterLatestCheck(state, "0.162.0", null, now).ActionError.Should().Be("npm 更新失敗：EBUSY", "還沒裝好、仍有新版時保留");
        CliUpdateRules.AfterLatestCheck(state with { Version = "0.162.0" }, "0.162.0", null, now).ActionError.Should().BeNull();
    }

    // ───── 程序是否在安裝資料夾裡 ─────

    [Fact]
    public void IsUnderAny_程序在安裝資料夾底下_不分大小寫與結尾斜線()
    {
        var codexExe = $@"{NpmPrefix}\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

        CliUpdateRules.IsUnderAny(codexExe, new[] { NpmPrefix }).Should().BeTrue();
        CliUpdateRules.IsUnderAny(codexExe.ToLowerInvariant(), new[] { NpmPrefix.ToUpperInvariant() + @"\" }).Should().BeTrue();
        CliUpdateRules.IsUnderAny(@"C:\npm2\codex.exe", new[] { @"C:\npm" }).Should().BeFalse("C:\\npm2 不在 C:\\npm 底下");
        CliUpdateRules.IsUnderAny(@"C:\npm", new[] { @"C:\npm" }).Should().BeFalse();
        CliUpdateRules.IsUnderAny(null, new[] { NpmPrefix }).Should().BeFalse();
        CliUpdateRules.IsUnderAny(codexExe, Array.Empty<string>()).Should().BeFalse();
        CliUpdateRules.IsUnderAny(codexExe, new[] { string.Empty, @"\" }).Should().BeFalse("空的根目錄不能算全部");
    }

    [Fact]
    public void IsUnderAny_連結的實際位置也算_nvm的nodejs捷徑()
    {
        // nvm-windows：PATH 上是 C:\nvm4w\nodejs（連結），codex.js 以解析後的實際路徑啟動 codex.exe
        var real = @"C:\Users\U\AppData\Local\nvm\v20.11.1";
        var exe = real + @"\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

        CliUpdateRules.IsUnderAny(exe, new[] { @"C:\nvm4w\nodejs" }).Should().BeFalse();
        CliUpdateRules.IsUnderAny(exe, new[] { @"C:\nvm4w\nodejs", real }).Should().BeTrue();
    }

    // ───── 查完最新版（本機版本由版本偵測負責） ─────

    [Fact]
    public void AfterLatestCheck_只動最新版與檢查時間_不碰安裝與版本()
    {
        var before = new DateTime(2026, 10, 10, 8, 0, 0);
        var now = before.AddHours(6);
        var state = new CliState(AgentProvider.Codex, NpmCodex, null, "0.161.0", false, null, before, null) { VersionCheckedFor = null };

        var next = CliUpdateRules.AfterLatestCheck(state, "0.162.0", null, now);

        next.Latest.Should().Be("0.162.0");
        next.LastCheckedAt.Should().Be(now);
        next.CheckedAt.Should().Be(now);
        next.Version.Should().BeNull("本機版本只由版本偵測（watcher、完整判定）寫入");
        next.Checking.Should().BeTrue("還沒查過本機執行檔：查到最新版不能讓 CLI 卡誤顯示「未安裝」");

        var offline = CliUpdateRules.AfterLatestCheck(state with { Version = "0.162.0", VersionCheckedFor = NpmCodex.DisplayPath }, null, "無法查詢最新版本：連線逾時", now);
        offline.Latest.Should().Be("0.161.0");
        offline.LastCheckedAt.Should().Be(before, "查不到最新版：不能看起來像剛確認過");
        offline.CheckedAt.Should().Be(now);

        var latest = state with { Version = "0.162.0", VersionCheckedFor = NpmCodex.DisplayPath, ActionError = "npm 更新失敗：EBUSY" };
        CliUpdateRules.AfterLatestCheck(latest, "0.162.0", null, now).ActionError.Should().BeNull("已是最新版就不再顯示上次的失敗原因");
        CliUpdateRules.AfterLatestCheck(latest, "0.163.0", null, now).ActionError.Should().Be("npm 更新失敗：EBUSY");
    }

    // ───── 更新方式（7.3） ─────

    [Theory]
    [InlineData(nameof(CliInstallKind.Npm), nameof(CliUpdateRoute.NpmBarrier))]
    [InlineData(nameof(CliInstallKind.Managed), nameof(CliUpdateRoute.ManagedSwap))]
    [InlineData(nameof(CliInstallKind.Native), nameof(CliUpdateRoute.SelfUpdate))]
    [InlineData(nameof(CliInstallKind.Other), nameof(CliUpdateRoute.SelfUpdate))]
    public void RouteFor_只有npm版要走屏障(string kind, string expected)
        => CliUpdateRules.RouteFor(Enum.Parse<CliInstallKind>(kind)).Should().Be(Enum.Parse<CliUpdateRoute>(expected),
            "Claude 原生與受管 Codex 換檔都用改名，執行中的舊程序照常回應");

    // ───── claude update 的輸出（2.1.296 的字串，cli_strings.py 可重現） ─────

    [Fact]
    public void ParseClaudeUpdate_成功換版()
    {
        var result = CliUpdateRules.ParseClaudeUpdate(0, "Current version: 2.1.296\nChecking for updates to latest version...\nUpdating to 2.1.297...\nSuccessfully updated from 2.1.296 to version 2.1.297\n", string.Empty);

        result.Kind.Should().Be(ClaudeUpdateKind.Updated);
        result.FromVersion.Should().Be("2.1.296");
        result.ToVersion.Should().Be("2.1.297");
        result.Message.Should().Be("Successfully updated from 2.1.296 to version 2.1.297");

        CliUpdateRules.ParseClaudeUpdate(0, "\u001b[32mSuccessfully downgraded from 2.1.297 to version 2.1.296\u001b[39m", null).Kind.Should().Be(ClaudeUpdateKind.Updated, "降版也是換了版");
    }

    [Fact]
    public void ParseClaudeUpdate_已是最新版()
    {
        var result = CliUpdateRules.ParseClaudeUpdate(0, "Current version: 2.1.296\nChecking for updates to latest version...\nClaude Code is up to date (2.1.296)\n", null);
        result.Kind.Should().Be(ClaudeUpdateKind.UpToDate);
        result.ToVersion.Should().Be("2.1.296");

        CliUpdateRules.ParseClaudeUpdate(0, "Claude is up to date!", null).Kind.Should().Be(ClaudeUpdateKind.UpToDate, "非原生安裝的舊說法");
    }

    [Theory]
    [InlineData("Another Claude process (PID 4242) is currently running. Please try again in a moment.", 4242)]
    [InlineData("Another Claude process is currently running. Please try again in a moment.", null)]
    public void ParseClaudeUpdate_版本鎖被占住_結束碼是0也不算成功(string line, int? pid)
    {
        var result = CliUpdateRules.ParseClaudeUpdate(0, "Current version: 2.1.296\n" + line + "\n", string.Empty);

        result.Kind.Should().Be(ClaudeUpdateKind.LockBusy);
        result.LockHolderProcessId.Should().Be(pid);
        result.Message.Should().Be(line);
        CliUpdateRules.ParseClaudeUpdate(0, string.Empty, line).Kind.Should().Be(ClaudeUpdateKind.LockBusy, "寫在 stderr 也一樣");
    }

    [Fact]
    public void ParseClaudeUpdate_結束碼不是0_取stderr的原因()
    {
        var result = CliUpdateRules.ParseClaudeUpdate(1, "Current version: 2.1.296\n", "Error: Failed to install native update\nEACCES\nTry running \"claude doctor\" for diagnostics\n");
        result.Kind.Should().Be(ClaudeUpdateKind.Failed);
        result.Message.Should().Be("Error: Failed to install native update");
        CliUpdateRules.ParseClaudeUpdate(1, "Current version: 2.1.296\n", "Failed to check for updates\n").Message.Should().Be("Failed to check for updates");
    }

    [Theory]
    [InlineData("Claude is managed by winget.\nTo update, run:\n  winget upgrade Anthropic.ClaudeCode")]
    [InlineData("The stable channel is at 2.1.290, which is older. Staying on 2.1.296.")]
    [InlineData("Updates are disabled by your administrator. Contact your IT team to get the latest version.")]
    [InlineData("Update target 2.1.297 (from @latest or a server-side override) predates release-signature enforcement; staying on 2.1.296. Run `claude install 2.1.297` to switch to it explicitly.")]
    public void ParseClaudeUpdate_沒有換版的其他說明(string stdout)
    {
        var result = CliUpdateRules.ParseClaudeUpdate(0, "Current version: 2.1.296\n" + stdout, null);
        result.Kind.Should().Be(ClaudeUpdateKind.Declined);
        result.Message.Should().NotBeNullOrWhiteSpace().And.NotContain("Current version");
    }

    [Fact]
    public void JudgeSelfUpdate_以執行前後的版本資訊為準()
    {
        var updated = new ClaudeUpdateResult(ClaudeUpdateKind.Updated, "2.1.296", "2.1.297", null, "Successfully updated from 2.1.296 to version 2.1.297");
        CliUpdateRules.JudgeSelfUpdate(updated, "2.1.296", "2.1.297").Should().Be(new CliUpdateJudgement(true, null, null, false));
        var lied = CliUpdateRules.JudgeSelfUpdate(updated, "2.1.296", "2.1.296");
        lied.Switched.Should().BeFalse();
        lied.Error.Should().Contain("回報已更新到 2.1.297").And.Contain("仍是 2.1.296", "回報成功卻沒換檔（#70039、#74733）要以 FileVersionInfo 驗證");

        var busy = CliUpdateRules.JudgeSelfUpdate(new ClaudeUpdateResult(ClaudeUpdateKind.LockBusy, null, null, 4242, "Another Claude process (PID 4242) is currently running."), "2.1.296", "2.1.296");
        busy.Should().Be(new CliUpdateJudgement(false, null, "另一個 Claude 程序（PID 4242）正在更新 Claude Code，30 分鐘後再試", RetryLater: true));
        CliUpdateRules.JudgeSelfUpdate(new ClaudeUpdateResult(ClaudeUpdateKind.LockBusy, null, null, 4242, null), "2.1.296", "2.1.297").Switched
            .Should().BeTrue("鎖的持有者（互動式 claude）同時把版本換好了");

        var failed = new ClaudeUpdateResult(ClaudeUpdateKind.Failed, null, null, null, "Failed to check for updates");
        CliUpdateRules.JudgeSelfUpdate(failed, "2.1.296", "2.1.296").Error.Should().Be("更新失敗：Failed to check for updates");
        CliUpdateRules.JudgeSelfUpdate(failed, "2.1.296", "2.1.297").Should().Be(new CliUpdateJudgement(true, null, null, false));

        var upToDate = new ClaudeUpdateResult(ClaudeUpdateKind.UpToDate, "2.1.296", "2.1.296", null, "Claude Code is up to date (2.1.296)");
        CliUpdateRules.JudgeSelfUpdate(upToDate, "2.1.296", "2.1.296").Should().Be(new CliUpdateJudgement(false, null, "Claude Code is up to date (2.1.296)", false));
        var declined = new ClaudeUpdateResult(ClaudeUpdateKind.Declined, null, null, null, "Claude is managed by winget.");
        CliUpdateRules.JudgeSelfUpdate(declined, "2.1.296", "2.1.296").Note.Should().Be("Claude is managed by winget.");
        CliUpdateRules.JudgeSelfUpdate(declined, "2.1.296.0", "2.1.296").Switched.Should().BeFalse("FileVersionInfo 的四段與 x.y.z 只比前三段");
    }

    // ───── npm 的結果 ─────

    [Fact]
    public void EvaluateNpm_成功_成功但留下殘留_檔案被占用_其他失敗()
    {
        CliUpdateRules.EvaluateNpm(0, "changed 2 packages in 12s", string.Empty).Should().Be(new NpmRunVerdict(true, null, false, false));

        var residue = CliUpdateRules.EvaluateNpm(0, "changed 2 packages in 12s",
            $"npm warn cleanup Failed to remove some directories [\nnpm warn cleanup   [\nnpm warn cleanup     '{NpmPrefix}\\node_modules\\@openai\\.codex-nsHu5pVi',\nnpm warn cleanup     [Error: EPERM: operation not permitted, unlink '…\\codex.exe']");
        residue.Succeeded.Should().BeTrue("研究實測：第一次更新會成功，只是舊程序還在用的退役資料夾刪不掉");
        residue.LeftResidue.Should().BeTrue();
        residue.FilesInUse.Should().BeFalse();

        var busy = CliUpdateRules.EvaluateNpm(4082, string.Empty, string.Join("\n",
            "npm error code EBUSY",
            "npm error syscall copyfile",
            $@"npm error EBUSY: resource busy or locked, copyfile '{NpmPrefix}\node_modules\@openai\codex\…\codex.exe'"));
        busy.Succeeded.Should().BeFalse();
        busy.FilesInUse.Should().BeTrue();
        busy.Error.Should().StartWith("npm error EBUSY: resource busy or locked, copyfile");

        var offline = CliUpdateRules.EvaluateNpm(1, string.Empty, "npm error code ENOTFOUND\nnpm error network request to https://registry.npmjs.org/@openai%2fcodex failed, reason: getaddrinfo ENOTFOUND registry.npmjs.org");
        offline.FilesInUse.Should().BeFalse();
        offline.Error.Should().StartWith("npm error network request");
        CliUpdateRules.EvaluateNpm(-1, string.Empty, "系統找不到指定的檔案。").Error.Should().Be("系統找不到指定的檔案。", "cmd 或 npm 起不來");
    }

    [Fact]
    public void NpmInstallArguments_cmd_call_使用者實際的npm_cmd_路徑有空白也照常()
    {
        var args = CliUpdateRules.NpmInstallArguments(@"C:\Program Files\nodejs\npm.cmd", CliLocator.CodexNpmPackage);
        args.Should().Equal("/d", "/c", "call", @"C:\Program Files\nodejs\npm.cmd", "install", "-g", "@openai/codex@latest");
    }

    // ───── 屏障的回覆之後怎麼走（7.3.1） ─────

    [Fact]
    public void DecidePreparation_全部停下才換檔_自動更新有對話在跑就延後_使用者取消就中止()
    {
        CliUpdateRules.DecidePreparation(new CliUpdatePreparation(true, 2, 1, 0), automatic: false, AgentProvider.Codex).Should().Be((CliBarrierDecision.Proceed, (string?)null));
        CliUpdateRules.DecidePreparation(new CliUpdatePreparation(true, 0, 0, 0), automatic: true, AgentProvider.Codex).Decision.Should().Be(CliBarrierDecision.Proceed);

        CliUpdateRules.DecidePreparation(new CliUpdatePreparation(false, 0, 0, 2), automatic: true, AgentProvider.Codex)
            .Should().Be((CliBarrierDecision.Defer, (string?)null), "Background：中樞什麼都沒停，安靜地延後");

        CliUpdateRules.DecidePreparation(new CliUpdatePreparation(false, 1, 1, 1, "已取消更新"), automatic: false, AgentProvider.Codex)
            .Should().Be((CliBarrierDecision.Abort, (string?)"已取消更新"), "Ready=false 且有原因：不換檔");
        CliUpdateRules.DecidePreparation(new CliUpdatePreparation(false, 0, 0, 0, "已取消更新"), automatic: true, AgentProvider.Codex).Decision
            .Should().Be(CliBarrierDecision.Abort);

        var (decision, reason) = CliUpdateRules.DecidePreparation(new CliUpdatePreparation(false, 0, 0, 2), automatic: false, AgentProvider.Claude);
        decision.Should().Be(CliBarrierDecision.Abort);
        reason.Should().Contain("還有 2 個 Claude Code 對話在執行或等你回應");
    }

    // ───── 程序與殘留 ─────

    private const string Scope = NpmPrefix + @"\node_modules\@openai";

    [Fact]
    public void FindProcessesUnder_只認執行檔在資料夾底下的_依PID排序()
    {
        var processes = new (int, string?, bool)[]
        {
            (30, Scope + @"\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe", false),
            (10, Scope + @"\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe", true),
            (20, @"C:\Program Files\nodejs\node.exe", true),
            (40, null, false),
            (50, NpmPrefix + @"\node_modules\opencode-ai\bin\opencode.exe", false),
        };

        var found = CliUpdateRules.FindProcessesUnder(processes, new[] { Scope });

        found.Select(p => (p.ProcessId, p.InAppJob)).Should().Equal((10, true), (30, false));
    }

    [Fact]
    public void DescribeForeignProcesses_列出不在工作物件裡的程序與刪不掉的殘留()
    {
        var foreign = new[] { new CliProcessUse(4242, Scope + @"\codex\…\codex.exe", false) };
        var text = CliUpdateRules.DescribeForeignProcesses(AgentProvider.Codex, foreign, new[] { Scope + @"\.codex-nsHu5pVi" });
        text.Should().Contain("PID 4242").And.Contain(".codex-nsHu5pVi").And.Contain("這次沒有更新");

        var many = Enumerable.Range(1, 7).Select(i => new CliProcessUse(i, $@"C:\x\{i}.exe", false)).ToList();
        CliUpdateRules.DescribeForeignProcesses(AgentProvider.Codex, many, Array.Empty<string>()).Should().Contain("等 7 個").And.NotContain("PID 6 ");

        CliUpdateRules.DescribeForeignProcesses(AgentProvider.Claude, Array.Empty<CliProcessUse>(), new[] { @"C:\npm\node_modules\@anthropic-ai\.claude-code-AbCd" })
            .Should().Contain(".claude-code-AbCd").And.Contain("Claude Code");
    }

    [Fact]
    public void DescribeInstallBusy_說明別人正在改安裝_附上判定的細節_沒有細節就不加括號()
    {
        CliUpdateRules.DescribeInstallBusy(AgentProvider.Codex, " npm 正在安裝或更新全域套件（PID 4242） ")
            .Should().Be("Codex 的安裝正在被其他程式安裝或更新（npm 正在安裝或更新全域套件（PID 4242）），這次沒有更新；請等它完成後再更新");
        CliUpdateRules.DescribeInstallBusy(AgentProvider.Claude, null)
            .Should().Be("Claude Code 的安裝正在被其他程式安裝或更新，這次沒有更新；請等它完成後再更新");
        CliUpdateRules.DescribeInstallBusy(AgentProvider.Claude, "  ").Should().NotContain("（");
    }

    [Theory]
    [InlineData(nameof(AgentProvider.Codex), ".codex-nsHu5pVi", true)]
    [InlineData(nameof(AgentProvider.Codex), "codex", false)]
    [InlineData(nameof(AgentProvider.Codex), "codex-win32-x64", false)]
    [InlineData(nameof(AgentProvider.Codex), ".claude-code-AbCd", false)]
    [InlineData(nameof(AgentProvider.Claude), ".claude-code-AbCd", true)]
    [InlineData(nameof(AgentProvider.Claude), "claude-code", false)]
    [InlineData(nameof(AgentProvider.Claude), null, false)]
    public void IsNpmResidue_npm退役的資料夾名稱(string provider, string? name, bool expected)
        => CliUpdateRules.IsNpmResidue(Enum.Parse<AgentProvider>(provider), name).Should().Be(expected);

    [Fact]
    public void NpmScopeDirectory_前綴底下這種CLI的scope()
    {
        CliUpdateRules.NpmScopeDirectory(AgentProvider.Codex, NpmPrefix).Should().Be(Scope);
        CliUpdateRules.NpmScopeDirectory(AgentProvider.Claude, NpmPrefix).Should().Be(NpmPrefix + @"\node_modules\@anthropic-ai");
    }
}
