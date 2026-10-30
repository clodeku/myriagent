using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Cli;

/// <summary>
/// 安裝變化的純函式規則：更新者 PID（Claude 換檔與版本暫存的檔名、locks 的 JSON）、npm 程序判斷、「安裝已穩定」與混版＝損壞、
/// 監看表與事件分類（以研究的 FileSystemWatcher 實測記錄當樣本），以及判定 → CliVersionChange（混版 0 次重開）
/// </summary>
public sealed class CliInstallStabilityRulesTests
{
    private const string Bin = @"C:\Users\U\.local\bin";
    private const string ClaudeExe = Bin + @"\claude.exe";
    private const string Npm = @"C:\Users\U\AppData\Roaming\npm";
    private static readonly DateTime Now = new(2026, 10, 10, 3, 44, 50, DateTimeKind.Utc);
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    private static CliInstallation ClaudeNative => new(AgentProvider.Claude, new AgentLauncher(ClaudeExe, Array.Empty<string>()), CliInstallKind.Native, ClaudeExe)
    {
        NativeExecutable = ClaudeExe,
    };

    private static CliInstallation CodexNpm => new(AgentProvider.Codex,
        new AgentLauncher(@"C:\Program Files\nodejs\node.exe", new[] { Npm + @"\node_modules\@openai\codex\bin\codex.js" }), CliInstallKind.Npm, Npm + @"\codex.cmd")
    {
        NativeExecutable = CliIdentities.CodexNpmExe,
    };

    private static CliFileStamp Stamp(ulong fileId = 0x220000003BF109) => new(0x5A3C_91E0, fileId, 260_489_376, new DateTime(2026, 10, 9, 19, 44, 47, DateTimeKind.Utc));

    private static CliInstallSnapshot Claude(string? fileVersion = "2.1.296") => new(AgentProvider.Claude, CliInstallKind.Native, ClaudeExe, Now)
    {
        Executable = Stamp(),
        FileVersion = fileVersion,
    };

    private static CliInstallSnapshot Codex(string? package = "0.162.0", string? vendor = "0.162.0") => new(AgentProvider.Codex, CliInstallKind.Npm, CliIdentities.CodexNpmExe, Now)
    {
        Executable = Stamp(0x80000003BF11C),
        PackageVersion = package,
        VendorVersion = vendor,
    };

    // ───────────── 去抖常數 ─────────────

    [Fact]
    public void 去抖_Claude3秒_npm5秒_不能用的結論30秒_輪詢60秒()
    {
        CliInstallStabilityRules.QuietPeriod(CliInstallKind.Native).Should().Be(TimeSpan.FromSeconds(3));
        CliInstallStabilityRules.QuietPeriod(CliInstallKind.Managed).Should().Be(TimeSpan.FromSeconds(3));
        CliInstallStabilityRules.QuietPeriod(CliInstallKind.Other).Should().Be(TimeSpan.FromSeconds(3));
        CliInstallStabilityRules.QuietPeriod(CliInstallKind.Npm).Should().Be(TimeSpan.FromSeconds(5));
        CliInstallStabilityRules.QuietPeriod(null).Should().Be(TimeSpan.FromSeconds(3));
        CliInstallStabilityRules.UnusableQuietPeriod.Should().Be(TimeSpan.FromSeconds(30));
        CliInstallStabilityRules.PollInterval.Should().Be(TimeSpan.FromSeconds(60));
        (CliInstallStabilityRules.NpmQuietPeriod + CliInstallStabilityRules.RecheckInterval).Should().BeLessThan(TimeSpan.FromSeconds(10), "S2：換版完成後 10 秒內判定");
    }

    // ───────────── 更新者 PID ─────────────

    /// <summary>研究的實測檔名：E1 的 watch-bin.log、這台電腦 2026-10-10 03:44:47 換檔留下的 .old（48780 是終端機裡的互動式 claude）</summary>
    [Theory]
    [InlineData("claude.exe.new.196064.1791571387439.abcd1234", true, 196064, 1791571387439L)]
    [InlineData("claude.exe.old.1791571387439.196064", false, 196064, 1791571387439L)]
    [InlineData("claude.exe.old.1791575087343.48780", false, 48780, 1791575087343L)]
    [InlineData("claude.exe.old.1791575087343", false, null, 1791575087343L)]
    [InlineData("CLAUDE.EXE.NEW.42.1791575087343.ABCDEF01", true, 42, 1791575087343L)]
    public void ParseExecutableMark_從換檔暫存檔名取更新者PID(string name, bool staged, int? pid, long ms)
    {
        var mark = CliInstallStabilityRules.ParseExecutableMark("claude.exe", name)!.Value;
        mark.Kind.Should().Be(staged ? CliUpdaterMarkKind.StagedExecutable : CliUpdaterMarkKind.BackupExecutable);
        mark.ProcessId.Should().Be(pid);
        mark.StampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime);
    }

    [Theory]
    [InlineData("claude.exe")]
    [InlineData("claude.exe.old.1791571387439.1960642x")]
    [InlineData("claude.exe.new.1.2")]
    [InlineData("claude.exe.new.1.2.xyz")]
    [InlineData("claude.exe.bak")]
    [InlineData("claude.exeold.1.2")]
    [InlineData("codex.exe.old.1.2")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseExecutableMark_其他檔名回Null(string? name)
        => CliInstallStabilityRules.ParseExecutableMark("claude.exe", name).Should().BeNull();

    [Theory]
    [InlineData("2.1.297.tmp.48780.1791575085123.1", 48780)]
    [InlineData("2.1.297.tmp.48780.1791575085123.4", 48780)]
    [InlineData("2.1.297.tmp.48780.1791575085123", 48780)]
    [InlineData("2.1.300-beta.1.tmp.5.1791575085123.2", 5)]
    public void ParseVersionMark_從Versions暫存檔名取更新者PID(string name, int pid)
    {
        var mark = CliInstallStabilityRules.ParseVersionMark(name)!.Value;
        mark.Kind.Should().Be(CliUpdaterMarkKind.StagedVersion);
        mark.ProcessId.Should().Be(pid);
        mark.StampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1791575085123).UtcDateTime);
    }

    [Theory]
    [InlineData("2.1.297")]
    [InlineData("2.1.296.lock")]
    [InlineData("x.tmp.1.2.3")]
    [InlineData(null)]
    public void ParseVersionMark_版本檔與其他檔名回Null(string? name)
        => CliInstallStabilityRules.ParseVersionMark(name).Should().BeNull();

    /// <summary>2.1.296 的鎖定檔內容（JSON.stringify(…, null, 2)）</summary>
    private const string LockJson = """
        {
          "pid": 48780,
          "version": "2.1.297",
          "execPath": "C:\\Users\\U\\.local\\bin\\claude.exe",
          "acquiredAt": 1791575085123
        }
        """;

    private static readonly DateTime Acquired = DateTimeOffset.FromUnixTimeMilliseconds(1791575085123).UtcDateTime;

    [Fact]
    public void ParseLock_讀出PID版本執行檔與取得時間_格式不符的欄位為Null()
    {
        var installLock = CliInstallStabilityRules.ParseLock("2.1.297.lock", false, LockJson, Now);
        installLock.ProcessId.Should().Be(48780);
        installLock.Version.Should().Be("2.1.297");
        installLock.ExecutablePath.Should().Be(@"C:\Users\U\.local\bin\claude.exe");
        installLock.AcquiredAtUtc.Should().Be(Acquired);
        installLock.LastWriteUtc.Should().Be(Now);

        CliInstallStabilityRules.ParseLock("x.lock", false, """{"pid": "48780"}""", Now).ProcessId.Should().BeNull("pid 要是數字（Claude 自己也這樣判斷）");
        CliInstallStabilityRules.ParseLock("x.lock", false, """{"pid": 0.5}""", Now).ProcessId.Should().BeNull();
        CliInstallStabilityRules.ParseLock("x.lock", false, "{", Now).ProcessId.Should().BeNull();
        CliInstallStabilityRules.ParseLock("x.lock", false, null, Now).Should().Be(new ClaudeInstallLock("x.lock", false, null, null, null, null, Now));
        CliInstallStabilityRules.ParseLock("x.lock", true, LockJson, Now).IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void IsLockHeld_照Claude的判斷_持有者還在而且是Claude才算_PID被重複使用不算()
    {
        var installLock = CliInstallStabilityRules.ParseLock("2.1.297.lock", false, LockJson, Now);
        var claude = new CliProcessInfo(48780, Acquired.AddMinutes(-30), @"C:\Users\U\.local\bin\claude.exe.old.1791575087343.48780");

        CliInstallStabilityRules.IsLockHeld(installLock, claude, Now).Should().BeTrue("執行更新的互動式 claude 還在跑（換檔後它的路徑是 .old）");
        CliInstallStabilityRules.IsLockHeld(installLock, claude with { ImagePath = null }, Now).Should().BeTrue("查不到路徑時照 Claude 的做法當成持有");
        CliInstallStabilityRules.IsLockHeld(installLock, claude with { ImagePath = @"C:\Program Files\nodejs\node.exe" }, Now)
            .Should().BeFalse("is running but does not appear to be Claude - treating as stale");
        CliInstallStabilityRules.IsLockHeld(installLock, null, Now).Should().BeFalse("持有者已結束：過期的鎖");
        CliInstallStabilityRules.IsLockHeld(installLock, claude with { StartTimeUtc = Acquired.AddMinutes(5) }, Now).Should().BeFalse("PID 被之後啟動的別的程序重複使用");
        CliInstallStabilityRules.IsLockHeld(installLock, claude with { Id = 1 }, Now).Should().BeFalse();
        CliInstallStabilityRules.IsLockHeld(installLock with { ExecutablePath = null }, claude, Now).Should().BeFalse("缺欄位的鎖 Claude 也當成沒有");

        var legacy = CliInstallStabilityRules.ParseLock("2.1.297.lock", true, null, Now.AddMinutes(-3));
        CliInstallStabilityRules.IsLockHeld(legacy, null, Now).Should().BeTrue("剛建立的資料夾鎖（proper-lockfile）");
        CliInstallStabilityRules.IsLockHeld(legacy with { LastWriteUtc = Now.AddHours(-2) }, null, Now).Should().BeFalse("Claude 會把舊的資料夾鎖清掉");
    }

    // ───────────── npm ─────────────

    [Theory]
    // %APPDATA%\npm\npm.cmd 的最後一行：「"%_prog%"  "%dp0%\node_modules\npm\bin\npm-cli.js" %*」（dp0 結尾已有反斜線，所以是 npm\\node_modules）
    [InlineData("\"node\"  \"C:\\Users\\U\\AppData\\Roaming\\npm\\\\node_modules\\npm\\bin\\npm-cli.js\" install -g @openai/codex@latest", true)]
    // C:\Program Files\nodejs\npm.cmd 轉交給前綴的 npm：「"%NODE_EXE%" "%NPM_CLI_JS%" %*」
    [InlineData("\"C:\\Program Files\\nodejs\\\\node.exe\" \"C:\\Users\\U\\AppData\\Roaming\\npm\\node_modules\\npm\\bin\\npm-cli.js\" i -g @anthropic-ai/claude-code", true)]
    [InlineData("node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js -g update", true)]
    [InlineData("node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js update --location=global", true)]
    [InlineData("node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js --location global uninstall @openai/codex", true)]
    [InlineData("node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js install --global @openai/codex", true)]
    [InlineData("node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js install @openai/codex", false)]
    [InlineData("node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js run dev", false)]
    [InlineData("node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js -g ls", false)]
    [InlineData("node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js -g", false)]
    // app 的 Codex 對話程序（02-procs.ps1 的實測命令列）
    [InlineData("\"node\"   \"C:\\Users\\User\\AppData\\Roaming\\npm\\\\node_modules\\@openai\\codex\\bin\\codex.js\" app-server", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGlobalNpmInstall_要有npm_cli_全域旗標與改動套件的指令(string? commandLine, bool expected)
        => CliInstallStabilityRules.IsGlobalNpmInstall(commandLine).Should().Be(expected);

    [Fact]
    public void SplitCommandLine_照CommandLineToArgvW的規則()
    {
        CliInstallStabilityRules.SplitCommandLine("\"C:\\Program Files\\nodejs\\\\node.exe\"  a\\b \"c d\" e\\\"f \"g\\\\\" h").Should().Equal(
            @"C:\Program Files\nodejs\\node.exe", @"a\b", "c d", "e\"f", @"g\", "h");
        CliInstallStabilityRules.SplitCommandLine("  ").Should().BeEmpty();
        CliInstallStabilityRules.SplitCommandLine("\"\"").Should().Equal(string.Empty);
    }

    // ───────────── 安裝已穩定、混版＝損壞 ─────────────

    [Fact]
    public void Evaluate_Claude原生_版本資訊與Version一致才穩定_不一致是損壞()
    {
        var pre = CliInstallStabilityRules.Evaluate(Claude(), null);
        pre.Condition.Should().Be(CliInstallCondition.Stable, "監看不啟動程序：只比讀得到的來源");
        pre.Version.Should().Be("2.1.296");

        var full = CliInstallStabilityRules.Evaluate(Claude(), "2.1.296 (Claude Code)\n");
        full.Condition.Should().Be(CliInstallCondition.Stable);
        full.Version.Should().Be("2.1.296");
        full.Detail.Should().Be("Claude Code 2.1.296");

        var mixed = CliInstallStabilityRules.Evaluate(Claude(), "2.1.295 (Claude Code)");
        mixed.Condition.Should().Be(CliInstallCondition.Broken);
        mixed.Detail.Should().Be("Claude Code 安裝不一致（版本資訊 2.1.296、執行檔 2.1.295），請修復");

        CliInstallStabilityRules.Evaluate(Claude("2.1.300"), "2.1.300-beta.1 (Claude Code)").Should().Be(
            new CliInstallVerdict(CliInstallCondition.Stable, "2.1.300-beta.1", "Claude Code 2.1.300-beta.1"), "版本資訊表達不了預發布字尾，只比 x.y.z");
        CliInstallStabilityRules.Evaluate(Claude(fileVersion: null), "2.1.296 (Claude Code)").Version.Should().Be("2.1.296", "讀不到版本資訊時以 --version 為準");

        var unreadable = CliInstallStabilityRules.Evaluate(Claude(), "Error: something went wrong\r\nat …");
        unreadable.Condition.Should().Be(CliInstallCondition.Broken);
        unreadable.Detail.Should().Be("無法取得 Claude Code 的版本（--version 的輸出：Error: something went wrong），請重新安裝");
    }

    [Fact]
    public void Evaluate_npm版Codex三方一致才穩定_rollback混版是損壞()
    {
        CliInstallStabilityRules.Evaluate(Codex(), "codex-cli 0.162.0\n").Should().Be(
            new CliInstallVerdict(CliInstallCondition.Stable, "0.162.0", "Codex 0.162.0"));

        // 研究 E2b：第二次更新失敗後 npm rollback 把殘留裡更舊的 codex.exe 搬回套件位置，package.json 仍是新版
        var rollback = CliInstallStabilityRules.Evaluate(Codex(), "codex-cli 0.160.1");
        rollback.Condition.Should().Be(CliInstallCondition.Broken);
        rollback.Detail.Should().Be("Codex 安裝不一致（package.json 0.162.0、codex-package.json 0.162.0、執行檔 0.160.1），請修復");

        var vendorMixed = CliInstallStabilityRules.Evaluate(Codex(vendor: "0.160.1"), null);
        vendorMixed.Condition.Should().Be(CliInstallCondition.Broken, "不啟動程序也看得出 package.json 與 codex-package.json 不一致");
        vendorMixed.Detail.Should().Be("Codex 安裝不一致（package.json 0.162.0、codex-package.json 0.160.1），請修復");

        CliInstallStabilityRules.Evaluate(Codex(vendor: null), "codex-cli 0.162.0").Condition.Should().Be(CliInstallCondition.Stable, "只用讀得到的來源比");
    }

    [Fact]
    public void Evaluate_進行中的更新_鎖_換檔暫存檔_Versions暫存_npm都算變動中()
    {
        var installLock = CliInstallStabilityRules.ParseLock("2.1.297.lock", false, LockJson, Now);
        var holder = new CliProcessInfo(48780, Acquired.AddMinutes(-30), @"C:\Users\U\.local\bin\claude.exe");
        var locked = CliInstallStabilityRules.Evaluate(Claude() with { Locks = new[] { new ClaudeLockObservation(installLock, holder) } }, "2.1.296 (Claude Code)");
        locked.Condition.Should().Be(CliInstallCondition.Changing);
        locked.UpdaterProcessId.Should().Be(48780);
        locked.Detail.Should().Be("Claude Code 正在安裝（版本鎖 2.1.297.lock，PID 48780）");
        CliInstallStabilityRules.Evaluate(Claude() with { Locks = new[] { new ClaudeLockObservation(installLock, null) } }, null)
            .Condition.Should().Be(CliInstallCondition.Stable, "持有者已結束的鎖是過期的");

        var stagedName = $"claude.exe.new.4242.{new DateTimeOffset(Now.AddSeconds(-2)).ToUnixTimeMilliseconds()}.abcd1234";
        var staged = CliInstallStabilityRules.Evaluate(Claude() with { ExecutableEntries = new[] { stagedName } }, null);
        staged.Condition.Should().Be(CliInstallCondition.Changing);
        staged.UpdaterProcessId.Should().Be(4242);
        var leftover = $"claude.exe.new.4242.{new DateTimeOffset(Now.AddHours(-3)).ToUnixTimeMilliseconds()}.abcd1234";
        CliInstallStabilityRules.Evaluate(Claude() with { ExecutableEntries = new[] { leftover, "claude.exe.old.1791575087343.48780" } }, null)
            .Condition.Should().Be(CliInstallCondition.Stable, "換檔失敗留下的舊暫存檔與執行中舊程序的 .old 不算進行中");

        var versionTemp = $"2.1.297.tmp.4242.{new DateTimeOffset(Now.AddSeconds(-1)).ToUnixTimeMilliseconds()}.1";
        CliInstallStabilityRules.Evaluate(Claude() with { VersionEntries = new[] { versionTemp } }, null).UpdaterProcessId.Should().Be(4242);

        var npm = new CliNpmProcess(31337, "node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js install -g @openai/codex@latest");
        var installing = CliInstallStabilityRules.Evaluate(Codex() with { NpmProcesses = new[] { npm } }, "codex-cli 0.160.1");
        installing.Condition.Should().Be(CliInstallCondition.Changing, "npm 還在跑：混版可能只是還沒裝完");
        installing.UpdaterProcessId.Should().Be(31337);
        CliInstallStabilityRules.Evaluate(Codex() with { NpmProcesses = new[] { npm with { CommandLine = "node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js run dev" } } }, null)
            .Condition.Should().Be(CliInstallCondition.Stable, "npm run 不是在改全域套件");
    }

    [Fact]
    public void Evaluate_執行檔不在_npm的殘留與原生的備份是損壞_其他是移除()
    {
        CliInstallStabilityRules.Evaluate(Codex() with { Executable = null }, null).Should().Be(
            new CliInstallVerdict(CliInstallCondition.Broken, null, "Codex 安裝不完整（找不到 codex.exe），請重新安裝"), "套件還在、vendor 的 exe 不在：codex.js 會報 Missing optional dependency");

        var retired = Codex() with { Executable = null, PackageMissing = true, RetiredPackages = new[] { ".codex-zUm5vyz0" } };
        CliInstallStabilityRules.Evaluate(retired, null).Should().Be(
            new CliInstallVerdict(CliInstallCondition.Broken, null, "Codex 安裝不完整（套件不見了，只剩 npm 退役的 .codex-zUm5vyz0），請重新安裝"));
        CliInstallStabilityRules.Evaluate(retired with { RetiredPackages = Array.Empty<string>() }, null).Condition.Should().Be(CliInstallCondition.Missing, "npm uninstall -g");

        CliInstallStabilityRules.Evaluate(Claude() with { Executable = null, ExecutableEntries = new[] { "claude.exe.old.1791575087343.48780" } }, null)
            .Condition.Should().Be(CliInstallCondition.Broken, "換檔沒完成又沒還原");
        CliInstallStabilityRules.Evaluate(Claude() with { Executable = null }, null).Should().Be(
            new CliInstallVerdict(CliInstallCondition.Missing, null, $"找不到 Claude Code 的執行檔（{ClaudeExe}）"));

        CliInstallStabilityRules.Evaluate(Codex() with { PackageMissing = true }, null).Condition.Should().Be(CliInstallCondition.Broken, "exe 在、package.json 不在");

        // npm 版 Claude：postinstall 還沒把原生檔放進 bin\claude.exe（約 500 位元組的佔位腳本）
        const string claudeNpmExe = Npm + @"\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
        var claudeNpm = new CliInstallSnapshot(AgentProvider.Claude, CliInstallKind.Npm, claudeNpmExe, Now) { Executable = Stamp() with { Size = 500 }, PackageVersion = "2.1.297" };
        CliInstallStabilityRules.Evaluate(claudeNpm, null).Should().Be(
            new CliInstallVerdict(CliInstallCondition.Broken, null, "Claude Code 安裝不完整（claude.exe 還是 postinstall 沒跑時的佔位腳本），請重新安裝"));
        CliInstallStabilityRules.Evaluate(claudeNpm with { Executable = Stamp(), FileVersion = "2.1.297" }, "2.1.297 (Claude Code)").Version.Should().Be("2.1.297");
        CliInstallStabilityRules.Evaluate(claudeNpm with { NativeExecutable = Npm + @"\node_modules\@anthropic-ai\claude-code\cli.js" }, null)
            .Condition.Should().Be(CliInstallCondition.Stable, "只檢查 bin\\claude.exe：2.1.112 以前的舊配置以 cli.js 代表");
        CliInstallStabilityRules.Evaluate(CliInstallSnapshot.NotInstalled(AgentProvider.Codex, Now), null).Should().Be(
            new CliInstallVerdict(CliInstallCondition.Missing, null, "找不到 Codex"));
    }

    [Fact]
    public void Evaluate_受管Codex沒有版本來源_監看時穩定但版本不明_完整判定以Version為準()
    {
        var managed = new CliInstallSnapshot(AgentProvider.Codex, CliInstallKind.Managed, @"C:\Users\U\AppData\Local\Myriagent\codex\codex.exe", Now) { Executable = Stamp(7) };
        CliInstallStabilityRules.Evaluate(managed, null).Should().Be(new CliInstallVerdict(CliInstallCondition.Stable, null, "Codex"));
        CliInstallStabilityRules.Evaluate(managed, "codex-cli 0.162.0").Version.Should().Be("0.162.0");
    }

    [Fact]
    public void Fingerprint_執行檔身分與版本來源()
    {
        Claude().Fingerprint.Should().Be(new CliInstallFingerprint(Stamp(), "2.1.296", null, null));
        Codex().Fingerprint.Should().NotBe(Codex(package: "0.163.0").Fingerprint);
        (Claude() with { Executable = Stamp(1) }).Fingerprint.Should().NotBe(Claude().Fingerprint, "換檔後 FileId 不同");
    }

    // ───────────── 監看表與事件分類 ─────────────

    [Fact]
    public void BuildWatchPlan_Claude原生_Bin_Versions_Locks_路徑跟著Home與XDG()
    {
        CliInstallStabilityRules.BuildWatchPlan(ClaudeNative, NoEnvironment).Should().Equal(
            new CliWatchTarget(CliWatchArea.ClaudeBin, Bin, "claude.exe*", false, "claude.exe"),
            new CliWatchTarget(CliWatchArea.ClaudeVersions, @"C:\Users\U\.local\share\claude\versions", "*", false, "versions"),
            new CliWatchTarget(CliWatchArea.ClaudeLocks, @"C:\Users\U\.local\state\claude\locks", "*.lock", false, "locks"));

        var xdg = CliInstallStabilityRules.BuildWatchPlan(ClaudeNative, k => k switch { "XDG_DATA_HOME" => @"D:\data", "XDG_STATE_HOME" => @"D:\state", _ => null });
        xdg[1].Directory.Should().Be(@"D:\data\claude\versions");
        xdg[2].Directory.Should().Be(@"D:\state\claude\locks");
        CliInstallStabilityRules.BuildWatchPlan(ClaudeNative, k => k == "XDG_DATA_HOME" ? " " : null)[1].Directory
            .Should().Be(@"C:\Users\U\.local\share\claude\versions", "空白的環境變數當成沒設");
    }

    [Fact]
    public void BuildWatchPlan_npm_受管_其他()
    {
        CliInstallStabilityRules.BuildWatchPlan(CodexNpm, NoEnvironment).Should().Equal(
            new CliWatchTarget(CliWatchArea.NpmPackages, Npm + @"\node_modules\@openai", "*", true, "codex"),
            new CliWatchTarget(CliWatchArea.NpmShims, Npm, "codex*", false, "codex"));

        var claudeNpm = new CliInstallation(AgentProvider.Claude, new AgentLauncher(Npm + @"\node_modules\@anthropic-ai\claude-code\bin\claude.exe", Array.Empty<string>()),
            CliInstallKind.Npm, Npm + @"\claude.cmd") { NativeExecutable = Npm + @"\node_modules\@anthropic-ai\claude-code\bin\claude.exe" };
        CliInstallStabilityRules.BuildWatchPlan(claudeNpm, NoEnvironment).Should().Equal(
            new CliWatchTarget(CliWatchArea.NpmPackages, Npm + @"\node_modules\@anthropic-ai", "*", true, "claude-code"),
            new CliWatchTarget(CliWatchArea.NpmShims, Npm, "claude*", false, "claude"));

        const string managedExe = @"C:\Users\U\AppData\Local\Myriagent\codex\codex.exe";
        var managed = new CliInstallation(AgentProvider.Codex, new AgentLauncher(managedExe, Array.Empty<string>()), CliInstallKind.Managed, managedExe) { NativeExecutable = managedExe };
        CliInstallStabilityRules.BuildWatchPlan(managed, NoEnvironment).Should().Equal(
            new CliWatchTarget(CliWatchArea.ManagedRoot, @"C:\Users\U\AppData\Local\Myriagent", "codex*", false, "codex"));

        const string winget = @"C:\Users\U\AppData\Local\Microsoft\WinGet\Links\claude.exe";
        var other = new CliInstallation(AgentProvider.Claude, new AgentLauncher(winget, Array.Empty<string>()), CliInstallKind.Other, winget) { NativeExecutable = winget };
        CliInstallStabilityRules.BuildWatchPlan(other, NoEnvironment).Should().Equal(
            new CliWatchTarget(CliWatchArea.ExecutableDirectory, @"C:\Users\U\AppData\Local\Microsoft\WinGet\Links", "claude.exe*", false, "claude.exe"));

        CliInstallStabilityRules.BuildWatchPlan(null, NoEnvironment).Should().BeEmpty();
    }

    /// <summary>研究 E1 的 watch-bin.log（Claude 照安裝程式的順序換檔，FileSystemWatcher 看到的事件原文）</summary>
    [Fact]
    public void ClassifyEvent_Claude換檔的實測事件_換檔相關的算_舊備份的清理不算()
    {
        var bin = CliInstallStabilityRules.BuildWatchPlan(ClaudeNative, NoEnvironment)[0];
        CliWatchEvent? E(WatcherChangeTypes change, string name, string? old = null) => CliInstallStabilityRules.ClassifyEvent(bin, change, name, old);

        E(WatcherChangeTypes.Created, "claude.exe.new.196064.1791571387439.abcd1234")!.Value.UpdaterProcessId.Should().Be(196064);
        E(WatcherChangeTypes.Changed, "claude.exe.new.196064.1791571387439.abcd1234")!.Value.UpdaterProcessId.Should().Be(196064);
        var retire = E(WatcherChangeTypes.Renamed, "claude.exe.old.1791571387439.196064", "claude.exe")!.Value;
        retire.UpdaterProcessId.Should().Be(196064);
        retire.Description.Should().Be("claude.exe 改名為 claude.exe.old.1791571387439.196064");
        E(WatcherChangeTypes.Renamed, "claude.exe", "claude.exe.new.196064.1791571387439.abcd1234")!.Value.UpdaterProcessId.Should().Be(196064);
        E(WatcherChangeTypes.Changed, "claude.exe")!.Value.UpdaterProcessId.Should().BeNull();
        E(WatcherChangeTypes.Deleted, "claude.exe").Should().NotBeNull("執行檔不見了也是變化");

        E(WatcherChangeTypes.Renamed, "claude.exe.old.1791571387439.1960642", "claude.exe.old.1791571387439.196064").Should().BeNull("備份之間改名");
        E(WatcherChangeTypes.Changed, "claude.exe.old.1791571387439.196064").Should().BeNull();
        E(WatcherChangeTypes.Deleted, "claude.exe.old.1791571387439.196064").Should().BeNull("claude -p 一小時後清掉舊備份");
        E(WatcherChangeTypes.Deleted, "claude.exe.new.196064.1791571387439.abcd1234").Should().BeNull("換檔失敗時清掉暫存檔，執行檔沒變");
        E(WatcherChangeTypes.Created, "claude.exe.bak").Should().BeNull();
    }

    [Fact]
    public void ClassifyEvent_Versions與Locks()
    {
        var plan = CliInstallStabilityRules.BuildWatchPlan(ClaudeNative, NoEnvironment);
        var versions = plan[1];
        var locks = plan[2];

        CliInstallStabilityRules.ClassifyEvent(versions, WatcherChangeTypes.Created, "2.1.297.tmp.48780.1791575085123.1", null)!.Value.UpdaterProcessId.Should().Be(48780);
        CliInstallStabilityRules.ClassifyEvent(versions, WatcherChangeTypes.Renamed, "2.1.297", "2.1.297.tmp.48780.1791575085123.1")!.Value.UpdaterProcessId.Should().Be(48780);
        CliInstallStabilityRules.ClassifyEvent(versions, WatcherChangeTypes.Created, "2.1.297", null).Should().NotBeNull("安裝程式先建立空的版本檔佔位");
        CliInstallStabilityRules.ClassifyEvent(versions, WatcherChangeTypes.Deleted, "2.1.293", null).Should().BeNull("保留兩版以外的舊版被清掉");
        CliInstallStabilityRules.ClassifyEvent(versions, WatcherChangeTypes.Created, "notes.txt", null).Should().BeNull();

        CliInstallStabilityRules.ClassifyEvent(locks, WatcherChangeTypes.Created, "2.1.297.lock", null).Should().NotBeNull("開始安裝");
        CliInstallStabilityRules.ClassifyEvent(locks, WatcherChangeTypes.Deleted, "2.1.297.lock", null).Should().NotBeNull("安裝結束：開始計時");
        CliInstallStabilityRules.ClassifyEvent(locks, WatcherChangeTypes.Created, "x.tmp", null).Should().BeNull();
    }

    /// <summary>研究 E2 的 watch-openai.log（npm 換套件，監看 node_modules\@openai 含子資料夾）</summary>
    [Fact]
    public void ClassifyEvent_npm換套件的實測事件_套件_退役資料夾_平台套件與殼才算()
    {
        var plan = CliInstallStabilityRules.BuildWatchPlan(CodexNpm, NoEnvironment);
        var packages = plan[0];
        var shims = plan[1];
        CliWatchEvent? P(WatcherChangeTypes change, string name, string? old = null) => CliInstallStabilityRules.ClassifyEvent(packages, change, name, old);

        P(WatcherChangeTypes.Renamed, ".codex-nsHu5pVi", "codex").Should().NotBeNull("retire");
        P(WatcherChangeTypes.Created, "codex").Should().NotBeNull();
        P(WatcherChangeTypes.Created, @"codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe").Should().NotBeNull();
        P(WatcherChangeTypes.Changed, @"codex\package.json").Should().NotBeNull();
        P(WatcherChangeTypes.Deleted, @".codex-nsHu5pVi\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\codex-package.json").Should().NotBeNull("removeTrash");
        P(WatcherChangeTypes.Created, @"codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe").Should().NotBeNull("提升到前綴的平台套件");
        P(WatcherChangeTypes.Created, @"codex-sdk\package.json").Should().BeNull("別的套件");
        P(WatcherChangeTypes.Created, @"agents\package.json").Should().BeNull();

        CliInstallStabilityRules.ClassifyEvent(shims, WatcherChangeTypes.Changed, "codex.cmd", null).Should().NotBeNull("npm 收尾重寫殼");
        CliInstallStabilityRules.ClassifyEvent(shims, WatcherChangeTypes.Changed, "codex", null).Should().NotBeNull();
        CliInstallStabilityRules.ClassifyEvent(shims, WatcherChangeTypes.Created, "codex.ps1", null).Should().NotBeNull();
        CliInstallStabilityRules.ClassifyEvent(shims, WatcherChangeTypes.Changed, "codex-other.cmd", null).Should().BeNull();
    }

    [Fact]
    public void ClassifyEvent_受管Codex的資料夾改名換版()
    {
        const string managedExe = @"C:\Users\U\AppData\Local\Myriagent\codex\codex.exe";
        var managed = new CliInstallation(AgentProvider.Codex, new AgentLauncher(managedExe, Array.Empty<string>()), CliInstallKind.Managed, managedExe) { NativeExecutable = managedExe };
        var root = CliInstallStabilityRules.BuildWatchPlan(managed, NoEnvironment)[0];

        CliInstallStabilityRules.ClassifyEvent(root, WatcherChangeTypes.Renamed, "codex.old-20261010034447123", "codex").Should().NotBeNull();
        CliInstallStabilityRules.ClassifyEvent(root, WatcherChangeTypes.Renamed, "codex", "codex.new").Should().NotBeNull();
        CliInstallStabilityRules.ClassifyEvent(root, WatcherChangeTypes.Deleted, "codex.old-20261010034447123", null).Should().BeNull("清掉換下來的舊版");
        CliInstallStabilityRules.ClassifyEvent(root, WatcherChangeTypes.Created, "codex.new", null).Should().BeNull("還在解壓，舊版完整可用");
    }

    [Fact]
    public void WatcherSurvivesError_緩衝溢位時還在監看_資料夾被刪或存取被拒時已停止()
    {
        CliInstallStabilityRules.WatcherSurvivesError(new InternalBufferOverflowException()).Should().BeTrue("只是漏了事件：全量檢查就好，不必重建");
        CliInstallStabilityRules.WatcherSurvivesError(new System.ComponentModel.Win32Exception(5)).Should().BeFalse("研究 exp5：監看的資料夾被刪時丟「存取被拒」");
        CliInstallStabilityRules.WatcherSurvivesError(new IOException("x")).Should().BeFalse();
        CliInstallStabilityRules.WatcherSurvivesError(null).Should().BeFalse();
    }

    // ───────────── 判定 → CliVersionChange ─────────────

    [Fact]
    public void BuildVersionChange_混版是安裝損壞_0次重開()
    {
        var old = CliIdentities.Codex("0.160.1", fileId: 1);
        var verdict = CliInstallStabilityRules.Evaluate(Codex(), "codex-cli 0.160.1");
        var change = CliInstallStabilityRules.BuildVersionChange(AgentProvider.Codex, old, verdict, CliIdentities.Codex("0.160.1", fileId: 2), CliChangeOrigin.App)!;

        change.InstallBroken.Should().BeTrue();
        change.Stable.Should().BeTrue();
        change.New.Should().BeNull("損壞時不宣告新身分");
        change.IsSettledBinary.Should().BeFalse();
        change.IsVersionSwitch.Should().BeFalse("S2：混版時不宣告換版");
        change.Detail.Should().Be("Codex 安裝不一致（package.json 0.162.0、codex-package.json 0.162.0、執行檔 0.160.1），請修復");

        var applied = CliManager.ApplyVersionChange(new CliState(AgentProvider.Codex, CodexNpm, "0.160.1", "0.162.0", false, null, null, null) { Identity = old }, change);
        applied.Identity.Should().Be(old, "損壞時保留最後可用的身分：對話不會因此重開");
        applied.InstallBrokenReason.Should().Be(change.Detail);
    }

    [Fact]
    public void BuildVersionChange_穩定的新身分是換版_變動中_移除_讀不到身分不發布()
    {
        var old = CliIdentities.Claude("2.1.296", fileId: 1);
        var fresh = CliIdentities.Claude("2.1.297", fileId: 2);
        var stable = new CliInstallVerdict(CliInstallCondition.Stable, "2.1.297", "Claude Code 2.1.297");

        var external = CliInstallStabilityRules.BuildVersionChange(AgentProvider.Claude, old, stable, fresh, CliChangeOrigin.External, Now)!;
        external.IsVersionSwitch.Should().BeTrue();
        external.Detail.Should().Be("Claude Code 已在 app 外更新為 2.1.297");
        external.DetectedAtUtc.Should().Be(Now);
        CliInstallStabilityRules.BuildVersionChange(AgentProvider.Claude, old, stable, fresh, CliChangeOrigin.App)!.Detail.Should().Be("Claude Code 已更新為 2.1.297");

        var first = CliInstallStabilityRules.BuildVersionChange(AgentProvider.Claude, null, stable, fresh, CliChangeOrigin.External)!;
        first.IsSettledBinary.Should().BeTrue();
        first.IsVersionSwitch.Should().BeFalse("啟動後第一次讀到的身分（Old: null）不算換版");
        first.Detail.Should().BeNull();

        CliInstallStabilityRules.BuildVersionChange(AgentProvider.Claude, old, stable, null, CliChangeOrigin.External).Should().BeNull("讀不到身分：這次不發布，不能被當成移除");

        var changing = CliInstallStabilityRules.BuildVersionChange(AgentProvider.Claude, old, new CliInstallVerdict(CliInstallCondition.Changing, null, "正在安裝"), null, CliChangeOrigin.External)!;
        changing.Stable.Should().BeFalse();
        changing.New.Should().BeNull();

        var removed = CliInstallStabilityRules.BuildVersionChange(AgentProvider.Claude, old, new CliInstallVerdict(CliInstallCondition.Missing, null, "找不到"), null, CliChangeOrigin.External)!;
        removed.Stable.Should().BeTrue();
        removed.InstallBroken.Should().BeFalse();
        removed.New.Should().BeNull();
    }

    [Fact]
    public void ResolveOrigin_更新者是App自己的程序或在工作物件裡才算App()
    {
        Func<int, bool> app = pid => pid == 4242;
        CliInstallStabilityRules.ResolveOrigin(4242, false, app).Should().Be(CliChangeOrigin.App, "app 自己的 claude update");
        CliInstallStabilityRules.ResolveOrigin(48780, true, app).Should().Be(CliChangeOrigin.External, "終端機裡的互動式 claude 背景更新（即使 app 同時在更新別的）");
        CliInstallStabilityRules.ResolveOrigin(null, true, app).Should().Be(CliChangeOrigin.App, "npm 的檔名沒有 PID：app 正在跑自己的 npm");
        CliInstallStabilityRules.ResolveOrigin(null, false, app).Should().Be(CliChangeOrigin.External);
    }
}
